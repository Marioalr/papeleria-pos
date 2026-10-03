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
