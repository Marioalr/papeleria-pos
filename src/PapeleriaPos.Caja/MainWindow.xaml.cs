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
/// ProgramData\PapeleriaPos\Caja con la llave cifrada por Windows. Esa carpeta la deja el instalador (administrador) con
/// escritura para los usuarios: la caja se usa desde una cuenta común de Windows y sirve igual para cualquier cuenta de la PC
/// (docs/plan.md, instalación y usuarios de Windows).
/// </summary>
/// <param name="Impresora">"tcp://ip:9100" (impresora de red), el nombre de una impresora de Windows, o vacío para ver el ticket en pantalla.</param>
/// <param name="LlaveProtegida">La llave de la caja cifrada para esta PC: copiada a otra no sirve.</param>
/// <param name="ColaImpresion">La cola de Windows de la WF-6590 que vigila esta PC (vacío: no vigila ninguna). Mientras
/// Control Print siga en uso, se prueba con su cola de laboratorio.</param>
/// <param name="QuitarImpresos">Quitar de la cola lo ya impreso y medido (la cola conserva los documentos impresos).</param>
/// <param name="Prueba">La caja de la prueba con una persona real: arranca su propio servicio y no pide alta.</param>
/// <param name="ServicioLocal">En la prueba: el servicio, relativo a la carpeta de la caja.</param>
/// <param name="BaseInicial">En la prueba: la base con que se instaló (de solo lectura, en Archivos de programa).</param>
/// <param name="CarpetaDatos">En la prueba: dónde va la base de trabajo y las respuestas (admite %ProgramData%).</param>
public sealed record ConfiguracionCaja(
    string Servicio, Guid? CajaId, string? Impresora, string? LlaveProtegida = null, string? ColaImpresion = null, bool QuitarImpresos = true,
    bool Prueba = false, string? ServicioLocal = null, string? BaseInicial = null, string? CarpetaDatos = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private static string Raiz => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PapeleriaPos");

    private static string Carpeta => Path.Combine(Raiz, "Caja");

    private static string Guardada => Path.Combine(Carpeta, "caja.json");

    /// <summary>Donde la guardaban las versiones anteriores (antes de la carpeta con permisos para los usuarios).</summary>
    private static string GuardadaAntes => Path.Combine(Raiz, "caja.json");

    /// <summary>La base local de la caja: copia del catálogo, bandeja de salida y diario del turno.</summary>
    public static string BaseLocal => Path.Combine(Carpeta, "caja-local.db");

    public static ConfiguracionCaja Leer()
    {
        // La de la prueba manda sobre la guardada: en la misma PC puede estar la caja de verdad.
        var junto = Path.Combine(AppContext.BaseDirectory, "caja.json");
        if (Deserializar(junto) is { Prueba: true } prueba)
            return prueba;
        var ruta = File.Exists(Guardada) ? Guardada : File.Exists(GuardadaAntes) ? GuardadaAntes : junto;
        return Deserializar(ruta) ?? throw new InvalidOperationException($"{ruta} está vacío.");
    }

    private static ConfiguracionCaja? Deserializar(string ruta) =>
        File.Exists(ruta) ? JsonSerializer.Deserialize<ConfiguracionCaja>(File.ReadAllText(ruta), Json) : null;

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
        // La prueba con una persona real: su propio servicio en esta PC, sin alta. También trabaja sin conexión (con su
        // copia y su bandeja junto a los datos de la prueba), para probar el crédito sin conexión con «Simular sin conexión».
        var prueba = configuracion.Prueba ? ServicioDePrueba.Iniciar(configuracion) : null;
        if (prueba is not null)
            Closed += (_, _) => prueba.Dispose();
        var conexion = new ConexionCaja
        {
            Servicio = new Uri(configuracion.Servicio),
            CajaId = prueba is null ? configuracion.CajaId : null,
            Llave = prueba is null ? configuracion.Llave() : null,
            PideAlta = prueba is null,
            Listo = prueba?.Listo ?? Task.CompletedTask,
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
        servicios.AgregarInterfazCaja(conexion, impresora,
            prueba is null ? ConfiguracionCaja.BaseLocal : Path.Combine(ServicioDePrueba.CarpetaDatos(configuracion), "caja-local.db"));
        // Cotizar: el PDF se mide aquí, en la PC donde está; el precio lo pone la caja principal.
        servicios.AddSingleton<IMedidorPdf, MedidorPdfWindows>();
        // La prueba se actualiza desde Configuración con los instaladores que arma GitHub.
        if (prueba is not null)
            servicios.AgregarActualizacionPrueba(new EquipoPrueba(ServicioDePrueba.CarpetaDatos(configuracion)), GitHubPrueba.RepositorioPorOmision);
        var proveedor = servicios.BuildServiceProvider();
        Vista.Services = proveedor;
        if (proveedor.GetService<Contingencia>() is { } contingencia)
        {
            IniciarAgente(configuracion, conexion, contingencia);
            RecordarElCorteAlCerrar(contingencia);
        }
    }

    /// <summary>
    /// El piloto con eleventa (decisión del dueño, 2026-10-12): al cerrar la ventana, la caja recuerda que el corte se hace
    /// en eleventa. Solo avisa: con «Aceptar» la ventana se cierra como siempre (el aviso no cambia qué se guarda ni cómo).
    /// Lo sabe por la contingencia, también sin conexión. Al apagar Windows no sale (WPF no pasa por Closing). Solo para
    /// convivir con eleventa: se quita al dejarlo.
    /// </summary>
    private void RecordarElCorteAlCerrar(Contingencia contingencia) =>
        Closing += (_, _) =>
        {
            if (contingencia.PilotoEleventa)
                MessageBox.Show(this, PilotoConEleventa.CorteEnEleventa, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        };

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
