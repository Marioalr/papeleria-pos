using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// «Devoluciones y cancelaciones» con el teclado (auditoría, 2026-11-04): al abrir el ticket el cursor llega a la primera
/// cantidad, Enter pasa de una cantidad a la siguiente y al motivo, y Enter en el motivo es «Devolver lo marcado». Después del
/// motivo, Tab lleva a «Devolver lo marcado», no a «Cancelar todo el ticket». Si se pierde la respuesta y se repite, se hace una vez.
/// </summary>
public sealed class DevolverConElTecladoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-devolver-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    /// <summary>Cobra los dos, a piezas (uno de ellos, tres), y abre su devolución con el folio, como el lector en el cuadro.</summary>
    private static async Task AbrirDevolucionAsync(VentaConCopia v)
    {
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync($"3*{a.Codigo}");
        await v.EscanearAsync(b.Codigo!);
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto")).WaitAsync(TimeSpan.FromSeconds(20));
        var folio = Regex.Match(await v.Pantalla.TextoAsync(), @"Venta (\S+) cobrada").Groups[1].Value;
        await v.EscanearAsync(folio);
        await v.Pantalla.EsperarAsync(t => t.Contains("Devolver lo marcado"), "la devolución de ese ticket");
    }

    private static Task<ElementoDibujado> MotivoAsync(VentaConCopia v) =>
        v.Pantalla.ElementoAsync(e => e.Etiqueta == "input" && e.Clase == "motivo-devolver", "el motivo");

    private static async Task<List<ElementoDibujado>> CantidadesAsync(VentaConCopia v) =>
        (await v.Pantalla.LeerAsync()).Elementos.Where(e => e.Clase == "cantidad-devolver").ToList();

    private static Task EscribirCantidadAsync(VentaConCopia v, ElementoDibujado cantidad, string valor) =>
        v.Pantalla.DispararAsync(cantidad, "onchange", new ChangeEventArgs { Value = valor });

    private static Task TeclearAsync(VentaConCopia v, ElementoDibujado campo, string tecla) =>
        v.Pantalla.DispararAsync(campo, "onkeydown", new KeyboardEventArgs { Key = tecla });

    private static string? UltimoFoco(PaginaQueContesta pagina) =>
        pagina.Llamadas.LastOrDefault(l => l.Funcion == "enfocar").Argumentos?[0] as string;

    [Fact]
    public async Task Al_abrir_el_ticket_el_cursor_llega_a_la_primera_cantidad_y_si_no_existe_se_queda_en_el_folio()
    {
        var pagina = new PaginaQueContesta();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina, quien: ("dueno", "3333"));
        await AbrirDevolucionAsync(v);

        await VentaConCopia.EsperarAsync(() => UltimoFoco(pagina) == ".cantidad-devolver, .impresiones-devolver", "el cursor en la primera cantidad");

        await v.Pantalla.EscribirAsync("folio", "A-999999");
        await v.Pantalla.TeclaAsync("folio", "Enter");
        await v.Pantalla.EsperarAsync(t => t.Contains("No existe el ticket"), "que no existe");
        await VentaConCopia.EsperarAsync(() => UltimoFoco(pagina) == "#folio", "el cursor de regreso en el folio");
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Enter_pasa_de_una_cantidad_a_la_siguiente_y_al_motivo_y_en_el_motivo_devuelve()
    {
        var pagina = new PaginaQueContesta();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina, quien: ("dueno", "3333"));
        await AbrirDevolucionAsync(v);
        var cantidades = await CantidadesAsync(v);
        Assert.Equal(2, cantidades.Count);

        await EscribirCantidadAsync(v, cantidades[0], "1");
        await TeclearAsync(v, cantidades[0], "Enter");
        Assert.StartsWith("[data-linea=", UltimoFoco(pagina));
        Assert.Equal(cantidades[1].Atributos["data-linea"]?.ToString(), Regex.Match(UltimoFoco(pagina)!, "'(.+)'").Groups[1].Value);
        await TeclearAsync(v, cantidades[1], "Enter");
        Assert.Equal(".motivo-devolver", UltimoFoco(pagina));

        // Otra tecla en el motivo no hace nada; sin motivo, lo dice; con él, Enter termina.
        await TeclearAsync(v, await MotivoAsync(v), "a");
        Assert.Contains("Devolver lo marcado", await v.Pantalla.TextoAsync());
        await TeclearAsync(v, await MotivoAsync(v), "Enter");
        await v.Pantalla.EsperarAsync(t => t.Contains("Escribe el motivo."), "que falta el motivo");
        await v.Pantalla.DispararAsync(await MotivoAsync(v), "oninput", new ChangeEventArgs { Value = "Defecto" });
        await TeclearAsync(v, await MotivoAsync(v), "Enter");

        await v.Pantalla.EsperarAsync(t => t.Contains("Devolución registrada"), "la devolución");
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Enter en el motivo sin nada marcado no devuelve ni cancela: pide las cantidades.</summary>
    [Fact]
    public async Task Enter_en_el_motivo_sin_cantidades_las_pide()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        await AbrirDevolucionAsync(v);
        await v.Pantalla.DispararAsync(await MotivoAsync(v), "oninput", new ChangeEventArgs { Value = "Defecto" });

        await TeclearAsync(v, await MotivoAsync(v), "Enter");

        await v.Pantalla.EsperarAsync(t => t.Contains("Escribe cuánto se devuelve de cada artículo."), "que pida las cantidades");
        Assert.DoesNotContain("Ticket cancelado", await v.Pantalla.TextoAsync());
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>En el marcado (el orden del Tab), «Devolver lo marcado» va antes que «Cancelar todo el ticket»; se sigue viendo a su derecha (CSS).</summary>
    [Fact]
    public async Task Despues_del_motivo_el_tabulador_llega_primero_a_devolver()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        await AbrirDevolucionAsync(v);

        var (_, elementos) = await v.Pantalla.LeerAsync();
        var botones = elementos.Where(e => e.Etiqueta == "button").Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        var devolver = botones.FindIndex(t => t.StartsWith("Devolver lo marcado", StringComparison.Ordinal));
        var cancelar = botones.FindIndex(t => t.StartsWith("Cancelar todo el ticket", StringComparison.Ordinal));
        Assert.True(devolver >= 0 && cancelar == devolver + 1, string.Join(" | ", botones));
        Assert.Contains("antes-del-principal", elementos.First(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto).StartsWith("Cancelar todo", StringComparison.Ordinal)).Clase);
    }

    /// <summary>El botón dice cuánto se regresa antes de oprimirlo (lo exacto; el redondeo al peso lo pone el servicio); sin nada marcado, nada.</summary>
    [Fact]
    public async Task El_boton_dice_cuanto_se_regresa()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        await AbrirDevolucionAsync(v);
        Assert.Equal("Devolver lo marcado", PantallaSinNavegador.Limpio((await v.Pantalla.BotonAsync("Devolver lo marcado")).Texto));

        await EscribirCantidadAsync(v, (await CantidadesAsync(v))[0], "1");

        Assert.Matches(@"^Devolver lo marcado · \$[1-9]\d*\.\d{2}$", PantallaSinNavegador.Limpio((await v.Pantalla.BotonAsync("Devolver lo marcado")).Texto));
        // Lo que no se entiende no suma: el error lo dice al oprimir.
        await EscribirCantidadAsync(v, (await CantidadesAsync(v))[0], "2,5");
        Assert.Equal("Devolver lo marcado", PantallaSinNavegador.Limpio((await v.Pantalla.BotonAsync("Devolver lo marcado")).Texto));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// La respuesta se pierde (la devolución ya quedó en el servicio) y la ventana se queda con lo mismo escrito: otro Enter no la
    /// repite, regresa su comprobante y la ventana termina. Antes se registraba otra.
    /// </summary>
    [Fact]
    public async Task Si_se_pierde_la_respuesta_y_se_repite_se_devuelve_una_vez()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        await AbrirDevolucionAsync(v);
        var folio = Regex.Match(await v.Pantalla.TextoAsync(), @"[A-Z]-\d{6}").Value;
        await EscribirCantidadAsync(v, (await CantidadesAsync(v))[0], "1");
        await v.Pantalla.DispararAsync(await MotivoAsync(v), "oninput", new ChangeEventArgs { Value = "Defecto" });

        v.Red.PierdeRespuesta = ruta => ruta.EndsWith("/devoluciones", StringComparison.Ordinal);
        await TeclearAsync(v, await MotivoAsync(v), "Enter");
        await VentaConCopia.EsperarAsync(() => v.Red.Cuantas($"/api/ventas/") > 0 && !v.Contingencia.EnLinea, "que se pierda la respuesta");
        await v.Pantalla.ElementoAsync(e => e.Clase == "error", "el error en la ventana");
        Assert.Contains("Devolver lo marcado", await v.Pantalla.TextoAsync());
        v.Red.PierdeRespuesta = null;
        await v.Contingencia.LatidoAsync();

        await TeclearAsync(v, await MotivoAsync(v), "Enter");
        await v.Pantalla.EsperarAsync(t => t.Contains("Devolución registrada"), "la devolución");

        var http = servicio.Fabrica.CreateClient();
        var venta = (await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<PapeleriaPos.Nucleo.Contratos.VentaParaDevolverDto>(http,
            $"api/sucursales/{v.Caja.SucursalId}/ventas/folio/{folio}"))!;
        Assert.Equal(1000, venta.Lineas[0].CantidadDevueltaMilesimas);
        Assert.Empty(v.Pantalla.Errores);
    }
}
