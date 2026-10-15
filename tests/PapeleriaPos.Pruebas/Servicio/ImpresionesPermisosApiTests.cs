using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Las impresiones con los permisos por usuario (entrega 1): el costo lo ve quien tiene «Ver costos, utilidad y margen» y el
/// desglose del día quien tiene «Consultar la bitácora». Con los usuarios de prueba, como antes (el costo, el dueño; el
/// desglose, el dueño y el encargado); al cambiarlos en «Usuarios y permisos», al momento.
/// </summary>
public class ImpresionesPermisosApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static TrabajoMedidoDto Trabajo(CajaDto caja, string documento) =>
        new(Guid.NewGuid(), caja.Id, null, "PC-PRUEBA", "ControlPrint Lab (WF-6590)", 7, documento, "cajero", DateTime.UtcNow, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            [new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)], true, null, null);

    [Fact]
    public async Task El_costo_y_el_desglose_siguen_a_los_permisos_de_cada_quien()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var trabajo = Trabajo(caja, "Constancia.pdf");
        await Leer<object>(await http.PostAsJsonAsync("api/impresiones", trabajo));
        async Task<ImpresionPorCobrarDto> PorCobrarAsync(SesionDto quien) =>
            (await http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>($"api/sucursales/{caja.SucursalId}/impresiones/por-cobrar?usuarioId={quien.UsuarioId}"))!
                .Single(t => t.Id == trabajo.Id);
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
        Task<HttpResponseMessage> DelDiaAsync(SesionDto quien) =>
            http.GetAsync($"api/sucursales/{caja.SucursalId}/impresiones?fecha={hoy:yyyy-MM-dd}&usuarioId={quien.UsuarioId}");
        async Task Cambiar(SesionDto de, AccionControlada accion, NivelPermiso nivel) =>
            await Leer<UsuariosYPermisosDto>(await http.PutAsJsonAsync($"api/cajas/{caja.Id}/usuarios-y-permisos/{de.UsuarioId}",
                new SolicitudPermisoUsuario(dueno.UsuarioId, accion, nivel)));

        // Como con los roles.
        Assert.Null((await PorCobrarAsync(encargado)).Costo);
        Assert.NotNull((await PorCobrarAsync(dueno)).Costo);
        var delCajero = await DelDiaAsync(cajero);
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal("El desglose de las impresiones lo ve quien tiene el permiso «Consultar la bitácora»: Encargado de prueba o Dueño de prueba.",
            (await delCajero.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.All(await Leer<List<ImpresionDelDiaDto>>(await DelDiaAsync(encargado)), t => Assert.Null(t.Costo));

        // El encargado ya ve costos, utilidad y margen: el costo le llega también en el desglose.
        await Cambiar(encargado, AccionControlada.VerUtilidad, NivelPermiso.Permitido);
        Assert.NotNull((await PorCobrarAsync(encargado)).Costo);
        Assert.Contains(await Leer<List<ImpresionDelDiaDto>>(await DelDiaAsync(encargado)), t => t.Id == trabajo.Id && t.Costo is not null);

        // Y ya no consulta la bitácora: el desglose, tampoco.
        await Cambiar(encargado, AccionControlada.ConsultarBitacora, NivelPermiso.NoPermitido);
        Assert.Equal(HttpStatusCode.Forbidden, (await DelDiaAsync(encargado)).StatusCode);
    }
}
