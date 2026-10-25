using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// El «último costo», uno solo para todo el POS (revisión del PR #138): el más reciente entre el de un proveedor y el de lo último
/// agregado con «Agregar inventario» con costo de verdad. La importación de eleventa (sin fecha) es el más viejo; lo agregado a $0
/// porque no se conocía ninguno no cuenta; un costo escrito, aunque sea $0, sí.
/// </summary>
public sealed class UltimosCostosTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _dueno = new() { Nombre = "Rosa", NombreUsuario = "rosa", Rol = Rol.Dueno };
    private readonly Proveedor _importado = new() { Nombre = "De eleventa" };
    private readonly Proveedor _papelera = new() { Nombre = "Papelera del centro" };

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _dueno.EstablecerPin("3333");
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        db.AddRange(_dueno, _importado, _papelera);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private EntradaInventario Entrada(DateTime fechaUtc, long costoUnitario, bool escrito) => new()
    {
        SucursalId = _catalogo.Matriz.Id, VarianteId = _catalogo.Azul.Id, Cantidad = Cantidad.Piezas(1), CostoUnitarioCentavos = costoUnitario,
        CostoCentavos = costoUnitario, CostoEscrito = escrito, UsuarioId = _dueno.Id, FechaUtc = fechaUtc,
    };

    [Fact]
    public async Task Es_el_mas_reciente_entre_el_de_un_proveedor_y_lo_agregado_con_costo()
    {
        var ahora = _base.Reloj.Ahora.UtcDateTime;
        await using var db = _base.Contexto();
        async Task<long?> UltimoAsync()
        {
            await db.SaveChangesAsync();
            var uno = await UltimosCostos.DeAsync(db, _catalogo.Azul.Id);
            Assert.Equal(uno, (await UltimosCostos.DeTodasAsync(db)).TryGetValue(_catalogo.Azul.Id, out var deTodas) ? deTodas : null);
            return uno;
        }

        var nunca = await UltimoAsync();
        // La importación de eleventa: sin fecha.
        db.ProveedoresVariantes.Add(new ProveedorVariante { ProveedorId = _importado.Id, VarianteId = _catalogo.Azul.Id, UltimoCostoCentavos = 800 });
        var importado = await UltimoAsync();
        // El cajero agregó sin costo conocido: entró a $0, y eso no es un costo.
        db.EntradasInventario.Add(Entrada(ahora.AddDays(-5), 0, escrito: false));
        var aCeroSinCosto = await UltimoAsync();
        // El dueño agregó con su costo.
        db.EntradasInventario.Add(Entrada(ahora.AddDays(-3), 1200, escrito: true));
        var agregado = await UltimoAsync();
        // Una compra cerrada después.
        db.ProveedoresVariantes.Add(new ProveedorVariante
        {
            ProveedorId = _papelera.Id, VarianteId = _catalogo.Azul.Id, UltimoCostoCentavos = 1500, UltimaCompraUtc = ahora.AddDays(-1),
        });
        var comprado = await UltimoAsync();
        // Lo agregado al costo de ese momento (sin escribirlo, pero de verdad).
        db.EntradasInventario.Add(Entrada(ahora.AddHours(-1), 1450, escrito: false));
        var alDeAhora = await UltimoAsync();
        // Un costo escrito de $0.00 (lo regaló el proveedor) sí cuenta.
        db.EntradasInventario.Add(Entrada(ahora, 0, escrito: true));
        var regalado = await UltimoAsync();

        Assert.Equal(new long?[] { null, 800, 800, 1200, 1500, 1450, 0 }, new[] { nunca, importado, aCeroSinCosto, agregado, comprado, alDeAhora, regalado });
        Assert.Empty(await UltimosCostos.DeAsync(db, []));
    }
}
