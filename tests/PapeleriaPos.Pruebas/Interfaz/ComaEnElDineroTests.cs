using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El dinero de todas las pantallas con la regla de los números del POS, la de los productos en Excel y la de «Agregar
/// inventario» (revisión del PR #138): el punto es el decimal y la coma solo separa los miles, de tres en tres. Antes «12,50» se
/// leía $1,250.00 sin avisar: en «Pagó con» daba un cambio de más de mil pesos y en el corte, un sobrante. Ahora se pide con
/// punto, ahí donde está el cajero, y nada se cobra, se abre ni se cierra con eso: «Pagó con» (sin perder la venta), el fondo al
/// abrir el turno, el efectivo del corte, Ctrl+E y un precio en F3.
/// </summary>
public sealed class ComaEnElDineroTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private const string CentavosConPunto = "Los centavos van con punto: 12.50 (la coma solo separa los miles: 1,250.00).";

    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-coma-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Dueno, SesionDto Encargado, SesionDto Cajero);

    private async Task<Caja> PrepararAsync()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var cliente = new ClienteCaja(servicio.Fabrica.CreateClient(), conexion);
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("dueno", "3333"), await cliente.EntrarAsync("encargado", "2222"),
            await cliente.EntrarAsync("cajero", "1111"));
    }

    /// <summary>Sin turno abierto en la caja: si hay uno (el de otra prueba), el encargado le hace el corte.</summary>
    private static async Task SinTurnoAsync(Caja c)
    {
        if (await c.Cliente.TurnoAsync(c.Datos.Id) is { } abierto)
            await c.Cliente.CorteZAsync(abierto.Id, new SolicitudCorteZ(c.Encargado.UsuarioId, abierto.FondoCentavos));
    }

    private static async Task HastaQueAsync(Func<Task<bool>> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!await condicion())
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No pasó: {que}.");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// «Pagó con» 12,50 no se cobra (antes, con un ticket de unos pesos, daba más de mil de cambio): bajo «Pagó con» dice cómo
    /// escribirlo, «Cobrar» no se puede, y ni Enter ni F12 cobran ni pierden la venta. «1,000» sí son mil pesos y cobra.
    /// </summary>
    [Fact]
    public async Task Pago_con_12_50_no_cobra_ni_pierde_la_venta_y_lo_dice_bajo_pago_con()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await v.Pantalla.EsperarElementoAsync("pago-nuevo");
        int Cobros() => v.Red.Pedidas.Count(r => r == "/api/ventas");
        var antes = Cobros();

        await v.Pantalla.EscribirAsync("pago-nuevo", "12,50");
        var problema = await v.Pantalla.EsperarElementoAsync("pago-problema");
        Assert.Equal(CentavosConPunto.TrimEnd('.'), PantallaSinNavegador.Limpio(problema.Texto));
        Assert.True((await v.Pantalla.PorIdAsync("cobrar-nuevo")).Desactivado);
        // Enter en «Pagó con», y F12 con el cursor ahí (cobra como Enter): no cobran.
        await v.Pantalla.TeclaAsync("pago-nuevo", "Enter");
        await v.Pantalla.DispararAsync(await v.Pantalla.PorIdAsync("pago-nuevo"), "onfocus", new FocusEventArgs { Type = "focus" });
        Task? f12 = null;
        await v.Pantalla.Dispatcher.InvokeAsync(() => f12 = v.Venta.Tecla("F12", ""));
        await f12!;

        Assert.Equal(antes, Cobros());
        Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.DoesNotMatch(@"Venta \S+ cobrada", await v.Pantalla.TextoAsync());
        Assert.Equal("12,50", (await v.Pantalla.PorIdAsync("pago-nuevo")).Valor);

        // Con la coma de los miles, bien puesta: mil pesos, y cobra con su cambio.
        await v.Pantalla.EscribirAsync("pago-nuevo", "1,000");
        await v.Pantalla.EsperarAsync(t => t.Contains("Cambio"), "el cambio");
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "pago-problema");
        await v.Pantalla.TeclaAsync("pago-nuevo", "Enter");
        await v.Pantalla.EsperarAsync(t => Regex.IsMatch(t, @"Venta \S+ cobrada"), "el cobro");
        Assert.Equal(antes + 1, Cobros());
        var ticket = (await v.Cliente.UltimoTicketAsync(v.Caja.Id))!;
        var efectivo = Assert.Single(ticket.Cobros);
        Assert.Equal((FormaPago.Efectivo, 100_000L), (efectivo.Forma, efectivo.RecibidoCentavos));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// El fondo al abrir el turno y el efectivo del corte: «500,00» y «12,50» se piden con punto (no abren con $50,000.00 ni cierran
    /// con $1,250.00); en el corte, lo dice ahí mismo mientras se escribe y Enter no cierra. «1,500.50» sí, en los dos.
    /// </summary>
    [Fact]
    public async Task El_fondo_al_abrir_el_turno_y_el_efectivo_del_corte_piden_los_centavos_con_punto()
    {
        var c = await PrepararAsync();
        await SinTurnoAsync(c);
        TurnoDto? abierto = null;
        await using (var p = new PantallaSinNavegador(c.Servicios))
        {
            await p.AbrirAsync<AbrirTurno>(new()
            {
                [nameof(AbrirTurno.Caja)] = c.Datos,
                [nameof(AbrirTurno.Sesion)] = c.Cajero,
                [nameof(AbrirTurno.AlAbrir)] = EventCallback.Factory.Create<TurnoDto>(this, t => abierto = t),
            });
            await p.EscribirAsync("fondo", "500,00");
            await p.TeclaAsync("fondo", "Enter");
            Assert.Contains("Los centavos van con punto: 500.00 (la coma solo separa los miles: 1,250.00).", await p.TextoAsync());
            Assert.Null(abierto);
            Assert.Null(await c.Cliente.TurnoAsync(c.Datos.Id));

            await p.EscribirAsync("fondo", "1,500.50");
            await p.TeclaAsync("fondo", "Enter");
            await HastaQueAsync(() => Task.FromResult(abierto is not null), "el turno abierto");
            Assert.Equal(150_050L, abierto!.FondoCentavos);
            Assert.Empty(p.Errores);
        }

        await using (var p = new PantallaSinNavegador(c.Servicios))
        {
            await p.AbrirAsync<CorteCaja>(new()
            {
                [nameof(CorteCaja.Turno)] = abierto,
                [nameof(CorteCaja.Sesion)] = c.Encargado,
                [nameof(CorteCaja.Caja)] = c.Datos.Nombre,
            });
            await p.EsperarElementoAsync("corte-contado");
            await p.EscribirAsync("corte-contado", "12,50");
            Assert.Equal(CentavosConPunto, PantallaSinNavegador.Limpio((await p.EsperarElementoAsync("corte-problema")).Texto));
            Assert.True((await p.BotonAsync("Cerrar el turno")).Desactivado);
            await p.TeclaAsync("corte-contado", "Enter");
            Assert.Equal(abierto.Id, (await c.Cliente.TurnoAsync(c.Datos.Id))?.Id);

            await p.EscribirAsync("corte-contado", "1,500.50");
            Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "corte-problema");
            await p.TeclaAsync("corte-contado", "Enter");
            await p.EsperarAsync(t => t.Contains("Turno de Cajero de prueba cerrado"), "el corte");
            Assert.Null(await c.Cliente.TurnoAsync(c.Datos.Id));
            Assert.Contains("Contado $1,500.50", PantallaSinNavegador.Limpio(await p.TextoAsync()));
            Assert.Empty(p.Errores);
        }
    }

    /// <summary>Ctrl+E: «12,50» se pide con punto y el precio no cambia; «12.50» sí.</summary>
    [Fact]
    public async Task Ctrl_E_pide_los_centavos_con_punto_y_no_cambia_el_precio()
    {
        var c = await PrepararAsync();
        (long Precio, Guid? AutorizacionId)? cambiado = null;
        await using var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<CambiarPrecio>(new()
        {
            [nameof(CambiarPrecio.Descripcion)] = "Pluma de prueba",
            [nameof(CambiarPrecio.PrecioActualCentavos)] = 1000L,
            [nameof(CambiarPrecio.Cantidad)] = Cantidad.Piezas(1),
            [nameof(CambiarPrecio.Caja)] = c.Datos,
            [nameof(CambiarPrecio.Sesion)] = c.Dueno,
            [nameof(CambiarPrecio.AlCambiar)] = EventCallback.Factory.Create<(long, Guid?)>(this, x => cambiado = x),
        });

        await p.EscribirAsync("precio-nuevo", "12,50");
        await p.TeclaAsync("precio-nuevo", "Enter");
        Assert.Contains(CentavosConPunto, await p.TextoAsync());
        Assert.Null(cambiado);

        await p.EscribirAsync("precio-nuevo", "12.50");
        await p.TeclaAsync("precio-nuevo", "Enter");
        await HastaQueAsync(() => Task.FromResult(cambiado is not null), "el precio cambiado");
        Assert.Equal(1250L, cambiado!.Value.Precio);
        Assert.Empty(p.Errores);
    }

    /// <summary>Un precio en F3 (la ficha del producto): «12,50» se pide con punto y no se guarda; «1,250.50» sí.</summary>
    [Fact]
    public async Task Un_precio_en_la_ficha_pide_los_centavos_con_punto()
    {
        var c = await PrepararAsync();
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var codigo = $"CD{Guid.NewGuid():N}"[..14];
        var ficha = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, "Coma en la ficha de prueba", opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true,
                false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])],
                    [new PrecioFichaDto(null, 1000, 2000)]),
            ])));
        await using var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = c.Encargado,
            [nameof(Productos.Acceso)] = new PermisosDePagina(c.Encargado, null),
            [nameof(Productos.Estado)] = new ProductosVistos { Texto = codigo },
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarAsync(t => t.Contains("Últimos movimientos") && t.Contains("Coma en la ficha de prueba"), "el detalle");
        await p.OprimirAsync(await p.BotonAsync("Editar"));
        await p.EsperarAsync(t => t.Contains("Precios (con IVA)"), "la ficha");
        async Task PrecioAsync(string escrito) =>
            await p.DispararAsync((await p.LeerAsync()).Elementos.Single(e => e.Etiqueta == "input" && e.Clase == "umbral" && e.Valor is "20.00" or "12,50"),
                "onchange", new ChangeEventArgs { Value = escrito });

        await PrecioAsync("12,50");
        await p.OprimirAsync(await p.BotonAsync("Guardar"));
        await p.EsperarAsync(t => t.Contains("Revisa los precios: los centavos van con punto: 12.50 (la coma solo separa los miles: 1,250.00)."), "el error");
        Assert.Equal(2000L, (await c.Cliente.FichaProductoAsync(ficha.Id!.Value, c.Datos.SucursalId, c.Encargado.UsuarioId))!.Variantes[0].Precios[0].PrecioCentavos);

        await PrecioAsync("1,250.50");
        await p.OprimirAsync(await p.BotonAsync("Guardar"));
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "el guardado");
        Assert.Equal(125_050L, (await c.Cliente.FichaProductoAsync(ficha.Id!.Value, c.Datos.SucursalId, c.Encargado.UsuarioId))!.Variantes[0].Precios[0].PrecioCentavos);
        Assert.Empty(p.Errores);
    }
}
