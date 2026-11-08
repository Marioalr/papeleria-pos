using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Impresiones.Pruebas.ControlPrint;
using PapeleriaPos.Nucleo.Contratos;
using static PapeleriaPos.Impresiones.Pruebas.ControlPrint.PorCobrarDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Auditoría (2026-11-04), arreglado con la revisión de #155: en la caja 2 los costos vienen de Control Print, pero «Aplicar la corrección»
/// de la 1 sí pasaba: se guardaba en los costos de la sucursal, no llegaba a lo impreso (que sale con los de Control Print), cambiaba el
/// precio de las copias y de lo cotizado, y se perdía sola cuando Control Print cambiaba sus costos. Ahora se rechaza, como los costos.
/// </summary>
public sealed class CalibracionConControlPrintTests : IAsyncLifetime
{
    private ServidorControlPrintFalso _controlPrint = null!;
    private ServicioDePrueba _servicio = null!;

    public async Task InitializeAsync()
    {
        _controlPrint = await ServidorControlPrintFalso.IniciarAsync(Codigo);
        _controlPrint.CambiarCostos(JsonSerializer.Serialize(new CostSettings { MinimumPrice = 0, RoundTo = 0, Scaling = ScalingMode.None, MarginPercent = 100 }, CostSettings.Json));
        _servicio = await ServicioDePrueba.CrearAsync(ControlPrintEnLaCajaTests.Ajustes(_controlPrint));
    }

    public async Task DisposeAsync()
    {
        await _servicio.DisposeAsync();
        await _controlPrint.DisposeAsync();
    }

    [Fact]
    public async Task Con_Control_Print_la_calibracion_de_la_1_no_se_aplica_aqui()
    {
        var http = _servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var db = _servicio.Base();
            if (await db.Set<ConfiguracionImpresion>().AnyAsync(c => c.SucursalId == caja.SucursalId))
                break;
            Assert.True(DateTime.UtcNow < limite, "los costos de Control Print no llegaron");
            await Task.Delay(50);
        }

        foreach (var ruta in new[] { $"api/sucursales/{caja.SucursalId}/impresora/calibracion", $"api/sucursales/{caja.SucursalId}/impresoras/1/calibracion" })
        {
            var aplicar = await http.PostAsJsonAsync(ruta, new SolicitudCalibracion(dueno.UsuarioId));
            Assert.Equal(HttpStatusCode.BadRequest, aplicar.StatusCode);
            Assert.Contains("la calibración de esta impresora se aplica allá", await aplicar.Content.ReadAsStringAsync());
        }
    }
}
