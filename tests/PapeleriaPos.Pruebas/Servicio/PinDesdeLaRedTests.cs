using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Servicio;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Adivinar un PIN desde la red, sin llave (auditoría, 2026-11-04): lo que hace /api/alta y lo que cuenta cada intento.</summary>
public class PinDesdeLaRedTests
{
    private static HttpClient DesdeLaRedCliente(ServicioDePrueba servicio) =>
        servicio.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, DesdeLaRed>())).CreateClient();

    private static async Task<(int Intentos, bool Bloqueado, int Renglones)> DuenoAsync(ServicioDePrueba servicio)
    {
        await using var db = servicio.Base();
        var dueno = await db.Usuarios.AsNoTracking().SingleAsync(u => u.NombreUsuario == "dueno");
        return (dueno.IntentosFallidos, dueno.BloqueadoHastaUtc is not null, await db.Bitacora.CountAsync());
    }

    private static SolicitudAltaCaja Alta(string pin, Guid sucursal) => new("dueno", pin, sucursal, null, "Caja X", "X", "PC-X");

    [Fact]
    public async Task Con_una_sucursal_inventada_el_pin_ni_se_revisa_y_no_se_nota_si_era_el_bueno()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = DesdeLaRedCliente(servicio);

        var malo = await http.PostAsJsonAsync("api/alta", Alta("0000", Guid.NewGuid()));
        var bueno = await http.PostAsJsonAsync("api/alta", Alta("3333", Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, malo.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bueno.StatusCode);
        Assert.Equal("La sucursal no existe.", (await malo.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal("La sucursal no existe.", (await bueno.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    [Fact]
    public async Task El_intento_se_guarda_aunque_el_renglon_de_la_bitacora_no_se_pueda_guardar()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using (var db = servicio.Base())
            await Assert.ThrowsAnyAsync<Exception>(() => new ServicioControl(db)
                .IdentificarAsync("dueno", "0000", Guid.NewGuid(), AccionControlada.AdministrarCajas));

        Assert.Equal(1, (await DuenoAsync(servicio)).Intentos);
    }

    [Fact]
    public async Task Los_intentos_que_llegan_a_la_vez_cuentan_todos()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(i => http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", $"000{i}"))));

        Assert.True((await DuenoAsync(servicio)).Bloqueado);
    }

    [Fact]
    public async Task Un_usuario_que_no_existe_no_escribe_en_la_bitacora()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();
        var antes = (await DuenoAsync(servicio)).Renglones;

        for (var i = 0; i < 10; i++)
            await http.PostAsJsonAsync("api/alta/opciones", new SolicitudCredenciales($"nadie{i}", "0000"));

        Assert.Equal(antes, (await DuenoAsync(servicio)).Renglones);
    }

    [Fact]
    public async Task Al_bloquearse_una_cuenta_queda_un_renglon_y_mientras_espera_ya_no_escribe()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();

        // Por la entrada del celular, que antes no dejaba rastro.
        for (var i = 0; i < 5; i++)
            await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "0000"));
        await using (var db = servicio.Base())
            Assert.Single(await db.Bitacora.Where(b => b.Resultado == ResultadoBitacora.Negado && b.Detalle.StartsWith("Demasiados PIN incorrectos")).ToListAsync());

        // Ya bloqueado, por donde sea: ni un renglón más.
        var antes = (await DuenoAsync(servicio)).Renglones;
        for (var i = 0; i < 5; i++)
        {
            await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "0000"));
            await http.PostAsJsonAsync("api/alta/opciones", new SolicitudCredenciales("dueno", "0000"));
        }
        Assert.Equal(antes, (await DuenoAsync(servicio)).Renglones);
    }

    // ── Por aparato: uno solo no llega a bloquear la cuenta ──

    /// <summary>Cada llamada dice desde qué dirección llega, como aparatos distintos de la red.</summary>
    private sealed class DesdeLaIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> siguiente) => app =>
        {
            app.Use((contexto, resto) =>
            {
                contexto.Connection.RemoteIpAddress = IPAddress.Parse(contexto.Request.Headers["X-Ip-De-Prueba"].ToString());
                return resto(contexto);
            });
            siguiente(app);
        };
    }

    private static Func<string, string, string, Task<HttpResponseMessage>> Aparatos(ServicioDePrueba servicio)
    {
        var http = servicio.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, DesdeLaIp>())).CreateClient();
        return (ip, usuario, pin) =>
        {
            var pedido = new HttpRequestMessage(HttpMethod.Post, "api/alta/opciones") { Content = JsonContent.Create(new SolicitudCredenciales(usuario, pin)) };
            pedido.Headers.Add("X-Ip-De-Prueba", ip);
            return http.SendAsync(pedido);
        };
    }

    [Fact]
    public async Task Un_aparato_con_tres_pin_malos_espera_y_la_cuenta_sigue_entrando()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var intento = Aparatos(servicio);

        for (var i = 0; i < 3; i++)
            Assert.Equal("Usuario o PIN incorrecto.", (await (await intento("192.168.1.50", "dueno", "0000")).Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        // Ya ni se revisa, ni con el bueno: no suma a la cuenta.
        Assert.Equal(FichasEntrada.DemasiadosIntentos, (await (await intento("192.168.1.50", "dueno", "3333")).Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal(FichasEntrada.DemasiadosIntentos, (await (await intento("192.168.1.50", "noexiste", "0000")).Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        var dueno = await DuenoAsync(servicio);
        Assert.Equal(3, dueno.Intentos);
        Assert.False(dueno.Bloqueado);

        // La ficha de la caja entra, y otro aparato no paga lo del primero.
        var http = servicio.Fabrica.CreateClient();
        Assert.True((await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).IsSuccessStatusCode);
        Assert.True((await intento("192.168.1.51", "dueno", "3333")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Tampoco_con_muchos_a_la_vez_un_aparato_bloquea_la_cuenta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var intento = Aparatos(servicio);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => intento("192.168.1.50", "dueno", $"00{i:00}")));

        var dueno = await DuenoAsync(servicio);
        Assert.Equal(3, dueno.Intentos);
        Assert.False(dueno.Bloqueado);
    }

    [Fact]
    public async Task Al_llegar_al_tope_queda_un_renglon_con_la_direccion_y_el_pin_bueno_no_cuenta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var intento = Aparatos(servicio);

        // El bueno no gasta intentos del aparato.
        for (var i = 0; i < 5; i++)
            Assert.True((await intento("192.168.1.50", "dueno", "3333")).IsSuccessStatusCode);
        for (var i = 0; i < 6; i++)
            await intento("192.168.1.60", "noexiste", "0000");

        await using var db = servicio.Base();
        var renglon = Assert.Single(await db.Bitacora.Where(b => b.Detalle.Contains("192.168.1.")).ToListAsync());
        Assert.Contains("192.168.1.60", renglon.Detalle);
        Assert.Contains("noexiste", renglon.Detalle);
        Assert.Equal(ResultadoBitacora.Negado, renglon.Resultado);
    }

    [Fact]
    public async Task El_alta_tambien_cuenta_por_aparato_y_desde_esta_pc_no()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var sucursal = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))![0].SucursalId;
        var red = DesdeLaRedCliente(servicio);
        for (var i = 0; i < 3; i++)
            await red.PostAsJsonAsync("api/alta", Alta("0000", sucursal));
        Assert.Equal(FichasEntrada.DemasiadosIntentos, (await (await red.PostAsJsonAsync("api/alta", Alta("3333", sucursal))).Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);

        // Desde esta misma PC no hay aparato que contar: a esa solo la frena el bloqueo de la cuenta.
        var aqui = servicio.Fabrica.CreateClient();
        for (var i = 0; i < 4; i++)
            Assert.Equal("Usuario o PIN incorrecto.", (await (await aqui.PostAsJsonAsync("api/alta/opciones", new SolicitudCredenciales("cajero", "0000"))).Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    [Fact]
    public void A_los_cinco_minutos_del_ultimo_intento_el_aparato_vuelve_a_poder()
    {
        var reloj = new RelojFijo(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var intentos = new IntentosPorAparato(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<IntentosPorAparato>.Instance, reloj);

        for (var i = 0; i < IntentosPorAparato.Permitidos; i++)
        {
            Assert.True(intentos.Intentar("10.0.0.5"));
            reloj.Avanzar(TimeSpan.FromMinutes(1));
        }
        Assert.False(intentos.Intentar("10.0.0.5"));
        // Pedir mientras espera no alarga la espera.
        reloj.Avanzar(TimeSpan.FromMinutes(3));
        Assert.False(intentos.Intentar("10.0.0.5"));
        reloj.Avanzar(TimeSpan.FromMinutes(1));
        Assert.True(intentos.Intentar("10.0.0.5"));

        // Sin aparato (esta misma PC, o la conexión no dice): no se cuenta.
        for (var i = 0; i < 10; i++)
            Assert.True(intentos.Intentar(null));
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        private DateTimeOffset _ahora = ahora;
        public override DateTimeOffset GetUtcNow() => _ahora;
        public void Avanzar(TimeSpan cuanto) => _ahora += cuanto;
    }
}
