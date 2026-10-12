using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Clientes;
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
    private static readonly TimeSpan RefrescoCredito = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EsperaCreditos = TimeSpan.FromSeconds(10);

    /// <summary>Lo anotado que el servicio no tiene después de esto (y no pasó por la bandeja), ya no lo va a tener.</summary>
    private static readonly TimeSpan CreditoHuerfano = TimeSpan.FromMinutes(2);

    /// <summary>Cuánto se recuerda lo ya confirmado (por si se vuelve a mandar con el mismo Id).</summary>
    private static readonly TimeSpan RecordarConfirmado = TimeSpan.FromDays(1);

    private const string SinSaldos = "Esta caja todavía no conoce los saldos del crédito: fiar y abonar esperan a que vuelva la conexión.";

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
            // Las familias de la búsqueda, igual que en línea: las marcas salen de esta copia del catálogo.
            var nombres = new LectorDeNombres(p.Productos.Select(x => x.Descripcion), p.Marcas);
            Partes = p.Productos.ToDictionary(x => x.VarianteId, x => nombres.Leer(x.Descripcion));
            // También se busca por lo que lee la caja del nombre: «negro» encuentra «Negra», «surtido» los «V/Colores».
            Normalizado = p.Productos.ToDictionary(x => x.VarianteId,
                x => Texto.Normalizar($"{x.Descripcion} {string.Join(" ", codigos[x.VarianteId].Select(c => c.Codigo))} "
                    + string.Join(" ", Partes[x.VarianteId].Select(parte => parte.Clave))));
            Palabras = Normalizado.ToDictionary(x => x.Key, x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            Sinonimos = new SinonimosDeBusqueda((p.Sinonimos ?? []).Select(s => (s.Palabra, s.Equivale)));
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

        /// <summary>Las palabras de cada producto, para el error de dedo.</summary>
        public Dictionary<Guid, string[]> Palabras { get; }

        public SinonimosDeBusqueda Sinonimos { get; }

        public Dictionary<Guid, PresentacionDto> Presentaciones { get; }
        public Dictionary<Guid, IReadOnlyList<ParteDeNombre>> Partes { get; }
        public Dictionary<string, UsuarioLocalDto> Usuarios { get; }
        public Dictionary<Guid, UsuarioLocalDto> UsuariosPorId { get; }
        public List<ReglaPermiso> Reglas { get; }
    }

    private readonly AlmacenLocal _almacen;
    private readonly Transporte _http;
    private readonly ConexionCaja _conexion;
    private readonly TimeProvider _reloj;
    private readonly SemaphoreSlim _subida = new(1, 1);
    private readonly SemaphoreSlim _creditos = new(1, 1);
    private readonly Lock _candado = new();
    private readonly Dictionary<Guid, AutorizacionLocal> _autorizaciones = [];
    private readonly Dictionary<string, (int Fallos, DateTime? Hasta)> _intentos = [];
    private Copia? _copia;
    private DateTime _paqueteBajadoUtc = DateTime.MinValue;
    private DateTime _ultimoReintentoUtc = DateTime.MinValue;
    private bool _responde = true;
    private CancellationTokenSource? _alto;
    private SaldosCreditoDto? _saldos;
    private long? _creditosPedidos;

    /// <summary>Algo del crédito pasó en línea en esta caja (o se anotó mientras se pedían): el siguiente latido pide los saldos.</summary>
    private volatile bool _refrescarCredito;

    public Contingencia(AlmacenLocal almacen, HttpClient http, ConexionCaja conexion, TimeProvider? reloj = null)
    {
        _almacen = almacen;
        _http = new Transporte(http, conexion);
        _conexion = conexion;
        _reloj = reloj ?? TimeProvider.System;
        if (almacen.Leer<PaqueteContingenciaDto>("paquete") is { } paquete)
            _copia = new Copia(paquete);
        _saldos = almacen.Leer<SaldosCreditoDto>("saldos");
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

    /// <summary>Los últimos saldos del crédito que mandó la caja principal (sin ellos, no se fía ni se abona sin conexión).</summary>
    public SaldosCreditoDto? Saldos => _saldos;
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
            // La caja de la prueba arranca su propio servicio: hasta que conteste, no se pasa a sin conexión.
            try
            {
                await _conexion.Listo.WaitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Si no arrancó, la pantalla lo dice; el latido sigue intentando.
            }
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
        if (!_conexion.Lista || (_conexion.CajaId is null && _conexion.PideAlta))
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
        // Sin alta (la caja de la prueba): es la primera caja de la sucursal, la misma que toma la pantalla.
        if (_conexion.CajaId is null)
        {
            try
            {
                _conexion.CajaId = (await _http.GetAsync<List<CajaDto>>("api/cajas"))?.FirstOrDefault()?.Id;
            }
            catch (ErrorCaja)
            {
            }
            if (_conexion.CajaId is null)
                return;
        }
        var subio = Pendientes > 0;
        if (Pendientes > 0 || ImpresionesPorSubir > 0 || (ConError > 0 && Ahora - _ultimoReintentoUtc > ReintentoErrores))
            await SubirAsync();
        // Los saldos del crédito: cuando cambiaron, cuando acaba de subir lo suyo, o cada tanto si tiene algo por confirmar.
        var tocaCredito = _refrescarCredito || _creditosPedidos is not { } pedidos || _reloj.GetElapsedTime(pedidos) > RefrescoCredito;
        if (_responde && (_saldos is null ? tocaCredito
                : estado!.VersionCredito != _saldos.Version || ((subio || tocaCredito) && _almacen.CuentaPropia().Count > 0)))
            await ActualizarCreditosAsync();
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

    /// <summary>
    /// Pide los saldos del crédito y pregunta cuáles de lo suyo sin confirmar ya están en ellos: esos los borra (en el
    /// mismo paso que guarda los saldos, así nada se cuenta dos veces ni se pierde).
    /// </summary>
    public async Task ActualizarCreditosAsync()
    {
        if (_conexion.CajaId is not { } caja || !await _creditos.WaitAsync(0))
            return;
        _refrescarCredito = false;
        try
        {
            var enviados = _almacen.CuentaPropia().Select(f => f.Id).ToList();
            if (await _http.PostAsync<SaldosCreditoDto>($"api/cajas/{caja}/creditos", new SolicitudSaldosCredito(enviados), espera: EsperaCreditos)
                is not { } saldos)
                return;
            var incluidos = saldos.Incluidos.ToHashSet();
            using (var lote = _almacen.Lote())
            {
                lote.Guardar("saldos", saldos).ConfirmarCredito(saldos.Incluidos, Ahora - RecordarConfirmado)
                    .QuitarCreditoHuerfano(enviados.Where(id => !incluidos.Contains(id)), Ahora - CreditoHuerfano)
                    .Confirmar();
            }
            _saldos = saldos;
            // Lo que se anotó mientras se pedían (un cobro en línea a la par) puede venir ya en esta foto sin estar en
            // Incluidos: se pregunta otra vez en el siguiente latido, ya con eso.
            var enviadosSet = enviados.ToHashSet();
            if (_almacen.CuentaPropia().Any(f => !enviadosSet.Contains(f.Id)))
                _refrescarCredito = true;
            Cambio?.Invoke();
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            Fijar(responde: false);
        }
        catch (ErrorCaja)
        {
            // Una caja principal de antes (sin la ruta) u otro error: se intenta otra vez en un rato.
        }
        finally
        {
            _creditosPedidos = _reloj.GetTimestamp();
            _creditos.Release();
        }
    }

    /// <summary>
    /// Empezar de cero (la prueba): lo de la base anterior ya no corresponde (lo propio del crédito, los saldos, el turno,
    /// su diario y el último folio).
    /// </summary>
    public void OlvidarCredito()
    {
        using (var lote = _almacen.Lote())
            lote.LimpiarCredito().Borrar("saldos").Borrar("turno").Borrar("base").Borrar("folio").Borrar(ClaveEspera).LimpiarDiario().Confirmar();
        _saldos = null;
        _creditosPedidos = null;
    }

    private const string ClaveEspera = "espera";

    /// <summary>Los tickets en espera de esta caja, en el orden en que se apartaron.</summary>
    public List<TicketGuardado> TicketsEnEspera() => _almacen.Leer<List<TicketGuardado>>(ClaveEspera) ?? [];

    public void GuardarEnEspera(IReadOnlyList<TicketGuardado> tickets)
    {
        using var lote = _almacen.Lote();
        (tickets.Count == 0 ? lote.Borrar(ClaveEspera) : lote.Guardar(ClaveEspera, tickets)).Confirmar();
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

    /// <summary>
    /// La cuenta de un cliente según esta caja: la última que mandó el servicio más lo suyo sin confirmar (sin la operación
    /// <paramref name="sin"/>, la que se está haciendo). Sin los saldos, no se sabe: null.
    /// </summary>
    public CreditoClienteDto? Credito(Guid clienteId, Guid? sin = null)
    {
        if (_saldos is not { } saldos)
            return null;
        // Si no viene en los saldos, no debe nada: sus condiciones salen de la copia.
        var delServicio = saldos.Cuentas.FirstOrDefault(c => c.ClienteId == clienteId);
        if (delServicio is null && _copia is not null && Clientes().FirstOrDefault(c => c.Id == clienteId) is { } cliente)
            delServicio = new CreditoClienteDto(clienteId, 0, 0, cliente.LimiteCreditoCentavos, cliente.PlazoCreditoDias, []);
        return delServicio is null ? null
            : CuentaCredito.Estimar(delServicio, _almacen.CuentaPropia(clienteId).Where(f => f.Id != sin).Select(f => f.Local));
    }

    /// <summary>Las cuentas que conoce esta caja: las de los saldos y las de lo suyo sin confirmar.</summary>
    public List<CreditoClienteDto> Creditos()
    {
        if (_saldos is not { } saldos)
            throw new InvalidOperationException(SinSaldos);
        var ids = saldos.Cuentas.Select(c => c.ClienteId).Concat(_almacen.CuentaPropia().Select(f => f.ClienteId)).Distinct();
        return ids.Select(id => Credito(id)).OfType<CreditoClienteDto>().ToList();
    }

    public DecisionDto Permiso(SolicitudPermiso s)
    {
        var decision = Evaluar(s.UsuarioId, s.Accion, s.MontoCentavos);
        return new DecisionDto(decision.Nivel, decision.PideMotivo);
    }

    public IReadOnlyList<PermisoDeSesion> PermisosDeSesion(Guid usuarioId)
    {
        var usuario = Datos.UsuariosPorId.GetValueOrDefault(usuarioId) ?? throw new InvalidOperationException("Ese usuario no está en la copia de esta caja.");
        return Permisos.DeLaSesion(Datos.Reglas, Datos.UsuariosPorId.Values.Where(u => u.Activo).Select(u => (u.Id, u.Usuario, u.Nombre, u.Rol)), usuario.Id, usuario.Rol,
            Caja.SucursalId);
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
        // Cada pedazo vale por sí mismo o por los sinónimos de la tienda, como en línea.
        var pedazos = palabras.Select(w => (Pedazo: w, Sinonimos: datos.Sinonimos.De(w))).ToList();
        bool Directo(ProductoVentaDto p, string w) => datos.Normalizado[p.VarianteId].Contains(w, StringComparison.Ordinal);
        var candidatos = datos.Paquete.Productos
            .Where(p => pedazos.All(x => Directo(p, x.Pedazo) || x.Sinonimos.Any(s => Directo(p, s.Equivale)))).ToList();
        // Si nada coincide, un error de dedo, como en línea.
        var parecido = false;
        if (candidatos.Count == 0)
        {
            candidatos = datos.Paquete.Productos
                .Where(p => pedazos.All(x => ErrorDeDedo.Coincide(x.Pedazo, datos.Palabras[p.VarianteId]) || x.Sinonimos.Any(s => Directo(p, s.Equivale))))
                .ToList();
            parecido = candidatos.Count > 0;
        }
        string? PorSinonimo(ProductoVentaDto p) =>
            pedazos.Where(x => !Directo(p, x.Pedazo)).SelectMany(x => x.Sinonimos).Where(s => Directo(p, s.Equivale))
                .Select(s => s.Palabra.ToLowerInvariant()).FirstOrDefault();
        return candidatos
            .OrderByDescending(p => p.Codigo is not null && string.Equals(p.Codigo, texto.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => parecido ? palabras.Count(w => Directo(p, w)) : 0)
            .ThenBy(p => PorSinonimo(p) is not null)
            .ThenByDescending(p => datos.Normalizado[p.VarianteId].StartsWith(consulta, StringComparison.Ordinal))
            .ThenBy(p => p.Descripcion, StringComparer.CurrentCultureIgnoreCase)
            .Take(maximo)
            .Select(p => new ResultadoBusquedaDto(p.VarianteId, p.Descripcion, p.Codigo, PrecioMenudeo(p), p.Existencia,
                Partes: datos.Partes[p.VarianteId], Parecido: parecido, PorSinonimo: PorSinonimo(p)))
            .ToList();
    }

    /// <summary>
    /// Cobra con la copia del catálogo y el siguiente folio de la caja. Los precios cambiados, los descuentos y pasar el
    /// límite del crédito se revisan con las reglas locales y las autorizaciones que se dieron sin conexión (se gastan
    /// cuando la venta quedó guardada). Lo fiado, con el saldo que conoce esta caja.
    /// </summary>
    public TicketDto Cobrar(SolicitudCobro s)
    {
        var datos = Datos;
        var caja = Caja;
        var turno = TurnoRequerido(s.TurnoId);
        if (s.Impresiones is { Count: > 0 })
            throw new InvalidOperationException("Las impresiones se cobran con conexión con la caja principal: quítalas del ticket o espera a que vuelva.");
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
        var usadas = new List<Guid>();
        foreach (var cambiada in venta.Lineas.Where(x => x.PrecioCambiado))
        {
            var decision = Evaluar(s.CajeroId, AccionControlada.CambiarPrecio, cambiada.ImporteCentavos);
            Guid? autorizo = null;
            if (decision.Nivel == NivelPermiso.RequiereAutorizacion)
                autorizo = Autorizada(cambiada.AutorizacionPrecioId, AccionControlada.CambiarPrecio, cambiada.ImporteCentavos, usadas)
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
                    autorizo = Autorizada(s.AutorizacionDescuentoId, AccionControlada.Descuento, revisar, usadas)
                        ?? throw new ErrorCaja("Este descuento requiere la autorización del encargado.", "requiere-autorizacion");
                else
                    Requerir(decision);
            }
            bitacora.Add(Registro(s.CajeroId, AccionControlada.Descuento,
                $"Descuento de {Formato.Dinero(venta.DescuentoCentavos)}" + (cliente is null ? "" : $" a {cliente.Nombre}, cliente registrado")
                + $": {string.Join("; ", venta.Lineas.Where(l => l.TieneDescuento).Select(l => l.MotivoDescuento).Distinct())}",
                venta.DescuentoCentavos, autorizo, autorizo is null ? ResultadoBitacora.Hecho : ResultadoBitacora.Autorizado, venta.Id));
        }

        // Lo fiado: con el saldo que conoce esta caja (sin esta misma venta, si ya se había anotado al mandarla en línea).
        var aCredito = s.Cobros.Where(c => c.Forma == FormaPago.Credito).Sum(c => c.MontoCentavos);
        CreditoClienteDto? cuenta = null;
        long excesoAutorizado = 0;
        DateOnly? vence = null;
        if (aCredito > 0)
        {
            if (_almacen.CreditoConfirmado(venta.Id))
                throw new InvalidOperationException("Ese ticket ya se fió en la caja principal: espera a que vuelva la conexión y cóbralo otra vez (sale el mismo).");
            cuenta = Credito(cliente!.Id, sin: venta.Id) ?? throw new InvalidOperationException(SinSaldos);
            if (cuenta.Exceso(aCredito) is var exceso and > 0)
            {
                if (cuenta.LimiteCentavos == 0)
                    throw new InvalidOperationException(cuenta.DisponibleCentavos > 0
                        ? $"«{cliente.Nombre}» no tiene crédito: solo puede usar su saldo a favor ({Formato.Dinero(cuenta.DisponibleCentavos)})."
                        : $"«{cliente.Nombre}» no tiene crédito.");
                var decision = Evaluar(s.CajeroId, AccionControlada.ExcederLimiteCredito, exceso);
                Guid? autorizo = null;
                if (decision.Nivel == NivelPermiso.RequiereAutorizacion)
                    autorizo = Autorizada(s.AutorizacionCreditoId, AccionControlada.ExcederLimiteCredito, exceso, usadas)
                        ?? throw new ErrorCaja("Pasar el límite requiere la autorización del encargado.", "requiere-autorizacion");
                else
                    Requerir(decision);
                excesoAutorizado = exceso;
                bitacora.Add(Registro(s.CajeroId, AccionControlada.ExcederLimiteCredito,
                    $"Fió {Formato.Dinero(aCredito)} a «{cliente.Nombre}» pasando el límite por {Formato.Dinero(exceso)} "
                    + $"(según esta caja debía {Formato.Dinero(cuenta.SaldoCentavos)}, límite {Formato.Dinero(cuenta.LimiteCentavos)})",
                    exceso, autorizo, autorizo is null ? ResultadoBitacora.Hecho : ResultadoBitacora.Autorizado, venta.Id));
            }
            vence = CuentaCredito.Vence(ahora, cuenta.PlazoDias);
        }

        var folio = UltimoFolio + 1;
        venta.Pagar(folio, s.Cobros, ahora);
        venta.EnContingencia = true;
        var ticket = ArmadoTicket.Desde(venta, caja.Nombre, Nombre(s.CajeroId),
            id => datos.Presentaciones.TryGetValue(id, out var p) ? (p.Nombre, p.EsBase) : null,
            datos.Paquete.Encabezado, datos.Paquete.Pie, datos.Paquete.Logo, cliente,
            cuenta is null ? null : new CreditoTicketDto(aCredito, cuenta.SaldoCentavos + aCredito, vence, Estimado: true));
        var subida = new VentaContingenciaDto(venta.Id, caja.Id, turno.Id, s.CajeroId, folio, ahora,
            venta.Lineas.Select(x => new LineaContingenciaDto(x.VarianteId, x.PresentacionId, x.Descripcion, x.Cantidad.Milesimas, x.PrecioCentavos,
                x.DescuentoCentesimas, x.DescuentoCentesimas is null && x.TieneDescuento ? x.DescuentoCentavos : null, x.MotivoDescuento)).ToList(),
            s.Cobros, venta.ClienteId, venta.RedondeoCentavos, venta.MayoreoDeCliente, cuenta?.SaldoCentavos, excesoAutorizado, vence);

        using (var lote = _almacen.Lote())
        {
            lote.Encolar(venta.Id, "api/bandeja/ventas", subida);
            foreach (var registro in bitacora)
                lote.Encolar(registro.Id, "api/bandeja/bitacora", registro);
            if (cuenta is not null)
                lote.Credito(venta.Id, cliente!.Id, aCredito, ahora, vence);
            lote.Anotar(Anotacion(turno.Id, ticket)).Guardar("folio", folio).Confirmar();
        }
        Gastar(usadas);
        Recontar();
        return ticket;
    }

    /// <summary>
    /// Un abono sin conexión, con el saldo que conoce esta caja: el mismo cálculo que en línea. Sube con la bandeja (el
    /// servicio lo guarda tal cual) y su comprobante dice que el saldo es el de esta caja.
    /// </summary>
    public ComprobanteAbonoDto Abonar(SolicitudAbono s)
    {
        var datos = Datos;
        var caja = Caja;
        var turno = TurnoRequerido(s.TurnoId);
        if (s.CajaId != caja.Id)
            throw new InvalidOperationException("El abono va en el turno abierto de esta caja.");
        var cliente = Clientes().FirstOrDefault(c => c.Id == s.ClienteId)
            ?? throw new InvalidOperationException("Ese cliente no está en la copia de esta caja: el abono espera a que vuelva la conexión.");
        if (_almacen.CreditoConfirmado(s.Id))
            throw new InvalidOperationException("Ese abono ya quedó en la caja principal: su comprobante se reimprime cuando vuelva la conexión.");
        var cuenta = Credito(cliente.Id, sin: s.Id) ?? throw new InvalidOperationException(SinSaldos);
        if (s.SaldoVistoCentavos is { } visto && visto != cuenta.SaldoCentavos)
            throw new InvalidOperationException($"La cuenta cambió mientras tanto: según esta caja debe {Formato.Dinero(cuenta.SaldoCentavos)}. Revísalo y abona otra vez.");
        var referencia = string.IsNullOrWhiteSpace(s.Referencia) ? null : s.Referencia.Trim();
        if (referencia is { Length: > 60 })
            throw new ArgumentException("La referencia va de hasta 60 letras.");
        var calculo = CalculoAbono.Calcular(cuenta.SaldoCentavos, s.Forma, s.MontoCentavos, s.RecibidoCentavos);
        var ahora = Ahora;
        var recibido = s.Forma == FormaPago.Efectivo ? s.RecibidoCentavos : calculo.ACobrarCentavos;
        var transferencia = s.Forma == FormaPago.Transferencia;
        var despues = CuentaCredito.Estimar(cuenta, [new CreditoLocal(-calculo.MontoCentavos, PorVerificar: transferencia)]);
        var vence = despues.SaldoCentavos > 0 ? despues.VenceMasAntiguo : null;
        var comprobante = new ComprobanteAbonoDto(s.Id, Abono.FolioDe(s.Id), HoraMexico.ALocal(ahora), caja.Nombre, Nombre(s.UsuarioId), cliente.Id,
            cliente.Nombre, s.Forma, calculo.MontoCentavos, calculo.RedondeoCentavos, recibido, calculo.CambioCentavos, referencia, cuenta.SaldoCentavos,
            despues.SaldoCentavos, vence, datos.Paquete.Encabezado, Estimado: true);
        using (var lote = _almacen.Lote())
        {
            lote.Encolar(s.Id, "api/bandeja/abonos", new AbonoContingenciaDto(s.Id, caja.Id, turno.Id, s.UsuarioId, cliente.Id, s.Forma, calculo.MontoCentavos,
                    calculo.RedondeoCentavos, recibido, calculo.CambioCentavos, referencia, ahora, cuenta.SaldoCentavos, vence))
                .Credito(s.Id, cliente.Id, -calculo.MontoCentavos, ahora, null, transferencia)
                .Anotar(AnotacionAbono(s.Id, turno.Id, comprobante, 1))
                .Confirmar();
        }
        Recontar();
        return comprobante;
    }

    // ── Lo del crédito que se manda en línea: se anota antes, por si la respuesta se pierde ──

    /// <summary>Lo fiado que se manda en línea; si el servicio lo rechaza, se quita (<see cref="QuitarCredito"/>).</summary>
    public void AnotarFiado(Guid ventaId, Guid clienteId, long creditoCentavos)
    {
        var plazo = Credito(clienteId)?.PlazoDias ?? (_copia is null ? null : Clientes().FirstOrDefault(c => c.Id == clienteId)?.PlazoCreditoDias)
            ?? ClienteRegistrado.PlazoPorOmision;
        using var lote = _almacen.Lote();
        lote.Credito(ventaId, clienteId, creditoCentavos, Ahora, CuentaCredito.Vence(Ahora, plazo)).Confirmar();
    }

    /// <summary>
    /// Un abono que se manda en línea, con lo que de verdad baja (el mismo cálculo que el servicio con el saldo que vio la
    /// caja: en efectivo, lo de más es cambio); se corrige con el comprobante.
    /// </summary>
    public void AnotarAbono(SolicitudAbono s)
    {
        long monto;
        try
        {
            monto = CalculoAbono.Calcular(s.SaldoVistoCentavos ?? Credito(s.ClienteId)?.SaldoCentavos ?? 0, s.Forma, s.MontoCentavos, s.RecibidoCentavos)
                .MontoCentavos;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return;
        }
        if (monto <= 0)
            return;
        using var lote = _almacen.Lote();
        lote.Credito(s.Id, s.ClienteId, -monto, Ahora, null, s.Forma == FormaPago.Transferencia).Confirmar();
    }

    /// <summary>
    /// Un saldo a favor que se devuelve en línea (sube la cuenta): se anota antes de mandarlo, así si se pierde la respuesta
    /// esta caja ya no cuenta con ese saldo a favor; se corrige con el comprobante.
    /// </summary>
    public void AnotarSaldoDevuelto(Guid id, Guid clienteId, long montoCentavos)
    {
        var plazo = Credito(clienteId)?.PlazoDias ?? ClienteRegistrado.PlazoPorOmision;
        using var lote = _almacen.Lote();
        lote.Credito(id, clienteId, montoCentavos, Ahora, CuentaCredito.Vence(Ahora, plazo)).Confirmar();
    }

    /// <summary>Si esta caja tiene algo del cliente sin subir: sus saldos del servicio todavía no lo incluyen.</summary>
    public bool CreditoSinSubir(Guid clienteId) => _almacen.CreditoSinSubir(clienteId);

    /// <summary>El servicio rechazó lo que se le mandó (por una regla): no pasó, ya no se cuenta.</summary>
    public void QuitarCredito(Guid id)
    {
        using var lote = _almacen.Lote();
        lote.QuitarCredito([id]).Confirmar();
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
                    resumen.Abonos, resumen.AbonosEfectivoCentavos, resumen.AbonosTarjetaCentavos, resumen.AbonosTransferenciaCentavos,
                    resumen.SaldoDevueltoCentavos))).LimpiarDiario();
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
        // Lo fiado ya estaba anotado: con el vencimiento que puso el servicio, y que el siguiente latido confirme que ya lo tiene.
        if (ticket.VentaId is { } ventaId && ticket.Credito is { } credito)
        {
            lote.CorregirCredito(ventaId, null, credito.Vence);
            _refrescarCredito = true;
        }
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
        // Ya estaba anotado en la cuenta propia: con lo que de verdad bajó, y que el siguiente latido confirme que ya lo tiene.
        lote.CorregirCredito(c.AbonoId, -c.MontoCentavos, null).Anotar(AnotacionAbono(c.AbonoId, turnoId, c, 1)).Confirmar();
        _refrescarCredito = true;
    }

    /// <summary>Un abono cancelado en línea sale de la cuenta del turno (con su propio renglón: el del abono se queda).</summary>
    public void AbonoCanceladoEnLinea(Guid turnoId, ComprobanteAbonoDto c)
    {
        using var lote = _almacen.Lote();
        lote.Anotar(AnotacionAbono(Ids.Nuevo(), turnoId, c, -1)).Confirmar();
    }

    /// <summary>
    /// Un saldo a favor que se devolvió en línea (parte 3; sin conexión no se devuelve): sale del efectivo del turno, se
    /// corrige lo anotado en la cuenta propia y los saldos se vuelven a pedir.
    /// </summary>
    public void SaldoDevueltoEnLinea(Guid turnoId, ComprobanteAbonoDto c)
    {
        using var lote = _almacen.Lote();
        // Ya estaba anotado en la cuenta propia: con lo que de verdad subió, y que el siguiente latido confirme que ya lo tiene.
        lote.CorregirCredito(c.AbonoId, c.MontoCentavos, null)
            .Anotar(new AnotacionDiario(c.AbonoId, turnoId, Redondeo: -c.RedondeoCentavos, SaldoDevuelto: c.CobradoCentavos)).Confirmar();
        _refrescarCredito = true;
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
        var saldoDevuelto = t.SaldoDevuelto + d.SaldoDevuelto;
        var esperado = base_.FondoCentavos + efectivo + abonosEfectivo + entradas - salidas - devoluciones - saldoDevuelto;
        long? contado = conteo?.Sum(c => c.Key * c.Value);
        return new CorteDto(tipo, HoraMexico.ALocal(Ahora), turno.Cajero, realizadoPor, t.Ventas + d.Ventas, base_.FondoCentavos, efectivo, entradas, salidas,
            t.Tarjeta + d.Tarjeta, t.Transferencia + d.Transferencia, esperado, contado, contado - esperado, devoluciones, t.Cancelaciones + d.Cancelaciones,
            EnContingencia: true, RedondeoCentavos: t.Redondeo + d.Redondeo, CreditoCentavos: t.Credito + d.Credito, Abonos: t.Abonos + d.Abonos,
            AbonosEfectivoCentavos: abonosEfectivo, AbonosTarjetaCentavos: t.AbonosTarjeta + d.AbonosTarjeta,
            AbonosTransferenciaCentavos: t.AbonosTransferencia + d.AbonosTransferencia, SaldoDevueltoCentavos: saldoDevuelto);
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

    /// <summary>
    /// De esa acción, sin usar, vigente y por un monto que alcance: devuelve quién autorizó y la aparta en
    /// <paramref name="usadas"/>. Se gasta (<see cref="Gastar"/>) cuando la operación quedó guardada: si algo más la
    /// detiene, sigue valiendo.
    /// </summary>
    private Guid? Autorizada(Guid? id, AccionControlada accion, long monto, List<Guid> usadas)
    {
        lock (_candado)
        {
            if (id is not { } clave || usadas.Contains(clave) || !_autorizaciones.TryGetValue(clave, out var a) || a.Accion != accion
                || Ahora > a.VenceUtc || monto > a.MontoCentavos)
                return null;
            usadas.Add(clave);
            return a.AutorizadorId;
        }
    }

    private void Gastar(IEnumerable<Guid> usadas)
    {
        lock (_candado)
            foreach (var id in usadas)
                _autorizaciones.Remove(id);
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
