using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Impresiones.Servicio;

/// <summary>
/// La sección «ControlPrint» del servicio (servicio.json, en la caja 2): a qué Control Print conectarse para ver su cola. El
/// código de conexión va solo en la variable de entorno del servicio, <c>ControlPrint__Codigo</c> (como <c>Eleventa__Clave</c>; lo
/// pone <c>instalacion\poner-codigo-controlprint.ps1</c>). Sin la sección no hay tarjetas de Control Print (como antes).
/// </summary>
public sealed class OpcionesControlPrint
{
    /// <summary>La IP o el nombre de la PC de Control Print (la caja principal, «192.168.1.106»).</summary>
    public string? Servidor { get; set; }

    public int Puerto { get; set; } = ProtocoloControlPrint.PuertoPorOmision;

    /// <summary>La huella SHA-256 de su certificado; sin ella, se fija la del primer saludo y se guarda junto a la base.</summary>
    public string? Huella { get; set; }

    /// <summary>De la variable de entorno ControlPrint__Codigo; nunca en un archivo.</summary>
    public string? Codigo { get; set; }

    /// <summary>De cuántos días se ven sus «por cobrar» (2: hoy y ayer).</summary>
    public int Dias { get; set; } = 2;
}

/// <summary>
/// Un trabajo de Control Print del ticket ya no se puede cobrar así (ya no está por cobrar allá, se cobró aquí, cambió su precio o
/// Control Print no contestó): el cobro no se hace y la caja recibe <see cref="ControlPrintAlCobrarDto"/> con un 409.
/// </summary>
public sealed class ControlPrintCambio(ControlPrintAlCobrarDto cambio) : InvalidOperationException(cambio.Mensaje)
{
    public ControlPrintAlCobrarDto Cambio { get; } = cambio;
}

/// <summary>
/// La cola de Control Print en el servicio de la caja 2 (2026-10-25): la mantiene al día (<see cref="ColaControlPrint"/>, solo
/// leyendo), guarda sus costos como los de la sucursal cuando cambian (con ellos se cobra lo que imprime la caja 2) y, al cobrar,
/// revisa otra vez los trabajos del ticket. Sin la sección «ControlPrint», no hace nada.
/// </summary>
public sealed class ControlPrintEnElServicio : BackgroundService
{
    private readonly IServiceScopeFactory _alcances;
    private readonly ILogger<ControlPrintEnElServicio> _log;

    public ControlPrintEnElServicio(OpcionesControlPrint? opciones, string? archivoHuella, IServiceScopeFactory alcances, TimeProvider reloj,
        ILogger<ControlPrintEnElServicio> log)
    {
        (_alcances, _log) = (alcances, log);
        Cola = new ColaControlPrint(opciones?.Servidor is { Length: > 0 } servidor
            ? new OpcionesColaControlPrint(servidor)
            {
                Puerto = opciones.Puerto,
                Huella = string.IsNullOrWhiteSpace(opciones.Huella) ? null : opciones.Huella.Trim(),
                Codigo = string.IsNullOrWhiteSpace(opciones.Codigo) ? null : opciones.Codigo,
                Dias = Math.Clamp(opciones.Dias, 1, 31),
                ArchivoHuella = archivoHuella,
                Reloj = reloj,
            }
            : null)
        {
            AlCambiarCostos = GuardarCostosAsync,
        };
        Cola.CambioDeEstado += estado =>
        {
            if (estado.Contesta == true)
                _log.LogInformation("Control Print en {Servidor} ({Nombre}, {Version}) contesta: se lee su cola por cobrar.", estado.Servidor, estado.Nombre,
                    estado.VersionServidor);
            else if (estado.Problema is { } problema)
                _log.LogWarning("Control Print no contesta: {Problema}", problema);
        };
    }

    public ColaControlPrint Cola { get; }

    /// <summary>
    /// La cola corre mientras corra el servicio. Algo que no se esperaba no detiene el servicio (que es el de la venta): se anota y la
    /// cola vuelve a empezar en un minuto.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Cola.CorrerAsync(stoppingToken);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogError(ex, "La cola de Control Print se detuvo por algo que no se esperaba: vuelve a empezar en un minuto.");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Los costos de Control Print quedan como los de cada sucursal de este servicio (hoy, una): con ellos se le pone precio a lo que
    /// imprime la caja 2, también a las copias del panel y al cotizar, y si al arrancar Control Print no contesta, son los de antes.
    /// </summary>
    private async Task GuardarCostosAsync(CostSettings costos, CancellationToken ct)
    {
        try
        {
            using var alcance = _alcances.CreateScope();
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            var registro = ImpresionesDelPos.Registro(db);
            foreach (var sucursal in await db.Sucursales.AsNoTracking().Select(s => s.Id).ToListAsync(ct))
            {
                if (await registro.GuardarCostosSiCambiaronAsync(sucursal, costos, ct))
                    _log.LogInformation("Los costos de impresión de la sucursal ahora son los de Control Print.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "No se pudieron guardar los costos de Control Print: se siguen usando los de memoria.");
        }
    }

    /// <summary>
    /// Los trabajos de Control Print que van en un ticket, revisados justo antes de cobrar (solo leer): siguen por cobrar allá, no
    /// se han cobrado aquí y tienen el precio que vio el cajero. Si no, <see cref="ControlPrintCambio"/>: no se cobra nada.
    /// </summary>
    public async Task<List<TrabajoControlPrintParaCobrar>> ParaCobrarAsync(PosDbContext db, Guid sucursalId,
        IReadOnlyList<ImpresionDeControlPrintSolicitada> pedidas, CancellationToken ct = default)
    {
        if (pedidas.Count == 0)
            return [];
        var ids = pedidas.Select(p => p.Id).Distinct().ToList();
        if (!Cola.Configurado)
            throw new ControlPrintCambio(new ControlPrintAlCobrarDto(
                "Este servicio no tiene configurado Control Print: se quitaron sus impresiones del ticket.", ids, []));
        var revision = await Cola.RevisarAsync(pedidas.Select(p => (p.Id, p.PrecioCentavos)).ToList(), ct);
        if (revision.NoContesta is { } noContesta)
            throw new ControlPrintCambio(new ControlPrintAlCobrarDto(
                $"No se cobró: Control Print no contesta y no se puede revisar que {Numeros(ids)} {(ids.Count == 1 ? "siga" : "sigan")} por cobrar "
                + $"(así nunca se cobra dos veces). Se {(ids.Count == 1 ? "quitó" : "quitaron")} del ticket: si el cliente no ha pagado, cóbralo a mano con IMP. "
                + noContesta, ids, []));

        var porque = revision.YaNo.Select(y => y.Porque).ToList();
        var quitar = revision.YaNo.Select(y => y.Id).ToList();
        var cobradosAqui = await ImpresionesDelPos.EnLaVenta(db).CobradosAsync(sucursalId, ids, ct);
        var siguen = new List<TrabajoControlPrint>();
        foreach (var t in revision.Siguen)
        {
            if (cobradosAqui.FirstOrDefault(c => EsElMismo(c, t)) is { } cobrado)
            {
                quitar.Add(t.Id);
                porque.Add($"#{t.Id} ya se cobró en esta caja ({HoraMexico.ALocal(cobrado.CobradoUtc):dd/MM HH:mm})");
            }
            else
                siguen.Add(t);
        }
        var precios = revision.OtroPrecio.Select(p => new ImpresionDeControlPrintSolicitada(p.Id, p.PrecioCentavos)).ToList();
        foreach (var p in precios)
            porque.Add($"a #{p.Id} le cambiaron el precio en Control Print: ahora es {AlertasTelegram.Dinero(p.PrecioCentavos)}");
        if (quitar.Count > 0 || precios.Count > 0)
            throw new ControlPrintCambio(new ControlPrintAlCobrarDto(
                $"No se cobró: {string.Join("; ", porque)}. {(quitar.Count > 0 ? "Se quitó del ticket lo que ya no se cobra; " : "")}"
                + "revisa el total y cobra otra vez.", quitar, precios));
        return siguen.Select(t => new TrabajoControlPrintParaCobrar(t.Id, TarjetaControlPrint.Utc(t.DetectadoUtc), TarjetaControlPrint.Titulo(t), t.PcOrigen.Trim(),
            TarjetaControlPrint.Centavos(TarjetaControlPrint.PrecioACobrar(t)), TarjetaControlPrint.Centavos(t.CostoTotal))).ToList();
    }

    /// <summary>El mismo trabajo de Control Print: el mismo número y la misma hora (un Control Print reinstalado repite números).</summary>
    public static bool EsElMismo(TrabajoControlPrintCobrado cobrado, TrabajoControlPrint trabajo) =>
        cobrado.TrabajoId == trabajo.Id && Math.Abs((cobrado.DetectadoUtc - TarjetaControlPrint.Utc(trabajo.DetectadoUtc)).TotalSeconds) < 1;

    /// <summary>
    /// La cola como la ve la caja de esa sucursal: sin los que ya se cobraron aquí y, si Control Print no contesta, sin tarjetas (no se
    /// pueden revisar al cobrar).
    /// </summary>
    public async Task<ColaControlPrintDto> ParaLaCajaAsync(PosDbContext db, Guid sucursalId, bool conCosto, CancellationToken ct = default)
    {
        var estado = Cola.Estado;
        if (!estado.Configurado)
            return new ColaControlPrintDto(false, null, null, null, null, 0, []);
        var visibles = new List<TrabajoControlPrint>();
        if (estado.Contesta == true)
        {
            var cobrados = await ImpresionesDelPos.EnLaVenta(db).CobradosAsync(sucursalId, estado.PorCobrar.Select(t => t.Id).ToList(), ct);
            visibles.AddRange(estado.PorCobrar.Where(t => !cobrados.Any(c => EsElMismo(c, t))));
        }
        var tarjetas = visibles.Select(t => TarjetaControlPrint.De(t, conCosto)).ToList();
        var mas = estado.Contesta == true ? Math.Max(0, estado.Total - estado.PorCobrar.Count) : 0;
        var servidor = string.IsNullOrEmpty(estado.Nombre) ? estado.Servidor : estado.Nombre;
        var aviso = estado.Contesta == false ? estado.Problema ?? "Control Print no contesta." : null;
        var version = TarjetaControlPrint.Version(tarjetas, estado.Contesta, aviso, estado.SoloInvitado, mas, servidor);
        return new ColaControlPrintDto(true, estado.Contesta, aviso, servidor, estado.LeidaUtc is { } leida ? HoraMexico.ALocal(leida.UtcDateTime) : null,
            version, tarjetas, estado.SoloInvitado, mas);
    }

    /// <summary>Cómo ve este servicio a Control Print (sin el código).</summary>
    public EstadoControlPrintDto EstadoParaVer()
    {
        var e = Cola.Estado;
        return new EstadoControlPrintDto(e.Configurado, e.Servidor, e.Puerto, e.TieneCodigo, e.Contesta, e.Nombre, e.VersionServidor,
            string.IsNullOrEmpty(e.Huella) ? null : ProtocoloControlPrint.HuellaLegible(e.Huella), e.HuellaConfigurada,
            e.LeidaUtc is { } leida ? HoraMexico.ALocal(leida.UtcDateTime) : null, e.Total, e.Lecturas, e.Problema, e.SoloInvitado);
    }

    private static string Numeros(IReadOnlyList<long> ids) => string.Join(", ", ids.Select(id => $"#{id}"));

    public override void Dispose()
    {
        Cola.Dispose();
        base.Dispose();
    }
}
