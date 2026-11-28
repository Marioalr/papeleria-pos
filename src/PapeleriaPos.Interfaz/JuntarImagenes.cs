using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Imagenes;

namespace PapeleriaPos.Interfaz;

/// <summary>Una imagen juntada, como se enseña en la ventanita: su miniatura (solo para la pantalla) y sus píxeles.</summary>
/// <param name="Miniatura">Un <c>data:</c> con el JPEG chico (112 px).</param>
/// <param name="Grande">Otro de 480 px, para verla grande con Espacio.</param>
public sealed record ImagenEnLaVentanita(string Miniatura, string Grande, int AnchoPx, int AltoPx);

/// <summary>Una imagen en el orden que se dejó en la ventanita, con su giro (0, 90, 180 o 270, a la derecha).</summary>
/// <param name="Indice">Su lugar en <see cref="IJuntarImagenes.Imagenes"/>.</param>
public sealed record ImagenOrdenada(int Indice, int Giro);

/// <summary>
/// Lo que la ventanita de «Juntar imágenes para Word» (docs/propuestas/juntar-imagenes.md) le pide a la caja de Windows: lo juntado, y
/// copiarlo, abrirlo en Word, seguir juntando o cancelar. La caja cierra la ventanita en cada uno.
/// </summary>
public interface IJuntarImagenes
{
    IReadOnlyList<ImagenEnLaVentanita> Imagenes { get; }

    /// <summary>Las que se copiaron dos veces y se contaron una.</summary>
    int Repetidas { get; }

    /// <summary>El orden de la vez pasada, si se siguió juntando: la ventanita abre igual, con las nuevas al final.</summary>
    IReadOnlyList<ImagenOrdenada>? Orden { get; }

    /// <summary>Los de la vez pasada en esta caja.</summary>
    AjustesDeJuntarImagenes Ajustes { get; }

    /// <summary>Si la PC tiene Word: sin él, «Abrir en Word» sale apagado y «Copiar» sigue sirviendo.</summary>
    bool HayWord { get; }

    Task CopiarAsync(IReadOnlyList<ImagenOrdenada> orden, AjustesDeJuntarImagenes ajustes);

    Task AbrirEnWordAsync(IReadOnlyList<ImagenOrdenada> orden, AjustesDeJuntarImagenes ajustes);

    /// <summary>Cierra la ventanita y sigue juntando sobre las mismas.</summary>
    void JuntarMas(IReadOnlyList<ImagenOrdenada> orden, AjustesDeJuntarImagenes ajustes);

    /// <summary>Cierra y borra lo juntado.</summary>
    void Cancelar();
}

/// <summary>juntar.js: las teclas de la ventanita (sin que el navegador haga lo suyo con ellas) y poner el cursor en el número.</summary>
public sealed class ModuloJuntar(IJSRuntime js) : IAsyncDisposable
{
    private Task<IJSObjectReference>? _modulo;

    private Task<IJSObjectReference> Modulo => _modulo ??= js.InvokeAsync<IJSObjectReference>("import", "./_content/PapeleriaPos.Interfaz/juntar.js").AsTask();

    public async Task PrepararAsync<T>(DotNetObjectReference<T> ventanita) where T : class =>
        await (await Modulo).InvokeVoidAsync("preparar", ventanita);

    public async Task EnfocarAsync(string selector) => await (await Modulo).InvokeVoidAsync("enfocar", selector);

    public async ValueTask DisposeAsync()
    {
        if (_modulo is null)
            return;
        try
        {
            await (await _modulo).DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
    }
}

/// <summary>Qué pasó con lo que se copió mientras se junta.</summary>
public enum LoQueSeCopio
{
    Nueva,

    /// <summary>La misma imagen ya estaba (misma huella): se cuenta una vez.</summary>
    Repetida,

    /// <summary>Un texto, un archivo que no es imagen: no cuenta ni avisa.</summary>
    NoEsImagen,
}

public enum EstadoDeLaJunta
{
    /// <summary>Nada juntado.</summary>
    Nada,

    /// <summary>Cada imagen que se copie entra.</summary>
    Juntando,

    /// <summary>Se terminó de juntar: la ventanita está abierta (o se cerró sin copiar).</summary>
    Juntadas,

    /// <summary>Ya se copiaron para Word: siguen guardadas un rato por si hay que volver a copiarlas.</summary>
    Copiadas,
}

/// <summary>
/// Lo que se junta en «Juntar imágenes para Word», sin nada de Windows (la caja de Windows pone el atajo, el portapapeles, el contador y
/// la ventanita): las imágenes tal como llegaron, sin repetir, el orden y los ajustes de la ventanita, y el bloque para Word. Nada de esto
/// se guarda en disco ni va a la caja principal; solo los ajustes.
/// </summary>
public sealed class JuntaDeImagenes
{
    /// <summary>Sin copiar nada en este tiempo, se deja de juntar sola.</summary>
    public static readonly TimeSpan SinCopiar = TimeSpan.FromMinutes(15);

    /// <summary>Lo juntado se borra este tiempo después de copiarlo (o de dejarlo sin copiar).</summary>
    public static readonly TimeSpan Guardadas = TimeSpan.FromMinutes(15);

    private readonly List<ImagenJuntada> _imagenes = [];
    private readonly List<ImagenEnLaVentanita?> _vistas = [];
    private readonly HashSet<string> _huellas = [];

    public JuntaDeImagenes(AjustesDeJuntarImagenes ajustes) => Ajustes = ajustes;

    public EstadoDeLaJunta Estado { get; private set; }

    public IReadOnlyList<ImagenJuntada> Imagenes => _imagenes;

    public int Repetidas { get; private set; }

    public IReadOnlyList<ImagenOrdenada>? Orden { get; private set; }

    public AjustesDeJuntarImagenes Ajustes { get; private set; }

    /// <summary>La última vez que pasó algo (se copió una imagen, se cerró la ventanita): de ahí cuentan los 15 minutos.</summary>
    public DateTimeOffset UltimoMovimiento { get; private set; }

    /// <summary>Empieza de cero: lo de antes se borra.</summary>
    public void Empezar(DateTimeOffset ahora)
    {
        Borrar();
        Estado = EstadoDeLaJunta.Juntando;
        UltimoMovimiento = ahora;
    }

    /// <summary>Sigue juntando sobre las mismas (desde la ventanita, «Juntar más»).</summary>
    public void Seguir(IReadOnlyList<ImagenOrdenada> orden, AjustesDeJuntarImagenes ajustes, DateTimeOffset ahora)
    {
        (Orden, Ajustes) = (orden, ajustes);
        Estado = EstadoDeLaJunta.Juntando;
        UltimoMovimiento = ahora;
    }

    /// <summary>Lo que se copió: una imagen (los bytes del PNG, del JPEG o del archivo, tal cual) entra si no estaba.</summary>
    public LoQueSeCopio Agregar(byte[] datos, DateTimeOffset ahora)
    {
        if (Estado != EstadoDeLaJunta.Juntando)
            return LoQueSeCopio.NoEsImagen;
        if (ImagenesJuntadas.Preparar(datos) is not { } imagen)
            return LoQueSeCopio.NoEsImagen;
        UltimoMovimiento = ahora;
        if (!_huellas.Add(imagen.Huella))
        {
            Repetidas++;
            return LoQueSeCopio.Repetida;
        }
        _imagenes.Add(imagen);
        _vistas.Add(null);
        return LoQueSeCopio.Nueva;
    }

    /// <summary>Se termina de juntar: true si hay algo que enseñar en la ventanita; sin nada, se vuelve a no tener nada.</summary>
    public bool Terminar(DateTimeOffset ahora)
    {
        UltimoMovimiento = ahora;
        if (_imagenes.Count == 0)
        {
            Borrar();
            return false;
        }
        Estado = EstadoDeLaJunta.Juntadas;
        return true;
    }

    /// <summary>La miniatura y la vista grande de una (se hacen una vez; pueden hacerse aparte, mientras se sigue juntando).</summary>
    public ImagenEnLaVentanita Vista(int indice)
    {
        lock (_vistas)
        {
            if (_vistas[indice] is { } hecha)
                return hecha;
        }
        var imagen = _imagenes[indice];
        var vista = new ImagenEnLaVentanita(
            "data:image/jpeg;base64," + Convert.ToBase64String(ImagenesJuntadas.Miniatura(imagen)),
            "data:image/jpeg;base64," + Convert.ToBase64String(ImagenesJuntadas.Miniatura(imagen, 480)),
            imagen.AnchoPx, imagen.AltoPx);
        lock (_vistas)
            return _vistas[indice] ??= vista;
    }

    /// <summary>
    /// El bloque RTF para Word en el orden de la ventanita, con sus giros y en gris si se pidió; con la hoja para «Abrir en Word». Null si el
    /// número de los ajustes no sirve. Desde aquí cuentan los 15 minutos para borrarlas.
    /// </summary>
    public string? ParaWord(IReadOnlyList<ImagenOrdenada> orden, AjustesDeJuntarImagenes ajustes, bool conHoja, DateTimeOffset ahora)
    {
        var finales = orden.Select(o => ImagenesJuntadas.ComoVa(_imagenes[o.Indice], o.Giro, ajustes.BlancoYNegro)).ToList();
        if (ajustes.Acomodar(finales.Select(f => new ImagenPorAcomodar(f.AnchoPx, f.AltoPx)).ToList()) is not { } acomodo)
            return null;
        (Orden, Ajustes, Estado, UltimoMovimiento) = (orden, ajustes, EstadoDeLaJunta.Copiadas, ahora);
        return DocumentoParaWord.Rtf(finales, acomodo, ajustes.Separacion, ajustes.LineasDeCorte, conHoja ? ajustes.Hoja : null);
    }

    /// <summary>La ventanita se cerró sin copiar ni cancelar (con la × de la ventana): lo juntado se guarda un rato, como al copiar.</summary>
    public void Dejar(DateTimeOffset ahora)
    {
        if (Estado == EstadoDeLaJunta.Juntadas)
            UltimoMovimiento = ahora;
    }

    /// <summary>Si ya pasó su tiempo: juntando, 15 minutos sin copiar; juntadas o copiadas, 15 minutos desde la última vez.</summary>
    public bool Vencida(DateTimeOffset ahora) =>
        Estado switch
        {
            EstadoDeLaJunta.Juntando => ahora - UltimoMovimiento >= SinCopiar,
            EstadoDeLaJunta.Juntadas or EstadoDeLaJunta.Copiadas => ahora - UltimoMovimiento >= Guardadas,
            _ => false,
        };

    /// <summary>Lo juntado se borra (cancelar, vencida, cerrar la caja). Los ajustes se quedan.</summary>
    public void Borrar()
    {
        _imagenes.Clear();
        lock (_vistas)
            _vistas.Clear();
        _huellas.Clear();
        (Repetidas, Orden, Estado) = (0, null, EstadoDeLaJunta.Nada);
    }

    /// <summary>Para el registro de la caja: cuántas y cómo, nada de lo que muestran.</summary>
    public static string Resumen(int cuantas, AjustesDeJuntarImagenes ajustes) =>
        $"{cuantas} {(cuantas == 1 ? "imagen" : "imágenes")} · " + ajustes.Modo switch
        {
            ModoDelTamano.Ancho => $"ancho {ajustes.Ancho.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} cm",
            ModoDelTamano.EnHojas => $"en {ajustes.Hojas} {(ajustes.Hojas == 1 ? "hoja" : "hojas")}",
            _ => $"alto {ajustes.Alto.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} cm",
        };
}
