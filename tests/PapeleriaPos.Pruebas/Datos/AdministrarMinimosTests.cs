using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// «Administrar mínimos» (dueño, 2026-11-16): prendido, el sugerido cubre por producto cada cuánto se surte y «Bajos en
/// inventario» usa el mínimo calculado para lo que no tiene propio. Con datos ficticios: la pluma azul se vende una al día (el
/// historial de eleventa) y se surte cada 30 días; hay 5.
/// </summary>
public class AdministrarMinimosTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _dueno = new() { Nombre = "Mario", NombreUsuario = "mario", Rol = Rol.Dueno };

    private Guid Sucursal => _catalogo.Matriz.Id;

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _dueno.EstablecerPin("3333");
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        db.Add(_dueno);
        var ahora = _base.Reloj.Ahora.UtcDateTime;
        // Una al día en los últimos 90 días (lo de hoy no cuenta).
        for (var d = 1; d <= 90; d++)
            db.Add(new VentaHistorica
            {
                SucursalId = Sucursal, FechaUtc = ahora.AddDays(-d), FolioOrigen = $"F{d}", TotalCentavos = 1500,
                Lineas = [new VentaHistoricaLinea { VarianteId = _catalogo.Azul.Id, Cantidad = Cantidad.Piezas(1), PrecioCentavos = 1500, ImporteCentavos = 1500 }],
            });
        await db.SaveChangesAsync();
        await MigracionPermisos.AplicarAsync(db);
        // Se surte cada 30 días: una compra y dos «Agregar inventario».
        var inventario = new RegistroInventario(db);
        foreach (var (hace, tipo, piezas) in new[] { (90, TipoMovimiento.Compra, 2), (60, TipoMovimiento.AgregarInventario, 2), (30, TipoMovimiento.AgregarInventario, 1) })
            await inventario.RegistrarAsync(Kardex.Entrada(Sucursal, _catalogo.Azul, tipo, Cantidad.Piezas(piezas), piezas * 500, ahora.AddDays(-hace)));
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private async Task PrenderAsync(bool prendido)
    {
        await using var db = _base.Contexto();
        var registro = new RegistroSugerido(db);
        var antes = await registro.ParametrosAsync(Sucursal);
        await registro.GuardarParametrosAsync(Sucursal, new SolicitudParametrosSugerido(_dueno.Id, antes with { AdministrarMinimos = prendido }));
    }

    private async Task<RenglonSugeridoDto> RenglonAsync()
    {
        await using var db = _base.Contexto();
        return (await new RegistroSugerido(db).CalcularAsync(Sucursal, _dueno.Id)).Proveedores.SelectMany(p => p.Renglones).Single();
    }

    private async Task<(BajosInventarioDto Bajos, int Cuenta)> BajosAsync()
    {
        await using var db = _base.Contexto();
        var reportes = new RegistroReportesInventario(db);
        return (await reportes.BajosAsync(Sucursal, _dueno.Id, null, null, soloConMinimoPropio: false), await reportes.ContarBajosAsync(Sucursal));
    }

    [Fact]
    public async Task Apagado_por_omision_el_sugerido_cubre_lo_general_y_los_bajos_son_3_o_menos()
    {
        await using (var db = _base.Contexto())
            Assert.False((await new RegistroSugerido(db).ParametrosAsync(Sucursal)).AdministrarMinimos);

        var renglon = await RenglonAsync();
        var (bajos, cuenta) = await BajosAsync();

        // 1 al día: mínimo 7 (el colchón), máximo 21 (14 + 7). Hay 5: faltan 16.
        Assert.Equal((1m, 7m, 21m, 16, (int?)null), (renglon.VentaDiaria, renglon.Minimo, renglon.Maximo, renglon.Paquetes, renglon.SeSurteCadaDias));
        // Hay 5, más de 3: no está baja.
        Assert.Empty(bajos.Productos);
        Assert.Equal(0, cuenta);
        Assert.False(bajos.MinimosAdministrados);
    }

    [Fact]
    public async Task Prendido_cubre_cada_cuanto_se_surte_y_los_bajos_usan_el_minimo_calculado()
    {
        await PrenderAsync(true);

        var renglon = await RenglonAsync();
        var (bajos, cuenta) = await BajosAsync();

        // Se surte cada 30 días: el máximo cubre 30 + 7; el mínimo sigue siendo el colchón.
        Assert.Equal((7m, 37m, 32, (int?)30), (renglon.Minimo, renglon.Maximo, renglon.Paquetes, renglon.SeSurteCadaDias));
        // Hay 5, debajo del mínimo calculado (7): está baja, y se pide lo del sugerido.
        var baja = Assert.Single(bajos.Productos);
        Assert.Equal((7m, false, true, 37m, 32m), (baja.Minimo, baja.MinimoPropio, baja.MinimoCalculado, baja.MaximoSugerido, baja.Pedir));
        Assert.Equal(1, cuenta);
        Assert.True(bajos.MinimosAdministrados);
    }

    [Fact]
    public async Task Prendido_el_minimo_propio_sigue_mandando()
    {
        await using (var db = _base.Contexto())
        {
            var azul = await db.Variantes.SingleAsync(v => v.Id == _catalogo.Azul.Id);
            azul.InventarioMinimo = Cantidad.Piezas(4);
            await db.SaveChangesAsync();
        }
        await PrenderAsync(true);

        var (bajos, cuenta) = await BajosAsync();

        // Con su mínimo propio (4), 5 no está baja aunque el calculado sea 7.
        Assert.Empty(bajos.Productos);
        Assert.Equal(0, cuenta);
    }

    [Fact]
    public async Task Prender_y_apagar_queda_en_la_bitacora()
    {
        await PrenderAsync(true);
        await PrenderAsync(true);
        await PrenderAsync(false);

        await using var db = _base.Contexto();
        var anotado = await db.Bitacora.Where(b => b.Accion == AccionControlada.PedirCompra).OrderBy(b => b.FechaUtc).ThenBy(b => b.Id)
            .Select(b => b.Detalle).ToListAsync();
        // Guardar sin cambiar nada no anota.
        Assert.Equal(2, anotado.Count);
        Assert.Contains("Sugerido de compra: Administrar mínimos prendido", anotado);
        Assert.Contains("Sugerido de compra: Administrar mínimos apagado", anotado);
        Assert.False((await new RegistroSugerido(db).ParametrosAsync(Sucursal)).AdministrarMinimos);
    }
}
