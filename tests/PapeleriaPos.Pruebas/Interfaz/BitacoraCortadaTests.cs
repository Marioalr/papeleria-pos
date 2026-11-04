using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Servicio;
using System.Net.Http.Json;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>La bitácora trae hasta cierto número de renglones, los más recientes (auditoría, 2026-11-04): si se cortó, la pantalla lo dice.</summary>
public class BitacoraCortadaTests
{
    private static async Task<PantallaSinNavegador> AbrirAsync(ServicioDePrueba servicio)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))![0];
        var sesion = await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<ConsultaBitacora>(new()
        {
            [nameof(ConsultaBitacora.Caja)] = caja,
            [nameof(ConsultaBitacora.Sesion)] = sesion,
        });
        return pantalla;
    }

    private static async Task RenglonesAsync(ServicioDePrueba servicio, int cuantos)
    {
        await using var db = servicio.Base();
        var sucursal = db.Sucursales.First().Id;
        var ya = db.Bitacora.Count(b => b.SucursalId == sucursal);
        db.Bitacora.AddRange(Enumerable.Range(0, cuantos - ya).Select(i => new Bitacora
        {
            SucursalId = sucursal, Accion = AccionControlada.AdministrarCajas, Detalle = $"Renglón {i}", Resultado = ResultadoBitacora.Negado, FechaUtc = DateTime.UtcNow,
        }));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Si_se_corto_lo_dice_y_si_no_no()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();

        await RenglonesAsync(servicio, BitacoraDto.Tope - 1);
        var pantalla = await AbrirAsync(servicio);
        await using (pantalla)
        {
            await pantalla.EsperarAsync(t => t.Contains($"{BitacoraDto.Tope - 1:N0} registros", StringComparison.Ordinal), "los renglones");
            Assert.DoesNotContain("más recientes", await pantalla.TextoAsync(), StringComparison.Ordinal);
        }

        await RenglonesAsync(servicio, BitacoraDto.Tope + 1);
        pantalla = await AbrirAsync(servicio);
        await using (pantalla)
            await pantalla.EsperarAsync(t => t.Contains($"Son los {BitacoraDto.Tope:N0} más recientes", StringComparison.Ordinal), "el aviso de que se cortó");
    }
}
