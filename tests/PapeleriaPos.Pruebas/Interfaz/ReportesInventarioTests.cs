using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Los reportes del inventario como en eleventa (2026-10-17), sin navegador: sus tarjetas en Inventario (F4) según los
/// permisos, lo que vale por departamento, los movimientos con sus filtros y sus páginas, lo que está en su mínimo con cuánto
/// pedir, y el mínimo y el máximo en la ficha de Productos (F3).
/// </summary>
public sealed class ReportesInventarioTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

    private async Task<Caja> PrepararAsync()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(servicio.Fabrica.CreateClient(), cliente, caja, servicios, await cliente.EntrarAsync("cajero", "1111"),
            await cliente.EntrarAsync("encargado", "2222"), await cliente.EntrarAsync("dueno", "3333"));
    }

    /// <summary>Un producto nuevo con su código, y <paramref name="existencia"/> piezas a $10 como inventario inicial.</summary>
    private async Task<FichaProductoDto> ProductoAsync(Caja c, string nombre, int existencia, MinimoYMaximoDto? minimoYMaximo = null, Guid? departamento = null)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var ficha = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, departamento ?? opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                    [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto($"RI{Guid.NewGuid():N}"[..14])])], [new PrecioFichaDto(null, 1000, 2000)],
                    MinimoYMaximo: minimoYMaximo),
            ])));
        if (existencia != 0)
        {
            await using var db = servicio.Base();
            var variante = await db.Variantes.SingleAsync(v => v.Id == ficha.Variantes[0].Id);
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(c.Datos.SucursalId, variante, TipoMovimiento.InventarioInicial,
                Cantidad.Piezas(existencia), existencia * 1000L, DateTime.UtcNow, documentoTipo: OrigenDelMovimiento.ImportacionEleventa));
        }
        return ficha;
    }

    private static string Codigo(FichaProductoDto ficha) => ficha.Variantes[0].Presentaciones[0].Codigos[0].Codigo;

    /// <summary>El cajero vende <paramref name="piezas"/> (con su turno): el folio.</summary>
    private static async Task<string> VenderAsync(Caja c, FichaProductoDto producto, int piezas)
    {
        var turno = await c.Cliente.TurnoAsync(c.Datos.Id) ?? await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        var variante = producto.Variantes[0];
        var respuesta = await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, turno.Id, turno.CajeroId,
            [new LineaSolicitada(variante.Id, variante.Presentaciones[0].Id, piezas * 1000L)], [new CobroSolicitado(FormaPago.Efectivo, 1_000_000)]));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<TicketDto>())!.Folio;
    }

    private static async Task<PantallaSinNavegador> InventarioAsync(Caja c, SesionDto quien, TareaInventario? inicial = null)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Inventario>(new()
        {
            [nameof(Inventario.Caja)] = c.Datos,
            [nameof(Inventario.Sesion)] = quien,
            [nameof(Inventario.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Inventario.EtiquetasPorImprimir)] = new EtiquetasPorImprimir(),
            [nameof(Inventario.TareaInicial)] = inicial,
        });
        return p;
    }

    private static async Task<List<string>> TarjetasAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos.Where(e => e.Id?.StartsWith("tarea-", StringComparison.Ordinal) == true).Select(e => e.Id!["tarea-".Length..]).ToList();

    private static async Task CambiarAsync(PantallaSinNavegador p, string id, string valor) =>
        await p.DispararAsync(await p.PorIdAsync(id), "onchange", new ChangeEventArgs { Value = valor });

    [Fact]
    public async Task Las_tarjetas_de_los_reportes_salen_con_su_permiso_y_bajos_dice_cuantos_estan_en_su_minimo()
    {
        var c = await PrepararAsync();
        await ProductoAsync(c, "Tarjeta de prueba bajo", 1, new MinimoYMaximoDto(5000, null));
        var pendientes = await c.Cliente.PendientesInventarioAsync(c.Datos.SucursalId);

        await using (var p = await InventarioAsync(c, c.Dueno))
        {
            await p.EsperarAsync(t => t.Contains("en su mínimo o debajo"), "lo pendiente de bajos");
            var tarjetas = await TarjetasAsync(p);
            // Lo que vale es costo: solo con «Ver costos, utilidad y margen» (el dueño). Va al final, después de los movimientos.
            Assert.Equal(["Movimientos", "Valor"], tarjetas.TakeLast(2));
            Assert.Contains("Bajos", tarjetas);
            Assert.True(pendientes.BajosEnInventario > 0);
            var bajos = PantallaSinNavegador.Limpio((await p.PorIdAsync("tarea-Bajos")).Texto);
            Assert.Contains($"{Formato.Numero(pendientes.BajosEnInventario)} en su mínimo o debajo", bajos);
            Assert.DoesNotContain("lo autoriza", bajos);
            Assert.Empty(p.Errores);
        }
        await using (var p = await InventarioAsync(c, c.Encargado))
        {
            await p.EsperarAsync(t => t.Contains("en su mínimo o debajo"), "lo pendiente de bajos");
            var tarjetas = await TarjetasAsync(p);
            Assert.Contains("Bajos", tarjetas);
            Assert.Contains("Movimientos", tarjetas);
            Assert.DoesNotContain("Valor", tarjetas);
            Assert.Empty(p.Errores);
        }
        await using (var p = await InventarioAsync(c, c.Cajero))
        {
            await p.EsperarElementoAsync("tarea-Recibir");
            // El cajero no lleva el inventario: no ve los reportes.
            Assert.Empty((await TarjetasAsync(p)).Intersect(["Bajos", "Movimientos", "Valor"]));
            Assert.Empty(p.Errores);
        }
    }

    [Fact]
    public async Task Lo_que_vale_el_inventario_por_departamento_y_los_productos_del_que_se_elige()
    {
        var c = await PrepararAsync();
        var departamento = (await c.Cliente.OpcionesCatalogoAsync()).Departamentos[^1];
        var negativo = await ProductoAsync(c, "Valor de prueba en negativo", 0, departamento: departamento.Id);
        await VenderAsync(c, negativo, 2);
        var valor = await c.Cliente.ValorInventarioAsync(c.Datos.SucursalId, c.Dueno.UsuarioId, null, FiltroValor.ConExistencia, 50);
        await using var p = await InventarioAsync(c, c.Dueno, TareaInventario.Valor);

        await p.EsperarAsync(t => t.Contains("Vale a costo"), "lo que vale");
        Assert.Equal(Formato.Dinero(valor.Total.ValorCentavos), PantallaSinNavegador.Limpio((await p.PorIdAsync("valor-total")).Texto));
        Assert.Contains("Todos los productos", await p.TextoAsync());
        // El nombre de un departamento enseña los suyos; «En negativo», lo que quedó en negativo.
        await p.OprimirAsync(await p.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == departamento.Nombre, "el departamento"));
        await p.EsperarAsync(t => t.Contains($"Productos de «{departamento.Nombre}»"), "los productos del departamento");
        await p.OprimirAsync(await p.BotonAsync("En negativo"));
        await p.EsperarAsync(t => t.Contains("Valor de prueba en negativo"), "el producto en negativo");
        Assert.Contains("-2", (await p.LeerAsync()).Elementos.Where(e => e.Etiqueta == "td").Select(e => PantallaSinNavegador.Limpio(e.Texto)));
        // «← Inventario» regresa a las tarjetas.
        await p.OprimirAsync(await p.BotonAsync("← Inventario"));
        await p.EsperarAsync(t => t.Contains("Solo las tareas que tienes"), "las tarjetas");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Movimientos_dice_el_folio_y_quien_se_filtran_y_van_por_paginas()
    {
        var c = await PrepararAsync();
        var producto = await ProductoAsync(c, "Movimiento de prueba pantalla", 10);
        var folio = await VenderAsync(c, producto, 2);
        await c.Cliente.AjustarAsync(new SolicitudAjuste(c.Encargado.UsuarioId, c.Datos.SucursalId, producto.Variantes[0].Id!.Value, 7000, "Conté el anaquel"));
        await using var p = await InventarioAsync(c, c.Encargado, TareaInventario.Movimientos);
        await p.EsperarElementoAsync("mov-buscar");

        // Los últimos 7 días (así no importa si se cruzó la medianoche) y el código del producto, como lo escanea el lector.
        await p.OprimirAsync(await p.BotonAsync("7 días"));
        await p.EscribirAsync("mov-buscar", Codigo(producto));
        await p.TeclaAsync("mov-buscar", "Enter");
        await p.EsperarAsync(t => t.Contains($"Venta {folio}") && t.Contains("3 movimientos"), "los movimientos del producto");
        var texto = await p.TextoAsync();
        Assert.Contains("Cajero de prueba", texto);
        Assert.Contains("Encargado de prueba", texto);
        Assert.Contains("Conté el anaquel", texto);
        Assert.Contains("Inventario inicial de la importación de eleventa", PantallaSinNavegador.Limpio(texto));
        // Sin «Ver costos, utilidad y margen», sin la columna del costo.
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Etiqueta == "th" && e.Texto == "Costo");
        Assert.Contains("El costo de cada movimiento lo ve quien tiene el permiso", texto);

        // Solo los ajustes.
        await CambiarAsync(p, "mov-tipo", nameof(ClaseMovimiento.Ajuste));
        await p.EsperarAsync(t => t.Contains("1 movimiento") && !t.Contains($"Venta {folio}"), "solo el ajuste");

        // Todos los de un día de muchos (el inventario inicial de la papelería ficticia): de 50 en 50.
        await CambiarAsync(p, "mov-tipo", "");
        await p.EscribirAsync("mov-buscar", "");
        await p.TeclaAsync("mov-buscar", "Enter");
        await CambiarAsync(p, "mov-desde", "2026-09-30");
        await CambiarAsync(p, "mov-hasta", "2026-09-30");
        await p.EsperarAsync(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"1–50 de [\d,]+ movimientos"), "la primera página");
        Assert.True((await p.BotonAsync("← Más recientes")).Desactivado);
        await p.OprimirAsync(await p.BotonAsync("Anteriores →"));
        await p.EsperarAsync(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"51–100 de [\d,]+ movimientos"), "la segunda página");
        Assert.False((await p.BotonAsync("← Más recientes")).Desactivado);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Bajos_en_inventario_dice_su_minimo_su_maximo_y_cuanto_pedir()
    {
        var c = await PrepararAsync();
        var producto = await ProductoAsync(c, "Bajo de prueba pantalla", 4, new MinimoYMaximoDto(5000, 20000));
        await using var p = await InventarioAsync(c, c.Encargado, TareaInventario.Bajos);
        await p.EsperarElementoAsync("bajos-buscar");

        await p.EscribirAsync("bajos-buscar", Codigo(producto));
        await p.TeclaAsync("bajos-buscar", "Enter");
        await p.EsperarAsync(t => t.Contains("Bajo de prueba pantalla") && System.Text.RegularExpressions.Regex.IsMatch(t, @"(?<!\d)1 producto(?!s)"),
            "el producto");
        var elementos = (await p.LeerAsync()).Elementos;
        var renglon = elementos.FindIndex(e => e.Etiqueta == "tr" && e.Texto.Contains("Bajo de prueba pantalla"));
        var celdas = elementos.Skip(renglon + 1).Where(e => e.Etiqueta == "td").Take(6).Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        // Hay 4, su mínimo es 5 y su máximo 20: pedir 16. Nada viene en camino.
        Assert.Equal(["4", "5", "20", "—", "16"], celdas.Skip(1));
        await p.OprimirAsync(await p.BotonAsync("Con mínimo propio"));
        await p.EsperarAsync(t => t.Contains("Bajo de prueba pantalla"), "con su mínimo propio");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task La_ficha_pone_el_minimo_y_el_maximo_y_Productos_los_dice()
    {
        var c = await PrepararAsync();
        var producto = await ProductoAsync(c, "Ficha de prueba mínimo", 4);
        await using var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = c.Encargado,
            [nameof(Productos.Acceso)] = new PermisosDePagina(c.Encargado, null),
            [nameof(Productos.Estado)] = new ProductosVistos { Texto = Codigo(producto) },
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarAsync(t => t.Contains("Últimos movimientos") && t.Contains("Ficha de prueba mínimo"), "el detalle");
        await p.OprimirAsync(await p.BotonAsync("Editar"));
        await p.EsperarAsync(t => t.Contains("Mínimo de inventario"), "la ficha");
        ElementoDibujado Campo(List<ElementoDibujado> elementos, string clase) =>
            elementos.Single(e => e.Etiqueta == "input" && e.Clase?.Split(' ').Contains(clase) == true);

        // Algo que no es cantidad no se manda.
        await p.DispararAsync(Campo((await p.LeerAsync()).Elementos, "minimo"), "onchange", new ChangeEventArgs { Value = "cinco" });
        await p.OprimirAsync(await p.BotonAsync("Guardar"));
        await p.EsperarAsync(t => t.Contains("Revisa el mínimo y el máximo de «Ficha de prueba mínimo»: una cantidad, o vacío."), "el error");
        // El máximo debajo del mínimo lo dice el servicio.
        await p.DispararAsync(Campo((await p.LeerAsync()).Elementos, "minimo"), "onchange", new ChangeEventArgs { Value = "5" });
        await p.DispararAsync(Campo((await p.LeerAsync()).Elementos, "maximo"), "onchange", new ChangeEventArgs { Value = "2" });
        await p.OprimirAsync(await p.BotonAsync("Guardar"));
        await p.EsperarAsync(t => t.Contains("El máximo de «Ficha de prueba mínimo» no puede ser menor que su mínimo."), "el error del servicio");
        await p.DispararAsync(Campo((await p.LeerAsync()).Elementos, "maximo"), "onchange", new ChangeEventArgs { Value = "20" });
        await p.OprimirAsync(await p.BotonAsync("Guardar"));
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "el guardado");

        var guardada = await c.Cliente.FichaProductoAsync(producto.Id!.Value, c.Datos.SucursalId, c.Encargado.UsuarioId);
        Assert.Equal(new MinimoYMaximoDto(5000, 20000), guardada!.Variantes[0].MinimoYMaximo);
        // De regreso al producto: su existencia (4) está en su mínimo o debajo y dice su mínimo y su máximo.
        await p.OprimirAsync(await p.BotonAsync("Volver al producto"));
        await p.EsperarAsync(t => t.Contains("según el kardex · mínimo 5 · máximo 20"), "el mínimo en el detalle");
        Assert.Contains((await p.LeerAsync()).Elementos, e => e.Etiqueta == "strong" && e.Clase == "bajo" && PantallaSinNavegador.Limpio(e.Texto) == "4");
        Assert.Empty(p.Errores);
    }
}
