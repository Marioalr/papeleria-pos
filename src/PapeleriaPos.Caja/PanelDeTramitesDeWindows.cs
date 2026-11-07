using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PapeleriaPos.Interfaz;
using WinPdf = global::Windows.Data.Pdf;
using WinImagen = global::Windows.Graphics.Imaging;
using WinStreams = global::Windows.Storage.Streams;

namespace PapeleriaPos.Caja;

/// <summary>
/// El panel de los trámites en la ventana de Windows (docs/tramites.md, «El diseño recomendado»; 2026-10-31): lo que solo Windows
/// puede hacer, y nada más. Las decisiones (el folio, el nombre neutro, revisar el PDF, en qué impresora, cobrar una vez, borrar, el
/// registro) son de <see cref="TramiteEnCurso"/>, que se prueba en Linux.
/// <list type="bullet">
/// <item><b>La página oficial</b> en un WebView2 aparte, encima del lugar del ticket (<see cref="Colocar"/>), con el mismo navegador de la
/// caja (su carpeta de datos, para no levantar otro en la caja 2) y <b>otro perfil, InPrivate</b>: lo que guarda vive en memoria y no se
/// comparte con la caja ni con Edge. Sin autollenado ni contraseñas, sin herramientas ni atajos del navegador (Ctrl+P o Ctrl+S
/// imprimirían o guardarían la página con los datos del cliente), sin más permisos que el de varias descargas, y con el menú del clic
/// derecho solo para cortar, copiar y pegar. F12 con el cursor ahí va a la caja; Esc, la segunda seguida (la primera es de la página).</item>
/// <item><b>Solo navega a donde dice la política</b> (los sitios del trámite y los marcos del reCAPTCHA); las ventanas nuevas que abra
/// la página se quedan en el panel o no se abren.</item>
/// <item><b>Atrapa lo que descarga</b> (DownloadStarting): lo guarda donde dice el trámite, con su nombre neutro, sin la ventanita de
/// descargas de Edge.</item>
/// <item><b>Imprime</b> con WPF, en un hilo aparte (la caja 2 no se congela), con el nombre de trabajo que se le da («T-0042 · CURP»): así
/// lo ve la cola de Windows y el agente de impresión, nunca con el nombre del archivo de la página ni con su título. Cada página a 200
/// puntos por pulgada, una a la vez.</item>
/// <item><b>Al cerrar</b> borra lo del perfil (cookies, caché, historial, descargas, lo escrito), suelta el WebView2 y quita del
/// portapapeles lo que se copió. Uno nuevo espera a que termine de cerrarse el de antes, hasta <see cref="TramiteEnCurso.EsperaDelCierre"/>:
/// después lo suelta como esté (es InPrivate: lo suyo se va con él) y lo anota.</item>
/// </list>
/// Lo que solo se ve en Windows o en la tienda (que atrape el PDF de la CURP, el nombre del trabajo en la cola, el captcha dentro del
/// panel, que el perfil quede limpio) está en la guía del fin de semana (docs/pruebas-en-tienda.md).
/// </summary>
/// <param name="ventana">La de la caja: es la dueña de los diálogos.</param>
/// <param name="capa">Donde va el WebView2 del trámite: encima de la página de la caja, en sus mismas coordenadas.</param>
/// <param name="paginaDeLaCaja">La página de la caja (su WebView2): su navegador es el mismo para los dos.</param>
/// <param name="zoomDeLaCaja">El zoom de la página de la caja: con él se pasan sus píxeles a los de WPF.</param>
/// <param name="volverALaCaja">Regresa el teclado a la página de la caja.</param>
/// <param name="colaDelAgente">La cola que vigila el agente de impresión (ColaImpresion): la única donde las copias de más llegan a F2.</param>
/// <param name="impresoraDeTickets">La de los tickets (Impresora en caja.json): no se ofrece para un trámite.</param>
public sealed class PanelDeTramitesDeWindows(
    Window ventana, Canvas capa, Func<CoreWebView2?> paginaDeLaCaja, Func<double> zoomDeLaCaja, Action volverALaCaja, string? colaDelAgente,
    string? impresoraDeTickets, RegistroDiario registro) : IPanelDeTramites
{
    /// <summary>El perfil del panel: aparte del de la caja (el de siempre) y del de Edge.</summary>
    public const string Perfil = "Tramites";

    /// <summary>
    /// A cuántos puntos por pulgada se dibuja cada página para imprimirla: de sobra para un documento de texto y su código QR, sin los ~34 MB
    /// por página de 300 (revisión de servicios/curp). Una carta a 200 son unos 15 MB, y se dibuja una a la vez.
    /// </summary>
    private const double PuntosPorPulgada = 200;

    /// <summary>Lo que se deja en el menú del clic derecho: editar lo que se escribe, nada más.</summary>
    private static readonly HashSet<string> DelMenu = new(StringComparer.OrdinalIgnoreCase) { "cut", "copy", "paste", "pasteAndMatchStyle", "selectAll", "undo", "redo" };

    private WebView2? _web;
    private PoliticaDeNavegacion? _politica;
    private IAtrapaDocumentos? _atrapa;
    private LugarEnPantalla _lugar;
    private string? _copiado;

    /// <summary>El cierre del de antes (borrar lo del perfil): abrir otro lo espera, para no limpiar el perfil mientras carga el nuevo.</summary>
    private Task _cerrando = Task.CompletedTask;

    /// <summary>El que se está cerrando y desde cuándo: si tarda de más, abrir otro lo suelta.</summary>
    private WebView2? _cerrandose;
    private DateTime _cerrandoseDesde;

    /// <summary>La carpeta Descargas (FOLDERID_Downloads): la de Windows, aunque se haya movido a otro disco.</summary>
    private static readonly Guid Descargas = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public string? ColaDelAgente => string.IsNullOrWhiteSpace(colaDelAgente) ? null : colaDelAgente.Trim();

    public async Task AbrirAsync(Uri direccion, PoliticaDeNavegacion politica, IAtrapaDocumentos atrapa)
    {
        var cierre = CerrarAsync();
        // El de antes, hasta el límite (contado desde que empezó a cerrarse): si no termina (ClearBrowsingDataAsync que no regresa), se suelta.
        var falta = TramiteEnCurso.EsperaDelCierre - (DateTime.UtcNow - _cerrandoseDesde);
        if (!cierre.IsCompleted && await Task.WhenAny(cierre, Task.Delay(falta > TimeSpan.Zero ? falta : TimeSpan.Zero)) != cierre)
            SoltarElDeAntes();
        else if (cierre.IsFaulted)
            registro.AnotarSinRepetir("Trámites: el panel de antes no se cerró bien", cierre.Exception!.InnerException?.GetType().Name ?? "");
        var entorno = paginaDeLaCaja()?.Environment ?? throw new InvalidOperationException("La página de la caja todavía no está lista.");
        (_politica, _atrapa) = (politica, atrapa);
        var web = new WebView2();
        _web = web;
        capa.Children.Add(web);
        try
        {
            await ArrancarAsync(web, entorno, direccion);
        }
        catch (Exception) when (_web == web)
        {
            // Que no se quede encima del ticket ni guardando el trámite: la caja dice «no abrió» y ofrece Reintentar o Edge. Sin esperarlo.
            _ = CerrarAsync();
            throw;
        }
    }

    private async Task ArrancarAsync(WebView2 web, CoreWebView2Environment entorno, Uri direccion)
    {
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
        pagina.PermissionRequested += AlPedirPermiso;
        pagina.ContextMenuRequested += AlPedirMenu;
        // Esc y F12 con el cursor en la página oficial (le llegan a WPF como teclas «aceleradoras»): a la caja, como si se oprimieran ahí.
        web.PreviewKeyDown += AlOprimirEnLaPagina;
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
            // Si abre, abre: un PDF con permisos (sin clave para verlo) también (Control Print los rechazaba, 2026-10-25). Desde una copia en
            // memoria: el archivo no se queda abierto y se puede borrar al terminar.
            var documento = await AbrirPdfAsync(archivo);
            return documento.PageCount > 0 ? (int)documento.PageCount : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static async Task<WinPdf.PdfDocument> AbrirPdfAsync(string archivo)
    {
        var memoria = new MemoryStream(await File.ReadAllBytesAsync(archivo), writable: false);
        return await WinPdf.PdfDocument.LoadFromStreamAsync(memoria.AsRandomAccessStream());
    }

    public Task MostrarDocumentoAsync(string archivo)
    {
        _web?.CoreWebView2?.Navigate(new Uri(archivo).AbsoluteUri);
        // Enter imprime: el teclado a la caja.
        volverALaCaja();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Las colas de esta PC (solo las suyas: las conexiones a las de otra PC ni se piden, así no se espera a una PC apagada), sin la de los
    /// tickets. Cuáles sirven para un trámite lo decide <see cref="ImpresorasParaTramites"/>.
    /// </summary>
    public IReadOnlyList<ColaDeLaPc> Colas()
    {
        var colas = new List<ColaDeLaPc>();
        // De qué clase es el puerto de cada una (TCP/IP, WSD, compartida, USB…), como lo lee el estado de la red: con eso se ve si imprime
        // aquí o en otra PC (segunda revisión).
        var puertos = new PuertosDeImpresorasWindows();
        try
        {
            using var servidor = new LocalPrintServer();
            foreach (var cola in servidor.GetPrintQueues([EnumeratedPrintQueueTypes.Local]))
            {
                using (cola)
                {
                    if (string.Equals(cola.FullName, impresoraDeTickets?.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;
                    var puerto = Leer(() => cola.QueuePort?.Name);
                    TipoDePuerto? tipo;
                    try
                    {
                        tipo = puertos.DeLaImpresora(cola.FullName)?.Tipo;
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                        tipo = null;
                    }
                    colas.Add(new ColaDeLaPc(cola.FullName, puerto, Leer(() => cola.QueueDriver?.Name), Tipo: tipo));
                }
            }
        }
        catch (Exception ex) when (ex is PrintSystemException or Win32Exception or InvalidOperationException)
        {
            registro.AnotarSinRepetir("Trámites: no se leyeron las impresoras", ex.Message);
        }
        return colas;

        static string? Leer(Func<string?> que)
        {
            try
            {
                return que();
            }
            catch (Exception ex) when (ex is PrintSystemException or Win32Exception or InvalidOperationException)
            {
                return null;
            }
        }
    }

    public async Task ImprimirAsync(string archivo, string impresora, string nombreDelTrabajo, int copias)
    {
        // Las páginas, ya dibujadas en PNG (chico): el hilo de la impresión las abre una a la vez.
        var paginas = await PaginasEnPngAsync(archivo);
        await EnUnHiloAparteAsync(() =>
        {
            using var servidor = new LocalPrintServer();
            using var cola = servidor.GetPrintQueue(impresora);
            var ticket = (cola.UserPrintTicket ?? cola.DefaultPrintTicket).Clone();
            ticket.CopyCount = Math.Max(1, copias);
            // El nombre del trabajo es la descripción: así sale en la cola de Windows y así lo lee el agente de impresión.
            new PrintDialog { PrintQueue = cola, PrintTicket = ticket }.PrintDocument(new PaginasEnPng(paginas), nombreDelTrabajo);
        });
    }

    /// <summary>Cada página del PDF (hasta <see cref="TramiteEnCurso.PaginasMaximas"/>) en PNG, con su tamaño en DIP, dibujada por Windows.</summary>
    private static async Task<List<(byte[] Png, Size Tamano)>> PaginasEnPngAsync(string archivo)
    {
        var pdf = await AbrirPdfAsync(archivo);
        var paginas = new List<(byte[], Size)>();
        for (uint i = 0; i < Math.Min(pdf.PageCount, (uint)TramiteEnCurso.PaginasMaximas); i++)
        {
            using var pagina = pdf.GetPage(i);
            var tamano = new Size(pagina.Size.Width, pagina.Size.Height);
            using var salida = new WinStreams.InMemoryRandomAccessStream();
            await pagina.RenderToStreamAsync(salida, new WinPdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(tamano.Width * PuntosPorPulgada / 96)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(tamano.Height * PuntosPorPulgada / 96)),
                BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255),
                BitmapEncoderId = WinImagen.BitmapEncoder.PngEncoderId,
            });
            salida.Seek(0);
            using var png = new MemoryStream();
            await salida.AsStreamForRead().CopyToAsync(png);
            paginas.Add((png.ToArray(), tamano));
        }
        return paginas;
    }

    /// <summary>
    /// La impresión de WPF, en un hilo suyo (STA, como lo pide WPF): mientras se manda a la cola, la caja sigue respondiendo (revisión de
    /// servicios/curp: antes se congelaba la caja 2 con PrintDocument en el hilo de la ventana).
    /// </summary>
    private static Task EnUnHiloAparteAsync(Action imprimir)
    {
        var listo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hilo = new Thread(() =>
        {
            try
            {
                imprimir();
                listo.SetResult();
            }
            catch (Exception ex)
            {
                listo.SetException(ex);
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        { IsBackground = true, Name = "Imprimir un trámite" };
        hilo.SetApartmentState(ApartmentState.STA);
        hilo.Start();
        return listo.Task;
    }

    /// <summary>Las páginas para imprimir: cada una se abre de su PNG cuando la pide la impresión, y se suelta después.</summary>
    private sealed class PaginasEnPng(IReadOnlyList<(byte[] Png, Size Tamano)> paginas) : DocumentPaginator
    {
        public override DocumentPage GetPage(int pageNumber)
        {
            var (png, tamano) = paginas[pageNumber];
            var imagen = new BitmapImage();
            imagen.BeginInit();
            imagen.CacheOption = BitmapCacheOption.OnLoad;
            imagen.StreamSource = new MemoryStream(png, writable: false);
            imagen.EndInit();
            imagen.Freeze();
            var dibujo = new DrawingVisual();
            using (var lienzo = dibujo.RenderOpen())
                lienzo.DrawImage(imagen, new Rect(tamano));
            return new DocumentPage(dibujo, tamano, new Rect(tamano), new Rect(tamano));
        }

        public override bool IsPageCountValid => true;

        public override int PageCount => paginas.Count;

        public override Size PageSize
        {
            get => paginas.Count > 0 ? paginas[0].Tamano : new Size(816, 1056);
            set { }
        }

        public override IDocumentPaginatorSource? Source => null;
    }

    public Task<GuardadoEnUsb> GuardarEnUsbAsync(string archivo, string nombre)
    {
        // Directo en la memoria, sin diálogo: así nunca queda en una carpeta de la PC (revisión de servicios/curp). Dice en cuál (su nombre y
        // su letra, segunda revisión): con dos puestas, la primera que da Windows.
        var usb = DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Removable && d.IsReady);
        if (usb is null)
            return Task.FromResult(GuardadoEnUsb.SinMemoria);
        var letra = usb.Name.TrimEnd('\\');
        var unidad = Leer(() => usb.VolumeLabel) is { Length: > 0 } etiqueta ? $"{etiqueta.Trim()} ({letra})" : letra;
        try
        {
            var destino = ArchivosDeTramites.NombreLibre(usb.RootDirectory.FullName, nombre);
            File.Copy(archivo, destino, overwrite: false);
            return Task.FromResult(new GuardadoEnUsb(destino, Unidad: unidad));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            registro.AnotarError("Trámites: no se guardó en la memoria USB", ex);
            return Task.FromResult(new GuardadoEnUsb(null, $"No se pudo guardar en la memoria USB «{unidad}»: revisa que siga conectada y con espacio.", unidad));
        }

        static string? Leer(Func<string> que)
        {
            try
            {
                return que();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }
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

    public string? CarpetaDeDescargas
    {
        get
        {
            try
            {
                var resultado = SHGetKnownFolderPath(Descargas, 0, IntPtr.Zero, out var ruta);
                try
                {
                    if (resultado == 0 && Marshal.PtrToStringUni(ruta) is { Length: > 0 } carpeta)
                        return carpeta;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ruta);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
            var deSiempre = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(deSiempre) ? deSiempre : null;
        }
    }

    public Task<string?> ElegirPdfDescargadoAsync()
    {
        var descargas = CarpetaDeDescargas;
        var dialogo = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Elegir el PDF descargado en Edge",
            Filter = "PDF (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = false,
            // Que su nombre (lleva la CURP de la persona) no quede en los recientes de Windows.
            AddToRecent = false,
            InitialDirectory = descargas is not null && Directory.Exists(descargas) ? descargas : "",
        };
        var elegido = dialogo.ShowDialog(ventana) == true ? dialogo.FileName : null;
        volverALaCaja();
        return Task.FromResult(elegido);
    }

    public Task CerrarAsync()
    {
        var web = _web;
        (_web, _politica, _atrapa) = (null, null, null);
        QuitarDelPortapapeles();
        if (web is null)
            return _cerrando;
        // Se recuerda: abrir otro espera a que termine.
        (_cerrandose, _cerrandoseDesde) = (web, DateTime.UtcNow);
        return _cerrando = CerrarAsync(web);
    }

    private async Task CerrarAsync(WebView2 web)
    {
        web.PreviewKeyDown -= AlOprimirEnLaPagina;
        // Mientras se limpia ya no tapa el ticket ni se queda con el teclado: lo que se teclee o se escanee va a la caja.
        web.Visibility = Visibility.Hidden;
        volverALaCaja();
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
        // Ya se soltó (tardó de más y se abrió otro): nada que hacer.
        if (_cerrandose != web)
            return;
        _cerrandose = null;
        capa.Children.Remove(web);
        web.Dispose();
        // Si ya se abrió otro mientras tanto, el teclado se queda en él.
        if (_web is null)
            volverALaCaja();
    }

    /// <summary>
    /// El de antes no terminó de cerrarse a tiempo (segunda revisión: si ClearBrowsingDataAsync no regresara, /curp ya no abriría nada hasta
    /// reiniciar la caja): se quita y se suelta como esté. Es InPrivate: lo que guardó se va con él. Se anota.
    /// </summary>
    private void SoltarElDeAntes()
    {
        if (_cerrandose is not { } viejo)
            return;
        _cerrandose = null;
        registro.Anotar($"Trámites: el panel de antes no terminó de cerrarse en {TramiteEnCurso.EsperaDelCierre.TotalSeconds:0} s; se soltó para abrir otro.");
        capa.Children.Remove(viejo);
        try
        {
            viejo.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            registro.AnotarSinRepetir("Trámites: el panel de antes no se soltó bien", ex.GetType().Name);
        }
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

    private void AlOprimirEnLaPagina(object sender, KeyEventArgs e)
    {
        var tecla = e.Key switch
        {
            Key.Escape => "Escape",
            Key.F12 => "F12",
            _ => null,
        };
        if (tecla is null || _atrapa is not { } atrapa)
            return;
        // F12 siempre es de la caja; Esc, la segunda seguida (la primera sigue a la página: cierra sus listas). Lo decide el trámite.
        e.Handled = atrapa.Tecla(tecla);
    }

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
        // Ninguna ventana aparte. Una en blanco (window.open() sin dirección) tampoco cambia el panel: se perdería la página oficial. Lo de
        // un sitio del trámite se abre en el panel.
        e.Handled = true;
        if (string.IsNullOrEmpty(e.Uri) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return;
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
                break;
            default:
                _atrapa?.NoCargo(FallaDeLaPagina.NoContesta);
                break;
        }
        // El teclado a la caja: ahí Enter es «Reintentar».
        volverALaCaja();
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

    /// <summary>
    /// Ningún permiso (cámara, ubicación, avisos, portapapeles), salvo varias descargas: sin él, «Descargar pdf» otra vez (después de una
    /// descarga cortada o de un PDF que no abrió) podría no bajar nada. Lo que se descarga igual lo atrapa el trámite (solo un PDF).
    /// </summary>
    private void AlPedirPermiso(object? sender, CoreWebView2PermissionRequestedEventArgs e) =>
        e.State = e.PermissionKind == CoreWebView2PermissionKind.MultipleAutomaticDownloads ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;

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
