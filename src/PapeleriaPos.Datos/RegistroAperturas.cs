using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos;

/// <summary>
/// Abrir paquete (docs/fase-2-inventario.md, entrega 2, parte 3): cuando el paquete y la pieza son productos distintos
/// (como vienen de eleventa), sale el paquete a su costo y entran las piezas con ese mismo costo. Sin existencia del
/// paquete también se puede (se vende sin existencia): sale a su último costo. En el piloto con eleventa
/// (<paramref name="lecturaDeEleventa"/>) no se abre aquí: el inventario se lleva en eleventa (<see cref="TrastiendaEnElPiloto"/>).
/// </summary>
public sealed class RegistroAperturas(PosDbContext db, bool lecturaDeEleventa = false)
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    public async Task<AperturaDto> AbrirAsync(SolicitudAbrirPaquete s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, s.SucursalId, AccionControlada.AbrirPaquete, "No tienes permiso para abrir paquetes.", ct);
        TrastiendaEnElPiloto.RevisarInventario(lecturaDeEleventa);
        if (s.OrigenId == s.DestinoId)
            throw new ArgumentException("El paquete y lo que trae son productos distintos.");
        var origen = await VarianteAsync(s.OrigenId, ct);
        var destino = await VarianteAsync(s.DestinoId, ct);
        var (cantidad, cantidadDestino) = (new Cantidad(s.CantidadMilesimas), new Cantidad(s.CantidadDestinoMilesimas));
        if (!cantidad.EsPositiva || !cantidadDestino.EsPositiva)
            throw new ArgumentException("Escribe cuántos paquetes se abren y cuántas piezas traen.");
        if (!origen.AdmiteCantidad(cantidad) || !destino.AdmiteCantidad(cantidadDestino))
            throw new ArgumentException("Las piezas se cuentan enteras.");

        var inventario = new RegistroInventario(db);
        var id = Ids.Nuevo();
        var salida = Kardex.Salida(s.SucursalId, origen, TipoMovimiento.AperturaPaquete, cantidad, await inventario.ExistenciaAsync(s.SucursalId, origen.Id, ct),
            await new RegistroCaja(db).UltimoCostoAsync(origen.Id, ct), Ahora, documentoTipo: nameof(AperturaPaquete), documentoId: id);
        var costo = -salida.CostoCentavos;
        await inventario.AgregarAsync(salida, ct);
        await inventario.AgregarAsync(Kardex.Entrada(s.SucursalId, destino, TipoMovimiento.AperturaPaquete, cantidadDestino, costo, Ahora,
            documentoTipo: nameof(AperturaPaquete), documentoId: id), ct);
        db.AperturasPaquete.Add(new AperturaPaquete
        {
            Id = id, SucursalId = s.SucursalId, OrigenId = origen.Id, Cantidad = cantidad, DestinoId = destino.Id, CantidadDestino = cantidadDestino,
            CostoCentavos = costo, UsuarioId = s.UsuarioId, FechaUtc = Ahora,
        });
        new ServicioControl(db).Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.AbrirPaquete,
            $"Abrió {cantidad} «{RegistroCompras.Nombre(origen)}» en {cantidadDestino} «{RegistroCompras.Nombre(destino)}»", costo);
        await db.SaveChangesAsync(ct);
        return (await RecientesAsync(s.SucursalId, 1, ct)).Single();
    }

    public async Task<List<AperturaDto>> RecientesAsync(Guid sucursalId, int maximo = 30, CancellationToken ct = default)
    {
        var aperturas = await db.AperturasPaquete.AsNoTracking().Where(a => a.SucursalId == sucursalId)
            .OrderByDescending(a => a.FechaUtc).Take(maximo).ToListAsync(ct);
        var ids = aperturas.SelectMany(a => new[] { a.OrigenId, a.DestinoId }).Distinct().ToList();
        var nombres = (await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca).Where(v => ids.Contains(v.Id)).ToListAsync(ct))
            .ToDictionary(v => v.Id, RegistroCompras.Nombre);
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        return aperturas.Select(a => new AperturaDto(HoraMexico.ALocal(a.FechaUtc), nombres.GetValueOrDefault(a.OrigenId, "?"), a.Cantidad.EnUnidades,
            nombres.GetValueOrDefault(a.DestinoId, "?"), a.CantidadDestino.EnUnidades, usuarios.GetValueOrDefault(a.UsuarioId, "?"))).ToList();
    }

    /// <summary>La última vez que se abrió ese paquete, para proponer lo mismo.</summary>
    public async Task<UltimaAperturaDto?> UltimaAsync(Guid origenId, CancellationToken ct = default)
    {
        var ultima = await db.AperturasPaquete.AsNoTracking().Where(a => a.OrigenId == origenId).OrderByDescending(a => a.FechaUtc).FirstOrDefaultAsync(ct);
        if (ultima is null || await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca)
                .SingleOrDefaultAsync(v => v.Id == ultima.DestinoId && v.Activa, ct) is not { } destino)
            return null;
        return new UltimaAperturaDto(destino.Id, RegistroCompras.Nombre(destino), ultima.CantidadDestino.EnUnidades / ultima.Cantidad.EnUnidades);
    }

    private async Task<Variante> VarianteAsync(Guid id, CancellationToken ct)
    {
        var variante = await db.Variantes.Include(v => v.Producto).ThenInclude(p => p!.Marca).SingleOrDefaultAsync(v => v.Id == id, ct)
            ?? throw new InvalidOperationException("El producto ya no existe.");
        if (!variante.Producto!.LlevaInventario)
            throw new InvalidOperationException($"«{RegistroCompras.Nombre(variante)}» es un servicio: no lleva inventario.");
        return variante;
    }
}
