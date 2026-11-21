using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La barra de Productos (2026-11-20, docs/propuestas/productos.md; por revisar), sin navegador y contra el servicio con datos
/// ficticios: arriba, los lugares de la página como pestañas (Productos · Cambiar precios · Tarjetas de precio · Promociones ·
/// Temporadas · Departamentos · Excel), «De eleventa ▾» con lo de la mudanza y «Nuevo producto». Cada pestaña abre su vista y
/// queda marcada; «← Producto» y Esc regresan al producto. Los permisos son los de antes: sin «Productos, precios y etiquetas»
/// solo quedan Productos, Departamentos, Excel y las decisiones de la limpieza; sin él, Alt+N no hace nada.
/// </summary>
public sealed class BarraDeProductosTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Encargado, SesionDto Dueno);

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
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("encargado", "2222"), await cliente.EntrarAsync("dueno", "3333"));
    }

    /// <summary>Un producto de mercancía con su código, para que la lista no abra vacía.</summary>
    private static async Task ProductoAsync(Caja c, string nombre)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Dueno.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                    [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto($"BP{Guid.NewGuid():N}"[..14])])],
                    [new PrecioFichaDto(null, 1000, 2500)]),
            ])));
    }

    /// <summary>La página de Productos, como la abre F3, con lo que se buscaba y los permisos dados (sin ellos, los de quien entró).</summary>
    private static async Task<(PantallaSinNavegador Pantalla, Productos Productos)> ProductosAsync(Caja c, SesionDto quien, string buscar,
        PermisosDePagina? acceso = null)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        var productos = await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = quien,
            [nameof(Productos.Acceso)] = acceso ?? new PermisosDePagina(quien, null),
            [nameof(Productos.Estado)] = new ProductosVistos { Texto = buscar },
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarElementoAsync("prod-0");
        await EsperarDetalleAsync(p);
        return (p, productos);
    }

    /// <summary>El detalle del elegido, ya cargado.</summary>
    private static Task EsperarDetalleAsync(PantallaSinNavegador p) =>
        p.EsperarAsync(t => (t.Contains("Últimos movimientos") || t.Contains("Es un servicio")) && !t.Contains("Cargando…"), "el detalle del producto");

    /// <summary>Lo que dicen las pestañas de la barra, en orden y sin espacios de más.</summary>
    private static async Task<List<string>> PestanasAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos.Where(e => e.Etiqueta == "button" && e.Atributos.GetValueOrDefault("role")?.ToString() == "tab"
                                                    && e.Id?.StartsWith("ver-", StringComparison.Ordinal) == true)
            .Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();

    /// <summary>El id de la pestaña marcada (aria-selected), o null si ninguna lo está.</summary>
    private static async Task<string?> MarcadaAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos.SingleOrDefault(e => e.Atributos.GetValueOrDefault("role")?.ToString() == "tab"
                                                            && e.Id?.StartsWith("ver-", StringComparison.Ordinal) == true
                                                            && e.Atributos.GetValueOrDefault("aria-selected")?.ToString() == "true")?.Id;

    private static async Task<string> TituloAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos.FirstOrDefault(e => e.Etiqueta == "h2") is { } titulo ? PantallaSinNavegador.Limpio(titulo.Texto)
            : throw new Xunit.Sdk.XunitException($"No está un título:\n{await p.TextoAsync()}");

    /// <summary>
    /// El título de la vista abierta: lo que sigue a «← Producto Esc» en su cabecera (es marcado fijo, no un elemento que se lea).
    /// </summary>
    private static async Task<string> TituloDeLaVistaAsync(PantallaSinNavegador p)
    {
        const string volver = "← Producto Esc";
        var texto = await p.TextoAsync();
        var i = texto.IndexOf(volver, StringComparison.Ordinal);
        return i < 0 ? throw new Xunit.Sdk.XunitException($"No está «{volver}»:\n{texto}")
            : texto[(i + volver.Length)..].TrimStart().Split('\n')[0].Trim();
    }

    private static async Task<bool> HayAsync(PantallaSinNavegador p, string id) => (await p.LeerAsync()).Elementos.Any(e => e.Id == id);

    private static Task<bool> EscAsync(PantallaSinNavegador p, Productos productos) => p.Dispatcher.InvokeAsync(productos.CerrarAsync);

    [Fact]
    public async Task Cada_pestana_abre_su_vista_queda_marcada_y_Producto_o_Esc_regresan()
    {
        var c = await PrepararAsync();
        var ficha = Guid.NewGuid().ToString("N")[..8];
        await ProductoAsync(c, $"Barra pestañas {ficha}");
        var (p, productos) = await ProductosAsync(c, c.Encargado, ficha);
        await using var _ = p;
        var producto = await TituloAsync(p);

        // Por qué tan seguido se usan, con «Nuevo producto» aparte, a la derecha; sin «Más» ni botones abajo de la lista.
        Assert.Equal(["Productos", "Cambiar precios", "Tarjetas de precio", "Promociones", "Temporadas", "Departamentos", "Excel"],
            (await PestanasAsync(p)).Select(t => t.StartsWith("Tarjetas de precio", StringComparison.Ordinal) ? "Tarjetas de precio" : t));
        Assert.Equal("ver-productos", await MarcadaAsync(p));
        Assert.Equal("Nuevo producto", PantallaSinNavegador.Limpio((await p.PorIdAsync("nuevo-producto")).Texto));
        Assert.Equal("Alt+N", (await p.PorIdAsync("nuevo-producto")).Atributos.GetValueOrDefault("title"));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Etiqueta is "details" or "summary");
        Assert.False(await HayAsync(p, "volver-al-producto"));

        (string Pestana, string Titulo)[] vistas =
        [
            ("ver-precios", "Cambiar precios"), ("ver-tarjetas", "Tarjetas de precio"), ("ver-promociones", "Promociones"),
            ("ver-temporadas", "Temporadas"), ("ver-departamentos", "Departamentos"), ("ver-excel", "Productos en Excel"),
        ];
        for (var i = 0; i < vistas.Length; i++)
        {
            var (pestana, titulo) = vistas[i];
            await p.OprimirAsync(await p.PorIdAsync(pestana));
            await p.EsperarElementoAsync("volver-al-producto");
            Assert.Equal(titulo, await TituloDeLaVistaAsync(p));
            Assert.Equal(pestana, await MarcadaAsync(p));
            Assert.Equal("← Producto Esc", PantallaSinNavegador.Limpio((await p.PorIdAsync("volver-al-producto")).Texto));
            // Ya no repiten «Esc regresa al producto»: lo dice el botón.
            Assert.DoesNotContain("Esc regresa al producto", await p.TextoAsync());

            // Una con «← Producto» y la otra con Esc, de una en una.
            if (i % 2 == 0)
                await p.OprimirAsync(await p.PorIdAsync("volver-al-producto"));
            else
                Assert.True(await EscAsync(p, productos));
            await EsperarDetalleAsync(p);
            Assert.Equal(producto, await TituloAsync(p));
            Assert.Equal("ver-productos", await MarcadaAsync(p));
        }

        // La pestaña «Productos» también regresa.
        await p.OprimirAsync(await p.PorIdAsync("ver-temporadas"));
        await p.EsperarElementoAsync("volver-al-producto");
        await p.OprimirAsync(await p.PorIdAsync("ver-productos"));
        await EsperarDetalleAsync(p);
        Assert.Equal(producto, await TituloAsync(p));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Cambiando_precios_la_lista_dice_que_un_toque_marca_y_quita_las_marcas()
    {
        var c = await PrepararAsync();
        var ficha = Guid.NewGuid().ToString("N")[..8];
        await ProductoAsync(c, $"Barra marcar {ficha}");
        var (p, _) = await ProductosAsync(c, c.Encargado, ficha);
        await using var __ = p;
        Assert.DoesNotContain("Toca para marcar", await p.TextoAsync());

        await p.OprimirAsync(await p.PorIdAsync("ver-precios"));
        await p.EsperarAsync(t => PantallaSinNavegador.Limpio(t).Contains("Toca para marcar · 0 marcados"), "la franja de marcar");
        Assert.True((await p.PorIdAsync("lista-quitar-marcas")).Desactivado);
        await p.OprimirAsync(await p.PorIdAsync("prod-0"));
        await p.EsperarAsync(t => PantallaSinNavegador.Limpio(t).Contains("Toca para marcar · 1 marcado"), "uno marcado");
        await p.OprimirAsync(await p.PorIdAsync("lista-quitar-marcas"));
        await p.EsperarAsync(t => PantallaSinNavegador.Limpio(t).Contains("· 0 marcados"), "sin marcas");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task De_eleventa_lleva_el_mayoreo_y_las_decisiones_y_se_cierra_al_elegir_o_con_Esc()
    {
        var c = await PrepararAsync();
        var ficha = Guid.NewGuid().ToString("N")[..8];
        await ProductoAsync(c, $"Barra eleventa {ficha}");
        var (p, productos) = await ProductosAsync(c, c.Encargado, ficha);
        await using var __ = p;
        var producto = await TituloAsync(p);
        Assert.False(await HayAsync(p, "ver-mayoreo"));

        // Abierto, Esc lo cierra primero y lo de abajo se queda.
        await p.OprimirAsync(await p.PorIdAsync("ver-eleventa"));
        Assert.Equal("true", (await p.PorIdAsync("ver-eleventa")).Atributos.GetValueOrDefault("aria-expanded"));
        Assert.Contains("De la mudanza, una sola vez", await p.TextoAsync());
        Assert.StartsWith("Mayoreo de eleventa por revisar", PantallaSinNavegador.Limpio((await p.PorIdAsync("ver-mayoreo")).Texto));
        Assert.Equal("Lo que se decidió al limpiar el catálogo", PantallaSinNavegador.Limpio((await p.PorIdAsync("ver-decisiones")).Texto));
        Assert.True(await EscAsync(p, productos));
        Assert.False(await HayAsync(p, "ver-mayoreo"));
        Assert.Equal(producto, await TituloAsync(p));

        // El mayoreo: ninguna pestaña marcada, «De eleventa» sí; el menú se cierra al elegir.
        await p.OprimirAsync(await p.PorIdAsync("ver-eleventa"));
        await p.OprimirAsync(await p.PorIdAsync("ver-mayoreo"));
        await p.EsperarElementoAsync("volver-al-producto");
        Assert.StartsWith("Mayoreo de eleventa por revisarEl mayoreo de eleventa", await TituloDeLaVistaAsync(p));
        Assert.False(await HayAsync(p, "ver-mayoreo"));
        Assert.Null(await MarcadaAsync(p));
        Assert.Contains("on", (await p.PorIdAsync("ver-eleventa")).Clase);
        await p.OprimirAsync(await p.PorIdAsync("volver-al-producto"));
        await EsperarDetalleAsync(p);
        Assert.Equal("ver-productos", await MarcadaAsync(p));

        // Las decisiones, con los textos sin comandos.
        await p.OprimirAsync(await p.PorIdAsync("ver-eleventa"));
        await p.OprimirAsync(await p.PorIdAsync("ver-decisiones"));
        await p.EsperarElementoAsync("volver-al-producto");
        Assert.StartsWith("Lo que se decidió al limpiar el catálogoLas decisiones", await TituloDeLaVistaAsync(p));
        await p.EsperarAsync(t => !t.Contains("Cargando…"), "las decisiones");
        Assert.DoesNotContain("aplicar-depuracion", await p.TextoAsync());
        Assert.True(await EscAsync(p, productos));
        await EsperarDetalleAsync(p);
        Assert.Equal(producto, await TituloAsync(p));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Con_la_ficha_abierta_sigue_marcada_Productos_y_su_pestana_avisa_de_lo_sin_guardar()
    {
        var c = await PrepararAsync();
        var ficha = Guid.NewGuid().ToString("N")[..8];
        await ProductoAsync(c, $"Barra ficha {ficha}");
        var (p, productos) = await ProductosAsync(c, c.Encargado, ficha);
        await using var __ = p;
        var producto = await TituloAsync(p);

        await p.OprimirAsync(await p.BotonAsync("Editar"));
        await p.EsperarElementoAsync("ficha-nombre");
        Assert.Equal("ver-productos", await MarcadaAsync(p));
        Assert.False(await HayAsync(p, "volver-al-producto"));

        // Con lo escrito sin guardar, la pestaña (o cualquier otra) espera al aviso, como tocar otro renglón.
        await p.DispararAsync(await p.PorIdAsync("ficha-nombre"), "onchange", new ChangeEventArgs { Value = "Lo que se escribía en la barra" });
        await p.OprimirAsync(await p.PorIdAsync("ver-productos"));
        await p.EsperarElementoAsync("ficha-guardar-cambios");
        Assert.Equal("Lo que se escribía en la barra", (await p.PorIdAsync("ficha-nombre")).Valor);
        await p.OprimirAsync(await p.PorIdAsync("ficha-descartar"));
        await EsperarDetalleAsync(p);
        Assert.Equal(producto, await TituloAsync(p));

        // Lo mismo con otra pestaña: al descartar, sigue a ella.
        await p.OprimirAsync(await p.BotonAsync("Editar"));
        await p.EsperarElementoAsync("ficha-nombre");
        await p.DispararAsync(await p.PorIdAsync("ficha-nombre"), "onchange", new ChangeEventArgs { Value = "Otra vez sin guardar" });
        await p.OprimirAsync(await p.PorIdAsync("ver-departamentos"));
        await p.EsperarElementoAsync("ficha-guardar-cambios");
        await p.OprimirAsync(await p.PorIdAsync("ficha-descartar"));
        await p.EsperarElementoAsync("volver-al-producto");
        Assert.Equal("Departamentos", await TituloDeLaVistaAsync(p));
        Assert.Equal("ver-departamentos", await MarcadaAsync(p));

        // Alt+N (desde la venta o la administración, NuevoProducto) abre la ficha nueva, con «Productos» marcada.
        await p.Dispatcher.InvokeAsync(productos.NuevoProducto);
        await p.EsperarElementoAsync("ficha-nombre");
        Assert.Equal("Nuevo producto", await TituloAsync(p));
        Assert.Equal("ver-productos", await MarcadaAsync(p));
        Assert.Equal("", (await p.PorIdAsync("ficha-nombre")).Valor ?? "");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Los_permisos_esconden_lo_mismo_que_antes()
    {
        var c = await PrepararAsync();
        var ficha = Guid.NewGuid().ToString("N")[..8];
        await ProductoAsync(c, $"Barra permisos {ficha}");

        // El dueño, con todo: lo mismo que el encargado.
        var (dueno, _) = await ProductosAsync(c, c.Dueno, ficha);
        await using (dueno)
        {
            Assert.Equal(7, (await PestanasAsync(dueno)).Count);
            Assert.True(await HayAsync(dueno, "nuevo-producto"));
        }

        // Con «Productos, precios y etiquetas» con autorización: entra a la página, pero solo Productos, Departamentos, Excel y,
        // en «De eleventa», las decisiones. Ni «Nuevo producto» ni Alt+N.
        var conAutorizacion = new PermisosDePagina(c.Encargado, new Dictionary<AccionControlada, PermisoDeSesion>
        {
            [AccionControlada.EditarCatalogo] = new(AccionControlada.EditarCatalogo, NivelPermiso.RequiereAutorizacion, null, false, []),
        });
        var (p, productos) = await ProductosAsync(c, c.Encargado, ficha, conAutorizacion);
        await using var __ = p;
        Assert.Equal(["Productos", "Departamentos", "Excel"], await PestanasAsync(p));
        Assert.False(await HayAsync(p, "nuevo-producto"));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Nuevo producto");
        await p.OprimirAsync(await p.PorIdAsync("ver-eleventa"));
        Assert.False(await HayAsync(p, "ver-mayoreo"));
        Assert.True(await HayAsync(p, "ver-decisiones"));
        Assert.True(await EscAsync(p, productos));

        await p.Dispatcher.InvokeAsync(productos.NuevoProducto);
        Assert.False(await HayAsync(p, "ficha-nombre"));
        Assert.Equal("ver-productos", await MarcadaAsync(p));
        // Sin «Ver costos, utilidad y margen», tampoco «Margen bajo», como antes.
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Margen bajo");
        Assert.Empty(p.Errores);
    }
}
