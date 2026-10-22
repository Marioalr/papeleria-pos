using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Impresion;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// F4 en el cobro, como en eleventa (docs/paridad-eleventa.md, 2026-10-22): la nota del ticket, uno o dos renglones que se
/// escriben ahí mismo, en el panel del total, y salen en el ticket. Enter (o F4 otra vez) la deja, Esc la deja como estaba y
/// el cobro la lleva aunque no se haya dado Enter. También sin conexión, y se queda con el ticket en espera. La impresora de
/// prueba anota lo que le llega; nada depende de lo que tarda la PC.
/// </summary>
public sealed class NotaEnLaVentaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-nota-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static Task TeclaAsync(VentaConCopia v, string tecla, string? texto = null) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, texto));

    private static async Task<bool> NotaAbiertaAsync(VentaConCopia v) => (await v.Pantalla.LeerAsync()).Elementos.Any(e => e.Id == "nota-ticket");

    /// <summary>La venta que se acaba de cobrar, como quedó en la caja principal (por su folio, que dice la pantalla).</summary>
    private async Task<Venta> CobradaAsync(VentaConCopia v)
    {
        var folio = Regex.Match(await v.Pantalla.TextoAsync(), @"Venta ([A-Z]+)-(\d+) cobrada");
        Assert.True(folio.Success, "No se cobró.");
        var (serie, numero) = (folio.Groups[1].Value, int.Parse(folio.Groups[2].Value));
        await using var db = servicio.Base();
        return await db.Ventas.AsNoTracking().SingleAsync(x => x.Serie == serie && x.Folio == numero);
    }

    [Fact]
    public async Task F4_en_el_cobro_abre_la_nota_y_sale_en_el_ticket_impreso()
    {
        var impresora = new TicketeraDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);

        // «Nota», junto a «Pagó con», hace lo mismo que F4.
        Assert.NotNull(await v.Pantalla.BotonAsync("Nota"));
        await TeclaAsync(v, "F4-cobro");
        Assert.True(await NotaAbiertaAsync(v));
        await v.Pantalla.EscribirAsync("nota-ticket", "Entregar en la escuela,  salon 3-B");
        await v.Pantalla.TeclaAsync("nota-ticket", "Enter");

        Assert.False(await NotaAbiertaAsync(v));
        Assert.Contains("Nota: Entregar en la escuela, salon 3-B", PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "el ticket");
        Assert.Contains("Nota: Entregar en la escuela, salon 3-B", impresora.Salieron.Single());
        Assert.Equal("Entregar en la escuela, salon 3-B", (await CobradaAsync(v)).Nota);
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Esc_la_deja_como_estaba_F4_otra_vez_la_deja_y_el_cobro_la_lleva_sin_Enter()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);

        await TeclaAsync(v, "F4-cobro");
        await v.Pantalla.EscribirAsync("nota-ticket", "Uno");
        await TeclaAsync(v, "Escape");
        Assert.False(await NotaAbiertaAsync(v));
        Assert.DoesNotContain("Nota: Uno", await v.Pantalla.TextoAsync());
        // Esc en la nota no deja el ticket: sigue ahí, listo para cobrar.
        Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));

        await TeclaAsync(v, "F4-cobro");
        await v.Pantalla.EscribirAsync("nota-ticket", "Dos");
        await TeclaAsync(v, "F4-cobro");
        Assert.Contains("Nota: Dos", PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));

        // Abierta otra vez trae la que tiene; se cambia y se cobra sin dar Enter: el cobro la lleva.
        await TeclaAsync(v, "F4-cobro");
        Assert.Equal("Dos", (await v.Pantalla.PorIdAsync("nota-ticket")).Valor);
        await v.Pantalla.EscribirAsync("nota-ticket", "Tres");
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro");

        Assert.Equal("Tres", (await CobradaAsync(v)).Nota);
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Una_nota_que_no_cabe_no_deja_cobrar_y_lo_dice_ahi()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);

        await TeclaAsync(v, "F4-cobro");
        await v.Pantalla.EscribirAsync("nota-ticket", new string('x', NotaDelTicket.Maximo + 5));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        var texto = await v.Pantalla.TextoAsync();
        Assert.Contains($"La nota del ticket va hasta {NotaDelTicket.Maximo} letras", texto);
        Assert.DoesNotContain("Cobrado", texto);
        Assert.True(await NotaAbiertaAsync(v));
        Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task La_nota_se_queda_con_su_ticket_en_espera()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await TeclaAsync(v, "F4-cobro");
        await v.Pantalla.EscribirAsync("nota-ticket", "Pasa por el a las 5");
        await v.Pantalla.TeclaAsync("nota-ticket", "Enter");

        await TeclaAsync(v, "F6");
        await v.EscanearAsync(b.Codigo!);
        Assert.DoesNotContain("Pasa por el a las 5", await v.Pantalla.TextoAsync());
        await TeclaAsync(v, "F5");

        Assert.Contains("Nota: Pasa por el a las 5", PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Sin_conexion_la_nota_sale_en_el_ticket_y_sube_con_la_venta()
    {
        var impresora = new TicketeraDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
        var (a, _) = v.DosDeLaCopia();
        v.Red.Caida = true;
        await v.Contingencia.LatidoAsync();
        Assert.False(v.Contingencia.EnLinea);

        await v.EscanearAsync(a.Codigo!);
        await TeclaAsync(v, "F4-cobro");
        await v.Pantalla.EscribirAsync("nota-ticket", "Sin conexion, para Lupita");
        await v.Pantalla.TeclaAsync("nota-ticket", "Enter");
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "el ticket sin conexión");
        Assert.Contains("Nota: Sin conexion, para Lupita", impresora.Salieron.Single());

        v.Red.Caida = false;
        await v.Contingencia.LatidoAsync();
        Assert.Equal(0, v.Contingencia.Pendientes);
        Assert.Equal("Sin conexion, para Lupita", (await CobradaAsync(v)).Nota);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Con el cobro en camino, F4 y «Nota» ya no abren la nota (revisión del PR #133): lo que se escribiera ahí no llegaba al
    /// ticket, que sale con la que se mandó, y no se decía. Lo dice, y el ticket sale con la que llevaba.
    /// </summary>
    [Fact]
    public async Task Con_el_cobro_en_camino_la_nota_no_se_cambia_y_lo_dice()
    {
        var impresora = new TicketeraDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await TeclaAsync(v, "F4-cobro");
        await v.Pantalla.EscribirAsync("nota-ticket", "Primera");
        await v.Pantalla.TeclaAsync("nota-ticket", "Enter");

        v.Red.CobroRetenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var antes = v.Red.Pedidas.Count(p => p == "/api/ventas");
        var cobro = v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await VentaConCopia.EsperarAsync(() => v.Red.Pedidas.Count(p => p == "/api/ventas") > antes, "el cobro en camino");

        await TeclaAsync(v, "F4-cobro");
        Assert.False(await NotaAbiertaAsync(v));
        Assert.Contains(PantallaVenta.NotaConElCobroEnCamino, await v.Pantalla.TextoAsync());
        Assert.True((await v.Pantalla.ElementoAsync(e => e.Etiqueta == "button" && e.Clase == "vn-enlace" && e.Texto.Contains("Cambiar"), "«Cambiar F4»")).Desactivado);

        v.Red.CobroRetenido.TrySetResult();
        await cobro;
        await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "el ticket");
        Assert.Contains("Nota: Primera", impresora.Salieron.Single());
        Assert.Equal("Primera", (await CobradaAsync(v)).Nota);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// F4 con el cursor en el monto del abono (también es data-cobro) es Inventario, como siempre (revisión del PR #133): la nota
    /// es del cobro de la venta. caja.js ya manda F4 ahí; si una página todavía mandara «F4-cobro», la pantalla hace lo mismo.
    /// </summary>
    [Fact]
    public async Task F4_en_el_abono_va_a_Inventario()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, fondoAparte: true);

        await TeclaAsync(v, "Ctrl+B");
        await v.Pantalla.EsperarAsync(t => t.Contains("Abono a cuenta"), "el panel del abono");
        await TeclaAsync(v, "F4-cobro");

        await v.Pantalla.EsperarAsync(t => t.Contains("Solo las tareas que tienes"), "Inventario");
        Assert.DoesNotContain("Primero termina aquí", await v.Pantalla.TextoAsync());
        Assert.False(await NotaAbiertaAsync(v));
        Assert.Empty(v.Pantalla.Errores);
    }
}
