using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>Dónde vive la base y sus respaldos en la caja principal.</summary>
public static class RutasServicio
{
    /// <summary>
    /// La configuración de esta PC (<see cref="PapeleriaPos.Servicio.ConfiguracionDeLaPc"/>): <c>servicio.json</c> en ProgramData
    /// (<c>C:\ProgramData\PapeleriaPos</c>), junto a la base y fuera de la carpeta del programa. «ConfiguracionDeLaPc» la cambia
    /// (las pruebas; vacía, ninguna).
    /// <para>
    /// La prueba con una persona real (<c>Prueba:Activa</c>) no la lee: tiene su propia carpeta de datos, puede estar en la misma
    /// PC que el servicio de verdad y tomaría su configuración (el puente con eleventa). Se sigue configurando solo con lo que le
    /// pasa la caja de la prueba al arrancarlo, como antes.
    /// </para>
    /// </summary>
    public static string? ConfiguracionDeLaPc(IConfiguration configuracion)
    {
        if (configuracion.GetValue("Prueba:Activa", false))
            return null;
        if (configuracion["ConfiguracionDeLaPc"] is { } ruta)
            return ruta.Length > 0 ? ruta : null;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PapeleriaPos", "servicio.json");
    }

    /// <summary>Sin configurar, la base va en ProgramData: como servicio de Windows no hay "carpeta actual" útil.</summary>
    public static string CadenaConexion(IConfiguration configuracion)
    {
        if (configuracion.GetConnectionString("Pos") is { Length: > 0 } cadena)
            return cadena;
        var carpeta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PapeleriaPos");
        Directory.CreateDirectory(carpeta);
        return $"Data Source={Path.Combine(carpeta, "pos.db")}";
    }

    /// <summary>
    /// "registros", junto a la base (C:\ProgramData\PapeleriaPos\registros): ahí va la bitácora de red de la caja principal
    /// (red-servicio-AAAA-MM-DD.log), que se abre con el Bloc de notas. Red:Carpeta la cambia.
    /// </summary>
    public static string CarpetaRegistros(IConfiguration configuracion, string cadenaConexion) =>
        configuracion["Red:Carpeta"] is { Length: > 0 } carpeta
            ? carpeta
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(new SqliteConnectionStringBuilder(cadenaConexion).DataSource)) ?? ".", "registros");

    /// <summary>"respaldos", junto a la base, salvo que se configure otra carpeta (una USB, por ejemplo).</summary>
    public static string CarpetaRespaldos(IConfiguration configuracion, string cadenaConexion) =>
        configuracion["Respaldos:Carpeta"] is { Length: > 0 } carpeta
            ? carpeta
            : Path.Combine(CarpetaDeLaBase(cadenaConexion), "respaldos");

    /// <summary>
    /// El piloto: los respaldos que hace el puente de eleventa (Eleventa:CarpetaRespaldos), por omisión en
    /// "respaldos-eleventa", junto a la base: en el disco de la caja 2 (ProgramData) y fuera de OneDrive.
    /// </summary>
    public static string CarpetaRespaldosEleventa(OpcionesEleventa eleventa, string cadenaConexion) =>
        eleventa.CarpetaRespaldos is { Length: > 0 } carpeta ? carpeta : Path.Combine(CarpetaDeLaBase(cadenaConexion), "respaldos-eleventa");

    private static string CarpetaDeLaBase(string cadenaConexion) =>
        Path.GetDirectoryName(Path.GetFullPath(new SqliteConnectionStringBuilder(cadenaConexion).DataSource)) ?? ".";
}

/// <summary>
/// Las copias de la base: la diaria la hace <see cref="RespaldoDiario"/>; la del corte Z, el corte; y la de «Respaldar ahora»,
/// quien tiene «Configuración de la tienda» (<see cref="Ahora"/>, 2026-10-22). Todas con la copia en línea de SQLite
/// (<see cref="Respaldos.Hacer"/>), que toma la base completa, también lo que sigue en el -wal, sin detener la venta.
/// </summary>
public sealed class ServicioRespaldos(string cadenaConexion, string carpeta, int dias, ILogger<ServicioRespaldos> log)
{
    private readonly Lock _candado = new();

    public bool Activo { get; init; } = true;

    /// <summary>
    /// La base contra apagones (entrega 10): si la base no pasó la revisión al arrancar, no se hacen copias. Saldrían
    /// dañadas, quedarían como la más reciente encima de la buena de antes del apagón y, pasados los días de historia,
    /// la limpieza borraría la buena.
    /// </summary>
    public RevisionAlArrancar? Revision { get; init; }

    /// <summary>Dónde se guardan (Respaldos:Carpeta; si no, «respaldos» junto a la base).</summary>
    public string Carpeta => carpeta;

    /// <summary>Los días que se guardan (Respaldos:Dias).</summary>
    public int Dias => dias;

    /// <summary>Por qué ahora no se hacen copias (apagadas, o la base no pasó la revisión al arrancar); vacío si se hacen.</summary>
    public string? NoSeHacenPorque =>
        !Activo ? "En esta caja principal los respaldos están apagados (Respaldos:Activo)."
        : Revision is { BaseDanada: true } revision
            ? $"La base no pasó la revisión al arrancar: la copia saldría dañada. Se guarda la de antes del apagón ({revision.Respaldo ?? "no hay ninguna"})."
            : null;

    public void Hacer(string? etiqueta = null)
    {
        if (!Activo)
            return;
        if (Revision is { BaseDanada: true } revision)
        {
            log.LogWarning("No se hace el respaldo {Etiqueta}: la base no pasó la revisión al arrancar y la copia saldría dañada. Se guarda el de antes del apagón ({Respaldo}).",
                etiqueta ?? "del día", revision.Respaldo ?? "no hay ninguno");
            return;
        }
        try
        {
            Copiar(_ => etiqueta);
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            // Un respaldo fallido no detiene la venta; queda en el registro y se reintenta en la siguiente revisión.
            log.LogError(ex, "No se pudo hacer el respaldo en {Carpeta}.", carpeta);
        }
    }

    // ── «Respaldar ahora» (Configuración › Respaldos, 2026-10-22) ──

    private readonly Lock _candadoAhora = new();
    private RespaldoAhoraDto? _ahora;

    /// <summary>El último «Respaldar ahora» que se pidió: haciéndose, hecho o fallido; vacío si no se ha pedido.</summary>
    public RespaldoAhoraDto? EstadoAhora
    {
        get
        {
            lock (_candadoAhora)
                return _ahora;
        }
    }

    /// <summary>Para las pruebas: lo que espera la copia de «Respaldar ahora» antes de empezar.</summary>
    internal Func<Task>? AntesDeCopiarAhora { get; set; }

    /// <summary>Para las pruebas: la limpieza de los de hace más días (<see cref="Respaldos.Limpiar"/>).</summary>
    internal Func<string, DateOnly, int, int> Limpiar { get; init; } = Respaldos.Limpiar;

    /// <summary>
    /// «Respaldar ahora» (Configuración › Respaldos): una copia en este momento, igual que la diaria y la del corte
    /// (<c>pos-AAAA-MM-DD-ahora-HHmm.db</c>), con su limpieza de los días de historia. Se hace en segundo plano y esto regresa
    /// enseguida, ya empezada (revisión del PR #133): con una base grande, esperarla pasaba de los 15 s que espera la caja, que
    /// lo daba por fallido aunque la copia saliera, y al volver a pedirlo hacía otra completa. Si ya hay uno en camino, no
    /// empieza otro: regresa ese. Quien lo pidió ve cómo va con <see cref="EstadoAhora"/>.
    /// </summary>
    /// <param name="alTerminar">Ya terminado, bien o mal (para la bitácora), antes de que <see cref="EstadoAhora"/> lo diga.</param>
    public RespaldoAhoraDto IniciarAhora(Func<RespaldoAhoraDto, Task>? alTerminar = null)
    {
        if (NoSeHacenPorque is { } porque)
            throw new InvalidOperationException($"No se hizo el respaldo. {porque}");
        RespaldoAhoraDto empezado;
        lock (_candadoAhora)
        {
            if (_ahora is { Haciendo: true } enCamino)
                return enCamino;
            _ahora = empezado = new RespaldoAhoraDto(true, HoraMexico.ALocal(DateTime.UtcNow));
        }
        _ = Task.Run(() => HacerAhoraAsync(empezado, alTerminar));
        return empezado;
    }

    private async Task HacerAhoraAsync(RespaldoAhoraDto empezado, Func<RespaldoAhoraDto, Task>? alTerminar)
    {
        RespaldoAhoraDto terminado;
        try
        {
            if (AntesDeCopiarAhora is { } antes)
                await antes();
            terminado = empezado with { Haciendo = false, Hecho = Copiar(hora => $"ahora-{hora:HHmm}") };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cualquier error termina el que estaba en camino: si no, se quedaría «haciéndose» y no se podría pedir otro.
            log.LogError(ex, "No se pudo hacer el respaldo en {Carpeta}.", carpeta);
            terminado = empezado with { Haciendo = false, Fallo = $"No se pudo hacer el respaldo en {carpeta}: {ex.Message}" };
        }
        if (alTerminar is not null)
        {
            try
            {
                await alTerminar(terminado);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogWarning(ex, "El respaldo terminó, pero no se pudo anotar en la bitácora.");
            }
        }
        lock (_candadoAhora)
            _ahora = terminado;
    }

    /// <summary>El más reciente de la carpeta (el del día, el de un corte Z o el de «Respaldar ahora»); vacío si no hay.</summary>
    public RespaldoDto? Ultimo()
    {
        try
        {
            if (Respaldos.Ultima(carpeta) is not { } ruta)
                return null;
            var archivo = new FileInfo(ruta);
            return new RespaldoDto(ruta, archivo.Length, HoraMexico.ALocal(archivo.LastWriteTimeUtc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "No se pudo leer la carpeta de respaldos {Carpeta}.", carpeta);
            return null;
        }
    }

    /// <summary>
    /// Uno a la vez: la copia (con su etiqueta, según la hora) y la limpieza de los de hace más días. Si la copia salió pero la
    /// limpieza no (un archivo ocupado), el respaldo está hecho: lo que no se borró va aparte (revisión del PR #133), en el
    /// registro y en <see cref="RespaldoDto.AvisoLimpieza"/>.
    /// </summary>
    private RespaldoDto Copiar(Func<DateTime, string?> etiqueta)
    {
        lock (_candado)
        {
            var ahora = HoraMexico.ALocal(DateTime.UtcNow);
            var ruta = Respaldos.Hacer(cadenaConexion, carpeta, ahora, etiqueta(ahora));
            var tamano = new FileInfo(ruta).Length;
            try
            {
                var borradas = Limpiar(carpeta, DateOnly.FromDateTime(ahora), dias);
                log.LogInformation("Respaldo en {Ruta}; se borraron {Borradas} de hace más de {Dias} días.", ruta, borradas, dias);
                return new RespaldoDto(ruta, tamano, ahora);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Respaldo en {Ruta}, pero no se pudieron borrar los de hace más de {Dias} días.", ruta, dias);
                return new RespaldoDto(ruta, tamano, ahora, $"No se pudieron borrar los respaldos de hace más de {dias} días: {ex.Message}");
            }
        }
    }

    public void DiarioSiFalta()
    {
        if (Activo && !Respaldos.HayDelDia(carpeta, DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow))))
            Hacer();
    }
}

/// <summary>
/// Cuántas preguntas por UDP contesta el servicio: a lo más <see cref="PorEquipo"/> por segundo a cada IP y
/// <see cref="EnTotal"/> por segundo entre todas. Así quien pregunta sin parar (o con IP falsas) no deja sin respuesta a las cajas
/// de verdad, que preguntan una vez cada tanto. Lo usa un solo hilo (el que recibe).
/// </summary>
public sealed class LimiteDeRespuestas(int porEquipo = LimiteDeRespuestas.PorEquipo, int enTotal = LimiteDeRespuestas.EnTotal)
{
    public const int PorEquipo = 5;
    public const int EnTotal = 200;

    private readonly Dictionary<IPAddress, int> _deCadaUno = [];
    private long _segundo = long.MinValue;
    private int _total;

    /// <summary>Si se le contesta a <paramref name="quien"/> en este segundo (de un reloj que solo avanza).</summary>
    public bool Contestar(IPAddress quien, long segundo)
    {
        if (segundo != _segundo)
        {
            (_segundo, _total) = (segundo, 0);
            _deCadaUno.Clear();
        }
        if (_total >= enTotal)
            return false;
        var suyas = _deCadaUno.GetValueOrDefault(quien);
        if (suyas >= porEquipo)
            return false;
        _deCadaUno[quien] = suyas + 1;
        _total++;
        return true;
    }
}

/// <summary>Revisa cada hora si ya está la copia del día; así sale aunque la PC se prenda tarde.</summary>
public sealed class RespaldoDiario(ServicioRespaldos respaldos) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var reloj = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            await Task.Run(respaldos.DiarioSiFalta, ct);
        }
        while (await reloj.WaitForNextTickAsync(ct));
    }
}

/// <summary>
/// Contesta a las cajas que buscan al servicio en la red (<see cref="Descubrimiento"/>): solo a las de la red de la tienda (la
/// misma red que alguna tarjeta de cable o Wi-Fi de esta PC; el firewall, además, solo abre el puerto para la red local), y solo
/// con su puerto. Contestar no prueba nada: la caja le pide después al servicio que pruebe quién es, con la huella de su llave.
/// Prendido solo en el servicio de la caja principal (Descubrimiento:Activo; apagado en el de la prueba y en desarrollo).
/// </summary>
public sealed class DescubrimientoEnRed(IServer servidor, RedLocalDelServicio redLocal, ILogger<DescubrimientoEnRed> log) : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, Descubrimiento.Puerto));
            // En Windows, un «puerto inalcanzable» (ICMP) de una caja que ya cerró su búsqueda no corta la recepción.
            Descubrimiento.SinCortesPorIcmp(udp.Client);
        }
        catch (SocketException ex)
        {
            log.LogWarning(ex, "No se pudo escuchar en el puerto {Puerto}: las cajas tendrán que usar la IP fija.", Descubrimiento.Puerto);
            return;
        }

        using (udp)
        {
            var limite = new LimiteDeRespuestas();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var recibido = await udp.ReceiveAsync(ct);
                    if (!Descubrimiento.EsPregunta(recibido.Buffer) || PuertoHttp() is not { } puerto || !redLocal.EsDeLaRedLocal(recibido.RemoteEndPoint.Address))
                        continue;
                    if (limite.Contestar(recibido.RemoteEndPoint.Address, Environment.TickCount64 / 1000))
                        await udp.SendAsync(Descubrimiento.ArmarRespuesta(puerto), recibido.RemoteEndPoint, ct);
                }
                catch (SocketException ex)
                {
                    log.LogWarning(ex, "Falló una respuesta de descubrimiento.");
                }
            }
        }
    }

    private int? PuertoHttp() =>
        servidor.Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(a => Uri.TryCreate(a.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var u) ? u.Port : (int?)null)
            .FirstOrDefault(p => p is not null);
}
