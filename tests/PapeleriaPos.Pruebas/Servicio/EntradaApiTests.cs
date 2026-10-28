using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La entrada con fichas (entrega 6) en el servicio: quién entra en la caja, sin usuario, PIN ni hash, con su letra y lo que
/// puede hacer; y entrar con la ficha y el PIN, que sigue revisando el servicio.
/// </summary>
public class EntradaApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private HttpClient Cliente() => servicio.Fabrica.CreateClient();

    private static async Task<CajaDto> CajaAsync(HttpClient http) =>
        (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single(c => c.Serie == "A");

    private static async Task<List<FichaUsuarioDto>> FichasAsync(HttpClient http, CajaDto caja) =>
        (await http.GetFromJsonAsync<List<FichaUsuarioDto>>($"api/cajas/{caja.Id}/usuarios"))!;

    private static async Task<string> ErrorAsync(HttpResponseMessage respuesta)
    {
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    /// <summary>Un usuario dado de alta directo en la base (la pantalla de usuarios llega con la entrega 1).</summary>
    private async Task<Usuario> DarDeAltaAsync(string nombre, string usuario, string pin, Guid? sucursalId, bool activo = true)
    {
        await using var db = servicio.Base();
        var nuevo = new Usuario { Nombre = nombre, NombreUsuario = usuario, Rol = Rol.Cajero, SucursalId = sucursalId, Activo = activo };
        nuevo.EstablecerPin(pin);
        db.Usuarios.Add(nuevo);
        await db.SaveChangesAsync();
        return nuevo;
    }

    [Fact]
    public async Task Las_fichas_traen_a_quien_entra_sin_su_usuario_ni_su_pin()
    {
        var http = Cliente();
        var caja = await CajaAsync(http);

        var json = await http.GetStringAsync($"api/cajas/{caja.Id}/usuarios");
        var fichas = JsonSerializer.Deserialize<List<FichaUsuarioDto>>(json, JsonSerializerOptions.Web)!;

        // Solo lo de la ficha: nada del PIN (ni su hash) ni el usuario con que se entraba escribiéndolo. Si todavía no tiene PIN (vino
        // de eleventa), solo eso: sí o no.
        using (var documento = JsonDocument.Parse(json))
        {
            Assert.All(documento.RootElement.EnumerateArray(), f => Assert.Equal(
                ["haceCorte", "nombre", "resumen", "sinPin", "tecla", "turnoAbierto", "usuarioId"], f.EnumerateObject().Select(p => p.Name).Order()));
        }
        Assert.DoesNotContain("pbkdf2", json);
        Assert.DoesNotContain("\"encargado\"", json);

        var cajero = fichas.Single(f => f.Nombre == "Cajero de prueba");
        var encargado = fichas.Single(f => f.Nombre == "Encargado de prueba");
        var dueno = fichas.Single(f => f.Nombre == "Dueño de prueba");
        Assert.Equal(("C", "Permisos por defecto", false), (cajero.Tecla, cajero.Resumen, cajero.HaceCorte));
        Assert.Equal(("E", "Corte de turno, productos y clientes", true), (encargado.Tecla, encargado.Resumen, encargado.HaceCorte));
        Assert.Equal(("D", "Todos los permisos", true), (dueno.Tecla, dueno.Resumen, dueno.HaceCorte));
    }

    [Fact]
    public async Task Solo_salen_los_activos_de_la_sucursal_de_la_caja_y_el_dueno()
    {
        var http = Cliente();
        var caja = await CajaAsync(http);
        Guid otraSucursal;
        await using (var db = servicio.Base())
        {
            var sucursal = new Sucursal { Clave = "S2", Nombre = "Sucursal 2" };
            db.Sucursales.Add(sucursal);
            await db.SaveChangesAsync();
            otraSucursal = sucursal.Id;
        }
        var deBaja = await DarDeAltaAsync("Inés de baja", "ines", "4444", caja.SucursalId, activo: false);
        var deOtra = await DarDeAltaAsync("Olga de la otra", "olga", "5555", otraSucursal);

        var fichas = await FichasAsync(http, caja);

        Assert.DoesNotContain(fichas, f => f.UsuarioId == deBaja.Id || f.UsuarioId == deOtra.Id);
        // El dueño no tiene sucursal: entra en todas.
        Assert.Contains(fichas, f => f.Nombre == "Dueño de prueba");
        // Y con su ficha tampoco entran: la de otra sucursal no es de esta caja; la de baja ya no entra, aun con su PIN bueno, y
        // dice lo mismo que la caja sin conexión, sin sumarle intentos.
        Assert.Equal("Ese usuario no entra en esta caja.",
            await ErrorAsync(await http.PostAsJsonAsync($"api/cajas/{caja.Id}/sesion", new SolicitudEntradaFicha(deOtra.Id, "5555"))));
        Assert.Equal("Ese usuario no entra en esta caja.",
            await ErrorAsync(await http.PostAsJsonAsync($"api/cajas/{caja.Id}/sesion", new SolicitudEntradaFicha(deBaja.Id, "4444"))));
        await using var datos = servicio.Base();
        Assert.Equal(0, (await datos.Usuarios.SingleAsync(u => u.Id == deBaja.Id)).IntentosFallidos);
    }

    [Fact]
    public async Task Las_letras_no_se_repiten_y_no_cambian_al_dar_de_alta_a_alguien()
    {
        var http = Cliente();
        var caja = await CajaAsync(http);
        var antes = await FichasAsync(http, caja);

        // Otra C: el cajero ya la tiene desde antes.
        var carmen = await DarDeAltaAsync("Carmen Prueba", "carmen", "6666", caja.SucursalId);
        var despues = await FichasAsync(http, caja);

        Assert.All(antes, f => Assert.Equal(f.Tecla, despues.Single(d => d.UsuarioId == f.UsuarioId).Tecla));
        var deCarmen = despues.Single(f => f.UsuarioId == carmen.Id).Tecla;
        Assert.NotNull(deCarmen);
        Assert.NotEqual("C", deCarmen);
        Assert.Equal(despues.Count, despues.Select(f => f.Tecla).Distinct().Count());
        // Las mismas letras cada vez que se piden.
        Assert.Equal(despues, await FichasAsync(http, caja));
    }

    [Fact]
    public async Task La_ficha_de_quien_tiene_el_turno_abierto_lo_dice_y_se_entra_con_la_ficha_y_el_pin()
    {
        var http = Cliente();
        var caja = await CajaAsync(http);
        var cajero = (await FichasAsync(http, caja)).Single(f => f.Nombre == "Cajero de prueba");

        var respuesta = await http.PostAsJsonAsync($"api/cajas/{caja.Id}/sesion", new SolicitudEntradaFicha(cajero.UsuarioId, "1111"));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        var sesion = (await respuesta.Content.ReadFromJsonAsync<SesionDto>())!;
        Assert.Equal((cajero.UsuarioId, "Cajero de prueba", Rol.Cajero), (sesion.UsuarioId, sesion.Nombre, sesion.Rol));

        if ((await http.GetAsync($"api/cajas/{caja.Id}/turno")).StatusCode == HttpStatusCode.NoContent)
            (await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, sesion.UsuarioId, 50000))).EnsureSuccessStatusCode();
        var fichas = await FichasAsync(http, caja);

        Assert.Equal([cajero.UsuarioId], fichas.Where(f => f.TurnoAbierto).Select(f => f.UsuarioId));
    }

    [Fact]
    public async Task Un_pin_equivocado_dice_cuantos_intentos_quedan_y_al_quinto_hay_que_esperar()
    {
        var http = Cliente();
        var caja = await CajaAsync(http);
        var lola = await DarDeAltaAsync("Lola Intentos", "lola", "4321", caja.SucursalId);
        Task<HttpResponseMessage> Entrar(string pin) => http.PostAsJsonAsync($"api/cajas/{caja.Id}/sesion", new SolicitudEntradaFicha(lola.Id, pin));

        var mensajes = new List<string>();
        for (var i = 0; i < 5; i++)
            mensajes.Add(await ErrorAsync(await Entrar("0000")));

        Assert.Equal(
            ["PIN incorrecto. Te quedan 4 intentos.", "PIN incorrecto. Te quedan 3 intentos.", "PIN incorrecto. Te quedan 2 intentos.",
                "PIN incorrecto. Te queda 1 intento.", "Demasiados intentos: espera unos minutos."], mensajes);
        // Bloqueada, ni con el bueno: como al escribir el usuario.
        Assert.Equal("Demasiados intentos: espera unos minutos.", await ErrorAsync(await Entrar("4321")));
        await using var db = servicio.Base();
        Assert.NotNull((await db.Usuarios.SingleAsync(u => u.Id == lola.Id)).BloqueadoHastaUtc);
    }
}
