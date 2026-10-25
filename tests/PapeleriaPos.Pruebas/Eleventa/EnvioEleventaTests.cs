using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo.Ventas;

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
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO) VALUES (7, 'Ana Ruiz', 'cajera2')");
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
    public async Task En_el_piloto_no_abre_el_turno_espera_a_que_lo_abra_eleventa_y_entra_al_suyo()
    {
        // El piloto (CorteEnElPiloto): el turno lo abre y lo cierra eleventa. El de la caja nueva (Turno: 9 de marzo, $500)
        // no se cierra cada día; si el puente abriera con él, eleventa tendría los del primer día del piloto.
        var piloto = Caja with { AbreTurno = false };
        await using var e = await TiendaAsync();
        var manana = new DateTime(2026, 3, 12, 9, 0, 0);
        var cuaderno = new ArticuloParaEleventa(10, "Cuaderno", 1, 30);

        // Antes de que abran la caja en eleventa: no escribe nada y la venta espera en la bandeja.
        var sinTurno = await Assert.ThrowsAsync<InvalidOperationException>(() => EnvioEleventa.MandarAsync(e.Conexion, piloto, Venta(manana, cuaderno)));
        Assert.Equal(CorteEnElPiloto.AbreLaCajaEnEleventa, sinTurno.Message);
        Assert.Equal((0, 0), (Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM TURNOS")),
            Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE CAJA_ID = 4"))));
        Assert.Equal("10.0.0.19", (string)(await e.ValorAsync("SELECT ULTIMA_IP FROM CAJAS WHERE ID = 4"))!);

        // Eleventa abre la caja con su hora y su fondo: la venta entra a ese turno, que no cambia.
        var apertura = new DateTime(2026, 3, 12, 8, 30, 0);
        var turno = await e.AbrirCajaAsync(4, 7, apertura, fondo: 300);
        var r = await EnvioEleventa.MandarAsync(e.Conexion, piloto, Venta(manana, cuaderno));
        var t = (await e.FilasAsync("SELECT * FROM TURNOS")).Single();
        Assert.Equal((turno, apertura, 300m, 30m), (Convert.ToInt32(t["ID"]), (DateTime)t["INICIO_EN"]!, (decimal)t["DINERO_INICIAL"]!,
            (decimal)t["ACUMULADO_VENTAS"]!));
        Assert.Equal(turno, Convert.ToInt32(await e.ValorAsync("SELECT TURNO_ID FROM VENTATICKETS WHERE ID = @id", ("id", r.TicketId))));

        // Después del corte en eleventa, lo que sigue (también una salida) espera otra vez a que la abran.
        await e.CortarAsync(turno, new DateTime(2026, 3, 12, 20, 0, 0));
        var salida = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EnvioEleventa.MoverEfectivoAsync(e.Conexion, piloto, Movimiento(false, 50, "Garrafón de agua", new DateTime(2026, 3, 12, 20, 30, 0))));
        Assert.Equal(CorteEnElPiloto.AbreLaCajaEnEleventa, salida.Message);
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM TURNOS")));
        Assert.Equal(0, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM CORTE_MOVIMIENTOS")));
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

    private static DevolucionParaEleventa Devolucion(int ticket, bool cancelacion, DateTime cuando, params ArticuloDevuelto[] articulos) =>
        new(ticket, cancelacion, cuando.AddTicks(4321), Turno, articulos);

    private static async Task<Dictionary<string, object?>> TurnoAsync(EleventaDePrueba e) => (await e.FilasAsync("SELECT * FROM TURNOS")).Single();

    [FactConFirebird]
    public async Task Cancelar_deja_el_ticket_cancelado_regresa_todo_y_lo_saca_del_turno()
    {
        await using var e = await TiendaAsync();
        var cobrada = new DateTime(2026, 3, 9, 10, 15, 42);
        var venta = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cobrada,
            new(10, "Cuaderno", 2, 60), new(11, "Impresiones", 0.3m, 3), new(null, "Recuerdo", 1, 15)));
        var cuando = cobrada.AddMinutes(5);

        var r = await EnvioEleventa.DevolverAsync(e.Conexion, Caja, Devolucion(venta.TicketId, cancelacion: true, cuando));
        var otraVez = await EnvioEleventa.DevolverAsync(e.Conexion, Caja, Devolucion(venta.TicketId, cancelacion: true, cuando));

        Assert.Equal((r.DevolucionId, true), (otraVez.DevolucionId, otraVez.YaEstaba));
        // El ticket conserva su total; queda cancelado, sin subtotal ni ganancia, y sus artículos no cambian. De sus 4 artículos
        // bajan las 3 piezas: las impresiones, a granel, siguen contando uno.
        var t = (await e.FilasAsync("SELECT * FROM VENTATICKETS WHERE ID = @id", ("id", venta.TicketId))).Single();
        Assert.Equal(("t", 1, 78d), ((string)t["ESTA_CANCELADO"]!, Convert.ToInt32(t["NUMERO_ARTICULOS"]), Math.Round(Doble(t["TOTAL"]), 3)));
        Assert.Equal((0d, 0d), (Math.Round(Doble(t["SUBTOTAL"]), 3), Math.Round(Doble(t["GANANCIA"]), 3)));
        Assert.Equal(3, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @id", ("id", venta.TicketId))));
        // La devolución «C», sin renglones de artículos, a nombre del cajero y la caja de eleventa.
        var d = (await e.FilasAsync("SELECT * FROM DEVOLUCIONES")).Single();
        Assert.Equal(("C", "cajera2", "Caja 2", 78d, cobrada), ((string)d["TIPO_DEVOLUCION"]!, (string)d["CAJERO"]!, (string)d["CAJA"]!,
            Math.Round(Doble(d["TOTAL_DEVUELTO"]), 3), (DateTime)d["PAGADO_EN"]!));
        Assert.Equal(0, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM DEVOLUCIONES_ARTICULOS")));
        // Un movimiento del corte por artículo.
        var movimientos = await e.FilasAsync("SELECT * FROM CORTE_MOVIMIENTOS ORDER BY ID");
        Assert.Equal([60m, 3m, 15m], movimientos.Select(m => (decimal)m["MONTO"]!));
        Assert.Equal("Devolución \"Cuaderno profesional\", Ticket #42", (string)movimientos[0]["DESCRIPCION"]!);
        Assert.All(movimientos, m => Assert.Equal(("Devolucion", "E", "V", 4), ((string)m["TIPO"]!, (string)m["FORMA_PAGO"]!,
            (string)m["RAZON_DEVOLUCION"]!, Convert.ToInt32(m["ID_CAJA"]))));
        // Lo vendido regresa al inventario; el común no lleva.
        Assert.Equal(12, Doble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);
        Assert.Equal(500, Doble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 11")), 3);
        var entradas = await e.FilasAsync("SELECT * FROM INVENTARIO_HISTORIAL WHERE DESCRIPCION = 'Devolución de venta #42' ORDER BY PRODUCTO_ID");
        Assert.Equal([-2d, -0.3], entradas.Select(k => Math.Round(Doble(k["CANTIDAD"]), 3)));
        Assert.Equal([10d, 499.7], entradas.Select(k => Math.Round(Doble(k["CANTIDAD_ANTERIOR"]), 3)));
        // El turno y el día: lo vendido y su ganancia bajan, lo devuelto sube, y la venta ya no cuenta.
        foreach (var tabla in new[] { "TURNOS", "CORTE_OPERACIONES" })
        {
            var a = (await e.FilasAsync($"SELECT * FROM {tabla}")).Single();
            Assert.Equal((0m, 0m, 78m, 78m, 0), ((decimal)a["ACUMULADO_VENTAS"]!, (decimal)a["ACUMULADO_GANANCIA"]!, (decimal)a["VENTAS_EFECTIVO"]!,
                (decimal)a["DEVOLUCIONES_VENTAS_EFECTIVO"]!, Convert.ToInt32(a["NUMERO_VENTAS"])));
        }
        Assert.All(await e.FilasAsync("SELECT * FROM CORTE_VENTAS_POR_DEPTO"), x => Assert.Equal(0m, (decimal)x["ACUMULADO_GANANCIAS"]!));
        Assert.All(await e.FilasAsync("SELECT * FROM CORTE_VENTAS_DEPTO_OPERACIONES"), x => Assert.Equal(0, Doble(x["ACUMULADO_VENTAS"]), 3));
    }

    [FactConFirebird]
    public async Task Devolver_una_parte_baja_el_articulo_borra_el_que_se_devolvio_completo_y_respeta_lo_danado()
    {
        await using var e = await TiendaAsync();
        var cobrada = new DateTime(2026, 3, 9, 11, 0, 0);
        var venta = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cobrada, new(10, "Cuaderno", 4, 120), new(null, "Recuerdo", 1, 15)));

        // Un cuaderno bueno, otro dañado y el recuerdo.
        await EnvioEleventa.DevolverAsync(e.Conexion, Caja, Devolucion(venta.TicketId, cancelacion: false, cobrada.AddMinutes(10),
            new(10, "Cuaderno", 1, 30), new(10, "Cuaderno", 1, 30, RegresaAlInventario: false), new(null, "Recuerdo", 1, 15)));

        var articulos = await e.FilasAsync("SELECT * FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @id", ("id", venta.TicketId));
        var cuaderno = Assert.Single(articulos);
        Assert.Equal((2d, 60d), (Math.Round(Doble(cuaderno["CANTIDAD"]), 3), Math.Round(Doble(cuaderno["TOTAL_ARTICULO"]), 3)));
        var t = (await e.FilasAsync("SELECT * FROM VENTATICKETS WHERE ID = @id", ("id", venta.TicketId))).Single();
        Assert.Equal(("f", 60d, 60d, 60d), ((string)t["ESTA_CANCELADO"]!, Math.Round(Doble(t["TOTAL"]), 3), Math.Round(Doble(t["SUBTOTAL"]), 3),
            Math.Round(Doble(t["TOTAL_FACTURABLE"]), 3)));
        // Eran 5 piezas y se devolvieron 3.
        Assert.Equal(2, Convert.ToInt32(t["NUMERO_ARTICULOS"]));
        // Quedan dos cuadernos que ganan 30 − 18.3017 cada uno.
        Assert.Equal(23.3966, Doble(t["GANANCIA"]), 3);

        var d = (await e.FilasAsync("SELECT * FROM DEVOLUCIONES")).Single();
        Assert.Equal(("P", 75d), ((string)d["TIPO_DEVOLUCION"]!, Math.Round(Doble(d["TOTAL_DEVUELTO"]), 3)));
        var renglones = await e.FilasAsync("SELECT * FROM DEVOLUCIONES_ARTICULOS ORDER BY ID");
        Assert.Equal(["Cuaderno profesional", "Recuerdo"], renglones.Select(x => (string)x["DESCRIPCION_PRODUCTO"]!));
        Assert.Equal([2d, 1d], renglones.Select(x => Math.Round(Doble(x["CANTIDAD_DEVUELTA"]), 3)));
        Assert.Equal([60d, 15d], renglones.Select(x => Math.Round(Doble(x["DINERO_DEVUELTO"]), 3)));
        Assert.Equal([60m, 15m], (await e.FilasAsync("SELECT MONTO FROM CORTE_MOVIMIENTOS ORDER BY ID")).Select(m => (decimal)m["MONTO"]!));
        // Solo el cuaderno bueno regresa: 12 − 4 + 1.
        Assert.Equal(9, Doble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);
        Assert.Equal(-1, Doble(await e.ValorAsync("SELECT CANTIDAD FROM INVENTARIO_HISTORIAL WHERE DESCRIPCION STARTING WITH 'Devoluci'")), 3);

        var turno = await TurnoAsync(e);
        Assert.Equal((60m, 135m, 75m, 1), ((decimal)turno["ACUMULADO_VENTAS"]!, (decimal)turno["VENTAS_EFECTIVO"]!,
            (decimal)turno["DEVOLUCIONES_VENTAS_EFECTIVO"]!, Convert.ToInt32(turno["NUMERO_VENTAS"])));
        Assert.Equal(23.3966m, (decimal)turno["ACUMULADO_GANANCIA"]!);
    }

    [FactConFirebird]
    public async Task Cancelar_despues_de_devolver_una_parte_cancela_lo_que_queda_y_lo_de_granel_sigue_contando_uno()
    {
        await using var e = await TiendaAsync();
        var cobrada = new DateTime(2026, 3, 9, 12, 0, 0);
        var venta = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cobrada, new(10, "Cuaderno", 1, 30), new(11, "Impresiones", 0.5m, 5)));

        await EnvioEleventa.DevolverAsync(e.Conexion, Caja, Devolucion(venta.TicketId, cancelacion: false, cobrada.AddMinutes(10), new ArticuloDevuelto(11, "Impresiones", 0.2m, 2)));
        var t = (await e.FilasAsync("SELECT * FROM VENTATICKETS WHERE ID = @id", ("id", venta.TicketId))).Single();
        // Las impresiones siguen en el ticket, y de todos modos lo de granel no baja NUMERO_ARTICULOS.
        Assert.Equal((33d, 33d, 2), (Math.Round(Doble(t["TOTAL"]), 3), Math.Round(Doble(t["SUBTOTAL"]), 3), Convert.ToInt32(t["NUMERO_ARTICULOS"])));

        await EnvioEleventa.DevolverAsync(e.Conexion, Caja, Devolucion(venta.TicketId, cancelacion: true, cobrada.AddMinutes(20)));

        // Se cancela lo que quedaba: el total se queda como estaba, el subtotal en cero y solo baja la pieza del cuaderno.
        t = (await e.FilasAsync("SELECT * FROM VENTATICKETS WHERE ID = @id", ("id", venta.TicketId))).Single();
        Assert.Equal(("t", 33d, 0d, 1), ((string)t["ESTA_CANCELADO"]!, Math.Round(Doble(t["TOTAL"]), 3), Math.Round(Doble(t["SUBTOTAL"]), 3),
            Convert.ToInt32(t["NUMERO_ARTICULOS"])));
        var cancelacion = (await e.FilasAsync("SELECT * FROM DEVOLUCIONES WHERE TIPO_DEVOLUCION = 'C'")).Single();
        Assert.Equal(33, Doble(cancelacion["TOTAL_DEVUELTO"]), 3);
        Assert.Equal(500, Doble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 11")), 3);
        var turno = await TurnoAsync(e);
        Assert.Equal((0m, 35m, 35m, 0), ((decimal)turno["ACUMULADO_VENTAS"]!, (decimal)turno["VENTAS_EFECTIVO"]!,
            (decimal)turno["DEVOLUCIONES_VENTAS_EFECTIVO"]!, Convert.ToInt32(turno["NUMERO_VENTAS"])));
    }

    /// <summary>
    /// Lo cobrado con una escala de mayoreo va como el F11 de eleventa (2026-10-24): el renglón con <c>USA_MAYOREO</c> y el ticket
    /// con lo ahorrado contra el menudeo en <c>TOTAL_AHORRADO</c>; lo demás, sin. Así lo lee la importación (para deducir el
    /// paquete del F11), y una devolución después no le cambia ni lo uno ni lo otro, como eleventa.
    /// </summary>
    [FactConFirebird]
    public async Task Lo_cobrado_con_mayoreo_va_como_el_F11_de_eleventa()
    {
        await using var e = await TiendaAsync();
        var cobrada = new DateTime(2026, 3, 9, 11, 0, 0);

        // Doce cuadernos a $27.50 (el menudeo es $30: se ahorraron $30) y unas impresiones a menudeo.
        var venta = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cobrada,
            new(10, "Cuaderno", 12, 330, ConMayoreo: true, AhorroMayoreo: 30), new(11, "Impresiones", 0.3m, 3)));

        var articulos = await e.FilasAsync("SELECT PRODUCTO_CODIGO, USA_MAYOREO, PRECIO_FINAL FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t ORDER BY ID",
            ("t", venta.TicketId));
        Assert.Equal([("CUAD-PRO", "t", 27.5d), ("IMP-BN", "f", 10d)],
            articulos.Select(a => ((string)a["PRODUCTO_CODIGO"]!, (string)a["USA_MAYOREO"]!, Math.Round(Doble(a["PRECIO_FINAL"]), 3))));
        Assert.Equal(30d, Math.Round(Doble(await e.ValorAsync("SELECT TOTAL_AHORRADO FROM VENTATICKETS WHERE ID = @t", ("t", venta.TicketId))), 3));
        var leidas = (await LectorEleventa.LeerAsync(e.Conexion)).Lineas.Where(l => l.TicketId == venta.TicketId).ToList();
        Assert.Equal([("CUAD-PRO", true), ("IMP-BN", false)], leidas.Select(l => (l.Codigo, l.UsaMayoreo)));

        await EnvioEleventa.DevolverAsync(e.Conexion, Caja, Devolucion(venta.TicketId, cancelacion: false, cobrada.AddMinutes(10),
            new ArticuloDevuelto(10, "Cuaderno", 2, 55)));

        Assert.Equal("t", (string)(await e.ValorAsync("SELECT USA_MAYOREO FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t AND PRODUCTO_CODIGO = 'CUAD-PRO'",
            ("t", venta.TicketId)))!);
        var t = (await e.FilasAsync("SELECT TOTAL, TOTAL_AHORRADO FROM VENTATICKETS WHERE ID = @t", ("t", venta.TicketId))).Single();
        Assert.Equal((278d, 30d), (Math.Round(Doble(t["TOTAL"]), 3), Math.Round(Doble(t["TOTAL_AHORRADO"]), 3)));
    }

    /// <summary>
    /// Lo rebajado va como el descuento de eleventa (revisión del PR #139; así están los 59 tickets con descuento de la base de la
    /// tienda): el renglón con su <c>PORCENTAJE_DESCUENTO</c> contra su precio de antes (con F11, el de mayoreo) y su precio ya
    /// rebajado, y el ticket con todo lo ahorrado contra el menudeo en <c>TOTAL_AHORRADO</c>, el mayoreo y el descuento.
    /// </summary>
    [FactConFirebird]
    public async Task Lo_rebajado_va_como_el_descuento_de_eleventa()
    {
        await using var e = await TiendaAsync();
        var cobrada = new DateTime(2026, 3, 9, 11, 0, 0);

        // Doce cuadernos con F11 ($27.50; se ahorraron $30) y 10 % de descuento ($297); tres impresiones de $1 con 25 % ($2.25).
        var venta = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cobrada,
            new(10, "Cuaderno", 12, 297, ConMayoreo: true, AhorroMayoreo: 30, Descuento: 33), new(11, "Impresiones", 3, 2.25m, Descuento: 0.75m)));

        var articulos = await e.FilasAsync("SELECT USA_MAYOREO, PORCENTAJE_DESCUENTO, PRECIO_USADO, PRECIO_FINAL, TOTAL_ARTICULO FROM VENTATICKETS_ARTICULOS "
            + "WHERE TICKET_ID = @t ORDER BY ID", ("t", venta.TicketId));
        Assert.Equal([("t", 10d, 24.75d, 24.75d, 297d), ("f", 25d, 0.75d, 0.75d, 2.25d)], articulos.Select(a => ((string)a["USA_MAYOREO"]!,
            Math.Round(Doble(a["PORCENTAJE_DESCUENTO"]), 4), Math.Round(Doble(a["PRECIO_USADO"]), 4), Math.Round(Doble(a["PRECIO_FINAL"]), 4),
            Math.Round(Doble(a["TOTAL_ARTICULO"]), 4))));
        Assert.Equal(63.75d, Math.Round(Doble(await e.ValorAsync("SELECT TOTAL_AHORRADO FROM VENTATICKETS WHERE ID = @t", ("t", venta.TicketId))), 3));
    }

    /// <summary>
    /// Lo que devuelve todo lo que le queda al ticket va como cancelación, como lo hace la tienda en eleventa (2026-10-24): allá un
    /// ticket nunca queda vendido en cero y sin artículos. Sin parcial antes, es la cancelación del ticket; con una, la de lo que
    /// quedó. El dinero es el que salió de la caja 2.
    /// </summary>
    [FactConFirebird]
    public async Task Devolver_todo_lo_que_queda_lo_cancela_como_la_tienda()
    {
        await using var e = await TiendaAsync();
        var cobrada = new DateTime(2026, 3, 9, 11, 0, 0);
        var sola = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cobrada, new(10, "Cuaderno", 1, 30), new(null, "Recuerdo", 1, 15)));
        var conParcial = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cobrada.AddMinutes(5), new ArticuloParaEleventa(10, "Cuaderno", 3, 90)));

        // Todo de una vez, y en dos partes: la segunda devuelve lo que quedaba.
        await EnvioEleventa.DevolverAsync(e.Conexion, Caja, Devolucion(sola.TicketId, cancelacion: false, cobrada.AddMinutes(10),
            new(10, "Cuaderno", 1, 30), new(null, "Recuerdo", 1, 15)));
        await EnvioEleventa.DevolverAsync(e.Conexion, Caja, Devolucion(conParcial.TicketId, cancelacion: false, cobrada.AddMinutes(11), new ArticuloDevuelto(10, "Cuaderno", 1, 30)));
        await EnvioEleventa.DevolverAsync(e.Conexion, Caja, Devolucion(conParcial.TicketId, cancelacion: false, cobrada.AddMinutes(12), new ArticuloDevuelto(10, "Cuaderno", 2, 60)));

        async Task<(string Tipos, string Cancelado, double Total, double Subtotal, int Renglones)> ComoQuedaAsync(int ticket)
        {
            var t = (await e.FilasAsync("SELECT ESTA_CANCELADO, TOTAL, SUBTOTAL FROM VENTATICKETS WHERE ID = @t", ("t", ticket))).Single();
            var tipos = string.Join("", (await e.FilasAsync("SELECT TIPO_DEVOLUCION FROM DEVOLUCIONES WHERE TICKET_ID = @t ORDER BY ID", ("t", ticket)))
                .Select(f => (string)f["TIPO_DEVOLUCION"]!));
            var renglones = Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM DEVOLUCIONES_ARTICULOS WHERE TICKET_ID = @t", ("t", ticket)));
            return (tipos, (string)t["ESTA_CANCELADO"]!, Math.Round(Doble(t["TOTAL"]), 3), Math.Round(Doble(t["SUBTOTAL"]), 3), renglones);
        }
        // Como la cancelación de eleventa: el ticket cancelado con su total, el subtotal en cero y sin renglones de la devolución.
        Assert.Equal(("C", "t", 45d, 0d, 0), await ComoQuedaAsync(sola.TicketId));
        Assert.Equal(("PC", "t", 60d, 0d, 1), await ComoQuedaAsync(conParcial.TicketId));
        Assert.Equal([45d, 30d, 60d], (await e.FilasAsync("SELECT TOTAL_DEVUELTO FROM DEVOLUCIONES ORDER BY ID")).Select(f => Math.Round(Doble(f["TOTAL_DEVUELTO"]), 3)));
        // Los cuatro cuadernos regresaron; en el turno, nada vendido, todo devuelto y las dos ventas canceladas.
        Assert.Equal(12, Doble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);
        var turno = await TurnoAsync(e);
        Assert.Equal((0m, 135m, 135m, 0), ((decimal)turno["ACUMULADO_VENTAS"]!, (decimal)turno["VENTAS_EFECTIVO"]!,
            (decimal)turno["DEVOLUCIONES_VENTAS_EFECTIVO"]!, Convert.ToInt32(turno["NUMERO_VENTAS"])));
    }

    /// <summary>
    /// Lo raro de eleventa que no debe tronar ni escribirse a medias (2026-10-24): un producto sin descripción ni departamento va
    /// con el nombre de aquí y el «- Sin Departamento -» de eleventa; uno con un código más largo de lo que eleventa guarda en sus
    /// ventas (20) no se escribe recortado: queda por revisar, para anotarlo a mano, sin dejar nada a medias. Y los tickets se
    /// llaman como los numera eleventa: los cobrados del turno, sin contar el que eleventa tiene abierto.
    /// </summary>
    [FactConFirebird]
    public async Task Lo_raro_de_un_producto_de_eleventa_no_truena_y_un_codigo_largo_queda_por_revisar()
    {
        await using var e = await TiendaAsync();
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (1, '- Sin Departamento -')");
        await e.EjecutarAsync("INSERT INTO PRODUCTOS (ID, CODIGO, TVENTA, PCOSTO, PVENTA, PFINAL) VALUES (12, 'SIN-NADA', 'U', 5, 10, 10)");
        await e.ProductoAsync(13, "CODIGO-DE-VEINTICINCO-XY", "Tijeras", depto: 5, costo: 20, precio: 35, existencia: 5);
        var cobrada = new DateTime(2026, 3, 9, 11, 0, 0);

        var venta = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cobrada, new ArticuloParaEleventa(12, "Sacapuntas de aquí", 1, 10)));
        var largo = await Assert.ThrowsAsync<ChoqueConEleventa>(() => EnvioEleventa.MandarAsync(e.Conexion, Caja,
            Venta(cobrada.AddMinutes(5), new ArticuloParaEleventa(13, "Tijeras", 1, 35))));

        var renglon = (await e.FilasAsync("SELECT PRODUCTO_NOMBRE, DEPARTAMENTO_ID FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t", ("t", venta.TicketId))).Single();
        Assert.Equal(("Sacapuntas de aquí", 1), ((string)renglon["PRODUCTO_NOMBRE"]!, Convert.ToInt32(renglon["DEPARTAMENTO_ID"])));
        Assert.Equal("Ticket 1", (string)(await e.ValorAsync("SELECT NOMBRE FROM VENTATICKETS WHERE ID = @t", ("t", venta.TicketId)))!);
        Assert.Equal(MotivoPorRevisar.AnotarAMano, largo.Motivo);
        Assert.Contains("CODIGO-DE-VEINTICINCO-XY", largo.Message);
        // De la del código largo no quedó nada: solo el ticket de la primera (y el abierto del turno).
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE ESTA_ABIERTO = 'f' AND CAJA_ID = 4")));
        Assert.Equal(5, Doble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 13")), 3);
    }

    [FactConFirebird]
    public async Task Lo_que_no_esta_en_el_ticket_de_eleventa_no_se_devuelve()
    {
        await using var e = await TiendaAsync();
        var venta = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(new DateTime(2026, 3, 9, 11, 0, 0), new ArticuloParaEleventa(10, "Cuaderno", 1, 30)));

        // Es un choque con eleventa (no la red): la bandeja lo deja por revisar y sigue con lo demás.
        var error = await Assert.ThrowsAsync<ChoqueConEleventa>(() => EnvioEleventa.DevolverAsync(e.Conexion, Caja,
            Devolucion(venta.TicketId, cancelacion: false, new DateTime(2026, 3, 9, 11, 30, 0), new ArticuloDevuelto(10, "Cuaderno", 2, 60))));

        Assert.Contains("Cuaderno", error.Message);
        Assert.Equal(0, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM DEVOLUCIONES")));
        Assert.Equal(11, Doble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);
    }

    [FactConFirebird]
    public async Task Cerrar_el_turno_anota_lo_que_falto_y_el_efectivo_que_calcula_eleventa_y_borra_su_ticket_abierto()
    {
        await using var e = await TiendaAsync();
        var cobrada = new DateTime(2026, 3, 9, 11, 0, 0);
        var venta = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cobrada, new(10, "Cuaderno", 3, 90), new(null, "Recuerdo", 1, 15)));
        await EnvioEleventa.DevolverAsync(e.Conexion, Caja, Devolucion(venta.TicketId, cancelacion: false, cobrada.AddMinutes(10), new ArticuloDevuelto(null, "Recuerdo", 1, 15)));
        var cierre = new DateTime(2026, 3, 9, 21, 45, 0);

        // Al contar faltaron $12.50.
        var r = await EnvioEleventa.CerrarTurnoAsync(e.Conexion, Caja, cierre, diferencia: -12.5m);
        var otraVez = await EnvioEleventa.CerrarTurnoAsync(e.Conexion, Caja, cierre);

        Assert.False(r.YaEstaba);
        Assert.Equal((null, true), (otraVez.TurnoId, otraVez.YaEstaba));
        var turno = await TurnoAsync(e);
        Assert.Equal(r.TurnoId, Convert.ToInt32(turno["ID"]));
        // El fondo de 500, más 105 cobrados, menos 15 devueltos y los 12.50 que faltaron: lo que se contó.
        Assert.Equal((cierre, 577.5m, 12.5m), ((DateTime)turno["TERMINO_EN"]!, (decimal)turno["EFECTIVO_AL_CIERRE"]!, (decimal)turno["ACUMULADO_SALIDAS"]!));
        var faltante = (await e.FilasAsync("SELECT * FROM CORTE_MOVIMIENTOS WHERE TIPO = 'Salida'")).Single();
        Assert.Equal((12.5m, "Efectivo faltante al cerrar turno de Ana Ruiz del 09/03/2026", 4), ((decimal)faltante["MONTO"]!,
            (string)faltante["DESCRIPCION"]!, Convert.ToInt32(faltante["ID_CAJA"])));
        // Cerró el mismo día que abrió: el día también lo lleva, como una salida.
        Assert.Equal(12.5m, (decimal)(await e.ValorAsync("SELECT ACUMULADO_SALIDAS FROM CORTE_OPERACIONES"))!);
        Assert.Equal(0, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE ESTA_ABIERTO = 't'")));
        // La siguiente venta abre otro turno.
        await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cierre.AddHours(11), new ArticuloParaEleventa(10, "Cuaderno", 1, 30)));
        Assert.Equal(2, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM TURNOS")));
    }

    private static MovimientoParaEleventa Movimiento(bool entrada, decimal monto, string motivo, DateTime cuando) => new(entrada, monto, motivo, cuando, Turno);

    [FactConFirebird]
    public async Task Una_entrada_y_una_salida_quedan_en_el_corte_del_turno_y_del_dia_y_no_se_duplican()
    {
        await using var e = await TiendaAsync();
        // Antes de la primera venta: eleventa también abre el turno para una entrada.
        var hora = new DateTime(2026, 3, 9, 8, 5, 12).AddTicks(3456789);
        var entrada = await EnvioEleventa.MoverEfectivoAsync(e.Conexion, Caja, Movimiento(true, 200, " Cambio del banco ", hora));
        await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(new DateTime(2026, 3, 9, 10, 0, 0), new ArticuloParaEleventa(10, "Cuaderno", 1, 30)));
        var salida = await EnvioEleventa.MoverEfectivoAsync(e.Conexion, Caja, Movimiento(false, 150.5m, "Garrafón de agua", new DateTime(2026, 3, 9, 12, 30, 0)));
        var otraVez = await EnvioEleventa.MoverEfectivoAsync(e.Conexion, Caja, Movimiento(true, 200, "Cambio del banco", hora));

        Assert.Equal((entrada.MovimientoId, false, true), (otraVez.MovimientoId, entrada.YaEstaba, otraVez.YaEstaba));
        Assert.False(salida.YaEstaba);
        var turno = await TurnoAsync(e);
        Assert.Equal((Abierto, 500m), ((DateTime)turno["INICIO_EN"]!, (decimal)turno["DINERO_INICIAL"]!));
        // Un movimiento del corte por cada una, con el motivo como descripción y sin forma de pago ni razón.
        var movimientos = await e.FilasAsync("SELECT * FROM CORTE_MOVIMIENTOS ORDER BY ID");
        Assert.Equal([("Entrada", 200m, "Cambio del banco"), ("Salida", 150.5m, "Garrafón de agua")],
            movimientos.Select(m => ((string)m["TIPO"]!, (decimal)m["MONTO"]!, (string)m["DESCRIPCION"]!)));
        Assert.All(movimientos, m =>
        {
            Assert.Equal((turno["ID"], 4), (m["ID_TURNO"], Convert.ToInt32(m["ID_CAJA"])));
            Assert.Equal([null, null, null], new[] { m["FORMA_PAGO"], m["RAZON_DEVOLUCION"], m["BORRADO_EL"] });
        });
        // La hora con sus diezmilésimas de segundo, como la anota eleventa.
        Assert.Equal(new DateTime(2026, 3, 9, 8, 5, 12).AddTicks(3456000), (DateTime)movimientos[0]["CUANDO_FUE"]!);
        // Las entradas y salidas, en el turno y en el día; lo vendido no cambia.
        foreach (var tabla in new[] { "TURNOS", "CORTE_OPERACIONES" })
        {
            var a = (await e.FilasAsync($"SELECT * FROM {tabla}")).Single();
            Assert.Equal((200m, 150.5m, 30m, 30m, 1), ((decimal)a["ACUMULADO_ENTRADAS"]!, (decimal)a["ACUMULADO_SALIDAS"]!, (decimal)a["ACUMULADO_VENTAS"]!,
                (decimal)a["VENTAS_EFECTIVO"]!, Convert.ToInt32(a["NUMERO_VENTAS"])));
        }
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE ESTA_ABIERTO = 't'")));

        // Al cerrar faltaron $10: el fondo, más lo cobrado, más lo que entró, menos lo que salió y lo que faltó.
        await EnvioEleventa.CerrarTurnoAsync(e.Conexion, Caja, new DateTime(2026, 3, 9, 21, 0, 0), diferencia: -10);
        var cerrado = await TurnoAsync(e);
        Assert.Equal((569.5m, 160.5m), ((decimal)cerrado["EFECTIVO_AL_CIERRE"]!, (decimal)cerrado["ACUMULADO_SALIDAS"]!));
    }

    [FactConFirebird]
    public async Task Despues_de_medianoche_solo_suma_al_dia_si_ese_dia_ya_tiene_renglon()
    {
        await using var e = await TiendaAsync();
        var noche = new DateTime(2026, 3, 9, 23, 50, 0);
        await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(noche, new ArticuloParaEleventa(10, "Cuaderno", 1, 30)));

        // Pasada la medianoche, antes de vender ese día: eleventa no crea el renglón del día para una salida.
        await EnvioEleventa.MoverEfectivoAsync(e.Conexion, Caja, Movimiento(false, 40, "Pago al de la basura", noche.AddMinutes(40)));
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM CORTE_OPERACIONES")));
        Assert.Equal(0m, (decimal)(await e.ValorAsync("SELECT ACUMULADO_SALIDAS FROM CORTE_OPERACIONES"))!);

        // Con una venta ese día ya tiene renglón: lo que entra después, y lo que sobró al cerrar, suman ahí.
        await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(noche.AddMinutes(70), new ArticuloParaEleventa(10, "Cuaderno", 2, 60)));
        await EnvioEleventa.MoverEfectivoAsync(e.Conexion, Caja, Movimiento(true, 100, "Cambio", noche.AddMinutes(90)));
        await EnvioEleventa.CerrarTurnoAsync(e.Conexion, Caja, noche.AddHours(7), diferencia: 5);

        var dias = await e.FilasAsync("SELECT DIA, ACUMULADO_ENTRADAS, ACUMULADO_SALIDAS FROM CORTE_OPERACIONES ORDER BY DIA");
        Assert.Equal([(noche.Date, 0m, 0m), (noche.Date.AddDays(1), 105m, 0m)],
            dias.Select(d => ((DateTime)d["DIA"]!, (decimal)d["ACUMULADO_ENTRADAS"]!, (decimal)d["ACUMULADO_SALIDAS"]!)));
        // El turno lleva todo: 500 + 90 cobrados + 105 que entraron − 40 que salieron.
        var turno = await TurnoAsync(e);
        Assert.Equal((105m, 40m, 655m), ((decimal)turno["ACUMULADO_ENTRADAS"]!, (decimal)turno["ACUMULADO_SALIDAS"]!, (decimal)turno["EFECTIVO_AL_CIERRE"]!));
    }
}
