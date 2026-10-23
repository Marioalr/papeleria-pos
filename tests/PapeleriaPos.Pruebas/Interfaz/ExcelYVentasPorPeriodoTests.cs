using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Reportes;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La entrega 9, parte 2, sin navegador, contra el servicio: «Exportar a Excel» en Reportes y en los reportes del inventario
/// (el archivo se guarda con el «Guardar como» de mentiras de las pruebas y la pantalla dice dónde quedó, o que se canceló), y
/// «Ventas por período»: se abre desde Reportes, sus días de un toque y con ← →, los tickets con sus totales, «Imprimir el
/// resumen» por la cola de la impresora (o en pantalla, sin impresora), su Excel, Esc de regreso a los reportes y lo que se
/// recuerda, y la nota del piloto. Nada depende de la hora a la que corren: lo esperado se calcula con el día de hoy.
/// </summary>
public sealed class ExcelYVentasPorPeriodoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(
        ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, GuardadoDePrueba Guardado, SesionDto Encargado, SesionDto Dueno, PaginaQueContesta Pagina);

    /// <param name="envolver">Lo que se pone entre la caja y el servicio (una red lenta, lo que anota lo que se pidió).</param>
    private static async Task<Caja> PrepararAsync(ServicioDePrueba servicio, IImpresora? impresora = null, Func<HttpMessageHandler, HttpMessageHandler>? envolver = null)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5080/") };
        var manejador = servicio.Fabrica.Server.CreateHandler();
        var cliente = new ClienteCaja(new HttpClient(envolver is null ? manejador : envolver(manejador)), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var guardado = new GuardadoDePrueba();
        var pagina = new PaginaQueContesta();
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton(impresora ?? new SinImpresora())
            .AddSingleton<IGuardarArchivo>(guardado)
            .AddSingleton<IJSRuntime>(pagina)
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, guardado, await cliente.EntrarAsync("encargado", "2222"), await cliente.EntrarAsync("dueno", "3333"), pagina);
    }

    private static DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));

    private static string Iso(DateOnly dia) => dia.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Un ticket de hoy en la caja (un artículo común): con el turno que haya, o uno del dueño. En efectivo pagan con un billete de
    /// $1,000 (el efectivo se cobra al peso); con tarjeta, exacto.
    /// </summary>
    private static async Task<TicketDto> VenderAsync(Caja c, long centavos, FormaPago forma = FormaPago.Efectivo)
    {
        var turno = await c.Cliente.TurnoAsync(c.Datos.Id) ?? await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Dueno.UsuarioId, 50000);
        return await c.Cliente.CobrarAsync(new SolicitudCobro(c.Datos.Id, turno.Id, turno.CajeroId, [new LineaSolicitada(null, null, 1000, "Recuerdo", centavos)],
            [forma == FormaPago.Efectivo ? new CobroSolicitado(FormaPago.Efectivo, 100_000) : new CobroSolicitado(forma, centavos, "Aut 9")]));
    }

    /// <summary>Reportes, solo la página (sin la venta), con lo que se recuerda de ella.</summary>
    private static async Task<(PantallaSinNavegador Pantalla, Reportes Reportes)> ReportesAsync(Caja c, SesionDto quien, ReportesVistos? estado = null)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        var reportes = await p.AbrirAsync<Reportes>(new()
        {
            [nameof(Reportes.Caja)] = c.Datos,
            [nameof(Reportes.Sesion)] = quien,
            [nameof(Reportes.Estado)] = estado ?? new ReportesVistos(),
        });
        // Abre donde se quedó: el resumen, o «Ventas por período».
        await p.EsperarElementoAsync(estado?.PorPeriodo.Abierta == true ? "vpp-totales" : "rep-kpi-venta");
        return (p, reportes);
    }

    private static async Task<string> TextoDeAsync(PantallaSinNavegador p, string id) => PantallaSinNavegador.Limpio((await p.PorIdAsync(id)).Texto);

    /// <summary>Espera a que lo que dice ese elemento cumpla (lo que se carga o se guarda en segundo plano).</summary>
    private static async Task<string> EsperarTextoAsync(PantallaSinNavegador p, string id, Func<string, bool> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            if ((await p.LeerAsync()).Elementos.FirstOrDefault(e => e.Id == id) is { } elemento && PantallaSinNavegador.Limpio(elemento.Texto) is var texto
                && condicion(texto) && !(await p.TextoAsync()).Contains("Cargando…"))
                return texto;
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No llegó {que}:\n{await p.TextoAsync()}");
            await Task.Delay(20);
        }
    }

    private static XLWorkbook Abrir(ArchivoPorGuardar archivo) => new(new MemoryStream(archivo.Datos));

    [Fact]
    public async Task Exportar_a_excel_en_reportes_lo_guarda_y_dice_donde_o_que_se_cancelo()
    {
        var c = await PrepararAsync(servicio);
        await VenderAsync(c, 4500);
        var (p, _) = await ReportesAsync(c, c.Dueno);
        await using var __ = p;

        await p.OprimirAsync(await p.PorIdAsync("rep-excel"));
        var dicho = await EsperarTextoAsync(p, "rep-excel-estado", t => t.Length > 0, "dónde se guardó");

        var nombre = $"Ventas {Iso(Hoy)}.xlsx";
        Assert.Equal($@"Se guardó en {GuardadoDePrueba.Carpeta}\{nombre}.", dicho);
        var archivo = Assert.Single(c.Guardado.Guardados);
        Assert.Equal((nombre, ArchivosDeReportes.TipoExcel), (archivo.Nombre, archivo.Tipo));
        using (var libro = Abrir(archivo))
            Assert.Equal("Resumen", libro.Worksheets.First().Name);
        // El botón vuelve a decir lo suyo.
        Assert.Equal("Exportar a Excel", await TextoDeAsync(p, "rep-excel"));

        // Si se cancela «Guardar como», lo dice.
        c.Guardado.Cancelar = true;
        await p.OprimirAsync(await p.PorIdAsync("rep-excel"));
        await EsperarTextoAsync(p, "rep-excel-estado", t => t == ArchivoGuardado.Cancelado, "que se canceló");
        Assert.Equal(2, c.Guardado.Guardados.Count);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Ventas_por_periodo_se_abre_en_reportes_con_sus_tickets_sus_totales_y_sus_dias()
    {
        var c = await PrepararAsync(servicio);
        var efectivo = await VenderAsync(c, 3000);
        var tarjeta = await VenderAsync(c, 1250, FormaPago.Tarjeta);
        var estado = new ReportesVistos();
        var (p, reportes) = await ReportesAsync(c, c.Dueno, estado);
        await using var _ = p;

        await p.OprimirAsync(await p.PorIdAsync("rep-por-periodo"));
        await p.EsperarElementoAsync("vpp-totales");

        // Abre en hoy (el de un toque, encendido), con los tickets de hoy y los totales de todos.
        Assert.Equal(TextosDeReportes.Periodo(Hoy, Hoy, Hoy), await TextoDeAsync(p, "vpp-titulo"));
        Assert.Contains("on", (await p.PorIdAsync("vpp-Hoy")).Clase);
        var tickets = await TextoDeAsync(p, "vpp-tickets");
        Assert.StartsWith("FolioFecha y horaCajaCajeroForma de pagoTotal", tickets);
        Assert.Contains($"{efectivo.Folio}", tickets);
        Assert.Contains($"{tarjeta.Folio}", tickets);
        Assert.Contains("Tarjeta $12.50", tickets);
        var deHoy = await c.Cliente.VentasPorPeriodoAsync(c.Datos.Id, c.Dueno.UsuarioId, null, null, null);
        var totales = await TextoDeAsync(p, "vpp-totales");
        Assert.Contains($"Tickets {Formato.Numero(deHoy.Totales.Tickets)}", totales);
        Assert.Contains($"Vendido {Formato.Dinero(deHoy.Totales.VendidoCentavos)}", totales);
        Assert.Contains($"Venta neta {Formato.Dinero(deHoy.Totales.VentaCentavos)}", totales);
        Assert.Contains($"Tarjeta {Formato.Dinero(deHoy.Totales.Formas.Single(f => f.Forma == FormaPago.Tarjeta).Centavos)}", await TextoDeAsync(p, "vpp-notas"));

        // «Ayer» y «Este mes»; ← lleva a los días de antes, del mismo largo.
        await p.OprimirAsync(await p.PorIdAsync("vpp-Ayer"));
        await EsperarTextoAsync(p, "vpp-titulo", t => t == TextosDeReportes.Periodo(Hoy.AddDays(-1), Hoy.AddDays(-1), Hoy), "ayer");
        await p.TeclaAsync("ventas-periodo", "ArrowLeft");
        await EsperarTextoAsync(p, "vpp-titulo", t => t == TextosDeReportes.Periodo(Hoy.AddDays(-2), Hoy.AddDays(-2), Hoy), "antier");
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Clase?.Contains("vn-filtro on") == true);
        await p.TeclaAsync("ventas-periodo", "ArrowRight");
        await p.TeclaAsync("ventas-periodo", "ArrowRight");
        await EsperarTextoAsync(p, "vpp-titulo", t => t == TextosDeReportes.Periodo(Hoy, Hoy, Hoy), "hoy, con →");
        await p.OprimirAsync(await p.PorIdAsync("vpp-EsteMes"));
        var (desdeMes, hastaMes) = DiasDeVentas.De(DiasElegidos.EsteMes, Hoy);
        await EsperarTextoAsync(p, "vpp-titulo", t => t == TextosDeReportes.Periodo(desdeMes, hastaMes, Hoy), "este mes");
        Assert.Equal(Iso(desdeMes), (await p.PorIdAsync("vpp-desde")).Valor);

        // Esc (desde la venta, la página cierra lo de adentro) regresa al resumen; al volver, el mismo mes.
        Assert.True(await p.Dispatcher.InvokeAsync(() => ((IPagina)reportes).CerrarAsync()));
        await p.EsperarElementoAsync("rep-kpi-venta");
        Assert.False(estado.PorPeriodo.Abierta);
        Assert.False(await p.Dispatcher.InvokeAsync(() => ((IPagina)reportes).CerrarAsync()));
        await p.OprimirAsync(await p.PorIdAsync("rep-por-periodo"));
        await EsperarTextoAsync(p, "vpp-titulo", t => t == TextosDeReportes.Periodo(desdeMes, hastaMes, Hoy), "el mismo mes al volver");
        Assert.Contains("on", (await p.PorIdAsync("vpp-EsteMes")).Clase);
        // «← Reportes» también regresa.
        await p.OprimirAsync(await p.PorIdAsync("vpp-volver"));
        await p.EsperarElementoAsync("rep-kpi-venta");
        Assert.Empty(p.Errores);
    }

    /// <summary>Una impresora que guarda lo que le llega, para leer lo que salió en el papel (la página 850).</summary>
    private sealed class ImpresoraQueGuarda : IImpresora
    {
        private readonly List<byte[]> _enviados = [];

        public bool MuestraVistaPrevia => false;

        public string Texto
        {
            get
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                lock (_enviados)
                    return string.Concat(_enviados.Select(Encoding.GetEncoding(850).GetString));
            }
        }

        public Task EnviarAsync(byte[] escPos, CancellationToken ct = default)
        {
            lock (_enviados)
                _enviados.Add(escPos);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Imprimir_el_resumen_va_por_la_cola_de_la_impresora_y_el_excel_lleva_todos_los_tickets()
    {
        var impresora = new ImpresoraQueGuarda();
        var c = await PrepararAsync(servicio, impresora);
        var vendido = await VenderAsync(c, 2250);
        var (p, _) = await ReportesAsync(c, c.Dueno, new ReportesVistos { PorPeriodo = { Abierta = true } });
        await using var __ = p;
        await p.EsperarElementoAsync("vpp-totales");

        await p.OprimirAsync(await p.PorIdAsync("vpp-imprimir"));

        // Por la cola, sin esperarla: lo de cada día y los totales, no cada ticket.
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!impresora.Texto.Contains("Venta neta") && DateTime.UtcNow < limite)
            await Task.Delay(20);
        var papel = impresora.Texto;
        Assert.Contains("VENTAS POR PERÍODO", papel);
        Assert.Contains("Lo imprimió: Dueño de prueba", papel);
        Assert.Contains("POR DÍA", papel);
        Assert.Contains("Venta neta", papel);
        Assert.DoesNotContain(vendido.Folio, papel);
        await p.EsperarAsync(t => t.Contains("impreso"), "que salió");

        // El Excel: todos los tickets de esos días y cada día.
        await p.OprimirAsync(await p.PorIdAsync("vpp-excel"));
        await EsperarTextoAsync(p, "vpp-excel-estado", t => t.StartsWith("Se guardó en", StringComparison.Ordinal), "dónde se guardó");
        var archivo = Assert.Single(c.Guardado.Guardados);
        Assert.Equal($"Ventas por período {Iso(Hoy)}.xlsx", archivo.Nombre);
        using var libro = Abrir(archivo);
        Assert.Equal(["Tickets", "Por día"], libro.Worksheets.Select(h => h.Name));
        Assert.Contains(libro.Worksheet("Tickets").Tables.Single().DataRange.Rows(), r => r.Cell(1).GetString() == vendido.Folio);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Sin_impresora_el_resumen_se_ve_en_pantalla()
    {
        var c = await PrepararAsync(servicio);
        await VenderAsync(c, 1800);
        var (p, _) = await ReportesAsync(c, c.Dueno, new ReportesVistos { PorPeriodo = { Abierta = true } });
        await using var __ = p;
        await p.EsperarElementoAsync("vpp-totales");

        await p.OprimirAsync(await p.PorIdAsync("vpp-imprimir"));

        var impreso = await TextoDeAsync(p, "vpp-impreso");
        Assert.Contains("VENTAS POR PERÍODO", impreso);
        Assert.Contains("TOTALES", impreso);
        Assert.Contains("Sin impresora: abajo de la lista, en «Así sale impreso».", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_el_piloto_ventas_por_periodo_dice_que_solo_es_lo_de_la_caja_2()
    {
        await using var conPiloto = await ServicioDePrueba.CrearAsync(
            ("Eleventa:Escribir", "true"), ("Eleventa:Activo", "false"), ("Eleventa:Servidor", "127.0.0.1"), ("Eleventa:Puerto", "1"),
            ("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"), ("Eleventa:Usuario", "SYSDBA"), ("Eleventa:Clave", "masterkey"), ("Eleventa:Caja", "4"),
            ("Eleventa:Cajero", "7"), ("Eleventa:SegundosAlCobrar", "10"), ("Eleventa:SegundosEntreEnvios", "3600"), ("Eleventa:Minutos", "600"),
            ("Eleventa:RespaldoObligatorio", "false"));
        var c = await PrepararAsync(conPiloto);
        var (p, _) = await ReportesAsync(c, c.Dueno, new ReportesVistos { PorPeriodo = { Abierta = true } });
        await using var __ = p;
        await p.EsperarElementoAsync("vpp-totales");

        Assert.Equal(PilotoConEleventa.VentasPorPeriodoDeLaCaja2, await TextoDeAsync(p, "vpp-piloto"));
        Assert.DoesNotContain("historial de eleventa", await TextoDeAsync(p, "vpp-notas"));
        Assert.Empty(p.Errores);
    }

    /// <summary>Un producto con <paramref name="existencia"/> piezas, su mínimo y su máximo (para que salga en bajos), en el primer departamento.</summary>
    private async Task<FichaProductoDto> ProductoAsync(Caja c, string nombre, int existencia)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var ficha = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                    [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto($"EP{Guid.NewGuid():N}"[..14])])], [new PrecioFichaDto(null, 1000, 2000)],
                    MinimoYMaximo: new MinimoYMaximoDto(5000, 10000)),
            ])));
        await using var db = servicio.Base();
        var variante = await db.Variantes.SingleAsync(v => v.Id == ficha.Variantes[0].Id);
        await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(c.Datos.SucursalId, variante, TipoMovimiento.InventarioInicial,
            Cantidad.Piezas(existencia), existencia * 1000L, DateTime.UtcNow, documentoTipo: OrigenDelMovimiento.ImportacionEleventa));
        return ficha;
    }

    private static async Task<PantallaSinNavegador> InventarioAsync(Caja c, SesionDto quien, TareaInventario tarea)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Inventario>(new()
        {
            [nameof(Inventario.Caja)] = c.Datos,
            [nameof(Inventario.Sesion)] = quien,
            [nameof(Inventario.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Inventario.EtiquetasPorImprimir)] = new EtiquetasPorImprimir(),
            [nameof(Inventario.TareaInicial)] = tarea,
        });
        return p;
    }

    [Fact]
    public async Task Los_reportes_del_inventario_se_exportan_a_excel_con_lo_que_se_ve()
    {
        var c = await PrepararAsync(servicio);
        await ProductoAsync(c, "Pantalla excel de prueba", 2);

        // Los movimientos, como el encargado (sin el costo).
        await using (var p = await InventarioAsync(c, c.Encargado, TareaInventario.Movimientos))
        {
            await p.EscribirAsync("mov-buscar", "pantalla excel");
            await p.EsperarAsync(t => t.Contains("Pantalla excel de prueba"), "los movimientos de hoy");
            await p.OprimirAsync(await p.PorIdAsync("mov-excel"));
            await EsperarTextoAsync(p, "mov-excel-estado", t => t.StartsWith("Se guardó en", StringComparison.Ordinal), "dónde se guardó");
            Assert.Empty(p.Errores);
        }
        // Bajos en inventario, como el encargado.
        await using (var p = await InventarioAsync(c, c.Encargado, TareaInventario.Bajos))
        {
            await p.EscribirAsync("bajos-buscar", "pantalla excel");
            await p.EsperarAsync(t => t.Contains("Pantalla excel de prueba"), "los bajos");
            await p.OprimirAsync(await p.PorIdAsync("bajos-excel"));
            await EsperarTextoAsync(p, "bajos-excel-estado", t => t.StartsWith("Se guardó en", StringComparison.Ordinal), "dónde se guardó");
            Assert.Empty(p.Errores);
        }
        // Lo que vale, como el dueño (es todo costo).
        await using (var p = await InventarioAsync(c, c.Dueno, TareaInventario.Valor))
        {
            await p.EsperarElementoAsync("valor-total");
            await p.OprimirAsync(await p.PorIdAsync("valor-excel"));
            await EsperarTextoAsync(p, "valor-excel-estado", t => t.StartsWith("Se guardó en", StringComparison.Ordinal), "dónde se guardó");
            Assert.Empty(p.Errores);
        }

        Assert.Equal([$"Movimientos {Iso(Hoy)}.xlsx", $"Bajos en inventario {Iso(Hoy)}.xlsx", $"Lo que vale el inventario {Iso(Hoy)}.xlsx"],
            c.Guardado.Guardados.Select(g => g.Nombre));
        using (var movimientos = Abrir(c.Guardado.Guardados[0]))
        {
            var tabla = movimientos.Worksheet("Movimientos").Tables.Single();
            Assert.DoesNotContain("Costo", tabla.Fields.Select(f => f.Name));
            // Con lo que se buscó en la pantalla.
            Assert.All(tabla.DataRange.Rows(), r => Assert.Equal("Pantalla excel de prueba", r.Field("Producto").GetString()));
        }
        using (var bajos = Abrir(c.Guardado.Guardados[1]))
            Assert.Equal(["Pantalla excel de prueba"], bajos.Worksheet("Bajos en inventario").Tables.Single().DataRange.Rows().Select(r => r.Field("Producto").GetString()));
        using (var valor = Abrir(c.Guardado.Guardados[2]))
            Assert.Equal(["Por departamento", "Productos"], valor.Worksheets.Select(h => h.Name));
    }

    // ── Lo que encontró la revisión del PR #134 ──

    /// <summary>Lo que la caja le pide al servicio, en orden; y, si se le dice, una espera antes de «Ventas por período».</summary>
    private sealed class RedQueAnota(HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        private readonly List<string> _pedidos = [];

        public TimeSpan EsperaPorPeriodo { get; set; }

        public IReadOnlyList<string> Pedidos
        {
            get
            {
                lock (_pedidos)
                    return [.. _pedidos];
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_pedidos)
                _pedidos.Add(request.RequestUri!.PathAndQuery);
            if (EsperaPorPeriodo > TimeSpan.Zero && request.RequestUri.AbsolutePath.EndsWith("/ventas-por-periodo", StringComparison.Ordinal))
                await Task.Delay(EsperaPorPeriodo, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// El Excel es del reporte que se ve: lleva su primer día, no «hoy» (pasada la medianoche, el de la caja principal ya sería
    /// otro periodo); y al terminar, el cursor vuelve al botón (mientras trabajaba estaba apagado y el navegador soltó el foco).
    /// </summary>
    [Fact]
    public async Task El_excel_es_del_periodo_que_se_ve_y_el_cursor_vuelve_al_boton()
    {
        RedQueAnota? red = null;
        var c = await PrepararAsync(servicio, envolver: m => red = new RedQueAnota(m));
        await VenderAsync(c, 4500);
        var (p, _) = await ReportesAsync(c, c.Dueno, new ReportesVistos { Periodo = PeriodoReporte.Semana });
        await using var __ = p;

        await p.OprimirAsync(await p.PorIdAsync("rep-excel"));
        await EsperarTextoAsync(p, "rep-excel-estado", t => t.StartsWith("Se guardó en", StringComparison.Ordinal), "dónde se guardó");

        Assert.Contains(red!.Pedidos, x => x.Contains("/reportes/ventas/excel?", StringComparison.Ordinal) && x.Contains("periodo=Semana", StringComparison.Ordinal)
            && x.Contains($"fecha={Iso(PeriodoDeReporte.Lunes(Hoy))}", StringComparison.Ordinal));
        await p.EsperarAsync(_ => c.Pagina.Llamadas.Any(l => l.Funcion == "enfocar" && Equals(l.Argumentos?[0], "#rep-excel")), "el cursor en el botón");
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Av Pág dos veces seguidas cerca del final (la tecla sostenida) mientras la caja principal contesta: se queda en la última,
    /// no regresa a la primera; y una página recordada que ya no existe lleva a la última. Con su propio servicio: llena tres
    /// páginas de tickets de hoy.
    /// </summary>
    [Fact]
    public async Task Av_pag_sostenida_no_pasa_de_la_ultima_pagina()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        RedQueAnota? red = null;
        var c = await PrepararAsync(propio, envolver: m => red = new RedQueAnota(m));
        for (var i = (await c.Cliente.VentasPorPeriodoAsync(c.Datos.Id, c.Dueno.UsuarioId, null, null, null)).Total; i < 201; i++)
            await VenderAsync(c, 1000);
        var hoy = await c.Cliente.VentasPorPeriodoAsync(c.Datos.Id, c.Dueno.UsuarioId, null, null, null);
        var ultima = (hoy.Total - 1) / hoy.Tamano;
        Assert.True(ultima >= 2);

        var estado = new ReportesVistos { PorPeriodo = { Abierta = true, Pagina = ultima - 1 } };
        var (p, _) = await ReportesAsync(c, c.Dueno, estado);
        await using (p)
        {
            await EsperarTextoAsync(p, "vpp-totales", _ => true, "la penúltima página");
            Assert.Equal(ultima - 1, estado.PorPeriodo.Pagina);

            red!.EsperaPorPeriodo = TimeSpan.FromMilliseconds(400);
            await Task.WhenAll(p.TeclaAsync("ventas-periodo", "PageDown"), p.TeclaAsync("ventas-periodo", "PageDown"));
            red.EsperaPorPeriodo = TimeSpan.Zero;

            await EsperarTextoAsync(p, "vpp-totales", _ => true, "la última página");
            Assert.Equal(ultima, estado.PorPeriodo.Pagina);
            Assert.Contains($"de {Formato.Numero(hoy.Total)} tickets", await p.TextoAsync());
            Assert.Contains($"{Formato.Numero(ultima * hoy.Tamano + 1)}–{Formato.Numero(hoy.Total)}", await p.TextoAsync());
            Assert.Empty(p.Errores);
        }

        // Una página recordada que ya no existe (la de otro día, con más tickets): la última, no la primera.
        var recordada = new ReportesVistos { PorPeriodo = { Abierta = true, Pagina = 50 } };
        var (otra, _) = await ReportesAsync(c, c.Dueno, recordada);
        await using (otra)
        {
            await EsperarTextoAsync(otra, "vpp-totales", _ => true, "la última página");
            Assert.Equal(ultima, recordada.PorPeriodo.Pagina);
            Assert.Empty(otra.Errores);
        }
    }

    /// <summary>
    /// Un año mal escrito («26» en «Del») ya no pide siglos de días: los campos tienen su mínimo (2000), lo escrito se queda en
    /// 2000 y en tres años, y ← no truena. Unos días escritos, cortados en hoy con →, regresan con ← a su mismo largo.
    /// </summary>
    [Fact]
    public async Task Las_fechas_escritas_van_de_2000_a_hoy_y_las_flechas_guardan_su_largo()
    {
        var c = await PrepararAsync(servicio);
        var (p, _) = await ReportesAsync(c, c.Dueno, new ReportesVistos { PorPeriodo = { Abierta = true } });
        await using var __ = p;
        Assert.Equal("2000-01-01", (await p.PorIdAsync("vpp-desde")).Atributos["min"]);
        Assert.Equal("2000-01-01", (await p.PorIdAsync("vpp-hasta")).Atributos["min"]);

        await p.DispararAsync(await p.PorIdAsync("vpp-desde"), "onchange", new ChangeEventArgs { Value = "0026-10-01" });
        var tresAnios = TextosDeReportes.Periodo(new DateOnly(2000, 1, 1), new DateOnly(2002, 12, 31), Hoy);
        await EsperarTextoAsync(p, "vpp-titulo", t => t == tresAnios, "de 2000, tres años");
        await p.TeclaAsync("ventas-periodo", "ArrowLeft");
        Assert.Equal(tresAnios, await TextoDeAsync(p, "vpp-titulo"));
        Assert.DoesNotContain(ReporteVentasPorPeriodo.AntesDelPrimerDia, await p.TextoAsync());
        Assert.DoesNotContain(ReporteVentasPorPeriodo.MasDeTresAnios, await p.TextoAsync());

        // Del Hoy−10 al Hoy−5 (seis días): → los corta en hoy (del Hoy−4 a hoy) y ← regresa a los mismos seis, no a cinco.
        await p.DispararAsync(await p.PorIdAsync("vpp-hasta"), "onchange", new ChangeEventArgs { Value = Iso(Hoy.AddDays(-5)) });
        await p.DispararAsync(await p.PorIdAsync("vpp-desde"), "onchange", new ChangeEventArgs { Value = Iso(Hoy.AddDays(-10)) });
        await EsperarTextoAsync(p, "vpp-titulo", t => t == TextosDeReportes.Periodo(Hoy.AddDays(-10), Hoy.AddDays(-5), Hoy), "los seis días");
        await p.TeclaAsync("ventas-periodo", "ArrowRight");
        await EsperarTextoAsync(p, "vpp-titulo", t => t == TextosDeReportes.Periodo(Hoy.AddDays(-4), Hoy, Hoy), "cortados en hoy");
        await p.TeclaAsync("ventas-periodo", "ArrowLeft");
        await EsperarTextoAsync(p, "vpp-titulo", t => t == TextosDeReportes.Periodo(Hoy.AddDays(-10), Hoy.AddDays(-5), Hoy), "otra vez los seis días");
        Assert.Empty(p.Errores);
    }
}
