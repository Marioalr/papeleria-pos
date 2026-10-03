using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Caja;

/// <summary>Configuración de esta PC (caja.json, junto al programa).</summary>
/// <param name="Impresora">"tcp://ip:9100" (impresora de red), el nombre de una impresora de Windows, o vacío para ver el ticket en pantalla.</param>
public sealed record ConfiguracionCaja(string Servicio, Guid? CajaId, string? Impresora)
{
    public static ConfiguracionCaja Leer()
    {
        var ruta = Path.Combine(AppContext.BaseDirectory, "caja.json");
        return JsonSerializer.Deserialize<ConfiguracionCaja>(File.ReadAllText(ruta), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("caja.json está vacío.");
    }
}

/// <summary>La ventana de la caja: muestra la interfaz compartida y manda el ticket a la impresora de tickets.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var configuracion = ConfiguracionCaja.Leer();
        var servicios = new ServiceCollection();
        servicios.AddWpfBlazorWebView();
        // "tcp://192.168.1.100:9100" imprime directo por la red; un nombre (como "Tickets") usa la impresora de Windows.
        var impresora = Impresoras.Crear(configuracion.Impresora, nombre => new ImpresoraWindows(nombre));
        servicios.AgregarInterfazCaja(new Uri(configuracion.Servicio), configuracion.CajaId, impresora);
        Vista.Services = servicios.BuildServiceProvider();
    }
}
