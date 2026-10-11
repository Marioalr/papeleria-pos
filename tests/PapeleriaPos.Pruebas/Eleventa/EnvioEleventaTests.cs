using PapeleriaPos.Eleventa;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// La venta de la caja nueva escrita en eleventa como la escribe una caja de eleventa al cobrar
/// (docs/migracion-eleventa.md, «Cómo escribe eleventa una venta»). Corren contra un Firebird de prueba local.
/// </summary>
public class EnvioEleventaTests
{
    private static readonly CajaEnEleventa Caja = new(CajaId: 4, CajeroId: 7, Ip: "10.0.0.20");
    private static readonly DateTime Abierto = new(2026, 3, 9, 8, 0, 0);
    private static readonly TurnoParaEleventa Turno = new(Abierto, DineroInicial: 500);

    private static async Task<EleventaDePrueba> TiendaAsync()
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (1, 'Principal', '10.0.0.10')");
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        foreach (var (id, nombre) in new[] { (5, "Papelería"), (9, EnvioEleventa.DepartamentoComunes), (14, "Impresiones") })
            await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (@id, @nombre)", ("id", id), ("nombre", nombre));
        await e.ProductoAsync(10, "CUAD-PRO", "Cuaderno profesional", depto: 5, costo: 18.3017, precio: 30, existencia: 12);
        await e.ProductoAsync(11, "IMP-BN", "Impresiones", depto: 14, costo: 2, precio: 10, existencia: 500, tipoVenta: 'D');
        // Una venta anterior de la caja principal: el folio sigue de ahí.
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN) VALUES (41, 1, 1, -1, 'f', @cuando)",
            ("cuando", Abierto.AddMinutes(-30)));
        return e;
    }

    private static VentaParaEleventa Venta(DateTime cobrada, params ArticuloParaEleventa[] articulos) =>
        new(cobrada.AddMinutes(-2).AddTicks(1234567), cobrada, PagoCon: 100, Turno, articulos);

    private static double Doble(object? valor) => Convert.ToDouble(valor);

    [FactConFirebird]
    public async Task Sin_turno_lo_abre_como_eleventa_y_la_venta_deja_ticket_articulos_inventario_y_acumulados()
    {
        await using var e = await TiendaAsync();
        var cobrada = new DateTime(2026, 3, 9, 10, 15, 42, 600);

        var r = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cobrada,
            new(10, "Cuaderno", 2, 60), new(11, "Impresiones", 0.3m, 3), new(null, "Recuerdo", 1, 15)));

        Assert.Equal((42, false, false), (r.Folio, r.YaEstaba, r.FolioRepetido));

        // El turno se abrió como lo abre eleventa: la caja anota su IP y la hora, el renglón del día y el primer ticket abierto.
        var caja = (await e.FilasAsync("SELECT ULTIMA_IP, ULTIMO_INGRESO FROM CAJAS WHERE ID = 4")).Single();
        Assert.Equal(("10.0.0.20", Abierto), ((string)caja["ULTIMA_IP"]!, (DateTime)caja["ULTIMO_INGRESO"]!));
        var turno = (await e.FilasAsync("SELECT * FROM TURNOS")).Single();
        var dia = (await e.FilasAsync("SELECT * FROM CORTE_OPERACIONES")).Single();
        Assert.Equal((4, 7, Abierto, 500m, "F"), (Convert.ToInt32(turno["ID_CAJA"]), Convert.ToInt32(turno["ID_CAJERO"]),
            (DateTime)turno["INICIO_EN"]!, (decimal)turno["DINERO_INICIAL"]!, (string)turno["SOSPECHOSO"]!));
        Assert.Equal(dia["ID"], turno["ID_OPERACION_INICIO"]);
        Assert.Equal((Abierto.Date, 4), ((DateTime)dia["DIA"]!, Convert.ToInt32(dia["ID_CAJA"])));
        var abierto = (await e.FilasAsync("SELECT * FROM VENTATICKETS WHERE ESTA_ABIERTO = 't'")).Single();
        Assert.Equal(("Ticket 1", -1, turno["ID"]), ((string)abierto["NOMBRE"]!, Convert.ToInt32(abierto["OPERACION_ID"]), abierto["TURNO_ID"]));
        Assert.Null(abierto["FOLIO"]);

        // El ticket, ya cobrado: la hora del cobro en segundos y la ganancia con el costo de eleventa.
        var t = (await e.FilasAsync("SELECT * FROM VENTATICKETS WHERE ID = @id", ("id", r.TicketId))).Single();
        var segundos = new DateTime(2026, 3, 9, 10, 15, 42);
        Assert.Equal((42, 4, 7, "f", segundos, segundos, "e", 4, -1), (Convert.ToInt32(t["FOLIO"]), Convert.ToInt32(t["CAJA_ID"]),
            Convert.ToInt32(t["CAJERO_ID"]), (string)t["ESTA_ABIERTO"]!, (DateTime)t["VENDIDO_EN"]!, (DateTime)t["PAGADO_EN"]!,
            ((string)t["FORMA_PAGO"]!).Trim(), Convert.ToInt32(t["NUMERO_ARTICULOS"]), Convert.ToInt32(t["OPERACION_ID"])));
        Assert.Equal(turno["ID"], t["TURNO_ID"]);
        Assert.Equal(78, Doble(t["TOTAL"]), 3);
        Assert.Equal(78, Doble(t["SUBTOTAL"]), 3);
        Assert.Equal(78, Doble(t["TOTAL_FACTURABLE"]), 3);
        Assert.Equal(100, Doble(t["PAGO_CON"]), 3);
        // 2 × (30 − 18.3017) + 0.3 × (10 − 2) + 15 del común, que no tiene costo: sin truncar la cuarta cifra.
        Assert.Equal(40.7966, Doble(t["GANANCIA"]), 3);

        // Un renglón por artículo, con el nombre y el departamento de eleventa; la ganancia es por pieza.
        var articulos = (await e.FilasAsync("SELECT * FROM VENTATICKETS_ARTICULOS ORDER BY ID")).ToList();
        Assert.Equal(["CUAD-PRO", "IMP-BN"], articulos.Take(2).Select(a => (string)a["PRODUCTO_CODIGO"]!));
        Assert.Equal(["Cuaderno profesional", "Impresiones", "Recuerdo"], articulos.Select(a => (string)a["PRODUCTO_NOMBRE"]!));
        Assert.Equal([5, 14, 9], articulos.Select(a => Convert.ToInt32(a["DEPARTAMENTO_ID"])));
        Assert.Equal([11.698, 8, 15], articulos.Select(a => Math.Round(Doble(a["GANANCIA"]), 3)));
        Assert.Equal([30d, 10, 15], articulos.Select(a => Math.Round(Doble(a["PRECIO_FINAL"]), 3)));
        Assert.Equal([60d, 3, 15], articulos.Select(a => Math.Round(Doble(a["TOTAL_ARTICULO"]), 3)));
        Assert.All(articulos, a => Assert.Equal((r.TicketId, 1000000, segundos, ","), (Convert.ToInt32(a["TICKET_ID"]),
            Convert.ToInt32(a["PORCENTAJE_PAGADO"]), (DateTime)a["PAGADO_EN"]!, (string)a["IMPUESTOS_USADOS"]!)));
        // El común lleva el código que arma eleventa: COMUN-<al azar>-<el contador antes de su renglón>.
        var comun = articulos[2];
        Assert.Matches($@"^COMUN-\d{{1,4}}-{Convert.ToInt32(comun["ID"]) - 1}$", (string)comun["PRODUCTO_CODIGO"]!);

        // El inventario: la existencia baja y el kardex dice «Venta #42»; el común no lleva inventario.
        Assert.Equal(10, Doble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);
        Assert.Equal(499.7, Doble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 11")), 3);
        var kardex = await e.FilasAsync("SELECT * FROM INVENTARIO_HISTORIAL ORDER BY PRODUCTO_ID");
        Assert.Equal([10, 11], kardex.Select(k => Convert.ToInt32(k["PRODUCTO_ID"])));
        Assert.Equal([12d, 500], kardex.Select(k => Math.Round(Doble(k["CANTIDAD_ANTERIOR"]), 3)));
        Assert.Equal([2d, 0.3], kardex.Select(k => Math.Round(Doble(k["CANTIDAD"]), 3)));
        Assert.Equal([18.302, 2], kardex.Select(k => Math.Round(Doble(k["COSTO_UNITARIO"]), 3)));
        Assert.All(kardex, k => Assert.Equal(("Venta #42", r.TicketId, 4, 7, "f", segundos), ((string)k["DESCRIPCION"]!,
            Convert.ToInt32(k["VENTA_ID"]), Convert.ToInt32(k["CAJA_ID"]), Convert.ToInt32(k["USUARIO_ID"]), (string)k["VENTA_POR_KIT"]!,
            (DateTime)k["CUANDO_FUE"]!)));

        // Los acumulados: el turno, el día y cada departamento, por turno y por día.
        foreach (var tabla in new[] { "TURNOS", "CORTE_OPERACIONES" })
        {
            var a = (await e.FilasAsync($"SELECT ACUMULADO_VENTAS, ACUMULADO_GANANCIA, VENTAS_EFECTIVO, NUMERO_VENTAS FROM {tabla}")).Single();
            Assert.Equal((78m, 40.7966m, 78m, 1), ((decimal)a["ACUMULADO_VENTAS"]!, (decimal)a["ACUMULADO_GANANCIA"]!,
                (decimal)a["VENTAS_EFECTIVO"]!, Convert.ToInt32(a["NUMERO_VENTAS"])));
        }
        foreach (var (tabla, columna, id) in new[] { ("CORTE_VENTAS_POR_DEPTO", "ID_TURNO", turno["ID"]), ("CORTE_VENTAS_DEPTO_OPERACIONES", "ID_OPERACION", dia["ID"]) })
        {
            var deptos = await e.FilasAsync($"SELECT * FROM {tabla} ORDER BY ID_DEPARTAMENTO");
            Assert.All(deptos, d => Assert.Equal(id, d[columna]));
            Assert.Equal([5, 9, 14], deptos.Select(d => Convert.ToInt32(d["ID_DEPARTAMENTO"])));
            Assert.Equal([60d, 15, 3], deptos.Select(d => Math.Round(Doble(d["ACUMULADO_VENTAS"]), 3)));
            Assert.Equal([23.3966m, 15m, 2.4m], deptos.Select(d => (decimal)d["ACUMULADO_GANANCIAS"]!));
        }
    }

    [FactConFirebird]
    public async Task Mandarla_otra_vez_no_la_duplica()
    {
        await using var e = await TiendaAsync();
        var venta = Venta(new DateTime(2026, 3, 9, 11, 0, 0), new ArticuloParaEleventa(10, "Cuaderno", 1, 30));

        var primera = await EnvioEleventa.MandarAsync(e.Conexion, Caja, venta);
        var segunda = await EnvioEleventa.MandarAsync(e.Conexion, Caja, venta);

        Assert.Equal((primera.TicketId, primera.Folio, true), (segunda.TicketId, segunda.Folio, segunda.YaEstaba));
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE CAJA_ID = 4 AND ESTA_ABIERTO = 'f'")));
        Assert.Equal(11, Doble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT NUMERO_VENTAS FROM TURNOS")));
    }

    [FactConFirebird]
    public async Task Con_turno_abierto_entra_a_ese_y_despues_de_medianoche_suma_al_dia_siguiente()
    {
        await using var e = await TiendaAsync();
        var noche = new DateTime(2026, 3, 9, 23, 50, 0);

        var antes = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(noche, new ArticuloParaEleventa(10, "Cuaderno", 1, 30)));
        var despues = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(noche.AddMinutes(20), new ArticuloParaEleventa(10, "Cuaderno", 2, 60)));

        Assert.Equal(antes.Folio + 1, despues.Folio);
        var turno = (await e.FilasAsync("SELECT ID, ACUMULADO_VENTAS, NUMERO_VENTAS FROM TURNOS")).Single();
        Assert.Equal((90m, 2), ((decimal)turno["ACUMULADO_VENTAS"]!, Convert.ToInt32(turno["NUMERO_VENTAS"])));
        var dias = await e.FilasAsync("SELECT DIA, ACUMULADO_VENTAS, NUMERO_VENTAS FROM CORTE_OPERACIONES ORDER BY DIA");
        Assert.Equal([(noche.Date, 30m, 1), (noche.Date.AddDays(1), 60m, 1)],
            dias.Select(d => ((DateTime)d["DIA"]!, (decimal)d["ACUMULADO_VENTAS"]!, Convert.ToInt32(d["NUMERO_VENTAS"]))));
        // El departamento del turno se suma en el mismo renglón; el del día, uno por día.
        var porTurno = (await e.FilasAsync("SELECT ACUMULADO_VENTAS FROM CORTE_VENTAS_POR_DEPTO")).Single();
        Assert.Equal(90, Doble(porTurno["ACUMULADO_VENTAS"]), 3);
        Assert.Equal(2, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM CORTE_VENTAS_DEPTO_OPERACIONES")));
        // Sigue habiendo un solo ticket abierto, el del turno.
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE ESTA_ABIERTO = 't'")));
    }

    [FactConFirebird]
    public async Task Un_producto_en_dos_lineas_va_en_una_y_lo_que_eleventa_borro_va_como_comun()
    {
        await using var e = await TiendaAsync();
        await e.ProductoAsync(12, "VIEJO", "Producto viejo", depto: 5, costo: 5, precio: 9, existencia: 3);
        await e.EjecutarAsync("UPDATE PRODUCTOS SET ELIMINADO_EN = @cuando WHERE ID = 12", ("cuando", Abierto.AddDays(-1)));

        var r = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(new DateTime(2026, 3, 9, 12, 0, 0),
            new(10, "Cuaderno", 1, 30), new(12, "Producto viejo", 1, 9), new(10, "Cuaderno", 2, 54)));

        var articulos = await e.FilasAsync("SELECT * FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t ORDER BY ID", ("t", r.TicketId));
        Assert.Equal(2, articulos.Count);
        var cuaderno = articulos[0];
        Assert.Equal(("CUAD-PRO", 3d, 84d, 28d), ((string)cuaderno["PRODUCTO_CODIGO"]!, Math.Round(Doble(cuaderno["CANTIDAD"]), 3),
            Math.Round(Doble(cuaderno["TOTAL_ARTICULO"]), 3), Math.Round(Doble(cuaderno["PRECIO_FINAL"]), 3)));
        Assert.StartsWith("COMUN-", (string)articulos[1]["PRODUCTO_CODIGO"]!);
        Assert.Equal(("Producto viejo", 9), ((string)articulos[1]["PRODUCTO_NOMBRE"]!, Convert.ToInt32(articulos[1]["DEPARTAMENTO_ID"])));
        // Un solo renglón de kardex para el cuaderno; el borrado no se toca.
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_HISTORIAL")));
        Assert.Equal(3, Doble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 12")), 3);
    }
}
