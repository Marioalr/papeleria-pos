using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Pruebas.ControlPrint;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;
using static PapeleriaPos.Impresiones.Pruebas.ControlPrint.PorCobrarDePrueba;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// F2 en la caja 2 con las tarjetas de la cola de Control Print (2026-10-25), sin navegador: se ven como en Control Print (su
/// número, su título, su resumen, sus chips y su precio) y en sus lotes, junto a lo que imprimió la caja 2; se eligen, entran al
/// ticket en la línea de impresiones y se cobran; lo cobrado en la caja principal mientras tanto sale del ticket; y si Control
/// Print no contesta, se dice arriba y en F2. Nunca se le escribe a Control Print: el falso falla si pasa.
/// </summary>
public sealed class TarjetasControlPrintEnLaVentaTests : IAsyncLifetime
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-cp-{Guid.NewGuid():N}.db");
    private ServidorControlPrintFalso _controlPrint = null!;
    private ServicioDePrueba _servicio = null!;

    public async Task InitializeAsync()
    {
        _controlPrint = await ServidorControlPrintFalso.IniciarAsync(Codigo);
        var conMiniatura = Trabajo(1234, Hace(4), precio: 15, documento: "Tesis.pdf");
        conMiniatura.Thumbnail = DatosDeControlPrint.Png;
        _controlPrint.CambiarTrabajos(t => t.AddRange([Trabajo(1230, Hace(40), precio: 4, documento: "Acta.pdf"), conMiniatura,
            Trabajo(1235, Hace(3), precio: 2, documento: "Portada.docx")]));
        _servicio = await ServicioDePrueba.CrearAsync(ControlPrintEnLaCajaTests.Ajustes(_controlPrint));
    }

    public async Task DisposeAsync()
    {
        await _servicio.DisposeAsync();
        ArchivosDePrueba.BorrarBase(_baseLocal);
        // Al terminar, el falso hace fallar la prueba si alguien le escribió.
        await _controlPrint.DisposeAsync();
    }

    private static Task TeclaAsync(VentaConCopia v, string tecla) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, null));

    /// <summary>Hasta que el servicio tenga leída la cola de Control Print (o diga que no contesta).</summary>
    private async Task<ColaControlPrintDto> ColaDelServicioAsync(Func<ColaControlPrintDto, bool> lista)
    {
        var http = _servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var usuario = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var cola = (await http.GetFromJsonAsync<ColaControlPrintDto>($"api/sucursales/{caja.SucursalId}/impresiones/control-print?usuarioId={usuario.UsuarioId}"))!;
            if (lista(cola) || DateTime.UtcNow > limite)
                return cola;
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task F2_las_muestra_como_en_Control_Print_en_sus_lotes_y_se_cobran_en_la_linea_de_impresiones()
    {
        await ColaDelServicioAsync(c => c.Trabajos.Count == 3);
        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);

        await TeclaAsync(v, "F2");
        await v.Pantalla.EsperarAsync(t => t.Contains("#1234"), "las tarjetas de Control Print");
        var texto = PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync());
        // Lo que el cajero tiene que saber, dicho una vez.
        Assert.Contains("Las de Control Print (#) solo se leen: cobrarlas aquí no las marca allá, donde siguen «por cobrar».", texto);
        // Como en Control Print: número, título, resumen, chips y precio; los de 2 minutos o menos, en un lote.
        Assert.Contains("lote de 2 trabajos · $17.00", texto);
        Assert.Contains("#1234 Tesis.pdf 2 págs · B/N · Carta · Papel normal", texto);
        Assert.Contains("PRINCIPAL · Sin sesión", texto);
        Assert.Contains("#1230 Acta.pdf", texto);
        Assert.Contains("$15.00", texto);
        // La miniatura llega sola, una vez.
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!(await v.Pantalla.LeerAsync()).Elementos.Any(e => e.Etiqueta == "img"
                   && e.Atributos.GetValueOrDefault("src")?.ToString()?.StartsWith("data:image/png;base64,", StringComparison.Ordinal) == true))
        {
            Assert.True(DateTime.UtcNow < limite, "No llegó la miniatura.");
            await Task.Delay(20);
        }
        Assert.Equal(1, _controlPrint.Pedidas("/api/jobs/1234/thumbnail"));

        // «Elegir 2» elige el lote; entran al ticket en una sola línea, con sus números.
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Elegir 2"));
        Assert.Contains("2 elegidas · $17.00", PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Agregar al ticket"));
        var renglon = Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains("Impresiones #1234, #1235", renglon);
        Assert.Contains("$17.00", renglon);

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("cobrada"), "el cobro");

        await using (var db = _servicio.Base())
        {
            var cobrados = await db.Set<TrabajoControlPrintCobrado>().OrderBy(x => x.TrabajoId).Select(x => x.TrabajoId).ToListAsync();
            Assert.Equal([1234L, 1235], cobrados);
        }
        // Ya no salen en F2; la que quedó, sí.
        await TeclaAsync(v, "F2");
        await v.Pantalla.EsperarAsync(t => t.Contains("#1230"), "F2 otra vez");
        Assert.DoesNotContain("#1234", await v.Pantalla.TextoAsync());
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Lo_cobrado_en_la_caja_principal_mientras_tanto_sale_del_ticket_y_se_dice()
    {
        await ColaDelServicioAsync(c => c.Trabajos.Count == 3);
        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);
        await TeclaAsync(v, "F2");
        await v.Pantalla.EsperarAsync(t => t.Contains("#1234"), "las tarjetas");
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Elegir 2"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Agregar al ticket"));

        // En la caja principal cobran el 1234 antes de que aquí se cobre.
        _controlPrint.CambiarTrabajos(t =>
        {
            var cobrado = t.Single(x => x.Id == 1234);
            (cobrado.Payment, cobrado.PaymentBy) = (PaymentState.Paid, "Ana");
        });
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        await v.Pantalla.EsperarAsync(t => t.Contains("#1234 ya se cobró en Control Print (Ana)"), "que lo diga");
        var renglon = Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains("Impresión #1235", renglon);
        Assert.Contains("$2.00", renglon);
        Assert.DoesNotContain("cobrada", await v.Pantalla.TextoAsync());

        // Se cobra otra vez, ya sin el que se cobró allá.
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("cobrada"), "el cobro");
        await using var db = _servicio.Base();
        Assert.Equal([1235L], await db.Set<TrabajoControlPrintCobrado>().Select(x => x.TrabajoId).ToListAsync());
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Revisión del PR #146 (2026-10-29): una tarjeta con «Revisar hojas» no se cobra aquí (se ve apagada y dice dónde se resuelve), y lo
    /// cobrado aquí que también se cobró en la caja principal sale arriba (su chip) y en F2.
    /// </summary>
    [Fact]
    public async Task F2_no_deja_cobrar_las_hojas_por_revisar_y_dice_lo_cobrado_dos_veces()
    {
        _controlPrint.CambiarTrabajos(t => t.Single(x => x.Id == 1230).CountedSides = 1);
        await ColaDelServicioAsync(c => c.Trabajos.Any(t => t.Id == 1230 && t.RevisarHojas is not null));
        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);
        await TeclaAsync(v, "F2");
        await v.Pantalla.EsperarAsync(t => t.Contains("#1230"), "las tarjetas");

        var conHojas = await v.Pantalla.ElementoAsync(e => e.Clase?.Contains("no-aqui") == true, "la tarjeta con hojas por revisar");
        Assert.Equal("true", conHojas.Atributos["aria-disabled"]?.ToString());
        Assert.Contains("Se resuelve en la caja principal (cobrar lo que salió o no vendida); después se cobra aquí.",
            PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));
        await v.Pantalla.OprimirAsync(conHojas);
        Assert.Contains("0 elegidas", PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));

        // Se cobra el #1235 y después resulta que en la caja principal también lo cobraron (lo que anota el servicio al notarlo).
        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Atributos.GetValueOrDefault("aria-label")?.ToString()?.StartsWith("#1235") == true,
            "la tarjeta #1235"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Agregar al ticket"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("cobrada"), "el cobro");
        await using (var db = _servicio.Base())
        {
            var cobrado = await db.Set<TrabajoControlPrintCobrado>().SingleAsync(x => x.TrabajoId == 1235);
            (cobrado.CobradoEnControlPrintUtc, cobrado.CobradoEnControlPrintPor, cobrado.RevisadoUtc) = (DateTime.UtcNow, "Ana", DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        // F2 lo dice al abrirse.
        await TeclaAsync(v, "F2");
        await v.Pantalla.EsperarAsync(t => t.Contains("Revisa con el cliente"), "el aviso en F2");
        Assert.Contains("#1235 (Portada.docx) se cobró dos veces", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("imp-dos-veces")).Texto));
        Assert.Contains("y en Control Print, en la caja principal (Ana,", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("imp-dos-veces")).Texto));
        // Y arriba, con lo que cuenta la barra (cada 45 s o después de cobrar): se cobra el #1234.
        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Atributos.GetValueOrDefault("aria-label")?.ToString()?.StartsWith("#1234") == true,
            "la tarjeta #1234"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Agregar al ticket"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cobradas dos veces"), "el chip de arriba");
        var chip = await v.Pantalla.PorIdAsync("chip-cobradas-dos-veces");
        Assert.Contains("aviso", chip.Clase);
        Assert.Contains("#1235 (Portada.docx) se cobró dos veces", chip.Atributos["title"]!.ToString());
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Si_Control_Print_no_contesta_arriba_y_en_F2_se_dice_y_lo_de_la_caja_2_sigue()
    {
        // La caja principal apagada antes de abrir la caja 2.
        _controlPrint.Antes = (ctx, _) =>
        {
            ctx.Abort();
            return Task.FromResult(true);
        };
        _controlPrint.Avisar(0);
        await ColaDelServicioAsync(c => c.Contesta == false);
        // Lo que imprimió la caja 2 sí está.
        var http = _servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        (await http.PostAsJsonAsync("api/impresiones", new TrabajoMedidoDto(Guid.NewGuid(), caja.Id, null, "CAJA2", "WF-6590 Series(Red)", 7, "Recibo.pdf",
            "cajera", DateTime.UtcNow, EstadoTrabajo.Impreso, new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            [new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)], true, null, null))).EnsureSuccessStatusCode();

        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);

        // Arriba, un aviso (cede como los demás: «Control Print no…»).
        await v.Pantalla.EsperarAsync(t => t.Contains("Control Print no contesta"), "el chip de arriba");
        var chip = await v.Pantalla.PorIdAsync("chip-control-print");
        Assert.Contains("aviso", chip.Clase);
        Assert.Contains("se cobran cuando conteste (no a mano)", chip.Atributos["title"]!.ToString());
        // En F2: lo dice, sin tarjetas de Control Print, y la de la caja 2 se puede cobrar.
        await TeclaAsync(v, "F2");
        await v.Pantalla.EsperarAsync(t => t.Contains("Recibo.pdf"), "la impresión de la caja 2");
        var texto = PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync());
        Assert.Contains("Control Print no contesta: sus impresiones no se ven; se cobran cuando conteste (no las cobres a mano: quedarían por "
            + "cobrar y se cobrarían dos veces). Las de esta caja siguen.", texto);
        Assert.DoesNotContain("IMP", texto);
        Assert.Contains("Impresa en CAJA2", texto);
        Assert.DoesNotContain("#1234", texto);
        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Clase?.Contains("imp-tarjeta") == true, "la tarjeta de la caja 2"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Agregar al ticket"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("cobrada"), "el cobro de lo de la caja 2");
        Assert.Empty(v.Pantalla.Errores);
    }
}
