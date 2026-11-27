using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El ensayo de la matriz (2026-11-27): las ventas del POS salieron con el folio de eleventa (42, 43…) y la caja les dio el suyo
/// (1, 2…), que empieza de cero en una base recién importada. Al devolver con el folio «2», la ventana enseñaba la venta del POS
/// (su folio de la caja) junto al ticket 2 de eleventa de 2022, y al abrirla decía «2» aunque el papel del cliente dice 43.
/// </summary>
public sealed class FolioDelTicketAlDevolverTests
{
    [FactConFirebird]
    public async Task El_folio_de_eleventa_del_ticket_la_encuentra_y_el_de_la_caja_no_la_cruza_con_un_ticket_viejo_de_eleventa()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        // Un ticket de los primeros días de eleventa en la principal, con el folio 2.
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN, TOTAL) VALUES (2, 1, 1, -1, 'f', @cuando, 20)",
            ("cuando", new DateTime(2022, 6, 11, 22, 5, 0)));
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        var primera = await CobrarAsync(c);
        var segunda = await CobrarAsync(c);

        // Los tickets salen con el folio de eleventa (el último era el 41); la caja lleva el suyo.
        Assert.Equal([("1", 42), ("2", 43)], new[] { primera, segunda }.Select(t => (t.Folio, t.FolioEleventa ?? 0)));
        await using (var db = servicio.Base())
            Assert.All(await db.EnviosAEleventa.Where(x => x.Tipo == TipoEnvioEleventa.Venta).ToListAsync(), x => Assert.True(x.FolioEnElTicket));

        // Con el folio del papel se encuentra, y al abrirla dice ese folio (con el de la caja al lado).
        var con43 = await Leer<FolioEleventaDto>(await c.Http.GetAsync("api/eleventa/folio/43"));
        var propio = Assert.Single(con43.Tickets);
        Assert.Equal(("2", true), (propio.FolioCaja, propio.TicketEleventa is not null));
        var abierta = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/-2?cajaId={c.Datos.Id}"));
        Assert.Equal((segunda.VentaId, "43 (en la caja 2)"), ((Guid?)abierta.Id, abierta.Folio));

        // Con el 2 solo sale el ticket de eleventa de 2022: la venta del POS no lleva ese folio en su papel.
        var con2 = await Leer<FolioEleventaDto>(await c.Http.GetAsync("api/eleventa/folio/2"));
        var viejo = Assert.Single(con2.Tickets);
        Assert.Equal(("Principal", (string?)null, new DateTime(2022, 6, 11, 22, 5, 0), 2000L), (viejo.Caja, viejo.FolioCaja, viejo.FechaLocal, viejo.TotalCentavos));

        // El comprobante de la devolución dice el folio del papel.
        var linea = abierta.Lineas.Single();
        var comprobante = await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{abierta.Id}/devoluciones",
            new SolicitudDevolucion(c.Sesion.UsuarioId, c.Datos.Id, "Ensayo", [new LineaDevolucionSolicitada(linea.LineaId, 1000)])));
        Assert.Equal("43 (en la caja 2)", comprobante.Folio);
    }

    [Fact]
    public async Task Si_eleventa_no_contesta_el_ticket_sale_con_el_folio_de_la_caja_y_con_el_se_encuentra()
    {
        // Nadie escucha en el puerto de eleventa: el ticket sale con el folio de la caja.
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        var ticket = await CobrarAsync(c);

        Assert.Equal(("1", (int?)null), (ticket.Folio, ticket.FolioEleventa));
        await using (var db = servicio.Base())
            Assert.False((await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta)).FolioEnElTicket);
        var con1 = await Leer<FolioEleventaDto>(await c.Http.GetAsync("api/eleventa/folio/1"));
        Assert.Equal("1", Assert.Single(con1.Tickets).FolioCaja);
        var abierta = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/1?cajaId={c.Datos.Id}"));
        Assert.Equal((ticket.VentaId, "1"), ((Guid?)abierta.Id, abierta.Folio));
    }
}
