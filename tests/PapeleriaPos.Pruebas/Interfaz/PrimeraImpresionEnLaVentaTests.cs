using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Impresion;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// En «Ventas del día», el ticket que no salió al cobrar se imprime sin pedir motivo (paridad con eleventa, 2026-10-22): no es
/// una reimpresión. Vale para lo que se cobró con «Al cobrar» en «No imprimirlo» (u «Ofrecerlo» sin Ctrl+I) y para lo que no
/// salió de la impresora y se dejó con «No imprimirlo». Después, lo que se pida es una reimpresión, con su motivo. La
/// impresora de prueba anota lo que le llega; lo que se espera tiene su límite, no depende de lo que tarda la PC.
/// </summary>
public sealed class PrimeraImpresionEnLaVentaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-primera-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private async Task AlCobrarAsync(TicketAlCobrar alCobrar)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var respuesta = await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion/ticket/al-cobrar", new SolicitudAlCobrar(dueno.UsuarioId, alCobrar));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
    }

    /// <summary>Cobra lo escaneado y regresa el Id de la venta (por el folio que dice la pantalla).</summary>
    private async Task<(Guid Id, string Folio)> CobrarAsync(VentaConCopia v, string codigo)
    {
        await v.EscanearAsync(codigo);
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        var folio = Regex.Match(await v.Pantalla.TextoAsync(), @"Venta ([A-Z]+)-(\d+) cobrada");
        Assert.True(folio.Success, "No se cobró.");
        var (serie, numero) = (folio.Groups[1].Value, int.Parse(folio.Groups[2].Value));
        await using var db = servicio.Base();
        return (await db.Ventas.AsNoTracking().Where(x => x.Serie == serie && x.Folio == numero).Select(x => x.Id).SingleAsync(), $"{serie}-{folio.Groups[2].Value}");
    }

    private async Task<bool> SinImprimirAsync(Guid ventaId)
    {
        await using var db = servicio.Base();
        return await db.Ventas.AsNoTracking().Where(x => x.Id == ventaId).Select(x => x.TicketSinImprimir).SingleAsync();
    }

    private static async Task EsperarAsync(Func<Task<bool>> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!await condicion())
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No llegó {que}.");
            await Task.Delay(20);
        }
    }

    /// <summary>«Ventas del día» abierto, con la venta ya a la vista; regresa su renglón.</summary>
    private static async Task<string> VentasDelDiaAsync(VentaConCopia v, Guid ventaId)
    {
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escaneado("/ventas del dia"));
        return PantallaSinNavegador.Limpio((await v.Pantalla.EsperarElementoAsync($"venta-{ventaId}")).Texto);
    }

    /// <summary>Un botón del renglón de esa venta (los de las demás ventas del día no cuentan).</summary>
    private static async Task OprimirEnLaVentaAsync(VentaConCopia v, Guid ventaId, string boton)
    {
        var elementos = (await v.Pantalla.LeerAsync()).Elementos;
        var desde = elementos.FindIndex(e => e.Id == $"venta-{ventaId}");
        Assert.True(desde >= 0, "No está la venta.");
        var suyo = elementos.Skip(desde + 1).TakeWhile(e => e.Id?.StartsWith("venta-", StringComparison.Ordinal) != true)
            .FirstOrDefault(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == boton)
            ?? throw new Xunit.Sdk.XunitException($"La venta no tiene «{boton}».");
        await v.Pantalla.OprimirAsync(suyo);
    }

    private async Task<List<string>> BitacoraAsync(Guid ventaId)
    {
        await using var db = servicio.Base();
        return await db.Bitacora.AsNoTracking().Where(b => b.VentaId == ventaId && b.Accion == AccionControlada.Reimprimir)
            .OrderBy(b => b.FechaUtc).Select(b => b.Detalle).ToListAsync();
    }

    [Fact]
    public async Task Lo_que_no_se_imprimio_al_cobrar_sale_sin_motivo_y_despues_ya_pide_motivo()
    {
        await AlCobrarAsync(TicketAlCobrar.NoImprimir);
        try
        {
            var impresora = new TicketeraDePrueba();
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
            var (a, _) = v.DosDeLaCopia();
            var (id, folio) = await CobrarAsync(v, a.Codigo!);
            Assert.Equal(0, impresora.Intentos);

            Assert.Contains("Ticket sin imprimir", await VentasDelDiaAsync(v, id));
            await OprimirEnLaVentaAsync(v, id, "Imprimir");

            // Sin pedir motivo: sale, y la caja principal lo anota como primera impresión.
            await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "el ticket");
            Assert.Contains(folio, impresora.Salieron.Single());
            Assert.DoesNotContain("Motivo de la reimpresión", await v.Pantalla.TextoAsync());
            Assert.Equal([$"Primera impresión del ticket {folio}: no salió al cobrar (no es una reimpresión)"], await BitacoraAsync(id));

            // Ya salió: la siguiente vez es «Reimprimir», con su motivo.
            Assert.DoesNotContain("Ticket sin imprimir", await VentasDelDiaAsync(v, id));
            await OprimirEnLaVentaAsync(v, id, "Reimprimir");
            Assert.Contains("Motivo de la reimpresión", await v.Pantalla.TextoAsync());
            Assert.Empty(v.Pantalla.Errores);
        }
        finally
        {
            await AlCobrarAsync(TicketAlCobrar.Imprimir);
        }
    }

    /// <summary>
    /// Con «Imprimir el ticket», la impresora no lo sacó y se dejó con «No imprimirlo» (la cola, #108): la caja le dice a la caja
    /// principal que sigue sin salir, y en «Ventas del día» sale sin motivo.
    /// </summary>
    [Fact]
    public async Task Lo_que_se_dejo_sin_imprimir_en_la_cola_sale_despues_sin_motivo()
    {
        var impresora = new TicketeraDePrueba { Falla = n => n == 1 ? new IOException("Sin papel.") : null };
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
        var (a, _) = v.DosDeLaCopia();
        var (id, folio) = await CobrarAsync(v, a.Codigo!);
        Assert.False(await SinImprimirAsync(id));

        await v.Pantalla.EsperarAsync(t => t.Contains($"No salió «Ticket {folio}»"), "el ticket que no salió");
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("No imprimirlo"));
        await EsperarAsync(() => SinImprimirAsync(id), "el aviso a la caja principal");

        Assert.Contains("Ticket sin imprimir", await VentasDelDiaAsync(v, id));
        await OprimirEnLaVentaAsync(v, id, "Imprimir");
        await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "el ticket");
        Assert.Contains(folio, impresora.Salieron.Single());
        Assert.False(await SinImprimirAsync(id));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>«Ofrecerlo» y Ctrl+I: esa ya fue su primera impresión. La caja principal lo sabe y después pide motivo.</summary>
    [Fact]
    public async Task Imprimir_ticket_con_Ctrl_I_ya_es_su_primera_impresion()
    {
        await AlCobrarAsync(TicketAlCobrar.Ofrecer);
        try
        {
            var impresora = new TicketeraDePrueba();
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
            var (a, _) = v.DosDeLaCopia();
            var (id, folio) = await CobrarAsync(v, a.Codigo!);
            Assert.True(await SinImprimirAsync(id));

            await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("Ctrl+I", null));
            await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "el ticket ofrecido");
            await EsperarAsync(async () => !await SinImprimirAsync(id), "la primera impresión en la caja principal");

            Assert.Equal([$"Primera impresión del ticket {folio}: no salió al cobrar (no es una reimpresión)"], await BitacoraAsync(id));
            Assert.DoesNotContain("Ticket sin imprimir", await VentasDelDiaAsync(v, id));
            Assert.Empty(v.Pantalla.Errores);
        }
        finally
        {
            await AlCobrarAsync(TicketAlCobrar.Imprimir);
        }
    }

    /// <summary>Si mientras estaba abierto «Ventas del día» ya salió en otra caja, es una reimpresión: pide el motivo y dice por qué.</summary>
    [Fact]
    public async Task Si_ya_salio_en_otra_caja_pide_el_motivo_y_dice_por_que()
    {
        await AlCobrarAsync(TicketAlCobrar.NoImprimir);
        try
        {
            var impresora = new TicketeraDePrueba();
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
            var (a, _) = v.DosDeLaCopia();
            var (id, folio) = await CobrarAsync(v, a.Codigo!);
            Assert.Contains("Ticket sin imprimir", await VentasDelDiaAsync(v, id));

            var otra = await servicio.Fabrica.CreateClient().PostAsJsonAsync($"api/ventas/{id}/primera-impresion",
                new SolicitudPrimeraImpresion(v.Cajero.UsuarioId, v.Caja.Id));
            Assert.True(otra.IsSuccessStatusCode);
            await OprimirEnLaVentaAsync(v, id, "Imprimir");

            var texto = await v.Pantalla.TextoAsync();
            Assert.Contains("Motivo de la reimpresión", texto);
            Assert.Contains($"El ticket {folio} ya salió: si lo vuelven a pedir, se reimprime con su motivo.", texto);
            Assert.Equal(0, impresora.Intentos);
            Assert.Empty(v.Pantalla.Errores);
        }
        finally
        {
            await AlCobrarAsync(TicketAlCobrar.Imprimir);
        }
    }
}
