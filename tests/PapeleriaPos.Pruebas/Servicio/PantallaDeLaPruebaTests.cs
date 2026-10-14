using System.Net.Http.Json;
using PapeleriaPos.Herramientas.Prueba;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que la pantalla nueva le enseña a quien prueba, con la base de <c>preparar-prueba</c> (la segunda prueba con una
/// persona real, 17 y 18 de octubre de 2026): la fila de rápidos y lo que el cajero hace en Clientes.
/// </summary>
public class PantallaDeLaPruebaTests(ServicioEnPrueba prueba) : IClassFixture<ServicioEnPrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Los_rapidos_cuestan_lo_que_decidio_el_dueno_la_copia_dos_pesos_y_la_curp_diez()
    {
        var http = prueba.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var rapidos = (await http.GetFromJsonAsync<List<Rapido>>($"api/sucursales/{caja.SucursalId}/rapidos"))!;

        var precios = new List<long>();
        foreach (var rapido in rapidos)
        {
            // Como lo dice la fila de la pantalla.
            var producto = (await http.GetFromJsonAsync<ProductoVentaDto>($"api/productos/{rapido.VarianteId}?sucursalId={caja.SucursalId}"))!;
            precios.Add(PantallaVenta.PrecioDelRapido(rapido, producto, caja.SucursalId));
        }

        Assert.Equal(["Copia B/N", "CURP"], rapidos.Select(r => r.Nombre));
        Assert.Equal([200L, 1000L], precios);
        Assert.Equal(PreparacionPrueba.PreciosDeLosRapidos.Select(p => p.PrecioCentavos), precios);
        // La CURP son 10 impresiones a $1.00.
        Assert.Equal(10m, rapidos[1].Cantidad);
    }

    /// <summary>
    /// Con los permisos del cajero, como los pide la pantalla nueva al entrar. La lista y la cuenta del cliente no piden
    /// usuario (cualquier caja las lee): lo que cambia para el cajero es lo que la pantalla le enseña.
    /// </summary>
    [Fact]
    public async Task Con_los_permisos_del_cajero_los_clientes_solo_se_consultan_y_devolver_el_saldo_a_favor_lo_autoriza_el_encargado()
    {
        var http = prueba.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var permisos = (await http.GetFromJsonAsync<List<PermisoDeSesion>>($"api/usuarios/{cajero.UsuarioId}/permisos?sucursalId={caja.SucursalId}"))!
            .ToDictionary(p => p.Accion);

        var clientes = await Leer<List<ClienteResumenDto>>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/clientes?inactivos=false"));
        var esquina = clientes.Single(c => c.Cliente.Nombre == PreparacionPrueba.ConSaldoAFavor);
        var cuenta = await Leer<CuentaClienteDto>(await http.GetAsync($"api/clientes/{esquina.Cliente.Id}/credito"));

        // La pestaña Clientes es de todos; dar de alta y editar, no: sin «Nuevo cliente» y la ficha solo se lee.
        Assert.Equal(NivelPermiso.NoPermitido, permisos[AccionControlada.AdministrarClientes].Nivel);
        Assert.Equal(PreparacionPrueba.ClientesDePrueba.Length, clientes.Count);
        // Con sus condiciones, como en la base que arma preparar-prueba: «Crédito hasta $1,000.00» en la cuenta de quien debe.
        Assert.Equal(PreparacionPrueba.ClientesDePrueba.Single(c => c.Nombre == PreparacionPrueba.ConDeuda).Credito,
            clientes.Single(c => c.Cliente.Nombre == PreparacionPrueba.ConDeuda).Cliente.LimiteCreditoCentavos);
        // En su cuenta, «Devolver saldo a favor · 🔒 Encargado de prueba o Dueño de prueba».
        Assert.Equal(PreparacionPrueba.SaldoAFavorCentavos, cuenta.Credito.DevolvibleCentavos);
        Assert.Equal(NivelPermiso.RequiereAutorizacion, permisos[AccionControlada.DevolverSaldoAFavor].Nivel);
        Assert.True(CreditoDelCliente.PuedeDevolverConPermisos(permisos));
        Assert.Equal("Encargado de prueba o Dueño de prueba", CreditoDelCliente.QuienAutorizaDevolverConPermisos(permisos));
        // En la pantalla de hoy (sin permisos), como siempre: sin candado, y el servicio decide al intentarlo.
        Assert.True(CreditoDelCliente.PuedeDevolverConPermisos(null));
        Assert.Null(CreditoDelCliente.QuienAutorizaDevolverConPermisos(null));
    }
}
