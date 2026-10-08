using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Interfaz;

/// <summary>Los totales del turno según la caja principal cuando la caja los pidió; el diario suma lo que pasó después.</summary>
public sealed record BaseTurno(Guid TurnoId, long FondoCentavos, AnotacionDiario Totales);

/// <summary>
/// La caja sin la caja principal (docs/fase-1-varias-cajas.md). Revisa cada pocos segundos si el servicio contesta;
/// mientras no, vende con su copia del catálogo y su propio folio, y todo lo que hace queda en la bandeja. Al volver la
/// conexión sube la bandeja en orden y, hasta que termina, sigue trabajando por su cuenta (así sus folios no chocan).
/// </summary>
public sealed class Contingencia
{
    private static readonly TimeSpan Latido = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EsperaLatido = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PaqueteViejo = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ReintentoErrores = TimeSpan.FromMinutes(5);

    private sealed record AutorizacionLocal(Guid AutorizadorId, AccionControlada Accion, long? MontoCentavos, DateTime VenceUtc);

    /// <summary>El paquete con sus índices, para buscar y cobrar al instante.</summary>
    private sealed class Copia
    {
        public Copia(PaqueteContingenciaDto p)
        {
            Paquete = p;
            PorVariante = p.Productos.ToDictionary(x => x.VarianteId);
            PorCodigo = new Dictionary<string, CodigoLocalDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in p.Codigos)
                PorCodigo.TryAdd(c.Codigo, c);
            var codigos = p.Codigos.ToLookup(c => c.VarianteId);
            Normalizado = p.Productos.ToDictionary(x => x.VarianteId,
                x => Texto.Normalizar($"{x.Descripcion} {string.Join(" ", codigos[x.VarianteId].Select(c => c.Codigo))}"));
            Presentaciones = p.Productos.SelectMany(x => x.Presentaciones).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
            Usuarios = p.Usuarios.ToDictionary(u => u.Usuario.Trim().ToLowerInvariant());
            UsuariosPorId = p.Usuarios.ToDictionary(u => u.Id);
            Reglas = p.Reglas.Select(r => new ReglaPermiso
            {
                Accion = r.Accion, Rol = r.Rol, Nivel = r.Nivel, UmbralCentavos = r.UmbralCentavos, PideMotivo = r.PideMotivo, SucursalId = p.Caja.SucursalId,
            }).ToList();
        }

        public PaqueteContingenciaDto Paquete { get; }
        public Dictionary<Guid, ProductoVentaDto> PorVariante { get; }
        public Dictionary<string, CodigoLocalDto> PorCodigo { get; }
        public Dictionary<Guid, string> Normalizado { get; }
        public Dictionary<Guid, PresentacionDto> Presentaciones { get; }
        public Dictionary<string, UsuarioLocalDto> Usuarios { get; }
        public Dictionary<Guid, UsuarioLocalDto> UsuariosPorId { get; }
        public List<ReglaPermiso> Reglas { get; }
    }

    private readonly AlmacenLocal _almacen;
    private readonly Transporte _http;
    private readonly ConexionCaja _conexion;
    private readonly TimeProvider _reloj;
    private readonly SemaphoreSlim _subida = new(1, 1);
    private readonly Lock _candado = new();
    private readonly Dictionary<Guid, AutorizacionLocal> _autorizaciones = [];
    private readonly Dictionary<string, (int Fallos, DateTime? Hasta)> _intentos = [];
    private Copia? _copia;
    private DateTime _paqueteBajadoUtc = DateTime.MinValue;
    private DateTime _ultimoReintentoUtc = DateTime.MinValue;
    private bool _responde = true;
    private CancellationTokenSource? _alto;

    public Contingencia(AlmacenLocal almacen, HttpClient http, ConexionCaja conexion, TimeProvider? reloj = null)
    {
        _almacen = almacen;
        _http = new Transporte(http, conexion);
        _conexion = conexion;
        _reloj = reloj ?? TimeProvider.System;
        if (almacen.Leer<PaqueteContingenciaDto>("paquete") is { } paquete)
            _copia = new Copia(paquete);
        (Pendientes, ConError, UltimoError, ImpresionesPorSubir) = almacen.Contar();
    }

    /// <summary>Cambió la conexión o la bandeja: la pantalla vuelve a dibujar la franja.</summary>
    public event Action? Cambio;

    public bool ServidorResponde => _responde;

    /// <summary>Se trabaja con la caja principal solo si contesta y ya no queda nada por subir.</summary>
    public bool EnLinea => _responde && Pendientes == 0;

    public int Pendientes { get; private set; }
    public int ConError { get; private set; }
    public string? UltimoError { get; private set; }

    /// <summary>Trabajos de impresión medidos en esta PC que todavía no llegan a la caja principal.</summary>
    public int ImpresionesPorSubir { get; private set; }
    public PaqueteContingenciaDto? Paquete => _copia?.Paquete;
    public int UltimoFolio => _almacen.Leer<int?>("folio") ?? 0;

    private DateTime Ahora => _reloj.GetUtcNow().UtcDateTime;

    // ── Latido, bandeja y copia local ───────────────────────────────────────

    public void Iniciar()
    {
        if (_alto is not null)
            return;
        _alto = new CancellationTokenSource();
        _ = Task.Run(() => LatirAsync(_alto.Token));
    }

    public void Detener() => _alto?.Cancel();

    private async Task LatirAsync(CancellationToken ct)
    {
        using var reloj = new PeriodicTimer(Latido);
        try
        {
            do
            {
                try
                {
                    await LatidoAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // El latido nunca tumba la caja: lo intenta otra vez en unos segundos.
                }
            }
            while (await reloj.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Se cerró la caja.
        }
    }

    /// <summary>Pregunta si la caja principal contesta; si sí, sube lo pendiente y renueva la copia si cambió.</summary>
    public async Task LatidoAsync()
    {
        if (!_conexion.Lista || _conexion.CajaId is null)
            return;
        EstadoServicioDto? estado;
        try
        {
            estado = await _http.GetAsync<EstadoServicioDto>("api/estado", espera: EsperaLatido);
        }
        catch (ErrorCaja)
        {
            Fijar(responde: false);
            return;
        }
        Fijar(responde: true);
        if (Pendientes > 0 || ImpresionesPorSubir > 0 || (ConError > 0 && Ahora - _ultimoReintentoUtc > ReintentoErrores))
            await SubirAsync();
        if (_responde && (Paquete is null || Paquete.Version != estado!.VersionDatos || Ahora - _paqueteBajadoUtc > PaqueteViejo))
            await ActualizarPaqueteAsync();
    }

    public void MarcarSinConexion() => Fijar(responde: false);

    /// <summary>
    /// Sube la bandeja en orden. Lo que el servicio rechaza queda marcado con su motivo (para revisarlo) y se reintenta
    /// más tarde; nada se borra. Al final avisa el último folio usado, para que la serie siga después de él.
    /// </summary>
    public async Task SubirAsync()
    {
        if (!await _subida.WaitAsync(0))
            return;
        try
        {
            _ultimoReintentoUtc = Ahora;
            foreach (var elemento in _almacen.PorSubir(conErrores: true))
            {
                try
                {
                    await _http.PostJsonAsync(elemento.Ruta, elemento.Json);
                    _almacen.MarcarSubido(elemento.Orden, Ahora);
                }
                catch (ErrorCaja ex) when (ex.Codigo != "sin-conexion")
                {
                    _almacen.MarcarError(elemento.Orden, ex.Message);
                }
            }
            if (_conexion.CajaId is { } caja && UltimoFolio > 0)
                await _http.PostAsync<object>($"api/cajas/{caja}/folio-usado", new FolioUsadoDto(UltimoFolio), sinRespuesta: true);
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            Fijar(responde: false);
        }
        catch (ErrorCaja)
        {
            // El aviso del folio falló por otra razón: se repite en la siguiente subida.
        }
        finally
        {
            _subida.Release();
            Recontar();
        }
    }

    /// <summary>Baja la copia para vender sin conexión: catálogo, usuarios, reglas, ticket y el último folio.</summary>
    public async Task ActualizarPaqueteAsync()
    {
        if (_conexion.CajaId is not { } caja)
            return;
        try
        {
            if (await _http.GetAsync<PaqueteContingenciaDto>($"api/cajas/{caja}/paquete") is not { } paquete)
                return;
            using (var lote = _almacen.Lote())
            {
                lote.Guardar("paquete", paquete).Guardar("folio", Math.Max(UltimoFolio, paquete.UltimoFolio)).Confirmar();
            }
            _copia = new Copia(paquete);
            _paqueteBajadoUtc = Ahora;
            Cambio?.Invoke();
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            Fijar(responde: false);
        }
        catch (ErrorCaja)
        {
            // Se intenta otra vez con el siguiente latido.
        }
    }

    private void Fijar(bool responde)
    {
        if (_responde == responde)
            return;
        _responde = responde;
        Cambio?.Invoke();
    }

    private void Recontar()
    {
        (Pendientes, ConError, UltimoError, ImpresionesPorSubir) = _almacen.Contar();
        Cambio?.Invoke();
    }

    /// <summary>
    /// Un trabajo que midió el agente de esta PC: queda en la bandeja (sin bloquear la caja) y se sube en seguida; sin
    /// conexión, sube cuando vuelva.
    /// </summary>
    public void EncolarImpresion(TrabajoMedidoDto trabajo)
    {
        using (var lote = _almacen.Lote())
            lote.Encolar(trabajo.Id, "api/impresiones", trabajo, bloquea: false).Confirmar();
        Recontar();
        if (_responde)
            _ = Task.Run(SubirAsync);
    }

    // ── Lo que se hace sin conexión ─────────────────────────────────────────

    private Copia Datos => _copia ?? throw new ErrorCaja(
        "No hay conexión con la caja principal y esta caja todavía no tiene su copia para vender sin ella.", "sin-conexion");

    private CajaDto Caja => Datos.Paquete.Caja;

    public List<CajaDto> Cajas() => [Caja];

    public SesionDto Entrar(string usuario, string pin)
    {
        var u = Identificar(usuario, pin);
        return new SesionDto(u.Id, u.Nombre, u.Rol);
    }

    public TurnoDto? Turno(Guid cajaId) => _almacen.Leer<TurnoDto>("turno") is { } turno && turno.CajaId == cajaId ? turno : null;

    public TurnoDto AbrirTurno(SolicitudTurno s)
    {
        if (Turno(s.CajaId) is not null)
            throw new InvalidOperationException($"La {Caja.Nombre} ya tiene un turno abierto: primero el corte del turno anterior.");
        if (s.FondoCentavos < 0)
            throw new InvalidOperationException("El fondo no puede ser negativo.");
        var ahora = Ahora;
        var id = s.Id ?? Ids.Nuevo();
        var turno = new TurnoDto(id, s.CajaId, s.CajeroId, Nombre(s.CajeroId), HoraMexico.ALocal(ahora), s.FondoCentavos);
        using (var lote = _almacen.Lote())
        {
            lote.Encolar(id, "api/bandeja/turnos", new TurnoContingenciaDto(id, s.CajaId, s.CajeroId, s.FondoCentavos, ahora))
                .Guardar("turno", turno)
                .Guardar("base", new BaseTurno(id, s.FondoCentavos, new AnotacionDiario(Guid.Empty, id)))
                .LimpiarDiario()
                .Confirmar();
        }
        Recontar();
        return turno;
    }

    public ComprobanteMovimientoDto Movimiento(Guid turnoId, SolicitudMovimiento m)
    {
        TurnoRequerido(turnoId);
        if (m.MontoCentavos <= 0)
            throw new InvalidOperationException("El monto debe ser mayor a cero.");
        if (string.IsNullOrWhiteSpace(m.Motivo))
            throw new InvalidOperationException("Toda entrada o salida de efectivo lleva motivo.");
        var entrada = m.Tipo == TipoMovimientoCaja.Entrada;
        var accion = entrada ? AccionControlada.EntradaEfectivo : AccionControlada.SalidaEfectivo;
        Requerir(Evaluar(m.UsuarioId, accion, m.MontoCentavos));

        var ahora = Ahora;
        var id = m.Id ?? Ids.Nuevo();
        var motivo = m.Motivo.Trim();
        var bitacora = Registro(m.UsuarioId, accion, $"{(entrada ? "Entrada" : "Salida")} de efectivo. Motivo: {motivo}", m.MontoCentavos);
        using (var lote = _almacen.Lote())
        {
            lote.Encolar(id, $"api/bandeja/turnos/{turnoId}/movimientos", new MovimientoContingenciaDto(id, m.Tipo, m.MontoCentavos, motivo, m.UsuarioId, ahora))
                .Encolar(bitacora.Id, "api/bandeja/bitacora", bitacora)
                .Anotar(new AnotacionDiario(id, turnoId, Entradas: entrada ? m.MontoCentavos : 0, Salidas: entrada ? 0 : m.MontoCentavos))
                .Confirmar();
        }
        Recontar();
        return new ComprobanteMovimientoDto(id.ToString()[^6..].ToUpperInvariant(), m.Tipo, m.MontoCentavos, motivo, HoraMexico.ALocal(ahora),
            Caja.Nombre, Nombre(m.UsuarioId), Datos.Paquete.Encabezado);
    }

    public EfectivoCajaDto Efectivo(Guid turnoId)
    {
        var corte = Corte(TurnoRequerido(turnoId), TipoCorte.X, "", null);
        return new EfectivoCajaDto(corte.EsperadoCentavos, Datos.Paquete.LimiteEfectivoCentavos);
    }

    public void Bitacora(SolicitudBitacora s)
    {
        var registro = Registro(s.UsuarioId, s.Accion, s.Detalle, s.MontoCentavos);
        Encolar(registro);
    }

    /// <summary>Los clientes registrados de la copia local, para elegir uno sin conexión.</summary>
    public List<ClienteRegistradoDto> Clientes() => (Datos.Paquete.Registrados ?? Datos.Paquete.Clientes)?.Where(c => c.Activo).ToList() ?? [];

    public DecisionDto Permiso(SolicitudPermiso s)
    {
        var decision = Evaluar(s.UsuarioId, s.Accion, s.MontoCentavos);
        return new DecisionDto(decision.Nivel, decision.PideMotivo);
    }

    /// <summary>Con la copia local de los PIN. Vale una vez, como en línea; queda en la bitácora al usarse.</summary>
    public AutorizacionDto Autorizar(SolicitudAutorizacion s)
    {
        var autorizador = Identificar(s.Usuario, s.Pin);
        if (!Permisos.PuedeAutorizar(Datos.Reglas, autorizador.Rol, Caja.SucursalId, s.Accion, s.MontoCentavos))
        {
            Encolar(Registro(s.SolicitanteId, s.Accion, $"Autorización negada: {s.Detalle}", s.MontoCentavos, autorizador.Id, ResultadoBitacora.Negado));
            throw new InvalidOperationException($"{autorizador.Nombre} no puede autorizar esto.");
        }
        var id = Ids.Nuevo();
        var vence = Ahora + Autorizacion.VigenciaDe(s.Accion);
        lock (_candado)
            _autorizaciones[id] = new AutorizacionLocal(autorizador.Id, s.Accion, s.MontoCentavos, vence);
        return new AutorizacionDto(id, vence, autorizador.Nombre);
    }

    public CorteDto CorteX(Guid turnoId, Guid usuarioId) => Corte(TurnoRequerido(turnoId), TipoCorte.X, Nombre(usuarioId), null);

    /// <summary>El encargado cuenta y la caja cierra el turno con su propia cuenta; se cuadra al subir.</summary>
    public CorteDto CorteZ(Guid turnoId, SolicitudCorteZ s)
    {
        var turno = TurnoRequerido(turnoId);
        var encargado = Identificar(s.EncargadoUsuario, s.EncargadoPin);
        if (Permisos.Evaluar(Datos.Reglas, encargado.Rol, Caja.SucursalId, AccionControlada.CorteZ).Nivel != NivelPermiso.Permitido)
        {
            Encolar(Registro(encargado.Id, AccionControlada.CorteZ, "Intento sin permiso", null, resultado: ResultadoBitacora.Negado));
            throw new InvalidOperationException("Esto lo autoriza el encargado o el dueño.");
        }
        if (s.Conteo.Any(c => !Denominaciones.Mexico.Contains(c.Key) || c.Value < 0))
            throw new InvalidOperationException("Conteo con una denominación que no existe o una cantidad negativa.");

        var corte = Corte(turno, TipoCorte.Z, encargado.Nombre, s.Conteo);
        var id = Ids.Nuevo();
        using (var lote = _almacen.Lote())
        {
            lote.Encolar(id, $"api/bandeja/turnos/{turnoId}/corte-z", new CorteContingenciaDto(id, encargado.Id, s.Conteo, Ahora))
                .Borrar("turno").Borrar("base").LimpiarDiario()
                .Confirmar();
        }
        Recontar();
        return corte;
    }

    public ProductoVentaDto? PorCodigo(string codigo) =>
        Datos.PorCodigo.TryGetValue(codigo.Trim(), out var c) && Datos.PorVariante.TryGetValue(c.VarianteId, out var p)
            ? p with { Presentacion = p.Presentaciones.FirstOrDefault(x => x.Id == c.PresentacionId) ?? p.Presentacion }
            : null;

    public ProductoVentaDto? PorVariante(Guid varianteId) => Datos.PorVariante.GetValueOrDefault(varianteId);

    /// <summary>Igual que en el servicio: cada palabra tiene que aparecer, sin importar acentos ni orden.</summary>
    public List<ResultadoBusquedaDto> Buscar(string texto, int maximo = 30)
    {
        var palabras = Texto.Normalizar(texto).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (palabras.Length == 0)
            return [];
        var datos = Datos;
        var consulta = Texto.Normalizar(texto);
        return datos.Paquete.Productos
            .Where(p => palabras.All(datos.Normalizado[p.VarianteId].Contains))
            .OrderByDescending(p => p.Codigo is not null && string.Equals(p.Codigo, texto.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => datos.Normalizado[p.VarianteId].StartsWith(consulta, StringComparison.Ordinal))
            .ThenBy(p => p.Descripcion, StringComparer.CurrentCultureIgnoreCase)
            .Take(maximo)
            .Select(p => new ResultadoBusquedaDto(p.VarianteId, p.Descripcion, p.Codigo, PrecioMenudeo(p), p.Existencia))
            .ToList();
    }

    /// <summary>
    /// Cobra con la copia del catálogo y el siguiente folio de la caja. Los precios cambiados se revisan con las reglas
    /// locales y las autorizaciones que se dieron sin conexión.
    /// </summary>
    public TicketDto Cobrar(SolicitudCobro s)
    {
        var datos = Datos;
        var caja = Caja;
        var turno = TurnoRequerido(s.TurnoId);
        if (s.Impresiones is { Count: > 0 })
            throw new InvalidOperationException("Las impresiones se cobran con conexión con la caja principal: quítalas del ticket o espera a que vuelva.");
        if (s.Cobros.Any(c => c.Forma == FormaPago.Credito))
            throw new InvalidOperationException("Sin conexión todavía no se fía: cóbralo de otra forma o espera a que vuelva la conexión.");
        var ahora = Ahora;
        // El cliente va antes que las líneas: con mayoreo siempre, sus precios son otros.
        ClienteRegistradoDto? cliente = null;
        if (s.ClienteId is { } clienteId && (cliente = Clientes().FirstOrDefault(c => c.Id == clienteId)) is null)
            throw new InvalidOperationException("Ese cliente no está en la copia de esta caja: quítalo del ticket (CTRL+L).");
        // Los mismos precios que calculó la pantalla: con el mayoreo del cliente si ella lo usó.
        var venta = new Venta
        {
            Id = s.VentaId ?? Ids.Nuevo(), SucursalId = caja.SucursalId, CajaId = caja.Id, TurnoId = turno.Id, CajeroId = s.CajeroId, Serie = caja.Serie,
            CreadaUtc = ahora, ClienteId = cliente?.Id, MayoreoDeCliente = cliente is not null && s.MayoreoDeCliente == true,
        };
        foreach (var l in s.Lineas)
        {
            var cantidad = new Cantidad(l.CantidadMilesimas);
            if (l.VarianteId is not { } varianteId)
            {
                var comun = venta.AgregarArticuloComun(l.Descripcion ?? "", l.PrecioCentavos ?? 0, cantidad);
                venta.AplicarDescuentoSolicitado(comun, l.DescuentoCentesimas, l.DescuentoCentavos, l.MotivoDescuento);
                continue;
            }
            var producto = datos.PorVariante.GetValueOrDefault(varianteId)
                ?? throw new InvalidOperationException($"«{l.Descripcion ?? "Un producto"}» no está en la copia del catálogo de esta caja.");
            var agregada = venta.AgregarProducto(producto.ParaVenta(), producto.PresentacionModelo(l.PresentacionId), cantidad, producto.PreciosModelo());
            if (l.PrecioCentavos is { } precio && precio != agregada.PrecioCentavos)
                venta.CambiarPrecio(agregada, precio, l.AutorizacionId);
            venta.AplicarDescuentoSolicitado(agregada, l.DescuentoCentesimas, l.DescuentoCentavos, l.MotivoDescuento);
        }
        venta.ValidarCobros(s.Cobros);

        var bitacora = new List<BitacoraContingenciaDto>();
        foreach (var cambiada in venta.Lineas.Where(x => x.PrecioCambiado))
        {
            var decision = Evaluar(s.CajeroId, AccionControlada.CambiarPrecio, cambiada.ImporteCentavos);
            Guid? autorizo = null;
            if (decision.Nivel == NivelPermiso.RequiereAutorizacion)
                autorizo = UsarAutorizacion(cambiada.AutorizacionPrecioId, AccionControlada.CambiarPrecio, cambiada.ImporteCentavos)
                    ?? throw new ErrorCaja("Esto requiere la autorización del encargado.", "requiere-autorizacion");
            else
                Requerir(decision);
            bitacora.Add(Registro(s.CajeroId, AccionControlada.CambiarPrecio,
                $"Precio de «{cambiada.Descripcion}»: {cambiada.PrecioOriginalCentavos / 100m:N2} → {cambiada.PrecioCentavos / 100m:N2}",
                cambiada.ImporteCentavos, autorizo, autorizo is null ? ResultadoBitacora.Hecho : ResultadoBitacora.Autorizado, venta.Id));
        }

        // Descuentos: la misma regla con la copia local. Sin conexión no se sabe el costo: eso no se revisa aquí.
        if (venta.DescuentoCentavos > 0)
        {
            var revisar = venta.DescuentoARevisar(cliente?.DescuentoCentesimas is > 0 and var d ? d : null);
            Guid? autorizo = null;
            if (revisar > 0)
            {
                var decision = Evaluar(s.CajeroId, AccionControlada.Descuento, revisar);
                if (decision.Nivel == NivelPermiso.RequiereAutorizacion)
                    autorizo = UsarAutorizacion(s.AutorizacionDescuentoId, AccionControlada.Descuento, revisar)
                        ?? throw new ErrorCaja("Este descuento requiere la autorización del encargado.", "requiere-autorizacion");
                else
                    Requerir(decision);
            }
            bitacora.Add(Registro(s.CajeroId, AccionControlada.Descuento,
                $"Descuento de {Formato.Dinero(venta.DescuentoCentavos)}" + (cliente is null ? "" : $" a {cliente.Nombre}, cliente registrado")
                + $": {string.Join("; ", venta.Lineas.Where(l => l.TieneDescuento).Select(l => l.MotivoDescuento).Distinct())}",
                venta.DescuentoCentavos, autorizo, autorizo is null ? ResultadoBitacora.Hecho : ResultadoBitacora.Autorizado, venta.Id));
        }

        var folio = UltimoFolio + 1;
        venta.Pagar(folio, s.Cobros, ahora);
        venta.EnContingencia = true;
        var ticket = ArmadoTicket.Desde(venta, caja.Nombre, Nombre(s.CajeroId),
            id => datos.Presentaciones.TryGetValue(id, out var p) ? (p.Nombre, p.EsBase) : null,
            datos.Paquete.Encabezado, datos.Paquete.Pie, datos.Paquete.Logo, cliente);
        var subida = new VentaContingenciaDto(venta.Id, caja.Id, turno.Id, s.CajeroId, folio, ahora,
            venta.Lineas.Select(x => new LineaContingenciaDto(x.VarianteId, x.PresentacionId, x.Descripcion, x.Cantidad.Milesimas, x.PrecioCentavos,
                x.DescuentoCentesimas, x.DescuentoCentesimas is null && x.TieneDescuento ? x.DescuentoCentavos : null, x.MotivoDescuento)).ToList(),
            s.Cobros, venta.ClienteId, venta.RedondeoCentavos, venta.MayoreoDeCliente);

        using (var lote = _almacen.Lote())
        {
            lote.Encolar(venta.Id, "api/bandeja/ventas", subida);
            foreach (var registro in bitacora)
                lote.Encolar(registro.Id, "api/bandeja/bitacora", registro);
            lote.Anotar(Anotacion(turno.Id, ticket)).Guardar("folio", folio).Confirmar();
        }
        Recontar();
        return ticket;
    }

    // ── Lo que pasó en línea, para la cuenta del turno ──────────────────────

    /// <summary>Al entrar en línea se toma la cuenta del servicio como base; el diario empieza de nuevo.</summary>
    public async Task TurnoEnLineaAsync(TurnoDto? turno)
    {
        if (turno is null)
        {
            using var lote = _almacen.Lote();
            lote.Borrar("turno").Borrar("base").LimpiarDiario().Confirmar();
            return;
        }
        CorteDto? resumen = null;
        try
        {
            resumen = await _http.GetAsync<CorteDto>($"api/turnos/{turno.Id}/resumen");
        }
        catch (ErrorCaja)
        {
            // Sin resumen se queda la base que había: el diario sigue sumando.
        }
        using (var lote = _almacen.Lote())
        {
            lote.Guardar("turno", turno);
            if (resumen is not null)
                lote.Guardar("base", new BaseTurno(turno.Id, resumen.FondoCentavos, new AnotacionDiario(Guid.Empty, turno.Id, resumen.Ventas,
                    resumen.EfectivoVentasCentavos, resumen.TarjetaCentavos, resumen.TransferenciaCentavos, resumen.EntradasCentavos,
                    resumen.SalidasCentavos, resumen.DevolucionesCentavos, resumen.Cancelaciones, resumen.RedondeoCentavos, resumen.CreditoCentavos,
                    resumen.Abonos, resumen.AbonosEfectivoCentavos, resumen.AbonosTarjetaCentavos, resumen.AbonosTransferenciaCentavos))).LimpiarDiario();
            lote.Confirmar();
        }
    }

    public void TurnoAbiertoEnLinea(TurnoDto turno)
    {
        using var lote = _almacen.Lote();
        lote.Guardar("turno", turno).Guardar("base", new BaseTurno(turno.Id, turno.FondoCentavos, new AnotacionDiario(Guid.Empty, turno.Id)))
            .LimpiarDiario().Confirmar();
    }

    public void TurnoCerradoEnLinea()
    {
        using var lote = _almacen.Lote();
        lote.Borrar("turno").Borrar("base").LimpiarDiario().Confirmar();
    }

    public void CobroEnLinea(Guid turnoId, TicketDto ticket)
    {
        var folio = int.TryParse(ticket.Folio[(ticket.Folio.LastIndexOf('-') + 1)..], out var f) ? f : 0;
        using var lote = _almacen.Lote();
        lote.Anotar(Anotacion(turnoId, ticket)).Guardar("folio", Math.Max(UltimoFolio, folio)).Confirmar();
    }

    public void MovimientoEnLinea(Guid turnoId, Guid id, ComprobanteMovimientoDto m)
    {
        var entrada = m.Tipo == TipoMovimientoCaja.Entrada;
        using var lote = _almacen.Lote();
        lote.Anotar(new AnotacionDiario(id, turnoId, Entradas: entrada ? m.MontoCentavos : 0, Salidas: entrada ? 0 : m.MontoCentavos)).Confirmar();
    }

    /// <summary>
    /// El reembolso sale del efectivo del turno abierto de esta caja; una cancelación deja de contar como venta, y su
    /// tarjeta, su transferencia y lo fiado dejan de contar en el corte (como en el del servicio, que solo suma las vigentes).
    /// </summary>
    public void DevolucionEnLinea(ComprobanteDevolucionDto d)
    {
        if (_almacen.Leer<TurnoDto>("turno") is not { } turno)
            return;
        using var lote = _almacen.Lote();
        lote.Anotar(new AnotacionDiario(Ids.Nuevo(), turno.Id, Ventas: d.EsCancelacion ? -1 : 0, Devoluciones: d.ReembolsoEfectivoCentavos,
            Cancelaciones: d.EsCancelacion ? 1 : 0, Redondeo: -d.RedondeoCentavos,
            Tarjeta: d.EsCancelacion ? -d.TarjetaCentavos : 0, Transferencia: d.EsCancelacion ? -d.TransferenciaCentavos : 0,
            Credito: d.EsCancelacion ? -d.AlCreditoCentavos : 0)).Confirmar();
    }

    /// <summary>Un abono recibido en línea entra a la cuenta del turno: en efectivo suma al esperado.</summary>
    public void AbonoEnLinea(Guid turnoId, ComprobanteAbonoDto c)
    {
        using var lote = _almacen.Lote();
        lote.Anotar(AnotacionAbono(c.AbonoId, turnoId, c, 1)).Confirmar();
    }

    /// <summary>Un abono cancelado en línea sale de la cuenta del turno (con su propio renglón: el del abono se queda).</summary>
    public void AbonoCanceladoEnLinea(Guid turnoId, ComprobanteAbonoDto c)
    {
        using var lote = _almacen.Lote();
        lote.Anotar(AnotacionAbono(Ids.Nuevo(), turnoId, c, -1)).Confirmar();
    }

    private static AnotacionDiario AnotacionAbono(Guid id, Guid turnoId, ComprobanteAbonoDto c, int signo) =>
        new(id, turnoId, Abonos: signo, Redondeo: signo * c.RedondeoCentavos,
            AbonosEfectivo: c.Forma == FormaPago.Efectivo ? signo * c.CobradoCentavos : 0,
            AbonosTarjeta: c.Forma == FormaPago.Tarjeta ? signo * c.MontoCentavos : 0,
            AbonosTransferencia: c.Forma == FormaPago.Transferencia ? signo * c.MontoCentavos : 0);

    // ── Apoyo ───────────────────────────────────────────────────────────────

    private TurnoDto TurnoRequerido(Guid turnoId) =>
        _almacen.Leer<TurnoDto>("turno") is { } turno && turno.Id == turnoId ? turno : throw new InvalidOperationException("El turno ya no está abierto en esta caja.");

    /// <summary>Base del servicio + lo anotado después; igual que <see cref="CalculoCorte"/>.</summary>
    private CorteDto Corte(TurnoDto turno, TipoCorte tipo, string realizadoPor, IReadOnlyDictionary<long, int>? conteo)
    {
        var base_ = _almacen.Leer<BaseTurno>("base") is { } b && b.TurnoId == turno.Id ? b : new BaseTurno(turno.Id, turno.FondoCentavos, new AnotacionDiario(Guid.Empty, turno.Id));
        var d = _almacen.Diario(turno.Id);
        var t = base_.Totales;
        var efectivo = t.Efectivo + d.Efectivo;
        var entradas = t.Entradas + d.Entradas;
        var salidas = t.Salidas + d.Salidas;
        var devoluciones = t.Devoluciones + d.Devoluciones;
        var abonosEfectivo = t.AbonosEfectivo + d.AbonosEfectivo;
        var esperado = base_.FondoCentavos + efectivo + abonosEfectivo + entradas - salidas - devoluciones;
        long? contado = conteo?.Sum(c => c.Key * c.Value);
        return new CorteDto(tipo, HoraMexico.ALocal(Ahora), turno.Cajero, realizadoPor, t.Ventas + d.Ventas, base_.FondoCentavos, efectivo, entradas, salidas,
            t.Tarjeta + d.Tarjeta, t.Transferencia + d.Transferencia, esperado, contado, contado - esperado, devoluciones, t.Cancelaciones + d.Cancelaciones,
            EnContingencia: true, RedondeoCentavos: t.Redondeo + d.Redondeo, CreditoCentavos: t.Credito + d.Credito, Abonos: t.Abonos + d.Abonos,
            AbonosEfectivoCentavos: abonosEfectivo, AbonosTarjetaCentavos: t.AbonosTarjeta + d.AbonosTarjeta,
            AbonosTransferenciaCentavos: t.AbonosTransferencia + d.AbonosTransferencia);
    }

    private static AnotacionDiario Anotacion(Guid turnoId, TicketDto ticket) =>
        new(ticket.VentaId ?? Ids.Nuevo(), turnoId, Ventas: 1,
            Efectivo: ticket.Cobros.Where(c => c.Forma == FormaPago.Efectivo).Sum(c => c.MontoCentavos),
            Tarjeta: ticket.Cobros.Where(c => c.Forma == FormaPago.Tarjeta).Sum(c => c.MontoCentavos),
            Transferencia: ticket.Cobros.Where(c => c.Forma == FormaPago.Transferencia).Sum(c => c.MontoCentavos),
            Redondeo: ticket.RedondeoCentavos, Credito: ticket.CreditoCentavos);

    private long PrecioMenudeo(ProductoVentaDto p)
    {
        var porUnidad = p.Precios.Where(x => x.PresentacionId is null).ToList();
        var deSucursal = porUnidad.Where(x => x.SucursalId == Caja.SucursalId).ToList();
        return (deSucursal.Count > 0 ? deSucursal : porUnidad.Where(x => x.SucursalId is null)).MinBy(x => x.CantidadMinimaMilesimas)?.PrecioCentavos ?? 0;
    }

    private string Nombre(Guid usuarioId) => Datos.UsuariosPorId.TryGetValue(usuarioId, out var u) ? u.Nombre : "—";

    private DecisionPermiso Evaluar(Guid usuarioId, AccionControlada accion, long? monto) =>
        Datos.UsuariosPorId.TryGetValue(usuarioId, out var u)
            ? Permisos.Evaluar(Datos.Reglas, u.Rol, Caja.SucursalId, accion, monto)
            : throw new InvalidOperationException("El usuario no está en la copia de esta caja.");

    private static void Requerir(DecisionPermiso decision)
    {
        switch (decision.Nivel)
        {
            case NivelPermiso.NoPermitido:
                throw new ErrorCaja("No tienes permiso para esto.", "no-permitido");
            case NivelPermiso.RequiereAutorizacion:
                throw new ErrorCaja("Esto requiere la autorización del encargado.", "requiere-autorizacion");
        }
    }

    /// <summary>La gasta: de esa acción, sin usar, vigente y por un monto que alcance. Devuelve quién autorizó.</summary>
    private Guid? UsarAutorizacion(Guid? id, AccionControlada accion, long monto)
    {
        lock (_candado)
        {
            if (id is not { } clave || !_autorizaciones.TryGetValue(clave, out var a) || a.Accion != accion || Ahora > a.VenceUtc || monto > a.MontoCentavos)
                return null;
            _autorizaciones.Remove(clave);
            return a.AutorizadorId;
        }
    }

    /// <summary>Usuario y PIN contra la copia local, con el mismo bloqueo por intentos que en el servicio.</summary>
    private UsuarioLocalDto Identificar(string usuario, string pin)
    {
        var clave = usuario.Trim().ToLowerInvariant();
        lock (_candado)
        {
            var (fallos, hasta) = _intentos.GetValueOrDefault(clave);
            if (hasta is { } h && Ahora < h)
                throw new InvalidOperationException("Demasiados intentos: espera unos minutos.");
            if (Datos.Usuarios.TryGetValue(clave, out var u) && u.Activo && Pin.Verificar(pin, u.PinHash))
            {
                _intentos.Remove(clave);
                return u;
            }
            if (++fallos >= Usuario.IntentosPermitidos)
            {
                _intentos[clave] = (0, Ahora + Usuario.TiempoDeBloqueo);
                throw new InvalidOperationException("Demasiados intentos: espera unos minutos.");
            }
            _intentos[clave] = (fallos, null);
            throw new InvalidOperationException("Usuario o PIN incorrecto.");
        }
    }

    private BitacoraContingenciaDto Registro(
        Guid? usuarioId, AccionControlada accion, string detalle, long? monto, Guid? autorizo = null, ResultadoBitacora resultado = ResultadoBitacora.Hecho,
        Guid? ventaId = null) =>
        new(Ids.Nuevo(), Caja.SucursalId, Caja.Id, usuarioId, accion, detalle, monto, autorizo, resultado, Ahora, ventaId);

    private void Encolar(BitacoraContingenciaDto registro)
    {
        using (var lote = _almacen.Lote())
            lote.Encolar(registro.Id, "api/bandeja/bitacora", registro).Confirmar();
        Recontar();
    }
}
