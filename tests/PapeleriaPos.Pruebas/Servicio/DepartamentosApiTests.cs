using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Los departamentos en el POS (Productos › Departamentos), como en eleventa: darlos de alta, cambiarles el nombre,
/// desactivarlos y reactivarlos, con «Productos, precios y etiquetas» de quien entró; nada se borra. Un departamento con
/// productos activos se desactiva pasándolos antes a otro. El filtro, la ficha y el alta rápida ya no ofrecen los
/// desactivados. En el piloto con eleventa (su lectura prendida) solo se consultan.
/// </summary>
public sealed class DepartamentosApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, Guid Sucursal, SesionDto Cajero, SesionDto Encargado);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Lo que dice el servicio al rechazar: un 400 con su mensaje.</summary>
    private static async Task<string> RechazoAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    private static async Task<Caja> PrepararAsync(HttpClient http)
    {
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        return new Caja(http, caja.SucursalId, await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))),
            await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222"))));
    }

    private Task<Caja> PrepararAsync() => PrepararAsync(servicio.Fabrica.CreateClient());

    private static async Task<DepartamentosDto> ListaAsync(Caja c) => (await c.Http.GetFromJsonAsync<DepartamentosDto>("api/catalogo/departamentos"))!;

    private static Task<HttpResponseMessage> AltaAsync(Caja c, SesionDto quien, string nombre) =>
        c.Http.PostAsJsonAsync("api/catalogo/departamentos", new SolicitudDepartamento(quien.UsuarioId, c.Sucursal, nombre));

    private static Task<HttpResponseMessage> NombreAsync(Caja c, SesionDto quien, Guid id, string nombre) =>
        c.Http.PutAsJsonAsync($"api/catalogo/departamentos/{id}/nombre", new SolicitudDepartamento(quien.UsuarioId, c.Sucursal, nombre));

    private static Task<HttpResponseMessage> DesactivarAsync(Caja c, SesionDto quien, Guid id, Guid? pasarA = null) =>
        c.Http.PostAsJsonAsync($"api/catalogo/departamentos/{id}/desactivar", new SolicitudDesactivarDepartamento(quien.UsuarioId, c.Sucursal, pasarA));

    private static Task<HttpResponseMessage> ReactivarAsync(Caja c, SesionDto quien, Guid id) =>
        c.Http.PostAsJsonAsync($"api/catalogo/departamentos/{id}/reactivar", new SolicitudReactivarDepartamento(quien.UsuarioId, c.Sucursal));

    private static async Task<DepartamentoDto> NuevoAsync(Caja c, string nombre) =>
        (await Leer<DepartamentosDto>(await AltaAsync(c, c.Encargado, nombre))).Departamentos.Single(d => d.Nombre == nombre);

    private static FichaProductoDto Ficha(Guid departamento, string nombre, bool activo = true) =>
        new(null, nombre, departamento, null, TipoProducto.Mercancia, Impuesto.Iva16, null, activo, false,
        [
            new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto($"DEP{Guid.NewGuid():N}"[..16])])],
                [new PrecioFichaDto(null, 1000, 1500)]),
        ]);

    private static Task<HttpResponseMessage> GuardarAsync(Caja c, FichaProductoDto ficha) =>
        c.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Sucursal, ficha));

    private static async Task<FichaProductoDto> ProductoAsync(Caja c, Guid departamento, string nombre, bool activo = true) =>
        await Leer<FichaProductoDto>(await GuardarAsync(c, Ficha(departamento, nombre, activo)));

    private static async Task<FichaProductoDto> FichaAsync(Caja c, FichaProductoDto producto) =>
        await Leer<FichaProductoDto>(await c.Http.GetAsync($"api/catalogo/productos/{producto.Id}?sucursalId={c.Sucursal}&usuarioId={c.Encargado.UsuarioId}"));

    private async Task<List<Bitacora>> BitacoraAsync(string contiene)
    {
        await using var db = servicio.Base();
        return await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.EditarCatalogo && b.Detalle.Contains(contiene)).ToListAsync();
    }

    [Fact]
    public async Task Quien_edita_el_catalogo_los_da_de_alta_y_les_cambia_el_nombre_sin_repetirlos()
    {
        var c = await PrepararAsync();

        var lista = await Leer<DepartamentosDto>(await AltaAsync(c, c.Encargado, "  Útiles   de  oficina "));
        var repetido = await RechazoAsync(await AltaAsync(c, c.Encargado, "UTILES de Oficina"));
        var vacio = await RechazoAsync(await AltaAsync(c, c.Encargado, "   "));
        var largo = await RechazoAsync(await AltaAsync(c, c.Encargado, new string('x', Departamento.LargoNombre + 1)));
        var utiles = lista.Departamentos.Single(d => d.Nombre == "Útiles de oficina");
        var comoOtro = await RechazoAsync(await NombreAsync(c, c.Encargado, utiles.Id, "escritura"));
        var renombrado = await Leer<DepartamentosDto>(await NombreAsync(c, c.Encargado, utiles.Id, "Papelería de oficina"));
        // El mismo con otras mayúsculas o acentos no choca consigo mismo.
        var mayusculas = await Leer<DepartamentosDto>(await NombreAsync(c, c.Encargado, utiles.Id, "Papelería de Oficina"));
        var noExiste = await RechazoAsync(await NombreAsync(c, c.Encargado, Guid.NewGuid(), "Lo que sea"));
        var opciones = (await c.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!;

        // Sin espacios de más, activo y sin productos.
        Assert.Equal((true, 0), (utiles.Activo, utiles.ProductosActivos));
        Assert.Null(lista.EnEleventa);
        Assert.Equal("Ya hay un departamento «Útiles de oficina».", repetido);
        Assert.Equal("Escribe el nombre del departamento.", vacio);
        Assert.Contains($"hasta {Departamento.LargoNombre} letras", largo);
        Assert.Equal("Ya hay un departamento «Escritura».", comoOtro);
        Assert.Contains(renombrado.Departamentos, d => d.Id == utiles.Id && d.Nombre == "Papelería de oficina");
        Assert.Contains(mayusculas.Departamentos, d => d.Id == utiles.Id && d.Nombre == "Papelería de Oficina");
        Assert.Equal("Ese departamento ya no existe.", noExiste);
        // Ya se ofrece en la ficha y en el filtro, con su nombre nuevo.
        Assert.Contains(opciones.Departamentos, d => d.Id == utiles.Id && d.Nombre == "Papelería de Oficina");
        Assert.Single(await BitacoraAsync("Alta del departamento «Útiles de oficina»"), b => b.UsuarioId == c.Encargado.UsuarioId);
        Assert.Single(await BitacoraAsync("Cambió el nombre del departamento «Útiles de oficina» a «Papelería de oficina»"));
    }

    [Fact]
    public async Task El_cajero_los_consulta_pero_no_los_cambia_y_el_intento_queda_en_la_bitacora()
    {
        var c = await PrepararAsync();
        var escritura = (await ListaAsync(c)).Departamentos.First(d => d.Nombre == "Escritura");

        var alta = await AltaAsync(c, c.Cajero, "Del cajero");
        var nombre = await NombreAsync(c, c.Cajero, escritura.Id, "Plumas");
        var desactivar = await DesactivarAsync(c, c.Cajero, escritura.Id);
        var reactivar = await ReactivarAsync(c, c.Cajero, escritura.Id);
        var lista = await ListaAsync(c);

        Assert.All(new[] { alta, nombre, desactivar, reactivar }, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Contains("«Productos, precios y etiquetas»", (await alta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        // Los ve igual, y nada cambió.
        Assert.DoesNotContain(lista.Departamentos, d => d.Nombre == "Del cajero");
        Assert.Contains(lista.Departamentos, d => d.Id == escritura.Id && d.Nombre == "Escritura" && d.Activo && d.ProductosActivos > 0);
        await using var db = servicio.Base();
        Assert.True(await db.Bitacora.CountAsync(b => b.UsuarioId == c.Cajero.UsuarioId && b.Accion == AccionControlada.EditarCatalogo
            && b.Resultado == ResultadoBitacora.Negado && b.Detalle == "Intento sin permiso") >= 4);
    }

    [Fact]
    public async Task Sin_productos_se_desactiva_ya_no_se_ofrece_y_se_reactiva()
    {
        var c = await PrepararAsync();
        var temporada = await NuevoAsync(c, "Temporada de prueba");

        var desactivada = await Leer<DepartamentosDto>(await DesactivarAsync(c, c.Encargado, temporada.Id));
        var opciones = (await c.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!;
        var repetido = await RechazoAsync(await AltaAsync(c, c.Encargado, "temporada  de PRUEBA"));
        var productoNuevo = await RechazoAsync(await GuardarAsync(c, Ficha(temporada.Id, "Adorno de prueba")));
        var altaRapida = await RechazoAsync(await c.Http.PostAsJsonAsync("api/catalogo/alta-rapida",
            new SolicitudAltaRapida(c.Cajero.UsuarioId, c.Sucursal, $"75{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}", "Adorno", 900, temporada.Id)));
        var otraVez = await Leer<DepartamentosDto>(await DesactivarAsync(c, c.Encargado, temporada.Id));
        var reactivada = await Leer<DepartamentosDto>(await ReactivarAsync(c, c.Encargado, temporada.Id));
        var opcionesDespues = (await c.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!;

        // Se queda en la lista, desactivado y después de los activos: nada se borra.
        Assert.False(desactivada.Departamentos.Single(d => d.Id == temporada.Id).Activo);
        Assert.Equal(desactivada.Departamentos.OrderByDescending(d => d.Activo).Select(d => d.Id), desactivada.Departamentos.Select(d => d.Id));
        // Ni el filtro ni la ficha lo ofrecen; solo se dice, para el producto que se quedó en él.
        Assert.DoesNotContain(opciones.Departamentos, d => d.Id == temporada.Id);
        Assert.Contains(opciones.Desactivados!, d => d.Id == temporada.Id);
        Assert.Equal("Ya hay un departamento «Temporada de prueba», desactivado: reactívalo en lugar de repetirlo.", repetido);
        Assert.Equal("El departamento «Temporada de prueba» está desactivado: elige otro.", productoNuevo);
        Assert.Equal(productoNuevo, altaRapida);
        Assert.False(otraVez.Departamentos.Single(d => d.Id == temporada.Id).Activo);
        Assert.True(reactivada.Departamentos.Single(d => d.Id == temporada.Id).Activo);
        Assert.Contains(opcionesDespues.Departamentos, d => d.Id == temporada.Id);
        Assert.DoesNotContain(opcionesDespues.Desactivados!, d => d.Id == temporada.Id);
        // Desactivarlo otra vez no hizo nada: una sola vez en la bitácora.
        Assert.Single(await BitacoraAsync("Desactivó el departamento «Temporada de prueba»"));
        Assert.Single(await BitacoraAsync("Reactivó el departamento «Temporada de prueba»"));
    }

    [Fact]
    public async Task Con_productos_activos_pasan_a_otro_departamento_antes_de_desactivarlo_y_los_inactivos_se_quedan()
    {
        var c = await PrepararAsync();
        var manualidades = await NuevoAsync(c, "Manualidades de prueba");
        var arte = await NuevoAsync(c, "Arte de prueba");
        var apagado = await NuevoAsync(c, "Apagado de prueba");
        await Leer<DepartamentosDto>(await DesactivarAsync(c, c.Encargado, apagado.Id));
        var foami = await ProductoAsync(c, manualidades.Id, "Foami de prueba");
        var diamantina = await ProductoAsync(c, manualidades.Id, "Diamantina de prueba");
        var viejo = await ProductoAsync(c, manualidades.Id, "Estambre de prueba", activo: false);

        var conProductos = (await ListaAsync(c)).Departamentos.Single(d => d.Id == manualidades.Id);
        var sinDestino = await RechazoAsync(await DesactivarAsync(c, c.Encargado, manualidades.Id));
        var aSiMismo = await RechazoAsync(await DesactivarAsync(c, c.Encargado, manualidades.Id, manualidades.Id));
        var aUnoApagado = await RechazoAsync(await DesactivarAsync(c, c.Encargado, manualidades.Id, apagado.Id));
        var desactivada = await Leer<DepartamentosDto>(await DesactivarAsync(c, c.Encargado, manualidades.Id, arte.Id));

        Assert.Equal(2, conProductos.ProductosActivos);
        Assert.Equal("«Manualidades de prueba» tiene 2 productos activos: elige a qué departamento pasarlos para desactivarlo.", sinDestino);
        Assert.Equal("Elige otro departamento, uno activo, para pasar sus productos.", aSiMismo);
        Assert.Equal(aSiMismo, aUnoApagado);
        Assert.Equal((false, 0), desactivada.Departamentos.Where(d => d.Id == manualidades.Id).Select(d => (d.Activo, d.ProductosActivos)).Single());
        Assert.Equal(2, desactivada.Departamentos.Single(d => d.Id == arte.Id).ProductosActivos);
        // Los activos pasaron a «Arte de prueba»; el inactivo se quedó, con su historia.
        Assert.Equal(arte.Id, (await FichaAsync(c, foami)).DepartamentoId);
        Assert.Equal(arte.Id, (await FichaAsync(c, diamantina)).DepartamentoId);
        var estambre = await FichaAsync(c, viejo);
        Assert.Equal(manualidades.Id, estambre.DepartamentoId);
        Assert.Single(await BitacoraAsync("Desactivó el departamento «Manualidades de prueba»; sus 2 productos activos pasaron a «Arte de prueba»"));

        // El inactivo se sigue guardando en su departamento desactivado, pero no se reactiva ahí: se elige otro.
        await Leer<FichaProductoDto>(await GuardarAsync(c, estambre with { Nombre = "Estambre grueso de prueba" }));
        var reactivarAhi = await RechazoAsync(await GuardarAsync(c, estambre with { Activo = true }));
        var reactivadoEnOtro = await Leer<FichaProductoDto>(await GuardarAsync(c, estambre with { Activo = true, DepartamentoId = arte.Id }));
        Assert.Equal("El departamento «Manualidades de prueba» está desactivado: elige otro.", reactivarAhi);
        Assert.Equal((true, arte.Id), (reactivadoEnOtro.Activo, reactivadoEnOtro.DepartamentoId));
    }

    /// <summary>
    /// Un producto activo que se quedó en un departamento desactivado (llegó así de eleventa, o es de antes) se sigue editando
    /// sin cambiarle el departamento: no se le obliga a elegir otro por cambiarle el precio. Otro no se pasa a ese departamento.
    /// </summary>
    [Fact]
    public async Task El_producto_que_se_quedo_en_un_departamento_desactivado_se_sigue_editando()
    {
        var c = await PrepararAsync();
        var legado = await NuevoAsync(c, "Legado de prueba");
        var otroDepartamento = await NuevoAsync(c, "Vigente de prueba");
        var clip = await ProductoAsync(c, legado.Id, "Clip de prueba");
        var grapa = await ProductoAsync(c, otroDepartamento.Id, "Grapa de prueba");
        await using (var db = servicio.Base())
        {
            (await db.Departamentos.SingleAsync(d => d.Id == legado.Id)).Activo = false;
            await db.SaveChangesAsync();
        }

        var conPrecio = await Leer<FichaProductoDto>(await GuardarAsync(c, clip with
        {
            Variantes = [clip.Variantes[0] with { Precios = [new PrecioFichaDto(null, 1000, 1800)] }],
        }));
        var pasarAlDesactivado = await RechazoAsync(await GuardarAsync(c, grapa with { DepartamentoId = legado.Id }));
        var lista = await ListaAsync(c);

        Assert.Equal(legado.Id, conPrecio.DepartamentoId);
        Assert.Equal(1800, conPrecio.Variantes[0].Precios.Single().PrecioCentavos);
        Assert.Equal("El departamento «Legado de prueba» está desactivado: elige otro.", pasarAlDesactivado);
        // Se ve en la lista con su producto activo, para pasarlo a otro.
        Assert.Equal((false, 1), lista.Departamentos.Where(d => d.Id == legado.Id).Select(d => (d.Activo, d.ProductosActivos)).Single());
    }

    /// <summary>
    /// En el piloto con eleventa (Eleventa:Activo, su lectura cada pocos minutos), los departamentos se dan de alta y se
    /// cambian en eleventa (dueño, 2026-10-10): aquí se consultan, y el servicio rechaza los cambios con el mismo aviso que
    /// enseña la página. Si no, la siguiente lectura regresaría el nombre y si está activo como allá.
    /// </summary>
    [Fact]
    public async Task En_el_piloto_con_eleventa_solo_se_consultan()
    {
        await using var fabrica = servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));
        var c = await PrepararAsync(fabrica.CreateClient());
        var antes = await ListaAsync(c);
        var escritura = antes.Departamentos.First(d => d.Nombre == "Escritura");

        var alta = await RechazoAsync(await AltaAsync(c, c.Encargado, "Del piloto"));
        var nombre = await RechazoAsync(await NombreAsync(c, c.Encargado, escritura.Id, "Plumas del piloto"));
        var desactivar = await RechazoAsync(await DesactivarAsync(c, c.Encargado, escritura.Id));
        var reactivar = await RechazoAsync(await ReactivarAsync(c, c.Encargado, escritura.Id));
        var delCajero = await AltaAsync(c, c.Cajero, "Del piloto");
        var despues = await ListaAsync(c);

        Assert.Equal(RegistroDepartamentos.SeCambianEnEleventa, antes.EnEleventa);
        Assert.All(new[] { alta, nombre, desactivar, reactivar }, m => Assert.Equal(RegistroDepartamentos.SeCambianEnEleventa, m));
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal(antes.Departamentos, despues.Departamentos);
        // Sin el piloto, el mismo servicio los deja cambiar.
        Assert.Null((await ListaAsync(await PrepararAsync())).EnEleventa);
    }
}
