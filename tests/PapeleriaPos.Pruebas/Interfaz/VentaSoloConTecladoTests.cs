using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;
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

        // F2 (y Alt+S, lo mismo) sin el permiso: solo avisa, en el cobro, y no cobra.
        await Tecla(v, "F2-cobro");
        Assert.Contains("No tienes permiso para «Cobrar sin imprimir el ticket»", await Texto(v));
        await Tecla(v, "Alt+S");
        Assert.Contains("No tienes permiso para «Cobrar sin imprimir el ticket»", await Texto(v));
        Assert.DoesNotContain("cobrada", await Texto(v));

        // Alt+R otra vez la quita, y Enter cobra exacto en efectivo, como siempre.
        await Tecla(v, "Alt+R");
        Assert.DoesNotContain("Transferencia", await Texto(v));
        await Tecla(v, "Enter-cobro");
        await v.Pantalla.EsperarAsync(t => t.Contains("cobrada"), "el cobro");
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Espera (con su límite) a que haya <paramref name="cuantas"/> ventas cobradas en la base.</summary>
    private static async Task CobradasAsync(ServicioDePrueba servicio, int cuantas)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            using (var db = servicio.Base())
                if (await db.Ventas.CountAsync(x => x.PagadaUtc != null) >= cuantas)
                    return;
            Assert.True(DateTime.UtcNow < limite, $"No llegaron las {cuantas} ventas cobradas.");
            await Task.Delay(50);
        }
    }

    /// <summary>La última venta cobrada.</summary>
    private static async Task<PapeleriaPos.Nucleo.Ventas.Venta> UltimaAsync(ServicioDePrueba servicio)
    {
        using var db = servicio.Base();
        return await db.Ventas.AsNoTracking().OrderByDescending(x => x.PagadaUtc).FirstAsync();
    }

    [Fact]
    public async Task Con_el_permiso_F2_vende_sin_imprimir_y_F1_con_ticket_sin_preguntar_mas()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "F12", "");
        await v.Pantalla.EscribirAsync("pago-nuevo", "500");

        // Los dos botones dicen su tecla: «Cobrar … F1» y, con el permiso, «Sin ticket F2».
        Assert.Equal("Sin ticket F2", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("cobro-sin-ticket")).Texto));
        var cobrar = PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("cobrar-nuevo")).Texto);
        Assert.StartsWith("Cobrar · cambio", cobrar);
        Assert.EndsWith("F1", cobrar);

        // F2 vende al momento, sin imprimir: sin segunda confirmación.
        await Tecla(v, "F2-cobro");
        await v.Pantalla.EsperarAsync(t => t.Contains("cobrada"), "el cobro sin ticket");
        Assert.Contains("sin imprimir", await Texto(v));
        var sinTicket = await UltimaAsync(servicio);
        Assert.True(sinTicket.TicketSinImprimir);
        using (var db = servicio.Base())
            Assert.True(await db.Bitacora.AnyAsync(b => b.VentaId == sinTicket.Id && b.Accion == AccionControlada.CobrarSinTicket));

        // El siguiente, con F1: con ticket.
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "F12", "");
        await Tecla(v, "F1-cobro");
        await CobradasAsync(servicio, 2);
        var conTicket = await UltimaAsync(servicio);
        Assert.False(conTicket.TicketSinImprimir);

        // Alt+S, lo mismo que F2; y después de él, Enter sigue cobrando con ticket.
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "F12", "");
        await Tecla(v, "Alt+S");
        await CobradasAsync(servicio, 3);
        var conAltS = await UltimaAsync(servicio);
        Assert.True(conAltS.TicketSinImprimir);
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "F12", "");
        await Tecla(v, "Enter-cobro");
        await CobradasAsync(servicio, 4);
        Assert.False((await UltimaAsync(servicio)).TicketSinImprimir);
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task F2_no_cobra_lo_que_falta_y_no_deja_sin_ticket_al_siguiente_Enter()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "F12", "");
        await v.Pantalla.EscribirAsync("pago-nuevo", "0.50");

        // Falta dinero: F2 no cobra; lo arregla y Enter cobra con ticket (lo de F2 no se queda).
        await Tecla(v, "F2-cobro");
        Assert.Contains("Faltan", await Texto(v));
        Assert.DoesNotContain("cobrada", await Texto(v));
        await v.Pantalla.EscribirAsync("pago-nuevo", "");
        await Tecla(v, "Enter-cobro");
        await v.Pantalla.EsperarAsync(t => t.Contains("cobrada"), "el cobro");
        Assert.False((await UltimaAsync(servicio)).TicketSinImprimir);
        Assert.Empty(v.Pantalla.Errores);
    }

    // ── El porcentaje, subir el precio y la ventana sin su renglón (2026-11-22) ──

    [Fact]
    public async Task El_descuento_dice_su_porcentaje_en_el_renglon_y_en_el_total()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync("10*" + a.Codigo);
        var total = await TotalAsync(v);

        // 10 % con ↑ (a «Porcentaje») y Enter: el renglón y el total lo dicen con su porcentaje.
        await Tecla(v, "Ctrl+D", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");
        await v.Pantalla.TeclaAsync("descuento-valor", "ArrowUp");
        await v.Pantalla.EscribirAsync("descuento-valor", "10");
        await v.Pantalla.TeclaAsync("descuento-valor", "Enter");
        await v.Pantalla.EsperarAsync(t => !t.Contains("Cuánto (%)"), "el descuento puesto");
        var descuento = PapeleriaPos.Interfaz.Formato.Dinero(total / 10);
        var texto = await Texto(v);
        Assert.Contains($"Descuento −{descuento} (10 %)", texto);
        Assert.Equal($"Descuentos −{descuento} (10 %)", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("total-descuentos")).Texto));

        // En pesos, con «Precio final»: el porcentaje sale del monto, con un decimal.
        await Tecla(v, "Ctrl+D", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");
        await v.Pantalla.EscribirAsync("descuento-valor", Pesos(total - 300));
        await v.Pantalla.TeclaAsync("descuento-valor", "Enter");
        await v.Pantalla.EsperarAsync(t => !t.Contains("Que quede en"), "el precio final");
        var porcentaje = PorcentajeDelDescuento.Texto(300, total);
        Assert.Contains($"Descuento −$3.00 ({porcentaje})", await Texto(v));
        Assert.Contains($"Descuentos −$3.00 ({porcentaje})", await Texto(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Ctrl_E_abre_la_misma_ventana_y_un_precio_final_mas_alto_sube_el_precio_sin_motivo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        var total = await TotalAsync(v);

        await Tecla(v, "Ctrl+E", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");
        Assert.Equal("descuento-modo-precio", await ModoAsync(v));
        Assert.Contains("Menos es descuento; más, sube el precio", await Texto(v));
        await v.Pantalla.EscribirAsync("descuento-valor", Pesos(total + 1000));
        await v.Pantalla.TeclaAsync("descuento-valor", "Enter");
        await v.Pantalla.EsperarAsync(t => t.Contains("Precio cambiado"), "el precio cambiado");
        Assert.Equal(total + 1000, await TotalAsync(v));
        Assert.DoesNotContain("Descuentos", await Texto(v));

        await Tecla(v, "F12", "");
        await Tecla(v, "Enter-cobro");
        await v.Pantalla.EsperarAsync(t => t.Contains("cobrada"), "el cobro");
        using var db = servicio.Base();
        var venta = await db.Ventas.Include(x => x.Lineas).AsNoTracking().OrderByDescending(x => x.PagadaUtc).FirstAsync();
        Assert.Equal(total + 1000, venta.TotalCentavos);
        Assert.NotNull(venta.Lineas.Single().PrecioOriginalCentavos);
        Assert.Equal(0, venta.Lineas.Single().DescuentoCentavos);
        Assert.True(await db.Bitacora.AnyAsync(b => b.VentaId == venta.Id && b.Accion == AccionControlada.CambiarPrecio));
        Assert.False(await db.Bitacora.AnyAsync(b => b.VentaId == venta.Id && b.Accion == AccionControlada.Descuento));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task A_la_cajera_subir_el_precio_le_pide_la_autorizacion_de_cambiar_precio_y_Esc_lo_deja_como_estaba()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        var total = await TotalAsync(v);

        await Tecla(v, "Ctrl+D", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");
        await v.Pantalla.EscribirAsync("descuento-valor", Pesos(total + 500));
        Assert.Contains("subir el precio no lo pide", await Texto(v));
        // Sin motivo: Enter no pasa al motivo, pide la autorización (de «Cambiar precio», no la del descuento).
        await v.Pantalla.TeclaAsync("descuento-valor", "Enter");
        await v.Pantalla.EsperarAsync(t => t.Contains("Lo autoriza"), "la autorización");
        Assert.Contains("Precio de «", await Texto(v));

        await Tecla(v, "Escape");
        await v.Pantalla.EsperarAsync(t => !t.Contains("Lo autoriza"), "la ventana cerrada");
        Assert.Equal(total, await TotalAsync(v));
        Assert.DoesNotContain("Precio cambiado", await Texto(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task La_ventana_se_cierra_si_su_renglon_se_quita_del_ticket()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync(b.Codigo!);

        // Ctrl+D sobre B (el último); con la ventana abierta, «Quitar» debajo de B (el único clic de la prueba: con el teclado, Supr es
        // del panel y no quita renglones).
        await Tecla(v, "Ctrl+D", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");
        Assert.Contains($"Al artículo: {b.Descripcion}", await Texto(v));
        await v.Pantalla.EscribirAsync("descuento-valor", "1");
        var quitar = await v.Pantalla.ElementoAsync(e => e.Etiqueta == "button" && e.Clase?.Contains("vn-accion", StringComparison.Ordinal) == true
            && PantallaSinNavegador.Limpio(e.Texto).StartsWith("Quitar", StringComparison.Ordinal), "«Quitar» en la fila");
        await v.Pantalla.OprimirAsync(quitar);

        await v.Pantalla.EsperarAsync(t => t.Contains($"«{b.Descripcion}» ya no está en el ticket"), "el aviso");
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "descuento-valor");
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.DoesNotContain(renglones, r => r.Contains("Descuento", StringComparison.Ordinal));

        // La venta sigue con el teclado: Ctrl+D abre la de A, y F12 y Enter cobran.
        await Tecla(v, "Ctrl+D", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");
        Assert.Contains($"Al artículo: {a.Descripcion}", await Texto(v));
        await Tecla(v, "Escape");
        await Tecla(v, "F12", "");
        await Tecla(v, "Enter-cobro");
        await v.Pantalla.EsperarAsync(t => t.Contains("cobrada"), "el cobro");
        Assert.Empty(v.Pantalla.Errores);
    }
}
