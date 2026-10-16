using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La Configuración nueva (entrega 7) en el servicio de verdad: la de la tienda y los rápidos se cambian con la sesión de
/// quien entró (sin volver a escribir su PIN), y la leen las cajas, también en su copia para trabajar sin conexión.
/// </summary>
public class ConfiguracionApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Dueno, SesionDto Encargado, SesionDto Cajero)> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> EntrarAsync(string usuario, string pin) =>
            await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return (http, caja, await EntrarAsync("dueno", "3333"), await EntrarAsync("encargado", "2222"), await EntrarAsync("cajero", "1111"));
    }

    [Fact]
    public async Task La_de_la_tienda_se_cambia_con_la_sesion_y_la_leen_las_cajas_tambien_sin_conexion()
    {
        var (http, caja, dueno, encargado, cajero) = await PrepararAsync();
        var url = $"api/sucursales/{caja.SucursalId}/configuracion";
        var antes = (await http.GetFromJsonAsync<ConfiguracionSucursalDto>(url))!;
        var nueva = antes with { NombreTienda = "Papelería La Prueba", FichasEnEspera = 4, FondoSugeridoCentavos = 50000 };
        // Las pruebas de esta clase comparten el servicio (y su bitácora): se cuentan los negados de esta.
        var negadosAntes = await NegadosAsync();

        var porCajero = await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion", new SolicitudConfiguracionSucursal(cajero.UsuarioId, nueva));
        var porEncargado = await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion", new SolicitudConfiguracionSucursal(encargado.UsuarioId, nueva));
        var guardada = await Leer<ConfiguracionSucursalDto>(await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion",
            new SolicitudConfiguracionSucursal(dueno.UsuarioId, nueva)));
        var invalida = await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion",
            new SolicitudConfiguracionSucursal(dueno.UsuarioId, nueva with { FichasEnEspera = 9 }));

        // Las de siempre antes de cambiarla: 3 fichas y sin fondo sugerido.
        Assert.Equal((3, (long?)null, (string?)null), (antes.FichasEnEspera, antes.FondoSugeridoCentavos, antes.NombreTienda));
        Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, porEncargado.StatusCode);
        var error = (await porCajero.Content.ReadFromJsonAsync<ErrorDto>())!;
        Assert.Equal(("Esto lo hace quien tiene el permiso «Configuración de la tienda»: Dueño de prueba.", "no-permitido"), (error.Mensaje, error.Codigo));
        Assert.Equal(HttpStatusCode.BadRequest, invalida.StatusCode);
        Assert.Equal("Se ven de 0 a 6 fichas; los demás tickets en espera van en «Más».", (await invalida.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal(nueva, guardada);
        Assert.Equal(nueva, await http.GetFromJsonAsync<ConfiguracionSucursalDto>(url));
        // La copia de la caja la trae: sin conexión, el nombre, las fichas y el fondo siguen igual.
        var paquete = (await http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete"))!;
        Assert.Equal(nueva, paquete.Configuracion);
        Assert.Equal(nueva.LimiteEfectivoCentavos, paquete.LimiteEfectivoCentavos);
        // En la bitácora, a nombre del dueño y de esta caja.
        await using var db = servicio.Base();
        var bitacora = await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.ConfigurarTienda).ToListAsync();
        Assert.Contains(bitacora, b => b.UsuarioId == dueno.UsuarioId && b.CajaId == caja.Id && b.Resultado == ResultadoBitacora.Hecho
            && b.Detalle == "Configuración de la tienda: Nombre de la tienda «Papelería La Prueba»; Tickets en espera a la vista 4; "
            + "Fondo sugerido al abrir turno $500.00");
        Assert.Equal(negadosAntes + 2, await NegadosAsync());
    }

    private async Task<int> NegadosAsync()
    {
        await using var db = servicio.Base();
        return await db.Bitacora.CountAsync(b => b.Accion == AccionControlada.ConfigurarTienda && b.Resultado == ResultadoBitacora.Negado);
    }

    [Fact]
    public async Task Los_rapidos_se_cambian_con_la_sesion_y_la_venta_los_lee()
    {
        var (http, caja, dueno, _, cajero) = await PrepararAsync();
        var url = $"api/sucursales/{caja.SucursalId}/rapidos";
        var antes = (await http.GetFromJsonAsync<List<Rapido>>(url))!;
        List<Rapido> alReves = [antes[1] with { Nombre = "CURP (10)" }, antes[0]];

        var porCajero = await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion/rapidos", new SolicitudRapidos(cajero.UsuarioId, alReves));
        var demasiados = await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion/rapidos",
            new SolicitudRapidos(dueno.UsuarioId, [.. Enumerable.Range(1, 7).Select(i => antes[0] with { Nombre = $"Copia {i}" })]));
        var guardados = await Leer<List<Rapido>>(await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion/rapidos",
            new SolicitudRapidos(dueno.UsuarioId, alReves)));

        Assert.Equal(["Copia B/N", "CURP"], antes.Select(r => r.Nombre));
        Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, demasiados.StatusCode);
        Assert.Equal("Van hasta 6 rápidos: Alt+1 a Alt+6.", (await demasiados.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal(alReves, guardados);
        Assert.Equal(alReves, await http.GetFromJsonAsync<List<Rapido>>(url));
    }

    [Fact]
    public async Task Cuanto_cubre_el_sugerido_se_lee_sin_calcularlo_y_lo_cambia_quien_hace_los_pedidos()
    {
        var (http, caja, _, encargado, cajero) = await PrepararAsync();
        var url = $"api/sucursales/{caja.SucursalId}/sugerido/parametros";

        var antes = (await http.GetFromJsonAsync<ParametrosSugeridoDto>(url))!;
        var porCajero = await http.PutAsJsonAsync(url, new SolicitudParametrosSugerido(cajero.UsuarioId, new ParametrosSugeridoDto(30, 10)));
        var guardados = await Leer<ParametrosSugeridoDto>(await http.PutAsJsonAsync(url,
            new SolicitudParametrosSugerido(encargado.UsuarioId, new ParametrosSugeridoDto(30, 10))));

        Assert.Equal(new ParametrosSugeridoDto(14, 7), antes);
        Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
        Assert.Equal(new ParametrosSugeridoDto(30, 10), guardados);
        Assert.Equal(guardados, await http.GetFromJsonAsync<ParametrosSugeridoDto>(url));
    }
}
