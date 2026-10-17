using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>Un ticket que escribió el puente: su <c>VENTATICKETS.ID</c>, su folio, su marca (<c>CREADO_EN</c>) y cuándo se mandó.</summary>
public sealed record TicketDelPuente(int Id, int Folio, DateTime MarcaLocal, DateTime EnviadaUtc);

/// <summary>
/// Lo que el puente sabe que escribió en eleventa: los últimos tickets que mandó, los turnos de eleventa que adoptó y el
/// número más alto que le dio cada contador de eleventa. Con eso se ve si eleventa se restauró a un respaldo anterior.
/// </summary>
/// <param name="Mayores">Por contador (<c>GEN_VENTATICKETS_ID</c>, …), el número más alto que el puente recibió de él.</param>
public sealed record HuellaDelPuente(IReadOnlyList<TicketDelPuente> Tickets, IReadOnlyList<TurnoDeEleventa> Turnos, IReadOnlyDictionary<string, int> Mayores);

/// <summary>Lo que se vio al comparar con eleventa: lo que mandó el puente ya no está, o los números de eleventa regresaron.</summary>
/// <param name="Pruebas">Qué se vio, dicho para la tienda.</param>
/// <param name="DesdeUtc">Cuándo se mandó lo más viejo que ya no está; vacío si solo regresaron los números.</param>
public sealed record Restauracion(IReadOnlyList<string> Pruebas, DateTime? DesdeUtc);

/// <summary>
/// Eleventa se restauró a un respaldo anterior (<see cref="RestauracionDeEleventa"/>): lo que se iba a escribir no se
/// escribió, y el puente no escribe nada hasta que se reanude a mano. No es un choque (eso queda por revisar y lo demás
/// sigue) ni la red (eso se reintenta).
/// </summary>
public sealed class EleventaRestaurada(Restauracion restauracion) : Exception(RestauracionDeEleventa.Texto(restauracion))
{
    public Restauracion Restauracion { get; } = restauracion;
}

/// <summary>
/// Los respaldos de eleventa (docs/migracion-eleventa.md, «Los respaldos de eleventa»): eleventa puede restaurar sola su
/// respaldo más reciente si cree que la base está dañada, y restaurar regresa a la fecha del respaldo y pierde lo de
/// después. Si pasa con la caja 2 escribiendo, lo que mandó ya no está y los números de eleventa (sus contadores, los
/// folios) regresan: lo siguiente que escriba chocaría con lo que eleventa vuelva a hacer con esos números. Por eso:
/// <list type="bullet">
/// <item>antes de cada escritura, en la misma transacción, y en la lectura de cada pocos minutos, en la misma foto, se
/// revisa que siga en eleventa lo último que escribió el puente (<see cref="HuellaDelPuente"/>): sus tickets con su folio
/// y su marca, los turnos que adoptó con su inicio, y que los contadores no hayan regresado. Solo con lo que el puente
/// sabe que escribió: lo que eleventa hace siempre (cancelar, devolver, cerrar el turno y borrar su ticket abierto) no lo
/// toca, así que no da falsas alarmas;</item>
/// <item>si algo no está, el puente se detiene (<see cref="DetencionDelPuente"/>): no escribe ni aplica la lectura, y la
/// pantalla de la caja 2 lo dice. Se reanuda a mano, con «Configuración de la tienda» y en la bitácora; al reanudar, lo
/// que se perdió en eleventa queda por revisar (<see cref="PerdidosAsync"/>), para anotarlo a mano allá.</item>
/// </list>
/// Solo del piloto: se quita al dejar eleventa.
/// </summary>
public static class RestauracionDeEleventa
{
    /// <summary>Hasta cuántos días atrás se compara lo que mandó el puente (como la vuelta, <see cref="DevolucionesConEleventa.DiasDeLaVuelta"/>).</summary>
    public const int Dias = 31;

    /// <summary>Antes de cada escritura: los últimos tickets que mandó. Basta: una restauración pierde primero lo más nuevo.</summary>
    public const int AlEscribir = 5;

    /// <summary>En la lectura de cada pocos minutos: los últimos que mandó, en una lista <c>IN</c>.</summary>
    public const int AlLeer = ListasIn.Tamano;

    /// <summary>Los contadores de eleventa de lo que escribe el puente: nunca regresan, salvo que se restaure un respaldo.</summary>
    private static readonly Dictionary<string, string> Contadores = new()
    {
        ["GEN_VENTATICKETS_ID"] = "los tickets", ["GEN_TURNOS_ID"] = "los turnos", ["GEN_DEVOLUCIONES_ID"] = "las devoluciones",
        ["GEN_CORTE_MOVIMIENTOS_ID"] = "las entradas y salidas",
    };

    /// <summary>Cuántas pruebas de cada tipo se escriben (si faltan cien tickets, basta con decir unos y cuántos).</summary>
    private const int PruebasPorTipo = 3;

    /// <summary>Lo que se le dice a la tienda: qué se vio y qué hacer.</summary>
    public static string Texto(Restauracion r) =>
        $"Eleventa ya no tiene lo que le mandó la caja 2 (¿se restauró un respaldo?): {string.Join("; ", r.Pruebas)}.";

    /// <summary>
    /// Lo que el puente sabe que escribió, de la base de la caja nueva: los últimos <paramref name="tickets"/> tickets que
    /// mandó en los últimos <see cref="Dias"/> días, los turnos que adoptó y que siguen sin su corte, y lo más alto que le dio
    /// cada contador. Lo que quedó por revisar o se anotó a mano no cuenta: ya no es del puente. Los contadores, solo desde
    /// la última vez que se reanudó: lo de antes ya se revisó al reanudar (si eleventa quedó con un contador más abajo de lo
    /// que todavía tiene, eso no lo arregla el puente, y no se detiene otra vez por lo mismo).
    /// </summary>
    public static async Task<HuellaDelPuente> HuellaAsync(PosDbContext db, DateTime ahoraUtc, int tickets, CancellationToken ct = default)
    {
        var desde = ahoraUtc.AddDays(-Dias);
        var enviados = db.EnviosAEleventa.AsNoTracking().Where(e => e.Estado == EstadoEnvioEleventa.Enviada && e.EnviadaUtc >= desde);
        var reanudado = await db.Set<DetencionDelPuente>().AsNoTracking().Where(d => d.ReanudadoUtc != null).MaxAsync(d => d.ReanudadoUtc, ct);
        var desdeReanudado = reanudado is { } r ? enviados.Where(e => e.EnviadaUtc > r) : enviados;
        var ultimos = await (from e in enviados
                             join v in db.Ventas.AsNoTracking() on e.VentaId equals v.Id
                             where e.Tipo == TipoEnvioEleventa.Venta && e.TicketEleventa != null && e.FolioEleventa != null
                             orderby e.EnviadaUtc descending
                             select new { Ticket = e.TicketEleventa!.Value, Folio = e.FolioEleventa!.Value, v.CreadaUtc, Enviada = e.EnviadaUtc!.Value })
            .Take(tickets).ToListAsync(ct);
        // Los que la caja nueva sigue usando: de uno ya cortado, lo que se perdió lo dicen sus tickets.
        var turnos = await db.Set<TurnoEnEleventa>().AsNoTracking().Where(t => t.TomadoUtc == null)
            .OrderByDescending(t => t.AdoptadoUtc).Take(50).Select(t => new { t.TurnoEleventa, t.InicioUtc, t.AdoptadoUtc }).ToListAsync(ct);

        var mayores = new Dictionary<string, int>();
        void Mayor(string contador, int? valor)
        {
            if (valor is { } v)
                mayores[contador] = Math.Max(v, mayores.GetValueOrDefault(contador));
        }
        Mayor("GEN_VENTATICKETS_ID", await desdeReanudado.Where(e => e.Tipo == TipoEnvioEleventa.Venta).MaxAsync(e => e.TicketEleventa, ct));
        Mayor("GEN_DEVOLUCIONES_ID", await desdeReanudado.Where(e => e.Tipo == TipoEnvioEleventa.Cancelacion || e.Tipo == TipoEnvioEleventa.Devolucion
            || e.Tipo == TipoEnvioEleventa.DevolucionDeEleventa).MaxAsync(e => e.IdEleventa, ct));
        Mayor("GEN_CORTE_MOVIMIENTOS_ID", await desdeReanudado.Where(e => e.Tipo == TipoEnvioEleventa.MovimientoDeCaja).MaxAsync(e => e.IdEleventa, ct));
        Mayor("GEN_TURNOS_ID", turnos.Where(t => reanudado is null || t.AdoptadoUtc > reanudado).Select(t => (int?)t.TurnoEleventa).Max());

        return new HuellaDelPuente(
            ultimos.Select(u => new TicketDelPuente(u.Ticket, u.Folio, Marca(u.CreadaUtc), u.Enviada)).ToList(),
            turnos.Select(t => new TurnoDeEleventa(t.TurnoEleventa, HoraMexico.ALocal(t.InicioUtc))).ToList(),
            mayores);
    }

    /// <summary>
    /// Compara la huella con eleventa en la transacción que se le pase (la de la escritura o la foto de la lectura): vacío si
    /// todo sigue ahí; si no, lo que se vio.
    /// </summary>
    public static async Task<Restauracion?> RevisarAsync(FbConnection c, FbTransaction tx, HuellaDelPuente huella, CancellationToken ct = default)
    {
        var faltan = new List<string>();
        var cambiaron = new List<string>();
        var perdidos = new List<DateTime>();

        // Los tickets: que sigan ahí, con su folio y su marca (si no, ese número ya es de otro ticket).
        var tickets = new Dictionary<int, object[]>();
        foreach (var grupo in ListasIn.Grupos(huella.Tickets.Select(t => t.Id)))
            foreach (var fila in await FilasAsync(c, tx, $"SELECT ID, FOLIO, CREADO_EN FROM VENTATICKETS WHERE ID IN ({grupo})", ct))
                tickets[Convert.ToInt32(fila[0], CultureInfo.InvariantCulture)] = fila;
        foreach (var t in huella.Tickets.OrderByDescending(t => t.EnviadaUtc))
        {
            if (!tickets.TryGetValue(t.Id, out var fila))
                faltan.Add($"ya no está el ticket #{t.Folio} que mandó el {Hora(t.EnviadaUtc)}");
            else if (fila[1] is DBNull || Convert.ToInt32(fila[1], CultureInfo.InvariantCulture) != t.Folio || fila[2] is not DateTime creado || creado != t.MarcaLocal)
                cambiaron.Add($"el ticket {t.Id} de eleventa ya no es el #{t.Folio} que mandó el {Hora(t.EnviadaUtc)}"
                    + (fila[1] is DBNull ? " (ahora es otro)" : $" (ahora es otro, con el folio {fila[1]})"));
            else
                continue;
            perdidos.Add(t.EnviadaUtc);
        }

        // Los turnos que adoptó: que sigan ahí, con su inicio.
        var turnos = new Dictionary<int, object[]>();
        foreach (var grupo in ListasIn.Grupos(huella.Turnos.Select(t => t.Id)))
            foreach (var fila in await FilasAsync(c, tx, $"SELECT ID, INICIO_EN FROM TURNOS WHERE ID IN ({grupo})", ct))
                turnos[Convert.ToInt32(fila[0], CultureInfo.InvariantCulture)] = fila;
        var pruebasDeTurnos = huella.Turnos
            .Where(t => !turnos.TryGetValue(t.Id, out var fila) || fila[1] is not DateTime inicio || inicio != t.InicioLocal)
            .Select(t => turnos.ContainsKey(t.Id)
                ? $"el turno {t.Id} de eleventa ya no es el que empezó el {t.InicioLocal:dd/MM HH:mm}"
                : $"ya no está el turno {t.Id} de eleventa, el que empezó el {t.InicioLocal:dd/MM HH:mm}")
            .ToList();

        // Los contadores nunca regresan.
        var pruebasDeContadores = new List<string>();
        var contadores = huella.Mayores.Keys.Where(Contadores.ContainsKey).ToList();
        if (contadores.Count > 0)
        {
            var valores = (await FilasAsync(c, tx, $"SELECT {string.Join(", ", contadores.Select(g => $"GEN_ID({g}, 0)"))} FROM RDB$DATABASE", ct))[0];
            for (var i = 0; i < contadores.Count; i++)
                if (Convert.ToInt64(valores[i], CultureInfo.InvariantCulture) < huella.Mayores[contadores[i]])
                    pruebasDeContadores.Add($"los números de {Contadores[contadores[i]]} regresaron: eleventa va en el {valores[i]} y ya le había dado el "
                        + $"{huella.Mayores[contadores[i]]} a la caja 2");
        }

        var pruebas = Resumir(faltan, "tickets que mandó ya no están").Concat(Resumir(cambiaron, "tickets que mandó ya son otros"))
            .Concat(pruebasDeTurnos.Take(PruebasPorTipo)).Concat(pruebasDeContadores).ToList();
        return pruebas.Count == 0 ? null : new Restauracion(pruebas, perdidos.Count == 0 ? null : perdidos.Min());
    }

    /// <summary>Unas cuantas pruebas y cuántas más hay.</summary>
    private static IEnumerable<string> Resumir(List<string> pruebas, string cuantas) =>
        pruebas.Count <= PruebasPorTipo ? pruebas : pruebas.Take(PruebasPorTipo).Append($"en total, {pruebas.Count} {cuantas}");

    /// <summary>La detención sin reanudar, si la hay: mientras tanto el puente no escribe ni aplica la lectura.</summary>
    public static Task<DetencionDelPuente?> DetencionAsync(PosDbContext db, CancellationToken ct = default) =>
        db.Set<DetencionDelPuente>().AsNoTracking().Where(d => d.ReanudadoUtc == null).OrderBy(d => d.DetectadaUtc).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Lo que mandó el puente en los últimos <see cref="Dias"/> días y ya no está en eleventa como lo dejó: la venta, por su
    /// ticket con su folio y su marca; la cancelación y la devolución (también la de un ticket de eleventa), por su
    /// renglón con su ticket y su marca (<c>DEVUELTO_EN</c>); la entrada o salida, por su movimiento con su marca
    /// (<c>CUANDO_FUE</c>). Un número que ya es de otra cosa también se perdió. Solo lee.
    /// </summary>
    public static async Task<List<EnvioAEleventa>> PerdidosAsync(PosDbContext db, FbConnection c, DateTime ahoraUtc, CancellationToken ct = default)
    {
        var desde = ahoraUtc.AddDays(-Dias);
        var enviados = await db.EnviosAEleventa.Where(e => e.Estado == EstadoEnvioEleventa.Enviada && e.EnviadaUtc >= desde).ToListAsync(ct);
        var ventaIds = enviados.Where(e => e.VentaId is not null).Select(e => e.VentaId!.Value).Distinct().ToList();
        var creadas = await db.Ventas.AsNoTracking().Where(v => ventaIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.CreadaUtc, ct);
        // El ticket de eleventa de cada venta (el de sus cancelaciones y devoluciones).
        var ticketDe = await db.EnviosAEleventa.AsNoTracking()
            .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.TicketEleventa != null && e.VentaId != null && ventaIds.Contains(e.VentaId.Value))
            .ToDictionaryAsync(e => e.VentaId!.Value, e => e.TicketEleventa!.Value, ct);
        var devolucionIds = enviados.Where(e => e.DevolucionId is not null).Select(e => e.DevolucionId!.Value).ToList();
        var devueltas = await db.Devoluciones.AsNoTracking().Where(d => devolucionIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.FechaUtc, ct);
        var idaIds = enviados.Where(e => e.DevolucionDeEleventaId is not null).Select(e => e.DevolucionDeEleventaId!.Value).ToList();
        var idas = await db.Set<DevolucionDeEleventa>().AsNoTracking().Where(d => idaIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => new { d.TicketEleventa, d.FechaUtc }, ct);
        var movimientoIds = enviados.Where(e => e.MovimientoId is not null).Select(e => e.MovimientoId!.Value).ToList();
        var movidos = await db.MovimientosCaja.AsNoTracking().Where(m => movimientoIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.FechaUtc, ct);

        await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        }, ct);
        var tickets = await PorIdAsync(c, tx, "SELECT ID, FOLIO, CREADO_EN FROM VENTATICKETS",
            enviados.Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.TicketEleventa is not null).Select(e => e.TicketEleventa!.Value), ct);
        var devoluciones = await PorIdAsync(c, tx, "SELECT ID, TICKET_ID, DEVUELTO_EN FROM DEVOLUCIONES",
            enviados.Where(e => e.Tipo is TipoEnvioEleventa.Cancelacion or TipoEnvioEleventa.Devolucion or TipoEnvioEleventa.DevolucionDeEleventa
                && e.IdEleventa is not null).Select(e => e.IdEleventa!.Value), ct);
        var movimientos = await PorIdAsync(c, tx, "SELECT ID, CUANDO_FUE FROM CORTE_MOVIMIENTOS",
            enviados.Where(e => e.Tipo == TipoEnvioEleventa.MovimientoDeCaja && e.IdEleventa is not null).Select(e => e.IdEleventa!.Value), ct);
        await tx.CommitAsync(ct);

        bool SigueAhi(EnvioAEleventa e) => e.Tipo switch
        {
            TipoEnvioEleventa.Venta => e.TicketEleventa is { } id && tickets.TryGetValue(id, out var t) && t[1] is not DBNull
                && Convert.ToInt32(t[1], CultureInfo.InvariantCulture) == e.FolioEleventa
                && creadas.TryGetValue(e.VentaId!.Value, out var creada) && t[2] is DateTime marca && marca == Marca(creada),
            TipoEnvioEleventa.Cancelacion or TipoEnvioEleventa.Devolucion => e.IdEleventa is { } id && devoluciones.TryGetValue(id, out var d)
                && ticketDe.TryGetValue(e.VentaId!.Value, out var ticket) && Convert.ToInt32(d[1], CultureInfo.InvariantCulture) == ticket
                && e.DevolucionId is { } devolucion && devueltas.TryGetValue(devolucion, out var devuelta) && d[2] is DateTime marca && marca == Marca(devuelta),
            TipoEnvioEleventa.DevolucionDeEleventa => e.IdEleventa is { } id && devoluciones.TryGetValue(id, out var d)
                && idas.TryGetValue(e.DevolucionDeEleventaId!.Value, out var ida) && Convert.ToInt32(d[1], CultureInfo.InvariantCulture) == ida.TicketEleventa
                && d[2] is DateTime marca && marca == Marca(ida.FechaUtc),
            TipoEnvioEleventa.MovimientoDeCaja => e.IdEleventa is { } id && movimientos.TryGetValue(id, out var m)
                && movidos.TryGetValue(e.MovimientoId!.Value, out var movido) && m[1] is DateTime marca && marca == Marca(movido),
            // El cierre del turno no se manda en el piloto (CorteEnElPiloto).
            _ => true,
        };
        return enviados.Where(e => !SigueAhi(e)).OrderBy(e => e.EnviadaUtc).ToList();
    }

    /// <summary>
    /// La marca con que el puente escribe en eleventa (<c>CREADO_EN</c>, <c>DEVUELTO_EN</c>, <c>CUANDO_FUE</c>): la hora local
    /// en diezmilésimas de segundo, como la guarda Firebird.
    /// </summary>
    public static DateTime Marca(DateTime fechaUtc)
    {
        var local = HoraMexico.ALocal(fechaUtc);
        return new DateTime(local.Ticks - local.Ticks % 1000);
    }

    private static string Hora(DateTime utc) => HoraMexico.ALocal(utc).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);

    private static async Task<Dictionary<int, object[]>> PorIdAsync(FbConnection c, FbTransaction tx, string consulta, IEnumerable<int> ids, CancellationToken ct)
    {
        var filas = new Dictionary<int, object[]>();
        foreach (var grupo in ListasIn.Grupos(ids))
            foreach (var fila in await FilasAsync(c, tx, $"{consulta} WHERE ID IN ({grupo})", ct))
                filas[Convert.ToInt32(fila[0], CultureInfo.InvariantCulture)] = fila;
        return filas;
    }

    private static async Task<List<object[]>> FilasAsync(FbConnection c, FbTransaction tx, string consulta, CancellationToken ct)
    {
        await using var comando = new FbCommand(consulta, c, tx);
        await using var lector = await comando.ExecuteReaderAsync(ct);
        var filas = new List<object[]>();
        while (await lector.ReadAsync(ct))
        {
            var fila = new object[lector.FieldCount];
            lector.GetValues(fila);
            filas.Add(fila);
        }
        return filas;
    }
}
