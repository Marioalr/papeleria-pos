using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// El papel de las impresiones en el inventario (el dueño, 2026-11-16): cada papel de Configuración › Impresoras e impresiones puede
/// tener su producto del inventario (por hoja). Al cobrar las impresiones, ese producto baja con las hojas usadas, como un consumo
/// del servicio (<see cref="TipoMovimiento.ConsumoServicio"/>, con la venta como documento); si se cancela la venta o se devuelve
/// su línea «Impresiones», las hojas regresan. Va dentro de la transacción de la venta (o de la devolución).
/// </summary>
internal static class PapelDeImpresiones
{
    /// <summary>El documento de esos movimientos del kardex: la venta, con este nombre.</summary>
    public const string Documento = "PapelDeImpresiones";

    public static async Task SacarAsync(PosDbContext db, RegistroInventario inventario, Venta venta, IReadOnlyList<TrabajoImpresion> trabajos, DateTime ahora,
        CancellationToken ct)
    {
        foreach (var papel in await ImpresionesDelPos.EnLaVenta(db).PapelUsadoAsync(venta.SucursalId, trabajos, ct))
        {
            // El producto que se eligió ya no está (o se juntó con otro): a donde quedó; si no hay, no se descuenta nada.
            if (await RegistroInventario.EnLaQueQuedoAsync(db, papel.VarianteId, ct) is not { } variante)
                continue;
            var cantidad = Cantidad.Piezas(papel.Hojas);
            if (!variante.AdmiteCantidad(cantidad))
                continue;
            var existencia = await inventario.ExistenciaAsync(venta.SucursalId, variante.Id, ct);
            var ultimo = await UltimosCostos.DeAsync(db, variante.Id, ct) ?? 0;
            await inventario.AgregarAsync(Kardex.Salida(venta.SucursalId, variante, TipoMovimiento.ConsumoServicio, cantidad, existencia, ultimo, ahora,
                $"Papel de las impresiones ({papel.Hojas} hojas)", Documento, venta.Id), ct);
        }
    }

    /// <summary>Lo que salió con esa venta (y no ha regresado) vuelve a entrar, con lo que valía al salir.</summary>
    public static async Task RegresarAsync(PosDbContext db, RegistroInventario inventario, Venta venta, Guid devolucionId, DateTime ahora, CancellationToken ct)
    {
        var movimientos = await db.MovimientosInventario.AsNoTracking()
            .Where(m => m.DocumentoTipo == Documento && m.DocumentoId == venta.Id && m.SucursalId == venta.SucursalId)
            .ToListAsync(ct);
        foreach (var porVariante in movimientos.GroupBy(m => m.VarianteId))
        {
            // Lo que salió (negativo) menos lo que ya regresó con otra devolución.
            var cantidad = new Cantidad(-porVariante.Sum(m => m.Cantidad.Milesimas));
            var costo = -porVariante.Sum(m => m.CostoCentavos);
            if (!cantidad.EsPositiva || await RegistroInventario.EnLaQueQuedoAsync(db, porVariante.Key, ct) is not { } variante)
                continue;
            await inventario.AgregarAsync(Kardex.Entrada(venta.SucursalId, variante, TipoMovimiento.Devolucion, cantidad, Math.Max(0, costo), ahora,
                $"Papel de las impresiones devueltas ({venta.FolioTexto})", Documento, venta.Id), ct);
        }
    }
}
