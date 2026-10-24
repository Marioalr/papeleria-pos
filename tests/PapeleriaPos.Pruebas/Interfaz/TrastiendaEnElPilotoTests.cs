using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Productos e Inventario en el piloto con eleventa (Eleventa:Activo), sin navegador: la ficha de un producto de eleventa
/// enseña lo que pone la lectura sin dejarlo cambiar (con su nota) y no ofrece depurarlo; Inventario no ofrece lo que mueve
/// existencias, con su nota, ni lo abre si se lo piden desde la venta; el conteo completo del celular lo dice. Fuera del
/// piloto, todo como siempre.
/// </summary>
public sealed class TrastiendaEnElPilotoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Encargado);

    private static async Task<Caja> PrepararAsync(WebApplicationFactory<Program> fabrica)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("encargado", "2222"));
    }

    private WebApplicationFactory<Program> Piloto() => servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));

    /// <summary>
    /// Un marcador de eleventa (con su ID): pieza con su código, caja de 12, menudeo de $25, una escala de aquí desde 24 y, como
    /// los pone la lectura, su mínimo y su máximo de eleventa (6 y 6).
    /// </summary>
    private async Task<(string Nombre, string Codigo)> MarcadorDeEleventaAsync(Caja c)
    {
        var nombre = $"Marcador de eleventa {Guid.NewGuid():N}"[..28];
        var codigo = $"MAR{Guid.NewGuid():N}"[..16];
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var ficha = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                    [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)]), new PresentacionFichaDto(null, "Caja 12", 12000, false, [])],
                    [new PrecioFichaDto(null, 1000, 2500), new PrecioFichaDto(null, 24000, 2100)]),
            ])));
        await using var db = servicio.Base();
        db.MapeosImportacion.Add(new MapeoImportacion
        {
            Origen = "eleventa", Entidad = nameof(Variante), IdOrigen = Random.Shared.Next(100_000, 999_999).ToString(), IdNuevo = ficha.Variantes[0].Id!.Value,
        });
        var variante = await db.Variantes.SingleAsync(v => v.Id == ficha.Variantes[0].Id);
        (variante.InventarioMinimo, variante.InventarioMaximo) = (Cantidad.Piezas(6), Cantidad.Piezas(6));
        await db.SaveChangesAsync();
        return (nombre, codigo);
    }

    /// <summary>Productos con el producto buscado, en su detalle.</summary>
    private static async Task<PantallaSinNavegador> DetalleAsync(Caja c, string nombre)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = c.Encargado,
            [nameof(Productos.Acceso)] = new PermisosDePagina(c.Encargado, null),
            [nameof(Productos.Estado)] = new ProductosVistos { Texto = nombre },
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarElementoAsync("prod-0");
        await p.EsperarAsync(t => t.Contains("Últimos movimientos") || t.Contains("Todavía no tiene movimientos"), "el detalle");
        return p;
    }

    /// <summary>Su ficha, abierta con «Editar».</summary>
    private static async Task EditarAsync(PantallaSinNavegador p)
    {
        await p.OprimirAsync(await p.BotonAsync("Editar"));
        await p.EsperarAsync(t => t.Contains("Presentaciones y códigos"), "la ficha");
    }

    private static async Task<ElementoDibujado> CampoAsync(PantallaSinNavegador p, Func<ElementoDibujado, bool> condicion, string que) =>
        await p.ElementoAsync(e => e.Etiqueta is "input" or "select" && condicion(e), que);

    [Fact]
    public async Task En_el_piloto_la_ficha_de_un_producto_de_eleventa_enseña_lo_que_no_se_cambia_y_deja_lo_demas()
    {
        await using var piloto = Piloto();
        var c = await PrepararAsync(piloto);
        var (nombre, codigo) = await MarcadorDeEleventaAsync(c);
        await using var p = await DetalleAsync(c, nombre);
        // El detalle ya lo dice; la ficha, también, y enseña qué.
        Assert.Contains(TrastiendaEnElPiloto.ProductosEnEleventa, await p.TextoAsync());
        await EditarAsync(p);

        var (texto, elementos) = await p.LeerAsync();
        Assert.Contains(TrastiendaEnElPiloto.ProductosEnEleventa, texto);
        // Lo que pone la lectura se ve sin poder cambiarse: el nombre, el departamento, si se vende, si está activa, el código
        // de la pieza (se agregan otros) y el menudeo, sin «Quitar».
        Assert.True((await CampoAsync(p, e => e.Valor == nombre && e.Id != "busca-prod", "el nombre")).Desactivado);
        Assert.True((await CampoAsync(p, e => e.Etiqueta == "select" && Guid.TryParse(e.Valor, out _), "el departamento")).Desactivado);
        Assert.Equal(2, elementos.Count(e => e.Etiqueta == "input" && e.Atributos.GetValueOrDefault("type") as string == "checkbox" && e.Desactivado));
        Assert.Contains(codigo, (await p.ElementoAsync(e => e.Clase == "codigos-fijos", "los códigos de eleventa")).Texto);
        Assert.Contains(elementos, e => e.Etiqueta == "input" && e.Atributos.GetValueOrDefault("placeholder") as string == "Otro código" && !e.Desactivado);
        Assert.True((await CampoAsync(p, e => e.Valor == "25.00", "el menudeo")).Desactivado);
        Assert.Contains("de eleventa", texto);
        // El mínimo y el máximo también (2026-10-24: el inventario se lleva en eleventa): se ven, sin poder cambiarse.
        var minimo = await CampoAsync(p, e => e.Clase == "umbral minimo", "el mínimo");
        var maximo = await CampoAsync(p, e => e.Clase == "umbral maximo", "el máximo");
        Assert.Equal(("6", true, "6", true), (minimo.Valor, minimo.Desactivado, maximo.Valor, maximo.Desactivado));
        // La escala capturada aquí, la caja y la marca sí.
        Assert.False((await CampoAsync(p, e => e.Valor == "21.00", "la escala de aquí")).Desactivado);
        Assert.False((await CampoAsync(p, e => e.Valor == "Caja 12", "la caja")).Desactivado);
        // Y no se depura: ni «Dar de baja» ni «Juntar».
        Assert.DoesNotContain(elementos, e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) is "Dar de baja" or "Juntar con otro…");

        // Guardar con lo de siempre (la escala de aquí, más barata) pasa: lo de eleventa sigue igual.
        await p.DispararAsync(await CampoAsync(p, e => e.Valor == "21.00", "la escala de aquí"), "onchange",
            new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "20.50" });
        await p.OprimirAsync(await p.BotonAsync("Guardar"));
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "el guardado");
        Assert.Contains(codigo, (await p.ElementoAsync(e => e.Clase == "codigos-fijos", "los códigos de eleventa")).Texto);
        // El mínimo y el máximo se quedaron como los puso la lectura.
        Assert.Equal("6", (await CampoAsync(p, e => e.Clase == "umbral minimo", "el mínimo")).Valor);
        await using (var db = servicio.Base())
        {
            var guardada = await db.Variantes.SingleAsync(v => db.Productos.Any(x => x.Id == v.ProductoId && x.Nombre == nombre));
            Assert.Equal("6/6", $"{guardada.InventarioMinimo}/{guardada.InventarioMaximo}");
        }
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Fuera_del_piloto_la_misma_ficha_se_cambia_toda()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var (nombre, codigo) = await MarcadorDeEleventaAsync(c);
        await using var p = await DetalleAsync(c, nombre);
        Assert.DoesNotContain(TrastiendaEnElPiloto.ProductosEnEleventa, await p.TextoAsync());
        await EditarAsync(p);

        var (texto, elementos) = await p.LeerAsync();
        Assert.DoesNotContain(TrastiendaEnElPiloto.ProductosEnEleventa, texto);
        Assert.False((await CampoAsync(p, e => e.Valor == nombre && e.Id != "busca-prod", "el nombre")).Desactivado);
        Assert.False((await CampoAsync(p, e => e.Valor == "25.00", "el menudeo")).Desactivado);
        Assert.False((await CampoAsync(p, e => e.Clase == "umbral minimo", "el mínimo")).Desactivado);
        Assert.Contains(elementos, e => e.Etiqueta == "input" && e.Valor == codigo && !e.Desactivado);
        Assert.DoesNotContain(elementos, e => e.Clase == "codigos-fijos");
        Assert.Contains(elementos, e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Dar de baja");
        Assert.Empty(p.Errores);
    }

    /// <summary>Inventario de quien lo puede todo (el encargado): en el piloto, solo lo que no mueve existencias.</summary>
    private static async Task<PantallaSinNavegador> InventarioAsync(Caja c, TareaInventario? inicial = null)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Inventario>(new()
        {
            [nameof(Inventario.Caja)] = c.Datos,
            [nameof(Inventario.Sesion)] = c.Encargado,
            [nameof(Inventario.Acceso)] = new PermisosDePagina(c.Encargado, null),
            [nameof(Inventario.EtiquetasPorImprimir)] = new EtiquetasPorImprimir(),
            [nameof(Inventario.TareaInicial)] = inicial,
        });
        return p;
    }

    private static async Task<List<string>> TarjetasAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos.Where(e => e.Id?.StartsWith("tarea-", StringComparison.Ordinal) == true).Select(e => e.Id!["tarea-".Length..]).ToList();

    [Fact]
    public async Task En_el_piloto_Inventario_no_ofrece_lo_que_mueve_existencias_ni_lo_abre_desde_la_venta()
    {
        await using var piloto = Piloto();
        var c = await PrepararAsync(piloto);

        await using (var p = await InventarioAsync(c))
        {
            await p.EsperarAsync(t => t.Contains(TrastiendaEnElPiloto.InventarioEnEleventa), "la nota del piloto");
            // Los reportes (bajos en inventario y movimientos) solo leen: van también en el piloto.
            Assert.Equal(["Ordenes", "Sugerido", "Bajos", "Etiquetas", "HojaCodigos", "Proveedores", "Movimientos"], await TarjetasAsync(p));
            Assert.Empty(p.Errores);
        }
        // El chip del conteo del día o «/» piden esa tarea: en el piloto queda en las tarjetas, con la nota.
        await using (var p = await InventarioAsync(c, TareaInventario.ConteoDelDia))
        {
            await p.EsperarAsync(t => t.Contains(TrastiendaEnElPiloto.InventarioEnEleventa), "la nota del piloto");
            Assert.DoesNotContain("← Inventario", await p.TextoAsync());
            Assert.DoesNotContain("ConteoDelDia", await TarjetasAsync(p));
            Assert.Empty(p.Errores);
        }
        // «Ir a Etiquetas» sí abre la suya.
        await using (var p = await InventarioAsync(c, TareaInventario.Etiquetas))
        {
            await p.EsperarAsync(t => t.Contains("← Inventario"), "las etiquetas");
            Assert.Empty(p.Errores);
        }
    }

    [Fact]
    public async Task Fuera_del_piloto_Inventario_ofrece_todo_como_siempre()
    {
        var c = await PrepararAsync(servicio.Fabrica);

        await using var p = await InventarioAsync(c, TareaInventario.Mermas);

        await p.EsperarAsync(t => t.Contains("← Inventario"), "la merma");
        Assert.DoesNotContain(TrastiendaEnElPiloto.InventarioEnEleventa, await p.TextoAsync());
        await p.OprimirAsync(await p.BotonAsync("← Inventario"));
        await p.EsperarAsync(t => t.Contains("Solo las tareas que tienes"), "las tarjetas");
        // Todas menos lo que vale el inventario: el encargado no tiene «Ver costos, utilidad y margen».
        Assert.Equal(PaginasDeAtras.Tareas.Where(t => t.Id != TareaInventario.Valor).Select(t => t.Id.ToString()), await TarjetasAsync(p));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_el_piloto_el_conteo_completo_del_celular_dice_que_el_inventario_se_lleva_en_eleventa()
    {
        await using var piloto = Piloto();
        var c = await PrepararAsync(piloto);
        await using var p = new PantallaSinNavegador(c.Servicios);

        await p.AbrirAsync<ConteoCompleto>(new()
        {
            [nameof(ConteoCompleto.Caja)] = c.Datos,
            [nameof(ConteoCompleto.Sesion)] = c.Encargado,
            [nameof(ConteoCompleto.EnCelular)] = true,
        });

        await p.EsperarAsync(t => t.Contains(TrastiendaEnElPiloto.InventarioEnEleventa), "la nota del piloto");
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Etiqueta == "button");
        Assert.Empty(p.Errores);
    }
}
