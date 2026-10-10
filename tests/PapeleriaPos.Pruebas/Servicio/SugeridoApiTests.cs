using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Servicio;

public class SugeridoApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task El_sugerido_sale_por_proveedor_y_cada_grupo_se_vuelve_una_orden()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var url = $"api/sucursales/{caja.SucursalId}/sugerido";

        var delCajero = await http.GetAsync($"{url}?usuarioId={cajero.UsuarioId}");
        var malos = await http.PutAsJsonAsync($"{url}/parametros", new SolicitudParametrosSugerido(encargado.UsuarioId, new ParametrosSugeridoDto(0, 7)));
        var parametros = await Leer<ParametrosSugeridoDto>(await http.PutAsJsonAsync($"{url}/parametros",
            new SolicitudParametrosSugerido(encargado.UsuarioId, new ParametrosSugeridoDto(30, 10))));
        var sugerido = await Leer<SugeridoDto>(await http.GetAsync($"{url}?usuarioId={encargado.UsuarioId}"));

        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malos.StatusCode);
        Assert.Equal(new ParametrosSugeridoDto(30, 10), parametros);
        Assert.Equal(parametros, sugerido.Parametros);
        Assert.NotEmpty(sugerido.Proveedores);
        Assert.All(sugerido.Proveedores.SelectMany(p => p.Renglones), r =>
        {
            Assert.True(r.Paquetes > 0 && r.VentaDiaria > 0);
            Assert.True(r.Maximo >= r.Minimo);
            Assert.True(r.Existencia + r.EnCamino < r.Maximo, r.Producto);
        });

        var grupo = sugerido.Proveedores.First(p => p.ProveedorId is not null);
        var elegidos = grupo.Renglones.Take(3).ToList();
        var orden = await Leer<OrdenDto>(await http.PostAsJsonAsync($"{url}/orden", new SolicitudOrdenSugerida(encargado.UsuarioId, grupo.ProveedorId!.Value,
            elegidos.Select(r => new RenglonOrdenSugeridaDto(r.VarianteId, r.PresentacionId, r.Paquetes)).ToList())));
        var despues = await Leer<SugeridoDto>(await http.GetAsync($"{url}?usuarioId={encargado.UsuarioId}"));
        var vacia = await http.PostAsJsonAsync($"{url}/orden", new SolicitudOrdenSugerida(encargado.UsuarioId, grupo.ProveedorId.Value, []));

        Assert.Equal((EstadoOrden.Abierta, grupo.ProveedorId, elegidos.Count), (orden.Estado, orden.ProveedorId, orden.Renglones.Count));
        // Lo que ya se pidió viene en camino: ya no se sugiere (o se sugiere menos).
        foreach (var r in elegidos)
        {
            var otra = despues.Proveedores.SelectMany(p => p.Renglones).FirstOrDefault(x => x.VarianteId == r.VarianteId);
            Assert.True(otra is null || (otra.EnCamino > r.EnCamino && otra.Paquetes < r.Paquetes), r.Producto);
        }
        Assert.Equal(HttpStatusCode.BadRequest, vacia.StatusCode);

        using var alcance = servicio.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        Assert.Equal("Del sugerido de compra", (await db.OrdenesCompra.SingleAsync(o => o.Id == orden.Id)).Notas);
    }
}
