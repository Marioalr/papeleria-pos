using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using PapeleriaPos.Impresion;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Impresiones.Windows;
using PapeleriaPos.Interfaz;

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
/// <param name="LlaveDelServicio">
/// La llave pública de la caja principal (de su servicio, no de su base): la caja no se cambia sola a otro que no firme con ella.
/// No es secreta; se aprende al darse de alta o la primera vez que la caja principal prueba quién es.
/// </param>
/// <param name="DireccionDeConfianza">
/// La dirección que puso el dueño al darla de alta (en una caja de antes, la que ya tenía): solo ahí se aprende esa llave.
/// </param>
/// <param name="Red">Lo de la bitácora de red: «"Red": { "DetalleWifi": true }» anota el Wi-Fi (apagado: ver <see cref="AjustesDeRed"/>).</param>
public sealed record ConfiguracionCaja(
    string Servicio, Guid? CajaId, string? Impresora, string? LlaveProtegida = null, string? ColaImpresion = null, bool QuitarImpresos = true,
    bool Prueba = false, string? ServicioLocal = null, string? BaseInicial = null, string? CarpetaDatos = null, string? LlaveDelServicio = null,
    string? DireccionDeConfianza = null, AjustesDeRed? Red = null)
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
            LlaveDelServicio = conexion.LlaveDelServicio,
            DireccionDeConfianza = conexion.DireccionDeConfianza?.ToString(),
            LlaveProtegida = conexion.Llave is { } llave
                ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(llave), null, DataProtectionScope.LocalMachine))
                : null,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Guardada)!);
        // Primero a un archivo aparte, hasta el disco, y después se cambia de un jalón: si se va la luz a media escritura (puede
        // pasar cuando la caja guarda sola la dirección nueva de la caja principal, en plena falla de la red), caja.json no queda
        // a medias ni en ceros.
        var nuevoArchivo = Guardada + ".nuevo";
        using (var archivo = new FileStream(nuevoArchivo, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            archivo.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(nueva, Json)));
            archivo.Flush(flushToDisk: true);
        }
        File.Move(nuevoArchivo, Guardada, overwrite: true);
        return nueva;
    }
}

/// <summary>«Red» en caja.json.</summary>
/// <param name="DetalleWifi">
/// Que la bitácora de red anote el nombre del Wi-Fi y su señal, para diagnosticar una caída (se prende en la caja 2 cuando hace
/// falta). Apagado por omisión: en Windows 11 24H2, leerlo podría sacar la pregunta del permiso de ubicación encima de la venta.
/// </param>
public sealed record AjustesDeRed(bool DetalleWifi = false);

/// <summary>La ventana de la caja: muestra la interfaz compartida y manda el ticket a la impresora de tickets.</summary>
public partial class MainWindow : Window
{
    private readonly RegistroDiario _registro;

    /// <summary>caja.json se guarda de uno en uno: el alta y la dirección nueva de la caja principal pueden llegar a la vez.</summary>
    private readonly Lock _guardando = new();

    /// <summary>Para volver a cargar sin que la caja se quede en «Cargando…» (AlgoFallo quita antes la pantalla).</summary>
    private readonly RecargaDeLaCaja _recarga = new();

    /// <param name="configuracion">La lee App al abrir: de ella sale también la carpeta del registro.</param>
    /// <param name="registro">El de la caja (App): aquí lo usan AlgoFallo y RaizCaja, y junto a él va el del servicio de la prueba.</param>
    public MainWindow(ConfiguracionCaja configuracion, RegistroDiario registro)
    {
        InitializeComponent();
        _registro = registro;

        // La prueba con una persona real: su propio servicio en esta PC, sin alta. También trabaja sin conexión (con su
        // copia y su bandeja junto a los datos de la prueba), para probar el crédito sin conexión con «Simular sin conexión».
        var prueba = configuracion.Prueba ? ServicioDePrueba.Iniciar(configuracion, new RegistroDiario(registro.Carpeta, "servicio")) : null;
        if (prueba is not null)
        {
            Closed += (_, _) => prueba.Dispose();
            // Si no arranca, la caja lo dice al entrar (PantallaCaja); aquí queda anotado.
            _ = prueba.Listo.ContinueWith(t => registro.AnotarError("El servicio de la prueba no arrancó", t.Exception!.InnerException ?? t.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        var conexion = new ConexionCaja
        {
            Servicio = new Uri(configuracion.Servicio),
            CajaId = prueba is null ? configuracion.CajaId : null,
            Llave = prueba is null ? configuracion.Llave() : null,
            LlaveDelServicio = prueba is null ? configuracion.LlaveDelServicio : null,
            // Una caja de antes (sin esto guardado): la dirección que ya tenía, la que puso el dueño.
            DireccionDeConfianza = new Uri(configuracion.DireccionDeConfianza ?? configuracion.Servicio),
            PideAlta = prueba is null,
            Listo = prueba?.Listo ?? Task.CompletedTask,
            AlGuardar = c =>
            {
                // Lo guarda el alta (en la ventana) y también la caja sola, de fondo, al encontrar la caja principal en otra IP.
                lock (_guardando)
                    configuracion = configuracion.Guardar(c);
                return Task.CompletedTask;
            },
        };
        var servicios = new ServiceCollection();
        servicios.AddWpfBlazorWebView();
        servicios.AddSingleton(registro);
        servicios.AddSingleton(_recarga);
        // Guardar un archivo (el Excel de los reportes, entrega 9, parte 2): «Guardar como» de Windows, en Documentos. Antes de la
        // interfaz: si no, la caja lo descargaría como en el navegador. Abrir uno (el Excel de los productos que se importa,
        // 2026-10-22): «Abrir» de Windows. Los dos empiezan en la carpeta del último que se guardó o se abrió: el Excel que se acaba de
        // exportar se encuentra ahí.
        var carpeta = new CarpetaDeArchivos();
        servicios.AddSingleton<IGuardarArchivo>(new GuardadoEnLaPc(new GuardarComoDeWindows(this, () => Vista.WebView?.Focus()).PreguntarAsync, carpeta));
        servicios.AddSingleton<IAbrirArchivo>(new AbiertoEnLaPc(new AbrirDeWindows(this, () => Vista.WebView?.Focus()).PreguntarAsync, carpeta));
        // "tcp://192.168.1.100:9100" imprime directo por la red; un nombre (como "Tickets") usa la impresora de Windows.
        var impresora = Impresoras.Crear(configuracion.Impresora, nombre => new ImpresoraWindows(nombre));
        // La copia para vender sin la caja principal y la bandeja de salida, junto a la configuración de la caja. La red de la
        // tienda en el POS: la bitácora de red junto al registro de la caja (red-caja-AAAA-MM-DD.log), el Wi-Fi y la MAC del
        // router, y el puerto de la impresora de tickets (si es WSD o por nombre, el estado de arriba lo dice).
        servicios.AgregarInterfazCaja(conexion, impresora,
            prueba is null ? ConfiguracionCaja.BaseLocal : Path.Combine(ServicioDePrueba.CarpetaDatos(configuracion), "caja-local.db"),
            new OpcionesRedDeLaCaja
            {
                CarpetaBitacora = registro.Carpeta,
                Impresora = configuracion.Impresora,
                Puertos = new PuertosDeImpresorasWindows(),
                Detalles = new RedDeWindows(),
                DetalleWifi = configuracion.Red?.DetalleWifi ?? false,
            });
        // Cotizar: el PDF se mide aquí, en la PC donde está; el precio lo pone la caja principal.
        servicios.AddSingleton<IMedidorPdf, MedidorPdfWindows>();
        // Los tiempos de la caja en su registro (el piloto alfa, 2026-10-25: juntar datos): cada venta (del primer artículo al cobro,
        // lo que esperó a eleventa y con qué folio salió) y, cada hora y al terminar el turno, lo que tardaron los escaneos.
        servicios.AddSingleton(p => new TiemposDeLaCaja(p.GetRequiredService<RendimientoCaja>(), registro));
        // La prueba se actualiza desde Configuración con los instaladores que arma GitHub.
        if (prueba is not null)
            servicios.AgregarActualizacionPrueba(new EquipoPrueba(ServicioDePrueba.CarpetaDatos(configuracion)), GitHubPrueba.RepositorioPorOmision);
        var proveedor = servicios.BuildServiceProvider();
        Vista.Services = proveedor;
        var tiempos = proveedor.GetRequiredService<TiemposDeLaCaja>();
        tiempos.Iniciar();
        // Al cerrar la caja, lo de la hora que iba y lo del turno hasta ahora.
        Closed += (_, _) =>
        {
            tiempos.Cerrar();
            tiempos.Dispose();
        };
        Vista.BlazorWebViewInitialized += AlIniciarLaPagina;
        Vista.UrlLoading += AlCargarUnaDireccion;
        // Antes que el aviso del corte en eleventa: si se decide no cerrar, ese ya no sale.
        AvisarLoQueNoSeImprimio(ColaDeImpresion.De(impresora));
        if (proveedor.GetService<Contingencia>() is { } contingencia)
        {
            IniciarAgente(configuracion, conexion, contingencia);
            RecordarElCorteAlCerrar(contingencia);
            // Lo que estaba en la cola del escritor de la base local (entrega 5) queda en el disco antes de cerrar.
            Closed += (_, _) => contingencia.Cerrar();
        }
    }

    // ── Cuando algo falla: el aviso de arriba (lo pide App) ──

    private const string AlgoFallo =
        "Algo falló y quedó anotado; la caja no se cerró. Si la pantalla no responde, vuelve a cargarla y entra con tu PIN: "
        + "los tickets en espera regresan solos. Si responde, puedes terminar el ticket y seguir.";

    private const string SinRespuesta =
        "Algo falló y la pantalla dejó de responder; quedó anotado y la caja no se cerró. Vuelve a cargarla y entra con tu PIN: "
        + "los tickets en espera regresan solos. Revisa el ticket que estabas atendiendo: puede regresar sin los últimos productos.";

    private const string SinPagina =
        "La pantalla de la caja dejó de funcionar y quedó anotado. Cierra la caja y vuelve a abrirla: los tickets en espera regresan solos.";

    /// <summary>Lo que la página le dice a la ventana cuando Blazor ya no contesta (registroCaja, en index.html).</summary>
    private const string PaginaSinRespuesta = "caja:sin-respuesta";

    private DateTime _ultimaSinResponder;

    /// <summary>
    /// La página ya no contesta hasta volver a cargarla (Blazor la dio por perdida, o su proceso se cayó): el aviso ya no
    /// ofrece «Seguir», aunque después llegue otro error.
    /// </summary>
    private bool _sinRespuesta;

    /// <summary>Se cayó el navegador entero: solo queda cerrar la caja y volver a abrirla.</summary>
    private bool _sinNavegador;

    private bool _recargando;

    /// <summary>
    /// Lo pide App con cada error que nadie atrapó. Arriba, sin tapar el ticket: si la pantalla sigue respondiendo, se
    /// termina la venta antes de volver a cargar (lo que se pasó al ticket que se atiende desde que se cambió de ticket se
    /// pierde al volver a cargar).
    /// </summary>
    internal void AvisarQueAlgoFallo()
    {
        if (_sinNavegador)
            Avisar(SinPagina, volverACargar: false, seguir: false);
        else if (_sinRespuesta)
            Avisar(SinRespuesta, volverACargar: true, seguir: false);
        else
            Avisar(AlgoFallo, volverACargar: true, seguir: true);
    }

    private void Avisar(string texto, bool volverACargar, bool seguir)
    {
        TextoAviso.Text = texto;
        BotonVolverACargar.Visibility = volverACargar ? Visibility.Visible : Visibility.Collapsed;
        BotonSeguir.Visibility = seguir ? Visibility.Visible : Visibility.Collapsed;
        Aviso.Visibility = Visibility.Visible;
    }

    private async void AlVolverACargar(object sender, RoutedEventArgs e) => await VolverACargarAsync();

    /// <summary>
    /// Toda recarga de la página que pide la propia página (el botón de «Algo falló», «Empezar de cero» de la prueba, F5
    /// donde la pantalla no lo usa) se detiene si hay pantalla de venta que quitar, y se repite con VolverACargarAsync. La
    /// de VolverACargarAsync ya no la encuentra (la quitó) y sigue; también la primera, cuando todavía no hay pantalla.
    /// </summary>
    private void AlCargarUnaDireccion(object? sender, UrlLoadingEventArgs e)
    {
        if (e.UrlLoadingStrategy != UrlLoadingStrategy.OpenInWebView || !_recarga.HayPantalla)
            return;
        e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
        // Ya fuera del aviso de WebView2 (que la está deteniendo): quitar la pantalla le habla a la página.
        var destino = e.Url;
        _ = Dispatcher.InvokeAsync(() => VolverACargarAsync(destino));
    }

    /// <summary>
    /// Vuelve a cargar la página: la caja se arma de nuevo (con el PIN) sin cerrar la ventana ni el servicio. Antes se quita
    /// la pantalla de la página de ahora (RecargaDeLaCaja): si no, la nueva se quedaba en «Cargando…» hasta volver a cargar
    /// otra vez. Async void desde el botón: aquí se atrapa lo que pueda fallar.
    /// </summary>
    /// <param name="destino">A dónde iba la página, si lo pidió ella; si no, la misma.</param>
    private async Task VolverACargarAsync(Uri? destino = null)
    {
        if (_recargando)
            return;
        _recargando = true;
        try
        {
            if (Vista.WebView?.CoreWebView2 is not { } pagina)
                throw new InvalidOperationException("La página todavía no estaba lista.");
            Aviso.Visibility = Visibility.Collapsed;
            try
            {
                await _recarga.SoltarAsync(RecargaDeLaCaja.Espera);
            }
            catch (Exception ex)
            {
                // Lo que sea: se vuelve a cargar igual, que es el remedio. Lo peor es que haya que volver a cargar otra vez.
                _registro.AnotarError("No se quitó la pantalla antes de volver a cargar", ex);
            }
            _registro.Anotar(destino is null ? "Se vuelve a cargar la página (el aviso de arriba)." : "Se vuelve a cargar la página (lo pidió la página).");
            (_sinRespuesta, _ultimaSinResponder) = (false, default);
            if (destino is null)
                pagina.Reload();
            else
                pagina.Navigate(destino.AbsoluteUri);
            // Lo que haya avisado App mientras se quitaba la pantalla ya no aplica: la página es otra.
            Aviso.Visibility = Visibility.Collapsed;
            Vista.WebView.Focus();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            _registro.AnotarError("No se pudo volver a cargar", ex);
            // Sin el navegador (COMException) ya no hay qué volver a cargar, aunque lleguen otros errores.
            _sinNavegador = ex is COMException;
            Avisar(SinPagina, volverACargar: false, seguir: false);
        }
        finally
        {
            _recargando = false;
        }
    }

    /// <summary>La pantalla responde: se quita el aviso y el teclado vuelve a la caja (al escáner).</summary>
    private void AlSeguir(object sender, RoutedEventArgs e)
    {
        Aviso.Visibility = Visibility.Collapsed;
        Vista.WebView?.Focus();
    }

    private void AlIniciarLaPagina(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        _registro.Anotar($"La página corre en WebView2 {e.WebView.CoreWebView2.Environment.BrowserVersionString}.");
        e.WebView.CoreWebView2.ProcessFailed += AlFallarLaPagina;
        e.WebView.CoreWebView2.WebMessageReceived += AlRecibirDeLaPagina;
        // El teclado a la página desde que abre la caja: en «¿Quién atiende?» la letra y el PIN se teclean sin tocar antes la
        // pantalla (entrega 6).
        e.WebView.Focus();
    }

    /// <summary>
    /// Lo que dice la página por su cuenta (Blazor usa el mismo canal con sus propios mensajes, que aquí no se tocan): que
    /// ya no responde, cuando Blazor saca su aviso. Desde ahí no le contesta a nadie: solo queda volver a cargar.
    /// </summary>
    private void AlRecibirDeLaPagina(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? mensaje;
        try
        {
            mensaje = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }
        if (mensaje != PaginaSinRespuesta || _sinRespuesta)
            return;
        _registro.Anotar("La página dejó de responder (el aviso de Blazor): hay que volver a cargarla.");
        _sinRespuesta = true;
        AvisarQueAlgoFallo();
    }

    /// <summary>
    /// La página (WebView2) también puede caerse o colgarse sin pasar por .NET: la pantalla se queda en blanco o sin
    /// responder. Se anota y se avisa igual; si se cayó el navegador entero, ya no hay qué volver a cargar.
    /// </summary>
    private void AlFallarLaPagina(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        // Colgada, WebView2 avisa cada pocos segundos hasta que vuelve a responder: se anota una vez cada medio minuto.
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            if (DateTime.UtcNow - _ultimaSinResponder < TimeSpan.FromSeconds(30))
                return;
            _ultimaSinResponder = DateTime.UtcNow;
        }
        _registro.Anotar($"Falló un proceso de la página: {e.ProcessFailedKind} ({e.Reason}, código {e.ExitCode}, {e.ProcessDescription}).");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                _sinNavegador = true;
                AvisarQueAlgoFallo();
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                // La página se fue con su proceso: ya no hay qué seguir.
                _sinRespuesta = true;
                AvisarQueAlgoFallo();
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                // Colgada puede volver a responder: se puede seguir.
                AvisarQueAlgoFallo();
                break;
            // Los demás (la tarjeta de video, los de apoyo) WebView2 los vuelve a levantar solo: basta con anotarlo.
        }
    }

    /// <summary>
    /// El piloto con eleventa (decisión del dueño, 2026-10-12): al cerrar la ventana, la caja recuerda que el corte se hace
    /// en eleventa. Solo avisa: con «Aceptar» la ventana se cierra como siempre (el aviso no cambia qué se guarda ni cómo).
    /// Lo sabe por la contingencia, también sin conexión. Al apagar Windows no sale (WPF no pasa por Closing). Solo para
    /// convivir con eleventa: se quita al dejarlo.
    /// </summary>
    private void RecordarElCorteAlCerrar(Contingencia contingencia) =>
        Closing += (_, e) =>
        {
            // Si ya se decidió no cerrar (quedaba algo por imprimir), no hace falta recordarlo.
            if (!e.Cancel && contingencia.PilotoEleventa)
                MessageBox.Show(this, PilotoConEleventa.CorteEnEleventa, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        };

    /// <summary>
    /// La cola de la impresora de tickets (entrega 5, parte 3): lo que se imprime no detiene la pantalla, así que al cerrar la
    /// caja puede quedar algo sin imprimir (no salió, o espera a la impresora). Nada se pierde sin decirlo: se pregunta antes
    /// de cerrar, con lo que es, y si se cierra igual queda anotado en el registro del día. No se guarda para imprimirlo al
    /// abrir otra vez (saldría tarde, sin que nadie lo espere): lo que dice ya quedó guardado y se puede volver a imprimir.
    /// </summary>
    private void AvisarLoQueNoSeImprimio(ColaDeImpresion cola)
    {
        Closing += (_, e) =>
        {
            // Cerrándose por un error, Windows no deja detenerlo: no se pregunta (queda anotado al cerrarse).
            if (e.Cancel || Application.Current is App { SeCierraPorUnError: true } || cola.Pendientes is not { Count: > 0 } pendientes)
                return;
            var motivo = cola.Problema is { Estado: EstadoImpresion.NoSalio, Motivo: { } m } ? $" ({m.TrimEnd('.')})" : "";
            var respuesta = MessageBox.Show(this,
                $"Falta imprimir {string.Join(", ", pendientes.Select(t => $"«{t.Titulo}»"))}{motivo}.\n\n"
                + "Si cierras la caja, no sale: lo que dice ya quedó guardado y se puede volver a imprimir. ¿Cerrar de todos modos?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (respuesta != MessageBoxResult.Yes)
                e.Cancel = true;
        };
        Closed += (_, _) =>
        {
            foreach (var trabajo in cola.Pendientes)
                _registro.Anotar($"Se cerró la caja sin imprimir «{trabajo.Titulo}»{(trabajo.Motivo is { } motivo ? $": {motivo}" : ".")}");
        };
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
