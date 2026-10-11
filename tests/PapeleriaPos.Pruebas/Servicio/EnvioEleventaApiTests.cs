using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El piloto, paso 3 (docs/migracion-eleventa.md, «Mandar cada venta»): cada venta cobrada entra a la bandeja hacia
/// eleventa; al cobrar se intenta ya, y el ticket sale con el folio de eleventa si contestó a tiempo.
/// </summary>
public class EnvioEleventaApiTests
{
    private const int CajaEleventa = 4;

    private static (string, string)[] Ajustes(string servidor, int puerto, string ruta, double segundosAlCobrar = 10) =>
    [
        ("Eleventa:Escribir", "true"), ("Eleventa:Servidor", servidor), ("Eleventa:Puerto", puerto.ToString(CultureInfo.InvariantCulture)),
        ("Eleventa:Base", ruta), ("Eleventa:Usuario", "SYSDBA"),
        ("Eleventa:Clave", Environment.GetEnvironmentVariable("PRUEBAS_FIREBIRD_CLAVE") is { Length: > 0 } clave ? clave : "masterkey"),
        ("Eleventa:Caja", CajaEleventa.ToString(CultureInfo.InvariantCulture)), ("Eleventa:Cajero", "7"),
        ("Eleventa:SegundosAlCobrar", segundosAlCobrar.ToString(CultureInfo.InvariantCulture)),
        // Que en la prueba solo mande el cobro o quien lo pida, no la vuelta de fondo.
        ("Eleventa:SegundosEntreEnvios", "3600"),
    ];

    private static (string, string)[] Ajustes(ServidorEleventa e) => Ajustes(e.Servidor, e.Puerto, e.Base);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Sesion, TurnoDto Turno, ResultadoBusquedaDto Producto);

    private static async Task<Caja> AbrirAsync(ServicioDePrueba servicio)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var sesion = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var turno = await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, sesion.UsuarioId, 50000)));
        var producto = (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=pluma%20punto%20fino&sucursalId={caja.SucursalId}"))!
            .First(r => r.Codigo is not null);
        return new Caja(http, caja, sesion, turno, producto);
    }

    /// <summary>Dos del producto y un artículo común de $15, en efectivo.</summary>
    private static async Task<TicketDto> CobrarAsync(Caja c, FormaPago forma = FormaPago.Efectivo)
    {
        var total = 2 * c.Producto.PrecioCentavos + 1500;
        return await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Sesion.UsuarioId,
            [new LineaSolicitada(c.Producto.VarianteId, null, 2000), new LineaSolicitada(null, null, 1000, "Recuerdo", 1500)],
            [new CobroSolicitado(forma, forma == FormaPago.Efectivo ? total + 10000 : total, forma == FormaPago.Tarjeta ? "4821" : null)])));
    }

    private static async Task<EstadoEnviosEleventaDto> EnviosAsync(Caja c) => (await c.Http.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;

    private static async Task<EleventaDePrueba> EleventaAsync(bool conComunes = true)
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        if (conComunes)
            await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, @nombre)", ("nombre", EnvioEleventa.DepartamentoComunes));
        await e.ProductoAsync(10, "PLU-FINA", "Pluma punto fino", depto: 5, costo: 4, precio: 9, existencia: 50);
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN) VALUES (41, 1, 1, -1, 'f', CURRENT_TIMESTAMP)");
        return e;
    }

    /// <summary>La pluma de la caja vino del producto 10 de eleventa (como lo deja la importación).</summary>
    private static async Task MapearAsync(ServicioDePrueba servicio, Guid varianteId)
    {
        await using var db = servicio.Base();
        db.MapeosImportacion.Add(new MapeoImportacion { Origen = SincronizacionEleventa.Origen, Entidad = nameof(Variante), IdOrigen = "10", IdNuevo = varianteId });
        await db.SaveChangesAsync();
    }

    [FactConFirebird]
    public async Task Al_cobrar_el_ticket_sale_con_el_folio_de_eleventa_y_la_devolucion_lo_encuentra()
    {
        await using var e = await EleventaAsync();
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio);
        await MapearAsync(servicio, c.Producto.VarianteId);

        var ticket = await CobrarAsync(c);

        Assert.Equal((42, "42"), (ticket.FolioEleventa, ticket.FolioImpreso));
        Assert.StartsWith("A-", ticket.Folio);
        var enEleventa = (await e.FilasAsync("SELECT * FROM VENTATICKETS WHERE FOLIO = 42")).Single();
        Assert.Equal(CajaEleventa, Convert.ToInt32(enEleventa["CAJA_ID"]));
        Assert.Equal(ticket.TotalCobradoCentavos / 100.0, Convert.ToDouble(enEleventa["TOTAL"]), 2);
        // Las líneas de la venta no guardan su orden: se buscan por su código.
        var articulos = await e.FilasAsync("SELECT PRODUCTO_CODIGO, CANTIDAD FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t", ("t", enEleventa["ID"]));
        Assert.Equal(2, articulos.Count);
        var pluma = Assert.Single(articulos, a => (string)a["PRODUCTO_CODIGO"]! == "PLU-FINA");
        Assert.Equal(2, Convert.ToDouble(pluma["CANTIDAD"]), 3);
        Assert.Single(articulos, a => ((string)a["PRODUCTO_CODIGO"]!).StartsWith("COMUN-", StringComparison.Ordinal));
        Assert.Equal(48, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);

        // El folio de eleventa abre la devolución en la caja nueva, y la reimpresión también lo lleva.
        var porFolio = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/42"));
        Assert.Equal(ticket.Folio, porFolio.Folio);
        Assert.Equal(42, (await Leer<TicketDto>(await c.Http.GetAsync($"api/ventas/{ticket.VentaId}/ticket"))).FolioEleventa);
        var envios = await EnviosAsync(c);
        Assert.Equal((true, 0, 0), (envios.Activo, envios.Pendientes, envios.PorRevisar));
    }

    [FactConFirebird]
    public async Task Si_no_entra_queda_en_la_bandeja_y_se_manda_despues_sin_duplicarse()
    {
        // Sin el departamento de los comunes, eleventa no puede recibir el artículo común: no es la red.
        await using var e = await EleventaAsync(conComunes: false);
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio);

        var ticket = await CobrarAsync(c);

        Assert.Null(ticket.FolioEleventa);
        Assert.Equal(ticket.Folio, ticket.FolioImpreso);
        var antes = await EnviosAsync(c);
        Assert.Equal(1, antes.Pendientes);
        Assert.Contains(EnvioEleventa.DepartamentoComunes, antes.PorAtender.Single().Error);

        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, @nombre)", ("nombre", EnvioEleventa.DepartamentoComunes));
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        var despues = await EsperarAsync(c, x => x.Pendientes == 0);

        Assert.Empty(despues.PorAtender);
        Assert.Equal(42, (await Leer<TicketDto>(await c.Http.GetAsync($"api/ventas/{ticket.VentaId}/ticket"))).FolioEleventa);
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE CAJA_ID = 4 AND ESTA_ABIERTO = 'f'")));
    }

    [Fact]
    public async Task Si_eleventa_no_contesta_el_ticket_sale_con_el_folio_de_la_caja_y_el_siguiente_cobro_ya_no_espera()
    {
        // Nadie escucha en ese puerto: como la caja principal apagada.
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes("127.0.0.1", 1, @"C:\eleventa\PDVDATA.FDB", segundosAlCobrar: 20));
        var c = await AbrirAsync(servicio);

        var primero = await CobrarAsync(c);
        var reloj = Stopwatch.StartNew();
        var segundo = await CobrarAsync(c);
        reloj.Stop();

        Assert.Equal((primero.Folio, segundo.Folio), (primero.FolioImpreso, segundo.FolioImpreso));
        Assert.Null(primero.FolioEleventa);
        // Después de la falla de red, el cobro no espera a eleventa: la venta se manda en la vuelta.
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(5), $"El segundo cobro tardó {reloj.Elapsed}.");
        var envios = await EnviosAsync(c);
        Assert.Equal(2, envios.Pendientes);
        Assert.StartsWith("No contesta el eleventa de 127.0.0.1", envios.UltimoError);
        Assert.True(envios.PorAtender[0].Intentos >= 1);
    }

    [Fact]
    public async Task Lo_cobrado_con_tarjeta_queda_por_revisar()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes("127.0.0.1", 1, @"C:\eleventa\PDVDATA.FDB"));
        var c = await AbrirAsync(servicio);

        var ticket = await CobrarAsync(c, FormaPago.Tarjeta);

        Assert.Null(ticket.FolioEleventa);
        var envios = await EnviosAsync(c);
        Assert.Equal((0, 1), (envios.Pendientes, envios.PorRevisar));
        Assert.Equal((EstadoEnvioEleventa.PorRevisar, 0), (envios.PorAtender.Single().Estado, envios.PorAtender.Single().Intentos));
        Assert.Contains("tarjeta", envios.PorAtender.Single().Error);
    }

    [Fact]
    public async Task Sin_escribir_a_eleventa_no_hay_bandeja()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await AbrirAsync(servicio);

        var ticket = await CobrarAsync(c);

        Assert.Null(ticket.FolioEleventa);
        var envios = await EnviosAsync(c);
        Assert.Equal((false, 0, null), (envios.Activo, envios.Pendientes, envios.Problema));
    }

    private static async Task<SesionDto> EncargadoAsync(Caja c) =>
        await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));

    [FactConFirebird]
    public async Task La_cancelacion_la_devolucion_y_el_corte_z_llegan_a_eleventa_en_orden()
    {
        await using var e = await EleventaAsync();
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, USUARIO) VALUES (7, 'cajera2')");
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio);
        await MapearAsync(servicio, c.Producto.VarianteId);
        var encargado = await EncargadoAsync(c);

        var primero = await CobrarAsync(c);
        var segundo = await CobrarAsync(c);
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{primero.VentaId}/cancelacion",
            new SolicitudCancelacion(encargado.UsuarioId, c.Datos.Id, "Se arrepintió")));
        var paraDevolver = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{segundo.FolioEleventa}"));
        var pluma = paraDevolver.Lineas.Single(l => l.LlevaInventario);
        var devolucion = await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{segundo.VentaId}/devoluciones",
            new SolicitudDevolucion(encargado.UsuarioId, c.Datos.Id, "No era la que buscaba", [new LineaDevolucionSolicitada(pluma.LineaId, 1000)])));
        Assert.True((await c.Http.PostAsJsonAsync($"api/turnos/{c.Turno.Id}/corte-z",
            new SolicitudCorteZ("encargado", "2222", new Dictionary<long, int> { [50000] = 1 }))).IsSuccessStatusCode);
        var envios = await EsperarAsync(c, x => x.Pendientes == 0);

        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        Assert.Equal((42, 43), (primero.FolioEleventa, segundo.FolioEleventa));
        Assert.Equal("t", (string)(await e.ValorAsync("SELECT ESTA_CANCELADO FROM VENTATICKETS WHERE FOLIO = 42"))!);
        Assert.Equal(["C", "P"], (await e.FilasAsync("SELECT TIPO_DEVOLUCION FROM DEVOLUCIONES ORDER BY ID")).Select(d => (string)d["TIPO_DEVOLUCION"]!));
        // 50 − 2 − 2 + 2 de la cancelación + 1 de la devolución.
        Assert.Equal(49, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);
        // Se contó solo el billete de 500: faltó lo de la segunda venta menos lo devuelto, y eleventa cierra con lo contado.
        var turno = (await e.FilasAsync("SELECT * FROM TURNOS")).Single();
        Assert.NotNull(turno["TERMINO_EN"]);
        var falto = (segundo.TotalCobradoCentavos - devolucion.ReembolsoEfectivoCentavos) / 100m;
        Assert.Equal((500m, falto), ((decimal)turno["EFECTIVO_AL_CIERRE"]!, (decimal)turno["ACUMULADO_SALIDAS"]!));
        Assert.StartsWith("Efectivo faltante al cerrar turno de ", (string)(await e.ValorAsync("SELECT DESCRIPCION FROM CORTE_MOVIMIENTOS WHERE TIPO = 'Salida'"))!);
        Assert.Equal(0, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE ESTA_ABIERTO = 't'")));
    }

    [Fact]
    public async Task Lo_cancelado_antes_de_llegar_a_eleventa_ya_no_se_manda()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes("127.0.0.1", 1, @"C:\eleventa\PDVDATA.FDB", segundosAlCobrar: 20));
        var c = await AbrirAsync(servicio);
        var encargado = await EncargadoAsync(c);
        var ticket = await CobrarAsync(c);
        Assert.Equal(1, (await EnviosAsync(c)).Pendientes);

        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion",
            new SolicitudCancelacion(encargado.UsuarioId, c.Datos.Id, "Se arrepintió")));
        var envios = await EsperarAsync(c, x => x.Pendientes == 0);

        // La venta y su cancelación se descartan sin buscar a eleventa: allá nunca existió.
        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        await using var db = servicio.Base();
        Assert.All(db.EnviosAEleventa.ToList(), x => Assert.Equal(EstadoEnvioEleventa.Descartada, x.Estado));
        Assert.Equal([TipoEnvioEleventa.Venta, TipoEnvioEleventa.Cancelacion], db.EnviosAEleventa.Select(x => x.Tipo).AsEnumerable().Order());
    }

    [Fact]
    public async Task Lo_que_no_entra_se_puede_dejar_por_revisar_para_que_lo_demas_pase()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes("127.0.0.1", 1, @"C:\eleventa\PDVDATA.FDB", segundosAlCobrar: 20));
        var c = await AbrirAsync(servicio);
        await CobrarAsync(c);
        var pendiente = (await EnviosAsync(c)).PorAtender.Single();

        var dejado = await Leer<EstadoEnviosEleventaDto>(await c.Http.PostAsync($"api/eleventa/envios/{pendiente.Id}/revisar", null));
        var otraVez = await c.Http.PostAsync($"api/eleventa/envios/{pendiente.Id}/revisar", null);

        Assert.Equal((0, 1), (dejado.Pendientes, dejado.PorRevisar));
        Assert.StartsWith("Se dejó para revisar a mano.", dejado.PorAtender.Single().Error);
        Assert.Equal(HttpStatusCode.BadRequest, otraVez.StatusCode);
    }

    private static async Task<EstadoEnviosEleventaDto> EsperarAsync(Caja c, Func<EstadoEnviosEleventaDto, bool> listo)
    {
        for (var intento = 0; ; intento++)
        {
            var estado = await EnviosAsync(c);
            if (listo(estado) || intento == 100)
                return estado;
            await Task.Delay(100);
        }
    }
}
