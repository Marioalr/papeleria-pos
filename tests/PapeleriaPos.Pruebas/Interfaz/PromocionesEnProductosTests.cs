using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
/// Productos (F3) › Promociones, sin navegador, contra el servicio con datos ficticios (fase 3): qué corre hoy; «Nueva
/// promoción» con los productos marcados en la lista de la izquierda (como en «Cambiar precios»), Enter para ver cómo queda y
/// Ctrl+Enter para guardarla; «Dejar al costo» con el costo de cada uno, para quien ve la utilidad; debajo del costo, la
/// autorización de quien puede; «Terminar ahora»; y en el piloto con eleventa, solo consultarlas, con la nota de por qué.
/// </summary>
public sealed class PromocionesEnProductosTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Encargado, SesionDto Dueno);

    private static async Task<Caja> PrepararAsync(WebApplicationFactory<Program> fabrica)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).First();
        conexion.CajaId = caja.Id;
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("encargado", "2222"), await cliente.EntrarAsync("dueno", "3333"));
    }

    private static string Ficha() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Un producto con su menudeo; regresa su variante.</summary>
    private static async Task<Guid> ProductoAsync(Caja c, string nombre, long menudeo)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var ficha = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                    [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto($"PEP{Guid.NewGuid():N}"[..16])])],
                    [new PrecioFichaDto(null, 1000, menudeo)]),
            ])));
        return ficha.Variantes[0].Id!.Value;
    }

    /// <summary>Su costo: el último de su proveedor (no tiene existencia).</summary>
    private static async Task CostoAsync(ServicioDePrueba s, Guid variante, long centavos)
    {
        await using var db = s.Base();
        var proveedor = new Proveedor { Nombre = $"Proveedor {Ficha()}" };
        db.Proveedores.Add(proveedor);
        db.ProveedoresVariantes.Add(new ProveedorVariante { ProveedorId = proveedor.Id, VarianteId = variante, UltimoCostoCentavos = centavos, UltimaCompraUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    /// <summary>Productos de quien entró, como la abre F3, con lo que se buscaba en ella, y ya en Promociones.</summary>
    private static async Task<(PantallaSinNavegador Pantalla, Productos Productos)> PromocionesAsync(Caja c, SesionDto quien, string texto)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        var productos = await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = quien,
            [nameof(Productos.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Productos.Estado)] = new ProductosVistos { Texto = texto },
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarElementoAsync("prod-0");
        await p.OprimirAsync(await p.PorIdAsync("ver-promociones"));
        // La lista llega del servicio: con ella, sus filtros.
        await p.EsperarElementoAsync("promo-filtro-Hoy");
        return (p, productos);
    }

    private static async Task CtrlEnterAsync(PantallaSinNavegador p, string id) =>
        await p.DispararAsync(await p.PorIdAsync(id), "onkeydown", new KeyboardEventArgs { Key = "Enter", CtrlKey = true });

    private static Task<bool> EscAsync(PantallaSinNavegador p, Productos productos) => p.Dispatcher.InvokeAsync(productos.CerrarAsync);

    private static string Limpio(string texto) => PantallaSinNavegador.Limpio(texto);

    /// <summary>Marca en la lista de la izquierda el renglón de ese producto.</summary>
    private static async Task MarcarAsync(PantallaSinNavegador p, string nombre)
    {
        var renglon = await p.ElementoAsync(e => e.Id?.StartsWith("prod-", StringComparison.Ordinal) == true && Limpio(e.Texto).StartsWith(nombre, StringComparison.Ordinal),
            $"«{nombre}» en la lista");
        await p.OprimirAsync(renglon);
    }

    [Fact]
    public async Task El_encargado_arma_un_tres_por_dos_con_los_marcados_y_queda_en_lo_de_hoy()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var ficha = Ficha();
        await ProductoAsync(c, $"Cuaderno {ficha}", 4500);
        await ProductoAsync(c, $"Libreta {ficha}", 3000);
        var (p, productos) = await PromocionesAsync(c, c.Encargado, ficha);
        await using var __ = p;
        // El encargado no ve la utilidad: no hay «Dejar al costo».
        Assert.DoesNotContain("Dejar al costo", await p.TextoAsync());

        await p.OprimirAsync(await p.PorIdAsync("promo-nueva"));
        await p.EscribirAsync("promo-nombre", $"3×2 {ficha}");
        // Mientras se arma una de productos, la lista marca: un toque marca.
        await MarcarAsync(p, $"Cuaderno {ficha}");
        await MarcarAsync(p, $"Libreta {ficha}");
        await p.EsperarAsync(t => Limpio(t).Contains($"Cuaderno {ficha} $45.00 por pieza") && Limpio(t).Contains($"Libreta {ficha} $30.00 por pieza"),
            "los renglones de los marcados");
        Assert.Contains("Productos · 2", Limpio((await p.PorIdAsync("promo-a-que-Productos")).Texto));

        // Enter: cómo queda; Ctrl+Enter: guardarla.
        await p.TeclaAsync("promo-nombre", "Enter");
        await p.EsperarAsync(t => Limpio(t).Contains("Toca 2 productos que se venden · ninguno queda debajo del costo."), "cómo queda");
        Assert.Contains("El costo y el margen los ve quien tiene el permiso «Ver costos, utilidad y margen».", Limpio(await p.TextoAsync()));
        await CtrlEnterAsync(p, "promo-nombre");
        await p.EsperarAsync(t => t.Contains($"Listo: se guardó la promoción «3×2 {ficha}»."), "lo guardado");

        var texto = Limpio(await p.TextoAsync());
        Assert.Contains($"Promoción «3×2 {ficha}»", texto);
        Assert.Contains("3×2 · desde el", texto);
        Assert.Contains("Toca 2 productos que se venden.", texto);
        Assert.Contains("La dio de alta Encargado de prueba, el ", texto);
        // Al guardarla, la lista deja de marcar.
        Assert.False((await p.PorIdAsync("prod-0")).Atributos.ContainsKey("aria-pressed"));

        // Esc: a la lista, donde está en lo de hoy.
        Assert.True(await EscAsync(p, productos));
        await p.EsperarAsync(t => Limpio(t).Contains($"3×2 {ficha} 3×2 · 2 productos"), "la lista con ella");
        Assert.Equal("true", (await p.PorIdAsync("promo-filtro-Hoy")).Atributos.GetValueOrDefault("aria-pressed"));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task El_dueno_deja_al_costo_los_marcados_y_ve_el_costo_y_el_margen()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var ficha = Ficha();
        var lapiz = await ProductoAsync(c, $"Lápiz {ficha}", 800);
        await CostoAsync(servicio, lapiz, 437);
        var (p, _) = await PromocionesAsync(c, c.Dueno, ficha);
        await using var __ = p;

        await p.OprimirAsync(await p.PorIdAsync("promo-al-costo"));
        Assert.Equal("Al costo", (await p.PorIdAsync("promo-nombre")).Valor);
        await MarcarAsync(p, $"Lápiz {ficha}");
        await p.EsperarElementoAsync("promo-precio-0");

        Assert.Equal("4.37", (await p.PorIdAsync("promo-precio-0")).Valor);
        var texto = Limpio(await p.TextoAsync());
        Assert.Contains("Costo", texto);
        Assert.Contains($"Lápiz {ficha} $8.00 por pieza", texto);
        await p.OprimirAsync(await p.PorIdAsync("promo-guardar"));
        await p.EsperarAsync(t => t.Contains("Listo: se guardó la promoción «Al costo»."), "lo guardado");
        texto = Limpio(await p.TextoAsync());
        Assert.Contains("Precio especial · del ", texto);
        Assert.Contains($"Lápiz {ficha} $8.00 por pieza $4.37 $4.37 0 %", texto);
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Debajo del costo, como Ctrl+D: quien lo tiene «con autorización» la guarda con la de quien puede, que toca su ficha y
    /// escribe su PIN. Con su propio servicio: se le cambia un permiso al encargado.
    /// </summary>
    [Fact]
    public async Task Debajo_del_costo_se_guarda_con_la_autorizacion_de_quien_puede()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var c = await PrepararAsync(propio.Fabrica);
        var ficha = Ficha();
        var pegamento = await ProductoAsync(c, $"Pegamento {ficha}", 1000);
        await CostoAsync(propio, pegamento, 700);
        await c.Cliente.CambiarPermisoAsync(c.Datos.Id, c.Encargado.UsuarioId,
            new SolicitudPermisoUsuario(c.Dueno.UsuarioId, AccionControlada.VenderBajoCosto, NivelPermiso.RequiereAutorizacion));
        // Con la sesión de ahora, que dice quién lo autoriza.
        var encargado = await c.Cliente.EntrarAsync("encargado", "2222");
        var (p, _) = await PromocionesAsync(c, encargado, ficha);
        await using var __ = p;

        await p.OprimirAsync(await p.PorIdAsync("promo-nueva"));
        await p.EscribirAsync("promo-nombre", $"Muy barato {ficha}");
        await p.DispararAsync(await p.PorIdAsync("promo-tipo"), "onchange", new ChangeEventArgs { Value = nameof(TipoPromocion.PrecioEspecial) });
        await MarcarAsync(p, $"Pegamento {ficha}");
        await p.EsperarElementoAsync("promo-precio-0");
        await p.EscribirAsync("promo-precio-0", "6");
        await p.OprimirAsync(await p.PorIdAsync("promo-guardar"));

        await p.EsperarElementoAsync("autoriza-pin");
        var texto = Limpio(await p.TextoAsync());
        Assert.Contains("Lo autoriza Dueño de prueba", texto);
        Assert.Contains($"La promoción «Muy barato {ficha}» deja debajo del costo: Pegamento {ficha}", texto);
        await p.EscribirAsync("autoriza-pin", "3333");
        await p.TeclaAsync("autoriza-pin", "Enter");
        await p.EsperarAsync(t => t.Contains($"Listo: se guardó la promoción «Muy barato {ficha}»."), "lo guardado con la autorización");
        Assert.Contains("debajo del costo", Limpio(await p.TextoAsync()));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Terminar_ahora_la_pasa_a_las_terminadas()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var ficha = Ficha();
        var regla = await ProductoAsync(c, $"Regla {ficha}", 1500);
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
        await c.Cliente.GuardarPromocionAsync(new SolicitudPromocion(c.Encargado.UsuarioId, c.Datos.SucursalId, c.Datos.Id, $"Segunda {ficha}",
            TipoPromocion.SegundoAMitad, 0, 0, 0, hoy.AddDays(-1), hoy.AddDays(5), DiasDeLaSemana.Todos, false, [new AlcanceSolicitadoDto(VarianteId: regla)],
            Id: Guid.NewGuid()));
        var (p, productos) = await PromocionesAsync(c, c.Encargado, ficha);
        await using var __ = p;

        await p.OprimirAsync(await p.ElementoAsync(e => e.Etiqueta == "tr" && Limpio(e.Texto).StartsWith($"Segunda {ficha}", StringComparison.Ordinal),
            "la promoción en la lista"));
        await p.EsperarElementoAsync("promo-terminar");
        await p.OprimirAsync(await p.PorIdAsync("promo-terminar"));
        await p.EsperarAsync(t => t.Contains($"Se terminó la promoción «Segunda {ficha}»"), "la terminada");

        Assert.Contains("Terminada", await p.TextoAsync());
        Assert.Contains("La terminó Encargado de prueba", Limpio(await p.TextoAsync()));
        Assert.True(await EscAsync(p, productos));
        await p.OprimirAsync(await p.PorIdAsync("promo-filtro-Terminadas"));
        await p.EsperarAsync(t => Limpio(t).Contains($"Segunda {ficha} el segundo a mitad de precio"), "las terminadas");
        // Esc en la lista: al producto.
        Assert.True(await EscAsync(p, productos));
        Assert.DoesNotContain("Se aplican solas en la venta", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_el_piloto_solo_se_consultan_y_dice_por_que()
    {
        await using var piloto = servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));
        var c = await PrepararAsync(piloto);
        var ficha = Ficha();
        await ProductoAsync(c, $"Compás {ficha}", 4000);
        var (p, _) = await PromocionesAsync(c, c.Dueno, ficha);
        await using var __ = p;

        await p.EsperarAsync(t => t.Contains(RegistroPromociones.EnElPiloto), "la nota del piloto");
        Assert.DoesNotContain("Nueva promoción", await p.TextoAsync());
        Assert.DoesNotContain("Dejar al costo", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    // ── Revisión del PR #132 ────────────────────────────────────────────────

    /// <summary>«7,5» en el porcentaje se leía como 75 %: con coma no se acepta y lo dice; con punto, «Cómo queda» dice cómo se leyó.</summary>
    [Fact]
    public async Task El_porcentaje_va_con_punto_y_como_queda_dice_como_se_leyo()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var ficha = Ficha();
        await ProductoAsync(c, $"Carpeta {ficha}", 2000);
        var (p, _) = await PromocionesAsync(c, c.Encargado, ficha);
        await using var __ = p;
        await p.OprimirAsync(await p.PorIdAsync("promo-nueva"));
        await p.EscribirAsync("promo-nombre", $"Siete y medio {ficha}");
        await p.DispararAsync(await p.PorIdAsync("promo-tipo"), "onchange", new ChangeEventArgs { Value = nameof(TipoPromocion.Porcentaje) });
        await MarcarAsync(p, $"Carpeta {ficha}");
        await p.EsperarAsync(t => Limpio(t).Contains($"Carpeta {ficha} $20.00 por pieza"), "el renglón del marcado");

        await p.EscribirAsync("promo-porcentaje", "7,5");
        await p.TeclaAsync("promo-porcentaje", "Enter");
        await p.EsperarAsync(t => t.Contains("El porcentaje va con punto, sin comas: 7.5 es siete y medio por ciento."), "el error de la coma");
        Assert.DoesNotContain("Cómo queda", await p.TextoAsync());
        await p.EscribirAsync("promo-porcentaje", "7.5");
        await p.TeclaAsync("promo-porcentaje", "Enter");

        await p.EsperarAsync(t => Limpio(t).Contains("7.5 % menos · Toca 1 producto que se vende"), "cómo queda, con el porcentaje como se leyó");
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Quien puede vender debajo del costo (el encargado) primero ve cuáles quedan debajo; guardarla otra vez sin cambiarla la guarda
    /// así (antes se guardaba sin enseñárselo).
    /// </summary>
    [Fact]
    public async Task Quien_puede_vender_debajo_del_costo_ve_cuales_quedan_y_la_guarda_otra_vez()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var ficha = Ficha();
        var pegamento = await ProductoAsync(c, $"Pegamento {ficha}", 1000);
        await CostoAsync(servicio, pegamento, 700);
        var (p, _) = await PromocionesAsync(c, c.Encargado, ficha);
        await using var __ = p;
        await p.OprimirAsync(await p.PorIdAsync("promo-nueva"));
        await p.EscribirAsync("promo-nombre", $"Barato {ficha}");
        await p.DispararAsync(await p.PorIdAsync("promo-tipo"), "onchange", new ChangeEventArgs { Value = nameof(TipoPromocion.PrecioEspecial) });
        await MarcarAsync(p, $"Pegamento {ficha}");
        await p.EsperarElementoAsync("promo-precio-0");
        await p.EscribirAsync("promo-precio-0", "6");

        await p.OprimirAsync(await p.PorIdAsync("promo-guardar"));
        await p.EsperarAsync(t => t.Contains("Con esta promoción quedan productos debajo del costo"), "lo que queda debajo del costo");
        Assert.Contains($"Quedan debajo del costo: «Pegamento {ficha}»", Limpio((await p.PorIdAsync("promo-bajo-costo")).Texto));
        Assert.DoesNotContain("Listo: se guardó", await p.TextoAsync());
        await CtrlEnterAsync(p, "promo-nombre");

        await p.EsperarAsync(t => t.Contains($"Listo: se guardó la promoción «Barato {ficha}»."), "lo guardado");
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Lo que llega después a un departamento en promoción y con ella quedaría debajo del costo no la lleva: el detalle lo dice
    /// («Fuera por el costo»), con qué hacer para dársela. Con su propio servicio y su propio departamento.
    /// </summary>
    [Fact]
    public async Task El_detalle_dice_lo_que_queda_fuera_por_el_costo()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var c = await PrepararAsync(propio.Fabrica);
        var ficha = Ficha();
        var http = propio.Fabrica.CreateClient();
        var departamento = (await (await http.PostAsJsonAsync("api/catalogo/departamentos",
                new SolicitudDepartamento(c.Encargado.UsuarioId, c.Datos.SucursalId, $"Mitad {ficha}"))).Content.ReadFromJsonAsync<DepartamentosDto>())!
            .Departamentos.Single(d => d.Nombre == $"Mitad {ficha}").Id;
        async Task<Guid> DelDepartamentoAsync(string nombre)
        {
            var guardado = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
                new FichaProductoDto(null, nombre, departamento, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
                [
                    new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                        [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto($"PEF{Guid.NewGuid():N}"[..16])])], [new PrecioFichaDto(null, 1000, 1000)]),
                ])));
            return guardado.Variantes[0].Id!.Value;
        }
        await CostoAsync(propio, await DelDepartamentoAsync($"Con margen {ficha}"), 300);
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
        await c.Cliente.GuardarPromocionAsync(new SolicitudPromocion(c.Encargado.UsuarioId, c.Datos.SucursalId, c.Datos.Id, $"Mitad {ficha}",
            TipoPromocion.Porcentaje, 0, 0, 5000, hoy.AddDays(-1), hoy.AddDays(5), DiasDeLaSemana.Todos, false, [new AlcanceSolicitadoDto(DepartamentoId: departamento)],
            Id: Guid.NewGuid()));
        await CostoAsync(propio, await DelDepartamentoAsync($"Llegó después {ficha}"), 700);
        var (p, _) = await PromocionesAsync(c, c.Encargado, ficha);
        await using var __ = p;

        await p.OprimirAsync(await p.ElementoAsync(e => e.Etiqueta == "tr" && Limpio(e.Texto).StartsWith($"Mitad {ficha}", StringComparison.Ordinal),
            "la promoción en la lista"));
        var fuera = Limpio((await p.EsperarElementoAsync("promo-fuera")).Texto);

        Assert.Contains($"Fuera por el costo (no la llevan: con ella quedarían debajo de lo que cuestan): «Llegó después {ficha}».", fuera);
        Assert.Contains("«Editar» y guárdala con «Vender debajo del costo»", fuera);
        Assert.Contains("Toca 1 producto que se vende", Limpio(await p.TextoAsync()));
        Assert.Empty(p.Errores);
    }
}
