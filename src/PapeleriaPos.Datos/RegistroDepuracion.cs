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
/// juntar lo capturado dos veces, cambiar la unidad de lo que se vende a granel y decidir el mayoreo de eleventa que la
/// importación no pudo pasar a escala (fase 3). Cada decisión se guarda con el ID de
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
        await ResolverMayoreoAsync(variante.Id, "Se dio de baja", ct);
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
        // Como sus precios, el mayoreo del origen no pasa: el destino se queda con el suyo.
        await ResolverMayoreoAsync(origen.Id, $"Se juntó con «{Nombre(destino)}»", ct);
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
        // El mayoreo de eleventa estaba en la unidad de antes: se captura de nuevo en la ficha, ya en la nueva.
        await ResolverMayoreoAsync(variante.Id, "Cambió la unidad: el mayoreo se captura en la ficha", ct);

        var decision = await DecidirAsync(TipoDecisionDepuracion.CambiarUnidad, variante, null,
            $"«{Nombre(variante)}» pasó de {anterior} a {unidad}" + (factor is { } y ? $" (1 = {y})" : ", se cuenta de nuevo") + $", a {precioCentavos / 100m:N2}",
            usuarioId, sucursalId, ct);
        (decision.Unidad, decision.FactorMilesimas, decision.PrecioCentavos) = (unidad, factorMilesimas, precioCentavos);
        await db.SaveChangesAsync(ct);
    }

    // ── El mayoreo de eleventa (fase 3, entrega 1) ───────────────────────────

    /// <summary>El mayoreo de eleventa que la importación no pudo pasar a escala, de lo que sigue a la venta.</summary>
    public async Task<List<MayoreoPorRevisarDto>> MayoreoPorRevisarAsync(CancellationToken ct = default)
    {
        var pendientes = await db.MayoreoPorRevisar.AsNoTracking().Where(m => m.ResueltoUtc == null).ToListAsync(ct);
        var ids = pendientes.Select(m => m.VarianteId).ToList();
        var variantes = await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca).Include(v => v.Presentaciones)
            .ThenInclude(p => p.Codigos).Where(v => ids.Contains(v.Id) && v.Activa).AsSplitQuery().ToDictionaryAsync(v => v.Id, ct);
        return pendientes.Where(m => variantes.ContainsKey(m.VarianteId))
            .Select(m =>
            {
                var v = variantes[m.VarianteId];
                var codigo = v.Presentaciones.OrderByDescending(p => p.EsBase).SelectMany(p => p.Codigos).Select(c => c.Codigo).FirstOrDefault();
                return new MayoreoPorRevisarDto(m.Id, v.Id, v.Producto!.Id, Nombre(v), codigo, v.UnidadBase, m.MenudeoCentavos, m.PrecioMayoreoCentavos,
                    m.CostoCentavos, m.VentasConMayoreo, m.Cantidades, m.Motivo, m.Sugerida?.Milesimas);
            })
            .OrderBy(m => m.Producto, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// El encargado decide el mayoreo de eleventa: una escala desde cierta cantidad a cierto precio (por unidad base), o
    /// sin mayoreo. La escala que ya era este mayoreo (la deducida al importar, o una decisión anterior) se cambia o se
    /// quita; las promociones y el menudeo se quedan, y el precio por pieza no puede subir al subir la cantidad. Queda
    /// como decisión de depuración para repetirse en la importación final.
    /// </summary>
    public async Task MayoreoAsync(Guid? usuarioId, Guid sucursalId, Guid varianteId, long? desdeMilesimas, long? precioCentavos, CancellationToken ct = default)
    {
        await RequerirAsync(usuarioId, sucursalId, ct);
        var variante = await VarianteAsync(varianteId, ct);
        var nombre = Nombre(variante);
        if (!variante.Activa)
            throw new InvalidOperationException(variante.JuntadaEnId is null
                ? $"«{nombre}» se dio de baja: ya no lleva mayoreo."
                : $"«{nombre}» se juntó con otro: decide el mayoreo en el que se quedó.");
        var registros = await db.MayoreoPorRevisar.Where(m => m.VarianteId == variante.Id).ToListAsync(ct);
        var escalas = await db.Precios.Where(p => p.VarianteId == variante.Id && p.PresentacionId == null && p.SucursalId == null).ToListAsync(ct);
        var deEleventa = registros.Select(m => m.Escala).OfType<Cantidad>().ToHashSet();
        var anteriores = escalas.Where(p => deEleventa.Contains(p.CantidadMinima)).ToList();
        if (desdeMilesimas is null)
        {
            db.Precios.RemoveRange(anteriores);
            foreach (var m in registros)
                m.Resolver("Sin mayoreo", Ahora);
            await DecidirAsync(TipoDecisionDepuracion.SinMayoreo, variante, null, $"«{nombre}» queda sin el mayoreo de eleventa", usuarioId, sucursalId, ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        var desde = new Cantidad(desdeMilesimas.Value);
        if (desde <= Cantidad.Unidades(1))
            throw new ArgumentException("El mayoreo empieza en más de una pieza: escribe desde cuántas.");
        if (!variante.AdmiteCantidad(desde))
            throw new ArgumentException($"«{nombre}» se vende por pieza: el mayoreo empieza en piezas enteras.");
        if (precioCentavos is not > 0)
            throw new ArgumentException("Escribe el precio de mayoreo.");
        var precio = precioCentavos.Value;
        // Las otras escalas (el menudeo y las promociones de eleventa) se quedan como están.
        var otras = escalas.Except(anteriores).ToList();
        var menudeo = otras.MinBy(p => p.CantidadMinima);
        if (menudeo is not null && precio >= menudeo.PrecioCentavos)
            throw new ArgumentException($"El mayoreo tiene que ser más barato que el menudeo ({Pesos(menudeo.PrecioCentavos)}).");
        var igual = otras.FirstOrDefault(p => p.CantidadMinima == desde);
        if (igual is not null && igual.PrecioCentavos != precio)
            throw new ArgumentException($"Desde {desde} ya hay otro precio ({Pesos(igual.PrecioCentavos)}): cámbialo en la ficha del producto.");
        if (otras.Where(p => p.CantidadMinima < desde).MaxBy(p => p.CantidadMinima) is { } antes && precio > antes.PrecioCentavos)
            throw new ArgumentException($"Desde {antes.CantidadMinima} ya sale a {Pesos(antes.PrecioCentavos)}: desde {desde} no puede costar más.");
        if (otras.Where(p => p.CantidadMinima > desde).MinBy(p => p.CantidadMinima) is { } despues && precio < despues.PrecioCentavos)
            throw new ArgumentException(
                $"Desde {despues.CantidadMinima} sale a {Pesos(despues.PrecioCentavos)}: desde {desde} no puede ser más barato (el precio subiría con la cantidad).");

        // La escala de este mayoreo se cambia o se crea; si ya había una promoción igual, ella es el mayoreo.
        var misma = anteriores.FirstOrDefault(p => p.CantidadMinima == desde);
        db.Precios.RemoveRange(anteriores.Where(p => p != misma));
        if (misma is not null)
            misma.PrecioCentavos = precio;
        else if (igual is null)
            db.Precios.Add(new Precio { VarianteId = variante.Id, CantidadMinima = desde, PrecioCentavos = precio });
        foreach (var m in registros)
            m.Resolver(igual is null ? $"Desde {desde} a {Pesos(precio)}" : $"Igual a la promoción desde {desde}", Ahora, igual is null ? desde : null);
        var decision = await DecidirAsync(TipoDecisionDepuracion.EscalaMayoreo, variante, null,
            $"«{nombre}»: mayoreo desde {desde} a {Pesos(precio)}", usuarioId, sucursalId, ct);
        (decision.FactorMilesimas, decision.PrecioCentavos) = (desde.Milesimas, precio);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// El mayoreo de eleventa ya no se decide (se dio de baja, se juntó, cambió la unidad): lo pendiente se resuelve, y
    /// ninguna escala queda como suya.
    /// </summary>
    private async Task ResolverMayoreoAsync(Guid varianteId, string resolucion, CancellationToken ct)
    {
        foreach (var m in await db.MayoreoPorRevisar.Where(m => m.VarianteId == varianteId).ToListAsync(ct))
            m.Resolver(m.Resolucion ?? resolucion, m.ResueltoUtc ?? Ahora);
    }

    private static string Pesos(long centavos) => (centavos / 100m).ToString("N2");

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
        // Del mayoreo de un producto vale lo último que se decidió.
        var ultimas = decisiones.Where(d => TiposMayoreo.Contains(d.Tipo)).GroupBy(d => d.OrigenEleventa ?? d.Id.ToString())
            .Select(g => g.MaxBy(d => d.FechaUtc)!).ToHashSet();
        foreach (var d in decisiones.OrderBy(d => d.FechaUtc))
        {
            if (TiposMayoreo.Contains(d.Tipo) && !ultimas.Contains(d))
            {
                omitidas.Add($"{d.Descripcion}: después se decidió otra cosa.");
                continue;
            }
            var variante = await PorEleventaAsync(d.OrigenEleventa, ct);
            var destino = d.Tipo == TipoDecisionDepuracion.Juntar ? await PorEleventaAsync(d.DestinoEleventa, ct) : null;
            if (variante is null || (d.Tipo == TipoDecisionDepuracion.Juntar && destino is null))
            {
                omitidas.Add($"{d.Descripcion}: no viene en esta importación.");
                continue;
            }
            var tipos = TiposMayoreo.Contains(d.Tipo) ? TiposMayoreo : [d.Tipo];
            if (await db.DecisionesDepuracion.AnyAsync(x => tipos.Contains(x.Tipo) && x.VarianteId == variante.Value, ct))
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
                    case TipoDecisionDepuracion.EscalaMayoreo:
                        await MayoreoAsync(null, sucursalId, variante.Value, d.FactorMilesimas, d.PrecioCentavos, ct);
                        break;
                    case TipoDecisionDepuracion.SinMayoreo:
                        await MayoreoAsync(null, sucursalId, variante.Value, null, null, ct);
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

    private static readonly TipoDecisionDepuracion[] TiposMayoreo = [TipoDecisionDepuracion.EscalaMayoreo, TipoDecisionDepuracion.SinMayoreo];

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
