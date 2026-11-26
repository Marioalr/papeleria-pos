using PapeleriaPos.Nucleo;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Cada encabezado de la lista del ticket la ordena (dueño, 2026-11-26): un clic, de menor a mayor; otro, de mayor a menor; el
/// tercero (o «Orden de llegada»), como fueron entrando. Es solo como se ve: la venta sigue en orden de llegada. ↑ ↓ van por la
/// lista como se ve, Supr deja elegido al que se ve en su lugar, y lo que se agrega con un orden puesto aparece en su lugar, elegido.
/// </summary>
public sealed class OrdenDeLaListaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-orden-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private static Task<ElementoDibujado> EncabezadoAsync(VentaConCopia v, string titulo) =>
        v.Pantalla.ElementoAsync(e => e.Etiqueta == "button" && e.Clase?.Contains("vn-ordenar", StringComparison.Ordinal) == true
                                      && PantallaSinNavegador.Limpio(e.Texto).StartsWith(titulo, StringComparison.Ordinal), $"el encabezado «{titulo}»");

    /// <summary>Quitar productos sin autorización: el dueño.</summary>
    private Task<VentaConCopia> AbrirAsync() => VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));

    private static async Task OrdenarAsync(VentaConCopia v, string titulo) => await v.Pantalla.OprimirAsync(await EncabezadoAsync(v, titulo));

    /// <summary>Lo que dice su th a un lector de pantalla: «ascending», «descending» o «none».</summary>
    private static async Task<string?> AriaAsync(VentaConCopia v, string titulo) =>
        (await v.Pantalla.ElementoAsync(e => e.Etiqueta == "th" && PantallaSinNavegador.Limpio(e.Texto).StartsWith(titulo, StringComparison.Ordinal),
            $"el th «{titulo}»")).Atributos.GetValueOrDefault("aria-sort")?.ToString();

    private static async Task<string> SeleccionadoAsync(VentaConCopia v) =>
        PantallaSinNavegador.Limpio((await v.Pantalla.ElementoAsync(e => e.Etiqueta == "tr" && e.Clase == "seleccionada", "el renglón seleccionado")).Texto);

    /// <summary>Cada renglón, como «cantidad nombre» al principio: así se ve en qué orden van.</summary>
    private static async Task AssertOrdenAsync(VentaConCopia v, params (int Cantidad, string Nombre)[] esperados)
    {
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(esperados.Length, renglones.Count);
        for (var i = 0; i < esperados.Length; i++)
            Assert.StartsWith($"{esperados[i].Cantidad} {esperados[i].Nombre}", renglones[i], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cada_clic_en_cantidad_ordena_de_menor_a_mayor_de_mayor_a_menor_y_regresa_al_orden_de_llegada()
    {
        await using var v = await AbrirAsync();
        var lista = v.DeLaCopia();
        var (a, b, c) = (lista[0].Descripcion, lista[1].Descripcion, lista[2].Descripcion);
        // Llegan C, A, B; la segunda C se suma a su renglón y no lo mueve.
        await v.EscanearAsync(lista[2].Codigo!);
        await v.EscanearAsync(lista[0].Codigo!);
        await v.EscanearAsync(lista[1].Codigo!);
        await v.EscanearAsync(lista[2].Codigo!);
        await AssertOrdenAsync(v, (2, c), (1, a), (1, b));
        Assert.Equal("none", await AriaAsync(v, "Cantidad"));

        // De menor a mayor: A y B empatan y van como llegaron.
        await OrdenarAsync(v, "Cantidad");
        await AssertOrdenAsync(v, (1, a), (1, b), (2, c));
        Assert.Equal("ascending", await AriaAsync(v, "Cantidad"));
        Assert.Contains("▲", PantallaSinNavegador.Limpio((await EncabezadoAsync(v, "Cantidad")).Texto));

        await OrdenarAsync(v, "Cantidad");
        await AssertOrdenAsync(v, (2, c), (1, a), (1, b));
        Assert.Equal("descending", await AriaAsync(v, "Cantidad"));
        Assert.Contains("▼", PantallaSinNavegador.Limpio((await EncabezadoAsync(v, "Cantidad")).Texto));

        // Con el orden puesto, otra B: sube a su lugar según la cantidad, elegida; su lugar de llegada no cambia.
        await v.EscanearAsync(lista[1].Codigo!);
        await AssertOrdenAsync(v, (2, c), (2, b), (1, a));
        Assert.Contains(b, await SeleccionadoAsync(v));

        // El tercer clic: como llegaron.
        await OrdenarAsync(v, "Cantidad");
        await AssertOrdenAsync(v, (2, c), (1, a), (2, b));
        Assert.Equal("none", await AriaAsync(v, "Cantidad"));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Por_descripcion_las_flechas_y_Supr_van_como_se_ve_y_el_boton_regresa_al_orden_de_llegada()
    {
        await using var v = await AbrirAsync();
        var lista = v.DeLaCopia().Take(3).ToList();
        foreach (var producto in lista)
            await v.EscanearAsync(producto.Codigo!);
        var alfabetico = lista.Select(p => p.Descripcion).OrderBy(Texto.Normalizar, StringComparer.Ordinal).ToList();

        await OrdenarAsync(v, "Descripción");
        await AssertOrdenAsync(v, [.. alfabetico.Select(n => (1, n))]);
        await OrdenarAsync(v, "Descripción");
        alfabetico.Reverse();
        await AssertOrdenAsync(v, [.. alfabetico.Select(n => (1, n))]);

        // ↑ ↓ por la lista como se ve: hasta arriba, y uno abajo.
        for (var i = 0; i < 3; i++)
            await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("ArrowUp", null));
        Assert.Contains(alfabetico[0], await SeleccionadoAsync(v));
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("ArrowDown", null));
        Assert.Contains(alfabetico[1], await SeleccionadoAsync(v));

        // Supr quita el de en medio: queda elegido el que ahora se ve en su lugar.
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("Delete", ""));
        await AssertOrdenAsync(v, (1, alfabetico[0]), (1, alfabetico[2]));
        Assert.Contains(alfabetico[2], await SeleccionadoAsync(v));

        // «Orden de llegada»: como entraron, sin el que se quitó.
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Orden de llegada"));
        await AssertOrdenAsync(v, [.. lista.Select(p => p.Descripcion).Where(n => n != alfabetico[1]).Select(n => (1, n))]);
        Assert.Equal("none", await AriaAsync(v, "Descripción"));
        Assert.Empty(v.Pantalla.Errores);
    }
}
