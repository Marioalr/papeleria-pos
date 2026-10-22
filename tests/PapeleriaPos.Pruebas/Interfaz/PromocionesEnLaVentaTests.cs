using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Las promociones en la venta de una caja con su copia (fase 3): el renglón dice la promoción y lo que se ahorra, como el
/// mayoreo, y el total lo que se ahorró; el cobro la cobra. Si la caja principal ya aplica otra cosa (empezó, terminó o
/// cambió después de la copia), no se cobra: el ticket se pone al día con las promociones que dijo y el panel lo dice. Y a
/// medianoche la caja aplica la que empieza sin bajar nada, también sin conexión, con su fecha.
/// </summary>
public sealed class PromocionesEnLaVentaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-promociones-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    /// <summary>Lo de la copia que se vende por pieza a un solo precio: una pieza cuesta lo que dice su precio.</summary>
    private static List<ProductoVentaDto> SinMayoreo(VentaConCopia v) => v.DeLaCopia().Where(p => p.Precios.Count == 1).ToList();

    /// <summary>Una promoción de un producto, dada de alta por el dueño en la caja principal; el catálogo en memoria, al día.</summary>
    private static async Task<PromocionFichaDto> PromocionAsync(ServicioDePrueba s, VentaConCopia v, string nombre, TipoPromocion tipo, Guid variante,
        int lleva = 0, int paga = 0, int centesimas = 0, DateOnly? desde = null, DateOnly? hasta = null)
    {
        var http = s.Fabrica.CreateClient();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
        var respuesta = await http.PostAsJsonAsync("api/promociones", new SolicitudPromocion(dueno.UsuarioId, v.Caja.SucursalId, v.Caja.Id, nombre, tipo,
            lleva, paga, centesimas, desde ?? hoy.AddDays(-1), hasta ?? hoy.AddDays(30), DiasDeLaSemana.Todos, false, [new AlcanceSolicitadoDto(VarianteId: variante)],
            Id: Guid.NewGuid()));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        await s.CatalogoAlDiaAsync();
        return (await respuesta.Content.ReadFromJsonAsync<PromocionFichaDto>())!;
    }

    /// <summary>La copia de la caja, al día con la caja principal (con la promoción).</summary>
    private static async Task CopiaAlDiaAsync(VentaConCopia v, Guid promocion)
    {
        await v.Contingencia.LatidoAsync();
        await VentaConCopia.EsperarAsync(() => v.Contingencia.Paquete!.Promociones!.Any(p => p.Id == promocion), "la promoción en la copia");
    }

    private static async Task<TicketDto> UltimoTicketAsync(ServicioDePrueba s, VentaConCopia v)
    {
        var http = s.Fabrica.CreateClient();
        var ultima = (await http.GetFromJsonAsync<List<VentaDelDiaDto>>($"api/cajas/{v.Caja.Id}/ventas-del-dia"))!.OrderByDescending(x => x.Folio, StringComparer.Ordinal).First();
        return (await http.GetFromJsonAsync<TicketDto>($"api/ventas/{ultima.Id}/ticket"))!;
    }

    [Fact]
    public async Task El_renglon_dice_la_promocion_el_total_lo_que_se_ahorra_y_el_cobro_la_cobra()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var producto = SinMayoreo(v)[2];
        var precio = producto.Precios[0].PrecioCentavos;
        var promocion = await PromocionAsync(servicio, v, "3×2 de prueba", TipoPromocion.LlevaNPagaM, producto.VarianteId, lleva: 3, paga: 2);
        await CopiaAlDiaAsync(v, promocion.Id);

        await v.EscanearAsync($"3*{producto.Codigo}");

        var renglon = Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains($"3×2 de prueba · ahorra {Formato.Dinero(precio)}", renglon);
        Assert.EndsWith(Formato.Dinero(2 * precio), renglon);
        Assert.Contains($"Ahorro por promociones {Formato.Dinero(precio)}", PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro");
        var ticket = await UltimoTicketAsync(servicio, v);
        Assert.Equal(2 * precio, ticket.TotalCentavos);
        Assert.Equal(("3×2 de prueba", precio), (ticket.Lineas[0].Promocion, ticket.Lineas[0].AhorroPromocionCentavos));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// La promoción se dio de alta después de la copia y la caja todavía no la tiene: la caja principal no cobra un precio que el
    /// cajero no vio. El ticket se pone al día con las promociones que dijo (sin esperar la copia) y el panel lo dice; al cobrar
    /// otra vez, con la promoción que ya vio.
    /// </summary>
    [Fact]
    public async Task Si_la_caja_principal_ya_aplica_una_promocion_el_ticket_se_pone_al_dia_y_lo_dice()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var producto = SinMayoreo(v)[3];
        var precio = producto.Precios[0].PrecioCentavos;
        await v.EscanearAsync($"2*{producto.Codigo}");
        Assert.DoesNotContain("ahorra", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        await PromocionAsync(servicio, v, "Segundo a mitad", TipoPromocion.SegundoAMitad, producto.VarianteId);

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        var nuevoTotal = 2 * precio - Redondeo.Dividir(precio, 2);
        Assert.Contains($"Cambió el precio de «{producto.Descripcion}»: revisa el total ({Formato.Dinero(nuevoTotal)}) y cobra otra vez.", await v.Pantalla.TextoAsync());
        Assert.Contains("Segundo a mitad · ahorra", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro");
        Assert.Equal(nuevoTotal, (await UltimoTicketAsync(servicio, v)).TotalCentavos);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// A medianoche, con el reloj de la caja: lo que se escanea antes va sin la promoción de mañana; lo que se escanea después (o
    /// se vuelve a calcular), con ella, sin bajar nada. Sin conexión se cobra igual, con la copia y la fecha de la caja, y sube
    /// como se cobró.
    /// </summary>
    [Fact]
    public async Task A_medianoche_la_caja_aplica_la_que_empieza_sin_bajar_nada_y_sin_conexion_la_cobra_igual()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
        var reloj = new RelojFijo(new DateTimeOffset(HoraMexico.AUtc(hoy.ToDateTime(new TimeOnly(23, 59))), TimeSpan.Zero));
        await using var v = await VentaConCopia.AbrirAsync(propio, _baseLocal, reloj: reloj);
        var producto = SinMayoreo(v)[4];
        var precio = producto.Precios[0].PrecioCentavos;
        var promocion = await PromocionAsync(propio, v, "Empieza mañana", TipoPromocion.Porcentaje, producto.VarianteId, centesimas: 2000,
            desde: hoy.AddDays(1), hasta: hoy.AddDays(10));
        await CopiaAlDiaAsync(v, promocion.Id);
        var pedidas = v.Red.Todas;

        await v.EscanearAsync(producto.Codigo!);
        Assert.DoesNotContain("ahorra", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));

        // Pasa la medianoche: el siguiente escaneo (que suma al mismo renglón) ya lleva la promoción.
        reloj.Ahora = reloj.Ahora.AddMinutes(2);
        await v.EscanearAsync(producto.Codigo!);
        var conDescuento = 2 * precio - Redondeo.Dividir(2 * precio * 2000, 10000);
        var renglon = Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains("Empieza mañana · ahorra", renglon);
        Assert.EndsWith(Formato.Dinero(conDescuento), renglon);
        // Sin bajar nada: ni la copia ni sus cambios.
        Assert.Equal(0, v.Red.Pedidas.Skip(pedidas).Count(r => r.Contains("/paquete", StringComparison.Ordinal) || r.Contains("/cambios", StringComparison.Ordinal)));

        // Sin conexión: con la copia y la fecha de la caja.
        v.Red.Caida = true;
        await v.Contingencia.LatidoAsync();
        Assert.False(v.Contingencia.EnLinea);
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro sin conexión");

        v.Red.Caida = false;
        await v.Contingencia.LatidoAsync();
        Assert.Equal(0, v.Contingencia.Pendientes);
        await using var db = propio.Base();
        var subida = await db.Ventas.AsNoTracking().Include(x => x.Lineas).Where(x => x.EnContingencia).OrderByDescending(x => x.PagadaUtc).FirstAsync();
        var linea = Assert.Single(subida.Lineas);
        Assert.Equal((conDescuento, promocion.Id, "Empieza mañana"), (subida.TotalCentavos, linea.PromocionId!.Value, linea.Promocion));
        Assert.Empty(v.Pantalla.Errores);
    }
}
