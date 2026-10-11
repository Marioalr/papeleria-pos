using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Pruebas.Datos;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>La caja 2 del piloto se pone al día con eleventa cada pocos minutos: lo de allá manda (docs/plan.md, paso 1).</summary>
public sealed class SincronizacionEleventaTests : IAsyncDisposable
{
    private BaseEnMemoria? _base;

    public async ValueTask DisposeAsync()
    {
        if (_base is not null)
            await _base.DisposeAsync();
    }

    private static FilaProducto Producto(int id, string codigo, string descripcion, double precio = 15, double costo = 10, double mayoreo = 0,
        int? departamento = 2, bool eliminado = false, char tipo = 'U') =>
        new(id, codigo, descripcion, tipo, 1, costo, precio, mayoreo, departamento, eliminado);

    private static DatosEleventa Datos(IEnumerable<FilaProducto> productos, IEnumerable<FilaExistencia>? existencias = null,
        IEnumerable<FilaPromocion>? promociones = null, IEnumerable<FilaDepartamento>? departamentos = null) => new()
    {
        Departamentos = departamentos?.ToList() ?? [new(2, "Papelería", true), new(5, "Escolar", true)],
        Productos = productos.ToList(),
        Existencias = existencias?.ToList() ?? [],
        Promociones = promociones?.ToList() ?? [],
    };

    /// <summary>La caja 2 como queda con importar-eleventa: el punto de partida de cada lectura.</summary>
    private async Task<BaseEnMemoria> ImportadaAsync(DatosEleventa inicial)
    {
        _base = await BaseEnMemoria.CrearAsync();
        await using var db = _base.Contexto();
        await GuardadoLote.GuardarAsync(ImportadorEleventa.Importar(inicial), db);
        return _base;
    }

    private async Task<ResultadoSincronizacion> SincronizarAsync(DatosEleventa e)
    {
        await using var db = _base!.Contexto();
        return await SincronizacionEleventa.AplicarAsync(db, e, _base.Reloj.GetUtcNow().UtcDateTime);
    }

    private static async Task<Variante> VarianteAsync(PosDbContext db, string codigo) =>
        await db.Variantes.Include(v => v.Producto).SingleAsync(v => v.Id == db.CodigosBarras.Where(c => c.Codigo == codigo).Select(c => c.Presentacion!.VarianteId).Single());

    private static async Task<long> VersionAsync(PosDbContext db) =>
        await db.Secuencias.Where(s => s.Nombre == Secuencia.Catalogo).Select(s => s.Valor).SingleOrDefaultAsync();

    private static Task<List<Precio>> EscalasAsync(PosDbContext db, Guid variante) =>
        db.Precios.Where(p => p.VarianteId == variante && p.PresentacionId == null && p.SucursalId == null).OrderBy(p => p.CantidadMinima).ToListAsync();

    [Fact]
    public async Task Con_lo_mismo_que_se_importo_no_cambia_nada()
    {
        var datos = Datos([Producto(1, "7501035102446", "Cinta Janel"), Producto(2, "LAP", "Lápiz del 2", precio: 6)],
            existencias: [new(1, 10), new(2, 3)], promociones: [new("LAP", 12, 5)]);
        await ImportadaAsync(datos);
        long version;
        await using (var db = _base!.Contexto())
            version = await VersionAsync(db);

        var primera = await SincronizarAsync(datos);
        var segunda = await SincronizarAsync(datos);

        Assert.False(primera.HuboCambios, primera.ToString());
        Assert.False(segunda.HuboCambios, segunda.ToString());
        Assert.Empty(primera.Avisos);
        await using var lectura = _base.Contexto();
        Assert.Equal(version, await VersionAsync(lectura));
        Assert.DoesNotContain(await lectura.MovimientosInventario.ToListAsync(), m => m.DocumentoTipo == SincronizacionEleventa.Documento);
    }

    [Fact]
    public async Task Un_producto_nuevo_en_eleventa_llega_con_su_codigo_precio_costo_existencia_y_mayoreo_por_revisar()
    {
        await ImportadaAsync(Datos([Producto(1, "A1", "Goma")]));

        var r = await SincronizarAsync(Datos([Producto(1, "A1", "Goma"), Producto(7, "7509876543210", "Folder carta", precio: 4.5, costo: 2.4, mayoreo: 3.8, departamento: 5)],
            existencias: [new(7, 20)]));

        Assert.Equal((1, 1), (r.Nuevos, r.Existencias));
        Assert.Contains(SincronizacionEleventa.AvisoMayoreoNuevo, r.Avisos.Keys);
        await using var db = _base!.Contexto();
        var folder = await VarianteAsync(db, "7509876543210");
        Assert.Equal(("Folder carta", "Escolar"), (folder.Producto!.Nombre, (await db.Departamentos.SingleAsync(d => d.Id == folder.Producto.DepartamentoId)).Nombre));
        Assert.Equal([450L], (await EscalasAsync(db, folder.Id)).Select(p => p.PrecioCentavos));
        Assert.Equal(240, (await db.ProveedoresVariantes.SingleAsync(pv => pv.VarianteId == folder.Id)).UltimoCostoCentavos);
        var existencia = await db.Existencias.SingleAsync(x => x.VarianteId == folder.Id);
        Assert.Equal((Cantidad.Piezas(20), 4800L), (existencia.Cantidad, existencia.CostoCentavos));
        Assert.Equal(TipoMovimiento.InventarioInicial, (await db.MovimientosInventario.SingleAsync(m => m.VarianteId == folder.Id)).Tipo);
        Assert.Equal(380, (await db.MayoreoPorRevisar.SingleAsync(m => m.VarianteId == folder.Id)).PrecioMayoreoCentavos);
        // La siguiente lectura ya lo conoce.
        Assert.False((await SincronizarAsync(Datos([Producto(1, "A1", "Goma"), Producto(7, "7509876543210", "Folder carta", precio: 4.5, costo: 2.4,
            mayoreo: 3.8, departamento: 5)], existencias: [new(7, 20)]))).HuboCambios);
    }

    [Fact]
    public async Task Llegan_los_cambios_de_precio_nombre_codigo_departamento_y_costo()
    {
        await ImportadaAsync(Datos([Producto(1, "A1", "Goma blanca", precio: 8, costo: 4)]));
        long version;
        await using (var db = _base!.Contexto())
            version = await VersionAsync(db);

        var r = await SincronizarAsync(Datos([Producto(1, "A1-N", "Goma blanca Pelikan", precio: 9.5, costo: 4.4, departamento: 5)]));

        Assert.Equal((1, 1, 1), (r.Cambiados, r.Precios, r.Costos));
        await using var lectura = _base.Contexto();
        var goma = await VarianteAsync(lectura, "A1-N");
        Assert.False(await lectura.CodigosBarras.AnyAsync(c => c.Codigo == "A1"));
        Assert.Equal("Goma blanca Pelikan", goma.Producto!.Nombre);
        Assert.Equal("Escolar", (await lectura.Departamentos.SingleAsync(d => d.Id == goma.Producto.DepartamentoId)).Nombre);
        Assert.Equal([950L], (await EscalasAsync(lectura, goma.Id)).Select(p => p.PrecioCentavos));
        Assert.Contains(await lectura.PreciosHistorial.ToListAsync(), h => h.VarianteId == goma.Id && h.PrecioAnteriorCentavos == 800 && h.PrecioNuevoCentavos == 950);
        Assert.True(await VersionAsync(lectura) > version);
    }

    [Fact]
    public async Task Lo_borrado_en_eleventa_queda_inactivo_y_lo_dado_de_alta_aqui_no_se_toca()
    {
        await ImportadaAsync(Datos([Producto(1, "A1", "Goma"), Producto(2, "B2", "Sacapuntas"), Producto(3, "C3", "Regla")]));
        Guid propia;
        await using (var db = _base!.Contexto())
        {
            var departamento = await db.Departamentos.FirstAsync();
            var producto = new Producto { Nombre = "Engargolado", DepartamentoId = departamento.Id };
            var variante = new Variante { ProductoId = producto.Id };
            db.AddRange(producto, variante, new Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true });
            await db.SaveChangesAsync();
            propia = variante.Id;
        }

        // La 2 se marcó como borrada; la 3 ya ni viene.
        var r = await SincronizarAsync(Datos([Producto(1, "A1", "Goma"), Producto(2, "B2", "Sacapuntas", eliminado: true)]));

        Assert.Equal(2, r.Desactivados);
        Assert.Contains(SincronizacionEleventa.AvisoDesaparecido, r.Avisos.Keys);
        await using var lectura = _base.Contexto();
        Assert.True((await VarianteAsync(lectura, "A1")).Activa);
        Assert.False((await VarianteAsync(lectura, "B2")).Activa);
        Assert.False((await VarianteAsync(lectura, "C3")).Producto!.Activo);
        Assert.True((await lectura.Variantes.SingleAsync(v => v.Id == propia)).Activa);
        // Si en eleventa lo vuelven a activar, regresa.
        Assert.Equal(1, (await SincronizarAsync(Datos([Producto(1, "A1", "Goma"), Producto(2, "B2", "Sacapuntas")]))).Cambiados);
    }

    [Fact]
    public async Task Las_promociones_se_agregan_cambian_y_se_quitan_sin_tocar_las_escalas_capturadas_aqui()
    {
        await ImportadaAsync(Datos([Producto(1, "LAP", "Lápiz del 2", precio: 6)], promociones: [new("LAP", 12, 5)]));
        Guid lapiz;
        await using (var db = _base!.Contexto())
        {
            lapiz = (await VarianteAsync(db, "LAP")).Id;
            // Una escala que puso el encargado en F3.
            db.Precios.Add(new Precio { VarianteId = lapiz, CantidadMinima = Cantidad.Piezas(100), PrecioCentavos = 400 });
            await db.SaveChangesAsync();
        }

        var cambio = await SincronizarAsync(Datos([Producto(1, "LAP", "Lápiz del 2", precio: 6)], promociones: [new("LAP", 12, 4.8), new("LAP", 24, 4.5)]));
        long version;
        await using (var db = _base.Contexto())
        {
            Assert.Equal([(1L, 600L), (12L, 480L), (24L, 450L), (100L, 400L)],
                (await EscalasAsync(db, lapiz)).Select(p => ((long)p.CantidadMinima.EnUnidades, p.PrecioCentavos)));
            version = await VersionAsync(db);
        }
        var quitadas = await SincronizarAsync(Datos([Producto(1, "LAP", "Lápiz del 2", precio: 6)]));

        Assert.Equal((2, 2), (cambio.Precios, quitadas.Precios));
        await using var lectura = _base.Contexto();
        Assert.Equal([(1L, 600L), (100L, 400L)], (await EscalasAsync(lectura, lapiz)).Select(p => ((long)p.CantidadMinima.EnUnidades, p.PrecioCentavos)));
        // Quitar también avisa a las cajas.
        Assert.True(await VersionAsync(lectura) > version);
    }

    [Fact]
    public async Task Las_existencias_quedan_como_en_eleventa_con_un_ajuste_en_el_kardex()
    {
        await ImportadaAsync(Datos([Producto(1, "A1", "Goma", costo: 2), Producto(2, "B2", "Sacapuntas")], existencias: [new(1, 10), new(2, 4)]));

        // En la caja 1 se vendieron 3 gomas; los sacapuntas tienen una fracción (está mal en eleventa).
        var r = await SincronizarAsync(Datos([Producto(1, "A1", "Goma", costo: 2), Producto(2, "B2", "Sacapuntas")], existencias: [new(1, 7), new(2, 4.5)]));

        Assert.Equal(1, r.Existencias);
        Assert.Contains(SincronizacionEleventa.AvisoFraccionEnPieza, r.Avisos.Keys);
        await using var db = _base!.Contexto();
        var goma = await VarianteAsync(db, "A1");
        Assert.Equal((Cantidad.Piezas(7), 1400L), await db.Existencias.Where(x => x.VarianteId == goma.Id).Select(x => ValueTuple.Create(x.Cantidad, x.CostoCentavos)).SingleAsync());
        var ajuste = await db.MovimientosInventario.SingleAsync(m => m.VarianteId == goma.Id && m.DocumentoTipo == SincronizacionEleventa.Documento);
        Assert.Equal((TipoMovimiento.Conteo, -Cantidad.Piezas(3), -600L), (ajuste.Tipo, ajuste.Cantidad, ajuste.CostoCentavos));
        Assert.Empty(await new RegistroInventario(db).VerificarExistenciasAsync());
    }

    [Fact]
    public async Task Un_codigo_que_aqui_ya_tiene_otro_producto_no_se_mueve()
    {
        await ImportadaAsync(Datos([Producto(1, "A1", "Goma"), Producto(2, "B2", "Sacapuntas")]));

        var r = await SincronizarAsync(Datos([Producto(1, "A1", "Goma"), Producto(2, "A1", "Sacapuntas")]));

        Assert.False(r.HuboCambios);
        await using var db = _base!.Contexto();
        Assert.Equal("Goma", (await VarianteAsync(db, "A1")).Producto!.Nombre);
        Assert.Equal("Sacapuntas", (await VarianteAsync(db, "B2")).Producto!.Nombre);
    }
}
