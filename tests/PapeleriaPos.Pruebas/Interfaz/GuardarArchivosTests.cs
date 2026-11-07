using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La única forma de guardar un archivo desde la caja (entrega 9, parte 2): en la ventana de Windows, «Guardar como» (aquí, uno de
/// mentiras que contesta lo que se le diga) y el archivo escrito sin quedar a medias; en el navegador, una descarga con caja.js; y
/// la de mentiras de las pruebas de las pantallas.
/// </summary>
public sealed class GuardarArchivosTests : IDisposable
{
    private readonly string _carpeta = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pos-guardar-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_carpeta, recursive: true);

    private static readonly ArchivoPorGuardar Excel = new("Ventas 2026-10.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [1, 2, 3, 4]);

    /// <summary>«Guardar como» de mentiras: anota lo que preguntó y contesta lo que se le diga.</summary>
    private sealed class GuardarComo(Func<PreguntaGuardarComo, string?> contestar)
    {
        public List<PreguntaGuardarComo> Preguntas { get; } = [];

        public Task<string?> PreguntarAsync(PreguntaGuardarComo pregunta)
        {
            Preguntas.Add(pregunta);
            return Task.FromResult(contestar(pregunta));
        }
    }

    [Fact]
    public async Task En_la_pc_pregunta_en_documentos_con_el_nombre_que_propone_y_escribe_ahi()
    {
        var otra = Directory.CreateDirectory(Path.Combine(_carpeta, "Memoria USB")).FullName;
        var dialogo = new GuardarComo(p => Path.Combine(p.Carpeta == otra ? otra : _carpeta, p.Nombre));
        var guardado = new GuardadoEnLaPc(dialogo.PreguntarAsync, _carpeta);

        var donde = await guardado.GuardarAsync(Excel);

        // La primera vez, en la carpeta de inicio (en la caja, Documentos), con el nombre de la caja principal y como Excel.
        Assert.Equal(new PreguntaGuardarComo(_carpeta, "Ventas 2026-10.xlsx", "Libro de Excel (*.xlsx)|*.xlsx", ".xlsx"), dialogo.Preguntas.Single());
        var ruta = Path.Combine(_carpeta, "Ventas 2026-10.xlsx");
        Assert.Equal(new ArchivoGuardado("Ventas 2026-10.xlsx", ruta), donde);
        Assert.Equal($"Se guardó en {ruta}.", donde!.Texto);
        Assert.Equal(Excel.Datos, await File.ReadAllBytesAsync(ruta));
        // Sin el archivo de paso.
        Assert.Equal([ruta], Directory.GetFiles(_carpeta));

        // Si ya existe, «Guardar como» pregunta si se reemplaza (en Windows); aquí se reemplaza completo.
        await guardado.GuardarAsync(Excel with { Datos = [9, 9] });
        Assert.Equal([9, 9], await File.ReadAllBytesAsync(ruta));

        // La siguiente vez empieza donde se guardó la última.
        var enOtra = new GuardadoEnLaPc(new GuardarComo(p => Path.Combine(otra, p.Nombre)).PreguntarAsync, _carpeta);
        await enOtra.GuardarAsync(Excel);
        Assert.Equal(otra, enOtra.Carpeta);
        // Sin carpeta de inicio, Documentos.
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), new GuardadoEnLaPc(_ => Task.FromResult<string?>(null)).Carpeta);
    }

    [Fact]
    public async Task Si_se_cancela_no_escribe_nada_y_si_no_se_puede_lo_dice_sin_dejar_nada_a_medias()
    {
        var cancelado = await new GuardadoEnLaPc(_ => Task.FromResult<string?>(null), _carpeta).GuardarAsync(Excel);
        Assert.Null(cancelado);
        Assert.Empty(Directory.GetFileSystemEntries(_carpeta));
        Assert.Equal("No se guardó: se canceló.", ArchivoGuardado.Cancelado);

        // Una carpeta que ya no existe (una memoria que se quitó): lo dice, en palabras de la tienda; es la carpeta, no Excel.
        var noExiste = Path.Combine(_carpeta, "Se quitó", "Ventas.xlsx");
        var error = await Assert.ThrowsAsync<ErrorCaja>(() => new GuardadoEnLaPc(_ => Task.FromResult<string?>(noExiste), _carpeta).GuardarAsync(Excel));
        Assert.Equal($"No se pudo guardar en {noExiste}: no se pudo escribir en esa carpeta (¿se quitó la memoria, ya no existe o no tiene espacio?). Elige otra, "
            + "como Documentos.", error.Message);
        Assert.DoesNotContain("Excel", error.Message);

        // En lugar del archivo hay una carpeta con ese nombre: no se puede reemplazar, lo dice, y no queda el archivo de paso.
        var ocupado = Directory.CreateDirectory(Path.Combine(_carpeta, "Ocupado.xlsx")).FullName;
        var conCarpeta = await Assert.ThrowsAsync<ErrorCaja>(() => new GuardadoEnLaPc(_ => Task.FromResult<string?>(ocupado), _carpeta).GuardarAsync(Excel));
        Assert.Equal($"No se pudo guardar en {ocupado}: ya hay una carpeta con ese nombre. Guárdalo con otro nombre.", conCarpeta.Message);
        Assert.Equal([ocupado], Directory.GetFileSystemEntries(_carpeta));
    }

    /// <summary>
    /// Lo que dice depende del paso que falló (revisión del PR #134): Windows no deja reemplazar un archivo abierto en Excel
    /// (UnauthorizedAccessException al cambiar el de paso por él), y eso no es que la carpeta no deje escribir.
    /// </summary>
    [Fact]
    public async Task Si_excel_lo_tiene_abierto_lo_dice_y_el_de_antes_se_queda()
    {
        var ruta = Path.Combine(_carpeta, "Ventas 2026-10.xlsx");
        await File.WriteAllBytesAsync(ruta, [7]);
        var guardado = new GuardadoEnLaPc(_ => Task.FromResult<string?>(ruta), _carpeta)
        {
            Mover = (_, _) => throw new UnauthorizedAccessException("Access to the path is denied."),
        };

        var error = await Assert.ThrowsAsync<ErrorCaja>(() => guardado.GuardarAsync(Excel));

        Assert.Equal($"No se pudo guardar en {ruta}: otro programa lo tiene abierto (como Excel). Ciérralo o guárdalo con otro nombre.", error.Message);
        Assert.Equal([7], await File.ReadAllBytesAsync(ruta));
        Assert.Equal([ruta], Directory.GetFiles(_carpeta));
        // La carpeta de la siguiente vez no cambia: no se guardó.
        Assert.Equal(_carpeta, guardado.Carpeta);
    }

    /// <summary>
    /// En pantalla, qué hacer; el motivo de Windows (sin espacio, una ruta demasiado larga) va al registro, para el que lo revise: si no, no quedaba
    /// en ningún lado (revisión de la auditoría, 2026-11-06).
    /// </summary>
    [Fact]
    public async Task El_motivo_de_windows_va_al_registro_y_no_a_la_pantalla()
    {
        var registro = new RegistroDiario(Path.Combine(_carpeta, "registros"), "caja");
        var carpeta = new CarpetaDeArchivos(_carpeta);

        var noExiste = Path.Combine(_carpeta, "Se quitó", "Ventas.xlsx");
        var alEscribir = await Assert.ThrowsAsync<ErrorCaja>(() => new GuardadoEnLaPc(_ => Task.FromResult<string?>(noExiste), carpeta, registro).GuardarAsync(Excel));
        Assert.Contains("No se guardó el archivo", File.ReadAllText(registro.ArchivoDeHoy));
        Assert.Contains(nameof(DirectoryNotFoundException), File.ReadAllText(registro.ArchivoDeHoy));
        Assert.DoesNotContain(nameof(DirectoryNotFoundException), alEscribir.Message);

        var ruta = Path.Combine(_carpeta, "Ventas.xlsx");
        var alCambiar = await Assert.ThrowsAsync<ErrorCaja>(() => new GuardadoEnLaPc(_ => Task.FromResult<string?>(ruta), carpeta, registro)
        {
            Mover = (_, _) => throw new IOException("There is not enough space on the disk."),
        }.GuardarAsync(Excel));
        Assert.Equal($"No se pudo guardar en {ruta}: no se pudo escribir en esa carpeta (¿se quitó la memoria o no tiene espacio?). Elige otra, como Documentos.", alCambiar.Message);
        Assert.Contains("There is not enough space on the disk.", File.ReadAllText(registro.ArchivoDeHoy));

        var noEsta = Path.Combine(_carpeta, "Se quitó", "Productos.xlsx");
        var alLeer = await Assert.ThrowsAsync<ErrorCaja>(() => new AbiertoEnLaPc(_ => Task.FromResult<string?>(noEsta), carpeta, registro)
            .AbrirAsync(new ArchivoPorAbrir("Abrir", ".xlsx", 1024)));
        Assert.Contains("No se leyó el archivo", File.ReadAllText(registro.ArchivoDeHoy));
        Assert.DoesNotContain(nameof(DirectoryNotFoundException), alLeer.Message);
    }

    /// <summary>Una carpeta en la que no se puede escribir: lo dice Windows al escribir el de paso, y eso es lo que dice la caja.</summary>
    [Fact]
    public async Task Una_carpeta_sin_permiso_para_escribir_lo_dice()
    {
        if (OperatingSystem.IsWindows())
            return;
        var soloLectura = Directory.CreateDirectory(Path.Combine(_carpeta, "Solo lectura")).FullName;
        File.SetUnixFileMode(soloLectura, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // Como administrador (root) sí se puede: ahí no hay qué probar.
            try
            {
                await File.WriteAllBytesAsync(Path.Combine(soloLectura, "prueba"), [1]);
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }
            var ruta = Path.Combine(soloLectura, "Ventas.xlsx");
            var error = await Assert.ThrowsAsync<ErrorCaja>(() => new GuardadoEnLaPc(_ => Task.FromResult<string?>(ruta), _carpeta).GuardarAsync(Excel));
            Assert.Equal($"No se pudo guardar en {ruta}: Windows no deja escribir en esa carpeta. Elige otra, como Documentos.", error.Message);
        }
        finally
        {
            File.SetUnixFileMode(soloLectura, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task En_el_navegador_es_una_descarga_con_caja_js()
    {
        var pagina = new PaginaQueContesta();
        var descarga = new DescargaDelNavegador(new ModuloCaja(pagina));

        var donde = await descarga.GuardarAsync(Excel);

        var (funcion, argumentos) = pagina.Llamadas.Last();
        Assert.Equal("descargar", funcion);
        Assert.Equal(Excel.Nombre, argumentos![0]);
        Assert.Equal(Excel.Tipo, argumentos[1]);
        // El contenido va por un flujo, no como texto en un mensaje.
        Assert.IsType<DotNetStreamReference>(argumentos[2]);
        Assert.Equal(new ArchivoGuardado(Excel.Nombre, null), donde);
        Assert.Equal("Se descargó «Ventas 2026-10.xlsx»: está en las descargas del navegador.", donde!.Texto);
    }

    /// <summary>El error de JavaScript trae su rastro completo (blazor.webview.js lo pega al mensaje): no se enseña.</summary>
    [Fact]
    public async Task En_el_navegador_si_la_descarga_falla_no_se_ve_el_rastro_de_javascript()
    {
        var descarga = new DescargaDelNavegador(new ModuloCaja(new PaginaQueFalla()));

        var error = await Assert.ThrowsAsync<ErrorCaja>(() => descarga.GuardarAsync(Excel));

        Assert.Equal($"El navegador no pudo descargar «{Excel.Nombre}»: vuelve a intentarlo.", error.Message);
    }

    private sealed class PaginaQueFalla : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new JSException("TypeError: x\n    at descargar (caja.js:1:1)");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    [Fact]
    public async Task La_de_mentiras_de_las_pantallas_guarda_lo_que_se_le_da_o_cancela()
    {
        var guardado = new GuardadoDePrueba();

        var donde = await guardado.GuardarAsync(Excel);
        guardado.Cancelar = true;
        var cancelado = await guardado.GuardarAsync(Excel);

        Assert.Equal(@"C:\Users\caja\Documents\Ventas 2026-10.xlsx", donde!.Ruta);
        Assert.Null(cancelado);
        Assert.Equal(2, guardado.Guardados.Count);
    }
}
