using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Productos (F3) › Departamentos, sin navegador, contra el servicio con datos ficticios: la lista con cuántos productos
/// activos tiene cada uno; dar de alta, cambiar el nombre (Enter guarda, Esc lo deja), desactivar pasando antes sus
/// productos a otro, y reactivar; el filtro de la lista y la ficha con los activos (y el suyo, aunque esté desactivado);
/// quien no tiene el permiso solo los consulta, y en el piloto con eleventa se cambian allá.
/// </summary>
public sealed class DepartamentosTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Cajero, SesionDto Encargado);

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
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("encargado", "2222"));
    }

    /// <summary>La página de Productos de quien entró, como la abre F3, con lo que se recordaba de ella.</summary>
    private static async Task<(PantallaSinNavegador Pantalla, Productos Productos)> ProductosAsync(Caja c, SesionDto quien, ProductosVistos? estado = null)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        var productos = await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = quien,
            [nameof(Productos.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Productos.Estado)] = estado ?? new ProductosVistos(),
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarElementoAsync("prod-0");
        return (p, productos);
    }

    /// <summary>«Departamentos», abajo de la lista: se abre en el detalle, con su tabla.</summary>
    private static async Task AbrirDepartamentosAsync(PantallaSinNavegador p)
    {
        await p.OprimirAsync(await p.BotonAsync("Departamentos"));
        await p.EsperarAsync(t => t.Contains("Productos activos"), "la tabla de los departamentos");
    }

    private static Task<bool> EscAsync(PantallaSinNavegador p, Productos productos) => p.Dispatcher.InvokeAsync(productos.CerrarAsync);

    /// <summary>Un botón del renglón de ese departamento (los renglones tienen los mismos botones).</summary>
    private static async Task<ElementoDibujado> BotonDelRenglonAsync(PantallaSinNavegador p, Guid departamento, string texto)
    {
        var elementos = (await p.LeerAsync()).Elementos;
        var renglon = elementos.FindIndex(e => e.Id == $"depto-{departamento}");
        Assert.True(renglon >= 0, $"No está el renglón del departamento:\n{await p.TextoAsync()}");
        return elementos.Skip(renglon + 1).First(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto).StartsWith(texto, StringComparison.Ordinal));
    }

    private static async Task<string> RenglonAsync(PantallaSinNavegador p, Guid departamento) =>
        PantallaSinNavegador.Limpio((await p.ElementoAsync(e => e.Id == $"depto-{departamento}", "el renglón del departamento")).Texto);

    /// <summary>Lo que ofrece el filtro de la lista: sus opciones, sin la de «Todos».</summary>
    private static async Task<List<string?>> FiltroAsync(PantallaSinNavegador p)
    {
        var elementos = (await p.LeerAsync()).Elementos;
        var filtro = elementos.FindIndex(e => e.Etiqueta == "select" && e.Atributos.GetValueOrDefault("aria-label") as string == "Departamento");
        return elementos.Skip(filtro + 1).TakeWhile(e => e.Etiqueta == "option").Select(e => e.Valor).Where(v => v is { Length: > 0 }).ToList();
    }

    /// <summary>Espera a que el filtro de la lista ofrezca lo que se dice (la página lo pone al día después de cada cambio).</summary>
    private static async Task EsperarFiltroAsync(PantallaSinNavegador p, Func<List<string?>, bool> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!condicion(await FiltroAsync(p)))
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"El filtro no llegó a {que}: {string.Join(", ", await FiltroAsync(p))}");
            await Task.Delay(20);
        }
    }

    private static async Task<FichaProductoDto> ProductoAsync(Caja c, Guid departamento, string nombre) =>
        await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, departamento, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                    [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto($"PAN{Guid.NewGuid():N}"[..16])])],
                    [new PrecioFichaDto(null, 1000, 2500)]),
            ])));

    private static async Task<Guid> DepartamentoAsync(Caja c, string nombre) =>
        (await c.Cliente.DarDeAltaDepartamentoAsync(new SolicitudDepartamento(c.Encargado.UsuarioId, c.Datos.SucursalId, nombre)))
            .Departamentos.Single(d => d.Nombre == nombre).Id;

    [Fact]
    public async Task El_encargado_da_de_alta_cambia_el_nombre_desactiva_pasando_sus_productos_y_reactiva()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var pinturas = await DepartamentoAsync(c, "Pinturas de pantalla");
        var acuarela = await ProductoAsync(c, pinturas, "Acuarela de pantalla");
        var escritura = (await c.Cliente.DepartamentosAsync()).Departamentos.Single(d => d.Nombre == "Escritura");
        var (p, productos) = await ProductosAsync(c, c.Encargado);
        await using var _ = p;

        await AbrirDepartamentosAsync(p);
        Assert.StartsWith($"Escritura {Formato.Numero(escritura.ProductosActivos)} Cambiar el nombre Desactivar", await RenglonAsync(p, escritura.Id));
        Assert.Contains(await FiltroAsync(p), v => v == pinturas.ToString());

        // «Nuevo departamento»: Enter guarda; lo repetido (sin importar acentos ni mayúsculas) no, y Esc lo deja.
        await p.OprimirAsync(await p.BotonAsync("Nuevo departamento"));
        await p.EscribirAsync("depto-nuevo", "  Útiles de pantalla ");
        await p.TeclaAsync("depto-nuevo", "Enter");
        await p.EsperarAsync(t => t.Contains("«Útiles de pantalla» quedó dado de alta"), "el alta");
        var utiles = (await c.Cliente.DepartamentosAsync()).Departamentos.Single(d => d.Nombre == "Útiles de pantalla").Id;
        Assert.Contains("on", (await p.PorIdAsync($"depto-{utiles}")).Clase);
        await EsperarFiltroAsync(p, f => f.Contains(utiles.ToString()), "el nuevo en el filtro");
        await p.OprimirAsync(await p.BotonAsync("Nuevo departamento"));
        await p.EscribirAsync("depto-nuevo", "UTILES de pantalla");
        await p.TeclaAsync("depto-nuevo", "Enter");
        await p.EsperarAsync(t => t.Contains("Ya hay un departamento «Útiles de pantalla»."), "el aviso de repetido");
        Assert.True(await EscAsync(p, productos));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "depto-nuevo");
        Assert.Contains("Productos activos", await p.TextoAsync());

        // Cambiar el nombre, ahí mismo.
        await p.OprimirAsync(await BotonDelRenglonAsync(p, utiles, "Cambiar el nombre"));
        Assert.Equal("Útiles de pantalla", (await p.PorIdAsync("depto-nombre")).Valor);
        await p.EscribirAsync("depto-nombre", "Útiles escolares de pantalla");
        await p.TeclaAsync("depto-nombre", "Enter");
        await p.EsperarAsync(t => t.Contains("Ahora se llama «Útiles escolares de pantalla»."), "el nombre nuevo");
        Assert.StartsWith("Útiles escolares de pantalla", await RenglonAsync(p, utiles));

        // Con un producto activo, antes de desactivarlo se elige a cuál pasa; el número lo enseña en la lista.
        await p.OprimirAsync(await BotonDelRenglonAsync(p, pinturas, "Desactivar"));
        await p.EsperarAsync(t => PantallaSinNavegador.Limpio(t).Contains("«Pinturas de pantalla» tiene 1 producto activo. Para desactivarlo, pásalo a"), "a dónde pasa");
        await p.OprimirAsync(await p.BotonAsync("Pasarlo y desactivar"));
        Assert.Contains("Elige a qué departamento pasa.", await p.TextoAsync());
        await p.DispararAsync(await p.PorIdAsync("depto-pasar"), "onchange", new ChangeEventArgs { Value = utiles.ToString() });
        await p.OprimirAsync(await p.BotonAsync("Pasarlo y desactivar"));
        await p.EsperarAsync(t => t.Contains("«Pinturas de pantalla» quedó desactivado y su producto activo pasó a «Útiles escolares de pantalla»."),
            "el desactivado");
        Assert.Contains("desactivado", await RenglonAsync(p, pinturas));
        await EsperarFiltroAsync(p, f => !f.Contains(pinturas.ToString()), "el filtro sin el desactivado");
        Assert.Equal(utiles, (await c.Cliente.FichaProductoAsync(acuarela.Id!.Value, c.Datos.SucursalId, c.Encargado.UsuarioId))!.DepartamentoId);

        // El número de productos activos los enseña en la lista de la izquierda.
        var verlos = await BotonDelRenglonAsync(p, utiles, "1");
        Assert.Equal("Verlos en la lista", verlos.Atributos.GetValueOrDefault("title"));
        await p.OprimirAsync(verlos);
        await p.EsperarAsync(t => t.Contains("1 producto."), "la lista del departamento");
        Assert.Contains("Acuarela de pantalla", PantallaSinNavegador.Limpio((await p.PorIdAsync("prod-0")).Texto));
        Assert.Equal(utiles.ToString(), (await p.ElementoAsync(e => e.Atributos.GetValueOrDefault("aria-label") as string == "Departamento", "el filtro")).Valor);

        // Reactivar: se vuelve a ofrecer.
        await p.OprimirAsync(await BotonDelRenglonAsync(p, pinturas, "Reactivar"));
        await p.EsperarAsync(t => t.Contains("«Pinturas de pantalla» se vuelve a ofrecer"), "el reactivado");
        await EsperarFiltroAsync(p, f => f.Contains(pinturas.ToString()), "el reactivado en el filtro");

        // Esc, sin nada abierto, regresa al producto; otro Esc ya no es de la página.
        Assert.True(await EscAsync(p, productos));
        Assert.DoesNotContain("Productos activos", await p.TextoAsync());
        Assert.False(await EscAsync(p, productos));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Quien_no_tiene_el_permiso_los_consulta_y_ve_quien_los_cambia()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var (p, _) = await ProductosAsync(c, c.Cajero);
        await using var __ = p;

        await AbrirDepartamentosAsync(p);
        await p.EsperarAsync(t => t.Contains("«Productos, precios y etiquetas»"), "quién los cambia");

        var botones = (await p.LeerAsync()).Elementos.Where(e => e.Etiqueta == "button").Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        Assert.DoesNotContain("Nuevo departamento", botones);
        Assert.DoesNotContain(botones, b => b.StartsWith("Cambiar el nombre", StringComparison.Ordinal) || b == "Desactivar" || b == "Reactivar");
        Assert.Contains("Escritura", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_el_piloto_con_eleventa_se_consultan_y_dice_que_se_cambian_alla()
    {
        await using var fabrica = servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));
        var c = await PrepararAsync(fabrica);
        var (p, _) = await ProductosAsync(c, c.Encargado);
        await using var __ = p;

        await AbrirDepartamentosAsync(p);

        Assert.Contains(RegistroDepartamentos.SeCambianEnEleventa, await p.TextoAsync());
        var botones = (await p.LeerAsync()).Elementos.Where(e => e.Etiqueta == "button").Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        Assert.DoesNotContain("Nuevo departamento", botones);
        Assert.DoesNotContain(botones, b => b.StartsWith("Cambiar el nombre", StringComparison.Ordinal) || b == "Desactivar");
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Un producto que se quedó en un departamento desactivado (llegó así de eleventa, o es de antes): su detalle y su ficha
    /// dicen el suyo, «(desactivado)», aunque ya no se ofrezca a los demás.
    /// </summary>
    [Fact]
    public async Task La_ficha_sigue_diciendo_el_departamento_desactivado_del_producto()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var archivo = await DepartamentoAsync(c, "Archivo de pantalla");
        await ProductoAsync(c, archivo, "Folder de pantalla único");
        await using (var db = servicio.Base())
        {
            (await db.Departamentos.SingleAsync(d => d.Id == archivo)).Activo = false;
            await db.SaveChangesAsync();
        }
        var (p, _) = await ProductosAsync(c, c.Encargado, new ProductosVistos { Texto = "Folder de pantalla único" });
        await using var __ = p;

        await p.EsperarAsync(t => t.Contains("Archivo de pantalla (desactivado)"), "su departamento en el detalle");
        Assert.DoesNotContain(await FiltroAsync(p), v => v == archivo.ToString());
        await p.OprimirAsync(await p.BotonAsync("Editar"));
        await p.EsperarAsync(t => t.Contains("Presentaciones y códigos"), "la ficha");

        var elementos = (await p.LeerAsync()).Elementos;
        var opcion = elementos.Single(e => e.Etiqueta == "option" && e.Valor == archivo.ToString());
        Assert.Equal("Archivo de pantalla (desactivado)", PantallaSinNavegador.Limpio(opcion.Texto));
        Assert.Contains(elementos, e => e.Etiqueta == "select" && e.Valor == archivo.ToString());
        Assert.Empty(p.Errores);
    }
}
