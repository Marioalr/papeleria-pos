using FirebirdSql.Data.FirebirdClient;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>Un ticket de otra caja de eleventa con el folio de una venta de la caja 2.</summary>
/// <param name="VendidoEn">Hora de la tienda.</param>
public sealed record OtroTicketConElFolio(int TicketId, int Folio, string Caja, DateTime? VendidoEn, decimal Total);

/// <summary>
/// Los folios repetidos (docs/migracion-eleventa.md, «Folios repetidos»): eleventa toma el folio como el último más uno,
/// sin candado, y dos cajas que cobran casi al mismo tiempo lo repiten. Justo después de guardar, el puente revisa el suyo
/// y, si choca, toma el siguiente (<see cref="EnvioEleventa.RevisarFolioAsync"/>). Pero la otra caja pudo guardar el suyo
/// un instante después de esa revisión: la revisión tardía vuelve a ver las ventas de la última hora, en su propio paso
/// de la bandeja (aunque la bandeja esté atorada en otra cosa). Para entonces el ticket ya pudo salir impreso con ese
/// folio: no se cambia. Queda anotado (<see cref="EnvioAEleventa.FolioRepetido"/> y el otro ticket, con su hora y su
/// total): sus reimpresiones salen con el folio de la caja, y al escribir el folio para devolver la caja enseña los dos
/// para elegir por la hora y el total. Solo del piloto: se quita al dejar eleventa.
/// </summary>
public static class FoliosRepetidos
{
    /// <summary>Hasta cuándo se vuelve a revisar el folio de una venta: otra caja no tarda más en guardar el suyo.</summary>
    public static readonly TimeSpan Ventana = TimeSpan.FromHours(1);

    /// <summary>Los tickets (que no son estos) con alguno de esos folios. Solo lee.</summary>
    public static async Task<List<OtroTicketConElFolio>> OtrosConEsosFoliosAsync(
        FbConnection c, IReadOnlyCollection<(int Folio, int Ticket)> nuestros, CancellationToken ct = default)
    {
        var otros = new List<OtroTicketConElFolio>();
        if (nuestros.Count == 0)
            return otros;
        var propios = nuestros.Select(n => n.Ticket).ToHashSet();
        await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.ReadCommitted | FbTransactionBehavior.RecVersion | FbTransactionBehavior.NoWait,
        }, ct);
        foreach (var grupo in ListasIn.Grupos(nuestros.Select(n => n.Folio)))
        {
            await using var comando = new FbCommand($"""
                SELECT T.ID, T.FOLIO, C.NOMBRE, T.VENDIDO_EN, T.TOTAL FROM VENTATICKETS T LEFT JOIN CAJAS C ON C.ID = T.CAJA_ID
                WHERE T.FOLIO IN ({grupo})
                """, c, tx);
            await using var lector = await comando.ExecuteReaderAsync(ct);
            while (await lector.ReadAsync(ct))
            {
                var id = Convert.ToInt32(lector.GetValue(0));
                if (!propios.Contains(id))
                    otros.Add(new OtroTicketConElFolio(id, Convert.ToInt32(lector.GetValue(1)), lector.IsDBNull(2) ? "Otra caja" : lector.GetString(2).Trim(),
                        lector.IsDBNull(3) ? null : lector.GetDateTime(3), lector.IsDBNull(4) ? 0 : Convert.ToDecimal(lector.GetValue(4))));
            }
        }
        await tx.CommitAsync(ct);
        return otros;
    }

    /// <summary>
    /// Lo que la revisión tardía vuelve a ver: las ventas mandadas en la última hora y las que ya se sabe que se repitieron
    /// sin que se haya anotado el otro ticket. Si no hay ninguna, no hace falta ir a eleventa.
    /// </summary>
    public static async Task<List<EnvioAEleventa>> PorRevisarAsync(PosDbContext db, DateTime ahoraUtc, CancellationToken ct = default)
    {
        var desde = ahoraUtc - Ventana;
        return await db.EnviosAEleventa
            .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.Estado == EstadoEnvioEleventa.Enviada && e.FolioEleventa != null && e.TicketEleventa != null
                && (e.EnviadaUtc >= desde || e.FolioRepetido && !db.Set<FolioRepetidoEnEleventa>().Any(f => f.EnvioId == e.Id)))
            .ToListAsync(ct);
    }

    /// <summary>
    /// La revisión tardía de <paramref name="enviadas"/> (<see cref="PorRevisarAsync"/>) contra eleventa. Lo que se repitió
    /// queda anotado; en eleventa no se cambia nada. Devuelve cuántas ventas se repitieron que no se sabían. Quien llama guarda.
    /// </summary>
    public static async Task<int> RevisarAsync(PosDbContext db, FbConnection c, IReadOnlyList<EnvioAEleventa> enviadas, DateTime ahoraUtc,
        CancellationToken ct = default)
    {
        if (enviadas.Count == 0)
            return 0;
        var otros = await OtrosConEsosFoliosAsync(c, enviadas.Select(e => (e.FolioEleventa!.Value, e.TicketEleventa!.Value)).ToList(), ct);
        var ids = enviadas.Select(e => e.Id).ToList();
        var anotados = await db.Set<FolioRepetidoEnEleventa>().Where(f => ids.Contains(f.EnvioId)).Select(f => new { f.EnvioId, f.TicketEleventa }).ToListAsync(ct);
        var nuevas = 0;
        foreach (var envio in enviadas)
        {
            var conElFolio = otros.Where(o => o.Folio == envio.FolioEleventa).ToList();
            if (conElFolio.Count == 0)
                continue;
            if (!envio.FolioRepetido)
            {
                envio.FolioRepetido = true;
                nuevas++;
            }
            foreach (var otro in conElFolio.Where(o => !anotados.Any(a => a.EnvioId == envio.Id && a.TicketEleventa == o.TicketId)))
                db.Set<FolioRepetidoEnEleventa>().Add(new FolioRepetidoEnEleventa
                {
                    EnvioId = envio.Id, Folio = otro.Folio, TicketEleventa = otro.TicketId, Caja = Recortar(otro.Caja, 50),
                    VendidoUtc = otro.VendidoEn is { } vendido ? HoraMexico.AUtc(vendido) : null,
                    TotalCentavos = ReglasEleventa.Centavos((double)otro.Total), VistoUtc = ahoraUtc,
                });
        }
        return nuevas;
    }

    /// <summary>Por qué un ticket de otra caja de eleventa no se devuelve aquí: eleventa no contestó para verlo.</summary>
    public const string SeDevuelveEnEleventa = "Es de otra caja de eleventa y eleventa no contesta: espera a la red, o devuélvelo en eleventa.";

    /// <summary>
    /// Para devolver con el folio de eleventa escrito (el ticket ya no lleva QR): las ventas de esta caja con ese folio y los
    /// tickets de eleventa con el mismo (en vivo, <paramref name="enEleventa"/>; o, si eleventa no contestó, los que la
    /// revisión tardía anotó). Si hay más de uno, la caja los enseña y se elige por la hora y el total; el de otra caja se
    /// devuelve aquí si se puede (la ida, <see cref="DevolucionesConEleventa"/>), o dice por qué no.
    /// </summary>
    /// <param name="enEleventa">
    /// Los tickets de eleventa con ese folio, leídos en vivo, con el turno que eleventa tiene abierto en la caja 2 (solo los de
    /// ese turno se devuelven aquí); vacío si eleventa no contestó.
    /// </param>
    /// <param name="conEleventa">Si esta caja manda a eleventa (si no, no se le preguntó).</param>
    public static async Task<FolioEleventaDto> BuscarAsync(PosDbContext db, int folio, (IReadOnlyList<TicketLeido> Tickets, TurnoDelPuente Puente)? enEleventa,
        bool conEleventa, DateTime ahoraUtc, CancellationToken ct = default)
    {
        var propios = await (from e in db.EnviosAEleventa.AsNoTracking()
                             join v in db.Ventas.AsNoTracking() on e.VentaId equals v.Id
                             join c in db.Cajas.AsNoTracking() on v.CajaId equals c.Id
                             where e.Tipo == TipoEnvioEleventa.Venta && e.FolioEleventa == folio
                             select new { Caja = c.Nombre, v.PagadaUtc, Total = v.TotalCentavos + v.RedondeoCentavos, v.Serie, v.Folio, e.TicketEleventa })
            .ToListAsync(ct);
        var tickets = propios
            .Select(p => new TicketConFolioDto(p.Caja, p.PagadaUtc is { } pagada ? HoraMexico.ALocal(pagada) : null, p.Total,
                p.Folio is { } f ? $"{p.Serie}-{f:000000}" : null, p.TicketEleventa))
            .ToList();
        var nuestros = propios.Where(p => p.TicketEleventa is not null).Select(p => p.TicketEleventa!.Value).ToHashSet();
        string? aviso = null;
        if (enEleventa is var (leidos, puente))
        {
            // Una venta de esta caja que llegó a eleventa sin que aquí se supiera (se cortó la red al confirmar) lleva su marca.
            var marcas = await MarcasSinFolioAsync(db, ct);
            foreach (var t in leidos.Where(t => !nuestros.Contains(t.Id) && !(t.CreadoEn is { } creado && marcas.Contains(creado))))
                tickets.Add(new TicketConFolioDto(t.Caja, t.VendidoEn, Centavos(t.Pagado), null, t.Id,
                    DevolucionesConEleventa.PorQueNoSeDevuelveAqui(t, puente, ahoraUtc)));
        }
        else
        {
            var otros = await db.Set<FolioRepetidoEnEleventa>().AsNoTracking().Where(f => f.Folio == folio).ToListAsync(ct);
            foreach (var otro in otros.Where(o => !nuestros.Contains(o.TicketEleventa)).DistinctBy(o => o.TicketEleventa))
                tickets.Add(new TicketConFolioDto(otro.Caja, otro.VendidoUtc is { } vendido ? HoraMexico.ALocal(vendido) : null, otro.TotalCentavos, null,
                    otro.TicketEleventa, SeDevuelveEnEleventa));
            if (conEleventa)
                aviso = DevolucionesConEleventa.NoContesta;
        }
        return new FolioEleventaDto(folio, tickets.OrderBy(t => t.FechaLocal).ToList(), aviso);
    }

    /// <summary>Las marcas (<c>CREADO_EN</c>) de las ventas de esta caja que pudieron llegar a eleventa sin que se supiera.</summary>
    private static async Task<HashSet<DateTime>> MarcasSinFolioAsync(PosDbContext db, CancellationToken ct)
    {
        var creadas = await (from e in db.EnviosAEleventa.AsNoTracking()
                             join v in db.Ventas.AsNoTracking() on e.VentaId equals v.Id
                             where e.Tipo == TipoEnvioEleventa.Venta && e.FolioEleventa == null && e.Intentos > 0
                             select v.CreadaUtc).ToListAsync(ct);
        return creadas.Select(c =>
        {
            var local = HoraMexico.ALocal(c);
            return new DateTime(local.Ticks - local.Ticks % 1000);
        }).ToHashSet();
    }

    private static long Centavos(decimal pesos) => (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero);

    private static string Recortar(string texto, int largo) => texto.Length <= largo ? texto : texto[..largo];
}
