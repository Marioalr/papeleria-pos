using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos;

/// <summary>
/// Ajustes y mermas (docs/fase-2-inventario.md, entrega 3, parte 1). Pasan por las reglas como lo de la fase 1: el
/// monto que se revisa es el costo (lo que vale lo que entra o sale), así una merma chica la registra el cajero solo y
/// una grande pide al encargado. Cada uno es un movimiento del kardex con su documento y queda en la bitácora; desde el
/// umbral de la sucursal se le avisa al dueño. En el piloto con eleventa (<paramref name="lecturaDeEleventa"/>) no se hacen:
/// el inventario se lleva en eleventa (<see cref="TrastiendaEnElPiloto"/>). La existencia se lee ya con el candado de
/// escritura tomado (revisión del PR #138): lo que agregue o venda otra caja mientras tanto espera, en lugar de perderse.
/// </summary>
public sealed class RegistroAjustes(PosDbContext db, bool lecturaDeEleventa = false)
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Corrige la existencia a la que se encontró: entra o sale la diferencia, con su costo promedio.</summary>
    public async Task<AjusteInventarioDto> AjustarAsync(SolicitudAjuste s, CancellationToken ct = default)
    {
        if (lecturaDeEleventa)
            await TrastiendaEnElPiloto.RechazarAsync(db, s.UsuarioId, s.SucursalId, AccionControlada.AjustarInventario, ct);
        if (string.IsNullOrWhiteSpace(s.Motivo))
            throw new ArgumentException("Escribe por qué se ajusta.");
        var variante = await VarianteAsync(s.VarianteId, ct);
        var real = new Cantidad(s.ExistenciaRealMilesimas);
        if (real.Milesimas < 0)
            throw new ArgumentException("La existencia no puede ser negativa.");
        if (!variante.AdmiteCantidad(real))
            throw new ArgumentException($"«{RegistroCompras.Nombre(variante)}» se cuenta en piezas enteras.");
        // Desde aquí, con el candado de escritura: la existencia que se lee es la que se escribe.
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var inventario = new RegistroInventario(db);
        var existencia = await inventario.ExistenciaAsync(s.SucursalId, variante.Id, ct);
        var diferencia = real - existencia.Cantidad;
        if (diferencia.Milesimas == 0)
            throw new InvalidOperationException("La existencia ya es esa: no hay nada que ajustar.");

        var id = Ids.Nuevo();
        var ultimo = await new RegistroCaja(db).UltimoCostoAsync(variante.Id, ct);
        var movimiento = diferencia.EsPositiva
            ? Kardex.Entrada(s.SucursalId, variante, TipoMovimiento.Ajuste, diferencia,
                CostoDeEntrada(existencia, diferencia, ultimo), Ahora, s.Motivo.Trim(), nameof(AjusteInventario), id)
            : Kardex.Salida(s.SucursalId, variante, TipoMovimiento.Ajuste, diferencia.Abs(), existencia, ultimo, Ahora, s.Motivo.Trim(),
                nameof(AjusteInventario), id);
        var detalle = $"Ajustó «{RegistroCompras.Nombre(variante)}»: de {existencia.Cantidad} a {real} ({(diferencia.EsPositiva ? "+" : "")}{diferencia})";
        var ajuste = await GuardarAsync(id, s.UsuarioId, s.SucursalId, variante, movimiento, null, s.Motivo.Trim(), AccionControlada.AjustarInventario,
            detalle, s.AutorizacionId, ct);
        await transaccion.CommitAsync(ct);
        return await DtoAsync(ajuste, variante, s.UsuarioId, ct);
    }

    /// <summary>Saca lo dañado, caducado o perdido a su costo promedio: es pérdida.</summary>
    public async Task<AjusteInventarioDto> MermaAsync(SolicitudMerma s, CancellationToken ct = default)
    {
        if (lecturaDeEleventa)
            await TrastiendaEnElPiloto.RechazarAsync(db, s.UsuarioId, s.SucursalId, AccionControlada.Merma, ct);
        if (s.Causa == CausaMerma.Otro && string.IsNullOrWhiteSpace(s.Motivo))
            throw new ArgumentException("Escribe qué pasó.");
        var variante = await VarianteAsync(s.VarianteId, ct);
        var cantidad = new Cantidad(s.CantidadMilesimas);
        if (!cantidad.EsPositiva)
            throw new ArgumentException("Escribe cuántos se perdieron.");
        if (!variante.AdmiteCantidad(cantidad))
            throw new ArgumentException($"«{RegistroCompras.Nombre(variante)}» se cuenta en piezas enteras.");
        var motivo = string.IsNullOrWhiteSpace(s.Motivo) ? Causa(s.Causa) : $"{Causa(s.Causa)}: {s.Motivo.Trim()}";

        var id = Ids.Nuevo();
        // Desde aquí, con el candado de escritura: la existencia que se lee es la que se escribe.
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var existencia = await new RegistroInventario(db).ExistenciaAsync(s.SucursalId, variante.Id, ct);
        var movimiento = Kardex.Salida(s.SucursalId, variante, TipoMovimiento.Merma, cantidad, existencia,
            await new RegistroCaja(db).UltimoCostoAsync(variante.Id, ct), Ahora, motivo, nameof(AjusteInventario), id);
        var detalle = $"Merma de {cantidad} «{RegistroCompras.Nombre(variante)}»";
        var merma = await GuardarAsync(id, s.UsuarioId, s.SucursalId, variante, movimiento, s.Causa, motivo, AccionControlada.Merma, detalle, s.AutorizacionId, ct);
        await transaccion.CommitAsync(ct);
        return await DtoAsync(merma, variante, s.UsuarioId, ct);
    }

    /// <summary>Los últimos 50 de la sucursal. El costo, solo para quien ve costos (el dueño y el encargado).</summary>
    public async Task<List<AjusteInventarioDto>> RecientesAsync(Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        var veCostos = await new RegistroCatalogo(db).VeCostosAsync(usuarioId, ct);
        var ajustes = await db.AjustesInventario.AsNoTracking().Where(a => a.SucursalId == sucursalId).OrderByDescending(a => a.FechaUtc).Take(50).ToListAsync(ct);
        var ids = ajustes.Select(a => a.VarianteId).Distinct().ToList();
        var variantes = await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca).Where(v => ids.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, ct);
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        return ajustes.Select(a => Dto(a, variantes.TryGetValue(a.VarianteId, out var v) ? RegistroCompras.Nombre(v) : "?", usuarios, veCostos)).ToList();
    }

    /// <summary>Guarda el ajuste o la merma, dentro de la transacción de quien llama (que la confirma).</summary>
    private async Task<AjusteInventario> GuardarAsync(
        Guid id, Guid usuarioId, Guid sucursalId, Variante variante, MovimientoInventario movimiento, CausaMerma? causa, string motivo,
        AccionControlada accion, string detalle, Guid? autorizacionId, CancellationToken ct)
    {
        // La regla se revisa con lo que vale lo que entra o sale; sin permiso o sin la autorización, no se guarda nada.
        var costo = Math.Abs(movimiento.CostoCentavos);
        await new ServicioControl(db).RequerirAsync(usuarioId, sucursalId, null, accion, costo, detalle, autorizacionId, motivo, ct: ct);
        var autorizo = autorizacionId is { } a ? await db.Autorizaciones.Where(x => x.Id == a).Select(x => (Guid?)x.AutorizadorId).FirstOrDefaultAsync(ct) : null;
        await new RegistroInventario(db).AgregarAsync(movimiento, ct);
        var ajuste = new AjusteInventario
        {
            Id = id, SucursalId = sucursalId, VarianteId = variante.Id, Tipo = movimiento.Tipo, Cantidad = movimiento.Cantidad,
            CostoCentavos = movimiento.CostoCentavos, Causa = causa, Motivo = motivo, UsuarioId = usuarioId, AutorizoId = autorizo, FechaUtc = Ahora,
        };
        db.AjustesInventario.Add(ajuste);
        await db.SaveChangesAsync(ct);
        return ajuste;
    }

    private async Task<AjusteInventarioDto> DtoAsync(AjusteInventario ajuste, Variante variante, Guid usuarioId, CancellationToken ct)
    {
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        return Dto(ajuste, RegistroCompras.Nombre(variante), usuarios, await new RegistroCatalogo(db).VeCostosAsync(usuarioId, ct));
    }

    /// <summary>Lo que entra en un ajuste vale el costo promedio (sin existencia, el último costo).</summary>
    private static long CostoDeEntrada(Existencia existencia, Cantidad cantidad, long ultimoCosto) =>
        existencia.CostoPromedio is { } promedio && promedio > 0
            ? (long)Math.Round(promedio * cantidad.Milesimas / Cantidad.Escala, MidpointRounding.AwayFromZero)
            : cantidad.Importe(ultimoCosto);

    private async Task<Variante> VarianteAsync(Guid id, CancellationToken ct)
    {
        var variante = await db.Variantes.Include(v => v.Producto).ThenInclude(p => p!.Marca).SingleOrDefaultAsync(v => v.Id == id, ct)
            ?? throw new InvalidOperationException("El producto ya no existe.");
        if (!variante.Producto!.LlevaInventario)
            throw new InvalidOperationException($"«{RegistroCompras.Nombre(variante)}» es un servicio: no lleva inventario.");
        return variante;
    }

    public static string Causa(CausaMerma causa) => causa switch
    {
        CausaMerma.Danado => "Dañado",
        CausaMerma.Caducado => "Caducado",
        CausaMerma.Perdido => "Perdido",
        _ => "Otro",
    };

    private static AjusteInventarioDto Dto(AjusteInventario a, string producto, Dictionary<Guid, string> usuarios, bool veCostos) =>
        new(HoraMexico.ALocal(a.FechaUtc), a.Tipo, producto, a.Cantidad.EnUnidades, veCostos ? a.CostoCentavos : null, a.Causa, a.Motivo,
            usuarios.GetValueOrDefault(a.UsuarioId, "?"), a.AutorizoId is { } x ? usuarios.GetValueOrDefault(x) : null);
}
