using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Servicio;

/// <summary>Dónde vive la base y sus respaldos en la caja principal.</summary>
public static class RutasServicio
{
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
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(new SqliteConnectionStringBuilder(cadenaConexion).DataSource)) ?? ".", "respaldos");
}

/// <summary>Las copias de la base: la diaria la hace <see cref="RespaldoDiario"/>; la del corte Z, el corte.</summary>
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
            lock (_candado)
            {
                var ahora = HoraMexico.ALocal(DateTime.UtcNow);
                var ruta = Respaldos.Hacer(cadenaConexion, carpeta, ahora, etiqueta);
                var borradas = Respaldos.Limpiar(carpeta, DateOnly.FromDateTime(ahora), dias);
                log.LogInformation("Respaldo en {Ruta}; se borraron {Borradas} de hace más de {Dias} días.", ruta, borradas, dias);
            }
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            // Un respaldo fallido no detiene la venta; queda en el registro y se reintenta en la siguiente revisión.
            log.LogError(ex, "No se pudo hacer el respaldo en {Carpeta}.", carpeta);
        }
    }

    public void DiarioSiFalta()
    {
        if (Activo && !Respaldos.HayDelDia(carpeta, DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow))))
            Hacer();
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
    /// <summary>Lo más que contesta por segundo: una pregunta repetida sin parar (o mal intencionada) no ocupa al servicio.</summary>
    public const int RespuestasPorSegundo = 20;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, Descubrimiento.Puerto));
        }
        catch (SocketException ex)
        {
            log.LogWarning(ex, "No se pudo escuchar en el puerto {Puerto}: las cajas tendrán que usar la IP fija.", Descubrimiento.Puerto);
            return;
        }

        using (udp)
        {
            var (segundo, contestadas) = (0L, 0);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var recibido = await udp.ReceiveAsync(ct);
                    if (!Descubrimiento.EsPregunta(recibido.Buffer) || PuertoHttp() is not { } puerto || !redLocal.EsDeLaRedLocal(recibido.RemoteEndPoint.Address))
                        continue;
                    var ahora = Environment.TickCount64 / 1000;
                    (segundo, contestadas) = ahora == segundo ? (segundo, contestadas + 1) : (ahora, 1);
                    if (contestadas <= RespuestasPorSegundo)
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
