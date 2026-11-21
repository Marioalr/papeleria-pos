using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La ficha con el acomodo del 2026-11-20 (docs/propuestas/nuevo-producto.md): el caso de todos los días (código, nombre,
/// departamento y precio) se guarda desde arriba; el precio escrito se queda al centavo aunque cambie el costo; la ganancia en %
/// pone el precio subido a los 50 centavos y lo dice; sin «Ver costos, utilidad y margen» no hay ganancia; al editar, las
/// secciones con algo se abren solas.
/// </summary>
public sealed class NuevoProductoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, PaginaQueContesta Pagina, SesionDto Encargado, SesionDto Dueno);

    private static async Task<Caja> PrepararAsync(WebApplicationFactory<Program> fabrica)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var pagina = new PaginaQueContesta();
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IJSRuntime>(pagina)
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, pagina, await cliente.EntrarAsync("encargado", "2222"), await cliente.EntrarAsync("dueno", "3333"));
    }

    private static async Task<PantallaSinNavegador> ProductosAsync(Caja c, SesionDto quien, string? buscar = null)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = quien,
            [nameof(Productos.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Productos.Estado)] = new ProductosVistos { Texto = buscar ?? "" },
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarElementoAsync("prod-0");
        return p;
    }

    private static async Task<PantallaSinNavegador> NuevoAsync(Caja c, SesionDto quien)
    {
        var p = await ProductosAsync(c, quien);
        await p.OprimirAsync(await p.BotonAsync("Nuevo producto"));
        await p.EsperarElementoAsync("ficha-codigo");
        return p;
    }

    private static string? UltimoFoco(Caja c) =>
        c.Pagina.Llamadas.LastOrDefault(l => l.Funcion == "enfocar").Argumentos?.FirstOrDefault()?.ToString();

    private static async Task FocoEnAsync(Caja c, string selector)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (UltimoFoco(c) != selector)
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"El cursor no fue a {selector}: está en {UltimoFoco(c) ?? "ningún lado"}.");
            await Task.Delay(20);
        }
    }

    /// <summary>Lo que se teclea en el costo o en el precio: la ganancia se ve sin salir de la casilla.</summary>
    private static Task TeclearAsync(PantallaSinNavegador p, string id, string valor) => p.EscribirAsync(id, valor);

    private static Task CambiarAsync(PantallaSinNavegador p, string id, string valor) =>
        p.PorIdAsync(id).ContinueWith(async e => await p.DispararAsync(await e, "onchange", new ChangeEventArgs { Value = valor })).Unwrap();

    private static async Task<string> ValorAsync(PantallaSinNavegador p, string id) => (await p.PorIdAsync(id)).Valor ?? "";

    private async Task<FichaProductoDto> GuardadoAsync(Caja c, string nombre)
    {
        Guid id;
        await using (var db = servicio.Base())
            id = await db.Productos.Where(x => x.Nombre == nombre).Select(x => x.Id).SingleAsync();
        return (await c.Cliente.FichaProductoAsync(id, c.Datos.SucursalId, c.Dueno.UsuarioId))!;
    }

    [Fact]
    public async Task El_caso_sencillo_se_guarda_desde_arriba_con_codigo_nombre_departamento_y_precio()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c, c.Encargado);
        // Abre en el código: se escanea lo que se tiene en la mano. Su Enter (también el del lector) pasa al nombre.
        await FocoEnAsync(c, FichaProducto.PrimerCampoDelNuevo);
        var codigo = $"75{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";
        await CambiarAsync(p, "ficha-codigo", codigo);
        await p.TeclaAsync("ficha-codigo", "Enter");
        await FocoEnAsync(c, "#ficha-nombre");

        var nombre = $"Cuaderno sencillo {Guid.NewGuid():N}"[..26];
        await CambiarAsync(p, "ficha-nombre", nombre);
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        await CambiarAsync(p, "ficha-departamento", opciones.Departamentos[0].Id.ToString());
        // Lo de mayoreo, cajas y variantes no se ve en el caso sencillo: está cerrado, con lo que tiene.
        var texto = await p.TextoAsync();
        Assert.Contains("Sin mayoreo ni cajas", texto);
        Assert.Contains("Un solo tipo", texto);
        Assert.Contains("IVA 16 % · se vende · lleva inventario", texto);
        Assert.Null((await p.PorIdAsync("sec-mayoreo")).Atributos.GetValueOrDefault("open"));

        await TeclearAsync(p, "ficha-precio", "45.37");
        // Enter en el precio con lo que hace falta: a «Guardar».
        await p.TeclaAsync("ficha-precio", "Enter");
        await FocoEnAsync(c, "#ficha-guardar");
        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "lo guardado");
        Assert.DoesNotContain("Sin código se vende", await p.TextoAsync());
        Assert.Empty(p.Errores);

        var ficha = await GuardadoAsync(c, nombre);
        var variante = Assert.Single(ficha.Variantes);
        Assert.Equal(opciones.Departamentos[0].Id, ficha.DepartamentoId);
        Assert.Equal(codigo, Assert.Single(Assert.Single(variante.Presentaciones).Codigos).Codigo);
        var precio = Assert.Single(variante.Precios);
        Assert.Equal((null, 1000L, 4537L), (precio.Presentacion, precio.CantidadMinimaMilesimas, precio.PrecioCentavos));
        Assert.True(ficha.Activo && variante.Activa);
    }

    [Fact]
    public async Task Sin_precio_lo_pide_junto_a_su_casilla_y_sin_codigo_avisa_sin_detener()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c, c.Encargado);
        var nombre = $"Sin código {Guid.NewGuid():N}"[..22];
        await CambiarAsync(p, "ficha-nombre", nombre);
        await CambiarAsync(p, "ficha-departamento", (await c.Cliente.OpcionesCatalogoAsync()).Departamentos[0].Id.ToString());
        // Enter en el precio vacío no guarda: se queda ahí.
        await p.TeclaAsync("ficha-precio", "Enter");
        Assert.NotEqual("#ficha-guardar", UltimoFoco(c));

        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        Assert.Contains("Escribe el precio de venta (lo que paga el cliente).", (await p.ElementoAsync(e => e.Clase == "ficha-error", "el error junto al precio")).Texto);
        await FocoEnAsync(c, "#ficha-precio");

        await TeclearAsync(p, "ficha-precio", "8");
        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "lo guardado");
        Assert.Contains("Sin código se vende buscándolo por nombre", await p.TextoAsync());
        Assert.Equal(800L, Assert.Single(Assert.Single((await GuardadoAsync(c, nombre)).Variantes).Precios).PrecioCentavos);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task El_precio_escrito_se_queda_al_centavo_cuando_cambia_el_costo()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c, c.Dueno);
        var nombre = $"Precio escrito {Guid.NewGuid():N}"[..24];
        await CambiarAsync(p, "ficha-nombre", nombre);
        await CambiarAsync(p, "ficha-departamento", (await c.Cliente.OpcionesCatalogoAsync()).Departamentos[0].Id.ToString());

        await TeclearAsync(p, "ficha-costo", "28.50");
        await TeclearAsync(p, "ficha-precio", "45.37");
        // La ganancia en pesos y en % del precio, mientras se escribe.
        Assert.Contains("Ganas $16.87 por pieza (37.2 % del precio)", await p.TextoAsync());
        Assert.Equal("37.2", await ValorAsync(p, "ficha-ganancia"));

        // Otro costo: el precio no se mueve; la ganancia, sí.
        await TeclearAsync(p, "ficha-costo", "30");
        await CambiarAsync(p, "ficha-costo", "30");
        Assert.Equal("45.37", await ValorAsync(p, "ficha-precio"));
        Assert.Contains("Ganas $15.37 por pieza (33.9 % del precio)", await p.TextoAsync());
        // Debajo del costo no se detiene: se dice en rojo.
        await TeclearAsync(p, "ficha-costo", "50");
        Assert.Contains("Con este precio pierdes $4.63 por pieza.", (await p.ElementoAsync(e => e.Clase?.StartsWith("ficha-ganancia") == true, "la ganancia")).Texto);
        Assert.Contains("pierde", (await p.ElementoAsync(e => e.Clase?.StartsWith("ficha-ganancia") == true, "la ganancia")).Clase);
        await TeclearAsync(p, "ficha-costo", "30");

        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "lo guardado");
        var variante = Assert.Single((await GuardadoAsync(c, nombre)).Variantes);
        Assert.Equal(4537L, Assert.Single(variante.Precios).PrecioCentavos);
        Assert.Equal(3000L, variante.CostoCentavos);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task La_ganancia_pone_el_precio_subido_a_50_centavos_y_lo_dice()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c, c.Dueno);
        var nombre = $"Con ganancia {Guid.NewGuid():N}"[..23];
        await CambiarAsync(p, "ficha-nombre", nombre);
        await CambiarAsync(p, "ficha-departamento", (await c.Cliente.OpcionesCatalogoAsync()).Departamentos[0].Id.ToString());

        // Sin costo, la ganancia se queda escrita y espera al costo.
        await CambiarAsync(p, "ficha-ganancia", "37");
        Assert.Contains("Escribe lo que te cuesta para calcular el precio.", await p.TextoAsync());
        Assert.Equal("", await ValorAsync(p, "ficha-precio"));
        Assert.False((await p.PorIdAsync("ficha-ganancia")).Desactivado);

        // Con el costo: $28.50 ÷ (1 − 37 %) = $45.24, subido a $45.50, y la ganancia con ese precio.
        await TeclearAsync(p, "ficha-costo", "28.50");
        Assert.Equal("45.50", await ValorAsync(p, "ficha-precio"));
        Assert.Equal("37.4", await ValorAsync(p, "ficha-ganancia"));
        Assert.Contains("Redondeado a $45.50 (hacia arriba, a 50 centavos): la ganancia queda en 37.4 %.", await p.TextoAsync());
        Assert.Contains("Ganas $17.00 por pieza (37.4 % del precio)", await p.TextoAsync());
        // La ganancia que se ve no se vuelve a aplicar (37.4 % daría $46.00).
        await CambiarAsync(p, "ficha-ganancia", "37.4");
        Assert.Equal("45.50", await ValorAsync(p, "ficha-precio"));

        // 40 % da justo $47.50: sin nota del redondeo. Con Enter, también.
        await p.EscribirAsync("ficha-ganancia", "40");
        await p.TeclaAsync("ficha-ganancia", "Enter");
        Assert.Equal("47.50", await ValorAsync(p, "ficha-precio"));
        Assert.DoesNotContain("Redondeado a", await p.TextoAsync());
        // Fuera de 0 a 99 % no pone precio y lo dice.
        await CambiarAsync(p, "ficha-ganancia", "120");
        Assert.Equal("47.50", await ValorAsync(p, "ficha-precio"));
        Assert.Contains("La ganancia va de 0 a 99 % del precio.", await p.TextoAsync());

        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "lo guardado");
        Assert.Equal(4750L, Assert.Single(Assert.Single((await GuardadoAsync(c, nombre)).Variantes).Precios).PrecioCentavos);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Sin_Ver_utilidad_se_ve_el_costo_pero_no_la_ganancia()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        Assert.False(c.Encargado.Puede(AccionControlada.VerUtilidad));
        await using var p = await NuevoAsync(c, c.Encargado);
        var (texto, elementos) = await p.LeerAsync();
        Assert.Contains(elementos, e => e.Id == "ficha-costo" && e.Clase == "umbral costo");
        Assert.DoesNotContain(elementos, e => e.Clase == "umbral margen");
        Assert.DoesNotContain("Si escribes la ganancia", texto);

        await TeclearAsync(p, "ficha-costo", "10");
        await TeclearAsync(p, "ficha-precio", "8");
        texto = await p.TextoAsync();
        Assert.DoesNotContain("Ganas", texto);
        Assert.DoesNotContain("pierdes", texto);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Al_editar_uno_con_mayoreo_y_caja_se_abren_esas_secciones_y_el_cursor_va_al_precio()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var nombre = $"Con mayoreo {Guid.NewGuid():N}"[..22];
        await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Dueno.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                    [new PresentacionFichaDto(null, "Pieza", 1000, true, []), new PresentacionFichaDto(null, "Caja", 20_000, false, [])],
                    [new PrecioFichaDto(null, 1000, 4500), new PrecioFichaDto(null, 12_000, 4000), new PrecioFichaDto("Caja", 1000, 81_000)],
                    MinimoYMaximo: new MinimoYMaximoDto(6000, 24_000), CostoCentavos: 2850),
            ])));

        await using var p = await ProductosAsync(c, c.Dueno, nombre);
        await p.EsperarAsync(t => t.Contains("Últimos movimientos") || t.Contains("Todavía no tiene movimientos"), "el detalle");
        await p.OprimirAsync(await p.BotonAsync("Editar"));
        await p.EsperarElementoAsync("ficha-precio");
        await FocoEnAsync(c, FichaProducto.PrimerCampoAlEditar);

        Assert.True((await p.PorIdAsync("sec-mayoreo")).Atributos.GetValueOrDefault("open") is true);
        Assert.True((await p.PorIdAsync("sec-inventario")).Atributos.GetValueOrDefault("open") is true);
        Assert.Null((await p.PorIdAsync("sec-variantes")).Atributos.GetValueOrDefault("open"));
        Assert.Null((await p.PorIdAsync("sec-otros")).Atributos.GetValueOrDefault("open"));
        var (texto, elementos) = await p.LeerAsync();
        Assert.Contains("Mayoreo desde 12 a $40.00 · Caja de 20 a $810.00", texto);
        Assert.Contains("Mínimo 6 · máximo 24", texto);
        Assert.Equal("45.00", await ValorAsync(p, "ficha-precio"));
        Assert.Contains(elementos, e => e.Clase == "umbral mayoreo" && e.Valor == "40.00");
        Assert.Contains(elementos, e => e.Clase == "umbral de-caja" && e.Valor == "810.00");
        Assert.Contains("Ganas $11.50 por pieza (28.8 % del precio)", texto);
        Assert.Contains("Ganas $240.00 por la caja (29.6 % del precio)", texto);

        // Sin el precio propio de la caja, se vende por sus piezas: guardarlo así lo quita.
        await p.DispararAsync(await p.ElementoAsync(e => e.Clase == "umbral de-caja", "el precio de la caja"), "onchange", new ChangeEventArgs { Value = "" });
        Assert.Contains("Sin precio propio: 20 × $45.00 = $900.00", await p.TextoAsync());
        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "lo guardado");
        var variante = Assert.Single((await GuardadoAsync(c, nombre)).Variantes);
        Assert.Equal([(null, 1000L, 4500L), (null, 12_000L, 4000L)],
            variante.Precios.Select(x => (x.Presentacion, x.CantidadMinimaMilesimas, x.PrecioCentavos)).OrderBy(x => x.CantidadMinimaMilesimas));
        Assert.Equal(2, variante.Presentaciones.Count);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task El_mayoreo_desde_una_o_repetido_no_se_guarda_y_abre_su_seccion()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c, c.Encargado);
        await CambiarAsync(p, "ficha-nombre", $"Mayoreo mal {Guid.NewGuid():N}"[..22]);
        await CambiarAsync(p, "ficha-departamento", (await c.Cliente.OpcionesCatalogoAsync()).Departamentos[0].Id.ToString());
        await TeclearAsync(p, "ficha-precio", "10");
        await p.OprimirAsync(await p.BotonAsync("+ Agregar mayoreo"));
        await p.DispararAsync(await p.ElementoAsync(e => e.Clase == "umbral desde", "desde cuántas"), "onchange", new ChangeEventArgs { Value = "1" });
        await p.DispararAsync(await p.ElementoAsync(e => e.Clase == "umbral mayoreo", "el precio del mayoreo"), "onchange", new ChangeEventArgs { Value = "9" });
        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        Assert.Contains("El mayoreo es desde 2 piezas o más.", await p.TextoAsync());
        Assert.True((await p.PorIdAsync("sec-mayoreo")).Atributos.GetValueOrDefault("open") is true);
        Assert.StartsWith("#may-desde-", UltimoFoco(c));
        Assert.Empty(p.Errores);
    }
}
