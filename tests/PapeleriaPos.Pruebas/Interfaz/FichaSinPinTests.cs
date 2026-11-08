using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La entrada con quien vino de eleventa sin PIN (el piloto alfa, 2026-10-25): su ficha sale, y al tocarla dice que le pida su
/// PIN al dueño en lugar de enseñar el teclado del PIN. No hay otro camino.
/// </summary>
public sealed class FichaSinPinTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-ficha-sin-pin-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    [Fact]
    public async Task Al_tocar_su_ficha_dice_que_le_pida_su_pin_al_duenio_y_no_hay_pin_que_teclear()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        await using (var db = servicio.Base())
        {
            await UsuariosDeEleventa.AplicarAsync(db, [new FilaUsuario(3, "cajera2", "María López", true, false)], caja.SucursalId);
            await db.SaveChangesAsync();
        }
        var red = new RedDePrueba(servicio.Fabrica.Server.CreateHandler());
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(red), conexion);
        var cliente = new ClienteCaja(new HttpClient(red), conexion, contingencia);
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton(contingencia)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        try
        {
            await pantalla.AbrirAsync<Entrada>(new() { [nameof(Entrada.Caja)] = caja, [nameof(Entrada.Turno)] = null });
            await pantalla.EsperarAsync(t => t.Contains("María López"), "las fichas, con la de María");
            Assert.Contains("María López", PantallaSinNavegador.Limpio(await pantalla.TextoAsync()));
            Assert.Contains("Permisos por defecto · sin PIN", PantallaSinNavegador.Limpio(await pantalla.TextoAsync()));

            await pantalla.OprimirAsync(await Ficha(pantalla, "María López"));

            await pantalla.EsperarAsync(t => t.Contains(FichasEntrada.SinPin), "el aviso de que no tiene PIN");
            var (texto, elementos) = await pantalla.LeerAsync();
            Assert.DoesNotContain("PIN de María López", texto);
            // Sin el teclado del PIN: ni sus cifras ni «Entrar».
            Assert.DoesNotContain(elementos, e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) is "1" or "0" or "Entrar");

            // Quien sí tiene PIN, como siempre.
            await pantalla.OprimirAsync(await Ficha(pantalla, "Cajero de prueba"));
            await pantalla.EsperarAsync(t => t.Contains("PIN de Cajero de prueba") && !t.Contains(FichasEntrada.SinPin), "el PIN del cajero");
            Assert.Empty(pantalla.Errores);
        }
        finally
        {
            await ((IAsyncDisposable)pantalla).DisposeAsync();
            await servicios.DisposeAsync();
            contingencia.Cerrar();
        }
    }

    private static Task<ElementoDibujado> Ficha(PantallaSinNavegador pantalla, string nombre) =>
        pantalla.ElementoAsync(e => e.Etiqueta == "button" && e.Clase?.StartsWith("ficha-entrada") == true && e.Texto.Contains(nombre), $"la ficha de {nombre}");
}
