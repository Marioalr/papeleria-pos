using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Herramientas.Simulador;

/// <summary>Lo que quedó en la bandeja de salida de una caja al cerrarla: qué era, si subió y, si la caja principal no lo quiso, por qué.</summary>
public sealed record ElementoDeLaBandeja(string Ruta, Guid Id, bool Subido, string? Error);

/// <summary>
/// Una caja simulada: la caja de verdad sin la pantalla. Habla con la caja principal con el mismo código que la ventana de
/// Windows (<see cref="ClienteCaja"/> con su <see cref="Contingencia"/>, su base local <c>caja-local.db</c> en una carpeta
/// temporal y la red con sus dos conexiones y sus plazos, <see cref="RedCaja"/>): escanea y busca contra su copia, cobra con la
/// versión de la copia, y si la caja principal no contesta vende con su copia y sube su bandeja al volver. Lo que hace la
/// pantalla con lo que contesta (agregar el renglón, poner al día los precios que cambiaron) va aquí como lo hace
/// <c>PantallaVenta</c>. Lleva su propio libro (lo que cobró, cuánto efectivo debe tener) para el cuadre.
/// </summary>
public sealed class CajaSimulada
{
    /// <summary>El ticket que se atiende: la venta con la versión de la copia de la que salió el precio de cada línea (como TicketAbierto).</summary>
    private sealed class TicketSimulado(Venta venta)
    {
        public Venta Venta { get; } = venta;
        public Dictionary<Guid, ProductoVentaDto> Productos { get; } = [];
        public Dictionary<VentaLinea, long?> Versiones { get; } = [];

        public long? VersionDe(VentaLinea linea) =>
            Versiones.TryGetValue(linea, out var version) ? version
            : linea.VarianteId is { } varianteId ? Productos.GetValueOrDefault(varianteId)?.Version : null;

        public void Calculada(VentaLinea linea, ProductoVentaDto producto)
        {
            foreach (var otra in Venta.Lineas.Where(l => l != linea && l.VarianteId == producto.VarianteId))
                Versiones.TryAdd(otra, VersionDe(otra));
            Productos[producto.VarianteId] = producto;
            Versiones[linea] = producto.Version;
        }
    }

    private sealed record CobroPorConfirmar(SolicitudCobro Solicitud, TicketSimulado Ticket, TicketDelGuion Guion);

    private sealed record PorHacer(string Que, Func<Task<bool>> Hacer)
    {
        public int Intentos { get; set; }
    }

    private readonly OpcionesSimulador _opciones;
    private readonly DiaSimulado _dia;
    private readonly Mediciones _mediciones;
    private readonly Random _azar;
    private readonly RedCaja _red;
    private readonly Lock _candado = new();
    private readonly List<Task> _deFondo = [];
    private readonly List<CobroPorConfirmar> _sinConfirmar = [];
    private readonly List<PorHacer> _porHacer = [];
    private EstadoRed _estado = EstadoRed.Buena;
    private CaidaSimulada? _caida;
    private int _ticketsEnLaCaida;
    private EfectivoCajaDto? _pideRetiro;

    /// <param name="red">La red de verdad hasta la caja principal: la de lo que espera quien atiende y la de lo de fondo (true).</param>
    public CajaSimulada(int numero, AltaCajaDto alta, Uri servicio, Func<bool, HttpMessageHandler> red, string carpeta, Mediciones mediciones, DiaSimulado dia)
    {
        Numero = numero;
        Caja = alta.Caja;
        _opciones = dia.Opciones;
        _dia = dia;
        _mediciones = mediciones;
        _azar = new Random(unchecked(_opciones.Semilla * 7919 + numero));
        Medidor = new MedidorDeRed(mediciones);
        // Como la caja de verdad (RegistroInterfaz): un cliente para lo que espera quien atiende y otro para lo de fondo, cada uno
        // con sus conexiones; aquí, además, con el interruptor de la caída y lo que mide cada llamada.
        _red = new RedCaja(Medidor.Envolver(Red.Envolver(red(false))), Medidor.Envolver(Red.Envolver(red(true))));
        Conexion = new ConexionCaja { Servicio = servicio, CajaId = Caja.Id, Llave = alta.Llave, PideAlta = true };
        RutaLocal = Path.Combine(carpeta, $"caja-{numero}", "caja-local.db");
        Almacen = new AlmacenLocal(RutaLocal);
        // La caja con el servicio en la misma PC no tiene red de por medio: solo si contesta o no. Las demás (la caja 2, por Wi-Fi),
        // buena, lenta o caída según lo que tarda el latido.
        var reglas = numero == 1 && servicio.IsLoopback ? ReglasDeLaRed.SoloSiContesta : ReglasDeLaRed.DeLaTienda;
        Contingencia = new Contingencia(Almacen, _red.Fondo, Conexion, reglas: reglas);
        Contingencia.Cambio += AlCambiar;
        Cliente = new ClienteCaja(_red.Interactivo, Conexion, Contingencia)
        {
            PlazoLectura = Transporte.PlazoLectura,
            PlazoEscritura = Transporte.PlazoEscritura,
        };
    }

    public int Numero { get; }
    public CajaDto Caja { get; }
    public string Nombre => Caja.Nombre;
    public ConexionCaja Conexion { get; }
    public RedSimulada Red { get; } = new();
    public MedidorDeRed Medidor { get; }
    public AlmacenLocal Almacen { get; }
    public string RutaLocal { get; }
    public Contingencia Contingencia { get; }
    public ClienteCaja Cliente { get; }
    public SesionDto Cajero { get; private set; } = null!;
    public TurnoDto Turno { get; private set; } = null!;

    /// <summary>Lo que tardó en tener su copia la primera vez: bajarla completa y armar su foto.</summary>
    public TimeSpan? AbrirLaCaja { get; private set; }

    // ── El libro de la caja ─────────────────────────────────────────────────

    public List<VentaSimulada> Ventas { get; } = [];

    /// <summary>Las devoluciones y cancelaciones que hizo esta caja (de ella sale el reembolso, aunque el ticket sea de otra).</summary>
    public List<DevolucionSimulada> Devoluciones { get; } = [];

    public List<MovimientoSimulado> Movimientos { get; } = [];
    public List<CambioDeRed> CambiosDeRed { get; } = [];

    /// <summary>Lo que no salió como debía: hace que la simulación no pase.</summary>
    public List<string> Errores { get; } = [];

    /// <summary>Lo que pasó y no es un error (un ticket que no se pudo armar, una devolución sin ticket que devolver).</summary>
    public List<string> Notas { get; } = [];

    public CaidaSimulada? Caida => _caida;
    public int Tickets { get; private set; }
    public int Renglones { get; private set; }
    public int Escaneos { get; private set; }
    public int Busquedas { get; private set; }
    public int Rapidos { get; private set; }
    public int ArticulosComunes { get; private set; }
    public int Quitados { get; private set; }
    public int PreciosCambiadosAlCobrar { get; private set; }
    public int CobrosSinConfirmar { get; private set; }
    public int Retiros { get; private set; }

    /// <summary>Un turno de otra corrida que había quedado abierto y se cerró antes de empezar.</summary>
    public string? TurnoDeOtraCorrida { get; private set; }

    public CorteInformativoDto? Informativo { get; private set; }

    /// <summary>El corte del turno, como se lo contestaron a la caja (o como lo hizo ella sin conexión).</summary>
    public CorteDto? Corte { get; private set; }

    /// <summary>Lo que contó el encargado: lo que dice el libro de la caja.</summary>
    public long? Contado { get; private set; }

    /// <summary>
    /// El efectivo que debe haber en el cajón según lo que pasó: el fondo, lo que entró de cada venta (también de las que
    /// después se cancelaron: su reembolso sale aparte), las entradas, menos las salidas y los reembolsos que salieron de esta
    /// caja. Es lo que cuenta el encargado al cerrar.
    /// </summary>
    public long EfectivoEnCaja =>
        Turno.FondoCentavos + Ventas.Sum(v => v.EfectivoCentavos)
        + Movimientos.Sum(m => m.Tipo == TipoMovimientoCaja.Entrada ? m.MontoCentavos : -m.MontoCentavos)
        - Devoluciones.Sum(d => d.ReembolsoEfectivoCentavos);

    // ── Abrir la caja ───────────────────────────────────────────────────────

    /// <summary>
    /// Como al abrir la caja nueva: el latido y el aviso empiezan, baja su copia completa y arma su foto; quien atiende entra
    /// (las fichas y su PIN) y abre su turno; la pantalla de venta pide lo suyo. Si quedó abierto un turno de otra corrida, el
    /// encargado lo cierra antes con lo que debe haber.
    /// </summary>
    public async Task AbrirAsync(CancellationToken ct)
    {
        var inicio = Stopwatch.GetTimestamp();
        Contingencia.Iniciar();
        while (Contingencia.Foto is null)
        {
            if (Stopwatch.GetElapsedTime(inicio) > _opciones.Espera)
                throw new InvalidOperationException($"La {Nombre} no bajó su copia del catálogo en {_opciones.Espera.TotalSeconds:0} s.");
            await Task.Delay(10, ct);
        }
        AbrirLaCaja = Stopwatch.GetElapsedTime(inicio);
        _mediciones.Anotar(Operacion.AbrirCaja, AbrirLaCaja.Value);

        inicio = Stopwatch.GetTimestamp();
        await Cliente.FichasAsync(Caja.Id);
        Cajero = await Cliente.EntrarAsync(_opciones.Cajero.Usuario, _opciones.Cajero.Pin);
        _mediciones.Anotar(Operacion.Entrar, inicio);

        if (await Cliente.TurnoAsync(Caja.Id) is { } abierto)
        {
            var encargado = await Cliente.EntrarAsync(_opciones.Encargado.Usuario, _opciones.Encargado.Pin);
            var esperado = (await Cliente.EfectivoAsync(abierto.Id))?.EsperadoCentavos ?? 0;
            await Cliente.CorteZAsync(abierto.Id, new SolicitudCorteZ(encargado.UsuarioId, Math.Max(0, esperado)));
            TurnoDeOtraCorrida = $"La {Nombre} tenía abierto un turno de otra corrida (desde las {abierto.AbiertoLocal:HH:mm}): el encargado lo "
                + "cerró antes de empezar, con lo que debía haber.";
        }

        inicio = Stopwatch.GetTimestamp();
        Turno = await Cliente.AbrirTurnoAsync(Caja.Id, Cajero.UsuarioId, _opciones.FondoCentavos);
        _mediciones.Anotar(Operacion.AbrirTurno, inicio);

        // Lo que pide la pantalla de venta al entrar.
        await Cliente.PermisosDeSesionAsync(Cajero.UsuarioId, Caja.SucursalId);
        await Cliente.PilotoEleventaAsync();
        await Cliente.RapidosAsync(Caja.SucursalId);
        await Cliente.ConfiguracionSucursalAsync(Caja.SucursalId);
        try
        {
            await Cliente.ConteoDelDiaAsync(Caja.SucursalId, Cajero.UsuarioId);
        }
        catch (ErrorCaja)
        {
            // La pantalla tampoco se detiene por esto.
        }
    }

    // ── El día ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Vende sus tickets del día, cada uno cuando llega su cliente (si va atrasada, el siguiente espera en la fila). Antes de
    /// cada uno atiende lo que esperaba a la caja principal (un cobro que no se supo si quedó, una devolución o cancelación) y,
    /// si toca, una entrada o salida de efectivo o una devolución de otro cliente.
    /// </summary>
    public async Task VenderElDiaAsync(IReadOnlyList<TicketDelGuion> tickets, CancellationToken ct)
    {
        var avisado = 0;
        foreach (var t in tickets)
        {
            await _dia.EsperarAsync(t.Momento, ct);
            ControlarLaCaida(t);
            try
            {
                await AtenderPendientesAsync();
                if (Interlocked.Exchange(ref _pideRetiro, null) is { } retiro)
                    await RetiroAsync(retiro);
                if (t.Movimiento is { } movimiento)
                    await MovimientoAsync(movimiento);
                if (t.Devolver)
                    await DevolucionDeUnClienteAsync();
                await VenderTicketAsync(t);
            }
            catch (Exception ex) when (ex is ErrorCaja or InvalidOperationException or ArgumentException or HttpRequestException or IOException
                                           or TimeoutException)
            {
                Errores.Add($"Ticket {t.Numero} de la {Nombre}: {ex.Message}");
            }
            if (_caida is { Fin: null })
                _ticketsEnLaCaida++;
            Tickets++;
            if (Tickets * 4 / tickets.Count > avisado)
            {
                avisado = Tickets * 4 / tickets.Count;
                _dia.Avisar($"{Nombre}: {Tickets} de {tickets.Count} tickets ({Ventas.Count(v => !v.EnLinea)} sin conexión).");
            }
        }
        if (_caida is { Fin: null } sigue)
        {
            sigue.TerminoConElDia = true;
            TerminarCaida();
        }
    }

    private async Task VenderTicketAsync(TicketDelGuion t)
    {
        var ticket = new TicketSimulado(new Venta
        {
            SucursalId = Caja.SucursalId, CajaId = Caja.Id, TurnoId = Turno.Id, CajeroId = Cajero.UsuarioId, Serie = Caja.Serie, CreadaUtc = DateTime.UtcNow,
        });
        foreach (var renglon in t.Renglones)
        {
            await PausaAsync(120, 350);
            await RenglonAsync(ticket, renglon, t);
        }
        // Algo que se escaneó de más: se quita (y queda en la bitácora).
        if (t.Sobra is { } sobra && ticket.Venta.Lineas.All(l => l.VarianteId != sobra.VarianteId))
        {
            await PausaAsync(120, 350);
            if (await RenglonAsync(ticket, sobra, t) is { } deMas)
                await QuitarAsync(ticket, deMas);
        }
        if (ticket.Venta.Lineas.Count == 0)
        {
            Notas.Add($"El ticket {t.Numero} de la {Nombre} se quedó sin renglones: no se cobró.");
            return;
        }
        // El dueño cambia el precio de lo que ya se escaneó y, unos segundos después (cuando ya llegó a la copia de esta caja), se
        // cobra: el cobro lleva la versión de antes y la caja principal dice que cambió.
        if (t.CambiaPrecio)
        {
            await _dia.CambiarPrecioAsync();
            try
            {
                await _dia.PrecioEnLaCopia(Numero).Task.WaitAsync(_opciones.Espera);
            }
            catch (TimeoutException)
            {
                // Lo dice el cuadre (la copia de esta caja no se puso al día).
            }
        }
        await PausaAsync(300, 800);
        if (await CobrarAsync(ticket, t) is { } venta && t.Cancelar)
            await CancelarOEsperarAsync(venta);
    }

    /// <summary>Un renglón como lo mete quien atiende; devuelve la línea que quedó en el ticket (vacío si no se pudo).</summary>
    private async Task<VentaLinea?> RenglonAsync(TicketSimulado ticket, RenglonDelGuion r, TicketDelGuion t)
    {
        switch (r.Forma)
        {
            case FormaRenglon.Busqueda:
                return await BuscarYElegirAsync(ticket, r);
            case FormaRenglon.Rapido:
                Rapidos++;
                return (Cliente.Foto?.PorVariante(r.VarianteId!.Value) ?? await Cliente.PorVarianteAsync(r.VarianteId!.Value, Caja.SucursalId)) is { } rapido
                    ? Agregar(ticket, rapido, r.Cantidad)
                    : null;
            case FormaRenglon.ProductoNuevo:
                return await ProductoNuevoAsync(ticket, r);
            default:
                if (t.UsaProductoNuevo && r.VarianteId is null)
                {
                    // Lo que la caja 1 dio de alta hace un momento: el cliente llega después de que exista.
                    ProductoVentaDto? nuevo;
                    try
                    {
                        nuevo = await _dia.ProductoNuevo.Task.WaitAsync(_opciones.Espera);
                    }
                    catch (TimeoutException)
                    {
                        nuevo = null;
                    }
                    if (nuevo is null)
                        return Comun(ticket, r.Descripcion, GuionDelDia.PrecioDelNuevo, r.Cantidad);
                }
                return await EscanearAsync(ticket, r);
        }
    }

    /// <summary>
    /// El lector, como lo atiende la pantalla de venta: el código en la foto de la copia (sin la red); si no está (lo recién dado
    /// de alta en otra caja), se le pregunta a la caja principal con su medio segundo, y si no contesta se escanea otra vez. Sin
    /// conexión, lo que no está en la copia se vende como artículo común.
    /// </summary>
    private async Task<VentaLinea?> EscanearAsync(TicketSimulado ticket, RenglonDelGuion r)
    {
        Escaneos++;
        var inicio = Stopwatch.GetTimestamp();
        if (Cliente.Foto?.PorCodigo(r.Codigo!) is { } enLaCopia)
        {
            var linea = Agregar(ticket, enLaCopia, r.Cantidad);
            _mediciones.Anotar(Operacion.Escanear, inicio);
            return linea;
        }
        for (var intento = 1; intento <= 3; intento++)
        {
            inicio = Stopwatch.GetTimestamp();
            var respuesta = await Cliente.BuscarEnLaPrincipalAsync(r.Codigo!, Caja.SucursalId);
            if (respuesta.Pregunto)
                _mediciones.Anotar(Operacion.CodigoALaPrincipal, inicio);
            if (respuesta.Producto is { } producto)
                return Agregar(ticket, producto, r.Cantidad);
            if (respuesta.Contesto)
            {
                Errores.Add($"La {Nombre} escaneó «{r.Codigo}» y la caja principal no lo tiene.");
                return null;
            }
            if (!respuesta.Pregunto)
                break;
            // «La caja principal no contestó: escanéalo otra vez en un momento.»
            await Task.Delay(200);
        }
        return Comun(ticket, r.Descripcion, GuionDelDia.PrecioDelNuevo, r.Cantidad);
    }

    /// <summary>
    /// Se escribe el nombre: se busca contra la copia en cuanto se teclea (desde la segunda letra), se juntan las familias como
    /// en la lista, y se elige. Lo que se eligió se le dice a la caja principal (es para aprender: no se espera).
    /// </summary>
    private async Task<VentaLinea?> BuscarYElegirAsync(TicketSimulado ticket, RenglonDelGuion r)
    {
        Busquedas++;
        var texto = r.Texto!;
        List<ResultadoBusquedaDto> resultados = [];
        for (var n = 2; n <= texto.Length; n++)
        {
            var parcial = texto[..n];
            if (!parcial.Any(char.IsLetter) || parcial.EndsWith(' '))
                continue;
            await PausaAsync(60, 140);
            var inicio = Stopwatch.GetTimestamp();
            resultados = Cliente.BuscarEnLaCopia(parcial, GuionDelDia.ParaFamilias)
                         ?? await Cliente.BuscarAsync(parcial, Caja.SucursalId, Caja.Id, GuionDelDia.ParaFamilias);
            _ = Familias.Agrupar(resultados, x => x.Partes ?? [], x => x.Vendidos);
            _mediciones.Anotar(Operacion.Buscar, inicio);
        }
        var elegido = resultados.FirstOrDefault(x => x.VarianteId == r.VarianteId) ?? resultados.FirstOrDefault();
        Fondo(RegistrarBusquedaAsync(new SolicitudBusqueda(Caja.Id, texto, resultados.Count, elegido?.VarianteId)));
        if (elegido is null)
        {
            Notas.Add($"En la {Nombre}, «{texto}» no encontró nada.");
            return null;
        }
        return (Cliente.Foto?.PorVariante(elegido.VarianteId) ?? await Cliente.PorVarianteAsync(elegido.VarianteId, Caja.SucursalId)) is { } producto
            ? Agregar(ticket, producto, r.Cantidad)
            : null;
    }

    private async Task RegistrarBusquedaAsync(SolicitudBusqueda busqueda)
    {
        var enLinea = Contingencia.EnLinea;
        var inicio = Stopwatch.GetTimestamp();
        await Cliente.RegistrarBusquedaAsync(busqueda);
        if (enLinea)
            _mediciones.Anotar(Operacion.RegistrarBusqueda, inicio);
    }

    /// <summary>
    /// Lo que llegó hoy y nadie había dado de alta (caja 1): se escanea, no está en la copia ni en la caja principal, y quien
    /// atiende lo da de alta ahí mismo (alta rápida) y lo vende. Sin conexión, se vende como artículo común.
    /// </summary>
    private async Task<VentaLinea?> ProductoNuevoAsync(TicketSimulado ticket, RenglonDelGuion r)
    {
        Escaneos++;
        if (Cliente.Foto?.PorCodigo(r.Codigo!) is { } yaEsta)
        {
            _dia.ProductoNuevo.TrySetResult(yaEsta);
            return Agregar(ticket, yaEsta, r.Cantidad);
        }
        var inicio = Stopwatch.GetTimestamp();
        var respuesta = await Cliente.BuscarEnLaPrincipalAsync(r.Codigo!, Caja.SucursalId);
        if (respuesta.Pregunto)
            _mediciones.Anotar(Operacion.CodigoALaPrincipal, inicio);
        if (respuesta.Producto is { } existe)
        {
            _dia.ProductoNuevo.TrySetResult(existe);
            return Agregar(ticket, existe, r.Cantidad);
        }
        if (respuesta.Contesto)
        {
            try
            {
                var departamento = (await Cliente.OpcionesCatalogoAsync()).Departamentos[0].Id;
                inicio = Stopwatch.GetTimestamp();
                var nuevo = await Cliente.AltaRapidaAsync(new SolicitudAltaRapida(Cajero.UsuarioId, Caja.SucursalId, r.Codigo!, r.Descripcion,
                    GuionDelDia.PrecioDelNuevo, departamento));
                _mediciones.Anotar(Operacion.AltaRapida, inicio);
                _dia.Anotar($"La {Nombre} dio de alta «{r.Descripcion}» ({r.Codigo}), que llegó hoy, y lo vende.");
                _dia.ProductoNuevo.TrySetResult(nuevo);
                return Agregar(ticket, nuevo, r.Cantidad);
            }
            catch (ErrorCaja ex)
            {
                Errores.Add($"La {Nombre} no pudo dar de alta «{r.Descripcion}»: {ex.Message}");
            }
        }
        _dia.ProductoNuevo.TrySetResult(null);
        return Comun(ticket, r.Descripcion, GuionDelDia.PrecioDelNuevo, r.Cantidad);
    }

    private VentaLinea? Agregar(TicketSimulado ticket, ProductoVentaDto producto, Cantidad cantidad)
    {
        try
        {
            var linea = ticket.Venta.AgregarProducto(producto.ParaVenta(), producto.PresentacionModelo(), cantidad, producto.PreciosModelo());
            ticket.Calculada(linea, producto);
            Renglones++;
            return linea;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Notas.Add($"La {Nombre} no pudo agregar «{producto.Descripcion}» × {cantidad}: {ex.Message}");
            return null;
        }
    }

    private VentaLinea Comun(TicketSimulado ticket, string descripcion, long precioCentavos, Cantidad cantidad)
    {
        ArticulosComunes++;
        Renglones++;
        return ticket.Venta.AgregarArticuloComun(descripcion, precioCentavos, cantidad);
    }

    /// <summary>Supr: se quita del ticket y queda en la bitácora (como RegistrarQuitadoAsync de la pantalla).</summary>
    private async Task QuitarAsync(TicketSimulado ticket, VentaLinea linea)
    {
        ticket.Venta.QuitarLinea(linea);
        ticket.Versiones.Remove(linea);
        Renglones--;
        Quitados++;
        var inicio = Stopwatch.GetTimestamp();
        try
        {
            await Cliente.BitacoraAsync(new SolicitudBitacora(Cajero.UsuarioId, Caja.SucursalId, Caja.Id, AccionControlada.QuitarProducto,
                $"Quitó «{linea.Descripcion}» × {linea.Cantidad} del ticket", linea.ImporteCentavos));
            _mediciones.Anotar(Operacion.Quitar, inicio);
        }
        catch (ErrorCaja ex)
        {
            Notas.Add($"Lo quitado en la {Nombre} no quedó en la bitácora: {ex.Message}");
        }
    }

    // ── Cobrar ──────────────────────────────────────────────────────────────

    /// <summary>
    /// F12, como la pantalla: con la versión de la copia de la que salió el precio de cada línea y el Id del ticket. Si la caja
    /// principal dice que cambió un precio, el ticket se pone al día y se cobra otra vez. Si se perdió la respuesta y no se
    /// sabe si quedó, el ticket espera a la caja principal (y se cobra otra vez con el mismo Id). Con
    /// <paramref name="otraVez"/>, se manda tal como se mandó la primera vez.
    /// </summary>
    private async Task<VentaSimulada?> CobrarAsync(TicketSimulado ticket, TicketDelGuion t, SolicitudCobro? otraVez = null)
    {
        var sinCopia = false;
        for (var intento = 1; ; intento++)
        {
            SolicitudCobro solicitud;
            if (otraVez is not null && intento == 1)
                solicitud = otraVez;
            else
            {
                var (lineas, version) = ArmadoSolicitud.ParaCobrar(ticket.Venta, ticket.VersionDe, Cliente.Foto?.Version);
                solicitud = new SolicitudCobro(Caja.Id, Turno.Id, Cajero.UsuarioId, lineas, Cobros(ticket.Venta, t), VentaId: ticket.Venta.Id,
                    VersionCatalogo: version);
            }
            var antes = Medidor.Exitos(MedidorDeRed.RutaCobro);
            var inicio = Stopwatch.GetTimestamp();
            try
            {
                var cobrado = await Cliente.CobrarAsync(solicitud);
                var enLinea = Medidor.Exitos(MedidorDeRed.RutaCobro) > antes;
                _mediciones.Anotar(enLinea ? Operacion.CobrarEnLinea : Operacion.CobrarSinConexion, inicio);
                return Registrar(cobrado, ticket, t, enLinea);
            }
            catch (ErrorCaja ex)
            {
                if (ex.PreciosCambiados is { } precios && intento < 4)
                {
                    // «Cambió el precio de …: revisa el total y cobra otra vez.»
                    _mediciones.Anotar(Operacion.PrecioCambiadoAlCobrar, inicio);
                    PreciosCambiadosAlCobrar++;
                    PonerPreciosAlDia(ticket, precios);
                    _dia.Anotar($"Al cobrar en la {Nombre}: {ex.Message}");
                    await PausaAsync(300, 700);
                    continue;
                }
                if (ex.Codigo is "cobro-sin-confirmar")
                {
                    CobrosSinConfirmar++;
                    _sinConfirmar.Add(new CobroPorConfirmar(solicitud, ticket, t));
                    _dia.Anotar($"La {Nombre} no sabe si quedó un cobro de {Formato.Dinero(ticket.Venta.TotalCentavos)} (se cortó la red a media "
                        + "respuesta): lo cobrará otra vez, con el mismo Id, cuando vuelva.");
                    return null;
                }
                if (!sinCopia && !Contingencia.EnLinea && SinLoQueNoTieneLaCopia(ticket))
                {
                    // Sin conexión, lo que la copia todavía no tiene (lo recién dado de alta) no se cobra: va como artículo común.
                    sinCopia = true;
                    Notas.Add($"Sin conexión, la {Nombre} no pudo cobrar algo que su copia no tiene ({ex.Message}): lo vendió como artículo común.");
                    continue;
                }
                Errores.Add($"La {Nombre} no pudo cobrar un ticket de {Formato.Dinero(ticket.Venta.TotalCentavos)}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Lo que dio el cliente: tarjeta o transferencia exactas; en efectivo, lo redondeado al peso o un billete que alcance.</summary>
    private List<CobroSolicitado> Cobros(Venta venta, TicketDelGuion t)
    {
        var referencia = $"SIM{Numero}-{t.Numero:0000}";
        switch (t.Pago)
        {
            case FormaDePago.Tarjeta:
                return [new CobroSolicitado(FormaPago.Tarjeta, venta.TotalCentavos, referencia)];
            case FormaDePago.Transferencia:
                return [new CobroSolicitado(FormaPago.Transferencia, venta.TotalCentavos, referencia)];
            case FormaDePago.Mixto when venta.TotalCentavos >= 400:
                var tarjeta = venta.TotalCentavos / 2 / 100 * 100;
                return
                [
                    new CobroSolicitado(FormaPago.Tarjeta, tarjeta, referencia),
                    new CobroSolicitado(FormaPago.Efectivo, PagaCon(venta.Efectivo(tarjeta).ACobrarCentavos, t.Billete)),
                ];
            default:
                return [new CobroSolicitado(FormaPago.Efectivo, PagaCon(venta.Efectivo(0).ACobrarCentavos, t.Billete))];
        }
    }

    private static readonly long[] Billetes = [1_000, 2_000, 5_000, 10_000, 20_000, 50_000];

    private static long PagaCon(long aCobrar, int billete) =>
        billete <= 0 ? aCobrar : (aCobrar + Billetes[billete - 1] - 1) / Billetes[billete - 1] * Billetes[billete - 1];

    /// <summary>
    /// Lo que cambió de precio (lo dijo el cobro): las líneas de esos productos se vuelven a calcular con los de ahora y lo demás
    /// queda revisado hasta esa versión; la foto de la caja se queda con los de ahora. Como PantallaVenta.PonerPreciosAlDia.
    /// </summary>
    private void PonerPreciosAlDia(TicketSimulado ticket, PreciosCambiadosDto precios)
    {
        var cambiados = precios.Productos.Select(p => p.VarianteId).ToHashSet();
        foreach (var linea in ticket.Venta.Lineas.Where(l => l.VarianteId is { } v && !l.EsImpresiones && !cambiados.Contains(v)))
        {
            if (ticket.VersionDe(linea) is not { } version || version < precios.VersionCatalogo)
                ticket.Versiones[linea] = precios.VersionCatalogo;
        }
        foreach (var (varianteId, producto) in ticket.Productos.ToList())
        {
            if (!cambiados.Contains(varianteId) && (producto.Version is not { } version || version < precios.VersionCatalogo))
                ticket.Productos[varianteId] = producto with { Version = precios.VersionCatalogo };
        }
        foreach (var producto in precios.Productos)
        {
            Cliente.PreciosAlDia(producto);
            var lineas = ticket.Venta.Lineas.Where(l => l.VarianteId == producto.VarianteId && !l.EsImpresiones).ToList();
            foreach (var linea in lineas)
                ticket.Versiones.TryAdd(linea, ticket.VersionDe(linea));
            ticket.Productos[producto.VarianteId] = producto;
            foreach (var linea in lineas)
            {
                try
                {
                    ticket.Venta.CambiarCantidad(linea, linea.Cantidad, producto.PresentacionModelo(linea.PresentacionId), producto.PreciosModelo());
                    ticket.Versiones[linea] = producto.Version;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // Como la pantalla: la línea se queda como estaba y la caja principal lo dice al cobrar.
                }
            }
        }
    }

    /// <summary>Sin conexión: lo del ticket que la copia no tiene pasa a artículo común (su nombre y su precio). Dice si cambió algo.</summary>
    private bool SinLoQueNoTieneLaCopia(TicketSimulado ticket)
    {
        var copia = Contingencia.Paquete?.Productos.Select(p => p.VarianteId).ToHashSet() ?? [];
        var faltan = ticket.Venta.Lineas.Where(l => l.VarianteId is { } v && !copia.Contains(v)).ToList();
        foreach (var linea in faltan)
        {
            ticket.Venta.QuitarLinea(linea);
            ticket.Versiones.Remove(linea);
            ticket.Venta.AgregarArticuloComun(linea.Descripcion, linea.PrecioCentavos, linea.Cantidad);
            ArticulosComunes++;
        }
        return faltan.Count > 0;
    }

    private VentaSimulada Registrar(TicketDto cobrado, TicketSimulado ticket, TicketDelGuion t, bool enLinea)
    {
        long Por(FormaPago forma) => cobrado.Cobros.Where(c => c.Forma == forma).Sum(c => c.MontoCentavos);
        var venta = new VentaSimulada
        {
            Caja = Numero, VentaId = cobrado.VentaId ?? ticket.Venta.Id, Folio = cobrado.Folio,
            Numero = int.Parse(cobrado.Folio[(cobrado.Folio.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture),
            TotalCentavos = cobrado.TotalCentavos, EfectivoCentavos = Por(FormaPago.Efectivo), TarjetaCentavos = Por(FormaPago.Tarjeta),
            TransferenciaCentavos = Por(FormaPago.Transferencia), EnLinea = enLinea, YaCobrada = cobrado.YaCobrada, Cuando = _dia.Transcurrido,
            Renglones = ticket.Venta.Lineas
                .Select(l => new RenglonVendido(l.VarianteId, l.Descripcion, l.Cantidad.Milesimas, l.CantidadBase.Milesimas, l.LlevaInventario))
                .ToList(),
            PorCancelar = t.Cancelar,
        };
        Ventas.Add(venta);
        _dia.Vendida(venta);
        if (cobrado.YaCobrada)
            _dia.Anotar($"La {Nombre} cobró otra vez el ticket que no se sabía si había quedado: ya estaba ({cobrado.Folio}), una sola vez.");
        // Lo que la pantalla revisa después de cobrar, sin que lo espere el siguiente cliente: si ya hay que hacer un retiro.
        Fondo(EfectivoDespuesDeCobrarAsync());
        return venta;
    }

    private async Task EfectivoDespuesDeCobrarAsync()
    {
        var enLinea = Contingencia.EnLinea;
        var inicio = Stopwatch.GetTimestamp();
        try
        {
            if (await Cliente.EfectivoAsync(Turno.Id) is { PideRetiro: true } efectivo)
                Volatile.Write(ref _pideRetiro, efectivo);
            if (enLinea)
                _mediciones.Anotar(Operacion.Efectivo, inicio);
        }
        catch (ErrorCaja)
        {
            // Sin conexión no hay aviso de retiro (como en la pantalla).
        }
    }

    /// <summary>El aviso de retiro: se saca lo que pasa del fondo, en billetes de $100 (una salida, F8).</summary>
    private async Task RetiroAsync(EfectivoCajaDto efectivo)
    {
        var monto = (Math.Min(efectivo.EsperadoCentavos, EfectivoEnCaja) - Turno.FondoCentavos) / 10_000 * 10_000;
        if (monto <= 0)
            return;
        Retiros++;
        await MovimientoAsync(new MovimientoDelGuion(TipoMovimientoCaja.Salida, monto, "Retiro de efectivo (el aviso de la caja)"));
    }

    // ── Efectivo, devoluciones y cancelaciones ──────────────────────────────

    /// <summary>F7 o F8, con su motivo: con la caja principal o, sin conexión, con la copia (sube con la bandeja).</summary>
    private async Task MovimientoAsync(MovimientoDelGuion m)
    {
        var antes = Medidor.Exitos(MedidorDeRed.RutaMovimiento);
        var inicio = Stopwatch.GetTimestamp();
        try
        {
            await Cliente.MovimientoAsync(Turno.Id, new SolicitudMovimiento(m.Tipo, m.MontoCentavos, m.Motivo, Cajero.UsuarioId));
        }
        catch (ErrorCaja ex)
        {
            Errores.Add($"La {Nombre} no pudo registrar la {(m.Tipo == TipoMovimientoCaja.Entrada ? "entrada" : "salida")} de "
                + $"{Formato.Dinero(m.MontoCentavos)} ({m.Motivo}): {ex.Message}");
            return;
        }
        _mediciones.Anotar(Operacion.Movimiento, inicio);
        Movimientos.Add(new MovimientoSimulado(m.Tipo, m.MontoCentavos, m.Motivo, Medidor.Exitos(MedidorDeRed.RutaMovimiento) > antes));
    }

    /// <summary>
    /// Llega un cliente a devolver algo de un ticket de antes (de cualquier caja). Si no hay caja principal, o el ticket todavía
    /// no llega a ella (su caja lo cobró sin conexión), espera; si todavía no hay un ticket que devolver, también.
    /// </summary>
    private async Task DevolucionDeUnClienteAsync()
    {
        VentaSimulada? elegida = null;
        async Task<bool> DevolverAlgoAsync()
        {
            elegida ??= _dia.ParaDevolver(Numero, _azar);
            return elegida is not null && await DevolverAsync(elegida);
        }
        if (!await DevolverAlgoAsync())
            Esperar(elegida is null ? "una devolución (todavía no había un ticket que devolver)" : $"la devolución de algo del ticket {elegida.Folio}",
                DevolverAlgoAsync);
    }

    /// <summary>
    /// Devolver (solo con la caja principal): se busca el ticket por su folio, se devuelve una pieza (o el renglón, si no es
    /// por pieza) de algo del inventario; casi siempre regresa a la tienda, a veces llegó dañado (merma). Si pasa de lo que
    /// quien atiende puede devolver sola, lo autoriza el encargado. Dice si ya no hay que intentarlo otra vez.
    /// </summary>
    private async Task<bool> DevolverAsync(VentaSimulada venta)
    {
        if (!Contingencia.EnLinea)
            return false;
        var inicio = Stopwatch.GetTimestamp();
        VentaParaDevolverDto? encontrada;
        try
        {
            encontrada = await Cliente.VentaPorFolioAsync(Caja.SucursalId, venta.Folio);
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            return false;
        }
        // Lo cobró sin conexión su caja y todavía no sube: otro rato.
        if (encontrada is null)
            return false;
        _mediciones.Anotar(Operacion.BuscarFolio, inicio);
        // Un renglón del inventario cuyo nombre no se repite en el ticket: así se reconoce en el ticket de la caja principal.
        var posibles = venta.Renglones
            .Where(x => x is { LlevaInventario: true, VarianteId: not null } && venta.Renglones.Count(y => y.Descripcion == x.Descripcion) == 1)
            .ToList();
        if (posibles.Count == 0)
        {
            Notas.Add($"Del ticket {venta.Folio} no había qué devolver (nada del inventario que se distinga).");
            return true;
        }
        var nuestro = posibles[_azar.Next(posibles.Count)];
        var suyo = encontrada.Lineas.FirstOrDefault(l =>
            l.Descripcion == nuestro.Descripcion && l.CantidadMilesimas == nuestro.Cantidad && l.CantidadDevueltaMilesimas == 0);
        if (suyo is null)
        {
            Errores.Add($"El ticket {venta.Folio} en la caja principal no trae «{nuestro.Descripcion}» × {new Cantidad(nuestro.Cantidad)} como se cobró.");
            return true;
        }
        var cantidad = nuestro.Cantidad >= Cantidad.Escala && nuestro.Cantidad % Cantidad.Escala == 0 ? Cantidad.Escala : nuestro.Cantidad;
        var regresa = _azar.NextDouble() >= 0.15;
        var solicitud = new SolicitudDevolucion(Cajero.UsuarioId, Caja.Id, regresa ? "No era el que quería" : "Venía dañado",
            [new LineaDevolucionSolicitada(suyo.LineaId, cantidad, regresa)]);
        inicio = Stopwatch.GetTimestamp();
        ComprobanteDevolucionDto comprobante;
        try
        {
            comprobante = await ConAutorizacionAsync(AccionControlada.Devolucion, suyo.ImporteCentavos, $"Devolución del ticket {venta.Folio}",
                id => Cliente.DevolverAsync(encontrada.Id, solicitud with { AutorizacionId = id }));
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            return false;
        }
        catch (ErrorCaja ex)
        {
            Errores.Add($"La {Nombre} no pudo devolver «{nuestro.Descripcion}» del ticket {venta.Folio}: {ex.Message}");
            return true;
        }
        _mediciones.Anotar(Operacion.Devolver, inicio);
        var cantidadBase = Redondeo.Dividir((Int128)nuestro.CantidadBase * cantidad, nuestro.Cantidad);
        Devoluciones.Add(new DevolucionSimulada(Numero, venta.VentaId, venta.Folio, false, comprobante.ReembolsoEfectivoCentavos,
            [(nuestro.VarianteId!.Value, cantidadBase, regresa)]));
        _dia.Anotar($"En la {Nombre} devolvieron «{nuestro.Descripcion}» del ticket {venta.Folio}{(regresa ? "" : " (dañado: merma)")}: "
            + $"se reembolsaron {Formato.Dinero(comprobante.ReembolsoEfectivoCentavos)} en efectivo.");
        return true;
    }

    /// <summary>El cliente se arrepiente recién cobrado: se cancela (lo autoriza el encargado). Sin caja principal, espera.</summary>
    private async Task CancelarOEsperarAsync(VentaSimulada venta)
    {
        if (!await CancelarAsync(venta))
            Esperar($"la cancelación del ticket {venta.Folio}", () => CancelarAsync(venta));
    }

    private async Task<bool> CancelarAsync(VentaSimulada venta)
    {
        if (!Contingencia.EnLinea)
            return false;
        VentaParaDevolverDto? encontrada;
        var inicio = Stopwatch.GetTimestamp();
        try
        {
            encontrada = await Cliente.VentaPorFolioAsync(Caja.SucursalId, venta.Folio);
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            return false;
        }
        if (encontrada is null)
            return false;
        _mediciones.Anotar(Operacion.BuscarFolio, inicio);
        if (!encontrada.PuedeCancelar)
        {
            Errores.Add($"El ticket {venta.Folio} ya no se pudo cancelar: su turno ya no está abierto.");
            venta.PorCancelar = false;
            return true;
        }
        var solicitud = new SolicitudCancelacion(Cajero.UsuarioId, Caja.Id, "El cliente se arrepintió");
        inicio = Stopwatch.GetTimestamp();
        ComprobanteDevolucionDto comprobante;
        try
        {
            // Lo que se autoriza es al menos el total del ticket (con su redondeo).
            comprobante = await ConAutorizacionAsync(AccionControlada.CancelarTicket, encontrada.TotalCentavos + 100, $"Cancelar el ticket {venta.Folio}",
                id => Cliente.CancelarAsync(encontrada.Id, solicitud with { AutorizacionId = id }));
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            return false;
        }
        catch (ErrorCaja ex)
        {
            Errores.Add($"La {Nombre} no pudo cancelar el ticket {venta.Folio}: {ex.Message}");
            venta.PorCancelar = false;
            return true;
        }
        _mediciones.Anotar(Operacion.Cancelar, inicio);
        Devoluciones.Add(new DevolucionSimulada(Numero, venta.VentaId, venta.Folio, true, comprobante.ReembolsoEfectivoCentavos,
            venta.Renglones.Where(r => r is { LlevaInventario: true, VarianteId: not null }).Select(r => (r.VarianteId!.Value, r.CantidadBase, true)).ToList()));
        venta.Cancelada = true;
        venta.PorCancelar = false;
        _dia.Anotar($"En la {Nombre} se canceló el ticket {venta.Folio} (el cliente se arrepintió): se reembolsaron "
            + $"{Formato.Dinero(comprobante.ReembolsoEfectivoCentavos)} en efectivo.");
        return true;
    }

    /// <summary>Lo hace quien atiende; si la caja principal pide autorización, el encargado la da con su PIN y se hace otra vez.</summary>
    private async Task<T> ConAutorizacionAsync<T>(AccionControlada accion, long montoCentavos, string detalle, Func<Guid?, Task<T>> hacer)
    {
        try
        {
            return await hacer(null);
        }
        catch (ErrorCaja ex) when (ex.Codigo == "requiere-autorizacion")
        {
            var inicio = Stopwatch.GetTimestamp();
            var autorizacion = await Cliente.AutorizarAsync(new SolicitudAutorizacion(accion, Caja.SucursalId, Caja.Id, Cajero.UsuarioId,
                _opciones.Encargado.Usuario, _opciones.Encargado.Pin, montoCentavos, detalle));
            _mediciones.Anotar(Operacion.Autorizar, inicio);
            return await hacer(autorizacion.Id);
        }
    }

    // ── Lo que espera a la caja principal ───────────────────────────────────

    private void Esperar(string que, Func<Task<bool>> hacer) => _porHacer.Add(new PorHacer(que, hacer));

    /// <summary>
    /// Con la caja principal otra vez: los cobros que no se supo si quedaron (con el mismo Id: si ya estaban, salen los mismos) y
    /// lo que esperaba a la caja principal (devoluciones, cancelaciones). Sin ella, nada.
    /// </summary>
    private async Task AtenderPendientesAsync()
    {
        foreach (var pendiente in _sinConfirmar.ToList())
        {
            if (!Contingencia.EnLinea)
                return;
            _sinConfirmar.Remove(pendiente);
            if (await CobrarAsync(pendiente.Ticket, pendiente.Guion, pendiente.Solicitud) is { } venta && pendiente.Guion.Cancelar)
                await CancelarOEsperarAsync(venta);
        }
        for (var i = 0; i < _porHacer.Count && Contingencia.EnLinea;)
        {
            var pendiente = _porHacer[i];
            pendiente.Intentos++;
            if (await pendiente.Hacer())
                _porHacer.RemoveAt(i);
            else
                i++;
        }
    }

    /// <summary>
    /// Al terminar su día: termina lo de fondo, espera a la caja principal para lo que quedó pendiente y a que suba su bandeja,
    /// y hace el corte: quien atiende ve el corte a ciegas, el encargado entra con su PIN y escribe lo que contó (lo que dice el
    /// libro de la caja). Si en <see cref="OpcionesSimulador.Espera"/> no se pudo, el corte se hace como esté (sin conexión, con la copia).
    /// </summary>
    public async Task CerrarElTurnoAsync(CancellationToken ct)
    {
        await TerminarLoDeFondoAsync();
        var inicio = Stopwatch.GetTimestamp();
        while (!(Contingencia.EnLinea && _sinConfirmar.Count == 0 && _porHacer.Count == 0 && Contingencia.Pendientes == 0))
        {
            if (Stopwatch.GetElapsedTime(inicio) > _opciones.Espera)
            {
                Errores.Add($"La {Nombre} no terminó antes del corte en {_opciones.Espera.TotalSeconds:0} s: "
                    + $"{(Contingencia.EnLinea ? "en línea" : $"sin la caja principal ({Contingencia.EstadoDeLaRed})")}, {Contingencia.Pendientes} por subir, "
                    + $"{_sinConfirmar.Count} cobros sin confirmar, pendiente: "
                    + $"{(_porHacer.Count == 0 ? "nada" : string.Join("; ", _porHacer.Select(p => p.Que)))}.");
                break;
            }
            await AtenderPendientesAsync();
            await Task.Delay(100, ct);
        }
        await TerminarLoDeFondoAsync();

        inicio = Stopwatch.GetTimestamp();
        try
        {
            Informativo = await Cliente.CorteInformativoAsync(Turno.Id, Cajero.UsuarioId);
            _mediciones.Anotar(Operacion.CorteInformativo, inicio);
        }
        catch (ErrorCaja ex)
        {
            Errores.Add($"El corte a ciegas de la {Nombre} no salió: {ex.Message}");
        }
        var encargado = await Cliente.EntrarAsync(_opciones.Encargado.Usuario, _opciones.Encargado.Pin);
        Contado = EfectivoEnCaja;
        inicio = Stopwatch.GetTimestamp();
        try
        {
            Corte = await Cliente.CorteZAsync(Turno.Id, new SolicitudCorteZ(encargado.UsuarioId, Contado.Value));
            _mediciones.Anotar(Operacion.CorteZ, inicio);
            _dia.Anotar($"Corte de la {Nombre}{(Corte.EnContingencia ? " (sin conexión)" : "")}: esperado {Formato.Dinero(Corte.EsperadoCentavos)}, "
                + $"contado {Formato.Dinero(Contado.Value)}, diferencia {Formato.ConSigno(Corte.DiferenciaCentavos ?? 0)}.");
        }
        catch (ErrorCaja ex)
        {
            Errores.Add($"El corte del turno de la {Nombre} no se hizo: {ex.Message}");
        }
    }

    /// <summary>Espera a que la caja deje de tener algo por subir (después del corte, el corte mismo si se hizo sin conexión).</summary>
    public async Task<bool> EsperarBandejaVaciaAsync(TimeSpan espera)
    {
        var inicio = Stopwatch.GetTimestamp();
        while (Contingencia.Pendientes > 0 || !Contingencia.EnLinea)
        {
            if (Stopwatch.GetElapsedTime(inicio) > espera)
                return false;
            await Task.Delay(100);
        }
        return true;
    }

    // ── La caída de la red ──────────────────────────────────────────────────

    private void ControlarLaCaida(TicketDelGuion t)
    {
        if (_opciones.Caida is not { } caida || Numero != _opciones.CajaDeLaCaida)
            return;
        if (_caida is null && t.Momento >= caida.Inicio)
        {
            lock (_candado)
                _caida = new CaidaSimulada { Caja = Numero, Inicio = _dia.Transcurrido };
            Red.Caer();
            _dia.Anotar($"Se cae la red de la {Nombre} ({caida.Cuanto}).");
            if (caida.Duracion is { } duracion)
                Fondo(Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(duracion, _finDeLaCaida.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Terminó antes, con el día de esta caja.
                        return;
                    }
                    TerminarCaida();
                }));
        }
        else if (_caida is { Fin: null } && caida.Tickets is { } tickets && _ticketsEnLaCaida >= tickets)
            TerminarCaida();
    }

    private readonly CancellationTokenSource _finDeLaCaida = new();

    private void TerminarCaida()
    {
        CaidaSimulada caida;
        lock (_candado)
        {
            if (_caida is not { Fin: null } enCurso)
                return;
            enCurso.Fin = _dia.Transcurrido;
            caida = enCurso;
        }
        _finDeLaCaida.Cancel();
        Red.Volver();
        _dia.Anotar($"Vuelve la red de la {Nombre}.");
        Fondo(VigilarElRegresoAsync(caida));
    }

    /// <summary>De que vuelve la red a que la caja subió lo que hizo sin ella y trabaja otra vez con la caja principal.</summary>
    private async Task VigilarElRegresoAsync(CaidaSimulada caida)
    {
        var inicio = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(inicio) < _opciones.Espera)
        {
            if (Contingencia.EnLinea && Contingencia.Pendientes == 0 && !Red.Caida)
            {
                caida.AlDia = _dia.Transcurrido;
                _mediciones.Anotar(Operacion.AlDiaTrasLaCaida, Stopwatch.GetElapsedTime(inicio));
                _dia.Anotar($"La {Nombre} ya subió lo que hizo sin conexión y trabaja otra vez con la caja principal.");
                return;
            }
            await Task.Delay(20);
        }
    }

    private void AlCambiar()
    {
        var estado = Contingencia.EstadoDeLaRed;
        lock (_candado)
        {
            if (estado == _estado)
                return;
            _estado = estado;
            CambiosDeRed.Add(new CambioDeRed(_dia.Transcurrido, estado));
            if (estado != EstadoRed.Buena && _caida is { LoNoto: null, Fin: null } caida)
                caida.LoNoto = _dia.Transcurrido;
        }
    }

    // ── Apoyo ───────────────────────────────────────────────────────────────

    /// <summary>Lo que una persona tarda entre una cosa y otra; con <c>--minutos 0</c>, nada.</summary>
    private Task PausaAsync(int minimoMs, int maximoMs) =>
        _opciones.ConPausas ? Task.Delay(_azar.Next(minimoMs, maximoMs)) : Task.CompletedTask;

    private void Fondo(Task tarea)
    {
        lock (_deFondo)
            _deFondo.Add(tarea);
    }

    public async Task TerminarLoDeFondoAsync()
    {
        while (true)
        {
            Task[] tareas;
            lock (_deFondo)
            {
                tareas = [.. _deFondo];
                _deFondo.Clear();
            }
            if (tareas.Length == 0)
                return;
            await Task.WhenAll(tareas).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Se cierra la caja: el latido y el aviso se detienen, lo que se escribía en su base termina y se sueltan sus conexiones
    /// (como al cerrar la ventana): después se puede leer y borrar caja-local.db.
    /// </summary>
    public void Cerrar()
    {
        Contingencia.Cambio -= AlCambiar;
        Contingencia.Cerrar(TimeSpan.FromSeconds(10));
        _red.Dispose();
    }

    /// <summary>Lo que quedó en la bandeja de salida (ya cerrada la caja), en el orden en que pasó.</summary>
    public List<ElementoDeLaBandeja> LeerBandeja()
    {
        using var conexion = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = RutaLocal, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT Ruta, Id, SubidoUtc IS NOT NULL, Error FROM Bandeja ORDER BY Orden";
        using var lector = comando.ExecuteReader();
        var elementos = new List<ElementoDeLaBandeja>();
        while (lector.Read())
            elementos.Add(new ElementoDeLaBandeja(lector.GetString(0), Guid.Parse(lector.GetString(1)), lector.GetBoolean(2),
                lector.IsDBNull(3) ? null : lector.GetString(3)));
        return elementos;
    }
}
