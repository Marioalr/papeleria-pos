using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Telegram;

namespace PapeleriaPos.Pruebas.Servicio;

public class AjustesApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record Escenario(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno, Guid Variante);

    /// <summary>Un producto nuevo con 30 piezas a $10.00 cada una (entraron con una compra).</summary>
    private async Task<Escenario> PrepararAsync(string nombre)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var opciones = (await http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!;
        var ficha = await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
                [new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [])], [new PrecioFichaDto(null, 1000, 2000)])]))));
        var variante = ficha.Variantes[0];
        var proveedor = (await http.GetFromJsonAsync<List<ProveedorDto>>("api/proveedores"))!.First();
        var compra = await Leer<CompraDto>(await http.PostAsJsonAsync("api/compras", new SolicitudAbrirCompra(encargado.UsuarioId, caja.SucursalId, proveedor.Id!.Value, null)));
        var pieza = variante.Presentaciones[0].Id!.Value;
        await Leer<CompraDto>(await http.PostAsJsonAsync($"api/compras/{compra.Id}/conteo", new SolicitudConteo(encargado.UsuarioId, variante.Id!.Value, pieza, 30000)));
        await Leer<CompraDto>(await http.PostAsJsonAsync($"api/compras/{compra.Id}/terminar-conteo", new SolicitudCompra(encargado.UsuarioId)));
        await Leer<CompraDto>(await http.PostAsJsonAsync($"api/compras/{compra.Id}/renglones",
            new SolicitudRenglonCompra(encargado.UsuarioId, null, variante.Id!.Value, pieza, 30000, 30000)));
        await Leer<CompraDto>(await http.PostAsJsonAsync($"api/compras/{compra.Id}/cerrar", new SolicitudCompra(encargado.UsuarioId)));
        return new Escenario(http, caja, cajero, encargado, dueno, variante.Id!.Value);
    }

    private async Task<(long Existencia, long Costo)> ExistenciaAsync(Guid variante)
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var e = await alcance.ServiceProvider.GetRequiredService<PosDbContext>().Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == variante);
        return (e.Cantidad.Milesimas, e.CostoCentavos);
    }

    [Fact]
    public async Task El_cajero_registra_una_merma_chica_y_una_grande_la_autoriza_el_encargado()
    {
        var e = await PrepararAsync("Plastilina de prueba merma");

        var chica = await Leer<AjusteInventarioDto>(await e.Http.PostAsJsonAsync("api/inventario/mermas",
            new SolicitudMerma(e.Cajero.UsuarioId, e.Caja.SucursalId, e.Variante, 2000, CausaMerma.Danado, "Se mojaron")));
        var sinMotivo = await e.Http.PostAsJsonAsync("api/inventario/mermas",
            new SolicitudMerma(e.Cajero.UsuarioId, e.Caja.SucursalId, e.Variante, 1000, CausaMerma.Otro, null));
        var grande = new SolicitudMerma(e.Cajero.UsuarioId, e.Caja.SucursalId, e.Variante, 21000, CausaMerma.Caducado, null);
        var sinAutorizacion = await e.Http.PostAsJsonAsync("api/inventario/mermas", grande);
        var autorizacion = await Leer<AutorizacionDto>(await e.Http.PostAsJsonAsync("api/autorizaciones",
            new SolicitudAutorizacion(AccionControlada.Merma, e.Caja.SucursalId, null, e.Cajero.UsuarioId, "encargado", "2222", null, "Merma grande")));
        var autorizada = await Leer<AjusteInventarioDto>(await e.Http.PostAsJsonAsync("api/inventario/mermas", grande with { AutorizacionId = autorizacion.Id }));
        var (existencia, costo) = await ExistenciaAsync(e.Variante);
        var vistaCajero = await e.Http.GetFromJsonAsync<List<AjusteInventarioDto>>($"api/sucursales/{e.Caja.SucursalId}/ajustes?usuarioId={e.Cajero.UsuarioId}");

        Assert.Equal((TipoMovimiento.Merma, -2m, "Dañado: Se mojaron", "Cajero de prueba"), (chica.Tipo, chica.Cantidad, chica.Motivo, chica.Usuario));
        Assert.Null(chica.CostoCentavos);
        Assert.Equal(HttpStatusCode.BadRequest, sinMotivo.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, sinAutorizacion.StatusCode);
        Assert.Equal("requiere-autorizacion", (await sinAutorizacion.Content.ReadFromJsonAsync<ErrorDto>())!.Codigo);
        Assert.Equal(("Caducado", "Encargado de prueba"), (autorizada.Motivo, autorizada.Autorizo));
        Assert.Equal((7000L, 7000L), (existencia, costo));
        Assert.All(vistaCajero!.Where(a => a.Producto.StartsWith("Plastilina de prueba merma")), a => Assert.Null(a.CostoCentavos));
    }

    [Fact]
    public async Task Ajustar_corrige_a_la_existencia_real_con_su_costo_y_el_cajero_necesita_al_encargado()
    {
        var e = await PrepararAsync("Diamantina de prueba ajuste");

        var porCajero = await e.Http.PostAsJsonAsync("api/inventario/ajustes", new SolicitudAjuste(e.Cajero.UsuarioId, e.Caja.SucursalId, e.Variante, 28000, "Conté"));
        var abajo = await Leer<AjusteInventarioDto>(await e.Http.PostAsJsonAsync("api/inventario/ajustes",
            new SolicitudAjuste(e.Encargado.UsuarioId, e.Caja.SucursalId, e.Variante, 27000, "Conté el anaquel")));
        var arriba = await Leer<AjusteInventarioDto>(await e.Http.PostAsJsonAsync("api/inventario/ajustes",
            new SolicitudAjuste(e.Encargado.UsuarioId, e.Caja.SucursalId, e.Variante, 32000, "Había otra caja en la bodega")));
        var igual = await e.Http.PostAsJsonAsync("api/inventario/ajustes", new SolicitudAjuste(e.Encargado.UsuarioId, e.Caja.SucursalId, e.Variante, 32000, "Igual"));
        var sinMotivo = await e.Http.PostAsJsonAsync("api/inventario/ajustes", new SolicitudAjuste(e.Encargado.UsuarioId, e.Caja.SucursalId, e.Variante, 30000, " "));
        var (existencia, costo) = await ExistenciaAsync(e.Variante);

        Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
        Assert.Equal((-3m, -3000L), (abajo.Cantidad, abajo.CostoCentavos!.Value));
        // Lo que entra en un ajuste vale el costo promedio ($10.00).
        Assert.Equal((5m, 5000L), (arriba.Cantidad, arriba.CostoCentavos!.Value));
        Assert.Equal(HttpStatusCode.BadRequest, igual.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, sinMotivo.StatusCode);
        Assert.Equal((32000L, 32000L), (existencia, costo));
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        Assert.Contains(await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.AjustarInventario).Select(b => b.Detalle).ToListAsync(),
            d => d == "Ajustó «Diamantina de prueba ajuste»: de 30 a 27 (-3). Motivo: Conté el anaquel");
        Assert.Empty(await new RegistroInventario(db).VerificarExistenciasAsync());
    }

    [Fact]
    public async Task Desde_el_umbral_se_avisa_al_dueno_salvo_lo_que_hace_el()
    {
        var e = await PrepararAsync("Brillantina de prueba aviso");
        using (var alcance = servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            if (!db.ChatsTelegram.Any(c => c.ChatId == 6161))
            {
                db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = e.Dueno.UsuarioId, ChatId = 6161, VinculadoUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
        }

        await Leer<AjusteInventarioDto>(await e.Http.PostAsJsonAsync("api/inventario/mermas",
            new SolicitudMerma(e.Encargado.UsuarioId, e.Caja.SucursalId, e.Variante, 21000, CausaMerma.Perdido, "No aparecen")));
        await Leer<AjusteInventarioDto>(await e.Http.PostAsJsonAsync("api/inventario/mermas",
            new SolicitudMerma(e.Encargado.UsuarioId, e.Caja.SucursalId, e.Variante, 1000, CausaMerma.Danado, "Rota")));
        await Leer<AjusteInventarioDto>(await e.Http.PostAsJsonAsync("api/inventario/ajustes",
            new SolicitudAjuste(e.Dueno.UsuarioId, e.Caja.SucursalId, e.Variante, 30000, "Las encontré")));

        using var otro = servicio.Fabrica.Services.CreateScope();
        var avisos = await otro.ServiceProvider.GetRequiredService<PosDbContext>().MensajesTelegram.AsNoTracking()
            .Where(m => m.ChatId == 6161 && m.Tipo == TipoAlerta.AjusteInventario && m.Texto.Contains("Brillantina de prueba aviso")).Select(m => m.Texto).ToListAsync();
        var aviso = Assert.Single(avisos);
        Assert.Contains("Merma de 21 «Brillantina de prueba aviso»", aviso);
        Assert.Contains("$210.00", aviso);
    }
}
