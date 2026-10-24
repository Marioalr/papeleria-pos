using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos;

/// <summary>
/// Inventario › «Agregar inventario», como en eleventa (2026-10-24): lo que llegó de un producto, cuántos y el costo de cada uno,
/// sin proveedor ni nota (<see cref="EntradaInventario"/>). En la tienda se usa todo el día, siempre de un producto a la vez;
/// «Recibir mercancía» (las compras de un proveedor, a ciegas y con su nota) sigue igual.
/// <list type="bullet">
/// <item>Todo en una transacción: la existencia y el costo promedio como los deja una compra (su movimiento del kardex con lo que
/// vale lo que entra, <see cref="RegistroInventario.AgregarAsync"/>: el promedio es lo que vale la existencia entre cuántos son),
/// el documento y la bitácora. La existencia nueva sube la versión de las existencias al guardar y llega a las cajas con su
/// aviso, como un ajuste.</item>
/// <item>Con el mismo Id (lo pone la caja), una sola vez: si se perdió la respuesta, otra vez regresa lo que ya se agregó.</item>
/// <item>Con el permiso «Agregar inventario» en «Puede». El costo lo ve y lo cambia quien ve los costos
/// (<see cref="RegistroCatalogo.VeCostosAsync"/>); los demás agregan al costo de ese momento, y si mandan uno se rechaza.</item>
/// <item>En el piloto con eleventa (<paramref name="lecturaDeEleventa"/>) no se hace: el inventario se lleva en eleventa
/// (<see cref="TrastiendaEnElPiloto"/>). El permiso va primero.</item>
/// <item>No se deshace: lo equivocado se corrige con un ajuste, como en eleventa.</item>
/// </list>
/// </summary>
public sealed class RegistroAgregarInventario(PosDbContext db, bool lecturaDeEleventa = false)
{
    /// <summary>Cuántas trae «Lo agregado hoy», como mucho (en la tienda son unas 7 al día).</summary>
    public const int MaximoDelDia = 200;

    /// <summary>A quien manda un costo sin poder verlos.</summary>
    public const string SinPermisoDelCosto =
        "El costo de lo que llega lo cambia quien ve los costos («Compras: la nota y los costos» o «Ver costos, utilidad y margen»). "
        + "Sin escribirlo, entra al costo de ahora.";

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>El producto que se va a agregar: su existencia de ahora y, para quien ve los costos, su costo de ahora.</summary>
    public async Task<ProductoParaAgregarDto> ProductoAsync(Guid sucursalId, Guid varianteId, Guid usuarioId, CancellationToken ct = default)
    {
        await ExigirAsync(usuarioId, sucursalId, ct);
        var variante = await VarianteAsync(varianteId, ct);
        var conCostos = await new RegistroCatalogo(db).VeCostosAsync(usuarioId, ct);
        var existencia = await db.Existencias.AsNoTracking().SingleOrDefaultAsync(e => e.SucursalId == sucursalId && e.VarianteId == varianteId, ct)
            ?? new Existencia { SucursalId = sucursalId, VarianteId = varianteId };
        var (costo, promedio) = conCostos ? await CostoDeAhoraAsync(varianteId, existencia, ct) : (null, false);
        return new ProductoParaAgregarDto(variante.Id, ProductoConMarca(variante), string.IsNullOrWhiteSpace(variante.Descripcion) ? null : variante.Descripcion,
            RegistroEtiquetas.Codigo(variante), variante.UnidadBase, existencia.Cantidad.EnUnidades, costo is { } c ? Centavos(c) : null, promedio, conCostos);
    }

    /// <summary>
    /// Agrega lo que llegó: la existencia, el costo promedio (como una compra), su movimiento del kardex y la bitácora, todo junto.
    /// Con el mismo Id, regresa lo que ya se agregó.
    /// </summary>
    public async Task<AgregadoDto> AgregarAsync(SolicitudAgregarInventario s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        TrastiendaEnElPiloto.RevisarInventario(lecturaDeEleventa);
        var catalogo = new RegistroCatalogo(db);
        // La misma solicitud otra vez (se perdió la respuesta): ya está hecha.
        if (await YaEstaAsync(s.Id, s.UsuarioId, ct) is { } hecha)
            return hecha;
        var conCostos = await catalogo.VeCostosAsync(s.UsuarioId, ct);
        if (s.CostoUnitarioCentavos is not null && !conCostos)
            throw new PermisoRequerido(AccionControlada.VerUtilidad, NivelPermiso.NoPermitido, SinPermisoDelCosto);
        if (s.CostoUnitarioCentavos is < 0 or > EntradaInventario.CostoMaximoCentavos)
            throw new ArgumentException($"Revisa el costo de cada uno: va de $0.00 a {AlertasTelegram.Dinero(EntradaInventario.CostoMaximoCentavos)}.");
        var cantidad = new Cantidad(s.CantidadMilesimas);
        if (!cantidad.EsPositiva)
            throw new ArgumentException("Escribe cuántos llegaron: más de cero.");
        if (cantidad > Cantidad.Piezas(EntradaInventario.CantidadMaxima))
            throw new ArgumentException($"De una vez se agregan hasta {EntradaInventario.CantidadMaxima.ToString("N0", Formato)}: revisa cuántos llegaron.");
        var variante = await VarianteAsync(s.VarianteId, ct);
        var nombre = RegistroCompras.Nombre(variante);
        if (!variante.AdmiteCantidad(cantidad))
            throw new ArgumentException($"«{nombre}» se cuenta en piezas enteras.");

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Otra petición con el mismo Id pudo guardarla mientras esta esperaba su turno.
            if (await YaEstaAsync(s.Id, s.UsuarioId, ct) is { } guardada)
                return guardada;
            var inventario = new RegistroInventario(db);
            var antes = await inventario.ExistenciaAsync(s.SucursalId, variante.Id, ct);
            var cantidadAntes = antes.Cantidad;
            var (costoDeAhora, _) = await CostoDeAhoraAsync(variante.Id, antes, ct);
            var costoUnitario = s.CostoUnitarioCentavos ?? costoDeAhora ?? 0m;
            var ahora = Ahora;
            var entrada = new EntradaInventario
            {
                Id = s.Id, SucursalId = s.SucursalId, VarianteId = variante.Id, Cantidad = cantidad, CostoUnitarioCentavos = Centavos(costoUnitario),
                CostoCentavos = (long)Math.Round(costoUnitario * cantidad.Milesimas / Cantidad.Escala, MidpointRounding.AwayFromZero),
                CostoEscrito = s.CostoUnitarioCentavos is not null, UsuarioId = s.UsuarioId, FechaUtc = ahora,
            };
            // Como una compra: el movimiento lleva lo que vale lo que entra y la existencia lo suma (ahí sale el promedio).
            await inventario.AgregarAsync(Kardex.Entrada(s.SucursalId, variante, TipoMovimiento.AgregarInventario, cantidad, entrada.CostoCentavos, ahora,
                documentoTipo: nameof(EntradaInventario), documentoId: entrada.Id), ct);
            db.EntradasInventario.Add(entrada);
            var despues = await db.Existencias.FindAsync([s.SucursalId, variante.Id], ct)
                ?? throw new InvalidOperationException("La existencia no quedó guardada.");
            new ServicioControl(db).Registrar(s.SucursalId, s.CajaId, s.UsuarioId, AccionControlada.AgregarInventario,
                Detalle(nombre, cantidad, cantidadAntes, despues.Cantidad, costoDeAhora, despues.CostoPromedio), entrada.CostoCentavos);
            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
            return new AgregadoDto(await DtoAsync(entrada, variante, conCostos, ct), despues.Cantidad.EnUnidades, YaEstaba: false);
        }
        catch
        {
            // Lo que se quedó a medias en el contexto no se guarda después por otro camino.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Lo agregado hoy (en la hora de México) en la sucursal, de lo más reciente a lo más viejo; el costo, solo para quien lo ve.</summary>
    public async Task<AgregadoHoyDto> HoyAsync(Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        await ExigirAsync(usuarioId, sucursalId, ct);
        var conCostos = await new RegistroCatalogo(db).VeCostosAsync(usuarioId, ct);
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(Ahora));
        var (desde, hasta) = (HoraMexico.AUtc(hoy.ToDateTime(TimeOnly.MinValue)), HoraMexico.AUtc(hoy.AddDays(1).ToDateTime(TimeOnly.MinValue)));
        var entradas = await db.EntradasInventario.AsNoTracking()
            .Where(e => e.SucursalId == sucursalId && e.FechaUtc >= desde && e.FechaUtc < hasta)
            .OrderByDescending(e => e.FechaUtc).ThenByDescending(e => e.Id).Take(MaximoDelDia).ToListAsync(ct);
        var variantes = await new RegistroEtiquetas(db).VariantesAsync(entradas.Select(e => e.VarianteId).Distinct(), tracking: false, ct);
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        return new AgregadoHoyDto(entradas.Select(e => Dto(e, variantes.GetValueOrDefault(e.VarianteId), usuarios, conCostos)).ToList(), conCostos);
    }

    // ── Apoyo ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Quien agrega tiene «Agregar inventario» en «Puede» (no se autoriza: o puede o no puede). Si no, el error dice quién lo tiene.
    /// </summary>
    private async Task ExigirAsync(Guid usuarioId, Guid sucursalId, CancellationToken ct)
    {
        var control = new ServicioControl(db);
        if ((await control.EvaluarAsync(usuarioId, sucursalId, AccionControlada.AgregarInventario, ct: ct)).Nivel == NivelPermiso.Permitido)
            return;
        throw new PermisoRequerido(AccionControlada.AgregarInventario, NivelPermiso.NoPermitido,
            Permisos.QuienLoHace(AccionControlada.AgregarInventario, await control.QuienesPuedenAsync(sucursalId, AccionControlada.AgregarInventario, ct)));
    }

    /// <summary>La que ya se guardó con ese Id, como se regresa (con la existencia de ahora); vacía si no hay.</summary>
    private async Task<AgregadoDto?> YaEstaAsync(Guid id, Guid usuarioId, CancellationToken ct)
    {
        if (await db.EntradasInventario.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct) is not { } entrada)
            return null;
        // Como esté ahora el producto (aunque ya no lleve inventario): lo agregado ya está, y se regresa igual.
        var variante = (await new RegistroEtiquetas(db).VariantesAsync([entrada.VarianteId], tracking: false, ct)).GetValueOrDefault(entrada.VarianteId);
        var existencia = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == entrada.SucursalId && e.VarianteId == entrada.VarianteId)
            .Select(e => (Cantidad?)e.Cantidad).SingleOrDefaultAsync(ct) ?? Cantidad.Cero;
        return new AgregadoDto(await DtoAsync(entrada, variante, await new RegistroCatalogo(db).VeCostosAsync(usuarioId, ct), ct), existencia.EnUnidades,
            YaEstaba: true);
    }

    /// <summary>
    /// El costo de cada uno de ahora, en centavos por unidad base y sin redondear: el promedio de lo que hay (lo que vale su
    /// existencia entre cuántos son); sin existencia (o sin costo en ella), el último que se conoce: el de su última entrada o el
    /// último de un proveedor (el de su última compra o el que trajo la importación), el más reciente. Vacío si nunca tuvo costo.
    /// </summary>
    private async Task<(decimal? Costo, bool Promedio)> CostoDeAhoraAsync(Guid varianteId, Existencia existencia, CancellationToken ct)
    {
        if (existencia.CostoPromedio is { } promedio && promedio > 0)
            return (promedio, true);
        var deProveedor = await db.ProveedoresVariantes.AsNoTracking().Where(p => p.VarianteId == varianteId && p.UltimoCostoCentavos != null)
            .OrderByDescending(p => p.UltimaCompraUtc).Select(p => new { Costo = p.UltimoCostoCentavos!.Value, p.UltimaCompraUtc }).FirstOrDefaultAsync(ct);
        var deEntrada = await db.EntradasInventario.AsNoTracking().Where(e => e.VarianteId == varianteId)
            .OrderByDescending(e => e.FechaUtc).Select(e => new { Costo = e.CostoUnitarioCentavos, e.FechaUtc }).FirstOrDefaultAsync(ct);
        if (deEntrada is not null && (deProveedor is null || deProveedor.UltimaCompraUtc is not { } compra || deEntrada.FechaUtc >= compra))
            return (deEntrada.Costo, false);
        return (deProveedor?.Costo, false);
    }

    /// <summary>
    /// Lo que queda en la bitácora: quién (la bitácora lo lleva), qué, cuántos y cómo quedó la existencia; y si cambió el costo, de
    /// cuánto a cuánto (el de ahora contra el promedio que dejó, al centavo; sin costo antes, cuenta como $0).
    /// </summary>
    private static string Detalle(string nombre, Cantidad cantidad, Cantidad antes, Cantidad despues, decimal? costoAntes, decimal? costoDespues)
    {
        var detalle = $"Agregó {cantidad} «{nombre}» (había {antes}, quedan {despues})";
        var (de, a) = (costoAntes is { } x ? Centavos(x) : (long?)null, costoDespues is { } y ? Centavos(y) : (long?)null);
        if (a is { } nuevo && nuevo != (de ?? 0))
            detalle += $". Costo: {(de is { } viejo ? AlertasTelegram.Dinero(viejo) : "sin costo")} → {AlertasTelegram.Dinero(nuevo)}";
        return detalle;
    }

    private async Task<EntradaInventarioDto> DtoAsync(EntradaInventario e, Variante? variante, bool conCostos, CancellationToken ct) =>
        Dto(e, variante, await db.Usuarios.AsNoTracking().Where(u => u.Id == e.UsuarioId).ToDictionaryAsync(u => u.Id, u => u.Nombre, ct), conCostos);

    private static EntradaInventarioDto Dto(EntradaInventario e, Variante? v, Dictionary<Guid, string> usuarios, bool conCostos) =>
        new(e.Id, HoraMexico.ALocal(e.FechaUtc), e.VarianteId, v is null ? "?" : RegistroCompras.Nombre(v), v is null ? null : RegistroEtiquetas.Codigo(v),
            v?.UnidadBase ?? UnidadBase.Pieza, e.Cantidad.EnUnidades, usuarios.GetValueOrDefault(e.UsuarioId, "?"), conCostos ? e.CostoUnitarioCentavos : null);

    /// <summary>La variante con su producto, su marca y sus códigos; solo lo que lleva inventario.</summary>
    private async Task<Variante> VarianteAsync(Guid varianteId, CancellationToken ct)
    {
        var variante = (await new RegistroEtiquetas(db).VariantesAsync([varianteId], tracking: false, ct)).GetValueOrDefault(varianteId)
            ?? throw new InvalidOperationException("El producto ya no existe.");
        if (!variante.Producto!.LlevaInventario)
            throw new InvalidOperationException($"«{RegistroCompras.Nombre(variante)}» es un servicio: no lleva inventario.");
        return variante;
    }

    /// <summary>El producto con su marca, sin la variante: «Cuaderno profesional Scribe».</summary>
    private static string ProductoConMarca(Variante v) =>
        string.Join(" ", new[] { v.Producto?.Nombre, v.Producto?.Marca?.Nombre }.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static long Centavos(decimal centavos) => (long)Math.Round(centavos, MidpointRounding.AwayFromZero);

    private static readonly System.Globalization.CultureInfo Formato = System.Globalization.CultureInfo.GetCultureInfo("es-MX");
}
