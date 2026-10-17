using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que cada caja elige en la búsqueda viaja con lo que cambió de su copia (entrega 5, partes 2 y 3): solo lo nuevo, desde el
/// número hasta el que lo trae su copia. Aquí, lo que pasa a la vez, en el orden exacto que lo rompía: la prueba detiene al
/// servicio en medio y lo suelta (no depende de lo que tarda la PC).
/// </summary>
public sealed class LoElegidoEnLaCopiaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private PapeleriaPos.Servicio.CatalogoEnMemoria Catalogo => servicio.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.CatalogoEnMemoria>();

    /// <summary>
    /// Se elige algo justo mientras se arman los cambios de la copia de la caja: ya tenía su número y todavía no estaba
    /// guardado. Antes los cambios decían «hasta aquí» con ese número sin traerlo, y los siguientes pedían desde después de él:
    /// se perdía para siempre en esa caja. Ahora tomar el número y guardarlo van juntos, y los cambios esperan a que quede.
    /// </summary>
    [Fact]
    public async Task Lo_elegido_justo_mientras_se_arman_los_cambios_no_se_pierde()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        await servicio.CatalogoAlDiaAsync();
        var antes = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));
        var producto = antes.Productos[5];
        using var conElNumero = new ManualResetEventSlim();
        using var soltar = new ManualResetEventSlim();
        Catalogo.EntreElNumeroYLoElegido = () =>
        {
            conElNumero.Set();
            soltar.Wait(TimeSpan.FromSeconds(30));
        };
        try
        {
            // Se elige (en otro hilo, como otra petición) y se detiene con su número ya tomado.
            var eligiendo = Task.Run(() => Catalogo.Aprender(caja.Id, "Texto Nuevo", producto.VarianteId));
            Assert.True(conElNumero.Wait(TimeSpan.FromSeconds(20)));
            // Mientras, la caja pide lo que cambió. Antes contestaba aquí mismo, sin lo elegido; ahora espera a que quede.
            var pidiendo = CopiaPuestaAlDiaTests.CambiosAsync(http, caja.Id, antes);
            await Task.WhenAny(pidiendo, Task.Delay(TimeSpan.FromSeconds(1)));
            soltar.Set();
            await eligiendo;
            var cambios = await pidiendo;

            var alDia = CopiaAlDia.Aplicar(antes, cambios);
            var final = CopiaAlDia.Aplicar(alDia, await CopiaPuestaAlDiaTests.CambiosAsync(http, caja.Id, alDia));
            Assert.Contains(new ElegidoDto("TEXTO NUEVO", producto.VarianteId), final.Elegidos!);
            Assert.Contains(new ElegidoDto("TEXTO NUEVO", producto.VarianteId),
                (await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"))).Elegidos!);
        }
        finally
        {
            Catalogo.EntreElNumeroYLoElegido = null;
            soltar.Set();
        }
    }

    /// <summary>
    /// Al arrancar, lo elegido se lee de la base la primera vez que hace falta. Si mientras se lee la caja elige otra cosa con el
    /// mismo texto, se queda lo nuevo: antes la lectura lo pisaba con lo que decía la base.
    /// </summary>
    [Fact]
    public async Task Lo_elegido_mientras_se_lee_la_base_al_arrancar_no_se_pisa()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        await servicio.CatalogoAlDiaAsync();
        var productos = (await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"))).Productos;
        var (deAntes, nuevo) = (productos[7], productos[8]);
        Assert.True((await http.PostAsJsonAsync("api/busquedas", new SolicitudBusqueda(caja.Id, "Texto Que Cambia", 2, deAntes.VarianteId))).IsSuccessStatusCode);
        // Como al arrancar: lo de memoria se olvida y se vuelve a leer de la base (que dice el de antes).
        Catalogo.Invalidar();
        var leido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var soltar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Catalogo.AlLeerLoElegido = async () =>
        {
            leido.TrySetResult();
            await soltar.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };
        try
        {
            await using var db = servicio.Base();
            var leyendo = Catalogo.ElegidosAsync(db, caja.Id);
            await leido.Task.WaitAsync(TimeSpan.FromSeconds(20));
            // Ya leyó la base; la caja elige otra cosa con el mismo texto.
            Catalogo.Aprender(caja.Id, "texto que cambia", nuevo.VarianteId);
            soltar.SetResult();
            await leyendo;

            var (_, elegidos) = await Catalogo.ElegidosAsync(db, caja.Id);
            Assert.Contains(new ElegidoDto("TEXTO QUE CAMBIA", nuevo.VarianteId), elegidos);
        }
        finally
        {
            Catalogo.AlLeerLoElegido = null;
            soltar.TrySetResult();
        }
    }
}
