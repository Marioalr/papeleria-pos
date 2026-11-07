using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Auditoría del 2026-11-04: una sola devolución de eleventa que no se podía pasar a su venta tronaba la vuelta, y como la vuelta
/// va antes de aplicar el catálogo, la lectura dejaba de poner al día precios y existencias hasta 31 días. Lo que no cuadra queda
/// por revisar, como lo que ya no alcanza, y la lectura sigue.
/// </summary>
public sealed class VueltaQueNoSePuedePasarTests
{
    private static async Task<(ServicioDePrueba Servicio, TicketDto Ticket)> VentaEnEleventaAsync(long? costoDeLaLinea = null)
    {
        var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await CobrarAsync(c, plumas: 3);
        await using var db = servicio.Base();
        // Como la deja la bandeja: en eleventa, el ticket 500.
        var envio = await db.EnviosAEleventa.SingleAsync(e => e.VentaId == ticket.VentaId);
        (envio.Estado, envio.FolioEleventa, envio.TicketEleventa, envio.EnviadaUtc) = (EstadoEnvioEleventa.Enviada, 42, 500, DateTime.UtcNow);
        if (costoDeLaLinea is { } costo)
            (await db.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId)).Lineas.Single().CostoCentavos = costo;
        await db.SaveChangesAsync();
        return (servicio, ticket);
    }

    [Fact]
    public async Task Una_fraccion_de_algo_que_aqui_es_por_pieza_queda_por_revisar_y_no_truena_la_lectura()
    {
        var (s, ticket) = await VentaEnEleventaAsync();
        await using var servicio = s;
        DevolucionLeida[] leidas = [new(7, 500, false, HoraLocal(0), 4.5m, "cajera2", "Caja 2", 32, [new ArticuloDevueltoLeido("PLU-FINA", "Pluma punto fino", 0.5m, 4.5m, 10)])];

        await using (var db = servicio.Base())
        {
            var vuelta = await DevolucionesConEleventa.PasarVueltaAsync(db, leidas, DateTime.UtcNow);
            Assert.Equal((0, 1), (vuelta.Pasadas, vuelta.PorRevisar));
        }

        await using var despues = servicio.Base();
        var registro = await despues.Set<DevolucionDesdeEleventa>().AsNoTracking().SingleAsync();
        Assert.Equal(DevolucionesConEleventa.FraccionPorPieza, registro.PorRevisar);
        Assert.Null(registro.DevolucionId);
        Assert.False(await despues.Devoluciones.AnyAsync(d => d.VentaId == ticket.VentaId));
        // La lectura siguiente ya la conoce: no la vuelve a intentar.
        var otra = await DevolucionesConEleventa.PasarVueltaAsync(despues, leidas, DateTime.UtcNow);
        Assert.Equal((0, 0), (otra.Pasadas, otra.PorRevisar));
    }

    [Fact]
    public async Task Una_linea_con_costo_negativo_se_pasa_con_entrada_a_costo_cero()
    {
        var (s, ticket) = await VentaEnEleventaAsync(costoDeLaLinea: -596);
        await using var servicio = s;

        await using (var db = servicio.Base())
        {
            var vuelta = await DevolucionesConEleventa.PasarVueltaAsync(db, [new DevolucionLeida(7, 500, true, HoraLocal(0), 27m, "cajera2", "Caja 2", 32, [])], DateTime.UtcNow);
            Assert.Equal((1, 0), (vuelta.Pasadas, vuelta.PorRevisar));
        }

        await using var despues = servicio.Base();
        var devolucion = await despues.Devoluciones.AsNoTracking().SingleAsync(d => d.VentaId == ticket.VentaId);
        var entrada = await despues.MovimientosInventario.AsNoTracking().SingleAsync(m => m.DocumentoId == devolucion.Id);
        Assert.Equal(0, entrada.CostoCentavos);
    }
}
