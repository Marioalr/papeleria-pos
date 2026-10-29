using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Pruebas.ControlPrint;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// En el piloto, las impresiones llegan a eleventa como las cobra la tienda (2026-10-25): con el producto IMP «Impresiones» (a
/// granel a $10), la cantidad es el monto entre su precio (1.7 = $17) y el renglón dice qué trabajos son. Las de las dos fuentes (la
/// cola de Control Print y lo que imprime la caja 2) van juntas y nunca quedan por revisar por ser de aquí; su devolución regresa
/// también como IMP. Contra un Firebird de prueba y el Control Print falso.
/// </summary>
public sealed class ImpresionesEnEleventaTests
{
    /// <summary>
    /// El producto con que la tienda cobra las impresiones (IMP, a granel a $10, como lo deja la importación): la «Impresión blanco y
    /// negro» de la base de prueba con el código IMP y, si se pide, ligada a ese producto de eleventa. Va antes de que arranque el
    /// servicio (su catálogo en memoria la lee al empezar).
    /// </summary>
    internal static Task<Guid> ProductoImpAsync(ServicioDePrueba servicio, int? idEleventa) =>
        ProductoDeImpresionesAsync(servicio, "Impresión blanco y negro", "IMP", idEleventa);

    /// <summary>
    /// Uno de los productos con que la tienda cobra lo impreso (IMP; las copias del panel, COP), a granel a $10, como lo deja la
    /// importación: el primero de la base de prueba cuyo nombre empieza así, con ese código.
    /// </summary>
    internal static async Task<Guid> ProductoDeImpresionesAsync(ServicioDePrueba servicio, string empiezaCon, string codigo, int? idEleventa)
    {
        await using var db = servicio.Base();
        var variante = await db.Variantes.Where(v => db.Productos.Any(p => p.Id == v.ProductoId && p.Nombre.StartsWith(empiezaCon)))
            .OrderBy(v => v.Id).FirstAsync();
        variante.UnidadBase = UnidadBase.PiezaAGranel;
        var presentacion = await db.Presentaciones.FirstAsync(p => p.VarianteId == variante.Id && p.EsBase);
        db.CodigosBarras.Add(new CodigoBarras { Codigo = codigo, PresentacionId = presentacion.Id, Origen = OrigenCodigo.Interno });
        db.Precios.RemoveRange(await db.Precios.Where(p => p.VarianteId == variante.Id).ToListAsync());
        db.Precios.Add(new Precio { VarianteId = variante.Id, PrecioCentavos = 1000, CantidadMinima = Cantidad.Piezas(1) });
        if (idEleventa is { } id)
            db.MapeosImportacion.Add(new MapeoImportacion
            {
                Origen = SincronizacionEleventa.Origen, Entidad = nameof(Variante), IdOrigen = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IdNuevo = variante.Id,
            });
        await db.SaveChangesAsync();
        return variante.Id;
    }

    private static TrabajoMedidoDto Medido(Caja c, string documento) =>
        new(Guid.NewGuid(), c.Datos.Id, null, "CAJA2", "WF-6590 Series(Red)", 7, documento, "cajera", DateTime.UtcNow, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            [new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)], true, null, null, DateTime.UtcNow.AddSeconds(-20));

    private static async Task<ColaControlPrintDto> ColaAsync(Caja c) =>
        (await c.Http.GetFromJsonAsync<ColaControlPrintDto>($"api/sucursales/{c.Datos.SucursalId}/impresiones/control-print?usuarioId={c.Sesion.UsuarioId}"))!;

    [FactConFirebird]
    public async Task Las_de_Control_Print_y_las_de_la_caja_2_llegan_a_eleventa_como_IMP_con_sus_numeros_y_regresan_igual()
    {
        var (e, turno) = await EleventaAsync();
        await using var eleventa = e;
        await e.ProductoAsync(11, "IMP", "Impresiones", depto: 5, costo: 2, precio: 10, existencia: 500, tipoVenta: 'D');
        await using var controlPrint = await ServidorControlPrintFalso.IniciarAsync(PorCobrarDePrueba.Codigo);
        controlPrint.CambiarTrabajos(t => t.AddRange([PorCobrarDePrueba.Trabajo(1234, PorCobrarDePrueba.Hace(4), precio: 15),
            PorCobrarDePrueba.Trabajo(1235, PorCobrarDePrueba.Hace(3), precio: 2)]));
        await using var servicio = await ServicioDePrueba.CrearAsync([.. Ajustes(e.Servidor), .. ControlPrintEnLaCajaTests.Ajustes(controlPrint)]);
        var pluma = await MapearAsync(servicio);
        var imp = await ProductoImpAsync(servicio, idEleventa: 11);
        var c = await AbrirAsync(servicio, pluma);
        await EsperarAsync(() => ColaAsync(c), x => x.Trabajos.Count == 2);
        // Lo que imprimió la caja 2 (con los costos de Control Print: el mínimo es $3).
        var medido = Medido(c, "Recibo de luz.pdf");
        await Leer<object>(await c.Http.PostAsJsonAsync("api/impresiones", medido));

        // Una pluma y las tres impresiones: $15 + $2 de Control Print y $3 de la caja 2.
        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Sesion.UsuarioId,
            [new LineaSolicitada(pluma, null, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 5000)], VentaId: Guid.NewGuid(), Impresiones: [medido.Id],
            ImpresionesControlPrint: [new ImpresionDeControlPrintSolicitada(1234, 1500), new ImpresionDeControlPrintSolicitada(1235, 200)])));
        var deLaPluma = ticket.Lineas.Single(l => !l.Descripcion.StartsWith("Impresi", StringComparison.Ordinal)).ImporteCentavos;
        Assert.Equal(deLaPluma + 2000, ticket.TotalCentavos);
        // En la caja, la línea va como IMP: 2 (= $20 a $10) con qué trabajos son.
        Assert.Contains(ticket.Lineas, l => (l.Cantidad, l.Descripcion, l.ImporteCentavos) == ("2", "Impresiones #1234, #1235 y 1 de CAJA2", 2000));
        await using (var db = servicio.Base())
        {
            var linea = await db.VentasLineas.SingleAsync(l => l.VentaId == ticket.VentaId && l.EsImpresiones);
            Assert.Equal((imp, 2000L, 1000L, 2000L, false), (linea.VarianteId!.Value, linea.Cantidad.Milesimas, linea.PrecioCentavos, linea.ImporteCentavos, linea.LlevaInventario));
        }

        var envios = await MandarAsync(c);
        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        int ticketEleventa;
        await using (var db = servicio.Base())
            ticketEleventa = (await db.EnviosAEleventa.SingleAsync(x => x.VentaId == ticket.VentaId)).TicketEleventa!.Value;
        var renglones = await e.FilasAsync(
            "SELECT PRODUCTO_CODIGO, PRODUCTO_NOMBRE, CANTIDAD, PRECIO_USADO, TOTAL_ARTICULO, DEPARTAMENTO_ID FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t ORDER BY ID",
            ("t", ticketEleventa));
        var deImpresiones = Assert.Single(renglones, r => ((string)r["PRODUCTO_CODIGO"]!).Trim() == "IMP");
        // Como lo cobra la tienda: IMP, 2 a $10, en su departamento; con los números de Control Print en el renglón.
        Assert.Equal(("Impresiones #1234, #1235 y 1 de CAJA2", 2d, 10d, 20d, 5),
            ((string)deImpresiones["PRODUCTO_NOMBRE"]!, Math.Round(Convert.ToDouble(deImpresiones["CANTIDAD"]), 3), Math.Round(Convert.ToDouble(deImpresiones["PRECIO_USADO"]), 2),
             Math.Round(Convert.ToDouble(deImpresiones["TOTAL_ARTICULO"]), 2), Convert.ToInt32(deImpresiones["DEPARTAMENTO_ID"])));
        Assert.Equal((deLaPluma + 2000) / 100d, Math.Round(Convert.ToDouble(await e.ValorAsync("SELECT TOTAL FROM VENTATICKETS WHERE ID = @t", ("t", ticketEleventa))), 2));
        Assert.Equal(498, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 11")), 3);
        // Ningún artículo común: todo pasó como productos de eleventa.
        Assert.DoesNotContain(renglones, r => ((string)r["PRODUCTO_CODIGO"]!).StartsWith("COMUN-", StringComparison.Ordinal));

        // Se devuelve la línea de impresiones (completa): regresa a eleventa como IMP, y sus trabajos vuelven a estar por cobrar.
        Guid lineaId;
        await using (var db = servicio.Base())
            lineaId = (await db.VentasLineas.SingleAsync(l => l.VentaId == ticket.VentaId && l.EsImpresiones)).Id;
        await Leer<object>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Sesion.UsuarioId, c.Datos.Id, "Salieron mal", [new LineaDevolucionSolicitada(lineaId, 2000)])));
        envios = await MandarAsync(c);
        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        var devuelto = (await e.FilasAsync("SELECT CODIGO_PRODUCTO, CANTIDAD_DEVUELTA, DINERO_DEVUELTO FROM DEVOLUCIONES_ARTICULOS WHERE TICKET_ID = @t",
            ("t", ticketEleventa))).Single();
        Assert.Equal(("IMP", 2d, 20d), (((string)devuelto["CODIGO_PRODUCTO"]!).Trim(), Math.Round(Convert.ToDouble(devuelto["CANTIDAD_DEVUELTA"]), 3),
            Math.Round(Convert.ToDouble(devuelto["DINERO_DEVUELTO"]), 2)));
        Assert.Null(await e.ValorAsync("SELECT ID FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t AND PRODUCTO_CODIGO = 'IMP'", ("t", ticketEleventa)));
        Assert.Equal(500, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 11")), 3);
        Assert.Equal([1235L, 1234], (await ColaAsync(c)).Trabajos.Select(t => t.Id));
        Assert.Contains((await c.Http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>(
            $"api/sucursales/{c.Datos.SucursalId}/impresiones/por-cobrar?usuarioId={c.Sesion.UsuarioId}"))!, t => t.Id == medido.Id);
        _ = turno;
    }

    /// <summary>
    /// Las copias del panel van aparte (2026-10-29): en eleventa como COP «Copias» (en otro departamento y con otro costo: la tienda las
    /// separa de las impresiones), con la cantidad = el monto entre su precio; las impresiones, como IMP. Las dos de Control Print y las
    /// registradas en F2. Su devolución regresa igual, y devolver la de copias solo vuelve a ofrecer sus tarjetas.
    /// </summary>
    [FactConFirebird]
    public async Task Las_copias_del_panel_llegan_a_eleventa_como_COP_y_las_impresiones_como_IMP()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await e.ProductoAsync(11, "IMP", "Impresiones", depto: 5, costo: 2, precio: 10, existencia: 500, tipoVenta: 'D');
        await e.ProductoAsync(12, "COP", "Copias", depto: 2, costo: 1, precio: 10, existencia: 1000, tipoVenta: 'D');
        await using var controlPrint = await ServidorControlPrintFalso.IniciarAsync(PorCobrarDePrueba.Codigo);
        var copias = PorCobrarDePrueba.Trabajo(1240, PorCobrarDePrueba.Hace(3), precio: 6, documento: "Copias en la impresora");
        copias.Kind = JobKind.PanelCopy;
        controlPrint.CambiarTrabajos(t => t.AddRange([PorCobrarDePrueba.Trabajo(1234, PorCobrarDePrueba.Hace(4), precio: 15), copias]));
        await using var servicio = await ServicioDePrueba.CrearAsync([.. Ajustes(e.Servidor), .. ControlPrintEnLaCajaTests.Ajustes(controlPrint)]);
        var pluma = await MapearAsync(servicio);
        await ProductoImpAsync(servicio, idEleventa: 11);
        await ProductoDeImpresionesAsync(servicio, "Copia blanco y negro", "COP", idEleventa: 12);
        var c = await AbrirAsync(servicio, pluma);
        await EsperarAsync(() => ColaAsync(c), x => x.Trabajos.Count == 2);
        // Y unas copias del panel registradas en esta caja (F2, «Copias del panel»): $4.
        var registradas = await Leer<ImpresionPorCobrarDto>(await c.Http.PostAsJsonAsync("api/impresiones/copias",
            new SolicitudCopias(Guid.NewGuid(), c.Datos.Id, c.Sesion.UsuarioId, 2, false, "Carta", false, 400, null)));

        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Sesion.UsuarioId,
            [], [new CobroSolicitado(FormaPago.Efectivo, 2500)], VentaId: Guid.NewGuid(), Impresiones: [registradas.Id],
            ImpresionesControlPrint: [new ImpresionDeControlPrintSolicitada(1234, 1500), new ImpresionDeControlPrintSolicitada(1240, 600)])));

        // En la caja, dos líneas: las impresiones como IMP (1.5) y las copias como COP (1 = $6 + $4).
        Assert.Contains(ticket.Lineas, l => (l.Cantidad, l.Descripcion, l.ImporteCentavos) == ("1.5", "Impresión #1234", 1500));
        Assert.Contains(ticket.Lineas, l => (l.Cantidad, l.Descripcion, l.ImporteCentavos) == ("1", "Copias #1240 y 1 de Caja 1", 1000));
        var envios = await MandarAsync(c);
        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        int ticketEleventa;
        await using (var db = servicio.Base())
            ticketEleventa = (await db.EnviosAEleventa.SingleAsync(x => x.VentaId == ticket.VentaId)).TicketEleventa!.Value;
        var renglones = (await e.FilasAsync(
                "SELECT PRODUCTO_CODIGO, PRODUCTO_NOMBRE, CANTIDAD, TOTAL_ARTICULO, DEPARTAMENTO_ID FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t ORDER BY ID",
                ("t", ticketEleventa)))
            .Select(r => (((string)r["PRODUCTO_CODIGO"]!).Trim(), (string)r["PRODUCTO_NOMBRE"]!, Math.Round(Convert.ToDouble(r["CANTIDAD"]), 3),
                Math.Round(Convert.ToDouble(r["TOTAL_ARTICULO"]), 2), Convert.ToInt32(r["DEPARTAMENTO_ID"])))
            .ToList();
        Assert.Equal([("IMP", "Impresión #1234", 1.5d, 15d, 5), ("COP", "Copias #1240 y 1 de Caja 1", 1d, 10d, 2)], renglones);
        Assert.Equal(999, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 12")), 3);

        // Se devuelve completa la línea de copias: a eleventa como COP, y solo vuelven a estar por cobrar las copias.
        await using (var db = servicio.Base())
        {
            var deCopias = await db.VentasLineas.SingleAsync(l => l.VentaId == ticket.VentaId && l.EsCopias);
            await Leer<object>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
                new SolicitudDevolucion(c.Sesion.UsuarioId, c.Datos.Id, "No eran", [new LineaDevolucionSolicitada(deCopias.Id, deCopias.Cantidad.Milesimas)])));
        }
        envios = await MandarAsync(c);
        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        var devuelto = (await e.FilasAsync("SELECT CODIGO_PRODUCTO, CANTIDAD_DEVUELTA FROM DEVOLUCIONES_ARTICULOS WHERE TICKET_ID = @t", ("t", ticketEleventa))).Single();
        Assert.Equal(("COP", 1d), (((string)devuelto["CODIGO_PRODUCTO"]!).Trim(), Math.Round(Convert.ToDouble(devuelto["CANTIDAD_DEVUELTA"]), 3)));
        Assert.Equal(1000, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 12")), 3);
        Assert.Equal([1240L], (await ColaAsync(c)).Trabajos.Select(t => t.Id));
        Assert.Contains((await c.Http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>(
            $"api/sucursales/{c.Datos.SucursalId}/impresiones/por-cobrar?usuarioId={c.Sesion.UsuarioId}"))!, t => t.Id == registradas.Id);
        Assert.Empty(controlPrint.Escrituras);
    }

    /// <summary>Una devolución de IMP hecha en eleventa (con su hora, sin la marca de la caja 2), de lo que se mandó en ese ticket.</summary>
    private static async Task DevolverEnEleventaAsync(EleventaDePrueba e, int ticket, int turno, decimal cantidad, DateTime devuelta)
    {
        await e.EjecutarAsync("""
            INSERT INTO DEVOLUCIONES (TURNO_ID, TICKET_ID, TIPO_DEVOLUCION, PAGADO_EN, DEVUELTO_EN, CAJERO, CAJA, TOTAL_DEVUELTO)
            VALUES (@turno, @ticket, 'P', @pagado, @devuelto, 'cajera2', 'Caja 2', @total)
            """, ("turno", turno), ("ticket", ticket), ("pagado", HoraLocal(5)), ("devuelto", devuelta), ("total", (double)(cantidad * 10)));
        await e.EjecutarAsync("""
            INSERT INTO DEVOLUCIONES_ARTICULOS (DEVOLUCION_ID, TICKET_ID, CODIGO_PRODUCTO, DESCRIPCION_PRODUCTO, CANTIDAD_DEVUELTA, DINERO_DEVUELTO, INVENTARIO_POST_DEVOLUCION)
            VALUES (@dev, @ticket, 'IMP', 'Impresiones #1234, #1235', @cantidad, @dinero, 0)
            """, ("dev", Convert.ToInt32(await e.ValorAsync("SELECT MAX(ID) FROM DEVOLUCIONES"))), ("ticket", ticket), ("cantidad", (double)cantidad),
            ("dinero", (double)(cantidad * 10)));
    }

    [FactConFirebird]
    public async Task Lo_que_eleventa_devuelve_de_IMP_llega_a_la_linea_y_solo_completa_vuelve_a_mostrar_las_tarjetas()
    {
        var (e, turno) = await EleventaAsync();
        await using var eleventa = e;
        await e.ProductoAsync(11, "IMP", "Impresiones", depto: 5, costo: 2, precio: 10, existencia: 500, tipoVenta: 'D');
        await e.EjecutarAsync("UPDATE PRODUCTOS SET MEDIDA_ID = 1 WHERE ID = 11");
        await using var controlPrint = await ServidorControlPrintFalso.IniciarAsync(PorCobrarDePrueba.Codigo);
        controlPrint.CambiarTrabajos(t => t.AddRange([PorCobrarDePrueba.Trabajo(1234, PorCobrarDePrueba.Hace(4), precio: 15),
            PorCobrarDePrueba.Trabajo(1235, PorCobrarDePrueba.Hace(3), precio: 2)]));
        await using var servicio = await ServicioDePrueba.CrearAsync([.. Ajustes(e.Servidor, leer: true), .. ControlPrintEnLaCajaTests.Ajustes(controlPrint)]);
        var pluma = await MapearAsync(servicio);
        await ProductoImpAsync(servicio, idEleventa: 11);
        var c = await AbrirAsync(servicio, pluma);
        await EsperarPrimeraLecturaAsync(c);
        await EsperarAsync(() => ColaAsync(c), x => x.Trabajos.Count == 2);
        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Sesion.UsuarioId,
            [], [new CobroSolicitado(FormaPago.Efectivo, 1700)], VentaId: Guid.NewGuid(),
            ImpresionesControlPrint: [new ImpresionDeControlPrintSolicitada(1234, 1500), new ImpresionDeControlPrintSolicitada(1235, 200)])));
        await MandarAsync(c);
        int ticketEleventa;
        await using (var db = servicio.Base())
            ticketEleventa = (await db.EnviosAEleventa.SingleAsync(x => x.VentaId == ticket.VentaId)).TicketEleventa!.Value;
        Assert.Empty((await ColaAsync(c)).Trabajos);

        // En eleventa le regresan $5 (0.5 de IMP): llega a la línea, sin efectivo de esta caja, y las impresiones siguen cobradas
        // (el cliente se quedó con ellas).
        await DevolverEnEleventaAsync(e, ticketEleventa, turno!.Value, 0.5m, HoraLocal(1));
        await LeerAsync(c);
        await using (var db = servicio.Base())
        {
            var linea = await db.VentasLineas.SingleAsync(l => l.VentaId == ticket.VentaId);
            Assert.Equal((500L, 1700L), (linea.CantidadDevuelta.Milesimas, linea.Cantidad.Milesimas));
            Assert.Equal(500, (await db.Devoluciones.SingleAsync(d => d.VentaId == ticket.VentaId)).TotalCentavos);
        }
        Assert.Empty((await ColaAsync(c)).Trabajos);

        // Después le regresan lo demás (1.2): la línea queda devuelta completa y las tarjetas vuelven a salir (en Control Print siguen
        // por cobrar).
        await DevolverEnEleventaAsync(e, ticketEleventa, turno.Value, 1.2m, HoraLocal(0));
        await LeerAsync(c);
        await using (var db = servicio.Base())
            Assert.Equal(1700L, (await db.VentasLineas.SingleAsync(l => l.VentaId == ticket.VentaId)).CantidadDevuelta.Milesimas);
        Assert.Equal([1235L, 1234], (await ColaAsync(c)).Trabajos.Select(t => t.Id));
        Assert.Empty((await EnviosAsync(c)).PorAtender);
        Assert.Empty(controlPrint.Escrituras);
    }

    [FactConFirebird]
    public async Task Sin_saber_aqui_su_producto_de_eleventa_lo_busca_por_su_codigo_y_tampoco_queda_por_revisar()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await e.ProductoAsync(11, "IMP", "Impresiones", depto: 5, costo: 2, precio: 10, existencia: 500, tipoVenta: 'D');
        await using var controlPrint = await ServidorControlPrintFalso.IniciarAsync(PorCobrarDePrueba.Codigo);
        controlPrint.CambiarTrabajos(t => t.Add(PorCobrarDePrueba.Trabajo(1234, PorCobrarDePrueba.Hace(4), precio: 15)));
        await using var servicio = await ServicioDePrueba.CrearAsync([.. Ajustes(e.Servidor), .. ControlPrintEnLaCajaTests.Ajustes(controlPrint)]);
        var pluma = await MapearAsync(servicio);
        // Con el código IMP en el catálogo, pero sin el mapeo de la importación.
        await ProductoImpAsync(servicio, idEleventa: null);
        var c = await AbrirAsync(servicio, pluma);
        await EsperarAsync(() => ColaAsync(c), x => x.Trabajos.Count == 1);

        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Sesion.UsuarioId,
            [], [new CobroSolicitado(FormaPago.Efectivo, 1500)], VentaId: Guid.NewGuid(),
            ImpresionesControlPrint: [new ImpresionDeControlPrintSolicitada(1234, 1500)])));
        var envios = await MandarAsync(c);

        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        int ticketEleventa;
        await using (var db = servicio.Base())
            ticketEleventa = (await db.EnviosAEleventa.SingleAsync(x => x.VentaId == ticket.VentaId)).TicketEleventa!.Value;
        var renglon = (await e.FilasAsync("SELECT PRODUCTO_CODIGO, PRODUCTO_NOMBRE, CANTIDAD FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t", ("t", ticketEleventa))).Single();
        Assert.Equal(("IMP", "Impresión #1234", 1.5d), (((string)renglon["PRODUCTO_CODIGO"]!).Trim(), (string)renglon["PRODUCTO_NOMBRE"]!,
            Math.Round(Convert.ToDouble(renglon["CANTIDAD"]), 3)));
        Assert.Empty(controlPrint.Escrituras);
    }
}
