using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Servicio;

public class EtiquetasApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado)> EntrarAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        return (http, caja, cajero, encargado);
    }

    [Fact]
    public async Task La_hoja_se_guarda_por_sucursal_y_solo_la_cambia_el_encargado()
    {
        var (http, caja, cajero, encargado) = await EntrarAsync();
        var url = $"api/sucursales/{caja.SucursalId}/etiquetas/configuracion";
        var propia = new FormatoEtiquetas("x", "x", 3, 7, 63.5m, 33.9m, 13m, 7m, 66m, 33.9m);

        var inicial = await Leer<ConfiguracionEtiquetasDto>(await http.GetAsync(url));
        var delCajero = await http.PutAsJsonAsync(url, new SolicitudConfiguracionEtiquetas(cajero.UsuarioId, new ConfiguracionEtiquetasDto("5167", null, 0, 0)));
        var noCabe = await http.PutAsJsonAsync(url, new SolicitudConfiguracionEtiquetas(encargado.UsuarioId,
            new ConfiguracionEtiquetasDto(FormatoEtiquetas.Propio, propia with { Filas = 9 }, 0, 0)));
        var guardada = await Leer<ConfiguracionEtiquetasDto>(await http.PutAsJsonAsync(url, new SolicitudConfiguracionEtiquetas(encargado.UsuarioId,
            new ConfiguracionEtiquetasDto(FormatoEtiquetas.Propio, propia, 15, -5))));

        Assert.Equal(("5160", 0), (inicial.FormatoId, inicial.AjusteXDecimas));
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noCabe.StatusCode);
        Assert.Equal((FormatoEtiquetas.Propio, 15, -5, 7), (guardada.FormatoId, guardada.AjusteXDecimas, guardada.AjusteYDecimas, guardada.Propio!.Filas));
        Assert.Equal(guardada, await Leer<ConfiguracionEtiquetasDto>(await http.GetAsync(url)));
    }

    [Fact]
    public async Task Lo_que_no_tiene_codigo_recibe_uno_interno_al_preparar_las_etiquetas()
    {
        var (http, caja, cajero, encargado) = await EntrarAsync();
        Guid sinCodigo, conCodigo, departamento;
        string codigoDeFabrica;
        using (var alcance = servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            var variantes = await db.Variantes.Include(v => v.Producto).Include(v => v.Presentaciones).ThenInclude(p => p.Codigos)
                .Where(v => v.Activa && v.Producto!.Activo && v.Producto.Tipo == TipoProducto.Mercancia && v.Presentaciones.Count == 1)
                .Take(2).ToListAsync();
            (sinCodigo, conCodigo, departamento) = (variantes[0].Id, variantes[1].Id, variantes[0].Producto!.DepartamentoId);
            db.CodigosBarras.RemoveRange(variantes[0].Presentaciones.SelectMany(p => p.Codigos));
            codigoDeFabrica = variantes[1].Presentaciones.SelectMany(p => p.Codigos).First().Codigo;
            await db.SaveChangesAsync();
        }

        var sinCodigos = await Leer<List<ProductoEtiquetaDto>>(await http.GetAsync($"api/etiquetas/sin-codigo?departamentoId={departamento}"));
        var solicitud = (SesionDto quien, bool precio) => new SolicitudEtiquetas(quien.UsuarioId, caja.SucursalId,
            [new RenglonEtiquetasDto(sinCodigo, 2), new RenglonEtiquetasDto(conCodigo, 1)], precio);
        var delCajero = await http.PostAsJsonAsync("api/etiquetas", solicitud(cajero, false));
        var preparadas = await Leer<EtiquetasDto>(await http.PostAsJsonAsync("api/etiquetas", solicitud(encargado, true)));
        var otraVez = await Leer<EtiquetasDto>(await http.PostAsJsonAsync("api/etiquetas", solicitud(cajero, false)));

        Assert.Contains(sinCodigos, p => p.VarianteId == sinCodigo && p.Codigo is null);
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal((3, 1), (preparadas.Etiquetas.Count, preparadas.CodigosNuevos));
        var nuevo = preparadas.Etiquetas[0].Codigo;
        Assert.True(nuevo.StartsWith('2') && Ean13.EsValido(nuevo), nuevo);
        Assert.Equal(nuevo, preparadas.Etiquetas[1].Codigo);
        Assert.Equal(codigoDeFabrica, preparadas.Etiquetas[2].Codigo);
        Assert.All(preparadas.Etiquetas, e => Assert.True(e.PrecioCentavos > 0));
        // Ya tiene código: la siguiente vez la prepara cualquiera, sin dar otro.
        Assert.Equal((0, nuevo), (otraVez.CodigosNuevos, otraVez.Etiquetas[0].Codigo));
        Assert.All(otraVez.Etiquetas, e => Assert.Null(e.PrecioCentavos));
        var producto = await Leer<ProductoVentaDto>(await http.GetAsync($"api/productos/codigo/{nuevo}?sucursalId={caja.SucursalId}"));
        Assert.Equal(sinCodigo, producto.VarianteId);

        using var otro = servicio.Fabrica.Services.CreateScope();
        var base_ = otro.ServiceProvider.GetRequiredService<PosDbContext>();
        Assert.True(await base_.Bitacora.AnyAsync(b => b.Accion == AccionControlada.EditarCatalogo && b.Detalle.EndsWith(nuevo)));
        Assert.Equal(OrigenCodigo.Interno, (await base_.CodigosBarras.SingleAsync(c => c.Codigo == nuevo)).Origen);
    }

    [Fact]
    public async Task De_una_compra_sale_una_etiqueta_por_pieza_recibida()
    {
        var (http, _, _, _) = await EntrarAsync();
        var compraId = Guid.NewGuid();
        Guid variante;
        using (var alcance = servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            var sucursal = await db.Sucursales.Select(s => s.Id).FirstAsync();
            var v = await db.Variantes.FirstAsync(x => x.UnidadBase == UnidadBase.Pieza && x.Producto!.Tipo == TipoProducto.Mercancia);
            variante = v.Id;
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(sucursal, v, TipoMovimiento.Compra, Cantidad.Piezas(12), 6000, DateTime.UtcNow,
                documentoTipo: nameof(Compra), documentoId: compraId));
        }

        var productos = await Leer<List<ProductoEtiquetaDto>>(await http.GetAsync($"api/compras/{compraId}/etiquetas"));
        var noCerrada = await http.GetAsync($"api/compras/{Guid.NewGuid()}/etiquetas");

        Assert.Equal((variante, 12), (productos.Single().VarianteId, productos.Single().Sugeridas));
        Assert.Equal(HttpStatusCode.BadRequest, noCerrada.StatusCode);
    }
}
