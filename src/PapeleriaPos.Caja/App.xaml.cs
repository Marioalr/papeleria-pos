using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Caja;

/// <summary>
/// La caja de Windows. Un error que nadie atrapó ya no la cierra sin rastro (antes de la segunda prueba con una persona
/// real, 2026-10-17, un null o un error de JavaScript la cerraba en silencio): se anota con fecha y hora en el registro
/// (<see cref="RegistroDiario"/>) y la ventana avisa con «Volver a cargar», sin cerrarse. Lo que falla dentro de la
/// pantalla casi siempre lo atrapa antes AlgoFallo; aquí llega lo demás.
/// </summary>
public partial class App : Application
{
    /// <summary>Hasta leer la configuración no se sabe si es la prueba: lo de antes va a la carpeta del usuario.</summary>
    private RegistroDiario _registro = new(RegistroDiario.CarpetaDeLaCaja(null), "caja");

    private readonly ErroresSeguidos _seguidos = new();
    private MainWindow? _ventana;
    private bool _cerrada;
    private bool _cerrando;

    /// <summary>Mientras la caja esté abierta; Windows lo suelta al salir, aunque se cierre a la fuerza.</summary>
    private Mutex? _unaSola;

    /// <summary>Ya había una caja abierta: esta la trajo al frente, o dijo que no responde, y se va sin abrir nada.</summary>
    private bool _otraAbierta;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Antes que nada: también lo que falle al abrir queda anotado.
        AppDomain.CurrentDomain.UnhandledException += AlCerrarsePorUnError;
        DispatcherUnhandledException += AlFallarEnLaVentana;
        TaskScheduler.UnobservedTaskException += AlFallarUnaTarea;
        try
        {
            var configuracion = ConfiguracionCaja.Leer();
            // La prueba anota junto a sus datos y sus respuestas: lo que se manda al terminar va en una sola carpeta.
            if (configuracion.Prueba)
                _registro = new RegistroDiario(RegistroDiario.CarpetaDeLaCaja(ServicioDePrueba.CarpetaDatos(configuracion)), "caja");
            // Una sola caja: dos ventanas se pisan la bandeja, los folios y los tickets en espera, y la segunda no puede leer el lector
            // por puerto. En una PC lenta es fácil darle doble clic otra vez. La de la prueba lleva su nombre.
            _unaSola = new Mutex(initiallyOwned: true, configuracion.Prueba ? @"Local\PapeleriaPos.Caja.Prueba" : @"Local\PapeleriaPos.Caja", out var esLaUnica);
            if (!esLaUnica && !SeQuedaConElLugarDeLaOtra(_unaSola))
            {
                _otraAbierta = true;
                Shutdown();
                return;
            }
            _registro.Limpiar();
            // Si se cerró sin «Se cerró la caja» ni un error, la apagaron o la cerró Windows a la fuerza.
            _registro.Anotar($"Se abrió la caja{(GitHubPrueba.CommitInstalado(typeof(App).Assembly) is { } c ? $" (commit {c[..7]})" : "")}"
                + (configuracion.Prueba ? ", la de la prueba." : "."));
            _ventana = new MainWindow(configuracion, _registro);
            _ventana.Closed += (_, _) => _cerrada = true;
            MainWindow = _ventana;
            _ventana.Show();
        }
        catch (Exception ex)
        {
            // Sin ventana no hay qué volver a cargar: se dice por qué y se cierra. Volver a abrirla fallaría igual.
            _registro.AnotarError("La caja no pudo abrir", ex);
            Cerrar($"La caja no pudo abrir: {ex.Message}", NoAbre);
        }
    }

    /// <summary>
    /// Ya hay otra caja. Si contesta, se trae al frente y esta se va. Si se termina de cerrar mientras tanto, esta abre. Si pasa la espera y
    /// sigue sin contestar (congelada, o viva sin ventana), se dice y se ofrece cerrarla: Windows no suelta el candado mientras viva.
    /// </summary>
    private bool SeQuedaConElLugarDeLaOtra(Mutex candado)
    {
        var reloj = Stopwatch.StartNew();
        while (reloj.Elapsed < EsperaALaOtra)
        {
            if (VentanaDeLaOtraCaja.AlFrente())
                return false;
            if (Tomar(candado, TimeSpan.FromMilliseconds(500)))
                return true;
        }
        _registro.Anotar("Se intentó abrir la caja con otra abierta que no responde.");
        var respuesta = MessageBox.Show(
            "La caja ya está abierta, pero no responde.\n\n¿La cierras para abrirla de nuevo? Lo ya cobrado está guardado y los tickets en espera regresan solos.\n\n"
            + "Si apenas está abriendo o terminando algo, elige «No» y espera un momento.",
            "Caja", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (respuesta != MessageBoxResult.Yes)
        {
            _registro.Anotar("No se quiso cerrar la caja que no responde.");
            return false;
        }
        VentanaDeLaOtraCaja.Cerrarla();
        if (Tomar(candado, TimeSpan.FromSeconds(10)))
        {
            _registro.Anotar("Se cerró a la fuerza la caja que no respondía.");
            return true;
        }
        _registro.Anotar("No se pudo cerrar la caja que no responde.");
        MessageBox.Show("No se pudo cerrar la caja que no responde. Reinicia esta PC para volver a abrirla.", "Caja", MessageBoxButton.OK, MessageBoxImage.Error);
        return false;
    }

    /// <summary>En una PC lenta, la otra puede tardar en mostrar su ventana o quedarse ocupada un rato sin estar congelada.</summary>
    private static readonly TimeSpan EsperaALaOtra = TimeSpan.FromSeconds(20);

    /// <summary>Si la otra caja soltó el candado. Nunca lo suelta ella: se lo quita Windows al terminar, y eso se avisa con esta excepción.</summary>
    private static bool Tomar(Mutex candado, TimeSpan espera)
    {
        try
        {
            return candado.WaitOne(espera);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_otraAbierta)
        {
            base.OnExit(e);
            return;
        }
        _registro.Anotar($"Se cerró la caja{(e.ApplicationExitCode == 0 ? "" : $" (código {e.ApplicationExitCode})")}.");
        base.OnExit(e);
    }

    /// <summary>
    /// Lo que truena en el hilo de la ventana: lo de Blazor que AlgoFallo no atrapó (WebViewRenderer y su despachador lo
    /// vuelven a lanzar aquí; entonces la página ya no responde y también muestra su aviso) y lo de la propia ventana. Se
    /// marca atendido para que la caja no se cierre, y avisa arriba sin tapar el ticket: si la pantalla sigue
    /// respondiendo, se puede terminar la venta antes de volver a cargar.
    /// </summary>
    private void AlFallarEnLaVentana(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _registro.AnotarError("Error en la ventana", e.Exception);
        e.Handled = true;
        if (_cerrada || _cerrando)
            return;
        if (_ventana is { IsVisible: true } ventana && _seguidos.SePuedeSeguir(e.Exception))
            ventana.AvisarQueAlgoFallo();
        else
            Cerrar($"La caja se tiene que cerrar por un error: {e.Exception.Message}");
    }

    /// <summary>
    /// Lo que truena en otro hilo: .NET cierra la caja de todos modos. Queda anotado y la cajera sabe que no fue ella.
    /// Al cerrarse la caja, el servicio de la prueba se detiene solo (se le cierra la entrada).
    /// </summary>
    private void AlCerrarsePorUnError(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _registro.AnotarError("La caja se cerró por un error", ex);
        else
            _registro.Anotar($"La caja se cerró por un error: {e.ExceptionObject}");
        if (!_cerrando)
            MessageBox.Show(Mensaje("La caja se cerró por un error"), "Caja", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// Una tarea que falló sin que nadie la esperara (el recolector avisa después, cuando la recoge). No cierra la caja y
    /// la pantalla sigue igual: solo se anota, sin interrumpir la venta con un aviso a destiempo.
    /// </summary>
    private void AlFallarUnaTarea(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _registro.AnotarError("Error en una tarea que nadie esperaba", e.Exception);
        e.SetObserved();
    }

    /// <summary>Lo que se hace después de un error que cerró la caja.</summary>
    private const string VuelveAAbrir = "Vuelve a abrir la caja y entra con tu PIN: los tickets en espera regresan solos.";

    /// <summary>Si no pudo abrir (caja.json dañado, por ejemplo), volver a abrirla falla igual.</summary>
    private const string NoAbre = "Avísale a quien te instaló la caja y mándale esa carpeta.";

    /// <summary>
    /// La caja se está cerrando por un error: la ventana ya no pregunta nada al cerrarse (no se puede detener, y «No» no
    /// haría nada); lo que quede sin imprimir solo se anota.
    /// </summary>
    internal bool SeCierraPorUnError => _cerrando;

    /// <summary>Se cierra en orden: la ventana se cierra como siempre y el servicio de la prueba se detiene bien.</summary>
    private void Cerrar(string porque, string queHacer = VuelveAAbrir)
    {
        if (_cerrando)
            return;
        _cerrando = true;
        _registro.Anotar("La caja se cierra por ese error.");
        MessageBox.Show(Mensaje(porque, queHacer), "Caja", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    private string Mensaje(string porque, string queHacer = VuelveAAbrir) =>
        $"{porque}{Environment.NewLine}{Environment.NewLine}Quedó anotado en {_registro.Carpeta}.{Environment.NewLine}{queHacer}";
}
