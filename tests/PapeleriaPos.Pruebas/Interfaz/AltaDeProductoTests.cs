using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Crear un producto en F3, con los pedidos del dueño del 2026-11-16: «Nuevo departamento» ahí mismo (el producto queda en él),
/// las marcas ya capturadas se sugieren al escribir, la casilla del costo, el margen de cada precio (escribirlo pone el precio) y
/// «Agregar otro» (antes «Agregar variante») con lo de la de arriba y el nombre en blanco. El costo solo lo cambia quien ve los costos.
/// </summary>
public sealed class AltaDeProductoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

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
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("encargado", "2222"),
            await cliente.EntrarAsync("dueno", "3333"));
    }

    private static async Task<PantallaSinNavegador> ProductosAsync(Caja c, SesionDto quien)
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
        return p;
    }

    private static async Task<List<ElementoDibujado>> CamposAsync(PantallaSinNavegador p, string clase) =>
        (await p.LeerAsync()).Elementos.Where(e => e.Etiqueta == "input" && e.Clase == clase).ToList();

    [Fact]
    public async Task El_dueno_crea_el_departamento_ahi_mismo_y_el_margen_pone_el_precio_de_cada_variante()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var sufijo = Guid.NewGuid().ToString("N")[..6];
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        // Una marca ya capturada.
        await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Dueno.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, $"Con marca {sufijo}", opciones.Departamentos[0].Id, $"Marquita{sufijo}", TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
                [new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [])], [new PrecioFichaDto(null, 1000, 1000)])])));

        await using var p = await ProductosAsync(c, c.Dueno);
        await p.OprimirAsync(await p.BotonAsync("Nuevo producto"));
        await p.EsperarElementoAsync("ficha-nombre");
        var nombre = $"Libreta alta {sufijo}";
        await p.DispararAsync(await p.PorIdAsync("ficha-nombre"), "onchange", new ChangeEventArgs { Value = nombre });

        // El departamento, ahí mismo.
        // Desde el 2026-11-21, «+ Nuevo departamento…» es la primera opción de la lista.
        await p.DispararAsync(await p.PorIdAsync("ficha-departamento"), "onchange", new ChangeEventArgs { Value = FichaProducto.OpcionNuevoDepartamento });
        await p.EscribirAsync("ficha-departamento-nombre", $"Libretas {sufijo}");
        await p.OprimirAsync(await p.BotonAsync("Crear y usarlo"));
        await p.EsperarAsync(t => !t.Contains("Nombre del departamento nuevo"), "el departamento creado");
        var departamento = (await c.Cliente.DepartamentosAsync()).Departamentos.Single(d => d.Nombre == $"Libretas {sufijo}");
        Assert.Equal(departamento.Id.ToString(), (await p.PorIdAsync("ficha-departamento")).Valor);

        // La marca: se sugiere la capturada.
        await p.EscribirAsync("ficha-marca", "marquita");
        await p.OprimirAsync(await p.BotonAsync($"Marquita{sufijo}"));
        Assert.Equal($"Marquita{sufijo}", (await p.PorIdAsync("ficha-marca")).Valor);

        // El costo y el margen: 50 % sobre $10 son $20.
        await p.DispararAsync((await CamposAsync(p, "umbral costo"))[0], "onchange", new ChangeEventArgs { Value = "10" });
        await p.DispararAsync((await CamposAsync(p, "umbral margen"))[0], "onchange", new ChangeEventArgs { Value = "50" });
        Assert.Contains(await CamposAsync(p, "umbral"), e => e.Valor == "20.00");

        // La segunda variante (en «Colores, tamaños…»: «viene en varios…» y «Agregar otro»), con lo de la primera y sin nombre.
        await p.DispararAsync(await p.PorIdAsync("ficha-varias"), "onchange", new ChangeEventArgs { Value = true });
        await p.OprimirAsync(await p.BotonAsync("+ Agregar otro"));
        Assert.Equal(["10", "10"], (await CamposAsync(p, "umbral costo")).Select(e => e.Valor));
        Assert.Equal(2, (await CamposAsync(p, "umbral")).Count(e => e.Valor == "20.00"));
        var variantes = (await p.LeerAsync()).Elementos.Where(e => e.Etiqueta == "input" && e.Atributos.GetValueOrDefault("placeholder") as string == "Azul, Carta…").ToList();
        Assert.Equal(2, variantes.Count);
        Assert.All(variantes, v => Assert.True(string.IsNullOrEmpty(v.Valor)));
        await p.DispararAsync(variantes[0], "onchange", new ChangeEventArgs { Value = "Rayas" });
        await p.DispararAsync((await p.LeerAsync()).Elementos.Where(e => e.Etiqueta == "input" && e.Atributos.GetValueOrDefault("placeholder") as string == "Azul, Carta…").ToList()[1],
            "onchange", new ChangeEventArgs { Value = "Cuadros" });

        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "lo guardado");
        Assert.Empty(p.Errores);

        await using var db = servicio.Base();
        var producto = await db.Productos.Include(x => x.Marca).Include(x => x.Variantes).SingleAsync(x => x.Nombre == nombre);
        Assert.Equal(departamento.Id, producto.DepartamentoId);
        Assert.Equal($"Marquita{sufijo}", producto.Marca?.Nombre);
        var ficha = (await c.Cliente.FichaProductoAsync(producto.Id, c.Datos.SucursalId, c.Dueno.UsuarioId))!;
        Assert.All(ficha.Variantes, v =>
        {
            Assert.Equal(1000, v.CostoCentavos);
            Assert.Equal(2000, Assert.Single(v.Precios).PrecioCentavos);
        });
        Assert.Equal(2, await db.CostosCapturados.CountAsync(x => producto.Variantes.Select(v => v.Id).Contains(x.VarianteId)));
    }

    [Fact]
    public async Task El_cajero_no_cambia_el_costo_y_guardar_sin_cambiarlo_no_escribe_otro()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var nombre = $"Costo a mano {Guid.NewGuid():N}"[..22];
        FichaProductoDto Ficha(Guid? id, Guid? variante, long? costo) => new(id, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [new VarianteFichaDto(variante, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [])], [new PrecioFichaDto(null, 1000, 2500)],
                CostoCentavos: costo)]);
        var guardada = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Dueno.UsuarioId, c.Datos.SucursalId, Ficha(null, null, 1200)));
        var variante = guardada.Variantes.Single().Id!.Value;

        // Con el mismo costo no queda otro.
        var leida = (await c.Cliente.FichaProductoAsync(guardada.Id!.Value, c.Datos.SucursalId, c.Dueno.UsuarioId))!;
        await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Dueno.UsuarioId, c.Datos.SucursalId,
            leida with { Variantes = [leida.Variantes.Single() with { CostoCentavos = 1200 }] }));
        await using (var db = servicio.Base())
            Assert.Equal(1, await db.CostosCapturados.CountAsync(x => x.VarianteId == variante));

        // El cajero no ve los costos: no lo cambia.
        var error = await Assert.ThrowsAsync<ErrorCaja>(() => c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Cajero.UsuarioId, c.Datos.SucursalId,
            Ficha(guardada.Id, variante, 900))));
        Assert.NotNull(error.Message);
        Assert.Equal(1200, (await c.Cliente.FichaProductoAsync(guardada.Id.Value, c.Datos.SucursalId, c.Dueno.UsuarioId))!.Variantes.Single().CostoCentavos);
    }
}
