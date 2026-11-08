using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Datos;

public class RegistroInventarioTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Variante _liston = new() { Descripcion = "Rojo", UnidadBase = UnidadBase.Metro };

    private Guid Matriz => _catalogo.Matriz.Id;
    private DateTime Ahora => _base.Reloj.Ahora.UtcDateTime;

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        _liston.ProductoId = _catalogo.Pluma.Id;
        db.Variantes.Add(_liston);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    [Fact]
    public async Task Registrar_actualiza_la_existencia_en_la_misma_operacion()
    {
        await using (var db = _base.Contexto())
        {
            var registro = new RegistroInventario(db);
            await registro.RegistrarAsync(Kardex.Entrada(Matriz, _catalogo.Azul, TipoMovimiento.Compra, Cantidad.Piezas(144), 10000, Ahora));

            var existencia = await registro.ExistenciaAsync(Matriz, _catalogo.Azul.Id);
            await registro.RegistrarAsync(Kardex.Salida(Matriz, _catalogo.Azul, TipoMovimiento.Venta, Cantidad.Piezas(10), existencia, 0, Ahora));
        }

        await using var lectura = _base.Contexto();
        var guardada = await new RegistroInventario(lectura).ExistenciaAsync(Matriz, _catalogo.Azul.Id);
        Assert.Equal(Cantidad.Piezas(134), guardada.Cantidad);
        Assert.Equal(10000 - 694, guardada.CostoCentavos); // 10 × 69.44 = 694.4 → 694
        Assert.Equal(2, await lectura.MovimientosInventario.CountAsync());
    }

    /// <summary>
    /// Se vendieron 10 sin existencia a $10 y lo que llega cruza el cero a otro costo: lo que queda vale lo que costó lo que llegó.
    /// Antes se sumaba al valor negativo: 2 piezas a $70 cada una si llegaba más caro, o a −$8 si llegaba más barato. La corrección
    /// es otro renglón del kardex, de cantidad cero, para que el valor siga siendo la suma de los movimientos.
    /// </summary>
    [Theory]
    [InlineData(2000, 4000, -10000)]
    [InlineData(700, 1400, 3000)]
    public async Task Lo_que_llega_con_la_existencia_en_negativo_deja_el_valor_a_su_costo(long costoPieza, long valor, long correccion)
    {
        await using (var db = _base.Contexto())
        {
            var registro = new RegistroInventario(db);
            await registro.AgregarAsync(Kardex.Salida(Matriz, _catalogo.Azul, TipoMovimiento.Venta, Cantidad.Piezas(10),
                await registro.ExistenciaAsync(Matriz, _catalogo.Azul.Id), 1000, Ahora));
            await db.SaveChangesAsync();
            await registro.RegistrarAsync(Kardex.Entrada(Matriz, _catalogo.Azul, TipoMovimiento.Compra, Cantidad.Piezas(12), 12 * costoPieza, Ahora,
                documentoTipo: "Compra", documentoId: Matriz));
        }

        await using var lectura = _base.Contexto();
        var guardada = await new RegistroInventario(lectura).ExistenciaAsync(Matriz, _catalogo.Azul.Id);
        Assert.Equal((Cantidad.Piezas(2), valor), (guardada.Cantidad, guardada.CostoCentavos));
        var corregido = await lectura.MovimientosInventario.SingleAsync(m => m.Cantidad == Cantidad.Cero);
        Assert.Equal((TipoMovimiento.Ajuste, correccion, "Compra", (Guid?)Matriz),
            (corregido.Tipo, corregido.CostoCentavos, corregido.DocumentoTipo, corregido.DocumentoId));
        Assert.Empty(await new RegistroInventario(lectura).VerificarExistenciasAsync());
    }

    [Fact]
    public async Task Lo_que_llega_al_mismo_costo_o_sobre_existencia_no_lleva_correccion()
    {
        await using (var db = _base.Contexto())
        {
            var registro = new RegistroInventario(db);
            await registro.RegistrarAsync(Kardex.Salida(Matriz, _catalogo.Azul, TipoMovimiento.Venta, Cantidad.Piezas(10),
                await registro.ExistenciaAsync(Matriz, _catalogo.Azul.Id), 1000, Ahora));
            await registro.RegistrarAsync(Kardex.Entrada(Matriz, _catalogo.Azul, TipoMovimiento.Compra, Cantidad.Piezas(12), 12000, Ahora));
            await registro.RegistrarAsync(Kardex.Entrada(Matriz, _catalogo.Azul, TipoMovimiento.Compra, Cantidad.Piezas(1), 5000, Ahora));
        }

        await using var lectura = _base.Contexto();
        var guardada = await new RegistroInventario(lectura).ExistenciaAsync(Matriz, _catalogo.Azul.Id);
        Assert.Equal((Cantidad.Piezas(3), 7000L), (guardada.Cantidad, guardada.CostoCentavos));
        Assert.Equal(3, await lectura.MovimientosInventario.CountAsync());
    }

    private MovimientoInventario Venta(Existencia existencia, decimal piezas, long ultimoCosto) =>
        Kardex.Salida(Matriz, _catalogo.Azul, TipoMovimiento.Venta, Cantidad.Unidades(piezas), existencia, ultimoCosto, Ahora, documentoTipo: "Venta", documentoId: Matriz);

    /// <summary>
    /// Vino de eleventa a costo $0 y después le capturaron el costo: el ticket lleva el último costo, pero el valor no baja de cero
    /// y, vendido todo, queda en cero exacto. Antes quedaba en −$50 y con un costo promedio negativo.
    /// </summary>
    [Fact]
    public async Task Lo_que_sale_de_una_existencia_sin_valor_no_la_deja_en_negativo()
    {
        await using (var db = _base.Contexto())
        {
            var registro = new RegistroInventario(db);
            await registro.RegistrarAsync(Kardex.Entrada(Matriz, _catalogo.Azul, TipoMovimiento.InventarioInicial, Cantidad.Piezas(10), 0, Ahora));
            var venta = Venta(await registro.ExistenciaAsync(Matriz, _catalogo.Azul.Id), 1, 500);
            Assert.Equal(-500, venta.CostoCentavos);
            await registro.RegistrarAsync(venta);
            var existencia = await registro.ExistenciaAsync(Matriz, _catalogo.Azul.Id);
            Assert.Equal((Cantidad.Piezas(9), 0L), (existencia.Cantidad, existencia.CostoCentavos));
            await registro.RegistrarAsync(Venta(existencia, 9, 500));
        }

        await using var lectura = _base.Contexto();
        var guardada = await new RegistroInventario(lectura).ExistenciaAsync(Matriz, _catalogo.Azul.Id);
        Assert.Equal((Cantidad.Cero, 0L), (guardada.Cantidad, guardada.CostoCentavos));
        Assert.Equal(new long[] { 500, 4500 }, await lectura.MovimientosInventario.Where(m => m.Cantidad == Cantidad.Cero).OrderBy(m => m.CostoCentavos).Select(m => m.CostoCentavos).ToArrayAsync());
        Assert.All(await lectura.MovimientosInventario.Where(m => m.Cantidad == Cantidad.Cero).ToListAsync(), m => Assert.Equal((TipoMovimiento.Ajuste, "Venta"), (m.Tipo, m.DocumentoTipo)));
        Assert.Empty(await new RegistroInventario(lectura).VerificarExistenciasAsync());
    }

    /// <summary>Una existencia que ya estaba dañada se compone con la siguiente salida. Antes, 5 con −$70 quedaban en 0 con −$120.</summary>
    [Theory]
    [InlineData(1, 4)]
    [InlineData(5, 0)]
    public async Task Lo_que_sale_de_una_existencia_con_su_valor_en_negativo_la_deja_en_cero(int vendidas, int quedan)
    {
        await using var db = _base.Contexto();
        var registro = new RegistroInventario(db);
        await registro.RegistrarAsync(Kardex.Entrada(Matriz, _catalogo.Azul, TipoMovimiento.InventarioInicial, Cantidad.Piezas(5), 0, Ahora));
        await registro.RegistrarAsync(new MovimientoInventario { SucursalId = Matriz, VarianteId = _catalogo.Azul.Id, FechaUtc = Ahora, Tipo = TipoMovimiento.Ajuste, CostoCentavos = -7000, Motivo = "Dañada" });

        await registro.RegistrarAsync(Venta(await registro.ExistenciaAsync(Matriz, _catalogo.Azul.Id), vendidas, 1000));

        var existencia = await registro.ExistenciaAsync(Matriz, _catalogo.Azul.Id);
        Assert.Equal((Cantidad.Piezas(quedan), 0L), (existencia.Cantidad, existencia.CostoCentavos));
        Assert.Empty(await registro.VerificarExistenciasAsync());
    }

    /// <summary>Si la salida cruza el cero, lo que falta queda al último costo, igual que al vender sin existencia.</summary>
    [Fact]
    public async Task Lo_que_sale_de_mas_de_una_existencia_sin_valor_deja_lo_que_falta_a_su_costo()
    {
        await using var db = _base.Contexto();
        var registro = new RegistroInventario(db);
        await registro.RegistrarAsync(Kardex.Entrada(Matriz, _catalogo.Azul, TipoMovimiento.InventarioInicial, Cantidad.Piezas(5), 0, Ahora));

        await registro.RegistrarAsync(Venta(await registro.ExistenciaAsync(Matriz, _catalogo.Azul.Id), 7, 500));

        var existencia = await registro.ExistenciaAsync(Matriz, _catalogo.Azul.Id);
        Assert.Equal((Cantidad.Piezas(-2), -1000L), (existencia.Cantidad, existencia.CostoCentavos));
    }

    /// <summary>Si lo que llega no alcanza, lo que sigue faltando también queda a su costo: lo siguiente que llegue ya cuadra.</summary>
    [Fact]
    public async Task Lo_que_llega_y_no_alcanza_deja_lo_que_falta_a_su_costo()
    {
        await using (var db = _base.Contexto())
        {
            var registro = new RegistroInventario(db);
            await registro.RegistrarAsync(Kardex.Salida(Matriz, _catalogo.Azul, TipoMovimiento.Venta, Cantidad.Piezas(10),
                await registro.ExistenciaAsync(Matriz, _catalogo.Azul.Id), 1000, Ahora));
            await registro.RegistrarAsync(Kardex.Entrada(Matriz, _catalogo.Azul, TipoMovimiento.Compra, Cantidad.Piezas(5), 10000, Ahora));
            var falta = await registro.ExistenciaAsync(Matriz, _catalogo.Azul.Id);
            Assert.Equal((Cantidad.Piezas(-5), -10000L), (falta.Cantidad, falta.CostoCentavos));
            await registro.RegistrarAsync(Kardex.Entrada(Matriz, _catalogo.Azul, TipoMovimiento.Compra, Cantidad.Piezas(7), 14000, Ahora));
        }

        await using var lectura = _base.Contexto();
        var guardada = await new RegistroInventario(lectura).ExistenciaAsync(Matriz, _catalogo.Azul.Id);
        Assert.Equal((Cantidad.Piezas(2), 4000L), (guardada.Cantidad, guardada.CostoCentavos));
        Assert.Empty(await new RegistroInventario(lectura).VerificarExistenciasAsync());
    }

    /// <summary>Lo que ya quedó mal guardado (2 piezas con valor negativo) también se arregla con lo siguiente que llega.</summary>
    [Fact]
    public async Task Una_existencia_con_el_valor_en_negativo_se_arregla_con_lo_que_llega()
    {
        await using (var db = _base.Contexto())
        {
            db.Existencias.Add(new Existencia { SucursalId = Matriz, VarianteId = _catalogo.Azul.Id, Cantidad = Cantidad.Piezas(2), CostoCentavos = -7420 });
            await db.SaveChangesAsync();
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(Matriz, _catalogo.Azul, TipoMovimiento.Compra, Cantidad.Piezas(1), 500, Ahora));
        }

        await using var lectura = _base.Contexto();
        var guardada = await new RegistroInventario(lectura).ExistenciaAsync(Matriz, _catalogo.Azul.Id);
        Assert.Equal((Cantidad.Piezas(3), 1500L), (guardada.Cantidad, guardada.CostoCentavos));
    }

    [Fact]
    public async Task Sin_movimientos_la_existencia_es_cero()
    {
        await using var db = _base.Contexto();

        var existencia = await new RegistroInventario(db).ExistenciaAsync(Matriz, _catalogo.Azul.Id);

        Assert.Equal(Cantidad.Cero, existencia.Cantidad);
    }

    [Fact]
    public async Task Muchos_movimientos_de_una_vez_y_la_verificacion_sale_limpia()
    {
        await using (var db = _base.Contexto())
        {
            var registro = new RegistroInventario(db);
            await registro.AgregarAsync(Kardex.Entrada(Matriz, _catalogo.Azul, TipoMovimiento.InventarioInicial, Cantidad.Piezas(50), 4000, Ahora));
            await registro.AgregarAsync(Kardex.Entrada(Matriz, _liston, TipoMovimiento.Compra, Cantidad.Unidades(100), 30000, Ahora));
            for (var i = 0; i < 20; i++)
            {
                var existencia = await registro.ExistenciaAsync(Matriz, _liston.Id);
                await registro.AgregarAsync(Kardex.Salida(Matriz, _liston, TipoMovimiento.Venta, Cantidad.Unidades(1.5m), existencia, 0, Ahora));
            }
            await db.SaveChangesAsync();
        }

        await using var lectura = _base.Contexto();
        var registroLectura = new RegistroInventario(lectura);
        Assert.Empty(await registroLectura.VerificarExistenciasAsync());
        var liston = await registroLectura.ExistenciaAsync(Matriz, _liston.Id);
        Assert.Equal(Cantidad.Unidades(70), liston.Cantidad);
    }

    [Fact]
    public async Task Una_existencia_alterada_se_detecta_y_se_reconstruye_desde_el_kardex()
    {
        await using (var db = _base.Contexto())
        {
            var registro = new RegistroInventario(db);
            await registro.RegistrarAsync(Kardex.Entrada(Matriz, _catalogo.Azul, TipoMovimiento.Compra, Cantidad.Piezas(10), 1000, Ahora));
            await db.Database.ExecuteSqlRawAsync("UPDATE Existencias SET Cantidad = 99000");
        }

        await using (var db = _base.Contexto())
        {
            var registro = new RegistroInventario(db);
            var diferencia = Assert.Single(await registro.VerificarExistenciasAsync());
            Assert.Equal(Cantidad.Piezas(10), diferencia.CantidadKardex);
            Assert.Equal(Cantidad.Piezas(99), diferencia.CantidadGuardada);

            await registro.ReconstruirExistenciasAsync();

            Assert.Empty(await registro.VerificarExistenciasAsync());
            Assert.Equal(Cantidad.Piezas(10), (await registro.ExistenciaAsync(Matriz, _catalogo.Azul.Id)).Cantidad);
        }
    }
}
