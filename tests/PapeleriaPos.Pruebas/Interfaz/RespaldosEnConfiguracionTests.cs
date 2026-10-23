using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Configuración › Respaldos (2026-10-22), con el servicio de verdad y su propia carpeta de respaldos: el último, dónde se
/// guardan y «Respaldar ahora», que dice dónde quedó y cuánto mide. Sin la copia diaria de fondo, así lo que se ve no depende
/// de cuándo corre esa. Quién ve el grupo lo revisa ConfiguracionTests.
/// </summary>
public sealed class RespaldosEnConfiguracionTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"respaldos-cfg-{Guid.NewGuid():N}");

    public void Dispose() => ArchivosDePrueba.BorrarCarpeta(_carpeta);

    [Fact]
    public async Task El_dueno_respalda_ahora_y_ve_donde_quedo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(
            s => s.Remove(s.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RespaldoDiario))),
            ("Respaldos:Activo", "true"), ("Respaldos:Carpeta", _carpeta));
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .BuildServiceProvider();
        await using var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<Configuracion>(new()
        {
            [nameof(Configuracion.Caja)] = caja,
            [nameof(Configuracion.Sesion)] = dueno,
            [nameof(Configuracion.AlCambiar)] = new EventCallback(null, (Action)(() => { })),
            [nameof(Configuracion.AlNoGuardar)] = new EventCallback<string>(null, (Action<string>)(_ => { })),
        });
        await pantalla.EsperarAsync(t => !t.Contains("Cargando…"), "lo guardado");

        await pantalla.OprimirAsync(await pantalla.ElementoAsync(
            e => e.Atributos.GetValueOrDefault("role") as string == "tab" && PantallaSinNavegador.Limpio(e.Texto) == "Respaldos", "el grupo «Respaldos»"));
        var antes = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        Assert.Contains("Último respaldo", antes);
        Assert.Contains("Todavía no hay ninguno", antes);
        Assert.Contains($"{_carpeta} · los de los últimos 30 días", antes);
        Assert.DoesNotContain("Restaurar un respaldo", antes);

        await pantalla.OprimirAsync(await pantalla.PorIdAsync("aj-respaldar"));
        await pantalla.EsperarAsync(t => t.Contains("Se guardó en"), "el respaldo");

        var archivo = Assert.Single(Directory.GetFiles(_carpeta, "pos-*-ahora-*.db"));
        var despues = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        Assert.Contains($"Se guardó en {archivo} (", despues);
        Assert.Contains(" a las ", despues);
        Assert.DoesNotContain("Todavía no hay ninguno", despues);
        Assert.Empty(pantalla.Errores);
    }
}
