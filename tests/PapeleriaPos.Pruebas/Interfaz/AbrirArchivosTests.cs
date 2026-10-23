using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Abrir un archivo desde la caja (2026-10-22, para importar el Excel de los productos), junto a guardar uno: en la ventana de
/// Windows, «Abrir» (aquí, uno de mentiras que contesta lo que se le diga), donde se guardó o se abrió el último; el archivo se lee
/// aunque Excel lo tenga abierto; lo que pesa demasiado o ya no está se dice en palabras de la tienda. En el navegador, lo que da su
/// selector de archivos. Y los de mentiras de las pruebas de las pantallas.
/// </summary>
public sealed class AbrirArchivosTests : IDisposable
{
    private readonly string _carpeta = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pos-abrir-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_carpeta, recursive: true);

    private static readonly ArchivoPorAbrir ExcelDeProductos = new("Elegir el Excel de los productos", ".xlsx", 100);

    /// <summary>«Abrir» de mentiras: anota lo que preguntó y contesta lo que se le diga.</summary>
    private sealed class Abrir(Func<PreguntaAbrir, string?> contestar)
    {
        public List<PreguntaAbrir> Preguntas { get; } = [];

        public Task<string?> PreguntarAsync(PreguntaAbrir pregunta)
        {
            Preguntas.Add(pregunta);
            return Task.FromResult(contestar(pregunta));
        }
    }

    [Fact]
    public async Task En_la_pc_empieza_donde_se_guardo_el_ultimo_y_lo_lee_aunque_excel_lo_tenga_abierto()
    {
        var otra = Directory.CreateDirectory(Path.Combine(_carpeta, "Memoria USB")).FullName;
        var carpeta = new CarpetaDeArchivos(_carpeta);
        var guardado = new GuardadoEnLaPc(p => Task.FromResult<string?>(Path.Combine(otra, p.Nombre)), carpeta);
        // Se exporta a la memoria: «Abrir» empieza ahí, con el filtro de Excel.
        await guardado.GuardarAsync(new ArchivoPorGuardar("Productos 2026-10-22.xlsx", "application/x", [1, 2, 3]));
        var ruta = Path.Combine(otra, "Productos 2026-10-22.xlsx");
        var dialogo = new Abrir(_ => ruta);
        var abierto = new AbiertoEnLaPc(dialogo.PreguntarAsync, carpeta);

        // Excel lo tiene abierto (deja leerlo, no escribirlo): se lee igual.
        using (new FileStream(ruta, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var archivo = await abierto.AbrirAsync(ExcelDeProductos);
            Assert.Equal(new PreguntaAbrir(otra, "Elegir el Excel de los productos", "Libro de Excel (*.xlsx)|*.xlsx"), dialogo.Preguntas.Single());
            Assert.Equal(("Productos 2026-10-22.xlsx", ruta), (archivo!.Nombre, archivo.Ruta));
            Assert.Equal([1, 2, 3], archivo.Datos);
        }

        // Al abrir uno de otra carpeta, «Guardar como» empieza ahí.
        var deDocumentos = Path.Combine(_carpeta, "Cambios.xlsx");
        await File.WriteAllBytesAsync(deDocumentos, [7]);
        await new AbiertoEnLaPc(_ => Task.FromResult<string?>(deDocumentos), carpeta).AbrirAsync(ExcelDeProductos);
        Assert.Equal(_carpeta, guardado.Carpeta);
    }

    [Fact]
    public async Task Si_se_cancela_no_lee_nada_y_lo_que_no_se_puede_lo_dice()
    {
        var carpeta = new CarpetaDeArchivos(_carpeta);
        Assert.Null(await new AbiertoEnLaPc(_ => Task.FromResult<string?>(null), carpeta).AbrirAsync(ExcelDeProductos));

        // Pesa más de lo que se importa: no se lee.
        var grande = Path.Combine(_carpeta, "Grande.xlsx");
        await File.WriteAllBytesAsync(grande, new byte[1536 * 1024]);
        var error = await Assert.ThrowsAsync<ErrorCaja>(() => new AbiertoEnLaPc(_ => Task.FromResult<string?>(grande), carpeta)
            .AbrirAsync(ExcelDeProductos with { BytesMaximos = 1024 * 1024 }));
        Assert.Equal("«Grande.xlsx» pesa 1.5 MB: se importan archivos de hasta 1 MB. Pártelo en dos, o exporta un departamento a la vez.", error.Message);
        // Ya no está (se quitó la memoria).
        var noEsta = Path.Combine(_carpeta, "Se quitó", "Productos.xlsx");
        error = await Assert.ThrowsAsync<ErrorCaja>(() => new AbiertoEnLaPc(_ => Task.FromResult<string?>(noEsta), carpeta).AbrirAsync(ExcelDeProductos));
        Assert.StartsWith($"No se pudo leer {noEsta}: si se quitó la memoria USB o se movió, búscalo otra vez.", error.Message);
        // Sin carpeta de inicio, Documentos.
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), new CarpetaDeArchivos().Ruta);
    }

    [Fact]
    public async Task En_el_navegador_se_lee_lo_que_da_su_selector_hasta_lo_que_puede_pesar()
    {
        var archivo = await ArchivoDelNavegador.LeerAsync(new ArchivoDelNavegadorDePrueba("Productos.xlsx", [4, 5]), 100);
        Assert.Equal(("Productos.xlsx", null), (archivo.Nombre, archivo.Ruta));
        Assert.Equal([4, 5], archivo.Datos);
        var error = await Assert.ThrowsAsync<ErrorCaja>(() => ArchivoDelNavegador.LeerAsync(new ArchivoDelNavegadorDePrueba("Enorme.xlsx", new byte[3 * 1024 * 1024]),
            2 * 1024 * 1024));
        Assert.Equal("«Enorme.xlsx» pesa 3 MB: se importan archivos de hasta 2 MB. Pártelo en dos, o exporta un departamento a la vez.", error.Message);
    }

    [Fact]
    public async Task El_de_mentiras_de_las_pantallas_contesta_lo_que_se_le_da_o_cancela()
    {
        var abierto = new AbiertoDePrueba { Elegido = new ArchivoAbierto("Productos.xlsx", [1]) };
        Assert.Equal("Productos.xlsx", (await abierto.AbrirAsync(ExcelDeProductos))!.Nombre);
        abierto.Elegido = null;
        Assert.Null(await abierto.AbrirAsync(ExcelDeProductos));
        Assert.Equal(2, abierto.Pedidos.Count);
    }
}
