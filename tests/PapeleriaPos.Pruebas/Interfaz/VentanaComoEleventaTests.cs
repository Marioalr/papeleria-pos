using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La caja de Windows como eleventa (el dueño, 2026-11-07): desde arriba solo se minimiza y se cierra desde el corte. Sin barra de
/// título, se minimiza con «–» junto a «Salir de …» (también en «¿Quién atiende?» y al abrir el turno), con Ctrl+Shift+M y con
/// «Minimizar la caja» arriba de «Salir» (2026-11-21: el dueño no lo encontraba); el corte trae «Cerrar la caja». En el navegador
/// (sin la ventana) todo sigue como antes.
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
    /// En la caja de Windows, «Salir» (2026-11-20) tiene «Minimizar la caja» y «Cerrar la caja»; arriba, el nombre y «Salir» son un
    /// solo botón, «Salir de …» (2026-11-21).
    /// </summary>
    [Fact]
    public async Task En_Windows_salir_minimiza_y_cierra_la_caja()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var ventana = new VentanaDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, ventana: ventana);

        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("boton-salir"));
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

        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("boton-salir"));
        await v.Pantalla.EsperarElementoAsync("salir-cambiar");
        Assert.False(await HayBotonAsync(v, "Minimizar la caja"));
        Assert.False(await HayBotonAsync(v, "Cerrar la caja"));
        Assert.Empty(v.Pantalla.Errores);
    }

    private static Task TeclaAsync(VentaConCopia v, string tecla) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, null));

    private static async Task<bool> SalirAbiertoAsync(VentaConCopia v) => (await v.Pantalla.LeerAsync()).Elementos.Any(e => e.Id == "salir-cambiar");

    [Fact]
    public async Task El_boton_de_arriba_minimiza_en_la_venta_y_en_Productos_sin_abrir_salir()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var ventana = new VentanaDePrueba();
        // El dueño, que tiene Productos (F3).
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, ventana: ventana, quien: ("dueno", "3333"));

        var boton = await v.Pantalla.PorIdAsync("boton-minimizar");
        Assert.Equal("Minimizar la caja (Ctrl+Shift+M)", boton.Atributos["title"]?.ToString());
        Assert.Equal("–", PantallaSinNavegador.Limpio(boton.Texto));
        await v.Pantalla.OprimirAsync(boton);
        Assert.Equal((1, 0), (ventana.Minimizada, ventana.Cerrada));
        Assert.False(await SalirAbiertoAsync(v));

        await TeclaAsync(v, "F3");
        await v.Pantalla.EsperarElementoAsync("prod-0");
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("boton-minimizar"));
        Assert.Equal((2, 0), (ventana.Minimizada, ventana.Cerrada));
        Assert.False(await SalirAbiertoAsync(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Ctrl_Shift_M_minimiza_desde_la_venta_y_esta_en_las_acciones()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var ventana = new VentanaDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, ventana: ventana);

        await TeclaAsync(v, "Ctrl+Shift+M");
        Assert.Equal(1, ventana.Minimizada);
        Assert.False(await SalirAbiertoAsync(v));
        // Con «Salir» abierto, también (y «Salir» se queda como está).
        await TeclaAsync(v, "Ctrl+S");
        await v.Pantalla.EsperarElementoAsync("salir-cambiar");
        await TeclaAsync(v, "Ctrl+Shift+M");
        Assert.Equal(2, ventana.Minimizada);

        // Y en «/»: «/minimizar» y Enter.
        await TeclaAsync(v, "Escape");
        await v.EscanearAsync("/minimizar");
        await VentaConCopia.EsperarAsync(() => ventana.Minimizada == 3, "la acción de «/»");
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task En_quien_atiende_tambien_esta_el_boton()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var ventana = new VentanaDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, ventana: ventana);
        await using var p = new PantallaSinNavegador(v.Servicios);
        await p.AbrirAsync<Entrada>(new() { [nameof(Entrada.Caja)] = v.Caja, [nameof(Entrada.Turno)] = await v.Cliente.TurnoAsync(v.Caja.Id) });
        await p.EsperarAsync(t => t.Contains("¿Quién atiende?"), "las fichas");

        await p.OprimirAsync(await p.PorIdAsync("boton-minimizar"));

        Assert.Equal(1, ventana.Minimizada);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_el_navegador_no_hay_boton_de_minimizar()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);

        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "boton-minimizar");
        await TeclaAsync(v, "Ctrl+Shift+M");
        await using var p = new PantallaSinNavegador(v.Servicios);
        await p.AbrirAsync<Entrada>(new() { [nameof(Entrada.Caja)] = v.Caja, [nameof(Entrada.Turno)] = await v.Cliente.TurnoAsync(v.Caja.Id) });
        await p.EsperarAsync(t => t.Contains("¿Quién atiende?"), "las fichas");
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "boton-minimizar");
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Con_el_turno_ya_cerrado_salir_sigue_minimizando()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var ventana = new VentanaDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, ventana: ventana);

        await TeclaAsync(v, "Ctrl+S");
        // Arriba de «Salir», a la vista.
        Assert.StartsWith("– Minimizar la caja", PantallaSinNavegador.Limpio((await v.Pantalla.EsperarElementoAsync("salir-minimizar")).Texto));
        await v.Pantalla.EsperarElementoAsync("corte-contado");
        await v.Pantalla.EscribirAsync("corte-contado", "500");
        await v.Pantalla.TeclaAsync("corte-contado", "Enter");
        await v.Pantalla.EsperarElementoAsync("corte-pin");
        await v.Pantalla.EscribirAsync("corte-pin", "1111");
        await v.Pantalla.TeclaAsync("corte-pin", "Enter");
        await v.Pantalla.EsperarElementoAsync("salir-listo");

        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("salir-minimizar"));

        Assert.Equal(1, ventana.Minimizada);
        Assert.Empty(v.Pantalla.Errores);
    }
}
