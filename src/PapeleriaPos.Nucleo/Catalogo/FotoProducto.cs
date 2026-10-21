using System.Security.Cryptography;

namespace PapeleriaPos.Nucleo.Catalogo;

// Las fotos de los productos (entrega 13, parte 2; docs/propuesta-interfaz.md, «Lo que se señala», punto 7): se toman con el
// celular desde la página del conteo o se suben desde la PC en Productos (F3), y salen en Ctrl+M, en la lista de la búsqueda
// y en el verificador, para reconocer el producto.

/// <summary>
/// La foto de un producto: una por variante (la que sale en Ctrl+M, en la lista de la búsqueda y en el verificador), ya chica
/// (un JPEG de a lo más 240 píxeles por lado, unos 15 KB): la original del celular nunca se guarda. Subir otra la reemplaza y
/// quitarla la borra. Va en la copia de cada caja solo por su huella (<see cref="Huella"/>): la foto la baja cada caja aparte,
/// una sola vez, y la guarda en su base.
/// </summary>
public class FotoProducto : IVersionado
{
    /// <summary>La variante de la foto: también es su llave (una por variante).</summary>
    public Guid VarianteId { get; set; }

    /// <summary>La foto ya lista para las cajas (JPEG).</summary>
    public required byte[] Datos { get; set; }

    /// <summary>De qué foto es (<see cref="HuellaDe"/>): con ella cada caja sabe si ya la tiene.</summary>
    public required string Huella { get; set; }

    public int Ancho { get; set; }
    public int Alto { get; set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    /// <summary>Lo largo de la huella: 16 cifras hexadecimales (64 bits del SHA-256 de la foto).</summary>
    public const int LargoHuella = 16;

    /// <summary>
    /// La huella de una foto: lo mismo en la caja principal y en cada caja, para la misma foto. No depende de la versión del
    /// catálogo: una base que empieza de cero o un respaldo restaurado no hacen que las cajas vuelvan a bajar las mismas fotos.
    /// </summary>
    public static string HuellaDe(ReadOnlySpan<byte> datos) => Convert.ToHexStringLower(SHA256.HashData(datos))[..LargoHuella];

    /// <summary>
    /// Cómo se pide la foto para verla en la pantalla (el <c>src</c> de su imagen): <c>api/fotos/{variante}-{huella}.jpg</c>.
    /// En la ventana de la caja la da su base local, sin la red; en la caja del navegador, el servicio de esa misma PC.
    /// </summary>
    public static string Archivo(Guid varianteId, string huella) => $"{Carpeta}{varianteId:N}-{huella}.jpg";

    /// <summary>Lo que va antes del archivo de cada foto.</summary>
    public const string Carpeta = "api/fotos/";

    /// <summary>
    /// De qué variante y de qué foto es un archivo (<see cref="Archivo"/>), con o sin la carpeta y la diagonal del principio;
    /// falso si no es de una foto.
    /// </summary>
    public static bool LeerArchivo(string? ruta, out Guid varianteId, out string huella)
    {
        (varianteId, huella) = (Guid.Empty, "");
        var nombre = (ruta ?? "").TrimStart('/');
        if (nombre.StartsWith(Carpeta, StringComparison.OrdinalIgnoreCase))
            nombre = nombre[Carpeta.Length..];
        // 32 cifras de la variante, el guion, la huella y «.jpg».
        if (nombre.Length != 32 + 1 + LargoHuella + 4 || nombre[32] != '-' || !nombre.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(nombre[..32], "N", out var id))
            return false;
        var leida = nombre.Substring(33, LargoHuella);
        if (!leida.All(char.IsAsciiHexDigitLower))
            return false;
        (varianteId, huella) = (id, leida);
        return true;
    }
}
