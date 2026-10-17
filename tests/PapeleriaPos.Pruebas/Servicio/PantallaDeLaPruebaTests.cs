using System.Net.Http.Json;
using PapeleriaPos.Herramientas.Prueba;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que la pantalla de venta le enseña a quien prueba, con la base de <c>preparar-prueba</c> (la segunda prueba con una
/// persona real, 17 y 18 de octubre de 2026): la fila de rápidos, lo que el cajero hace en Clientes y el estado de arriba
/// con «Simular sin conexión».
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
    /// Con los permisos del cajero, como los pide la pantalla de venta al entrar. La lista y la cuenta del cliente no piden
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
        // Sin los permisos (sin conexión ni copia, o en Administración), como siempre: sin candado, y el servicio decide al
        // intentarlo.
        Assert.True(CreditoDelCliente.PuedeDevolverConPermisos(null));
        Assert.Null(CreditoDelCliente.QuienAutorizaDevolverConPermisos(null));
    }

    [Fact]
    public async Task Al_simular_sin_conexion_el_estado_de_arriba_dice_sin_la_principal_aunque_el_servicio_este_en_esta_pc()
    {
        // Como la arma la caja de Windows de la prueba: su servicio en esta PC y sin alta (la primera caja).
        var http = new HttpClient(prueba.Fabrica.Server.CreateHandler());
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5180/") };
        var contingencia = new Contingencia(new AlmacenLocal(Path.Combine(prueba.Carpeta, $"caja-local-{Guid.NewGuid():N}.db")), http, conexion);
        await contingencia.LatidoAsync();
        var enLinea = EstadoCaja.QueDice(contingencia, conexion);

        // Lo que hace «Simular sin conexión» en la franja de la prueba, y luego «Volver a conectar».
        conexion.SimularSinConexion = true;
        await contingencia.LatidoAsync();
        var simulando = EstadoCaja.QueDice(contingencia, conexion);
        conexion.SimularSinConexion = false;
        await contingencia.LatidoAsync();
        var deVuelta = EstadoCaja.QueDice(contingencia, conexion);

        // Sin simular, si el servicio de esta PC no contesta un momento (la caja principal de verdad, al actualizarlo):
        // «Sin la principal» no aplica ahí. En otra PC, sí.
        contingencia.MarcarSinConexion();
        var sinSuServicio = EstadoCaja.QueDice(contingencia, conexion);
        var secundariaSinLaPrincipal = EstadoCaja.QueDice(contingencia, new ConexionCaja { Servicio = new Uri("http://192.168.1.10:5080/") });

        Assert.Equal(EstadoCaja.ChipConexion.ServicioAqui, enLinea);
        Assert.Equal(EstadoCaja.ChipConexion.SinLaPrincipal, simulando);
        Assert.Equal(EstadoCaja.ChipConexion.ServicioAqui, deVuelta);
        Assert.Equal(EstadoCaja.ChipConexion.ServicioAqui, sinSuServicio);
        Assert.Equal(EstadoCaja.ChipConexion.SinLaPrincipal, secundariaSinLaPrincipal);
        // Sin contingencia (el navegador en la PC del servicio, o el de otra PC), solo dónde está el servicio.
        Assert.Equal(EstadoCaja.ChipConexion.ServicioAqui, EstadoCaja.QueDice(null, conexion));
        Assert.Equal(EstadoCaja.ChipConexion.ConectadaALaPrincipal,
            EstadoCaja.QueDice(null, new ConexionCaja { Servicio = new Uri("http://192.168.1.10:5080/") }));
        // Su base local está en la carpeta de la prueba, que se borra al final: la caja se cierra y la suelta.
        contingencia.Cerrar(TimeSpan.FromSeconds(20));
    }
}
