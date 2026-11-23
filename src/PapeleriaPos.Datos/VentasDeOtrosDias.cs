using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// Las ventas de otros días (paridad con eleventa, «Ventas del día y devoluciones», 2026-10-17): las de un día, de una caja
/// o de todas y de un cajero o de todos, cada una con su folio, la hora, quién cobró, la caja, el total, cómo se pagó y si
/// se canceló o se devolvió algo; o las de un folio, de cualquier día; o una sola, con su detalle. Solo se leen, un día a la vez.
/// <para>
/// Desde el 2026-11-22 (el dueño: «si tienes el permiso puedes ver todo, si no solo los tuyos»): quien tiene «Ver reportes de
/// ventas» en «Puede» (al migrar, solo el dueño) ve las de todos, de cualquier día y caja; los demás, solo las que cobraron
/// ellos, también de cualquier día y caja. Lo revisa aquí el servicio: la caja no lo esconde nada más. Un folio (con su letra
/// o solo el número) sin ese permiso se busca en las suyas: si no, recorriendo los números se verían las de los demás. Para
/// devolver el ticket de otro, el folio se escribe en la venta (la devolución tiene sus reglas).
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
    /// <paramref name="cajaId"/> por <paramref name="usuarioId"/>, quien entró a esa caja. Sin «Ver reportes de ventas», solo las
    /// suyas (pedir las de otro cajero no se deja).
    /// </summary>
    public async Task<VentasDeUnDiaDto> DelDiaAsync(Guid cajaId, Guid usuarioId, DateOnly? fecha, Guid? deCaja, Guid? cajero, CancellationToken ct = default)
    {
        var caja = await CajaAsync(cajaId, ct);
        var hoy = Hoy;
        var dia = fecha ?? hoy;
        if (dia > hoy)
            throw new ArgumentException("Ese día todavía no llega: elige hoy o uno de antes.");
        var todas = await VeTodasAsync(usuarioId, caja.SucursalId, ct);
        if (!todas && cajero is { } otro && otro != usuarioId)
            throw await SinPermisoAsync(caja.SucursalId, ct);

        var desde = HoraMexico.AUtc(dia.ToDateTime(TimeOnly.MinValue));
        var hasta = HoraMexico.AUtc(dia.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var consulta = Cobradas(caja.SucursalId).Where(v => v.PagadaUtc >= desde && v.PagadaUtc < hasta);
        if (!todas)
            consulta = consulta.Where(v => v.CajeroId == usuarioId);
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
        if (!todas)
            cajeros = [usuarioId];
        return new VentasDeUnDiaDto(dia, hoy, await FilasAsync(ventas, ct), await CajasAsync(caja.SucursalId, ct), await NombresAsync(cajeros, ct),
            HayMas: hayMas, SoloLasSuyas: !todas);
    }

    /// <summary>
    /// Las que tienen ese folio, de cualquier día (<see cref="Folios.Leer"/>): «A-000123» (también «a123»), de cualquier caja de la
    /// sucursal; o solo el número («4812», «004812»), que puede ser el sin letra (lo de en línea desde 2026-11-22), el de cualquier serie
    /// o, en el piloto, el folio de eleventa que salió en el ticket; «-004812», solo el sin letra. Sin «Ver reportes de ventas», solo las
    /// que cobró quien busca.
    /// </summary>
    public async Task<VentasDeUnDiaDto> PorFolioAsync(Guid cajaId, Guid usuarioId, string folio, CancellationToken ct = default)
    {
        var caja = await CajaAsync(cajaId, ct);
        var todas = await VeTodasAsync(usuarioId, caja.SucursalId, ct);
        var texto = folio.Trim().ToUpperInvariant();
        if (Folios.Leer(texto) is not { } leido)
            throw new ArgumentException($"«{folio.Trim()}» no es un folio: escríbelo como sale en el ticket, por ejemplo 004812 (o B-000123).");
        var numero = leido.Numero;
        var consulta = Cobradas(caja.SucursalId);
        if (leido.Serie is { } serie)
            consulta = consulta.Where(v => v.Serie == serie && v.Folio == numero);
        else
        {
            var deEleventa = await db.EnviosAEleventa.AsNoTracking()
                .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.FolioEleventa == numero && e.VentaId != null)
                .Select(e => e.VentaId!.Value).ToListAsync(ct);
            consulta = consulta.Where(v => v.Folio == numero || deEleventa.Contains(v.Id));
        }
        if (!todas)
            consulta = consulta.Where(v => v.CajeroId == usuarioId);
        var ventas = await consulta.OrderByDescending(v => v.PagadaUtc).ThenBy(v => v.Id).Take(MaximoPorFolio)
            .Include(v => v.Lineas).Include(v => v.Cobros).AsSplitQuery().ToListAsync(ct);
        var hoy = Hoy;
        return new VentasDeUnDiaDto(hoy, hoy, await FilasAsync(ventas, ct), await CajasAsync(caja.SucursalId, ct),
            await NombresAsync(todas ? ventas.Select(v => v.CajeroId).ToList() : [usuarioId], ct), Folio: texto, SoloLasSuyas: !todas);
    }

    /// <summary>Una venta, con lo que la caja enseña al abrirla en «Ventas del día».</summary>
    /// <param name="Venta">Con sus líneas y sus cobros, para armar su ticket.</param>
    public sealed record UnaVenta(Venta Venta, VentaDeUnDiaDto Fila, IReadOnlyList<DevolucionDeLaVentaDto> Devoluciones);

    /// <summary>
    /// Una venta cobrada de la sucursal, vista desde <paramref name="cajaId"/> por <paramref name="usuarioId"/>: la de otro
    /// cajero, solo con «Ver reportes de ventas» (2026-11-22).
    /// </summary>
    public async Task<UnaVenta> UnaAsync(Guid cajaId, Guid usuarioId, Guid ventaId, CancellationToken ct = default)
    {
        var caja = await CajaAsync(cajaId, ct);
        var todas = await VeTodasAsync(usuarioId, caja.SucursalId, ct);
        var venta = await Cobradas(caja.SucursalId).Where(v => v.Id == ventaId).Include(v => v.Lineas).Include(v => v.Cobros).AsSplitQuery()
            .SingleOrDefaultAsync(ct) ?? throw new InvalidOperationException("Ese ticket no existe.");
        if (!todas && venta.CajeroId != usuarioId)
            throw await SinPermisoAsync(caja.SucursalId, ct);
        var devoluciones = await db.Devoluciones.AsNoTracking().Where(d => d.VentaId == ventaId).OrderBy(d => d.FechaUtc)
            .Select(d => new { d.FechaUtc, d.EsCancelacion, d.TotalCentavos, d.ReembolsoEfectivoCentavos, d.UsuarioId, d.Motivo }).ToListAsync(ct);
        var quienes = devoluciones.Select(d => d.UsuarioId).Distinct().ToList();
        var nombres = await db.Usuarios.AsNoTracking().Where(u => quienes.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        return new UnaVenta(venta, (await FilasAsync([venta], ct))[0], devoluciones
            .Select(d => new DevolucionDeLaVentaDto(HoraMexico.ALocal(d.FechaUtc), d.EsCancelacion, d.TotalCentavos, d.ReembolsoEfectivoCentavos,
                nombres.GetValueOrDefault(d.UsuarioId, "—"), d.Motivo))
            .ToList());
    }

    /// <summary>Con «Ver reportes de ventas» en «Puede», las de todos; si no, las suyas. También dice si el usuario sigue activo.</summary>
    private async Task<bool> VeTodasAsync(Guid usuarioId, Guid sucursalId, CancellationToken ct) =>
        (await new ServicioControl(db).EvaluarAsync(usuarioId, sucursalId, AccionControlada.VerReportes, ct: ct)).Nivel == NivelPermiso.Permitido;

    private async Task<PermisoRequerido> SinPermisoAsync(Guid sucursalId, CancellationToken ct) =>
        new(AccionControlada.VerReportes, NivelPermiso.NoPermitido,
            SinPermiso(await new ServicioControl(db).QuienesPuedenAsync(sucursalId, AccionControlada.VerReportes, ct)));


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
                ahora - v.PagadaUtc.Value < TimeSpan.FromDays(Devolucion.DiasParaDevolver), v.Nota,
                v.TicketSinImprimir && v.Estado == EstadoVenta.Pagada && devuelto.GetValueOrDefault(v.Id) == 0))
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
    /// «Los tickets de otros cajeros los ve quien tiene «Ver reportes de ventas»: Dueño de prueba.», con quienes lo tienen en la
    /// sucursal (si nadie, sin nombres).
    /// </summary>
    public static string SinPermiso(IReadOnlyList<QuienAutoriza> quienes)
    {
        var principio = $"Los tickets de otros cajeros los ve quien tiene «{Permisos.Nombre(AccionControlada.VerReportes)}»";
        return quienes.Count == 0 ? $"{principio}." : $"{principio}: {Permisos.Nombres(quienes)}.";
    }
}
