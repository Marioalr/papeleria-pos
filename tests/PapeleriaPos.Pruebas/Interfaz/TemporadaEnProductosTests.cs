using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Pruebas.Servicio;
using static PapeleriaPos.Pruebas.Servicio.ServicioDeTemporadas;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Productos (F3) › Temporadas y Tarjetas de precio, y el verificador que cuenta lo que se pregunta (entrega 13, parte 3), sin
/// navegador, contra el servicio con la hora fija (17 de octubre de 2040): la lista del año pasado con sus precios y los que no
/// tienen foto, confirmar con el teclado y los botones, el costo solo para el dueño, la hoja y las tarjetas que se ven y se
/// mandan a imprimir, terminar con el conteo (y sin él en el piloto con eleventa) y las tarjetas que cambiaron de precio.
/// </summary>
public sealed class TemporadaEnProductosTests(ServicioDeTemporadas servicio) : IClassFixture<ServicioDeTemporadas>
{
    private sealed record Caja(ClienteCaja Cliente, HttpClient Http, CajaDto Datos, ServiceProvider Servicios, PaginaQueContesta Pagina,
        SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

    private static async Task<Caja> PrepararAsync(WebApplicationFactory<Program> fabrica)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).First();
        conexion.CajaId = caja.Id;
        var pagina = new PaginaQueContesta();
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IJSRuntime>(pagina)
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, fabrica.CreateClient(), caja, servicios, pagina, await cliente.EntrarAsync("cajero", "1111"),
            await cliente.EntrarAsync("encargado", "2222"), await cliente.EntrarAsync("dueno", "3333"));
    }

    /// <summary>La página de Productos de quien entró, como la abre F3, con su lista ya cargada.</summary>
    private static async Task<(PantallaSinNavegador Pantalla, Productos Productos)> ProductosAsync(Caja c, SesionDto quien)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        var productos = await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = quien,
            [nameof(Productos.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Productos.Estado)] = new ProductosVistos(),
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarElementoAsync("prod-0");
        return (p, productos);
    }

    /// <summary>Productos › Temporadas, en esa temporada y ese paso.</summary>
    private static async Task AbrirTemporadaAsync(PantallaSinNavegador p, Guid temporada, PasoDeTemporada paso = PasoDeTemporada.Precios)
    {
        await p.OprimirAsync(await p.PorIdAsync("ver-temporadas"));
        await p.OprimirAsync(await p.EsperarElementoAsync($"temporada-{temporada}"));
        if (paso != PasoDeTemporada.Precios)
            await p.OprimirAsync(await p.EsperarElementoAsync($"paso-{paso}"));
    }

    private static async Task<string> RenglonAsync(PantallaSinNavegador p, string id) =>
        PantallaSinNavegador.Limpio((await p.EsperarElementoAsync(id)).Texto);

    /// <summary>Lo último que se le mandó a un marco de vista previa (el HTML de la hoja o de las tarjetas).</summary>
    private static string? EnElMarco(Caja c, string marco) =>
        c.Pagina.Llamadas.LastOrDefault(l => l.Funcion == "mostrarEnMarco" && Equals(l.Argumentos?[0], marco)).Argumentos?[1] as string;

    private static bool SeImprimio(Caja c, string marco) => c.Pagina.Llamadas.Any(l => l.Funcion == "imprimirMarco" && Equals(l.Argumentos?[0], marco));

    private static async Task EsperarAsync(Func<Task<bool>> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!await condicion())
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No llegó a {que}.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task El_encargado_empieza_la_temporada_con_la_lista_del_anio_pasado_y_confirma_los_precios_con_el_teclado()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var departamento = await servicio.DepartamentoAsync();
        var vela = await servicio.ProductoAsync(departamento, "Vela de la pantalla", 4500, El(2039, 9, 1), codigo: Codigo("VPA"));
        await servicio.CambiarPrecioAsync(vela, 5000, El(2040, 8, 1));
        var copal = await servicio.ProductoAsync(departamento, "Copal de la pantalla", 1000, El(2039, 9, 1), codigo: Codigo("CPA"), escalas: [(12, 900)]);
        var temporada = await ServicioDeTemporadas.TemporadaAsync(c.Http, c.Dueno.UsuarioId, c.Datos.SucursalId, Del16DeSeptiembre, Al5DeNoviembre, vela, copal);
        var (p, productos) = await ProductosAsync(c, c.Encargado);
        await using var _ = p;

        await AbrirTemporadaAsync(p, temporada);

        var renglon = await RenglonAsync(p, $"temp-{vela}");
        // El de entonces ($45, de su historial), el de hoy ($50) y que no tiene foto; el encargado no ve el costo.
        Assert.Contains("Vela de la pantalla", renglon);
        Assert.Contains("sin foto", renglon);
        Assert.Contains("$45.00 $50.00", renglon);
        var texto = await p.TextoAsync();
        Assert.Contains("2 productos no tienen foto", texto);
        Assert.DoesNotContain("Sugerido", texto);
        Assert.Contains("El costo, el margen y el sugerido los ve quien tiene «Ver costos, utilidad y margen».", texto);
        Assert.Contains("mayoreo desde 12: $9.00", await RenglonAsync(p, $"temp-{copal}"));

        // Se escribe el de la vela, Enter pasa al siguiente; el copal a $8.50 quedaría abajo de su mayoreo: se marca y no se manda.
        await p.EscribirAsync("precio-0", "55");
        await p.TeclaAsync("precio-0", "Enter");
        Assert.Contains("#precio-1", c.Pagina.Llamadas.Where(l => l.Funcion == "enfocar").Select(l => l.Argumentos?[0] as string));
        Assert.Contains("cambia", (await p.PorIdAsync($"temp-{vela}")).Clase);
        await p.EscribirAsync("precio-1", "8.50");
        Assert.Contains("más barato que su mayoreo", await RenglonAsync(p, $"temp-{copal}"));
        await p.OprimirAsync(await p.PorIdAsync("confirmar-precios"));
        await p.EsperarAsync(t => t.Contains("Revisa los precios marcados en rojo"), "el aviso de los precios en rojo");
        Assert.Equal((5000L, 1000L), (await servicio.MenudeoAsync(vela), await servicio.MenudeoAsync(copal)));

        // El copal como estaba («Dejar los de hoy» lo regresa todo; se vuelve a escribir la vela) y Ctrl+Enter confirma.
        await p.OprimirAsync(await p.BotonAsync("Dejar los de hoy"));
        await p.EscribirAsync("precio-0", "55");
        await p.DispararAsync(await p.PorIdAsync("precio-0"), "onkeydown", new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter", CtrlKey = true });
        await p.EsperarAsync(t => t.Contains("Listo: cambió 1 precio; las cajas ya lo tienen."), "la confirmación");

        Assert.Equal((5500L, 1000L), (await servicio.MenudeoAsync(vela), await servicio.MenudeoAsync(copal)));
        Assert.Contains("Encargado de prueba confirmó los precios el 17/10/2040", await p.TextoAsync());
        // Esc regresa al producto.
        Assert.True(await p.Dispatcher.InvokeAsync(productos.CerrarAsync));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "confirmar-precios");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task El_duenio_ve_el_costo_y_el_margen_y_pone_los_sugeridos()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var departamento = await servicio.DepartamentoAsync();
        var esfera = await servicio.ProductoAsync(departamento, "Esfera del dueño", 2500, El(2039, 9, 1), codigo: Codigo("ESD"));
        await servicio.MovimientoAsync(c.Datos.SucursalId, esfera, TipoMovimiento.Compra, 10, El(2040, 9, 1));
        await using (var db = servicio.Base())
        {
            // Las 10 de la compra costaron $150: $15 cada una.
            var existencia = await db.Existencias.SingleAsync(e => e.SucursalId == c.Datos.SucursalId && e.VarianteId == esfera);
            Assert.Equal(15_000, existencia.CostoCentavos);
        }
        var temporada = await ServicioDeTemporadas.TemporadaAsync(c.Http, c.Dueno.UsuarioId, c.Datos.SucursalId, Del16DeSeptiembre, Al5DeNoviembre, esfera);
        var (p, _) = await ProductosAsync(c, c.Dueno);
        await using var __ = p;

        await AbrirTemporadaAsync(p, temporada);

        // Con $25 y un costo de $15 deja 40 %, arriba del mínimo (30 %): el sugerido es el de hoy.
        var renglon = await RenglonAsync(p, $"temp-{esfera}");
        Assert.Contains("$25.00 $15.00 40 % $25.00", renglon);
        Assert.Contains("Sugerido", await p.TextoAsync());
        await servicio.CambiarPrecioAsync(esfera, 1800, ServicioDeTemporadas.Hoy);
        await p.OprimirAsync(await p.PorIdAsync($"paso-{PasoDeTemporada.Hoja}"));
        await p.OprimirAsync(await p.PorIdAsync($"paso-{PasoDeTemporada.Precios}"));
        // Rebajado a $18 deja 16.7 %, abajo del mínimo: el sugerido ($25, el del año pasado) es más que el del 30 % ($21.50).
        await p.EsperarAsync(t => t.Contains("$18.00"), "el precio nuevo");
        Assert.Contains("$25.00 $18.00 $15.00 16.7 % $25.00", await RenglonAsync(p, $"temp-{esfera}"));
        await p.OprimirAsync(await p.BotonAsync("Todos al sugerido"));
        Assert.Equal("25.00", (await p.PorIdAsync("precio-0")).Valor);
        await p.OprimirAsync(await p.PorIdAsync("confirmar-precios"));
        await p.EsperarAsync(t => t.Contains("Listo: cambió 1 precio"), "la confirmación");
        Assert.Equal(2500, await servicio.MenudeoAsync(esfera));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task La_hoja_y_las_tarjetas_de_la_mesa_se_ven_y_se_mandan_a_imprimir()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var departamento = await servicio.DepartamentoAsync();
        var conFoto = await servicio.ProductoAsync(departamento, "Calavera de la hoja", 3500, El(2040, 1, 1), codigo: Codigo("CHO"));
        var sinCodigo = await servicio.ProductoAsync(departamento, "Máscara de la hoja", 2800, El(2040, 1, 1));
        await using (var db = servicio.Base())
        {
            db.FotosProducto.Add(new FotoProducto { VarianteId = conFoto, Datos = [0xFF, 0xD8, 0xFF, 0x01], Huella = "00aa11bb22cc33dd", Ancho = 1, Alto = 1 });
            await db.SaveChangesAsync();
        }
        var temporada = await ServicioDeTemporadas.TemporadaAsync(c.Http, c.Dueno.UsuarioId, c.Datos.SucursalId, Del16DeSeptiembre, Al5DeNoviembre, conFoto, sinCodigo);
        var (p, _) = await ProductosAsync(c, c.Encargado);
        await using var __ = p;

        await AbrirTemporadaAsync(p, temporada, PasoDeTemporada.Hoja);
        await p.OprimirAsync(await p.EsperarElementoAsync("vista-hoja"));
        await p.EsperarAsync(t => t.Contains("2 productos en 1 hoja; 1 sin foto llevan sus iniciales."), "la vista previa de la hoja");

        var hoja = EnElMarco(c, "#marco-temporada")!;
        Assert.Contains("Calavera de la hoja", hoja);
        Assert.Contains("Máscara de la hoja", hoja);
        Assert.Contains("Mesa de la entrada", hoja);
        Assert.Contains($"data:image/jpeg;base64,{Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0x01 })}", hoja);
        Assert.Contains(">MH<", hoja);
        Assert.Contains("A 1 se les dio código interno", await p.TextoAsync());
        Assert.False(SeImprimio(c, "#marco-temporada"));
        await p.OprimirAsync(await p.PorIdAsync("imprimir-hoja"));
        await EsperarAsync(() => Task.FromResult(SeImprimio(c, "#marco-temporada")), "la hoja a la impresora");

        // Las tarjetas de la mesa: las dos van marcadas (no tienen); salen con su precio de hoy y quedan anotadas.
        await p.OprimirAsync(await p.PorIdAsync($"paso-{PasoDeTemporada.Tarjetas}"));
        Assert.Contains("sin tarjeta", await RenglonAsync(p, $"tarjeta-{conFoto}"));
        await p.OprimirAsync(await p.PorIdAsync("imprimir-tarjetas"));
        await p.EsperarAsync(t => t.Contains("Listo: 2 tarjetas a la impresora."), "las tarjetas");

        Assert.True(SeImprimio(c, "#marco-tarjetas-temporada"));
        var tarjetas = EnElMarco(c, "#marco-tarjetas-temporada")!;
        Assert.Contains(">$35.00<", tarjetas);
        Assert.Contains(">$28.00<", tarjetas);
        await p.EsperarAsync(t => !t.Contains("sin tarjeta"), "las tarjetas anotadas");
        Assert.Contains("$35.00 $35.00", await RenglonAsync(p, $"tarjeta-{conFoto}"));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Productos_dice_que_tarjetas_volver_a_imprimir_y_lo_mas_preguntado_sin_tarjeta()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var departamento = await servicio.DepartamentoAsync();
        var calculadora = await servicio.ProductoAsync(departamento, "Calculadora de la vitrina", 8500, El(2040, 1, 1), codigo: Codigo("CVI"));
        var preguntada = await servicio.ProductoAsync(departamento, "Memoria que se pregunta", 13900, El(2040, 1, 1), codigo: Codigo("MPR"));
        var codigo = (await c.Cliente.PrepararTarjetasAsync(new SolicitudPrepararTarjetas(c.Encargado.UsuarioId, c.Datos.SucursalId, [calculadora]))).Tarjetas;
        await c.Cliente.TarjetasImpresasAsync(new SolicitudTarjetasImpresas(c.Encargado.UsuarioId, c.Datos.SucursalId, codigo));
        await servicio.CambiarPrecioAsync(calculadora, 9000, ServicioDeTemporadas.Hoy);
        await c.Cliente.ContarConsultaAsync(c.Datos.SucursalId, preguntada);
        await c.Cliente.ContarConsultaAsync(c.Datos.SucursalId, preguntada);
        var (p, _) = await ProductosAsync(c, c.Encargado);
        await using var __ = p;

        // Abajo de la lista, el botón ya dice cuántas hay que volver a imprimir.
        await p.EsperarAsync(t => t.Contains("por reimprimir"), "el botón con las tarjetas por reimprimir");
        await p.OprimirAsync(await p.PorIdAsync("ver-tarjetas"));

        // Abre en «Por reimprimir»: la calculadora salió a $85 y hoy vale $90.
        var renglon = await RenglonAsync(p, $"tarjeta-{calculadora}");
        Assert.Contains("$85.00 cambió el precio $90.00", renglon);
        await p.OprimirAsync(await p.PorIdAsync("imprimir-tarjetas"));
        await p.EsperarAsync(t => t.Contains("tarjeta") && t.Contains("a la impresora."), "la reimpresión");
        await EsperarAsync(async () => !(await p.LeerAsync()).Elementos.Any(e => e.Id == $"tarjeta-{calculadora}"), "que salga de por reimprimir");
        Assert.Contains(">$90.00<", EnElMarco(c, "#marco-tarjetas")!);
        Assert.DoesNotContain((await c.Cliente.TarjetasAsync(c.Datos.SucursalId)).PorReimprimir, t => t.VarianteId == calculadora);

        // Lo más preguntado sin tarjeta: la memoria, dos veces; empieza sin marcar.
        await p.OprimirAsync(await p.PorIdAsync("lista-preguntado"));
        Assert.Contains("Memoria que se pregunta", await RenglonAsync(p, $"tarjeta-{preguntada}"));
        Assert.Contains("2 veces", await RenglonAsync(p, $"tarjeta-{preguntada}"));
        Assert.True((await p.PorIdAsync("imprimir-tarjetas")).Desactivado);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task El_verificador_cuenta_cada_consulta_escaneada_o_tocada_en_el_mostrador()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var departamento = await servicio.DepartamentoAsync();
        var codigo = Codigo("VER");
        var compas = await servicio.ProductoAsync(departamento, "Compás del verificador", 12000, El(2040, 1, 1), codigo: codigo);
        await servicio.CatalogoAlDiaAsync();
        async Task<int> Consultas()
        {
            await using var db = servicio.Base();
            return await db.ConsultasDePrecio.CountAsync(x => x.VarianteId == compas);
        }

        var p = new PantallaSinNavegador(c.Servicios);
        await using (p)
        {
            await p.AbrirAsync<Verificador>(new() { [nameof(Verificador.SucursalId)] = c.Datos.SucursalId });
            await p.EscribirAsync("verificador", codigo);
            await p.TeclaAsync("verificador", "Enter");
            await p.EsperarAsync(t => t.Contains("Compás del verificador") && t.Contains("$120.00"), "el precio del compás");
            await EsperarAsync(async () => await Consultas() == 1, "la consulta escaneada");
            Assert.Empty(p.Errores);
        }

        // Tocarlo en Ctrl+M con el verificador: también es una consulta.
        var producto = await c.Cliente.PorCodigoAsync(codigo, c.Datos.SucursalId);
        var otra = new PantallaSinNavegador(c.Servicios);
        await using (otra)
        {
            await otra.AbrirAsync<Verificador>(new() { [nameof(Verificador.SucursalId)] = c.Datos.SucursalId, [nameof(Verificador.Producto)] = producto });
            await EsperarAsync(async () => await Consultas() == 2, "la consulta tocada");
        }
        // Lo que no existe no cuenta.
        var tercera = new PantallaSinNavegador(c.Servicios);
        await using (tercera)
        {
            await tercera.AbrirAsync<Verificador>(new() { [nameof(Verificador.SucursalId)] = c.Datos.SucursalId });
            await tercera.EscribirAsync("verificador", "NO-EXISTE-123");
            await tercera.TeclaAsync("verificador", "Enter");
            await tercera.EsperarAsync(t => t.Contains("No existe el código"), "el aviso");
        }
        Assert.Equal(2, await Consultas());
        await using (var db = servicio.Base())
            Assert.All(await db.ConsultasDePrecio.Where(x => x.VarianteId == compas).ToListAsync(), x => Assert.Equal(c.Datos.Id, x.CajaId));
    }

    [Fact]
    public async Task Al_terminar_se_cuenta_lo_que_sobro_y_queda_el_resumen()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var departamento = await servicio.DepartamentoAsync();
        var vela = await servicio.ProductoAsync(departamento, "Vela para terminar", 2000, El(2040, 1, 1), codigo: Codigo("VPT"));
        await servicio.MovimientoAsync(c.Datos.SucursalId, vela, TipoMovimiento.InventarioInicial, 12, El(2040, 9, 1));
        await servicio.MovimientoAsync(c.Datos.SucursalId, vela, TipoMovimiento.Venta, 12, El(2040, 10, 1));
        var temporada = await ServicioDeTemporadas.TemporadaAsync(c.Http, c.Dueno.UsuarioId, c.Datos.SucursalId, Del16DeSeptiembre, Al5DeNoviembre, vela);
        var (p, productos) = await ProductosAsync(c, c.Encargado);
        await using var _ = p;

        await AbrirTemporadaAsync(p, temporada, PasoDeTemporada.Terminar);

        // Había 12 y se vendieron todas: «se acabó». Pero en la mesa quedó una que no se cobró: se cuenta 1.
        Assert.Contains("12 12 0 se acabó", await RenglonAsync(p, $"resumen-{vela}"));
        await p.EscribirAsync("contado-0", "1");
        await p.OprimirAsync(await p.PorIdAsync("terminar"));
        await p.EsperarAsync(t => t.Contains("lo contado reemplaza la existencia de 1 producto"), "la pregunta");
        // Esc la deja sin terminar.
        Assert.True(await p.Dispatcher.InvokeAsync(productos.CerrarAsync));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "terminar-si");
        await p.OprimirAsync(await p.PorIdAsync("terminar"));
        await p.OprimirAsync(await p.EsperarElementoAsync("terminar-si"));
        await p.EsperarAsync(t => t.Contains("Listo: terminada."), "la temporada terminada");

        Assert.Contains("Encargado de prueba la terminó y contó lo que sobró el 17/10/2040", await p.TextoAsync());
        Assert.Contains("12 12 1", await RenglonAsync(p, $"resumen-{vela}"));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id is "terminar" or "contado-0");
        await using (var db = servicio.Base())
            Assert.Equal(1m, (await db.Existencias.SingleAsync(e => e.SucursalId == c.Datos.SucursalId && e.VarianteId == vela)).Cantidad.EnUnidades);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_el_piloto_terminar_no_ofrece_contar_y_dice_por_que()
    {
        var c = await PrepararAsync(servicio.Piloto());
        var departamento = await servicio.DepartamentoAsync();
        var papel = await servicio.ProductoAsync(departamento, "Papel china del piloto", 300, El(2040, 1, 1), codigo: Codigo("PCP"));
        await using (var db = servicio.Base())
        {
            db.MapeosImportacion.Add(new MapeoImportacion { Origen = "eleventa", Entidad = nameof(Variante), IdOrigen = Random.Shared.Next(100_000, 999_999).ToString(), IdNuevo = papel });
            await db.SaveChangesAsync();
        }
        await servicio.MovimientoAsync(c.Datos.SucursalId, papel, TipoMovimiento.InventarioInicial, 40, El(2040, 9, 1));
        var temporada = await ServicioDeTemporadas.TemporadaAsync(c.Http, c.Dueno.UsuarioId, c.Datos.SucursalId, Del16DeSeptiembre, Al5DeNoviembre, papel);
        var (p, _) = await ProductosAsync(c, c.Encargado);
        await using var __ = p;

        // Los precios: el de eleventa no se escribe aquí (se cambia allá) y la página lo dice.
        await AbrirTemporadaAsync(p, temporada);
        Assert.Contains("de eleventa", await RenglonAsync(p, $"temp-{papel}"));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "precio-0");
        Assert.Contains(TrastiendaEnElPiloto.ProductosEnEleventa, await p.TextoAsync());

        await p.OprimirAsync(await p.PorIdAsync($"paso-{PasoDeTemporada.Terminar}"));
        await p.EsperarElementoAsync($"resumen-{papel}");
        Assert.Contains(TrastiendaEnElPiloto.InventarioEnEleventa, await p.TextoAsync());
        Assert.Contains("Por eso aquí no se cuenta", await p.TextoAsync());
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "contado-0");
        await p.OprimirAsync(await p.PorIdAsync("terminar"));
        await p.OprimirAsync(await p.EsperarElementoAsync("terminar-si"));
        await p.EsperarAsync(t => t.Contains("Listo: terminada."), "la temporada terminada");
        Assert.Contains("Encargado de prueba la terminó el 17/10/2040", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }
}
