using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que se puede sin llave desde la misma PC del servicio (auditoría, 2026-11-04): antes, todo. En Edge, solo escribiendo direcciones,
/// salían los usuarios, los reportes y el hash de cada PIN. Ahora, lo que usan los scripts; la página del servicio entra con su llave interna.
/// </summary>
public class LaMismaPcTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    /// <summary>Como Kestrel: las llamadas sí dicen su dirección, la de esta misma PC.</summary>
    private sealed class DesdeEstaPc(string ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> siguiente) => app =>
        {
            app.Use((contexto, resto) =>
            {
                contexto.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                return resto(contexto);
            });
            siguiente(app);
        };
    }

    private HttpClient EstaPc(string ip = "127.0.0.1", string? host = null, string? llave = null, ServicioDePrueba? otro = null)
    {
        var http = (otro ?? servicio).Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new DesdeEstaPc(ip)))).CreateClient();
        if (host is not null)
            http.DefaultRequestHeaders.Host = host;
        if (llave is not null)
            http.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, llave);
        return http;
    }

    private async Task<(CajaDto Caja, Guid Dueno)> DatosAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))![0];
        var sesion = await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>();
        return (caja, sesion!.UsuarioId);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Sin_llave_no_salen_los_usuarios_ni_los_reportes_ni_los_pin(string ip)
    {
        var (caja, dueno) = await DatosAsync();
        var http = EstaPc(ip);

        foreach (var ruta in new[]
                 {
                     $"api/cajas/{caja.Id}/usuarios", $"api/cajas/{caja.Id}/paquete", $"api/cajas/{caja.Id}/usuarios-y-permisos?usuarioId={dueno}",
                     $"api/reportes/ventas?usuarioId={dueno}", "api/clientes", "API/CAJAS/" + caja.Id + "/USUARIOS",
                 })
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(ruta)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PutAsJsonAsync($"api/cajas/{caja.Id}/usuarios-y-permisos/{dueno}/pin", new { usuarioId = dueno, pinNuevo = "4444" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("api/eleventa/leer", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("api/bandeja/bitacora", new { })).StatusCode);
    }

    [Fact]
    public async Task Lo_que_usan_los_scripts_y_las_guias_sigue_sin_llave()
    {
        var (caja, dueno) = await DatosAsync();
        var http = EstaPc();

        // poner-clave-eleventa, poner-codigo-controlprint, actualizar, restaurar y las guías en Edge.
        foreach (var ruta in new[] { "api/estado", "api/eleventa", "api/eleventa/envios", "api/controlprint" })
            Assert.True((await http.GetAsync(ruta)).IsSuccessStatusCode, ruta);
        // juntar-datos-piloto: «Respaldar ahora», con el PIN de quien lo pide.
        Assert.NotEmpty(await http.GetFromJsonAsync<List<CajaDto>>("api/cajas") ?? []);
        Assert.True((await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).IsSuccessStatusCode);
        Assert.True((await http.GetAsync($"api/cajas/{caja.Id}/configuracion/respaldos?usuarioId={dueno}")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Una_pagina_de_internet_con_otro_host_no_pasa_ni_por_lo_de_los_scripts()
    {
        var http = EstaPc(host: "malo.example");
        foreach (var ruta in new[] { "api/eleventa", "api/cajas" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(ruta)).StatusCode);
        foreach (var host in new[] { "localhost:5080", "127.0.0.1:5080", "[::1]:5080" })
            Assert.True((await EstaPc(host: host).GetAsync("api/eleventa")).IsSuccessStatusCode, host);
    }

    [Fact]
    public async Task La_pagina_del_servicio_entra_con_su_llave_interna_sin_ser_una_caja()
    {
        var (caja, _) = await DatosAsync();
        var fabrica = servicio.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new DesdeEstaPc("127.0.0.1"))));
        var conexion = fabrica.Services.GetRequiredService<ConexionCaja>();
        Assert.False(string.IsNullOrEmpty(conexion.LlaveDeLaPagina));
        // Para la caja del navegador sigue siendo una caja sin alta: así crea la tienda en una base nueva.
        Assert.Null(conexion.Llave);

        // Como ClienteCaja en la página: lleva la llave en cada llamada.
        var cliente = new ClienteCaja(fabrica.CreateClient(), new ConexionCaja { Servicio = new Uri("http://localhost/"), LlaveDeLaPagina = conexion.LlaveDeLaPagina });
        Assert.Contains(await cliente.FichasAsync(caja.Id), f => f.Nombre.Length > 0);

        Assert.Equal(HttpStatusCode.Unauthorized, (await EstaPc(llave: "otra-cualquiera").GetAsync($"api/cajas/{caja.Id}/usuarios")).StatusCode);
    }

    [Fact]
    public async Task Con_la_llave_de_una_caja_se_entra_como_siempre()
    {
        var (caja, _) = await DatosAsync();
        var alta = await (await servicio.Fabrica.CreateClient().PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, null, "Caja L", "L", "PC-L"))).Content.ReadFromJsonAsync<AltaCajaDto>();

        Assert.True((await EstaPc(llave: alta!.Llave).GetAsync($"api/cajas/{alta.Caja.Id}/usuarios")).IsSuccessStatusCode);
    }

    /// <summary>Lo que hace el flujo «Instalación» de GitHub al restaurar desde la API: la lista y restaurar piden la llave, y con otro PIN no es por falta de ella.</summary>
    [Fact]
    public async Task Restaurar_un_respaldo_pide_la_llave_aun_desde_esta_pc()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var http = EstaPc(otro: propio);
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))![0];
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!.UsuarioId;
        var respaldos = $"api/cajas/{caja.Id}/configuracion/respaldos";
        var restaurar = new SolicitudRestaurar(dueno, "1111", "pos-2026-11-04-ahora-0000.db");

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"{respaldos}/lista?usuarioId={dueno}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync($"{respaldos}/restaurar", restaurar)).StatusCode);

        var alta = await (await http.PostAsJsonAsync("api/alta", new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, caja.Id, null, null, "PC-DE-PRUEBA"))).Content.ReadFromJsonAsync<AltaCajaDto>();
        var conLlave = EstaPc(otro: propio, llave: alta!.Llave);
        Assert.True((await conLlave.GetAsync($"{respaldos}/lista?usuarioId={dueno}")).IsSuccessStatusCode);
        var rechazo = await conLlave.PostAsJsonAsync($"{respaldos}/restaurar", restaurar);
        Assert.Equal(HttpStatusCode.BadRequest, rechazo.StatusCode);
    }

    [Fact]
    public async Task La_caja_de_la_prueba_con_una_persona_sigue_sin_alta_con_su_servicio()
    {
        await using var prueba = await ServicioDePrueba.CrearAsync(("Prueba:Activa", "true"));
        var caja = (await prueba.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))![0];

        Assert.True((await EstaPc(otro: prueba).GetAsync($"api/cajas/{caja.Id}/usuarios")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EstaPc(otro: prueba, host: "malo.example").GetAsync($"api/cajas/{caja.Id}/usuarios")).StatusCode);
    }
}
