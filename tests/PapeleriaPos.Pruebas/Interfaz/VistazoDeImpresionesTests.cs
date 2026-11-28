using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El vistazo rápido de F2 (2026-11-28, el dueño: «agregar la función de ampliar imagen también para las impresiones, similar a la de
/// los productos», como la foto del producto de #213): Ctrl+Espacio en una tarjeta enseña su vista previa en grande, ↑ ↓ la cambian por
/// la de la otra tarjeta, Espacio la elige sin cerrarla, Ctrl+Espacio o un clic (Esc, en caja.js) la cierran y el cursor vuelve a la
/// tarjeta; Enter agrega como siempre. Todo con el teclado. Con su propio servicio: solo estas impresiones.
/// </summary>
public sealed class VistazoDeImpresionesTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    private static async Task CtrlEspacioAsync(PantallaSinNavegador p, string id) =>
        await p.DispararAsync(await p.PorIdAsync(id), "onkeydown", new KeyboardEventArgs { Key = " ", CtrlKey = true });

    private static async Task<ElementoDibujado?> VistazoAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos.SingleOrDefault(e => e.Id == "imp-vistazo");

    private static async Task<string?> SrcDelVistazoAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos.SingleOrDefault(e => e.Clase == "imp-vistazo-img")?.Atributos["src"] as string;

    [Fact]
    public async Task Con_el_teclado_se_abre_se_recorre_y_se_cierra_y_sigue_a_la_tarjeta_del_cursor()
    {
        var (http, caja, _, _) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ahora = DateTime.UtcNow;
        var miniatura = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 4, 5, 6]);
        var conVista = ApiDeImpresoras.Trabajo(caja, "Credencial.pdf", 1, ahora.AddMinutes(-30), null) with { MiniaturaBase64 = miniatura };
        var sinVista = ApiDeImpresoras.Trabajo(caja, "Sin vista.pdf", 2, ahora, null);
        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones", conVista));
        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones", sinVista));

        var pagina = new PaginaQueContesta();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = new ServiceCollection().AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion)).AddSingleton<IJSRuntime>(pagina).BuildServiceProvider();
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var sesion = await cliente.EntrarAsync("cajero", "1111");
        ImpresionesPorCobrar.Elegidas? agregadas = null;
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ImpresionesPorCobrar>(new()
        {
            [nameof(ImpresionesPorCobrar.Caja)] = caja, [nameof(ImpresionesPorCobrar.Sesion)] = sesion,
            [nameof(ImpresionesPorCobrar.AlAgregar)] = EventCallback.Factory.Create<ImpresionesPorCobrar.Elegidas>(this, e => agregadas = e),
        });
        await p.EsperarAsync(t => t.Contains("Sin vista.pdf"), "las tarjetas");
        await CajaConVariasImpresoras.EsperarAsync(async () => (await p.LeerAsync()).Elementos.Any(e => e.Etiqueta == "img"
            && e.Atributos.GetValueOrDefault("src") is string src && src.EndsWith(miniatura, StringComparison.Ordinal)), "la miniatura");
        var primera = $"imp-m{sinVista.Id:N}";
        var segunda = $"imp-m{conVista.Id:N}";

        // La miniatura lleva lo que caja.js enseña en grande al pasar el mouse; la ayuda del panel dice la tecla.
        var mini = Assert.Single((await p.LeerAsync()).Elementos, e => e.Etiqueta == "img" && e.Atributos.ContainsKey("data-foto"));
        Assert.Equal(mini.Atributos["src"], mini.Atributos["data-foto"]);
        Assert.Equal("Credencial.pdf", mini.Atributos["data-foto-nombre"]);
        Assert.Contains("Ctrl+Espacio ver en grande", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        Assert.Null(await VistazoAsync(p));

        // Ctrl+Espacio en la primera (la más nueva, sin vista previa): se abre y lo dice; no la elige.
        await CtrlEspacioAsync(p, primera);
        var vistazo = await VistazoAsync(p);
        Assert.NotNull(vistazo);
        Assert.Contains("Sin vista.pdf", vistazo.Texto);
        Assert.Contains("no tiene vista previa", vistazo.Texto);
        Assert.Null(await SrcDelVistazoAsync(p));
        Assert.Equal("false", (await p.PorIdAsync(primera)).Atributos["aria-checked"]);

        // ↓ pasa a la otra y el vistazo la sigue, ya con su miniatura.
        await p.TeclaAsync(primera, "ArrowDown");
        Assert.Contains(pagina.Llamadas, l => l.Funcion == "enfocar" && Equals(l.Argumentos?[0], $"#{segunda}"));
        Assert.Contains("Credencial.pdf", (await VistazoAsync(p))!.Texto);
        Assert.EndsWith(miniatura, await SrcDelVistazoAsync(p));

        // Espacio la elige y el vistazo se queda; Ctrl+Espacio lo cierra.
        await p.TeclaAsync(segunda, " ");
        Assert.Equal("true", (await p.PorIdAsync(segunda)).Atributos["aria-checked"]);
        Assert.NotNull(await VistazoAsync(p));
        await CtrlEspacioAsync(p, segunda);
        Assert.Null(await VistazoAsync(p));

        // Otra vez abierto, el clic (lo que hace caja.js con Esc) lo cierra y el cursor regresa a su tarjeta.
        await CtrlEspacioAsync(p, segunda);
        var llamadas = pagina.Llamadas.Count;
        await p.OprimirAsync((await VistazoAsync(p))!);
        Assert.Null(await VistazoAsync(p));
        Assert.Contains(pagina.Llamadas.Skip(llamadas), l => l.Funcion == "enfocar" && Equals(l.Argumentos?[0], $"#{segunda}"));

        // Enter con el vistazo abierto lo cierra y agrega lo elegido, como siempre.
        await CtrlEspacioAsync(p, segunda);
        await p.TeclaAsync(segunda, "Enter");
        Assert.Equal([conVista.Id], agregadas!.DelModulo.Select(t => t.Id));
        Assert.Null(await VistazoAsync(p));
        Assert.Empty(p.Errores);
    }
}
