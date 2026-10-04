using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Caja;

/// <summary>
/// Configuración de esta PC. La primera vez sale de caja.json, junto al programa; al dar de alta la caja se guarda en
/// ProgramData (sirve para cualquier usuario de Windows que entre a la PC) con la llave cifrada por Windows.
/// </summary>
/// <param name="Impresora">"tcp://ip:9100" (impresora de red), el nombre de una impresora de Windows, o vacío para ver el ticket en pantalla.</param>
/// <param name="LlaveProtegida">La llave de la caja cifrada para esta PC: copiada a otra no sirve.</param>
public sealed record ConfiguracionCaja(string Servicio, Guid? CajaId, string? Impresora, string? LlaveProtegida = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private static string Guardada => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PapeleriaPos", "caja.json");

    public static ConfiguracionCaja Leer()
    {
        var ruta = File.Exists(Guardada) ? Guardada : Path.Combine(AppContext.BaseDirectory, "caja.json");
        return JsonSerializer.Deserialize<ConfiguracionCaja>(File.ReadAllText(ruta), Json)
            ?? throw new InvalidOperationException($"{ruta} está vacío.");
    }

    /// <summary>Si no se puede descifrar (el archivo viene de otra PC), la caja se vuelve a dar de alta.</summary>
    public string? Llave()
    {
        if (LlaveProtegida is null)
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(LlaveProtegida), null, DataProtectionScope.LocalMachine));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public ConfiguracionCaja Guardar(ConexionCaja conexion)
    {
        var nueva = this with
        {
            Servicio = conexion.Servicio.ToString(),
            CajaId = conexion.CajaId,
            LlaveProtegida = conexion.Llave is { } llave
                ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(llave), null, DataProtectionScope.LocalMachine))
                : null,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Guardada)!);
        File.WriteAllText(Guardada, JsonSerializer.Serialize(nueva, Json));
        return nueva;
    }
}

/// <summary>La ventana de la caja: muestra la interfaz compartida y manda el ticket a la impresora de tickets.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var configuracion = ConfiguracionCaja.Leer();
        var conexion = new ConexionCaja
        {
            Servicio = new Uri(configuracion.Servicio),
            CajaId = configuracion.CajaId,
            Llave = configuracion.Llave(),
            PideAlta = true,
            AlGuardar = c =>
            {
                configuracion = configuracion.Guardar(c);
                return Task.CompletedTask;
            },
        };
        var servicios = new ServiceCollection();
        servicios.AddWpfBlazorWebView();
        // "tcp://192.168.1.100:9100" imprime directo por la red; un nombre (como "Tickets") usa la impresora de Windows.
        var impresora = Impresoras.Crear(configuracion.Impresora, nombre => new ImpresoraWindows(nombre));
        servicios.AgregarInterfazCaja(conexion, impresora);
        Vista.Services = servicios.BuildServiceProvider();
    }
}
