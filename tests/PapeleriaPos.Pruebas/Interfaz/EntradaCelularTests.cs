using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Pruebas.Servicio;
using PapeleriaPos.Servicio;
using PapeleriaPos.Servicio.Componentes;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>La entrada de /conteo (auditoría, 2026-11-04): un celular no llega a bloquear la cuenta, y el tope lo dice como siempre.</summary>
public class EntradaCelularTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private async Task<(PantallaSinNavegador Pantalla, Func<int> Entradas)> AbrirAsync(string? aparato)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            // El del servicio: el renglón del tope va a su base.
            .AddSingleton(servicio.Fabrica.Services.GetRequiredService<IntentosPorAparato>())
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        var entradas = 0;
        await pantalla.AbrirAsync<EntradaCelular>(new()
        {
            ["Titulo"] = "Conteo",
            ["Aparato"] = aparato,
            ["AlEntrar"] = EventCallback.Factory.Create<SesionDto>(this, (SesionDto _) => entradas++),
        });
        await pantalla.EsperarElementoAsync("entrada-usuario");
        return (pantalla, () => entradas);
    }

    private static async Task ProbarAsync(PantallaSinNavegador pantalla, string usuario, string pin, string mensaje)
    {
        await pantalla.EscribirAsync("entrada-usuario", usuario);
        await pantalla.EscribirAsync("entrada-pin", pin);
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Entrar"));
        await pantalla.EsperarAsync(t => t.Contains(mensaje, StringComparison.Ordinal), mensaje);
    }

    private async Task<int> IntentosDeAsync(string usuario)
    {
        await using var db = servicio.Base();
        return (await db.Usuarios.AsNoTracking().SingleAsync(u => u.NombreUsuario == usuario)).IntentosFallidos;
    }

    [Fact]
    public async Task Con_tres_pin_malos_el_celular_espera_sin_bloquear_la_cuenta()
    {
        var (pantalla, entradas) = await AbrirAsync("192.168.1.77");
        await using var _ = pantalla;

        for (var i = 0; i < 3; i++)
            await ProbarAsync(pantalla, "encargado", "0000", "Usuario o PIN incorrecto.");
        // Ni con el bueno: ya no se pregunta, y a la cuenta no le suma.
        await ProbarAsync(pantalla, "encargado", "2222", FichasEntrada.DemasiadosIntentosDelAparato);

        Assert.Equal(0, entradas());
        Assert.Equal(3, await IntentosDeAsync("encargado"));
        await using var db = servicio.Base();
        Assert.Contains(await db.Bitacora.Select(b => b.Detalle).ToListAsync(), d => d.Contains("192.168.1.77", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Con_el_bueno_entra_y_no_gasta_intentos_del_celular()
    {
        var (pantalla, entradas) = await AbrirAsync("192.168.1.78");
        await using var _ = pantalla;

        for (var i = 1; i <= 4; i++)
        {
            await pantalla.EscribirAsync("entrada-usuario", "cajero");
            await pantalla.EscribirAsync("entrada-pin", "1111");
            await pantalla.OprimirAsync(await pantalla.BotonAsync("Entrar"));
            var hasta = DateTime.UtcNow.AddSeconds(30);
            while (entradas() < i && DateTime.UtcNow < hasta)
                await Task.Delay(20);
            Assert.Equal(i, entradas());
        }
    }
}
