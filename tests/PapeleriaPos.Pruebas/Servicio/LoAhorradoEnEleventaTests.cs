using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que se ahorró en una venta de la caja 2 llega a eleventa como lo deja eleventa (revisión del PR #139): en la base de la tienda,
/// los 59 tickets con un renglón con descuento tienen <c>TOTAL_AHORRADO</c>, lo ahorrado contra el precio de lista, y el renglón su
/// <c>PORCENTAJE_DESCUENTO</c>. No es dinero: es lo que dicen el ticket de eleventa y sus reportes.
/// </summary>
public sealed class LoAhorradoEnEleventaTests
{
    /// <summary>
    /// Tres plumas de $9 con 1.05 % de descuento (Ctrl+D): $26.72, se ahorraron $0.28. Allá el ticket lleva lo ahorrado y el renglón
    /// el porcentaje que de verdad bajó ($0.28 de $27, como lo calcula eleventa con el precio). El efectivo se redondeó al peso ($27):
    /// ese redondeo va en el renglón, para que el total de eleventa sea lo que entró a la caja, y no cuenta como descuento.
    /// </summary>
    [FactConFirebird]
    public async Task R139_Lo_ahorrado_con_un_descuento_de_la_caja_2_llega_al_TOTAL_AHORRADO_como_en_eleventa()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Sesion.UsuarioId,
            [new LineaSolicitada(c.Variante, null, 3000, DescuentoCentesimas: 105, MotivoDescuento: "Cliente frecuente")],
            [new CobroSolicitado(FormaPago.Efectivo, 100000)])));
        await MandarAsync(c);
        int enEleventa;
        await using (var db = servicio.Base())
            enEleventa = (await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == ticket.VentaId)).TicketEleventa!.Value;

        var ahorrado = Convert.ToDouble(await e.ValorAsync("SELECT TOTAL_AHORRADO FROM VENTATICKETS WHERE ID = @t", ("t", enEleventa)));
        Assert.Equal(0.28, Math.Round(ahorrado, 2));
        var renglon = (await e.FilasAsync("SELECT PORCENTAJE_DESCUENTO, PRECIO_FINAL, TOTAL_ARTICULO, USA_MAYOREO FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t",
            ("t", enEleventa))).Single();
        Assert.Equal((1.037, 9d, 27d, "f"), (Math.Round(Convert.ToDouble(renglon["PORCENTAJE_DESCUENTO"]), 4),
            Math.Round(Convert.ToDouble(renglon["PRECIO_FINAL"]), 4), Math.Round(Convert.ToDouble(renglon["TOTAL_ARTICULO"]), 2), (string)renglon["USA_MAYOREO"]!));
        Assert.Equal((27d, 28L), (Math.Round(Convert.ToDouble(await e.ValorAsync("SELECT TOTAL FROM VENTATICKETS WHERE ID = @t", ("t", enEleventa))), 2),
            ticket.RedondeoCentavos));
    }
}
