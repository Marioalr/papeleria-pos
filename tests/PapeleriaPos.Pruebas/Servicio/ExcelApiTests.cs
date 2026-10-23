using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Datos;
using PapeleriaPos.Servicio.Excel;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Los Excel que arma el servicio (entrega 9, parte 2), abiertos con ClosedXML: la pestaña Reportes (una hoja por tabla), «Ventas
/// por período» (todos los tickets, sus totales y cada día) y los reportes del inventario (movimientos, bajos y lo que vale), con
/// lo que se ve en la pantalla y sin las páginas. Cada hoja empieza con la tienda, qué es, quién lo exportó y cuándo, y los
/// títulos de la tabla se quedan fijos. Los permisos, los de la pantalla: sin «Ver costos, utilidad y margen», ni la utilidad ni el
/// costo van en el archivo, ni vacíos.
/// </summary>
public sealed class ExcelApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Puesto(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno)
    {
        public string Inventario => $"api/sucursales/{Caja.SucursalId}/inventario";
    }

    private static DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));

    private static string Iso(DateOnly dia) => dia.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Puesto> PuestoAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Puesto(http, caja, await Entrar("cajero", "1111"), await Entrar("encargado", "2222"), await Entrar("dueno", "3333"));
    }

    /// <summary>El encargado, también con «Ver reportes de ventas» (sin «Ver costos, utilidad y margen», como al migrar).</summary>
    private static async Task ConReportesAsync(Puesto p) =>
        await Leer<UsuariosYPermisosDto>(await p.Http.PutAsJsonAsync($"api/cajas/{p.Caja.Id}/usuarios-y-permisos/{p.Encargado.UsuarioId}",
            new SolicitudPermisoUsuario(p.Dueno.UsuarioId, AccionControlada.VerReportes, NivelPermiso.Permitido)));

    private static async Task<TurnoDto> TurnoAsync(Puesto p) =>
        await p.Http.GetAsync($"api/cajas/{p.Caja.Id}/turno") is { StatusCode: HttpStatusCode.OK } abierto
            ? await Leer<TurnoDto>(abierto)
            : await Leer<TurnoDto>(await p.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(p.Caja.Id, p.Cajero.UsuarioId, 50000)));

    /// <summary>Un producto nuevo de $20 la pieza en el primer departamento, con <paramref name="existencia"/> piezas a $8 como inventario inicial.</summary>
    private async Task<FichaProductoDto> ProductoAsync(Puesto p, string nombre, int existencia, MinimoYMaximoDto? minimoYMaximo = null)
    {
        var opciones = (await p.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!;
        var ficha = await Leer<FichaProductoDto>(await p.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(p.Encargado.UsuarioId,
            p.Caja.SucursalId, new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                    [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto($"XL{Guid.NewGuid():N}"[..14])])], [new PrecioFichaDto(null, 1000, 2000)],
                    MinimoYMaximo: minimoYMaximo),
            ]))));
        if (existencia != 0)
        {
            await using var db = servicio.Base();
            var variante = await db.Variantes.SingleAsync(v => v.Id == ficha.Variantes[0].Id);
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(p.Caja.SucursalId, variante, TipoMovimiento.InventarioInicial, Cantidad.Piezas(existencia),
                existencia * 800L, DateTime.UtcNow, documentoTipo: OrigenDelMovimiento.ImportacionEleventa));
        }
        return ficha;
    }

    /// <summary>El cajero vende piezas de un producto, o un artículo común, en efectivo o con tarjeta: el ticket.</summary>
    private static async Task<TicketDto> VenderAsync(Puesto p, FichaProductoDto? producto, int piezas, FormaPago forma = FormaPago.Efectivo)
    {
        var turno = await TurnoAsync(p);
        var linea = producto is { } f
            ? new LineaSolicitada(f.Variantes[0].Id, f.Variantes[0].Presentaciones[0].Id, piezas * 1000L)
            : new LineaSolicitada(null, null, piezas * 1000L, "Recuerdo", 2500);
        var total = (producto is null ? 2500 : 2000) * piezas;
        return await Leer<TicketDto>(await p.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(p.Caja.Id, turno.Id, turno.CajeroId, [linea],
            [forma == FormaPago.Efectivo ? new CobroSolicitado(FormaPago.Efectivo, 1_000_000) : new CobroSolicitado(forma, total, "Aut 1")])));
    }

    /// <summary>
    /// El Excel que contestó el servicio, abierto, con el nombre que propone. Antes, que cumpla el formato de Office Open XML
    /// (lo que revisa Excel al abrirlo: si no, dice que el archivo tiene un problema).
    /// </summary>
    private static async Task<(XLWorkbook Libro, string Nombre)> ExcelAsync(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal(ArchivosDeReportes.TipoExcel, r.Content.Headers.ContentType?.MediaType);
        var nombre = r.Content.Headers.ContentDisposition?.FileNameStar ?? r.Content.Headers.ContentDisposition?.FileName;
        var datos = await r.Content.ReadAsByteArrayAsync();
        using (var documento = SpreadsheetDocument.Open(new MemoryStream(datos), false))
        {
            var errores = new OpenXmlValidator().Validate(documento).Take(5).Select(e => $"{e.Description} ({e.Path?.XPath})").ToList();
            Assert.True(errores.Count == 0, string.Join('\n', errores));
        }
        return (new XLWorkbook(new MemoryStream(datos)), nombre!);
    }

    /// <summary>La tabla de la hoja: sus títulos y sus renglones (cada uno, del título a su valor).</summary>
    private static (List<string> Titulos, List<Dictionary<string, XLCellValue>> Renglones) Tabla(IXLWorksheet hoja)
    {
        var tabla = hoja.Tables.Single();
        var titulos = tabla.Fields.Select(f => f.Name).ToList();
        var renglones = tabla.DataRange.Rows().Select(r => titulos.Select((t, i) => (t, r.Cell(i + 1).Value)).ToDictionary(x => x.t, x => x.Value)).ToList();
        return (titulos, renglones);
    }

    /// <summary>Arriba de cada hoja: la tienda, el título (que dice <paramref name="titulo"/>), quién lo exportó; y los títulos de la tabla, fijos al bajar.</summary>
    private static void ConEncabezado(IXLWorksheet hoja, string titulo, string quien)
    {
        Assert.Equal("Matriz", hoja.Cell(1, 1).GetString());
        Assert.Contains(titulo, hoja.Cell(2, 1).GetString());
        Assert.StartsWith($"Lo exportó {quien} el ", hoja.Cell(3, 1).GetString());
        var tabla = hoja.Tables.Single();
        Assert.Equal(tabla.HeadersRow().RowNumber(), hoja.SheetView.SplitRow);
    }

    /// <summary>Todo lo que dice el libro, en todas sus hojas.</summary>
    private static string Todo(XLWorkbook libro) =>
        string.Join('\n', libro.Worksheets.SelectMany(h => h.CellsUsed()).Select(c => c.GetFormattedString()));

    [Fact]
    public async Task El_reporte_de_ventas_trae_una_hoja_por_tabla_y_la_utilidad_solo_para_quien_la_ve()
    {
        var p = await PuestoAsync();
        await ConReportesAsync(p);
        var producto = await ProductoAsync(p, "Excel reporte de prueba", 10);
        await VenderAsync(p, producto, 3);
        await VenderAsync(p, null, 1, FormaPago.Tarjeta);
        var ruta = $"api/cajas/{p.Caja.Id}/reportes/ventas";
        var reporte = await Leer<ReporteVentasDto>(await p.Http.GetAsync($"{ruta}?usuarioId={p.Dueno.UsuarioId}"));

        var (delDueno, nombre) = await ExcelAsync(await p.Http.GetAsync($"{ruta}/excel?usuarioId={p.Dueno.UsuarioId}"));
        var (delEncargado, _) = await ExcelAsync(await p.Http.GetAsync($"{ruta}/excel?usuarioId={p.Encargado.UsuarioId}&periodo=Hoy"));
        var delCajero = await p.Http.GetAsync($"{ruta}/excel?usuarioId={p.Cajero.UsuarioId}");

        // Se llama como el día, con una hoja por tabla.
        Assert.Equal($"Ventas {Iso(Hoy)}.xlsx", nombre);
        Assert.Equal(["Resumen", "Por hora", "Departamentos", "Cajeros", "Formas de pago", "Lo más vendido"], delDueno.Worksheets.Select(h => h.Name));
        foreach (var hoja in delDueno.Worksheets)
            ConEncabezado(hoja, "Reporte de ventas: Hoy, ", "Dueño de prueba");

        // Las cifras, como en la pantalla (el dinero en pesos), y la utilidad para el dueño.
        var (titulos, cifras) = Tabla(delDueno.Worksheet("Resumen"));
        var cifra = cifras.ToDictionary(c => c["Concepto"].GetText(), c => c["Este periodo"]);
        Assert.Equal(reporte.Actual.VentaCentavos / 100m, (decimal)cifra["Venta neta"].GetNumber());
        Assert.Equal(reporte.Actual.Tickets, cifra["Tickets"].GetNumber());
        Assert.Equal(reporte.Actual.Utilidad!.UtilidadCentavos / 100m, (decimal)cifra["Utilidad bruta"].GetNumber());
        // El dinero, en pesos con centavos; el margen, en porcentaje.
        var resumen = delDueno.Worksheet("Resumen");
        Assert.Equal("$#,##0.00;-$#,##0.00", resumen.CellsUsed().First(c => c.GetString() == "Venta neta").CellRight().Style.NumberFormat.Format);
        Assert.Equal("0.0%", resumen.CellsUsed().First(c => c.GetString() == "Margen").CellRight().Style.NumberFormat.Format);
        Assert.Equal(["Concepto", "Este periodo"], titulos.Take(2));
        Assert.Contains("Margen", Tabla(delDueno.Worksheet("Departamentos")).Titulos);
        Assert.Contains("Costo", Tabla(delDueno.Worksheet("Departamentos")).Titulos);
        // La gráfica, por hora: cada hora con lo de este año.
        var (_, horas) = Tabla(delDueno.Worksheet("Por hora"));
        Assert.Equal(reporte.Serie.Count, horas.Count);
        Assert.Equal(reporte.Serie.Sum(s => s.ActualCentavos ?? 0) / 100m, horas.Sum(h => h["Este año"].IsNumber ? (decimal)h["Este año"].GetNumber() : 0));
        // Las formas de pago y lo que más se vende, como en el reporte.
        var formas = Tabla(delDueno.Worksheet("Formas de pago")).Renglones;
        Assert.Equal(reporte.FormasDePago.Select(x => x.Centavos / 100m), formas.Select(x => (decimal)x["Venta"].GetNumber()));
        Assert.Contains(Tabla(delDueno.Worksheet("Lo más vendido")).Renglones, x => x["Producto"].GetText() == "Excel reporte de prueba");
        Assert.Contains(Tabla(delDueno.Worksheet("Cajeros")).Renglones, x => x["Cajero"].GetText() == "Cajero de prueba");

        // El encargado, sin «Ver costos, utilidad y margen»: ni la utilidad ni el costo van en el archivo.
        Assert.DoesNotContain(Tabla(delEncargado.Worksheet("Resumen")).Renglones, x => x["Concepto"].GetText() is "Utilidad bruta" or "Margen" or "Costo de lo vendido");
        Assert.DoesNotContain("Margen", Tabla(delEncargado.Worksheet("Departamentos")).Titulos);
        Assert.DoesNotContain("Costo", Tabla(delEncargado.Worksheet("Departamentos")).Titulos);
        Assert.DoesNotContain("Utilidad", Tabla(delEncargado.Worksheet("Departamentos")).Titulos);
        Assert.DoesNotContain("utilidad", Todo(delEncargado), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("costo", Todo(delEncargado), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Lo exportó Encargado de prueba", delEncargado.Worksheet("Resumen").Cell(3, 1).GetString());
        // El cajero no ve los reportes: tampoco su Excel.
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Contains("«Ver reportes de ventas»", (await delCajero.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    /// <summary>
    /// Las ventas por departamento, como en la pantalla (revisión del PR #131): lo que un ticket del historial de eleventa suma de
    /// más contra sus artículos va en «Sin desglose», y el artículo común, aparte; los dos «sin costo», fuera de la utilidad, y
    /// todos suman la venta neta.
    /// </summary>
    [Fact]
    public async Task Los_departamentos_llevan_sin_desglose_y_el_articulo_comun_sin_costo_como_la_pantalla()
    {
        var ahora = new DateTime(2026, 10, 21, 13, 30, 0);
        await using var t = await TiendaDeReportes.CrearAsync(ahora);
        var turno = await t.TurnoAsync(t.Ana, TiendaDeReportes.A(2026, 10, 21, 9));
        await t.VenderAsync(turno, TiendaDeReportes.A(2026, 10, 21, 10), [(t.Pluma, 2)], comun: 1500);
        // Un ticket del historial de eleventa que suma $5 más que su artículo (una pluma de $10).
        var historico = await t.HistorialAsync(TiendaDeReportes.A(2026, 10, 21, 9, 30), [(t.Pluma, 1, 400)]);
        await using (var db = t.Contexto())
        {
            (await db.VentasHistoricas.SingleAsync(h => h.Id == historico.Id)).TotalCentavos += 500;
            await db.SaveChangesAsync();
        }
        var reporte = await t.ReporteAsync(PeriodoReporte.Hoy);

        using var libro = new XLWorkbook(new MemoryStream(ExcelDeReportes.Armar(reporte, new QuienExporta("Matriz", "Luis", ahora))));

        var hoja = libro.Worksheet(ExcelDeReportes.HojaDepartamentos);
        var (_, renglones) = Tabla(hoja);
        var porNombre = renglones.ToDictionary(r => r["Departamento"].GetText());
        Assert.Equal(["Escritura", "Artículo común", "Sin desglose"], porNombre.Keys);
        Assert.Equal((5m, "sin costo"), ((decimal)porNombre["Sin desglose"]["Venta"].GetNumber(), porNombre["Sin desglose"]["Margen"].GetText()));
        Assert.Equal((15m, "sin costo"), ((decimal)porNombre["Artículo común"]["Venta"].GetNumber(), porNombre["Artículo común"]["Margen"].GetText()));
        Assert.All([porNombre["Sin desglose"], porNombre["Artículo común"]], r => Assert.True(r["Costo"].IsBlank && r["Utilidad"].IsBlank));
        // La pluma sí tiene su costo y su margen.
        Assert.True(porNombre["Escritura"]["Margen"].IsNumber);
        // Suman la venta neta, y la nota dice qué es «Sin desglose».
        Assert.Equal(reporte.Actual.VentaCentavos / 100m, renglones.Sum(r => (decimal)r["Venta"].GetNumber()));
        Assert.Contains(hoja.CellsUsed(), c => c.GetString() == TextosDeReportes.NotaSinDesglose(reporte));
        Assert.StartsWith("«Sin desglose» es lo que los tickets del historial de eleventa suman", TextosDeReportes.NotaSinDesglose(reporte));
        // El resumen lleva las notas de la pantalla.
        var resumen = libro.Worksheet(ExcelDeReportes.HojaResumen);
        Assert.All(TextosDeReportes.Notas(reporte), nota => Assert.Contains(resumen.CellsUsed(), c => c.GetString() == nota));
    }

    [Fact]
    public async Task Ventas_por_periodo_en_pantalla_por_paginas_y_en_excel_todos_los_tickets_con_sus_totales_y_cada_dia()
    {
        var p = await PuestoAsync();
        var producto = await ProductoAsync(p, "Excel por período de prueba", 0);
        var vendidos = new List<TicketDto>
        {
            await VenderAsync(p, producto, 2), await VenderAsync(p, null, 1, FormaPago.Tarjeta), await VenderAsync(p, producto, 1),
        };
        var ruta = $"api/cajas/{p.Caja.Id}/reportes/ventas-por-periodo";
        var dias = $"&desde={Iso(Hoy.AddDays(-1))}&hasta={Iso(Hoy)}";

        var pagina = await Leer<VentasPorPeriodoDto>(await p.Http.GetAsync($"{ruta}?usuarioId={p.Dueno.UsuarioId}{dias}"));
        var (libro, nombre) = await ExcelAsync(await p.Http.GetAsync($"{ruta}/excel?usuarioId={p.Dueno.UsuarioId}{dias}"));
        var (deLaCaja, nombreDeLaCaja) = await ExcelAsync(await p.Http.GetAsync($"{ruta}/excel?usuarioId={p.Dueno.UsuarioId}{dias}&caja={p.Caja.Id}"));
        var delCajero = await p.Http.GetAsync($"{ruta}/excel?usuarioId={p.Cajero.UsuarioId}{dias}");
        var manana = await p.Http.GetAsync($"{ruta}?usuarioId={p.Dueno.UsuarioId}&desde={Iso(Hoy.AddDays(1))}");

        Assert.All(vendidos, v => Assert.Contains(pagina.Ventas, x => x.Folio == v.Folio));
        Assert.Equal(ReporteVentasPorPeriodo.TicketsPorPagina, pagina.Tamano);
        Assert.Equal($"Ventas por período {Iso(Hoy.AddDays(-1))} a {Iso(Hoy)}.xlsx", nombre);
        Assert.Equal($"Ventas por período {Iso(Hoy.AddDays(-1))} a {Iso(Hoy)} Caja 1.xlsx", nombreDeLaCaja);
        Assert.Equal(["Tickets", "Por día"], libro.Worksheets.Select(h => h.Name));
        var hoja = libro.Worksheet("Tickets");
        ConEncabezado(hoja, "Ventas por período: Del ", "Dueño de prueba");
        Assert.Equal("De todas las cajas.", hoja.Cell(4, 1).GetString());
        Assert.Equal("De la caja: Caja 1.", deLaCaja.Worksheet("Tickets").Cell(4, 1).GetString());

        // Todos los tickets, con lo de la pantalla: folio, fecha y hora, caja, cajero, forma de pago y total.
        var (titulos, tickets) = Tabla(hoja);
        Assert.Equal(["Folio", "Fecha y hora", "Caja", "Cajero", "Forma de pago", "Total", "Cuenta en lo vendido", "Cancelada o devuelta",
            "Devuelto (cualquier día)"], titulos);
        Assert.Equal(pagina.Ventas.Select(v => v.Folio), tickets.Select(t => t["Folio"].GetText()));
        var conTarjeta = tickets.Single(t => t["Folio"].GetText() == vendidos[1].Folio);
        Assert.Equal(("Tarjeta", "Caja 1", "Cajero de prueba", 25m), (conTarjeta["Forma de pago"].GetText(), conTarjeta["Caja"].GetText(),
            conTarjeta["Cajero"].GetText(), (decimal)conTarjeta["Total"].GetNumber()));
        Assert.True(conTarjeta["Fecha y hora"].IsDateTime);
        Assert.Equal("dd/mm/yyyy hh:mm", hoja.Cell(hoja.Tables.Single().DataRange.FirstRow().RowNumber(), 2).Style.NumberFormat.Format);
        // Los totales al final, como en la pantalla.
        var totales = hoja.CellsUsed().Where(c => c.Address.RowNumber > hoja.Tables.Single().RangeAddress.LastAddress.RowNumber)
            .GroupBy(c => c.Address.RowNumber).ToDictionary(g => g.First().GetString(), g => g.Last().Value);
        Assert.Equal(pagina.Totales.VendidoCentavos / 100m, (decimal)totales["Vendido"].GetNumber());
        Assert.Equal(pagina.Totales.VentaCentavos / 100m, (decimal)totales["Venta neta"].GetNumber());
        Assert.Equal(pagina.Totales.Tickets, totales["Tickets cobrados"].GetNumber());
        // «Cuenta en lo vendido» (los cancelados, en cero) suma lo vendido de abajo, con una autosuma (revisión del PR #134).
        Assert.Equal((decimal)totales["Vendido"].GetNumber(), tickets.Sum(t => (decimal)t["Cuenta en lo vendido"].GetNumber()));

        // Del primer día con algo al último (los de antes, sin nada, no van: revisión del PR #134), y el total.
        var (columnas, porDia) = Tabla(libro.Worksheet("Por día"));
        Assert.Equal(["Día", "Tickets", "Vendido", "Efectivo", "Tarjeta", "Devoluciones", "Devuelto", "Venta neta", "Cancelaciones", "Cancelado"], columnas);
        var conAlgo = pagina.Dias.Select(d => d.Fecha!.Value).ToList();
        Assert.Equal(Enumerable.Range(0, conAlgo.Max().DayNumber - conAlgo.Min().DayNumber + 1).Select(n => conAlgo.Min().AddDays(n).ToDateTime(TimeOnly.MinValue)),
            porDia.Select(d => d["Día"].GetDateTime()));
        var deHoy = porDia.Single(d => d["Día"].GetDateTime() == Hoy.ToDateTime(TimeOnly.MinValue));
        Assert.Equal(pagina.Dias.Single(d => d.Fecha == Hoy).VendidoCentavos / 100m, (decimal)deHoy["Vendido"].GetNumber());
        Assert.Equal((decimal)deHoy["Vendido"].GetNumber(), (decimal)deHoy["Efectivo"].GetNumber() + (decimal)deHoy["Tarjeta"].GetNumber());

        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, manana.StatusCode);
    }

    [Fact]
    public async Task Los_movimientos_con_sus_filtros_lo_que_quedo_y_el_costo_solo_para_quien_lo_ve()
    {
        var p = await PuestoAsync();
        var producto = await ProductoAsync(p, "Excel movimientos de prueba", 10);
        await VenderAsync(p, producto, 3);
        await VenderAsync(p, producto, 2);
        var otro = await ProductoAsync(p, "Excel otro de prueba", 4);
        await VenderAsync(p, otro, 1);
        var filtros = $"&desde={Iso(Hoy.AddDays(-1))}&hasta={Iso(Hoy)}";

        // En la pantalla, de 50 en 50; en el Excel, todos.
        var enPantalla = await Leer<MovimientosInventarioDto>(await p.Http.GetAsync($"{p.Inventario}/movimientos?usuarioId={p.Dueno.UsuarioId}{filtros}"));
        var (delDueno, nombre) = await ExcelAsync(await p.Http.GetAsync($"{p.Inventario}/movimientos/excel?usuarioId={p.Dueno.UsuarioId}{filtros}"));
        var (delEncargado, _) = await ExcelAsync(await p.Http.GetAsync($"{p.Inventario}/movimientos/excel?usuarioId={p.Encargado.UsuarioId}{filtros}"));
        var (deUno, _) = await ExcelAsync(await p.Http.GetAsync(
            $"{p.Inventario}/movimientos/excel?usuarioId={p.Encargado.UsuarioId}{filtros}&q={Uri.EscapeDataString("excel movimientos")}&clase=Venta"));
        var delCajero = await p.Http.GetAsync($"{p.Inventario}/movimientos/excel?usuarioId={p.Cajero.UsuarioId}{filtros}");

        Assert.Equal($"Movimientos {Iso(Hoy.AddDays(-1))} a {Iso(Hoy)}.xlsx", nombre);
        var hoja = delDueno.Worksheet("Movimientos");
        ConEncabezado(hoja, "Movimientos del inventario: Del ", "Dueño de prueba");
        var (titulos, todos) = Tabla(hoja);
        Assert.Equal(["Fecha y hora", "Movimiento", "Documento", "Motivo", "Producto", "Código", "Departamento", "Cantidad", "Quedó", "Unidad", "Quién", "Costo"],
            titulos);
        Assert.Equal(enPantalla.Total, todos.Count);
        // Lo que quedó después de cada uno, igual que en la pantalla (del más reciente al más viejo).
        Assert.Equal(enPantalla.Movimientos.Select(m => (m.Producto, m.Cantidad, m.Existencia)),
            todos.Take(enPantalla.Movimientos.Count).Select(m => (m["Producto"].GetText(), (decimal)m["Cantidad"].GetNumber(), (decimal)m["Quedó"].GetNumber())));
        Assert.Equal([5m, 7m, 10m], todos.Where(m => m["Producto"].GetText() == "Excel movimientos de prueba").Select(m => (decimal)m["Quedó"].GetNumber()));

        // Sin «Ver costos, utilidad y margen», sin la columna del costo.
        Assert.DoesNotContain("Costo", Tabla(delEncargado.Worksheet("Movimientos")).Titulos);
        Assert.DoesNotContain("costo", Todo(delEncargado), StringComparison.OrdinalIgnoreCase);
        // Con sus filtros: solo las ventas de ese producto, y el encabezado lo dice.
        var deEse = Tabla(deUno.Worksheet("Movimientos")).Renglones;
        Assert.Equal([("Venta", -2m), ("Venta", -3m)], deEse.Select(m => (m["Movimiento"].GetText(), (decimal)m["Cantidad"].GetNumber())));
        Assert.Equal("Solo tipo: Venta · producto: «excel movimientos».", deUno.Worksheet("Movimientos").Cell(4, 1).GetString());
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
    }

    [Fact]
    public async Task Bajos_en_inventario_y_lo_que_vale_todos_con_sus_filtros_y_lo_que_vale_solo_para_quien_ve_costos()
    {
        var p = await PuestoAsync();
        await ProductoAsync(p, "Excel bajo de prueba", 1, new MinimoYMaximoDto(5000, 12000));

        var bajos = await Leer<BajosInventarioDto>(await p.Http.GetAsync($"{p.Inventario}/bajos?usuarioId={p.Encargado.UsuarioId}&pagina=0"));
        var todosLosBajos = new List<BajoInventarioDto>();
        for (var pagina = 0; pagina * bajos.Tamano < bajos.Total; pagina++)
            todosLosBajos.AddRange((await Leer<BajosInventarioDto>(await p.Http.GetAsync($"{p.Inventario}/bajos?usuarioId={p.Encargado.UsuarioId}&pagina={pagina}"))).Productos);
        var (libro, nombre) = await ExcelAsync(await p.Http.GetAsync($"{p.Inventario}/bajos/excel?usuarioId={p.Encargado.UsuarioId}"));
        var (conMinimo, _) = await ExcelAsync(await p.Http.GetAsync($"{p.Inventario}/bajos/excel?usuarioId={p.Encargado.UsuarioId}&conMinimo=true"));

        Assert.Equal($"Bajos en inventario {Iso(Hoy)}.xlsx", nombre);
        var hoja = libro.Worksheet("Bajos en inventario");
        ConEncabezado(hoja, "Bajos en inventario", "Encargado de prueba");
        var (titulos, renglones) = Tabla(hoja);
        Assert.Equal(["Departamento", "Producto", "Código", "Hay", "Mínimo", "Mínimo propio", "Máximo", "Máximo del sugerido", "En camino", "Pedir", "Pedir en", "Unidad"],
            titulos);
        Assert.Equal(todosLosBajos.Select(b => b.Nombre), renglones.Select(r => r["Producto"].GetText()));
        var elBajo = renglones.Single(r => r["Producto"].GetText() == "Excel bajo de prueba");
        Assert.Equal((1m, 5m, "Sí", 12m, 11m), ((decimal)elBajo["Hay"].GetNumber(), (decimal)elBajo["Mínimo"].GetNumber(), elBajo["Mínimo propio"].GetText(),
            (decimal)elBajo["Máximo"].GetNumber(), (decimal)elBajo["Pedir"].GetNumber()));
        Assert.All(Tabla(conMinimo.Worksheet("Bajos en inventario")).Renglones, r => Assert.Equal("Sí", r["Mínimo propio"].GetText()));

        // Lo que vale: cada departamento y todos los productos con existencia; solo con «Ver costos, utilidad y margen».
        var valor = await Leer<ValorInventarioDto>(await p.Http.GetAsync($"{p.Inventario}/valor?usuarioId={p.Dueno.UsuarioId}"));
        var (delDueno, nombreDelValor) = await ExcelAsync(await p.Http.GetAsync($"{p.Inventario}/valor/excel?usuarioId={p.Dueno.UsuarioId}"));
        var delEncargado = await p.Http.GetAsync($"{p.Inventario}/valor/excel?usuarioId={p.Encargado.UsuarioId}");

        Assert.Equal($"Lo que vale el inventario {Iso(Hoy)}.xlsx", nombreDelValor);
        Assert.Equal(["Por departamento", "Productos"], delDueno.Worksheets.Select(h => h.Name));
        ConEncabezado(delDueno.Worksheet("Por departamento"), "Lo que vale el inventario a costo", "Dueño de prueba");
        var departamentos = Tabla(delDueno.Worksheet("Por departamento")).Renglones;
        Assert.Equal(valor.Departamentos.Select(d => (d.Nombre, d.ValorCentavos / 100m)),
            departamentos.Select(d => (d["Departamento"].GetText(), (decimal)d["Vale"].GetNumber())));
        var productos = Tabla(delDueno.Worksheet("Productos")).Renglones;
        Assert.Equal(valor.TotalProductos, productos.Count);
        Assert.Equal(valor.Total.ValorCentavos / 100m, productos.Sum(x => (decimal)x["Vale"].GetNumber()));
        Assert.Equal(HttpStatusCode.Forbidden, delEncargado.StatusCode);
    }
}
