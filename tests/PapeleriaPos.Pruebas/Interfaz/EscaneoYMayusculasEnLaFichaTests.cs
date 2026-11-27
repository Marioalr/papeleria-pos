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
/// Dos encargos del dueño para la ficha del producto (2026-11-26), sin navegador y contra el servicio:
/// <list type="bullet">
/// <item>«En la descripción de un producto agrega el reconocimiento de código de barras donde no van, por ejemplo la cantidad»: toda la
/// ficha es data-sin-codigos (y cada renglón de variante y de caja, con su propio código), y lo que hace caja.js con la ráfaga del
/// lector ahí lo prueba CajaJsTests. Aquí, que la ficha se marca así, que sus campos de códigos no, y que lo que caja.js le hace (el
/// campo vuelve a lo que tenía y el código llega a «Código de barras» por su «change») queda al guardar.</item>
/// <item>«El inicio con mayúscula va en todos los apartados, por ejemplo marca, departamento, etc»: el nombre, la variante, la caja, la
/// marca nueva, el departamento, el proveedor y la temporada se guardan con la primera letra en mayúscula (solo esa); lo que ya estaba
/// guardado así y no se toca se queda como estaba.</item>
/// </list>
/// </summary>
public sealed class EscaneoYMayusculasEnLaFichaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Encargado, SesionDto Dueno);

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
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("encargado", "2222"), await cliente.EntrarAsync("dueno", "3333"));
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

    private static async Task TeclearAsync(PantallaSinNavegador p, string id, string valor)
    {
        var campo = await p.PorIdAsync(id);
        await p.DispararAsync(campo, campo.Eventos.ContainsKey("oninput") ? "oninput" : "onchange", new ChangeEventArgs { Value = valor });
    }

    private static FichaProductoDto Ficha(string nombre, Guid departamento, string? marca, params VarianteFichaDto[] variantes) =>
        new(null, nombre, departamento, marca, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false, variantes);

    private static VarianteFichaDto Variante(string descripcion, string codigo, params PresentacionFichaDto[] cajas) =>
        new(null, descripcion, UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)]), .. cajas],
            [new PrecioFichaDto(null, 1000, 1500)]);

    // ── El lector donde no van códigos ──

    [Fact]
    public async Task La_ficha_marca_donde_no_van_codigos_y_el_codigo_que_caja_js_pasa_al_codigo_de_barras_se_guarda()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c);
        var elementos = (await p.LeerAsync()).Elementos;
        Assert.True(elementos.Single(e => e.Id == "ficha-campos").Atributos.ContainsKey("data-sin-codigos"));
        // El de códigos es el destino (data-escaneo), no uno de los que no llevan.
        Assert.Equal("varios", elementos.Single(e => e.Id == "ficha-codigo").Atributos["data-escaneo"]?.ToString());

        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var codigo = $"75{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";
        var nombre = $"Escaneado en la cantidad {Guid.NewGuid():N}"[..36];
        await TeclearAsync(p, "ficha-nombre", nombre);
        await TeclearAsync(p, "ficha-departamento", opciones.Departamentos[0].Id.ToString());
        await TeclearAsync(p, "ficha-precio", "15");
        // Lo que hace caja.js con la ráfaga en el precio: lo tecleado llega (su input), el campo vuelve a lo que tenía (su input y su
        // change) y el código va a «Código de barras» (su input y su change), sin Enter.
        await TeclearAsync(p, "ficha-precio", "15" + codigo);
        await TeclearAsync(p, "ficha-precio", "15");
        await p.DispararAsync(await p.PorIdAsync("ficha-precio"), "onchange", new ChangeEventArgs { Value = "15" });
        await p.DispararAsync(await p.PorIdAsync("ficha-codigo"), "onchange", new ChangeEventArgs { Value = codigo });
        Assert.Equal("15", (await p.PorIdAsync("ficha-precio")).Valor);

        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        await p.EsperarAsync(t => t.Contains($"Se guardó «{nombre}»."), "lo guardado");
        await using var db = servicio.Base();
        var guardado = await db.CodigosBarras.Where(x => x.Codigo == codigo).Select(x => x.Presentacion!.Variante!.Producto!.Nombre).SingleAsync();
        Assert.Equal(nombre, guardado);
        var precio = await db.Precios.Where(x => x.Variante!.Producto!.Nombre == nombre).Select(x => x.PrecioCentavos).SingleAsync();
        Assert.Equal(1500, precio);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Con_varias_variantes_cada_renglon_lleva_su_codigo_y_la_alta_rapida_no_deja_un_codigo_en_el_nombre()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        await using var p = await NuevoAsync(c);
        await p.DispararAsync(await p.PorIdAsync("ficha-varias"), "onchange", new ChangeEventArgs { Value = true });
        await p.EsperarElementoAsync("var-nombre-0");
        var elementos = (await p.LeerAsync()).Elementos;
        // El renglón de la variante es su propio grupo: lo que se escanea en su precio va a su código, no a otro.
        Assert.Contains(elementos, e => e.Clase?.Contains("ficha-v-fila") == true && e.Atributos.ContainsKey("data-sin-codigos"));

        var alta = new PantallaSinNavegador(c.Servicios);
        await using var _ = alta;
        await alta.AbrirAsync<AltaRapida>(new()
        {
            [nameof(AltaRapida.Codigo)] = "7500000000017",
            [nameof(AltaRapida.Caja)] = c.Datos,
            [nameof(AltaRapida.Sesion)] = c.Encargado,
        });
        Assert.True((await alta.PorIdAsync("alta-nombre")).Atributos.ContainsKey("data-sin-codigos"));
    }

    // ── La mayúscula al inicio ──

    [Fact]
    public async Task El_producto_su_variante_su_caja_y_la_marca_nueva_se_guardan_con_mayuscula_al_inicio_y_nada_mas()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var caja = new PresentacionFichaDto(null, "paquete de 12", 12_000, false, [new CodigoFichaDto($"PQ{sufijo}")]);
        var guardada = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            Ficha($"cuaderno Scribe {sufijo}", opciones.Departamentos[0].Id, $"scribe de México {sufijo}",
                Variante("raya azul", $"CR{sufijo}", caja), Variante("¿cuadro? chico", $"CC{sufijo}"))));

        Assert.Equal($"Cuaderno Scribe {sufijo}", guardada.Nombre);
        Assert.Equal($"Scribe de México {sufijo}", guardada.Marca);
        Assert.Equal(new[] { "Raya azul", "¿Cuadro? chico" }.Order(StringComparer.Ordinal), guardada.Variantes.Select(v => v.Descripcion).Order(StringComparer.Ordinal));
        var raya = guardada.Variantes.Single(v => v.Descripcion == "Raya azul");
        Assert.Contains(raya.Presentaciones, x => x.Nombre == "Paquete de 12");
        // El precio de la caja la sigue encontrando (por su nombre, sin importar mayúsculas).
        Assert.Contains(raya.Presentaciones, x => x.Nombre == "Pieza");

        // Otro con la misma marca escrita en minúsculas: es la misma, como estaba.
        var otro = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            Ficha($"iPhone funda {sufijo}", opciones.Departamentos[0].Id, $"SCRIBE de méxico {sufijo}", Variante("", $"IF{sufijo}"))));
        Assert.Equal($"Scribe de México {sufijo}", otro.Marca);
        // «iPhone» se escribió así a propósito: se queda.
        Assert.Equal($"iPhone funda {sufijo}", otro.Nombre);
    }

    [Fact]
    public async Task Lo_que_ya_estaba_guardado_en_minusculas_y_no_se_toca_se_queda_como_estaba()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var guardada = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            Ficha($"Goma {sufijo}", opciones.Departamentos[0].Id, null, Variante("", $"GM{sufijo}"))));
        // Como llegaron de antes (o de eleventa): en minúsculas.
        await using (var db = servicio.Base())
        {
            var producto = await db.Productos.SingleAsync(x => x.Id == guardada.Id);
            producto.Nombre = $"goma de migajón {sufijo}";
            await db.SaveChangesAsync();
        }
        var ficha = (await c.Cliente.FichaProductoAsync(guardada.Id!.Value, c.Datos.SucursalId, c.Encargado.UsuarioId))!;
        Assert.Equal($"goma de migajón {sufijo}", ficha.Nombre);

        // Se guarda cambiando solo el precio: el nombre no se toca.
        var conOtroPrecio = ficha with { Variantes = [ficha.Variantes[0] with { Precios = [new PrecioFichaDto(null, 1000, 1800)] }] };
        var otraVez = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId, conOtroPrecio));
        Assert.Equal($"goma de migajón {sufijo}", otraVez.Nombre);

        // Si se cambia, ya con mayúscula.
        var renombrada = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            otraVez with { Nombre = $"goma blanca {sufijo}" }));
        Assert.Equal($"Goma blanca {sufijo}", renombrada.Nombre);
    }

    [Fact]
    public async Task El_departamento_el_proveedor_y_la_temporada_tambien_empiezan_con_mayuscula()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var sufijo = Guid.NewGuid().ToString("N")[..8];

        var departamentos = await c.Cliente.DarDeAltaDepartamentoAsync(new SolicitudDepartamento(c.Encargado.UsuarioId, c.Datos.SucursalId, $"útiles de arte {sufijo}"));
        var departamento = departamentos.Departamentos.Single(d => d.Nombre.EndsWith(sufijo, StringComparison.Ordinal));
        Assert.Equal($"Útiles de arte {sufijo}", departamento.Nombre);
        departamentos = await c.Cliente.CambiarNombreDepartamentoAsync(departamento.Id,
            new SolicitudDepartamento(c.Encargado.UsuarioId, c.Datos.SucursalId, $"arte y dibujo {sufijo}"));
        Assert.Equal($"Arte y dibujo {sufijo}", departamentos.Departamentos.Single(d => d.Id == departamento.Id).Nombre);

        var proveedor = await c.Cliente.GuardarProveedorAsync(new SolicitudGuardarProveedor(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new ProveedorDto(null, $"papelera del Norte {sufijo}", null, "abc010101aa1", null)));
        Assert.Equal($"Papelera del Norte {sufijo}", proveedor.Nombre);
        // El RFC es una clave: se queda como siempre (en mayúsculas), no «Abc…».
        Assert.Equal("ABC010101AA1", proveedor.Rfc);

        var mostrador = await c.Cliente.NuevoExhibidorAsync(new SolicitudNuevoExhibidor(c.Dueno.UsuarioId, c.Datos.SucursalId, $"regreso a clases {sufijo}",
            TipoExhibidor.Temporada));
        Assert.Contains(mostrador.Exhibidores, e => e.Nombre == $"Regreso a clases {sufijo}");
    }
}
