using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Servicio;
using PapeleriaPos.Servicio.Componentes;

// Servicio de la sucursal: el único que escribe en la base. Atiende la API de las cajas y, para probar sin la
// ventana de Windows, muestra la caja en el navegador (con vista previa del ticket en lugar de imprimir).
var builder = WebApplication.CreateBuilder(args);

var cadena = builder.Configuration.GetConnectionString("Pos") ?? "Data Source=pos.db";
builder.Services.AddDbContext<PosDbContext>(o => o.UseSqlite(cadena));
builder.Services.AddSingleton<CatalogoEnMemoria>();
builder.Services.Configure<OpcionesTicket>(builder.Configuration.GetSection("Ticket"));

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
var urlPropia = new Uri(builder.Configuration["Caja:Servicio"] ?? "http://localhost:5080");
// Sin impresora configurada, la caja del navegador muestra el ticket en pantalla; con "tcp://ip:9100", imprime.
builder.Services.AgregarInterfazCaja(urlPropia, cajaId: null, Impresoras.Crear(builder.Configuration["Caja:Impresora"]));

var app = builder.Build();

using (var alcance = app.Services.CreateScope())
    await BaseDeDatos.PrepararAsync(alcance.ServiceProvider.GetRequiredService<PosDbContext>());

app.UseAntiforgery();
app.MapStaticAssets();
app.MapearApiCaja();
app.MapearApiConfiguracion();
app.MapearApiControl();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

/// <summary>Para las pruebas de la API (WebApplicationFactory).</summary>
public partial class Program;
