using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace PapeleriaPos.Instalacion;

/// <summary>Esta PC con Windows: el registro, las impresoras (winspool), los puertos COM y los archivos de siempre.</summary>
[SupportedOSPlatform("windows")]
public sealed class EquipoWindows : IEquipo
{
    public string Datos { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PapeleriaPos");

    public string Programa { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PapeleriaPos");

    public string? ExeDelServicio()
    {
        using var clave = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PapeleriaPos");
        return clave?.GetValue("ImagePath") is string ruta ? Rutas.ExeDeLaLinea(ruta) : null;
    }

    public string? Version(string exe) => Rutas.Version(exe);

    public IReadOnlyList<ImpresoraDeWindows> Impresoras()
    {
        const int locales = 0x2, conexiones = 0x4;
        try
        {
            EnumPrinters(locales | conexiones, null, 2, IntPtr.Zero, 0, out var hacenFalta, out _);
            if (hacenFalta <= 0)
                return [];
            var memoria = Marshal.AllocHGlobal(hacenFalta);
            try
            {
                if (!EnumPrinters(locales | conexiones, null, 2, memoria, hacenFalta, out _, out var cuantas))
                    return [];
                // PRINTER_INFO_2: trece apuntadores y ocho números de 32 bits; se leen sin copiar la estructura.
                var tamano = 13 * IntPtr.Size + 8 * sizeof(int);
                tamano = (tamano + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
                var impresoras = new List<ImpresoraDeWindows>();
                for (var i = 0; i < cuantas; i++)
                {
                    var info = memoria + i * tamano;
                    var nombre = Marshal.PtrToStringUni(Marshal.ReadIntPtr(info, 1 * IntPtr.Size));
                    if (string.IsNullOrEmpty(nombre))
                        continue;
                    var puerto = Marshal.PtrToStringUni(Marshal.ReadIntPtr(info, 3 * IntPtr.Size));
                    var atributos = Marshal.ReadInt32(info, 13 * IntPtr.Size);
                    impresoras.Add(new ImpresoraDeWindows(nombre, puerto, (atributos & 0x1000) != 0,
                        nombre.StartsWith(@"\\", StringComparison.Ordinal) || (atributos & 0x10) != 0));
                }
                return impresoras.OrderBy(i => i.Nombre, StringComparer.OrdinalIgnoreCase).ToList();
            }
            finally
            {
                Marshal.FreeHGlobal(memoria);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumPrintersW")]
    private static extern bool EnumPrinters(int flags, string? nombre, int nivel, IntPtr memoria, int tamano, out int hacenFalta, out int cuantas);

    public IReadOnlyList<string> PuertosCom()
    {
        using var clave = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (clave is null)
            return [];
        return Rutas.Puertos(clave.GetValueNames().Select(n => clave.GetValue(n) as string).OfType<string>());
    }

    public IReadOnlyList<string> Ips()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(t => t.OperationalStatus == OperationalStatus.Up && BusquedaDeLaPrincipal.EsDeLaTienda(t.NetworkInterfaceType))
                .SelectMany(t => t.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip) && ip.GetAddressBytes()[0] != 169)
                .Select(ip => ip.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> IpsPropias()
    {
        try
        {
            // De cualquier tarjeta y aunque esté apagada: dos PC pueden traer la misma IP en una red virtual, y esa aquí es esta PC.
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(t => t.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .Select(ip => ip.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Ips();
        }
    }

    /// <summary>
    /// Como la abre la caja (<c>ProtectedData.Unprotect(…, null, DataProtectionScope.LocalMachine)</c>, en ConfiguracionCaja.Llave): DPAPI
    /// de la máquina, sin entropía y sin preguntar nada en pantalla. El ayudante no lleva paquetes, así que va directo a crypt32, con la
    /// misma llamada que hace ese paquete. Una prueba de Windows revisa que abra lo que cierra ProtectedData.
    /// </summary>
    public byte[]? AbrirLoDeEstaPc(byte[] cifrado)
    {
        const int sinPreguntar = 0x1, deLaMaquina = 0x4;
        if (cifrado.Length == 0)
            return null;
        var entrada = new Bloque { Largo = cifrado.Length, Datos = Marshal.AllocHGlobal(cifrado.Length) };
        var sinEntropia = default(Bloque);
        var salida = default(Bloque);
        try
        {
            Marshal.Copy(cifrado, 0, entrada.Datos, cifrado.Length);
            if (!CryptUnprotectData(ref entrada, IntPtr.Zero, ref sinEntropia, IntPtr.Zero, IntPtr.Zero, sinPreguntar | deLaMaquina, ref salida)
                || salida.Datos == IntPtr.Zero || salida.Largo < 0)
                return null;
            var abierto = new byte[salida.Largo];
            Marshal.Copy(salida.Datos, abierto, 0, abierto.Length);
            return abierto;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (salida.Datos != IntPtr.Zero)
            {
                // Lo abierto es un secreto: no se queda en una memoria que ya no es de nadie.
                for (var i = 0; i < salida.Largo; i++)
                    Marshal.WriteByte(salida.Datos, i, 0);
                // Lo que entrega crypt32 se suelta con LocalFree.
                Marshal.FreeHGlobal(salida.Datos);
            }
            Marshal.FreeHGlobal(entrada.Datos);
        }
    }

    /// <summary>DATA_BLOB de Windows: cuántos bytes y dónde están.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Bloque
    {
        public int Largo;
        public IntPtr Datos;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Bloque cifrado, IntPtr descripcion, ref Bloque entropia, IntPtr reservado, IntPtr pregunta, int banderas,
        ref Bloque abierto);

    public IReadOnlyList<string> Gbak()
    {
        var raices = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\",
        };
        return Rutas.Gbak(Datos, raices.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public string? IniDeEleventa()
    {
        var ruta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AbarrotesMultiCaja", "pdventa.ini");
        try
        {
            // Solo se lee (eleventa no se toca): en la página de códigos de Windows, como lo escribe eleventa.
            return File.Exists(ruta) ? File.ReadAllText(ruta, Encoding.Latin1) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Lo de las rutas y los nombres que no depende de Windows (y se prueba en Linux).</summary>
public static class Rutas
{
    /// <summary>
    /// El .exe de la línea con que está registrado un servicio («"C:\Program Files\…\PapeleriaPos.Servicio.exe"», con o sin comillas y
    /// con sus argumentos), como lo lee actualizar.ps1.
    /// </summary>
    public static string ExeDeLaLinea(string linea)
    {
        var ruta = Environment.ExpandEnvironmentVariables(linea).Trim();
        if (ruta.StartsWith('"'))
        {
            var cierra = ruta.IndexOf('"', 1);
            return cierra > 0 ? ruta[1..cierra] : ruta.Trim('"');
        }
        var exe = ruta.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? ruta[..(exe + 4)] : ruta;
    }

    /// <summary>
    /// La versión de un programa como la lee actualizar.ps1: la del producto sin lo de después del «+» («1.0.57+abc» es 1.0.57); si no
    /// trae, la de su .dll, y si tampoco, los cuatro números del archivo.
    /// </summary>
    public static string? Version(string exe)
    {
        try
        {
            if (!File.Exists(exe))
                return null;
            var info = FileVersionInfo.GetVersionInfo(exe);
            var dll = Path.ChangeExtension(exe, ".dll");
            if (string.IsNullOrWhiteSpace(info.ProductVersion) && File.Exists(dll))
                info = FileVersionInfo.GetVersionInfo(dll);
            if (!string.IsNullOrWhiteSpace(info.ProductVersion) && char.IsAsciiDigit(info.ProductVersion.Trim()[0]))
                return info.ProductVersion.Split('+')[0].Trim();
            return info.FileMajorPart + info.FileMinorPart + info.FileBuildPart + info.FilePrivatePart == 0
                ? null
                : $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Los puertos COM sin repetir, en orden de su número (COM1, COM3, COM10).</summary>
    public static IReadOnlyList<string> Puertos(IEnumerable<string> puertos) =>
        puertos.Select(p => p.Trim()).Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(p[3..], out var n) ? n : int.MaxValue)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Los gbak.exe que hay en los lugares de siempre: la copia de Firebird 2.0 que dice el instructivo (en la carpeta de datos,
    /// «firebird20\bin»), y en Archivos de programa (y la raíz del disco) las carpetas de Firebird o de eleventa, hasta dos niveles abajo
    /// («Firebird\Firebird_2_0\bin», «FirebirdSQL20\bin», «AbarrotesMultiCaja\FirebirdSQL20\bin»).
    /// </summary>
    public static IReadOnlyList<string> Gbak(string datos, IReadOnlyList<string> raices)
    {
        var encontrados = new List<string>();
        void Revisar(string carpeta)
        {
            var gbak = Path.Combine(carpeta, "bin", "gbak.exe");
            if (File.Exists(gbak) && !encontrados.Contains(gbak, StringComparer.OrdinalIgnoreCase))
                encontrados.Add(gbak);
        }
        Revisar(Path.Combine(datos, "firebird20"));
        foreach (var raiz in raices)
        {
            IEnumerable<string> primeras;
            try
            {
                primeras = Directory.Exists(raiz) ? Directory.EnumerateDirectories(raiz).ToList() : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var primera in primeras.Where(EsDeFirebirdOEleventa))
            {
                Revisar(primera);
                try
                {
                    foreach (var segunda in Directory.EnumerateDirectories(primera))
                        Revisar(segunda);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Una carpeta que no se deja leer: las demás sí.
                }
            }
        }
        return encontrados;
    }

    private static bool EsDeFirebirdOEleventa(string carpeta)
    {
        var nombre = Path.GetFileName(carpeta);
        return nombre.Contains("firebird", StringComparison.OrdinalIgnoreCase) || nombre.Contains("abarrotes", StringComparison.OrdinalIgnoreCase)
            || nombre.Contains("eleventa", StringComparison.OrdinalIgnoreCase);
    }
}
