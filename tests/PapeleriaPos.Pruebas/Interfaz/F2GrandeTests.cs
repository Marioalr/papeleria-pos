using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// F2 con lo que pidió el dueño (2026-11-26): «No vendida» es una × arriba a la derecha de la tarjeta (con la misma confirmación), y la
/// vista grande («Agrandar») trae todas las que faltan, no solo las 200 más nuevas, con una barra para buscar sin acentos ni mayúsculas.
/// Con su propio servicio: solo estas impresiones.
/// </summary>
public sealed class F2GrandeTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    [Fact]
    public async Task La_x_de_la_tarjeta_marca_no_vendida_con_la_confirmacion_de_siempre()
    {
        var (http, caja, _, _) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var trabajo = ApiDeImpresoras.Trabajo(caja, "No la quiso.pdf", 2, DateTime.UtcNow, null);
        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones", trabajo));

        var (pagina, servicios, cliente) = Caja(caja);
        await using var _s = servicios;
        var sesion = await cliente.EntrarAsync("cajero", "1111");
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ImpresionesPorCobrar>(new() { [nameof(ImpresionesPorCobrar.Caja)] = caja, [nameof(ImpresionesPorCobrar.Sesion)] = sesion });
        await p.EsperarAsync(t => t.Contains("No la quiso.pdf"), "la tarjeta");

        // Ya no hay botón «No vendida» con letras: es la × de la tarjeta, con su nombre para el lector de pantalla.
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "No vendida");
        var x = await p.PorIdAsync($"no-vendida-{trabajo.Id:N}");
        Assert.Equal("button", x.Etiqueta);
        Assert.Equal("No vendida", x.Atributos["aria-label"]);
        Assert.Equal("No vendida", x.Atributos["title"]);
        // No elige la tarjeta (su clic no llega a ella).
        Assert.True(x.Eventos.ContainsKey("onclick"));
        Assert.Equal("false", (await p.PorIdAsync($"imp-m{trabajo.Id:N}")).Atributos["aria-checked"]);

        await p.OprimirAsync(x);
        Assert.Contains("Marcar como no vendida", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        await p.OprimirAsync(await p.BotonAsync("Marcar no vendida"));
        await p.EsperarAsync(t => !t.Contains("No la quiso.pdf"), "que se quite la no vendida");
        Assert.DoesNotContain(await cliente.ImpresionesPorCobrarAsync(caja.SucursalId, sesion.UsuarioId), t => t.Id == trabajo.Id);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task La_vista_grande_trae_todas_y_busca_sin_acentos()
    {
        var (http, caja, _, _) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        // Más de las que trae la de siempre (las 200 más nuevas), cada una a 5 minutos de la otra (cada una en su lote).
        var ahora = DateTime.UtcNow;
        var vieja = ApiDeImpresoras.Trabajo(caja, "Cotización Pelikán.pdf", 1, ahora.AddMinutes(-5 * (RegistroImpresiones.MaximoPorCobrar + 5)), null);
        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones", vieja));
        for (var i = 0; i < RegistroImpresiones.MaximoPorCobrar + 1; i++)
            await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones",
                ApiDeImpresoras.Trabajo(caja, $"Tarea {i:000}.pdf", 1, ahora.AddMinutes(-5 * i), null)));

        var (pagina, servicios, cliente) = Caja(caja);
        await using var _s = servicios;
        var sesion = await cliente.EntrarAsync("cajero", "1111");
        var todas = await cliente.ImpresionesPorCobrarAsync(caja.SucursalId, sesion.UsuarioId, todas: true);
        Assert.Equal(RegistroImpresiones.MaximoPorCobrar, (await cliente.ImpresionesPorCobrarAsync(caja.SucursalId, sesion.UsuarioId)).Count);
        Assert.True(todas.Count > RegistroImpresiones.MaximoPorCobrar);
        Assert.Contains(todas, t => t.Id == vieja.Id);

        // La de siempre: la más vieja no sale, y se dice cuántas faltan.
        await using (var chica = new PantallaSinNavegador(servicios))
        {
            await chica.AbrirAsync<ImpresionesPorCobrar>(new() { [nameof(ImpresionesPorCobrar.Caja)] = caja, [nameof(ImpresionesPorCobrar.Sesion)] = sesion });
            await chica.EsperarAsync(t => t.Contains("Tarea 000.pdf"), "las tarjetas");
            var texto = PantallaSinNavegador.Limpio(await chica.TextoAsync());
            Assert.DoesNotContain("Cotización Pelikán.pdf", texto);
            Assert.Contains("más por cobrar de esta sucursal", texto);
            Assert.DoesNotContain((await chica.LeerAsync()).Elementos, e => e.Id == "imp-buscar");
        }

        // La grande: todas, la barra para buscar con el cursor, y sin «más por cobrar».
        ImpresionesPorCobrar.Elegidas? agregadas = null;
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ImpresionesPorCobrar>(new()
        {
            [nameof(ImpresionesPorCobrar.Caja)] = caja, [nameof(ImpresionesPorCobrar.Sesion)] = sesion, [nameof(ImpresionesPorCobrar.Grande)] = true,
            [nameof(ImpresionesPorCobrar.AlAgregar)] = EventCallback.Factory.Create<ImpresionesPorCobrar.Elegidas>(this, e => agregadas = e),
        });
        await p.EsperarAsync(t => t.Contains("Cotización Pelikán.pdf"), "la más vieja");
        Assert.DoesNotContain("más por cobrar de esta sucursal", await p.TextoAsync());
        Assert.Contains($"{todas.Count} impresiones", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        await CajaConVariasImpresoras.EsperarAsync(() => Task.FromResult(pagina.Llamadas.Any(l => l.Funcion == "enfocar" && Equals(l.Argumentos?[0], "#imp-buscar"))),
            "el cursor en la barra para buscar");

        // Sin acentos ni mayúsculas, y cada palabra cuenta.
        await p.EscribirAsync("imp-buscar", "  pelikan  COTIZACION ");
        var visto = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.Contains("Cotización Pelikán.pdf", visto);
        Assert.DoesNotContain("Tarea 000.pdf", visto);
        Assert.Contains($"1 de {todas.Count}", visto);
        await p.EscribirAsync("imp-buscar", "pelikan tarea");
        Assert.Contains("Ninguna impresión por cobrar tiene", await p.TextoAsync());

        // ↓ desde la barra pasa a la tarjeta; Espacio la elige y Enter la agrega, como siempre; la × sigue en ella.
        await p.EscribirAsync("imp-buscar", "pelikan");
        var tarjeta = $"imp-m{vieja.Id:N}";
        await p.TeclaAsync("imp-buscar", "ArrowDown");
        Assert.Contains(pagina.Llamadas, l => l.Funcion == "enfocar" && Equals(l.Argumentos?[0], $"#{tarjeta}"));
        await p.PorIdAsync($"no-vendida-{vieja.Id:N}");
        await p.TeclaAsync(tarjeta, " ");
        Assert.Equal("true", (await p.PorIdAsync(tarjeta)).Atributos["aria-checked"]);
        await p.TeclaAsync(tarjeta, "Enter");
        Assert.Equal([vieja.Id], agregadas!.DelModulo.Select(t => t.Id));
        Assert.Empty(p.Errores);
    }

    private (PaginaQueContesta Pagina, ServiceProvider Servicios, ClienteCaja Cliente) Caja(CajaDto caja)
    {
        var pagina = new PaginaQueContesta();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var servicios = new ServiceCollection().AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion)).AddSingleton<IJSRuntime>(pagina).BuildServiceProvider();
        return (pagina, servicios, servicios.GetRequiredService<ClienteCaja>());
    }
}
