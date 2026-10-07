using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Datos.Documentos;
using PapeleriaPos.Herramientas;
using PapeleriaPos.Herramientas.Ficticios;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Herramientas.Prueba;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Impresiones;
using PapeleriaPos.Nucleo.Telegram;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El servicio como lo arranca la caja de la prueba con una persona real: la base inicial la arma <c>preparar-prueba</c>
/// y la de trabajo todavía no existe (se copia al arrancar).
/// </summary>
public sealed class ServicioEnPrueba : IAsyncLifetime
{
    public string Carpeta { get; } = Path.Combine(Path.GetTempPath(), $"pos-prueba-real-{Guid.NewGuid():N}");
    public string BaseInicial => Path.Combine(Carpeta, "prueba.db");
    public string Ejemplos => Path.Combine(Carpeta, "Ejemplos");
    public WebApplicationFactory<Program> Fabrica { get; private set; } = null!;
    public List<string> Hecho { get; private set; } = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Carpeta);
        await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={BaseInicial};Pooling=False")))
        {
            await BaseDeDatos.PrepararAsync(db);
            await GuardadoLote.GuardarAsync(GeneradorFicticios.Generar(new OpcionesFicticios { Dias = 30 }), db);
            Hecho = await PreparacionPrueba.PrepararAsync(db, Ejemplos);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");
        }
        Fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:Pos", $"Data Source={Path.Combine(Carpeta, "pos.db")};Pooling=False")
            .UseSetting("Prueba:Activa", "true").UseSetting("Prueba:BaseInicial", BaseInicial).UseSetting("Prueba:CarpetaRespuestas", Carpeta)
            .UseSetting("Respaldos:Activo", "false").UseSetting("Telegram:Activo", "false"));
    }

    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Carpeta, recursive: true);
    }
}

public class PruebaApiTests(ServicioEnPrueba prueba) : IClassFixture<ServicioEnPrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task La_base_de_prueba_trae_avisos_simulados_impresiones_y_documentos_que_se_reconocen()
    {
        var http = prueba.Fabrica.CreateClient();
        var estado = await Leer<EstadoServicioDto>(await http.GetAsync("api/estado"));
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var factura = LectorDocumentos.Leer(await File.ReadAllBytesAsync(Path.Combine(prueba.Ejemplos, "Factura de ejemplo.xml")));
        var nota = LectorDocumentos.Leer(await File.ReadAllBytesAsync(Path.Combine(prueba.Ejemplos, "Nota de ejemplo.pdf")));
        var leeme = await File.ReadAllTextAsync(Path.Combine(prueba.Ejemplos, "Léeme.txt"));

        Assert.True(estado.Prueba);
        Assert.True(File.Exists(Path.Combine(prueba.Carpeta, "pos.db")));
        using var alcance = prueba.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        Assert.True(await db.ChatsTelegram.AnyAsync(c => c.ChatId == ChatTelegram.Simulado));
        Assert.Equal(4, await db.TrabajosImpresion.CountAsync(t => t.Cobro == EstadoCobroImpresion.PorCobrar && t.CajaId == caja.Id && t.PrecioCentavos > 0));
        // La factura trae tres códigos de barras de la base (llegan ligados) y una clave del proveedor (se liga a mano).
        Assert.Equal(4, factura.Lineas.Count);
        var codigos = factura.Lineas.Select(l => l.Codigo!).ToList();
        Assert.Equal(3, await db.CodigosBarras.CountAsync(c => codigos.Contains(c.Codigo)));
        Assert.Equal(3, nota.Lineas.Count);
        Assert.Contains(nota.Avisos, a => a.Contains("repartió"));
        Assert.Contains("cajero / 1111", leeme);
        Assert.Contains(factura.Lineas[0].Codigo!, leeme);
    }

    [Fact]
    public async Task Lo_que_se_le_avisaria_al_dueno_se_ve_en_la_caja()
    {
        var http = prueba.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var producto = (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=pluma&sucursalId={caja.SucursalId}"))!.First();

        await http.PostAsJsonAsync("api/catalogo/precio-menudeo", new SolicitudPrecioMenudeo(encargado.UsuarioId, caja.SucursalId, producto.VarianteId,
            producto.PrecioCentavos + 500));
        var avisos = await Leer<List<AvisoPruebaDto>>(await http.GetAsync("api/prueba/avisos"));

        Assert.Contains(avisos, a => a.Tipo == TipoAlerta.CambioPrecioCatalogo && a.Texto.Contains("Encargado de prueba"));
    }

    [Fact]
    public async Task Las_respuestas_se_guardan_y_se_exportan_a_un_archivo()
    {
        var http = prueba.Fabrica.CreateClient();
        var respuestas = new RespuestasPruebaDto("Ana", new Dictionary<string, RespuestaTareaDto>
        {
            ["vender"] = new(true, null),
            ["corte"] = new(false, "No encontré dónde se cuenta el efectivo"),
        }, "Todo claro");

        await Leer<RespuestasPruebaDto>(await http.PutAsJsonAsync("api/prueba/respuestas", respuestas));
        var leidas = await Leer<RespuestasPruebaDto>(await http.GetAsync("api/prueba/respuestas"));
        var texto = GuiaPrueba.Documento(leidas, new DateTime(2026, 10, 7, 12, 0, 0));
        var archivo = await Leer<ArchivoGuardadoDto>(await http.PostAsJsonAsync("api/prueba/respuestas/exportar", new SolicitudExportarRespuestas(texto)));

        Assert.Equal("Ana", leidas.Nombre);
        Assert.False(leidas.Tareas["corte"].SalioBien);
        Assert.Contains("Contestadas: 2 de", texto);
        Assert.Contains("- Lo que vio: No encontré dónde se cuenta el efectivo", texto);
        Assert.Equal(prueba.Carpeta, Path.GetDirectoryName(archivo.Ruta));
        Assert.Equal(texto, await File.ReadAllTextAsync(archivo.Ruta));
        File.Delete(archivo.Ruta);
    }

    [Fact]
    public async Task Empezar_de_cero_regresa_a_la_base_con_que_se_instalo()
    {
        var http = prueba.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        await Leer<ProveedorDto>(await http.PostAsJsonAsync("api/proveedores",
            new SolicitudGuardarProveedor(encargado.UsuarioId, caja.SucursalId, new ProveedorDto(null, "Proveedor creado en la prueba", null, null, null))));

        var reinicio = await http.PostAsync("api/prueba/empezar-de-cero", null);
        var proveedores = await http.GetFromJsonAsync<List<ProveedorDto>>("api/proveedores");
        var buscados = await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=pluma&sucursalId={caja.SucursalId}");

        Assert.Equal(HttpStatusCode.NoContent, reinicio.StatusCode);
        Assert.DoesNotContain(proveedores!, p => p.Nombre == "Proveedor creado en la prueba");
        Assert.NotEmpty(buscados!);
    }
}

public class SinPruebaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    [Fact]
    public async Task En_la_tienda_no_existen_las_rutas_de_la_prueba()
    {
        var http = servicio.Fabrica.CreateClient();

        var estado = await http.GetFromJsonAsync<EstadoServicioDto>("api/estado");
        var avisos = await http.GetAsync("api/prueba/avisos");
        var reinicio = await http.PostAsync("api/prueba/empezar-de-cero", null);

        Assert.False(estado!.Prueba);
        Assert.Equal(HttpStatusCode.NotFound, avisos.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reinicio.StatusCode);
    }
}
