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

    [Fact]
    public async Task En_Windows_el_menu_de_arriba_minimiza_y_el_corte_cierra()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var ventana = new VentanaDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, ventana: ventana);

        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Clase == "vn-usuario", "el usuario de arriba"));
        Assert.False(await HayBotonAsync(v, "Salir"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Minimizar la caja"));
        Assert.Equal((1, 0), (ventana.Minimizada, ventana.Cerrada));
        // El menú se cerró al minimizar.
        Assert.False(await HayBotonAsync(v, "Minimizar la caja"));

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Corte"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cerrar la caja"));
        Assert.Equal((1, 1), (ventana.Minimizada, ventana.Cerrada));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task En_el_navegador_el_menu_sigue_con_Salir_y_el_corte_no_cierra_nada()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);

        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Clase == "vn-usuario", "el usuario de arriba"));
        Assert.True(await HayBotonAsync(v, "Salir"));
        Assert.False(await HayBotonAsync(v, "Minimizar la caja"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Corte"));
        Assert.False(await HayBotonAsync(v, "Cerrar la caja"));
        Assert.Empty(v.Pantalla.Errores);
    }
}
