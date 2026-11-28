using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>Lo que se le manda a eleventa de un renglón de un conteo del celular, o por qué no se manda solo.</summary>
/// <param name="Descartada">Ya no hace falta mandarlo (el renglón ya no espera nada de eleventa).</param>
public sealed record AjusteArmado(AjusteParaEleventa? Ajuste, string? PorRevisar, bool Descartada = false);

/// <summary>
/// El conteo del celular aceptado en el piloto, hacia eleventa (docs/propuestas/conteo-del-celular-a-eleventa.md, entrega 3;
/// docs/migracion-eleventa.md, «El conteo del celular, mandado a eleventa»): cada producto con diferencia va en la bandeja como un
/// envío más (<see cref="TipoEnvioEleventa.AjusteDeConteo"/>) y el puente lo escribe con
/// <see cref="EnvioEleventa.AjustarInventarioAsync(FbConnection, CajaEnEleventa, AjusteParaEleventa, CancellationToken)"/>.
/// Aquí está lo que une las dos puntas: poner el renglón en la bandeja con su marca, armar el ajuste, y dejar el renglón (y la
/// existencia de aquí) como quedó el envío. Solo sirve para convivir con eleventa: se quita al dejarlo.
/// </summary>
public static class ConteosParaEleventa
{
    /// <summary>Un producto dado de alta aquí: en eleventa no existe y no hay a qué ajustarle.</summary>
    public const string NoEstaEnEleventa = "No está en eleventa: dalo de alta allá.";

    /// <summary>Lo que dice un renglón que se perdió en eleventa al restaurar un respaldo.</summary>
    public const string SePerdio = "Eleventa ya no tiene este ajuste: se perdió al restaurar un respaldo. Captúralo a mano en eleventa.";

    /// <summary>
    /// Pone en la bandeja los renglones que quedaron «va a eleventa» y les fija su marca: la hora del ajuste en eleventa, al
    /// segundo, <b>una sola vez</b> y antes de escribir nada (se guarda con la misma transacción que aceptar la lista). Cada renglón
    /// lleva su propio segundo, y ninguno la misma hora que otro ajuste de su producto que el POS ya haya marcado: dos ajustes del
    /// mismo producto con la misma hora y la misma diferencia se tomarían por uno solo.
    /// </summary>
    public static async Task EncolarAsync(PosDbContext db, IReadOnlyList<ConteoDelCelularRenglon> renglones, DateTime ahoraUtc, CancellationToken ct = default)
    {
        if (renglones.Count == 0)
            return;
        var variantes = renglones.Select(r => r.VarianteId).Distinct().ToList();
        var ultima = await db.ConteosDelCelularRenglones.AsNoTracking().Where(r => variantes.Contains(r.VarianteId) && r.MarcaUtc != null)
            .MaxAsync(r => r.MarcaUtc, ct);
        var marca = AlSegundo(ahoraUtc);
        if (ultima is { } ya && ya >= marca)
            marca = AlSegundo(ya).AddSeconds(1);
        var creado = ahoraUtc;
        foreach (var r in renglones)
        {
            (r.MarcaUtc, r.Eleventa, r.EleventaMotivo, r.AjusteEleventa) = (marca, ConteoEnEleventa.Pendiente, null, null);
            db.EnviosAEleventa.Add(new EnvioAEleventa { Tipo = TipoEnvioEleventa.AjusteDeConteo, ConteoRenglonId = r.Id, CreadoUtc = creado });
            marca = marca.AddSeconds(1);
            // En el orden de la lista: la bandeja se recorre por cuándo entró cada cosa.
            creado = creado.AddMilliseconds(1);
        }
    }

    /// <summary>Lo que dice en la bandeja un ajuste que espera porque el interruptor se apagó después de aceptar su conteo.</summary>
    public const string Apagado = "Está apagado «Mandar los conteos a eleventa»: este ajuste no se le escribe a eleventa mientras siga así. "
        + "Préndelo en Configuración › Inventario para mandarlo, o déjalo por revisar y captúralo a mano en eleventa.";

    /// <summary>
    /// Si «Mandar los conteos a eleventa» sigue prendido en la sucursal de ese renglón. Se revisa otra vez al mandar cada ajuste: con el
    /// interruptor apagado no se le escribe nada a eleventa, tampoco lo que ya estaba en la bandeja.
    /// </summary>
    public static async Task<bool> SiguePrendidoAsync(PosDbContext db, Guid renglonId, CancellationToken ct = default) =>
        await (from r in db.ConteosDelCelularRenglones.AsNoTracking()
               join c in db.ConteosDelCelular.AsNoTracking() on r.ConteoId equals c.Id
               join k in db.ConfiguracionesSucursal.AsNoTracking() on c.SucursalId equals k.SucursalId
               where r.Id == renglonId
               select k.MandarConteosAEleventa).FirstOrDefaultAsync(ct);

    private static DateTime AlSegundo(DateTime hora) => new(hora.Ticks - hora.Ticks % TimeSpan.TicksPerSecond, hora.Kind);

    /// <summary>
    /// El ajuste de un renglón, como lo entiende eleventa: su producto de allá, la diferencia que salió al aceptarlo (nunca el
    /// total), su marca, cuándo se contó y los ajustes que el propio POS ya escribió de ese producto (para que el segundo conteo
    /// del día no choque con el primero).
    /// </summary>
    public static async Task<AjusteArmado> ArmarAsync(PosDbContext db, Guid renglonId, CancellationToken ct = default)
    {
        var r = await db.ConteosDelCelularRenglones.AsNoTracking().SingleAsync(x => x.Id == renglonId, ct);
        if (r.Eleventa != ConteoEnEleventa.Pendiente)
            return new(null, "Ese producto del conteo ya no espera ir a eleventa.", Descartada: true);
        if (r.Diferencia is not { } diferencia || diferencia.Milesimas == 0 || r.MarcaUtc is not { } marca)
            return new(null, "Ese producto del conteo no tiene diferencia que mandar.", Descartada: true);
        var contado = r.ContadoUtc ?? await db.ConteosDelCelular.AsNoTracking().Where(c => c.Id == r.ConteoId).Select(c => c.ContadoUtc).SingleAsync(ct);
        if (await ProductoDeEleventaAsync(db, r.VarianteId, ct) is not { } producto)
            return new(null, NoEstaEnEleventa);
        var delPos = await db.ConteosDelCelularRenglones.AsNoTracking().Where(x => x.VarianteId == r.VarianteId && x.AjusteEleventa != null)
            .Select(x => x.AjusteEleventa!.Value).ToListAsync(ct);
        return new(new AjusteParaEleventa(producto, diferencia.EnUnidades, HoraMexico.ALocal(marca), HoraMexico.ALocal(contado), delPos), null);
    }

    /// <summary>El <c>PRODUCTOS.ID</c> de eleventa de una variante (el de la importación o la lectura); vacío si se dio de alta aquí.</summary>
    public static async Task<int?> ProductoDeEleventaAsync(PosDbContext db, Guid varianteId, CancellationToken ct = default) =>
        await db.MapeosImportacion.AsNoTracking()
            .Where(m => m.Origen == SincronizacionEleventa.Origen && m.Entidad == nameof(Variante) && m.IdNuevo == varianteId)
            .Select(m => m.IdOrigen).FirstOrDefaultAsync(ct) is { } id && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var producto)
            ? producto
            : null;

    /// <summary>
    /// Deja el renglón como quedó su envío (sin guardar: lo guarda quien llama, junto con el envío):
    /// <list type="bullet">
    /// <item>enviado: «Ya quedó en eleventa», con el <c>INVENTARIO_AJUSTES.ID</c> que le tocó;</item>
    /// <item>por revisar (eleventa no lo tomó, o se perdió al restaurar un respaldo): con su motivo, y la existencia de aquí, que al
    /// aceptar ya había tomado la diferencia, <b>regresa</b> con el movimiento contrario del mismo conteo. Así vuelve a seguir a
    /// eleventa, la lectura no encuentra nada que corregir y el siguiente conteo no lo toma por un movimiento;</item>
    /// <item>a mano (alguien dijo que ya lo capturó allá): «capturado». Si la existencia de aquí ya había regresado, la de eleventa
    /// llega con su lectura.</item>
    /// </list>
    /// </summary>
    public static async Task AlQuedarAsync(PosDbContext db, EnvioAEleventa envio, DateTime ahoraUtc, CancellationToken ct = default)
    {
        if (envio.Tipo != TipoEnvioEleventa.AjusteDeConteo || envio.ConteoRenglonId is not { } id
            || await db.ConteosDelCelularRenglones.SingleOrDefaultAsync(x => x.Id == id, ct) is not { } r)
            return;
        // Si la diferencia está aplicada en la existencia de aquí: desde que se acepta hasta que queda por revisar.
        var aplicada = r.Eleventa is ConteoEnEleventa.Pendiente or ConteoEnEleventa.Enviado;
        switch (envio.Estado)
        {
            case EstadoEnvioEleventa.Enviada:
                (r.Eleventa, r.AjusteEleventa, r.EleventaMotivo) = (ConteoEnEleventa.Enviado, envio.IdEleventa, null);
                break;
            case EstadoEnvioEleventa.PorRevisar:
                (r.Eleventa, r.AjusteEleventa, r.EleventaMotivo) = (ConteoEnEleventa.PorRevisar, null, Recortar(envio.Error ?? "Eleventa no lo tomó."));
                if (aplicada && r.Diferencia is { } diferencia)
                {
                    var conteo = await db.ConteosDelCelular.AsNoTracking().Where(c => c.Id == r.ConteoId).Select(c => new { c.Id, c.SucursalId }).SingleAsync(ct);
                    var variante = await db.Variantes.Include(v => v.Producto).SingleAsync(v => v.Id == r.VarianteId, ct);
                    await new RegistroInventario(db).AgregarDiferenciaDeConteoAsync(conteo.SucursalId, variante, Cantidad.Cero - diferencia, ahoraUtc,
                        nameof(ConteoDelCelular), conteo.Id, ct);
                    r.CostoDiferenciaCentavos = null;
                }
                break;
            case EstadoEnvioEleventa.AMano:
                (r.Eleventa, r.EleventaMotivo) = (ConteoEnEleventa.Capturado, null);
                break;
            case EstadoEnvioEleventa.Descartada when r.Eleventa == ConteoEnEleventa.Pendiente:
                // No debería pasar (un renglón pendiente siempre tiene qué mandar): que no se quede «va a eleventa» para siempre.
                (r.Eleventa, r.EleventaMotivo) = (ConteoEnEleventa.PorRevisar, Recortar(envio.Error ?? "No se pudo mandar."));
                goto case EstadoEnvioEleventa.PorRevisar;
        }
    }

    private static string Recortar(string texto) => texto.Length > 500 ? texto[..500] : texto;

    /// <summary>La sucursal y la caja donde se aceptó el conteo de ese renglón: ahí va su fila de la bandeja, su permiso y su bitácora.</summary>
    public static async Task<(Guid Sucursal, Guid Caja)?> DondeAsync(PosDbContext db, Guid renglonId, CancellationToken ct = default) =>
        await (from r in db.ConteosDelCelularRenglones.AsNoTracking()
               join c in db.ConteosDelCelular.AsNoTracking() on r.ConteoId equals c.Id
               where r.Id == renglonId
               select new { c.SucursalId, c.CajaId }).FirstOrDefaultAsync(ct) is { } donde
            ? (donde.SucursalId, donde.CajaId)
            : null;

    /// <summary>«Ajuste de inventario de «Lápiz HB»: −3 (conteo del celular)», para la bandeja y el registro.</summary>
    public static async Task<string> QueEsAsync(PosDbContext db, Guid? renglonId, CancellationToken ct = default)
    {
        var r = renglonId is { } id
            ? await db.ConteosDelCelularRenglones.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.Descripcion, x.Aceptado, x.Desde, x.Sistema }).FirstOrDefaultAsync(ct)
            : null;
        return r is null ? "Ajuste de inventario de un conteo del celular" : Texto(r.Descripcion, r.Aceptado + r.Desde - r.Sistema);
    }

    public static string Texto(string descripcion, Cantidad? diferencia) =>
        $"Ajuste de inventario de «{descripcion}»"
        + (diferencia is { } d ? $": {d.EnUnidades.ToString("+#,##0.###;-#,##0.###;0", CultureInfo.InvariantCulture)}" : "") + " (conteo del celular)";
}

/// <summary>Lo que eleventa movió de un producto desde que se contó, leído de su kardex.</summary>
/// <param name="Movido">
/// La suma, con el signo de la existencia: lo que eleventa vendió resta; lo que le devolvieron o recibió, suma. Sin lo que escribió
/// el propio POS (sus ventas y devoluciones ya están en el kardex de aquí; sus ajustes de conteos son correcciones, no movimientos).
/// </param>
/// <param name="AjustadoALas">Si alguien lo ajustó a mano en eleventa después de contarlo: cuándo (la hora de eleventa).</param>
/// <param name="NoSeSabe">Después de contarlo hubo en eleventa un movimiento que el POS no sabe sumar (una transferencia): qué fue.</param>
/// <param name="Firma">Lo que se leyó (su existencia y sus renglones): si cambia entre dos lecturas, eleventa lo movió mientras tanto.</param>
public sealed record MovidoEnEleventa(Cantidad Movido, DateTime? AjustadoALas, string? NoSeSabe, string Firma);

/// <summary>De los renglones del kardex de eleventa, los que escribió el propio POS (para no contarlos dos veces).</summary>
/// <param name="Tickets">Los <c>VENTATICKETS.ID</c> de las ventas que mandó el puente: sus renglones y los de sus devoluciones.</param>
/// <param name="Ajustes">Los <c>INVENTARIO_AJUSTES.ID</c> de los conteos que ya mandó.</param>
/// <param name="Idas">Las devoluciones de aquí de un ticket de eleventa: el ticket y su marca (<c>CUANDO_FUE</c>).</param>
public sealed record LoDelPosEnElKardex(IReadOnlySet<int> Tickets, IReadOnlySet<int> Ajustes, IReadOnlySet<(int Ticket, DateTime Marca)> Idas);

/// <summary>
/// El kardex de eleventa (<c>INVENTARIO_HISTORIAL</c>) de los productos de un conteo, desde que se contó cada uno (2026-11-27): eleventa
/// anota la hora de cada movimiento (<c>CUANDO_FUE</c>), así que «lo movido desde que se contó» sale de ahí y no de cuándo lo trajo la
/// lectura de cada 5 minutos. Con la lectura, lo que eleventa vendió en los minutos antes de contar (y que la lectura trajo después)
/// se restaba dos veces. Solo lee.
/// <para>
/// El signo, como en docs/migracion-eleventa.md («Cómo ajusta eleventa el inventario»): una venta lleva la cantidad en positivo y la
/// existencia baja (su devolución, en negativo y sube); un ajuste, una recepción y un alta la llevan con su signo y suman.
/// </para>
/// </summary>
public static class KardexDeEleventa
{
    /// <summary>Un renglón del kardex de eleventa, como se lee.</summary>
    public sealed record Renglon(int Id, int Producto, DateTime Cuando, decimal Cantidad, int? Ajuste, int? Recibo, int? Venta, int? Transferencia);

    /// <summary>Lo leído: la existencia de cada producto y sus renglones posteriores a cuando se contó.</summary>
    public sealed record Leido(IReadOnlyDictionary<int, decimal> Existencias, IReadOnlyList<Renglon> Renglones);

    /// <summary>
    /// Lee, en una sola foto, la existencia de esos productos y los renglones de su kardex posteriores a cuando se contó cada uno
    /// (<paramref name="contadoLocal"/>, la hora local por <c>PRODUCTOS.ID</c>).
    /// </summary>
    public static async Task<Leido> LeerAsync(ServidorEleventa servidor, IReadOnlyDictionary<int, DateTime> contadoLocal, CancellationToken ct = default)
    {
        if (contadoLocal.Count == 0)
            return new Leido(new Dictionary<int, decimal>(), []);
        await using var conexion = new FbConnection(servidor.Cadena());
        await conexion.OpenAsync(ct);
        await using var tx = await conexion.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        }, ct);
        var desde = contadoLocal.Values.Min();
        var existencias = new Dictionary<int, decimal>();
        var renglones = new List<Renglon>();
        foreach (var grupo in ListasIn.Grupos(contadoLocal.Keys))
        {
            await using (var comando = new FbCommand($"SELECT PRODUCTO_ID, CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID IN ({grupo})", conexion, tx))
            await using (var lector = await comando.ExecuteReaderAsync(ct))
                while (await lector.ReadAsync(ct))
                    existencias[lector.GetInt32(0)] = Numero(lector.GetValue(1));
            await using (var comando = new FbCommand(
                "SELECT ID, PRODUCTO_ID, CUANDO_FUE, CANTIDAD, AJUSTE_ID, RECIBO_INVENTARIO_ID, VENTA_ID, TRANSFERENCIA_ID FROM INVENTARIO_HISTORIAL "
                + $"WHERE PRODUCTO_ID IN ({grupo}) AND CUANDO_FUE > @desde ORDER BY ID", conexion, tx))
            {
                comando.Parameters.AddWithValue("desde", desde);
                await using var lector = await comando.ExecuteReaderAsync(ct);
                while (await lector.ReadAsync(ct))
                {
                    var producto = lector.GetInt32(1);
                    var cuando = lector.GetDateTime(2);
                    if (cuando > contadoLocal[producto])
                        renglones.Add(new Renglon(lector.GetInt32(0), producto, cuando, Numero(lector.GetValue(3)), Entero(lector.GetValue(4)),
                            Entero(lector.GetValue(5)), Entero(lector.GetValue(6)), Entero(lector.GetValue(7))));
                }
            }
        }
        await tx.CommitAsync(ct);
        return new Leido(existencias, renglones);
    }

    /// <summary>Las cantidades de eleventa son FLOAT: 0.3 y no 0.300000011920929.</summary>
    private static decimal Numero(object valor) => valor is DBNull ? 0 : (decimal)Convert.ToSingle(valor, CultureInfo.InvariantCulture);

    private static int? Entero(object valor) => valor is DBNull ? null : Convert.ToInt32(valor, CultureInfo.InvariantCulture);

    /// <summary>Lo que hace falta de la base de aquí para saber qué renglones de los leídos escribió el propio POS.</summary>
    public static async Task<LoDelPosEnElKardex> DelPosAsync(PosDbContext db, Leido leido, CancellationToken ct = default)
    {
        var tickets = leido.Renglones.Where(r => r.Venta is not null).Select(r => r.Venta!.Value).Distinct().ToList();
        var ajustes = leido.Renglones.Where(r => r.Ajuste is not null).Select(r => r.Ajuste!.Value).Distinct().ToList();
        var delPuente = tickets.Count == 0 ? [] : await db.EnviosAEleventa.AsNoTracking()
            .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.TicketEleventa != null && tickets.Contains(e.TicketEleventa.Value))
            .Select(e => e.TicketEleventa!.Value).ToListAsync(ct);
        var deConteos = ajustes.Count == 0 ? [] : await db.ConteosDelCelularRenglones.AsNoTracking()
            .Where(r => r.AjusteEleventa != null && ajustes.Contains(r.AjusteEleventa.Value)).Select(r => r.AjusteEleventa!.Value).ToListAsync(ct);
        var idas = tickets.Count == 0 ? [] : await db.Set<DevolucionDeEleventa>().AsNoTracking().Where(d => tickets.Contains(d.TicketEleventa))
            .Select(d => new { d.TicketEleventa, d.FechaUtc }).ToListAsync(ct);
        return new LoDelPosEnElKardex(delPuente.ToHashSet(), deConteos.ToHashSet(),
            idas.Select(i => (i.TicketEleventa, RestauracionDeEleventa.Marca(i.FechaUtc))).ToHashSet());
    }

    /// <summary>
    /// De cada producto, lo que eleventa movió desde que se contó. Los renglones del propio POS no cuentan
    /// (<paramref name="delPos"/>); un ajuste que no es del POS es alguien que lo ajustó a mano allá.
    /// </summary>
    public static Dictionary<int, MovidoEnEleventa> Calcular(Leido leido, LoDelPosEnElKardex delPos, IEnumerable<int> productos)
    {
        var porProducto = leido.Renglones.ToLookup(r => r.Producto);
        var resultado = new Dictionary<int, MovidoEnEleventa>();
        foreach (var producto in productos.Distinct())
        {
            var movido = 0m;
            DateTime? ajustado = null;
            string? noSeSabe = null;
            foreach (var r in porProducto[producto])
            {
                if (r.Ajuste is { } ajuste)
                {
                    if (!delPos.Ajustes.Contains(ajuste) && (ajustado is null || r.Cuando > ajustado))
                        ajustado = r.Cuando;
                }
                else if (r.Venta is { } ticket)
                {
                    // Una venta o una devolución de eleventa. Las del puente ya están en el kardex de aquí.
                    if (!delPos.Tickets.Contains(ticket) && !delPos.Idas.Contains((ticket, r.Cuando)))
                        movido -= r.Cantidad;
                }
                else if (r.Recibo is not null)
                    movido += r.Cantidad;
                else
                    noSeSabe = r.Transferencia is not null ? "una transferencia" : "un movimiento que el POS no conoce";
            }
            var firma = $"{leido.Existencias.GetValueOrDefault(producto).ToString(CultureInfo.InvariantCulture)}|{string.Join(",", porProducto[producto].Select(r => r.Id))}";
            resultado[producto] = new MovidoEnEleventa(ReglasEleventa.CantidadDe((double)movido), ajustado, noSeSabe, firma);
        }
        return resultado;
    }
}
