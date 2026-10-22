using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La pestaña Reportes (entrega 9, parte 1), sin navegador, contra el servicio: sale solo con «Ver reportes de ventas» (también
/// en «/»), cambia de periodo con sus botones y con ← y →, la utilidad solo con «Ver costos, utilidad y margen», «Ver como tabla»,
/// lo que se recuerda al ir a vender y volver, F1 y Esc, y la nota del piloto. Las cifras se prueban en el servicio
/// (ReportesDeVentasTests); aquí, lo que se ve. Nada depende de la hora a la que corren: el año pasado de hoy se arma con un
/// ticket del historial de ese día, que cuenta completo en la gráfica.
/// </summary>
public sealed class ReportesTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

    /// <param name="reportes">Entre la caja y el servicio, si la prueba contesta el reporte (<see cref="ReporteDePrueba"/>).</param>
    private static async Task<Caja> PrepararAsync(ServicioDePrueba servicio, ReporteDePrueba? reportes = null)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5080/") };
        var cliente = new ClienteCaja(new HttpClient(reportes ?? servicio.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("encargado", "2222"),
            await cliente.EntrarAsync("dueno", "3333"));
    }

    /// <summary>Entre la caja y el servicio de la prueba: el reporte de ventas lo contesta la prueba, si quiere; lo demás pasa.</summary>
    private sealed class ReporteDePrueba(HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        /// <summary>La respuesta al reporte que se pide; vacía, la del servicio.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage?>? Contestar { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath.EndsWith("/reportes/ventas", StringComparison.Ordinal) && Contestar?.Invoke(request) is { } respuesta
                ? respuesta
                : await base.SendAsync(request, cancellationToken);
    }

    private static HttpResponseMessage Json(object valor, System.Net.HttpStatusCode estado = System.Net.HttpStatusCode.OK) =>
        new(estado) { Content = JsonContent.Create(valor, valor.GetType(), options: System.Text.Json.JsonSerializerOptions.Web) };

    /// <summary>La pestaña Reportes sola, como la abre la venta.</summary>
    private static async Task<PantallaSinNavegador> ReportesAsync(Caja c, SesionDto quien)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Reportes>(new()
        {
            [nameof(Reportes.Caja)] = c.Datos,
            [nameof(Reportes.Sesion)] = quien,
            [nameof(Reportes.Estado)] = new ReportesVistos(),
        });
        return p;
    }

    /// <summary>Deja abierto el turno de quien vende (las pruebas de esta clase comparten la base y no corren en orden fijo).</summary>
    private static async Task<TurnoDto> TurnoDeAsync(Caja c, SesionDto quien)
    {
        if (await c.Cliente.TurnoAsync(c.Datos.Id) is { } abierto)
        {
            if (abierto.CajeroId == quien.UsuarioId)
                return abierto;
            await c.Cliente.CorteZAsync(abierto.Id, new SolicitudCorteZ(c.Encargado.UsuarioId, 50000));
        }
        return await c.Cliente.AbrirTurnoAsync(c.Datos.Id, quien.UsuarioId, 50000);
    }

    private static async Task<(PantallaSinNavegador Pantalla, PantallaVenta Venta)> VentaAsync(Caja c, SesionDto quien, TurnoDto? turno = null)
    {
        turno ??= await TurnoDeAsync(c, quien);
        var p = new PantallaSinNavegador(c.Servicios);
        var venta = await p.AbrirAsync<PantallaVenta>(new()
        {
            [nameof(PantallaVenta.Caja)] = c.Datos,
            [nameof(PantallaVenta.Sesion)] = quien,
            [nameof(PantallaVenta.Turno)] = turno,
        });
        await p.EsperarAsync(t => t.Contains("Copia B/N"), "los rápidos");
        return (p, venta);
    }

    private static Task TeclaAsync(PantallaSinNavegador p, PantallaVenta venta, string tecla) => p.Dispatcher.InvokeAsync(() => venta.Tecla(tecla, null));

    /// <summary>Las pestañas de arriba, como se leen.</summary>
    private static async Task<List<string>> PestanasAsync(PantallaSinNavegador p)
    {
        var elementos = (await p.LeerAsync()).Elementos;
        var nav = elementos.First(e => e.Clase == "vn-pestanas");
        return elementos.SkipWhile(e => e != nav).Skip(1).TakeWhile(e => e.Etiqueta is "button" or "kbd").Where(e => e.Etiqueta == "button")
            .Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
    }

    private static async Task<string> TextoDeAsync(PantallaSinNavegador p, string id) => PantallaSinNavegador.Limpio((await p.PorIdAsync(id)).Texto);

    /// <summary>Abre Reportes con su pestaña y espera a que llegue el reporte.</summary>
    private static async Task AbrirReportesAsync(PantallaSinNavegador p)
    {
        await p.OprimirAsync(await p.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Reportes", "la pestaña Reportes"));
        await p.EsperarElementoAsync("rep-kpi-venta");
    }

    /// <summary>Espera a que el título del periodo diga eso y ya no esté cargando (lo de antes se queda mientras llega lo nuevo).</summary>
    private static async Task EsperarTituloAsync(PantallaSinNavegador p, Func<string, bool> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!((await p.LeerAsync()).Elementos.FirstOrDefault(e => e.Id == "rep-titulo") is { } t && condicion(PantallaSinNavegador.Limpio(t.Texto))
                 && !(await p.TextoAsync()).Contains("Cargando…")))
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"El reporte no llegó a {que}:\n{await p.TextoAsync()}");
            await Task.Delay(20);
        }
    }

    /// <summary>Hoy en la caja principal, y un ticket del historial de eleventa de hace 364 días (el mismo día de la semana).</summary>
    private static async Task<DateOnly> ConAnioPasadoAsync(ServicioDePrueba servicio, CajaDto caja)
    {
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
        await using var db = servicio.Base();
        var variante = db.Variantes.OrderBy(v => v.Id).First().Id;
        var venta = new VentaHistorica
        {
            SucursalId = caja.SucursalId, FolioOrigen = $"prueba-{Guid.NewGuid():N}", TotalCentavos = 12345,
            FechaUtc = HoraMexico.AUtc(hoy.AddDays(-364).ToDateTime(new TimeOnly(0, 1))),
        };
        venta.Lineas.Add(new VentaHistoricaLinea { VentaHistoricaId = venta.Id, VarianteId = variante, Cantidad = Cantidad.Piezas(1), ImporteCentavos = 12345 });
        db.VentasHistoricas.Add(venta);
        await db.SaveChangesAsync();
        return hoy;
    }

    [Fact]
    public async Task La_pestana_sale_solo_con_ver_reportes_de_ventas_tambien_en_la_lista()
    {
        var c = await PrepararAsync(servicio);
        var (cajero, ventaDelCajero) = await VentaAsync(c, c.Cajero);
        await using (cajero)
        {
            // El cajero no lo tiene: ni la pestaña, ni en «/».
            Assert.DoesNotContain("Reportes", await PestanasAsync(cajero));
            await cajero.Dispatcher.InvokeAsync(() => ventaDelCajero.Escribiendo("/report"));
            Assert.DoesNotContain("Reportes de ventas", await cajero.TextoAsync());
            Assert.Empty(cajero.Errores);
        }

        var (p, venta) = await VentaAsync(c, c.Dueno);
        await using var _ = p;
        // El dueño sí, como en el prototipo: después de «Corte» y antes de «Configuración».
        var pestanas = await PestanasAsync(p);
        Assert.Equal(pestanas.IndexOf("Corte") + 1, pestanas.IndexOf("Reportes"));
        Assert.Equal(pestanas.IndexOf("Reportes") + 1, pestanas.IndexOf("Configuración"));
        await p.Dispatcher.InvokeAsync(() => venta.Escribiendo("/report"));
        Assert.Contains("Reportes de ventas", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Los_periodos_cambian_con_sus_botones_y_con_las_flechas()
    {
        var c = await PrepararAsync(servicio);
        var hoy = await ConAnioPasadoAsync(servicio, c.Datos);
        var (p, venta) = await VentaAsync(c, c.Dueno);
        await using var _ = p;

        await AbrirReportesAsync(p);

        // Abre en hoy, contra el mismo día de la semana del año pasado (el ticket de eleventa de ese día hace la línea).
        Assert.StartsWith("Hoy, ", await TextoDeAsync(p, "rep-titulo"));
        Assert.Contains("on", (await p.PorIdAsync("rep-Hoy")).Clase);
        Assert.StartsWith($"Contra el {TextosDeReportes.Dia(hoy.AddDays(-364))}, hasta la misma hora", await TextoDeAsync(p, "rep-contra"));
        Assert.Contains("Año pasado", await TextoDeAsync(p, "grafica"));
        Assert.True((await p.PorIdAsync("rep-siguiente")).Desactivado);
        foreach (var kpi in new[] { "Venta neta", "Tickets", "Ticket promedio", "Utilidad bruta" })
            Assert.Contains(kpi, await p.TextoAsync());
        // La gráfica, por hora: barras, la línea del año pasado y el detalle de cada hora (sale al pasar el ratón).
        var elementos = (await p.LeerAsync()).Elementos;
        Assert.Contains(elementos, e => e.Etiqueta == "svg" && e.Atributos.GetValueOrDefault("role") is "img");
        Assert.Contains(elementos, e => e.Etiqueta == "polyline");
        Assert.Contains(elementos, e => e.Etiqueta == "text" && PantallaSinNavegador.Limpio(e.Texto) == "9");
        Assert.Contains(elementos, e => e.Clase == "vn-rep-tip" && e.Texto.Contains("de 9:00 a 10:00") && e.Texto.Contains("Este año:")
            && e.Texto.Contains($"Año pasado ({TextosDeReportes.DetalleAnterior(PeriodoReporte.Hoy, hoy.AddDays(-364).ToDateTime(new TimeOnly(9, 0)))})"));

        // Los otros periodos, con sus botones.
        await p.OprimirAsync(await p.PorIdAsync("rep-Semana"));
        await EsperarTituloAsync(p, t => t.StartsWith("Esta semana, del ", StringComparison.Ordinal), "la semana");
        Assert.Contains("Esta semana, por día", await TextoDeAsync(p, "grafica"));
        await p.OprimirAsync(await p.PorIdAsync("rep-Mes"));
        await EsperarTituloAsync(p, t => t.EndsWith(" a la fecha", StringComparison.Ordinal) && !char.IsDigit(t[0]), "el mes");
        await p.OprimirAsync(await p.PorIdAsync("rep-Anio"));
        await EsperarTituloAsync(p, t => t == $"{hoy.Year} a la fecha", "el año");
        Assert.Contains($"{hoy.Year} a la fecha, por mes", await TextoDeAsync(p, "grafica"));

        // ← lleva al año anterior, completo (ya se puede ir hacia adelante, y volver al de hoy); → regresa.
        await p.TeclaAsync("reportes", "ArrowLeft");
        await EsperarTituloAsync(p, t => t == (hoy.Year - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "el año pasado");
        Assert.False((await p.PorIdAsync("rep-siguiente")).Desactivado);
        Assert.Contains($"{hoy.Year - 2}", await TextoDeAsync(p, "rep-contra"));
        Assert.NotNull(await p.PorIdAsync("rep-al-de-hoy"));
        await p.TeclaAsync("reportes", "ArrowRight");
        await EsperarTituloAsync(p, t => t == $"{hoy.Year} a la fecha", "el año de hoy otra vez");
        // Hoy, y el día anterior con ←: un día que ya pasó, con «Al de hoy».
        await p.OprimirAsync(await p.PorIdAsync("rep-Hoy"));
        await EsperarTituloAsync(p, t => t.StartsWith("Hoy, ", StringComparison.Ordinal), "hoy");
        await p.OprimirAsync(await p.PorIdAsync("rep-anterior"));
        await EsperarTituloAsync(p, t => !t.StartsWith("Hoy", StringComparison.Ordinal), "ayer");
        Assert.Equal(TextosDeReportes.Dia(hoy.AddDays(-1), hoy.Year), (await TextoDeAsync(p, "rep-titulo")).ToLower(Formato.Mexico));
        await p.OprimirAsync(await p.PorIdAsync("rep-al-de-hoy"));
        await EsperarTituloAsync(p, t => t.StartsWith("Hoy, ", StringComparison.Ordinal), "hoy otra vez");

        // F1 regresa a la venta, como en las otras páginas.
        await TeclaAsync(p, venta, "F1");
        Assert.Null((await p.LeerAsync()).Elementos.FirstOrDefault(e => e.Id == "reportes"));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task La_utilidad_solo_con_ver_costos_utilidad_y_margen()
    {
        var c = await PrepararAsync(servicio);
        // El encargado, con los reportes (al migrar, solo el dueño los tiene) y sin «Ver costos, utilidad y margen».
        var http = servicio.Fabrica.CreateClient();
        var cambio = await http.PutAsJsonAsync($"api/cajas/{c.Datos.Id}/usuarios-y-permisos/{c.Encargado.UsuarioId}",
            new SolicitudPermisoUsuario(c.Dueno.UsuarioId, AccionControlada.VerReportes, NivelPermiso.Permitido));
        Assert.True(cambio.IsSuccessStatusCode, await cambio.Content.ReadAsStringAsync());
        var encargado = await c.Cliente.EntrarAsync("encargado", "2222");
        var (p, venta) = await VentaAsync(c, encargado);
        await using var _ = p;

        await AbrirReportesAsync(p);

        // (Lo fijo del marcado se lee junto, sin espacios: «Utilidad—».)
        var utilidad = await TextoDeAsync(p, "rep-kpi-utilidad");
        Assert.StartsWith("Utilidad—", utilidad);
        Assert.EndsWith("la ve quien tiene «Ver costos, utilidad y margen»", utilidad);
        Assert.DoesNotContain("Utilidad bruta", await p.TextoAsync());
        Assert.DoesNotContain("Margen", await TextoDeAsync(p, "rep-departamentos"));
        Assert.DoesNotContain("La utilidad es lo vendido", await p.TextoAsync());
        Assert.DoesNotContain("restringido", (await p.PorIdAsync("rep-kpi-utilidad")).Clase);

        // El dueño, con todo: la utilidad bruta con su margen (borde punteado) y el margen de cada departamento.
        var (delDueno, _) = await VentaAsync(c, c.Dueno);
        await using (delDueno)
        {
            await AbrirReportesAsync(delDueno);
            Assert.StartsWith("Utilidad bruta", await TextoDeAsync(delDueno, "rep-kpi-utilidad"));
            Assert.Contains("restringido", (await delDueno.PorIdAsync("rep-kpi-utilidad")).Clase);
            Assert.Contains("Margen", await TextoDeAsync(delDueno, "rep-departamentos"));
            Assert.Empty(delDueno.Errores);
        }
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Ver_como_tabla_y_lo_que_se_recuerda_al_ir_a_vender_y_volver()
    {
        var c = await PrepararAsync(servicio);
        var hoy = await ConAnioPasadoAsync(servicio, c.Datos);
        // Una venta de hoy en la caja: la gráfica tiene algo de este año.
        var turno = await TurnoDeAsync(c, c.Dueno);
        await c.Cliente.CobrarAsync(new SolicitudCobro(c.Datos.Id, turno.Id, c.Dueno.UsuarioId, [new LineaSolicitada(null, null, 1000, "Recuerdo", 2500)],
            [new CobroSolicitado(FormaPago.Efectivo, 2500)]));
        var (p, venta) = await VentaAsync(c, c.Dueno, turno);
        await using var _ = p;
        await AbrirReportesAsync(p);
        await p.OprimirAsync(await p.PorIdAsync("rep-Semana"));
        await EsperarTituloAsync(p, t => t.StartsWith("Esta semana", StringComparison.Ordinal), "la semana");

        await p.OprimirAsync(await p.PorIdAsync("rep-ver-tabla"));

        // La misma información, en renglones: los siete días, con el de hoy en curso y el año pasado; ya sin la gráfica.
        var tabla = await TextoDeAsync(p, "rep-tabla");
        // (Lo fijo del marcado se lee junto, sin espacios.)
        Assert.StartsWith("DíaEste añoAño pasadoCambio", tabla);
        var lunes = PapeleriaPos.Nucleo.Reportes.PeriodoDeReporte.Lunes(hoy);
        Assert.All(Enumerable.Range(0, 7), i => Assert.Contains(TextosDeReportes.Detalle(PeriodoReporte.Semana, lunes.AddDays(i).ToDateTime(TimeOnly.MinValue)), tabla));
        Assert.Contains($"{TextosDeReportes.Detalle(PeriodoReporte.Semana, hoy.ToDateTime(TimeOnly.MinValue))} (va en curso)", tabla);
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Clase == "vn-grafica-lienzo");
        Assert.Equal("Ver la gráfica", await TextoDeAsync(p, "rep-ver-tabla"));

        // A vender (F1) y de regreso: la semana, como tabla.
        await TeclaAsync(p, venta, "F1");
        await AbrirReportesAsync(p);
        Assert.Contains("on", (await p.PorIdAsync("rep-Semana")).Clase);
        await p.EsperarElementoAsync("rep-tabla");
        // «Ver la gráfica» la regresa.
        await p.OprimirAsync(await p.PorIdAsync("rep-ver-tabla"));
        Assert.Contains((await p.LeerAsync()).Elementos, e => e.Clase == "vn-grafica-lienzo");
        // Esc, sin nada abierto, también regresa a la venta.
        await TeclaAsync(p, venta, "Escape");
        Assert.Null((await p.LeerAsync()).Elementos.FirstOrDefault(e => e.Id == "reportes"));
        Assert.Empty(p.Errores);
    }

    /// <summary>En el piloto con eleventa: solo lo de la caja 2, sin comparar, y lo dice.</summary>
    [Fact]
    public async Task En_el_piloto_dice_que_solo_es_lo_de_la_caja_2()
    {
        await using var conPiloto = await ServicioDePrueba.CrearAsync(
            ("Eleventa:Escribir", "true"), ("Eleventa:Activo", "false"), ("Eleventa:Servidor", "127.0.0.1"), ("Eleventa:Puerto", "1"),
            ("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"), ("Eleventa:Usuario", "SYSDBA"), ("Eleventa:Clave", "masterkey"), ("Eleventa:Caja", "4"),
            ("Eleventa:Cajero", "7"), ("Eleventa:SegundosAlCobrar", "10"), ("Eleventa:SegundosEntreEnvios", "3600"), ("Eleventa:Minutos", "600"),
            ("Eleventa:RespaldoObligatorio", "false"));
        var c = await PrepararAsync(conPiloto);
        await ConAnioPasadoAsync(conPiloto, c.Datos);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Dueno.UsuarioId, 50000);
        var (p, _) = await VentaAsync(c, c.Dueno, turno);
        await using var __ = p;

        await AbrirReportesAsync(p);

        Assert.Equal(PilotoConEleventa.ReportesDeLaCaja2, await TextoDeAsync(p, "rep-piloto"));
        Assert.StartsWith("Sin comparar", await TextoDeAsync(p, "rep-contra"));
        Assert.Contains("sin comparar en el piloto", await TextoDeAsync(p, "rep-kpi-venta"));
        Assert.DoesNotContain("Año pasado", await TextoDeAsync(p, "grafica"));
        Assert.DoesNotContain("vs. año pasado", await TextoDeAsync(p, "rep-departamentos"));
        Assert.Contains("En el piloto el corte se hace en eleventa", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Hoy todavía sin tickets y el año pasado a estas alturas uno (revisión del PR #131): el ticket promedio dice que hoy no
    /// hay tickets, no que el año pasado no hubo ventas; la utilidad del año pasado, negativa, se dice con su cifra. La prueba
    /// contesta el reporte: así no depende de la hora a la que corre.
    /// </summary>
    [Fact]
    public async Task Sin_tickets_hoy_no_dice_que_el_anio_pasado_no_hubo_ventas()
    {
        var reportes = new ReporteDePrueba(servicio.Fabrica.Server.CreateHandler());
        var c = await PrepararAsync(servicio, reportes);
        var (hoy, antes) = (new DateOnly(2026, 10, 21), new DateOnly(2025, 10, 22));
        reportes.Contestar = _ => Json(new ReporteVentasDto(PeriodoReporte.Hoy, hoy, hoy, hoy, new DateTime(2026, 10, 21, 13, 30, 0), antes, antes,
            new DateTime(2025, 10, 22, 13, 30, 0), new CifrasReporteDto(0, 0, 0, 0, 0, 0, 0, 0, 0, new UtilidadDelDiaDto(0, 0, 0)),
            new CifrasReporteDto(12345, 1, 12345, 0, 0, 0, 0, 12345, 1, new UtilidadDelDiaDto(12345, 13000, 0)),
            [new PuntoReporteDto(new DateTime(2026, 10, 21, 9, 0, 0), new DateTime(2025, 10, 22, 9, 0, 0), 0, 12345)],
            [new DepartamentoReporteDto("Escritura", ClaseVenta.Departamento, 0, 12345, 0)], [], [], [], ConUtilidad: true, Piloto: false));
        var p = await ReportesAsync(c, c.Dueno);
        await using var _ = p;
        await p.EsperarElementoAsync("rep-kpi-venta");

        var promedio = await TextoDeAsync(p, "rep-kpi-promedio");
        Assert.Contains("sin tickets todavía", promedio);
        Assert.DoesNotContain("año pasado", promedio);
        Assert.Contains("▼ 100 % contra el año pasado", await TextoDeAsync(p, "rep-kpi-venta"));
        Assert.Contains("▼ 100 % contra el año pasado", await TextoDeAsync(p, "rep-kpi-tickets"));
        var utilidad = await TextoDeAsync(p, "rep-kpi-utilidad");
        Assert.Contains("el año pasado a estas alturas: −$6.55", utilidad);
        Assert.DoesNotContain("sin ventas el año pasado", utilidad);
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Si el periodo que se pidió no llega (aquí, la caja principal dice que no), las cifras de antes no se quedan bajo ese periodo
    /// (revisión del PR #131): se ve el error, y «Volver a intentar» pide ese periodo otra vez.
    /// </summary>
    [Fact]
    public async Task Si_un_periodo_no_llega_no_se_quedan_las_cifras_de_antes()
    {
        var reportes = new ReporteDePrueba(servicio.Fabrica.Server.CreateHandler());
        var c = await PrepararAsync(servicio, reportes);
        var p = await ReportesAsync(c, c.Dueno);
        await using var _ = p;
        await p.EsperarElementoAsync("rep-kpi-venta");
        Assert.StartsWith("Hoy, ", await TextoDeAsync(p, "rep-titulo"));

        const string NoContesta = "La caja principal no deja verlo ahora.";
        reportes.Contestar = r => r.RequestUri!.Query.Contains("periodo=Mes", StringComparison.Ordinal)
            ? Json(new ErrorDto(NoContesta, "no-permitido"), System.Net.HttpStatusCode.Forbidden)
            : null;
        await p.OprimirAsync(await p.PorIdAsync("rep-Mes"));
        await p.EsperarAsync(t => t.Contains(NoContesta), "el error");

        // Ni las cifras ni el título de hoy bajo «Mes»: sin reporte, las flechas no mueven nada.
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id is "rep-kpi-venta" or "grafica" or "rep-departamentos");
        Assert.Equal("", await TextoDeAsync(p, "rep-titulo"));
        Assert.Contains("on", (await p.PorIdAsync("rep-Mes")).Clase);
        Assert.True((await p.PorIdAsync("rep-anterior")).Desactivado);

        // Ya contesta: «Volver a intentar» trae el mes.
        reportes.Contestar = null;
        await p.OprimirAsync(await p.BotonAsync("Volver a intentar"));
        await EsperarTituloAsync(p, t => t.EndsWith(" a la fecha", StringComparison.Ordinal) && !char.IsDigit(t[0]), "el mes");
        Assert.NotNull(await p.PorIdAsync("rep-kpi-venta"));
        Assert.DoesNotContain(NoContesta, await p.TextoAsync());
        Assert.Empty(p.Errores);
    }
}
