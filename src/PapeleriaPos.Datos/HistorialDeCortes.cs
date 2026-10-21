using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Datos;

/// <summary>
/// El historial de cortes (paridad con eleventa, 2026-10-17): los turnos de un día, de una caja o de todas y de un cajero o
/// de todos, cada uno con su corte del turno: lo esperado, lo contado, la diferencia, quién lo hizo y cuándo. Los turnos del
/// día son los que tocan ese día, como en el corte del día: los que se abrieron, los que se cerraron y los que seguían
/// abiertos. Solo se lee, un día a la vez.
/// <para>
/// Lo ve quien hoy hace o ve los cortes: «Hacer el corte de un turno» (ve lo esperado y la diferencia de cada turno que
/// cierra, en cualquier caja) o «Ver y cerrar el corte del día» (ve los de todas las cajas), en «Puede». Al migrar, el
/// encargado y el dueño. El cajero no: su corte es a ciegas.
/// </para>
/// <para>
/// En el piloto con eleventa el corte de la caja se hace allá: un turno abierto dice «Corte en eleventa», y también uno que
/// se cerró sin corte Z aquí (así lo deja el corte de eleventa cuando la caja lo toma). Solo para convivir con eleventa.
/// </para>
/// </summary>
public sealed class HistorialDeCortes(PosDbContext db)
{
    /// <summary>Hoy en la tienda (hora de México).</summary>
    public DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(db.Reloj.GetUtcNow().UtcDateTime));

    /// <summary>
    /// Los turnos de un día (vacío: hoy) de una caja (vacía: todas) y de un cajero (vacío: todos), con su corte, para quien
    /// entró a <paramref name="cajaId"/>.
    /// </summary>
    /// <param name="piloto">El piloto con eleventa: el corte de un turno abierto se hace allá.</param>
    public async Task<HistorialCortesDto> DelDiaAsync(
        Guid cajaId, Guid usuarioId, DateOnly? fecha, Guid? deCaja, Guid? cajero, bool piloto = false, CancellationToken ct = default)
    {
        var caja = await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cajaId, ct) ?? throw new InvalidOperationException("La caja no existe.");
        await ExigirAsync(usuarioId, caja.SucursalId, ct);
        var hoy = Hoy;
        var dia = fecha ?? hoy;
        if (dia > hoy)
            throw new ArgumentException("Ese día todavía no llega: elige hoy o uno de antes.");

        var desde = HoraMexico.AUtc(dia.ToDateTime(TimeOnly.MinValue));
        var hasta = HoraMexico.AUtc(dia.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var consulta = db.Turnos.AsNoTracking()
            .Where(t => t.SucursalId == caja.SucursalId && t.AbiertoUtc < hasta && (t.CerradoUtc == null || t.CerradoUtc >= desde));
        if (deCaja is { } soloCaja)
            consulta = consulta.Where(t => t.CajaId == soloCaja);
        // El filtro ofrece a los cajeros de los turnos de ese día en esas cajas, aunque se haya elegido a uno.
        var cajeros = await consulta.Select(t => t.CajeroId).Distinct().ToListAsync(ct);
        if (cajero is { } soloCajero)
            consulta = consulta.Where(t => t.CajeroId == soloCajero);
        var turnos = await consulta.OrderBy(t => t.AbiertoUtc).ToListAsync(ct);

        var ids = turnos.Select(t => t.Id).ToList();
        // El corte del turno es el Z (el último, como en el corte del día).
        var cortes = (await db.Cortes.AsNoTracking().Where(c => c.Tipo == TipoCorte.Z && ids.Contains(c.TurnoId)).ToListAsync(ct))
            .GroupBy(c => c.TurnoId).ToDictionary(g => g.Key, g => g.MaxBy(c => c.FechaUtc)!);
        var cajas = await db.Cajas.AsNoTracking().Where(c => c.SucursalId == caja.SucursalId).ToDictionaryAsync(c => c.Id, c => c.Nombre, ct);
        var personas = turnos.Select(t => t.CajeroId).Concat(cortes.Values.Select(c => c.RealizadoPorId)).Distinct().ToList();
        var nombres = await db.Usuarios.AsNoTracking().Where(u => personas.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);

        var filas = turnos.Select(t =>
        {
            var cajeroDelTurno = nombres.GetValueOrDefault(t.CajeroId, "—");
            var corte = cortes.TryGetValue(t.Id, out var z) ? Dto(z, cajeroDelTurno, nombres.GetValueOrDefault(z.RealizadoPorId, "—")) : null;
            // Sin corte aquí: abierto en el piloto (se corta allá), o cerrado sin corte Z. La caja solo cierra un turno con su
            // corte Z; sin él lo cierra el corte de eleventa cuando la caja lo toma (TurnosDelPiloto, en el puente), y eso
            // sigue siendo cierto después del piloto.
            var enEleventa = corte is null && (t.CerradoUtc is not null || piloto);
            return new TurnoConCorteDto(t.Id, t.CajaId, cajas.GetValueOrDefault(t.CajaId, "Caja"), t.CajeroId, cajeroDelTurno, HoraMexico.ALocal(t.AbiertoUtc),
                t.CerradoUtc is { } cerrado ? HoraMexico.ALocal(cerrado) : null, corte, enEleventa);
        }).ToList();
        if (cajero is { } elegido)
            cajeros.Add(elegido);
        return new HistorialCortesDto(dia, hoy, filas, await VentasDeOtrosDias.CajasDeAsync(db, caja.SucursalId, ct),
            await VentasDeOtrosDias.NombresDeAsync(db, cajeros, ct), piloto);
    }

    /// <summary>
    /// Quien entró tiene «Hacer el corte de un turno» o «Ver y cerrar el corte del día» en «Puede»; si no, el error dice
    /// quiénes lo ven. Solo se consulta: no queda en la bitácora, como la bitácora misma.
    /// </summary>
    private async Task ExigirAsync(Guid usuarioId, Guid sucursalId, CancellationToken ct)
    {
        var control = new ServicioControl(db);
        if ((await control.EvaluarAsync(usuarioId, sucursalId, AccionControlada.CorteZ, ct: ct)).Nivel == NivelPermiso.Permitido
            || (await control.EvaluarAsync(usuarioId, sucursalId, AccionControlada.CorteDelDia, ct: ct)).Nivel == NivelPermiso.Permitido)
            return;
        var quienes = Permisos.EnOrden((await control.UsuariosQueEntranAsync(sucursalId, ct)).Where(u =>
                Permisos.Evaluar(u.Permisos, AccionControlada.CorteZ).Nivel == NivelPermiso.Permitido
                || Permisos.Evaluar(u.Permisos, AccionControlada.CorteDelDia).Nivel == NivelPermiso.Permitido))
            .Select(u => new QuienAutoriza(u.Usuario, u.Nombre)).ToList();
        throw new PermisoRequerido(AccionControlada.CorteZ, NivelPermiso.NoPermitido, SinPermiso(quienes));
    }

    /// <summary>
    /// «El historial de cortes lo ve quien tiene «Hacer el corte de un turno» o «Ver y cerrar el corte del día»: Encargado de
    /// prueba o Dueño de prueba.», con quienes lo tienen en la sucursal (si nadie, sin nombres).
    /// </summary>
    public static string SinPermiso(IReadOnlyList<QuienAutoriza> quienes)
    {
        var principio = $"El historial de cortes lo ve quien tiene «{Permisos.Nombre(AccionControlada.CorteZ)}» o «{Permisos.Nombre(AccionControlada.CorteDelDia)}»";
        return quienes.Count == 0 ? $"{principio}." : $"{principio}: {Permisos.Nombres(quienes)}.";
    }

    /// <summary>El corte como lo enseña la caja y como se imprime (lo mismo que al cerrarlo, sin el aviso de Telegram).</summary>
    private static CorteDto Dto(Corte c, string cajero, string realizadoPor) =>
        new(c.Tipo, HoraMexico.ALocal(c.FechaUtc), cajero, realizadoPor, c.Ventas, c.FondoCentavos, c.EfectivoVentasCentavos, c.EntradasCentavos,
            c.SalidasCentavos, c.TarjetaCentavos, c.TransferenciaCentavos, c.EsperadoCentavos, c.ContadoCentavos, c.DiferenciaCentavos,
            c.DevolucionesCentavos, c.Cancelaciones, c.EnContingencia, c.RedondeoCentavos, c.CreditoCentavos, c.Abonos, c.AbonosEfectivoCentavos,
            c.AbonosTarjetaCentavos, c.AbonosTransferenciaCentavos, c.SaldoDevueltoCentavos);
}
