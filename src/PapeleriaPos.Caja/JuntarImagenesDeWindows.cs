using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Imagenes;

namespace PapeleriaPos.Caja;

/// <summary>
/// «Juntar imágenes para Word» en la caja de Windows (docs/propuestas/juntar-imagenes.md): <c>Ctrl+Alt+J</c> en cualquier programa empieza
/// a juntar; cada imagen que se copie (en la app de WhatsApp, en una página, varios archivos de una carpeta) entra sola y el contador la
/// cuenta; <c>Ctrl+Alt+J</c> otra vez abre la ventanita, que copia todas para Word con su tamaño o abre un documento nuevo en Word. Lo de
/// las imágenes lo lleva <see cref="JuntaDeImagenes"/>; aquí va lo de Windows: el atajo, el portapapeles, el contador y la ventanita.
/// <para>
/// Nada de lo juntado va a la base ni a la caja principal: vive en la memoria de la caja y se borra al cancelar, 15 minutos después de
/// copiarlo y al cerrar la caja. El documento de «Abrir en Word» se guarda en una carpeta temporal y se borra al abrir la caja. El
/// portapapeles que deja la caja no entra al historial de Windows. En el registro solo queda cuántas y de qué tamaño.
/// </para>
/// </summary>
internal sealed class JuntarImagenesDeWindows : IJuntarImagenes, IDisposable
{
    private const int IdDelAtajo = 0x4A4A; // «JJ»
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    private const uint VK_J = 0x4A;
    private const int WM_HOTKEY = 0x0312, WM_CLIPBOARDUPDATE = 0x031D;

    private readonly RegistroDiario _registro;
    private readonly string _archivoDeAjustes;
    private readonly JuntaDeImagenes _junta;
    private readonly Dispatcher _hilo;
    private readonly DispatcherTimer _reloj;
    private HwndSource? _mensajes;
    private bool _atajoRegistrado;
    private bool _escuchandoElPortapapeles;
    private ContadorDeImagenes? _contador;
    private VentanitaJuntarImagenes? _ventanita;
    private Point? _lugarDelContador;

    /// <summary>El número del portapapeles después de que la caja puso el bloque para Word: lo que llegue con ese número es suyo.</summary>
    private uint _loNuestro;

    private readonly Dictionary<int, Task<ImagenEnLaVentanita>> _vistas = [];

    public JuntarImagenesDeWindows(RegistroDiario registro, string carpetaDeLaCaja)
    {
        _registro = registro;
        _hilo = Dispatcher.CurrentDispatcher;
        _archivoDeAjustes = Path.Combine(carpetaDeLaCaja, "juntar-imagenes.json");
        var (ajustes, lugar) = LeerAjustes();
        _junta = new JuntaDeImagenes(ajustes);
        _lugarDelContador = lugar;
        _reloj = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => Revisar(), _hilo);
    }

    /// <summary>Donde se guardan los documentos de «Abrir en Word» hasta que la caja vuelve a abrir.</summary>
    public static string CarpetaTemporal => Path.Combine(Path.GetTempPath(), "PapeleriaPos-Juntar");

    /// <summary>Registra el atajo. Si otro programa ya lo tiene, se anota y se avisa una vez con el contador.</summary>
    public void Iniciar()
    {
        BorrarTemporales();
        _mensajes = new HwndSource(new HwndSourceParameters("PapeleriaPos.JuntarImagenes") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
        _mensajes.AddHook(AlRecibir);
        _atajoRegistrado = RegisterHotKey(_mensajes.Handle, IdDelAtajo, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_J);
        if (_atajoRegistrado)
            return;
        _registro.Anotar($"Juntar imágenes: Ctrl+Alt+J lo usa otro programa (error {Marshal.GetLastWin32Error()}); no se puede juntar con el atajo.");
        Contador().Mostrar(ContadorDeImagenes.Color.Rojo, "Ctrl+Alt+J lo usa otro programa", "Juntar imágenes no funciona con el atajo",
            dura: TimeSpan.FromSeconds(10));
        Contador().AlVencerse = () => Contador().Esconder();
    }

    // ── IJuntarImagenes (la ventanita) ──

    public IReadOnlyList<ImagenEnLaVentanita> Imagenes { get; private set; } = [];

    public int Repetidas => _junta.Repetidas;

    public IReadOnlyList<ImagenOrdenada>? Orden => _junta.Orden;

    public AjustesDeJuntarImagenes Ajustes => _junta.Ajustes;

    public bool HayWord => RutaDeWord() is not null;

    public Task CopiarAsync(IReadOnlyList<ImagenOrdenada> orden, AjustesDeJuntarImagenes ajustes) => _hilo.InvokeAsync(() =>
    {
        if (_junta.ParaWord(orden, ajustes, conHoja: false, DateTimeOffset.Now) is not { } rtf)
            return;
        GuardarAjustes();
        var datos = new DataObject();
        datos.SetData(DataFormats.Rtf, rtf);
        SinHistorial(datos);
        if (!Poner(datos))
        {
            Contador().Mostrar(ContadorDeImagenes.Color.Rojo, "No se pudo copiar", "Otro programa tenía el portapapeles: Ctrl+Alt+J y otra vez Enter",
                dura: TimeSpan.FromSeconds(8));
            Contador().AlVencerse = () => Contador().Esconder();
            CerrarVentanita();
            return;
        }
        _registro.Anotar($"Juntar imágenes: se copiaron para Word {JuntaDeImagenes.Resumen(orden.Count, ajustes)}.");
        CerrarVentanita();
        Contador().Mostrar(ContadorDeImagenes.Color.Verde, $"{Cuantas(orden.Count)} {(orden.Count == 1 ? "lista" : "listas")}", "Pégalas en Word con Ctrl+V",
            dura: TimeSpan.FromSeconds(10));
        Contador().AlVencerse = () => Contador().Esconder();
    }).Task;

    public Task AbrirEnWordAsync(IReadOnlyList<ImagenOrdenada> orden, AjustesDeJuntarImagenes ajustes) => _hilo.InvokeAsync(() =>
    {
        if (RutaDeWord() is not { } word || _junta.ParaWord(orden, ajustes, conHoja: true, DateTimeOffset.Now) is not { } rtf)
            return;
        GuardarAjustes();
        CerrarVentanita();
        try
        {
            Directory.CreateDirectory(CarpetaTemporal);
            var archivo = Path.Combine(CarpetaTemporal, $"Juntar imágenes {DateTime.Now:HHmmss}.rtf");
            File.WriteAllBytes(archivo, DocumentoParaWord.Bytes(rtf));
            Process.Start(new ProcessStartInfo(word, $"\"{archivo}\"") { UseShellExecute = false });
            _registro.Anotar($"Juntar imágenes: se abrieron en Word {JuntaDeImagenes.Resumen(orden.Count, ajustes)}.");
            Contador().Mostrar(ContadorDeImagenes.Color.Verde, "Abriendo Word…", "Con la hoja y los márgenes puestos", dura: TimeSpan.FromSeconds(6));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _registro.AnotarError("Juntar imágenes: no se pudo abrir Word", ex);
            Contador().Mostrar(ContadorDeImagenes.Color.Rojo, "No se pudo abrir Word", "Ctrl+Alt+J y copia con Enter", dura: TimeSpan.FromSeconds(8));
        }
        Contador().AlVencerse = () => Contador().Esconder();
    }).Task;

    public void JuntarMas(IReadOnlyList<ImagenOrdenada> orden, AjustesDeJuntarImagenes ajustes) => _hilo.Invoke(() =>
    {
        _junta.Seguir(orden, ajustes, DateTimeOffset.Now);
        CerrarVentanita();
        EmpezarAEscuchar();
        Juntando(destello: false);
    });

    public void Cancelar() => _hilo.Invoke(() =>
    {
        var cuantas = _junta.Imagenes.Count;
        Borrar();
        CerrarVentanita();
        if (cuantas > 0)
            _registro.Anotar($"Juntar imágenes: se cancelaron {Cuantas(cuantas)}.");
        Contador().Mostrar(ContadorDeImagenes.Color.Ambar, "Se borraron las imágenes juntadas", "", dura: TimeSpan.FromSeconds(3));
        Contador().AlVencerse = () => Contador().Esconder();
    });

    // ── El atajo y el portapapeles ──

    private IntPtr AlRecibir(IntPtr hwnd, int mensaje, IntPtr wParam, IntPtr lParam, ref bool atendido)
    {
        if (mensaje == WM_HOTKEY && wParam.ToInt32() == IdDelAtajo)
        {
            atendido = true;
            Atajo();
        }
        else if (mensaje == WM_CLIPBOARDUPDATE)
        {
            atendido = true;
            // Ya fuera del aviso de Windows: el programa que copió todavía puede tener el portapapeles abierto.
            _hilo.BeginInvoke(DispatcherPriority.Background, LeerElPortapapeles);
        }
        return IntPtr.Zero;
    }

    /// <summary>Ctrl+Alt+J (o un clic en el contador): empieza, termina, o vuelve a abrir lo de hace un rato.</summary>
    private void Atajo()
    {
        Revisar();
        switch (_junta.Estado)
        {
            case EstadoDeLaJunta.Juntando:
                DejarDeEscuchar();
                if (_junta.Terminar(DateTimeOffset.Now))
                    AbrirVentanita();
                else
                {
                    Contador().Mostrar(ContadorDeImagenes.Color.Ambar, "No juntaste ninguna imagen", "", dura: TimeSpan.FromSeconds(3));
                    Contador().AlVencerse = () => Contador().Esconder();
                }
                break;
            case EstadoDeLaJunta.Juntadas or EstadoDeLaJunta.Copiadas when _ventanita is null:
                // La de hace un rato (se cerró la ventanita con la ×, o se quiere copiar otra vez).
                AbrirVentanita();
                break;
            case EstadoDeLaJunta.Juntadas or EstadoDeLaJunta.Copiadas:
                _ventanita!.Activate();
                break;
            default:
                Empezar();
                break;
        }
    }

    private void Empezar()
    {
        Borrar();
        _junta.Empezar(DateTimeOffset.Now);
        EmpezarAEscuchar();
        Contador().SinMiniatura();
        Juntando(destello: false);
        _reloj.Start();
    }

    private void Juntando(bool destello, byte[]? miniatura = null) =>
        Contador().Mostrar(ContadorDeImagenes.Color.Azul, $"Juntando imágenes · {_junta.Imagenes.Count}", "Ctrl+Alt+J para terminar", miniatura, destello: destello);

    private void EmpezarAEscuchar()
    {
        if (_escuchandoElPortapapeles || _mensajes is null)
            return;
        _escuchandoElPortapapeles = AddClipboardFormatListener(_mensajes.Handle);
        if (!_escuchandoElPortapapeles)
            _registro.Anotar($"Juntar imágenes: no se pudo escuchar el portapapeles (error {Marshal.GetLastWin32Error()}).");
    }

    private void DejarDeEscuchar()
    {
        if (_escuchandoElPortapapeles && _mensajes is not null)
            RemoveClipboardFormatListener(_mensajes.Handle);
        _escuchandoElPortapapeles = false;
    }

    /// <summary>
    /// Lo que se acaba de copiar. Primero los archivos (Ctrl+C en una carpeta, o lo que deje la app de WhatsApp): son el original, tal
    /// cual. Luego el «PNG» que ponen los navegadores y muchas apps, y al último la imagen de Windows (se guarda en PNG, sin perder nada).
    /// </summary>
    private void LeerElPortapapeles()
    {
        if (_junta.Estado != EstadoDeLaJunta.Juntando || GetClipboardSequenceNumber() == _loNuestro)
            return;
        List<byte[]>? leidas = null;
        for (var intento = 0; intento < 6 && leidas is null; intento++)
        {
            try
            {
                leidas = LoQueHay();
            }
            catch (COMException)
            {
                // Otro programa lo tiene abierto todavía.
                Thread.Sleep(60);
            }
        }
        if (leidas is null)
        {
            Contador().Mostrar(ContadorDeImagenes.Color.Rojo, "No alcancé a leer esa", "Cópiala otra vez", dura: TimeSpan.FromSeconds(3));
            Contador().AlVencerse = () => Juntando(destello: false);
            return;
        }
        var antes = _junta.Imagenes.Count;
        var repetidas = 0;
        foreach (var datos in leidas)
            if (_junta.Agregar(datos, DateTimeOffset.Now) == LoQueSeCopio.Repetida)
                repetidas++;
        var nuevas = _junta.Imagenes.Count - antes;
        for (var i = antes; i < _junta.Imagenes.Count; i++)
            PrepararVista(i);
        if (nuevas == 0 && repetidas > 0)
        {
            Contador().Mostrar(ContadorDeImagenes.Color.Ambar, $"Esa ya estaba · siguen {_junta.Imagenes.Count}", "Ctrl+Alt+J para terminar",
                dura: TimeSpan.FromSeconds(2));
            Contador().AlVencerse = () => Juntando(destello: false);
        }
        else if (nuevas > 0)
        {
            var ultima = _junta.Imagenes.Count - 1;
            Juntando(destello: true);
            if (leidas.Count > 1)
                Contador().Mostrar(ContadorDeImagenes.Color.Azul, $"+{nuevas} de la carpeta · {_junta.Imagenes.Count}", "Ctrl+Alt+J para terminar", destello: true);
            _ = MiniaturaAlContadorAsync(ultima);
        }
    }

    private static List<byte[]> LoQueHay()
    {
        var leidas = new List<byte[]>();
        if (Clipboard.ContainsFileDropList())
        {
            foreach (var archivo in Clipboard.GetFileDropList().Cast<string>())
            {
                try
                {
                    if (new FileInfo(archivo) is { Exists: true, Length: > 0 and < 100L * 1024 * 1024 })
                        leidas.Add(File.ReadAllBytes(archivo));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            if (leidas.Count > 0)
                return leidas;
        }
        if (Clipboard.GetData("PNG") is MemoryStream png)
        {
            leidas.Add(png.ToArray());
            return leidas;
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } imagen)
        {
            var codificador = new PngBitmapEncoder();
            codificador.Frames.Add(BitmapFrame.Create(imagen));
            using var salida = new MemoryStream();
            codificador.Save(salida);
            leidas.Add(salida.ToArray());
        }
        return leidas;
    }

    // ── La ventanita ──

    private void AbrirVentanita()
    {
        Contador().Esconder();
        var cuantas = _junta.Imagenes.Count;
        for (var i = 0; i < cuantas; i++)
            PrepararVista(i);
        // Las miniaturas se hicieron mientras se juntaba; las que falten se esperan aquí (unos instantes con muchas).
        Imagenes = Task.WhenAll(Enumerable.Range(0, cuantas).Select(i => _vistas[i])).GetAwaiter().GetResult();
        _ventanita = new VentanitaJuntarImagenes(this, _registro);
        _ventanita.Closed += (_, _) =>
        {
            _ventanita = null;
            _junta.Dejar(DateTimeOffset.Now);
        };
        _ventanita.Show();
    }

    /// <summary>
    /// Se cierra un momento después: lo pide la propia ventanita desde un botón o una tecla, y su página termina de contestar antes de
    /// irse.
    /// </summary>
    private void CerrarVentanita()
    {
        var ventanita = _ventanita;
        _ventanita = null;
        if (ventanita is not null)
            _hilo.BeginInvoke(DispatcherPriority.Background, ventanita.Close);
    }

    private void PrepararVista(int indice)
    {
        if (!_vistas.ContainsKey(indice))
            _vistas[indice] = Task.Run(() => _junta.Vista(indice));
    }

    private async Task MiniaturaAlContadorAsync(int indice)
    {
        try
        {
            var vista = await _vistas[indice];
            var jpeg = Convert.FromBase64String(vista.Miniatura[(vista.Miniatura.IndexOf(',') + 1)..]);
            await _hilo.InvokeAsync(() =>
            {
                if (_junta.Estado == EstadoDeLaJunta.Juntando && _junta.Imagenes.Count - 1 == indice)
                    Contador().Mostrar(ContadorDeImagenes.Color.Azul, $"Juntando imágenes · {_junta.Imagenes.Count}", "Ctrl+Alt+J para terminar", jpeg);
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
        }
    }

    // ── Los 15 minutos ──

    private void Revisar()
    {
        var ahora = DateTimeOffset.Now;
        if (!_junta.Vencida(ahora))
            return;
        if (_junta.Estado == EstadoDeLaJunta.Juntando)
        {
            // Se olvidó que estaba juntando: deja de escuchar, y lo juntado se queda para verlo con el atajo.
            DejarDeEscuchar();
            if (!_junta.Terminar(ahora))
            {
                Borrar();
                Contador().Esconder();
                return;
            }
            var n = _junta.Imagenes.Count;
            Contador().Mostrar(ContadorDeImagenes.Color.Ambar, "Se detuvo: 15 min sin copiar", $"Ctrl+Alt+J para ver {(n == 1 ? "la imagen" : $"las {n}")}");
            return;
        }
        if (_ventanita is not null)
            return;
        var cuantas = _junta.Imagenes.Count;
        Borrar();
        Contador().Esconder();
        _registro.Anotar($"Juntar imágenes: se borraron {Cuantas(cuantas)} (pasaron 15 minutos).");
    }

    private void Borrar()
    {
        DejarDeEscuchar();
        _junta.Borrar();
        _vistas.Clear();
        Imagenes = [];
        // Si lo último en el portapapeles es el bloque para Word, también se quita.
        if (_loNuestro != 0 && GetClipboardSequenceNumber() == _loNuestro)
        {
            try
            {
                Clipboard.Clear();
            }
            catch (COMException)
            {
            }
        }
        _loNuestro = 0;
        if (_junta.Estado == EstadoDeLaJunta.Nada)
            _reloj.Stop();
    }

    private bool Poner(DataObject datos)
    {
        for (var intento = 0; intento < 5; intento++)
        {
            try
            {
                Clipboard.SetDataObject(datos, copy: true);
                _loNuestro = GetClipboardSequenceNumber();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(80);
            }
        }
        return false;
    }

    /// <summary>Que no quede en el historial del portapapeles (Win+V), ni se suba a la nube (como «Copiar para WhatsApp» de los trámites).</summary>
    private static void SinHistorial(DataObject datos)
    {
        datos.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        datos.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        datos.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(0)));
    }

    private ContadorDeImagenes Contador() => _contador ??= new ContadorDeImagenes(Atajo, lugar =>
    {
        _lugarDelContador = lugar;
        GuardarAjustes();
    }, _lugarDelContador);

    private static string Cuantas(int n) => n == 1 ? "1 imagen" : $"{n} imágenes";

    // ── Word ──

    /// <summary>Word, si está: Windows lo registra en «App Paths». Sin él, «Abrir en Word» sale apagado.</summary>
    private static string? RutaDeWord()
    {
        foreach (var raiz in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var llave = raiz.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Winword.exe");
                if (llave?.GetValue(null) is string ruta && File.Exists(ruta.Trim('"')))
                    return ruta.Trim('"');
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return null;
    }

    private static void BorrarTemporales()
    {
        try
        {
            if (Directory.Exists(CarpetaTemporal))
                Directory.Delete(CarpetaTemporal, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Word todavía tiene abierto uno: se borra la próxima vez.
        }
    }

    // ── Lo que se recuerda en esta caja ──

    private sealed record Guardado(AjustesDeJuntarImagenes? Ajustes, double? ContadorX, double? ContadorY);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private (AjustesDeJuntarImagenes, Point?) LeerAjustes()
    {
        try
        {
            if (File.Exists(_archivoDeAjustes) && JsonSerializer.Deserialize<Guardado>(File.ReadAllText(_archivoDeAjustes), Json) is { } g)
                return (g.Ajustes ?? new(), g.ContadorX is { } x && g.ContadorY is { } y ? new Point(x, y) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _registro.Anotar($"Juntar imágenes: no se pudieron leer los ajustes ({ex.GetType().Name}); se usan los de siempre.");
        }
        return (new(), null);
    }

    private void GuardarAjustes()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_archivoDeAjustes)!);
            File.WriteAllText(_archivoDeAjustes, JsonSerializer.Serialize(new Guardado(_junta.Ajustes, _lugarDelContador?.X, _lugarDelContador?.Y), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _registro.AnotarSinRepetir("Juntar imágenes: no se guardaron los ajustes", ex.GetType().Name);
        }
    }

    public void Dispose()
    {
        _reloj.Stop();
        if (_mensajes is not null)
        {
            if (_atajoRegistrado)
                UnregisterHotKey(_mensajes.Handle, IdDelAtajo);
            DejarDeEscuchar();
            _mensajes.RemoveHook(AlRecibir);
            _mensajes.Dispose();
        }
        Borrar();
        CerrarVentanita();
        _contador?.Close();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
