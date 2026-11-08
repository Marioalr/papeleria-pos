using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Las curvas del margen por la API (2026-11-08): el probador y la comparación con lo impreso solo para quien ve los costos (revisado en el
/// servicio) y sin cambiar nada; elegirlas en los costos; el detalle del margen («curva: …») con el costo de cada impresión; y los márgenes
/// propios de la L5590.
/// </summary>
public sealed class CurvasDelMargenApiTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    private static CurvasMargenDto Curvas => new(
        [new(5, 300), new(20, 180), new(50, 110), new(100, 70)], [new(1, 100), new(10, 85), new(100, 65), new(500, 55)]);

    [Fact]
    public async Task El_probador_y_la_comparacion_son_de_quien_ve_los_costos_y_elegirlas_cambia_el_precio_con_su_detalle()
    {
        var (http, caja, cajero, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var curvas = $"api/sucursales/{caja.SucursalId}/impresiones/curvas";
        var costos = $"api/sucursales/{caja.SucursalId}/impresiones/costos";
        var ahora = servicio.Reloj.GetUtcNow().UtcDateTime;
        await ApiDeImpresoras.Leer<ImpresionPorCobrarDto>(await http.PostAsJsonAsync("api/impresiones",
            ApiDeImpresoras.Trabajo(caja, "Tarea.pdf", 10, ahora.AddHours(-2), null)));

        var pruebaDelCajero = await http.PostAsJsonAsync($"{curvas}/probar",
            new SolicitudProbarCurvas(cajero.UsuarioId, null, Curvas, 5, 10, false, "Carta", "Plain"));
        var comparacionDelCajero = await http.PostAsJsonAsync($"{curvas}/comparar", new SolicitudCompararCurvas(cajero.UsuarioId, null, Curvas));
        var prueba = await ApiDeImpresoras.Leer<PruebaCurvasDto>(await http.PostAsJsonAsync($"{curvas}/probar",
            new SolicitudProbarCurvas(dueno.UsuarioId, null, Curvas, 5, 10, false, "Carta", "Plain")));
        var comparacion = await ApiDeImpresoras.Leer<ComparacionCurvasDto>(await http.PostAsJsonAsync($"{curvas}/comparar",
            new SolicitudCompararCurvas(dueno.UsuarioId, null, Curvas)));
        var malas = await http.PostAsJsonAsync($"{curvas}/probar",
            new SolicitudProbarCurvas(dueno.UsuarioId, null, Curvas with { PorCaras = [new(1, 100)] }, 5, 10, false, "Carta", "Plain"));

        Assert.Equal(HttpStatusCode.Forbidden, pruebaDelCajero.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, comparacionDelCajero.StatusCode);
        Assert.Equal((300m, 85m, 255m), (prueba.MargenPorTinta, prueba.Factor, prueba.Margen));
        Assert.Equal(1, comparacion.Trabajos);
        Assert.True(comparacion.ConCurvasCentavos > comparacion.HoyCentavos);
        Assert.Equal(HttpStatusCode.BadRequest, malas.StatusCode);
        Assert.Contains("al menos 2 puntos", await malas.Content.ReadAsStringAsync());
        // Probar y comparar no cambia los costos.
        var antes = await ApiDeImpresoras.Leer<EstadoCostosImpresionDto>(await http.GetAsync($"{costos}?usuarioId={dueno.UsuarioId}"));
        Assert.Equal(MargenDeImpresiones.PorCostoDelTrabajo, antes.Costos.Margen);

        // Elegirlas, con sus puntos: lo que se imprime desde ahí sale con ellas, y su costo dice de dónde salió el margen.
        var elegidas = await ApiDeImpresoras.Leer<EstadoCostosImpresionDto>(await http.PutAsJsonAsync(costos,
            new SolicitudCostosImpresion(dueno.UsuarioId, antes.Costos with { Margen = MargenDeImpresiones.PorCurvas, Curvas = Curvas })));
        Assert.Equal(MargenDeImpresiones.PorCurvas, elegidas.Costos.Margen);
        Assert.Equal(Curvas.PorTinta, elegidas.Costos.Curvas!.PorTinta);
        var conCurvas = await ApiDeImpresoras.Leer<ImpresionPorCobrarDto>(await http.PostAsJsonAsync("api/impresiones",
            ApiDeImpresoras.Trabajo(caja, "Otra tarea.pdf", 10, ahora.AddHours(-1), null)));
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(ahora)).ToString("yyyy-MM-dd");
        var delDia = await ApiDeImpresoras.Leer<List<ImpresionDelDiaDto>>(
            await http.GetAsync($"api/sucursales/{caja.SucursalId}/impresiones?fecha={hoy}&usuarioId={dueno.UsuarioId}"));
        var suya = Assert.Single(delDia, d => d.Id == conCurvas.Id);
        Assert.Equal("curva: 5 % de tinta → 300 % × 85 % (10 caras)", suya.Costo!.Margen);
        Assert.Equal(255, suya.Costo.MargenPorcentaje);
        await using var db = servicio.Base();
        Assert.Contains(await db.Bitacora.Select(b => b.Detalle).ToListAsync(),
            d => d.Contains("cómo se calcula el margen") && d.Contains("la curva del margen por la tinta de cada cara"));
    }

    [Fact]
    public async Task La_L5590_tiene_sus_propios_margenes_con_permiso_y_vuelve_a_los_de_la_tienda()
    {
        var (http, caja, cajero, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ruta = ApiDeImpresoras.Ruta(caja);
        var costos = $"api/sucursales/{caja.SucursalId}/impresiones/costos";
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto(null, 10, 5))));
        var l5590 = await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ruta,
            new SolicitudNuevaImpresora(dueno.UsuarioId, "L5590", PerfilImpresora.L5590)));
        var estado = await ApiDeImpresoras.Leer<EstadoCostosImpresionDto>(await http.GetAsync($"{costos}?usuarioId={dueno.UsuarioId}"));
        var margenes = new MargenesImpresionDto(MargenDeImpresiones.Unico, 150, estado.Costos.TramosPorTrabajo, estado.Costos.TramosPorHoja, null, Curvas);

        var delCajero = await http.PutAsJsonAsync($"{ruta}/{l5590.Numero}/margenes", new SolicitudMargenesImpresora(cajero.UsuarioId, margenes));
        var suyos = await ApiDeImpresoras.Leer<EstadoCostosImpresionDto>(await http.PutAsJsonAsync($"{ruta}/{l5590.Numero}/margenes",
            new SolicitudMargenesImpresora(dueno.UsuarioId, margenes)));
        var deUnaWf = await http.PutAsJsonAsync($"{ruta}/1/margenes", new SolicitudMargenesImpresora(dueno.UsuarioId, margenes));
        var deVuelta = await ApiDeImpresoras.Leer<EstadoCostosImpresionDto>(await http.PutAsJsonAsync($"{ruta}/{l5590.Numero}/margenes",
            new SolicitudMargenesImpresora(dueno.UsuarioId, null)));

        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        var suya = Assert.Single(suyos.Impresoras!, p => p.Numero == l5590.Numero);
        Assert.Equal((MargenDeImpresiones.Unico, 150m), (suya.Margenes!.Margen, suya.Margenes.MargenUnico));
        // Los de la tienda no cambian.
        Assert.Equal(estado.Costos.Margen, suyos.Costos.Margen);
        Assert.Equal(HttpStatusCode.BadRequest, deUnaWf.StatusCode);
        Assert.Null(Assert.Single(deVuelta.Impresoras!, p => p.Numero == l5590.Numero).Margenes);
        await using var db = servicio.Base();
        var bitacora = await db.Bitacora.Select(b => b.Detalle).ToListAsync();
        Assert.Contains(bitacora, d => d.Contains("los márgenes de la L5590 (ahora los suyos)"));
        Assert.Contains(bitacora, d => d.Contains("los márgenes de la L5590 (vuelve a los de la sucursal)"));
        Assert.DoesNotContain(bitacora, d => d.Contains("150"));
    }
}
