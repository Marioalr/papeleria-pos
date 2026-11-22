using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La ventana «Precio y descuento» (Ctrl+D o Ctrl+E, 2026-11-22) sola, con el teclado: a todo el ticket y al artículo común solo se les
/// baja (el «Precio final» más alto lo dice y no cambia nada), y si su renglón o su ticket se van, avisa una vez para que la pantalla
/// la cierre (dueño: «la ventana del descuento se queda abierta después de quitar su producto»).
/// </summary>
public sealed class PrecioYDescuentoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private async Task<(ServiceProvider Servicios, CajaDto Caja, SesionDto Dueno)> PrepararAsync()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return (servicios, caja, await cliente.EntrarAsync("dueno", "3333"));
    }

    private static Venta NuevaVenta(CajaDto caja, SesionDto quien) =>
        new() { SucursalId = caja.SucursalId, CajaId = caja.Id, TurnoId = Guid.NewGuid(), CajeroId = quien.UsuarioId, Serie = "A" };

    private static Dictionary<string, object?> Parametros(Venta venta, VentaLinea? linea, CajaDto caja, SesionDto sesion, Action alTerminar, Action alPerder) => new()
    {
        [nameof(Descuento.Venta)] = venta,
        [nameof(Descuento.Linea)] = linea,
        [nameof(Descuento.Caja)] = caja,
        [nameof(Descuento.Sesion)] = sesion,
        [nameof(Descuento.AlTerminar)] = EventCallback.Factory.Create<string>(new object(), _ => alTerminar()),
        [nameof(Descuento.AlPerderSuRenglon)] = EventCallback.Factory.Create(new object(), alPerder),
    };

    [Fact]
    public async Task A_todo_el_ticket_y_al_articulo_comun_el_precio_final_mas_alto_no_sube_nada()
    {
        var (servicios, caja, dueno) = await PrepararAsync();
        var venta = NuevaVenta(caja, dueno);
        var comun = venta.AgregarArticuloComun("Recuerdo", 5000, Cantidad.Piezas(1));
        var terminado = 0;

        // Sin renglón: a todo el ticket.
        await using (var p = new PantallaSinNavegador(servicios))
        {
            await p.AbrirAsync<Descuento>(Parametros(venta, null, caja, dueno, () => terminado++, () => { }));
            await p.EscribirAsync("descuento-valor", "60");
            await p.TeclaAsync("descuento-valor", "Enter");
            Assert.Contains("A todo el ticket solo se le baja", await p.TextoAsync());
            Assert.Equal(0, terminado);
        }

        // Al artículo común: ya lleva el precio que se escribió. Menos sí: es descuento.
        await using (var p = new PantallaSinNavegador(servicios))
        {
            await p.AbrirAsync<Descuento>(Parametros(venta, comun, caja, dueno, () => terminado++, () => { }));
            Assert.DoesNotContain("sube el precio", await p.TextoAsync());
            await p.EscribirAsync("descuento-valor", "60");
            await p.TeclaAsync("descuento-valor", "Enter");
            Assert.Contains(Venta.ComunNoSube, await p.TextoAsync());
            Assert.False(comun.PrecioCambiado);
            Assert.Equal(5000, venta.TotalCentavos);

            await p.EscribirAsync("descuento-valor", "50");
            await p.TeclaAsync("descuento-valor", "Enter");
            Assert.Contains("Ya se cobra $50.00", await p.TextoAsync());

            await p.EscribirAsync("descuento-valor", "45");
            await p.EscribirAsync("descuento-motivo", "Detalle");
            await p.TeclaAsync("descuento-valor", "Enter");
            await p.EsperarAsync(_ => terminado == 1, "el descuento");
            Assert.Equal(4500, venta.TotalCentavos);
            Assert.Empty(p.Errores);
        }
    }

    [Fact]
    public async Task Si_su_renglon_o_su_ticket_se_van_avisa_una_sola_vez()
    {
        var (servicios, caja, dueno) = await PrepararAsync();
        var venta = NuevaVenta(caja, dueno);
        var recuerdo = venta.AgregarArticuloComun("Recuerdo", 5000, Cantidad.Piezas(1));
        venta.AgregarArticuloComun("Otro", 2000, Cantidad.Piezas(1));
        var perdidas = 0;
        var parametros = Parametros(venta, recuerdo, caja, dueno, () => { }, () => perdidas++);
        await using var p = new PantallaSinNavegador(servicios);
        var ventana = await p.AbrirAsync<Descuento>(parametros);

        // La pantalla vuelve a dibujar con lo mismo: nada.
        await p.Dispatcher.InvokeAsync(() => ventana.SetParametersAsync(ParameterView.FromDictionary(parametros)));
        Assert.Equal(0, perdidas);

        // Se quita su renglón: avisa; y otra vez lo mismo, ya no.
        venta.QuitarLinea(recuerdo);
        await p.Dispatcher.InvokeAsync(() => ventana.SetParametersAsync(ParameterView.FromDictionary(parametros)));
        Assert.Equal(1, perdidas);
        await p.Dispatcher.InvokeAsync(() => ventana.SetParametersAsync(ParameterView.FromDictionary(parametros)));
        Assert.Equal(1, perdidas);

        // A todo el ticket: el ticket que cambia (F6, F5, cobrado) también.
        var otraVenta = NuevaVenta(caja, dueno);
        otraVenta.AgregarArticuloComun("Recuerdo", 5000, Cantidad.Piezas(1));
        var delTicket = Parametros(otraVenta, null, caja, dueno, () => { }, () => perdidas++);
        await using var q = new PantallaSinNavegador(servicios);
        var deTodo = await q.AbrirAsync<Descuento>(delTicket);
        delTicket[nameof(Descuento.Venta)] = NuevaVenta(caja, dueno);
        await q.Dispatcher.InvokeAsync(() => deTodo.SetParametersAsync(ParameterView.FromDictionary(delTicket)));
        Assert.Equal(2, perdidas);
        Assert.Empty(p.Errores);
        Assert.Empty(q.Errores);
    }
}
