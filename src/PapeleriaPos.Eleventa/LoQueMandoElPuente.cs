using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>Una venta de la caja nueva que entró a la bandeja hacia eleventa: lo que hace falta para encontrarla allá.</summary>
/// <param name="Folio">El de la caja (A-000123).</param>
/// <param name="CreadaUtc">Cuándo se empezó: es su marca en eleventa (<c>CREADO_EN</c>).</param>
/// <param name="PagadaUtc">Cuándo se cobró: en eleventa es <c>VENDIDO_EN</c>, al segundo.</param>
/// <param name="CobradoCentavos">Lo que pagó el cliente (con el redondeo del efectivo): el total que el puente escribe allá.</param>
/// <param name="TicketEleventa"><c>VENTATICKETS.ID</c> que le dio eleventa, si aquí se supo.</param>
/// <param name="Porque">Por qué quedó por revisar, si quedó: lo que «se anota a mano» pudo anotarse allá.</param>
public sealed record VentaMandada(Guid VentaId, string Folio, DateTime CreadaUtc, DateTime? PagadaUtc, long CobradoCentavos,
    EstadoEnvioEleventa Estado, int? TicketEleventa, int? FolioEleventa, MotivoPorRevisar? Porque = null);

/// <summary>Una devolución o cancelación hecha aquí de una de esas ventas: su hora es su marca en eleventa (<c>DEVUELTO_EN</c>).</summary>
/// <param name="EsperaAnotacion">
/// No pasó sola a eleventa y quedó para anotarse a mano allá: su anotación llega sin la marca, y la vuelta la retiene hasta que
/// alguien la elige («Ya lo anoté en eleventa»).
/// </param>
public sealed record DevolucionDeAqui(Guid VentaId, DateTime FechaUtc, bool EsperaAnotacion = false);

/// <summary>Una devolución de eleventa de un ticket de la caja 2 que ya leyó la vuelta (<see cref="DevolucionDesdeEleventa"/>).</summary>
/// <param name="Pasada">
/// Ya está en su venta de aquí: se pasó sin efectivo o, si es <paramref name="Propia"/>, se ligó con la de aquí que se anotó a
/// mano allá.
/// </param>
/// <param name="PorRevisar">Por qué no se pasó, si no se pasó.</param>
/// <param name="Revisada">Alguien ya la revisó en la pantalla de la caja 2 («Ya lo revisé»), sin pasarla.</param>
public sealed record VueltaLeida(int DevolucionEleventa, DateTime DevueltaUtc, bool Pasada, bool Propia, string? PorRevisar, bool Revisada);

/// <summary>La ida: lo que se devolvió en la caja nueva de un ticket cobrado en eleventa (<see cref="DevolucionDeEleventa"/>).</summary>
/// <param name="TotalCentavos">Lo devuelto, exacto: lo que eleventa le baja a su ticket.</param>
/// <param name="Estado">Cómo va su envío a eleventa.</param>
/// <param name="Error">Por qué no entró o quedó por revisar, dicho para la tienda.</param>
public sealed record IdaMandada(int TicketEleventa, string Caja, DateTime FechaUtc, long TotalCentavos, EstadoEnvioEleventa? Estado, string? Error);

/// <summary>Lo que la base de la caja nueva sabe de lo que el puente mandó a eleventa.</summary>
public sealed class PuenteEnLaBase
{
    public List<VentaMandada> Ventas { get; init; } = [];
    public List<DevolucionDeAqui> Devoluciones { get; init; } = [];
    public List<VueltaLeida> Vuelta { get; init; } = [];
    public List<IdaMandada> Idas { get; init; } = [];

    /// <summary>
    /// Lo que reconoció una importación final anterior (<c>VENTATICKETS.ID</c> → la venta de aquí, en los mapeos). Solo vale
    /// si ya no está la bandeja (se quitó el puente): con ella se vuelve a reconocer todo.
    /// </summary>
    public Dictionary<int, Guid> Reconocidos { get; init; } = [];
}

/// <summary>Lo que la importación final no trae de eleventa porque ya está en la base, y lo que no se puede decidir solo.</summary>
public sealed class LoDelPuenteEnEleventa
{
    /// <summary>Los tickets de eleventa que son ventas de aquí (<c>VENTATICKETS.ID</c> → la venta): no se importan.</summary>
    public Dictionary<int, Guid> Tickets { get; } = [];

    /// <summary>De esos, los que se reconocieron solo por su marca: aquí no se supo su número (se cortó la red al mandarla).</summary>
    public int PorSuMarca { get; internal set; }

    /// <summary>De esos, los que ya había reconocido otra importación final (la bandeja ya no está).</summary>
    public int YaReconocidos { get; internal set; }

    /// <summary>Lo que suman en eleventa, sin los cancelados y con lo devuelto ya bajado.</summary>
    public long TotalCentavos { get; internal set; }

    public int Cancelados { get; internal set; }

    /// <summary>Los que tienen devoluciones o una cancelación en eleventa.</summary>
    public int ConDevoluciones { get; internal set; }

    /// <summary>De sus devoluciones y cancelaciones, las que hizo la caja 2 (o se hicieron aquí y se anotaron a mano allá).</summary>
    public int DevolucionesDeAqui { get; internal set; }

    /// <summary>Las que se hicieron en eleventa y la vuelta ya pasó a su venta de aquí, sin efectivo.</summary>
    public int DevolucionesDeEleventa { get; internal set; }

    /// <summary>Las que se hicieron en eleventa y no están en su venta de aquí: van en <see cref="PorRevisar"/>.</summary>
    public int DevolucionesSinPasar { get; internal set; }

    /// <summary>
    /// Ventas de aquí que el puente no mandó (siguen pendientes o quedaron por revisar): allá no hay nada que quitar. Las que
    /// quedaron para anotarse a mano también van en <see cref="PorRevisar"/>: quizá se anotaron.
    /// </summary>
    public int NoLlegaron { get; internal set; }

    /// <summary>
    /// Lo devuelto aquí de tickets cobrados en eleventa (la ida) que ya está en eleventa, o se anotó a mano allá: esos tickets
    /// se importan con lo que eleventa ya les bajó.
    /// </summary>
    public int Idas { get; internal set; }

    /// <summary>
    /// Lo que no se puede decidir solo, una línea por cosa, para revisarlo a mano: primero si la copia de eleventa es de antes de
    /// lo último que mandó la caja 2, y lo demás de lo más viejo a lo más nuevo.
    /// </summary>
    public List<string> PorRevisar { get; } = [];
}

/// <summary>
/// La importación final (docs/migracion-eleventa.md, «La importación final»): al dejar eleventa, su historial de ventas se trae
/// a la base de la tienda, donde corrió el piloto. Las ventas de la caja nueva que escribió el puente ya están ahí como ventas,
/// con sus devoluciones y cancelaciones (las de aquí y las que se hicieron en eleventa, que la vuelta pasó sin efectivo): sus
/// tickets de eleventa no se traen, o contarían dos veces en los reportes. Se reconocen como el puente reconoce lo suyo, nunca
/// por el folio (eleventa lo repite entre cajas):
/// <list type="bullet">
/// <item>por el número que le dio eleventa (<c>VENTATICKETS.ID</c>) y su marca (<c>CREADO_EN</c>: cuándo se empezó la venta
/// aquí, en diezmilésimas de segundo);</item>
/// <item>o, si aquí no se supo su número (se cortó la red justo al guardar), por su marca y la hora del cobro
/// (<c>VENDIDO_EN</c>).</item>
/// </list>
/// Lo que eleventa les devolvió o canceló ya se lo bajó a esos tickets, así que tampoco entra. Lo que no se puede decidir solo
/// (una venta que se anotó a mano allá, una devolución que no llegó a uno de los dos lados) queda en una lista para revisarlo a
/// mano. Solo lee. Del piloto: se quita con el puente, después de la importación final.
/// </summary>
public static class LoQueMandoElPuente
{
    /// <summary>La entidad de los mapeos con que la importación final recuerda qué ticket de eleventa es cada venta de aquí.</summary>
    public const string EntidadMapeo = nameof(Venta);

    /// <summary>Cuántos tickets de eleventa se sugieren, a lo más, para una venta de aquí que se anotó a mano allá.</summary>
    private const int Sugeridos = 3;

    /// <summary>Lo que se le puede ir al total de una venta anotada a mano en eleventa: el redondeo del efectivo, que allá no va.</summary>
    private const long HolguraDeTotal = 100;

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Lo que la base sabe de lo que el puente mandó a eleventa: la bandeja, la vuelta, la ida y lo ya reconocido.</summary>
    public static async Task<PuenteEnLaBase> LeerAsync(PosDbContext db, CancellationToken ct = default)
    {
        var ventas = await (from e in db.EnviosAEleventa.AsNoTracking()
                            join v in db.Ventas.AsNoTracking() on e.VentaId equals v.Id
                            where e.Tipo == TipoEnvioEleventa.Venta
                            select new
                            {
                                v.Id, v.Serie, v.Folio, v.CreadaUtc, v.PagadaUtc, v.TotalCentavos, v.RedondeoCentavos, e.Estado, e.TicketEleventa, e.FolioEleventa,
                                e.PorRevisarPorque,
                            })
            .ToListAsync(ct);
        var devoluciones = await (from d in db.Devoluciones.AsNoTracking()
                                  join e in db.EnviosAEleventa.AsNoTracking() on d.VentaId equals e.VentaId
                                  where e.Tipo == TipoEnvioEleventa.Venta
                                  select new
                                  {
                                      d.VentaId, d.FechaUtc,
                                      Espera = db.EnviosAEleventa.Any(x => x.DevolucionId == d.Id && x.Estado == EstadoEnvioEleventa.PorRevisar
                                          && x.PorRevisarPorque == MotivoPorRevisar.AnotarAMano),
                                  })
            .ToListAsync(ct);
        var vuelta = await db.Set<DevolucionDesdeEleventa>().AsNoTracking()
            .Select(v => new { v.DevolucionEleventa, v.DevueltaUtc, Pasada = v.DevolucionId != null, v.Propia, v.PorRevisar, Revisada = v.RevisadaUtc != null })
            .ToListAsync(ct);
        var idas = await (from d in db.Set<DevolucionDeEleventa>().AsNoTracking()
                          join e in db.EnviosAEleventa.AsNoTracking() on d.Id equals e.DevolucionDeEleventaId into envios
                          from e in envios.DefaultIfEmpty()
                          select new
                          {
                              d.TicketEleventa, d.CajaNombre, d.FechaUtc, d.TotalCentavos,
                              Estado = e == null ? (EstadoEnvioEleventa?)null : e.Estado, Error = e == null ? null : e.Error,
                          })
            .ToListAsync(ct);
        var reconocidos = await db.MapeosImportacion.AsNoTracking()
            .Where(m => m.Origen == SincronizacionEleventa.Origen && m.Entidad == EntidadMapeo)
            .Select(m => new { m.IdOrigen, m.IdNuevo })
            .ToListAsync(ct);

        return new PuenteEnLaBase
        {
            Ventas = ventas.Select(v => new VentaMandada(v.Id, v.Folio is { } f ? $"{v.Serie}-{f:000000}" : v.Serie, v.CreadaUtc, v.PagadaUtc,
                v.TotalCentavos + v.RedondeoCentavos, v.Estado, v.TicketEleventa, v.FolioEleventa, v.PorRevisarPorque)).ToList(),
            Devoluciones = devoluciones.Select(d => new DevolucionDeAqui(d.VentaId, d.FechaUtc, d.Espera)).ToList(),
            Vuelta = vuelta.Select(v => new VueltaLeida(v.DevolucionEleventa, v.DevueltaUtc, v.Pasada, v.Propia, v.PorRevisar, v.Revisada)).ToList(),
            Idas = idas.Select(d => new IdaMandada(d.TicketEleventa, d.CajaNombre, d.FechaUtc, d.TotalCentavos, d.Estado, d.Error)).ToList(),
            Reconocidos = reconocidos.Where(m => int.TryParse(m.IdOrigen, CultureInfo.InvariantCulture, out _))
                .GroupBy(m => int.Parse(m.IdOrigen, CultureInfo.InvariantCulture)).ToDictionary(g => g.Key, g => g.First().IdNuevo),
        };
    }

    /// <summary>
    /// Qué tickets de eleventa son ventas de aquí, cuánto suman y lo que no se puede decidir solo. Sin la bandeja (ya se quitó
    /// el puente), vale lo que reconoció la importación final anterior.
    /// </summary>
    public static LoDelPuenteEnEleventa Reconocer(PuenteEnLaBase puente, DatosEleventa e)
    {
        var r = new LoDelPuenteEnEleventa();
        var porRevisar = new List<(DateTime Cuando, string Texto)>();
        var cobrados = e.Tickets.Where(t => t.VendidoEn is not null).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());

        if (puente.Ventas.Count > 0)
            ReconocerVentas(puente, cobrados, r, porRevisar);
        else
        {
            foreach (var (ticket, venta) in puente.Reconocidos)
                if (cobrados.ContainsKey(ticket))
                    r.Tickets[ticket] = venta;
            r.YaReconocidos = r.Tickets.Count;
        }

        foreach (var t in r.Tickets.Keys.Select(id => cobrados[id]))
        {
            if (t.Cancelado)
                r.Cancelados++;
            else
                r.TotalCentavos += ReglasEleventa.Centavos(t.Total);
        }
        r.ConDevoluciones = e.Devoluciones.Where(d => r.Tickets.ContainsKey(d.TicketId)).Select(d => d.TicketId).Distinct().Count();
        if (puente.Ventas.Count > 0)
        {
            Devoluciones(puente, e, cobrados, r, porRevisar);
            Idas(puente, e, cobrados, r, porRevisar);
        }

        // Una copia de eleventa de antes de lo último que mandó la caja 2 no lo tiene: lo de después parecería perdido.
        if (CopiaVieja(puente, cobrados) is { } vieja)
            r.PorRevisar.Add(vieja);
        r.PorRevisar.AddRange(porRevisar.OrderBy(x => x.Cuando).Select(x => x.Texto));
        return r;
    }

    /// <summary>
    /// Si la copia de eleventa es de antes de la última venta que la caja 2 mandó: el puente escribe la hora del cobro de aquí,
    /// así que una copia que la tiene llega por lo menos hasta ella.
    /// </summary>
    private static string? CopiaVieja(PuenteEnLaBase puente, Dictionary<int, FilaTicket> cobrados)
    {
        var mandadas = puente.Ventas.Where(v => v.Estado == EstadoEnvioEleventa.Enviada && v.PagadaUtc is not null).Select(v => AlSegundo(v.PagadaUtc!.Value)).ToList();
        if (mandadas.Count == 0)
            return null;
        var ultimaMandada = mandadas.Max();
        var ultimoTicket = cobrados.Values.Select(t => t.VendidoEn).Max();
        return ultimoTicket is null || ultimoTicket < ultimaMandada
            ? $"La copia de eleventa es de antes de lo último que mandó la caja 2 (su último ticket es del "
              + $"{(ultimoTicket is { } u ? Fecha(u) : "?")} y la caja 2 mandó hasta el {Fecha(ultimaMandada)}): lo de después no está en ella "
              + "y abajo parece perdido. Conviene sacar una copia nueva de eleventa y repetir la importación."
            : null;
    }

    /// <summary>
    /// Cada ticket con la marca de una venta de aquí: por su número y su marca o, si aquí no se supo su número, por su marca y
    /// la hora del cobro. Después, lo de cada venta que no cuadra: está dos veces, eleventa la tiene aunque aquí no se mandó,
    /// o se mandó (o se anotó a mano) y allá no está con su marca.
    /// </summary>
    private static void ReconocerVentas(PuenteEnLaBase puente, Dictionary<int, FilaTicket> cobrados, LoDelPuenteEnEleventa r,
        List<(DateTime Cuando, string Texto)> porRevisar)
    {
        var porMarca = puente.Ventas.ToLookup(v => RestauracionDeEleventa.Marca(v.CreadaUtc));
        var deCadaVenta = new Dictionary<Guid, List<FilaTicket>>();
        foreach (var t in cobrados.Values.OrderBy(t => t.Id))
        {
            if (t.CreadoEn is not { } marca || porMarca[marca].ToList() is not { Count: > 0 } mismas)
                continue;
            var venta = mismas.FirstOrDefault(v => v.TicketEleventa == t.Id)
                ?? mismas.FirstOrDefault(v => v.PagadaUtc is { } pagada && AlSegundo(pagada) == t.VendidoEn);
            if (venta is null)
                continue;
            r.Tickets[t.Id] = venta.VentaId;
            if (venta.TicketEleventa != t.Id)
                r.PorSuMarca++;
            if (!deCadaVenta.TryGetValue(venta.VentaId, out var suyos))
                deCadaVenta[venta.VentaId] = suyos = [];
            suyos.Add(t);
        }

        // Los tickets de eleventa que se importan: entre ellos estaría una venta de aquí que se anotó a mano allá.
        var propios = cobrados.Values.Where(t => !r.Tickets.ContainsKey(t.Id) && !t.Cancelado).OrderBy(t => t.VendidoEn).ToList();
        foreach (var v in puente.Ventas)
        {
            var suyos = deCadaVenta.GetValueOrDefault(v.VentaId) ?? [];
            var cuando = HoraMexico.ALocal(v.PagadaUtc ?? v.CreadaUtc);
            var venta = $"La venta {v.Folio} de la caja 2 (cobrada el {Fecha(cuando)}, {Pesos(v.CobradoCentavos)})";
            if (suyos.Count > 1)
                porRevisar.Add((cuando, $"{venta} está {suyos.Count} veces en eleventa ({string.Join("; ", suyos.Select(Ticket))}): no se importó "
                    + "ninguna. En eleventa cuenta de más."));
            else if (suyos is [var t] && v.Estado == EstadoEnvioEleventa.Descartada)
                porRevisar.Add((cuando, $"{venta} se canceló aquí antes de llegar a eleventa, pero eleventa sí la tiene ({Ticket(t)}"
                    + $"{(t.Cancelado ? ", cancelado" : ", sin cancelar")}): no se importó."));
            else if (suyos is [var u] && v.Estado is EstadoEnvioEleventa.Pendiente or EstadoEnvioEleventa.PorRevisar)
                porRevisar.Add((cuando, $"{venta} quedó {(v.Estado == EstadoEnvioEleventa.Pendiente ? "pendiente" : "por revisar")} en la bandeja, "
                    + $"pero eleventa sí la tiene ({Ticket(u)}): no se importó."));
            else if (suyos.Count == 0)
            {
                switch (v.Estado)
                {
                    case EstadoEnvioEleventa.Enviada:
                        var otro = v.TicketEleventa is { } id && cobrados.TryGetValue(id, out var ocupado)
                            ? $" (su número ya es de otro ticket, el {Ticket(ocupado)}, que se importó como de eleventa)"
                            : "";
                        porRevisar.Add((cuando, $"{venta} se mandó a eleventa{(v.FolioEleventa is { } f ? $" con el folio {f}" : "")} y eleventa ya no "
                            + $"la tiene{otro}: ¿se restauró un respaldo? {SiSeAnoto(v, propios)}"));
                        break;
                    case EstadoEnvioEleventa.AMano:
                        porRevisar.Add((cuando, $"{venta} se anotó a mano en eleventa: allá es un ticket como cualquier otro, sin la marca de la "
                            + $"caja 2, y se importó. {SiSeAnoto(v, propios)}"));
                        break;
                    case EstadoEnvioEleventa.PorRevisar when v.Porque == MotivoPorRevisar.AnotarAMano:
                        // No llegó sola y había que anotarla a mano allá: si alguien la anotó (sin marcar «Ya lo anoté»), cuenta dos veces.
                        r.NoLlegaron++;
                        porRevisar.Add((cuando, $"{venta} quedó para anotarse a mano en eleventa. Si alguien la anotó, allá es un ticket como "
                            + $"cualquier otro y se importó; si no, no hay nada que quitar. {SiSeAnoto(v, propios)}"));
                        break;
                    case EstadoEnvioEleventa.Pendiente or EstadoEnvioEleventa.PorRevisar:
                        r.NoLlegaron++;
                        break;
                }
            }
        }
    }

    /// <summary>Qué ticket de eleventa puede ser una venta de aquí anotada a mano: con su total (al peso) y cobrado desde un poco antes.</summary>
    private static string SiSeAnoto(VentaMandada v, List<FilaTicket> propios)
    {
        var desde = HoraMexico.ALocal(v.PagadaUtc ?? v.CreadaUtc) - DevolucionesConEleventa.HolguraDeHora;
        var hasta = desde.AddDays(DevolucionesConEleventa.DiasDeLaVuelta);
        var quiza = propios
            .Where(t => t.VendidoEn >= desde && t.VendidoEn <= hasta && Math.Abs(ReglasEleventa.Centavos(t.Total) - v.CobradoCentavos) <= HolguraDeTotal)
            .Take(Sugeridos).ToList();
        return quiza.Count == 0
            ? "No se encontró en eleventa un ticket con ese total después de esa hora."
            : $"Si se anotó como {(quiza.Count == 1 ? "este ticket" : "uno de estos")}, cuenta dos veces: {string.Join("; ", quiza.Select(Ticket))}.";
    }

    /// <summary>
    /// Lo devuelto o cancelado en eleventa de los tickets de la caja 2: eleventa ya se lo bajó al ticket, que no se importa. Lo
    /// hizo la caja 2 (su marca: <c>TICKET_ID</c> + <c>DEVUELTO_EN</c>), o se hizo allá y la vuelta ya lo pasó a su venta o lo
    /// ligó como la anotación a mano de una de aquí (por <c>DEVOLUCIONES.ID</c> y su hora). Lo demás no está aquí y, como el
    /// ticket no se importa, no contaría en ningún lado; si puede ser la anotación de una de aquí que falta elegir (la vuelta
    /// la retiene), se dice.
    /// </summary>
    private static void Devoluciones(PuenteEnLaBase puente, DatosEleventa e, Dictionary<int, FilaTicket> cobrados, LoDelPuenteEnEleventa r,
        List<(DateTime Cuando, string Texto)> porRevisar)
    {
        var deAqui = puente.Devoluciones.Select(d => (d.VentaId, RestauracionDeEleventa.Marca(d.FechaUtc))).ToHashSet();
        var esperan = puente.Devoluciones.Where(d => d.EsperaAnotacion).ToLookup(d => d.VentaId, d => d.FechaUtc);
        var leidas = puente.Vuelta.ToLookup(v => v.DevolucionEleventa);
        var folios = puente.Ventas.ToDictionary(v => v.VentaId, v => v.Folio);
        foreach (var d in e.Devoluciones.Where(d => r.Tickets.ContainsKey(d.TicketId)).OrderBy(d => d.Id))
        {
            var ventaId = r.Tickets[d.TicketId];
            // Primero la vuelta: lo que pasó a la venta lleva la hora de eleventa, igual que la marca de una de aquí.
            var leida = leidas[d.Id].FirstOrDefault(v => v.DevueltaUtc == HoraMexico.AUtc(d.DevueltoEn));
            if (leida is { Pasada: true })
            {
                if (leida.Propia)
                    r.DevolucionesDeAqui++;
                else
                    r.DevolucionesDeEleventa++;
                continue;
            }
            if (leida is null && deAqui.Contains((ventaId, d.DevueltoEn)))
            {
                r.DevolucionesDeAqui++;
                continue;
            }
            r.DevolucionesSinPasar++;
            var que = $"{(d.EsCancelacion ? "La cancelación" : $"La devolución de {Pesos(Centavos(d.Total))}")} que se hizo en eleventa ({d.Caja}, "
                + $"{d.Cajero}, {Fecha(d.DevueltoEn)}) del ticket {Ticket(cobrados[d.TicketId])}, la venta {folios.GetValueOrDefault(ventaId, "?")} de la caja 2,";
            // La anotación a mano de una de aquí llega sin la marca: mientras nadie la elija, la vuelta la retiene.
            var devueltaUtc = HoraMexico.AUtc(d.DevueltoEn);
            if (leida is null && esperan[ventaId].Where(hecha => devueltaUtc >= hecha - DevolucionesConEleventa.HolguraDeHora).ToList() is { Count: > 0 } aqui)
                porRevisar.Add((d.DevueltoEn, $"{que} puede ser la anotación a mano de la devolución de aquí del {Fecha(HoraMexico.ALocal(aqui.Max()))}, "
                    + "que espera que alguien la elija en la pantalla de la caja 2 («Ya lo anoté en eleventa»). Si es esa, aquí ya está; si no, "
                    + "como ese ticket no se importa, no cuenta en ningún lado hasta pasarla a la venta."));
            else
                porRevisar.Add((d.DevueltoEn, $"{que} no está en esa venta: "
                    + $"{(leida?.PorRevisar ?? "la lectura de eleventa no la trajo.").TrimEnd()}{(leida is { Revisada: true } ? " Ya se revisó en la caja 2." : "")} "
                    + "Ese ticket no se importa (es venta de aquí): si no se pasa a la venta, no cuenta en ningún lado."));
        }
    }

    /// <summary>
    /// La ida: lo devuelto en la caja 2 de un ticket cobrado en eleventa. Ese ticket se importa como de eleventa, con lo que
    /// eleventa ya le bajó. Si la devolución no llegó allá (quedó pendiente o por revisar) y nadie la anotó a mano, el ticket se
    /// importa sin ella y cuenta de más.
    /// </summary>
    private static void Idas(PuenteEnLaBase puente, DatosEleventa e, Dictionary<int, FilaTicket> cobrados, LoDelPuenteEnEleventa r,
        List<(DateTime Cuando, string Texto)> porRevisar)
    {
        var enEleventa = e.Devoluciones.Select(d => (d.TicketId, d.DevueltoEn)).ToHashSet();
        foreach (var ida in puente.Idas)
        {
            if (enEleventa.Contains((ida.TicketEleventa, RestauracionDeEleventa.Marca(ida.FechaUtc))) || ida.Estado == EstadoEnvioEleventa.AMano)
            {
                r.Idas++;
                continue;
            }
            if (!cobrados.TryGetValue(ida.TicketEleventa, out var t) || r.Tickets.ContainsKey(t.Id))
                continue;
            var cuando = HoraMexico.ALocal(ida.FechaUtc);
            var porque = ida.Estado switch
            {
                EstadoEnvioEleventa.Enviada => "se mandó y eleventa ya no la tiene: ¿se restauró un respaldo?",
                EstadoEnvioEleventa.Pendiente => "todavía no se manda",
                _ => ida.Error is { Length: > 0 } error ? char.ToLower(error[0], Mexico) + error[1..] : "quedó por revisar",
            };
            porRevisar.Add((cuando, $"En la caja 2 se devolvieron {Pesos(ida.TotalCentavos)} del ticket {Ticket(t)} de eleventa ({ida.Caja}) el "
                + $"{Fecha(cuando)}, y eso no está en eleventa ({porque.TrimEnd('.')}). El ticket se importó sin esa devolución: si nadie la "
                + $"anotó a mano allá, cuenta {Pesos(ida.TotalCentavos)} de más."));
        }
    }

    /// <summary>La hora del cobro como la escribe el puente en <c>VENDIDO_EN</c>: la de la tienda, al segundo.</summary>
    private static DateTime AlSegundo(DateTime utc)
    {
        var local = HoraMexico.ALocal(utc);
        return new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerSecond);
    }

    private static string Ticket(FilaTicket t) =>
        $"#{t.Folio ?? t.Id} del {(t.VendidoEn is { } vendido ? Fecha(vendido) : "?")}, {Pesos(ReglasEleventa.Centavos(t.Total))}";

    private static string Fecha(DateTime local) => local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Pesos(long centavos) => (centavos / 100m).ToString("$#,##0.00", Mexico);

    private static long Centavos(decimal pesos) => (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero);
}
