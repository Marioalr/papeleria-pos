using System.Windows;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;

namespace PapeleriaPos.Caja;

/// <summary>
/// La ventanita de «Juntar imágenes para Word» (docs/propuestas/juntar-imagenes-interfaz.md): una ventana propia, encima de todo y al
/// centro, con la página <see cref="JuntarImagenes"/> (los mismos componentes y colores de la caja). Usa la misma carpeta de WebView2 que
/// la caja, así que no arranca otro navegador. Se abre al terminar de juntar y se cierra sola al copiar, abrir en Word, juntar más o
/// cancelar.
/// </summary>
internal sealed class VentanitaJuntarImagenes : Window
{
    private readonly BlazorWebView _vista;

    public VentanitaJuntarImagenes(IJuntarImagenes juntar, RegistroDiario registro)
    {
        Title = "Juntar imágenes";
        Width = 840;
        Height = 600;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        var servicios = new ServiceCollection();
        servicios.AddWpfBlazorWebView();
        servicios.AddSingleton(juntar);
        servicios.AddSingleton(registro);
        servicios.AddScoped<ModuloJuntar>();
        _vista = new BlazorWebView
        {
            HostPage = @"wwwroot\juntar.html",
            Services = servicios.BuildServiceProvider(),
        };
        _vista.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(JuntarImagenes) });
        _vista.BlazorWebViewInitialized += AlIniciar;
        Content = _vista;
        // Al frente aunque WhatsApp lo esté: se abre con el atajo, desde otro programa.
        Loaded += (_, _) =>
        {
            Activate();
            _vista.WebView?.Focus();
        };
    }

    private void AlIniciar(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        // Sin las teclas del navegador (Ctrl+R, Ctrl+F…), como en la caja; las de la ventanita las atrapa juntar.js.
        e.WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        e.WebView.Focus();
    }
}
