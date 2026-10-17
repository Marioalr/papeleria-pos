using System.Text.RegularExpressions;
using FirebirdSql.Data.FirebirdClient;

namespace PapeleriaPos.Eleventa;

/// <summary>
/// La caja de eleventa como la que vende la caja nueva (<c>CAJAS.ID</c>) y su cajero (<c>USUARIOS.ID</c>). Son datos
/// de la tienda: van en la configuración de esa PC, nunca en el repositorio.
/// </summary>
/// <param name="Ip">La IP de esta PC, para <c>CAJAS.ULTIMA_IP</c> al abrir el turno, como lo anota eleventa.</param>
/// <param name="AbreTurno">
/// Si eleventa no tiene turno abierto para esa caja, el puente lo abre con el turno de la caja nueva. Falso en el piloto
/// (<see cref="CorteEnElPiloto"/>): el turno lo abre eleventa con su fondo y lo cierra su corte; sin turno, lo que se
/// manda espera (<see cref="CorteEnElPiloto.AbreLaCajaEnEleventa"/>). Solo para convivir con eleventa: se quita al dejarlo.
/// </param>
public sealed record CajaEnEleventa(int CajaId, int CajeroId, string? Ip = null, bool AbreTurno = true)
{
    /// <summary>
    /// Lo último que escribió el puente: antes de escribir, en la misma transacción, se revisa que siga en eleventa; si no,
    /// eleventa se restauró a un respaldo anterior y no se escribe (<see cref="EleventaRestaurada"/>). Vacía, no se revisa.
    /// Solo del piloto (<see cref="RestauracionDeEleventa"/>).
    /// </summary>
    public HuellaDelPuente? Huella { get; init; }
}

/// <summary>Una venta cobrada en efectivo, como la entiende eleventa.</summary>
/// <param name="CreadaLocal">Cuándo se empezó en la caja nueva (hora local). También es su marca en eleventa: así no se manda dos veces.</param>
/// <param name="CobradaLocal">Cuándo se cobró (hora local).</param>
/// <param name="PagoCon">El efectivo que dio el cliente.</param>
/// <param name="Turno">El turno de la caja nueva, por si en eleventa no hay uno abierto y hay que abrirlo.</param>
public sealed record VentaParaEleventa(
    DateTime CreadaLocal, DateTime CobradaLocal, decimal PagoCon, TurnoParaEleventa Turno, IReadOnlyList<ArticuloParaEleventa> Articulos)
{
    public decimal Total => Articulos.Sum(a => a.Importe);
}

public sealed record TurnoParaEleventa(DateTime AbiertoLocal, decimal DineroInicial);

/// <param name="ProductoId"><c>PRODUCTOS.ID</c> de eleventa; vacío si eleventa no lo tiene: entonces va como artículo común.</param>
/// <param name="Nombre">Para el artículo común; los de eleventa llevan su propio nombre.</param>
/// <param name="Cantidad">En la unidad de eleventa (las mismas que se importaron: piezas, metros, kilos).</param>
/// <param name="Importe">Lo que se cobró por la línea, con IVA.</param>
public sealed record ArticuloParaEleventa(int? ProductoId, string Nombre, decimal Cantidad, decimal Importe);

/// <param name="YaEstaba">Ya se había mandado (por ejemplo, se cortó la red justo al confirmar): no se escribió otra vez.</param>
/// <param name="FolioRepetido">
/// Otra caja de eleventa cobró con el mismo folio en el mismo instante y, después de tomar el siguiente unas veces, sigue
/// chocando. Eleventa también lo permite; queda para revisar.
/// </param>
/// <param name="Turno">El turno de eleventa donde quedó (vacío si ya estaba): el turno de la caja nueva lo adopta.</param>
/// <param name="FolioAnterior">El folio que tenía antes de tomar el siguiente porque otra caja lo usó al mismo tiempo.</param>
public sealed record VentaEnEleventa(int TicketId, int Folio, bool YaEstaba, bool FolioRepetido, TurnoDeEleventa? Turno = null, int? FolioAnterior = null);

/// <summary>Un turno de eleventa (<c>TURNOS.ID</c>) y cuándo empezó (hora de la tienda): juntos dicen que es el mismo turno.</summary>
public sealed record TurnoDeEleventa(int Id, DateTime InicioLocal);

/// <summary>
/// Eleventa ya tiene otra cosa: su turno ya se cortó, hay dos abiertos para la caja, el ticket ya está cancelado… No es la
/// red y reintentar no lo arregla: lo que choca queda por revisar y lo demás de la bandeja sigue (docs/migracion-eleventa.md,
/// «Lo que falta para el piloto»). Solo del piloto: se quita al dejar eleventa.
/// </summary>
public sealed class ChoqueConEleventa(string mensaje) : InvalidOperationException(mensaje);

/// <summary>Cómo quedó el folio de un ticket después de revisar que otra caja no lo usó al mismo tiempo.</summary>
/// <param name="Anterior">El que tenía antes de tomar el siguiente; vacío si no se cambió.</param>
public sealed record FolioRevisado(int Folio, bool Repetido, int? Anterior);

/// <summary>
/// Escribe en eleventa una venta de la caja nueva como la escribe una caja de eleventa al cobrar
/// (docs/migracion-eleventa.md, «Cómo escribe eleventa una venta»): el ticket con su folio, sus artículos, la salida de
/// inventario y los acumulados del turno, del día y del departamento. Todo en una sola transacción: entra completa o
/// no entra. La caja nueva escanea contra su propia base, así que no deja los apartados que eleventa pone al escanear
/// (al cobrar ya no queda ninguno).
/// </summary>
public static partial class EnvioEleventa
{
    /// <summary>El departamento donde eleventa pone sus artículos comunes.</summary>
    public const string DepartamentoComunes = "Productos Comunes";

    // Lo que Firebird dice cuando otra caja tiene apartado el mismo renglón (la misma existencia, por ejemplo).
    private static readonly HashSet<int> Choques = [335544345 /* lock_conflict */, 335544336 /* deadlock */, 335544451 /* update_conflict */];

    public static async Task<VentaEnEleventa> MandarAsync(ServidorEleventa servidor, CajaEnEleventa caja, VentaParaEleventa venta, CancellationToken ct = default)
    {
        await using var conexion = new FbConnection(servidor.Cadena());
        await conexion.OpenAsync(ct);
        return await MandarAsync(conexion, caja, venta, ct);
    }

    /// <summary>
    /// Si choca con otra caja que escribe lo mismo en ese instante, lo vuelve a intentar desde el principio unas
    /// veces; si sigue chocando, el error sale y la venta se queda en la bandeja para la siguiente vuelta.
    /// </summary>
    public static async Task<VentaEnEleventa> MandarAsync(FbConnection c, CajaEnEleventa caja, VentaParaEleventa venta, CancellationToken ct = default)
    {
        if (venta.Articulos.Count == 0)
            throw new ArgumentException("La venta no tiene artículos.", nameof(venta));

        for (var intento = 1; ; intento++)
        {
            try
            {
                return await EscribirAsync(c, caja, venta, ct);
            }
            catch (FbException ex) when (intento < 5 && ex.Errors.Any(e => Choques.Contains(e.Number)))
            {
                await Task.Delay(200 * intento, ct);
            }
        }
    }

    private static async Task<VentaEnEleventa> EscribirAsync(FbConnection c, CajaEnEleventa caja, VentaParaEleventa venta, CancellationToken ct)
    {
        // Firebird guarda la hora en diezmilésimas de segundo; eleventa, la del cobro en segundos.
        var creada = new DateTime(venta.CreadaLocal.Ticks - venta.CreadaLocal.Ticks % 1000);
        var cobrada = new DateTime(venta.CobradaLocal.Ticks - venta.CobradaLocal.Ticks % TimeSpan.TicksPerSecond);

        await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Write | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        }, ct);
        var sql = new Sql(c, tx, ct);
        await RevisarHuellaAsync(c, tx, caja, ct);

        if (await sql.FilaAsync("SELECT ID, FOLIO FROM VENTATICKETS WHERE CAJA_ID = @caja AND CREADO_EN = @creada AND ESTA_ABIERTO = 'f'",
                [("caja", caja.CajaId), ("creada", creada)]) is { } ya)
        {
            await tx.CommitAsync(ct);
            // Si ya estaba es porque aquí no se supo (se cortó la red al confirmar): su folio todavía no se imprimió y se
            // revisa como al guardar.
            var yaRevisado = await RevisarFolioAsync(c, Convert.ToInt32(ya[0]), Convert.ToInt32(ya[1]), ct);
            return new VentaEnEleventa(Convert.ToInt32(ya[0]), yaRevisado.Folio, YaEstaba: true, yaRevisado.Repetido, FolioAnterior: yaRevisado.Anterior);
        }

        var deEleventa = await TurnoParaAsync(sql, caja, cobrada, venta.Turno);
        var turno = deEleventa.Id;
        var dia = await DiaAsync(sql, caja.CajaId, cobrada.Date);
        var articulos = await ArmarAsync(sql, venta.Articulos);

        var total = articulos.Sum(a => a.Importe);
        var ganancia = articulos.Sum(a => a.Ganancia);
        var folio = Convert.ToInt32(await sql.ValorAsync("SELECT MAX(FOLIO) FROM VENTATICKETS") ?? 0) + 1;
        var nombre = $"Ticket {Convert.ToInt32(await sql.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE TURNO_ID = @turno", [("turno", turno)])) + 1}";
        var ticket = await sql.NuevoIdAsync("GEN_VENTATICKETS_ID");

        await sql.EjecutarAsync("""
            INSERT INTO VENTATICKETS (ID, FOLIO, CAJA_ID, CAJERO_ID, NOMBRE, CREADO_EN, SUBTOTAL, IMPUESTOS, TOTAL, GANANCIA,
                ESTA_ABIERTO, VENDIDO_EN, PAGO_CON, NUMERO_ARTICULOS, PAGADO_EN, OPERACION_ID, NOTAS, FORMA_PAGO, REFERENCIA,
                TURNO_ID, NOTAS_AL_PIE, TOTAL_FACTURABLE, IMPRIMIR_DATOS_CLIENTE)
            VALUES (@id, @folio, @caja, @cajero, @nombre, @creada, @total, 0, @total, @ganancia,
                'f', @cobrada, @pagoCon, @numero, @cobrada, -1, @vacio, 'e', '',
                @turno, @vacio, @total, 'f')
            """,
            [("id", ticket), ("folio", folio), ("caja", caja.CajaId), ("cajero", caja.CajeroId), ("nombre", nombre), ("creada", creada),
             ("total", (double)total), ("ganancia", (double)ganancia), ("cobrada", cobrada), ("pagoCon", (double)venta.PagoCon),
             ("numero", articulos.Sum(a => a.Piezas)), ("vacio", Array.Empty<byte>()), ("turno", turno)]);

        foreach (var a in articulos)
        {
            var id = await sql.NuevoIdAsync("GEN_VENTATICKETS_ARTICULOS_ID");
            // Así arma eleventa el código de un artículo común: un número al azar y el contador antes de este renglón.
            var codigo = a.Codigo ?? $"COMUN-{Random.Shared.Next(1, 10000)}-{id - 1}";
            await sql.EjecutarAsync("""
                INSERT INTO VENTATICKETS_ARTICULOS (ID, TICKET_ID, PRODUCTO_CODIGO, PRODUCTO_NOMBRE, CANTIDAD, GANANCIA, DEPARTAMENTO_ID,
                    PAGADO_EN, USA_MAYOREO, PORCENTAJE_DESCUENTO, IMPUESTOS_USADOS, IMPUESTO_UNITARIO, PRECIO_USADO, CANTIDAD_DEVUELTA,
                    FUE_DEVUELTO, PORCENTAJE_PAGADO, PRECIO_FINAL, AGREGADO_EN, TOTAL_ARTICULO)
                VALUES (@id, @ticket, @codigo, @nombre, @cantidad, @ganancia, @depto,
                    @cobrada, 'f', 0, ',', 0, @precio, 0,
                    'f', 1000000, @precio, @cobrada, @importe)
                """,
                [("id", id), ("ticket", ticket), ("codigo", codigo), ("nombre", Recortar(a.Nombre, 255)), ("cantidad", (double)a.Cantidad),
                 ("ganancia", (double)a.GananciaPorPieza), ("depto", a.DepartamentoId), ("cobrada", cobrada), ("precio", (double)a.Precio),
                 ("importe", (double)a.Importe)]);
        }

        foreach (var a in articulos.Where(a => a.ProductoId is not null))
            await SacarDeInventarioAsync(sql, a, folio, ticket, cobrada, caja);

        await SumarAsync(sql, "TURNOS", "ID = @id", turno, total, ganancia);
        await SumarAsync(sql, "CORTE_OPERACIONES", "ID = @id", dia, total, ganancia);
        foreach (var depto in articulos.GroupBy(a => a.DepartamentoId))
        {
            var (ventas, ganancias) = (depto.Sum(a => a.Importe), depto.Sum(a => a.Ganancia));
            await SumarPorDepartamentoAsync(sql, "CORTE_VENTAS_POR_DEPTO", "ID_TURNO", turno, depto.Key, ventas, ganancias);
            await SumarPorDepartamentoAsync(sql, "CORTE_VENTAS_DEPTO_OPERACIONES", "ID_OPERACION", dia, depto.Key, ventas, ganancias);
        }

        await tx.CommitAsync(ct);

        // Eleventa toma el folio como el último más uno, sin candado: si otra caja cobró en el mismo instante, se repite. Este
        // ticket todavía no se entrega, así que es él quien toma el siguiente.
        var revisado = await RevisarFolioAsync(c, ticket, folio, ct);
        return new VentaEnEleventa(ticket, revisado.Folio, YaEstaba: false, revisado.Repetido, deEleventa, revisado.Anterior);
    }

    /// <summary>
    /// Justo después de guardar (docs/migracion-eleventa.md, «Folios repetidos»): si otro ticket ya tiene ese folio (otra
    /// caja cobró casi al mismo tiempo y eleventa no lo impide), este todavía no se entregó con él (el cobro lo imprime
    /// después; si la venta va en la vuelta, salió con el folio de la caja). Toma el siguiente y corrige solo lo suyo: el
    /// folio del ticket y su kardex («Venta #folio»). El otro ticket no se toca. Si después de unas vueltas sigue chocando,
    /// se queda como está y se avisa.
    /// </summary>
    public static async Task<FolioRevisado> RevisarFolioAsync(FbConnection c, int ticket, int folio, CancellationToken ct = default)
    {
        int? anterior = null;
        for (var vuelta = 0; ; vuelta++)
        {
            await using (var revision = await c.BeginTransactionAsync(new FbTransactionOptions
            {
                TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.ReadCommitted | FbTransactionBehavior.RecVersion | FbTransactionBehavior.NoWait,
            }, ct))
            {
                var otros = Convert.ToInt32(await new Sql(c, revision, ct).ValorAsync(
                    "SELECT COUNT(*) FROM VENTATICKETS WHERE FOLIO = @folio AND ID <> @ticket", [("folio", folio), ("ticket", ticket)]));
                await revision.CommitAsync(ct);
                if (otros == 0)
                    return new FolioRevisado(folio, Repetido: false, anterior);
            }
            if (vuelta == 3)
                return new FolioRevisado(folio, Repetido: true, anterior);
            var deAntes = folio;
            try
            {
                folio = await ConReintentosAsync(() => CambiarFolioAsync(c, ticket, deAntes, ct), ct);
            }
            catch (FbException ex) when (ex.Errors.Any(e => Choques.Contains(e.Number)))
            {
                // Otra caja escribe lo mismo una y otra vez: la venta ya está en eleventa; se queda con su folio y se avisa.
                return new FolioRevisado(folio, Repetido: true, anterior);
            }
            anterior ??= deAntes;
        }
    }

    private static async Task<int> CambiarFolioAsync(FbConnection c, int ticket, int folio, CancellationToken ct)
    {
        await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Write | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        }, ct);
        var sql = new Sql(c, tx, ct);
        var nuevo = Convert.ToInt32(await sql.ValorAsync("SELECT MAX(FOLIO) FROM VENTATICKETS") ?? 0) + 1;
        await sql.EjecutarAsync("UPDATE VENTATICKETS SET FOLIO = @nuevo WHERE ID = @ticket", [("nuevo", nuevo), ("ticket", ticket)]);
        await sql.EjecutarAsync("UPDATE INVENTARIO_HISTORIAL SET DESCRIPCION = @nueva WHERE VENTA_ID = @ticket AND DESCRIPCION = @vieja",
            [("nueva", $"Venta #{nuevo}"), ("vieja", $"Venta #{folio}"), ("ticket", ticket)]);
        await tx.CommitAsync(ct);
        return nuevo;
    }

    /// <summary>
    /// El turno de eleventa al que va lo que pasó a esa hora en la caja nueva (docs/migracion-eleventa.md, «Eleventa
    /// abierto en la caja 2»): el de la caja que estaba abierto entonces o, si no había ninguno, el siguiente que se abrió.
    /// Es el primero que terminó después (o sigue abierto), y su corte es el que cuenta ese efectivo:
    /// <list type="bullet">
    /// <item>si ya se cortó, lo que pasó antes del corte ya se contó allá y no se manda: queda por revisar
    /// (<see cref="ChoqueConEleventa"/>);</item>
    /// <item>si hay dos turnos abiertos para la caja, no se sabe cuál: también queda por revisar. La base de eleventa no lo
    /// deja (<c>UNQ_TURNO_ABIERTO_CAJA</c>), pero por si una no lo trae. El puente nunca abre un segundo turno;</item>
    /// <item>si no hay ninguno, en el piloto (<see cref="CajaEnEleventa.AbreTurno"/>) no se escribe nada y lo que se manda
    /// espera a que eleventa lo abra; fuera del piloto (las herramientas que escriben en una copia) se abre como lo abre
    /// eleventa.</item>
    /// </list>
    /// Así, lo que se cobró después del corte de eleventa y antes de que la caja nueva lo viera entra al turno siguiente.
    /// </summary>
    private static async Task<TurnoDeEleventa> TurnoParaAsync(Sql sql, CajaEnEleventa caja, DateTime cuando, TurnoParaEleventa nuevo)
    {
        var turnos = await sql.FilasAsync(
            "SELECT ID, INICIO_EN, TERMINO_EN FROM TURNOS WHERE ID_CAJA = @caja AND (TERMINO_EN IS NULL OR TERMINO_EN > @cuando) ORDER BY ID",
            [("caja", caja.CajaId), ("cuando", cuando)]);
        if (turnos.Count == 0)
            return caja.AbreTurno ? await AbrirTurnoAsync(sql, caja, nuevo) : throw new InvalidOperationException(CorteEnElPiloto.AbreLaCajaEnEleventa);
        if (turnos[0][2] is DateTime cortado)
            throw new ChoqueConEleventa(CorteEnElPiloto.YaSeContoEnEleventa(cortado));
        if (turnos.Count(t => t[2] is DBNull) > 1)
            throw new ChoqueConEleventa(CorteEnElPiloto.DosTurnosAbiertos);
        return new TurnoDeEleventa(Convert.ToInt32(turnos[0][0]), (DateTime)turnos[0][1]);
    }

    /// <summary>
    /// Sin turno abierto para la caja (y fuera del piloto), se abre como lo abre eleventa: la hora en <c>CAJAS</c>, el
    /// renglón del día, el turno en ceros y su primer ticket abierto, que eleventa siempre tiene mientras hay turno.
    /// </summary>
    private static async Task<TurnoDeEleventa> AbrirTurnoAsync(Sql sql, CajaEnEleventa caja, TurnoParaEleventa nuevo)
    {
        var inicio = new DateTime(nuevo.AbiertoLocal.Ticks - nuevo.AbiertoLocal.Ticks % 10000);
        await sql.EjecutarAsync(caja.Ip is null
                ? "UPDATE CAJAS SET ULTIMO_INGRESO = @ahora WHERE ID = @caja"
                : "UPDATE CAJAS SET ULTIMO_INGRESO = @ahora, ULTIMA_IP = @ip WHERE ID = @caja",
            [("ahora", inicio), ("caja", caja.CajaId), ("ip", caja.Ip)]);
        var dia = await DiaAsync(sql, caja.CajaId, inicio.Date);
        var turno = await sql.NuevoIdAsync("GEN_TURNOS_ID");
        await sql.EjecutarAsync($"""
            INSERT INTO TURNOS (ID, ID_CAJA, ID_OPERACION_INICIO, ID_CAJERO, INICIO_EN, DINERO_INICIAL, SOSPECHOSO, {string.Join(", ", ColumnasTurno)})
            VALUES (@id, @caja, @dia, @cajero, @inicio, @dinero, 'F', {string.Join(", ", ColumnasTurno.Select(_ => "0"))})
            """,
            [("id", turno), ("caja", caja.CajaId), ("dia", dia), ("cajero", caja.CajeroId), ("inicio", inicio), ("dinero", nuevo.DineroInicial)]);
        await sql.EjecutarAsync("""
            INSERT INTO VENTATICKETS (CAJA_ID, CAJERO_ID, NOMBRE, CREADO_EN, OPERACION_ID, TURNO_ID)
            VALUES (@caja, @cajero, 'Ticket 1', @inicio, -1, @turno)
            """,
            [("caja", caja.CajaId), ("cajero", caja.CajeroId), ("inicio", inicio), ("turno", turno)]);
        return new TurnoDeEleventa(turno, inicio);
    }

    /// <summary>
    /// El turno abierto de la caja (la base no deja dos: <c>UNQ_TURNO_ABIERTO_CAJA</c>); si una base no lo trajera, el más
    /// reciente. Solo para cerrarlo fuera del piloto.
    /// </summary>
    private static async Task<int?> TurnoDeLaCajaAsync(Sql sql, int caja) =>
        await sql.ValorAsync("SELECT FIRST 1 ID FROM TURNOS WHERE ID_CAJA = @caja AND TERMINO_EN IS NULL ORDER BY ID DESC", [("caja", caja)]) is { } id
            ? Convert.ToInt32(id)
            : null;

    /// <summary>El renglón del día y la caja en <c>CORTE_OPERACIONES</c>: lo vendido se suma al día en que se cobró.</summary>
    private static async Task<int> DiaAsync(Sql sql, int caja, DateTime dia)
    {
        if (await sql.ValorAsync("SELECT ID FROM CORTE_OPERACIONES WHERE DIA = @dia AND ID_CAJA = @caja", [("dia", dia), ("caja", caja)]) is { } id)
            return Convert.ToInt32(id);

        var nuevo = await sql.NuevoIdAsync("GEN_CORTE_OPERACIONES_ID");
        await sql.EjecutarAsync($"""
            INSERT INTO CORTE_OPERACIONES (ID, DIA, ID_CAJA, {string.Join(", ", ColumnasDia)})
            VALUES (@id, @dia, @caja, {string.Join(", ", ColumnasDia.Select(_ => "0"))})
            """,
            [("id", nuevo), ("dia", dia), ("caja", caja)]);
        return nuevo;
    }

    private sealed record Articulo(
        int? ProductoId, string? Codigo, string Nombre, int DepartamentoId, decimal Cantidad, decimal Importe, decimal Costo, bool AGranel)
    {
        public decimal Precio => Importe / Cantidad;
        public decimal GananciaPorPieza => Precio - Costo;
        /// <summary>A diezmilésimas, como los acumulados del corte (NUMERIC(18,4)), que si no la truncan.</summary>
        public decimal Ganancia => Math.Round(Importe - Cantidad * Costo, 4, MidpointRounding.AwayFromZero);

        /// <summary>Lo que eleventa cuenta como artículos del ticket: las piezas, y uno por lo vendido a granel.</summary>
        public int Piezas => AGranel || !ReglasEleventa.EsEntero((double)Cantidad) ? 1 : (int)Math.Round(Cantidad);
    }

    /// <summary>
    /// Lo que eleventa necesita de cada producto lo toma de eleventa mismo (su código, nombre, departamento y costo de
    /// hoy). Eleventa no repite un producto en el ticket: si la caja nueva lo tiene en dos líneas, se juntan. Lo que
    /// eleventa no tiene (o ya borró) va como artículo común, sin costo ni inventario, como lo hace eleventa.
    /// </summary>
    private static async Task<List<Articulo>> ArmarAsync(Sql sql, IReadOnlyList<ArticuloParaEleventa> articulos)
    {
        int? comunes = null;
        var armados = new List<Articulo>();
        // Los comunes no se juntan entre sí: cada uno es su propia línea.
        foreach (var grupo in articulos.Select((a, i) => (Articulo: a, Llave: a.ProductoId ?? -1 - i)).GroupBy(x => x.Llave, x => x.Articulo))
        {
            var (cantidad, importe) = (grupo.Sum(a => a.Cantidad), grupo.Sum(a => a.Importe));
            if (cantidad <= 0)
                throw new ArgumentException($"«{grupo.First().Nombre}» no tiene cantidad.", nameof(articulos));

            var producto = grupo.First().ProductoId is { } id
                ? await sql.FilaAsync("SELECT CODIGO, DESCRIPCION, DEPT, PCOSTO, TVENTA FROM PRODUCTOS WHERE ID = @id AND ELIMINADO_EN IS NULL", [("id", id)])
                : null;
            if (producto is not null)
            {
                armados.Add(new Articulo((int)grupo.First().ProductoId!, ((string)producto[0]).Trim(), ((string)producto[1]).Trim(),
                    // PCOSTO es FLOAT: leído como tal da 3.6984 y no 3.69840002059937.
                    Convert.ToInt32(producto[2]), cantidad, importe, (decimal)Convert.ToSingle(producto[3] is DBNull ? 0 : producto[3]),
                    producto[4] is string tv && tv.Trim() == "D"));
                continue;
            }

            comunes ??= Convert.ToInt32(await sql.ValorAsync("SELECT FIRST 1 ID FROM DEPARTAMENTOS WHERE NOMBRE = @nombre ORDER BY ID",
                [("nombre", DepartamentoComunes)]) ?? throw new InvalidOperationException($"En eleventa no está el departamento «{DepartamentoComunes}»."));
            armados.Add(new Articulo(null, null, grupo.First().Nombre, comunes.Value, cantidad, importe, 0, AGranel: false));
        }
        return armados;
    }

    /// <summary>La salida de inventario: un renglón en el kardex («Venta #folio») y la existencia que baja.</summary>
    private static async Task SacarDeInventarioAsync(Sql sql, Articulo a, int folio, int ticket, DateTime cobrada, CajaEnEleventa caja)
    {
        var antes = await sql.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = @p", [("p", a.ProductoId)]);
        if (antes is null)
            await sql.EjecutarAsync("INSERT INTO INVENTARIO_BALANCES (PRODUCTO_ID, CANTIDAD_ACTUAL, ALMACEN_ID) VALUES (@p, @c, 1)",
                [("p", a.ProductoId), ("c", -(double)a.Cantidad)]);
        else
            await sql.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = CANTIDAD_ACTUAL - @c WHERE PRODUCTO_ID = @p",
                [("p", a.ProductoId), ("c", (double)a.Cantidad)]);

        await sql.EjecutarAsync("""
            INSERT INTO INVENTARIO_HISTORIAL (PRODUCTO_ID, CUANDO_FUE, CANTIDAD_ANTERIOR, CANTIDAD, DESCRIPCION, COSTO_UNITARIO, COSTO_DESPUES,
                VENTA_ID, CAJA_ID, VENTA_POR_KIT, USUARIO_ID, ALMACEN_ID)
            VALUES (@p, @cuando, @antes, @cantidad, @descripcion, @costo, @costo, @ticket, @caja, 'f', @cajero, 1)
            """,
            [("p", a.ProductoId), ("cuando", cobrada), ("antes", antes is DBNull or null ? 0 : Convert.ToDouble(antes)), ("cantidad", (double)a.Cantidad),
             ("descripcion", $"Venta #{folio}"), ("costo", (double)a.Costo), ("ticket", ticket), ("caja", caja.CajaId), ("cajero", caja.CajeroId)]);
    }

    /// <summary>Eleventa suma cada venta al turno y al día en cuanto se cobra, no al cerrar.</summary>
    private static Task SumarAsync(Sql sql, string tabla, string donde, int id, decimal total, decimal ganancia) =>
        sql.EjecutarAsync($"""
            UPDATE {tabla} SET ACUMULADO_VENTAS = ACUMULADO_VENTAS + @total, ACUMULADO_GANANCIA = ACUMULADO_GANANCIA + @ganancia,
                VENTAS_EFECTIVO = VENTAS_EFECTIVO + @total, NUMERO_VENTAS = NUMERO_VENTAS + 1
            WHERE {donde}
            """,
            [("id", id), ("total", total), ("ganancia", ganancia)]);

    /// <summary>Lo de cada departamento, por turno y por día; el renglón se crea con la primera venta de ese departamento.</summary>
    private static async Task SumarPorDepartamentoAsync(Sql sql, string tabla, string columna, int id, int depto, decimal ventas, decimal ganancias)
    {
        var cambiados = await sql.EjecutarAsync($"""
            UPDATE {tabla} SET ACUMULADO_VENTAS = ACUMULADO_VENTAS + @ventas, ACUMULADO_GANANCIAS = ACUMULADO_GANANCIAS + @ganancias
            WHERE {columna} = @id AND ID_DEPARTAMENTO = @depto
            """,
            [("id", id), ("depto", depto), ("ventas", (double)ventas), ("ganancias", ganancias)]);
        if (cambiados == 0)
            await sql.EjecutarAsync($"INSERT INTO {tabla} (ID_DEPARTAMENTO, ACUMULADO_VENTAS, {columna}, ACUMULADO_GANANCIAS) VALUES (@depto, @ventas, @id, @ganancias)",
                [("id", id), ("depto", depto), ("ventas", (double)ventas), ("ganancias", ganancias)]);
    }

    private static string Recortar(string texto, int largo) => texto.Length <= largo ? texto : texto[..largo];

    /// <summary>Los acumulados que eleventa deja en cero al abrir un turno (los de tarjeta, vales, crédito y devoluciones no se usan en la tienda).</summary>
    private static readonly string[] ColumnasTurno =
    [
        "ACUMULADO_VENTAS", "ACUMULADO_ENTRADAS", "ACUMULADO_SALIDAS", "ACUMULADO_GANANCIA", "VENTAS_EFECTIVO", "VENTAS_TARJETA",
        "VENTAS_VALES", "VENTAS_CREDITO", "EFECTIVO_AL_CIERRE", "ABONOS_EFECTIVO", "DEVOLUCIONES_VENTAS_EFECTIVO",
        "DEVOLUCIONES_VENTAS_CREDITO", "DEVOLUCIONES_ABONOS_EFECTIVO", "NUMERO_VENTAS", "DEVOLUCIONES_VENTAS_VALES", "ABONOS_TARJETA",
        "ABONOS_VALES", "COMISIONES_TARJETA", "DEVOLUCIONES_VENTAS_TARJETA", "VENTAS_TRANSFERENCIA", "ABONOS_TRANSFERENCIA",
        "VENTAS_CHEQUE", "ABONOS_CHEQUE", "DEVOLUCIONES_VENTAS_TRANS", "DEVOLUCIONES_VENTAS_CHEQUE",
    ];

    /// <summary>Los del día y la caja: los mismos, menos el efectivo al cierre.</summary>
    private static readonly string[] ColumnasDia = [.. ColumnasTurno.Where(c => c != "EFECTIVO_AL_CIERRE")];

    /// <summary>
    /// Antes de escribir, en la misma transacción: si eleventa ya no tiene lo último que escribió el puente (se restauró un
    /// respaldo), no se escribe nada (<see cref="EleventaRestaurada"/>).
    /// </summary>
    private static async Task RevisarHuellaAsync(FbConnection c, FbTransaction tx, CajaEnEleventa caja, CancellationToken ct)
    {
        if (caja.Huella is { } huella && await RestauracionDeEleventa.RevisarAsync(c, tx, huella, ct) is { } restauracion)
            throw new EleventaRestaurada(restauracion);
    }

    private sealed class Sql(FbConnection c, FbTransaction tx, CancellationToken ct)
    {
        public async Task<int> EjecutarAsync(string texto, (string Nombre, object? Valor)[]? parametros = null)
        {
            await using var comando = Comando(texto, parametros);
            return await comando.ExecuteNonQueryAsync(ct);
        }

        public async Task<object?> ValorAsync(string texto, (string Nombre, object? Valor)[]? parametros = null)
        {
            await using var comando = Comando(texto, parametros);
            return await comando.ExecuteScalarAsync(ct) is { } valor and not DBNull ? valor : null;
        }

        public async Task<object[]?> FilaAsync(string texto, (string Nombre, object? Valor)[] parametros)
        {
            await using var comando = Comando(texto, parametros);
            await using var lector = await comando.ExecuteReaderAsync(ct);
            if (!await lector.ReadAsync(ct))
                return null;
            var fila = new object[lector.FieldCount];
            lector.GetValues(fila);
            return fila;
        }

        public async Task<List<object[]>> FilasAsync(string texto, (string Nombre, object? Valor)[] parametros)
        {
            await using var comando = Comando(texto, parametros);
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

        /// <summary>El ID se toma del contador antes de insertar, como lo haría el trigger: así se sabe sin RETURNING.</summary>
        public async Task<int> NuevoIdAsync(string contador) =>
            Convert.ToInt32(await ValorAsync($"SELECT GEN_ID({contador}, 1) FROM RDB$DATABASE"));

        private FbCommand Comando(string texto, (string Nombre, object? Valor)[]? parametros)
        {
            var comando = new FbCommand(texto, c, tx);
            foreach (var (nombre, valor) in parametros ?? [])
            {
                if (Regex.IsMatch(texto, $@"@{nombre}\b"))
                    comando.Parameters.AddWithValue(nombre, valor ?? DBNull.Value);
            }
            return comando;
        }
    }
}
