using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// El conteo cíclico diario (docs/fase-2-inventario.md, entrega 3, parte 2): 15 productos al día, elegidos por valor,
/// rotación y tiempo sin contar, contados a ciegas en la caja. La diferencia entra al kardex como movimiento de conteo,
/// queda en la bitácora y, desde el umbral de ajustes de la sucursal, se le avisa al dueño. En el piloto con eleventa
/// (<paramref name="lecturaDeEleventa"/>) no hay conteo del día: el inventario se lleva en eleventa (<see cref="TrastiendaEnElPiloto"/>).
/// </summary>
public sealed class RegistroConteoCiclico(PosDbContext db, bool lecturaDeEleventa = false)
{
    private const int DiasDeVenta = 90;

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>El conteo de hoy en la sucursal; si todavía no hay, lo arma. Lo que esperaba el sistema, solo para quien ve costos.</summary>
    public async Task<ConteoCiclicoDto> HoyAsync(Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        // En el piloto no se arma ni se pide: la venta no enseña su chip.
        if (lecturaDeEleventa)
            return new ConteoCiclicoDto(Guid.Empty, Hoy(), 0, 0, []);
        var conteo = await HoyOArmarAsync(sucursalId, ct);
        return await DtoAsync(conteo, usuarioId, ct);
    }

    /// <summary>Cuenta un producto del conteo de hoy: la diferencia contra lo que dice el sistema entra al kardex.</summary>
    public async Task<ConteoCiclicoDto> ContarAsync(Guid conteoId, SolicitudContar s, CancellationToken ct = default)
    {
        var conteo = await db.ConteosCiclicos.Include(c => c.Renglones).SingleOrDefaultAsync(c => c.Id == conteoId, ct)
            ?? throw new InvalidOperationException("El conteo ya no existe.");
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, conteo.SucursalId, AccionControlada.ContarInventario, "No tienes permiso para contar.", ct);
        TrastiendaEnElPiloto.RevisarInventario(lecturaDeEleventa);
        if (conteo.Fecha != Hoy())
            throw new InvalidOperationException("Ese conteo era de otro día.");
        var renglon = conteo.Renglones.FirstOrDefault(r => r.VarianteId == s.VarianteId)
            ?? throw new InvalidOperationException("Ese producto no está en el conteo de hoy.");
        var variante = await db.Variantes.Include(v => v.Producto).ThenInclude(p => p!.Marca).SingleAsync(v => v.Id == s.VarianteId, ct);
        var contado = new Cantidad(s.CantidadMilesimas);
        if (contado.Milesimas < 0)
            throw new ArgumentException("Lo contado no puede ser negativo.");
        if (!variante.AdmiteCantidad(contado))
            throw new ArgumentException($"«{RegistroCompras.Nombre(variante)}» se cuenta en piezas enteras.");

        var inventario = new RegistroInventario(db);
        var esperado = (await inventario.ExistenciaAsync(conteo.SucursalId, variante.Id, ct)).Cantidad;
        var diferencia = contado - esperado;
        var costo = await inventario.AgregarDiferenciaDeConteoAsync(conteo.SucursalId, variante, diferencia, Ahora, nameof(ConteoCiclico), conteo.Id, ct);
        renglon.Contar(contado, esperado, costo, s.UsuarioId, Ahora);
        new ServicioControl(db).Registrar(conteo.SucursalId, null, s.UsuarioId, AccionControlada.ContarInventario,
            diferencia.Milesimas == 0
                ? $"Contó «{RegistroCompras.Nombre(variante)}»: {contado}, cuadra"
                : $"Contó «{RegistroCompras.Nombre(variante)}»: {contado}; el sistema tenía {esperado} ({(diferencia.EsPositiva ? "sobró" : "faltó")} {diferencia.Abs()})",
            Math.Abs(costo));
        await db.SaveChangesAsync(ct);
        return await DtoAsync(conteo, s.UsuarioId, ct);
    }

    private DateOnly Hoy() => DateOnly.FromDateTime(HoraMexico.ALocal(Ahora));

    private async Task<ConteoCiclico> HoyOArmarAsync(Guid sucursalId, CancellationToken ct)
    {
        var hoy = Hoy();
        if (await db.ConteosCiclicos.Include(c => c.Renglones).SingleOrDefaultAsync(c => c.SucursalId == sucursalId && c.Fecha == hoy, ct) is { } existente)
            return existente;

        var elegidos = SeleccionConteo.Elegir(await CandidatosAsync(sucursalId, ct));
        var conteo = new ConteoCiclico { SucursalId = sucursalId, Fecha = hoy, CreadoUtc = Ahora };
        conteo.Renglones.AddRange(elegidos.Select((v, i) => new ConteoCiclicoRenglon { ConteoId = conteo.Id, Orden = i + 1, VarianteId = v }));
        db.ConteosCiclicos.Add(conteo);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Otra caja lo armó al mismo tiempo: vale el suyo.
            db.ChangeTracker.Clear();
            return await db.ConteosCiclicos.Include(c => c.Renglones).SingleAsync(c => c.SucursalId == sucursalId && c.Fecha == hoy, ct);
        }
        return conteo;
    }

    /// <summary>Lo que pesa cada producto de inventario activo en la sucursal: su valor, lo vendido en 90 días y cuándo se contó.</summary>
    private async Task<List<SeleccionConteo.Candidato>> CandidatosAsync(Guid sucursalId, CancellationToken ct)
    {
        var ahora = Ahora;
        var desde = ahora.AddDays(-DiasDeVenta);
        var variantes = await db.Variantes.AsNoTracking().Where(v => v.Activa && v.Producto!.Activo && v.Producto.Tipo == TipoProducto.Mercancia)
            .Select(v => v.Id).ToListAsync(ct);
        var existencias = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId)
            .ToDictionaryAsync(e => e.VarianteId, e => (e.Cantidad, e.CostoCentavos), ct);
        var vendido = (await (from l in db.VentasLineas
                              join v in db.Ventas on l.VentaId equals v.Id
                              where v.SucursalId == sucursalId && v.Estado == EstadoVenta.Pagada && v.PagadaUtc >= desde && l.VarianteId != null
                              group l by l.VarianteId into g
                              select new { Variante = g.Key!.Value, Costo = g.Sum(x => x.CostoCentavos) }).ToListAsync(ct))
            .ToDictionary(x => x.Variante, x => x.Costo);
        foreach (var h in await (from l in db.VentasHistoricasLineas
                                 join v in db.VentasHistoricas on l.VentaHistoricaId equals v.Id
                                 where v.SucursalId == sucursalId && !v.Cancelada && v.FechaUtc >= desde
                                 group l by l.VarianteId into g
                                 select new { Variante = g.Key, Costo = g.Sum(x => x.CostoCentavos) }).ToListAsync(ct))
            vendido[h.Variante] = vendido.GetValueOrDefault(h.Variante) + h.Costo;
        var contados = await db.MovimientosInventario.AsNoTracking()
            .Where(m => m.SucursalId == sucursalId && (m.Tipo == TipoMovimiento.Conteo || (m.Tipo == TipoMovimiento.Ajuste && m.DocumentoTipo == nameof(AjusteInventario))))
            .GroupBy(m => m.VarianteId).Select(g => new { Variante = g.Key, Ultimo = g.Max(m => m.FechaUtc) })
            .ToDictionaryAsync(x => x.Variante, x => x.Ultimo, ct);
        // Lo que se contó hoy y cuadró no dejó movimiento: también cuenta como contado.
        foreach (var r in await db.ConteosCiclicos.AsNoTracking().Where(c => c.SucursalId == sucursalId).SelectMany(c => c.Renglones)
                     .Where(r => r.ContadoUtc != null).Select(r => new { r.VarianteId, r.ContadoUtc }).ToListAsync(ct))
            if (!contados.TryGetValue(r.VarianteId, out var antes) || r.ContadoUtc > antes)
                contados[r.VarianteId] = r.ContadoUtc!.Value;
        // Lo del conteo completo de un departamento, igual.
        foreach (var r in await (from r in db.ConteosDepartamentoRenglones
                                 join c in db.ConteosDepartamento on r.ConteoId equals c.Id
                                 where c.SucursalId == sucursalId && c.Estado == EstadoConteoDepartamento.Cerrado
                                 select new { r.VarianteId, c.TerminadoUtc }).ToListAsync(ct))
            if (!contados.TryGetValue(r.VarianteId, out var antes) || r.TerminadoUtc > antes)
                contados[r.VarianteId] = r.TerminadoUtc!.Value;

        return variantes.Select(v =>
            {
                var (cantidad, costo) = existencias.GetValueOrDefault(v);
                return new SeleccionConteo.Candidato(v, costo, vendido.GetValueOrDefault(v),
                    contados.TryGetValue(v, out var ultimo) ? (int)(ahora - ultimo).TotalDays : null, cantidad.Milesimas < 0);
            })
            .ToList();
    }

    private async Task<ConteoCiclicoDto> DtoAsync(ConteoCiclico conteo, Guid usuarioId, CancellationToken ct)
    {
        var veCostos = await new RegistroCatalogo(db).VeCostosAsync(usuarioId, ct);
        var ids = conteo.Renglones.Select(r => r.VarianteId).ToList();
        var variantes = await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca).Include(v => v.Presentaciones)
            .ThenInclude(p => p.Codigos).Where(v => ids.Contains(v.Id)).AsSplitQuery().ToDictionaryAsync(v => v.Id, ct);
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        var renglones = conteo.Renglones.OrderBy(r => r.Orden).Select(r =>
        {
            var v = variantes[r.VarianteId];
            var codigo = v.Presentaciones.OrderByDescending(p => p.EsBase).SelectMany(p => p.Codigos).Select(c => c.Codigo).FirstOrDefault();
            var paquetes = v.Presentaciones.Where(p => !p.EsBase).OrderBy(p => p.Factor).Select(p => $"{p.Nombre} = {p.Factor}").ToList();
            return new RenglonConteoDto(v.Id, RegistroCompras.Nombre(v), codigo, v.UnidadBase, paquetes.Count == 0 ? null : string.Join(" · ", paquetes),
                r.Contado?.EnUnidades, r.ContadoUtc is { } f ? HoraMexico.ALocal(f) : null, r.ContadoPorId is { } u ? usuarios.GetValueOrDefault(u) : null,
                veCostos ? r.Esperado?.EnUnidades : null, veCostos ? r.Diferencia?.EnUnidades : null, veCostos ? r.CostoDiferenciaCentavos : null);
        }).ToList();
        return new ConteoCiclicoDto(conteo.Id, conteo.Fecha, renglones.Count, renglones.Count(r => r.Contado is not null), renglones);
    }
}
