using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

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
}
