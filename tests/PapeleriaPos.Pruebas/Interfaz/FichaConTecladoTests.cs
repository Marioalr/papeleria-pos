using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Más pedidos del dueño para «Nuevo producto» (2026-11-21), sin navegador y solo con teclas:
/// <list type="bullet">
/// <item>Enter en el título de una sección cerrada («Inventario», «Mayoreo y cajas», «Colores, tamaños…», «Otros datos») la abre y
/// pone el cursor en su primer campo; abierta, no la cierra. Lo que hace caja.js con ese Enter (que el navegador no la cierre, y el
/// Enter del último campo a la sección siguiente) lo prueba CajaJsTests.</item>
/// <item>«+ Nuevo departamento…» es la primera opción de la lista del departamento: abre el nombre con el cursor ahí; Enter lo crea y
/// deja el producto en él; Esc regresa al que tenía; sin terminarlo, guardar pide el departamento.</item>
/// <item>Guardar un nuevo deja la ficha vacía para el siguiente, con el cursor en el código, el aviso de lo guardado y «Abrirlo»; se
/// quedan el departamento y la marca.</item>
/// </list>
/// </summary>
public sealed class FichaConTecladoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, PaginaQueContesta Pagina, SesionDto Encargado);

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
        return new Caja(cliente, caja, servicios, pagina, await cliente.EntrarAsync("encargado", "2222"));
    }

    private static async Task<PantallaSinNavegador> NuevoAsync(Caja c)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = c.Encargado,
            [nameof(Productos.Acceso)] = new PermisosDePagina(c.Encargado, null),
            [nameof(Productos.Estado)] = new ProductosVistos(),
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarElementoAsync("prod-0");
        await p.OprimirAsync(await p.BotonAsync("Nuevo producto"));
        await p.EsperarElementoAsync("ficha-codigo");
        await p.EsperarElementoAsync("ficha-hay");
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

    /// <summary>Lo que se teclea en una casilla (su «oninput»; las que se toman al salir, su «onchange»), y en una lista, lo elegido.</summary>
    private static async Task TeclearAsync(PantallaSinNavegador p, string id, string valor)
    {
        var campo = await p.PorIdAsync(id);
        await p.DispararAsync(campo, campo.Eventos.ContainsKey("oninput") ? "oninput" : "onchange", new ChangeEventArgs { Value = valor });
    }

    private static async Task CtrlEnterAsync(PantallaSinNavegador p) =>
        await p.DispararAsync(await p.PorIdAsync("ficha-campos"), "onkeydown", new KeyboardEventArgs { Key = "Enter", CtrlKey = true });

    private static async Task<bool> AbiertaAsync(PantallaSinNavegador p, string seccion) =>
        (await p.PorIdAsync($"sec-{seccion}")).Atributos.GetValueOrDefault("open") is true;

    // ── Enter en el título de una sección ──

    [Fact]
    public async Task Enter_en_el_titulo_de_Inventario_la_abre_y_el_cursor_queda_en_su_primer_campo_para_escribir()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c);
        Assert.False(await AbiertaAsync(p, "inventario"));

        // Con Tab el cursor llega al título (el navegador); Enter ahí.
        await p.TeclaAsync("titulo-inventario", "Enter");
        Assert.True(await AbiertaAsync(p, "inventario"));
        await FocoEnAsync(c, "#ficha-minimo");
        await TeclearAsync(p, "ficha-minimo", "6");
        Assert.Equal("6", (await p.PorIdAsync("ficha-minimo")).Valor);
        Assert.Contains("Mínimo 6", await p.TextoAsync());

        // Abierta, Enter en su título no la cierra: el cursor vuelve a su primer campo.
        await p.TeclaAsync("titulo-inventario", "Enter");
        Assert.True(await AbiertaAsync(p, "inventario"));
        await FocoEnAsync(c, "#ficha-minimo");
        // Otras teclas en el título no hacen nada aquí (Espacio la abre y la cierra el navegador).
        await p.TeclaAsync("titulo-inventario", " ");
        Assert.True(await AbiertaAsync(p, "inventario"));

        // «Otros datos»: el IVA. «Colores, tamaños…»: la casilla de «viene en varios…»; marcada, Inventario empieza en «¿Cuántas hay?» del primero.
        await p.TeclaAsync("titulo-otros", "Enter");
        Assert.True(await AbiertaAsync(p, "otros"));
        await FocoEnAsync(c, "#ficha-iva");
        await p.TeclaAsync("titulo-variantes", "Enter");
        await FocoEnAsync(c, "#ficha-varias");
        await p.DispararAsync(await p.PorIdAsync("ficha-varias"), "onchange", new ChangeEventArgs { Value = true });
        await p.TeclaAsync("titulo-inventario", "Enter");
        await FocoEnAsync(c, "#var-hay-0");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Enter_en_el_titulo_de_Mayoreo_va_a_agregar_mayoreo_y_en_un_servicio_Inventario_solo_se_abre()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c);
        await p.TeclaAsync("titulo-mayoreo", "Enter");
        Assert.True(await AbiertaAsync(p, "mayoreo"));
        // Sin mayoreo ni cajas, lo primero que hay es «+ Agregar mayoreo» (el primer botón o campo de la sección).
        await FocoEnAsync(c, "#sec-mayoreo .dentro :is(input:not([disabled]):not([type=hidden]):not([type=radio]), button:not([disabled]))");

        await p.DispararAsync(await p.PorIdAsync("ficha-servicio"), "onchange", new ChangeEventArgs { Value = true });
        var antes = c.Pagina.Llamadas.Count;
        await p.TeclaAsync("titulo-inventario", "Enter");
        Assert.True(await AbiertaAsync(p, "inventario"));
        Assert.DoesNotContain(c.Pagina.Llamadas.Skip(antes), l => l.Funcion == "enfocar");
        Assert.Empty(p.Errores);
    }

    // ── «+ Nuevo departamento…» en la lista ──

    [Fact]
    public async Task Nuevo_departamento_desde_la_lista_con_el_teclado_crea_y_deja_el_producto_en_el()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c);
        // La primera opción de la lista.
        var opciones = (await p.LeerAsync()).Elementos.Where(e => e.Etiqueta == "option").ToList();
        var nueva = opciones.FindIndex(o => o.Valor == FichaProducto.OpcionNuevoDepartamento);
        Assert.Equal("+ Nuevo departamento…", PantallaSinNavegador.Limpio(opciones[nueva].Texto));
        Assert.Equal("Elige el departamento…", PantallaSinNavegador.Limpio(opciones[nueva + 1].Texto));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "ficha-nuevo-departamento");

        var nombre = $"Producto con depto {Guid.NewGuid():N}"[..28];
        await TeclearAsync(p, "ficha-nombre", nombre);
        await p.TeclaAsync("ficha-nombre", "Enter");
        await FocoEnAsync(c, "#ficha-departamento");
        // ↑ hasta «+ Nuevo departamento…» (la lista cambia con la flecha: su «change»): el nombre, con el cursor ahí.
        await TeclearAsync(p, "ficha-departamento", FichaProducto.OpcionNuevoDepartamento);
        await FocoEnAsync(c, "#ficha-departamento-nombre");
        var departamento = $"Depto {Guid.NewGuid():N}"[..16];
        await TeclearAsync(p, "ficha-departamento-nombre", departamento);
        await p.TeclaAsync("ficha-departamento-nombre", "Enter");
        await p.EsperarAsync(t => t.Contains($"Se creó el departamento «{departamento}» y el producto quedó en él."), "el departamento creado");
        var creado = (await c.Cliente.DepartamentosAsync()).Departamentos.Single(d => d.Nombre == departamento);
        Assert.Equal(creado.Id.ToString(), (await p.PorIdAsync("ficha-departamento")).Valor);
        await FocoEnAsync(c, "#ficha-marca");

        await TeclearAsync(p, "ficha-precio", "15");
        await CtrlEnterAsync(p);
        await p.EsperarAsync(t => t.Contains($"Se guardó «{nombre}»."), "lo guardado");
        await using var db = servicio.Base();
        Assert.Equal(creado.Id, await db.Productos.Where(x => x.Nombre == nombre).Select(x => x.DepartamentoId).SingleAsync());
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Nuevo_departamento_sin_terminar_pide_el_departamento_y_Esc_regresa_al_que_tenia()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c);
        var catalogo = await c.Cliente.OpcionesCatalogoAsync();
        var antes = catalogo.Departamentos[0].Id;
        await TeclearAsync(p, "ficha-nombre", $"Sin terminar depto {Guid.NewGuid():N}"[..28]);
        await TeclearAsync(p, "ficha-departamento", antes.ToString());
        await TeclearAsync(p, "ficha-precio", "15");

        // Elegido «Nuevo», sin crearlo: no se guarda con ningún departamento.
        await TeclearAsync(p, "ficha-departamento", FichaProducto.OpcionNuevoDepartamento);
        await FocoEnAsync(c, "#ficha-departamento-nombre");
        await TeclearAsync(p, "ficha-departamento-nombre", "A medias");
        await CtrlEnterAsync(p);
        await p.EsperarAsync(t => t.Contains("Elige el departamento."), "que pida el departamento");

        // Esc en el nombre: no se crea y regresa el de antes, con el cursor en la lista.
        await TeclearAsync(p, "ficha-departamento", FichaProducto.OpcionNuevoDepartamento);
        await p.TeclaAsync("ficha-departamento-nombre", "Escape");
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "ficha-departamento-nombre");
        Assert.Equal(antes.ToString(), (await p.PorIdAsync("ficha-departamento")).Valor);
        await FocoEnAsync(c, "#ficha-departamento");
        Assert.DoesNotContain((await c.Cliente.DepartamentosAsync()).Departamentos, d => d.Nombre == "A medias");
        Assert.Empty(p.Errores);
    }

    // ── Guardar y dar de alta otro ──

    [Fact]
    public async Task Dos_productos_seguidos_con_Ctrl_Enter_la_ficha_se_limpia_y_el_cursor_vuelve_al_codigo()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c);
        var departamento = (await c.Cliente.OpcionesCatalogoAsync()).Departamentos[0].Id;
        var primero = $"Primero seguido {Guid.NewGuid():N}"[..26];
        var segundo = $"Segundo seguido {Guid.NewGuid():N}"[..26];
        var codigo = $"75{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";

        await TeclearAsync(p, "ficha-codigo", codigo);
        await p.TeclaAsync("ficha-codigo", "Enter");
        await TeclearAsync(p, "ficha-nombre", primero);
        await p.TeclaAsync("ficha-nombre", "Enter");
        await TeclearAsync(p, "ficha-departamento", departamento.ToString());
        await p.TeclaAsync("ficha-departamento", "Enter");
        await FocoEnAsync(c, "#ficha-marca");
        await TeclearAsync(p, "ficha-marca", "Marca seguida");
        await TeclearAsync(p, "ficha-precio", "10");
        await TeclearAsync(p, "ficha-hay", "3");
        await TeclearAsync(p, "ficha-costo", "6");
        await CtrlEnterAsync(p);

        // Vacía para el siguiente, con el aviso de lo guardado (también lo que entró al inventario) y el cursor en el código.
        await p.EsperarAsync(t => t.Contains($"Se guardó «{primero}». Ya puedes dar de alta otro."), "el aviso del primero");
        Assert.Contains("Entraron al inventario 3 piezas.", await p.TextoAsync());
        Assert.Contains("Nuevo producto", await p.TextoAsync());
        Assert.True(string.IsNullOrEmpty((await p.PorIdAsync("ficha-codigo")).Valor));
        Assert.True(string.IsNullOrEmpty((await p.PorIdAsync("ficha-nombre")).Valor));
        Assert.True(string.IsNullOrEmpty((await p.PorIdAsync("ficha-precio")).Valor));
        await FocoEnAsync(c, FichaProducto.PrimerCampoDelNuevo);
        // Se quedan el departamento y la marca (por revisar), y no cuentan como cambios sin guardar.
        Assert.Equal(departamento.ToString(), (await p.PorIdAsync("ficha-departamento")).Valor);
        Assert.Equal("Marca seguida", (await p.PorIdAsync("ficha-marca")).Valor);
        Assert.NotNull(await p.PorIdAsync("nuevo-abrirlo"));

        await TeclearAsync(p, "ficha-nombre", segundo);
        await TeclearAsync(p, "ficha-precio", "11");
        await CtrlEnterAsync(p);
        await p.EsperarAsync(t => t.Contains($"Se guardó «{segundo}». Ya puedes dar de alta otro."), "el aviso del segundo");
        Assert.DoesNotContain("Tienes cambios sin guardar", await p.TextoAsync());

        await using (var db = servicio.Base())
        {
            Assert.Equal(1, await db.Productos.CountAsync(x => x.Nombre == primero));
            Assert.Equal(1, await db.Productos.CountAsync(x => x.Nombre == segundo));
            Assert.Equal(2, await db.Productos.CountAsync(x => (x.Nombre == primero || x.Nombre == segundo) && x.DepartamentoId == departamento && x.Marca!.Nombre == "Marca seguida"));
        }

        // «Abrirlo» abre el segundo para revisarlo, y ese Guardar ya no limpia la ficha.
        await p.OprimirAsync(await p.PorIdAsync("nuevo-abrirlo"));
        await p.EsperarAsync(t => t.Contains($"Editar «{segundo}»"), "el segundo abierto");
        await p.EsperarElementoAsync("ficha-precio");
        await TeclearAsync(p, "ficha-precio", "12");
        await CtrlEnterAsync(p);
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "lo editado");
        Assert.Contains($"Editar «{segundo}»", await p.TextoAsync());
        Assert.Equal(segundo, (await p.PorIdAsync("ficha-nombre")).Valor);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Si_no_se_guarda_la_ficha_no_se_limpia_y_dice_que_paso()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c);
        var nombre = $"No se guarda {Guid.NewGuid():N}"[..24];
        await TeclearAsync(p, "ficha-nombre", nombre);
        await TeclearAsync(p, "ficha-departamento", (await c.Cliente.OpcionesCatalogoAsync()).Departamentos[0].Id.ToString());
        await TeclearAsync(p, "ficha-precio", "10");
        await TeclearAsync(p, "ficha-costo", "5");
        // Media pieza: el servicio (o la ficha) lo rechaza y nada se guarda.
        await TeclearAsync(p, "ficha-hay", "1.5");
        await CtrlEnterAsync(p);
        await p.EsperarAsync(t => t.Contains("se cuenta en piezas enteras"), "lo que no se pudo");
        Assert.Equal(nombre, (await p.PorIdAsync("ficha-nombre")).Valor);
        Assert.DoesNotContain("Ya puedes dar de alta otro", await p.TextoAsync());
        await using var db = servicio.Base();
        Assert.False(await db.Productos.AnyAsync(x => x.Nombre == nombre));
        Assert.Empty(p.Errores);
    }
}
