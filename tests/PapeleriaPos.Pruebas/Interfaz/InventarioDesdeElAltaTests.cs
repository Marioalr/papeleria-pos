using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Lo que pidió el dueño el 2026-11-21, sin navegador y solo con el teclado (la regla: toda la interfaz se usa con el teclado):
/// <list type="bullet">
/// <item>Productos › «Nuevo producto» con «¿Cuántas hay?» (en eleventa, «Hay»): Enter de casilla en casilla (código, nombre,
/// marca, costo, precio, cuántas hay) y Ctrl+Enter guarda; lo que hay entra al inventario como un «Agregar inventario», al costo
/// escrito. Con varias variantes, cada una con lo suyo. Sin el permiso «Agregar inventario», o en el piloto con eleventa, la
/// casilla no sale.</item>
/// <item>Inventario (F4): «Agregar inventario» es la primera tarjeta y el cursor está en ella; en su código, F10 abre «Buscar por
/// nombre» con lo escrito, y ↓ y Enter eligen; quien ve los costos pasa con Enter de «Cuántos llegaron» al costo, y Enter ahí
/// agrega.</item>
/// </list>
/// </summary>
public sealed class InventarioDesdeElAltaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, PaginaQueContesta Pagina, SesionDto Cajero, SesionDto Encargado);

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
        return new Caja(cliente, caja, servicios, pagina, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("encargado", "2222"));
    }

    /// <summary>El mismo servicio, con la lectura de eleventa prendida (sin servidor configurado: no lee nada).</summary>
    private WebApplicationFactory<Program> Piloto() => servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));

    /// <summary>Le pone al usuario ese nivel de un permiso (en la base, como lo dejaría «Usuarios y permisos»).</summary>
    private async Task NivelAsync(SesionDto quien, AccionControlada accion, NivelPermiso nivel)
    {
        await using var db = servicio.Base();
        (await db.PermisosUsuario.SingleAsync(p => p.UsuarioId == quien.UsuarioId && p.Accion == accion)).Nivel = nivel;
        await db.SaveChangesAsync();
    }

    private static async Task<PantallaSinNavegador> NuevoAsync(Caja c, SesionDto quien)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = quien,
            [nameof(Productos.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Productos.Estado)] = new ProductosVistos(),
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarElementoAsync("prod-0");
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

    /// <summary>Lo que se teclea en una casilla (su «oninput»; las que se toman al salir, su «onchange»): como en el navegador.</summary>
    private static async Task TeclearAsync(PantallaSinNavegador p, string id, string valor)
    {
        var campo = await p.PorIdAsync(id);
        await p.DispararAsync(campo, campo.Eventos.ContainsKey("oninput") ? "oninput" : "onchange", new ChangeEventArgs { Value = valor });
    }

    /// <summary>Ctrl+Enter en la ficha (la oye toda la ficha, también desde la casilla donde está el cursor).</summary>
    private static async Task CtrlEnterAsync(PantallaSinNavegador p) =>
        await p.DispararAsync(await p.PorIdAsync("ficha-campos"), "onkeydown", new KeyboardEventArgs { Key = "Enter", CtrlKey = true });

    private static async Task<bool> HayCasillaAsync(PantallaSinNavegador p, string id) => (await p.LeerAsync()).Elementos.Any(e => e.Id == id);

    private async Task<Guid> ProductoIdAsync(string nombre)
    {
        await using var db = servicio.Base();
        return await db.Productos.Where(x => x.Nombre == nombre).Select(x => x.Id).SingleAsync();
    }

    // ── Nuevo producto con «¿Cuántas hay?» ──

    [Fact]
    public async Task Nuevo_producto_con_cuantas_hay_solo_con_el_teclado_entra_al_inventario_al_costo_escrito()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c, c.Encargado);
        await FocoEnAsync(c, FichaProducto.PrimerCampoDelNuevo);
        await p.EsperarElementoAsync("ficha-hay");

        // Código y Enter → nombre; nombre y Enter → departamento (se elige con las flechas: su «change»).
        var codigo = $"75{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";
        await TeclearAsync(p, "ficha-codigo", codigo);
        await p.TeclaAsync("ficha-codigo", "Enter");
        await FocoEnAsync(c, "#ficha-nombre");
        var nombre = $"Hay al darlo de alta {Guid.NewGuid():N}"[..30];
        await TeclearAsync(p, "ficha-nombre", nombre);
        await p.TeclaAsync("ficha-nombre", "Enter");
        await FocoEnAsync(c, "#ficha-departamento");
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        await TeclearAsync(p, "ficha-departamento", opciones.Departamentos[0].Id.ToString());
        // Marca y Enter → lo que te cuesta → el precio → «¿Cuántas hay?».
        await TeclearAsync(p, "ficha-marca", "");
        await p.TeclaAsync("ficha-marca", "Enter");
        await FocoEnAsync(c, "#ficha-costo");
        await TeclearAsync(p, "ficha-costo", "28.50");
        await p.TeclaAsync("ficha-costo", "Enter");
        await FocoEnAsync(c, "#ficha-precio");
        await TeclearAsync(p, "ficha-precio", "45");
        await p.TeclaAsync("ficha-precio", "Enter");
        await FocoEnAsync(c, "#ficha-hay");
        Assert.Contains("Al guardar entran al inventario (como «Agregar inventario»), a lo que te cuesta.", await p.TextoAsync());
        await TeclearAsync(p, "ficha-hay", "12");
        // Su Enter, a «Guardar» (Enter ahí, como el clic); y Ctrl+Enter guarda desde donde esté el cursor.
        await p.TeclaAsync("ficha-hay", "Enter");
        await FocoEnAsync(c, "#ficha-guardar");
        await CtrlEnterAsync(p);
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así. Entraron al inventario 12 piezas."), "lo guardado con lo que hay");
        Assert.Empty(p.Errores);

        // Como un «Agregar inventario»: la entrada al costo escrito, su movimiento del kardex y la existencia.
        var id = await ProductoIdAsync(nombre);
        await using var db = servicio.Base();
        var variante = await db.Variantes.AsNoTracking().SingleAsync(v => v.ProductoId == id);
        var entrada = await db.EntradasInventario.AsNoTracking().SingleAsync(x => x.VarianteId == variante.Id);
        Assert.Equal((Cantidad.Piezas(12), 2850L, 34_200L, true, c.Encargado.UsuarioId),
            (entrada.Cantidad, entrada.CostoUnitarioCentavos, entrada.CostoCentavos, entrada.CostoEscrito, entrada.UsuarioId));
        var movimiento = await db.MovimientosInventario.AsNoTracking().SingleAsync(m => m.VarianteId == variante.Id);
        Assert.Equal((TipoMovimiento.AgregarInventario, Cantidad.Piezas(12), 34_200L, (Guid?)entrada.Id),
            (movimiento.Tipo, movimiento.Cantidad, movimiento.CostoCentavos, movimiento.DocumentoId));
        var existencia = await db.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == variante.Id && x.SucursalId == c.Datos.SucursalId);
        Assert.Equal((Cantidad.Piezas(12), 34_200L), (existencia.Cantidad, existencia.CostoCentavos));
        // Y la ficha queda vacía para el siguiente (2026-11-21), con su «¿Cuántas hay?» vacía y el cursor en el código.
        Assert.True(string.IsNullOrEmpty((await p.PorIdAsync("ficha-hay")).Valor));
        Assert.True(string.IsNullOrEmpty((await p.PorIdAsync("ficha-codigo")).Valor));
        await FocoEnAsync(c, FichaProducto.PrimerCampoDelNuevo);
    }

    [Fact]
    public async Task Sin_costo_escrito_lo_pide_y_lo_que_no_es_una_cantidad_se_dice_junto_a_su_casilla()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c, c.Encargado);
        await p.EsperarElementoAsync("ficha-hay");
        var nombre = $"Hay mal escrito {Guid.NewGuid():N}"[..26];
        await TeclearAsync(p, "ficha-nombre", nombre);
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        await TeclearAsync(p, "ficha-departamento", opciones.Departamentos[0].Id.ToString());
        await TeclearAsync(p, "ficha-precio", "20");

        await TeclearAsync(p, "ficha-hay", "2.5");
        await CtrlEnterAsync(p);
        await p.EsperarAsync(t => t.Contains("se cuenta en piezas enteras: escribe cuántos, sin punto."), "lo de las piezas enteras");
        await FocoEnAsync(c, "#ficha-hay");

        await TeclearAsync(p, "ficha-hay", "5");
        await CtrlEnterAsync(p);
        await p.EsperarAsync(t => t.Contains($"Escribe lo que te cuesta «{nombre}»: lo que hay entra al inventario a ese costo."), "que pida el costo");
        await FocoEnAsync(c, "#ficha-costo");
        await using (var db = servicio.Base())
            Assert.False(await db.Productos.AnyAsync(x => x.Nombre == nombre));

        // El cero es «sin inventario», como vacío.
        await TeclearAsync(p, "ficha-hay", "0");
        await CtrlEnterAsync(p);
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "lo guardado sin inventario");
        Assert.DoesNotContain("Entraron al inventario", await p.TextoAsync());
        var id = await ProductoIdAsync(nombre);
        await using (var db = servicio.Base())
            Assert.False(await db.EntradasInventario.AnyAsync(x => db.Variantes.Any(v => v.Id == x.VarianteId && v.ProductoId == id)));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Con_varios_colores_cada_uno_lleva_lo_que_hay_en_Inventario()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c, c.Encargado);
        await p.EsperarElementoAsync("ficha-hay");
        var nombre = $"Hay de cada color {Guid.NewGuid():N}"[..28];
        await TeclearAsync(p, "ficha-nombre", nombre);
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        await TeclearAsync(p, "ficha-departamento", opciones.Departamentos[0].Id.ToString());
        await TeclearAsync(p, "ficha-costo", "10");
        await TeclearAsync(p, "ficha-precio", "18");

        // «viene en varios…» (espacio en la casilla: su «change») y «Agregar otro»: «Inventario» se abre con una casilla por cada uno.
        await p.DispararAsync(await p.PorIdAsync("ficha-varias"), "onchange", new ChangeEventArgs { Value = true });
        await p.OprimirAsync(await p.PorIdAsync("ficha-agregar-variante"));
        Assert.True((await p.PorIdAsync("sec-inventario")).Atributos.GetValueOrDefault("open") is true);
        Assert.False(await HayCasillaAsync(p, "ficha-hay"));
        await TeclearAsync(p, "var-nombre-0", "Azul");
        await TeclearAsync(p, "var-nombre-1", "Rojo");
        await TeclearAsync(p, "var-hay-0", "4");
        await TeclearAsync(p, "var-hay-1", "7");
        await CtrlEnterAsync(p);
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así. Entró al inventario lo que hay de 2."), "lo guardado");
        Assert.Empty(p.Errores);

        var id = await ProductoIdAsync(nombre);
        await using var db = servicio.Base();
        var hay = await db.Variantes.AsNoTracking().Where(v => v.ProductoId == id)
            .Join(db.Existencias.AsNoTracking(), v => v.Id, e => e.VarianteId, (v, e) => new { v.Descripcion, e.Cantidad }).ToListAsync();
        Assert.Equal([("Azul", Cantidad.Piezas(4)), ("Rojo", Cantidad.Piezas(7))], hay.OrderBy(x => x.Descripcion).Select(x => (x.Descripcion, x.Cantidad)));
        Assert.Equal(2, await db.EntradasInventario.CountAsync(x => db.Variantes.Any(v => v.Id == x.VarianteId && v.ProductoId == id)));
    }

    [Fact]
    public async Task Sin_el_permiso_de_agregar_inventario_no_sale_la_casilla_y_el_precio_pasa_a_Guardar()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await NivelAsync(c.Encargado, AccionControlada.AgregarInventario, NivelPermiso.NoPermitido);
        try
        {
            var sinPermiso = await c.Cliente.EntrarAsync("encargado", "2222");
            Assert.False(sinPermiso.Puede(AccionControlada.AgregarInventario));
            await using var p = await NuevoAsync(c, sinPermiso);
            await p.EsperarAsync(t => t.Contains("Precio de venta"), "la ficha");
            await TeclearAsync(p, "ficha-nombre", $"Sin permiso de hay {Guid.NewGuid():N}"[..28]);
            var opciones = await c.Cliente.OpcionesCatalogoAsync();
            await TeclearAsync(p, "ficha-departamento", opciones.Departamentos[0].Id.ToString());
            await TeclearAsync(p, "ficha-precio", "20");
            await p.TeclaAsync("ficha-precio", "Enter");
            await FocoEnAsync(c, "#ficha-guardar");
            Assert.False(await HayCasillaAsync(p, "ficha-hay"));
            Assert.DoesNotContain("¿Cuántas hay?", await p.TextoAsync());
            Assert.Empty(p.Errores);
        }
        finally
        {
            await NivelAsync(c.Encargado, AccionControlada.AgregarInventario, NivelPermiso.Permitido);
        }
    }

    [Fact]
    public async Task En_el_piloto_con_eleventa_no_sale_la_casilla_y_dice_por_que()
    {
        await using var piloto = Piloto();
        var c = await PrepararAsync(piloto);
        await using var p = await NuevoAsync(c, c.Encargado);
        await p.EsperarAsync(t => t.Contains(TrastiendaEnElPiloto.InventarioEnEleventa), "la nota del piloto");
        Assert.False(await HayCasillaAsync(p, "ficha-hay"));
        Assert.Empty(p.Errores);
    }

    // ── Inventario › «Agregar inventario» con el teclado ──

    private static async Task<(PantallaSinNavegador Pantalla, Inventario Inventario)> InventarioAsync(Caja c, SesionDto quien)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        var inventario = await p.AbrirAsync<Inventario>(new()
        {
            [nameof(Inventario.Caja)] = c.Datos,
            [nameof(Inventario.Sesion)] = quien,
            [nameof(Inventario.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Inventario.EtiquetasPorImprimir)] = new EtiquetasPorImprimir(),
            [nameof(Inventario.AgregarVisto)] = new AgregarInventarioVisto(),
        });
        return (p, inventario);
    }

    /// <summary>Un producto con su código y precio, a $10 de costo, sin existencia; ya en el catálogo en memoria (para buscarlo).</summary>
    private async Task<(Guid Variante, string Nombre)> ProductoAsync(Caja c, string nombre)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var ficha = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto($"F10{Guid.NewGuid():N}"[..14])])],
                    [new PrecioFichaDto(null, 1000, 2000)], CostoCentavos: 1000),
            ])));
        await servicio.CatalogoAlDiaAsync();
        return (ficha.Variantes[0].Id!.Value, nombre);
    }

    [Fact]
    public async Task Agregar_inventario_es_la_primera_y_el_cursor_esta_en_ella()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var (p, _) = await InventarioAsync(c, c.Cajero);
        await using var _p = p;
        await p.EsperarElementoAsync("tarea-Agregar");
        var tarjetas = (await p.LeerAsync()).Elementos.Where(e => e.Id?.StartsWith("tarea-", StringComparison.Ordinal) == true).Select(e => e.Id).ToList();
        Assert.Equal("tarea-Agregar", tarjetas[0]);
        // El cursor, en la primera tarjeta: Enter (el clic del botón) la abre, con el cursor en el código.
        await FocoEnAsync(c, ".vn-tarea");
        await p.OprimirAsync(await p.PorIdAsync(tarjetas[0]!));
        await p.EsperarElementoAsync("codigo-compra");
        await FocoEnAsync(c, "#codigo-compra");
        Assert.Contains("o F10, «Buscar por nombre»", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task F10_en_el_codigo_busca_por_nombre_y_Enter_pasa_de_cuantos_al_costo_y_agrega()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var (variante, nombre) = await ProductoAsync(c, $"Lapiz buscado {sufijo}");
        var (p, inventario) = await InventarioAsync(c, c.Encargado);
        await using var _p = p;
        await p.EsperarElementoAsync("tarea-Agregar");
        await p.Dispatcher.InvokeAsync(() => inventario.AbrirAsync(TareaInventario.Agregar));
        await p.EsperarElementoAsync("codigo-compra");

        // Lo escrito en el código y F10: la búsqueda se abre con eso, con el cursor en ella.
        await p.EscribirAsync("codigo-compra", $"buscado {sufijo}");
        await p.TeclaAsync("codigo-compra", "F10");
        await p.EsperarElementoAsync("buscar");
        Assert.Equal($"buscado {sufijo}", (await p.PorIdAsync("buscar")).Valor);
        Assert.Equal("", (await p.PorIdAsync("codigo-compra")).Valor ?? "");
        await FocoEnAsync(c, "#buscar");
        await p.EsperarElementoAsync("resultado-0");
        // F10 otra vez en el código (ya abierta): solo el cursor a la búsqueda.
        await p.TeclaAsync("codigo-compra", "F10");
        await FocoEnAsync(c, "#buscar");

        // ↓ y Enter eligen: el producto se llena y el cursor va a «Cuántos llegaron».
        await p.TeclaAsync("buscar", "ArrowDown");
        await p.TeclaAsync("buscar", "Enter");
        await p.EsperarElementoAsync("agregar-cantidad");
        Assert.Equal(nombre, PantallaSinNavegador.Limpio((await p.PorIdAsync("agregar-producto")).Texto));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "buscar");
        await FocoEnAsync(c, "#agregar-cantidad");

        // Lo que no es una cantidad se dice ahí mismo y el cursor no pasa.
        await p.EscribirAsync("agregar-cantidad", "tres");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await p.EsperarAsync(t => t.Contains("«tres» no es una cantidad"), "lo que no es una cantidad");
        await FocoEnAsync(c, "#agregar-cantidad");
        // Cuántos y Enter → «Costo de cada pieza»; el costo y Enter agregan.
        await p.EscribirAsync("agregar-cantidad", "5");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await FocoEnAsync(c, "#agregar-costo");
        Assert.Contains("Costo de cada pieza", await p.TextoAsync());
        await using (var db = servicio.Base())
            Assert.False(await db.EntradasInventario.AnyAsync(x => x.VarianteId == variante));
        await p.EscribirAsync("agregar-costo", "12.50");
        await p.TeclaAsync("agregar-costo", "Enter");
        await p.EsperarAsync(t => t.Contains($"Listo: +5 «{nombre}». Ahora hay 5."), "lo agregado");
        await FocoEnAsync(c, "#codigo-compra");

        await using (var db = servicio.Base())
        {
            var entrada = await db.EntradasInventario.AsNoTracking().SingleAsync(x => x.VarianteId == variante);
            Assert.Equal((Cantidad.Piezas(5), 1250L), (entrada.Cantidad, entrada.CostoUnitarioCentavos));
        }
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Sin_ver_los_costos_Enter_en_cuantos_llegaron_agrega_como_antes()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var (variante, nombre) = await ProductoAsync(c, $"Sin costo a la vista {Guid.NewGuid():N}"[..30]);
        var (p, inventario) = await InventarioAsync(c, c.Cajero);
        await using var _p = p;
        await p.EsperarElementoAsync("tarea-Agregar");
        await p.Dispatcher.InvokeAsync(() => inventario.AbrirAsync(TareaInventario.Agregar));
        await p.EsperarElementoAsync("codigo-compra");
        await p.EscribirAsync("codigo-compra", nombre);
        await p.TeclaAsync("codigo-compra", "F10");
        await p.EsperarElementoAsync("buscar");
        // Enter antes de que lleguen los resultados: busca lo escrito y elige el primero.
        await p.TeclaAsync("buscar", "Enter");
        await p.EsperarElementoAsync("agregar-cantidad");
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "agregar-costo");
        await p.EscribirAsync("agregar-cantidad", "3");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await p.EsperarAsync(t => t.Contains($"Listo: +3 «{nombre}». Ahora hay 3."), "lo agregado");
        await using (var db = servicio.Base())
            Assert.Equal(Cantidad.Piezas(3), (await db.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == variante)).Cantidad);
        Assert.Empty(p.Errores);
    }
}
