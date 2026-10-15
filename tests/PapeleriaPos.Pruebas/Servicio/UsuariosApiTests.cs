using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Nucleo;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// «Usuarios y permisos» en el servicio (entrega 1): la pantalla la usa quien entró con ese permiso, sin volver a escribir su
/// PIN, y lo que cambia se ve al momento en las fichas, al entrar y en lo que decide el servicio.
/// </summary>
public class UsuariosApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Contexto(HttpClient Http, CajaDto Caja, SesionDto Dueno, SesionDto Cajero);

    private async Task<Contexto> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single(c => c.Serie == "A");
        return new Contexto(http, caja, await EntrarAsync(http, "dueno", "3333"), await EntrarAsync(http, "cajero", "1111"));
    }

    private static async Task<SesionDto> EntrarAsync(HttpClient http, string usuario, string pin) =>
        (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin))).Content.ReadFromJsonAsync<SesionDto>())!;

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta)
    {
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage respuesta, HttpStatusCode codigo = HttpStatusCode.BadRequest)
    {
        Assert.Equal(codigo, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    private static string Url(Contexto c, string resto = "") => $"api/cajas/{c.Caja.Id}/usuarios-y-permisos{resto}";

    [Fact]
    public async Task Quien_no_tiene_el_permiso_no_ve_la_pantalla_y_se_le_dice_quien_si()
    {
        var c = await PrepararAsync();

        var delCajero = await c.Http.GetAsync(Url(c, $"?usuarioId={c.Cajero.UsuarioId}"));
        var delDueno = await LeerAsync<UsuariosYPermisosDto>(await c.Http.GetAsync(Url(c, $"?usuarioId={c.Dueno.UsuarioId}")));

        Assert.Equal("Esto lo hace quien tiene el permiso «Usuarios y permisos»: Dueño de prueba.", await ErrorAsync(delCajero, HttpStatusCode.Forbidden));
        Assert.Contains(delDueno.Usuarios, u => u.Nombre == "Dueño de prueba" && u.PrimeraCuenta && u.Resumen == "Todos los permisos");
        Assert.Contains(delDueno.Usuarios, u => u.Nombre == "Encargado de prueba" && u.Resumen == "Corte de turno, productos y clientes");
        // Al entrar, la sesión ya trae lo que puede y su resumen (el menú del usuario).
        Assert.Equal("Todos los permisos", c.Dueno.Resumen);
        Assert.Equal("Permisos por defecto", c.Cajero.Resumen);
        Assert.True(c.Dueno.Puede(AccionControlada.EditarReglas));
        Assert.False(c.Cajero.Ve(AccionControlada.EditarReglas));
        Assert.Equal(["Encargado de prueba", "Dueño de prueba"], c.Cajero.Permiso(AccionControlada.CancelarTicket)!.Autorizan.Select(a => a.Nombre));
    }

    [Fact]
    public async Task Un_usuario_nuevo_entra_con_su_ficha_con_los_de_defecto_y_lo_que_se_le_cambia_vale_al_momento()
    {
        var c = await PrepararAsync();
        var antes = await c.Http.GetFromJsonAsync<List<FichaUsuarioDto>>($"api/cajas/{c.Caja.Id}/usuarios");

        var lista = await LeerAsync<UsuariosYPermisosDto>(await c.Http.PostAsJsonAsync(Url(c, "/usuarios"),
            new SolicitudNuevoUsuario(c.Dueno.UsuarioId, "Ximena Ruiz", "4321")));
        var ximena = lista.Usuarios.Single(u => u.Id == lista.Elegido);

        var fichas = (await c.Http.GetFromJsonAsync<List<FichaUsuarioDto>>($"api/cajas/{c.Caja.Id}/usuarios"))!;
        var suFicha = fichas.Single(f => f.UsuarioId == ximena.Id);
        Assert.Equal(("Ximena Ruiz", "Permisos por defecto", false), (suFicha.Nombre, suFicha.Resumen, suFicha.HaceCorte));
        Assert.All(antes!, f => Assert.Equal(f.Tecla, fichas.Single(x => x.UsuarioId == f.UsuarioId).Tecla));
        var sesion = await LeerAsync<SesionDto>(await c.Http.PostAsJsonAsync($"api/cajas/{c.Caja.Id}/sesion", new SolicitudEntradaFicha(ximena.Id, "4321")));
        Assert.Equal(("Permisos por defecto", false), (sesion.Resumen, sesion.Puede(AccionControlada.CorteZ)));

        // El corte del turno, al momento: su ficha y su sesión lo dicen, y el servicio lo deja hacer.
        await LeerAsync<UsuariosYPermisosDto>(await c.Http.PutAsJsonAsync(Url(c, $"/{ximena.Id}"),
            new SolicitudPermisoUsuario(c.Dueno.UsuarioId, AccionControlada.CorteZ, NivelPermiso.Permitido)));
        Assert.True((await c.Http.GetFromJsonAsync<List<FichaUsuarioDto>>($"api/cajas/{c.Caja.Id}/usuarios"))!.Single(f => f.UsuarioId == ximena.Id).HaceCorte);
        var conCorte = await LeerAsync<SesionDto>(await c.Http.PostAsJsonAsync($"api/cajas/{c.Caja.Id}/sesion", new SolicitudEntradaFicha(ximena.Id, "4321")));
        Assert.Equal(("Corte de turno", true), (conCorte.Resumen, conCorte.Puede(AccionControlada.CorteZ)));
        var decision = await LeerAsync<DecisionDto>(await c.Http.PostAsJsonAsync("api/permisos",
            new SolicitudPermiso(ximena.Id, c.Caja.SucursalId, AccionControlada.CorteZ)));
        Assert.Equal(NivelPermiso.Permitido, decision.Nivel);

        // El PIN nuevo entra; el de antes, no.
        await LeerAsync<UsuariosYPermisosDto>(await c.Http.PutAsJsonAsync(Url(c, $"/{ximena.Id}/pin"), new SolicitudPinUsuario(c.Dueno.UsuarioId, "8765")));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Http.PostAsJsonAsync($"api/cajas/{c.Caja.Id}/sesion", new SolicitudEntradaFicha(ximena.Id, "4321"))).StatusCode);
        await LeerAsync<SesionDto>(await c.Http.PostAsJsonAsync($"api/cajas/{c.Caja.Id}/sesion", new SolicitudEntradaFicha(ximena.Id, "8765")));

        // De baja: su ficha se va (sin moverle la letra a nadie) y ya no entra.
        await LeerAsync<UsuariosYPermisosDto>(await c.Http.PutAsJsonAsync(Url(c, $"/{ximena.Id}/activo"), new SolicitudActivarUsuario(c.Dueno.UsuarioId, false)));
        var sinElla = (await c.Http.GetFromJsonAsync<List<FichaUsuarioDto>>($"api/cajas/{c.Caja.Id}/usuarios"))!;
        Assert.DoesNotContain(sinElla, f => f.UsuarioId == ximena.Id);
        Assert.All(sinElla, f => Assert.Equal(fichas.Single(x => x.UsuarioId == f.UsuarioId).Tecla, f.Tecla));
        Assert.Equal("Ese usuario no entra en esta caja.",
            await ErrorAsync(await c.Http.PostAsJsonAsync($"api/cajas/{c.Caja.Id}/sesion", new SolicitudEntradaFicha(ximena.Id, "8765"))));

        // Todo quedó en la bitácora, a nombre del dueño, en la sucursal de la caja.
        await using var db = servicio.Base();
        var bitacora = await db.Bitacora.Where(b => b.Accion == AccionControlada.EditarReglas && b.UsuarioId == c.Dueno.UsuarioId && b.CajaId == c.Caja.Id)
            .Select(b => b.Detalle).ToListAsync();
        Assert.Contains("Dio de alta a Ximena Ruiz (usuario «ximena»), con los permisos por defecto", bitacora);
        Assert.Contains("Ximena Ruiz: «Hacer el corte de un turno» → Puede", bitacora);
        Assert.Contains("Cambió el PIN de Ximena Ruiz", bitacora);
        Assert.Contains("Dio de baja a Ximena Ruiz", bitacora);
    }

    [Fact]
    public async Task Los_de_defecto_se_cambian_ahi_mismo_y_siempre_queda_alguien_con_usuarios_y_permisos()
    {
        var c = await PrepararAsync();

        var quitarselo = await c.Http.PutAsJsonAsync(Url(c, $"/{c.Dueno.UsuarioId}"),
            new SolicitudPermisoUsuario(c.Dueno.UsuarioId, AccionControlada.EditarReglas, NivelPermiso.NoPermitido));
        var losDeDefecto = await c.Http.PostAsJsonAsync(Url(c, $"/{c.Dueno.UsuarioId}/defecto"), new SolicitudUsuarios(c.Dueno.UsuarioId));
        var defecto = await LeerAsync<UsuariosYPermisosDto>(await c.Http.PutAsJsonAsync(Url(c, "/defecto"),
            new SolicitudPermisoUsuario(c.Dueno.UsuarioId, AccionControlada.AltaRapida, NivelPermiso.RequiereAutorizacion)));
        var soloDefecto = await LeerAsync<List<Permiso>>(await c.Http.GetAsync(Url(c, $"/defecto?usuarioId={c.Dueno.UsuarioId}")));

        Assert.Equal(RegistroUsuarios.SiempreAlguien, await ErrorAsync(quitarselo));
        Assert.Equal(RegistroUsuarios.SiempreAlguien, await ErrorAsync(losDeDefecto));
        Assert.Equal(NivelPermiso.RequiereAutorizacion, defecto.Defecto.Single(p => p.Accion == AccionControlada.AltaRapida).Nivel);
        Assert.Equal(defecto.Defecto, soloDefecto);
        // El cajero de prueba ya no tiene los de defecto: los suyos no cambian.
        Assert.Equal("Permisos propios", defecto.Usuarios.Single(u => u.Usuario == "cajero").Resumen);
        Assert.Equal(NivelPermiso.Permitido, defecto.Usuarios.Single(u => u.Usuario == "cajero").Permisos.Single(p => p.Accion == AccionControlada.AltaRapida).Nivel);

        await LeerAsync<UsuariosYPermisosDto>(await c.Http.PutAsJsonAsync(Url(c, "/defecto"),
            new SolicitudPermisoUsuario(c.Dueno.UsuarioId, AccionControlada.AltaRapida, NivelPermiso.Permitido)));
    }

    [Fact]
    public async Task En_administracion_no_se_puede_poner_con_autorizacion()
    {
        var c = await PrepararAsync();
        var encargado = (await LeerAsync<UsuariosYPermisosDto>(await c.Http.GetAsync(Url(c, $"?usuarioId={c.Dueno.UsuarioId}"))))
            .Usuarios.Single(u => u.Nombre == "Encargado de prueba");

        var enAdministracion = await c.Http.PutAsJsonAsync(Url(c, $"/{encargado.Id}"),
            new SolicitudPermisoUsuario(c.Dueno.UsuarioId, AccionControlada.ConfigurarTicket, NivelPermiso.RequiereAutorizacion));
        var enLosDeDefecto = await c.Http.PutAsJsonAsync(Url(c, "/defecto"),
            new SolicitudPermisoUsuario(c.Dueno.UsuarioId, AccionControlada.AdministrarCajas, NivelPermiso.RequiereAutorizacion));

        const string mensaje = "En Administración, cada permiso es «Puede» o «No»: quien no puede le pide a quien sí que entre a la caja.";
        Assert.Equal(mensaje, await ErrorAsync(enAdministracion));
        Assert.Equal(mensaje, await ErrorAsync(enLosDeDefecto));
        var despues = await LeerAsync<List<Permiso>>(await c.Http.GetAsync(Url(c, $"/{encargado.Id}?usuarioId={c.Dueno.UsuarioId}")));
        Assert.Equal(NivelPermiso.Permitido, despues.Single(p => p.Accion == AccionControlada.ConfigurarTicket).Nivel);
    }
}

/// <summary>
/// La paridad de la migración en el servicio de verdad, con la base de prueba (la de preparar-caja): para el cajero, el
/// encargado y el dueño de prueba, lo que decide cada operación, lo que trae la sesión, las fichas y quién hace el corte son
/// lo mismo que con los roles de antes (<see cref="PermisosDeAntes"/>). Con su propio servicio: nadie cambia permisos aquí.
/// </summary>
public class ParidadPermisosApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static readonly long?[] Montos = [null, 0, 1000, 1001, 20000, 20001, 99_999_999];

    [Fact]
    public async Task Con_los_usuarios_de_prueba_el_servicio_decide_igual_que_con_los_roles()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single(c => c.Serie == "A");
        await using var db = servicio.Base();
        var usuarios = await db.Usuarios.AsNoTracking().Where(u => u.Activo && (u.SucursalId == null || u.SucursalId == caja.SucursalId))
            .Select(u => new { u.Id, u.NombreUsuario, u.Nombre, u.Rol }).ToListAsync();
        var activos = usuarios.Select(u => (u.Id, u.NombreUsuario, u.Nombre, u.Rol)).ToList();
        var reglas = await db.ReglasPermiso.AsNoTracking().ToListAsync();
        Assert.Equal(["cajero", "dueno", "encargado"], usuarios.Select(u => u.NombreUsuario).Order());

        foreach (var (usuario, pin) in new[] { ("cajero", "1111"), ("encargado", "2222"), ("dueno", "3333") })
        {
            var u = usuarios.Single(x => x.NombreUsuario == usuario);
            var antes = PermisosDeAntes.DeLaSesion(reglas, activos, u.Id, u.Rol, caja.SucursalId);
            var sesion = (await (await http.PostAsJsonAsync($"api/cajas/{caja.Id}/sesion", new SolicitudEntradaFicha(u.Id, pin)))
                .Content.ReadFromJsonAsync<SesionDto>())!;
            var aparte = (await http.GetFromJsonAsync<List<PermisoDeSesion>>($"api/usuarios/{u.Id}/permisos?sucursalId={caja.SucursalId}"))!;
            Assert.Equal(antes, sesion.Permisos!);
            Assert.Equal(antes, aparte);
            foreach (var accion in Permisos.Acciones)
            foreach (var monto in Montos)
            {
                var decision = (await (await http.PostAsJsonAsync("api/permisos", new SolicitudPermiso(u.Id, caja.SucursalId, accion, monto)))
                    .Content.ReadFromJsonAsync<DecisionDto>())!;
                var deAntes = PermisosDeAntes.Evaluar(reglas, u.Rol, caja.SucursalId, accion, monto);
                Assert.True((deAntes.Nivel, deAntes.PideMotivo) == (decision.Nivel, decision.PideMotivo),
                    $"{usuario}, {accion}, {monto}: antes {deAntes}, ahora {decision}");
            }
        }

        // Con el turno del cajero abierto: las fichas y quién hace el corte.
        var cajero = usuarios.Single(u => u.NombreUsuario == "cajero");
        var turno = (await (await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.Id, 50000))).Content.ReadFromJsonAsync<TurnoDto>())!;
        var fichas = (await http.GetFromJsonAsync<List<FichaUsuarioDto>>($"api/cajas/{caja.Id}/usuarios"))!;
        Assert.Equal(PermisosDeAntes.Fichas(usuarios.Select(u => (u.Id, u.Nombre, u.Rol)), reglas, caja.SucursalId, cajero.Id), fichas);
        var corte = (await http.GetFromJsonAsync<CorteInformativoDto>($"api/turnos/{turno.Id}/corte-informativo?usuarioId={cajero.Id}"))!;
        Assert.Equal(PermisosDeAntes.QuienesPueden(reglas, activos, caja.SucursalId, AccionControlada.CorteZ), corte.QuienesCortan);
        Assert.False(corte.PuedeCortar);
    }
}

/// <summary>
/// Dos cambios al mismo tiempo desde dos cajas: el segundo espera a que se guarde el primero y ya lo ve. Así dos que se
/// quitan «Usuarios y permisos» uno al otro no dejan a la tienda sin nadie que lo tenga.
/// </summary>
public class UsuariosAlMismoTiempoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    [Fact]
    public async Task Dos_que_se_quitan_usuarios_y_permisos_al_mismo_tiempo_no_dejan_a_nadie_sin_el()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single(c => c.Serie == "A");
        async Task<SesionDto> EntrarAsync(string usuario, string pin) =>
            (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin))).Content.ReadFromJsonAsync<SesionDto>())!;
        var dueno = await EntrarAsync("dueno", "3333");
        var encargado = await EntrarAsync("encargado", "2222");
        string Url(SesionDto de) => $"api/cajas/{caja.Id}/usuarios-y-permisos/{de.UsuarioId}";
        (await http.PutAsJsonAsync(Url(encargado), new SolicitudPermisoUsuario(dueno.UsuarioId, AccionControlada.EditarReglas, NivelPermiso.Permitido)))
            .EnsureSuccessStatusCode();

        // El dueño le quita «Usuarios y permisos» al encargado: ya revisó que queda él y está guardándolo.
        await using var primero = servicio.Base();
        await using (var turno = await primero.Database.BeginTransactionAsync())
        {
            await primero.PermisosUsuario.Where(p => p.UsuarioId == encargado.UsuarioId && p.Accion == AccionControlada.EditarReglas)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.Nivel, NivelPermiso.NoPermitido));

            // Al mismo tiempo, desde otra caja, el encargado se lo quita al dueño: espera su turno.
            var segundo = http.PutAsJsonAsync(Url(dueno), new SolicitudPermisoUsuario(encargado.UsuarioId, AccionControlada.EditarReglas, NivelPermiso.NoPermitido));
            await Task.Delay(500);
            Assert.False(segundo.IsCompleted);
            await turno.CommitAsync();

            // No se puede: ya no queda nadie más con él (o, si el segundo llegó tarde, el encargado ya no lo tiene).
            var respuesta = await segundo;
            Assert.False(respuesta.IsSuccessStatusCode);
            Assert.Contains((await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje,
                new[] { RegistroUsuarios.SiempreAlguien, "Esto lo hace quien tiene el permiso «Usuarios y permisos»: Dueño de prueba." });
        }

        await using var db = servicio.Base();
        Assert.Equal(NivelPermiso.Permitido, (await new ServicioControl(db).EvaluarAsync(dueno.UsuarioId, caja.SucursalId, AccionControlada.EditarReglas)).Nivel);
    }
}
