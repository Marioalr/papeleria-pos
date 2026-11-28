using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Imagenes;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La ventanita de «Juntar imágenes para Word» (docs/propuestas/juntar-imagenes-interfaz.md), sin mouse: el cursor empieza en el número,
/// Enter copia, Alt+A/N/H cambian el modo, en las miniaturas se reordena, quita, gira y deshace, y Esc pregunta antes de borrar más de 3.
/// </summary>
public class JuntarImagenesTests
{
    private sealed class JuntadasDePrueba(params (int Ancho, int Alto)[] medidas) : IJuntarImagenes
    {
        public IReadOnlyList<ImagenEnLaVentanita> Imagenes { get; } =
            medidas.Select(m => new ImagenEnLaVentanita("data:image/jpeg;base64,", "data:image/jpeg;base64,", m.Ancho, m.Alto)).ToList();
        public int Repetidas { get; init; }
        public IReadOnlyList<ImagenOrdenada>? Orden { get; init; }
        public AjustesDeJuntarImagenes Ajustes { get; init; } = new();
        public bool HayWord { get; init; } = true;
        public List<(string Que, IReadOnlyList<ImagenOrdenada> Orden, AjustesDeJuntarImagenes? Ajustes)> Pedidos { get; } = [];

        public Task CopiarAsync(IReadOnlyList<ImagenOrdenada> orden, AjustesDeJuntarImagenes ajustes)
        {
            Pedidos.Add(("copiar", orden, ajustes));
            return Task.CompletedTask;
        }

        public Task AbrirEnWordAsync(IReadOnlyList<ImagenOrdenada> orden, AjustesDeJuntarImagenes ajustes)
        {
            Pedidos.Add(("abrir", orden, ajustes));
            return Task.CompletedTask;
        }

        public void JuntarMas(IReadOnlyList<ImagenOrdenada> orden, AjustesDeJuntarImagenes ajustes) => Pedidos.Add(("mas", orden, ajustes));

        public void Cancelar() => Pedidos.Add(("cancelar", [], null));
    }

    private sealed record Abierta(PantallaSinNavegador Pantalla, JuntarImagenes Ventanita, JuntadasDePrueba Juntadas, PaginaQueContesta Pagina) : IAsyncDisposable
    {
        public Task TeclaAsync(string tecla, bool alt = false, bool ctrl = false, string? donde = "") =>
            Pantalla.Dispatcher.InvokeAsync(() => Ventanita.TeclaAsync(tecla, alt, ctrl, donde));

        public Task MiniaturaAsync(string tecla, bool alt = false, bool ctrl = false) => TeclaAsync(tecla, alt, ctrl, "miniaturas");

        public string? UltimoFoco => Pagina.Llamadas.LastOrDefault(l => l.Funcion == "enfocar").Argumentos?[0] as string;

        public async ValueTask DisposeAsync() => await Pantalla.DisposeAsync();
    }

    private static async Task<Abierta> AbrirAsync(JuntadasDePrueba juntadas)
    {
        var pagina = new PaginaQueContesta();
        var servicios = new ServiceCollection()
            .AddSingleton<IJSRuntime>(pagina)
            .AddSingleton<IJuntarImagenes>(juntadas)
            .AddScoped<ModuloJuntar>()
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        var ventanita = await pantalla.AbrirAsync<JuntarImagenes>([]);
        return new Abierta(pantalla, ventanita, juntadas, pagina);
    }

    private static JuntadasDePrueba Capturas(int cuantas) => new(Enumerable.Range(0, cuantas).Select(_ => (1080, 2340)).ToArray());

    [Fact]
    public async Task Abre_con_el_cursor_en_el_numero_y_Enter_copia_con_el_alto_escrito()
    {
        await using var a = await AbrirAsync(Capturas(24));
        Assert.Equal("#jn-num", a.UltimoFoco);
        Assert.Contains("24 imágenes", await a.Pantalla.TextoAsync());
        Assert.Equal("5", (await a.Pantalla.PorIdAsync("jn-num")).Valor);

        await a.Pantalla.EscribirAsync("jn-num", "6");
        Assert.StartsWith("Salen ", a.Ventanita.Resultado);
        await a.TeclaAsync("Enter");

        var (que, orden, ajustes) = Assert.Single(a.Juntadas.Pedidos);
        Assert.Equal("copiar", que);
        Assert.Equal(6, ajustes!.Alto);
        Assert.Equal(Enumerable.Range(0, 24), orden.Select(o => o.Indice));
        Assert.Empty(a.Pantalla.Errores);
    }

    [Fact]
    public async Task Enter_en_un_boton_o_en_una_lista_no_copia()
    {
        await using var a = await AbrirAsync(Capturas(3));
        await a.TeclaAsync("Enter", donde: "boton");
        await a.TeclaAsync("Enter", donde: "lista");
        await a.MiniaturaAsync("Enter");
        Assert.Empty(a.Juntadas.Pedidos);
    }

    [Fact]
    public async Task Alt_H_cambia_a_en_hojas_y_dice_de_que_alto_salen()
    {
        await using var a = await AbrirAsync(Capturas(24));
        await a.TeclaAsync("h", alt: true);
        Assert.Equal(ModoDelTamano.EnHojas, a.Ventanita.Ajustes.Modo);
        Assert.Equal("#jn-num", a.UltimoFoco);
        Assert.Equal("1", (await a.Pantalla.PorIdAsync("jn-num")).Valor);
        Assert.Matches(@"^Salen de [\d.]+ cm de alto · 1 hoja$", a.Ventanita.Resultado);

        await a.Pantalla.EscribirAsync("jn-num", "2");
        Assert.Matches(@"^Salen de [\d.]+ cm de alto · 2 hojas$", a.Ventanita.Resultado);
        // Cada modo guarda su número: de regreso en Alto siguen los 5 cm.
        await a.TeclaAsync("a", alt: true);
        Assert.Equal("5", (await a.Pantalla.PorIdAsync("jn-num")).Valor);
        Assert.Equal(2, a.Ventanita.Ajustes.Hojas);
    }

    [Fact]
    public async Task Sin_numero_no_copia_y_lo_dice()
    {
        await using var a = await AbrirAsync(Capturas(3));
        await a.Pantalla.EscribirAsync("jn-num", "");
        Assert.Equal("Escribe un número", a.Ventanita.Resultado);
        Assert.True((await a.Pantalla.PorIdAsync("jn-copiar")).Desactivado);
        await a.TeclaAsync("Enter");
        Assert.Empty(a.Juntadas.Pedidos);
    }

    [Fact]
    public async Task En_las_miniaturas_se_reordena_quita_gira_y_deshace_con_el_teclado()
    {
        await using var a = await AbrirAsync(Capturas(5));
        await a.MiniaturaAsync("ArrowRight");
        Assert.Equal(1, a.Ventanita.Foco);
        // Alt+→ lleva la segunda al tercer lugar.
        await a.MiniaturaAsync("ArrowRight", alt: true);
        Assert.Equal([0, 2, 1, 3, 4], a.Ventanita.Orden.Select(o => o.Indice));
        Assert.Equal(2, a.Ventanita.Foco);
        await a.MiniaturaAsync("r");
        Assert.Equal(90, a.Ventanita.Orden[2].Giro);
        // Girada, la captura queda acostada: más ancha que alta.
        var pieza = a.Ventanita.Acomodo!.Piezas[2];
        Assert.True(pieza.Ancho > pieza.Alto);
        await a.MiniaturaAsync("Delete");
        Assert.Equal([0, 2, 3, 4], a.Ventanita.Orden.Select(o => o.Indice));
        Assert.Contains("4 imágenes · 1 quitada", await a.Pantalla.TextoAsync());

        await a.MiniaturaAsync("z", ctrl: true);
        Assert.Equal([0, 2, 1, 3, 4], a.Ventanita.Orden.Select(o => o.Indice));
        await a.MiniaturaAsync("z", ctrl: true);
        Assert.Equal(0, a.Ventanita.Orden[2].Giro);
        await a.MiniaturaAsync("z", ctrl: true);
        Assert.Equal([0, 1, 2, 3, 4], a.Ventanita.Orden.Select(o => o.Indice));

        await a.TeclaAsync("Enter");
        Assert.Equal([0, 1, 2, 3, 4], Assert.Single(a.Juntadas.Pedidos).Orden.Select(o => o.Indice));
    }

    [Fact]
    public async Task Espacio_la_ensena_grande_y_Esc_la_cierra_sin_cancelar()
    {
        await using var a = await AbrirAsync(Capturas(5));
        await a.MiniaturaAsync(" ");
        Assert.Contains("Imagen 1 de 5 · 1080 × 2340 píxeles", await a.Pantalla.TextoAsync());
        await a.TeclaAsync("Escape");
        Assert.DoesNotContain("Espacio o Esc para cerrar", await a.Pantalla.TextoAsync());
        Assert.Empty(a.Juntadas.Pedidos);
    }

    [Fact]
    public async Task Esc_con_mas_de_tres_pregunta_y_Enter_borra()
    {
        await using var a = await AbrirAsync(Capturas(24));
        await a.TeclaAsync("Escape");
        Assert.Contains("¿Borrar las 24 imágenes?", await a.Pantalla.TextoAsync());
        Assert.Equal("#jn-si-borrar", a.UltimoFoco);
        Assert.Empty(a.Juntadas.Pedidos);
        await a.TeclaAsync("Escape");
        Assert.DoesNotContain("¿Borrar", await a.Pantalla.TextoAsync());
        Assert.Equal("#jn-num", a.UltimoFoco);

        await a.TeclaAsync("Escape");
        await a.TeclaAsync("Enter");
        Assert.Equal("cancelar", Assert.Single(a.Juntadas.Pedidos).Que);
    }

    [Fact]
    public async Task Esc_con_tres_o_menos_cancela_sin_preguntar()
    {
        await using var a = await AbrirAsync(Capturas(3));
        await a.TeclaAsync("Escape");
        Assert.Equal("cancelar", Assert.Single(a.Juntadas.Pedidos).Que);
    }

    [Fact]
    public async Task Ctrl_O_abre_en_Word_y_sin_Word_no()
    {
        await using (var a = await AbrirAsync(Capturas(3)))
        {
            await a.TeclaAsync("o", ctrl: true);
            Assert.Equal("abrir", Assert.Single(a.Juntadas.Pedidos).Que);
        }
        await using (var b = await AbrirAsync(new JuntadasDePrueba((800, 600)) { HayWord = false }))
        {
            await b.TeclaAsync("o", ctrl: true);
            Assert.Empty(b.Juntadas.Pedidos);
            Assert.True((await b.Pantalla.PorIdAsync("jn-abrir")).Desactivado);
            Assert.Contains("No encontré Word en esta PC", await b.Pantalla.TextoAsync());
        }
    }

    [Fact]
    public async Task Avisa_las_borrosas_y_las_que_no_caben_a_lo_ancho()
    {
        await using var a = await AbrirAsync(new JuntadasDePrueba((1080, 2340), (240, 240), (1600, 400), (160, 160)));
        var avisos = PantallaSinNavegador.Limpio((await a.Pantalla.PorIdAsync("jn-avisos")).Texto);
        Assert.Contains("⚠ 2 se verán borrosas La 2 y la 4", avisos);
        Assert.Contains("↔ 1 no cabe completa", avisos);
    }

    [Fact]
    public async Task Al_juntar_mas_regresa_con_el_mismo_orden_y_las_nuevas_al_final()
    {
        var juntadas = new JuntadasDePrueba((100, 100), (200, 200), (300, 300), (400, 400))
        {
            Orden = [new(2, 90), new(0, 0)],
            Repetidas = 1,
            Ajustes = new AjustesDeJuntarImagenes { Modo = ModoDelTamano.Ancho, Ancho = 7 },
        };
        await using var a = await AbrirAsync(juntadas);
        Assert.Equal([(2, 90), (0, 0), (1, 0), (3, 0)], a.Ventanita.Orden.Select(o => (o.Indice, o.Giro)));
        Assert.Equal("7", (await a.Pantalla.PorIdAsync("jn-num")).Valor);
        Assert.Contains("4 imágenes · 1 repetida", await a.Pantalla.TextoAsync());
        await a.Pantalla.OprimirAsync(await a.Pantalla.PorIdAsync("jn-mas"));
        Assert.Equal("mas", Assert.Single(a.Juntadas.Pedidos).Que);
    }

    [Theory]
    [InlineData("5", 5.0)]
    [InlineData("6,5", 6.5)]
    [InlineData(" 4.2 ", 4.2)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void Lee_el_numero_con_punto_o_con_coma(string texto, double? esperado) =>
        Assert.Equal(esperado, JuntarImagenes.LeerNumero(texto));
}
