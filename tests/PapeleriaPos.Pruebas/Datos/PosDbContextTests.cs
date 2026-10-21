using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Datos;

public class PosDbContextTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    [Fact]
    public async Task El_catalogo_se_guarda_y_se_lee_completo()
    {
        await using var db = _base.Contexto();

        var producto = await db.Productos
            .Include(p => p.Marca)
            .Include(p => p.Variantes).ThenInclude(v => v.Presentaciones).ThenInclude(p => p.Codigos)
            .SingleAsync();

        Assert.Equal("Bic", producto.Marca!.Nombre);
        var variante = Assert.Single(producto.Variantes);
        Assert.Equal(2, variante.Presentaciones.Count);
        Assert.Equal(Cantidad.Piezas(12), variante.Presentaciones.Single(p => p.Nombre == "Docena").Factor);
        Assert.Equal("7501234567890", variante.Presentaciones.Single(p => p.EsBase).Codigos.Single().Codigo);
    }

    [Fact]
    public async Task Enumeraciones_y_cantidades_se_guardan_legibles()
    {
        await using var db = _base.Contexto();

        var tipo = await db.Database.SqlQueryRaw<string>("SELECT Tipo AS Value FROM Productos").SingleAsync();
        var factor = await db.Database.SqlQueryRaw<long>("SELECT Factor AS Value FROM Presentaciones WHERE Nombre = 'Docena'").SingleAsync();

        Assert.Equal("Mercancia", tipo);
        Assert.Equal(12_000, factor);
    }

    [Fact]
    public async Task Cada_guardado_del_catalogo_lleva_una_version_nueva()
    {
        long versionInicial;
        await using (var db = _base.Contexto())
            versionInicial = (await db.Productos.SingleAsync()).Version;

        _base.Reloj.Ahora = _base.Reloj.Ahora.AddHours(1);
        await using (var db = _base.Contexto())
        {
            var producto = await db.Productos.SingleAsync();
            producto.Nombre = "Pluma punto mediano";
            await db.SaveChangesAsync();
        }

        await using (var db = _base.Contexto())
        {
            var producto = await db.Productos.SingleAsync();
            var variante = await db.Variantes.SingleAsync();
            var departamento = await db.Departamentos.SingleAsync();
            Assert.True(versionInicial > 0);
            Assert.Equal(versionInicial + 1, producto.Version);
            Assert.Equal(versionInicial, variante.Version);
            // El departamento no va en la copia de las cajas (entrega 5, parte 2): no lleva versión.
            Assert.Equal(0, departamento.Version);
            Assert.Equal(_base.Reloj.Ahora.UtcDateTime, producto.ModificadoUtc);
            Assert.Equal(DateTimeKind.Utc, producto.ModificadoUtc.Kind);
        }
    }

    [Fact]
    public async Task No_se_repiten_marcas_aunque_cambien_mayusculas_o_acentos()
    {
        await using var db = _base.Contexto();
        db.Marcas.Add(new Marca { Nombre = " BIC " });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task No_se_repiten_codigos_de_barras()
    {
        await using var db = _base.Contexto();
        db.CodigosBarras.Add(new CodigoBarras { Codigo = "7501234567890", PresentacionId = _catalogo.Docena.Id });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task No_se_repite_una_escala_de_precio_aunque_tenga_campos_vacios()
    {
        await using var db = _base.Contexto();
        db.Precios.Add(new Precio { VarianteId = _catalogo.Azul.Id, PrecioCentavos = 800 });
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio { VarianteId = _catalogo.Azul.Id, PrecioCentavos = 900 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Cada_cambio_de_precio_queda_en_el_historial()
    {
        var precio = new Precio { VarianteId = _catalogo.Azul.Id, PrecioCentavos = 800 };
        await using (var db = _base.Contexto())
        {
            db.Precios.Add(precio);
            await db.SaveChangesAsync();
        }
        _base.Reloj.Ahora = _base.Reloj.Ahora.AddMinutes(1);
        await using (var db = _base.Contexto())
        {
            (await db.Precios.SingleAsync()).PrecioCentavos = 850;
            await db.SaveChangesAsync();
        }
        _base.Reloj.Ahora = _base.Reloj.Ahora.AddMinutes(1);
        await using (var db = _base.Contexto())
        {
            db.Precios.Remove(await db.Precios.SingleAsync());
            await db.SaveChangesAsync();
        }

        await using var lectura = _base.Contexto();
        var historial = await lectura.PreciosHistorial.OrderBy(h => h.FechaUtc).ToListAsync();
        Assert.Collection(historial,
            h => Assert.Equal((null, 800L), (h.PrecioAnteriorCentavos, h.PrecioNuevoCentavos)),
            h => Assert.Equal((800L, 850L), (h.PrecioAnteriorCentavos, h.PrecioNuevoCentavos)),
            h => Assert.Equal((850L, (long?)null), (h.PrecioAnteriorCentavos, h.PrecioNuevoCentavos)));
        Assert.All(historial, h => Assert.Equal(precio.Id, h.PrecioId));
    }

    [Fact]
    public async Task Los_movimientos_del_kardex_no_se_editan_ni_se_borran()
    {
        await using var db = _base.Contexto();
        var compra = Kardex.Entrada(_catalogo.Matriz.Id, _catalogo.Azul, TipoMovimiento.Compra, Cantidad.Piezas(10), 1000,
            _base.Reloj.Ahora.UtcDateTime);
        db.MovimientosInventario.Add(compra);
        await db.SaveChangesAsync();

        db.Entry(compra).Property(m => m.CostoCentavos).CurrentValue = 1;
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());

        db.Entry(compra).State = EntityState.Deleted;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
