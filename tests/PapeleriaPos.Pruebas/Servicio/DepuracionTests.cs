using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Servicio;

public class DepuracionTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado)> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        return (http, caja, cajero, encargado);
    }

    /// <summary>Un producto con su pieza, una caja de 12, su código, su precio y existencia (y opcionalmente su ID de eleventa).</summary>
    private async Task<(Guid Variante, string Codigo)> ProductoAsync(Guid sucursalId, string nombre, int existencia, long costoPieza, UnidadBase unidad = UnidadBase.Pieza,
        string? eleventa = null)
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        var codigo = $"DEP{Guid.NewGuid():N}"[..16];
        var producto = new Producto { Nombre = nombre, DepartamentoId = await db.Departamentos.Select(d => d.Id).FirstAsync() };
        var variante = new Variante { ProductoId = producto.Id, UnidadBase = unidad };
        var pieza = new Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true };
        var caja12 = new Presentacion { VarianteId = variante.Id, Nombre = "Caja", Factor = Cantidad.Unidades(12) };
        db.AddRange(producto, variante, pieza, caja12, new CodigoBarras { Codigo = codigo, PresentacionId = pieza.Id },
            new Precio { VarianteId = variante.Id, PrecioCentavos = costoPieza * 2 });
        if (eleventa is not null)
            db.MapeosImportacion.Add(new MapeoImportacion { Origen = "eleventa", Entidad = nameof(Variante), IdOrigen = eleventa, IdNuevo = variante.Id });
        await db.SaveChangesAsync();
        if (existencia > 0)
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(sucursalId, variante, TipoMovimiento.InventarioInicial, Cantidad.Unidades(existencia),
                existencia * costoPieza, DateTime.UtcNow, "Inicial de prueba"));
        return (variante.Id, codigo);
    }

    private async Task<T> ConBaseAsync<T>(Func<PosDbContext, Task<T>> consulta)
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        return await consulta(alcance.ServiceProvider.GetRequiredService<PosDbContext>());
    }

    [Fact]
    public async Task Juntar_pasa_codigos_existencia_y_costo_y_deja_la_historia()
    {
        var (http, caja, cajero, encargado) = await PrepararAsync();
        var (repetido, codigoRepetido) = await ProductoAsync(caja.SucursalId, "Goma blanca", 5, 300);
        var (bueno, _) = await ProductoAsync(caja.SucursalId, "Goma Pelikan blanca", 3, 400);

        var porCajero = await http.PostAsJsonAsync("api/catalogo/depuracion/juntar", new SolicitudJuntar(cajero.UsuarioId, caja.SucursalId, repetido, bueno));
        (await http.PostAsJsonAsync("api/catalogo/depuracion/juntar", new SolicitudJuntar(encargado.UsuarioId, caja.SucursalId, repetido, bueno))).EnsureSuccessStatusCode();
        var escaneado = await Leer<ProductoVentaDto>(await http.GetAsync($"api/productos/codigo/{codigoRepetido}?sucursalId={caja.SucursalId}"));
        var decisiones = await http.GetFromJsonAsync<List<DecisionDepuracionDto>>("api/catalogo/depuracion");

        Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
        Assert.Equal(bueno, escaneado.VarianteId);
        var (origen, destino, existenciaOrigen, existenciaDestino, cajas) = await ConBaseAsync(async db => (
            await db.Variantes.AsNoTracking().SingleAsync(v => v.Id == repetido),
            await db.Variantes.AsNoTracking().Include(v => v.Presentaciones).ThenInclude(p => p.Codigos).SingleAsync(v => v.Id == bueno),
            await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == repetido),
            await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == bueno),
            await db.MovimientosInventario.AsNoTracking().CountAsync(m => m.VarianteId == repetido)));
        Assert.False(origen.Activa);
        Assert.Equal(bueno, origen.JuntadaEnId);
        Assert.Equal(Cantidad.Cero, existenciaOrigen.Cantidad);
        Assert.Equal(0, existenciaOrigen.CostoCentavos);
        Assert.Equal(Cantidad.Unidades(8), existenciaDestino.Cantidad);
        Assert.Equal(5 * 300 + 3 * 400, existenciaDestino.CostoCentavos);
        Assert.Equal(2, cajas);                                         // el inicial y el ajuste: la historia se queda
        Assert.Equal(2, destino.Presentaciones.Single(p => p.EsBase).Codigos.Count);
        Assert.Single(destino.Presentaciones, p => !p.EsBase);           // la caja de 12 ya existía: no se duplica
        Assert.Contains(decisiones!, d => d.Tipo == TipoDecisionDepuracion.Juntar && d.Descripcion == "«Goma blanca» se juntó con «Goma Pelikan blanca»");
    }

    [Fact]
    public async Task Cambiar_la_unidad_convierte_la_existencia_o_la_deja_para_contar()
    {
        var (http, caja, _, encargado) = await PrepararAsync();
        var (liston, _) = await ProductoAsync(caja.SucursalId, "Listón de raso rojo", 2, 5000);
        var (micas, _) = await ProductoAsync(caja.SucursalId, "Micas tamaño carta", 4, 1000);

        (await http.PostAsJsonAsync("api/catalogo/depuracion/unidad",
            new SolicitudCambiarUnidad(encargado.UsuarioId, caja.SucursalId, liston, UnidadBase.Metro, 50_000, 350))).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync("api/catalogo/depuracion/unidad",
            new SolicitudCambiarUnidad(encargado.UsuarioId, caja.SucursalId, micas, UnidadBase.Kilogramo, null, 2000))).EnsureSuccessStatusCode();
        var otraVez = await http.PostAsJsonAsync("api/catalogo/depuracion/unidad",
            new SolicitudCambiarUnidad(encargado.UsuarioId, caja.SucursalId, liston, UnidadBase.Metro, null, 350));

        var (variante, existencia, precios, sinConvertir) = await ConBaseAsync(async db => (
            await db.Variantes.AsNoTracking().Include(v => v.Presentaciones).SingleAsync(v => v.Id == liston),
            await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == liston),
            await db.Precios.AsNoTracking().Where(p => p.VarianteId == liston).ToListAsync(),
            await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == micas)));
        Assert.Equal(UnidadBase.Metro, variante.UnidadBase);
        Assert.Equal("Metro", variante.Presentaciones.Single(p => p.EsBase).Nombre);
        Assert.Equal(Cantidad.Unidades(600), variante.Presentaciones.Single(p => !p.EsBase).Factor);   // la caja de 12 = 600 m
        Assert.Equal(Cantidad.Unidades(100), existencia.Cantidad);
        Assert.Equal(2 * 5000, existencia.CostoCentavos);                                                  // el valor no cambia
        Assert.Equal(350, Assert.Single(precios).PrecioCentavos);
        Assert.Equal(Cantidad.Cero, sinConvertir.Cantidad);
        Assert.Equal(HttpStatusCode.BadRequest, otraVez.StatusCode);
    }

    [Fact]
    public async Task Dar_de_baja_deja_la_existencia_en_cero_y_lo_saca_de_la_venta()
    {
        var (http, caja, _, encargado) = await PrepararAsync();
        var (viejo, codigo) = await ProductoAsync(caja.SucursalId, "Disquete 3.5", 7, 500);

        (await http.PostAsJsonAsync("api/catalogo/depuracion/baja", new SolicitudBaja(encargado.UsuarioId, caja.SucursalId, viejo))).EnsureSuccessStatusCode();
        var escaneo = await http.GetAsync($"api/productos/codigo/{codigo}?sucursalId={caja.SucursalId}");

        var (producto, existencia) = await ConBaseAsync(async db => (
            await db.Productos.AsNoTracking().SingleAsync(p => p.Variantes.Any(v => v.Id == viejo)),
            await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == viejo)));
        Assert.False(producto.Activo);
        Assert.Equal(Cantidad.Cero, existencia.Cantidad);
        Assert.Equal(HttpStatusCode.NotFound, escaneo.StatusCode);
    }

    [Fact]
    public async Task Las_decisiones_se_repiten_en_otra_importacion_por_su_id_de_eleventa()
    {
        var (_, caja, _, _) = await PrepararAsync();
        var (a, _) = await ProductoAsync(caja.SucursalId, "Lápiz amarillo", 2, 200, eleventa: "90001");
        var (b, _) = await ProductoAsync(caja.SucursalId, "Lápiz Mirado amarillo", 1, 250, eleventa: "90002");
        var (c, _) = await ProductoAsync(caja.SucursalId, "Pluma fuente vieja", 1, 900, eleventa: "90003");
        // Así llegan de la otra base: con los IDs de eleventa, no con los de aquí.
        var decisiones = new List<DecisionDepuracion>
        {
            new() { Tipo = TipoDecisionDepuracion.Juntar, OrigenEleventa = "90001", DestinoEleventa = "90002", Descripcion = "lápices", FechaUtc = DateTime.UtcNow.AddMinutes(-2) },
            new() { Tipo = TipoDecisionDepuracion.DarDeBaja, OrigenEleventa = "90003", Descripcion = "pluma", FechaUtc = DateTime.UtcNow.AddMinutes(-1) },
            new() { Tipo = TipoDecisionDepuracion.DarDeBaja, OrigenEleventa = "99999", Descripcion = "no existe", FechaUtc = DateTime.UtcNow },
        };

        var (aplicadas, omitidas) = await ConBaseAsync(db => new RegistroDepuracion(db).RepetirAsync(decisiones));
        var (otraVez, _) = await ConBaseAsync(db => new RegistroDepuracion(db).RepetirAsync(decisiones));

        Assert.Equal(2, aplicadas);
        Assert.Equal("no existe: no viene en esta importación.", Assert.Single(omitidas));
        Assert.Equal(0, otraVez);
        var (juntada, baja, existencia) = await ConBaseAsync(async db => (
            await db.Variantes.AsNoTracking().SingleAsync(v => v.Id == a),
            await db.Variantes.AsNoTracking().SingleAsync(v => v.Id == c),
            await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == b)));
        Assert.Equal(b, juntada.JuntadaEnId);
        Assert.False(baja.Activa);
        Assert.Equal(Cantidad.Unidades(3), existencia.Cantidad);
    }
}
