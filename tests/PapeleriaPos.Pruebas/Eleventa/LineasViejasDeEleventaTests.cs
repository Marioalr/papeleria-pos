using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// Las líneas viejas de eleventa (en la tienda, todas las de antes del 2024-10-05) no traen <c>TOTAL_ARTICULO</c> ni
/// <c>PRECIO_FINAL</c>: su precio está en <c>PRECIO_USADO</c> (2026-10-24). Contra el Firebird de prueba, con una línea así
/// inventada: la importación, la búsqueda en vivo de un ticket y el puente que le devuelve algo la toman con su precio.
/// </summary>
public sealed class LineasViejasDeEleventaTests
{
    /// <summary>
    /// Un ticket de la caja 2, en su turno abierto, con una línea como las viejas: tres plumas a $10 en PRECIO_USADO, sin
    /// PRECIO_FINAL ni TOTAL_ARTICULO, que ganan $4 cada una; el ticket, $30. Regresa su <c>VENTATICKETS.ID</c>.
    /// </summary>
    private static async Task<int> TicketViejoAsync(EleventaDePrueba e, int turno)
    {
        var vendido = HoraLocal(30);
        await e.EjecutarAsync("""
            INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, NOMBRE, CREADO_EN, SUBTOTAL, TOTAL, GANANCIA, ESTA_ABIERTO, VENDIDO_EN, PAGO_CON,
                NUMERO_ARTICULOS, PAGADO_EN, OPERACION_ID, FORMA_PAGO, TURNO_ID, TOTAL_FACTURABLE)
            VALUES (77, @caja, 7, 'Ticket 1', @vendido, 30, 30, 12, 'f', @vendido, 30, 3, @vendido, -1, 'e', @turno, 30)
            """, ("caja", CajaEleventa), ("vendido", vendido), ("turno", turno));
        var ticket = Convert.ToInt32(await e.ValorAsync("SELECT ID FROM VENTATICKETS WHERE FOLIO = 77"));
        await e.EjecutarAsync("""
            INSERT INTO VENTATICKETS_ARTICULOS (TICKET_ID, PRODUCTO_CODIGO, PRODUCTO_NOMBRE, CANTIDAD, GANANCIA, DEPARTAMENTO_ID, PAGADO_EN, PRECIO_USADO,
                PRECIO_FINAL, TOTAL_ARTICULO)
            VALUES (@ticket, 'PLU-FINA', 'Pluma punto fino', 3, 4, 5, @vendido, 10, NULL, NULL)
            """, ("ticket", ticket), ("vendido", vendido));
        return ticket;
    }

    [FactConFirebird]
    public async Task La_importacion_y_la_busqueda_en_vivo_toman_el_precio_usado_de_una_linea_vieja()
    {
        var (e, turno) = await EleventaAsync();
        await using var eleventa = e;
        var ticket = await TicketViejoAsync(e, turno!.Value);

        var r = ImportadorEleventa.Importar(await LectorEleventa.LeerAsync(e.Conexion));
        var leido = await TicketsDeEleventa.TicketAsync(e.Conexion, ticket);

        // $30, con el costo de la ganancia por pieza ($6 cada una) y sin «sin desglose»: antes, $0 y el costo en −$12.
        var venta = r.Ventas.Single(v => v.FolioOrigen == "77");
        var linea = Assert.Single(venta.Lineas);
        Assert.Equal((3000L, 3000L, 1000L, 1800L), (venta.TotalCentavos, linea.ImporteCentavos, linea.PrecioCentavos, linea.CostoCentavos));
        Assert.DoesNotContain(ImportadorEleventa.AvisoTotalSinDesglose, r.Avisos.Keys);
        // Buscado en vivo (la ventana de devoluciones), con su precio: antes salía en $0.
        var articulo = Assert.Single(leido!.Articulos);
        Assert.Equal((10m, 30m), (articulo.Precio, articulo.Total));
    }

    /// <summary>El puente le devuelve una pluma a esa línea: no truena por el precio vacío, y allá queda con dos.</summary>
    [FactConFirebird]
    public async Task El_puente_devuelve_de_una_linea_vieja_sin_tronar()
    {
        var (e, turno) = await EleventaAsync();
        await using var eleventa = e;
        var ticket = await TicketViejoAsync(e, turno!.Value);

        await EnvioEleventa.DevolverAsync(e.Conexion, new CajaEnEleventa(CajaEleventa, 7, AbreTurno: false),
            new DevolucionParaEleventa(ticket, false, HoraLocal(0).AddSeconds(-5), new TurnoParaEleventa(HoraLocal(120), 300), [new ArticuloDevuelto(10, "Pluma", 1, 10)]));

        Assert.Equal(2d, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t", ("t", ticket))));
        Assert.Equal(20d, Convert.ToDouble(await e.ValorAsync("SELECT TOTAL FROM VENTATICKETS WHERE ID = @t", ("t", ticket))));
        Assert.Equal(10d, Convert.ToDouble(await e.ValorAsync("SELECT TOTAL_DEVUELTO FROM DEVOLUCIONES WHERE TICKET_ID = @t", ("t", ticket))));
    }
}
