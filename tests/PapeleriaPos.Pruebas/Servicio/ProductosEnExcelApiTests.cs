using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Productos (F3) › Productos en Excel, contra el servicio con datos ficticios: exportar la lista con sus filtros (una fila por
/// variante, sus presentaciones en otra hoja, el costo solo para quien lo ve) e importarla ya cambiada en Excel. Siempre primero la
/// vista, que no cambia nada: qué cambia producto por producto, qué es nuevo y qué se ignora y por qué. Al aplicar, todo junto, con
/// un renglón en la bitácora, el historial de cada precio y una sola versión del catálogo (las cajas lo reciben con su copia).
/// Las reglas de siempre: el mayoreo más barato que el menudeo, un paquete no más caro que sus piezas, nada debajo del costo, un
/// código de un solo producto. Un precio que sube a más del doble (casi siempre «12,50», que Excel tomó como 1,250) va primero y
/// resaltado, y se aplica igual. En el piloto con eleventa no se importa; se exporta igual.
/// </summary>
public sealed class ProductosEnExcelApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno)
    {
        public Guid Sucursal => Datos.SucursalId;
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Lo que dice el servicio al rechazar: un 400 con su mensaje.</summary>
    private static async Task<string> RechazoAsync(HttpResponseMessage r)
    {
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    private static async Task<Caja> PrepararAsync(HttpClient http)
    {
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> EntrarAsync(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Caja(http, caja, await EntrarAsync("cajero", "1111"), await EntrarAsync("encargado", "2222"), await EntrarAsync("dueno", "3333"));
    }

    private Task<Caja> PrepararAsync() => PrepararAsync(servicio.Fabrica.CreateClient());

    private static string Ficha() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Un código de 13 cifras que no tiene nadie (como un EAN de fábrica que empieza con 75).</summary>
    private static string Codigo() => $"75{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";

    /// <summary>Un departamento solo para la prueba: así lo que se exporta es solo lo suyo.</summary>
    private static async Task<(Guid Id, string Nombre)> DepartamentoAsync(Caja c, string? nombre = null)
    {
        nombre ??= $"Excel {Ficha()}";
        var lista = await Leer<DepartamentosDto>(await c.Http.PostAsJsonAsync("api/catalogo/departamentos",
            new SolicitudDepartamento(c.Encargado.UsuarioId, c.Sucursal, nombre)));
        return (lista.Departamentos.Single(d => d.Nombre == nombre).Id, nombre);
    }

    /// <summary>Una variante para la ficha: su pieza con sus códigos, su menudeo y sus escalas, y sus paquetes (con sus códigos y su precio propio).</summary>
    private static VarianteFichaDto Variante(
        string descripcion, string[] codigos, long menudeo, (long Desde, long Precio)[]? escalas = null,
        (string Nombre, long Piezas, long? Precio, string[] Codigos)[]? paquetes = null, MinimoYMaximoDto? minimoYMaximo = null,
        UnidadBase unidad = UnidadBase.Pieza, bool activa = true)
    {
        var presentaciones = new List<PresentacionFichaDto>
        {
            new(null, unidad == UnidadBase.Pieza ? "Pieza" : "Metro", 1000, true, codigos.Select(x => new CodigoFichaDto(x)).ToList()),
        };
        var precios = new List<PrecioFichaDto> { new(null, 1000, menudeo) };
        precios.AddRange((escalas ?? []).Select(e => new PrecioFichaDto(null, e.Desde * 1000, e.Precio)));
        foreach (var (nombre, piezas, precio, suyos) in paquetes ?? [])
        {
            presentaciones.Add(new PresentacionFichaDto(null, nombre, piezas * 1000, false, suyos.Select(x => new CodigoFichaDto(x)).ToList()));
            if (precio is { } propio)
                precios.Add(new PrecioFichaDto(nombre, 1000, propio));
        }
        return new VarianteFichaDto(null, descripcion, unidad, activa, presentaciones, precios, MinimoYMaximo: minimoYMaximo);
    }

    private static async Task<FichaProductoDto> ProductoAsync(Caja c, string nombre, Guid departamento, string? marca, bool activo, params VarianteFichaDto[] variantes) =>
        await Leer<FichaProductoDto>(await c.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Sucursal,
            new FichaProductoDto(null, nombre, departamento, marca, TipoProducto.Mercancia, Impuesto.Iva16, null, activo, false, variantes))));

    private static Task<FichaProductoDto> ProductoAsync(Caja c, string nombre, Guid departamento, string codigo, long menudeo, (long Desde, long Precio)[]? escalas = null,
        string? marca = null) =>
        ProductoAsync(c, nombre, departamento, marca, true, Variante("", [codigo], menudeo, escalas));

    /// <summary>Existencia con su costo: <paramref name="piezas"/> a <paramref name="costo"/> cada una, como inventario inicial.</summary>
    private async Task ConCostoAsync(Caja c, FichaProductoDto producto, int piezas, long costo)
    {
        await using var db = servicio.Base();
        var variante = await db.Variantes.SingleAsync(v => v.Id == producto.Variantes[0].Id);
        await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(c.Sucursal, variante, TipoMovimiento.InventarioInicial, Cantidad.Piezas(piezas),
            piezas * costo, DateTime.UtcNow, documentoTipo: OrigenDelMovimiento.ImportacionEleventa));
    }

    private static async Task<FichaProductoDto> FichaAsync(Caja c, Guid productoId) =>
        await Leer<FichaProductoDto>(await c.Http.GetAsync($"api/catalogo/productos/{productoId}?sucursalId={c.Sucursal}&usuarioId={c.Encargado.UsuarioId}"));

    /// <summary>Sus precios como quedaron: (paquete, desde cuántas, precio), el menudeo primero.</summary>
    private static async Task<List<(string? Paquete, long Desde, long Precio)>> PreciosAsync(Caja c, Guid productoId, int variante = 0) =>
        (await FichaAsync(c, productoId)).Variantes[variante].Precios.Select(p => (p.Presentacion, p.CantidadMinimaMilesimas / 1000, p.PrecioCentavos)).ToList();

    /// <summary>
    /// El Excel de la lista con esos filtros, como lo pide la caja, abierto con su nombre. Antes, que cumpla el formato de Office
    /// Open XML (lo que revisa Excel al abrirlo).
    /// </summary>
    private static async Task<(XLWorkbook Libro, string Nombre, byte[] Datos)> ExportarAsync(
        Caja c, SesionDto quien, string? texto = null, Guid? departamento = null, FiltroProductos filtro = FiltroProductos.Todos, bool inactivos = false)
    {
        var r = await ExportarRespuestaAsync(c, quien, texto, departamento, filtro, inactivos);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal(ArchivosDeReportes.TipoExcel, r.Content.Headers.ContentType?.MediaType);
        var nombre = r.Content.Headers.ContentDisposition?.FileNameStar ?? r.Content.Headers.ContentDisposition?.FileName;
        var datos = await r.Content.ReadAsByteArrayAsync();
        using (var documento = SpreadsheetDocument.Open(new MemoryStream(datos), false))
        {
            var errores = new OpenXmlValidator().Validate(documento).Take(5).Select(e => $"{e.Description} ({e.Path?.XPath})").ToList();
            Assert.True(errores.Count == 0, string.Join('\n', errores));
        }
        return (new XLWorkbook(new MemoryStream(datos)), nombre!, datos);
    }

    private static Task<HttpResponseMessage> ExportarRespuestaAsync(Caja c, SesionDto quien, string? texto = null, Guid? departamento = null,
        FiltroProductos filtro = FiltroProductos.Todos, bool inactivos = false) =>
        c.Http.GetAsync($"api/catalogo/lista/excel?sucursalId={c.Sucursal}&usuarioId={quien.UsuarioId}&q={Uri.EscapeDataString(texto ?? "")}&filtro={filtro}"
            + (departamento is { } d ? $"&departamentoId={d}" : "") + $"&inactivos={inactivos}");

    /// <summary>Una hoja del Excel: dónde están sus títulos, sus columnas y el renglón de cada código.</summary>
    private sealed class Hoja(IXLWorksheet hoja)
    {
        public IXLWorksheet Excel => hoja;

        public int FilaTitulos { get; } = hoja.RowsUsed().First(r => r.CellsUsed().Any(x => x.GetString() is "Código" or "Código del producto")).RowNumber();

        public List<string> Titulos => hoja.Row(FilaTitulos).CellsUsed().Select(x => x.GetString()).ToList();

        public int Columna(string titulo) =>
            hoja.Row(FilaTitulos).CellsUsed().FirstOrDefault(x => x.GetString() == titulo)?.Address.ColumnNumber
            ?? throw new Xunit.Sdk.XunitException($"No está la columna «{titulo}»: {string.Join(", ", Titulos)}");

        /// <summary>Los renglones de datos, cada uno del título a su valor.</summary>
        public List<Dictionary<string, XLCellValue>> Renglones()
        {
            var titulos = hoja.Row(FilaTitulos).CellsUsed().Select(x => (x.GetString(), x.Address.ColumnNumber)).ToList();
            return hoja.RowsUsed().Where(r => r.RowNumber() > FilaTitulos)
                .Select(r => titulos.ToDictionary(t => t.Item1, t => r.Cell(t.Item2).Value)).Where(r => r.Values.Any(v => !v.IsBlank)).ToList();
        }

        /// <summary>El renglón de ese código (el de la columna «Código», o «Código del producto» y la presentación).</summary>
        public IXLRow Renglon(string codigo, string? presentacion = null) =>
            hoja.RowsUsed().FirstOrDefault(r => r.RowNumber() > FilaTitulos && r.Cell(1).GetString() == codigo
                && (presentacion is null || r.Cell(Columna("Presentación")).GetString() == presentacion))
            ?? throw new Xunit.Sdk.XunitException($"No está el renglón de «{codigo}».");

        public void Poner(string codigo, string titulo, XLCellValue valor, string? presentacion = null) =>
            Renglon(codigo, presentacion).Cell(Columna(titulo)).Value = valor;

        /// <summary>Un renglón nuevo abajo de todos, con esos valores; regresa su número.</summary>
        public int Agregar(params (string Titulo, XLCellValue Valor)[] valores)
        {
            var fila = Math.Max(hoja.LastRowUsed()!.RowNumber(), FilaTitulos) + 1;
            foreach (var (titulo, valor) in valores)
                hoja.Cell(fila, Columna(titulo)).Value = valor;
            return fila;
        }
    }

    private static Hoja Productos(XLWorkbook libro) => new(libro.Worksheet(ProductosEnExcel.HojaProductos));

    private static Hoja Presentaciones(XLWorkbook libro) => new(libro.Worksheet(ProductosEnExcel.HojaPresentaciones));

    /// <summary>
    /// El libro como .xlsx. Una sola vez por libro: ClosedXML suelta el archivo del que lo abrió al guardarlo, así que un libro
    /// abierto de un archivo no se guarda dos veces.
    /// </summary>
    private static byte[] Bytes(XLWorkbook libro)
    {
        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        return flujo.ToArray();
    }

    private static SolicitudImportarProductos Solicitud(Caja c, SesionDto quien, byte[] datos, string archivo = "Productos de prueba.xlsx", string? huella = null,
        Guid? id = null) => new(quien.UsuarioId, c.Sucursal, archivo, Convert.ToBase64String(datos), huella, id);

    private static Task<HttpResponseMessage> VistaAsync(Caja c, SolicitudImportarProductos s) => c.Http.PostAsJsonAsync("api/catalogo/importar/vista", s);

    private static Task<HttpResponseMessage> AplicarAsync(Caja c, SolicitudImportarProductos s) => c.Http.PostAsJsonAsync("api/catalogo/importar", s);

    private static async Task<VistaImportacionDto> VerAsync(Caja c, SesionDto quien, XLWorkbook libro) => await VerAsync(c, quien, Bytes(libro));

    private static async Task<VistaImportacionDto> VerAsync(Caja c, SesionDto quien, byte[] datos) =>
        await Leer<VistaImportacionDto>(await VistaAsync(c, Solicitud(c, quien, datos)));

    /// <summary>Lo que se vio, aplicado: con su huella y un Id nuevo.</summary>
    private static Task<(VistaImportacionDto Vista, ImportacionAplicadaDto Hecho)> VerYAplicarAsync(Caja c, SesionDto quien, XLWorkbook libro) =>
        VerYAplicarAsync(c, quien, Bytes(libro));

    private static async Task<(VistaImportacionDto Vista, ImportacionAplicadaDto Hecho)> VerYAplicarAsync(Caja c, SesionDto quien, byte[] datos)
    {
        var vista = await Leer<VistaImportacionDto>(await VistaAsync(c, Solicitud(c, quien, datos)));
        return (vista, await Leer<ImportacionAplicadaDto>(await AplicarAsync(c, Solicitud(c, quien, datos, huella: vista.Huella, id: Guid.NewGuid()))));
    }

    private async Task<long> VersionDelCatalogoAsync()
    {
        await using var db = servicio.Base();
        return await db.Secuencias.Where(s => s.Nombre == Secuencia.Catalogo).Select(s => s.Valor).SingleAsync();
    }

    private static string Cambios(ProductoQueCambiaDto cambio) => string.Join(" · ", cambio.Cambios.Select(x => $"{x.Dato}: {x.Antes} → {x.Despues}"));

    // ── Exportar ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exporta_la_lista_con_sus_filtros_una_fila_por_variante_y_sus_presentaciones()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (lapizPieza, lapizOtro, lapizCaja) = (Codigo(), $"INT-{ficha}", Codigo());
        var lapiz = await ProductoAsync(c, $"Lápiz {ficha}", departamento, "Mirado", true,
            Variante("", [lapizPieza, lapizOtro], 650, [(12, 550), (48, 500)], [("Caja 12", 12, 6000, [lapizCaja]), ("Paquete 3", 3, null, [])],
                new MinimoYMaximoDto(5000, 40000)));
        var (azul, rojo) = (Codigo(), Codigo());
        var pluma = await ProductoAsync(c, $"Pluma {ficha}", departamento, null, true, Variante("Azul", [azul], 1200), Variante("Roja", [rojo], 1200, activa: false));
        var inactivo = await ProductoAsync(c, $"Inactivo {ficha}", departamento, null, false, Variante("", [Codigo()], 900));
        var metro = await ProductoAsync(c, $"Listón {ficha}", departamento, null, true, Variante("", [Codigo()], 450, unidad: UnidadBase.Metro));

        var (libro, nombre, _) = await ExportarAsync(c, c.Encargado, departamento: departamento);

        // Se llama como la lista: los productos de ese departamento, de hoy.
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
        Assert.Equal($"Productos {nombreDepartamento} {hoy:yyyy-MM-dd}.xlsx", nombre);
        Assert.Equal([ProductosEnExcel.HojaProductos, ProductosEnExcel.HojaPresentaciones], libro.Worksheets.Select(h => h.Name));
        var productos = Productos(libro);
        // El encabezado de los Excel de los reportes: la tienda, qué es, quién lo exportó y los filtros; los títulos, fijos al bajar.
        Assert.Equal("Matriz", productos.Excel.Cell(1, 1).GetString());
        Assert.Equal("Productos: 3 variantes", productos.Excel.Cell(2, 1).GetString());
        Assert.StartsWith("Lo exportó Encargado de prueba el ", productos.Excel.Cell(3, 1).GetString());
        Assert.Equal($"Solo del departamento «{nombreDepartamento}».", productos.Excel.Cell(4, 1).GetString());
        Assert.Equal(productos.FilaTitulos, productos.Excel.SheetView.SplitRow);
        // Las columnas, sin costo para el encargado; el mayoreo, al menos dos escalas; y la clave de cada variante, escondida al final.
        Assert.Equal(["Código", "Otros códigos", "Producto", "Variante", "Departamento", "Marca", "Unidad", "Precio", "Mayoreo 1: desde", "Mayoreo 1: precio",
            "Mayoreo 2: desde", "Mayoreo 2: precio", "Existencia", "Mínimo", "Máximo", "Se vende", "Clave interna"], productos.Titulos);
        Assert.True(productos.Excel.Column(productos.Columna("Clave interna")).IsHidden);
        Assert.False(productos.Excel.Column(productos.Columna("Se vende")).IsHidden);
        Assert.Equal(lapiz.Variantes[0].Id!.Value.ToString("N"), productos.Renglon(lapizPieza).Cell(productos.Columna("Clave interna")).GetString());
        // Con qué catálogo se exportó, escondido en el libro.
        Assert.Equal((await VersionDelCatalogoAsync()).ToString(System.Globalization.CultureInfo.InvariantCulture),
            libro.CustomProperties.Single(x => x.Name == ProductosEnExcel.PropiedadVersion).GetValue<string>());

        // Una fila por variante que se vende: la roja (inactiva) y el producto inactivo no van sin «Con inactivos».
        var renglones = productos.Renglones();
        Assert.Equal(new[] { lapizPieza, azul, metro.Variantes[0].Presentaciones[0].Codigos[0].Codigo }.Order(), renglones.Select(r => r["Código"].GetText()).Order());
        var deLapiz = renglones.Single(r => r["Código"].GetText() == lapizPieza);
        Assert.Equal(lapizOtro, deLapiz["Otros códigos"].GetText());
        Assert.Equal(($"Lápiz {ficha}", nombreDepartamento, "Mirado", "piezas"), (deLapiz["Producto"].GetText(), deLapiz["Departamento"].GetText(),
            deLapiz["Marca"].GetText(), deLapiz["Unidad"].GetText()));
        Assert.True(deLapiz["Variante"].IsBlank);
        Assert.Equal((6.5, 12d, 5.5, 48d, 5d), (deLapiz["Precio"].GetNumber(), deLapiz["Mayoreo 1: desde"].GetNumber(), deLapiz["Mayoreo 1: precio"].GetNumber(),
            deLapiz["Mayoreo 2: desde"].GetNumber(), deLapiz["Mayoreo 2: precio"].GetNumber()));
        Assert.Equal((0d, 5d, 40d, "Sí"), (deLapiz["Existencia"].GetNumber(), deLapiz["Mínimo"].GetNumber(), deLapiz["Máximo"].GetNumber(), deLapiz["Se vende"].GetText()));
        var deAzul = renglones.Single(r => r["Código"].GetText() == azul);
        Assert.Equal(($"Pluma {ficha}", "Azul"), (deAzul["Producto"].GetText(), deAzul["Variante"].GetText()));
        Assert.Equal("metros", renglones.Single(r => r["Producto"].GetText() == $"Listón {ficha}")["Unidad"].GetText());
        // El dinero en pesos y los códigos como texto (Excel no les quita los ceros).
        var columnaPrecio = productos.Columna("Precio");
        Assert.Equal("$#,##0.00;-$#,##0.00", productos.Renglon(lapizPieza).Cell(columnaPrecio).Style.NumberFormat.Format);
        Assert.Equal("@", productos.Renglon(lapizPieza).Cell(1).Style.NumberFormat.Format);
        Assert.Equal("@", productos.Excel.Cell(productos.FilaTitulos + 10, 1).Style.NumberFormat.Format);

        // Las presentaciones: con sus códigos y su precio propio; sin él, vacío (se cobra por sus piezas).
        var presentaciones = Presentaciones(libro);
        Assert.Equal(["Código del producto", "Producto", "Presentación", "Contiene", "Códigos", "Precio", "Clave interna"], presentaciones.Titulos);
        Assert.True(presentaciones.Excel.Column(presentaciones.Columna("Clave interna")).IsHidden);
        var cajas = presentaciones.Renglones();
        Assert.Equal([("Paquete 3", 3d, "", true), ("Caja 12", 12d, lapizCaja, false)],
            cajas.Select(x => (x["Presentación"].GetText(), x["Contiene"].GetNumber(), x["Códigos"].IsBlank ? "" : x["Códigos"].GetText(), x["Precio"].IsBlank)));
        Assert.All(cajas, x => Assert.Equal(lapizPieza, x["Código del producto"].GetText()));
        Assert.Equal(60d, cajas[1]["Precio"].GetNumber());

        // Con los inactivos: también la roja y el producto inactivo, con «Se vende: No».
        var (todos, _, _) = await ExportarAsync(c, c.Encargado, departamento: departamento, inactivos: true);
        var conInactivos = Productos(todos).Renglones();
        Assert.Equal(5, conInactivos.Count);
        Assert.Equal("No", conInactivos.Single(r => r["Código"].GetText() == rojo)["Se vende"].GetText());
        Assert.Equal("No", conInactivos.Single(r => r["Producto"].GetText() == $"Inactivo {ficha}")["Se vende"].GetText());
        Assert.Equal($"Solo del departamento «{nombreDepartamento}» · también los inactivos.", Productos(todos).Excel.Cell(4, 1).GetString());

        // Con lo que se busca, como la lista (también por la marca); y «por completar».
        var (buscado, _, _) = await ExportarAsync(c, c.Encargado, texto: $"mirado {ficha}");
        Assert.Equal([lapizPieza], Productos(buscado).Renglones().Select(r => r["Código"].GetText()));
        Assert.Equal($"Solo los que coinciden con «mirado {ficha}».", Productos(buscado).Excel.Cell(4, 1).GetString());
        var (porCompletar, _, _) = await ExportarAsync(c, c.Encargado, departamento: departamento, filtro: FiltroProductos.PorCompletar);
        Assert.Empty(Productos(porCompletar).Renglones());
        Assert.Contains("Ningún producto con esos filtros.", Productos(porCompletar).Excel.Column(1).CellsUsed().Select(x => x.GetString()));
        _ = (pluma, inactivo);
    }

    // ── Importar ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task De_ida_y_vuelta_primero_se_ve_que_cambia_y_al_aplicar_cambia_todo_junto()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var (otroDepartamento, nombreOtro) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (cuadernoCodigo, lapizCodigo, gomaCodigo, cajaCodigo) = (Codigo(), Codigo(), Codigo(), Codigo());
        var cuaderno = await ProductoAsync(c, $"Cuaderno {ficha}", departamento, null, true,
            Variante("", [cuadernoCodigo], 2500, [(12, 2200)], [("Caja 12", 12, 25000, [cajaCodigo])]));
        var lapiz = await ProductoAsync(c, $"Lápiz {ficha}", departamento, lapizCodigo, 650);
        var goma = await ProductoAsync(c, $"Goma {ficha}", departamento, gomaCodigo, 500, marca: $"Pelikan {ficha}");
        var (libro, _, exportado) = await ExportarAsync(c, c.Encargado, departamento: departamento);

        // Tal como se exportó: nada cambia, y no hay qué aplicar.
        var igual = await VerAsync(c, c.Encargado, exportado);
        Assert.Equal((0, 0, 0, 4, false), (igual.TotalCambian, igual.TotalNuevos, igual.TotalIgnorados, igual.SinCambios, igual.HayQueAplicar));
        Assert.Equal(4, igual.Renglones);
        Assert.Equal(["La existencia, el costo y el margen no se importan: la existencia cambia con las compras, los conteos y los ajustes de Inventario, "
            + "y el costo sale de las compras."], igual.Avisos);

        // En Excel: precios, una escala nueva, el nombre, el departamento (como se escriba), la marca, un código, el mínimo y el
        // máximo, quitar una marca, que ya no se venda, y el precio de la caja.
        var marca = $"Mirado {ficha}";
        var hoja = Productos(libro);
        hoja.Poner(cuadernoCodigo, "Precio", 27.5);
        hoja.Poner(cuadernoCodigo, "Mayoreo 1: precio", 24);
        hoja.Poner(cuadernoCodigo, "Mayoreo 2: desde", 24);
        hoja.Poner(cuadernoCodigo, "Mayoreo 2: precio", "23.00");
        hoja.Poner(lapizCodigo, "Producto", $"Lápiz HB {ficha}");
        hoja.Poner(lapizCodigo, "Departamento", $"  {nombreOtro.ToUpperInvariant()} ");
        hoja.Poner(lapizCodigo, "Marca", marca);
        hoja.Poner(lapizCodigo, "Otros códigos", $"INT-{ficha}");
        hoja.Poner(lapizCodigo, "Mínimo", 5);
        hoja.Poner(lapizCodigo, "Máximo", "30");
        hoja.Poner(gomaCodigo, "Marca", Blank.Value);
        hoja.Poner(gomaCodigo, "Se vende", "no");
        Presentaciones(libro).Poner(cuadernoCodigo, "Precio", 270, "Caja 12");
        var datos = Bytes(libro);

        var vista = await Leer<VistaImportacionDto>(await VistaAsync(c, Solicitud(c, c.Encargado, datos)));

        Assert.Equal((3, 0, 0, 0, true), (vista.TotalCambian, vista.TotalNuevos, vista.TotalIgnorados, vista.SinCambios, vista.HayQueAplicar));
        Assert.Equal([marca], vista.MarcasNuevas);
        Assert.Empty(vista.DepartamentosNuevos);
        var deCuaderno = vista.Cambian.Single(x => x.ProductoId == cuaderno.Id);
        Assert.Equal((ProductosEnExcel.HojaProductos, cuadernoCodigo, $"Cuaderno {ficha}"), (deCuaderno.Hoja, deCuaderno.Codigo, deCuaderno.Producto));
        Assert.Equal("Precio: $25.00 → $27.50 · Mayoreo desde 12: $22.00 → $24.00 · Mayoreo desde 24: — → $23.00 · Caja 12: precio: $250.00 → $270.00",
            Cambios(deCuaderno));
        Assert.Equal(Productos(libro).Renglon(cuadernoCodigo).RowNumber(), deCuaderno.Renglon);
        Assert.Equal($"Nombre: Lápiz {ficha} → Lápiz HB {ficha} · Departamento: {nombreDepartamento} → {nombreOtro} · Marca: — → {marca} · Mínimo: — → 5 · "
            + $"Máximo: — → 30 · Códigos: {lapizCodigo} → {lapizCodigo}, INT-{ficha}", Cambios(vista.Cambian.Single(x => x.ProductoId == lapiz.Id)));
        Assert.Equal($"Marca: Pelikan {ficha} → — · Se vende: Sí → No", Cambios(vista.Cambian.Single(x => x.ProductoId == goma.Id)));
        // El encargado no ve la utilidad: sin margen.
        Assert.False(vista.ConMargen);
        Assert.All(vista.Cambian, x => Assert.Null(x.MargenAntesCentesimas));
        // Ver no cambia nada.
        Assert.Equal(2500, (await PreciosAsync(c, cuaderno.Id!.Value))[0].Precio);

        var antes = await VersionDelCatalogoAsync();
        var copia = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));
        var id = Guid.NewGuid();
        var hecho = await Leer<ImportacionAplicadaDto>(await AplicarAsync(c, Solicitud(c, c.Encargado, datos, huella: vista.Huella, id: id)));

        Assert.Equal((3, 0, 0, false), (hecho.Cambiaron, hecho.Nuevos, hecho.DepartamentosNuevos, hecho.YaSeHabiaAplicado));
        Assert.Equal([(null, 1L, 2750L), (null, 12L, 2400L), (null, 24L, 2300L), ("Caja 12", 1L, 27000L)], await PreciosAsync(c, cuaderno.Id!.Value));
        var fichaLapiz = await FichaAsync(c, lapiz.Id!.Value);
        Assert.Equal(($"Lápiz HB {ficha}", otroDepartamento, marca), (fichaLapiz.Nombre, fichaLapiz.DepartamentoId, fichaLapiz.Marca));
        Assert.Equal([lapizCodigo, $"INT-{ficha}"], fichaLapiz.Variantes[0].Presentaciones[0].Codigos.Select(x => x.Codigo).Order());
        Assert.Equal(OrigenCodigo.Interno, fichaLapiz.Variantes[0].Presentaciones[0].Codigos.Single(x => x.Codigo == $"INT-{ficha}").Origen);
        Assert.Equal(new MinimoYMaximoDto(5000, 30000), fichaLapiz.Variantes[0].MinimoYMaximo);
        var fichaGoma = await FichaAsync(c, goma.Id!.Value);
        Assert.Equal((null, false), (fichaGoma.Marca, fichaGoma.Activo));

        // Una sola versión del catálogo para todo; el historial de cada precio, como en la ficha; un renglón en la bitácora.
        var version = await VersionDelCatalogoAsync();
        Assert.Equal(antes + 1, version);
        await using (var db = servicio.Base())
        {
            var variante = cuaderno.Variantes[0].Id!.Value;
            var historial = await db.PreciosHistorial.Where(h => h.VarianteId == variante && h.VersionCatalogo == version).ToListAsync();
            Assert.Equal([(null, 2300L), (2200L, 2400L), (2500L, 2750L), (25000L, 27000L)],
                historial.Select(h => (h.PrecioAnteriorCentavos, h.PrecioNuevoCentavos!.Value)).OrderBy(h => h.Item1 ?? 0));
            var renglon = await db.Bitacora.SingleAsync(b => b.Id == id);
            Assert.Equal((AccionControlada.EditarCatalogo, c.Encargado.UsuarioId, ResultadoBitacora.Hecho), (renglon.Accion, renglon.UsuarioId, renglon.Resultado));
            Assert.Equal("Importó de Excel «Productos de prueba.xlsx»: cambió 3 productos", renglon.Detalle);
            Assert.Equal(renglon.Detalle, hecho.Bitacora);
        }

        // Las cajas lo reciben con lo que cambió desde su copia: los precios nuevos, el código nuevo y que la goma ya no se vende.
        await servicio.CatalogoAlDiaAsync();
        var cambios = await Leer<CambiosCopiaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/cambios?desde={copia.Version}"
            + $"&existencias={copia.VersionExistencias}&base={copia.Base}&formato={CopiaAlDia.Formato}"));
        Assert.Null(cambios.PaqueteCompleto);
        Assert.Contains(cambios.Productos, p => p.VarianteId == cuaderno.Variantes[0].Id && p.Precios.Any(x => x.PrecioCentavos == 2750));
        Assert.Contains(cambios.Codigos, x => x.Codigo == $"INT-{ficha}" && x.VarianteId == lapiz.Variantes[0].Id);
        Assert.Contains(goma.Variantes[0].Id!.Value, cambios.Quitados);

        // Si la respuesta se perdió y se vuelve a oprimir: no se aplica dos veces.
        var otraVez = await Leer<ImportacionAplicadaDto>(await AplicarAsync(c, Solicitud(c, c.Encargado, datos, huella: vista.Huella, id: id)));
        Assert.Equal((true, hecho.Bitacora), (otraVez.YaSeHabiaAplicado, otraVez.Bitacora));
        Assert.Equal(version, await VersionDelCatalogoAsync());
        // Y el mismo archivo otra vez ya no cambia nada.
        Assert.Equal("Con ese Excel no cambia nada: todo está igual o se ignora.",
            await RechazoAsync(await AplicarAsync(c, Solicitud(c, c.Encargado, datos, huella: vista.Huella, id: Guid.NewGuid()))));
    }

    [Fact]
    public async Task Si_algo_cambio_desde_la_vista_no_se_aplica_y_sin_vista_tampoco()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var codigo = Codigo();
        var producto = await ProductoAsync(c, $"Regla {Ficha()}", departamento, codigo, 1500);
        var (libro, _, _) = await ExportarAsync(c, c.Encargado, departamento: departamento);
        Productos(libro).Poner(codigo, "Precio", 16);
        var datos = Bytes(libro);
        var vista = await Leer<VistaImportacionDto>(await VistaAsync(c, Solicitud(c, c.Encargado, datos)));

        // Sin haber visto qué cambia, no se aplica.
        Assert.Equal("Primero ve qué cambia.", await RechazoAsync(await AplicarAsync(c, Solicitud(c, c.Encargado, datos, id: Guid.NewGuid()))));
        // Alguien le cambió el precio en la ficha mientras tanto: lo que se vio ya no es lo que quedaría.
        Assert.True((await c.Http.PostAsJsonAsync("api/catalogo/precio-menudeo",
            new SolicitudPrecioMenudeo(c.Encargado.UsuarioId, c.Sucursal, producto.Variantes[0].Id!.Value, 1550))).IsSuccessStatusCode);
        Assert.Equal(ProductosEnExcel.CambioDesdeLaVista,
            await RechazoAsync(await AplicarAsync(c, Solicitud(c, c.Encargado, datos, huella: vista.Huella, id: Guid.NewGuid()))));
        Assert.Equal(1550, (await PreciosAsync(c, producto.Id!.Value))[0].Precio);
        // La vista de ahora: cambió después de exportar el Excel, así que va aparte, con lo que cambió, y no se aplica…
        var otra = await Leer<VistaImportacionDto>(await VistaAsync(c, Solicitud(c, c.Encargado, datos)));
        Assert.False(otra.HayQueAplicar);
        var aparte = Assert.Single(otra.CambiaronDespues!);
        Assert.Equal(("Precio: $15.00 → $15.50", "Precio: $15.50 → $16.00"), (string.Join(" · ", aparte.CambioDespues!), Cambios(aparte)));
        // …salvo que se pida aplicarlo de todos modos.
        var deTodosModos = await Leer<VistaImportacionDto>(await VistaAsync(c, Solicitud(c, c.Encargado, datos) with { ConLosQueCambiaron = true }));
        Assert.Equal(("Precio: $15.50 → $16.00", true), (Cambios(Assert.Single(deTodosModos.Cambian)), deTodosModos.ConLosQueCambiaron));
        await Leer<ImportacionAplicadaDto>(await AplicarAsync(c, Solicitud(c, c.Encargado, datos, huella: deTodosModos.Huella, id: Guid.NewGuid())
            with { ConLosQueCambiaron = true }));
        Assert.Equal(1600, (await PreciosAsync(c, producto.Id!.Value))[0].Precio);
    }

    [Fact]
    public async Task Un_codigo_que_no_tiene_nadie_es_un_producto_nuevo_con_sus_presentaciones()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var existente = Codigo();
        await ProductoAsync(c, $"Folder {ficha}", departamento, existente, 800);
        var (libro, _, _) = await ExportarAsync(c, c.Encargado, departamento: departamento);
        var hoja = Productos(libro);
        var (nuevo, otro, metro, caja) = (Codigo(), Codigo(), Codigo(), Codigo());
        var nuevoDepartamento = $"Arte {ficha}";
        var marca = $"Maped {ficha}";
        var fila = hoja.Agregar(("Código", nuevo), ("Otros códigos", $"N-{ficha}"), ("Producto", $"Compás {ficha}"), ("Departamento", nombreDepartamento),
            ("Marca", marca), ("Precio", 45), ("Mayoreo 1: desde", 6), ("Mayoreo 1: precio", 42), ("Mínimo", 2), ("Máximo", 10));
        hoja.Agregar(("Código", otro), ("Producto", $"Acuarelas {ficha}"), ("Departamento", nuevoDepartamento), ("Precio", 60), ("Se vende", "No"));
        hoja.Agregar(("Código", metro), ("Producto", $"Listón {ficha}"), ("Variante", "Rojo"), ("Departamento", nuevoDepartamento.ToLowerInvariant()),
            ("Unidad", "m"), ("Precio", 4.5));
        // Uno que se llama como otro que ya existe: quizá se le cambió el código.
        hoja.Agregar(("Código", Codigo()), ("Producto", $"Folder {ficha}"), ("Departamento", nombreDepartamento), ("Precio", 8));
        Presentaciones(libro).Agregar(("Código del producto", nuevo), ("Presentación", "Caja 6"), ("Contiene", 6), ("Códigos", caja), ("Precio", 240));

        var (vista, hecho) = await VerYAplicarAsync(c, c.Encargado, libro);

        Assert.Equal((0, 4, 0), (vista.TotalCambian, vista.TotalNuevos, vista.TotalIgnorados));
        Assert.Equal([nuevoDepartamento], vista.DepartamentosNuevos);
        Assert.Equal([marca], vista.MarcasNuevas);
        var compas = vista.Nuevos.Single(n => n.Codigo == nuevo);
        Assert.Equal((fila, $"Compás {ficha}", nombreDepartamento, 4500L), (compas.Renglon, compas.Producto, compas.Departamento, compas.PrecioCentavos));
        Assert.Equal($"mayoreo desde 6 a $42.00 · también N-{ficha} · Caja 6 (nueva: trae 6 piezas · {caja} · $240.00) · marca {marca} · mínimo 2 · máximo 10",
            compas.Detalle);
        Assert.Null(compas.Aviso);
        Assert.Equal("no se vende", vista.Nuevos.Single(n => n.Codigo == otro).Detalle);
        Assert.Equal(($"Listón {ficha} Rojo", "por metro"), (vista.Nuevos.Single(n => n.Codigo == metro).Producto, vista.Nuevos.Single(n => n.Codigo == metro).Detalle));
        Assert.StartsWith($"Ya hay uno que se llama igual: «Folder {ficha}» ({existente}).", vista.Nuevos.Single(n => n.Producto == $"Folder {ficha}").Aviso);
        Assert.Equal((0, 4, 1), (hecho.Cambiaron, hecho.Nuevos, hecho.DepartamentosNuevos));
        Assert.Equal($"Importó de Excel «Productos de prueba.xlsx»: dio de alta 4 productos nuevos; dio de alta el departamento «{nuevoDepartamento}»",
            hecho.Bitacora);

        // Ya se venden con su código, con su mayoreo y su caja.
        await servicio.CatalogoAlDiaAsync();
        var delCompas = await Leer<ProductoVentaDto>(await c.Http.GetAsync($"api/productos/codigo/{nuevo}?sucursalId={c.Sucursal}"));
        Assert.StartsWith($"Compás {ficha}", delCompas.Descripcion);
        Assert.Equal(UnidadBase.Pieza, delCompas.Unidad);
        Assert.Equal([(null, 1000L, 4500L), (null, 6000L, 4200L), ("Caja 6", 1000L, 24000L)],
            delCompas.Precios.Select(p => (delCompas.Presentaciones.FirstOrDefault(x => x.Id == p.PresentacionId)?.Nombre, p.CantidadMinimaMilesimas, p.PrecioCentavos))
                .OrderBy(p => p.Nombre is not null).ThenBy(p => p.CantidadMinimaMilesimas));
        var porCaja = await Leer<ProductoVentaDto>(await c.Http.GetAsync($"api/productos/codigo/{caja}?sucursalId={c.Sucursal}"));
        Assert.Equal(("Caja 6", 6000L), (porCaja.Presentacion.Nombre, porCaja.Presentacion.FactorMilesimas));
        await using var db = servicio.Base();
        var acuarelas = await db.Productos.Include(p => p.Departamento).SingleAsync(p => p.Nombre == $"Acuarelas {ficha}");
        Assert.Equal((nuevoDepartamento, false, false), (acuarelas.Departamento!.Nombre, acuarelas.Activo, acuarelas.PorCompletar));
        var liston = await db.Variantes.Include(v => v.Presentaciones).SingleAsync(v => v.Descripcion == "Rojo" && v.Producto!.Nombre == $"Listón {ficha}");
        Assert.Equal((UnidadBase.Metro, "Metro"), (liston.UnidadBase, liston.Presentaciones.Single().Nombre));
        Assert.Equal(1, await db.Departamentos.CountAsync(d => d.Nombre == nuevoDepartamento));
    }

    /// <summary>Un servicio (sin inventario), como las copias: con su código.</summary>
    private static async Task<FichaProductoDto> ServicioAsync(Caja c, string nombre, Guid departamento, string codigo, long precio) =>
        await Leer<FichaProductoDto>(await c.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Sucursal,
            new FichaProductoDto(null, nombre, departamento, null, TipoProducto.Servicio, Impuesto.Iva16, null, true, false, [Variante("", [codigo], precio)]))));

    private static RenglonIgnoradoDto Ignorado(VistaImportacionDto vista, int renglon, string hoja = ProductosEnExcel.HojaProductos) =>
        vista.Ignorados.SingleOrDefault(x => x.Hoja == hoja && x.Renglon == renglon)
        ?? throw new Xunit.Sdk.XunitException($"El renglón {renglon} de «{hoja}» no se ignoró: "
            + string.Join("; ", vista.Ignorados.Select(x => $"{x.Hoja} {x.Renglon}: {x.Detalle}")));

    [Fact]
    public async Task Cada_renglon_que_se_ignora_dice_por_que_y_los_demas_se_importan()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var (otro, _) = await DepartamentoAsync(c);
        var (apagado, nombreApagado) = await DepartamentoAsync(c);
        Assert.True((await c.Http.PostAsJsonAsync($"api/catalogo/departamentos/{apagado}/desactivar",
            new SolicitudDesactivarDepartamento(c.Encargado.UsuarioId, c.Sucursal))).IsSuccessStatusCode);
        var ficha = Ficha();
        string Nombre(string que) => $"{que} {ficha}";
        var codigos = Enumerable.Range(0, 18).Select(_ => Codigo()).ToArray();
        var bien = await ProductoAsync(c, Nombre("Bien"), departamento, codigos[0], 1000);
        // Con dos códigos: en «Código» va el menor (los de fábrica, en orden), y el otro en «Otros códigos».
        (codigos[1], codigos[2]) = string.CompareOrdinal(codigos[1], codigos[2]) < 0 ? (codigos[1], codigos[2]) : (codigos[2], codigos[1]);
        var dosCodigos = await ProductoAsync(c, Nombre("Dos códigos"), departamento, null, true, Variante("", [codigos[1], codigos[2]], 1000));
        // Los de otro departamento no vienen en el Excel: sus códigos ya son de alguien.
        var ajeno = codigos[3];
        await ProductoAsync(c, Nombre("Ajeno"), otro, ajeno, 1000);
        var conCaja = await ProductoAsync(c, Nombre("Con caja"), otro, null, true,
            Variante("", [codigos[4]], 1000, paquetes: [("Caja 10", 10, 9000, [codigos[5]])]));
        var conCosto = await ProductoAsync(c, Nombre("Con costo"), departamento, codigos[6], 1500);
        await ConCostoAsync(c, conCosto, 10, 1000);
        var conMayoreo = await ProductoAsync(c, Nombre("Con mayoreo"), departamento, codigos[7], 1000, [(12, 900)]);
        await ProductoAsync(c, Nombre("Variantes"), departamento, null, true, Variante("Azul", [codigos[8]], 1000), Variante("Roja", [codigos[9]], 1000));
        foreach (var (codigo, i) in codigos[10..15].Concat(codigos[16..18]).Select((x, i) => (x, i)))
            await ProductoAsync(c, Nombre($"Otro {i}"), departamento, codigo, 1000);
        var copias = await ServicioAsync(c, Nombre("Copias"), departamento, codigos[15], 200);
        // Un código de fábrica que empieza con cero: escrito como número en Excel, pierde el cero.
        var conCero = $"0{Random.Shared.NextInt64(100_000_000_000, 999_999_999_999)}";
        await ProductoAsync(c, Nombre("Con cero"), departamento, conCero, 1000);

        var (libro, _, _) = await ExportarAsync(c, c.Encargado, departamento: departamento);
        var hoja = Productos(libro);
        hoja.Poner(codigos[0], "Precio", 11);
        int Fila(string codigo) => hoja.Renglon(codigo).RowNumber();
        // Sin código, y nadie sin código se llama así.
        var sinCodigo = hoja.Agregar(("Producto", Nombre("Nadie")), ("Departamento", nombreDepartamento), ("Precio", 5));
        // El mismo código nuevo en dos renglones.
        var repetido = Codigo();
        var repetido1 = hoja.Agregar(("Código", repetido), ("Producto", Nombre("Repetido uno")), ("Departamento", nombreDepartamento), ("Precio", 5));
        var repetido2 = hoja.Agregar(("Código", repetido), ("Producto", Nombre("Repetido dos")), ("Departamento", nombreDepartamento), ("Precio", 5));
        // El mismo producto otra vez, por su otro código.
        var otraVez = hoja.Agregar(("Código", codigos[2]), ("Producto", Nombre("Dos códigos")), ("Departamento", nombreDepartamento), ("Precio", 12));
        // Un código que ya es de otro producto, y el de una presentación en la hoja de los productos.
        hoja.Poner(codigos[10], "Otros códigos", ajeno);
        var dePresentacion = hoja.Agregar(("Código", codigos[5]), ("Producto", Nombre("Caja")), ("Departamento", nombreDepartamento), ("Precio", 5));
        // Excel le quitó el cero de adelante (lo escribió como número).
        var sinCeros = hoja.Agregar(("Código", double.Parse(conCero, System.Globalization.CultureInfo.InvariantCulture)), ("Producto", Nombre("Con cero")),
            ("Departamento", nombreDepartamento), ("Precio", 10));
        // Un código que Excel guardó como número con decimales.
        var invalido = hoja.Agregar(("Código", 7.5e12 + 0.5), ("Producto", Nombre("Inválido")), ("Departamento", nombreDepartamento), ("Precio", 5));
        // Nuevo sin precio (y su caja, en «Presentaciones»).
        var sinPrecio = Codigo();
        var faltaPrecio = hoja.Agregar(("Código", sinPrecio), ("Producto", Nombre("Sin precio")), ("Departamento", nombreDepartamento));
        // Un precio que no es un número, uno con coma de decimales y un «Se vende» que no se entiende.
        hoja.Poner(codigos[11], "Precio", "doce");
        hoja.Poner(codigos[12], "Precio", "12,50");
        hoja.Poner(codigos[13], "Se vende", "Tal vez");
        // El mayoreo igual que el menudeo, y un precio debajo del costo.
        hoja.Poner(codigos[7], "Mayoreo 1: precio", 10);
        hoja.Poner(conCosto.Variantes[0].Presentaciones[0].Codigos[0].Codigo, "Precio", 9);
        // Un departamento con un nombre que no cabe, y uno desactivado.
        hoja.Poner(codigos[14], "Departamento", new string('X', 61));
        hoja.Poner(codigos[17], "Departamento", nombreApagado);
        // Las variantes de un producto con distinto nombre; la unidad cambiada; el mínimo de un servicio.
        hoja.Poner(codigos[8], "Producto", Nombre("Variantes nuevas"));
        hoja.Poner(codigos[16], "Unidad", "metros");
        hoja.Poner(codigos[15], "Mínimo", 3);
        // Presentaciones: de un producto que no existe, y de uno nuevo que se ignoró.
        var presentaciones = Presentaciones(libro);
        var noExiste = presentaciones.Agregar(("Código del producto", Codigo()), ("Presentación", "Caja 6"), ("Contiene", 6));
        var suProducto = presentaciones.Agregar(("Código del producto", sinPrecio), ("Presentación", "Caja 6"), ("Contiene", 6));

        var datos = Bytes(libro);
        var vista = await VerAsync(c, c.Encargado, datos);

        // Lo que está bien se importa igual.
        Assert.Equal("Precio: $10.00 → $11.00", Cambios(Assert.Single(vista.Cambian)));
        Assert.Equal(bien.Id, vista.Cambian[0].ProductoId);
        Assert.Empty(vista.Nuevos);
        void Es(int renglon, PorQueSeIgnora porQue, string detalle, string hojaDe = ProductosEnExcel.HojaProductos)
        {
            var ignorado = Ignorado(vista, renglon, hojaDe);
            Assert.Equal((porQue, detalle), (ignorado.PorQue, ignorado.Detalle));
        }
        Es(sinCodigo, PorQueSeIgnora.SinCodigo, $"Sin código, y ningún producto sin código se llama «{Nombre("Nadie")}» en «{nombreDepartamento}». Un "
            + "producto nuevo lleva su código.");
        Es(repetido1, PorQueSeIgnora.CodigoRepetido, $"El código «{repetido}» está también en el renglón {repetido2}: un código es de un solo producto.");
        Es(repetido2, PorQueSeIgnora.CodigoRepetido, $"El código «{repetido}» está también en el renglón {repetido1}: un código es de un solo producto.");
        Es(otraVez, PorQueSeIgnora.Repetido, $"«{Nombre("Dos códigos")}» viene en los renglones {Fila(codigos[1])} y {otraVez}: déjalo en uno solo.");
        Es(Fila(codigos[1]), PorQueSeIgnora.Repetido, $"«{Nombre("Dos códigos")}» viene en los renglones {Fila(codigos[1])} y {otraVez}: déjalo en uno solo.");
        Es(Fila(codigos[10]), PorQueSeIgnora.CodigoDeOtroProducto, $"El código «{ajeno}» ya es de «{Nombre("Ajeno")}»: un código es de un solo producto.");
        Es(dePresentacion, PorQueSeIgnora.CodigoDeOtroProducto, $"El código «{codigos[5]}» es de la presentación «Caja 10» de «{Nombre("Con caja")}»: en esta "
            + "hoja va el de su pieza; las presentaciones van en la hoja «Presentaciones».");
        Es(sinCeros, PorQueSeIgnora.CodigoSinCeros, $"El código «{conCero[1..]}» parece «{conCero}» sin sus ceros: Excel se los quitó al leerlo como número. "
            + "Escríbelo como texto (con un apóstrofo ' antes) o cópialo del Excel que se exportó.");
        Es(invalido, PorQueSeIgnora.CodigoInvalido, "«Código» dice «7500000000000.5»: Excel guardó el código como número y perdió cifras. Escríbelo como texto "
            + "(con un apóstrofo ' antes).");
        Es(faltaPrecio, PorQueSeIgnora.FaltaDato, $"Es nuevo (nadie tiene el código «{sinPrecio}») y le falta su precio, en «Precio».");
        Es(Fila(codigos[11]), PorQueSeIgnora.NumeroInvalido, "«Precio» dice «doce»: no es un número.");
        Es(Fila(codigos[12]), PorQueSeIgnora.ComaDecimal, "«Precio» dice «12,50»: la coma solo separa los miles (1,250) y los decimales van con punto (12.50). "
            + "Corrígelo para que no se lea otro número.");
        Es(Fila(codigos[13]), PorQueSeIgnora.DatoInvalido, "«Se vende» dice «Tal vez»: va «Sí» o «No».");
        Es(Fila(codigos[7]), PorQueSeIgnora.MayoreoMasCaro, "Su mayoreo desde 12 ($10.00) quedaría igual o más caro que su precio de $10.00: el mayoreo va más "
            + "barato. Cámbialo también en las columnas del mayoreo.");
        // El encargado no ve costos: no se le dice cuánto es.
        Es(Fila(conCosto.Variantes[0].Presentaciones[0].Codigos[0].Codigo), PorQueSeIgnora.DebajoDelCosto,
            "Su precio de $9.00 quedaría debajo de su costo promedio: para venderlo debajo del costo, cámbialo en su ficha.");
        Es(Fila(codigos[14]), PorQueSeIgnora.DepartamentoNoExiste, $"No hay un departamento «{new string('X', 61)}», y no se puede dar de alta: su nombre va de "
            + "hasta 60 letras.");
        Es(Fila(codigos[17]), PorQueSeIgnora.DepartamentoDesactivado, $"El departamento «{nombreApagado}» está desactivado: reactívalo en Productos › "
            + "Departamentos o elige otro.");
        Es(Fila(codigos[8]), PorQueSeIgnora.DatosDistintos, $"Las variantes de «{Nombre("Variantes")}» (renglones {Fila(codigos[8])} y {Fila(codigos[9])}) traen "
            + "distinto nombre, en «Producto»: es del producto, así que va igual en todas.");
        Es(Fila(codigos[9]), PorQueSeIgnora.DatosDistintos, $"Las variantes de «{Nombre("Variantes")}» (renglones {Fila(codigos[8])} y {Fila(codigos[9])}) traen "
            + "distinto nombre, en «Producto»: es del producto, así que va igual en todas.");
        Es(Fila(codigos[16]), PorQueSeIgnora.NoSeCambiaDesdeExcel, $"«{Nombre("Otro 5")}» se vende en piezas: la unidad no se cambia desde Excel (se cambia en "
            + "su ficha, en la depuración).");
        Es(Fila(codigos[15]), PorQueSeIgnora.NoSeCambiaDesdeExcel, $"«{Nombre("Copias")}» es un servicio: no lleva mínimo ni máximo de inventario.");
        Es(noExiste, PorQueSeIgnora.ProductoNoExiste, $"Ningún producto tiene el código «{presentaciones.Excel.Cell(noExiste, 1).GetString()}», ni es uno nuevo de "
            + "la hoja «Productos».", ProductosEnExcel.HojaPresentaciones);
        Es(suProducto, PorQueSeIgnora.SuProductoSeIgnoro, $"Su producto nuevo (renglón {faltaPrecio} de «Productos») se ignoró: Es nuevo (nadie tiene el código "
            + $"«{sinPrecio}») y le falta su precio, en «Precio».", ProductosEnExcel.HojaPresentaciones);

        // Cada renglón ignorado dice su código y su producto.
        Assert.Equal((codigos[10], Nombre("Otro 0")), (Ignorado(vista, Fila(codigos[10])).Codigo, Ignorado(vista, Fila(codigos[10])).Producto));
        Assert.Equal(23, vista.TotalIgnorados);
        Assert.Equal(vista.TotalIgnorados, vista.Ignorados.Count);

        // Aplicar cambia solo lo que se vio que cambia.
        var hecho = await Leer<ImportacionAplicadaDto>(await AplicarAsync(c, Solicitud(c, c.Encargado, datos, huella: vista.Huella, id: Guid.NewGuid())));
        Assert.Equal("Importó de Excel «Productos de prueba.xlsx»: cambió 1 producto; se ignoraron 23 renglones", hecho.Bitacora);
        Assert.Equal(1100, (await PreciosAsync(c, bien.Id!.Value))[0].Precio);
        Assert.Equal(1000, (await PreciosAsync(c, dosCodigos.Id!.Value))[0].Precio);
        Assert.Equal([(null, 1L, 1000L), (null, 12L, 900L)], await PreciosAsync(c, conMayoreo.Id!.Value));
        _ = (copias, conCaja);
    }

    [Fact]
    public async Task Un_departamento_desactivado_no_recibe_productos_activos()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var (apagado, nombreApagado) = await DepartamentoAsync(c);
        var (activo, inactivo, enApagado) = (Codigo(), Codigo(), Codigo());
        await ProductoAsync(c, $"Activo {Ficha()}", departamento, activo, 1000);
        await ProductoAsync(c, $"Inactivo {Ficha()}", departamento, null, false, Variante("", [inactivo], 1000));
        var queSeQueda = await ProductoAsync(c, $"Se queda {Ficha()}", apagado, enApagado, 1000);
        // Al desactivar el departamento, su producto activo pasa a otro; se reactiva aquí para dejarlo en el desactivado, inactivo.
        Assert.True((await c.Http.PostAsJsonAsync($"api/catalogo/departamentos/{apagado}/desactivar",
            new SolicitudDesactivarDepartamento(c.Encargado.UsuarioId, c.Sucursal, departamento))).IsSuccessStatusCode);
        await using (var db = servicio.Base())
        {
            var producto = await db.Productos.SingleAsync(p => p.Id == queSeQueda.Id);
            (producto.DepartamentoId, producto.Activo) = (apagado, false);
            await db.SaveChangesAsync();
        }
        var (libro, _, _) = await ExportarAsync(c, c.Encargado, texto: "", inactivos: true, departamento: departamento);
        var hoja = Productos(libro);
        hoja.Poner(activo, "Departamento", nombreApagado);
        hoja.Poner(inactivo, "Departamento", nombreApagado);
        var nuevo = Codigo();
        var fila = hoja.Agregar(("Código", nuevo), ("Producto", $"Nuevo {Ficha()}"), ("Departamento", nombreApagado), ("Precio", 5));

        var vista = await VerAsync(c, c.Encargado, libro);

        // Un activo no pasa a uno desactivado, ni uno nuevo llega ahí; uno inactivo sí puede quedarse ahí.
        var detalle = $"El departamento «{nombreApagado}» está desactivado: reactívalo en Productos › Departamentos o elige otro.";
        Assert.Equal((PorQueSeIgnora.DepartamentoDesactivado, detalle), (Ignorado(vista, hoja.Renglon(activo).RowNumber()).PorQue,
            Ignorado(vista, hoja.Renglon(activo).RowNumber()).Detalle));
        Assert.Equal((PorQueSeIgnora.DepartamentoDesactivado, detalle), (Ignorado(vista, fila).PorQue, Ignorado(vista, fila).Detalle));
        Assert.Equal($"Departamento: {nombreDepartamento} → {nombreApagado}", Cambios(Assert.Single(vista.Cambian)));
        _ = nuevo;
    }

    [Fact]
    public async Task Lo_que_se_cobra_queda_en_orden_como_en_la_ficha_y_en_cambiar_precios()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (bajaMenudeo, inversion, cajaCara, ambos, costo) = (Codigo(), Codigo(), Codigo(), Codigo(), Codigo());
        await ProductoAsync(c, $"Baja menudeo {ficha}", departamento, bajaMenudeo, 600, [(12, 500)]);
        await ProductoAsync(c, $"Inversión {ficha}", departamento, inversion, 600, [(12, 500), (24, 450)]);
        await ProductoAsync(c, $"Caja cara {ficha}", departamento, null, true, Variante("", [cajaCara], 600, paquetes: [("Caja 12", 12, 7000, [])]));
        var bajanLosDos = await ProductoAsync(c, $"Bajan los dos {ficha}", departamento, null, true, Variante("", [ambos], 600, paquetes: [("Caja 12", 12, 7000, [])]));
        var conCosto = await ProductoAsync(c, $"Con costo {ficha}", departamento, costo, 1500, [(12, 1300)]);
        await ConCostoAsync(c, conCosto, 10, 1250);
        var (libro, _, _) = await ExportarAsync(c, c.Dueno, departamento: departamento);
        var hoja = Productos(libro);
        // El menudeo baja hasta su mayoreo, que no viene cambiado.
        hoja.Poner(bajaMenudeo, "Precio", 5);
        // La escala de más piezas, más cara que la de menos.
        hoja.Poner(inversion, "Mayoreo 2: precio", 5.5);
        // La caja, más cara que sus 12 piezas sueltas ($72.00).
        Presentaciones(libro).Poner(cajaCara, "Precio", 80, "Caja 12");
        // Bajan el menudeo y la caja juntos: la caja sigue más barata que sus piezas.
        hoja.Poner(ambos, "Precio", 5);
        Presentaciones(libro).Poner(ambos, "Precio", 58, "Caja 12");
        // El mayoreo debajo del costo ($12.50): el dueño ve cuánto es.
        hoja.Poner(costo, "Mayoreo 1: precio", 12);

        var datos = Bytes(libro);
        var vista = await VerAsync(c, c.Dueno, datos);

        string PorQue(string codigo) => Ignorado(vista, hoja.Renglon(codigo).RowNumber()).Detalle;
        Assert.Equal("Su mayoreo desde 12 ($5.00) quedaría igual o más caro que su precio de $5.00: el mayoreo va más barato. Cámbialo también en las columnas "
            + "del mayoreo.", PorQue(bajaMenudeo));
        Assert.Equal("Su mayoreo desde 24 ($5.50) quedaría más caro que el de desde 12 ($5.00): con más, igual o más barato.", PorQue(inversion));
        var deLaCaja = Ignorado(vista, Presentaciones(libro).Renglon(cajaCara, "Caja 12").RowNumber(), ProductosEnExcel.HojaPresentaciones);
        Assert.Equal((PorQueSeIgnora.MayoreoMasCaro, "«Caja 12» a $80.00 quedaría más cara que sus 12 piezas por separado ($72.00)."),
            (deLaCaja.PorQue, deLaCaja.Detalle));
        Assert.Equal("Su mayoreo desde 12 de $12.00 quedaría debajo de su costo promedio ($12.50 por pieza): para venderlo debajo del costo, cámbialo en su "
            + "ficha.", PorQue(costo));
        var deAmbos = Assert.Single(vista.Cambian);
        Assert.Equal("Precio: $6.00 → $5.00 · Caja 12: precio: $70.00 → $58.00", Cambios(deAmbos));
        Assert.Equal(4, vista.TotalIgnorados);

        // Lo que se aplicó se cobra en orden: 12 piezas cuestan lo de la caja, nunca más que sueltas.
        await VerYAplicarAsync(c, c.Dueno, datos);
        await using var db = servicio.Base();
        var variante = bajanLosDos.Variantes[0].Id!.Value;
        var precios = await db.Precios.Where(p => p.VarianteId == variante).ToListAsync();
        var caja = await db.Presentaciones.SingleAsync(p => p.VarianteId == variante && !p.EsBase);
        var pieza = await db.Presentaciones.SingleAsync(p => p.VarianteId == variante && p.EsBase);
        Assert.Equal(5800, CalculoPrecio.Calcular(precios, c.Sucursal, caja, Cantidad.Piezas(1))!.ImporteCentavos);
        Assert.Equal(6000, CalculoPrecio.Calcular(precios, c.Sucursal, pieza, Cantidad.Piezas(12))!.ImporteCentavos);
    }

    // ── Un precio que sube a más del doble ───────────────────────────────────

    [Fact]
    public async Task Un_precio_que_sube_a_mas_del_doble_se_revisa_sea_el_menudeo_una_escala_o_una_caja()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (menudeo, escala, caja, doble) = (Codigo(), Codigo(), Codigo(), Codigo());
        await ProductoAsync(c, $"Sube el menudeo {ficha}", departamento, menudeo, 1250);
        await ProductoAsync(c, $"Sube la escala {ficha}", departamento, escala, 1000, [(12, 400)]);
        await ProductoAsync(c, $"Sube la caja {ficha}", departamento, null, true, Variante("", [caja], 600, paquetes: [("Caja 12", 12, 3000, [])]));
        await ProductoAsync(c, $"Justo al doble {ficha}", departamento, null, true, Variante("", [doble], 1000, [(12, 400)], [("Caja 12", 12, 3000, [])]));
        var (libro, _, _) = await ExportarAsync(c, c.Encargado, departamento: departamento);
        var hoja = Productos(libro);
        // «12,50» en el menudeo: Excel con la configuración de México guarda el número 1250. La escala y la caja, un centavo arriba del
        // doble y en orden (la escala, más barata que el menudeo; la caja, que sus 12 piezas sueltas).
        hoja.Poner(menudeo, "Precio", 1250);
        hoja.Poner(escala, "Mayoreo 1: precio", 8.01);
        Presentaciones(libro).Poner(caja, "Precio", 60.01, "Caja 12");
        // Los tres, justo al doble.
        hoja.Poner(doble, "Precio", 20);
        hoja.Poner(doble, "Mayoreo 1: precio", 8);
        Presentaciones(libro).Poner(doble, "Precio", 60, "Caja 12");

        var vista = await VerAsync(c, c.Encargado, libro);

        Assert.Empty(vista.Ignorados);
        (string Dato, string Antes, string Despues, PorQueSeRevisa? Revisar) ElCambio(string producto)
        {
            var cambio = Assert.Single(vista.Cambian.Single(x => x.Producto == producto).Cambios);
            return (cambio.Dato, cambio.Antes, cambio.Despues, cambio.Revisar);
        }
        Assert.Equal(("Precio", "$12.50", "$1,250.00", PorQueSeRevisa.SubeMucho), ElCambio($"Sube el menudeo {ficha}"));
        Assert.Equal(("Mayoreo desde 12", "$4.00", "$8.01", PorQueSeRevisa.SubeMucho), ElCambio($"Sube la escala {ficha}"));
        Assert.Equal(("Caja 12: precio", "$30.00", "$60.01", PorQueSeRevisa.SubeMucho), ElCambio($"Sube la caja {ficha}"));
        // Los tres resaltados van primero; justo al doble cambia igual, sin resaltar y después.
        Assert.Equal(4, vista.TotalCambian);
        Assert.All(vista.Cambian.Take(3), x => Assert.True(x.Revisar));
        var alDoble = vista.Cambian[3];
        Assert.Equal(($"Justo al doble {ficha}", "Precio: $10.00 → $20.00 · Mayoreo desde 12: $4.00 → $8.00 · Caja 12: precio: $30.00 → $60.00", false),
            (alDoble.Producto, Cambios(alDoble), alDoble.Revisar));
    }

    [Fact]
    public async Task Un_excel_con_un_precio_que_sube_a_mas_del_doble_lo_ensena_primero_y_se_aplica_igual()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (agenda, cuaderno, goma, sinPrecio, nuevo) = (Codigo(), Codigo(), Codigo(), Codigo(), Codigo());
        var deAgenda = await ProductoAsync(c, $"Agenda {ficha}", departamento, agenda, 6500);
        var deCuaderno = await ProductoAsync(c, $"Cuaderno {ficha}", departamento, cuaderno, 1250);
        var deGoma = await ProductoAsync(c, $"Goma {ficha}", departamento, goma, 500);
        // Uno sin precio, como llegan los de eleventa con precio 0.
        var deSinPrecio = await ProductoAsync(c, $"Sin precio {ficha}", departamento, sinPrecio, 1000);
        await using (var db = servicio.Base())
        {
            var varianteId = deSinPrecio.Variantes[0].Id!.Value;
            db.Precios.RemoveRange(await db.Precios.Where(p => p.VarianteId == varianteId).ToListAsync());
            await db.SaveChangesAsync();
        }
        var (libro, _, _) = await ExportarAsync(c, c.Encargado, departamento: departamento);
        var hoja = Productos(libro);
        // La agenda sube como siempre. En el cuaderno alguien escribió «12,50» y Excel con la configuración de México guardó el número
        // 1250: llega como número y no como texto, así que no se rechaza como coma de decimales.
        hoja.Poner(agenda, "Precio", 70);
        hoja.Poner(cuaderno, "Precio", 1250);
        // Sin precio de antes no hay con qué comparar: ni el que no tenía ni uno nuevo se resaltan.
        hoja.Poner(sinPrecio, "Precio", 1250);
        hoja.Agregar(("Código", nuevo), ("Producto", $"Compás {ficha}"), ("Departamento", nombreDepartamento), ("Precio", 1250));

        var (vista, hecho) = await VerYAplicarAsync(c, c.Encargado, libro);

        // Nada se ignora. El cuaderno va primero, resaltado y con su porqué; los demás, en el orden del archivo y sin resaltar.
        Assert.Equal((3, 1, 0, 1), (vista.TotalCambian, vista.TotalNuevos, vista.TotalIgnorados, vista.SinCambios));
        Assert.Equal([$"Cuaderno {ficha}", $"Agenda {ficha}", $"Sin precio {ficha}"], vista.Cambian.Select(x => x.Producto));
        Assert.Equal([true, false, false], vista.Cambian.Select(x => x.Revisar));
        var sube = Assert.Single(vista.Cambian[0].Cambios);
        Assert.Equal(("Precio", "$12.50", "$1,250.00", PorQueSeRevisa.SubeMucho), (sube.Dato, sube.Antes, sube.Despues, sube.Revisar));
        Assert.Equal("Precio: — → $1,250.00", Cambios(vista.Cambian[2]));
        Assert.Equal(125_000, Assert.Single(vista.Nuevos).PrecioCentavos);

        // Se aplica con el mismo botón, todo junto: el resaltado es para que se vea, no detiene nada.
        Assert.Equal((3, 1), (hecho.Cambiaron, hecho.Nuevos));
        Assert.Equal("Importó de Excel «Productos de prueba.xlsx»: cambió 3 productos; dio de alta 1 producto nuevo", hecho.Bitacora);
        Assert.Equal(125_000, (await PreciosAsync(c, deCuaderno.Id!.Value))[0].Precio);
        Assert.Equal(7000, (await PreciosAsync(c, deAgenda.Id!.Value))[0].Precio);
        Assert.Equal(125_000, (await PreciosAsync(c, deSinPrecio.Id!.Value))[0].Precio);
        Assert.Equal(500, (await PreciosAsync(c, deGoma.Id!.Value))[0].Precio);
    }

    [Fact]
    public async Task El_costo_y_el_margen_solo_los_ve_quien_tiene_el_permiso_y_exporta_quien_ve_productos()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var codigo = Codigo();
        var producto = await ProductoAsync(c, $"Costeado {Ficha()}", departamento, codigo, 2000);
        await ConCostoAsync(c, producto, 10, 1250);

        var (delDueno, _, _) = await ExportarAsync(c, c.Dueno, departamento: departamento);
        var (delEncargado, _, _) = await ExportarAsync(c, c.Encargado, departamento: departamento);

        // El dueño (con «Ver costos, utilidad y margen»): el costo promedio y el margen, al final.
        var hoja = Productos(delDueno);
        Assert.Equal(["Se vende", "Costo promedio", "Margen", "Clave interna"], hoja.Titulos.TakeLast(4));
        var renglon = Assert.Single(hoja.Renglones());
        Assert.Equal((10d, 12.5, 0.375), (renglon["Existencia"].GetNumber(), renglon["Costo promedio"].GetNumber(), renglon["Margen"].GetNumber()));
        Assert.Equal("0.0%", hoja.Renglon(codigo).Cell(hoja.Columna("Margen")).Style.NumberFormat.Format);
        Assert.Contains("el costo y el margen no se importan", string.Join(' ', hoja.Excel.Column(1).CellsUsed().Select(x => x.GetString())));
        // El encargado no: ni sus columnas, ni la palabra en todo el libro.
        Assert.DoesNotContain(Productos(delEncargado).Titulos, t => t is "Costo promedio" or "Margen");
        Assert.DoesNotContain("costo", string.Join('\n', delEncargado.Worksheets.SelectMany(h => h.CellsUsed()).Select(x => x.GetString())),
            StringComparison.OrdinalIgnoreCase);

        // Al importar, el dueño ve el margen de lo que cambia (con el mínimo de su departamento o el general); el encargado, no.
        Productos(delDueno).Poner(codigo, "Precio", 15);
        var vista = await VerAsync(c, c.Dueno, delDueno);
        var cambio = Assert.Single(vista.Cambian);
        Assert.True(vista.ConMargen);
        Assert.Equal((3750, 1667, 3000), (cambio.MargenAntesCentesimas, cambio.MargenDespuesCentesimas, cambio.MargenMinimoCentesimas));

        // El cajero no ve Productos: tampoco exporta.
        var delCajero = await ExportarRespuestaAsync(c, c.Cajero, departamento: departamento);
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal(RegistroProductosEnExcel.QuienExporta, (await delCajero.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    [Fact]
    public async Task Importa_quien_cambia_precios_y_exporta_tambien_quien_lo_tiene_con_autorizacion()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var codigo = Codigo();
        await ProductoAsync(c, $"Permiso {Ficha()}", departamento, codigo, 1000);
        // Alguien con «Productos, precios y etiquetas» «Con autorización»: ve Productos, pero no cambia precios.
        var usuarios = await Leer<UsuariosYPermisosDto>(await c.Http.PostAsJsonAsync($"api/cajas/{c.Datos.Id}/usuarios-y-permisos/usuarios",
            new SolicitudNuevoUsuario(c.Dueno.UsuarioId, $"Mostrador {Ficha()}", "4747")));
        var mostrador = usuarios.Elegido!.Value;
        await Leer<UsuariosYPermisosDto>(await c.Http.PutAsJsonAsync($"api/cajas/{c.Datos.Id}/usuarios-y-permisos/{mostrador}",
            new SolicitudPermisoUsuario(c.Dueno.UsuarioId, AccionControlada.EditarCatalogo, NivelPermiso.RequiereAutorizacion)));
        var conAutorizacion = c.Encargado with { UsuarioId = mostrador };

        var (libro, _, datos) = await ExportarAsync(c, conAutorizacion, departamento: departamento);

        Assert.Single(Productos(libro).Renglones());
        var vista = await VistaAsync(c, Solicitud(c, conAutorizacion, datos));
        Assert.Equal(HttpStatusCode.Forbidden, vista.StatusCode);
        Assert.Contains("«Productos, precios y etiquetas»", (await vista.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal(HttpStatusCode.Forbidden, (await VistaAsync(c, Solicitud(c, c.Cajero, datos))).StatusCode);
        var aplicar = await AplicarAsync(c, Solicitud(c, c.Cajero, datos, huella: "x", id: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, aplicar.StatusCode);
        // El intento de aplicar sin permiso queda en la bitácora.
        await using var db = servicio.Base();
        Assert.True(await db.Bitacora.AnyAsync(b => b.UsuarioId == c.Cajero.UsuarioId && b.Accion == AccionControlada.EditarCatalogo
            && b.Resultado == ResultadoBitacora.Negado));
    }

    [Fact]
    public async Task En_el_piloto_con_eleventa_no_se_importa_y_si_se_exporta()
    {
        var c = await PrepararAsync(servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true")).CreateClient());
        var (departamento, _) = await DepartamentoAsync(await PrepararAsync());
        var codigo = Codigo();
        await ProductoAsync(await PrepararAsync(), $"Piloto {Ficha()}", departamento, codigo, 1000);

        // Exportar, sí: es solo leer.
        var (libro, _, _) = await ExportarAsync(c, c.Encargado, departamento: departamento);
        Productos(libro).Poner(codigo, "Precio", 12);
        var datos = Bytes(libro);

        // Importar, no: las altas y los precios se hacen en eleventa (el permiso va primero).
        Assert.Equal(ProductosEnExcel.ImportarEnElPiloto, await RechazoAsync(await VistaAsync(c, Solicitud(c, c.Encargado, datos))));
        Assert.Equal(ProductosEnExcel.ImportarEnElPiloto,
            await RechazoAsync(await AplicarAsync(c, Solicitud(c, c.Encargado, datos, huella: "x", id: Guid.NewGuid()))));
        Assert.Equal(HttpStatusCode.Forbidden, (await VistaAsync(c, Solicitud(c, c.Cajero, datos))).StatusCode);
        // Lo que dice la caja: el mismo aviso.
        Assert.NotNull((await c.Http.GetFromJsonAsync<TrastiendaEnElPilotoDto>("api/catalogo/piloto"))!.Productos);
        Assert.Equal(1000, (await PreciosAsync(c, (await FichaPorCodigoAsync(c, codigo)).Id!.Value))[0].Precio);
    }

    private static async Task<FichaProductoDto> FichaPorCodigoAsync(Caja c, string codigo)
    {
        var lista = await Leer<ListaProductosDto>(await c.Http.GetAsync(
            $"api/catalogo/lista?sucursalId={c.Sucursal}&usuarioId={c.Encargado.UsuarioId}&q={codigo}&filtro=Todos"));
        return await FichaAsync(c, lista.Productos.Single().ProductoId);
    }

    [Fact]
    public async Task Lo_que_no_es_el_Excel_de_los_productos_se_rechaza_con_lo_que_hay_que_hacer()
    {
        var c = await PrepararAsync();
        async Task<string> RechazoDeAsync(byte[] datos, string archivo = "Productos.xlsx") =>
            await RechazoAsync(await VistaAsync(c, Solicitud(c, c.Encargado, datos, archivo)));

        Assert.Equal("«Productos.xls» no es un libro de Excel (.xlsx): ábrelo en Excel y guárdalo como «Libro de Excel (*.xlsx)». Luego vuelve a importarlo.",
            await RechazoDeAsync("Esto no es un Excel"u8.ToArray(), "Productos.xls"));
        Assert.Equal("No llegó el archivo: elígelo otra vez.", await RechazoAsync(await VistaAsync(c, new SolicitudImportarProductos(c.Encargado.UsuarioId,
            c.Sucursal, "Productos.xlsx", ""))));
        Assert.Equal("El archivo no llegó completo: elígelo otra vez.", await RechazoAsync(await VistaAsync(c, new SolicitudImportarProductos(
            c.Encargado.UsuarioId, c.Sucursal, "Productos.xlsx", "esto-no-es-base64"))));
        Assert.StartsWith("El archivo pesa más de 8 MB", await RechazoAsync(await VistaAsync(c, new SolicitudImportarProductos(c.Encargado.UsuarioId,
            c.Sucursal, "Productos.xlsx", new string('A', 12 * 1024 * 1024)))));

        // Un Excel sin la columna «Código».
        using (var otro = new XLWorkbook())
        {
            otro.AddWorksheet("Hoja1").Cell(1, 1).Value = "Nombre";
            Assert.Equal("«Productos.xlsx» no es el Excel de los productos: ninguna hoja trae la columna «Código». Exporta los productos con «Exportar a Excel» "
                + "y cambia ese archivo.", await RechazoDeAsync(Bytes(otro)));
        }
        // Más de 10,000 renglones.
        using (var grande = new XLWorkbook())
        {
            var hoja = grande.AddWorksheet("Productos");
            hoja.Cell(1, 1).Value = "Código";
            hoja.Cell(1, 2).Value = "Precio";
            for (var fila = 2; fila <= ProductosEnExcel.MaximoRenglones + 2; fila++)
                hoja.Cell(fila, 1).Value = $"G{fila}";
            Assert.Equal("La hoja «Productos» trae más de 10,000 renglones: se importan hasta 10,000 a la vez. Pártelo en dos archivos, o exporta un "
                + "departamento a la vez.", await RechazoDeAsync(Bytes(grande)));
        }
    }

    [Fact]
    public async Task Las_formulas_valen_lo_que_calculan_y_se_toleran_los_espacios_y_los_miles()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (formula, miles, espacios, sinCalcular) = (Codigo(), Codigo(), Codigo(), Codigo());
        var conFormula = await ProductoAsync(c, $"Fórmula {ficha}", departamento, formula, 1000);
        var conMiles = await ProductoAsync(c, $"Miles {ficha}", departamento, miles, 1000);
        var conEspacios = await ProductoAsync(c, $"Espacios {ficha}", departamento, espacios, 1000, [(12, 900)]);
        await ProductoAsync(c, $"Sin calcular {ficha}", departamento, sinCalcular, 1000);
        var (libro, _, _) = await ExportarAsync(c, c.Encargado, departamento: departamento);
        var hoja = Productos(libro);
        // Una fórmula (Excel guarda lo que calculó; si no lo trae, como aquí, se calcula al importar): 10 % más, con decimales de
        // más: se redondea al centavo, como se ve con el formato de pesos.
        var precio = hoja.Columna("Precio");
        hoja.Renglon(formula).Cell(precio).FormulaA1 = "10*1.1+0.001";
        hoja.Poner(miles, "Precio", " $1,234.50 ");
        hoja.Poner(espacios, "Mayoreo 1: desde", "1 2");
        hoja.Poner(espacios, "Precio", "\u00A012.5");
        hoja.Renglon(sinCalcular).Cell(precio).FormulaA1 = "WEBSERVICE(\"http://localhost/precio\")";

        var vista = await VerAsync(c, c.Encargado, libro);

        Assert.Equal("Precio: $10.00 → $11.00", Cambios(vista.Cambian.Single(x => x.ProductoId == conFormula.Id)));
        Assert.Equal("Precio: $10.00 → $1,234.50", Cambios(vista.Cambian.Single(x => x.ProductoId == conMiles.Id)));
        Assert.Equal("Precio: $10.00 → $12.50", Cambios(vista.Cambian.Single(x => x.ProductoId == conEspacios.Id)));
        // Una fórmula que solo calcula Excel: se pide abrirlo y guardarlo en Excel (o, si ya trae su error, corregirlo). Los dos
        // mensajes, con un archivo hecho a mano, en ExcelDeProductosTests.
        var ignorado = Assert.Single(vista.Ignorados);
        Assert.Equal(PorQueSeIgnora.DatoInvalido, ignorado.PorQue);
        Assert.StartsWith("«Precio» tiene ", ignorado.Detalle);
        _ = nombreDepartamento;
    }

    [Fact]
    public async Task Variantes_presentaciones_y_productos_sin_codigo()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (azul, roja, pieza, caja, otraCaja, bolsa) = (Codigo(), Codigo(), Codigo(), Codigo(), Codigo(), Codigo());
        var pluma = await ProductoAsync(c, $"Pluma {ficha}", departamento, null, true, Variante("Azul", [azul], 1000), Variante("Roja", [roja], 1000));
        var lapiz = await ProductoAsync(c, $"Lápiz {ficha}", departamento, null, true,
            Variante("", [pieza], 600, paquetes: [("Caja 12", 12, 6500, [caja]), ("Bolsa 50", 50, null, [bolsa])]));
        // Un servicio sin código (como las copias): se reconoce por su nombre.
        var engargolado = await Leer<FichaProductoDto>(await c.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(c.Encargado.UsuarioId,
            c.Sucursal, new FichaProductoDto(null, $"Engargolado {ficha}", departamento, null, TipoProducto.Servicio, Impuesto.Iva16, null, true, false,
                [Variante("", [], 2500)]))));
        var (libro, _, _) = await ExportarAsync(c, c.Encargado, departamento: departamento);
        var hoja = Productos(libro);
        // El nombre de la pluma, igual en sus dos variantes; la roja ya no se vende.
        hoja.Poner(azul, "Producto", $"Pluma fina {ficha}");
        hoja.Poner(roja, "Producto", $"Pluma fina {ficha}");
        hoja.Poner(roja, "Se vende", "No");
        // El engargolado, sin código: su precio.
        var filaEngargolado = hoja.Excel.RowsUsed().First(r => r.Cell(hoja.Columna("Producto")).GetString() == $"Engargolado {ficha}");
        Assert.True(filaEngargolado.Cell(1).IsEmpty());
        filaEngargolado.Cell(hoja.Columna("Precio")).Value = 30;
        // Presentaciones: la caja con otro código y sin precio propio; la bolsa cambia lo que trae (no se puede); una nueva.
        var presentaciones = Presentaciones(libro);
        presentaciones.Poner(pieza, "Códigos", $"{caja}, {otraCaja}", "Caja 12");
        presentaciones.Poner(pieza, "Precio", Blank.Value, "Caja 12");
        presentaciones.Poner(pieza, "Contiene", 40, "Bolsa 50");
        var nueva = presentaciones.Agregar(("Código del producto", pieza), ("Presentación", "Paquete 6"), ("Contiene", 6), ("Precio", 33));

        var (vista, _) = await VerYAplicarAsync(c, c.Encargado, libro);

        Assert.Equal($"Nombre: Pluma {ficha} → Pluma fina {ficha}", Cambios(vista.Cambian.Single(x => x.VarianteId == pluma.Variantes[0].Id)));
        Assert.Equal($"Nombre: Pluma {ficha} → Pluma fina {ficha} · Se vende: Sí → No", Cambios(vista.Cambian.Single(x => x.VarianteId == pluma.Variantes[1].Id)));
        Assert.Equal("Precio: $25.00 → $30.00", Cambios(vista.Cambian.Single(x => x.ProductoId == engargolado.Id)));
        var deLapiz = vista.Cambian.Single(x => x.ProductoId == lapiz.Id);
        Assert.Equal((ProductosEnExcel.HojaProductos, pieza), (deLapiz.Hoja, deLapiz.Codigo));
        Assert.Equal($"Caja 12: códigos: {caja} → {caja}, {otraCaja} · Caja 12: precio: $65.00 → — · Paquete 6: — → nueva: trae 6 piezas · $33.00",
            Cambios(deLapiz));
        var bolsaIgnorada = Ignorado(vista, presentaciones.Renglon(pieza, "Bolsa 50").RowNumber(), ProductosEnExcel.HojaPresentaciones);
        Assert.Equal((PorQueSeIgnora.NoSeCambiaDesdeExcel, "«Bolsa 50» trae 50 piezas: lo que trae una presentación se cambia en su ficha."),
            (bolsaIgnorada.PorQue, bolsaIgnorada.Detalle));

        var fichaPluma = await FichaAsync(c, pluma.Id!.Value);
        Assert.Equal(($"Pluma fina {ficha}", true), (fichaPluma.Nombre, fichaPluma.Activo));
        Assert.Equal([("Azul", true), ("Roja", false)], fichaPluma.Variantes.Select(v => (v.Descripcion, v.Activa)));
        var fichaLapiz = await FichaAsync(c, lapiz.Id!.Value);
        Assert.Equal([(null, 1L, 600L), ("Paquete 6", 1L, 3300L)], fichaLapiz.Variantes[0].Precios.Select(p => (p.Presentacion, p.CantidadMinimaMilesimas / 1000,
            p.PrecioCentavos)));
        Assert.Equal(new[] { caja, otraCaja }.Order(), fichaLapiz.Variantes[0].Presentaciones.Single(p => p.Nombre == "Caja 12").Codigos.Select(x => x.Codigo).Order());
        Assert.Equal(6000, fichaLapiz.Variantes[0].Presentaciones.Single(p => p.Nombre == "Paquete 6").FactorMilesimas);
        Assert.Equal(3000, (await PreciosAsync(c, engargolado.Id!.Value))[0].Precio);
        _ = (nueva, nombreDepartamento);
    }

    [Fact]
    public async Task Todo_el_catalogo_de_ida_y_vuelta_sin_cambios_no_cambia_nada()
    {
        var c = await PrepararAsync();
        var (libro, _, datos) = await ExportarAsync(c, c.Dueno, inactivos: true);
        var renglones = Productos(libro).Renglones().Count + Presentaciones(libro).Renglones().Count;

        var vista = await VerAsync(c, c.Dueno, datos);

        Assert.Equal(renglones, vista.Renglones);
        // Todos se reconocen, también los productos sin código que se llaman igual que otro (la papelería ficticia los trae a
        // propósito, como el catálogo de eleventa): cada renglón trae su clave escondida.
        Assert.Equal((0, 0, 0), (vista.TotalCambian, vista.TotalNuevos, vista.TotalIgnorados));
        Assert.Empty(vista.CambiaronDespues!);
        Assert.Equal(renglones, vista.SinCambios);
    }

    [Fact]
    public async Task Lo_que_esta_por_completar_se_completa_en_Excel_y_las_columnas_ajenas_se_dicen()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var (completo, nombreCompleto) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var codigo = Codigo();
        // El cajero lo dio de alta desde la caja: queda «por completar».
        await Leer<ProductoVentaDto>(await c.Http.PostAsJsonAsync("api/catalogo/alta-rapida",
            new SolicitudAltaRapida(c.Cajero.UsuarioId, c.Sucursal, codigo, $"Pegamento {ficha}", 1800, departamento)));
        var (libro, _, _) = await ExportarAsync(c, c.Encargado, departamento: departamento, filtro: FiltroProductos.PorCompletar);
        var hoja = Productos(libro);
        Assert.Equal([codigo], hoja.Renglones().Select(r => r["Código"].GetText()));
        hoja.Poner(codigo, "Departamento", nombreCompleto);
        hoja.Poner(codigo, "Marca", $"Resistol {ficha}");
        // Una columna que el Excel no trae: se dice que no se usa.
        hoja.Excel.Cell(hoja.FilaTitulos, hoja.Titulos.Count + 1).Value = "Notas";
        hoja.Renglon(codigo).Cell(hoja.Titulos.Count).Value = "Llamar al proveedor";

        var (vista, _) = await VerYAplicarAsync(c, c.Encargado, libro);

        var cambio = Assert.Single(vista.Cambian);
        Assert.Equal($"Departamento: {nombreDepartamento} → {nombreCompleto} · Marca: — → Resistol {ficha} · Por completar: Sí → No", Cambios(cambio));
        Assert.Contains("No se usan la columna «Notas»: no es de este Excel.", vista.Avisos);
        var fichaCompleta = await FichaPorCodigoAsync(c, codigo);
        Assert.Equal((completo, false), (fichaCompleta.DepartamentoId, fichaCompleta.PorCompletar));
    }
}
