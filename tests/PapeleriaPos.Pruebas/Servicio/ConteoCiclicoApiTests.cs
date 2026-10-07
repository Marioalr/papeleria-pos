using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Telegram;

namespace PapeleriaPos.Pruebas.Servicio;

public class ConteoCiclicoApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task El_conteo_del_dia_se_cuenta_a_ciegas_y_la_diferencia_entra_al_inventario()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        using (var alcance = servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = dueno.UsuarioId, ChatId = 7171, VinculadoUtc = DateTime.UtcNow });
            if (await db.ConfiguracionesSucursal.FindAsync(caja.SucursalId) is { } c)
                c.UmbralAjusteCentavos = 1;
            else
                db.ConfiguracionesSucursal.Add(new ConfiguracionSucursal { SucursalId = caja.SucursalId, UmbralAjusteCentavos = 1 });
            await db.SaveChangesAsync();
        }

        var delCajero = await Leer<ConteoCiclicoDto>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/conteo-del-dia?usuarioId={cajero.UsuarioId}"));
        var delEncargado = await Leer<ConteoCiclicoDto>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/conteo-del-dia?usuarioId={encargado.UsuarioId}"));
        var cuadra = delEncargado.Renglones[0];
        var falta = delEncargado.Renglones[1];
        long existencia(Guid variante)
        {
            using var alcance = servicio.Fabrica.Services.CreateScope();
            return alcance.ServiceProvider.GetRequiredService<PosDbContext>().Existencias.AsNoTracking()
                .Where(e => e.VarianteId == variante).Select(e => e.Cantidad.Milesimas).FirstOrDefault();
        }
        var contado = await Leer<ConteoCiclicoDto>(await http.PostAsJsonAsync($"api/conteos/{delCajero.Id}/contar",
            new SolicitudContar(cajero.UsuarioId, cuadra.VarianteId, Math.Max(0, existencia(cuadra.VarianteId)))));
        var conFaltante = await Leer<ConteoCiclicoDto>(await http.PostAsJsonAsync($"api/conteos/{delCajero.Id}/contar",
            new SolicitudContar(cajero.UsuarioId, falta.VarianteId, existencia(falta.VarianteId) - 2000)));
        var otraVez = await http.PostAsJsonAsync($"api/conteos/{delCajero.Id}/contar", new SolicitudContar(cajero.UsuarioId, falta.VarianteId, 0));
        var visto = await Leer<ConteoCiclicoDto>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/conteo-del-dia?usuarioId={encargado.UsuarioId}"));

        Assert.Equal(15, delCajero.Total);
        Assert.Equal(delCajero.Id, delEncargado.Id);
        // Nadie ve lo que dice el sistema antes de contar: se cuenta a ciegas.
        Assert.All(delCajero.Renglones.Concat(delEncargado.Renglones), r => Assert.Null(r.Esperado));
        Assert.Equal(2, conFaltante.Contados);
        Assert.All(conFaltante.Renglones, r => Assert.Null(r.Diferencia));
        Assert.Equal(HttpStatusCode.BadRequest, otraVez.StatusCode);
        var renglonFalta = visto.Renglones.Single(r => r.VarianteId == falta.VarianteId);
        Assert.Equal((-2m, "Cajero de prueba"), (renglonFalta.Diferencia!.Value, renglonFalta.ContadoPor));
        Assert.True(renglonFalta.CostoDiferenciaCentavos < 0);
        Assert.Equal(0m, visto.Renglones.Single(r => r.VarianteId == cuadra.VarianteId).Diferencia);

        using var otro = servicio.Fabrica.Services.CreateScope();
        var base_ = otro.ServiceProvider.GetRequiredService<PosDbContext>();
        var movimientos = await base_.MovimientosInventario.AsNoTracking().Where(m => m.Tipo == TipoMovimiento.Conteo && m.DocumentoId == delCajero.Id).ToListAsync();
        var movimiento = Assert.Single(movimientos);
        Assert.Equal((falta.VarianteId, -2000L), (movimiento.VarianteId, movimiento.Cantidad.Milesimas));
        Assert.Equal(renglonFalta.CostoDiferenciaCentavos, movimiento.CostoCentavos);
        Assert.Empty(await new RegistroInventario(base_).VerificarExistenciasAsync());
        Assert.True(await base_.Bitacora.AnyAsync(b => b.Accion == AccionControlada.ContarInventario && b.Detalle.EndsWith("(faltó 2)")));
        var aviso = Assert.Single(await base_.MensajesTelegram.AsNoTracking().Where(m => m.ChatId == 7171 && m.Tipo == TipoAlerta.DiferenciaConteo).ToListAsync());
        Assert.Contains("faltó 2", aviso.Texto);
    }
}
