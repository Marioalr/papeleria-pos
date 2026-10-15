using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Lo de la Configuración nueva (entrega 7) que usan la entrada, abrir el turno y la venta: el nombre de la tienda, el fondo
/// sugerido y las fichas de la espera. Sin navegador, contra el servicio de verdad.
/// </summary>
public sealed class ConfiguracionEnLaCajaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Dueno, SesionDto Cajero,
        SesionDto Encargado);

    private async Task<Caja> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var cliente = new ClienteCaja(servicio.Fabrica.CreateClient(), conexion);
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(http, cliente, caja, servicios, await cliente.EntrarAsync("dueno", "3333"), await cliente.EntrarAsync("cajero", "1111"),
            await cliente.EntrarAsync("encargado", "2222"));
    }

    private static async Task ConfigurarAsync(Caja c, Func<ConfiguracionSucursalDto, ConfiguracionSucursalDto> cambio)
    {
        var actual = await c.Cliente.ConfiguracionSucursalAsync(c.Datos.SucursalId);
        await c.Cliente.GuardarConfiguracionSucursalAsync(c.Datos.Id, new SolicitudConfiguracionSucursal(c.Dueno.UsuarioId, cambio(actual)));
    }

    /// <summary>Sin turno abierto en la caja: si hay uno, el encargado le hace el corte.</summary>
    private static async Task SinTurnoAsync(Caja c)
    {
        if (await c.Cliente.TurnoAsync(c.Datos.Id) is { } abierto)
            await c.Cliente.CorteZAsync(abierto.Id, new SolicitudCorteZ(c.Encargado.UsuarioId, abierto.FondoCentavos));
    }

    [Fact]
    public async Task La_entrada_dice_el_nombre_de_la_tienda_y_sin_el_el_de_la_sucursal()
    {
        var c = await PrepararAsync();
        try
        {
            await using (var p = new PantallaSinNavegador(c.Servicios))
            {
                await p.AbrirAsync<LadoEntrada>(new() { [nameof(LadoEntrada.Caja)] = c.Datos });
                Assert.Contains($"\n{c.Datos.Sucursal}", await p.TextoAsync());
            }

            await ConfigurarAsync(c, x => x with { NombreTienda = "Papelería La Esquina" });

            await using (var p = new PantallaSinNavegador(c.Servicios))
            {
                await p.AbrirAsync<LadoEntrada>(new() { [nameof(LadoEntrada.Caja)] = c.Datos });
                await p.EsperarAsync(t => t.Contains("\nPapelería La Esquina"), "el nombre de la tienda");
                Assert.DoesNotContain($"\n{c.Datos.Sucursal}", await p.TextoAsync());
                Assert.Empty(p.Errores);
            }
        }
        finally
        {
            await ConfigurarAsync(c, x => x with { NombreTienda = null });
        }
    }

    [Fact]
    public async Task Abrir_turno_propone_el_fondo_sugerido_y_Enter_con_el_campo_vacio_lo_toma()
    {
        var c = await PrepararAsync();
        await SinTurnoAsync(c);
        try
        {
            // Sin fondo sugerido, como antes: se escribe.
            await using (var p = new PantallaSinNavegador(c.Servicios))
            {
                await p.AbrirAsync<AbrirTurno>(new() { [nameof(AbrirTurno.Caja)] = c.Datos, [nameof(AbrirTurno.Sesion)] = c.Cajero });
                Assert.Equal("$0.00", (await p.PorIdAsync("fondo")).Atributos["placeholder"]);
                await p.OprimirAsync(await p.BotonAsync("Abrir turno"));
                Assert.Contains("Escribe el fondo que contaste (puede ser $0).", await p.TextoAsync());
            }

            await ConfigurarAsync(c, x => x with { FondoSugeridoCentavos = 50000 });

            TurnoDto? abierto = null;
            await using (var p = new PantallaSinNavegador(c.Servicios))
            {
                await p.AbrirAsync<AbrirTurno>(new()
                {
                    [nameof(AbrirTurno.Caja)] = c.Datos,
                    [nameof(AbrirTurno.Sesion)] = c.Cajero,
                    [nameof(AbrirTurno.AlAbrir)] = EventCallback.Factory.Create<TurnoDto>(this, t => abierto = t),
                });
                await p.EsperarAsync(t => t.Contains("Vacío, abre con el fondo sugerido: $500.00."), "el fondo sugerido");
                Assert.Equal("$500.00", (await p.PorIdAsync("fondo")).Atributos["placeholder"]);
                // Pasado el Enter de la costumbre después del PIN (el que llega en cuanto aparece no abre nada).
                await Task.Delay(TimeSpan.FromSeconds(1.1));
                await p.TeclaAsync("fondo", "Enter");
                Assert.Empty(p.Errores);
            }

            Assert.Equal(50000, abierto?.FondoCentavos);
            Assert.Equal(50000, (await c.Cliente.TurnoAsync(c.Datos.Id))!.FondoCentavos);
        }
        finally
        {
            await ConfigurarAsync(c, x => x with { FondoSugeridoCentavos = null });
        }
    }

    [Fact]
    public async Task La_venta_ensena_las_fichas_de_espera_que_dice_la_configuracion()
    {
        var c = await PrepararAsync();
        await SinTurnoAsync(c);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        await ConfigurarAsync(c, x => x with { FichasEnEspera = 1 });
        try
        {
            await using var p = new PantallaSinNavegador(c.Servicios);
            var venta = await p.AbrirAsync<PantallaVenta>(new()
            {
                [nameof(PantallaVenta.Caja)] = c.Datos,
                [nameof(PantallaVenta.Sesion)] = c.Cajero,
                [nameof(PantallaVenta.Turno)] = turno,
            });
            await p.EsperarAsync(t => t.Contains("Copia B/N"), "los rápidos");

            // Dos tickets en espera, cada uno con una copia: con 1 ficha a la vista, el otro va en «Más».
            foreach (var _ in Enumerable.Range(0, 2))
            {
                await p.Dispatcher.InvokeAsync(() => venta.Tecla("Alt+1", ""));
                await p.Dispatcher.InvokeAsync(() => venta.Tecla("F6", ""));
            }
            await p.EsperarAsync(t => t.Contains("Más (1)"), "«Más (1)»");

            var (_, elementos) = await p.LeerAsync();
            Assert.Single(elementos, e => e.Etiqueta == "button" && e.Clase == "vn-ficha");
            Assert.Contains(elementos, e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto).StartsWith("Más (1)", StringComparison.Ordinal));
            Assert.Empty(p.Errores);
        }
        finally
        {
            await ConfigurarAsync(c, x => x with { FichasEnEspera = 3 });
        }
    }
}
