using System.Collections.Immutable;
using System.Net.ServerSentEvents;
using System.Text.Json;
using PapeleriaPos.Impresiones.Contratos;
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
/// <para>
/// La copia al día y la red con tres estados (entrega 5, parte 2, docs/propuesta-interfaz.md, «Rendimiento»): la caja
/// principal avisa por SSE cuando cambia una versión y la caja pide solo lo que cambió desde la suya, lo aplica fuera del
/// hilo de la pantalla y cambia la foto del catálogo de un jalón; el latido queda de respaldo. El latido también mide cuánto
/// tarda la caja principal en contestar: buena, lenta o caída (<see cref="CalidadDeLaRed"/>); en lenta, la caja trabaja
/// como sin conexión hasta que esté buena.
/// </para>
/// </summary>
public sealed class Contingencia
{
    private static readonly TimeSpan Latido = TimeSpan.FromSeconds(5);

    /// <summary>Lo más que se espera un latido: después de esto, la caja principal no contestó (caída).</summary>
    private static readonly TimeSpan EsperaLatido = TimeSpan.FromSeconds(2);

    /// <summary>Con un servicio de antes (sin cambios por versión), la copia se vuelve a bajar completa cada tanto (las existencias).</summary>
    private static readonly TimeSpan PaqueteViejo = TimeSpan.FromMinutes(30);

    /// <summary>El formato de la copia de una caja principal que no lo dice (la de la entrega 5, parte 2).</summary>
    private const int FormatoDeAntes = 1;

    /// <summary>
    /// Con los cambios por versión, la copia completa se vuelve a bajar a lo más cada tanto, por si acaso: lo que no llegue con
    /// los cambios (un error nuestro) se corrige solo. Al abrir la caja también se baja si la caja principal se reinició.
    /// </summary>
    private static readonly TimeSpan CopiaCompletaCada = TimeSpan.FromHours(4);

    /// <summary>Lo más que se espera la copia completa (varios MB) y los cambios.</summary>
    private static readonly TimeSpan EsperaCopia = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan EsperaCambios = TimeSpan.FromSeconds(10);

    /// <summary>Lo más que se espera cada cosa de la bandeja al subir.</summary>
    private static readonly TimeSpan EsperaSubida = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Lo que solo cambió de existencias se guarda en la base de la caja a lo más cada tanto (la copia pesa varios MB): al
    /// abrir, la caja pide las que cambiaron desde las de la copia guardada. Lo del catálogo se guarda al momento.
    /// </summary>
    private static readonly TimeSpan GuardarExistenciasCada = TimeSpan.FromMinutes(10);

    /// <summary>Un servicio de antes no tiene los cambios ni el aviso: se vuelve a probar después de esto.</summary>
    private static readonly TimeSpan ServicioDeAntesPor = TimeSpan.FromMinutes(5);

    /// <summary>Sin nada del aviso (ni el «vivo» de cada 15 s) en este rato, la conexión quedó medio muerta: se vuelve a conectar.</summary>
    private static readonly TimeSpan AvisoCallado = TimeSpan.FromSeconds(45);

    /// <summary>Lo que se espera antes de volver a conectar el aviso: se dobla con cada intento fallido, hasta el tope.</summary>
    private static readonly TimeSpan ReconectarAviso = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan ReconectarAvisoTope = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReintentoErrores = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefrescoCredito = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EsperaCreditos = TimeSpan.FromSeconds(10);

    /// <summary>Lo anotado que el servicio no tiene después de esto (y no pasó por la bandeja), ya no lo va a tener.</summary>
    private static readonly TimeSpan CreditoHuerfano = TimeSpan.FromMinutes(2);

    /// <summary>Cuánto se recuerda lo ya confirmado (por si se vuelve a mandar con el mismo Id).</summary>
    private static readonly TimeSpan RecordarConfirmado = TimeSpan.FromDays(1);

    private const string SinSaldos = "Esta caja todavía no conoce los saldos del crédito: fiar y abonar esperan a que vuelva la conexión.";

    private sealed record AutorizacionLocal(Guid AutorizadorId, AccionControlada Accion, long? MontoCentavos, DateTime VenceUtc);

    /// <summary>
    /// El paquete con sus índices, para buscar y cobrar al instante. Se arma fuera del hilo de la pantalla (miles de
    /// productos): al abrir la caja y cada vez que baja una copia nueva.
    /// </summary>
    private sealed class Copia
    {
        /// <param name="anterior">La copia que había: de ella sale desde cuándo tiene cada producto sus precios (<see cref="PreciosDesde"/>).</param>
        /// <param name="mismaBusqueda">
        /// Lo que llegó no cambia nada de lo que arma la búsqueda por nombre (<see cref="CambiosCopiaDto.CambiaLaBusqueda"/>): el
        /// índice de la foto de <paramref name="anterior"/> sirve igual y no se arma otra vez.
        /// </param>
        public Copia(PaqueteContingenciaDto p, Copia? anterior = null, bool mismaBusqueda = false)
        {
            Paquete = p;
            PorVariante = p.Productos.ToDictionary(x => x.VarianteId);
            PreciosDesde = DesdeCuando(p, PorVariante, anterior);
            // Los códigos, en la foto del catálogo: el escaneo, en línea o sin conexión, resuelve igual (entrega 5). Y la búsqueda
            // por nombre, la misma del servicio, también sin conexión (entrega 5, parte 3).
            Foto = FotoCatalogo.Armar(p, mismaBusqueda ? anterior?.Foto : null);
            Presentaciones = p.Productos.SelectMany(x => x.Presentaciones).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
            Usuarios = p.Usuarios.ToDictionary(u => u.Usuario.Trim().ToLowerInvariant());
            UsuariosPorId = p.Usuarios.ToDictionary(u => u.Id);
            (PermisosPorUsuario, Defecto) = PermisosDe(p);
            Activos = p.Usuarios.Where(u => u.Activo).Select(u => new UsuarioConPermisos(u.Id, u.Usuario, u.Nombre, PermisosDe(u.Id))).ToList();
        }

        /// <summary>
        /// La misma copia con otras existencias (entrega 5, parte 2: después de cada venta), o con otro «lo más vendido» o «lo
        /// elegido» (parte 3): los mismos productos, códigos y nombres, así que lo que sale de ellos (el índice de la búsqueda por
        /// nombre, los usuarios) se queda; la foto se arma otra vez con los productos de ahora (<see cref="FotoCatalogo.Armar"/>).
        /// </summary>
        public static Copia ConOtrasExistencias(PaqueteContingenciaDto p, Copia anterior) => new(anterior, p);

        private Copia(Copia anterior, PaqueteContingenciaDto p)
        {
            Paquete = p;
            PorVariante = p.Productos.ToDictionary(x => x.VarianteId);
            PreciosDesde = anterior.PreciosDesde;
            Foto = FotoCatalogo.Armar(p, anterior.Foto);
            Presentaciones = anterior.Presentaciones;
            (Usuarios, UsuariosPorId, PermisosPorUsuario, Defecto, Activos) =
                (anterior.Usuarios, anterior.UsuariosPorId, anterior.PermisosPorUsuario, anterior.Defecto, anterior.Activos);
        }

        /// <summary>
        /// Desde qué versión del catálogo tiene cada producto, en esta copia, los precios que tiene (entrega 5, parte 2): la de
        /// la copia con que llegaron así. Si llega otra vez con los mismos precios y presentaciones (en una copia completa, o
        /// porque cambió su nombre o sus códigos), se queda la de antes. Sin conexión no se cobra con precios que llegaron
        /// después de lo que vio el cajero (<see cref="Contingencia.Cobrar"/>).
        /// </summary>
        public IReadOnlyDictionary<Guid, long> PreciosDesde { get; }

        private static Dictionary<Guid, long> DesdeCuando(PaqueteContingenciaDto p, Dictionary<Guid, ProductoVentaDto> productos, Copia? anterior)
        {
            // De otra base (la prueba empezó de cero) o más nueva que esta: sus versiones no son de esta.
            if (anterior is not null && (anterior.Paquete.Base != p.Base || anterior.Paquete.Version > p.Version))
                anterior = null;
            var desde = new Dictionary<Guid, long>(productos.Count);
            foreach (var (varianteId, producto) in productos)
            {
                var version = producto.Version ?? p.Version;
                if (anterior is not null && anterior.PorVariante.TryGetValue(varianteId, out var antes)
                    && anterior.PreciosDesde.TryGetValue(varianteId, out var suya) && suya <= version && MismosPrecios(antes, producto))
                    version = suya;
                desde[varianteId] = version;
            }
            return desde;
        }

        /// <summary>Los mismos precios y presentaciones: lo que cuenta para lo que se cobra (no el nombre ni los códigos).</summary>
        public static bool MismosPrecios(ProductoVentaDto a, ProductoVentaDto b) =>
            ReferenceEquals(a, b)
            || ((ReferenceEquals(a.Precios, b.Precios) || a.Precios.SequenceEqual(b.Precios))
                && (ReferenceEquals(a.Presentaciones, b.Presentaciones) || a.Presentaciones.SequenceEqual(b.Presentaciones)));

        /// <summary>
        /// Los permisos de cada usuario y los de defecto (entrega 1). Una copia de antes (con las reglas por rol) se lee como la
        /// leería la migración del servicio, para seguir decidiendo igual mientras baja la nueva.
        /// </summary>
        private static (Dictionary<Guid, PermisosDeUsuario>, PermisosDeUsuario) PermisosDe(PaqueteContingenciaDto p)
        {
            if (p.Permisos is { } permisos)
                return (permisos.Where(x => x.UsuarioId is not null).ToDictionary(x => x.UsuarioId!.Value, x => new PermisosDeUsuario(x.Permisos)),
                    new PermisosDeUsuario(permisos.FirstOrDefault(x => x.UsuarioId is null)?.Permisos ?? []));
            var reglas = (p.Reglas ?? []).Select(r => new ReglaPermiso
            {
                Accion = r.Accion, Rol = r.Rol, Nivel = r.Nivel, UmbralCentavos = r.UmbralCentavos, PideMotivo = r.PideMotivo, SucursalId = p.Caja.SucursalId,
            }).ToList();
            return (p.Usuarios.ToDictionary(u => u.Id, u => new PermisosDeUsuario(PermisosDeRoles.De(u.Rol, reglas, p.Caja.SucursalId))),
                new PermisosDeUsuario(PermisosDeRoles.De(Rol.Cajero, reglas, p.Caja.SucursalId)));
        }

        /// <summary>Los permisos de un usuario de la copia (si no trae, ninguno).</summary>
        public PermisosDeUsuario PermisosDe(Guid usuarioId) =>
            PermisosPorUsuario.TryGetValue(usuarioId, out var permisos) ? permisos : new PermisosDeUsuario([]);

        public PaqueteContingenciaDto Paquete { get; }
        public Dictionary<Guid, ProductoVentaDto> PorVariante { get; }

        /// <summary>Solo lo de esta copia: lo que llegó después de la caja principal va en la foto de la contingencia.</summary>
        public FotoCatalogo Foto { get; }

        public Dictionary<Guid, PresentacionDto> Presentaciones { get; }
        /// <summary>Por su usuario: también los dados de baja (no entran).</summary>
        public Dictionary<string, UsuarioLocalDto> Usuarios { get; }
        public Dictionary<Guid, UsuarioLocalDto> UsuariosPorId { get; }
        public Dictionary<Guid, PermisosDeUsuario> PermisosPorUsuario { get; }
        public PermisosDeUsuario Defecto { get; }

        /// <summary>Los que entran, con sus permisos: de entre ellos sale quién autoriza y quién puede.</summary>
        public List<UsuarioConPermisos> Activos { get; }
    }

    private readonly AlmacenLocal _almacen;
    private readonly Transporte _http;
    private readonly ConexionCaja _conexion;
    private readonly TimeProvider _reloj;
    private readonly SemaphoreSlim _subida = new(1, 1);
    private readonly SemaphoreSlim _creditos = new(1, 1);
    private readonly Lock _candado = new();
    private readonly Lock _candadoSinConfirmar = new();

    /// <summary>
    /// Los cobros que no se sabe si quedaron (<see cref="NoSeSabeSiQuedo"/>), y si se mandaron fiados; también en la base de la
    /// caja.
    /// </summary>
    private volatile ImmutableDictionary<Guid, bool> _sinConfirmar;

    private readonly Dictionary<Guid, AutorizacionLocal> _autorizaciones = [];
    private readonly Dictionary<string, (int Fallos, DateTime? Hasta)> _intentos = [];
    private volatile Copia? _copia;

    /// <summary>La foto del catálogo para escanear: la de la copia, con lo que llegó después encima (<see cref="Foto"/>).</summary>
    private FotoCatalogo? _foto;

    /// <summary>La copia guardada en la base local, que se lee y se arma fuera del hilo de la pantalla al abrir la caja.</summary>
    private readonly Task _carga;

    private readonly RendimientoCaja? _rendimiento;

    private DateTime _paqueteBajadoUtc = DateTime.MinValue;
    private DateTime _ultimoReintentoUtc = DateTime.MinValue;
    private readonly CalidadDeLaRed _red;

    /// <summary>La red de la tienda en el POS: la bitácora de red, el vigía y volver a encontrar a la caja principal.</summary>
    private readonly RedDeLaCaja? _redDeLaCaja;

    private CancellationTokenSource? _alto;

    /// <summary>El latido y el aviso (<see cref="Iniciar"/>): al cerrar la caja se espera a que terminen lo que iban haciendo.</summary>
    private Task _deFondo = Task.CompletedTask;

    /// <summary>Se cerró la caja (<see cref="Detener"/>): ya no empieza otra vuelta de ponerse al día.</summary>
    private bool Detenida => _alto?.IsCancellationRequested == true;

    /// <summary>Las versiones de la caja principal que se conocen (las del aviso o las del latido); vacías con un servicio de antes.</summary>
    private VersionesCopiaDto? _dePrincipal;

    /// <summary>Las versiones que dijo la caja principal la última vez (en el latido o en el aviso): para las pruebas.</summary>
    internal VersionesCopiaDto? VersionesDeLaPrincipal => _dePrincipal;

    /// <summary>Una puesta al día a la vez; lo que se avise mientras, se revisa al terminar (<see cref="_otraVez"/>).</summary>
    private readonly SemaphoreSlim _poniendoAlDia = new(1, 1);

    private volatile bool _otraVez;

    /// <summary>Hasta cuándo no se piden cambios ni el aviso (el servicio es de antes).</summary>
    private DateTime _sinCambiosHasta = DateTime.MinValue;

    private DateTime _sinAvisoHasta = DateTime.MinValue;

    /// <summary>Desde cuándo la copia no se baja completa (al abrir la caja cuenta como bajada): <see cref="CopiaCompletaCada"/>.</summary>
    private DateTime _completaDesdeUtc;

    /// <summary>Cuándo se guardó la copia en la base de la caja y si tiene existencias más nuevas que no se han guardado.</summary>
    private DateTime _copiaGuardadaUtc = DateTime.MinValue;

    private bool _existenciasSinGuardar;

    /// <summary>Mientras baja la copia completa, la red la llena la propia caja: un latido lento no cuenta.</summary>
    private volatile bool _bajandoCopia;
    private SaldosCreditoDto? _saldos;
    private long? _creditosPedidos;

    /// <summary>Algo del crédito pasó en línea en esta caja (o se anotó mientras se pedían): el siguiente latido pide los saldos.</summary>
    private volatile bool _refrescarCredito;

    /// <param name="http">El cliente de lo de fondo (<see cref="RedCaja.Fondo"/>): el latido, el aviso, la copia y la bandeja.</param>
    /// <param name="rendimiento">Para decir qué pasaba si la pantalla se traba (bajar y armar la copia); sin él, no se dice.</param>
    /// <param name="reglas">
    /// Cómo se decide el estado de la red (<see cref="ReglasDeLaRed"/>). La caja de verdad usa
    /// <see cref="ReglasDeLaRed.DeLaTienda"/>; sin ellas, solo si contesta o no (<see cref="ReglasDeLaRed.SoloSiContesta"/>).
    /// </param>
    /// <param name="redDeLaCaja">
    /// La red de la tienda en el POS: la bitácora de red, su vigía y volver a encontrar a la caja principal si le cambió la IP. Cada
    /// latido se le pasa; sin ella (las pruebas de lo demás), nada de eso.
    /// </param>
    public Contingencia(AlmacenLocal almacen, HttpClient http, ConexionCaja conexion, TimeProvider? reloj = null, RendimientoCaja? rendimiento = null,
        ReglasDeLaRed? reglas = null, RedDeLaCaja? redDeLaCaja = null)
    {
        _almacen = almacen;
        _redDeLaCaja = redDeLaCaja;
        if (redDeLaCaja is not null)
        {
            redDeLaCaja.Cambio += () => Cambio?.Invoke();
            // Encontró a la caja principal en otra dirección (y probó que es ella): el latido la usa al momento.
            if (redDeLaCaja.Reencuentro is { } reencuentro)
                reencuentro.SeCambio += _ => LatirYa();
        }
        _rendimiento = rendimiento;
        _http = new Transporte(http, conexion);
        _conexion = conexion;
        _reloj = reloj ?? TimeProvider.System;
        _red = new CalidadDeLaRed(reglas ?? ReglasDeLaRed.SoloSiContesta, _reloj);
        // Las fotos de los productos (entrega 13, parte 2): las que ya bajó, de su base; las que falten, de fondo.
        Fotos = new FotosDeLaCaja(almacen, _http, _reloj);
        _completaDesdeUtc = Ahora;
        _saldos = almacen.Leer<SaldosCreditoDto>("saldos");
        _sinConfirmar = (almacen.Leer<Dictionary<Guid, bool>>(ClaveSinConfirmar) ?? []).ToImmutableDictionary();
        PilotoEleventa = almacen.Leer<bool?>(ClavePilotoEleventa) ?? false;
        (Pendientes, ConError, UltimoError, ImpresionesPorSubir) = almacen.Contar();
        // La copia trae miles de productos: leerla y armar sus índices ya no detiene la ventana al abrir (entrega 5). Lo que
        // la necesita antes de que esté (vender sin conexión al abrir) la espera.
        _carga = Task.Run(CargarCopiaGuardada);
    }

    private void CargarCopiaGuardada()
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_almacen.LeerCopia<PaqueteContingenciaDto>() is { } paquete)
            {
                Poner(new Copia(paquete), deLaRed: false);
                _rendimiento?.Actividad("leer la copia del catálogo al abrir la caja", reloj.Elapsed);
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException
                                       or ArgumentException or IOException)
        {
            // Una copia que no se puede leer es como no tenerla: el primer latido con conexión baja otra.
        }
    }

    /// <summary>
    /// La copia nueva y su foto, de un jalón: lo que llegó de la caja principal y es más nuevo que ella se queda encima. Y de qué
    /// productos hay foto: las que falten se bajan de fondo (entrega 13, parte 2).
    /// </summary>
    /// <param name="deLaRed">Si llegó de la caja principal (y no es la guardada en la base, al abrir).</param>
    private void Poner(Copia copia, bool deLaRed = true)
    {
        _copia = copia;
        FotoCatalogo? antes;
        do
            antes = Volatile.Read(ref _foto);
        while (Interlocked.CompareExchange(ref _foto, copia.Foto.ConLoNuevoDe(antes), antes) != antes);
        Fotos.Referencias(copia.Paquete.Fotos, deLaRed);
    }

    /// <summary>
    /// Las fotos de los productos en esta caja (entrega 13, parte 2): las que dice la copia, bajadas aparte a su base. Ctrl+M, la
    /// lista de la búsqueda y el verificador las enseñan de aquí, sin la red.
    /// </summary>
    public FotosDeLaCaja Fotos { get; }

    /// <summary>
    /// Termina cuando ya se leyó la copia guardada al abrir la caja (fuera del hilo de la pantalla): la entrada la espera así,
    /// sin trabar la pantalla, y sale con la copia sin esperar a la red (entrega 5, parte 3).
    /// </summary>
    public Task CopiaLeida => _carga;

    /// <summary>La copia, esperando a que termine de cargarse si la caja recién abrió.</summary>
    private Copia? CopiaCargada()
    {
        if (_copia is null && !_carga.IsCompleted)
            ((IAsyncResult)_carga).AsyncWaitHandle.WaitOne();
        return _copia;
    }

    /// <summary>
    /// La foto del catálogo de esta caja para escanear sin la red (entrega 5): la copia, más lo que llegó después de la caja
    /// principal. Vacía mientras la caja no tiene copia (o mientras la lee al abrir): entonces se pregunta como antes.
    /// </summary>
    public FotoCatalogo? Foto => Volatile.Read(ref _foto);

    /// <summary>
    /// Solo la copia, sin lo que llegó después: sin conexión se escanea contra ella, que es contra lo que se cobra sin
    /// conexión (<see cref="Cobrar"/>). Vacía mientras la caja no tiene copia.
    /// </summary>
    public FotoCatalogo? FotoDeLaCopia => _copia?.Foto;

    /// <summary>
    /// Un producto que la copia no tenía (recién dado de alta en otra caja) o los precios de ahora de uno que sí tenía (los
    /// dijo el cobro): queda en la foto, encima de la copia, hasta que baje una copia que ya lo traiga.
    /// </summary>
    /// <param name="codigo">El código con que se escaneó, si la copia no lo tiene.</param>
    public void AgregarALaFoto(ProductoVentaDto producto, string? codigo = null)
    {
        FotoCatalogo? antes;
        do
        {
            antes = Volatile.Read(ref _foto);
            if (antes is null)
                return;
        }
        while (Interlocked.CompareExchange(ref _foto, antes.Con(producto, codigo), antes) != antes);
    }

    /// <summary>
    /// Lo que esta caja eligió en la lista con cada texto desde que se abrió y ya guardó el servicio (entrega 5, parte 3): la
    /// búsqueda contra la copia lo pone primero la vez siguiente, como el servicio, sin esperar a que baje otra copia. Es lo
    /// más nuevo: solo esta caja elige lo suyo.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Guid> _elegidos = new(StringComparer.Ordinal);

    /// <summary>Lo que se eligió de la lista con ese texto (con la clave con que lo guarda el servicio).</summary>
    public void Aprender(string texto, Guid varianteId) => _elegidos[IndiceDeBusqueda.ClaveGuardada(texto)] = varianteId;

    /// <summary>Lo que se eligió aquí con ese texto desde que se abrió la caja; vacío si nada.</summary>
    public Guid? ElegidoAqui(string texto) => _elegidos.TryGetValue(IndiceDeBusqueda.Clave(texto), out var elegido) ? elegido : null;

    /// <summary>Cambió la conexión o la bandeja: la pantalla vuelve a dibujar la franja.</summary>
    public event Action? Cambio;

    /// <summary>La caja principal contesta (aunque sea tarde).</summary>
    public bool ServidorResponde => _red.Estado != EstadoRed.Caida;

    /// <summary>
    /// Se trabaja con la caja principal solo si la red está buena y ya no queda nada por subir. Con la red lenta, como sin
    /// conexión (entrega 5, parte 2). Tampoco mientras la caja principal no pruebe otra vez quién es (después de una caída o al
    /// abrir la caja): a quien no lo ha probado no se le manda la llave.
    /// </summary>
    public bool EnLinea => _red.Estado == EstadoRed.Buena && Pendientes == 0 && !_conexion.SinComprobar;

    /// <summary>Buena, lenta o caída (<see cref="CalidadDeLaRed"/>).</summary>
    public EstadoRed EstadoDeLaRed => _red.Estado;

    /// <summary>Lo que tardó en contestar el último latido; vacío si no contestó.</summary>
    public TimeSpan? Latencia => _red.Latencia;

    /// <summary>Con qué reglas se decide el estado de la red.</summary>
    public ReglasDeLaRed ReglasDeLaRed => _red.Reglas;

    /// <summary>
    /// Lo que la caja principal dijo en el último latido que contestó: si hay internet y si contesta su router. Vacío con un
    /// servicio de antes o mientras no lo diga. La franja lo junta con el estado de la red (<see cref="AvisosDeRed.QueDice"/>).
    /// </summary>
    public RedDeLaPrincipalDto? RedDeLaPrincipal { get; private set; }

    /// <summary>La red de la tienda en el POS (la bitácora, el vigía, volver a encontrar a la caja principal); vacía sin ella.</summary>
    public RedDeLaCaja? RedDeLaCaja => _redDeLaCaja;

    /// <summary>
    /// Lo que no se puede hacer con la copia (devolver, el catálogo, los reportes) se intenta con la caja principal aunque la
    /// red esté lenta: no hay otra forma de hacerlo. Con algo por subir, no: primero sube (sus folios y su orden).
    /// </summary>
    public bool SeIntentaSinCopia => _red.Estado != EstadoRed.Caida && Pendientes == 0 && !_conexion.SinComprobar;

    public int Pendientes { get; private set; }
    public int ConError { get; private set; }
    public string? UltimoError { get; private set; }

    /// <summary>Trabajos de impresión medidos en esta PC que todavía no llegan a la caja principal.</summary>
    public int ImpresionesPorSubir { get; private set; }

    /// <summary>
    /// La base del servicio no pasó la revisión al arrancar después de un apagón (entrega 10): la franja lo dice hasta
    /// que se restaure el respaldo. Vacío si está bien.
    /// </summary>
    public string? AvisoBase { get; private set; }

    /// <summary>
    /// La base local de esta caja no quedó con los ajustes contra apagones al abrirla (entrega 10): la franja lo dice para
    /// que se avise al dueño. Vacío si está bien.
    /// </summary>
    public string? AvisoBaseLocal => _almacen.ProblemaDeAjustes is { } problema
        ? $"La base de esta caja no quedó lista contra apagones ({problema.TrimEnd('.')}): avisa al dueño."
        : null;

    public PaqueteContingenciaDto? Paquete => CopiaCargada()?.Paquete;

    /// <summary>Los últimos saldos del crédito que mandó la caja principal (sin ellos, no se fía ni se abona sin conexión).</summary>
    public SaldosCreditoDto? Saldos => _saldos;
    public int UltimoFolio => _almacen.Leer<int?>("folio") ?? 0;

    private const string ClavePilotoEleventa = "piloto-eleventa";

    /// <summary>
    /// El piloto con eleventa (<see cref="PilotoConEleventa"/>): lo dice el servicio en cada latido y se guarda en la
    /// base local, para que sin conexión (también si la caja arranca sin ella) se siga cobrando solo en efectivo y el corte
    /// se siga haciendo en eleventa. Solo para convivir con eleventa: se quita al dejarlo.
    /// </summary>
    public bool PilotoEleventa { get; private set; }

    /// <summary>Lo que dijo el servicio del piloto con eleventa; se guarda solo si cambió.</summary>
    public async Task RecordarPilotoEleventaAsync(bool piloto)
    {
        if (piloto == PilotoEleventa)
            return;
        PilotoEleventa = piloto;
        Cambio?.Invoke();
        await _almacen.EscribirAsync(l => l.Guardar(ClavePilotoEleventa, piloto), "el piloto con eleventa");
    }

    private DateTime Ahora => _reloj.GetUtcNow().UtcDateTime;

    // ── Latido, bandeja y copia local ───────────────────────────────────────

    public void Iniciar()
    {
        if (_alto is not null)
            return;
        _alto = new CancellationTokenSource();
        var alto = _alto.Token;
        // Al abrir, la dirección guardada prueba quién es antes de recibir la llave (pudo cambiar mientras la caja estuvo cerrada).
        PorComprobar();
        _deFondo = Task.Run(() =>
        {
            // La bitácora de red empieza antes que el primer latido, que ya puede anotar que la caja principal no contesta (fuera del
            // hilo de la pantalla: lee lo de los días anteriores). Termina al cerrar la caja.
            _redDeLaCaja?.Vigia?.Empezar();
            return Task.WhenAll(Task.Run(() => LatirAsync(alto)), Task.Run(() => EscucharAvisosAsync(alto)),
                Task.Run(() => _redDeLaCaja?.VigilarAsync(alto) ?? Task.CompletedTask));
        });
    }

    /// <summary>
    /// Solo el aviso de la caja principal (SSE), sin el latido de cada 5 s: para las pruebas, que piden cada latido. La caja
    /// de verdad usa <see cref="Iniciar"/>, que pone los dos.
    /// </summary>
    public void IniciarAvisos()
    {
        if (_alto is not null)
            return;
        _alto = new CancellationTokenSource();
        _deFondo = Task.Run(() => EscucharAvisosAsync(_alto.Token));
    }

    public void Detener() => _alto?.Cancel();

    /// <summary>
    /// Se cierra la caja: el latido y el aviso se detienen y lo que se mandó escribir en la base local queda en el disco antes
    /// de irse (los tickets en espera, lo último de un cobro). Lo de fondo que iba a medias termina antes: la copia que se leía
    /// al abrir (si se cierra enseguida), el latido o el aviso, y la vuelta de ponerse al día (la copia a medio guardar). Al
    /// final suelta las conexiones de la base: caja-local.db no se queda abierta. Espera poco en total: una base o una red que
    /// no responden no detienen el cierre.
    /// </summary>
    public void Cerrar(TimeSpan? espera = null)
    {
        var limite = DateTime.UtcNow + (espera ?? TimeSpan.FromSeconds(3));
        Detener();
        foreach (var tarea in new[] { _carga, _deFondo })
            ((IAsyncResult)tarea).AsyncWaitHandle.WaitOne(Restante(limite));
        if (_poniendoAlDia.Wait(Restante(limite)))
            _poniendoAlDia.Release();
        // Las fotos que iban bajando se sueltan; lo que se escribía de ellas, termina.
        Fotos.Detener(Restante(limite));
        _almacen.Cerrar(Restante(limite));
    }

    private static TimeSpan Restante(DateTime limite) => limite - DateTime.UtcNow is var falta && falta > TimeSpan.Zero ? falta : TimeSpan.Zero;

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

    /// <summary>
    /// Pregunta si la caja principal contesta; si sí, sube lo pendiente y renueva la copia si cambió. Corre fuera del hilo
    /// de la pantalla aunque lo pida ella (la franja de la prueba): bajar la copia y armar su foto no la detiene.
    /// </summary>
    public Task LatidoAsync() => Task.Run(LatirUnaVezAsync);

    /// <summary>Un latido ya, sin esperarlo (se encontró a la caja principal en otra dirección); si falla, lo dice el siguiente.</summary>
    private void LatirYa() =>
        _ = LatidoAsync().ContinueWith(t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private async Task LatirUnaVezAsync()
    {
        // Primero la copia guardada: la que baje después es más nueva.
        await _carga.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!_conexion.Lista || (_conexion.CajaId is null && _conexion.PideAlta))
            return;
        // Después de una caída (o al abrir), la dirección guardada prueba quién es antes de volver a recibir la llave.
        if (await ComprobarSiFaltaAsync() is Comprobacion.NoEs or Comprobacion.NoContesta or Comprobacion.NoLaConoce)
        {
            _redDeLaCaja?.Latido(null, _red.Estado, null);
            return;
        }
        EstadoServicioDto? estado;
        var desde = _reloj.GetTimestamp();
        try
        {
            // Con lo que tiene por subir: el corte del día espera a que suba (entrega 6).
            estado = await _http.GetAsync<EstadoServicioDto>(RutaEstado, espera: EsperaLatido);
        }
        catch (ErrorCaja)
        {
            // No contestó, ni a tiempo: caída (y al volver, primero prueba quién es).
            if (_red.Fallo())
                Cambio?.Invoke();
            PorComprobar();
            // A la bitácora de red; si no contesta un rato, se busca a la caja principal en la red (por si le cambió la IP).
            _redDeLaCaja?.Latido(null, _red.Estado, null);
            return;
        }
        // Lo que tardó en contestar decide si la red está buena o lenta (con histéresis); el chip de arriba lo dice en ms.
        var antes = _red.Latencia;
        var tardo = _reloj.GetElapsedTime(desde);
        if (_red.Contesto(tardo, ocupada: _bajandoCopia)
            || Math.Round(antes?.TotalMilliseconds ?? -1) != Math.Round(_red.Latencia?.TotalMilliseconds ?? -1))
            Cambio?.Invoke();
        // Lo que dice la caja principal del internet y de su router (la franja «Sin internet»), y a la bitácora de red.
        if (estado?.Red != RedDeLaPrincipal)
        {
            RedDeLaPrincipal = estado?.Red;
            Cambio?.Invoke();
        }
        _redDeLaCaja?.Latido(tardo, _red.Estado, estado?.Red);
        await RecordarPilotoEleventaAsync(estado?.PilotoEleventa == true);
        if (estado?.AvisoBase != AvisoBase)
        {
            AvisoBase = estado?.AvisoBase;
            Cambio?.Invoke();
        }
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
        // Lo de la bandeja sube también con la red lenta: va de fondo, y mientras la caja sigue con su copia.
        var subio = Pendientes > 0;
        if (Pendientes > 0 || ImpresionesPorSubir > 0 || (ConError > 0 && Ahora - _ultimoReintentoUtc > ReintentoErrores))
            await SubirAsync();
        if (subio && ServidorResponde)
            await AvisarLoQueQuedaAsync();
        // Los saldos del crédito: cuando cambiaron, cuando acaba de subir lo suyo, o cada tanto si tiene algo por confirmar.
        var tocaCredito = _refrescarCredito || _creditosPedidos is not { } pedidos || _reloj.GetElapsedTime(pedidos) > RefrescoCredito;
        if (ServidorResponde && (_saldos is null ? tocaCredito
                : estado!.VersionCredito != _saldos.Version || ((subio || tocaCredito) && _almacen.CuentaPropia().Count > 0)))
            await ActualizarCreditosAsync();
        if (!ServidorResponde || estado is null)
            return;
        // La copia: con un servicio de ahora, pide lo que cambió desde la suya (el aviso por SSE casi siempre ya lo hizo; el
        // latido queda de respaldo). Con uno de antes, la baja completa en el primer latido, cuando cambia y cada tanto.
        if (estado.Base is { } base_ && estado.VersionExistencias is { } existencias)
        {
            _dePrincipal = new VersionesCopiaDto(base_, estado.VersionDatos, existencias, estado.VersionCredito, estado.FormatoCopia);
            await PonerAlDiaAsync();
        }
        else if (Paquete is null || Paquete.Version != estado.VersionDatos || Ahora - _paqueteBajadoUtc > PaqueteViejo)
            await ActualizarPaqueteAsync();
        // Las fotos que no se pudieron bajar (no había conexión): otra vuelta, ya con la caja principal contestando.
        Fotos.SiFaltan();
    }

    /// <summary>
    /// La caja principal no contestó a algo de fondo: si pasó su plazo, la red está lenta; si ni se pudo conectar, caída.
    /// </summary>
    private void NoContesto(ErrorCaja error)
    {
        if (error.PorPlazo ? _red.PlazoVencido() : _red.Fallo())
            Cambio?.Invoke();
        if (!error.PorPlazo)
            PorComprobar();
    }

    /// <summary>La caja principal no contestó a algo que se le pidió (ni se pudo conectar): sin conexión, hasta el siguiente latido.</summary>
    public void MarcarSinConexion()
    {
        if (_red.Fallo())
            Cambio?.Invoke();
        PorComprobar();
    }

    // ── Que la caja principal pruebe quién es antes de recibir la llave ─────

    /// <summary>
    /// La caja principal dice que no conoce a esta caja (la quitaron o la dieron de alta en otra PC): la pantalla la da de alta
    /// otra vez, como cuando una llamada lo dice. Con el porqué. Nunca con alguien en la caja (<see cref="ConexionCaja.UsuarioEnCaja"/>):
    /// se perdería el ticket que lleva. Mientras, vende con la copia, y en el primer latido después de que salga, se dice.
    /// </summary>
    public event Action<string>? SinAlta;

    private readonly SemaphoreSlim _comprobando = new(1, 1);

    /// <summary>Lo más que una llamada espera, al abrir la caja, a que la caja principal pruebe quién es.</summary>
    private static readonly TimeSpan EsperaComprobacion = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Después de una caída (o al abrir), la dirección guardada tiene que probar otra vez quién es antes de recibir la llave
    /// (<see cref="ConexionCaja.SinComprobar"/>). Solo si esta caja la puede comprobar (con llave, la caja principal en otra PC).
    /// </summary>
    private void PorComprobar()
    {
        if (_redDeLaCaja?.Reencuentro is { Aplica: true })
            _conexion.SinComprobar = true;
    }

    /// <summary>
    /// Si falta, que la dirección guardada pruebe quién es (una comprobación a la vez). Es (o un servicio de antes, sin la prueba,
    /// si esta caja no conoce su llave): se le vuelve a mandar la llave. No es, no contesta o no conoce a esta caja: caída (la caja
    /// trabaja con su copia y no le manda la llave), y si no la conoce, la pantalla la da de alta otra vez (<see cref="SinAlta"/>).
    /// Vacío si no faltaba.
    /// </summary>
    private async Task<Comprobacion?> ComprobarSiFaltaAsync()
    {
        if (!_conexion.SinComprobar || _redDeLaCaja?.Reencuentro is not { Aplica: true } reencuentro)
            return null;
        await _comprobando.WaitAsync();
        try
        {
            if (!_conexion.SinComprobar)
                return Comprobacion.Es;
            var resultado = await reencuentro.ComprobarGuardadaAsync();
            if (resultado is Comprobacion.Es or Comprobacion.SinLaPrueba)
                _conexion.SinComprobar = false;
            else
            {
                if (_red.Fallo())
                    Cambio?.Invoke();
                if (resultado == Comprobacion.NoLaConoce && _conexion.UsuarioEnCaja is null)
                    SinAlta?.Invoke("La caja principal ya no reconoce a esta PC (la quitaron o la dieron de alta en otra): el dueño la da de alta otra vez.");
            }
            return resultado;
        }
        finally
        {
            _comprobando.Release();
        }
    }

    /// <summary>
    /// Antes de una llamada a la caja principal: si falta que pruebe quién es y la red no se sabe caída (al abrir la caja), se
    /// comprueba ya, sin esperar al latido, a lo más <see cref="EsperaComprobacion"/>. Después de una caída no espera: la caja sigue
    /// con su copia hasta que el latido la compruebe.
    /// </summary>
    internal async Task ComprobarAntesDeLlamarAsync()
    {
        if (!_conexion.SinComprobar || _red.Estado == EstadoRed.Caida)
            return;
        try
        {
            await ComprobarSiFaltaAsync().WaitAsync(EsperaComprobacion);
        }
        catch (TimeoutException)
        {
            // Sigue sin comprobar: la llamada se hace con la copia.
        }
    }

    /// <summary>
    /// Algo que el cajero esperaba no llegó a tiempo (pasó su plazo, <see cref="Transporte"/>): la red está lenta, y la caja
    /// trabaja como sin conexión hasta que el latido diga que está buena (entrega 5, parte 2).
    /// </summary>
    public void MarcarLenta()
    {
        if (_red.PlazoVencido())
            Cambio?.Invoke();
    }

    /// <summary>El latido, con lo que esta caja tiene por subir y lo que no pudo subir (entrega 6).</summary>
    private string RutaEstado => _conexion.CajaId is { } esta ? $"api/estado?caja={esta}&porSubir={Pendientes}&conError={ConError}" : "api/estado";

    /// <summary>
    /// Acaba de subir lo que tenía: el servicio lo sabe ya, no hasta el siguiente latido. Si la caja se apaga en esos
    /// segundos (se reconectó al final del día y la cerraron), el corte del día no se queda esperando lo que ya subió.
    /// </summary>
    private async Task AvisarLoQueQuedaAsync()
    {
        try
        {
            await _http.GetAsync<EstadoServicioDto>(RutaEstado, espera: EsperaLatido);
        }
        catch (ErrorCaja)
        {
            // Lo dice el siguiente latido.
        }
    }

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
                    await _http.PostJsonAsync(elemento.Ruta, elemento.Json, EsperaSubida);
                    await _almacen.MarcarSubidoAsync(elemento.Orden, Ahora);
                }
                catch (ErrorCaja ex) when (ex.Codigo != "sin-conexion")
                {
                    await _almacen.MarcarErrorAsync(elemento.Orden, ex.Message);
                }
            }
            if (_conexion.CajaId is { } caja && UltimoFolio > 0)
                await _http.PostAsync<object>($"api/cajas/{caja}/folio-usado", new FolioUsadoDto(UltimoFolio), sinRespuesta: true);
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            NoContesto(ex);
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

    /// <summary>
    /// Baja la copia para vender sin conexión: catálogo, usuarios con sus permisos, ticket y el último folio. Todo fuera del
    /// hilo de la pantalla: leerla, guardarla y armar sus índices y su foto; al final se cambia por la anterior de un jalón.
    /// </summary>
    public Task ActualizarPaqueteAsync() => Task.Run(BajarCopiaAsync);

    private async Task BajarCopiaAsync()
    {
        await _carga.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_conexion.CajaId is not { } caja)
            return;
        _bajandoCopia = true;
        try
        {
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            if (await _http.GetAsync<PaqueteContingenciaDto>($"api/cajas/{caja}/paquete", espera: EsperaCopia) is not { } paquete)
                return;
            _rendimiento?.Actividad("la sincronización (bajar la copia del catálogo)", reloj.Elapsed);
            reloj.Restart();
            Poner(new Copia(paquete, _copia));
            _rendimiento?.Actividad("la sincronización (armar la copia y su foto)", reloj.Elapsed);
            (_paqueteBajadoUtc, _completaDesdeUtc) = (Ahora, Ahora);
            Cambio?.Invoke();
            await GuardarAsync(paquete);
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            NoContesto(ex);
        }
        catch (ErrorCaja)
        {
            // Se intenta otra vez con el siguiente latido.
        }
        finally
        {
            _bajandoCopia = false;
        }
    }

    /// <summary>
    /// La copia en la base de la caja, con su folio. El folio, como cualquier escritura; la copia (miles de productos) se
    /// convierte aquí, fuera de la cola, y se guarda en pedazos: lo que escribe la venta mientras tanto no la espera completa,
    /// y lo que se lee no la espera.
    /// </summary>
    private async Task GuardarAsync(PaqueteContingenciaDto paquete)
    {
        await _almacen.EscribirAsync(l => l.Guardar("folio", Math.Max(l.Leer<int?>("folio") ?? 0, paquete.UltimoFolio)), "el folio de la copia");
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        await _almacen.GuardarCopiaAsync(JsonSerializer.SerializeToUtf8Bytes(paquete, AlmacenLocal.Json));
        (_copiaGuardadaUtc, _existenciasSinGuardar) = (Ahora, false);
        _rendimiento?.Actividad("la sincronización (guardar la copia en la base de la caja)", reloj.Elapsed);
    }

    // ── La copia al día: lo que cambió, avisado por la caja principal (entrega 5, parte 2) ──

    /// <summary>
    /// Pone la copia al día con las versiones que se conocen de la caja principal (las del aviso o las del latido): pide lo que
    /// cambió desde la suya y lo aplica; si no se puede (no hay copia, es de otra base o de otro formato, la caja principal es de
    /// antes o lo que llegó no cuadra), baja la copia completa, como antes. Una a la vez: lo que se avise mientras, se revisa al
    /// terminar. Nunca detiene un escaneo: la foto cambia de un jalón al final.
    /// </summary>
    public Task PonerAlDiaAsync() => Task.Run(async () =>
    {
        _otraVez = true;
        // Lo que se avise justo cuando la otra vuelta ya terminó, pero todavía no suelta, lo atiende ella al soltar.
        while (_otraVez && !Detenida && await _poniendoAlDia.WaitAsync(0))
        {
            try
            {
                while (_otraVez && !Detenida)
                {
                    _otraVez = false;
                    await PonerAlDiaUnaVezAsync();
                }
            }
            finally
            {
                _poniendoAlDia.Release();
            }
        }
    });

    private async Task PonerAlDiaUnaVezAsync()
    {
        await _carga.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_conexion.CajaId is not { } caja || _dePrincipal is not { } principal)
            return;
        var copia = _copia?.Paquete;
        // La caja principal arma la copia en otro formato (una de las dos se actualizó antes que la otra): no se le piden cambios
        // y la copia completa se baja como con una de antes, solo cuando cambia su versión del catálogo o cada tanto. Antes se
        // bajaba completa con cada latido y cada aviso: la que llegaba nunca era del formato de esta caja.
        if ((principal.Formato ?? FormatoDeAntes) != CopiaAlDia.Formato)
        {
            if (copia is null || copia.Version != principal.Catalogo || Ahora - _paqueteBajadoUtc > PaqueteViejo)
                await BajarCopiaAsync();
            return;
        }
        // Sin los exhibidores o sin las fotos, una copia de este formato la guardó una versión de esta caja que todavía no los
        // conocía (y los cambios solo los traen cuando cambian): se baja completa una vez (entrega 13, partes 1 y 2).
        if (copia is null || copia.Base != principal.Base || copia.Formato != CopiaAlDia.Formato || copia.VersionExistencias is not { } existencias
            || copia.Exhibidores is null || copia.Fotos is null || Ahora - _completaDesdeUtc > CopiaCompletaCada)
        {
            await BajarCopiaAsync();
            return;
        }
        // Ya está (o es más nueva que lo que se avisó: bajó completa después del aviso).
        if (copia.Version >= principal.Catalogo && existencias >= principal.Existencias)
        {
            if (_existenciasSinGuardar && Ahora - _copiaGuardadaUtc > GuardarExistenciasCada)
                await GuardarAsync(copia);
            return;
        }
        if (Ahora < _sinCambiosHasta)
        {
            await BajarCopiaAsync();
            return;
        }
        CambiosCopiaDto? cambios;
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            cambios = await _http.GetAsync<CambiosCopiaDto>(
                $"api/cajas/{caja}/cambios?desde={copia.Version}&existencias={existencias}&base={copia.Base}&formato={CopiaAlDia.Formato}"
                + $"&vendidos={copia.VersionVendidos ?? 0}&elegidos={copia.VersionElegidos ?? 0}",
                noEncontradoEsNulo: true, espera: EsperaCambios);
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            NoContesto(ex);
            return;
        }
        catch (ErrorCaja)
        {
            // Lo que no salió (un error del servicio): la copia completa, como antes.
            await BajarCopiaAsync();
            return;
        }
        if (cambios is null)
        {
            // Una caja principal de antes, sin los cambios: la copia completa, y no se vuelve a preguntar en un rato.
            _sinCambiosHasta = Ahora + ServicioDeAntesPor;
            await BajarCopiaAsync();
            return;
        }
        if (cambios.PaqueteCompleto is not null)
        {
            await BajarCopiaAsync();
            return;
        }
        _rendimiento?.Actividad("la sincronización (pedir lo que cambió)", reloj.Elapsed);
        try
        {
            await AplicarAsync(copia, cambios);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or NullReferenceException)
        {
            // Lo que llegó no cuadra con la copia: la copia completa.
            await BajarCopiaAsync();
        }
    }

    /// <summary>
    /// Lo que cambió, sobre la copia (<see cref="CopiaAlDia.Aplicar"/>), con su foto armada aquí, fuera del hilo de la pantalla,
    /// igual que al bajarla completa; se cambia de un jalón. Lo del catálogo se guarda al momento en la base de la caja (por el
    /// escritor de siempre, en pedazos); lo que solo cambió de existencias, cada tanto.
    /// </summary>
    private async Task AplicarAsync(PaqueteContingenciaDto copia, CambiosCopiaDto cambios)
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var nueva = CopiaAlDia.Aplicar(copia, cambios);
        // Solo existencias (lo de cada venta), lo más vendido o lo elegido: lo que sale de los nombres se queda; la foto, otra vez,
        // como siempre. Si no cambia nada de lo que arma la búsqueda por nombre (entrega 5, parte 3), su índice tampoco.
        var anterior = _copia;
        var deEsta = anterior is not null && ReferenceEquals(anterior.Paquete, copia);
        var armada = !cambios.CambiaCatalogo && deEsta
            ? Copia.ConOtrasExistencias(nueva, anterior!)
            : new Copia(nueva, anterior, mismaBusqueda: deEsta && !cambios.CambiaLaBusqueda);
        // Si mientras tanto bajó otra (o empezó de cero), esta ya no.
        if (!ReferenceEquals(_copia?.Paquete, copia))
            return;
        Poner(armada);
        _rendimiento?.Actividad("la sincronización (aplicar lo que cambió y armar la foto)", reloj.Elapsed);
        Cambio?.Invoke();
        if (cambios.CambiaCatalogo || Ahora - _copiaGuardadaUtc > GuardarExistenciasCada)
            await GuardarAsync(nueva);
        else
        {
            _existenciasSinGuardar = true;
            // El último folio de la serie de esta caja, como lo dice la caja principal: sin guardar la copia entera.
            if (cambios.UltimoFolio > copia.UltimoFolio)
                await _almacen.EscribirAsync(l => l.Guardar("folio", Math.Max(l.Leer<int?>("folio") ?? 0, cambios.UltimoFolio)), "el folio de la copia");
        }
    }

    /// <summary>
    /// El aviso de la caja principal (SSE, <c>api/cajas/{id}/avisos</c>): se queda conectado y, con cada versión nueva, la caja
    /// se pone al día (y pide los saldos del crédito si cambiaron). Si se corta (o se queda callado: ni el «vivo» de cada 15 s),
    /// se vuelve a conectar, esperando cada vez un poco más. Con una caja principal de antes (sin el aviso) queda el latido.
    /// </summary>
    private async Task EscucharAvisosAsync(CancellationToken ct)
    {
        var espera = ReconectarAviso;
        try
        {
            try
            {
                await _conexion.Listo.WaitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Si no arrancó, el latido lo dice.
            }
            while (!ct.IsCancellationRequested)
            {
                if (_conexion.Lista && _conexion.CajaId is { } caja && Ahora >= _sinAvisoHasta && !_conexion.SimularSinConexion && !_conexion.SinComprobar)
                {
                    try
                    {
                        if (await EscucharAvisoAsync(caja, ct))
                            espera = ReconectarAviso;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        // Se cortó: se vuelve a conectar.
                    }
                }
                await Task.Delay(espera + TimeSpan.FromMilliseconds(Random.Shared.Next(250)), _reloj, ct);
                espera = TimeSpan.FromTicks(Math.Min(espera.Ticks * 2, ReconectarAvisoTope.Ticks));
            }
        }
        catch (OperationCanceledException)
        {
            // Se cerró la caja.
        }
    }

    /// <summary>Una conexión del aviso, hasta que se corta. Dice si llegó algo (para no esperar de más al volver a conectar).</summary>
    private async Task<bool> EscucharAvisoAsync(Guid caja, CancellationToken ct)
    {
        using var respuesta = await _http.AbrirAvisoAsync($"api/cajas/{caja}/avisos", EsperaCambios, ct);
        if (respuesta is null)
        {
            // Una caja principal de antes: el latido basta.
            _sinAvisoHasta = Ahora + ServicioDeAntesPor;
            return false;
        }
        using var callado = new CancellationTokenSource(AvisoCallado, _reloj);
        using var fin = CancellationTokenSource.CreateLinkedTokenSource(ct, callado.Token);
        await using var flujo = new FlujoVigilado(await respuesta.Content.ReadAsStreamAsync(ct), callado, AvisoCallado);
        var llego = false;
        await foreach (var aviso in SseParser.Create(flujo).EnumerateAsync(fin.Token))
        {
            llego = true;
            if (aviso.EventType == "versiones" && JsonSerializer.Deserialize<VersionesCopiaDto>(aviso.Data, AlmacenLocal.Json) is { } versiones)
                Avisaron(versiones);
        }
        return llego;
    }

    /// <summary>La caja principal avisó sus versiones: la copia se pone al día y, si cambiaron, se piden los saldos del crédito.</summary>
    private void Avisaron(VersionesCopiaDto versiones)
    {
        _dePrincipal = versiones;
        if (_saldos is { } saldos && saldos.Version != versiones.Credito)
            _ = Task.Run(ActualizarCreditosAsync);
        _ = PonerAlDiaAsync();
    }

    /// <summary>
    /// El cuerpo del aviso: cada vez que llega algo (un aviso o el «vivo»), se le da otro rato; si se queda callado ese rato, la
    /// lectura se corta y el aviso se vuelve a conectar.
    /// </summary>
    private sealed class FlujoVigilado(Stream interno, CancellationTokenSource callado, TimeSpan rato) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var leidos = await interno.ReadAsync(buffer, cancellationToken);
            if (leidos > 0)
                callado.CancelAfter(rato);
            return leidos;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => interno.Read(buffer, offset, count);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                interno.Dispose();
            base.Dispose(disposing);
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
            var huerfanos = enviados.Where(id => !incluidos.Contains(id)).ToList();
            var (olvidarAntes, huerfanoAntes) = (Ahora - RecordarConfirmado, Ahora - CreditoHuerfano);
            await _almacen.EscribirAsync(l => l.Guardar("saldos", saldos).ConfirmarCredito(saldos.Incluidos, olvidarAntes)
                .QuitarCreditoHuerfano(huerfanos, huerfanoAntes), "los saldos del crédito");
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
            NoContesto(ex);
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
    /// su diario y el último folio, y lo que llegó de la caja principal encima de la copia).
    /// </summary>
    public void OlvidarCredito()
    {
        _almacen.Escribir(l => l.LimpiarCredito().Borrar("saldos").Borrar("turno").Borrar("base").Borrar("folio").Borrar(ClaveEspera)
            .Borrar(ClaveSinConfirmar).LimpiarDiario(), "empezar de cero");
        lock (_candadoSinConfirmar)
            _sinConfirmar = ImmutableDictionary<Guid, bool>.Empty;
        _saldos = null;
        _creditosPedidos = null;
        // Los productos y precios de encima eran de la base que se quitó: hasta que baje la copia nueva, solo la copia.
        if (_copia is { } copia)
            Volatile.Write(ref _foto, copia.Foto);
    }

    private const string ClaveEspera = "espera";

    /// <summary>Los tickets en espera de esta caja, en el orden en que se apartaron.</summary>
    public List<TicketGuardado> TicketsEnEspera() => _almacen.Leer<List<TicketGuardado>>(ClaveEspera) ?? [];

    /// <summary>Se guardan en la cola del escritor: la pantalla no se detiene mientras.</summary>
    public Task GuardarEnEsperaAsync(IReadOnlyList<TicketGuardado> tickets) =>
        _almacen.EscribirAsync(l => _ = tickets.Count == 0 ? l.Borrar(ClaveEspera) : l.Guardar(ClaveEspera, tickets), "los tickets en espera");

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
        _almacen.Escribir(l => l.Encolar(trabajo.Id, "api/impresiones", trabajo, bloquea: false), "una impresión medida");
        Recontar();
        if (ServidorResponde)
            _ = Task.Run(SubirAsync);
    }

    // ── Lo que se hace sin conexión ─────────────────────────────────────────

    private Copia Datos => CopiaCargada() ?? throw new ErrorCaja(
        "No hay conexión con la caja principal y esta caja todavía no tiene su copia para vender sin ella.", "sin-conexion");

    private CajaDto Caja => Datos.Paquete.Caja;

    public List<CajaDto> Cajas() => [Caja];

    public SesionDto Entrar(string usuario, string pin) => Sesion(Identificar(usuario, pin));

    /// <summary>
    /// Las fichas de la entrada sin la caja principal (entrega 6): de la copia local, que ya trae a los usuarios de la
    /// sucursal (los dados de baja, solo para las letras) y sus permisos; quien tiene el turno abierto, del turno de esta
    /// caja. Iguales a las del servicio.
    /// </summary>
    public List<FichaUsuarioDto> Fichas(Guid cajaId) =>
        FichasEntrada.Armar(Datos.Activos, Datos.UsuariosPorId.Values.Select(u => (u.Id, u.Nombre)), Datos.Defecto, Turno(cajaId)?.CajeroId);

    /// <summary>Entrar con la ficha: el PIN contra la copia local, con el mismo bloqueo por intentos que en el servicio.</summary>
    public SesionDto Entrar(Guid usuarioId, string pin)
    {
        if (Datos.UsuariosPorId.GetValueOrDefault(usuarioId) is not { Activo: true } usuario)
            throw new InvalidOperationException("Ese usuario no entra en esta caja.");
        return Sesion(Identificar(usuario.Usuario, pin, FichasEntrada.PinIncorrecto));
    }

    /// <summary>Quien entró, con todo lo que puede y quién autoriza cada cosa, como lo manda el servicio.</summary>
    private SesionDto Sesion(UsuarioLocalDto u) =>
        new(u.Id, u.Nombre, u.Rol, PermisosDeSesion(u.Id), FichasEntrada.Resumen(Datos.PermisosDe(u.Id), Datos.Defecto));

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
        _almacen.Escribir(lote => lote.Encolar(id, "api/bandeja/turnos", new TurnoContingenciaDto(id, s.CajaId, s.CajeroId, s.FondoCentavos, ahora))
            .Guardar("turno", turno)
            .Guardar("base", new BaseTurno(id, s.FondoCentavos, new AnotacionDiario(Guid.Empty, id)))
            .LimpiarDiario(), "abrir el turno sin conexión");
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
        _almacen.Escribir(lote => lote
            .Encolar(id, $"api/bandeja/turnos/{turnoId}/movimientos", new MovimientoContingenciaDto(id, m.Tipo, m.MontoCentavos, motivo, m.UsuarioId, ahora))
            .Encolar(bitacora.Id, "api/bandeja/bitacora", bitacora)
            .Anotar(new AnotacionDiario(id, turnoId, Entradas: entrada ? m.MontoCentavos : 0, Salidas: entrada ? 0 : m.MontoCentavos)),
            "una entrada o salida sin conexión");
        Recontar();
        return new ComprobanteMovimientoDto(id.ToString()[^6..].ToUpperInvariant(), m.Tipo, m.MontoCentavos, motivo, HoraMexico.ALocal(ahora),
            Caja.Nombre, Nombre(m.UsuarioId), Datos.Paquete.Encabezado);
    }

    public EfectivoCajaDto Efectivo(Guid turnoId)
    {
        var corte = Corte(TurnoRequerido(turnoId), TipoCorte.X, "", null);
        return new EfectivoCajaDto(corte.EsperadoCentavos, Datos.Paquete.LimiteEfectivoCentavos);
    }

    /// <summary>
    /// La configuración de la tienda de la copia local (entrega 7). Una copia de antes no la trae: lo de siempre (3 fichas, sin
    /// fondo sugerido), con su límite de efectivo.
    /// </summary>
    public ConfiguracionSucursalDto ConfiguracionSucursal()
    {
        if (Datos.Paquete.Configuracion is { } configuracion)
            return configuracion;
        var omision = new Nucleo.Configuracion.ConfiguracionSucursal();
        return new ConfiguracionSucursalDto(null, null, null, omision.HoraApertura, omision.HoraCierre, Datos.Paquete.LimiteEfectivoCentavos, null,
            omision.UmbralCorteCentavos, omision.FichasEnEspera);
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
        if (delServicio is null && CopiaCargada() is not null && Clientes().FirstOrDefault(c => c.Id == clienteId) is { } cliente)
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
        if (Datos.UsuariosPorId.GetValueOrDefault(usuarioId) is not { Activo: true } usuario)
            throw new InvalidOperationException("Ese usuario no está en la copia de esta caja.");
        return Permisos.DeLaSesion(Datos.PermisosDe(usuario.Id), Datos.Activos, usuario.Id);
    }

    /// <summary>Con la copia local de los PIN. Vale una vez, como en línea; queda en la bitácora al usarse.</summary>
    public AutorizacionDto Autorizar(SolicitudAutorizacion s)
    {
        var autorizador = Identificar(s.Usuario, s.Pin);
        if (!Permisos.PuedeAutorizar(Datos.PermisosDe(autorizador.Id), s.Accion, s.MontoCentavos))
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

    /// <summary>
    /// El corte X a ciegas, con la cuenta de esta caja y los permisos de su copia. Las transferencias por verificar no se
    /// saben sin la caja principal.
    /// </summary>
    public CorteInformativoDto CorteInformativo(Guid turnoId, Guid usuarioId)
    {
        var turno = TurnoRequerido(turnoId);
        var c = Corte(turno, TipoCorte.X, Nombre(usuarioId), null);
        return new CorteInformativoDto(turno.Cajero, turno.AbiertoLocal, c.Ventas, c.Cancelaciones, c.TarjetaCentavos, c.TransferenciaCentavos, null,
            c.CreditoCentavos, c.Abonos, c.AbonosTarjetaCentavos, c.AbonosTransferenciaCentavos,
            Evaluar(usuarioId, AccionControlada.CorteZ, null).Nivel == NivelPermiso.Permitido,
            Permisos.QuienesPueden(Datos.Activos, AccionControlada.CorteZ),
            Evaluar(usuarioId, AccionControlada.CorteDelDia, null).Nivel == NivelPermiso.Permitido, EnContingencia: true);
    }

    /// <summary>
    /// El corte del turno sin conexión (2026-10-14): quien entró, con el permiso en la copia de la caja, escribe el total
    /// que contó; no se le vuelve a pedir el PIN. La caja cierra el turno con su propia cuenta y se cuadra al subir.
    /// </summary>
    public CorteDto CorteZ(Guid turnoId, SolicitudCorteZ s)
    {
        // El piloto con eleventa: el corte de esta caja se hace en eleventa, también sin conexión (al subir, el servicio ya
        // no lo podría rechazar: ya pasó).
        if (PilotoEleventa)
            throw new InvalidOperationException(PilotoConEleventa.CorteEnEleventa);
        var turno = TurnoRequerido(turnoId);
        if (Evaluar(s.UsuarioId, AccionControlada.CorteZ, null).Nivel != NivelPermiso.Permitido)
        {
            Encolar(Registro(s.UsuarioId, AccionControlada.CorteZ, "Intento sin permiso", null, resultado: ResultadoBitacora.Negado));
            throw new ErrorCaja($"El corte del turno lo hace quien tiene ese permiso: "
                + $"{Permisos.Nombres(Permisos.QuienesPueden(Datos.Activos, AccionControlada.CorteZ))}.", "no-permitido");
        }
        if (s.ContadoCentavos < 0)
            throw new InvalidOperationException("El efectivo contado no puede ser negativo.");

        var corte = Corte(turno, TipoCorte.Z, Nombre(s.UsuarioId), s.ContadoCentavos);
        // El mismo Id con que se mandó en línea: si la caja principal sí lo guardó, al subir ya está.
        var id = s.Id ?? Ids.Nuevo();
        var ahora = Ahora;
        _almacen.Escribir(lote => lote.Encolar(id, $"api/bandeja/turnos/{turnoId}/corte-z", new CorteContingenciaDto(id, s.UsuarioId, null, ahora, s.ContadoCentavos))
            .Borrar("turno").Borrar("base").LimpiarDiario(), "el corte sin conexión");
        Recontar();
        return corte;
    }

    /// <summary>Sin conexión, solo con lo de la copia (cobrar sin conexión solo conoce lo de ella).</summary>
    public ProductoVentaDto? PorCodigo(string codigo) => Datos.Foto.PorCodigo(codigo);

    public ProductoVentaDto? PorVariante(Guid varianteId) => Datos.Foto.PorVariante(varianteId);

    /// <summary>
    /// La misma búsqueda del servicio, con la copia (entrega 5, parte 3): sin acentos, en cualquier orden, con los sinónimos de
    /// la tienda y lo más vendido. Sin la caja, como el servicio sin ella: sin lo elegido antes.
    /// </summary>
    public List<ResultadoBusquedaDto> Buscar(string texto, int maximo = 30) => Datos.Foto.Buscar(texto, maximo, conLoElegido: false);

    /// <summary>
    /// Cobra con la copia del catálogo y el siguiente folio de la caja. Los precios cambiados, los descuentos y pasar el
    /// límite del crédito se revisan con los permisos de la copia local y las autorizaciones que se dieron sin conexión (se
    /// gastan cuando la venta quedó guardada). Lo fiado, con el saldo que conoce esta caja.
    /// </summary>
    public TicketDto Cobrar(SolicitudCobro s)
    {
        // Se mandó y no se sabe si quedó: con la copia sería otra venta con el mismo Id (ClienteCaja.CobrarAsync).
        if (s.VentaId is { } enviada && CobroSinConfirmar(enviada))
            throw ErrorSinConfirmar(enviada, s.Cobros.Any(c => c.Forma == FormaPago.Credito));
        var datos = Datos;
        var caja = Caja;
        var turno = TurnoRequerido(s.TurnoId);
        if (s.Impresiones is { Count: > 0 })
            throw new InvalidOperationException("Las impresiones se cobran con conexión con la caja principal: quítalas del ticket o espera a que vuelva.");
        // El piloto con eleventa: solo efectivo también sin conexión (al subir, el servicio ya no lo puede rechazar).
        if (PilotoEleventa && s.Cobros.Any(c => c.Forma != FormaPago.Efectivo))
            throw new InvalidOperationException(PilotoConEleventa.NoSeCobra);
        var ahora = Ahora;
        // El cliente va antes que las líneas: con mayoreo siempre, sus precios son otros.
        ClienteRegistradoDto? cliente = null;
        if (s.ClienteId is { } clienteId && (cliente = Clientes().FirstOrDefault(c => c.Id == clienteId)) is null)
            throw new InvalidOperationException("Ese cliente no está en la copia de esta caja: quítalo del ticket (CTRL+L).");
        // Tampoco sin conexión se cobra con precios que el cajero no vio (entrega 5, parte 2): con la red lenta la copia se sigue
        // poniendo al día. La pantalla pone el ticket al día con los de la copia y lo dice, como con lo que contesta la caja
        // principal; el cobro no se repite solo.
        if (PreciosCambiados(datos, s) is { } cambiados)
            throw new ErrorCaja(cambiados.Mensaje, PreciosCambiadosDto.CodigoDelError) { PreciosCambiados = cambiados };
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
                    ?? throw new ErrorCaja(RequiereAutorizacion, "requiere-autorizacion");
            else
                Requerir(decision);
            bitacora.Add(Registro(s.CajeroId, AccionControlada.CambiarPrecio,
                $"Precio de «{cambiada.Descripcion}»: {cambiada.PrecioOriginalCentavos / 100m:N2} → {cambiada.PrecioCentavos / 100m:N2}",
                cambiada.ImporteCentavos, autorizo, autorizo is null ? ResultadoBitacora.Hecho : ResultadoBitacora.Autorizado, venta.Id));
        }

        // Descuentos: el mismo permiso con la copia local. Sin conexión no se sabe el costo: eso no se revisa aquí.
        if (venta.DescuentoCentavos > 0)
        {
            var revisar = venta.DescuentoARevisar(cliente?.DescuentoCentesimas is > 0 and var d ? d : null);
            Guid? autorizo = null;
            if (revisar > 0)
            {
                var decision = Evaluar(s.CajeroId, AccionControlada.Descuento, revisar);
                if (decision.Nivel == NivelPermiso.RequiereAutorizacion)
                    autorizo = Autorizada(s.AutorizacionDescuentoId, AccionControlada.Descuento, revisar, usadas)
                        ?? throw new ErrorCaja("Este descuento requiere autorización.", "requiere-autorizacion");
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
                        ?? throw new ErrorCaja("Pasar el límite requiere autorización.", "requiere-autorizacion");
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
        // «Al cobrar» (Configuración › Ticket), como lo trae la copia; y que se cobró sin la caja principal: hasta que suba no
        // se puede reimprimir, así que «No imprimirlo» se ofrece (TicketDto.LoQueSeHaceAlCobrar).
        var ticket = ArmadoTicket.Desde(venta, caja.Nombre, Nombre(s.CajeroId),
            id => datos.Presentaciones.TryGetValue(id, out var p) ? (p.Nombre, p.EsBase) : null,
            datos.Paquete.Encabezado, datos.Paquete.Pie, datos.Paquete.Logo, cliente,
            cuenta is null ? null : new CreditoTicketDto(aCredito, cuenta.SaldoCentavos + aCredito, vence, Estimado: true))
            with { AlCobrar = datos.Paquete.AlCobrar, EnContingencia = true };
        var subida = new VentaContingenciaDto(venta.Id, caja.Id, turno.Id, s.CajeroId, folio, ahora,
            venta.Lineas.Select(x => new LineaContingenciaDto(x.VarianteId, x.PresentacionId, x.Descripcion, x.Cantidad.Milesimas, x.PrecioCentavos,
                x.DescuentoCentesimas, x.DescuentoCentesimas is null && x.TieneDescuento ? x.DescuentoCentavos : null, x.MotivoDescuento)).ToList(),
            s.Cobros, venta.ClienteId, venta.RedondeoCentavos, venta.MayoreoDeCliente, cuenta?.SaldoCentavos, excesoAutorizado, vence);

        _almacen.Escribir(lote =>
        {
            lote.Encolar(venta.Id, "api/bandeja/ventas", subida);
            foreach (var registro in bitacora)
                lote.Encolar(registro.Id, "api/bandeja/bitacora", registro);
            if (cuenta is not null)
                lote.Credito(venta.Id, cliente!.Id, aCredito, ahora, vence);
            lote.Anotar(Anotacion(turno.Id, ticket)).Guardar("folio", folio);
        }, "un cobro sin conexión");
        Gastar(usadas);
        Recontar();
        return ticket;
    }

    /// <summary>
    /// Lo del ticket cuyos precios en la copia no son los que vio el cajero: llegaron después de la versión con que se pusieron
    /// los de su línea (<see cref="LineaSolicitada.VersionCatalogo"/>, o la del ticket). Una versión más nueva que la copia salió
    /// de lo que llegó encima de ella (lo dijo el cobro, o la caja principal tenía el código): vale si sus precios son los de la
    /// copia. Sin versión (una pantalla de antes), como siempre. Con los productos como están en la copia.
    /// </summary>
    private PreciosCambiadosDto? PreciosCambiados(Copia datos, SolicitudCobro s)
    {
        var foto = Foto;
        var cambiados = new List<ProductoVentaDto>();
        foreach (var lineas in s.Lineas.Where(l => l.VarianteId is not null).GroupBy(l => l.VarianteId!.Value))
        {
            if (lineas.Min(l => l.VersionCatalogo ?? s.VersionCatalogo) is not { } vista || !datos.PorVariante.TryGetValue(lineas.Key, out var enLaCopia))
                continue;
            var losVio = vista > datos.Paquete.Version
                ? foto?.PorVariante(lineas.Key) is not { } visto || Copia.MismosPrecios(visto, enLaCopia)
                : datos.PreciosDesde.GetValueOrDefault(lineas.Key, enLaCopia.Version ?? datos.Paquete.Version) <= vista;
            if (!losVio)
                cambiados.Add(enLaCopia);
        }
        if (cambiados.Count == 0)
            return null;
        var nombres = cambiados.Select(p => $"«{p.Descripcion}»").ToList();
        var lista = nombres.Count == 1 ? nombres[0] : $"{string.Join(", ", nombres.Take(nombres.Count - 1))} y {nombres[^1]}";
        return new PreciosCambiadosDto($"Cambió el precio de {lista} en la copia de esta caja: revisa el total y cobra otra vez.", cambiados,
            datos.Paquete.Version);
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
        if (PilotoEleventa)
            throw new InvalidOperationException(PilotoConEleventa.NoSeAbona);
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
        _almacen.Escribir(lote => lote
            .Encolar(s.Id, "api/bandeja/abonos", new AbonoContingenciaDto(s.Id, caja.Id, turno.Id, s.UsuarioId, cliente.Id, s.Forma, calculo.MontoCentavos,
                calculo.RedondeoCentavos, recibido, calculo.CambioCentavos, referencia, ahora, cuenta.SaldoCentavos, vence))
            .Credito(s.Id, cliente.Id, -calculo.MontoCentavos, ahora, null, transferencia)
            .Anotar(AnotacionAbono(s.Id, turno.Id, comprobante, 1)), "un abono sin conexión");
        Recontar();
        return comprobante;
    }

    // ── Lo del crédito que se manda en línea: se anota antes, por si la respuesta se pierde ──
    // Lo que se anota de lo que pasó en línea va a la cola del escritor (entrega 5): la pantalla lo espera sin detenerse.

    /// <summary>Lo fiado que se manda en línea; si el servicio lo rechaza, se quita (<see cref="QuitarCreditoAsync"/>).</summary>
    public Task AnotarFiadoAsync(Guid ventaId, Guid clienteId, long creditoCentavos)
    {
        var plazo = Credito(clienteId)?.PlazoDias ?? (CopiaCargada() is null ? null : Clientes().FirstOrDefault(c => c.Id == clienteId)?.PlazoCreditoDias)
            ?? ClienteRegistrado.PlazoPorOmision;
        var ahora = Ahora;
        return _almacen.EscribirAsync(l => l.Credito(ventaId, clienteId, creditoCentavos, ahora, CuentaCredito.Vence(ahora, plazo)), "lo fiado");
    }

    /// <summary>
    /// Un abono que se manda en línea, con lo que de verdad baja (el mismo cálculo que el servicio con el saldo que vio la
    /// caja: en efectivo, lo de más es cambio); se corrige con el comprobante.
    /// </summary>
    public Task AnotarAbonoAsync(SolicitudAbono s)
    {
        long monto;
        try
        {
            monto = CalculoAbono.Calcular(s.SaldoVistoCentavos ?? Credito(s.ClienteId)?.SaldoCentavos ?? 0, s.Forma, s.MontoCentavos, s.RecibidoCentavos)
                .MontoCentavos;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Task.CompletedTask;
        }
        if (monto <= 0)
            return Task.CompletedTask;
        var ahora = Ahora;
        return _almacen.EscribirAsync(l => l.Credito(s.Id, s.ClienteId, -monto, ahora, null, s.Forma == FormaPago.Transferencia), "un abono");
    }

    /// <summary>
    /// Un saldo a favor que se devuelve en línea (sube la cuenta): se anota antes de mandarlo, así si se pierde la respuesta
    /// esta caja ya no cuenta con ese saldo a favor; se corrige con el comprobante.
    /// </summary>
    public Task AnotarSaldoDevueltoAsync(Guid id, Guid clienteId, long montoCentavos)
    {
        var plazo = Credito(clienteId)?.PlazoDias ?? ClienteRegistrado.PlazoPorOmision;
        var ahora = Ahora;
        return _almacen.EscribirAsync(l => l.Credito(id, clienteId, montoCentavos, ahora, CuentaCredito.Vence(ahora, plazo)), "un saldo devuelto");
    }

    /// <summary>Si esta caja tiene algo del cliente sin subir: sus saldos del servicio todavía no lo incluyen.</summary>
    public bool CreditoSinSubir(Guid clienteId) => _almacen.CreditoSinSubir(clienteId);

    /// <summary>El servicio rechazó lo que se le mandó (por una regla): no pasó, ya no se cuenta.</summary>
    public Task QuitarCreditoAsync(Guid id) => _almacen.EscribirAsync(l => l.QuitarCredito([id]), "lo que no se fió");

    // ── Los cobros que no se sabe si quedaron (entrega 5, parte 2) ──────────

    private const string ClaveSinConfirmar = "cobros-sin-confirmar";

    /// <summary>Ese cobro se mandó y no se sabe si quedó (<see cref="NoSeSabeSiQuedo"/>): solo se cobra en línea, con su mismo Id.</summary>
    public bool CobroSinConfirmar(Guid ventaId) => _sinConfirmar.ContainsKey(ventaId);

    /// <summary>Lo que se le dice al cajero de ese cobro: lo de lo fiado si se mandó fiado (o se manda así ahora).</summary>
    public ErrorCaja ErrorSinConfirmar(Guid ventaId, bool fiado) => ClienteCaja.SinConfirmar(fiado || _sinConfirmar.GetValueOrDefault(ventaId));

    /// <summary>
    /// La caja principal no contestó a un cobro (puede que lo haya guardado, con el siguiente folio de esta caja) y sin conexión
    /// no se pudo hacer. Ese folio se aparta: el siguiente cobro sin conexión no lo repite (si no quedó, se brinca). El ticket
    /// solo se cobra en línea, con el mismo Id, hasta saberlo: <see cref="Cobrar"/> no lo hace y <see cref="CobroEnLineaAsync"/>
    /// lo suelta. Queda en la base de la caja (también si se reinicia).
    /// </summary>
    /// <param name="fiado">Se mandó fiado: el cajero lee lo de lo fiado (<see cref="ErrorSinConfirmar"/>).</param>
    public void NoSeSabeSiQuedo(Guid ventaId, bool fiado)
    {
        lock (_candadoSinConfirmar)
        {
            if (_sinConfirmar.ContainsKey(ventaId))
                return;
            _almacen.Escribir(l =>
            {
                var registro = l.Leer<Dictionary<Guid, bool>>(ClaveSinConfirmar) ?? [];
                registro[ventaId] = fiado;
                l.Guardar(ClaveSinConfirmar, registro).Guardar("folio", (l.Leer<int?>("folio") ?? 0) + 1);
            }, "un cobro que no se sabe si quedó");
            _sinConfirmar = _sinConfirmar.SetItem(ventaId, fiado);
        }
    }

    // ── Lo que pasó en línea, para la cuenta del turno ──────────────────────

    /// <summary>Al entrar en línea se toma la cuenta del servicio como base; el diario empieza de nuevo.</summary>
    public async Task TurnoEnLineaAsync(TurnoDto? turno)
    {
        if (turno is null)
        {
            await _almacen.EscribirAsync(l => l.Borrar("turno").Borrar("base").LimpiarDiario(), "el turno");
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
        await _almacen.EscribirAsync(lote =>
        {
            lote.Guardar("turno", turno);
            if (resumen is not null)
                lote.Guardar("base", new BaseTurno(turno.Id, resumen.FondoCentavos, new AnotacionDiario(Guid.Empty, turno.Id, resumen.Ventas,
                    resumen.EfectivoVentasCentavos, resumen.TarjetaCentavos, resumen.TransferenciaCentavos, resumen.EntradasCentavos,
                    resumen.SalidasCentavos, resumen.DevolucionesCentavos, resumen.Cancelaciones, resumen.RedondeoCentavos, resumen.CreditoCentavos,
                    resumen.Abonos, resumen.AbonosEfectivoCentavos, resumen.AbonosTarjetaCentavos, resumen.AbonosTransferenciaCentavos,
                    resumen.SaldoDevueltoCentavos))).LimpiarDiario();
        }, "el turno");
    }

    public Task TurnoAbiertoEnLineaAsync(TurnoDto turno) =>
        _almacen.EscribirAsync(l => l.Guardar("turno", turno).Guardar("base", new BaseTurno(turno.Id, turno.FondoCentavos, new AnotacionDiario(Guid.Empty, turno.Id)))
            .LimpiarDiario(), "el turno");

    public Task TurnoCerradoEnLineaAsync() => _almacen.EscribirAsync(l => l.Borrar("turno").Borrar("base").LimpiarDiario(), "el turno");

    public Task CobroEnLineaAsync(Guid turnoId, TicketDto ticket)
    {
        var folio = int.TryParse(ticket.Folio[(ticket.Folio.LastIndexOf('-') + 1)..], out var f) ? f : 0;
        // Lo fiado ya estaba anotado: con el vencimiento que puso el servicio, y que el siguiente latido confirme que ya lo tiene.
        var fiado = ticket.VentaId is { } ventaId && ticket.Credito is { } credito ? (ventaId, credito.Vence) : ((Guid, DateOnly?)?)null;
        if (fiado is not null)
            _refrescarCredito = true;
        // Si no se sabía si había quedado, ya se sabe.
        Guid? sabido = null;
        if (ticket.VentaId is { } id && _sinConfirmar.ContainsKey(id))
        {
            lock (_candadoSinConfirmar)
                _sinConfirmar = _sinConfirmar.Remove(id);
            sabido = id;
        }
        return _almacen.EscribirAsync(lote =>
        {
            if (fiado is { } corregir)
                lote.CorregirCredito(corregir.Item1, null, corregir.Item2);
            if (sabido is { } quitar && lote.Leer<Dictionary<Guid, bool>>(ClaveSinConfirmar) is { } registro && registro.Remove(quitar))
                _ = registro.Count == 0 ? lote.Borrar(ClaveSinConfirmar) : lote.Guardar(ClaveSinConfirmar, registro);
            lote.Anotar(Anotacion(turnoId, ticket)).Guardar("folio", Math.Max(lote.Leer<int?>("folio") ?? 0, folio));
        }, "un cobro");
    }

    public Task MovimientoEnLineaAsync(Guid turnoId, Guid id, ComprobanteMovimientoDto m)
    {
        var entrada = m.Tipo == TipoMovimientoCaja.Entrada;
        return _almacen.EscribirAsync(l => l.Anotar(new AnotacionDiario(id, turnoId, Entradas: entrada ? m.MontoCentavos : 0,
            Salidas: entrada ? 0 : m.MontoCentavos)), "una entrada o salida");
    }

    /// <summary>
    /// El reembolso sale del efectivo del turno abierto de esta caja; una cancelación deja de contar como venta, y su
    /// tarjeta, su transferencia y lo fiado dejan de contar en el corte (como en el del servicio, que solo suma las vigentes).
    /// </summary>
    public Task DevolucionEnLineaAsync(ComprobanteDevolucionDto d) =>
        _almacen.EscribirAsync(lote =>
        {
            if (lote.Leer<TurnoDto>("turno") is not { } turno)
                return;
            lote.Anotar(new AnotacionDiario(Ids.Nuevo(), turno.Id, Ventas: d.EsCancelacion ? -1 : 0, Devoluciones: d.ReembolsoEfectivoCentavos,
                Cancelaciones: d.EsCancelacion ? 1 : 0, Redondeo: -d.RedondeoCentavos,
                Tarjeta: d.EsCancelacion ? -d.TarjetaCentavos : 0, Transferencia: d.EsCancelacion ? -d.TransferenciaCentavos : 0,
                Credito: d.EsCancelacion ? -d.AlCreditoCentavos : 0));
        }, "una devolución");

    /// <summary>Un abono recibido en línea entra a la cuenta del turno: en efectivo suma al esperado.</summary>
    public Task AbonoEnLineaAsync(Guid turnoId, ComprobanteAbonoDto c)
    {
        _refrescarCredito = true;
        // Ya estaba anotado en la cuenta propia: con lo que de verdad bajó, y que el siguiente latido confirme que ya lo tiene.
        return _almacen.EscribirAsync(l => l.CorregirCredito(c.AbonoId, -c.MontoCentavos, null).Anotar(AnotacionAbono(c.AbonoId, turnoId, c, 1)), "un abono");
    }

    /// <summary>Un abono cancelado en línea sale de la cuenta del turno (con su propio renglón: el del abono se queda).</summary>
    public Task AbonoCanceladoEnLineaAsync(Guid turnoId, ComprobanteAbonoDto c)
    {
        var id = Ids.Nuevo();
        return _almacen.EscribirAsync(l => l.Anotar(AnotacionAbono(id, turnoId, c, -1)), "un abono cancelado");
    }

    /// <summary>
    /// Un saldo a favor que se devolvió en línea (parte 3; sin conexión no se devuelve): sale del efectivo del turno, se
    /// corrige lo anotado en la cuenta propia y los saldos se vuelven a pedir.
    /// </summary>
    public Task SaldoDevueltoEnLineaAsync(Guid turnoId, ComprobanteAbonoDto c)
    {
        _refrescarCredito = true;
        // Ya estaba anotado en la cuenta propia: con lo que de verdad subió, y que el siguiente latido confirme que ya lo tiene.
        return _almacen.EscribirAsync(l => l.CorregirCredito(c.AbonoId, c.MontoCentavos, null)
            .Anotar(new AnotacionDiario(c.AbonoId, turnoId, Redondeo: -c.RedondeoCentavos, SaldoDevuelto: c.CobradoCentavos)), "un saldo devuelto");
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
    private CorteDto Corte(TurnoDto turno, TipoCorte tipo, string realizadoPor, long? contado)
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

    private string Nombre(Guid usuarioId) => Datos.UsuariosPorId.TryGetValue(usuarioId, out var u) ? u.Nombre : "—";

    /// <summary>Con los permisos de la copia: lo mismo que decide el servicio. Uno dado de baja no hace nada.</summary>
    private DecisionPermiso Evaluar(Guid usuarioId, AccionControlada accion, long? monto) =>
        Datos.UsuariosPorId.TryGetValue(usuarioId, out var u) && u.Activo
            ? Permisos.Evaluar(Datos.PermisosDe(u.Id), accion, monto)
            : throw new InvalidOperationException("El usuario no está en la copia de esta caja.");

    private static void Requerir(DecisionPermiso decision)
    {
        switch (decision.Nivel)
        {
            case NivelPermiso.NoPermitido:
                throw new ErrorCaja("No tienes permiso para esto.", "no-permitido");
            case NivelPermiso.RequiereAutorizacion:
                throw new ErrorCaja(RequiereAutorizacion, "requiere-autorizacion");
        }
    }

    private const string RequiereAutorizacion = "Esto requiere autorización.";

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
    /// <param name="incorrecto">
    /// Lo que se dice si el PIN no es, con los intentos que le quedan. En la entrada con fichas ya se sabe quién es: dice
    /// cuántos intentos quedan, como el servicio. Sin él, «Usuario o PIN incorrecto.».
    /// </param>
    private UsuarioLocalDto Identificar(string usuario, string pin, Func<int, string>? incorrecto = null)
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
            throw new InvalidOperationException(incorrecto?.Invoke(Usuario.IntentosPermitidos - fallos) ?? "Usuario o PIN incorrecto.");
        }
    }

    private BitacoraContingenciaDto Registro(
        Guid? usuarioId, AccionControlada accion, string detalle, long? monto, Guid? autorizo = null, ResultadoBitacora resultado = ResultadoBitacora.Hecho,
        Guid? ventaId = null) =>
        new(Ids.Nuevo(), Caja.SucursalId, Caja.Id, usuarioId, accion, detalle, monto, autorizo, resultado, Ahora, ventaId);

    private void Encolar(BitacoraContingenciaDto registro)
    {
        _almacen.Escribir(lote => lote.Encolar(registro.Id, "api/bandeja/bitacora", registro), "la bitácora sin conexión");
        Recontar();
    }
}
