using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Pruebas.Datos;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// La lectura de eleventa de cada pocos minutos decide si un código es de fábrica o interno con la misma regla que el
/// catálogo (<see cref="CodigoBarras.OrigenDe"/>): los EAN-13 que empiezan con 2 son internos, como los que da el POS.
/// </summary>
public sealed class OrigenDeLosCodigosTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        await using var db = _base.Contexto();
        db.Sucursales.Add(new Sucursal { Clave = "M", Nombre = "Matriz" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private static FilaProducto Producto(int id, string codigo, string descripcion) => new(id, codigo, descripcion, 'U', 1, 10, 15, 0, 2, false);

    private async Task SincronizarAsync(params FilaProducto[] productos)
    {
        await using var db = _base.Contexto();
        await SincronizacionEleventa.AplicarAsync(db, new DatosEleventa { Departamentos = [new(2, "Papelería", true)], Productos = [.. productos] },
            _base.Reloj.GetUtcNow().UtcDateTime);
    }

    private async Task<Dictionary<string, OrigenCodigo>> OrigenesAsync()
    {
        await using var db = _base.Contexto();
        return await db.CodigosBarras.AsNoTracking().ToDictionaryAsync(c => c.Codigo, c => c.Origen);
    }

    [Fact]
    public async Task Un_producto_nuevo_con_un_EAN_13_que_empieza_con_2_llega_como_interno()
    {
        // 2000000000015 es el primer código interno que da el POS.
        await SincronizarAsync(Producto(1, "2000000000015", "Separadores de colores"), Producto(2, "7501035102446", "Cinta Janel"),
            Producto(3, "IMP", "Impresiones"));

        Assert.Equal(new Dictionary<string, OrigenCodigo>
        {
            ["2000000000015"] = OrigenCodigo.Interno,
            ["7501035102446"] = OrigenCodigo.Fabrica,
            ["IMP"] = OrigenCodigo.Interno,
        }, await OrigenesAsync());
    }

    [Fact]
    public async Task Si_en_eleventa_le_cambian_el_codigo_por_uno_que_empieza_con_2_queda_como_interno()
    {
        await SincronizarAsync(Producto(1, "7501035102446", "Goma"), Producto(2, "7509876543213", "Regla"));
        // A la regla aquí le agregaron otro código: el nuevo de eleventa se le agrega, no cambia el que tenía.
        await using (var db = _base.Contexto())
        {
            var pieza = await db.CodigosBarras.Where(c => c.Codigo == "7509876543213").Select(c => c.PresentacionId).SingleAsync();
            db.CodigosBarras.Add(new CodigoBarras { Codigo = "7500000000015", PresentacionId = pieza });
            await db.SaveChangesAsync();
        }

        await SincronizarAsync(Producto(1, "2000000000022", "Goma"), Producto(2, "2000000000039", "Regla"));

        Assert.Equal(new Dictionary<string, OrigenCodigo>
        {
            ["2000000000022"] = OrigenCodigo.Interno,
            ["7509876543213"] = OrigenCodigo.Fabrica,
            ["7500000000015"] = OrigenCodigo.Fabrica,
            ["2000000000039"] = OrigenCodigo.Interno,
        }, await OrigenesAsync());
    }
}
