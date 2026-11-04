using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Varias impresoras en la caja (2026-11-03): Configuración › Impresoras e impresiones las lista, agrega otra y cambia las botellas de la
/// L5590; con una sola se ve como antes. F2 dice de qué impresora salió cada trabajo y las copias del panel se registran en la que se
/// elige.
/// </summary>
internal static class CajaConVariasImpresoras
{
    public static ServiceProvider Servicios(ServicioConVariasImpresoras servicio, CajaDto caja)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        return new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .BuildServiceProvider();
    }

    public static async Task EsperarAsync(Func<Task<bool>> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!await condicion())
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No llegó {que}.");
            await Task.Delay(20);
        }
    }
}

/// <summary>Configuración › Impresoras e impresiones con varias impresoras (cada prueba con su propio servicio).</summary>
public sealed class VariasImpresorasEnLaConfiguracionTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    private ServiceProvider Servicios(CajaDto caja) => CajaConVariasImpresoras.Servicios(servicio, caja);

    private static Task EsperarAsync(Func<Task<bool>> condicion, string que) => CajaConVariasImpresoras.EsperarAsync(condicion, que);

    [Fact]
    public async Task La_configuracion_lista_las_impresoras_agrega_otra_y_cambia_las_botellas_de_la_L5590()
    {
        var (http, caja, _, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto("wf-de-prueba", 10, 5))));
        await using var servicios = Servicios(caja);
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var sesion = await cliente.EntrarAsync("dueno", "3333");
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = sesion });

        // Con una sola, como antes: sin su nombre arriba ni el campo del nombre; los campos de siempre.
        Assert.Equal("wf-de-prueba", (await p.EsperarElementoAsync("impresora-host")).Valor);
        var antes = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.DoesNotContain("impresora 1 ·", antes);
        Assert.Contains("Agregar una impresora", antes);
        Assert.Null((await p.LeerAsync()).Elementos.FirstOrDefault(e => e.Id == "impresora-nombre"));

        await p.EscribirAsync("impresora-nueva-nombre", "L5590");
        await p.OprimirAsync(await p.PorIdAsync("impresora-nueva-L5590"));
        await p.EscribirAsync("impresora-nueva-host", "l5590-de-prueba");
        await p.OprimirAsync(await p.BotonAsync("Agregar"));

        Assert.Equal("l5590-de-prueba", (await p.EsperarElementoAsync("impresora-host-2")).Valor);
        var despues = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.Contains("impresora 2 · Epson EcoTank L5590", despues);
        Assert.Contains("impresora 1 · Epson WorkForce Pro WF-6590", despues);
        Assert.Contains("Agregada como la impresora 2", despues);
        Assert.Contains("Por verificar en la tienda", despues);
        Assert.Equal("WF-6590", (await p.PorIdAsync("impresora-nombre")).Valor);
        Assert.Empty(p.Errores);

        // Sus botellas, en los costos: sin precios (salen en lo que falta) y se escriben aquí.
        await EsperarAsync(async () => (await p.LeerAsync()).Elementos.Any(e => e.Id == "costo-impresora-2-0"), "la tabla de las botellas");
        Assert.Contains("Botellas de la L5590", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        Assert.Contains("Botella negra de la L5590", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        // Sin precios no sale gratis: lo dice debajo de las botellas.
        Assert.Contains("se cobra como en una WF-6590", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        await p.EscribirAsync("costo-impresora-2-0", "259");
        await p.TeclaAsync("costo-impresora-2-0", "Enter");
        await EsperarAsync(async () => (await cliente.CostosImpresionAsync(caja.SucursalId, sesion.UsuarioId))!.Impresoras!.Single().Tintas[0].Precio == 259,
            "el precio de la botella negra guardado");
        // Cambiarle el nombre, al salir del campo.
        await p.EscribirAsync("impresora-nombre-2", "L5590 del fondo");
        await p.SalirAsync("impresora-nombre-2");
        await EsperarAsync(async () => (await cliente.ImpresorasAsync(caja.SucursalId))!.Any(i => i.Nombre == "L5590 del fondo"), "el nombre nuevo");
        Assert.Empty(p.Errores);
    }

}

/// <summary>Una tienda sin impresoras todavía (2026-11-03: al instalar desde cero la lista viene vacía): su IP se escribe como siempre.</summary>
public sealed class ImpresorasEnUnaTiendaNuevaTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    [Fact]
    public async Task Sin_ninguna_sale_el_campo_de_la_IP_de_siempre_y_al_escribirla_se_da_de_alta_la_1()
    {
        var (http, caja, _, _) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        Assert.Empty((await http.GetFromJsonAsync<List<ImpresoraDto>>(ApiDeImpresoras.Ruta(caja)))!);
        await using var servicios = CajaConVariasImpresoras.Servicios(servicio, caja);
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var sesion = await cliente.EntrarAsync("dueno", "3333");
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = sesion });

        Assert.Equal("", (await p.EsperarElementoAsync("impresora-host")).Valor);
        Assert.Contains("Escribe la IP de la impresora (la WF-6590)", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        // Mirarla no la da de alta.
        Assert.Empty((await cliente.ImpresorasAsync(caja.SucursalId))!);

        await p.EscribirAsync("impresora-host", "wf-de-prueba");
        await p.SalirAsync("impresora-host");
        await CajaConVariasImpresoras.EsperarAsync(async () => (await cliente.ImpresorasAsync(caja.SucursalId))!.Count == 1, "la impresora 1");

        var primera = Assert.Single((await cliente.ImpresorasAsync(caja.SucursalId))!);
        Assert.Equal((1, "WF-6590", "wf-de-prueba"), (primera.Numero, primera.Nombre, primera.Estado.Configuracion.Host));
        // Desde aquí es una como las demás: al agregar otra, salen las dos.
        await p.EscribirAsync("impresora-nueva-nombre", "WF-6590 2");
        await p.EscribirAsync("impresora-nueva-host", "otra-de-prueba");
        await p.OprimirAsync(await p.BotonAsync("Agregar"));
        Assert.Equal("otra-de-prueba", (await p.EsperarElementoAsync("impresora-host-2")).Valor);
        Assert.Equal("wf-de-prueba", (await p.PorIdAsync("impresora-host")).Valor);
        Assert.Empty(p.Errores);
    }
}

/// <summary>F2 con varias impresoras.</summary>
public sealed class VariasImpresorasEnF2Tests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    private ServiceProvider Servicios(CajaDto caja) => CajaConVariasImpresoras.Servicios(servicio, caja);

    private static Task EsperarAsync(Func<Task<bool>> condicion, string que) => CajaConVariasImpresoras.EsperarAsync(condicion, que);

    [Fact]
    public async Task F2_dice_de_que_impresora_salio_cada_trabajo_y_las_copias_van_a_la_que_se_elige()
    {
        var (http, caja, _, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ruta = ApiDeImpresoras.Ruta(caja);
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto(null, 10, 5))));
        var otra = (await http.GetFromJsonAsync<List<ImpresoraDto>>(ruta))!.FirstOrDefault(i => i.Perfil == PerfilImpresora.L5590)
            ?? await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ruta,
                new SolicitudNuevaImpresora(dueno.UsuarioId, "L5590 de F2", PerfilImpresora.L5590)));
        var ahora = DateTime.UtcNow;
        var enLaWf = ApiDeImpresoras.Trabajo(caja, "En la WF.pdf", 2, ahora, 1);
        var enLaOtra = ApiDeImpresoras.Trabajo(caja, "En la otra.pdf", 1, ahora, otra.Numero);
        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones", enLaWf));
        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones", enLaOtra));
        await using var servicios = Servicios(caja);
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var sesion = await cliente.EntrarAsync("cajero", "1111");
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ImpresionesPorCobrar>(new() { [nameof(ImpresionesPorCobrar.Caja)] = caja, [nameof(ImpresionesPorCobrar.Sesion)] = sesion });

        await p.EsperarAsync(t => t.Contains("En la otra.pdf"), "las tarjetas");
        var tarjetaWf = await p.ElementoAsync(e => e.Atributos.GetValueOrDefault("aria-label") is string l && l.StartsWith("En la WF.pdf"), "la tarjeta de la WF");
        var tarjetaOtra = await p.ElementoAsync(e => e.Atributos.GetValueOrDefault("aria-label") is string l && l.StartsWith("En la otra.pdf"), "la de la otra");
        Assert.EndsWith(", en la WF-6590", (string)tarjetaWf.Atributos["aria-label"]!);
        Assert.EndsWith($", en la {otra.Nombre}", (string)tarjetaOtra.Atributos["aria-label"]!);
        Assert.Contains(otra.Nombre, tarjetaOtra.Texto);

        // Las copias del panel, en la que se elige.
        await p.OprimirAsync(await p.BotonAsync("Copias del panel"));
        var lista = await p.EsperarElementoAsync("copias-impresora");
        await p.DispararAsync(lista, "onchange", new ChangeEventArgs { Value = otra.Numero.ToString() });
        await p.EsperarAsync(t => t.Contains("Precio sugerido"), "el precio sugerido");
        await p.OprimirAsync(await p.BotonAsync("Registrar y elegir"));
        await EsperarAsync(async () => (await cliente.ImpresionesPorCobrarAsync(caja.SucursalId, sesion.UsuarioId))
            .Any(t => t.Tipo == TipoTrabajoImpresion.Copia && t.Impresora == otra.Nombre), "las copias en la otra impresora");
        Assert.Empty(p.Errores);
    }
}
