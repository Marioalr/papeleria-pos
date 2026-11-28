using FirebirdSql.Data.FirebirdClient;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>El ajuste de inventario de un producto, como lo entiende eleventa: cuánto sube o baja, no cuánto queda.</summary>
/// <param name="ProductoId"><c>PRODUCTOS.ID</c> de eleventa.</param>
/// <param name="Diferencia">
/// Lo que se le suma a la existencia de eleventa, con su signo: lo contado menos lo que había (−3 si había 12 y se contaron 9).
/// Nunca el total: eleventa pudo vender entre que se calculó y que llega, y eso se respeta.
/// </param>
/// <param name="MarcaLocal">
/// La hora del ajuste en eleventa (<c>CUANDO_FUE</c>, hora local, al segundo). También es su marca: quien lo manda la fija
/// <b>una sola vez</b>, la guarda antes de escribir y la repite igual en cada reintento. Con el producto y la diferencia dice si
/// ya entró, porque el motivo («Ajuste de inventario #folio») es igual al de uno hecho a mano y no dice de dónde viene. Dos
/// ajustes del mismo producto nunca llevan la misma marca: el segundo se tomaría por el primero y no se escribiría.
/// </param>
/// <param name="ContadoLocal">
/// Cuándo se contó (hora local). Si después de esa hora alguien ajustó el producto en eleventa, no se aplica: manda lo más
/// nuevo. Vacío, no se revisa (una herramienta que ajusta una copia).
/// </param>
/// <param name="AjustesDelPos">
/// Los <c>INVENTARIO_AJUSTES.ID</c> que el propio POS ya escribió de este producto (los guarda al mandarlos): esos no cuentan
/// como «ajustado a mano después». Sin esto, el segundo conteo del mismo producto en el día chocaría con el primero.
/// </param>
public sealed record AjusteParaEleventa(int ProductoId, decimal Diferencia, DateTime MarcaLocal, DateTime? ContadoLocal = null,
    IReadOnlyCollection<int>? AjustesDelPos = null);

/// <param name="AjusteId"><c>INVENTARIO_AJUSTES.ID</c>, que también es su folio: el del motivo «Ajuste de inventario #folio».</param>
/// <param name="YaEstaba">Ya se había escrito (por ejemplo, se cortó la red justo al confirmar): no se escribió otra vez.</param>
/// <param name="Habia">La existencia de eleventa justo antes del ajuste (<c>CANTIDAD_ANTERIOR</c> de su renglón del kardex).</param>
/// <param name="Quedo">Lo que había más la diferencia.</param>
public sealed record AjusteEnEleventa(int AjusteId, bool YaEstaba, decimal Habia, decimal Quedo);

public static partial class EnvioEleventa
{
    /// <summary>Con qué empieza el motivo de un ajuste sin motivo escrito, en eleventa y aquí: le sigue el folio.</summary>
    public const string MotivoDelAjuste = "Ajuste de inventario #";

    /// <summary>
    /// El ajuste de inventario de un producto como lo guarda eleventa en Inventario › Ajustes (docs/migracion-eleventa.md,
    /// «Cómo ajusta eleventa el inventario»), en una sola transacción corta que lee la existencia y escribe:
    /// <list type="bullet">
    /// <item>el ajuste (<c>INVENTARIO_AJUSTES</c>): su <c>ID</c> del contador, el folio igual al <c>ID</c>, la diferencia con su
    /// signo, el costo y el nombre del producto de eleventa en ese momento, y el motivo «Ajuste de inventario #folio»;</item>
    /// <item>su renglón del kardex (<c>INVENTARIO_HISTORIAL</c>, con <c>AJUSTE_ID</c>): lo que había, la misma diferencia con su
    /// signo y el motivo;</item>
    /// <item>la existencia (<c>INVENTARIO_BALANCES</c>): lo que había <b>más</b> la diferencia. Al revés que una venta, que lleva
    /// la cantidad en positivo y resta.</item>
    /// </list>
    /// A nombre de la caja y del cajero de <paramref name="caja"/>, los mismos de las ventas de esa caja: en eleventa no se anota
    /// quién contó ni quién aceptó. No lleva turno: eleventa no le pone turno a un ajuste.
    /// <para>
    /// Antes de escribir, en la misma transacción: la huella (<see cref="EleventaRestaurada"/>); la marca (si ya está, regresa el
    /// mismo <c>ID</c> sin escribir); que eleventa siga teniendo el producto y que nadie lo haya ajustado allá después de contarlo
    /// (si no, <see cref="ChoqueConEleventa"/>: queda por revisar). Si una caja de eleventa mueve la misma existencia en ese
    /// instante, se vuelve a leer y a intentar unas veces, como eleventa, que se niega a guardar un ajuste si el inventario cambió
    /// mientras tanto.
    /// </para>
    /// </summary>
    public static async Task<AjusteEnEleventa> AjustarInventarioAsync(FbConnection c, CajaEnEleventa caja, AjusteParaEleventa ajuste, CancellationToken ct = default)
    {
        if (ajuste.Diferencia == 0)
            throw new ArgumentException("El ajuste no tiene diferencia: eleventa no guarda ajustes en cero.", nameof(ajuste));
        return await ConReintentosAsync(() => EscribirAjusteAsync(c, caja, ajuste, ct), ct);
    }

    public static async Task<AjusteEnEleventa> AjustarInventarioAsync(ServidorEleventa servidor, CajaEnEleventa caja, AjusteParaEleventa ajuste, CancellationToken ct = default)
    {
        await using var conexion = new FbConnection(servidor.Cadena());
        await conexion.OpenAsync(ct);
        return await AjustarInventarioAsync(conexion, caja, ajuste, ct);
    }

    /// <summary>
    /// Un ajuste que escribió el POS, por su marca (el producto, la hora y la diferencia, como lo busca
    /// <see cref="AjustarInventarioAsync(FbConnection, CajaEnEleventa, AjusteParaEleventa, CancellationToken)"/> antes de
    /// escribirlo), sin escribir nada: para saber si un intento que se cortó alcanzó a guardarlo. Vacío si no está.
    /// </summary>
    public static async Task<int?> BuscarAjusteAsync(ServidorEleventa servidor, int producto, DateTime marcaLocal, decimal diferencia, CancellationToken ct = default) =>
        await BuscarAsync(servidor, ConsultaDeLaMarca,
            [("producto", producto), ("cuando", AlSegundo(marcaLocal)), ("diferencia", (float)diferencia)], ct);

    // CANTIDAD es FLOAT: la diferencia se compara como tal (0.3 guardado no es el 0.3 de doble precisión).
    private const string ConsultaDeLaMarca =
        "SELECT FIRST 1 ID FROM INVENTARIO_AJUSTES WHERE PRODUCTO_ID = @producto AND CUANDO_FUE = @cuando AND CANTIDAD = @diferencia ORDER BY ID";

    /// <summary>Eleventa guarda la hora de un ajuste al segundo, sin milésimas, igual en el ajuste y en su renglón del kardex.</summary>
    private static DateTime AlSegundo(DateTime hora) => new(hora.Ticks - hora.Ticks % TimeSpan.TicksPerSecond);

    private static async Task<AjusteEnEleventa> EscribirAjusteAsync(FbConnection c, CajaEnEleventa caja, AjusteParaEleventa a, CancellationToken ct)
    {
        var cuando = AlSegundo(a.MarcaLocal);

        await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Write | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        }, ct);
        await using var sql = new Sql(c, tx, ct);
        await RevisarHuellaAsync(c, tx, caja, ct);

        // La marca: ya entró (un intento anterior que aquí no se supo, o se restauró un respaldo del POS). No se escribe otra vez.
        if (await sql.ValorAsync(ConsultaDeLaMarca, [("producto", a.ProductoId), ("cuando", cuando), ("diferencia", (float)a.Diferencia)]) is { } ya)
        {
            var renglon = await sql.FilaAsync("SELECT FIRST 1 CANTIDAD_ANTERIOR FROM INVENTARIO_HISTORIAL WHERE AJUSTE_ID = @ajuste ORDER BY ID", [("ajuste", ya)]);
            await tx.CommitAsync(ct);
            var habiaYa = renglon is null ? 0 : Cantidad(renglon[0]);
            return new AjusteEnEleventa(Convert.ToInt32(ya), YaEstaba: true, habiaYa, habiaYa + a.Diferencia);
        }

        // El nombre y el costo, los de eleventa en este momento. Si ya lo borró, no hay a qué ajustarle.
        var producto = await sql.FilaAsync("SELECT DESCRIPCION, PCOSTO FROM PRODUCTOS WHERE ID = @id AND ELIMINADO_EN IS NULL", [("id", a.ProductoId)])
            ?? throw new ChoqueConEleventa($"Eleventa ya no tiene el producto {a.ProductoId} (se borró allá): su conteo no se le puede mandar.");
        var nombre = producto[0] is string descripcion ? descripcion.Trim() : "";
        // PCOSTO es FLOAT: leído como tal da 3.6984 y no 3.69840002059937.
        var costo = (double)Cantidad(producto[1]);

        // Alguien lo ajustó a mano en eleventa después de contarlo: manda lo más nuevo, este ya no se aplica.
        if (a.ContadoLocal is { } contado)
        {
            var despues = await sql.FilasAsync(
                "SELECT AJUSTE_ID, CUANDO_FUE FROM INVENTARIO_HISTORIAL WHERE PRODUCTO_ID = @p AND CUANDO_FUE > @contado AND AJUSTE_ID IS NOT NULL ORDER BY ID",
                [("p", a.ProductoId), ("contado", contado)]);
            if (despues.FirstOrDefault(h => a.AjustesDelPos?.Contains(Convert.ToInt32(h[0])) != true) is { } aMano)
                throw new ChoqueConEleventa($"«{nombre}» se ajustó en eleventa el {(DateTime)aMano[1]:dd/MM} a las {(DateTime)aMano[1]:HH:mm}, después de contarlo "
                    + $"({contado:dd/MM HH:mm}): manda ese ajuste y este conteo no se aplica.", MotivoPorRevisar.EleventaYaLoTenia);
        }

        // Lo que hay en eleventa ahora, en la misma foto con la que se escribe: si otra caja lo mueve antes de confirmar, Firebird
        // rechaza la escritura (no se esperan candados) y se empieza de nuevo con la existencia nueva.
        var balance = await sql.FilaAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = @p", [("p", a.ProductoId)]);
        var habia = Cantidad(balance?[0]);
        // OJO: al revés que una venta. El ajuste lleva la diferencia con su signo y la existencia es lo anterior MÁS eso.
        var queda = habia + a.Diferencia;

        // El folio es el ID, y el motivo lleva el folio: el ID se toma del contador antes de armar el texto.
        var id = await sql.NuevoIdAsync("GEN_INVENTARIO_AJUSTES_ID");
        var motivo = $"{MotivoDelAjuste}{id}";
        await sql.EjecutarAsync("""
            INSERT INTO INVENTARIO_AJUSTES (ID, FOLIO, CUANDO_FUE, PRODUCTO_ID, CANTIDAD, COSTO_UNITARIO, DESCRIPCION, MOTIVO, CAJA_ID, USUARIO_ID, ALMACEN_ID)
            VALUES (@id, @id, @cuando, @p, @diferencia, @costo, @nombre, @motivo, @caja, @usuario, 1)
            """,
            [("id", id), ("cuando", cuando), ("p", a.ProductoId), ("diferencia", (double)a.Diferencia), ("costo", costo), ("nombre", Recortar(nombre, 255)),
             ("motivo", motivo), ("caja", caja.CajaId), ("usuario", caja.CajeroId)]);
        await sql.EjecutarAsync("""
            INSERT INTO INVENTARIO_HISTORIAL (PRODUCTO_ID, CUANDO_FUE, CANTIDAD_ANTERIOR, CANTIDAD, DESCRIPCION, COSTO_UNITARIO, COSTO_DESPUES,
                AJUSTE_ID, CAJA_ID, VENTA_POR_KIT, USUARIO_ID, ALMACEN_ID)
            VALUES (@p, @cuando, @antes, @diferencia, @motivo, @costo, @costo, @id, @caja, 'f', @usuario, 1)
            """,
            [("p", a.ProductoId), ("cuando", cuando), ("antes", (double)habia), ("diferencia", (double)a.Diferencia), ("motivo", motivo), ("costo", costo),
             ("id", id), ("caja", caja.CajaId), ("usuario", caja.CajeroId)]);
        // La existencia nueva se calcula con lo leído en esta misma transacción (foto fija): es «lo que hay ahora más la diferencia»,
        // nunca un total calculado antes en el POS.
        if (balance is null)
            await sql.EjecutarAsync("INSERT INTO INVENTARIO_BALANCES (PRODUCTO_ID, CANTIDAD_ACTUAL, ALMACEN_ID) VALUES (@p, @queda, 1)",
                [("p", a.ProductoId), ("queda", (double)queda)]);
        else
            await sql.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = @queda WHERE PRODUCTO_ID = @p",
                [("p", a.ProductoId), ("queda", (double)queda)]);

        SigueEsperando(caja);
        await tx.CommitAsync(ct);
        return new AjusteEnEleventa(id, YaEstaba: false, habia, queda);
    }

    /// <summary>
    /// Una cantidad o un costo de eleventa (FLOAT) como lo ve la tienda: 0.3 y no 0.300000011920929. Así la cuenta de «lo que había
    /// más la diferencia» se hace con los números que se ven (1.1 − 0.7 = 0.4) y no arrastra el error del FLOAT. Vacío es cero.
    /// </summary>
    private static decimal Cantidad(object? valor) => valor is null or DBNull ? 0 : (decimal)Convert.ToSingle(valor);
}
