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

    public void Hacer(string? etiqueta = null)
    {
        if (!Activo)
            return;
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

/// <summary>Contesta a las cajas que buscan al servicio en la red (<see cref="Descubrimiento"/>).</summary>
public sealed class DescubrimientoEnRed(IServer servidor, ILogger<DescubrimientoEnRed> log) : BackgroundService
{
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
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var recibido = await udp.ReceiveAsync(ct);
                    if (Descubrimiento.EsPregunta(recibido.Buffer) && PuertoHttp() is { } puerto)
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
