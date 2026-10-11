using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// «Eleventa» en la configuración del servicio de la caja 2 durante el piloto: de dónde leer el catálogo y a qué caja
/// de eleventa mandar cada venta. Los datos reales (la PC de la caja principal, la ruta de su base, la clave, los ID)
/// van en la configuración de esa PC, nunca en el repositorio. La clave puede ir en la variable de entorno
/// Eleventa__Clave.
/// </summary>
public sealed class OpcionesEleventa
{
    /// <summary>Leer el catálogo cada pocos minutos (paso 1).</summary>
    public bool Activo { get; set; }

    /// <summary>Mandar cada venta a eleventa (paso 3). Necesita <see cref="Caja"/> y <see cref="Cajero"/>.</summary>
    public bool Escribir { get; set; }

    /// <summary>La caja de eleventa como la que vende esta caja (<c>CAJAS.ID</c>).</summary>
    public int? Caja { get; set; }

    /// <summary>El cajero de eleventa a cuyo nombre van las ventas (<c>USUARIOS.ID</c>).</summary>
    public int? Cajero { get; set; }

    /// <summary>Cuánto espera el cobro a que eleventa dé el folio antes de imprimir con el de esta caja.</summary>
    public double SegundosAlCobrar { get; set; } = 2;

    /// <summary>Cada cuánto se vuelve a intentar lo que quedó en la bandeja.</summary>
    public int SegundosEntreEnvios { get; set; } = 30;

    /// <summary>La PC de la caja principal de eleventa (nombre o IP).</summary>
    public string? Servidor { get; set; }

    public int Puerto { get; set; } = 3050;

    /// <summary>La ruta de la base en esa PC («C:\…\PDVDATA.FDB»).</summary>
    public string? Base { get; set; }

    public string Usuario { get; set; } = "SYSDBA";

    public string? Clave { get; set; }

    /// <summary>Cada cuánto se lee.</summary>
    public int Minutos { get; set; } = 5;

    public ServidorEleventa? Conexion() => Activo ? Destino() : null;

    /// <summary>A dónde y como qué caja escribir; vacío si no se escribe o falta algo.</summary>
    public (ServidorEleventa Servidor, CajaEnEleventa Caja)? ParaEscribir() =>
        Escribir && Destino() is { } servidor && Caja is { } caja && Cajero is { } cajero
            ? (servidor, new CajaEnEleventa(caja, cajero, IpLocal()))
            : null;

    private ServidorEleventa? Destino() =>
        Servidor is { Length: > 0 } servidor && Base is { Length: > 0 } ruta && Clave is { Length: > 0 } clave
            ? new ServidorEleventa(servidor, ruta, Usuario, clave, Puerto)
            : null;

    /// <summary>La IP de esta PC en la red de la tienda, para <c>CAJAS.ULTIMA_IP</c>, como la anota eleventa.</summary>
    private static string? IpLocal()
    {
        try
        {
            return System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))?.ToString();
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }
}

/// <summary>
/// El piloto, paso 1 (docs/plan.md): cada pocos minutos lee el catálogo, los precios, las promociones y las existencias
/// del eleventa de la caja principal, solo leyendo, y pone al día la base de esta caja. Si eleventa no contesta, se
/// sigue vendiendo con lo último que se leyó y se vuelve a intentar en la siguiente vuelta.
/// </summary>
public sealed class LecturaEleventa(IServiceScopeFactory alcances, OpcionesEleventa opciones, TimeProvider reloj, ILogger<LecturaEleventa> log)
    : BackgroundService
{
    private readonly SemaphoreSlim _ahora = new(0, 1);
    private readonly ServidorEleventa? _servidor = opciones.Conexion();
    private EstadoEleventaDto? _estado;
    private int _lecturas;

    public EstadoEleventaDto Estado => _estado ?? new EstadoEleventaDto(_servidor is not null, _servidor?.ToString(), null, false, null,
        opciones.Activo && _servidor is null ? "Falta el servidor, la base o la clave de eleventa en la configuración." : null, null, 0,
        new Dictionary<string, int>());

    /// <summary>Que lea ya, sin esperar la vuelta (después de cambiar algo en eleventa).</summary>
    public void LeerAhora()
    {
        try
        {
            _ahora.Release();
        }
        catch (SemaphoreFullException)
        {
            // Ya estaba pedida.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_servidor is not { } servidor)
        {
            if (opciones.Activo)
                log.LogWarning("La lectura de eleventa está activa pero falta el servidor, la base o la clave.");
            return;
        }
        var espera = TimeSpan.FromMinutes(Math.Max(1, opciones.Minutos));
        while (!ct.IsCancellationRequested)
        {
            await LeerAsync(servidor, espera, ct);
            try
            {
                await _ahora.WaitAsync(espera, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task LeerAsync(ServidorEleventa servidor, TimeSpan espera, CancellationToken ct)
    {
        var inicio = reloj.GetUtcNow().UtcDateTime;
        try
        {
            var datos = await LectorEleventa.LeerCatalogoAsync(servidor, ct);
            using var alcance = alcances.CreateScope();
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            var resultado = await SincronizacionEleventa.AplicarAsync(db, datos, reloj.GetUtcNow().UtcDateTime, ct);
            _estado = new EstadoEleventaDto(true, servidor.ToString(), HoraMexico.ALocal(inicio), true, resultado.ToString(), null,
                HoraMexico.ALocal(inicio + espera), ++_lecturas, resultado.Avisos);
            if (resultado.HuboCambios)
                log.LogInformation("Eleventa ({Servidor}): {Resultado}.", servidor, resultado);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Si eleventa no contesta (la PC apagada, la red caída), se sigue vendiendo con lo último que se leyó.
            _estado = new EstadoEleventaDto(true, servidor.ToString(), HoraMexico.ALocal(inicio), false, _estado?.Resultado, LectorEleventa.Explicar(ex, servidor),
                HoraMexico.ALocal(inicio + espera), _lecturas, _estado?.Avisos ?? new Dictionary<string, int>());
            log.LogWarning(ex, "No se pudo leer eleventa ({Servidor}).", servidor);
        }
    }
}

public static class ApiEleventa
{
    public static void MapearApiEleventa(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/eleventa").ConErrores().SoloSuCaja();

        api.MapGet("", (LecturaEleventa lectura) => lectura.Estado);

        api.MapPost("/leer", (LecturaEleventa lectura) =>
        {
            lectura.LeerAhora();
            return Results.Accepted();
        });

        // La bandeja de salida: lo pendiente, lo que hay que revisar y el último error.
        api.MapGet("/envios", (EnvioVentasEleventa envios, PosDbContext db) => envios.EstadoAsync(db));

        api.MapPost("/enviar", (EnvioVentasEleventa envios) =>
        {
            envios.Despertar();
            return Results.Accepted();
        });

        // Lo que no entra y ya se arregló a mano en eleventa: se deja por revisar para que lo demás pase.
        api.MapPost("/envios/{id:guid}/revisar", async (Guid id, EnvioVentasEleventa envios, PosDbContext db) =>
            await envios.DejarPorRevisarAsync(db, id)
                ? Results.Ok(await envios.EstadoAsync(db))
                : Results.BadRequest(new ErrorDto("Solo se deja por revisar lo que está pendiente.")));
    }
}
