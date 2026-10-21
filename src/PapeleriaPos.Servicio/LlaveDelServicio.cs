using System.Security.Cryptography;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Servicio;

/// <summary>
/// La llave propia del servicio (la red de la tienda en el POS): con ella firma la prueba de quién es (<see cref="Descubrimiento"/>)
/// y las cajas la recuerdan (al darlas de alta, o la primera vez que la comprueban en su dirección de siempre). Así, una copia de la
/// base en otra PC (la caja principal de antes, un respaldo abierto en una laptop) sabe la huella de la llave de cada caja, pero no
/// firma como este servicio: una caja que ya la conoce no se cambia sola a ella.
/// <para>
/// Es una llave ECDSA P-256 que se hace sola la primera vez y se guarda junto a la base, en su propio archivo
/// (<c>pos.llave</c>), fuera de la base y de sus respaldos. En Windows va cifrada para esa PC (DPAPI de la máquina): copiada a otra
/// PC no sirve. En los demás sistemas, el archivo solo lo lee quien corre el servicio. Si no se puede leer (viene de otra PC), se
/// aparta y se hace otra: las cajas que conocían la de antes tienen que darse de alta otra vez (lo dicen en la franja).
/// </para>
/// </summary>
public sealed class LlaveDelServicio : IDisposable
{
    /// <summary>Con qué se cifra en Windows, además de la máquina: solo sirve para esto.</summary>
    private static readonly byte[] Proposito = "PapeleriaPos.Servicio/llave-del-servicio"u8.ToArray();

    private readonly ECDsa _privada;

    private LlaveDelServicio(ECDsa privada)
    {
        _privada = privada;
        Publica = Convert.ToBase64String(privada.ExportSubjectPublicKeyInfo());
    }

    /// <summary>La llave pública, en base64 (SubjectPublicKeyInfo): la que recuerdan las cajas.</summary>
    public string Publica { get; }

    /// <summary>Junto a la base: «pos.db» → «pos.llave».</summary>
    public static string Archivo(string rutaDeLaBase) => Path.ChangeExtension(Path.GetFullPath(rutaDeLaBase), ".llave");

    /// <summary>La firma (SHA-256, IEEE P1363) de ese mensaje, en base64.</summary>
    public string Firmar(byte[] mensaje) =>
        Convert.ToBase64String(_privada.SignData(mensaje, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>La de ese archivo; si no hay (o no se puede leer: viene de otra PC), una nueva, guardada ahí.</summary>
    /// <param name="noSeLeyo">Lo que se avisa si había una que no se pudo leer (al registro del servicio).</param>
    public static LlaveDelServicio CargarOCrear(string archivo, Action<Exception>? noSeLeyo = null)
    {
        if (File.Exists(archivo))
        {
            try
            {
                var privada = ECDsa.Create();
                privada.ImportPkcs8PrivateKey(Abrir(File.ReadAllBytes(archivo)), out _);
                return new LlaveDelServicio(privada);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                noSeLeyo?.Invoke(ex);
                Apartar(archivo);
            }
        }
        var nueva = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Guardar(archivo, Cerrar(nueva.ExportPkcs8PrivateKey()));
        return new LlaveDelServicio(nueva);
    }

    private static byte[] Cerrar(byte[] pkcs8) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(pkcs8, Proposito, DataProtectionScope.LocalMachine) : pkcs8;

    private static byte[] Abrir(byte[] guardado) =>
        OperatingSystem.IsWindows() ? ProtectedData.Unprotect(guardado, Proposito, DataProtectionScope.LocalMachine) : guardado;

    /// <summary>Primero a un archivo aparte, hasta el disco, y después se cambia de un jalón; fuera de Windows, solo para su dueño.</summary>
    private static void Guardar(string archivo, byte[] datos)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivo)!);
        var nuevo = archivo + ".nuevo";
        var opciones = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            opciones.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var flujo = new FileStream(nuevo, opciones))
        {
            flujo.Write(datos);
            flujo.Flush(flushToDisk: true);
        }
        File.Move(nuevo, archivo, overwrite: true);
    }

    /// <summary>La que no se pudo leer se queda aparte (para revisarla), no se borra.</summary>
    private static void Apartar(string archivo)
    {
        try
        {
            File.Move(archivo, $"{archivo}.no-sirve-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Se escribe encima al guardar la nueva.
        }
    }

    public void Dispose() => _privada.Dispose();
}
