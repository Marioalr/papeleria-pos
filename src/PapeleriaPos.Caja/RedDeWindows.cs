using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PapeleriaPos.Interfaz;
using Windows.Networking.Connectivity;

namespace PapeleriaPos.Caja;

/// <summary>
/// Lo de la red que solo sabe Windows, para la bitácora de red (la red de la tienda en el POS): el Wi-Fi de la caja (su nombre y
/// su señal, de las API de Windows, sin pedir permisos) y la MAC del router (de la tabla ARP que Windows ya tiene, sin mandar
/// nada). Desde Windows 11 24H2, el nombre del Wi-Fi puede pedir el permiso de ubicación: sin él, va el nombre del perfil de red
/// de Windows (casi siempre el mismo). Lo que no se puede leer se queda vacío: nunca detiene la caja.
/// </summary>
public sealed class RedDeWindows : IDetallesDeRed
{
    public (string? Red, int? Senal)? Wifi(string idDelAdaptador)
    {
        if (!Guid.TryParse(idDelAdaptador, out var id))
            return null;
        foreach (var perfil in NetworkInformation.GetConnectionProfiles())
        {
            if (perfil.NetworkAdapter?.NetworkAdapterId != id || !perfil.IsWlanConnectionProfile)
                continue;
            string? red = null;
            try
            {
                red = perfil.WlanConnectionProfileDetails?.GetConnectedSsid();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or COMException)
            {
                // Sin el permiso de ubicación: el nombre del perfil.
            }
            if (string.IsNullOrWhiteSpace(red))
                red = perfil.ProfileName;
            // Windows da de 0 a 5 rayitas: en porcentaje, de 20 en 20.
            int? senal = perfil.GetSignalBars() is { } rayitas ? Math.Clamp(rayitas * 20, 0, 100) : null;
            return (string.IsNullOrWhiteSpace(red) ? null : red, senal);
        }
        return null;
    }

    public string? Mac(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return null;
        var buscada = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
        var tamano = 0;
        if (GetIpNetTable(IntPtr.Zero, ref tamano, false) is not (SinEspacio or 0) || tamano <= 0)
            return null;
        var tabla = Marshal.AllocHGlobal(tamano);
        try
        {
            if (GetIpNetTable(tabla, ref tamano, false) != 0)
                return null;
            // MIB_IPNETTABLE: cuántas, y cada MIB_IPNETROW (24 bytes): índice, largo de la MAC, la MAC (8), la IP y el tipo.
            var cuantas = Marshal.ReadInt32(tabla);
            for (var i = 0; i < cuantas; i++)
            {
                var renglon = tabla + 4 + i * 24;
                var largo = Marshal.ReadInt32(renglon, 4);
                var direccion = (uint)Marshal.ReadInt32(renglon, 16);
                var tipo = Marshal.ReadInt32(renglon, 20);
                if (direccion != buscada || tipo == Invalida || largo is <= 0 or > 8)
                    continue;
                var mac = new byte[largo];
                Marshal.Copy(renglon + 8, mac, 0, largo);
                return string.Join(':', mac.Select(b => b.ToString("x2")));
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(tabla);
        }
    }

    private const int SinEspacio = 122;

    /// <summary>MIB_IPNET_TYPE_INVALID: una entrada que ya no sirve.</summary>
    private const int Invalida = 2;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetIpNetTable(IntPtr tabla, ref int tamano, [MarshalAs(UnmanagedType.Bool)] bool ordenada);
}

/// <summary>
/// El puerto de cada impresora de Windows, del registro (sin permisos de administrador): si es TCP/IP estándar (y a qué IP o
/// nombre), WSD, compartida desde otra PC o local (USB). Para el aviso «configúrala por IP» (<see cref="AvisosDeRed.DeLaImpresora"/>).
/// </summary>
public sealed class PuertosDeImpresorasWindows : IPuertosDeImpresoras
{
    private const string Impresoras = @"SYSTEM\CurrentControlSet\Control\Print\Printers";
    private const string PuertosTcpIp = @"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports";
    private const string PuertosWsd = @"SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports";

    public PuertoDeImpresora? DeLaImpresora(string nombre)
    {
        if (nombre.Contains('\\'))
            return new PuertoDeImpresora(nombre, TipoDePuerto.Compartida);
        using var impresora = Registry.LocalMachine.OpenSubKey($@"{Impresoras}\{nombre}");
        // Varios puertos (una cola con varias impresoras) van separados por coma: cuenta el primero.
        if (impresora?.GetValue("Port") is not string puertos || puertos.Split(',')[0].Trim() is not { Length: > 0 } puerto)
            return null;
        using (var tcpIp = Registry.LocalMachine.OpenSubKey($@"{PuertosTcpIp}\{puerto}"))
        {
            if (tcpIp is not null)
            {
                var host = tcpIp.GetValue("HostName") as string is { Length: > 0 } nombreDelHost ? nombreDelHost : tcpIp.GetValue("IPAddress") as string;
                return new PuertoDeImpresora(puerto, TipoDePuerto.TcpIp, string.IsNullOrWhiteSpace(host) ? null : host.Trim());
            }
        }
        using (var wsd = Registry.LocalMachine.OpenSubKey($@"{PuertosWsd}\{puerto}"))
        {
            if (wsd is not null || puerto.StartsWith("WSD", StringComparison.OrdinalIgnoreCase))
                return new PuertoDeImpresora(puerto, TipoDePuerto.Wsd);
        }
        if (puerto.StartsWith(@"\\", StringComparison.Ordinal))
            return new PuertoDeImpresora(puerto, TipoDePuerto.Compartida);
        string[] locales = ["USB", "LPT", "COM", "FILE:", "PORTPROMPT:", "NUL:", "XPS", "PDF", "SHRFAX:"];
        return new PuertoDeImpresora(puerto, locales.Any(l => puerto.StartsWith(l, StringComparison.OrdinalIgnoreCase)) ? TipoDePuerto.Local : TipoDePuerto.Otro);
    }
}
