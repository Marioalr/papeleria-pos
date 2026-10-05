using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos;

/// <summary>
/// La depuración del catálogo antes del piloto (docs/fase-2-inventario.md, entrega 1): dar de baja lo que ya no existe,
/// juntar lo capturado dos veces y cambiar la unidad de lo que se vende a granel. Cada decisión se guarda con el ID de
/// eleventa para repetirla igual en la importación final (<see cref="RepetirAsync"/>). Las existencias se mueven con
/// ajustes del kardex: la historia nunca se reescribe.
/// </summary>
public sealed class RegistroDepuracion(PosDbContext db)
{
    private const string Eleventa = "eleventa";

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Lo decide quien puede editar el catálogo (el dueño y el encargado). Al repetir en otra base no hay usuario.</summary>
    private async Task RequerirAsync(Guid? usuarioId, Guid sucursalId, CancellationToken ct)
    {
        if (usuarioId is not { } id)
            return;
        var decision = await new ServicioControl(db).EvaluarAsync(id, sucursalId, AccionControlada.EditarCatalogo, ct: ct);
        if (decision.Nivel != NivelPermiso.Permitido)
            throw new PermisoRequerido(AccionControlada.EditarCatalogo, NivelPermiso.NoPermitido, "La depuración la hacen el dueño y el encargado.");
    }

    // ── Dar de baja ──────────────────────────────────────────────────────────

    /// <summary>Ya no existe: el producto sale de la venta y su existencia queda en cero (ajuste en cada sucursal).</summary>
    public async Task DarDeBajaAsync(Guid? usuarioId, Guid sucursalId, Guid varianteId, CancellationToken ct = default)
    {
        await RequerirAsync(usuarioId, sucursalId, ct);
        var variante = await VarianteAsync(varianteId, ct);
        var nombre = Nombre(variante);
        await DejarEnCeroAsync(variante, $"Dado de baja del catálogo", ct);
        variante.Activa = false;
        if (variante.Producto!.Variantes.All(v => !v.Activa))
            variante.Producto.Activo = false;
        await DecidirAsync(TipoDecisionDepuracion.DarDeBaja, variante, null, $"«{nombre}» se dio de baja", usuarioId, sucursalId, ct);
        await db.SaveChangesAsync(ct);
    }

    // ── Juntar ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Estaba capturado dos veces: los códigos (con sus presentaciones), la existencia y los proveedores del origen pasan
    /// al destino; el origen queda inactivo con su historia y apuntando al destino. Los precios del destino se quedan.
    /// </summary>
    public async Task JuntarAsync(Guid? usuarioId, Guid sucursalId, Guid origenId, Guid destinoId, CancellationToken ct = default)
    {
        await RequerirAsync(usuarioId, sucursalId, ct);
        if (origenId == destinoId)
            throw new ArgumentException("Elige otro producto para juntar.");
        var origen = await VarianteAsync(origenId, ct);
        var destino = await VarianteAsync(destinoId, ct);
        if (origen.JuntadaEnId is not null)
            throw new InvalidOperationException($"«{Nombre(origen)}» ya se había juntado con otro.");
        if (!destino.Activa || destino.JuntadaEnId is not null)
            throw new InvalidOperationException($"«{Nombre(destino)}» no está activo: elige el que se queda.");
        if (origen.UnidadBase != destino.UnidadBase)
            throw new InvalidOperationException($"Uno se vende por {origen.UnidadBase} y el otro por {destino.UnidadBase}: primero cambia la unidad.");

        // Presentaciones: los códigos van a la del mismo contenido; si no hay, la presentación entera pasa al destino.
        foreach (var presentacion in origen.Presentaciones.ToList())
        {
            var igual = destino.Presentaciones.FirstOrDefault(p => p.Factor == presentacion.Factor);
            if (igual is not null)
            {
                foreach (var codigo in presentacion.Codigos.ToList())
                {
                    presentacion.Codigos.Remove(codigo);
                    codigo.PresentacionId = igual.Id;
                    igual.Codigos.Add(codigo);
                }
            }
            else if (!presentacion.EsBase)
            {
                if (destino.Presentaciones.Any(p => string.Equals(p.Nombre, presentacion.Nombre, StringComparison.CurrentCultureIgnoreCase)))
                    presentacion.Nombre = $"{presentacion.Nombre} ({presentacion.Factor})";
                origen.Presentaciones.Remove(presentacion);
                presentacion.VarianteId = destino.Id;
                destino.Presentaciones.Add(presentacion);
                // Su precio propio, si tenía, se va con ella.
                foreach (var precio in await db.Precios.Where(p => p.PresentacionId == presentacion.Id).ToListAsync(ct))
                    precio.VarianteId = destino.Id;
            }
        }

        // Existencia: sale del origen y entra al destino con el mismo costo, en cada sucursal.
        foreach (var existencia in await ConExistenciaAsync(origen.Id, ct))
        {
            // También un faltante (lo vendido sin existencia) pasa al destino.
            var motivo = $"Se juntó «{Nombre(origen)}» con «{Nombre(destino)}»";
            var inventario = new RegistroInventario(db);
            var (cantidad, costo) = (existencia.Cantidad, existencia.CostoCentavos);
            await inventario.AgregarAsync(Movimiento(existencia.SucursalId, origen, -cantidad, -costo, motivo), ct);
            await inventario.AgregarAsync(Movimiento(existencia.SucursalId, destino, cantidad, costo, motivo), ct);
        }

        // Proveedores: el destino se queda con el último costo más reciente de cada uno.
        var proveedoresDestino = await db.ProveedoresVariantes.Where(p => p.VarianteId == destino.Id).ToListAsync(ct);
        foreach (var pv in await db.ProveedoresVariantes.Where(p => p.VarianteId == origen.Id).ToListAsync(ct))
        {
            var mismo = proveedoresDestino.FirstOrDefault(p => p.ProveedorId == pv.ProveedorId);
            if (mismo is null)
                pv.VarianteId = destino.Id;
            else
            {
                if ((pv.UltimaCompraUtc ?? DateTime.MinValue) > (mismo.UltimaCompraUtc ?? DateTime.MinValue) || mismo.UltimoCostoCentavos is null)
                    (mismo.UltimoCostoCentavos, mismo.UltimaCompraUtc) = (pv.UltimoCostoCentavos, pv.UltimaCompraUtc);
                mismo.CodigoProveedor ??= pv.CodigoProveedor;
                db.ProveedoresVariantes.Remove(pv);
            }
        }

        (origen.Activa, origen.JuntadaEnId) = (false, destino.Id);
        if (origen.Producto!.Variantes.All(v => !v.Activa))
            origen.Producto.Activo = false;
        await DecidirAsync(TipoDecisionDepuracion.Juntar, origen, destino, $"«{Nombre(origen)}» se juntó con «{Nombre(destino)}»", usuarioId, sucursalId, ct);
        await db.SaveChangesAsync(ct);
    }

    // ── Cambiar la unidad ────────────────────────────────────────────────────

    /// <summary>
    /// Se vende por otra unidad. Con <paramref name="factorMilesimas"/> (cuántas nuevas hay en una de antes) la existencia
    /// y las presentaciones se convierten; sin él, la existencia queda en cero para contarla de nuevo. El precio de
    /// menudeo es el nuevo y las escalas de mayoreo (en la unidad de antes) se quitan.
    /// </summary>
    public async Task CambiarUnidadAsync(
        Guid? usuarioId, Guid sucursalId, Guid varianteId, UnidadBase unidad, long? factorMilesimas, long precioCentavos, CancellationToken ct = default)
    {
        await RequerirAsync(usuarioId, sucursalId, ct);
        var variante = await VarianteAsync(varianteId, ct);
        if (variante.UnidadBase == unidad)
            throw new ArgumentException("Ya se vende por esa unidad.");
        if (precioCentavos <= 0)
            throw new ArgumentException("Escribe el precio de menudeo en la unidad nueva.");
        if (factorMilesimas is <= 0)
            throw new ArgumentException("La conversión debe ser mayor a cero.");
        var anterior = variante.UnidadBase;
        var factor = factorMilesimas is { } f ? new Cantidad(f) : (Cantidad?)null;

        var existencias = await ConExistenciaAsync(variante.Id, ct);
        var inventario = new RegistroInventario(db);
        var motivo = factor is { } x
            ? $"Cambio de unidad: de {anterior} a {unidad} (1 = {x})"
            : $"Cambio de unidad: de {anterior} a {unidad}, se cuenta de nuevo";
        var saldos = existencias.Select(e => (e.SucursalId, e.Cantidad, e.CostoCentavos)).ToList();
        foreach (var (sucursal, cantidad, costo) in saldos)
            await inventario.AgregarAsync(Movimiento(sucursal, variante, -cantidad, -costo, motivo), ct);

        variante.UnidadBase = unidad;
        if (factor is { } conversion)
        {
            foreach (var p in variante.Presentaciones.Where(p => !p.EsBase))
                p.Factor = new Cantidad(Redondeo.Dividir((Int128)p.Factor.Milesimas * conversion.Milesimas, Cantidad.Escala));
            foreach (var (sucursal, cantidad, costo) in saldos)
            {
                var nueva = new Cantidad(Redondeo.Dividir((Int128)cantidad.Milesimas * conversion.Milesimas, Cantidad.Escala));
                if (nueva.Milesimas != 0)
                    await inventario.AgregarAsync(Movimiento(sucursal, variante, nueva, costo, motivo), ct);
            }
        }
        var base_ = variante.Presentaciones.Single(p => p.EsBase);
        base_.Nombre = unidad switch { UnidadBase.Metro => "Metro", UnidadBase.Kilogramo => "Kilo", UnidadBase.Litro => "Litro", _ => "Pieza" };

        db.Precios.RemoveRange(await db.Precios.Where(p => p.VarianteId == variante.Id && p.PresentacionId == null && p.SucursalId == null).ToListAsync(ct));
        db.Precios.Add(new Precio { VarianteId = variante.Id, PrecioCentavos = precioCentavos });

        var decision = await DecidirAsync(TipoDecisionDepuracion.CambiarUnidad, variante, null,
            $"«{Nombre(variante)}» pasó de {anterior} a {unidad}" + (factor is { } y ? $" (1 = {y})" : ", se cuenta de nuevo") + $", a {precioCentavos / 100m:N2}",
            usuarioId, sucursalId, ct);
        (decision.Unidad, decision.FactorMilesimas, decision.PrecioCentavos) = (unidad, factorMilesimas, precioCentavos);
        await db.SaveChangesAsync(ct);
    }

    // ── Las decisiones y repetirlas ──────────────────────────────────────────

    public async Task<List<DecisionDepuracion>> DecisionesAsync(CancellationToken ct = default) =>
        await db.DecisionesDepuracion.AsNoTracking().OrderByDescending(d => d.FechaUtc).ToListAsync(ct);

    /// <summary>
    /// Repite en esta base (una importación nueva de eleventa) las decisiones tomadas en otra, en el mismo orden. Se
    /// buscan por el ID de eleventa; las que no se encuentran (un producto creado en el POS, o que ya no viene) se reportan.
    /// </summary>
    public async Task<(int Aplicadas, List<string> Omitidas)> RepetirAsync(IReadOnlyList<DecisionDepuracion> decisiones, CancellationToken ct = default)
    {
        var sucursalId = await db.Sucursales.Select(s => s.Id).FirstAsync(ct);
        var aplicadas = 0;
        var omitidas = new List<string>();
        foreach (var d in decisiones.OrderBy(d => d.FechaUtc))
        {
            var variante = await PorEleventaAsync(d.OrigenEleventa, ct);
            var destino = d.Tipo == TipoDecisionDepuracion.Juntar ? await PorEleventaAsync(d.DestinoEleventa, ct) : null;
            if (variante is null || (d.Tipo == TipoDecisionDepuracion.Juntar && destino is null))
            {
                omitidas.Add($"{d.Descripcion}: no viene en esta importación.");
                continue;
            }
            if (await db.DecisionesDepuracion.AnyAsync(x => x.Tipo == d.Tipo && x.VarianteId == variante.Value, ct))
            {
                omitidas.Add($"{d.Descripcion}: ya estaba aplicada.");
                continue;
            }
            try
            {
                switch (d.Tipo)
                {
                    case TipoDecisionDepuracion.DarDeBaja:
                        await DarDeBajaAsync(null, sucursalId, variante.Value, ct);
                        break;
                    case TipoDecisionDepuracion.Juntar:
                        await JuntarAsync(null, sucursalId, variante.Value, destino!.Value, ct);
                        break;
                    case TipoDecisionDepuracion.CambiarUnidad:
                        await CambiarUnidadAsync(null, sucursalId, variante.Value, d.Unidad!.Value, d.FactorMilesimas, d.PrecioCentavos!.Value, ct);
                        break;
                }
                aplicadas++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                db.ChangeTracker.Clear();
                omitidas.Add($"{d.Descripcion}: {ex.Message}");
            }
        }
        return (aplicadas, omitidas);
    }

    private async Task<Guid?> PorEleventaAsync(string? idEleventa, CancellationToken ct) =>
        idEleventa is null
            ? null
            : await db.MapeosImportacion.Where(m => m.Origen == Eleventa && m.Entidad == nameof(Variante) && m.IdOrigen == idEleventa)
                .Select(m => (Guid?)m.IdNuevo).FirstOrDefaultAsync(ct);

    private async Task<DecisionDepuracion> DecidirAsync(
        TipoDecisionDepuracion tipo, Variante variante, Variante? destino, string descripcion, Guid? usuarioId, Guid sucursalId, CancellationToken ct)
    {
        var decision = new DecisionDepuracion
        {
            Tipo = tipo, VarianteId = variante.Id, DestinoId = destino?.Id, Descripcion = descripcion, UsuarioId = usuarioId, FechaUtc = Ahora,
            OrigenEleventa = await EleventaAsync(variante.Id, ct), DestinoEleventa = destino is null ? null : await EleventaAsync(destino.Id, ct),
        };
        db.DecisionesDepuracion.Add(decision);
        new ServicioControl(db).Registrar(sucursalId, null, usuarioId, AccionControlada.EditarCatalogo, $"Depuración: {descripcion}");
        return decision;
    }

    private async Task<string?> EleventaAsync(Guid varianteId, CancellationToken ct) =>
        await db.MapeosImportacion.Where(m => m.Origen == Eleventa && m.Entidad == nameof(Variante) && m.IdNuevo == varianteId)
            .Select(m => m.IdOrigen).FirstOrDefaultAsync(ct);

    private async Task<Variante> VarianteAsync(Guid varianteId, CancellationToken ct) =>
        await db.Variantes.Include(v => v.Producto).ThenInclude(p => p!.Variantes).Include(v => v.Presentaciones).ThenInclude(p => p.Codigos)
            .AsSplitQuery().SingleOrDefaultAsync(v => v.Id == varianteId, ct)
        ?? throw new InvalidOperationException("El producto ya no existe.");

    private async Task<List<Existencia>> ConExistenciaAsync(Guid varianteId, CancellationToken ct) =>
        (await db.Existencias.Where(e => e.VarianteId == varianteId).ToListAsync(ct)).Where(e => e.Cantidad.Milesimas != 0 || e.CostoCentavos != 0).ToList();

    private async Task DejarEnCeroAsync(Variante variante, string motivo, CancellationToken ct)
    {
        var inventario = new RegistroInventario(db);
        foreach (var e in await ConExistenciaAsync(variante.Id, ct))
            await inventario.AgregarAsync(Movimiento(e.SucursalId, variante, -e.Cantidad, -e.CostoCentavos, motivo), ct);
    }

    /// <summary>Un ajuste por la cantidad (con signo) y su costo, que puede dejar la existencia en cero exacto.</summary>
    private MovimientoInventario Movimiento(Guid sucursalId, Variante variante, Cantidad cantidad, long costoCentavos, string motivo) =>
        new()
        {
            SucursalId = sucursalId, VarianteId = variante.Id, FechaUtc = Ahora, Tipo = TipoMovimiento.Ajuste, Cantidad = cantidad,
            CostoCentavos = costoCentavos, Motivo = motivo, DocumentoTipo = nameof(DecisionDepuracion),
        };

    private static string Nombre(Variante v) =>
        string.IsNullOrWhiteSpace(v.Descripcion) ? v.Producto!.Nombre : $"{v.Producto!.Nombre} {v.Descripcion}";
}
