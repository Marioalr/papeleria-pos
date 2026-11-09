using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Las tarjetas «Copias o celular» de punta a punta (2026-11-08): una impresora de prueba cuenta caras que nadie registró, la vigilancia
/// (sus lecturas, pedidas por la prueba) pone la tarjeta, F2 la enseña con su precio estimado, el cajero la parte con el teclado, las
/// copias que registra se toman de otra, «Ya se cobró como copias» la quita y lo que queda se agrega al ticket. La Configuración enseña lo
/// del día y sus ajustes. Con su propio servicio.
/// </summary>
public sealed class TarjetasDelContadorEnF2Tests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    private const string Host = "wf-del-contador";

    /// <summary>La impresora cuenta esas caras (y de ellas, a color) y la vigilancia la lee hasta cerrar la tarjeta, como cada 10 s.</summary>
    private async Task ImprimirSinRegistrarAsync(HttpClient http, CajaDto caja, int caras, int color = 0)
    {
        var ruta = $"{ApiDeImpresoras.Ruta(caja)}/1/leer";
        var antes = servicio.Impresoras[Host];
        servicio.Impresoras[Host] = antes with
        {
            LifeCount = antes.LifeCount + caras, ColorCount = antes.ColorCount + color, MonoCount = antes.MonoCount + caras - color,
        };
        foreach (var espera in new[] { 10, 70, 30 })
        {
            servicio.Reloj.Avanzar(TimeSpan.FromSeconds(espera));
            await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsync(ruta, null));
        }
    }

    [Fact]
    public async Task La_impresora_cuenta_caras_sin_registrar_y_F2_las_enseña_las_parte_y_las_cobra()
    {
        var (http, caja, cajero, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        servicio.Impresoras[Host] = servicio.Impresoras[Host] with { Serial = "X1" };
        // Lo que contó antes de leerla la primera vez no es tarjeta.
        servicio.Impresoras.Contar(Host, 50);
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto(Host, 10, 5))));

        // Un cliente imprime desde su celular 3 caras, una a color.
        await ImprimirSinRegistrarAsync(http, caja, 3, color: 1);
        var porCobrar = (await http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>(
            $"api/sucursales/{caja.SucursalId}/impresiones/por-cobrar?usuarioId={cajero.UsuarioId}"))!;
        var tarjeta = Assert.Single(porCobrar);
        Assert.Equal((TipoTrabajoImpresion.Contador, "Copias o celular · 3 caras (2 B/N, 1 color)", 3), (tarjeta.Tipo, tarjeta.Documento, tarjeta.Contador!.Contadas));
        Assert.True(tarjeta.PrecioCentavos > 0);

        await using var servicios = CajaConVariasImpresoras.Servicios(servicio, caja);
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var sesion = await cliente.EntrarAsync("cajero", "1111");
        ImpresionesPorCobrar.Elegidas? agregadas = null;
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ImpresionesPorCobrar>(new()
        {
            [nameof(ImpresionesPorCobrar.Caja)] = caja, [nameof(ImpresionesPorCobrar.Sesion)] = sesion,
            [nameof(ImpresionesPorCobrar.AlAgregar)] = EventCallback.Factory.Create<ImpresionesPorCobrar.Elegidas>(this, e => agregadas = e),
        });
        await p.EsperarAsync(t => t.Contains("Copias o celular"), "la tarjeta del contador");
        var texto = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.Contains("Contador", texto);
        Assert.Contains("Precio estimado", texto);
        Assert.Contains("Celular o copias", texto);
        Assert.Contains("La impresora las contó", texto);

        // Partirla: 1 fue copia del panel en B/N; del celular se cobran 1 en B/N y 1 a color. Con el teclado: Enter guarda.
        await p.OprimirAsync(await p.BotonAsync("Partir o corregir"));
        await p.EsperarElementoAsync("partir-copias-bn");
        Assert.Contains("la impresora contó 3 caras (2 en B/N y 1 a color)", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        await p.EscribirAsync("partir-copias-bn", "1");
        await p.EscribirAsync("partir-celular-bn", "1");
        await p.TeclaAsync("partir-celular-bn", "Enter");
        await p.EsperarAsync(t => t.Contains("Copias o celular · 2 caras (1 B/N, 1 color)") && t.Contains("1 copia en B/N"), "la tarjeta partida y sus copias");
        Assert.Empty(p.Errores);

        // Las dos salen elegidas: Enter las agrega al ticket (la tarjeta en «Impresiones», las copias en su línea).
        var lista = (await cliente.ImpresionesPorCobrarAsync(caja.SucursalId, sesion.UsuarioId)).OrderBy(t => t.Tipo).ToList();
        Assert.Equal([TipoTrabajoImpresion.Copia, TipoTrabajoImpresion.Contador], lista.Select(t => t.Tipo).Order());
        var copias = lista.Single(t => t.Tipo == TipoTrabajoImpresion.Copia);
        await p.TeclaAsync($"imp-m{copias.Id:N}", "Enter");
        Assert.Equal(lista.Select(t => t.Id).Order(), agregadas!.DelModulo.Select(t => t.Id).Order());

        // Otra tanda sin registrar (2 caras): el cajero registra después 1 copia del panel y se toma de ella.
        await ImprimirSinRegistrarAsync(http, caja, 2);
        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones/copias",
            new SolicitudCopias(Guid.NewGuid(), caja.Id, sesion.UsuarioId, 1, false, "Carta", false, null, null)));
        var otra = (await cliente.ImpresionesPorCobrarAsync(caja.SucursalId, sesion.UsuarioId)).Single(t => t.Tipo == TipoTrabajoImpresion.Contador
            && t.Id != tarjeta.Id);
        Assert.Equal("Copias o celular · 1 cara (B/N)", otra.Documento);

        // «Ya se cobró como copias»: se junta con las copias de antes y deja de estar por cobrar.
        await p.EsperarAsync(t => t.Contains("Copias o celular · 1 cara (B/N)"), "la otra tarjeta");
        var botones = (await p.LeerAsync()).Elementos.Where(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Ya se cobró como copias")
            .ToList();
        Assert.Equal(2, botones.Count);
        // La más nueva va primero.
        await p.OprimirAsync(botones[0]);
        var ligar = await p.EsperarElementoAsync($"ligar-{copias.Id:N}");
        await p.OprimirAsync(ligar);
        await p.EsperarAsync(t => !t.Contains("Copias o celular · 1 cara"), "la tarjeta ligada fuera de F2");
        Assert.Contains("Copias o celular · 2 caras", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        Assert.Empty(p.Errores);

        // La Configuración dice lo del día: lo que contó, lo registrado y lo que fue a tarjetas.
        var impresora = Assert.Single((await http.GetFromJsonAsync<List<ImpresoraDto>>(ApiDeImpresoras.Ruta(caja)))!);
        var ajustes = impresora.Contador!.Ajustes;
        Assert.Equal((true, 5d, 5d, 60), (ajustes.Activas, ajustes.CoberturaBN, ajustes.CoberturaColor, ajustes.PausaSegundos));
        var dia = impresora.Contador.Hoy!;
        Assert.Equal(5L, dia.Contadas);
        Assert.Equal(2, dia.EnTarjetas);
        // Las copias: la de la tarjeta partida, la registrada y la cara que se les juntó.
        Assert.Equal(3, dia.Copias);

        var dueno2 = await cliente.EntrarAsync("dueno", "3333");
        await using var c = new PantallaSinNavegador(servicios);
        await c.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = dueno2 });
        await c.EsperarAsync(t => t.Contains("«Copias o celular» hoy"), "lo del día");
        Assert.Contains("2 caras en tarjetas (0 cobradas, 2 por cobrar y 0 no vendidas)", PantallaSinNavegador.Limpio(await c.TextoAsync()));
        // Quitarlas: se guarda al momento.
        await c.OprimirAsync(await c.PorIdAsync("impresora-contador"));
        await CajaConVariasImpresoras.EsperarAsync(async () => (await cliente.ImpresorasAsync(caja.SucursalId))!.Single().Contador!.Ajustes.Activas == false,
            "las tarjetas quitadas");
        await c.EscribirAsync("impresora-contador-pausa", "90");
        await c.SalirAsync("impresora-contador-pausa");
        await CajaConVariasImpresoras.EsperarAsync(async () => (await cliente.ImpresorasAsync(caja.SucursalId))!.Single().Contador!.Ajustes.PausaSegundos == 90,
            "la pausa nueva");
        Assert.Empty(c.Errores);
    }
}
