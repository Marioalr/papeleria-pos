using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Lo que el cajero hace sobre un renglón le pasa a ese renglón, aunque mientras tanto llegue el «buscando…» de un código
/// que no estaba en la copia (que entra en su lugar y, si queda al final, se lleva la selección) o el clic salga antes de
/// que la página tome el dibujo nuevo. Antes, el precio escrito para B quedaba en la libreta que llegó, y «Quitar» debajo
/// de B quitaba la libreta. Nada depende de lo que tarda la PC: la búsqueda en la caja principal se detiene hasta
/// soltarla, y cada espera tiene su límite.
/// </summary>
public sealed class AccionSobreSuRenglonTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-renglon-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    /// <summary>Cambiar precios y descuentos sin autorización: el dueño.</summary>
    private Task<VentaConCopia> AbrirAsync() => VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));

    private static async Task<string> TextoAsync(VentaConCopia v) => PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync());

    /// <summary>El «+» de cada renglón, en orden.</summary>
    private static async Task<ElementoDibujado> MasDeAsync(VentaConCopia v, int renglon) =>
        (await v.Pantalla.LeerAsync()).Elementos
            .Where(e => e.Etiqueta == "button" && e.Atributos.GetValueOrDefault("title")?.ToString() == "Uno más").ToList()[renglon];

    /// <summary>Un botón de la tira de acciones, debajo del renglón seleccionado.</summary>
    private static Task<ElementoDibujado> DeLaTiraAsync(VentaConCopia v, string texto) =>
        v.Pantalla.ElementoAsync(e => e.Etiqueta == "button" && e.Clase?.Contains("vn-accion", StringComparison.Ordinal) == true
                                      && PantallaSinNavegador.Limpio(e.Texto).StartsWith(texto, StringComparison.Ordinal), $"«{texto}» en la fila");

    private static async Task<string> SeleccionadoAsync(VentaConCopia v) =>
        PantallaSinNavegador.Limpio((await v.Pantalla.ElementoAsync(e => e.Etiqueta == "tr" && e.Clase == "seleccionada", "el renglón seleccionado")).Texto);

    /// <summary>
    /// A y B de la copia, y luego la libreta, que no está en ella: se queda «buscando…» (debajo de B) hasta soltar la red.
    /// Regresa el nombre de B, que queda seleccionado.
    /// </summary>
    private static async Task<string> ByLaLibretaQueSeBuscaAsync(VentaConCopia v, string codigo, string libreta)
    {
        var (a, b) = v.DosDeLaCopia();
        var nueva = await v.DarDeAltaEnOtraCajaAsync(codigo, libreta);
        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync(b.Codigo!);
        v.Red.Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await v.EscanearAsync(nueva.Codigo!);
        Assert.Contains("Buscando…", await TextoAsync(v));
        Assert.Contains(b.Descripcion, await SeleccionadoAsync(v));
        return b.Descripcion;
    }

    private static async Task LlegaLaLineaAsync(VentaConCopia v, string nombre)
    {
        v.Red.Detenida!.SetResult();
        await v.Pantalla.EsperarAsync(t => t.Contains(nombre, StringComparison.Ordinal) && !t.Contains("Buscando…", StringComparison.Ordinal), "la línea que llegó");
    }

    private static async Task CambiarPrecioAsync(VentaConCopia v, string precio)
    {
        await v.Pantalla.EscribirAsync("precio-nuevo", precio);
        await v.Pantalla.TeclaAsync("precio-nuevo", "Enter");
        await v.Pantalla.EsperarAsync(t => t.Contains("Precio cambiado", StringComparison.Ordinal), "el precio cambiado");
    }

    [Fact]
    public async Task El_mas_de_la_fila_suma_a_su_renglon_aunque_antes_entre_el_que_se_buscaba()
    {
        await using var v = await AbrirAsync();
        var (a, b) = v.DosDeLaCopia();
        var nuevo = await v.DarDeAltaEnOtraCajaAsync("9900000000801", "Libreta del más");
        v.Red.Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync(nuevo.Codigo!);
        await v.EscanearAsync(b.Codigo!);
        var masDeB = await MasDeAsync(v, 1);

        // Como en el navegador: el clic salió antes de que la página tomara el dibujo con la libreta en medio.
        v.Pantalla.AcuseDelDibujo = TimeSpan.FromSeconds(3);
        await LlegaLaLineaAsync(v, "Libreta del más");
        await v.Pantalla.DispararAsync(masDeB, "onclick", new MouseEventArgs());

        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(3, renglones.Count);
        Assert.StartsWith($"1 {a.Descripcion}", renglones[0], StringComparison.Ordinal);
        Assert.StartsWith("1 Libreta del más", renglones[1], StringComparison.Ordinal);
        Assert.StartsWith($"2 {b.Descripcion}", renglones[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task El_mas_de_un_renglon_que_se_acaba_de_quitar_no_cambia_otro()
    {
        await using var v = await AbrirAsync();
        var lista = v.DeLaCopia();
        var (a, b, c) = (lista[0], lista[1], lista[2]);
        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync(b.Codigo!);
        await v.EscanearAsync(c.Codigo!);
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("ArrowUp", null));
        var masDeB = await MasDeAsync(v, 1);

        // Supr quita B; el «+» de B ya iba en camino.
        v.Pantalla.AcuseDelDibujo = TimeSpan.FromSeconds(3);
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("Delete", ""));
        await v.Pantalla.DispararAsync(masDeB, "onclick", new MouseEventArgs());

        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.StartsWith($"1 {a.Descripcion}", renglones[0], StringComparison.Ordinal);
        Assert.StartsWith($"1 {c.Descripcion}", renglones[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quitar_debajo_de_B_quita_B_aunque_al_final_llegue_el_que_se_buscaba()
    {
        await using var v = await AbrirAsync();
        const string libreta = "Libreta de quitar";
        var b = await ByLaLibretaQueSeBuscaAsync(v, "9900000000802", libreta);
        var quitar = await DeLaTiraAsync(v, "Quitar");

        // La libreta llega al final y se lleva la selección (y la tira); el clic en «Quitar» de B ya iba en camino.
        v.Pantalla.AcuseDelDibujo = TimeSpan.FromSeconds(3);
        await LlegaLaLineaAsync(v, libreta);
        await v.Pantalla.DispararAsync(quitar, "onclick", new MouseEventArgs());

        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.DoesNotContain(renglones, r => r.Contains(b, StringComparison.Ordinal));
        Assert.Contains(renglones, r => r.Contains(libreta, StringComparison.Ordinal));
    }

    [Fact]
    public async Task El_precio_de_la_fila_espera_al_que_se_buscaba_y_es_de_su_renglon()
    {
        await using var v = await AbrirAsync();
        const string libreta = "Libreta del botón";
        var b = await ByLaLibretaQueSeBuscaAsync(v, "9900000000803", libreta);

        // «Precio» debajo de B, mientras se busca la libreta: espera a que llegue, como Ctrl+E, y luego abre el de B.
        var clic = v.Pantalla.OprimirAsync(await DeLaTiraAsync(v, "Precio"));
        Assert.DoesNotContain("precio actual", await TextoAsync(v));
        await LlegaLaLineaAsync(v, libreta);
        await clic;
        Assert.Contains($"{b} · precio actual", await TextoAsync(v));
        Assert.Contains(b, await SeleccionadoAsync(v));

        await CambiarPrecioAsync(v, "1.00");
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Contains("Precio cambiado", renglones.Single(r => r.Contains(b, StringComparison.Ordinal)));
        Assert.DoesNotContain("Precio cambiado", renglones.Single(r => r.Contains(libreta, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task La_ventana_del_precio_se_queda_con_su_renglon_cuando_llega_el_que_se_buscaba()
    {
        await using var v = await AbrirAsync();
        const string libreta = "Libreta de la ventana";
        var b = await ByLaLibretaQueSeBuscaAsync(v, "9900000000804", libreta);

        // Desde «/» la ventana se abre al momento (no espera): es la de B.
        await v.EscanearAsync("/cambiar precio");
        Assert.Contains($"{b} · precio actual", await TextoAsync(v));

        // Llega la libreta al final: con la ventana abierta, la selección no se mueve y la ventana sigue siendo de B.
        await LlegaLaLineaAsync(v, libreta);
        Assert.Contains($"{b} · precio actual", await TextoAsync(v));
        Assert.Contains(b, await SeleccionadoAsync(v));

        await CambiarPrecioAsync(v, "1.00");
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Contains("Precio cambiado", renglones.Single(r => r.Contains(b, StringComparison.Ordinal)));
        Assert.DoesNotContain("Precio cambiado", renglones.Single(r => r.Contains(libreta, StringComparison.Ordinal)));
        Assert.Contains("$45.50", renglones.Single(r => r.Contains(libreta, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task El_descuento_abierto_se_queda_con_su_renglon_cuando_llega_el_que_se_buscaba()
    {
        await using var v = await AbrirAsync();
        const string libreta = "Libreta del descuento";
        var b = await ByLaLibretaQueSeBuscaAsync(v, "9900000000805", libreta);
        await v.EscanearAsync("/descuento");
        Assert.Contains($"Al artículo: {b}", await TextoAsync(v));

        await LlegaLaLineaAsync(v, libreta);
        Assert.Contains($"Al artículo: {b}", await TextoAsync(v));

        await v.Pantalla.EscribirAsync("descuento-valor", "5");
        var motivo = await v.Pantalla.ElementoAsync(e => e.Etiqueta == "input"
            && e.Atributos.GetValueOrDefault("placeholder")?.ToString()?.StartsWith("Cliente frecuente", StringComparison.Ordinal) == true, "el motivo");
        await v.Pantalla.DispararAsync(motivo, "oninput", new ChangeEventArgs { Value = "Cliente de siempre" });
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Aplicar"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cliente de siempre", StringComparison.Ordinal) && !t.Contains("Al artículo:", StringComparison.Ordinal),
            "el descuento en el ticket");

        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Contains("Cliente de siempre", renglones.Single(r => r.Contains(b, StringComparison.Ordinal)));
        Assert.DoesNotContain("Descuento", renglones.Single(r => r.Contains(libreta, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Como + y Supr, Ctrl+E y Ctrl+D esperan a que llegue lo que se escaneó antes: son de lo último que se escaneó (la
    /// libreta, que se lleva la selección), y la ventana no se abre con un renglón para luego cambiarse a otro.
    /// </summary>
    [Theory]
    [InlineData("Ctrl+E", "9900000000806", "Libreta de la tecla E", "{0} · precio actual")]
    [InlineData("Ctrl+D", "9900000000807", "Libreta de la tecla D", "Al artículo: {0}")]
    public async Task Ctrl_E_y_Ctrl_D_esperan_al_que_se_buscaba(string tecla, string codigo, string libreta, string ventana)
    {
        await using var v = await AbrirAsync();
        var b = await ByLaLibretaQueSeBuscaAsync(v, codigo, libreta);

        var oprimida = v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, ""));
        var mientras = await TextoAsync(v);
        Assert.DoesNotContain(string.Format(ventana, b), mientras);
        Assert.DoesNotContain(string.Format(ventana, libreta), mientras);

        await LlegaLaLineaAsync(v, libreta);
        await oprimida;
        Assert.Contains(string.Format(ventana, libreta), await TextoAsync(v));
        Assert.Contains(libreta, await SeleccionadoAsync(v));
    }
}
