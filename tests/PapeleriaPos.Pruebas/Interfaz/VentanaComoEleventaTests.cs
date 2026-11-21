using PapeleriaPos.Interfaz;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La caja de Windows como eleventa (el dueño, 2026-11-07): desde arriba solo se minimiza y se cierra desde el corte. El menú del
/// usuario dice «Minimizar la caja» en lugar de «Salir», y el corte trae «Cerrar la caja». En el navegador (sin la ventana) todo sigue
/// como antes.
/// </summary>
public sealed class VentanaComoEleventaTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-ventana-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private sealed class VentanaDePrueba : IVentanaDeLaCaja
    {
        public int Minimizada { get; private set; }
        public int Cerrada { get; private set; }
        public void Minimizar() => Minimizada++;
        public void Cerrar() => Cerrada++;
    }

    private static Task<bool> HayBotonAsync(VentaConCopia v, string texto) => v.Pantalla.LeerAsync()
        .ContinueWith(t => t.Result.Elementos.Any(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == texto));

    /// <summary>
    /// En la caja de Windows, «Salir» (2026-11-20) tiene «Minimizar la caja» y «Cerrar la caja»; el nombre de arriba ya no es un
    /// menú: abre «Salir», como el botón.
    /// </summary>
    [Fact]
    public async Task En_Windows_salir_minimiza_y_cierra_la_caja()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var ventana = new VentanaDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, ventana: ventana);

        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Clase == "vn-usuario", "el usuario de arriba"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.EsperarElementoAsync("salir-minimizar"));
        Assert.Equal((1, 0), (ventana.Minimizada, ventana.Cerrada));
        // «Salir» se quitó al minimizar.
        Assert.False(await HayBotonAsync(v, "Minimizar la caja"));

        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("boton-salir"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.EsperarElementoAsync("salir-cerrar-caja"));
        Assert.Equal((1, 1), (ventana.Minimizada, ventana.Cerrada));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task En_el_navegador_salir_no_minimiza_ni_cierra_la_caja()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);

        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Clase == "vn-usuario", "el usuario de arriba"));
        await v.Pantalla.EsperarElementoAsync("salir-cambiar");
        Assert.False(await HayBotonAsync(v, "Minimizar la caja"));
        Assert.False(await HayBotonAsync(v, "Cerrar la caja"));
        Assert.Empty(v.Pantalla.Errores);
    }
}
