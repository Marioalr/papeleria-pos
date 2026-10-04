using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Medicion;

namespace PapeleriaPos.Caja;

/// <summary>
/// Configuración de esta PC. La primera vez sale de caja.json, junto al programa; al dar de alta la caja se guarda en
/// ProgramData (sirve para cualquier usuario de Windows que entre a la PC) con la llave cifrada por Windows.
/// </summary>
/// <param name="Impresora">"tcp://ip:9100" (impresora de red), el nombre de una impresora de Windows, o vacío para ver el ticket en pantalla.</param>
/// <param name="LlaveProtegida">La llave de la caja cifrada para esta PC: copiada a otra no sirve.</param>
/// <param name="ColaImpresion">La cola de Windows de la WF-6590 que vigila esta PC (vacío: no vigila ninguna). Mientras
/// Control Print siga en uso, se prueba con su cola de laboratorio.</param>
/// <param name="QuitarImpresos">Quitar de la cola lo ya impreso y medido (la cola conserva los documentos impresos).</param>
public sealed record ConfiguracionCaja(
    string Servicio, Guid? CajaId, string? Impresora, string? LlaveProtegida = null, string? ColaImpresion = null, bool QuitarImpresos = true)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private static string Carpeta => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PapeleriaPos");

    private static string Guardada => Path.Combine(Carpeta, "caja.json");

    /// <summary>La base local de la caja: copia del catálogo, bandeja de salida y diario del turno.</summary>
    public static string BaseLocal => Path.Combine(Carpeta, "caja-local.db");

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
        // La copia para vender sin la caja principal y la bandeja de salida, junto a la configuración de la caja.
        servicios.AgregarInterfazCaja(conexion, impresora, ConfiguracionCaja.BaseLocal);
        // Cotizar: el PDF se mide aquí, en la PC donde está; el precio lo pone la caja principal.
        servicios.AddSingleton<IMedidorPdf, MedidorPdfWindows>();
        var proveedor = servicios.BuildServiceProvider();
        Vista.Services = proveedor;
        IniciarAgente(configuracion, conexion, proveedor.GetRequiredService<Contingencia>());
    }

    private AgenteImpresion? _agente;

    /// <summary>
    /// Si esta PC tiene la WF-6590, mide cada impresión y la manda a la caja principal (con la bandeja: sin conexión, sube
    /// cuando vuelva). Queda a nombre de quien atiende la caja.
    /// </summary>
    private void IniciarAgente(ConfiguracionCaja configuracion, ConexionCaja conexion, Contingencia contingencia)
    {
        if (string.IsNullOrWhiteSpace(configuracion.ColaImpresion))
            return;
        _agente = new AgenteImpresion(configuracion.ColaImpresion, trabajo =>
        {
            if (conexion.CajaId is not { } caja)
                throw new InvalidOperationException("La caja todavía no está dada de alta.");
            contingencia.EncolarImpresion(trabajo with { CajaId = caja, AtendioId = conexion.UsuarioEnCaja });
            return Task.CompletedTask;
        }, configuracion.QuitarImpresos);
        _agente.Aviso += aviso => System.Diagnostics.Trace.TraceInformation(aviso);
        _agente.Iniciar();
        Closed += async (_, _) => await _agente.DisposeAsync();
    }
}
