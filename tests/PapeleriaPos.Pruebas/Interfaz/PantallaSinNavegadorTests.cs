using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El ayudante de las pruebas de pantalla (<see cref="PantallaSinNavegador"/>): lo que se oprime o se escribe llega al elemento
/// que la prueba leyó aunque la pantalla se haya vuelto a dibujar en medio (terminó de cargar algo, llegó un aviso) y su evento
/// ya tenga otro número. Antes fallaba a veces con «There is no event handler associated with this event» en el CI.
/// </summary>
public class PantallaSinNavegadorTests
{
    /// <summary>
    /// Como la devolución: un folio con id, dos cantidades iguales (sin id, una por renglón), el motivo (sin id ni texto) y un
    /// botón. Cada dibujo arma sus manejadores otra vez (como <c>@bind</c> o una lambda por renglón), así que cada evento
    /// cambia de número al volver a dibujarse; cada manejador anota de qué dibujo es.
    /// </summary>
    private sealed class Formulario : ComponentBase
    {
        private int _dibujos;

        public string Folio { get; private set; } = "";
        public string Motivo { get; private set; } = "";
        public string[] Cantidades { get; } = ["", ""];
        public string Cancelado { get; private set; } = "";

        /// <summary>Lo que hace la pantalla cuando algo de fondo la cambia: se vuelve a dibujar.</summary>
        public void Redibujar() => StateHasChanged();

        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            var dibujo = ++_dibujos;
            b.OpenElement(0, "input");
            b.AddAttribute(1, "id", "folio");
            b.AddAttribute(2, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e => Folio = $"{e.Value} (dibujo {dibujo})"));
            b.CloseElement();
            for (var i = 0; i < Cantidades.Length; i++)
            {
                var renglon = i;
                b.OpenElement(3, "input");
                b.AddAttribute(4, "class", "cantidad");
                b.AddAttribute(5, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => Cantidades[renglon] = $"{e.Value} (dibujo {dibujo})"));
                b.CloseElement();
            }
            b.OpenElement(6, "label");
            b.AddContent(7, "Motivo ");
            b.OpenElement(8, "input");
            b.AddAttribute(9, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e => Motivo = $"{e.Value} (dibujo {dibujo})"));
            b.CloseElement();
            b.CloseElement();
            b.OpenElement(10, "button");
            b.AddAttribute(11, "onclick", EventCallback.Factory.Create(this, () => Cancelado = $"dibujo {dibujo}"));
            b.AddContent(12, "Cancelar todo el ticket");
            b.CloseElement();
        }
    }

    private static async Task<(PantallaSinNavegador Pantalla, Formulario Formulario)> AbrirAsync()
    {
        var pantalla = new PantallaSinNavegador(new ServiceCollection().BuildServiceProvider());
        return (pantalla, await pantalla.AbrirAsync<Formulario>(new()));
    }

    /// <summary>Se vuelve a dibujar y revisa que el evento ya tenga otro número: el de antes ya no existe.</summary>
    private static async Task RedibujarAsync(PantallaSinNavegador p, Formulario f, ElementoDibujado antes, string evento,
        Func<List<ElementoDibujado>, ElementoDibujado> cual)
    {
        await p.Dispatcher.InvokeAsync(f.Redibujar);
        Assert.NotEqual(antes.Eventos[evento], cual((await p.LeerAsync()).Elementos).Eventos[evento]);
    }

    private static ElementoDibujado Motivo(List<ElementoDibujado> elementos) =>
        elementos.First(e => e.Etiqueta == "input" && e.Id is null && e.Clase != "cantidad");

    private static ElementoDibujado SegundaCantidad(List<ElementoDibujado> elementos) => elementos.Where(e => e.Clase == "cantidad").ToList()[1];

    [Fact]
    public async Task Un_campo_sin_id_ni_texto_recibe_lo_que_se_escribe_aunque_se_haya_vuelto_a_dibujar()
    {
        var (p, f) = await AbrirAsync();
        await using var _ = p;
        // Como el motivo de la devolución (AlCobrarEnLaVentaTests): el único campo sin id que no es una cantidad.
        var motivo = await p.ElementoAsync(e => e.Etiqueta == "input" && e.Id is null && e.Clase != "cantidad", "el motivo");
        await RedibujarAsync(p, f, motivo, "oninput", Motivo);

        await p.DispararAsync(motivo, "oninput", new ChangeEventArgs { Value = "Ya no lo quiso" });

        Assert.Equal("Ya no lo quiso (dibujo 2)", f.Motivo);
        Assert.Equal(["", ""], f.Cantidades);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Uno_de_varios_iguales_leido_de_la_lista_es_el_mismo_aunque_se_haya_vuelto_a_dibujar()
    {
        var (p, f) = await AbrirAsync();
        await using var _ = p;
        // Leído de la lista, sin buscarlo con una condición: la segunda cantidad, igual a la primera salvo por su lugar.
        var segunda = SegundaCantidad((await p.LeerAsync()).Elementos);
        await RedibujarAsync(p, f, segunda, "onchange", SegundaCantidad);

        await p.DispararAsync(segunda, "onchange", new ChangeEventArgs { Value = "3" });

        Assert.Equal(["", "3 (dibujo 2)"], f.Cantidades);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Lo_que_se_busca_por_id_o_por_su_texto_tambien()
    {
        var (p, f) = await AbrirAsync();
        await using var _ = p;
        var folio = await p.PorIdAsync("folio");
        var cancelar = await p.BotonAsync("Cancelar todo el ticket");
        await RedibujarAsync(p, f, folio, "oninput", elementos => elementos.Single(e => e.Id == "folio"));

        await p.DispararAsync(folio, "oninput", new ChangeEventArgs { Value = "A-000123" });
        // Escribir en el folio la volvió a dibujar (el 3): el botón leído en el primero se oprime en el que se ve.
        await p.OprimirAsync(cancelar);

        Assert.Equal(("A-000123 (dibujo 2)", "dibujo 3"), (f.Folio, f.Cancelado));
        Assert.Empty(p.Errores);
    }
}
