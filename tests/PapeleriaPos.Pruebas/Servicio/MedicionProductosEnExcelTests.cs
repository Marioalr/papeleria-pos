using System.Diagnostics;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
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
/// variantes (el tope de la importación), exportada completa e importada con todos sus precios cambiados, por la API, en esta PC.
/// Para el plan; no revisa nada.
/// </summary>
public sealed class MedicionProductosEnExcelTests(ITestOutputHelper salida)
{
    [FactDeMedicionDeProductosEnExcel]
    public async Task Diez_mil_productos_de_ida_y_vuelta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        await using (var db = servicio.Base())
        {
            var variantes = await db.Variantes.CountAsync(v => v.Activa && v.Producto!.Activo);
            var departamento = new Departamento { Nombre = "Medición" };
            db.Departamentos.Add(departamento);
            for (var i = variantes; i < ProductosEnExcel.MaximoRenglones; i++)
            {
                var producto = new Producto { Nombre = $"Producto de medición {i:00000}", DepartamentoId = departamento.Id };
                var variante = new Variante { ProductoId = producto.Id };
                var pieza = new Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true };
                db.AddRange(producto, variante, pieza, new CodigoBarras { Codigo = $"77{i:00000000000}", PresentacionId = pieza.Id },
                    new Precio { VarianteId = variante.Id, PrecioCentavos = 1000 + i % 500 });
            }
            await db.SaveChangesAsync();
        }

        var reloj = Stopwatch.StartNew();
        var respuesta = await http.GetAsync($"api/catalogo/lista/excel?sucursalId={caja.SucursalId}&usuarioId={dueno.UsuarioId}&filtro=Todos&inactivos=false");
        var datos = await respuesta.Content.ReadAsByteArrayAsync();
        var exportar = reloj.ElapsedMilliseconds;

        byte[] cambiado;
        int renglones;
        using (var libro = new XLWorkbook(new MemoryStream(datos)))
        {
            var hoja = libro.Worksheet(ProductosEnExcel.HojaProductos);
            var titulos = hoja.RowsUsed().First(r => r.Cell(1).GetString() == "Código");
            var precio = titulos.CellsUsed().First(c => c.GetString() == "Precio").Address.ColumnNumber;
            var filas = hoja.RowsUsed().Where(r => r.RowNumber() > titulos.RowNumber() && r.Cell(precio).Value.IsNumber).ToList();
            renglones = filas.Count;
            foreach (var fila in filas)
                fila.Cell(precio).Value = Math.Round(fila.Cell(precio).GetDouble() * 1.1 + 0.5);
            using var flujo = new MemoryStream();
            libro.SaveAs(flujo);
            cambiado = flujo.ToArray();
        }
        var solicitud = new SolicitudImportarProductos(dueno.UsuarioId, caja.SucursalId, "Medición.xlsx", Convert.ToBase64String(cambiado));
        reloj.Restart();
        var vista = (await (await http.PostAsJsonAsync("api/catalogo/importar/vista", solicitud)).Content.ReadFromJsonAsync<VistaImportacionDto>())!;
        var ver = reloj.ElapsedMilliseconds;
        reloj.Restart();
        var aplicada = await http.PostAsJsonAsync("api/catalogo/importar", solicitud with { Huella = vista.Huella, Id = Guid.NewGuid() });
        var aplicar = reloj.ElapsedMilliseconds;

        salida.WriteLine($"Exportar {renglones:#,##0} renglones: {exportar:#,##0} ms, {datos.Length / 1024:#,##0} KB; cambiado: {cambiado.Length / 1024:#,##0} KB.");
        salida.WriteLine($"Ver: {ver:#,##0} ms (cambian {vista.TotalCambian:#,##0}, se ignoran {vista.TotalIgnorados:#,##0}, sin cambios {vista.SinCambios:#,##0}).");
        salida.WriteLine($"Aplicar: {aplicar:#,##0} ms ({(int)aplicada.StatusCode}).");
    }
}
