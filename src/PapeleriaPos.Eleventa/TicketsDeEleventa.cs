using System.Data.Common;
using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>Un ticket cobrado en eleventa, leído en vivo para devolverlo (o para saber qué se le devolvió allá).</summary>
/// <param name="CajaId"><c>CAJAS.ID</c> de la caja que lo cobró y su nombre.</param>
/// <param name="TurnoId">El turno de eleventa en que se cobró.</param>
/// <param name="VendidoEn">Hora de la tienda.</param>
/// <param name="Total">Lo que queda en el ticket: eleventa le baja lo devuelto (la cancelación no).</param>
/// <param name="FormaPago">'e' es efectivo (la tienda solo cobra en efectivo).</param>
/// <param name="CreadoEn">La marca de un ticket que mandó la caja 2 (cuándo se empezó allá).</param>
public sealed record TicketLeido(
    int Id, int Folio, int CajaId, string Caja, int? TurnoId, DateTime? VendidoEn, decimal Total, bool Cancelado, string FormaPago,
    IReadOnlyList<ArticuloLeidoDeEleventa> Articulos, IReadOnlyList<DevolucionLeida> Devoluciones, DateTime? CreadoEn = null)
{
    public bool EnEfectivo => FormaPago.Trim().Equals("e", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lo que pagó el cliente: lo que queda más lo que ya se devolvió en partes (la cancelación no le baja el total).</summary>
    public decimal Pagado => Total + Devoluciones.Where(d => !d.EsCancelacion).Sum(d => d.Total);
}

/// <summary>Una devolución (<c>P</c>) o cancelación (<c>C</c>) de eleventa (<c>DEVOLUCIONES</c>).</summary>
/// <param name="DevueltoEn">Hora de la tienda; con el ticket es la marca de la caja 2 (<c>TICKET_ID</c> + <c>DEVUELTO_EN</c>).</param>
/// <param name="Total"><c>TOTAL_DEVUELTO</c>: lo que salió de la caja.</param>
public sealed record DevolucionLeida(
    int Id, int TicketId, bool EsCancelacion, DateTime DevueltoEn, decimal Total, string Cajero, string Caja, int? TurnoId,
    IReadOnlyList<ArticuloDevueltoLeido> Articulos);

/// <summary>Un renglón de <c>DEVOLUCIONES_ARTICULOS</c> (solo las parciales los llevan).</summary>
/// <param name="ProductoId">El de su código en <c>PRODUCTOS</c>; vacío si fue un artículo común.</param>
public sealed record ArticuloDevueltoLeido(string Codigo, string Nombre, decimal Cantidad, decimal Dinero, int? ProductoId);

/// <summary>
/// Solo lee eleventa (docs/migracion-eleventa.md, «Devoluciones de ida y vuelta»): los tickets con un folio, un ticket con sus
/// artículos y sus devoluciones (la ida), y las devoluciones de los tickets que mandó la caja 2 (la vuelta), en grupos de
/// menos de 1500 para Firebird 2.0. Solo del piloto: se quita al dejar eleventa.
/// </summary>
public static class TicketsDeEleventa
{
    private static FbTransactionOptions SoloLeer => new()
    {
        TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
    };

    private const string Encabezado = """
        SELECT T.ID, T.FOLIO, T.CAJA_ID, C.NOMBRE, T.TURNO_ID, T.VENDIDO_EN, T.TOTAL, T.ESTA_CANCELADO, T.FORMA_PAGO, T.CREADO_EN
        FROM VENTATICKETS T LEFT JOIN CAJAS C ON C.ID = T.CAJA_ID
        """;

    /// <summary>Los tickets cobrados con ese folio (eleventa lo repite entre cajas), sin sus artículos.</summary>
    public static async Task<List<TicketLeido>> PorFolioAsync(FbConnection c, int folio, CancellationToken ct = default)
    {
        await using var tx = await c.BeginTransactionAsync(SoloLeer, ct);
        var tickets = await LeerAsync(c, tx, $"{Encabezado} WHERE T.FOLIO = {folio} AND T.ESTA_ABIERTO = 'f' ORDER BY T.ID", Ticket, ct);
        await tx.CommitAsync(ct);
        return tickets;
    }

    /// <summary>Un ticket con sus artículos y sus devoluciones, todo de la misma foto; vacío si no está.</summary>
    public static async Task<TicketLeido?> TicketAsync(FbConnection c, int ticketId, CancellationToken ct = default)
    {
        await using var tx = await c.BeginTransactionAsync(SoloLeer, ct);
        var ticket = (await LeerAsync(c, tx, $"{Encabezado} WHERE T.ID = {ticketId} AND T.ESTA_ABIERTO = 'f'", Ticket, ct)).SingleOrDefault();
        if (ticket is not null)
        {
            // En las líneas viejas no hay PRECIO_FINAL ni TOTAL_ARTICULO: el precio está en PRECIO_USADO (como al importar).
            var articulos = await LeerAsync(c, tx, $"""
                SELECT A.ID, A.PRODUCTO_CODIGO, A.PRODUCTO_NOMBRE, A.CANTIDAD, COALESCE(A.PRECIO_FINAL, A.PRECIO_USADO), A.TOTAL_ARTICULO, A.GANANCIA,
                    (SELECT MAX(P.TVENTA) FROM PRODUCTOS P WHERE P.CODIGO = A.PRODUCTO_CODIGO),
                    (SELECT MAX(P.ID) FROM PRODUCTOS P WHERE P.CODIGO = A.PRODUCTO_CODIGO)
                FROM VENTATICKETS_ARTICULOS A WHERE A.TICKET_ID = {ticketId} ORDER BY A.ID
                """, r =>
                {
                    var cantidad = Decimal(r, 3);
                    var precio = Decimal(r, 4);
                    // En las líneas viejas el total va vacío: es precio por cantidad.
                    var total = r.IsDBNull(5) ? precio * cantidad : Decimal(r, 5);
                    var codigo = Texto(r, 1);
                    var esComun = codigo.StartsWith("COMUN-", StringComparison.OrdinalIgnoreCase);
                    return new ArticuloLeidoDeEleventa(Entero(r, 0), codigo, Texto(r, 2), cantidad, precio, total, Decimal(r, 6),
                        Texto(r, 7) == "D", esComun || r.IsDBNull(8) ? null : Entero(r, 8));
                }, ct);
            ticket = ticket with { Articulos = articulos, Devoluciones = await DevolucionesAsync(c, tx, [ticketId], ct) };
        }
        await tx.CommitAsync(ct);
        return ticket;
    }

    /// <summary>
    /// El turno que eleventa tiene abierto ahora en esa caja: el único en que el puente sabe escribir una devolución. Vacío si
    /// no hay (después del corte, antes de volver a abrir la caja allá) o si hubiera dos (la base no lo deja).
    /// </summary>
    public static async Task<int?> TurnoAbiertoAsync(FbConnection c, int cajaId, CancellationToken ct = default)
    {
        await using var tx = await c.BeginTransactionAsync(SoloLeer, ct);
        var abiertos = await LeerAsync(c, tx, $"SELECT ID FROM TURNOS WHERE ID_CAJA = {cajaId} AND TERMINO_EN IS NULL", r => Entero(r, 0), ct);
        await tx.CommitAsync(ct);
        return abiertos is [var turno] ? turno : null;
    }

    /// <summary>El turno de eleventa en que se cobró un ticket; vacío si ya no está o no tiene.</summary>
    public static async Task<int?> TurnoDelTicketAsync(FbConnection c, FbTransaction tx, int ticket, CancellationToken ct = default) =>
        (await LeerAsync(c, tx, $"SELECT TURNO_ID FROM VENTATICKETS WHERE ID = {ticket}", r => r.IsDBNull(0) ? (int?)null : Entero(r, 0), ct))
        .FirstOrDefault();

    /// <summary>Las devoluciones y cancelaciones de esos tickets, con lo que se devolvió de cada artículo.</summary>
    public static async Task<List<DevolucionLeida>> DevolucionesAsync(FbConnection c, FbTransaction tx, IReadOnlyCollection<int> tickets, CancellationToken ct)
    {
        var devoluciones = new List<DevolucionLeida>();
        var articulos = new List<(int Devolucion, ArticuloDevueltoLeido Articulo)>();
        foreach (var grupo in ListasIn.Grupos(tickets))
        {
            devoluciones.AddRange(await LeerAsync(c, tx, $"""
                SELECT ID, TICKET_ID, TIPO_DEVOLUCION, DEVUELTO_EN, TOTAL_DEVUELTO, CAJERO, CAJA, TURNO_ID FROM DEVOLUCIONES
                WHERE TICKET_ID IN ({grupo})
                """,
                r => new DevolucionLeida(Entero(r, 0), Entero(r, 1), Texto(r, 2) is "C" or "c", r.GetDateTime(3), Decimal(r, 4), Texto(r, 5), Texto(r, 6),
                    r.IsDBNull(7) ? null : Entero(r, 7), []), ct));
            articulos.AddRange(await LeerAsync(c, tx, $"""
                SELECT D.DEVOLUCION_ID, D.CODIGO_PRODUCTO, D.DESCRIPCION_PRODUCTO, D.CANTIDAD_DEVUELTA, D.DINERO_DEVUELTO,
                    (SELECT MAX(P.ID) FROM PRODUCTOS P WHERE P.CODIGO = D.CODIGO_PRODUCTO)
                FROM DEVOLUCIONES_ARTICULOS D WHERE D.TICKET_ID IN ({grupo})
                """,
                r => (Entero(r, 0), new ArticuloDevueltoLeido(Texto(r, 1), Texto(r, 2), Decimal(r, 3), Decimal(r, 4),
                    Texto(r, 1).StartsWith("COMUN-", StringComparison.OrdinalIgnoreCase) || r.IsDBNull(5) ? null : Entero(r, 5))), ct));
        }
        return devoluciones.OrderBy(d => d.Id)
            .Select(d => d with { Articulos = articulos.Where(a => a.Devolucion == d.Id).Select(a => a.Articulo).ToList() })
            .ToList();
    }

    private static TicketLeido Ticket(DbDataReader r) =>
        new(Entero(r, 0), r.IsDBNull(1) ? 0 : Entero(r, 1), Entero(r, 2), r.IsDBNull(3) ? "Otra caja" : Texto(r, 3), r.IsDBNull(4) ? null : Entero(r, 4),
            r.IsDBNull(5) ? null : r.GetDateTime(5), Decimal(r, 6), Texto(r, 7) is "t" or "T", Texto(r, 8), [], [], r.IsDBNull(9) ? null : r.GetDateTime(9));

    private static async Task<List<T>> LeerAsync<T>(FbConnection c, FbTransaction tx, string sql, Func<DbDataReader, T> fila, CancellationToken ct)
    {
        await using var comando = new FbCommand(sql, c, tx);
        await using var lector = await comando.ExecuteReaderAsync(ct);
        var filas = new List<T>();
        while (await lector.ReadAsync(ct))
            filas.Add(fila(lector));
        return filas;
    }

    private static string Texto(DbDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i).Trim();
    private static int Entero(DbDataReader r, int i) => Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);

    /// <summary>Los montos y las cantidades son FLOAT: leídos como tales, sin la cola de cifras del double.</summary>
    private static decimal Decimal(DbDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetValue(i) switch
    {
        float f => (decimal)f,
        var v => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
    };
}
