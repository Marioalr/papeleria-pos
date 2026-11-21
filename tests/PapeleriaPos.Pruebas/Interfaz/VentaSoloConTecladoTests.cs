using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Lo que pidió el dueño el 2026-11-21, solo con el teclado («toda la interfaz se debe poder usar solo con el teclado»): el descuento
/// (Ctrl+D) con «Precio final» primero y los modos con ↑ ↓, sin motivo para quien lo puede dar solo; «Salir de …» en un solo botón
/// con Ctrl+S; y el cobro (F12) con Alt+T, Alt+R, Alt+C y Alt+S («Cobrar sin imprimir el ticket», con su permiso). Ninguna prueba
/// de aquí oprime un botón: solo teclas y lo que se escribe.
/// </summary>
public sealed class VentaSoloConTecladoTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-teclado-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static Task Tecla(VentaConCopia v, string tecla, string? texto = null) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, texto));

    private static async Task<string> Texto(VentaConCopia v) => PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync());

    /// <summary>El total del ticket, como se ve en grande.</summary>
    private static async Task<long> TotalAsync(VentaConCopia v) =>
        PapeleriaPos.Interfaz.Formato.LeerPesos(PantallaSinNavegador.Limpio((await v.Pantalla.ElementoAsync(e => e.Clase?.Split(' ').Contains("vn-cifra") == true, "el total")).Texto))
        ?? throw new InvalidOperationException("El total no es dinero.");

    private static string Pesos(long centavos) => (centavos / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string?> ModoAsync(VentaConCopia v) =>
        (await v.Pantalla.LeerAsync()).Elementos
            .SingleOrDefault(e => e.Id?.StartsWith("descuento-modo-", StringComparison.Ordinal) == true && e.Etiqueta == "input" && e.Atributos.GetValueOrDefault("checked") is true)
            ?.Id;

    // ── El descuento ────────────────────────────────────────────────────────

    [Fact]
    public async Task Precio_final_va_primero_y_elegido_y_las_flechas_cambian_el_modo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);

        await Tecla(v, "Ctrl+D", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");

        // En este orden, con «Precio final» elegido y las teclas a la vista.
        var modos = (await v.Pantalla.LeerAsync()).Elementos.Where(e => e.Etiqueta == "input" && e.Id?.StartsWith("descuento-modo-", StringComparison.Ordinal) == true)
            .Select(e => e.Id).ToList();
        Assert.Equal(["descuento-modo-precio", "descuento-modo-monto", "descuento-modo-porcentaje"], modos);
        Assert.Equal("descuento-modo-precio", await ModoAsync(v));
        Assert.Contains("Que quede en ($)", await Texto(v));
        Assert.Contains("↑ ↓ cambian el modo", await Texto(v));

        // ↓ al siguiente, ↑ al de antes, y dan la vuelta; también desde «Motivo».
        await v.Pantalla.TeclaAsync("descuento-valor", "ArrowDown");
        Assert.Equal("descuento-modo-monto", await ModoAsync(v));
        await v.Pantalla.TeclaAsync("descuento-valor", "ArrowDown");
        Assert.Equal("descuento-modo-porcentaje", await ModoAsync(v));
        Assert.Contains("Cuánto (%)", await Texto(v));
        await v.Pantalla.TeclaAsync("descuento-valor", "ArrowDown");
        Assert.Equal("descuento-modo-precio", await ModoAsync(v));
        await v.Pantalla.TeclaAsync("descuento-motivo", "ArrowUp");
        Assert.Equal("descuento-modo-porcentaje", await ModoAsync(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task El_dueno_pone_el_precio_final_sin_motivo_con_Enter_y_cobra_con_F12()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        var objetivo = await TotalAsync(v) - 100;

        await Tecla(v, "Ctrl+D", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");
        // Con todo el descuento en «Puede», el motivo es opcional y Enter aplica (no pasa al motivo).
        Assert.Contains("Motivo (opcional)", await Texto(v));
        await v.Pantalla.EscribirAsync("descuento-valor", Pesos(objetivo));
        await v.Pantalla.TeclaAsync("descuento-valor", "Enter");
        await v.Pantalla.EsperarAsync(t => !t.Contains("Que quede en"), "el descuento puesto");
        Assert.Contains("Descuentos −$1.00", await Texto(v));

        // F12 y Enter: cobrado, sin motivo en la venta.
        await Tecla(v, "F12", "");
        await Tecla(v, "Enter-cobro");
        await v.Pantalla.EsperarAsync(t => t.Contains("cobrada"), "el cobro");
        using var db = servicio.Base();
        var venta = await db.Ventas.Include(x => x.Lineas).OrderByDescending(x => x.PagadaUtc).FirstAsync();
        Assert.Equal(objetivo, venta.TotalCentavos);
        Assert.Null(venta.Lineas.Single().MotivoDescuento);
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task La_cajera_da_sin_motivo_hasta_su_limite_y_arriba_de_el_le_pide_el_motivo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync("10*" + a.Codigo);

        // 5 % (↑ desde «Precio final» llega a «Porcentaje»), sin motivo: Enter lo aplica.
        await Tecla(v, "Ctrl+D", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");
        Assert.Contains("Motivo (opcional hasta 10 %)", await Texto(v));
        await v.Pantalla.TeclaAsync("descuento-valor", "ArrowUp");
        await v.Pantalla.EscribirAsync("descuento-valor", "5");
        await v.Pantalla.TeclaAsync("descuento-valor", "Enter");
        await v.Pantalla.EsperarAsync(t => !t.Contains("Cuánto (%)"), "el descuento puesto");
        Assert.Contains("Descuentos", await Texto(v));

        // 15 %, sin motivo: no pide la autorización sin él; lo dice y el cursor va al motivo. Con el motivo y Enter, la pide.
        await Tecla(v, "Ctrl+D", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");
        await v.Pantalla.TeclaAsync("descuento-valor", "ArrowUp");
        await v.Pantalla.EscribirAsync("descuento-valor", "15");
        await v.Pantalla.TeclaAsync("descuento-valor", "Enter");
        await v.Pantalla.EsperarAsync(t => t.Contains(Permisos.MotivoDelDescuento), "que falta el motivo");
        Assert.DoesNotContain("Lo autoriza", await Texto(v));
        await v.Pantalla.EscribirAsync("descuento-motivo", "Pasta doblada");
        await v.Pantalla.TeclaAsync("descuento-motivo", "Enter");
        await v.Pantalla.EsperarAsync(t => t.Contains("Lo autoriza"), "la autorización");
        Assert.Empty(v.Pantalla.Errores);
    }

    // ── «Salir de …» ────────────────────────────────────────────────────────

    [Fact]
    public async Task Sin_vender_Ctrl_S_es_salir_de_quien_entro()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var dueno = await v.Cliente.EntrarAsync("dueno", "3333");
        var salidas = 0;
        await using var p = new PantallaSinNavegador(v.Servicios);
        var administracion = await p.AbrirAsync<Administracion>(new()
        {
            [nameof(Administracion.Caja)] = v.Caja,
            [nameof(Administracion.Sesion)] = dueno,
            [nameof(Administracion.AlSalir)] = new EventCallback(null, (Action)(() => salidas++)),
        });

        // Un solo botón arriba, sin menú: «Salir de» y su tecla.
        var elementos = (await p.LeerAsync()).Elementos;
        Assert.Single(elementos, e => e.Etiqueta == "button" && e.Clase?.Split(' ').Contains("vn-usuario") == true);
        var boton = PantallaSinNavegador.Limpio((await p.PorIdAsync("boton-salir")).Texto);
        Assert.Contains($"Salir de {dueno.Nombre}", boton);
        Assert.EndsWith("sin vender Ctrl+S", boton);

        await p.Dispatcher.InvokeAsync(() => administracion.Tecla("Ctrl+S", null));
        await p.EsperarAsync(_ => salidas == 1, "la salida");
        Assert.Empty(p.Errores);
    }

    // ── El cobro (F12) ──────────────────────────────────────────────────────

    [Fact]
    public async Task En_el_cobro_las_formas_entran_y_salen_con_su_tecla()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        var total = await TotalAsync(v);
        await Tecla(v, "F12", "");

        // Cada forma dice su tecla; sin el permiso, no hay «Sin ticket».
        var texto = await Texto(v);
        Assert.Contains("Tarjeta Alt+T", texto);
        Assert.Contains("Transf. Alt+R", texto);
        Assert.Contains("Nota F4", texto);
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "cobro-sin-ticket");

        // Alt+T: la tarjeta con todo lo que falta, y el cursor en su monto; otra vez Alt+T la quita.
        await Tecla(v, "Alt+T");
        Assert.Equal(Pesos(total), (await v.Pantalla.PorIdAsync("pago-monto-0")).Valor);
        Assert.Contains("Tarjeta (terminal)", await Texto(v));
        await Tecla(v, "Alt+T");
        Assert.DoesNotContain("Tarjeta (terminal)", await Texto(v));

        // Alt+R: la transferencia. Alt+C sin cliente: lo dice, no agrega nada.
        await Tecla(v, "Alt+R");
        Assert.Contains("Transferencia", await Texto(v));
        await Tecla(v, "Alt+C");
        Assert.Contains("primero elige al cliente (Ctrl+L)", await Texto(v));
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "pago-monto-1");

        // Alt+S sin el permiso: solo avisa.
        await Tecla(v, "Alt+S");
        Assert.Contains("Cobrar sin imprimir el ticket", await Texto(v));

        // Alt+R otra vez la quita, y Enter cobra exacto en efectivo, como siempre.
        await Tecla(v, "Alt+R");
        Assert.DoesNotContain("Transferencia", await Texto(v));
        await Tecla(v, "Enter-cobro");
        await v.Pantalla.EsperarAsync(t => t.Contains("cobrada"), "el cobro");
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Con_el_permiso_Alt_S_cobra_sin_imprimir_y_la_venta_queda_por_imprimir()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "F12", "");
        await v.Pantalla.EscribirAsync("pago-nuevo", "500");

        // Alt+S lo pone (y otra vez lo quita); el botón de cobrar lo dice. F12 cobra sin preguntar nada más.
        Assert.Equal("Sin ticket Alt+S", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("cobro-sin-ticket")).Texto));
        await Tecla(v, "Alt+S");
        Assert.Equal("true", (await v.Pantalla.PorIdAsync("cobro-sin-ticket")).Atributos["aria-pressed"]?.ToString());
        Assert.StartsWith("Cobrar sin ticket · cambio", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("cobrar-nuevo")).Texto));
        await Tecla(v, "Alt+S");
        Assert.StartsWith("Cobrar · cambio", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("cobrar-nuevo")).Texto));
        await Tecla(v, "Alt+S");

        await Tecla(v, "Enter-cobro");
        await v.Pantalla.EsperarAsync(t => t.Contains("cobrada"), "el cobro");
        Assert.Contains("sin imprimir", await Texto(v));
        using (var db = servicio.Base())
        {
            var venta = await db.Ventas.OrderByDescending(x => x.PagadaUtc).FirstAsync();
            Assert.True(venta.TicketSinImprimir);
            Assert.True(await db.Bitacora.AnyAsync(b => b.VentaId == venta.Id && b.Accion == AccionControlada.CobrarSinTicket));
        }

        // El siguiente ticket empieza sin «Sin ticket».
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "F12", "");
        Assert.Equal("false", (await v.Pantalla.PorIdAsync("cobro-sin-ticket")).Atributos["aria-pressed"]?.ToString());
        Assert.Empty(v.Pantalla.Errores);
    }
}
