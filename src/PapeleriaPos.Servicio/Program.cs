using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
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
builder.Host.UseWindowsService(o => o.ServiceName = "PapeleriaPos");

var cadena = RutasServicio.CadenaConexion(builder.Configuration);
builder.Services.AddDbContext<PosDbContext>(o => o.UseSqlite(cadena));
builder.Services.AddSingleton<CatalogoEnMemoria>();
builder.Services.Configure<OpcionesTicket>(builder.Configuration.GetSection("Ticket"));

var respaldos = builder.Configuration.GetValue("Respaldos:Activo", true);
builder.Services.AddSingleton(proveedor => new ServicioRespaldos(cadena, RutasServicio.CarpetaRespaldos(builder.Configuration, cadena),
    builder.Configuration.GetValue("Respaldos:Dias", 30), proveedor.GetRequiredService<ILogger<ServicioRespaldos>>()) { Activo = respaldos });
if (respaldos)
    builder.Services.AddHostedService<RespaldoDiario>();
if (builder.Configuration.GetValue("Descubrimiento:Activo", true))
    builder.Services.AddHostedService<DescubrimientoEnRed>();

// Telegram: el bot vive en la caja principal; sin token (Telegram:Token o telegram-token.txt) no hace nada.
builder.Services.AddSingleton<PresenciaCajas>();
builder.Services.AddSingleton<ITelegram>(new TelegramBot(TelegramBot.LeerToken(builder.Configuration)));
builder.Services.AddSingleton<MotorTelegram>();
if (builder.Configuration.GetValue("Telegram:Activo", true))
    builder.Services.AddHostedService<TrabajadorTelegram>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
var urlPropia = new Uri(builder.Configuration["Caja:Servicio"] ?? "http://localhost:5080");
// Sin impresora configurada, la caja del navegador muestra el ticket en pantalla; con "tcp://ip:9100", imprime.
// Para probar en el navegador lo que hace la ventana de Windows: Caja:PideAlta=true pasa por el alta, y Caja:BaseLocal
// (una ruta) la deja vender sin la caja principal de Caja:Servicio.
var conexion = new ConexionCaja { Servicio = urlPropia, PideAlta = builder.Configuration.GetValue("Caja:PideAlta", false) };
builder.Services.AgregarInterfazCaja(conexion, Impresoras.Crear(builder.Configuration["Caja:Impresora"]), builder.Configuration["Caja:BaseLocal"]);

var app = builder.Build();

using (var alcance = app.Services.CreateScope())
    await BaseDeDatos.PrepararAsync(alcance.ServiceProvider.GetRequiredService<PosDbContext>());

app.UseAntiforgery();
app.UsarLlavesDeCaja();
app.MapStaticAssets();
app.MapearApiCajas();
app.MapearApiCaja();
app.MapearApiConfiguracion();
app.MapearApiControl();
app.MapearApiDevoluciones();
app.MapearApiContingencia();
app.MapearApiTelegram();
app.MapearApiImpresiones();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

/// <summary>Para las pruebas de la API (WebApplicationFactory).</summary>
public partial class Program;
