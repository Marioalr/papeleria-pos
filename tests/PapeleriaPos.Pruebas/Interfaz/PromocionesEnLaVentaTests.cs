using System.Globalization;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;
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

    // ── Revisión del PR #132 ────────────────────────────────────────────────

    /// <summary>
    /// Sin conexión, un precio cambiado con Ctrl+E al mismo precio de la copia (con su autorización) se cobra como precio
    /// cambiado, sin la promoción que el producto tiene: antes no se cobraba nunca («Cambió la promoción…»).
    /// </summary>
    [Fact]
    public async Task Sin_conexion_un_precio_cambiado_al_de_la_copia_se_cobra_sin_promocion()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var producto = SinMayoreo(v)[5];
        var precio = producto.Precios[0].PrecioCentavos;
        var promocion = await PromocionAsync(servicio, v, "10 % del precio cambiado", TipoPromocion.Porcentaje, producto.VarianteId, centesimas: 1000);
        await CopiaAlDiaAsync(v, promocion.Id);
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        v.Red.Caida = true;
        await v.Contingencia.LatidoAsync();
        Assert.False(v.Contingencia.EnLinea);
        var autorizacion = await v.Cliente.AutorizarAsync(new SolicitudAutorizacion(AccionControlada.CambiarPrecio, v.Caja.SucursalId, v.Caja.Id,
            v.Cajero.UsuarioId, "dueno", "3333", precio, "Precio"));

        var ticket = v.Contingencia.Cobrar(new SolicitudCobro(v.Caja.Id, turno.Id, v.Cajero.UsuarioId,
            [new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000, producto.Descripcion, precio, autorizacion.Id)],
            [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)], VentaId: Ids.Nuevo(), DiaPromociones: v.Contingencia.Hoy));

        Assert.Equal((precio, (string?)null), (ticket.TotalCentavos, ticket.Lineas[0].Promocion));
    }

    /// <summary>
    /// Una caja con el reloj dos días atrás: la caja principal no acepta su día (no es casi medianoche) y cobra con el suyo; su 409
    /// lo dice. La caja pone el ticket al día con ese día (la promoción que para ella todavía no empezaba), avisa que la fecha de la
    /// PC está mal, y el siguiente cobro pasa: no se repite.
    /// </summary>
    [Fact]
    public async Task Con_la_fecha_de_la_pc_mal_el_ticket_se_pone_al_dia_con_la_de_la_principal_y_lo_dice()
    {
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
        var mediodia = new DateTimeOffset(HoraMexico.AUtc(hoy.ToDateTime(new TimeOnly(12, 0))), TimeSpan.Zero);
        await using var propio = await ServicioDePrueba.CrearAsync(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(new RelojFijo(mediodia));
        });
        await using var v = await VentaConCopia.AbrirAsync(propio, _baseLocal, reloj: new RelojFijo(mediodia.AddDays(-2)));
        var producto = SinMayoreo(v)[6];
        var precio = producto.Precios[0].PrecioCentavos;
        var promocion = await PromocionAsync(propio, v, "Desde hoy", TipoPromocion.Porcentaje, producto.VarianteId, centesimas: 2000, desde: hoy,
            hasta: hoy.AddDays(10));
        await CopiaAlDiaAsync(v, promocion.Id);
        await v.EscanearAsync(producto.Codigo!);
        // Para la caja todavía no empieza.
        Assert.DoesNotContain("ahorra", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        await v.Pantalla.EsperarAsync(t => PantallaSinNavegador.Limpio(t).Contains("no es la de la caja principal"), "el aviso de la fecha");
        Assert.Contains($"La fecha de esta computadora ({hoy.AddDays(-2).ToString("d/MM/yyyy", CultureInfo.InvariantCulture)}) no es la de la caja principal "
            + $"({hoy.ToString("d/MM/yyyy", CultureInfo.InvariantCulture)})", PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));
        Assert.Contains("Desde hoy · ahorra", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Equal(hoy, v.Contingencia.Hoy);
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro");
        Assert.Equal(precio - Redondeo.Dividir(precio * 2000, 10000), (await UltimoTicketAsync(propio, v)).TotalCentavos);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Ctrl+E en un renglón con promoción dice en cuánto va con ella y que cambiarle el precio se la quita; F11 dice que va con la
    /// promoción (no marca una escala como «así va ahora»).
    /// </summary>
    [Fact]
    public async Task Ctrl_E_y_F11_dicen_que_el_renglon_va_con_su_promocion()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var producto = SinMayoreo(v)[7];
        var precio = producto.Precios[0].PrecioCentavos;
        var promocion = await PromocionAsync(servicio, v, "3×2 del renglón", TipoPromocion.LlevaNPagaM, producto.VarianteId, lleva: 3, paga: 2);
        await CopiaAlDiaAsync(v, promocion.Id);
        await v.EscanearAsync($"3*{producto.Codigo}");

        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("Ctrl+E", ""));
        var enCtrlE = PantallaSinNavegador.Limpio((await v.Pantalla.EsperarElementoAsync("precio-con-promocion")).Texto);
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("Escape", null));
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F11", ""));
        var enF11 = PantallaSinNavegador.Limpio((await v.Pantalla.EsperarElementoAsync("mayoreo-con-promocion")).Texto);

        var porPieza = (long)Math.Round(2m * precio / 3, MidpointRounding.AwayFromZero);
        Assert.Equal($"Con la promoción «3×2 del renglón» va en {Formato.Dinero(2 * precio)} ({Formato.Dinero(porPieza)} c/u). "
            + "Si le cambias el precio, ya no lleva la promoción.", enCtrlE);
        Assert.Contains($"Así va ahora: con la promoción «3×2 del renglón», {Formato.Dinero(2 * precio)}", enF11);
        Assert.Empty(v.Pantalla.Errores);
    }
}
