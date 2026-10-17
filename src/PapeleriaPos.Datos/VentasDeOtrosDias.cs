using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// Las ventas de otros días (paridad con eleventa, «Ventas del día y devoluciones», 2026-10-17): las de un día, de una caja
/// o de todas y de un cajero o de todos, cada una con su folio, la hora, quién cobró, la caja, el total, cómo se pagó y si
/// se canceló o se devolvió algo; o las de un folio, de cualquier día y caja. Solo se leen, un día a la vez.
/// <para>
/// Las de hoy de la caja desde la que se ven las ve cualquiera que entró a ella, como antes. Las de otro día o de otra caja,
/// quien tiene «Ver reportes de ventas» en «Puede» (al migrar, solo el dueño): es lo que vendieron los demás, lo mismo que
/// dirán los reportes (entrega 9). Buscar un folio, cualquiera: es lo que ya hace quien lo escribe en la caja para devolver.
/// </para>
/// </summary>
public sealed partial class VentasDeOtrosDias(PosDbContext db)
{
    /// <summary>Lo más que se manda de un día (la tienda vende de 60 a 120 tickets): las más nuevas.</summary>
    public const int MaximoPorDia = 1000;

    /// <summary>Lo más que se manda al buscar un folio: el número solo puede estar en varias series.</summary>
    public const int MaximoPorFolio = 50;

    /// <summary>Hoy en la tienda (hora de México).</summary>
    public DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(db.Reloj.GetUtcNow().UtcDateTime));

    /// <summary>
    /// Las de un día (vacío: hoy) de una caja (vacía: todas) y de un cajero (vacío: todos), vistas desde
    /// <paramref name="cajaId"/> por <paramref name="usuarioId"/>, quien entró a esa caja.
    /// </summary>
    public async Task<VentasDeUnDiaDto> DelDiaAsync(Guid cajaId, Guid usuarioId, DateOnly? fecha, Guid? deCaja, Guid? cajero, CancellationToken ct = default)
    {
        var caja = await CajaAsync(cajaId, ct);
        var hoy = Hoy;
        var dia = fecha ?? hoy;
        if (dia > hoy)
            throw new ArgumentException("Ese día todavía no llega: elige hoy o uno de antes.");
        var control = new ServicioControl(db);
        // También dice si el usuario sigue activo. Las de hoy de esta caja, como antes, sin permiso.
        var reportes = await control.EvaluarAsync(usuarioId, caja.SucursalId, AccionControlada.VerReportes, ct: ct);
        if ((dia != hoy || deCaja != caja.Id) && reportes.Nivel != NivelPermiso.Permitido)
            throw new PermisoRequerido(AccionControlada.VerReportes, NivelPermiso.NoPermitido,
                SinPermiso(await control.QuienesPuedenAsync(caja.SucursalId, AccionControlada.VerReportes, ct)));

        var desde = HoraMexico.AUtc(dia.ToDateTime(TimeOnly.MinValue));
        var hasta = HoraMexico.AUtc(dia.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var consulta = Cobradas(caja.SucursalId).Where(v => v.PagadaUtc >= desde && v.PagadaUtc < hasta);
        if (deCaja is { } soloCaja)
            consulta = consulta.Where(v => v.CajaId == soloCaja);
        // El filtro ofrece a quienes cobraron ese día en esas cajas, aunque se haya elegido a uno.
        var cajeros = await consulta.Select(v => v.CajeroId).Distinct().ToListAsync(ct);
        if (cajero is { } soloCajero)
            consulta = consulta.Where(v => v.CajeroId == soloCajero);
        var ventas = await consulta.OrderByDescending(v => v.PagadaUtc).ThenByDescending(v => v.Folio).ThenBy(v => v.Id).Take(MaximoPorDia + 1)
            .Include(v => v.Lineas).Include(v => v.Cobros).AsSplitQuery().ToListAsync(ct);
        var hayMas = ventas.Count > MaximoPorDia;
        if (hayMas)
            ventas.RemoveAt(MaximoPorDia);
        if (cajero is { } elegido)
            cajeros.Add(elegido);
        return new VentasDeUnDiaDto(dia, hoy, await FilasAsync(ventas, ct), await CajasAsync(caja.SucursalId, ct), await NombresAsync(cajeros, ct),
            HayMas: hayMas);
    }

    /// <summary>
    /// Las que tienen ese folio, de cualquier día y caja de la sucursal: «A-000123» (también «a123»), o solo el número, que
    /// puede ser de cualquier serie o, en el piloto, el folio de eleventa que salió en el ticket.
    /// </summary>
    public async Task<VentasDeUnDiaDto> PorFolioAsync(Guid cajaId, Guid usuarioId, string folio, CancellationToken ct = default)
    {
        var caja = await CajaAsync(cajaId, ct);
        if (!await db.Usuarios.AnyAsync(u => u.Id == usuarioId && u.Activo, ct))
            throw new InvalidOperationException("El usuario no existe o está inactivo.");
        var texto = folio.Trim().ToUpperInvariant();
        if (FormaDelFolio().Match(texto) is not { Success: true } partes)
            throw new ArgumentException($"«{folio.Trim()}» no es un folio: escríbelo como sale en el ticket, por ejemplo A-000123.");
        var numero = int.Parse(partes.Groups["numero"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var consulta = Cobradas(caja.SucursalId);
        if (partes.Groups["serie"].Success)
        {
            var serie = partes.Groups["serie"].Value;
            consulta = consulta.Where(v => v.Serie == serie && v.Folio == numero);
        }
        else
        {
            var deEleventa = await db.EnviosAEleventa.AsNoTracking()
                .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.FolioEleventa == numero && e.VentaId != null)
                .Select(e => e.VentaId!.Value).ToListAsync(ct);
            consulta = consulta.Where(v => v.Folio == numero || deEleventa.Contains(v.Id));
        }
        var ventas = await consulta.OrderByDescending(v => v.PagadaUtc).ThenBy(v => v.Id).Take(MaximoPorFolio)
            .Include(v => v.Lineas).Include(v => v.Cobros).AsSplitQuery().ToListAsync(ct);
        var hoy = Hoy;
        return new VentasDeUnDiaDto(hoy, hoy, await FilasAsync(ventas, ct), await CajasAsync(caja.SucursalId, ct),
            await NombresAsync(ventas.Select(v => v.CajeroId).ToList(), ct), Folio: texto);
    }

    /// <summary>«A-000123», «A123» o «123» (ya en mayúsculas).</summary>
    [GeneratedRegex(@"^(?:(?<serie>[A-Z]{1,3})-?)?0*(?<numero>[0-9]{1,9})$")]
    private static partial Regex FormaDelFolio();

    /// <summary>Las cobradas (también las que después se cancelaron) de la sucursal.</summary>
    private IQueryable<Venta> Cobradas(Guid sucursalId) =>
        db.Ventas.AsNoTracking().Where(v => v.SucursalId == sucursalId && v.Folio != null && v.PagadaUtc != null
            && (v.Estado == EstadoVenta.Pagada || v.Estado == EstadoVenta.Cancelada));

    private async Task<Nucleo.Turnos.Caja> CajaAsync(Guid cajaId, CancellationToken ct) =>
        await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cajaId, ct) ?? throw new InvalidOperationException("La caja no existe.");

    private async Task<List<VentaDeUnDiaDto>> FilasAsync(List<Venta> ventas, CancellationToken ct)
    {
        var ids = ventas.Select(v => v.Id).ToList();
        var devuelto = (await db.Devoluciones.AsNoTracking().Where(d => ids.Contains(d.VentaId) && !d.EsCancelacion)
                .Select(d => new { d.VentaId, d.TotalCentavos }).ToListAsync(ct))
            .GroupBy(d => d.VentaId).ToDictionary(g => g.Key, g => g.Sum(d => d.TotalCentavos));
        var conVenta = ids.Select(id => (Guid?)id).ToList();
        var folios = (await db.EnviosAEleventa.AsNoTracking()
                .Where(e => e.Tipo == TipoEnvioEleventa.Venta && conVenta.Contains(e.VentaId) && e.FolioEleventa != null)
                .Select(e => new { e.VentaId, e.FolioEleventa }).ToListAsync(ct))
            .GroupBy(e => e.VentaId!.Value).ToDictionary(g => g.Key, g => g.First().FolioEleventa);
        var cajas = await db.Cajas.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Nombre, ct);
        var personas = ventas.Select(v => v.CajeroId).Distinct().ToList();
        var nombres = await db.Usuarios.AsNoTracking().Where(u => personas.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        var ahora = db.Reloj.GetUtcNow().UtcDateTime;
        return ventas.Select(v => new VentaDeUnDiaDto(v.Id, v.FolioTexto!, folios.GetValueOrDefault(v.Id), HoraMexico.ALocal(v.PagadaUtc!.Value),
                v.CajaId, cajas.GetValueOrDefault(v.CajaId, "Caja"), v.CajeroId, nombres.GetValueOrDefault(v.CajeroId, "—"), v.TotalCentavos,
                (int)v.NumeroDeArticulos, Formas(v), v.Estado == EstadoVenta.Cancelada, devuelto.GetValueOrDefault(v.Id),
                v.Lineas.Count > 0 && v.Lineas.All(l => l.CantidadDevuelta >= l.Cantidad),
                // Como la ventana de la devolución: le queda alguno de sus 7 días (cuenta los días completos que pasaron).
                ahora - v.PagadaUtc.Value < TimeSpan.FromDays(Devolucion.DiasParaDevolver)))
            .ToList();
    }

    /// <summary>Cómo se pagó, en el orden de siempre (lo fiado al final).</summary>
    private static List<FormaPago> Formas(Venta v) =>
        v.Cobros.Where(c => c.MontoCentavos > 0).Select(c => c.Forma).Distinct().Order().ToList();

    /// <summary>Las cajas de la sucursal, por su serie.</summary>
    internal static async Task<List<OpcionFiltroDto>> CajasDeAsync(PosDbContext db, Guid sucursalId, CancellationToken ct) =>
        await db.Cajas.AsNoTracking().Where(c => c.SucursalId == sucursalId).OrderBy(c => c.Serie)
            .Select(c => new OpcionFiltroDto(c.Id, c.Nombre)).ToListAsync(ct);

    private Task<List<OpcionFiltroDto>> CajasAsync(Guid sucursalId, CancellationToken ct) => CajasDeAsync(db, sucursalId, ct);

    private Task<List<OpcionFiltroDto>> NombresAsync(List<Guid> usuarios, CancellationToken ct) => NombresDeAsync(db, usuarios, ct);

    /// <summary>Los usuarios, por su nombre (los dados de baja también: sus ventas y sus turnos se quedan).</summary>
    internal static async Task<List<OpcionFiltroDto>> NombresDeAsync(PosDbContext db, List<Guid> usuarios, CancellationToken ct)
    {
        var ids = usuarios.Distinct().ToList();
        return (await db.Usuarios.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new OpcionFiltroDto(u.Id, u.Nombre)).ToListAsync(ct))
            .OrderBy(u => u.Nombre, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>
    /// «Las ventas de otro día o de otra caja las ve quien tiene «Ver reportes de ventas»: Dueño de prueba.», con quienes lo
    /// tienen en la sucursal (si nadie, sin nombres).
    /// </summary>
    public static string SinPermiso(IReadOnlyList<QuienAutoriza> quienes)
    {
        var principio = $"Las ventas de otro día o de otra caja las ve quien tiene «{Permisos.Nombre(AccionControlada.VerReportes)}»";
        return quienes.Count == 0 ? $"{principio}." : $"{principio}: {Permisos.Nombres(quienes)}.";
    }
}
