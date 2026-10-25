using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Quien vino de eleventa sin PIN (el piloto alfa, 2026-10-25) en todo lo que enseña o cuenta usuarios: su ficha sale en la
/// entrada, con y sin la caja principal; al tocarla o al escribir su usuario (el celular, dar de alta una caja) se dice que se lo
/// pida al dueño, sin sumarle intentos; no se le nombra para autorizar; y en «Usuarios y permisos» el dueño ve que no tiene PIN
/// y se lo pone.
/// </summary>
public sealed class EntradaSinPinTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-sin-pin-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta)
    {
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage respuesta)
    {
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    /// <summary>La caja y quien vino de eleventa con ese número, como lo deja la importación (sin PIN, con los permisos por defecto).</summary>
    private async Task<(HttpClient Http, CajaDto Caja, Usuario DeEleventa)> PrepararAsync(int numero, string nombre)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single(c => c.Serie == "A");
        await using var db = servicio.Base();
        if (!await db.Usuarios.AnyAsync(u => u.EleventaId == numero))
        {
            await UsuariosDeEleventa.AplicarAsync(db, [new FilaUsuario(numero, nombre.Split(' ')[0].ToLowerInvariant(), nombre, true, false)], caja.SucursalId);
            await db.SaveChangesAsync();
        }
        return (http, caja, await db.Usuarios.AsNoTracking().SingleAsync(u => u.EleventaId == numero));
    }

    [Fact]
    public async Task Su_ficha_sale_y_al_entrar_dice_que_le_pida_su_pin_al_duenio_sin_sumarle_intentos()
    {
        var (http, caja, maria) = await PrepararAsync(3, "María López");

        var fichas = (await http.GetFromJsonAsync<List<FichaUsuarioDto>>($"api/cajas/{caja.Id}/usuarios"))!;
        Assert.True(fichas.Single(f => f.UsuarioId == maria.Id).SinPin);
        Assert.All(fichas.Where(f => f.Nombre.EndsWith("de prueba")), f => Assert.False(f.SinPin));

        // Con su ficha, con su usuario (el celular) o para dar de alta una caja: lo mismo, y sin que cuenten como intentos.
        for (var i = 0; i < Usuario.IntentosPermitidos + 1; i++)
            Assert.Equal(FichasEntrada.SinPin,
                await ErrorAsync(await http.PostAsJsonAsync($"api/cajas/{caja.Id}/sesion", new SolicitudEntradaFicha(maria.Id, "1234"))));
        Assert.Equal(FichasEntrada.SinPin, await ErrorAsync(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(maria.NombreUsuario, "1234"))));
        Assert.Equal(FichasEntrada.SinPin,
            await ErrorAsync(await http.PostAsJsonAsync("api/alta/opciones", new SolicitudCredenciales(maria.NombreUsuario, "1234"))));
        await using var db = servicio.Base();
        var despues = await db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == maria.Id);
        Assert.Equal((0, null), (despues.IntentosFallidos, despues.BloqueadoHastaUtc));
    }

    [Fact]
    public async Task El_duenio_ve_que_no_tiene_pin_se_lo_pone_en_usuarios_y_permisos_y_ya_entra()
    {
        var (http, caja, rosa) = await PrepararAsync(8, "Rosa Ruiz");
        var dueno = await LeerAsync<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var url = $"api/cajas/{caja.Id}/usuarios-y-permisos";

        var antes = await LeerAsync<UsuariosYPermisosDto>(await http.GetAsync($"{url}?usuarioId={dueno.UsuarioId}"));
        var suyo = antes.Usuarios.Single(u => u.Id == rosa.Id);
        Assert.Equal((true, 8, "Permisos por defecto"), (suyo.SinPin, suyo.EleventaId, suyo.Resumen));
        Assert.All(antes.Usuarios.Where(u => u.EleventaId is null), u => Assert.False(u.SinPin));

        var despues = await LeerAsync<UsuariosYPermisosDto>(await http.PutAsJsonAsync($"{url}/{rosa.Id}/pin", new SolicitudPinUsuario(dueno.UsuarioId, "2468")));

        Assert.False(despues.Usuarios.Single(u => u.Id == rosa.Id).SinPin);
        var sesion = await LeerAsync<SesionDto>(await http.PostAsJsonAsync($"api/cajas/{caja.Id}/sesion", new SolicitudEntradaFicha(rosa.Id, "2468")));
        Assert.Equal(rosa.Id, sesion.UsuarioId);
        await using var db = servicio.Base();
        Assert.True(await db.Bitacora.AnyAsync(b => b.Detalle == "Le puso PIN a Rosa Ruiz"));
    }

    [Fact]
    public async Task Sin_pin_no_se_le_nombra_para_autorizar_ni_para_hacer_el_corte()
    {
        var (http, caja, pablo) = await PrepararAsync(12, "Pablo Soto");
        var dueno = await LeerAsync<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        // El dueño le da todos los permisos antes de ponerle PIN.
        await LeerAsync<UsuariosYPermisosDto>(await http.PostAsJsonAsync($"api/cajas/{caja.Id}/usuarios-y-permisos/{pablo.Id}/todos",
            new SolicitudUsuarios(dueno.UsuarioId)));

        var cajero = await LeerAsync<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var corte = (await http.GetFromJsonAsync<List<QuienAutoriza>>($"api/sucursales/{caja.SucursalId}/quienes-pueden?accion=CorteZ"))!;

        Assert.DoesNotContain(cajero.Permiso(AccionControlada.CancelarTicket)!.Autorizan, a => a.Nombre == "Pablo Soto");
        Assert.DoesNotContain(corte, q => q.Nombre == "Pablo Soto");
        // Ni cuenta como quien da los PIN: el dueño no se puede quitar «Usuarios y permisos» dejándoselo solo a él.
        var quitar = await http.PutAsJsonAsync($"api/cajas/{caja.Id}/usuarios-y-permisos/{dueno.UsuarioId}",
            new SolicitudPermisoUsuario(dueno.UsuarioId, AccionControlada.EditarReglas, NivelPermiso.NoPermitido));
        Assert.Equal(PapeleriaPos.Datos.RegistroUsuarios.SiempreAlguien, await ErrorAsync(quitar));
    }

    [Fact]
    public async Task Sin_la_caja_principal_su_ficha_sale_de_la_copia_y_tampoco_entra()
    {
        var (_, caja, carla) = await PrepararAsync(10, "Carla Díaz");
        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);
        try
        {
            await contingencia.LatidoAsync();
            var enLinea = await cliente.FichasAsync(caja.Id);
            interruptor.Caido = true;
            await contingencia.LatidoAsync();
            Assert.False(contingencia.EnLinea);

            var sinConexion = await cliente.FichasAsync(caja.Id);

            // Las mismas fichas que da el servicio, también la suya sin PIN.
            Assert.Equal(enLinea, sinConexion);
            Assert.True(sinConexion.Single(f => f.UsuarioId == carla.Id).SinPin);
            // Con la copia, lo mismo que dice el servicio, y sin sumar intentos: después de seis, el mensaje es el mismo.
            for (var i = 0; i < Usuario.IntentosPermitidos + 1; i++)
                Assert.Equal(FichasEntrada.SinPin, (await Assert.ThrowsAsync<ErrorCaja>(() => cliente.EntrarAsync(caja.Id, carla.Id, "1234"))).Message);
            Assert.Equal(FichasEntrada.SinPin, (await Assert.ThrowsAsync<ErrorCaja>(() => cliente.EntrarAsync(carla.NombreUsuario, "1234"))).Message);
            // Los demás entran como siempre.
            var cajero = sinConexion.Single(f => f.Nombre == "Cajero de prueba");
            Assert.Equal(cajero.UsuarioId, (await cliente.EntrarAsync(caja.Id, cajero.UsuarioId, "1111")).UsuarioId);
        }
        finally
        {
            contingencia.Cerrar();
        }
    }
}
