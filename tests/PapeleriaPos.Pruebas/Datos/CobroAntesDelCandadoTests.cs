using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// Lo que el cobro solo lee va antes de tomar el candado de escritura (entrega 5, parte 2): los productos y su último costo se
/// leen sin la transacción abierta, y dos cobros a la vez no se esperan mientras tanto. Dentro quedan el folio y la existencia,
/// que se leen para escribirlos. Se revisa qué manda a la base y si ya tenía la transacción: no depende de lo que tarde.
/// </summary>
public class CobroAntesDelCandadoTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _cajera = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private Caja _caja = null!;
    private Precio _precio = null!;

    /// <summary>Cada lectura y escritura que manda el contexto, y si iba dentro de una transacción.</summary>
    private sealed class Espia : DbCommandInterceptor
    {
        public ConcurrentQueue<(string Sql, bool EnTransaccion)> Comandos { get; } = new();

        private DbCommand Anotar(DbCommand comando)
        {
            Comandos.Enqueue((comando.CommandText, comando.Transaction is not null));
            return comando;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Anotar(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Anotar(command);
            return ValueTask.FromResult(result);
        }
    }

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _caja = new Caja { SucursalId = _catalogo.Matriz.Id, Nombre = "Caja 1", Serie = "A", EsPrincipal = true };
        _precio = new Precio { VarianteId = _catalogo.Azul.Id, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 1000 };
        _cajera.EstablecerPin("1111");
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        db.AddRange(_caja, _cajera, _precio);
        await db.SaveChangesAsync();
        await MigracionPermisos.AplicarAsync(db);
        await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(_catalogo.Matriz.Id, _catalogo.Azul, TipoMovimiento.Compra,
            Cantidad.Piezas(10), 4000, _base.Reloj.Ahora.UtcDateTime));
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    [Fact]
    public async Task Los_productos_y_su_costo_se_leen_antes_de_abrir_la_transaccion()
    {
        Turno turno;
        await using (var db = _base.Contexto())
            turno = await new RegistroCaja(db).AbrirTurnoAsync(_caja.Id, _cajera.Id, 0);
        var venta = new Venta { SucursalId = turno.SucursalId, CajaId = turno.CajaId, TurnoId = turno.Id, CajeroId = turno.CajeroId, Serie = _caja.Serie };
        venta.AgregarProducto(new ProductoEnVenta(_catalogo.Azul.Id, "Pluma punto fino Azul", Impuesto.Iva16, UnidadBase.Pieza, true), _catalogo.Pieza,
            Cantidad.Piezas(2), [_precio]);
        var espia = new Espia();

        await using (var db = _base.Contexto(espia))
            await new RegistroCaja(db).CobrarAsync(venta, [new CobroSolicitado(FormaPago.Efectivo, 2000)]);

        var comandos = espia.Comandos.ToList();
        bool Lee(string sql, string tabla) => sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && sql.Contains($"FROM \"{tabla}\"", StringComparison.Ordinal);
        Assert.Contains(comandos, c => Lee(c.Sql, "Variantes"));
        Assert.All(comandos.Where(c => Lee(c.Sql, "Variantes") || Lee(c.Sql, "ProveedoresVariantes")), c => Assert.False(c.EnTransaccion, c.Sql));
        // El folio y la existencia, dentro: se leen para escribirlos.
        Assert.Contains(comandos, c => Lee(c.Sql, "Existencias") && c.EnTransaccion);
        Assert.Contains(comandos, c => c.Sql.Contains("INSERT INTO \"Ventas\"", StringComparison.Ordinal) && c.EnTransaccion);
    }
}
