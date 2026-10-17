using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Impresion;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Imprimir aparte (entrega 5, parte 3, docs/propuesta-interfaz.md, «Rendimiento»): el ticket va a la cola de la impresora
/// de la caja y la pantalla queda lista para el siguiente cliente sin esperarla; si no sale, el panel lo dice con
/// «Reintentar», lo que sigue espera en su orden y nada se pierde; las reimpresiones van por la misma cola. La impresora de
/// prueba se detiene o falla a voluntad: nada depende de lo que tarda la PC.
/// </summary>
public sealed class ImprimirAparteTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-imprimir-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private static async Task<string> CobrarAsync(VentaConCopia v, string codigo)
    {
        await v.EscanearAsync(codigo);
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto")).WaitAsync(TimeSpan.FromSeconds(20));
        var texto = await v.Pantalla.TextoAsync();
        return System.Text.RegularExpressions.Regex.Match(texto, @"Venta (\S+) cobrada").Groups[1].Value;
    }

    /// <summary>
    /// Con la impresora ocupada (imprime lo de la otra caja), el cobro termina sin esperarla: el cambio se ve, el ticket del
    /// siguiente cliente está vacío y se puede cobrar otra vez. Cuando la impresora se desocupa, salen los dos, en orden.
    /// </summary>
    [Fact]
    public async Task Cobrar_no_espera_a_la_impresora_y_los_tickets_salen_en_orden()
    {
        var impresora = new TicketeraDePrueba { Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
        var (a, b) = v.DosDeLaCopia();

        var primero = await CobrarAsync(v, a.Codigo!);

        var texto = await v.Pantalla.TextoAsync();
        Assert.Contains("Cobrado", texto);
        Assert.Contains($"Ticket {primero} · imprimiendo…", texto);
        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Empty(impresora.Salieron);

        // El siguiente cliente, sin esperar a la impresora.
        var segundo = await CobrarAsync(v, b.Codigo!);
        Assert.NotEqual(primero, segundo);
        var cola = ColaDeImpresion.De(impresora);
        Assert.Equal([$"Ticket {primero}", $"Ticket {segundo}"], cola.Pendientes.Select(t => t.Titulo));

        impresora.Detenida.SetResult();
        await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 2, "los dos tickets");
        Assert.Contains(primero, impresora.Salieron.First());
        Assert.Contains(segundo, impresora.Salieron.Last());
        await v.Pantalla.EsperarAsync(t => t.Contains($"Ticket {segundo} impreso"), "el ticket impreso en el panel");
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// La impresora no contesta al conectarse (la otra caja imprime): el panel y el estado de arriba dicen que espera a la
    /// impresora y que se vuelve a intentar sola, sin «Reintentar»; cuando contesta, sale una sola vez.
    /// </summary>
    [Fact]
    public async Task Si_la_impresora_no_contesta_el_panel_dice_que_espera_y_sale_sola()
    {
        var segundo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var impresora = new TicketeraDePrueba
        {
            Falla = n => n == 1 ? new ErrorAlConectar("No responde la impresora de tickets en 192.168.1.50:9100.") : null,
            AntesDeCadaIntento = n => n == 2 ? segundo.Task : Task.CompletedTask,
        };
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
        var (a, _) = v.DosDeLaCopia();

        var folio = await CobrarAsync(v, a.Codigo!);
        await v.Pantalla.EsperarAsync(t => t.Contains($"«Ticket {folio}» espera a la impresora de tickets: no contesta (puede estar imprimiendo lo de la otra caja). Se vuelve a intentar sola."),
            "el aviso de que espera a la impresora");
        var texto = await v.Pantalla.TextoAsync();
        Assert.Contains("Tickets · esperando a la impresora", texto);
        Assert.DoesNotContain("Reintentar", texto);
        Assert.Empty(impresora.Salieron);

        segundo.SetResult();
        await v.Pantalla.EsperarAsync(t => t.Contains($"Ticket {folio} impreso") && !t.Contains("esperando a la impresora"), "el ticket impreso");
        Assert.Single(impresora.Salieron);
        Assert.Equal(2, impresora.Intentos);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Si no sale (se acabó el papel), el panel lo dice con «Reintentar» y «Verlo en pantalla», y el estado de arriba también;
    /// lo que se cobra después espera su turno y una reimpresión va por la misma cola, detrás. Con «Reintentar» sale todo, en
    /// su orden.
    /// </summary>
    [Fact]
    public async Task Lo_que_no_sale_se_dice_con_reintentar_y_espera_en_su_orden()
    {
        var sinPapel = true;
        var impresora = new TicketeraDePrueba { Falla = _ => sinPapel ? new IOException("Se acabó el papel de la impresora de tickets.") : null };
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
        var (a, b) = v.DosDeLaCopia();

        var primero = await CobrarAsync(v, a.Codigo!);
        await v.Pantalla.EsperarAsync(t => t.Contains($"No salió «Ticket {primero}»: Se acabó el papel de la impresora de tickets. La venta ya quedó guardada."),
            "el aviso de que no salió");
        Assert.NotNull(await v.Pantalla.BotonAsync("Reintentar"));
        Assert.NotNull(await v.Pantalla.BotonAsync("Verlo en pantalla"));
        Assert.Contains("Tickets · no salió", await v.Pantalla.TextoAsync());

        // El siguiente cliente se cobra igual; su ticket espera detrás del que no salió.
        var segundo = await CobrarAsync(v, b.Codigo!);
        await v.Pantalla.EsperarAsync(t => t.Contains($"«Ticket {segundo}» espera su turno: antes no salió «Ticket {primero}»."), "el segundo en espera");
        // La reimpresión del último, también detrás.
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Volver al total"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Reimprimir", "Reimprimir"));
        await v.Pantalla.EscribirAsync("motivo", "El cliente lo pidió");
        await v.Pantalla.DispararAsync(await v.Pantalla.PorIdAsync("motivo"), "onkeydown", new KeyboardEventArgs { Key = "Enter" });
        var cola = ColaDeImpresion.De(impresora);
        await TicketeraDePrueba.EsperarAsync(() => cola.Pendientes.Count == 3, "la reimpresión en la cola");
        Assert.Equal(1, impresora.Intentos);
        Assert.Empty(impresora.Salieron);

        sinPapel = false;
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Reintentar"));
        await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 3, "los tres papeles");
        var salieron = impresora.Salieron.ToList();
        Assert.Contains(primero, salieron[0]);
        Assert.Contains(segundo, salieron[1]);
        Assert.Contains(segundo, salieron[2]);
        await v.Pantalla.EsperarAsync(t => !t.Contains("No salió") && !t.Contains("Tickets · no salió"), "el aviso fuera");
        Assert.Empty(cola.Pendientes);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// «No imprimirlo», junto a «Reintentar»: lo que no salió con «no confirmó» pudo salir ya (con «Reintentar» saldría dos
    /// veces), o el cliente ya se fue. Se deja sin imprimir, sin preguntar (se reimprime desde las ventas del día), queda en
    /// el registro de la caja, y la cola sigue con lo que esperaba.
    /// </summary>
    [Fact]
    public async Task No_imprimirlo_deja_el_que_no_salio_y_sigue_con_lo_que_esperaba()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), $"registros-{Guid.NewGuid():N}");
        var registro = new RegistroDiario(carpeta, "caja");
        var impresora = new TicketeraDePrueba { Falla = n => n == 1 ? new IOException("La impresora de tickets no confirmó que lo recibió.") : null };
        try
        {
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora, registro: registro);
            var (a, b) = v.DosDeLaCopia();
            var primero = await CobrarAsync(v, a.Codigo!);
            await v.Pantalla.EsperarAsync(t => t.Contains($"No salió «Ticket {primero}»: La impresora de tickets no confirmó que lo recibió."),
                "el aviso de que no salió");
            var segundo = await CobrarAsync(v, b.Codigo!);
            await v.Pantalla.EsperarAsync(t => t.Contains($"«Ticket {segundo}» espera su turno: antes no salió «Ticket {primero}»."), "el segundo en espera");
            Assert.Equal(1, impresora.Intentos);

            await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("No imprimirlo"));

            await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "el segundo ticket");
            Assert.Contains(segundo, impresora.Salieron.Single());
            Assert.Equal(2, impresora.Intentos);
            await v.Pantalla.EsperarAsync(t => t.Contains($"Ticket {segundo} impreso") && !t.Contains("no salió") && !t.Contains("No imprimirlo"),
                "el segundo impreso, sin aviso");
            Assert.Empty(ColaDeImpresion.De(impresora).Pendientes);
            Assert.Contains($"Se dejó sin imprimir «Ticket {primero}» («No imprimirlo»): no salió porque la impresora de tickets no confirmó que lo recibió.",
                File.ReadAllText(registro.ArchivoDeHoy));
            Assert.Empty(v.Pantalla.Errores);
        }
        finally
        {
            ArchivosDePrueba.BorrarCarpeta(carpeta);
        }
    }

    /// <summary>
    /// Con el logo del ticket dañado (un apagón a media escritura de la configuración), el ticket no se puede armar para la
    /// impresora. Antes eso tronaba en la pantalla con la venta ya guardada, sin papel y sin nada que reintentar; ahora el
    /// panel dice que no salió y por qué, y «Verlo en pantalla» lo muestra. A la impresora no se le manda nada.
    /// </summary>
    [Fact]
    public async Task Si_el_ticket_no_se_puede_armar_no_salio_y_se_ve_en_pantalla()
    {
        var impresora = new TicketeraDePrueba();
        // Antes de que la caja baje su copia: lo de la copia y lo del servicio, igual (si no, el cobro pide cobrar otra vez).
        var sucursalId = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single().SucursalId;
        await LogoDanadoAsync(sucursalId);
        try
        {
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
            var (a, _) = v.DosDeLaCopia();
            var folio = await CobrarAsync(v, a.Codigo!);

            await v.Pantalla.EsperarAsync(t => t.Contains($"No salió «Ticket {folio}»: No se pudo armar para la impresora. El logo del ticket está dañado"),
                "el aviso de que no salió");
            Assert.Contains("La venta ya quedó guardada.", await v.Pantalla.TextoAsync());
            Assert.Equal(0, impresora.Intentos);
            await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Verlo en pantalla"));
            await v.Pantalla.EsperarAsync(t => t.Contains("[logo 8×3]") && t.Contains(folio), "el ticket en pantalla");
            Assert.Empty(v.Pantalla.Errores);
        }
        finally
        {
            await using var db = servicio.Base();
            await db.ConfiguracionesTicket.Where(c => c.SucursalId == sucursalId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// El logo del ticket de la sucursal, dañado: dice 3 renglones de 8 puntos y trae 2 (la configuración no lo deja guardar
    /// así; se escribe directo en la base).
    /// </summary>
    private async Task LogoDanadoAsync(Guid sucursalId)
    {
        await using var db = servicio.Base();
        Assert.False(await db.ConfiguracionesTicket.AnyAsync(c => c.SucursalId == sucursalId));
        var configuracion = new ConfiguracionTicket { SucursalId = sucursalId };
        configuracion.CambiarTextos(["Papelería de prueba"], []);
        configuracion.CambiarLogo(8, 2, [0xFF, 0x81]);
        db.ConfiguracionesTicket.Add(configuracion);
        await db.SaveChangesAsync();
        await db.ConfiguracionesTicket.Where(c => c.SucursalId == sucursalId).ExecuteUpdateAsync(c => c.SetProperty(x => x.LogoAlto, 3));
    }
}
