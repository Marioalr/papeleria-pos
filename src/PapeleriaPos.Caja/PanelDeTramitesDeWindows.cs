using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PapeleriaPos.Interfaz;
using WinPdf = global::Windows.Data.Pdf;
using WinImagen = global::Windows.Graphics.Imaging;
using WinStorage = global::Windows.Storage;
using WinStreams = global::Windows.Storage.Streams;

namespace PapeleriaPos.Caja;

/// <summary>
/// El panel de los trámites en la ventana de Windows (docs/tramites.md, «El diseño recomendado»; 2026-10-31): lo que solo Windows
/// puede hacer, y nada más. Las decisiones (el folio, el nombre neutro, revisar el PDF, cobrar una vez, borrar, el registro) son de
/// <see cref="TramiteEnCurso"/>, que se prueba en Linux.
/// <list type="bullet">
/// <item><b>La página oficial</b> en un WebView2 aparte, encima del lugar del ticket (<see cref="Colocar"/>), con el mismo navegador de la
/// caja (su carpeta de datos, para no levantar otro en la caja 2) y <b>otro perfil, InPrivate</b>: lo que guarda vive en memoria y no se
/// comparte con la caja ni con Edge. Sin autollenado ni contraseñas, sin herramientas ni atajos del navegador (Ctrl+P o Ctrl+S
/// imprimirían o guardarían la página con los datos del cliente), sin permisos (cámara, ubicación, avisos) y con el menú del clic
/// derecho solo para cortar, copiar y pegar.</item>
/// <item><b>Solo navega a donde dice la política</b> (los sitios del trámite y los marcos del reCAPTCHA); las ventanas nuevas que abra
/// la página se quedan en el panel o no se abren.</item>
/// <item><b>Atrapa lo que descarga</b> (DownloadStarting): lo guarda donde dice el trámite, con su nombre neutro, sin la ventanita de
/// descargas de Edge.</item>
/// <item><b>Imprime</b> con WPF, con el nombre de trabajo que se le da («T-0042 · CURP»): así lo ve la cola de Windows y el agente de
/// impresión, nunca con el nombre del archivo de la página ni con su título.</item>
/// <item><b>Al cerrar</b> borra lo del perfil (cookies, caché, historial, descargas, lo escrito), suelta el WebView2 y quita del
/// portapapeles lo que se copió.</item>
/// </list>
/// Lo que solo se ve en Windows o en la tienda (que atrape el PDF de la CURP, el nombre del trabajo en la cola, el captcha dentro del
/// panel, que el perfil quede limpio) está en la guía del fin de semana (docs/pruebas-en-tienda.md).
/// </summary>
/// <param name="ventana">La de la caja: es la dueña de los diálogos.</param>
/// <param name="capa">Donde va el WebView2 del trámite: encima de la página de la caja, en sus mismas coordenadas.</param>
/// <param name="paginaDeLaCaja">La página de la caja (su WebView2): su navegador es el mismo para los dos.</param>
/// <param name="zoomDeLaCaja">El zoom de la página de la caja: con él se pasan sus píxeles a los de WPF.</param>
/// <param name="volverALaCaja">Regresa el teclado a la página de la caja.</param>
/// <param name="colaDelAgente">La cola que vigila el agente de impresión (ColaImpresion): es la que se ofrece primero.</param>
public sealed class PanelDeTramitesDeWindows(
    Window ventana, Canvas capa, Func<CoreWebView2?> paginaDeLaCaja, Func<double> zoomDeLaCaja, Action volverALaCaja, string? colaDelAgente,
    RegistroDiario registro) : IPanelDeTramites
{
    /// <summary>El perfil del panel: aparte del de la caja (el de siempre) y del de Edge.</summary>
    public const string Perfil = "Tramites";

    /// <summary>A cuántos puntos por pulgada se dibuja cada página para imprimirla.</summary>
    private const double PuntosPorPulgada = 300;

    /// <summary>Lo que se deja en el menú del clic derecho: editar lo que se escribe, nada más.</summary>
    private static readonly HashSet<string> DelMenu = new(StringComparer.OrdinalIgnoreCase) { "cut", "copy", "paste", "pasteAndMatchStyle", "selectAll", "undo", "redo" };

    private WebView2? _web;
    private PoliticaDeNavegacion? _politica;
    private IAtrapaDocumentos? _atrapa;
    private LugarEnPantalla _lugar;
    private string? _copiado;

    public async Task AbrirAsync(Uri direccion, PoliticaDeNavegacion politica, IAtrapaDocumentos atrapa)
    {
        await CerrarAsync();
        var entorno = paginaDeLaCaja()?.Environment ?? throw new InvalidOperationException("La página de la caja todavía no está lista.");
        (_politica, _atrapa) = (politica, atrapa);
        var web = new WebView2();
        _web = web;
        capa.Children.Add(web);
        Colocar(_lugar.SeVe ? _lugar : LugarDeRespaldo());
        var opciones = entorno.CreateCoreWebView2ControllerOptions();
        opciones.ProfileName = Perfil;
        opciones.IsInPrivateModeEnabled = true;
        await web.EnsureCoreWebView2Async(entorno, opciones);
        if (_web != web)
            return;
        var pagina = web.CoreWebView2;
        var ajustes = pagina.Settings;
        ajustes.IsGeneralAutofillEnabled = false;
        ajustes.IsPasswordAutosaveEnabled = false;
        ajustes.AreDevToolsEnabled = false;
        ajustes.AreBrowserAcceleratorKeysEnabled = false;
        ajustes.AreHostObjectsAllowed = false;
        ajustes.IsWebMessageEnabled = false;
        ajustes.IsStatusBarEnabled = false;
        ajustes.IsSwipeNavigationEnabled = false;
        // El visor del documento sin guardar ni imprimir: eso lo hacen los botones del POS (con el nombre del trabajo).
        ajustes.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.Save | CoreWebView2PdfToolbarItems.SaveAs | CoreWebView2PdfToolbarItems.Print
            | CoreWebView2PdfToolbarItems.MoreSettings | CoreWebView2PdfToolbarItems.FullScreen;
        pagina.Profile.IsGeneralAutofillEnabled = false;
        pagina.Profile.IsPasswordAutosaveEnabled = false;
        pagina.NavigationStarting += AlNavegar;
        pagina.FrameNavigationStarting += AlNavegarUnMarco;
        pagina.NewWindowRequested += AlPedirVentana;
        pagina.NavigationCompleted += AlCargar;
        pagina.DownloadStarting += AlDescargar;
        pagina.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        pagina.ContextMenuRequested += AlPedirMenu;
        pagina.Navigate(direccion.AbsoluteUri);
        web.Focus();
    }

    public void Colocar(LugarEnPantalla lugar)
    {
        if (lugar.SeVe)
            _lugar = lugar;
        if (_web is not { } web)
            return;
        if (!lugar.SeVe)
        {
            web.Visibility = Visibility.Hidden;
            return;
        }
        // Los píxeles de la página de la caja son los de WPF, salvo que alguien le haya cambiado el zoom.
        var zoom = zoomDeLaCaja() is var z and > 0 ? z : 1;
        Canvas.SetLeft(web, lugar.X * zoom);
        Canvas.SetTop(web, lugar.Y * zoom);
        web.Width = Math.Max(1, lugar.Ancho * zoom);
        web.Height = Math.Max(1, lugar.Alto * zoom);
        web.Visibility = Visibility.Visible;
    }

    /// <summary>Antes de que la página de la caja diga dónde está el ticket: todo menos el panel de la derecha (384) y las barras.</summary>
    private LugarEnPantalla LugarDeRespaldo() =>
        new(0, 110, Math.Max(400, capa.ActualWidth - 384), Math.Max(300, capa.ActualHeight - 110 - 48));

    public Task IrAAsync(Uri direccion)
    {
        _web?.CoreWebView2?.Navigate(direccion.AbsoluteUri);
        return Task.CompletedTask;
    }

    public async Task<bool> GuardarPaginaComoPdfAsync(string destino) =>
        _web?.CoreWebView2 is { } pagina && await pagina.PrintToPdfAsync(destino, null);

    public async Task<int?> PaginasAsync(string archivo)
    {
        try
        {
            // Si abre, abre: un PDF con permisos (sin clave para verlo) también (Control Print los rechazaba, 2026-10-25).
            var documento = await WinPdf.PdfDocument.LoadFromFileAsync(await WinStorage.StorageFile.GetFileFromPathAsync(archivo));
            return documento.PageCount > 0 ? (int)documento.PageCount : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public Task MostrarDocumentoAsync(string archivo)
    {
        _web?.CoreWebView2?.Navigate(new Uri(archivo).AbsoluteUri);
        // Enter imprime: el teclado a la caja.
        volverALaCaja();
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> Impresoras()
    {
        var nombres = new List<string>();
        try
        {
            using var servidor = new LocalPrintServer();
            foreach (var cola in servidor.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]))
            {
                using (cola)
                    nombres.Add(cola.FullName);
            }
        }
        catch (Exception ex) when (ex is PrintSystemException or Win32Exception or InvalidOperationException)
        {
            registro.AnotarSinRepetir("Trámites: no se leyeron las impresoras", ex.Message);
        }
        // Primero la que mide el agente: su trabajo se liga al trámite y no se cobra dos veces.
        if (!string.IsNullOrWhiteSpace(colaDelAgente))
        {
            nombres.RemoveAll(n => string.Equals(n, colaDelAgente, StringComparison.OrdinalIgnoreCase));
            nombres.Insert(0, colaDelAgente);
        }
        return nombres;
    }

    public async Task ImprimirAsync(string archivo, string impresora, string nombreDelTrabajo, int copias)
    {
        var documento = new FixedDocument();
        foreach (var (imagen, ancho, alto) in await PaginasParaImprimirAsync(archivo))
        {
            var hoja = new FixedPage { Width = ancho, Height = alto };
            hoja.Children.Add(new Image { Source = imagen, Width = ancho, Height = alto, Stretch = Stretch.Fill });
            var contenido = new PageContent();
            ((IAddChild)contenido).AddChild(hoja);
            documento.Pages.Add(contenido);
        }
        using var cola = Cola(impresora);
        var ticket = (cola.UserPrintTicket ?? cola.DefaultPrintTicket).Clone();
        ticket.CopyCount = Math.Max(1, copias);
        // El nombre del trabajo es la descripción: así sale en la cola de Windows y así lo lee el agente de impresión.
        new PrintDialog { PrintQueue = cola, PrintTicket = ticket }.PrintDocument(documento.DocumentPaginator, nombreDelTrabajo);
    }

    /// <summary>La cola por su nombre: una de esta PC («EPSON WF-6590 Series») o una conexión («\\PRINCIPAL\Tickets»).</summary>
    private static PrintQueue Cola(string nombre)
    {
        if (nombre.StartsWith(@"\\", StringComparison.Ordinal) && nombre.LastIndexOf('\\') is var corte and > 2)
            return new PrintServer(nombre[..corte]).GetPrintQueue(nombre[(corte + 1)..]);
        return new LocalPrintServer().GetPrintQueue(nombre);
    }

    /// <summary>Cada página del PDF como imagen a 300 dpi (con el motor de PDF de Windows, como al cotizar), con su tamaño en DIP.</summary>
    private static async Task<List<(BitmapSource Imagen, double Ancho, double Alto)>> PaginasParaImprimirAsync(string archivo)
    {
        var pdf = await WinPdf.PdfDocument.LoadFromFileAsync(await WinStorage.StorageFile.GetFileFromPathAsync(archivo));
        var paginas = new List<(BitmapSource, double, double)>();
        for (uint i = 0; i < Math.Min(pdf.PageCount, 50u); i++)
        {
            using var pagina = pdf.GetPage(i);
            double ancho = pagina.Size.Width, alto = pagina.Size.Height;
            using var salida = new WinStreams.InMemoryRandomAccessStream();
            await pagina.RenderToStreamAsync(salida, new WinPdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(ancho * PuntosPorPulgada / 96)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(alto * PuntosPorPulgada / 96)),
                BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255),
                BitmapEncoderId = WinImagen.BitmapEncoder.BmpEncoderId,
            });
            salida.Seek(0);
            var decodificador = await WinImagen.BitmapDecoder.CreateAsync(salida);
            var datos = await decodificador.GetPixelDataAsync(WinImagen.BitmapPixelFormat.Bgra8, WinImagen.BitmapAlphaMode.Ignore,
                new WinImagen.BitmapTransform(), WinImagen.ExifOrientationMode.IgnoreExifOrientation, WinImagen.ColorManagementMode.DoNotColorManage);
            var (largo, alto2) = ((int)decodificador.PixelWidth, (int)decodificador.PixelHeight);
            var imagen = BitmapSource.Create(largo, alto2, PuntosPorPulgada, PuntosPorPulgada, PixelFormats.Bgr32, null, datos.DetachPixelData(), largo * 4);
            imagen.Freeze();
            paginas.Add((imagen, ancho, alto));
        }
        return paginas;
    }

    public Task<bool> GuardarEnUsbAsync(string archivo, string nombre)
    {
        var usb = DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Removable && d.IsReady);
        var dialogo = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar en la memoria USB",
            FileName = nombre,
            DefaultExt = ".pdf",
            Filter = "PDF (*.pdf)|*.pdf",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = usb?.RootDirectory.FullName ?? "",
        };
        var guardar = dialogo.ShowDialog(ventana) == true;
        if (guardar)
            File.Copy(archivo, dialogo.FileName, overwrite: true);
        volverALaCaja();
        return Task.FromResult(guardar);
    }

    public void CopiarArchivo(string archivo)
    {
        var datos = new DataObject();
        datos.SetFileDropList(new StringCollection { archivo });
        // Que no quede en el historial del portapapeles (Win+V), ni se suba a la nube, ni lo lean los programas que lo vigilan.
        datos.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        datos.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        datos.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(0)));
        Clipboard.SetDataObject(datos, copy: true);
        _copiado = archivo;
    }

    public void AbrirEnElNavegador(Uri direccion)
    {
        try
        {
            // InPrivate: sin historial ni autollenado, como se recomienda mientras no hay panel (docs/tramites.md, «Mientras tanto»).
            Process.Start(new ProcessStartInfo("msedge.exe", $"--inprivate {direccion.AbsoluteUri}") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            Process.Start(new ProcessStartInfo(direccion.AbsoluteUri) { UseShellExecute = true });
        }
    }

    public async Task CerrarAsync()
    {
        var web = _web;
        (_web, _politica, _atrapa) = (null, null, null);
        QuitarDelPortapapeles();
        if (web is null)
            return;
        try
        {
            if (web.CoreWebView2 is { } pagina)
                await pagina.Profile.ClearBrowsingDataAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Es InPrivate: lo que guardó se va con él de todos modos.
            registro.AnotarSinRepetir("Trámites: no se borró lo del perfil del panel", ex.GetType().Name);
        }
        capa.Children.Remove(web);
        web.Dispose();
        volverALaCaja();
    }

    /// <summary>Lo que se copió para WhatsApp, si sigue en el portapapeles: el archivo ya no va a estar.</summary>
    private void QuitarDelPortapapeles()
    {
        if (_copiado is not { } copiado)
            return;
        _copiado = null;
        try
        {
            if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().Any(a => string.Equals(a, copiado, StringComparison.OrdinalIgnoreCase)))
                Clipboard.Clear();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Otro programa tiene el portapapeles en ese momento: el archivo ya no existe, pegarlo no hace nada.
        }
    }

    // ── Lo que pasa en la página ──

    private void AlNavegar(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var a) && _politica?.Permite(a) == true)
            return;
        e.Cancel = true;
        _atrapa?.Bloqueado();
    }

    private void AlNavegarUnMarco(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Un marco de otro sitio (publicidad, medición) no carga, sin decir nada.
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var a) || _politica?.Permite(a, esMarco: true) != true)
            e.Cancel = true;
    }

    private void AlPedirVentana(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Ninguna ventana aparte: lo de un sitio del trámite se abre en el panel.
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var a) && _politica?.Permite(a) == true)
            _web?.CoreWebView2?.Navigate(e.Uri);
        else
            _atrapa?.Bloqueado();
    }

    private void AlCargar(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _atrapa?.Cargo();
            return;
        }
        switch (e.WebErrorStatus)
        {
            // Lo que se detuvo aquí (un sitio que no es del trámite) o se volvió descarga.
            case CoreWebView2WebErrorStatus.OperationCanceled:
                return;
            case CoreWebView2WebErrorStatus.HostNameNotResolved or CoreWebView2WebErrorStatus.Disconnected:
                _atrapa?.NoCargo(FallaDeLaPagina.SinInternet);
                return;
            default:
                _atrapa?.NoCargo(FallaDeLaPagina.NoContesta);
                return;
        }
    }

    private void AlDescargar(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // Del nombre que trae (CURP_ y la CURP de la persona) solo se toma su extensión.
        var destino = _atrapa?.DondeGuardar(Path.GetExtension(e.ResultFilePath), e.DownloadOperation.MimeType);
        if (destino is null)
        {
            e.Cancel = true;
            return;
        }
        e.ResultFilePath = destino;
        // Sin la ventanita de descargas de Edge.
        e.Handled = true;
        var atrapa = _atrapa;
        var operacion = e.DownloadOperation;
        operacion.StateChanged += (_, _) =>
        {
            switch (operacion.State)
            {
                case CoreWebView2DownloadState.Completed:
                    _ = DescargadoAsync(atrapa, destino);
                    break;
                case CoreWebView2DownloadState.Interrupted:
                    atrapa?.NoSeDescargo();
                    break;
            }
        };
    }

    private async Task DescargadoAsync(IAtrapaDocumentos? atrapa, string destino)
    {
        try
        {
            if (atrapa is not null)
                await atrapa.DescargadoAsync(destino);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            registro.AnotarSinRepetir("Trámites: no se revisó un documento", ex.GetType().Name);
        }
    }

    private void AlPedirMenu(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var menu = e.MenuItems;
        for (var i = menu.Count - 1; i >= 0; i--)
            if (!DelMenu.Contains(menu[i].Name))
                menu.RemoveAt(i);
        if (menu.Count == 0)
            e.Handled = true;
    }
}
