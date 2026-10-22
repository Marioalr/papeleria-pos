using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
using PapeleriaPos.Impresiones.Servicio;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Servicio;
using PapeleriaPos.Servicio.Componentes;
using PapeleriaPos.Servicio.Telegram;

// Servicio de la sucursal: el único que escribe en la base. En la caja principal corre como servicio de Windows y
// atiende por la red a las cajas dadas de alta. Para probar sin la ventana de Windows, muestra la caja en el navegador
// (con vista previa del ticket en lugar de imprimir).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Los archivos (configuración, wwwroot) se buscan junto al programa, no en la carpeta actual: como servicio de
    // Windows es System32, y abierto desde otra carpeta la caja del navegador saldría sin estilos ni scripts.
    ContentRootPath = AppContext.BaseDirectory,
});
// La configuración de esta PC (C:\ProgramData\PapeleriaPos\servicio.json), fuera de la carpeta del programa: actualizar no la
// toca. Manda sobre appsettings.json; las variables de entorno y la línea de órdenes, sobre ella (la clave de eleventa va solo en
// una variable de entorno).
ConfiguracionDeLaPc.Agregar(builder.Configuration, RutasServicio.ConfiguracionDeLaPc(builder.Configuration), args);
builder.Host.UseWindowsService(o => o.ServiceName = "PapeleriaPos");
// La caja de la prueba (la que pasa esta opción) guarda lo que escribe este servicio en su registro
// (registros\servicio-….log): en UTF-8, para que lleguen los acentos; si no, sale en la página de códigos de la consola.
if (builder.Configuration.GetValue("Prueba:DetenerAlCerrarEntrada", false))
{
    try
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    }
    catch (IOException)
    {
        // Sin consola ya escribe en UTF-8.
    }
}

var cadena = RutasServicio.CadenaConexion(builder.Configuration);
// La prueba con una persona real: la base empieza como copia de la de prueba (docs/plan.md).
var prueba = ModoPrueba.Crear(builder.Configuration, cadena);
prueba.PrepararBase();
builder.Services.AddSingleton(prueba);
// Las versiones de la copia de las cajas y sus llaves, en memoria (entrega 5, parte 2): lo que se guarda en la base las
// vuelve a leer al momento.
builder.Services.AddSingleton<VersionesDeLaCopia>();
builder.Services.AddSingleton<LlavesDeCajas>();
builder.Services.AddDbContext<PosDbContext>((proveedor, o) => o.UseSqlite(cadena)
    .AddInterceptors(new AlGuardarEnLaBase(proveedor.GetRequiredService<VersionesDeLaCopia>())));
builder.Services.AddSingleton<CatalogoEnMemoria>();
// Las fotos de los productos se preparan de una en una: leer una grande ocupa memoria por un momento (entrega 13, parte 2).
builder.Services.AddSingleton<PreparacionDeFotos>();
builder.Services.Configure<OpcionesTicket>(builder.Configuration.GetSection("Ticket"));

// La base contra apagones (entrega 10): si la vez anterior no se detuvo bien, se revisa la base antes de seguir. Si
// salió dañada, se avisa (también directo por Telegram si ni se puede migrar) y no se hacen respaldos.
builder.Services.TryAddSingleton<IRevisionRapida, RevisionRapidaSqlite>();
builder.Services.AddSingleton(proveedor => new RevisionAlArrancar(cadena, RutasServicio.CarpetaRespaldos(builder.Configuration, cadena),
    proveedor.GetRequiredService<IRevisionRapida>(), proveedor.GetRequiredService<ILogger<RevisionAlArrancar>>(),
    builder.Configuration.GetValue("Telegram:Activo", true) ? proveedor.GetRequiredService<ITelegram>() : null));
var respaldos = builder.Configuration.GetValue("Respaldos:Activo", true);
builder.Services.AddSingleton(proveedor => new ServicioRespaldos(cadena, RutasServicio.CarpetaRespaldos(builder.Configuration, cadena),
    builder.Configuration.GetValue("Respaldos:Dias", 30), proveedor.GetRequiredService<ILogger<ServicioRespaldos>>())
    {
        Activo = respaldos,
        Revision = proveedor.GetRequiredService<RevisionAlArrancar>(),
    });
if (respaldos)
    builder.Services.AddHostedService<RespaldoDiario>();
// La red de la tienda en el POS (docs/arquitectura.md, «La red de la matriz»): solo se contesta a la red de la tienda.
builder.Services.TryAddSingleton(new RedLocalDelServicio());
// La llave propia del servicio (pos.llave, junto a la base y fuera de ella): firma quién es. Se hace o se lee la primera vez que
// se usa (al dar de alta una caja o al probar quién es).
builder.Services.TryAddSingleton(proveedor => new Lazy<LlaveDelServicio>(() => LlaveDelServicio.CargarOCrear(
    LlaveDelServicio.Archivo(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cadena).DataSource),
    ex => proveedor.GetRequiredService<ILogger<LlaveDelServicio>>().LogWarning(ex,
        "La llave del servicio no se pudo leer (¿viene de otra PC?): se hizo otra. Las cajas que conocían la de antes se dan de alta otra vez."))));
if (builder.Configuration.GetValue("Descubrimiento:Activo", true))
    builder.Services.AddHostedService<DescubrimientoEnRed>();
// El vigía de la red: si hay internet y si contesta el router (lo dice en /api/estado, para la franja de las cajas), y la bitácora
// de red (registros\red-servicio-AAAA-MM-DD.log, junto a la base). Apagado en desarrollo (Red:Vigilar).
if (builder.Configuration.GetValue("Red:Vigilar", true))
{
    builder.Services.TryAddSingleton<IPruebasDeRed, PruebasDeRed>();
    builder.Services.TryAddSingleton<IRedDelEquipo>(new RedDelEquipo());
    builder.Services.AddSingleton<CajasQueLaten>();
    builder.Services.AddSingleton(proveedor =>
    {
        var reloj = proveedor.GetRequiredService<TimeProvider>();
        var bitacora = new BitacoraDeRed(RutasServicio.CarpetaRegistros(builder.Configuration, cadena), "red-servicio", reloj,
            builder.Configuration.GetValue("Red:Dias", BitacoraDeRed.DiasPorOmision));
        var cajas = proveedor.GetRequiredService<CajasQueLaten>();
        return new VigiaDeLaRed(bitacora, proveedor.GetRequiredService<IRedDelEquipo>(), proveedor.GetRequiredService<IPruebasDeRed>(), new OpcionesDelVigia
        {
            Quien = "la caja principal",
            AlTerminar = "se detuvo el servicio",
            Internet = ConfiguracionDeRed.Internet(builder.Configuration),
            NombreDelRouter = builder.Configuration.GetValue<string?>("Red:NombreDelRouter", "tendawifi.com") is { Length: > 0 } nombre ? nombre : null,
            Equipos = ConfiguracionDeRed.Equipos(builder.Configuration, proveedor.GetRequiredService<OpcionesEleventa>()),
            AlEmpezar = () => ConfiguracionDeRed.PorNombre(proveedor.GetRequiredService<OpcionesEleventa>()),
            Ademas = cajas.RevisarAsync,
        }, reloj);
    });
    builder.Services.AddHostedService<VigiaDeRedDelServicio>();
}

// Telegram: el bot vive en la caja principal; sin token (Telegram:Token o telegram-token.txt) no hace nada.
builder.Services.AddSingleton<PresenciaCajas>();
builder.Services.AddSingleton<ITelegram>(new TelegramBot(TelegramBot.LeerToken(builder.Configuration)));
builder.Services.AddSingleton<MotorTelegram>();
if (builder.Configuration.GetValue("Telegram:Activo", true))
    builder.Services.AddHostedService<TrabajadorTelegram>();

// La hora de los trabajos de fondo (la impresora, eleventa, los celulares); las pruebas la cambian.
builder.Services.TryAddSingleton(TimeProvider.System);
// El módulo de impresión (src/Impresiones): la impresora de cada sucursal, su IP está en la configuración de impresión
// (niveles, contador, hojas que salieron). Sus rutas van abajo, en el grupo «/api» de siempre.
builder.Services.AgregarImpresiones(builder.Configuration);

// El piloto en la caja 2: lee el catálogo y las existencias de eleventa cada pocos minutos (solo si Eleventa:Activo).
var opcionesEleventa = builder.Configuration.GetSection("Eleventa").Get<OpcionesEleventa>() ?? new OpcionesEleventa();
builder.Services.AddSingleton(opcionesEleventa);
// Antes de lo primero que escribe en eleventa cada día, el puente la respalda con gbak (las pruebas ponen uno de mentiras).
builder.Services.TryAddSingleton<PapeleriaPos.Eleventa.IEjecutorGbak, PapeleriaPos.Eleventa.EjecutorGbak>();
builder.Services.AddSingleton(proveedor => new PapeleriaPos.Eleventa.RespaldoDeEleventa(
    new PapeleriaPos.Eleventa.OpcionesRespaldoEleventa(opcionesEleventa.Gbak, RutasServicio.CarpetaRespaldosEleventa(opcionesEleventa, cadena),
        opcionesEleventa.DiasDeRespaldos),
    proveedor.GetRequiredService<PapeleriaPos.Eleventa.IEjecutorGbak>()));
builder.Services.AddSingleton<LecturaEleventa>();
builder.Services.AddHostedService(proveedor => proveedor.GetRequiredService<LecturaEleventa>());
// Y manda cada venta a eleventa (solo si Eleventa:Escribir), con su bandeja de salida.
builder.Services.AddSingleton<EnvioVentasEleventa>();
builder.Services.AddHostedService(proveedor => proveedor.GetRequiredService<EnvioVentasEleventa>());

// Un celular que apaga la pantalla mientras cuenta se desconecta: al volver retoma donde estaba si no pasó media hora.
builder.Services.AddRazorComponents().AddInteractiveServerComponents(o => o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30));
builder.Services.AddSingleton<SesionesCelular>();
var urlPropia = new Uri(builder.Configuration["Caja:Servicio"] ?? "http://localhost:5080");
// Sin impresora configurada, la caja del navegador muestra el ticket en pantalla; con "tcp://ip:9100", imprime.
// Para probar en el navegador lo que hace la ventana de Windows: Caja:PideAlta=true pasa por el alta, y Caja:BaseLocal
// (una ruta) la deja vender sin la caja principal de Caja:Servicio.
var conexion = new ConexionCaja { Servicio = urlPropia, PideAlta = builder.Configuration.GetValue("Caja:PideAlta", false) };
// La impresora de tickets por nombre («tcp://tickets:9100»): el estado de arriba avisa que se configure por IP.
builder.Services.AgregarInterfazCaja(conexion, Impresoras.Crear(builder.Configuration["Caja:Impresora"]), builder.Configuration["Caja:BaseLocal"],
    new OpcionesRedDeLaCaja { Impresora = builder.Configuration["Caja:Impresora"] });
// Caja:ActualizacionPrueba=true muestra Configuración › Actualización en el navegador: busca y baja, pero no instala.
if (builder.Configuration.GetValue("Caja:ActualizacionPrueba", false))
    builder.Services.AgregarActualizacionPrueba(new EquipoDeNavegador(), GitHubPrueba.RepositorioPorOmision);

var app = builder.Build();

// Antes de migrar, la revisión si se apagó de golpe; al detenerse bien (también al apagar Windows), se quita la marca.
var revision = app.Services.GetRequiredService<RevisionAlArrancar>();
revision.Revisar();
app.Lifetime.ApplicationStopped.Register(revision.Detenido);
// La caja de la prueba arranca este servicio y, al cerrarse, le cierra la entrada: se detiene bien y quita la marca.
if (builder.Configuration.GetValue("Prueba:DetenerAlCerrarEntrada", false))
    ModoPrueba.DetenerAlCerrarEntrada(Console.OpenStandardInput(), app.Lifetime.StopApplication);
using (var alcance = app.Services.CreateScope())
    await revision.PrepararAsync(alcance.ServiceProvider.GetRequiredService<PosDbContext>());

app.UseAntiforgery();
app.UsarLlavesDeCaja();
app.MapStaticAssets();
app.MapearApiCajas();
app.MapearApiCaja();
app.MapearApiConfiguracion();
app.MapearApiControl();
app.MapearApiUsuarios();
app.MapearApiDevoluciones();
app.MapearApiOtrosDias();
app.MapearApiContingencia();
app.MapearApiCopia();
app.MapearApiTelegram();
app.MapGroup("/api").ConErrores().SoloSuCaja().MapearApiImpresiones();
app.MapearApiRed();
app.MapearApiDescuentos();
app.MapearApiClientes();
app.MapearApiCredito();
app.MapearApiCatalogo();
app.MapearApiMostrador();
app.MapearApiFotos();
app.MapearApiTemporadas();
app.MapearApiCompras();
app.MapearApiReportesInventario();
app.MapearApiConteos();
app.MapearApiEtiquetas();
app.MapearApiPrueba();
app.MapearApiEleventa();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

/// <summary>Para las pruebas de la API (WebApplicationFactory).</summary>
public partial class Program;
