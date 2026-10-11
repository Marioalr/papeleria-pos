using PapeleriaPos.Eleventa;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// La revisión de lo que se ve en vivo: lo que escribe la caja 2 tiene que pasarla, y lo que eleventa haga distinto, salir.
/// </summary>
public class RevisionEnVivoTests
{
    private static readonly CajaEnEleventa Caja = new(CajaId: 4, CajeroId: 7);
    private static readonly TurnoParaEleventa Turno = new(new DateTime(2026, 3, 9, 8, 0, 0), 500);

    private static async Task<EleventaDePrueba> TiendaAsync()
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO) VALUES (7, 'Ana Ruiz', 'cajera2')");
        foreach (var (id, nombre) in new[] { (5, "Papelería"), (9, EnvioEleventa.DepartamentoComunes) })
            await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (@id, @nombre)", ("id", id), ("nombre", nombre));
        await e.ProductoAsync(10, "CUAD-PRO", "Cuaderno profesional", depto: 5, costo: 18, precio: 30, existencia: 12);
        return e;
    }

    private static VentaParaEleventa Venta(DateTime cobrada, params ArticuloParaEleventa[] articulos) =>
        new(cobrada.AddMinutes(-2), cobrada, PagoCon: 200, Turno, articulos);

    [FactConFirebird]
    public async Task Lo_que_escribe_la_caja_2_al_cancelar_devolver_y_cerrar_pasa_la_revision()
    {
        await using var e = await TiendaAsync();
        var cobrada = new DateTime(2026, 3, 9, 11, 0, 0);
        var primera = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cobrada, new(10, "Cuaderno", 2, 60), new(null, "Recuerdo", 1, 15)));
        var segunda = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(cobrada.AddMinutes(5), new(10, "Cuaderno", 3, 90), new(null, "Recuerdo", 1, 15)));

        var antes = await FotoEleventa.TomarAsync(e.Conexion);
        await EnvioEleventa.DevolverAsync(e.Conexion, Caja, new DevolucionParaEleventa(primera.TicketId, true, cobrada.AddMinutes(10), Turno, []));
        var cancelada = await FotoEleventa.TomarAsync(e.Conexion);
        await EnvioEleventa.DevolverAsync(e.Conexion, Caja, new DevolucionParaEleventa(segunda.TicketId, false, cobrada.AddMinutes(20), Turno,
            [new ArticuloDevuelto(10, "Cuaderno", 1, 30), new ArticuloDevuelto(null, "Recuerdo", 1, 15)]));
        var devuelta = await FotoEleventa.TomarAsync(e.Conexion);
        await EnvioEleventa.CerrarTurnoAsync(e.Conexion, Caja, cobrada.AddHours(10), diferencia: -5);
        var cerrada = await FotoEleventa.TomarAsync(e.Conexion);

        var cancelacion = RevisionEnVivo.Revisar(antes, cancelada);
        var devolucion = RevisionEnVivo.Revisar(cancelada, devuelta);
        var cierre = RevisionEnVivo.Revisar(devuelta, cerrada);

        Assert.StartsWith("Cancelación del ticket #", cancelacion[0]);
        Assert.StartsWith("Devolución del ticket #", devolucion[0]);
        Assert.StartsWith("Cierre del turno", cierre[0]);
        foreach (var lineas in new[] { cancelacion, devolucion, cierre })
        {
            Assert.DoesNotContain(lineas, l => l.Contains('✗'));
            Assert.Contains(lineas, l => l.Contains('✓'));
        }
        Assert.Contains(devolucion, l => l.Contains("se devolvió completo y su renglón se borró"));
        Assert.Contains(cierre, l => l.Contains("faltante de $5.00 quedó como Salida"));
        // Ese día ya tenía renglón (hubo ventas): lo lleva también.
        Assert.Contains(cierre, l => l.Contains("el día también lo lleva"));
        // Sin cancelaciones, devoluciones ni cierres, no hay nada que revisar.
        Assert.Empty(RevisionEnVivo.Revisar(cerrada, await FotoEleventa.TomarAsync(e.Conexion)));
    }

    // Sin Firebird: fotos armadas a mano, con lo mínimo de cada tabla.

    private static TablaEnFoto Tabla(string nombre, string[] columnas, params string[][] renglones) =>
        new(nombre, columnas, [columnas[0]], renglones.Length, true, renglones.ToDictionary(r => r[0], r => r));

    private static FotoEleventa Foto(params TablaEnFoto[] tablas) =>
        new(DateTime.UtcNow, new Dictionary<string, long>(), tablas.ToDictionary(t => t.Nombre, t => t));

    private static readonly string[] Devoluciones = ["ID", "TURNO_ID", "TICKET_ID", "TIPO_DEVOLUCION", "TOTAL_DEVUELTO"];
    private static readonly string[] Tickets = ["ID", "FOLIO", "TURNO_ID", "TOTAL", "GANANCIA", "ESTA_CANCELADO", "ESTA_ABIERTO", "NUMERO_ARTICULOS"];
    private static readonly string[] Articulos = ["ID", "TICKET_ID", "PRODUCTO_CODIGO", "CANTIDAD"];
    private static readonly string[] Movimientos = ["ID", "TIPO", "MONTO", "DESCRIPCION", "ID_TURNO", "FORMA_PAGO", "RAZON_DEVOLUCION"];
    private static readonly string[] Kardex = ["ID", "DESCRIPCION", "CANTIDAD"];
    private static readonly string[] Turnos = ["ID", "ID_CAJA", "DINERO_INICIAL", "ACUMULADO_VENTAS", "VENTAS_EFECTIVO", "DEVOLUCIONES_VENTAS_EFECTIVO",
        "NUMERO_VENTAS", "ACUMULADO_ENTRADAS", "ACUMULADO_SALIDAS", "EFECTIVO_AL_CIERRE", "TERMINO_EN", "ID_OPERACION_FIN"];
    private static readonly string[] Dias = ["ID", "ACUMULADO_VENTAS", "DEVOLUCIONES_VENTAS_EFECTIVO", "ACUMULADO_ENTRADAS", "ACUMULADO_SALIDAS"];

    [Fact]
    public void Lo_que_eleventa_hace_distinto_se_marca()
    {
        var antes = Foto(
            Tabla("DEVOLUCIONES", Devoluciones),
            Tabla("VENTATICKETS", Tickets, ["50", "4521", "3", "30", "12", "f", "f", "2"]),
            Tabla("VENTATICKETS_ARTICULOS", Articulos, ["70", "50", "CUAD-PRO", "1"], ["71", "50", "LAP-01", "1"]),
            Tabla("CORTE_MOVIMIENTOS", Movimientos),
            Tabla("INVENTARIO_HISTORIAL", Kardex),
            Tabla("TURNOS", Turnos, ["3", "4", "500", "30", "30", "0", "1", "0", "0", "0", "∅", "∅"]),
            Tabla("CORTE_OPERACIONES", Dias, ["8", "30", "0", "0", "0"]),
            Tabla("BITACORA", ["ID", "TEXTO"]));
        // Eleventa canceló, pero dejó la ganancia y anotó algo en una tabla que la caja 2 no toca.
        var despues = Foto(
            Tabla("DEVOLUCIONES", Devoluciones, ["90", "3", "50", "C", "30"]),
            Tabla("VENTATICKETS", Tickets, ["50", "4521", "3", "30", "12", "t", "f", "0"]),
            Tabla("VENTATICKETS_ARTICULOS", Articulos, ["70", "50", "CUAD-PRO", "1"], ["71", "50", "LAP-01", "1"]),
            Tabla("CORTE_MOVIMIENTOS", Movimientos,
                ["1", "Devolucion", "20", "Devolución \"Cuaderno profesional\", Ticket #4521", "3", "E", "V"],
                ["2", "Devolucion", "10", "Devolución \"Lápiz\", Ticket #4521", "3", "E", "V"]),
            Tabla("INVENTARIO_HISTORIAL", Kardex, ["5", "Devolución de venta #4521", "-1"], ["6", "Devolución de venta #4521", "-1"]),
            Tabla("TURNOS", Turnos, ["3", "4", "500", "0", "30", "30", "0", "0", "0", "0", "∅", "∅"]),
            Tabla("CORTE_OPERACIONES", Dias, ["8", "0", "30", "0", "0"]),
            Tabla("BITACORA", ["ID", "TEXTO"], ["1", "Canceló el ticket"]));

        var lineas = RevisionEnVivo.Revisar(antes, despues);

        Assert.Equal("Cancelación del ticket #4521 (DEVOLUCIONES 90, $30.00):", lineas[0]);
        Assert.Contains("  ✓ el ticket quedó cancelado", lineas);
        Assert.Contains("  ✗ su ganancia quedó en 12 (la caja 2 la deja en cero)", lineas);
        Assert.Contains(lineas, l => l.StartsWith("  ✓ 2 movimientos «Devolucion» del corte"));
        Assert.Contains(lineas, l => l.StartsWith("  ✓ el turno 3: lo vendido bajó $30.00 y el número de ventas 1"));
        Assert.Contains("✗ Eleventa también tocó BITACORA, que la caja 2 no escribe: hay que ver qué es.", lineas);
    }

    [Fact]
    public void Un_cierre_con_sobrante_que_no_entra_al_turno_se_marca()
    {
        var antes = Foto(
            Tabla("TURNOS", Turnos, ["3", "4", "500", "100", "100", "0", "4", "0", "0", "0", "∅", "∅"]),
            Tabla("VENTATICKETS", Tickets, ["60", "∅", "3", "0", "0", "f", "t", "0"]),
            Tabla("CORTE_MOVIMIENTOS", Movimientos),
            Tabla("CORTE_OPERACIONES", Dias, ["8", "100", "0", "0", "0"]));
        var despues = Foto(
            Tabla("TURNOS", Turnos, ["3", "4", "500", "100", "100", "0", "4", "0", "0", "600", "2026-03-09 21:00:00.000", "∅"]),
            Tabla("VENTATICKETS", Tickets),
            Tabla("CORTE_MOVIMIENTOS", Movimientos, ["1", "Entrada", "20", "Efectivo sobrante al cerrar turno de Ana Ruiz del 09/03/2026", "3", "∅", "∅"]),
            Tabla("CORTE_OPERACIONES", Dias, ["8", "100", "0", "0", "0"]));

        var lineas = RevisionEnVivo.Revisar(antes, despues);

        Assert.Equal("Cierre del turno 3 (caja 4):", lineas[0]);
        Assert.Contains(lineas, l => l.StartsWith("  ✓ el efectivo al cierre ($600.00)"));
        Assert.Contains("  ✓ su ticket abierto se borró", lineas);
        Assert.Contains("  ✗ el sobrante de $20.00 quedó como «Entrada» y ACUMULADO_ENTRADAS del turno cambió $0.00", lineas);
        Assert.Contains("  ✓ ese día no tenía renglón: no lo lleva", lineas);
    }
}
