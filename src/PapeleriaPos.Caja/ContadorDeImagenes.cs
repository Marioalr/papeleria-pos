using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PapeleriaPos.Caja;

/// <summary>
/// El contador de «Juntar imágenes para Word» (docs/propuestas/juntar-imagenes-interfaz.md, «El contador»): una tira chica arriba a la
/// derecha, justo debajo del marco de la ventana de al frente (el dueño, 2026-11-28: ahí cubre menos de WhatsApp), siempre encima y sin
/// tomar el foco: WhatsApp sigue al frente y se sigue copiando. Sin sonido. Enseña la miniatura de la última imagen con un destello y
/// el número. Un clic hace lo mismo que el atajo; se puede arrastrar, y se recuerda dónde quedó. Es WPF puro (sin WebView2): no pesa.
/// </summary>
internal sealed class ContadorDeImagenes : Window
{
    public enum Color
    {
        Azul,
        Ambar,
        Rojo,
        Verde,
    }

    private static readonly Dictionary<Color, SolidColorBrush> Fondos = new()
    {
        [Color.Azul] = Pincel(0x23, 0x43, 0x9b),
        [Color.Ambar] = Pincel(0x8a, 0x53, 0x00),
        [Color.Rojo] = Pincel(0xb3, 0x26, 0x1e),
        [Color.Verde] = Pincel(0x0f, 0x7a, 0x4a),
    };

    private readonly Border _marco;
    private readonly Border _mini;
    private readonly Image _imagen;
    private readonly TextBlock _titulo;
    private readonly TextBlock _sub;
    private readonly DispatcherTimer _ocultar;
    private readonly Action _alTocar;
    private readonly Action<Point> _alMover;

    /// <param name="alTocar">Un clic (sin arrastrar): lo mismo que el atajo.</param>
    /// <param name="alMover">Se arrastró: dónde quedó, para recordarlo.</param>
    /// <param name="lugar">Donde quedó la vez pasada; sin él, arriba a la derecha.</param>
    public ContadorDeImagenes(Action alTocar, Action<Point> alMover, Point? lugar)
    {
        _alTocar = alTocar;
        _alMover = alMover;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Juntar imágenes";

        _imagen = new Image { Stretch = Stretch.UniformToFill };
        _mini = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2e, 0xff, 0xff, 0xff)),
            ClipToBounds = true,
            Child = _imagen,
            Margin = new Thickness(0, 0, 10, 0),
        };
        _titulo = new TextBlock { FontSize = 14.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
        _sub = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xd9, 0xff, 0xff, 0xff)) };
        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _titulo, _sub } };
        var fila = new DockPanel { Children = { _mini, textos } };
        DockPanel.SetDock(_mini, Dock.Left);
        _marco = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 6, 14, 6),
            MinWidth = 250,
            MaxWidth = 380,
            Child = fila,
            Cursor = Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.3 },
            Margin = new Thickness(8),
        };
        Content = _marco;
        FontFamily = new FontFamily("Segoe UI");

        _ocultar = new DispatcherTimer();
        _ocultar.Tick += (_, _) =>
        {
            _ocultar.Stop();
            AlVencerse?.Invoke();
        };

        Loaded += (_, _) => Colocar(lugar);
        SourceInitialized += (_, _) => NoTomarElFoco();
        MouseLeftButtonDown += AlTocar;
    }

    /// <summary>Lo que pasa cuando termina un aviso con plazo (regresar a «Juntando…» o esconderse): lo decide quien lo enseñó.</summary>
    public Action? AlVencerse { get; set; }

    /// <summary>
    /// Enseña un aviso. Con <paramref name="dura"/>, al terminar llama a <see cref="AlVencerse"/>. Con <paramref name="miniatura"/> (un JPEG),
    /// la enseña; con <paramref name="destello"/>, destella (entró una imagen).
    /// </summary>
    public void Mostrar(Color color, string titulo, string sub, byte[]? miniatura = null, TimeSpan? dura = null, bool destello = false)
    {
        _marco.Background = Fondos[color];
        _titulo.Text = titulo;
        _sub.Text = sub;
        _sub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
        if (miniatura is not null)
            _imagen.Source = Leer(miniatura);
        _mini.Visibility = _imagen.Source is null && miniatura is null && color != Color.Azul ? Visibility.Collapsed : Visibility.Visible;
        if (destello)
            Destellar();
        _ocultar.Stop();
        if (dura is { } d)
        {
            _ocultar.Interval = d;
            _ocultar.Start();
        }
        if (!IsVisible)
            Show();
        AutomationAnunciar();
    }

    /// <summary>Quita la miniatura (empieza otra junta).</summary>
    public void SinMiniatura() => _imagen.Source = null;

    public void Esconder()
    {
        _ocultar.Stop();
        Hide();
    }

    private void Destellar()
    {
        var animacion = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(450));
        _mini.BeginAnimation(OpacityProperty, animacion);
    }

    /// <summary>Para el lector de pantalla: lo que dice el contador cuando cambia.</summary>
    private void AutomationAnunciar() =>
        System.Windows.Automation.AutomationProperties.SetName(_marco, string.IsNullOrEmpty(_sub.Text) ? _titulo.Text : $"{_titulo.Text}. {_sub.Text}");

    private static BitmapImage? Leer(byte[] jpeg)
    {
        try
        {
            var imagen = new BitmapImage();
            imagen.BeginInit();
            imagen.CacheOption = BitmapCacheOption.OnLoad;
            imagen.StreamSource = new MemoryStream(jpeg);
            imagen.EndInit();
            imagen.Freeze();
            return imagen;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Arriba a la derecha de la pantalla, justo debajo del marco de una ventana maximizada (la barra de título, donde están minimizar y
    /// cerrar). Si se arrastró antes, donde quedó, siempre que siga dentro de la pantalla.
    /// </summary>
    private void Colocar(Point? lugar)
    {
        var area = SystemParameters.WorkArea;
        if (lugar is { } p && p.X >= area.Left && p.Y >= area.Top && p.X + ActualWidth <= area.Right + 1 && p.Y + ActualHeight <= area.Bottom + 1)
        {
            (Left, Top) = (p.X, p.Y);
            return;
        }
        Left = area.Right - ActualWidth - 8;
        Top = area.Top + SystemParameters.CaptionHeight + SystemParameters.WindowResizeBorderThickness.Top + 6;
    }

    private void AlTocar(object sender, MouseButtonEventArgs e)
    {
        var antes = new Point(Left, Top);
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        var despues = new Point(Left, Top);
        if (Math.Abs(despues.X - antes.X) < 3 && Math.Abs(despues.Y - antes.Y) < 3)
        {
            (Left, Top) = (antes.X, antes.Y);
            _alTocar();
        }
        else
        {
            _alMover(despues);
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Que Windows no le dé el foco al enseñarla ni al tocarla, ni la ponga en Alt+Tab: WhatsApp se queda al frente.</summary>
    private void NoTomarElFoco()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private static SolidColorBrush Pincel(byte r, byte g, byte b)
    {
        var pincel = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        pincel.Freeze();
        return pincel;
    }
}
