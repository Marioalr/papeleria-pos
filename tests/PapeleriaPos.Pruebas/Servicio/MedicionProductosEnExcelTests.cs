using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Servicio.Excel;
using Xunit.Abstractions;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Solo corre con la variable <c>MEDIR_PRODUCTOS_EXCEL</c>: mide, no prueba (y tarda).</summary>
public sealed class FactDeMedicionDeProductosEnExcelAttribute : FactAttribute
{
    public FactDeMedicionDeProductosEnExcelAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEDIR_PRODUCTOS_EXCEL")))
            Skip = "Solo mide con la variable MEDIR_PRODUCTOS_EXCEL.";
    }
}

/// <summary>
/// Cuánto tarda «Productos en Excel» con un catálogo grande: la papelería ficticia con productos de más hasta llegar a 10,000
/// variantes (el tope de la importación; otra cantidad con <c>MEDIR_PRODUCTOS_EXCEL_VARIANTES</c>), exportada completa e importada
/// con todos sus precios cambiados, en esta PC. Para el plan; no revisa nada.
/// </summary>
public sealed class MedicionProductosEnExcelTests(ITestOutputHelper salida)
{
    private static int Variantes => int.TryParse(Environment.GetEnvironmentVariable("MEDIR_PRODUCTOS_EXCEL_VARIANTES"), out var n) ? n : ProductosEnExcel.MaximoRenglones;

    /// <summary>La papelería ficticia con productos de más, hasta llegar a <see cref="Variantes"/> que se venden.</summary>
    private static async Task LlenarAsync(ServicioDePrueba servicio)
    {
        await using var db = servicio.Base();
        var variantes = await db.Variantes.CountAsync(v => v.Activa && v.Producto!.Activo);
        var departamento = new Departamento { Nombre = "Medición" };
        db.Departamentos.Add(departamento);
        for (var i = variantes; i < Variantes; i++)
        {
            var producto = new Producto { Nombre = $"Producto de medición {i:00000}", DepartamentoId = departamento.Id };
            var variante = new Variante { ProductoId = producto.Id };
            var pieza = new Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true };
            db.AddRange(producto, variante, pieza, new CodigoBarras { Codigo = $"77{i:00000000000}", PresentacionId = pieza.Id },
                new Precio { VarianteId = variante.Id, PrecioCentavos = 1000 + i % 500 });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>El Excel de todo el catálogo, como lo exporta el dueño, con todos los precios 10 % más arriba.</summary>
    private static async Task<(byte[] Datos, int Renglones, byte[] Exportado)> ExcelConTodoCambiadoAsync(HttpClient http, CajaDto caja, SesionDto dueno)
    {
        var respuesta = await http.GetAsync($"api/catalogo/lista/excel?sucursalId={caja.SucursalId}&usuarioId={dueno.UsuarioId}&filtro=Todos&inactivos=false");
        var datos = await respuesta.Content.ReadAsByteArrayAsync();
        using var libro = new XLWorkbook(new MemoryStream(datos));
        var hoja = libro.Worksheet(ProductosEnExcel.HojaProductos);
        var titulos = hoja.RowsUsed().First(r => r.Cell(1).GetString() == "Código");
        var precio = titulos.CellsUsed().First(c => c.GetString() == "Precio").Address.ColumnNumber;
        var filas = hoja.RowsUsed().Where(r => r.RowNumber() > titulos.RowNumber() && r.Cell(precio).Value.IsNumber).ToList();
        foreach (var fila in filas)
            fila.Cell(precio).Value = Math.Round(fila.Cell(precio).GetDouble() * 1.1 + 0.5);
        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        return (flujo.ToArray(), filas.Count, datos);
    }

    /// <summary>
    /// Cuánto queda apartada la base para escribir al aplicar, y cuánto espera mientras otra caja que quiere guardar (una venta),
    /// con todos los precios cambiados: antes se armaba todo con la base apartada (de 1.3 a 2.1 s con 6,000 a 10,000 variantes,
    /// y la venta de otra caja se va a su bandeja si espera más de 2.5 s).
    /// </summary>
    [FactDeMedicionDeProductosEnExcel]
    public async Task Cuanto_queda_apartada_la_base_al_aplicar()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        await LlenarAsync(servicio);
        var (datos, renglones, _) = await ExcelConTodoCambiadoAsync(http, caja, dueno);
        var solicitud = new SolicitudImportarProductos(dueno.UsuarioId, caja.SucursalId, "Medición.xlsx", Convert.ToBase64String(datos));
        var vista = (await (await http.PostAsJsonAsync("api/catalogo/importar/vista", solicitud)).Content.ReadFromJsonAsync<VistaImportacionDto>())!;

        // Otra caja guarda algo cada 50 ms mientras se aplica (una venta toma la base igual), y anota cuánto esperó cada vez.
        var esperas = new ConcurrentBag<double>();
        using var alto = new CancellationTokenSource();
        var otraCaja = Task.Run(async () =>
        {
            while (!alto.IsCancellationRequested)
            {
                await using var otra = servicio.Base();
                var inicio = Stopwatch.GetTimestamp();
                await using (var escritura = await otra.Database.BeginTransactionAsync())
                    await escritura.RollbackAsync();
                esperas.Add(Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
                await Task.Delay(50);
            }
        });
        var reloj = Stopwatch.StartNew();
        long armado = 0;
        ImportacionAplicadaDto hecho;
        await using (var db = servicio.Base())
        {
            var registro = new RegistroProductosEnExcel(db) { DespuesDeLeer = _ => { armado = reloj.ElapsedMilliseconds; return Task.CompletedTask; } };
            hecho = await registro.AplicarAsync(solicitud with { Huella = vista.Huella, Id = Guid.NewGuid() }, () => ExcelDeProductos.Leer(datos, "Medición.xlsx"));
        }
        var total = reloj.ElapsedMilliseconds;
        await alto.CancelAsync();
        await otraCaja;

        salida.WriteLine($"Aplicar {renglones:#,##0} renglones ({hecho.Cambiaron:#,##0} cambian): {total:#,##0} ms; armar sin apartar la base {armado:#,##0} ms; "
            + $"con la base apartada {total - armado:#,##0} ms.");
        salida.WriteLine($"Otra caja guardó {esperas.Count} veces mientras; la que más esperó, {esperas.DefaultIfEmpty(0).Max():#,##0} ms.");
    }

    /// <summary>Lo más que se deja abrir (casi 1,000,000 de celdas en una hoja): cuánto tarda y cuánta memoria toma leerlo.</summary>
    [FactDeMedicionDeProductosEnExcel]
    public void Lo_mas_grande_que_se_deja_abrir()
    {
        const int Filas = 10_040, Columnas = 98;
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
            using var hoja = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize).Open(), new UTF8Encoding(false));
            hoja.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
            hoja.Write("""<row><c t="inlineStr"><is><t>Código</t></is></c><c t="inlineStr"><is><t>Precio</t></is></c></row>""");
            var renglon = $"<row>{string.Concat(Enumerable.Repeat("<c><v>1</v></c>", Columnas))}</row>";
            for (var i = 0; i < Filas; i++)
                hoja.Write(renglon);
            hoja.Write("</sheetData></worksheet>");
        }
        var datos = memoria.ToArray();
        GC.Collect();
        var antes = GC.GetTotalMemory(true);
        var proceso = Process.GetCurrentProcess();
        var reloj = Stopwatch.StartNew();
        long maximo = 0;
        string resultado;
        using (new Timer(_ => { var m = GC.GetTotalMemory(false); if (m > Interlocked.Read(ref maximo)) Interlocked.Exchange(ref maximo, m); }, null, 0, 20))
        {
            try
            {
                resultado = $"leyó {ExcelDeProductos.Leer(datos, "Grande.xlsx").Productos.Renglones.Count:#,##0} renglones";
            }
            catch (ArgumentException ex)
            {
                resultado = ex.Message;
            }
        }
        proceso.Refresh();
        salida.WriteLine($"{Filas:#,##0} × {Columnas} celdas, {datos.Length / 1024:#,##0} KB: {reloj.ElapsedMilliseconds:#,##0} ms; memoria administrada máx. ≈ "
            + $"{(Interlocked.Read(ref maximo) - antes) / 1024 / 1024:#,##0} MB; pico del proceso {proceso.PeakWorkingSet64 / 1024 / 1024:#,##0} MB; {resultado}.");
    }

    [FactDeMedicionDeProductosEnExcel]
    public async Task Diez_mil_productos_de_ida_y_vuelta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        await LlenarAsync(servicio);

        var reloj = Stopwatch.StartNew();
        var (cambiado, renglones, datos) = await ExcelConTodoCambiadoAsync(http, caja, dueno);
        var exportar = reloj.ElapsedMilliseconds;
        long descomprimido;
        using (var zip = new ZipArchive(new MemoryStream(cambiado)))
            descomprimido = zip.Entries.Sum(e => e.Length);
        var solicitud = new SolicitudImportarProductos(dueno.UsuarioId, caja.SucursalId, "Medición.xlsx", Convert.ToBase64String(cambiado));
        reloj.Restart();
        var vista = (await (await http.PostAsJsonAsync("api/catalogo/importar/vista", solicitud)).Content.ReadFromJsonAsync<VistaImportacionDto>())!;
        var ver = reloj.ElapsedMilliseconds;
        reloj.Restart();
        var aplicada = await http.PostAsJsonAsync("api/catalogo/importar", solicitud with { Huella = vista.Huella, Id = Guid.NewGuid() });
        var aplicar = reloj.ElapsedMilliseconds;

        salida.WriteLine($"Exportar {renglones:#,##0} renglones (y cambiarlos): {exportar:#,##0} ms, {datos.Length / 1024:#,##0} KB; cambiado: "
            + $"{cambiado.Length / 1024:#,##0} KB, descomprimido {descomprimido / 1024:#,##0} KB.");
        salida.WriteLine($"Ver: {ver:#,##0} ms (cambian {vista.TotalCambian:#,##0}, se ignoran {vista.TotalIgnorados:#,##0}, sin cambios {vista.SinCambios:#,##0}).");
        salida.WriteLine($"Aplicar: {aplicar:#,##0} ms ({(int)aplicada.StatusCode}).");
        foreach (var grupo in vista.Ignorados.GroupBy(x => x.PorQue))
            salida.WriteLine($"Ver: se ignoran {grupo.Count()} por {grupo.Key}, como «{grupo.First().Producto}»: {grupo.First().Detalle}");
    }
}
