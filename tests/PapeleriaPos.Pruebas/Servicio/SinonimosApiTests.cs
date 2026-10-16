using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Los sinónimos de la tienda: los da de alta quien edita el catálogo, y la búsqueda los usa en línea y sin conexión.</summary>
public sealed class SinonimosApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-local-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<List<ResultadoBusquedaDto>> BuscarAsync(HttpClient http, string texto, Guid sucursalId) =>
        (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q={Uri.EscapeDataString(texto)}&sucursalId={sucursalId}"))!;

    [Fact]
    public async Task Los_da_de_alta_quien_edita_el_catalogo_y_la_busqueda_los_usa()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        // La base de prueba tiene un «Bolígrafo multicolor», pero ninguno de punto fino.
        Assert.Empty(await BuscarAsync(http, "boligrafo punto fino", caja.SucursalId));

        var delCajero = await http.PostAsJsonAsync("api/catalogo/sinonimos", new SolicitudSinonimo(cajero.UsuarioId, caja.SucursalId, "boligrafo", "pluma", true));
        var dosPalabras = await http.PostAsJsonAsync("api/catalogo/sinonimos", new SolicitudSinonimo(encargado.UsuarioId, caja.SucursalId, "papel china", "lustre", false));
        var lista = await Leer<List<SinonimoDto>>(await http.PostAsJsonAsync("api/catalogo/sinonimos",
            new SolicitudSinonimo(encargado.UsuarioId, caja.SucursalId, " Bolígrafo ", "pluma", AlReves: true)));

        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, dosPalabras.StatusCode);
        // Sin acentos, en mayúsculas y también al revés.
        Assert.Equal([("BOLIGRAFO", "PLUMA"), ("PLUMA", "BOLIGRAFO")], lista.Select(s => (s.Palabra, s.Equivale)));

        // «boligrafo» encuentra las plumas, y dice por qué; desde 4 letras también vale el principio.
        var plumas = await BuscarAsync(http, "boligrafo punto fino", caja.SucursalId);
        Assert.NotEmpty(plumas);
        Assert.All(plumas, r => Assert.StartsWith("Pluma punto fino", r.Descripcion));
        Assert.All(plumas, r => Assert.Equal("boligrafo", r.PorSinonimo));
        Assert.NotEmpty(await BuscarAsync(http, "bolig gel", caja.SucursalId));
        Assert.Empty(await BuscarAsync(http, "bol gel", caja.SucursalId));
        // Lo que ya se llama así no lleva sinónimo.
        Assert.All(await BuscarAsync(http, "pluma punto fino", caja.SucursalId), r => Assert.Null(r.PorSinonimo));

        // Quitarlo: ya no encuentra nada.
        foreach (var s in lista)
            await Leer<List<SinonimoDto>>(await http.PostAsJsonAsync($"api/catalogo/sinonimos/{s.Id}/quitar", new SolicitudQuitarSinonimo(encargado.UsuarioId, caja.SucursalId)));
        Assert.Empty(await http.GetFromJsonAsync<List<SinonimoDto>>("api/catalogo/sinonimos") ?? []);
        Assert.Empty(await BuscarAsync(http, "boligrafo punto fino", caja.SucursalId));
    }

    [Fact]
    public async Task Sin_conexion_la_caja_busca_con_los_sinonimos_de_su_copia()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await local.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        await Leer<List<SinonimoDto>>(await local.PostAsJsonAsync("api/catalogo/sinonimos",
            new SolicitudSinonimo(encargado.UsuarioId, caja.SucursalId, "rotulador", "plumon", AlReves: false)));
        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);
        await contingencia.LatidoAsync();

        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        var plumones = await cliente.BuscarAsync("rotulador", caja.SucursalId);

        Assert.False(contingencia.EnLinea);
        Assert.NotEmpty(plumones);
        Assert.All(plumones, r => Assert.StartsWith("Plum", r.Descripcion, StringComparison.OrdinalIgnoreCase));
        Assert.All(plumones, r => Assert.Equal("rotulador", r.PorSinonimo));
    }
}
