using FirebirdSql.Data.FirebirdClient;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>Una cancelación o una devolución de un ticket que ya está en eleventa.</summary>
/// <param name="TicketEleventa"><c>VENTATICKETS.ID</c> del ticket.</param>
/// <param name="DevueltaLocal">Cuándo se hizo en la caja nueva (hora local). También es su marca en eleventa: así no se aplica dos veces.</param>
/// <param name="Turno">El turno de la caja nueva de donde salió el dinero, por si en eleventa no hay uno abierto.</param>
/// <param name="Articulos">Lo devuelto. En una cancelación se devuelve todo el ticket de eleventa; aquí solo cuenta qué no regresa al inventario.</param>
public sealed record DevolucionParaEleventa(
    int TicketEleventa, bool EsCancelacion, DateTime DevueltaLocal, TurnoParaEleventa Turno, IReadOnlyList<ArticuloDevuelto> Articulos);

/// <param name="ProductoId"><c>PRODUCTOS.ID</c> de eleventa; vacío si fue un artículo común (se busca en el ticket por su nombre).</param>
/// <param name="Cantidad">En la unidad de eleventa.</param>
/// <param name="Importe">El dinero que salió de la caja por este artículo.</param>
/// <param name="RegresaAlInventario">Falso si volvió dañado: no se suma a la existencia de eleventa.</param>
/// <param name="ArticuloEleventa">
/// <c>VENTATICKETS_ARTICULOS.ID</c>, si se sabe (la devolución de un ticket cobrado en eleventa, que se leyó allá): se busca por
/// él y no por el código.
/// </param>
/// <param name="Codigo">
/// Sin <paramref name="ProductoId"/>, el código con que se mandó (la línea de impresiones, «IMP», 2026-10-25): se busca en el ticket por
/// él antes que por su nombre.
/// </param>
public sealed record ArticuloDevuelto(int? ProductoId, string Nombre, decimal Cantidad, decimal Importe, bool RegresaAlInventario = true,
    int? ArticuloEleventa = null, string? Codigo = null);

/// <param name="DevolucionId"><c>DEVOLUCIONES.ID</c>.</param>
/// <param name="Turno">El turno de eleventa donde quedó (vacío si ya estaba): el turno de la caja nueva lo adopta.</param>
public sealed record DevolucionEnEleventa(int DevolucionId, bool YaEstaba, TurnoDeEleventa? Turno = null);

/// <param name="TurnoId">El turno de eleventa que se cerró; vacío si ya no había uno abierto.</param>
public sealed record CierreEnEleventa(int? TurnoId, bool YaEstaba);

public static partial class EnvioEleventa
{
    /// <summary>
    /// Cancela o devuelve en eleventa como lo hace una caja de eleventa (docs/migracion-eleventa.md, «Cancelar, devolver
    /// y cerrar el turno»), en una sola transacción:
    /// <list type="bullet">
    /// <item>el renglón de <c>DEVOLUCIONES</c> (C o P) y, en la parcial, uno de <c>DEVOLUCIONES_ARTICULOS</c> por artículo; la
    /// que devuelve todo lo que le queda al ticket va como cancelación, como lo hace la tienda (2026-10-24);</item>
    /// <item>en la cancelación, el ticket queda cancelado con su total, sin subtotal ni ganancia; en la parcial, el artículo
    /// baja su cantidad y su importe (o se borra si se devolvió completo) y el ticket, su total, subtotal y ganancia;</item>
    /// <item>en las dos, <c>NUMERO_ARTICULOS</c> baja las piezas devueltas; lo de granel contó uno al vender y no baja;</item>
    /// <item>un movimiento <c>Devolucion</c> del corte por artículo;</item>
    /// <item>la entrada al inventario («Devolución de venta #folio»), salvo lo que volvió dañado;</item>
    /// <item>los acumulados del turno y del día en que se devuelve (que es cuando sale el dinero) y de sus departamentos.</item>
    /// </list>
    /// Va al turno de eleventa de su hora (<see cref="TurnoParaAsync"/>). Lo que choca con eleventa queda por revisar
    /// (<see cref="ChoqueConEleventa"/>): el ticket ya no está, ya está cancelado o ya no tiene lo que se devuelve, y la
    /// devolución de un ticket de otra caja o de otro turno de eleventa, que todavía no se ha visto cómo la anota eleventa.
    /// </summary>
    public static async Task<DevolucionEnEleventa> DevolverAsync(FbConnection c, CajaEnEleventa caja, DevolucionParaEleventa devolucion, CancellationToken ct = default)
    {
        if (!devolucion.EsCancelacion && devolucion.Articulos.Count == 0)
            throw new ArgumentException("La devolución no tiene artículos.", nameof(devolucion));
        return await ConReintentosAsync(() => EscribirDevolucionAsync(c, caja, devolucion, ct), ct);
    }

    public static async Task<DevolucionEnEleventa> DevolverAsync(ServidorEleventa servidor, CajaEnEleventa caja, DevolucionParaEleventa devolucion, CancellationToken ct = default)
    {
        await using var conexion = new FbConnection(servidor.Cadena());
        await conexion.OpenAsync(ct);
        return await DevolverAsync(conexion, caja, devolucion, ct);
    }

    /// <summary>
    /// El corte de la caja nueva cierra el turno de eleventa como lo cierra eleventa:
    /// <list type="bullet">
    /// <item>lo que faltó o sobró al contar queda como una salida o entrada del corte («Efectivo faltante al cerrar turno
    /// de…»), en el acumulado del turno y, si ese día ya tiene renglón, en el del día (como una entrada o salida);</item>
    /// <item>la hora de término y el efectivo al cierre, que eleventa calcula: el fondo más el efectivo, menos lo devuelto,
    /// más entradas y menos salidas (con la diferencia, es lo que se contó);</item>
    /// <item>se borra su ticket abierto.</item>
    /// </list>
    /// Si ya no hay turno abierto, no hace nada.
    /// </summary>
    /// <param name="diferencia">Lo contado menos lo esperado en el corte de la caja nueva: negativo si faltó.</param>
    public static async Task<CierreEnEleventa> CerrarTurnoAsync(
        FbConnection c, CajaEnEleventa caja, DateTime cerradoLocal, decimal diferencia = 0, CancellationToken ct = default) =>
        await ConReintentosAsync(() => EscribirCierreAsync(c, caja, cerradoLocal, diferencia, ct), ct);

    public static async Task<CierreEnEleventa> CerrarTurnoAsync(
        ServidorEleventa servidor, CajaEnEleventa caja, DateTime cerradoLocal, decimal diferencia = 0, CancellationToken ct = default)
    {
        await using var conexion = new FbConnection(servidor.Cadena());
        await conexion.OpenAsync(ct);
        return await CerrarTurnoAsync(conexion, caja, cerradoLocal, diferencia, ct);
    }

    private static async Task<T> ConReintentosAsync<T>(Func<Task<T>> escribir, CancellationToken ct)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                return await escribir();
            }
            catch (FbException ex) when (intento < 5 && ex.Errors.Any(e => Choques.Contains(e.Number)))
            {
                await Task.Delay(200 * intento, ct);
            }
        }
    }

    /// <summary>Un artículo del ticket en eleventa.</summary>
    private sealed record EnElTicket(int Id, string Codigo, string Nombre, decimal Cantidad, decimal Total, decimal GananciaPorPieza, int DepartamentoId, bool AGranel);

    /// <summary>Lo que se devuelve de un artículo del ticket, y cuánto de eso regresa al inventario (lo dañado no).</summary>
    private sealed record Devuelto(EnElTicket Articulo, decimal Cantidad, decimal Monto, decimal ARegresar)
    {
        public decimal Ganancia => Math.Round(Cantidad * Articulo.GananciaPorPieza, 4, MidpointRounding.AwayFromZero);

        /// <summary>Lo que baja de <c>NUMERO_ARTICULOS</c>: las piezas; lo de granel no, aunque se devuelva todo.</summary>
        public int Piezas => Articulo.AGranel ? 0 : (int)Math.Round(Cantidad, MidpointRounding.AwayFromZero);
    }

    private static async Task<DevolucionEnEleventa> EscribirDevolucionAsync(FbConnection c, CajaEnEleventa caja, DevolucionParaEleventa d, CancellationToken ct)
    {
        var devuelta = new DateTime(d.DevueltaLocal.Ticks - d.DevueltaLocal.Ticks % 1000);
        var enSegundos = new DateTime(d.DevueltaLocal.Ticks - d.DevueltaLocal.Ticks % TimeSpan.TicksPerSecond);

        await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Write | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        }, ct);
        var sql = new Sql(c, tx, ct);
        await RevisarHuellaAsync(c, tx, caja, ct);

        if (await sql.ValorAsync("SELECT ID FROM DEVOLUCIONES WHERE TICKET_ID = @ticket AND DEVUELTO_EN = @devuelta",
                [("ticket", d.TicketEleventa), ("devuelta", devuelta)]) is { } ya)
        {
            await tx.CommitAsync(ct);
            return new DevolucionEnEleventa(Convert.ToInt32(ya), YaEstaba: true);
        }

        var ticket = await sql.FilaAsync("SELECT FOLIO, PAGADO_EN, ESTA_CANCELADO, CAJA_ID, TURNO_ID FROM VENTATICKETS WHERE ID = @ticket",
                [("ticket", d.TicketEleventa)])
            ?? throw new ChoqueConEleventa($"En eleventa ya no está el ticket {d.TicketEleventa}.");
        if (ticket[2] is string cancelado && cancelado.Trim() is "t" or "T")
            throw new ChoqueConEleventa($"En eleventa el ticket #{ticket[0]} ya está cancelado.", MotivoPorRevisar.EleventaYaLoTenia);
        var folio = Convert.ToInt32(ticket[0]);

        var articulos = await ArticulosDelTicketAsync(sql, d.TicketEleventa);
        var devueltos = d.EsCancelacion ? await TodoAsync(sql, articulos, d.Articulos) : await LoDevueltoAsync(sql, articulos, d.Articulos);
        // Lo que devuelve todo lo que le queda al ticket va como cancelación, como lo hace la tienda en eleventa: allá nunca queda un
        // ticket vendido en cero y sin artículos (2026-10-24; decidido, por revisar). Con una parcial antes, es la cancelación de lo
        // que quedó, como la de eleventa. Sale lo que salió de la caja 2.
        var cancela = d.EsCancelacion || DejaElTicketSinNada(articulos, devueltos);
        var total = devueltos.Sum(x => x.Monto);
        var ganancia = devueltos.Sum(x => x.Ganancia);
        var piezas = devueltos.Sum(x => x.Piezas);

        var deEleventa = await TurnoParaAsync(sql, caja, devuelta, d.Turno);
        var turno = deEleventa.Id;
        // En la copia y en vivo, eleventa devolvió en el mismo turno y en la misma caja en que se cobró. Lo de otra caja o de
        // otro turno todavía no se ha visto cómo lo anota (docs/pruebas-en-tienda.md): mientras, queda por revisar.
        if (Convert.ToInt32(ticket[3]) != caja.CajaId || ticket[4] is DBNull || Convert.ToInt32(ticket[4]) != turno)
            throw new ChoqueConEleventa(DeOtroTurnoODeOtraCaja, MotivoPorRevisar.AnotarAMano);
        var dia = await DiaAsync(sql, caja.CajaId, devuelta.Date);

        if (cancela)
        {
            // Eleventa le resta lo devuelto al subtotal y a la ganancia (quedan en cero), pero no al total.
            await sql.EjecutarAsync("""
                UPDATE VENTATICKETS SET ESTA_CANCELADO = 't', SUBTOTAL = SUBTOTAL - @total, GANANCIA = 0, NUMERO_ARTICULOS = NUMERO_ARTICULOS - @piezas
                WHERE ID = @ticket
                """,
                [("ticket", d.TicketEleventa), ("total", (double)total), ("piezas", piezas)]);
        }
        else
        {
            foreach (var x in devueltos)
            {
                // Eleventa no deja artículos en cero: si se devolvió todo, el renglón se va.
                if (x.Articulo.Cantidad - x.Cantidad <= 0.0005m)
                    await sql.EjecutarAsync("DELETE FROM VENTATICKETS_ARTICULOS WHERE ID = @id", [("id", x.Articulo.Id)]);
                else
                    await sql.EjecutarAsync("UPDATE VENTATICKETS_ARTICULOS SET CANTIDAD = CANTIDAD - @cantidad, TOTAL_ARTICULO = TOTAL_ARTICULO - @monto WHERE ID = @id",
                        [("id", x.Articulo.Id), ("cantidad", (double)x.Cantidad), ("monto", (double)x.Monto)]);
            }
            await sql.EjecutarAsync("""
                UPDATE VENTATICKETS SET TOTAL = TOTAL - @total, SUBTOTAL = SUBTOTAL - @total, TOTAL_FACTURABLE = TOTAL_FACTURABLE - @total,
                    GANANCIA = GANANCIA - @ganancia, NUMERO_ARTICULOS = NUMERO_ARTICULOS - @piezas
                WHERE ID = @ticket
                """,
                [("ticket", d.TicketEleventa), ("total", (double)total), ("ganancia", (double)ganancia), ("piezas", piezas)]);
        }

        var id = await sql.NuevoIdAsync("GEN_DEVOLUCIONES_ID");
        await sql.EjecutarAsync("""
            INSERT INTO DEVOLUCIONES (ID, TURNO_ID, TICKET_ID, TIPO_DEVOLUCION, PAGADO_EN, DEVUELTO_EN, CAJERO, CAJA, TOTAL_DEVUELTO)
            VALUES (@id, @turno, @ticket, @tipo, @pagado, @devuelta, @cajero, @caja, @total)
            """,
            [("id", id), ("turno", turno), ("ticket", d.TicketEleventa), ("tipo", cancela ? "C" : "P"), ("pagado", ticket[1] is DBNull ? null : ticket[1]),
             ("devuelta", devuelta), ("total", (double)total),
             ("cajero", Recortar(((string?)await sql.ValorAsync("SELECT USUARIO FROM USUARIOS WHERE ID = @id", [("id", caja.CajeroId)]) ?? "").Trim(), 50)),
             ("caja", Recortar(((string?)await sql.ValorAsync("SELECT NOMBRE FROM CAJAS WHERE ID = @id", [("id", caja.CajaId)]) ?? "").Trim(), 50))]);

        foreach (var x in devueltos)
        {
            if (!cancela)
                await sql.EjecutarAsync("""
                    INSERT INTO DEVOLUCIONES_ARTICULOS (ID, DEVOLUCION_ID, TICKET_ID, CODIGO_PRODUCTO, DESCRIPCION_PRODUCTO, CANTIDAD_DEVUELTA, DINERO_DEVUELTO,
                        INVENTARIO_POST_DEVOLUCION)
                    VALUES (@id, @devolucion, @ticket, @codigo, @nombre, @cantidad, @monto, 0)
                    """,
                    [("id", await sql.NuevoIdAsync("GEN_DEVOLUCIONES_ARTICULOS_ID")), ("devolucion", id), ("ticket", d.TicketEleventa),
                     ("codigo", x.Articulo.Codigo), ("nombre", x.Articulo.Nombre), ("cantidad", (double)x.Cantidad), ("monto", (double)x.Monto)]);

            await sql.EjecutarAsync("""
                INSERT INTO CORTE_MOVIMIENTOS (ID, CUANDO_FUE, MONTO, DESCRIPCION, TIPO, ID_TURNO, FORMA_PAGO, RAZON_DEVOLUCION, ID_CAJA)
                VALUES (@id, @cuando, @monto, @descripcion, 'Devolucion', @turno, 'E', 'V', @caja)
                """,
                [("id", await sql.NuevoIdAsync("GEN_CORTE_MOVIMIENTOS_ID")), ("cuando", devuelta), ("monto", x.Monto),
                 ("descripcion", Recortar($"Devolución \"{x.Articulo.Nombre}\", Ticket #{folio}", 255)), ("turno", turno), ("caja", caja.CajaId)]);

            if (x.ARegresar > 0)
                await RegresarAlInventarioAsync(sql, x, folio, d.TicketEleventa, enSegundos, caja);
        }

        // Lo devuelto sale de la caja en este turno: baja lo vendido y su ganancia, y sube lo devuelto en efectivo.
        foreach (var (tabla, renglon) in new[] { ("TURNOS", turno), ("CORTE_OPERACIONES", dia) })
            await sql.EjecutarAsync($"""
                UPDATE {tabla} SET ACUMULADO_VENTAS = ACUMULADO_VENTAS - @total, ACUMULADO_GANANCIA = ACUMULADO_GANANCIA - @ganancia,
                    DEVOLUCIONES_VENTAS_EFECTIVO = DEVOLUCIONES_VENTAS_EFECTIVO + @total, NUMERO_VENTAS = NUMERO_VENTAS - @ventas
                WHERE ID = @id
                """,
                [("id", renglon), ("total", total), ("ganancia", ganancia), ("ventas", cancela ? 1 : 0)]);
        foreach (var depto in devueltos.GroupBy(x => x.Articulo.DepartamentoId))
        {
            var (ventas, ganancias) = (-depto.Sum(x => x.Monto), -depto.Sum(x => x.Ganancia));
            await SumarPorDepartamentoAsync(sql, "CORTE_VENTAS_POR_DEPTO", "ID_TURNO", turno, depto.Key, ventas, ganancias);
            await SumarPorDepartamentoAsync(sql, "CORTE_VENTAS_DEPTO_OPERACIONES", "ID_OPERACION", dia, depto.Key, ventas, ganancias);
        }

        SigueEsperando(caja);
        await tx.CommitAsync(ct);
        return new DevolucionEnEleventa(id, YaEstaba: false, deEleventa);
    }

    private static async Task<List<EnElTicket>> ArticulosDelTicketAsync(Sql sql, int ticket)
    {
        // En las líneas viejas no hay PRECIO_FINAL ni TOTAL_ARTICULO: el precio está en PRECIO_USADO (como al importar).
        var filas = await sql.FilasAsync("""
            SELECT a.ID, a.PRODUCTO_CODIGO, a.PRODUCTO_NOMBRE, a.CANTIDAD, COALESCE(a.PRECIO_FINAL, a.PRECIO_USADO), a.TOTAL_ARTICULO, a.GANANCIA,
                a.DEPARTAMENTO_ID, (SELECT MAX(p.TVENTA) FROM PRODUCTOS p WHERE p.CODIGO = a.PRODUCTO_CODIGO)
            FROM VENTATICKETS_ARTICULOS a WHERE a.TICKET_ID = @ticket ORDER BY a.ID
            """, [("ticket", ticket)]);
        return filas.Select(f =>
        {
            var cantidad = (decimal)Convert.ToSingle(f[3]);
            // En las líneas viejas el total va vacío: es precio por cantidad (sin precio, cero: no truena).
            var total = f[5] is DBNull ? (f[4] is DBNull ? 0 : (decimal)Convert.ToSingle(f[4])) * cantidad : (decimal)Convert.ToSingle(f[5]);
            return new EnElTicket(Convert.ToInt32(f[0]), ((string)f[1]).Trim(), ((string)f[2]).Trim(), cantidad, total,
                f[6] is DBNull ? 0 : (decimal)Convert.ToSingle(f[6]), f[7] is DBNull ? 0 : Convert.ToInt32(f[7]),
                // Con el tipo de venta que el producto tiene hoy, como eleventa; un común cuenta por piezas.
                AGranel: f[8] is string tipo && tipo.Trim() == "D");
        }).ToList();
    }

    /// <summary>La cancelación devuelve todo lo que queda en el ticket de eleventa; lo que volvió dañado no regresa al inventario.</summary>
    private static async Task<List<Devuelto>> TodoAsync(Sql sql, List<EnElTicket> articulos, IReadOnlyList<ArticuloDevuelto> pedidos)
    {
        var danados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pedidos.Where(p => !p.RegresaAlInventario && p.ProductoId is not null))
        {
            if (await CodigoAsync(sql, p.ProductoId!.Value) is { } codigo)
                danados.Add(codigo);
        }
        return articulos.Select(a => new Devuelto(a, a.Cantidad, a.Total, ARegresar: EsComun(a) || danados.Contains(a.Codigo) ? 0 : a.Cantidad)).ToList();
    }

    /// <summary>Cada artículo devuelto, buscado en el ticket de eleventa por su código (o, si fue común, por su nombre).</summary>
    private static async Task<List<Devuelto>> LoDevueltoAsync(Sql sql, List<EnElTicket> articulos, IReadOnlyList<ArticuloDevuelto> pedidos)
    {
        var devueltos = new List<Devuelto>();
        var usado = new Dictionary<int, decimal>();
        foreach (var p in pedidos)
        {
            var codigo = p.ProductoId is { } productoId ? await CodigoAsync(sql, productoId) : string.IsNullOrWhiteSpace(p.Codigo) ? null : p.Codigo.Trim();
            bool Alcanza(EnElTicket a) => a.Cantidad - usado.GetValueOrDefault(a.Id) >= p.Cantidad - 0.0005m;
            // Por su renglón si se sabe (un ticket que se leyó en eleventa); si no, por su código, o si en la venta pasó como
            // común (eleventa ya no lo tenía), por su nombre.
            var articulo = (p.ArticuloEleventa is { } renglon ? articulos.FirstOrDefault(a => a.Id == renglon && Alcanza(a)) : null)
                ?? (p.ArticuloEleventa is null && codigo is not null ? articulos.FirstOrDefault(a => string.Equals(a.Codigo, codigo, StringComparison.OrdinalIgnoreCase) && Alcanza(a)) : null)
                ?? (p.ArticuloEleventa is null ? articulos.FirstOrDefault(a => EsComun(a) && string.Equals(a.Nombre, p.Nombre.Trim(), StringComparison.OrdinalIgnoreCase) && Alcanza(a)) : null)
                ?? throw new ChoqueConEleventa($"En el ticket de eleventa ya no está «{p.Nombre}» con {p.Cantidad} por devolver.",
                    MotivoPorRevisar.EleventaYaLoTenia);
            usado[articulo.Id] = usado.GetValueOrDefault(articulo.Id) + p.Cantidad;
            // La de un ticket de eleventa (por su renglón) no se pasa de lo que le queda a ese renglón, y si se lo lleva todo,
            // es exacto lo que le queda: si no, el ticket de eleventa quedaría abajo de cero o con un total sin artículos. Si
            // eleventa cambió desde que se devolvió aquí, no se escribe (revisión del 2026-10-17).
            if (p.ArticuloEleventa is not null)
            {
                var queda = articulo.Total - devueltos.Where(x => x.Articulo.Id == articulo.Id).Sum(x => x.Monto);
                if (p.Importe > queda + 0.005m || articulo.Cantidad - usado[articulo.Id] <= 0.0005m && Math.Abs(p.Importe - queda) > 0.005m)
                    throw new ChoqueConEleventa($"En el ticket de eleventa a «{articulo.Nombre}» le quedan {queda:0.00} y aquí se devolvieron {p.Importe:0.00}: "
                        + "cambió desde que se devolvió aquí. Anótala a mano en eleventa.", MotivoPorRevisar.AnotarAMano);
            }
            devueltos.Add(new Devuelto(articulo, p.Cantidad, p.Importe, p.RegresaAlInventario && !EsComun(articulo) ? p.Cantidad : 0));
        }
        // Dos devoluciones del mismo artículo en una: se juntan, como eleventa lo tiene en un solo renglón.
        return devueltos.GroupBy(x => x.Articulo.Id)
            .Select(g => new Devuelto(g.First().Articulo, g.Sum(x => x.Cantidad), g.Sum(x => x.Monto), g.Sum(x => x.ARegresar)))
            .ToList();
    }

    private static bool EsComun(EnElTicket a) => a.Codigo.StartsWith("COMUN-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Si con esto se devuelve todo lo que le queda al ticket: cada artículo, completo.</summary>
    private static bool DejaElTicketSinNada(List<EnElTicket> articulos, List<Devuelto> devueltos) =>
        articulos.Count > 0 && articulos.All(a => devueltos.Where(x => x.Articulo.Id == a.Id).Sum(x => x.Cantidad) >= a.Cantidad - 0.0005m);

    /// <summary>Por qué la devolución de un ticket de otra caja o de otro turno de eleventa queda por revisar.</summary>
    public const string DeOtroTurnoODeOtraCaja =
        "El ticket es de otra caja o de otro turno de eleventa: todavía no se ha visto cómo anota eleventa esa devolución. "
        + "Anótala a mano en eleventa.";

    private static async Task<string?> CodigoAsync(Sql sql, int productoId) =>
        await sql.ValorAsync("SELECT CODIGO FROM PRODUCTOS WHERE ID = @id", [("id", productoId)]) is string codigo ? codigo.Trim() : null;

    /// <summary>La entrada al inventario: «Devolución de venta #folio», con la cantidad en negativo, y la existencia que sube.</summary>
    private static async Task RegresarAlInventarioAsync(Sql sql, Devuelto x, int folio, int ticket, DateTime cuando, CajaEnEleventa caja)
    {
        if (await sql.FilaAsync("SELECT ID, PCOSTO FROM PRODUCTOS WHERE CODIGO = @codigo", [("codigo", x.Articulo.Codigo)]) is not { } producto)
            return;
        var productoId = Convert.ToInt32(producto[0]);
        var costo = producto[1] is DBNull ? 0 : (decimal)Convert.ToSingle(producto[1]);
        var antes = await sql.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = @p", [("p", productoId)]);
        if (antes is null)
            await sql.EjecutarAsync("INSERT INTO INVENTARIO_BALANCES (PRODUCTO_ID, CANTIDAD_ACTUAL, ALMACEN_ID) VALUES (@p, @c, 1)",
                [("p", productoId), ("c", (double)x.ARegresar)]);
        else
            await sql.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = CANTIDAD_ACTUAL + @c WHERE PRODUCTO_ID = @p",
                [("p", productoId), ("c", (double)x.ARegresar)]);

        await sql.EjecutarAsync("""
            INSERT INTO INVENTARIO_HISTORIAL (PRODUCTO_ID, CUANDO_FUE, CANTIDAD_ANTERIOR, CANTIDAD, DESCRIPCION, COSTO_UNITARIO, COSTO_DESPUES,
                VENTA_ID, CAJA_ID, VENTA_POR_KIT, USUARIO_ID, ALMACEN_ID)
            VALUES (@p, @cuando, @antes, @cantidad, @descripcion, @costo, @costo, @ticket, @caja, 'f', @cajero, 1)
            """,
            [("p", productoId), ("cuando", cuando), ("antes", antes is null ? 0 : Convert.ToDouble(antes)), ("cantidad", -(double)x.ARegresar),
             ("descripcion", $"Devolución de venta #{folio}"), ("costo", (double)costo), ("ticket", ticket), ("caja", caja.CajaId), ("cajero", caja.CajeroId)]);
    }

    private static async Task<CierreEnEleventa> EscribirCierreAsync(FbConnection c, CajaEnEleventa caja, DateTime cerradoLocal, decimal diferencia, CancellationToken ct)
    {
        var cerrado = new DateTime(cerradoLocal.Ticks - cerradoLocal.Ticks % 10000);
        await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Write | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        }, ct);
        var sql = new Sql(c, tx, ct);
        await RevisarHuellaAsync(c, tx, caja, ct);

        if (await TurnoDeLaCajaAsync(sql, caja.CajaId) is not { } turno)
        {
            await tx.CommitAsync(ct);
            return new CierreEnEleventa(null, YaEstaba: true);
        }

        // El ticket que eleventa tiene abierto mientras hay turno se borra al cerrar (con lo que tuviera escaneado).
        const string abiertos = "SELECT ID FROM VENTATICKETS WHERE TURNO_ID = @turno AND ESTA_ABIERTO = 't'";
        await sql.EjecutarAsync($"DELETE FROM INVENTARIO_APARTADO WHERE TICKET_ID IN ({abiertos})", [("turno", turno)]);
        await sql.EjecutarAsync($"DELETE FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID IN ({abiertos})", [("turno", turno)]);
        await sql.EjecutarAsync("DELETE FROM VENTATICKETS WHERE TURNO_ID = @turno AND ESTA_ABIERTO = 't'", [("turno", turno)]);

        if (diferencia != 0)
        {
            var quien = await sql.FilaAsync("SELECT u.NOMBRE_COMPLETO, u.USUARIO, t.INICIO_EN FROM TURNOS t LEFT JOIN USUARIOS u ON u.ID = t.ID_CAJERO WHERE t.ID = @turno",
                [("turno", turno)]);
            var nombre = quien?[0] is string completo && completo.Trim().Length > 0 ? completo.Trim() : (quien?[1] as string)?.Trim() ?? "";
            var inicio = quien?[2] is DateTime abierto ? abierto : cerrado;
            var falto = diferencia < 0;
            await AnotarEntradaOSalidaAsync(sql, caja.CajaId, turno, esEntrada: !falto, Math.Abs(diferencia),
                $"Efectivo {(falto ? "faltante" : "sobrante")} al cerrar turno de {nombre} del {inicio:dd/MM/yyyy}", cerrado);
        }

        await sql.EjecutarAsync("""
            UPDATE TURNOS SET TERMINO_EN = @cerrado,
                EFECTIVO_AL_CIERRE = DINERO_INICIAL + VENTAS_EFECTIVO - DEVOLUCIONES_VENTAS_EFECTIVO + COALESCE(ACUMULADO_ENTRADAS, 0) - COALESCE(ACUMULADO_SALIDAS, 0)
            WHERE ID = @turno
            """,
            [("turno", turno), ("cerrado", cerrado)]);

        SigueEsperando(caja);
        await tx.CommitAsync(ct);
        return new CierreEnEleventa(turno, YaEstaba: false);
    }
}
