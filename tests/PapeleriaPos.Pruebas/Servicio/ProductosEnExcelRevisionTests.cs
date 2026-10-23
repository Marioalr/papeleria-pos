using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Servicio.Excel;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Productos en Excel después de la revisión del PR #135 (2026-10-23), contra el servicio con datos ficticios: cada caso que
/// encontró, ya corregido.
/// <list type="bullet">
/// <item>Un renglón sin código se reconoce por la clave escondida de su variante, nunca por el nombre de otro producto; uno escrito
/// a mano, por su nombre y su departamento, entre los que no tienen código en su pieza (como los deja el Excel).</item>
/// <item>Un Excel exportado antes no regresa lo que cambió después: va aparte, con lo que cambió, y no se aplica hasta «Aplicarlos
/// de todos modos»; uno que no trae su versión pone así a todos los que ya existen.</item>
/// <item>«Se vende» es de todo el producto; las listas van completas, con lo que conviene revisar primero; la base se aparta solo
/// para guardar; y la bitácora guarda lo completo de lo que cambió.</item>
/// <item>Los topes del archivo antes de abrirlo, un precio enorme, las presentaciones de un cobro de terceros, los códigos que
/// solo cambian en mayúsculas, la caja con precio propio desde 2, el producto sin precio y los códigos con coma.</item>
/// </list>
/// </summary>
public sealed class ProductosEnExcelRevisionTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
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

    private static async Task<string> RechazoAsync(HttpResponseMessage r)
    {
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    private async Task<Caja> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> EntrarAsync(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Caja(http, caja, await EntrarAsync("cajero", "1111"), await EntrarAsync("encargado", "2222"), await EntrarAsync("dueno", "3333"));
    }

    private static string Ficha() => Guid.NewGuid().ToString("N")[..8];

    private static string Codigo() => $"75{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";

    private static async Task<(Guid Id, string Nombre)> DepartamentoAsync(Caja c, string? nombre = null)
    {
        nombre ??= $"Revisión {Ficha()}";
        var lista = await Leer<DepartamentosDto>(await c.Http.PostAsJsonAsync("api/catalogo/departamentos",
            new SolicitudDepartamento(c.Encargado.UsuarioId, c.Sucursal, nombre)));
        return (lista.Departamentos.Single(d => d.Nombre == nombre).Id, nombre);
    }

    /// <summary>Una variante para la ficha: su pieza con sus códigos, su menudeo y sus escalas, y sus paquetes (con sus códigos y su precio propio).</summary>
    private static VarianteFichaDto Variante(
        string descripcion, string[] codigos, long menudeo, (long Desde, long Precio)[]? escalas = null,
        (string Nombre, long Piezas, long? Precio, string[] Codigos)[]? paquetes = null, bool activa = true)
    {
        var presentaciones = new List<PresentacionFichaDto> { new(null, "Pieza", 1000, true, codigos.Select(x => new CodigoFichaDto(x)).ToList()) };
        var precios = new List<PrecioFichaDto> { new(null, 1000, menudeo) };
        precios.AddRange((escalas ?? []).Select(e => new PrecioFichaDto(null, e.Desde * 1000, e.Precio)));
        foreach (var (nombre, piezas, precio, suyos) in paquetes ?? [])
        {
            presentaciones.Add(new PresentacionFichaDto(null, nombre, piezas * 1000, false, suyos.Select(x => new CodigoFichaDto(x)).ToList()));
            if (precio is { } propio)
                precios.Add(new PrecioFichaDto(nombre, 1000, propio));
        }
        return new VarianteFichaDto(null, descripcion, UnidadBase.Pieza, activa, presentaciones, precios);
    }

    private static async Task<FichaProductoDto> ProductoAsync(Caja c, string nombre, Guid departamento, params VarianteFichaDto[] variantes) =>
        await ProductoAsync(c, nombre, departamento, true, TipoProducto.Mercancia, variantes);

    private static async Task<FichaProductoDto> ProductoAsync(Caja c, string nombre, Guid departamento, bool activo, TipoProducto tipo,
        params VarianteFichaDto[] variantes) =>
        await Leer<FichaProductoDto>(await c.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Sucursal,
            new FichaProductoDto(null, nombre, departamento, null, tipo, Impuesto.Iva16, null, activo, false, variantes))));

    private static async Task<FichaProductoDto> FichaAsync(Caja c, Guid productoId) =>
        await Leer<FichaProductoDto>(await c.Http.GetAsync($"api/catalogo/productos/{productoId}?sucursalId={c.Sucursal}&usuarioId={c.Encargado.UsuarioId}"));

    private static async Task<long?> MenudeoAsync(Caja c, Guid productoId, int variante = 0) =>
        (await FichaAsync(c, productoId)).Variantes[variante].Precios.SingleOrDefault(p => p.Presentacion is null && p.CantidadMinimaMilesimas == 1000)?.PrecioCentavos;

    private static async Task<(XLWorkbook Libro, byte[] Datos)> ExportarAsync(Caja c, SesionDto quien, Guid? departamento = null, bool inactivos = false)
    {
        var r = await c.Http.GetAsync($"api/catalogo/lista/excel?sucursalId={c.Sucursal}&usuarioId={quien.UsuarioId}&q=&filtro=Todos"
            + (departamento is { } d ? $"&departamentoId={d}" : "") + $"&inactivos={inactivos}");
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var datos = await r.Content.ReadAsByteArrayAsync();
        return (new XLWorkbook(new MemoryStream(datos)), datos);
    }

    /// <summary>Una hoja del Excel: dónde están sus títulos, sus columnas y el renglón de cada código.</summary>
    private sealed class Hoja(IXLWorksheet hoja)
    {
        public IXLWorksheet Excel => hoja;
        public int FilaTitulos { get; } = hoja.RowsUsed().First(r => r.CellsUsed().Any(x => x.GetString() is "Código" or "Código del producto")).RowNumber();
        public List<string> Titulos => hoja.Row(FilaTitulos).CellsUsed().Select(x => x.GetString()).ToList();

        public int Columna(string titulo) =>
            hoja.Row(FilaTitulos).CellsUsed().FirstOrDefault(x => x.GetString() == titulo)?.Address.ColumnNumber
            ?? throw new Xunit.Sdk.XunitException($"No está la columna «{titulo}»: {string.Join(", ", Titulos)}");

        public IXLRow Renglon(string codigo, string? presentacion = null) =>
            hoja.RowsUsed().FirstOrDefault(r => r.RowNumber() > FilaTitulos && r.Cell(1).GetString() == codigo
                && (presentacion is null || r.Cell(Columna("Presentación")).GetString() == presentacion))
            ?? throw new Xunit.Sdk.XunitException($"No está el renglón de «{codigo}».");

        public IXLRow RenglonPorNombre(string producto, string? variante = null) =>
            hoja.RowsUsed().First(r => r.RowNumber() > FilaTitulos && r.Cell(Columna("Producto")).GetString() == producto
                && (variante is null || r.Cell(Columna("Variante")).GetString() == variante));

        public void Poner(string codigo, string titulo, XLCellValue valor, string? presentacion = null) =>
            Renglon(codigo, presentacion).Cell(Columna(titulo)).Value = valor;

        public int Agregar(params (string Titulo, XLCellValue Valor)[] valores)
        {
            var fila = Math.Max(hoja.LastRowUsed()!.RowNumber(), FilaTitulos) + 1;
            foreach (var (titulo, valor) in valores)
                hoja.Cell(fila, Columna(titulo)).Value = valor;
            return fila;
        }

        public int Renglones => hoja.RowsUsed().Count(r => r.RowNumber() > FilaTitulos);
    }

    private static Hoja Productos(XLWorkbook libro) => new(libro.Worksheet(ProductosEnExcel.HojaProductos));
    private static Hoja Presentaciones(XLWorkbook libro) => new(libro.Worksheet(ProductosEnExcel.HojaPresentaciones));

    /// <summary>El libro como .xlsx. Una sola vez por libro (ClosedXML suelta el archivo del que lo abrió al guardarlo).</summary>
    private static byte[] Bytes(XLWorkbook libro)
    {
        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        return flujo.ToArray();
    }

    private static SolicitudImportarProductos Solicitud(Caja c, SesionDto quien, byte[] datos, string? huella = null, Guid? id = null, bool deTodosModos = false) =>
        new(quien.UsuarioId, c.Sucursal, "Revisión.xlsx", Convert.ToBase64String(datos), huella, id, deTodosModos);

    private static async Task<VistaImportacionDto> VerAsync(Caja c, SesionDto quien, byte[] datos, bool deTodosModos = false) =>
        await Leer<VistaImportacionDto>(await c.Http.PostAsJsonAsync("api/catalogo/importar/vista", Solicitud(c, quien, datos, deTodosModos: deTodosModos)));

    private static async Task<(VistaImportacionDto Vista, ImportacionAplicadaDto Hecho)> VerYAplicarAsync(Caja c, SesionDto quien, byte[] datos,
        bool deTodosModos = false)
    {
        var vista = await VerAsync(c, quien, datos, deTodosModos);
        return (vista, await Leer<ImportacionAplicadaDto>(await c.Http.PostAsJsonAsync("api/catalogo/importar",
            Solicitud(c, quien, datos, vista.Huella, Guid.NewGuid(), deTodosModos))));
    }

    private static string Cambios(ProductoQueCambiaDto cambio) => string.Join(" · ", cambio.Cambios.Select(x => $"{x.Dato}: {x.Antes} → {x.Despues}"));

    private static RenglonIgnoradoDto Ignorado(VistaImportacionDto vista, int renglon, string hoja = ProductosEnExcel.HojaProductos) =>
        vista.Ignorados.SingleOrDefault(x => x.Hoja == hoja && x.Renglon == renglon)
        ?? throw new Xunit.Sdk.XunitException($"El renglón {renglon} de «{hoja}» no se ignoró: "
            + string.Join("; ", vista.Ignorados.Select(x => $"{x.Hoja} {x.Renglon}: {x.Detalle}")));

    // ── Un renglón sin código: por su clave, nunca otro que se llama igual ───────

    [Fact]
    public async Task Una_pieza_sin_codigo_se_reconoce_por_su_clave_y_nunca_cambia_a_otro_que_se_llama_igual()
    {
        var c = await PrepararAsync();
        var (escolar, _) = await DepartamentoAsync(c);
        var (oficina, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        // X: la pieza sin código y la caja con el del fabricante (como los lápices); Y: otro que se llama igual, sin ningún código.
        var x = await ProductoAsync(c, $"Lápiz {ficha}", escolar, Variante("", [], 600, paquetes: [("Caja 12", 12, null, [Codigo()])]));
        var y = await ProductoAsync(c, $"Lápiz {ficha}", oficina, Variante("", [], 900));
        var (libro, datos) = await ExportarAsync(c, c.Encargado, escolar);

        // Tal cual se exportó: no cambia nada, y el de la oficina ni se toca.
        var igual = await VerAsync(c, c.Encargado, datos);
        Assert.Equal((0, 0, 0, 2), (igual.TotalCambian, igual.TotalNuevos, igual.TotalIgnorados, igual.SinCambios));

        // Su precio y el de su caja: cambian en X, por su clave.
        Productos(libro).RenglonPorNombre($"Lápiz {ficha}").Cell(Productos(libro).Columna("Precio")).Value = 7;
        var presentaciones = Presentaciones(libro);
        presentaciones.Excel.RowsUsed().First(r => r.Cell(presentaciones.Columna("Presentación")).GetString() == "Caja 12")
            .Cell(presentaciones.Columna("Precio")).Value = 70;
        var (vista, _) = await VerYAplicarAsync(c, c.Encargado, Bytes(libro));

        var cambio = Assert.Single(vista.Cambian);
        Assert.Equal((x.Id, "Precio: $6.00 → $7.00 · Caja 12: precio: — → $70.00"), (cambio.ProductoId, Cambios(cambio)));
        Assert.Equal(700, await MenudeoAsync(c, x.Id!.Value));
        var deY = await FichaAsync(c, y.Id!.Value);
        Assert.Equal((oficina, 900L), (deY.DepartamentoId, deY.Variantes[0].Precios.Single().PrecioCentavos));
    }

    [Fact]
    public async Task Escrito_a_mano_sin_codigo_se_reconoce_por_su_nombre_y_su_departamento()
    {
        var c = await PrepararAsync();
        var (uno, nombreUno) = await DepartamentoAsync(c);
        var (otro, nombreOtro) = await DepartamentoAsync(c);
        var ficha = Ficha();
        Task<FichaProductoDto> ServicioAsync(string nombre, Guid departamento, long precio) =>
            ProductoAsync(c, nombre, departamento, true, TipoProducto.Servicio, Variante("", [], precio));
        var enUno = await ServicioAsync($"Engargolado {ficha}", uno, 2500);
        var enOtro = await ServicioAsync($"Engargolado {ficha}", otro, 2500);
        // Dos que se llaman igual en el mismo departamento: no se sabe cuál.
        await ServicioAsync($"Copias {ficha}", uno, 100);
        await ServicioAsync($"Copias {ficha}", uno, 150);

        // Un Excel hecho a mano (sin la clave de cada renglón, ni de cuándo es).
        using var libro = new XLWorkbook();
        var hoja = libro.AddWorksheet(ProductosEnExcel.HojaProductos);
        string[] titulos = ["Código", "Producto", "Departamento", "Precio"];
        for (var i = 0; i < titulos.Length; i++)
            hoja.Cell(1, i + 1).Value = titulos[i];
        void Renglon(int fila, string producto, string? departamento, double precio)
        {
            hoja.Cell(fila, 2).Value = producto;
            if (departamento is not null)
                hoja.Cell(fila, 3).Value = departamento;
            hoja.Cell(fila, 4).Value = precio;
        }
        Renglon(2, $"Engargolado {ficha}", $" {nombreUno.ToUpperInvariant()} ", 30);
        Renglon(3, $"Copias {ficha}", nombreUno, 2);
        Renglon(4, $"Engargolado {ficha}", null, 30);
        var datos = Bytes(libro);

        var vista = await VerAsync(c, c.Encargado, datos);

        // El de ese departamento, y no el del otro; como el Excel no dice de cuándo es, va aparte hasta aplicarlo de todos modos.
        var aparte = Assert.Single(vista.CambiaronDespues!);
        Assert.Equal((enUno.Id, ProductosEnExcel.NoSeSabeSiCambio, "Precio: $25.00 → $30.00"), (aparte.ProductoId, Assert.Single(aparte.CambioDespues!), Cambios(aparte)));
        Assert.Contains(ProductosEnExcel.SinVersion, vista.Avisos);
        Assert.False(vista.HayQueAplicar);
        Assert.Equal((PorQueSeIgnora.SinCodigo, $"Sin código, y hay 2 productos sin código que se llaman «Copias {ficha}» en «{nombreUno}»: cámbialo en su ficha."),
            (Ignorado(vista, 3).PorQue, Ignorado(vista, 3).Detalle));
        Assert.Equal((PorQueSeIgnora.SinCodigo, $"Sin código: para reconocer «Engargolado {ficha}» por su nombre también va su departamento. Un producto nuevo "
            + "lleva su código."), (Ignorado(vista, 4).PorQue, Ignorado(vista, 4).Detalle));

        var (deTodosModos, _) = await VerYAplicarAsync(c, c.Encargado, datos, deTodosModos: true);
        Assert.Equal(enUno.Id, Assert.Single(deTodosModos.Cambian).ProductoId);
        Assert.Equal((3000L, 2500L), ((await MenudeoAsync(c, enUno.Id!.Value))!.Value, (await MenudeoAsync(c, enOtro.Id!.Value))!.Value));
        _ = nombreOtro;
    }

    [Fact]
    public async Task Un_renglon_cuyo_codigo_ahora_es_de_otro_producto_no_lo_cambia()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (a, b, d, e) = (Codigo(), Codigo(), Codigo(), Codigo());
        var deA = await ProductoAsync(c, $"Folder A {ficha}", departamento, Variante("", [a], 1000));
        var deB = await ProductoAsync(c, $"Folder B {ficha}", departamento, Variante("", [b], 2000));
        var deD = await ProductoAsync(c, $"Sobre D {ficha}", departamento, Variante("", [d], 500));
        var deE = await ProductoAsync(c, $"Sobre E {ficha}", departamento, Variante("", [e], 600));
        var (libro, _) = await ExportarAsync(c, c.Encargado, departamento);
        var hoja = Productos(libro);
        var (filaA, filaD) = (hoja.Renglon(a).RowNumber(), hoja.Renglon(d).RowNumber());

        // Después de exportar: el código de A pasó a B, y D se juntó con E (sus códigos ya son de E).
        await using (var db = servicio.Base())
        {
            var codigo = await db.CodigosBarras.SingleAsync(x => x.Codigo == a);
            codigo.PresentacionId = await db.Presentaciones.Where(p => p.VarianteId == deB.Variantes[0].Id && p.EsBase).Select(p => p.Id).SingleAsync();
            await db.SaveChangesAsync();
        }
        Assert.True((await c.Http.PostAsJsonAsync("api/catalogo/depuracion/juntar", new SolicitudJuntar(c.Encargado.UsuarioId, c.Sucursal,
            deD.Variantes[0].Id!.Value, deE.Variantes[0].Id!.Value))).IsSuccessStatusCode);
        hoja.Poner(a, "Precio", 11);
        hoja.Poner(d, "Precio", 7);

        var vista = await VerAsync(c, c.Encargado, Bytes(libro));

        Assert.Equal((PorQueSeIgnora.CodigoDeOtroProducto, $"Este renglón era de «Folder A {ficha}» al exportar el Excel, y el código «{a}» ahora es de "
            + $"«Folder B {ficha}»: revísalos en sus fichas."), (Ignorado(vista, filaA).PorQue, Ignorado(vista, filaA).Detalle));
        Assert.Equal((PorQueSeIgnora.ProductoNoExiste, $"Este renglón era de «Sobre D {ficha}», que se juntó con «Sobre E {ficha}» después de exportar el "
            + "Excel: cambia ese en su propio renglón."), (Ignorado(vista, filaD).PorQue, Ignorado(vista, filaD).Detalle));
        // B y E tampoco: su renglón les quitaría el código que les llegó después de exportar; van aparte, con lo que cambió.
        Assert.DoesNotContain(vista.Cambian, x => x.ProductoId == deB.Id || x.ProductoId == deE.Id);
        Assert.Contains("sus códigos", vista.CambiaronDespues!.Single(x => x.ProductoId == deB.Id).CambioDespues!);
        Assert.Contains("sus códigos", vista.CambiaronDespues!.Single(x => x.ProductoId == deE.Id).CambioDespues!);
        _ = deA;
    }

    [Fact]
    public async Task Un_codigo_nuevo_en_un_producto_que_no_tenia_se_le_pone_a_el()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var engargolado = await ProductoAsync(c, $"Engargolado {ficha}", departamento, true, TipoProducto.Servicio, Variante("", [], 2500));
        var (libro, _) = await ExportarAsync(c, c.Encargado, departamento);
        var codigo = Codigo();
        Productos(libro).RenglonPorNombre($"Engargolado {ficha}").Cell(1).Value = codigo;

        var (vista, _) = await VerYAplicarAsync(c, c.Encargado, Bytes(libro));

        Assert.Empty(vista.Nuevos);
        var cambio = Assert.Single(vista.Cambian);
        Assert.Equal((engargolado.Id, $"Códigos: — → {codigo}"), (cambio.ProductoId, Cambios(cambio)));
        await servicio.CatalogoAlDiaAsync();
        var escaneado = await Leer<ProductoVentaDto>(await c.Http.GetAsync($"api/productos/codigo/{codigo}?sucursalId={c.Sucursal}"));
        Assert.Equal(engargolado.Variantes[0].Id, escaneado.VarianteId);
    }

    // ── Lo que cambió después de exportar el Excel ─────────────────────────────

    [Fact]
    public async Task Un_excel_exportado_antes_no_regresa_lo_que_cambio_despues_sin_verlo()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (subio, otro) = (Codigo(), Codigo());
        var cuaderno = await ProductoAsync(c, $"Cuaderno {ficha}", departamento, Variante("", [subio], 2500));
        var regla = await ProductoAsync(c, $"Regla {ficha}", departamento, Variante("", [otro], 1500));
        var (libro, _) = await ExportarAsync(c, c.Encargado, departamento);

        // Después de exportar, el dueño le subió el precio al cuaderno (en su ficha, o en otra caja).
        Assert.True((await c.Http.PostAsJsonAsync("api/catalogo/precio-menudeo",
            new SolicitudPrecioMenudeo(c.Dueno.UsuarioId, c.Sucursal, cuaderno.Variantes[0].Id!.Value, 3200))).IsSuccessStatusCode);
        // El encargado termina su Excel días después: solo le cambió la regla.
        Productos(libro).Poner(otro, "Precio", 16);
        var datos = Bytes(libro);

        var (vista, hecho) = await VerYAplicarAsync(c, c.Encargado, datos);

        // La regla se aplica; el cuaderno va aparte, con lo que cambió, y se queda como está.
        Assert.Equal((regla.Id, "Precio: $15.00 → $16.00"), (Assert.Single(vista.Cambian).ProductoId, Cambios(vista.Cambian[0])));
        var aparte = Assert.Single(vista.CambiaronDespues!);
        Assert.Equal((cuaderno.Id, "Precio: $25.00 → $32.00", "Precio: $32.00 → $25.00"), (aparte.ProductoId, Assert.Single(aparte.CambioDespues!), Cambios(aparte)));
        Assert.Equal(PorQueSeRevisa.RegresaLoDeAntes, Assert.Single(aparte.Cambios).Revisar);
        Assert.Equal((3200L, 1600L), ((await MenudeoAsync(c, cuaderno.Id!.Value))!.Value, (await MenudeoAsync(c, regla.Id!.Value))!.Value));
        Assert.Equal(1, hecho.Cambiaron);

        // De todos modos, después de verlo: va primero, y regresa al del Excel.
        var (deTodosModos, _) = await VerYAplicarAsync(c, c.Encargado, datos, deTodosModos: true);
        Assert.Empty(deTodosModos.CambiaronDespues!);
        Assert.Equal((cuaderno.Id, true), (deTodosModos.Cambian[0].ProductoId, deTodosModos.Cambian[0].Revisar));
        Assert.Equal(2500, await MenudeoAsync(c, cuaderno.Id!.Value));
    }

    [Fact]
    public async Task Un_departamento_que_cambio_de_nombre_despues_de_exportar_no_se_vuelve_a_dar_de_alta()
    {
        var c = await PrepararAsync();
        var (departamento, nombre) = await DepartamentoAsync(c);
        var codigo = Codigo();
        var producto = await ProductoAsync(c, $"Carpeta {Ficha()}", departamento, Variante("", [codigo], 3000));
        var (_, datos) = await ExportarAsync(c, c.Encargado, departamento);
        var nuevoNombre = $"{nombre} y más";
        await Leer<DepartamentosDto>(await c.Http.PutAsJsonAsync($"api/catalogo/departamentos/{departamento}/nombre",
            new SolicitudDepartamento(c.Encargado.UsuarioId, c.Sucursal, nuevoNombre)));

        var vista = await VerAsync(c, c.Encargado, datos);

        // Tal cual: el Excel trae el nombre de antes, que ya no es de nadie. No se da de alta otro con ese nombre: va aparte.
        Assert.Empty(vista.DepartamentosNuevos);
        var aparte = Assert.Single(vista.CambiaronDespues!);
        Assert.Equal((producto.Id, $"Departamento: {nuevoNombre} → {nombre}"), (aparte.ProductoId, Cambios(aparte)));
        Assert.Contains($"su departamento, «{nuevoNombre}» (cambió de nombre o se desactivó)", aparte.CambioDespues!);
        Assert.False(vista.HayQueAplicar);
    }

    [Fact]
    public async Task Un_excel_que_no_dice_de_cuando_es_pone_aparte_a_los_que_ya_existen()
    {
        var c = await PrepararAsync();
        var (departamento, nombre) = await DepartamentoAsync(c);
        var codigo = Codigo();
        var producto = await ProductoAsync(c, $"Agenda {Ficha()}", departamento, Variante("", [codigo], 9000));
        var (exportado, _) = await ExportarAsync(c, c.Encargado, departamento);
        // Copiado a otro libro (o de una versión de antes): sin sus propiedades escondidas.
        foreach (var propiedad in exportado.CustomProperties.Select(x => x.Name).ToList())
            exportado.CustomProperties.Delete(propiedad);
        Productos(exportado).Poner(codigo, "Precio", 95);
        var nuevo = Codigo();
        Productos(exportado).Agregar(("Código", nuevo), ("Producto", $"Agenda nueva {Ficha()}"), ("Departamento", nombre), ("Precio", 80));
        var datos = Bytes(exportado);

        var (vista, hecho) = await VerYAplicarAsync(c, c.Encargado, datos);

        // Lo nuevo sí; lo que ya existía, aparte: no se sabe si cambió.
        Assert.Equal((0, 1, 1), (vista.TotalCambian, vista.TotalNuevos, hecho.Nuevos));
        Assert.Equal([ProductosEnExcel.NoSeSabeSiCambio], Assert.Single(vista.CambiaronDespues!).CambioDespues);
        Assert.Contains(ProductosEnExcel.SinVersion, vista.Avisos);
        Assert.Equal(9000, await MenudeoAsync(c, producto.Id!.Value));
    }

    // ── «Se vende», de todo el producto ──────────────────────────────────────

    [Fact]
    public async Task Volver_a_vender_una_variante_no_vende_las_demas_que_dicen_que_no()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (azul, roja, negro, rojo) = (Codigo(), Codigo(), Codigo(), Codigo());
        // Productos que no se venden (los apagó la ficha), con sus variantes «activas».
        var pluma = await ProductoAsync(c, $"Pluma {ficha}", departamento, false, TipoProducto.Mercancia, Variante("Azul", [azul], 1000), Variante("Roja", [roja], 1000));
        var lapiz = await ProductoAsync(c, $"Lápiz {ficha}", departamento, false, TipoProducto.Mercancia, Variante("Negro", [negro], 500), Variante("Rojo", [rojo], 500));
        var (libro, _) = await ExportarAsync(c, c.Encargado, departamento, inactivos: true);
        var hoja = Productos(libro);
        Assert.All([azul, roja, negro, rojo], x => Assert.Equal("No", hoja.Renglon(x).Cell(hoja.Columna("Se vende")).GetString()));
        // Solo la azul y el negro se vuelven a vender; la roja dice «No», y el rojo ya no viene en el Excel.
        hoja.Poner(azul, "Se vende", "Sí");
        hoja.Poner(negro, "Se vende", "Sí");
        hoja.Renglon(rojo).Delete();

        var (vista, _) = await VerYAplicarAsync(c, c.Encargado, Bytes(libro));

        var deAzul = vista.Cambian.Single(x => x.VarianteId == pluma.Variantes[0].Id);
        Assert.Equal("Se vende: No → Sí · Sus otras variantes: no se venden → siguen sin venderse: Roja", Cambios(deAzul));
        Assert.Equal(PorQueSeRevisa.SeVende, deAzul.Cambios[0].Revisar);
        Assert.Equal("Se vende: No → Sí · Sus otras variantes: no se venden → siguen sin venderse: Rojo",
            Cambios(vista.Cambian.Single(x => x.VarianteId == lapiz.Variantes[0].Id)));
        Assert.Equal(2, vista.TotalCambian);
        var dePluma = await FichaAsync(c, pluma.Id!.Value);
        Assert.Equal((true, true, false), (dePluma.Activo, dePluma.Variantes.Single(v => v.Descripcion == "Azul").Activa,
            dePluma.Variantes.Single(v => v.Descripcion == "Roja").Activa));
        var deLapiz = await FichaAsync(c, lapiz.Id!.Value);
        Assert.Equal((true, true, false), (deLapiz.Activo, deLapiz.Variantes.Single(v => v.Descripcion == "Negro").Activa,
            deLapiz.Variantes.Single(v => v.Descripcion == "Rojo").Activa));
        // En la venta: la azul sí, la roja no.
        await servicio.CatalogoAlDiaAsync();
        Assert.True((await c.Http.GetAsync($"api/productos/codigo/{azul}?sucursalId={c.Sucursal}")).IsSuccessStatusCode);
        Assert.False((await c.Http.GetAsync($"api/productos/codigo/{roja}?sucursalId={c.Sucursal}")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Si_ninguna_variante_se_vende_el_producto_deja_de_venderse()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (azul, roja) = (Codigo(), Codigo());
        var pluma = await ProductoAsync(c, $"Pluma {ficha}", departamento, Variante("Azul", [azul], 1000), Variante("Roja", [roja], 1000));
        var (libro, _) = await ExportarAsync(c, c.Encargado, departamento);
        Productos(libro).Poner(azul, "Se vende", "No");
        Productos(libro).Poner(roja, "Se vende", "No");

        var (vista, _) = await VerYAplicarAsync(c, c.Encargado, Bytes(libro));

        Assert.All(vista.Cambian, x => Assert.Equal("Se vende: Sí → No", Cambios(x)));
        // El producto deja de venderse; sus variantes se quedan como estaban, para cuando se reactive en su ficha.
        var despues = await FichaAsync(c, pluma.Id!.Value);
        Assert.False(despues.Activo);
        Assert.All(despues.Variantes, v => Assert.True(v.Activa));

        // Y si luego solo la azul vuelve a venderse, la roja (que dice «No») sigue sin venderse.
        var (otra, _) = await ExportarAsync(c, c.Encargado, departamento, inactivos: true);
        Productos(otra).Poner(azul, "Se vende", "Sí");
        await VerYAplicarAsync(c, c.Encargado, Bytes(otra));
        despues = await FichaAsync(c, pluma.Id!.Value);
        Assert.Equal((true, true, false), (despues.Activo, despues.Variantes.Single(v => v.Descripcion == "Azul").Activa,
            despues.Variantes.Single(v => v.Descripcion == "Roja").Activa));
    }

    // ── Todas las listas completas; lo que conviene revisar, primero ──────────

    [Fact]
    public async Task Todo_lo_que_cambia_se_ve_y_lo_que_conviene_revisar_va_primero()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var (otroDepartamento, nombreOtro) = await DepartamentoAsync(c);
        const int Cuantos = 540;
        var ficha = Ficha();
        await using (var db = servicio.Base())
        {
            for (var i = 0; i < Cuantos; i++)
            {
                var producto = new Producto { Nombre = $"Muchos {ficha} {i:000}", DepartamentoId = departamento };
                var variante = new Variante { ProductoId = producto.Id };
                var pieza = new Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true };
                db.AddRange(producto, variante, pieza, new CodigoBarras { Codigo = $"M{ficha}{i:000}", PresentacionId = pieza.Id },
                    new Precio { VarianteId = variante.Id, PrecioCentavos = 1000 });
                if (i == 530)
                    db.Add(new CodigoBarras { Codigo = $"M{ficha}{i:000}-B", PresentacionId = pieza.Id });
            }
            await db.SaveChangesAsync();
        }
        var (libro, _) = await ExportarAsync(c, c.Encargado, departamento);
        var hoja = Productos(libro);
        for (var i = 0; i < Cuantos; i++)
            hoja.Poner($"M{ficha}{i:000}", "Precio", 11);
        // Entre todos, cuatro que conviene revisar: uno que baja, uno que deja de venderse, uno que se cambia de departamento y uno al
        // que se le quita un código.
        hoja.Poner($"M{ficha}510", "Precio", 9);
        hoja.Poner($"M{ficha}520", "Se vende", "No");
        hoja.Poner($"M{ficha}525", "Departamento", nombreOtro);
        hoja.Poner($"M{ficha}530", "Otros códigos", Blank.Value);

        var vista = await VerAsync(c, c.Encargado, Bytes(libro));

        // Todos, sin tope (la caja los enseña por páginas)…
        Assert.Equal((Cuantos, Cuantos), (vista.TotalCambian, vista.Cambian.Count));
        // …y primero los cuatro, en el orden del archivo, con lo que hay que ver resaltado.
        Assert.Equal([$"Muchos {ficha} 510", $"Muchos {ficha} 520", $"Muchos {ficha} 525", $"Muchos {ficha} 530"], vista.Cambian.Take(4).Select(x => x.Producto));
        Assert.All(vista.Cambian.Take(4), x => Assert.True(x.Revisar));
        Assert.All(vista.Cambian.Skip(4), x => Assert.False(x.Revisar));
        Assert.Equal([PorQueSeRevisa.BajaDePrecio, PorQueSeRevisa.SeVende, PorQueSeRevisa.Departamento, PorQueSeRevisa.QuitaCodigos],
            vista.Cambian.Take(4).Select(x => x.Cambios.First(y => y.Revisar is not null).Revisar!.Value));
        Assert.Equal($"Muchos {ficha} 000", vista.Cambian[4].Producto);
        _ = otroDepartamento;
    }

    // ── La base, apartada solo para guardar ──────────────────────────────────

    [Fact]
    public async Task Al_aplicar_la_base_se_aparta_solo_para_guardar_y_si_algo_cambio_mientras_no_se_aplica()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var (codigo, otro) = (Codigo(), Codigo());
        var producto = await ProductoAsync(c, $"Compás {Ficha()}", departamento, Variante("", [codigo], 1000));
        var ajeno = await ProductoAsync(c, $"Ajeno {Ficha()}", departamento, Variante("", [otro], 2000));
        var (libro, _) = await ExportarAsync(c, c.Encargado, departamento);
        Productos(libro).Poner(codigo, "Precio", 11);
        var datos = Bytes(libro);
        var vista = await VerAsync(c, c.Encargado, datos);
        var solicitud = Solicitud(c, c.Encargado, datos, vista.Huella, Guid.NewGuid());

        // Mientras se arma lo que se aplica, otra caja guarda algo (aquí, una venta haría lo mismo): la base no está apartada, así que
        // lo guarda enseguida (si lo estuviera, esperaría y fallaría con «database is locked»). Si es del catálogo, no se aplica.
        await using (var db = servicio.Base())
        {
            var registro = new RegistroProductosEnExcel(db)
            {
                DespuesDeLeer = async ct =>
                {
                    await using var otraCaja = servicio.Base();
                    await using var escritura = await otraCaja.Database.BeginTransactionAsync(ct);
                    (await otraCaja.Precios.SingleAsync(p => p.VarianteId == ajeno.Variantes[0].Id, ct)).PrecioCentavos = 2100;
                    await otraCaja.SaveChangesAsync(ct);
                    await escritura.CommitAsync(ct);
                },
            };
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registro.AplicarAsync(solicitud, () => ExcelDeProductos.Leer(datos, "Revisión.xlsx")));
            Assert.Equal(ProductosEnExcel.CambioDesdeLaVista, error.Message);
        }
        Assert.Equal(1000, await MenudeoAsync(c, producto.Id!.Value));

        // Algo que no es del catálogo (un renglón de la bitácora), guardado igual mientras se arma: se aplica. (Con la vista de ahora: el
        // otro producto cambió después de exportar, y ya va aparte.)
        solicitud = solicitud with { Huella = (await VerAsync(c, c.Encargado, datos)).Huella, Id = Guid.NewGuid() };
        await using (var db = servicio.Base())
        {
            var registro = new RegistroProductosEnExcel(db)
            {
                DespuesDeLeer = async ct =>
                {
                    await using var otraCaja = servicio.Base();
                    await using var escritura = await otraCaja.Database.BeginTransactionAsync(ct);
                    otraCaja.Bitacora.Add(new Bitacora
                    {
                        SucursalId = c.Sucursal, UsuarioId = c.Cajero.UsuarioId, Accion = AccionControlada.QuitarProducto, Detalle = "Quitó un producto de la venta",
                        Resultado = ResultadoBitacora.Hecho, FechaUtc = DateTime.UtcNow,
                    });
                    await otraCaja.SaveChangesAsync(ct);
                    await escritura.CommitAsync(ct);
                },
            };
            var hecho = await registro.AplicarAsync(solicitud, () => ExcelDeProductos.Leer(datos, "Revisión.xlsx"));
            Assert.Equal(1, hecho.Cambiaron);
        }
        Assert.Equal(1100, await MenudeoAsync(c, producto.Id!.Value));
    }

    [Fact]
    public async Task Dos_aplicar_a_la_vez_no_aplican_dos_veces()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var codigo = Codigo();
        var producto = await ProductoAsync(c, $"Doble {Ficha()}", departamento, Variante("", [codigo], 1000, [(12, 900)]));
        var (libro, _) = await ExportarAsync(c, c.Encargado, departamento);
        Productos(libro).Poner(codigo, "Precio", 11);
        var datos = Bytes(libro);
        var vista = await VerAsync(c, c.Encargado, datos);
        var id = Guid.NewGuid();

        // El mismo Id dos veces a la vez (se perdió la respuesta), y otro Id (otra caja con el mismo archivo).
        var respuestas = await Task.WhenAll(
            c.Http.PostAsJsonAsync("api/catalogo/importar", Solicitud(c, c.Encargado, datos, vista.Huella, id)),
            c.Http.PostAsJsonAsync("api/catalogo/importar", Solicitud(c, c.Encargado, datos, vista.Huella, id)),
            c.Http.PostAsJsonAsync("api/catalogo/importar", Solicitud(c, c.Encargado, datos, vista.Huella, Guid.NewGuid())));

        await using var db = servicio.Base();
        var varianteId = producto.Variantes[0].Id!.Value;
        Assert.Equal(1, await db.PreciosHistorial.CountAsync(h => h.VarianteId == varianteId && h.PrecioAnteriorCentavos == 1000 && h.PrecioNuevoCentavos == 1100));
        var aplicadas = new List<ImportacionAplicadaDto>();
        foreach (var r in respuestas.Where(r => r.IsSuccessStatusCode))
            aplicadas.Add((await r.Content.ReadFromJsonAsync<ImportacionAplicadaDto>())!);
        Assert.Single(aplicadas, x => !x.YaSeHabiaAplicado);
        Assert.All(respuestas.Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
    }

    // ── La bitácora, con lo completo ─────────────────────────────────────────

    [Fact]
    public async Task La_bitacora_guarda_lo_completo_de_lo_que_cambio_y_se_ve_en_excel()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (codigo, segundo, nuevo) = (Codigo(), $"INT-{ficha}", Codigo());
        await ProductoAsync(c, $"Mochila {ficha}", departamento, Variante("", [codigo, segundo], 45000));
        var (libro, _) = await ExportarAsync(c, c.Encargado, departamento);
        var hoja = Productos(libro);
        hoja.Poner(codigo, "Producto", $"Mochila escolar {ficha}");
        hoja.Poner(codigo, "Otros códigos", Blank.Value);
        hoja.Poner(codigo, "Precio", 420);
        hoja.Agregar(("Código", nuevo), ("Producto", $"Lonchera {ficha}"), ("Departamento", nombreDepartamento), ("Precio", 150));

        var (_, hecho) = await VerYAplicarAsync(c, c.Encargado, Bytes(libro));

        // En la bitácora: el renglón de siempre, que ahora trae lo completo.
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
        var bitacora = await Leer<List<BitacoraDto>>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/bitacora?usuarioId={c.Dueno.UsuarioId}"
            + $"&desde={hoy:yyyy-MM-dd}&hasta={hoy:yyyy-MM-dd}"));
        var renglon = bitacora.Single(b => b.Detalle == hecho.Bitacora);
        Assert.True(renglon.ConDetalle);

        var r = await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/bitacora/{renglon.Id}/excel?usuarioId={c.Dueno.UsuarioId}");
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.StartsWith("Importación de Excel ", r.Content.Headers.ContentDisposition?.FileNameStar ?? r.Content.Headers.ContentDisposition?.FileName);
        using var excel = new XLWorkbook(new MemoryStream(await r.Content.ReadAsByteArrayAsync()));
        var cambios = excel.Worksheet(ExcelDeImportacion.HojaCambios);
        var filas = cambios.RowsUsed().Select(x => string.Join(" | ", x.Cells(1, 6).Select(y => y.GetString()))).ToList();
        Assert.Contains($"Mochila {ficha} | {codigo} | {hoja.Renglon(codigo).RowNumber()} | Nombre | Mochila {ficha} | Mochila escolar {ficha}", filas);
        Assert.Contains(filas, x => x.Contains($"| Códigos | {codigo}, {segundo} | {codigo}", StringComparison.Ordinal));
        Assert.Contains(filas, x => x.Contains("| Precio | $450.00 | $420.00", StringComparison.Ordinal));
        var nuevos = excel.Worksheet(ExcelDeImportacion.HojaNuevos);
        Assert.Contains(nuevos.RowsUsed(), x => x.Cell(1).GetString() == nuevo && x.Cell(2).GetString() == $"Lonchera {ficha}" && x.Cell(4).GetDouble() == 150);

        // Quien no consulta la bitácora, no lo ve.
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/bitacora/{renglon.Id}/excel?usuarioId={c.Cajero.UsuarioId}")).StatusCode);
    }

    // ── El archivo, antes de abrirlo ─────────────────────────────────────────

    /// <summary>Un .xlsx mínimo con una hoja «Productos» de <paramref name="filas"/> renglones de <paramref name="columnas"/> celdas, y lo que se agregue.</summary>
    private static byte[] LibroGrande(int filas, int columnas, string? relleno = null)
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Escribir(string ruta, string texto)
            {
                using var w = new StreamWriter(zip.CreateEntry(ruta, CompressionLevel.SmallestSize).Open(), new UTF8Encoding(false));
                w.Write(texto);
            }
            Escribir("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Escribir("_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Escribir("xl/workbook.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Productos" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Escribir("xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            if (relleno is not null)
                Escribir("xl/relleno.xml", relleno);
            using var hoja = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize).Open(), new UTF8Encoding(false));
            hoja.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
            hoja.Write("""<row><c t="inlineStr"><is><t>Código</t></is></c><c t="inlineStr"><is><t>Precio</t></is></c></row>""");
            var renglon = $"<row>{string.Concat(Enumerable.Repeat("<c><v>1</v></c>", columnas))}</row>";
            for (var i = 0; i < filas; i++)
                hoja.Write(renglon);
            hoja.Write("</sheetData></worksheet>");
        }
        return memoria.ToArray();
    }

    /// <summary>Le cambia al zip lo que dice que pesa descomprimida esa entrada (como un archivo hecho para engañar).</summary>
    private static void Mentir(byte[] datos, string entrada, uint tamano)
    {
        var nombre = Encoding.ASCII.GetBytes(entrada);
        for (var i = 0; i < datos.Length - 46; i++)
        {
            if (datos[i] != 0x50 || datos[i + 1] != 0x4B)
                continue;
            if (datos[i + 2] == 0x03 && datos[i + 3] == 0x04 && datos.AsSpan(i + 30, BitConverter.ToUInt16(datos, i + 26)).SequenceEqual(nombre))
                BitConverter.GetBytes(tamano).CopyTo(datos, i + 22);
            else if (datos[i + 2] == 0x01 && datos[i + 3] == 0x02 && datos.AsSpan(i + 46, BitConverter.ToUInt16(datos, i + 28)).SequenceEqual(nombre))
                BitConverter.GetBytes(tamano).CopyTo(datos, i + 24);
        }
    }

    [Fact]
    public void El_archivo_se_revisa_antes_de_abrirlo_contando_lo_que_trae_de_verdad()
    {
        // Una hoja de 300,000 renglones (comprimida, casi no pesa): se cuentan sus renglones antes de abrirla, y no se abre.
        var muchos = LibroGrande(300_000, 4);
        Assert.StartsWith("«Muchos.xlsx» trae una hoja con más de 10,000 renglones: se importan hasta 10,000 a la vez.",
            Assert.Throws<ArgumentException>(() => ExcelDeProductos.Leer(muchos, "Muchos.xlsx")).Message);
        // La misma, diciendo que pesa 4 KB: al descomprimirla no cuadra, y tampoco se abre.
        var bomba = LibroGrande(300_000, 4);
        Mentir(bomba, "xl/worksheets/sheet1.xml", 4096);
        using (var zip = new ZipArchive(new MemoryStream(bomba)))
            Assert.Equal(4096, zip.GetEntry("xl/worksheets/sheet1.xml")!.Length);
        Assert.StartsWith("«Bomba.xlsx» no es un libro de Excel (.xlsx)", Assert.Throws<ArgumentException>(() => ExcelDeProductos.Leer(bomba, "Bomba.xlsx")).Message);

        // Pocos renglones con muchísimas celdas (cabe en 8 MB comprimido): tampoco.
        var celdas = LibroGrande(2_000, 600);
        Assert.True(celdas.Length < ProductosEnExcel.BytesMaximos);
        Assert.StartsWith("«Celdas.xlsx» trae demasiado adentro para ser el Excel de los productos (más de 1,000,000 celdas con datos).",
            Assert.Throws<ArgumentException>(() => ExcelDeProductos.Leer(celdas, "Celdas.xlsx")).Message);

        // Algo de más de 32 MB descomprimido, aunque sea otra parte del libro.
        var relleno = LibroGrande(10, 2, new string(' ', 33 * 1024 * 1024));
        Assert.StartsWith("«Relleno.xlsx» trae demasiado adentro para ser el Excel de los productos (más de 32 MB sin comprimir).",
            Assert.Throws<ArgumentException>(() => ExcelDeProductos.Leer(relleno, "Relleno.xlsx")).Message);

        // Uno chico, sí se lee.
        Assert.Equal(10, ExcelDeProductos.Leer(LibroGrande(10, 2), "Chico.xlsx").Productos.Renglones.Count);
    }

    [Fact]
    public void Importar_y_ver_que_cambia_no_reciben_mas_de_lo_que_ocupa_el_excel_mas_grande()
    {
        var puntos = servicio.Fabrica.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is "/api/catalogo/importar" or "/api/catalogo/importar/vista").ToList();
        Assert.Equal(2, puntos.Count);
        var base64 = (ProductosEnExcel.BytesMaximos + 2) / 3 * 4;
        Assert.All(puntos, e => Assert.InRange(e.Metadata.GetMetadata<IRequestSizeLimitMetadata>()!.MaxRequestBodySize!.Value, base64, base64 + 64 * 1024));
    }

    // ── Lo demás ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_precio_enorme_escrito_como_texto_se_ignora_con_su_porque()
    {
        Assert.Null(LecturaDeCeldas.Centavos(CeldaLeida.DeTexto("9999999999999999999999999999"), "Precio", out var rechazo));
        Assert.Equal(PorQueSeIgnora.NumeroInvalido, rechazo!.PorQue);
        Assert.Null(LecturaDeCeldas.Centavos(CeldaLeida.DeTexto("-9999999999999999999999999999"), "Precio", out rechazo));
        Assert.Equal("«Precio» dice «-9999999999999999999999999999»: el precio va mayor que cero.", rechazo!.Detalle);

        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var codigo = Codigo();
        await ProductoAsync(c, $"Enorme {Ficha()}", departamento, Variante("", [codigo], 1000));
        var (libro, _) = await ExportarAsync(c, c.Encargado, departamento);
        Productos(libro).Poner(codigo, "Precio", "9999999999999999999999999999");
        var vista = await VerAsync(c, c.Encargado, Bytes(libro));
        var ignorado = Assert.Single(vista.Ignorados);
        Assert.Equal((PorQueSeIgnora.NumeroInvalido, "«Precio» dice «9999999999999999999999999999»: revisa el precio (se aceptan hasta $100,000.00)."),
            (ignorado.PorQue, ignorado.Detalle));
    }

    [Fact]
    public async Task Las_presentaciones_de_un_cobro_de_terceros_no_se_cambian_desde_excel()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var codigo = Codigo();
        var recarga = await ProductoAsync(c, $"Recarga {ficha}", departamento, true, TipoProducto.CobroTerceros,
            Variante("", [codigo], 10000, paquetes: [("Paquete 2", 2, 19000, [])]));
        var (libro, _) = await ExportarAsync(c, c.Encargado, departamento);
        var presentaciones = Presentaciones(libro);
        var cambiada = presentaciones.Agregar(("Código del producto", codigo), ("Presentación", "Paquete 2"), ("Contiene", 2), ("Precio", 150));
        var nueva = presentaciones.Agregar(("Código del producto", codigo), ("Presentación", "Paquete 5"), ("Contiene", 5), ("Precio", 400));

        var vista = await VerAsync(c, c.Encargado, Bytes(libro));

        Assert.False(vista.HayQueAplicar);
        foreach (var renglon in new[] { cambiada, nueva })
        {
            var ignorado = Ignorado(vista, renglon, ProductosEnExcel.HojaPresentaciones);
            Assert.Equal((PorQueSeIgnora.NoSeCambiaDesdeExcel, $"«Recarga {ficha}» es un kit o un cobro de terceros: sus presentaciones se cambian en su ficha."),
                (ignorado.PorQue, ignorado.Detalle));
        }
        var despues = await FichaAsync(c, recarga.Id!.Value);
        Assert.Equal(19000, despues.Variantes[0].Precios.Single(p => p.Presentacion == "Paquete 2").PrecioCentavos);
        Assert.DoesNotContain(despues.Variantes[0].Presentaciones, p => p.Nombre == "Paquete 5");
    }

    [Fact]
    public async Task Los_codigos_que_solo_cambian_en_mayusculas_hacen_ida_y_vuelta()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var minusculas = await ProductoAsync(c, $"Minúsculas {ficha}", departamento, Variante("", [$"zz-{ficha}"], 1000));
        var mayusculas = await ProductoAsync(c, $"Mayúsculas {ficha}", departamento, Variante("", [$"ZZ-{ficha.ToUpperInvariant()}"], 1200));
        var (libro, datos) = await ExportarAsync(c, c.Encargado, departamento);

        var igual = await VerAsync(c, c.Encargado, datos);
        Assert.Equal((0, 0, 2), (igual.TotalIgnorados, igual.TotalCambian, igual.SinCambios));

        // Cada uno cambia el suyo; y dos códigos nuevos que solo cambian en mayúsculas sí chocan (al escanear son el mismo).
        var hoja = Productos(libro);
        hoja.Poner($"ZZ-{ficha.ToUpperInvariant()}", "Precio", 13);
        var nuevo1 = hoja.Agregar(("Código", $"yy-{ficha}"), ("Producto", $"Nuevo uno {ficha}"), ("Departamento", nombreDepartamento), ("Precio", 5));
        var nuevo2 = hoja.Agregar(("Código", $"YY-{ficha.ToUpperInvariant()}"), ("Producto", $"Nuevo dos {ficha}"), ("Departamento", nombreDepartamento), ("Precio", 5));
        var (vista, _) = await VerYAplicarAsync(c, c.Encargado, Bytes(libro));

        Assert.Equal((mayusculas.Id, "Precio: $12.00 → $13.00"), (Assert.Single(vista.Cambian).ProductoId, Cambios(vista.Cambian[0])));
        Assert.Equal((PorQueSeIgnora.CodigoRepetido, PorQueSeIgnora.CodigoRepetido), (Ignorado(vista, nuevo1).PorQue, Ignorado(vista, nuevo2).PorQue));
        Assert.Equal(1000, await MenudeoAsync(c, minusculas.Id!.Value));
        Assert.Equal(1300, await MenudeoAsync(c, mayusculas.Id!.Value));
    }

    [Fact]
    public async Task Una_caja_con_precio_propio_desde_dos_hace_ida_y_vuelta()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var codigo = Codigo();
        var producto = await ProductoAsync(c, $"Caja rara {Ficha()}", departamento, Variante("", [codigo], 1000, paquetes: [("Caja 12", 12, 11000, [])]));
        await using (var db = servicio.Base())
        {
            var varianteId = producto.Variantes[0].Id!.Value;
            (await db.Precios.SingleAsync(p => p.VarianteId == varianteId && p.PresentacionId != null)).CantidadMinima = Cantidad.Piezas(2);
            await db.SaveChangesAsync();
        }
        var (libro, datos) = await ExportarAsync(c, c.Encargado, departamento);

        // Tal cual: nada cambia (su precio es el de desde 2, y así se queda).
        var igual = await VerAsync(c, c.Encargado, datos);
        Assert.Equal((0, 0, 2), (igual.TotalCambian, igual.TotalIgnorados, igual.SinCambios));

        // Sin la hoja «Presentaciones», el menudeo cambia (la caja se queda con su precio desde 2).
        libro.Worksheet(ProductosEnExcel.HojaPresentaciones).Delete();
        Productos(libro).Poner(codigo, "Precio", 11);
        var (vista, _) = await VerYAplicarAsync(c, c.Encargado, Bytes(libro));
        Assert.Equal("Precio: $10.00 → $11.00", Cambios(Assert.Single(vista.Cambian)));
        Assert.Empty(vista.Ignorados);
        var precios = (await FichaAsync(c, producto.Id!.Value)).Variantes[0].Precios;
        Assert.Equal([(null, 1000L, 1100L), ("Caja 12", 2000L, 11000L)], precios.Select(p => (p.Presentacion, p.CantidadMinimaMilesimas, p.PrecioCentavos)));
    }

    [Fact]
    public async Task Un_producto_sin_precio_hace_ida_y_vuelta_y_se_le_cambia_lo_demas()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var codigo = Codigo();
        var producto = await ProductoAsync(c, $"Sin precio {ficha}", departamento, Variante("", [codigo], 1000));
        await using (var db = servicio.Base())
        {
            var varianteId = producto.Variantes[0].Id!.Value;
            db.Precios.RemoveRange(await db.Precios.Where(p => p.VarianteId == varianteId).ToListAsync());
            await db.SaveChangesAsync();
        }
        var (libro, datos) = await ExportarAsync(c, c.Encargado, departamento);
        var igual = await VerAsync(c, c.Encargado, datos);
        Assert.Equal((0, 0, 1), (igual.TotalIgnorados, igual.TotalCambian, igual.SinCambios));

        // Su marca sí cambia; el precio se queda sin él.
        Productos(libro).Poner(codigo, "Marca", $"Marca {ficha}");
        var (vista, _) = await VerYAplicarAsync(c, c.Encargado, Bytes(libro));
        Assert.Equal($"Marca: — → Marca {ficha}", Cambios(Assert.Single(vista.Cambian)));
        Assert.Null(await MenudeoAsync(c, producto.Id!.Value));

        // Con un precio, se le pone.
        var (otro, _) = await ExportarAsync(c, c.Encargado, departamento);
        Productos(otro).Poner(codigo, "Precio", 12);
        await VerYAplicarAsync(c, c.Encargado, Bytes(otro));
        Assert.Equal(1200, await MenudeoAsync(c, producto.Id!.Value));
    }

    [Fact]
    public async Task Un_codigo_nuevo_que_parece_otro_sin_sus_ceros_lo_avisa()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var ficha = Ficha();
        // Un código de 7 cifras que empieza con ceros (no es de 8, 12, 13 ni 14: esos se ignoran).
        var conCeros = $"00{Random.Shared.Next(10_000, 99_999)}";
        await ProductoAsync(c, $"Con ceros {ficha}", departamento, Variante("", [conCeros], 1000));
        var (libro, _) = await ExportarAsync(c, c.Encargado, departamento);
        Productos(libro).Agregar(("Código", double.Parse(conCeros, System.Globalization.CultureInfo.InvariantCulture)), ("Producto", $"Otro {ficha}"),
            ("Departamento", nombreDepartamento), ("Precio", 5));

        var vista = await VerAsync(c, c.Encargado, Bytes(libro));

        var nuevo = Assert.Single(vista.Nuevos);
        Assert.Equal($"Ya hay un producto con el código «{conCeros}» («Con ceros {ficha}»): si Excel le quitó los ceros de adelante, escríbelo como texto "
            + "(con un apóstrofo ' antes) y vuelve a importarlo.", nuevo.Aviso);
    }

    [Fact]
    public async Task Un_codigo_con_coma_no_va_en_otros_codigos_y_no_se_quita()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (codigo, conComa, soloConComa) = (Codigo(), $"X,{ficha}", $"Y;{ficha}");
        var producto = await ProductoAsync(c, $"Con coma {ficha}", departamento, Variante("", [codigo, conComa], 1000));
        var otro = await ProductoAsync(c, $"Solo con coma {ficha}", departamento, Variante("", [soloConComa], 2000));
        var (libro, datos) = await ExportarAsync(c, c.Encargado, departamento);
        var hoja = Productos(libro);

        // No va en «Otros códigos» (se partiría); el encabezado lo dice. El único, en «Código», sí va.
        Assert.True(hoja.Renglon(codigo).Cell(hoja.Columna("Otros códigos")).IsEmpty());
        Assert.Equal(soloConComa, hoja.Renglon(soloConComa).Cell(1).GetString());
        Assert.Contains(hoja.Excel.Column(1).CellsUsed(), x => x.GetString().StartsWith("Un código lleva coma o punto y coma", StringComparison.Ordinal));
        var igual = await VerAsync(c, c.Encargado, datos);
        Assert.Equal((0, 0, 2), (igual.TotalCambian, igual.TotalIgnorados, igual.SinCambios));

        // Cambiar el otro código no le quita el de la coma.
        hoja.Poner(codigo, "Otros códigos", $"INT-{ficha}");
        await VerYAplicarAsync(c, c.Encargado, Bytes(libro));
        Assert.Equal(new[] { codigo, conComa, $"INT-{ficha}" }.Order(),
            (await FichaAsync(c, producto.Id!.Value)).Variantes[0].Presentaciones[0].Codigos.Select(x => x.Codigo).Order());
        _ = otro;
    }

    [Fact]
    public async Task De_ida_y_vuelta_sin_cambios_con_cada_tipo_de_producto()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        // Granel por metro, con fracciones en el mínimo, el máximo y el mayoreo.
        var granel = new VarianteFichaDto(null, "", UnidadBase.Metro, true,
            [new PresentacionFichaDto(null, "Metro", 1000, true, [new CodigoFichaDto(Codigo())]), new PresentacionFichaDto(null, "Rollo 50 m", 50_000, false, [])],
            [new PrecioFichaDto(null, 1000, 450), new PrecioFichaDto(null, 5500, 420), new PrecioFichaDto(null, 20_250, 400), new PrecioFichaDto("Rollo 50 m", 1000, 19000)],
            MinimoYMaximo: new MinimoYMaximoDto(2500, 10_250));
        await ProductoAsync(c, $"Listón {ficha}", departamento, granel);
        // Varias variantes, una inactiva; tres escalas; caja con precio propio y mayoreo propio; sin código en la pieza y con la caja con código.
        await ProductoAsync(c, $"Pluma {ficha}", departamento,
            Variante("Azul", [Codigo()], 1200, [(12, 1100), (24, 1000), (48, 950)]), Variante("Roja", [Codigo()], 1200, activa: false));
        var caja = Variante("", [Codigo(), $"INT-{ficha}"], 600, [(12, 550)], [("Caja 12", 12, 6000, [Codigo()])]);
        caja = caja with { Precios = [.. caja.Precios, new PrecioFichaDto("Caja 12", 3000, 5800)] };
        await ProductoAsync(c, $"Lápiz {ficha}", departamento, caja);
        await ProductoAsync(c, $"Crayón {ficha}", departamento, Variante("", [], 600, paquetes: [("Caja 12", 12, 6000, [Codigo()])]));
        // Inactivo; servicio sin código; un kit (no se exporta).
        await ProductoAsync(c, $"Inactivo {ficha}", departamento, false, TipoProducto.Mercancia, Variante("", [Codigo()], 900));
        await ProductoAsync(c, $"Engargolado {ficha}", departamento, true, TipoProducto.Servicio, Variante("", [], 2500));
        await ProductoAsync(c, $"Kit {ficha}", departamento, true, TipoProducto.Kit, Variante("", [Codigo()], 5000));

        var (libro, datos) = await ExportarAsync(c, c.Encargado, departamento, inactivos: true);
        var vista = await VerAsync(c, c.Encargado, datos);

        Assert.Equal((0, 0, 0), (vista.TotalCambian, vista.TotalNuevos, vista.TotalIgnorados));
        Assert.Empty(vista.CambiaronDespues!);
        Assert.Equal(Productos(libro).Renglones + Presentaciones(libro).Renglones, vista.SinCambios);
    }
}
