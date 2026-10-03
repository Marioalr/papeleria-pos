using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Caja;

/// <summary>Configuración de esta PC (caja.json, junto al programa).</summary>
public sealed record ConfiguracionCaja(string Servicio, Guid? CajaId, string Impresora)
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
        servicios.AgregarInterfazCaja(new Uri(configuracion.Servicio), configuracion.CajaId, new ImpresoraWindows(configuracion.Impresora));
        Vista.Services = servicios.BuildServiceProvider();
    }
}
