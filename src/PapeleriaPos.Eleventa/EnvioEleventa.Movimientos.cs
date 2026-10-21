using FirebirdSql.Data.FirebirdClient;

namespace PapeleriaPos.Eleventa;

/// <summary>Una entrada (F7) o salida (F8) de efectivo de la caja nueva.</summary>
/// <param name="Monto">Lo que entró o salió de la caja.</param>
/// <param name="Motivo">Lo que se escribió en la caja nueva: eleventa guarda ahí el comentario de la entrada o salida.</param>
/// <param name="HechaLocal">Cuándo se hizo en la caja nueva (hora local). También es su marca en eleventa: así no se escribe dos veces.</param>
/// <param name="Turno">El turno de la caja nueva, por si en eleventa no hay uno abierto y hay que abrirlo.</param>
public sealed record MovimientoParaEleventa(bool EsEntrada, decimal Monto, string Motivo, DateTime HechaLocal, TurnoParaEleventa Turno);

/// <param name="MovimientoId"><c>CORTE_MOVIMIENTOS.ID</c>.</param>
/// <param name="Turno">El turno de eleventa donde quedó (vacío si ya estaba): el turno de la caja nueva lo adopta.</param>
public sealed record MovimientoEnEleventa(int MovimientoId, bool YaEstaba, TurnoDeEleventa? Turno = null);

public static partial class EnvioEleventa
{
    /// <summary>
    /// Una entrada o salida de efectivo como la escribe una caja de eleventa (docs/migracion-eleventa.md, «Entradas y
    /// salidas de dinero»), en una sola transacción:
    /// <list type="bullet">
    /// <item>un movimiento del corte (<c>CORTE_MOVIMIENTOS</c>, <c>Entrada</c> o <c>Salida</c>) con el motivo, el turno y
    /// la caja, sin forma de pago ni razón;</item>
    /// <item>lo suma a las entradas o salidas del turno y, si ese día ya tiene renglón para la caja, a las del día.</item>
    /// </list>
    /// Va al turno de eleventa de su hora (<see cref="TurnoParaAsync"/>). Fuera del piloto, si en eleventa no hay turno
    /// abierto para esa caja, lo abre: eleventa también abre el turno para una entrada.
    /// </summary>
    public static async Task<MovimientoEnEleventa> MoverEfectivoAsync(FbConnection c, CajaEnEleventa caja, MovimientoParaEleventa movimiento, CancellationToken ct = default)
    {
        if (movimiento.Monto <= 0)
            throw new ArgumentException("La entrada o salida no tiene monto.", nameof(movimiento));
        return await ConReintentosAsync(() => EscribirMovimientoAsync(c, caja, movimiento, ct), ct);
    }

    public static async Task<MovimientoEnEleventa> MoverEfectivoAsync(ServidorEleventa servidor, CajaEnEleventa caja, MovimientoParaEleventa movimiento, CancellationToken ct = default)
    {
        await using var conexion = new FbConnection(servidor.Cadena());
        await conexion.OpenAsync(ct);
        return await MoverEfectivoAsync(conexion, caja, movimiento, ct);
    }

    private static async Task<MovimientoEnEleventa> EscribirMovimientoAsync(FbConnection c, CajaEnEleventa caja, MovimientoParaEleventa m, CancellationToken ct)
    {
        // Eleventa anota la hora del movimiento con sus diezmilésimas de segundo, como Firebird las guarda.
        var cuando = new DateTime(m.HechaLocal.Ticks - m.HechaLocal.Ticks % 1000);
        var tipo = m.EsEntrada ? "Entrada" : "Salida";

        await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Write | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        }, ct);
        var sql = new Sql(c, tx, ct);
        await RevisarHuellaAsync(c, tx, caja, ct);

        if (await sql.ValorAsync("SELECT ID FROM CORTE_MOVIMIENTOS WHERE ID_CAJA = @caja AND TIPO = @tipo AND CUANDO_FUE = @cuando",
                [("caja", caja.CajaId), ("tipo", tipo), ("cuando", cuando)]) is { } ya)
        {
            await tx.CommitAsync(ct);
            return new MovimientoEnEleventa(Convert.ToInt32(ya), YaEstaba: true);
        }

        var turno = await TurnoParaAsync(sql, caja, cuando, m.Turno);
        var motivo = m.Motivo.Trim();
        var id = await AnotarEntradaOSalidaAsync(sql, caja.CajaId, turno.Id, m.EsEntrada, m.Monto,
            motivo.Length > 0 ? motivo : m.EsEntrada ? "Entrada de efectivo" : "Salida de efectivo", cuando);

        SigueEsperando(caja);
        await tx.CommitAsync(ct);
        return new MovimientoEnEleventa(id, YaEstaba: false, turno);
    }

    /// <summary>
    /// Lo que entra o sale de la caja fuera de las ventas (una entrada, una salida, o lo que sobró o faltó al cerrar el
    /// turno): un movimiento del corte, que suma a las entradas o salidas del turno y, si ese día ya tiene renglón para
    /// la caja, a las del día. Eleventa no crea el renglón del día para esto: en la copia, lo que pasó al día siguiente de
    /// abrir el turno, antes de que hubiera algo más ese día, no está en ningún día.
    /// </summary>
    private static async Task<int> AnotarEntradaOSalidaAsync(Sql sql, int caja, int turno, bool esEntrada, decimal monto, string descripcion, DateTime cuando)
    {
        var id = await sql.NuevoIdAsync("GEN_CORTE_MOVIMIENTOS_ID");
        await sql.EjecutarAsync("""
            INSERT INTO CORTE_MOVIMIENTOS (ID, CUANDO_FUE, MONTO, DESCRIPCION, TIPO, ID_TURNO, ID_CAJA)
            VALUES (@id, @cuando, @monto, @descripcion, @tipo, @turno, @caja)
            """,
            [("id", id), ("cuando", cuando), ("monto", monto), ("descripcion", Recortar(descripcion, 255)), ("tipo", esEntrada ? "Entrada" : "Salida"),
             ("turno", turno), ("caja", caja)]);

        var columna = esEntrada ? "ACUMULADO_ENTRADAS" : "ACUMULADO_SALIDAS";
        await sql.EjecutarAsync($"UPDATE TURNOS SET {columna} = COALESCE({columna}, 0) + @monto WHERE ID = @turno",
            [("turno", turno), ("monto", monto)]);
        await sql.EjecutarAsync($"UPDATE CORTE_OPERACIONES SET {columna} = COALESCE({columna}, 0) + @monto WHERE DIA = @dia AND ID_CAJA = @caja",
            [("dia", cuando.Date), ("caja", caja), ("monto", monto)]);
        return id;
    }
}
