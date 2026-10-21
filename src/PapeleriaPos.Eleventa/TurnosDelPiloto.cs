using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>Lo que cambió en los turnos del piloto con una lectura de eleventa.</summary>
/// <param name="Adoptados">Turnos de la caja nueva que tomaron el turno abierto de eleventa.</param>
/// <param name="Cortes">Turnos de la caja nueva que se cerraron con el corte de eleventa.</param>
public sealed record ResultadoTurnos(int Adoptados, int Cortes, IReadOnlyList<string> Avisos);

/// <summary>
/// Eleventa abierto en la caja 2, en el mismo turno (decisión del dueño, 2026-10-12; docs/migracion-eleventa.md, «Lo que
/// falta para el piloto»). Hay una sola persona en la caja 2 y vende en uno o en el otro:
/// <list type="bullet">
/// <item>el turno de la caja nueva adopta el que eleventa tiene abierto para esa caja y guarda su <c>TURNOS.ID</c>
/// (<see cref="TurnoEnEleventa"/>): al mandar lo primero (<see cref="AdoptarAsync"/>) o en la lectura de cada pocos
/// minutos, si allá hay uno solo. Con dos abiertos no adopta ninguno y avisa. El puente no abre ni cierra el turno de
/// eleventa;</item>
/// <item>la caja toma el corte de eleventa: cuando la lectura ve cerrado el turno adoptado, cierra el de la caja nueva como
/// «corte hecho en eleventa», a la hora de ese corte (o de lo último que pasó en él, si fue después), sin conteo aquí (sin corte Z ni diferencia: el cajón es de las dos y
/// lo esperado de la caja nueva no lo es todo), y guarda lo que se contó allá. Queda en la bitácora. Lo que siga en la
/// bandeja de ese turno lo decide su hora al mandarse: lo de antes del corte ya se contó allá y queda por revisar; lo de
/// después entra al turno siguiente de eleventa (<see cref="CorteEnElPiloto.YaSeContoEnEleventa"/>).</item>
/// </list>
/// Así otro cajero puede abrir su turno en la caja nueva después del corte de eleventa. Solo del piloto: se quita al
/// dejar eleventa.
/// </summary>
public static class TurnosDelPiloto
{
    public const string AvisoDosTurnos = "En eleventa hay dos turnos abiertos para esta caja: la caja nueva no adopta ninguno hasta que quede uno";
    public const string AvisoTurnoNoEsta = "Un turno de eleventa que adoptó esta caja ya no está como estaba (¿se restauró un respaldo?): no se tomó su corte";

    /// <summary>Lo que la lectura le pregunta a eleventa: los turnos adoptados que siguen sin su corte.</summary>
    public static async Task<ConsultaDelPiloto> ConsultaAsync(PosDbContext db, int cajaEleventa, CancellationToken ct = default) =>
        new(cajaEleventa, await db.Set<TurnoEnEleventa>().AsNoTracking().Where(t => t.TomadoUtc == null).Select(t => t.TurnoEleventa).Distinct().ToListAsync(ct));

    /// <summary>
    /// Lo que trajo la lectura: toma el corte de los turnos que eleventa ya cerró y adopta el abierto para los turnos de
    /// aquí que todavía no tienen uno.
    /// </summary>
    public static async Task<ResultadoTurnos> AplicarAsync(PosDbContext db, int cajaEleventa, DatosDelPiloto datos, DateTime ahoraUtc, CancellationToken ct = default)
    {
        var avisos = new List<string>();
        var cortes = 0;
        var pendientes = await db.Set<TurnoEnEleventa>().Where(t => t.TomadoUtc == null).ToListAsync(ct);
        var ids = pendientes.Select(p => p.TurnoId).ToList();
        var turnos = await db.Turnos.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var control = new ServicioControl(db);
        foreach (var adoptado in pendientes)
        {
            if (datos.Turnos.FirstOrDefault(t => t.Id == adoptado.TurnoEleventa) is not { } fila || HoraMexico.AUtc(fila.Inicio) != adoptado.InicioUtc)
            {
                avisos.Add(AvisoTurnoNoEsta);
                continue;
            }
            if (fila.Termino is not { } termino)
                continue;

            // Eleventa hizo su corte: lo que se contó allá y lo que faltó o sobró, en el movimiento que lo anota.
            var cierre = datos.Cierres.FirstOrDefault(c => c.TurnoId == fila.Id);
            adoptado.CortadoUtc = HoraMexico.AUtc(termino);
            adoptado.ContadoCentavos = fila.EfectivoAlCierre is { } contado ? Centavos(contado) : null;
            adoptado.DiferenciaCentavos = cierre is null ? null : cierre.Falto ? -Centavos(cierre.Monto) : Centavos(cierre.Monto);
            adoptado.TomadoUtc = ahoraUtc;
            if (turnos.TryGetValue(adoptado.TurnoId, out var turno) && turno.Abierto)
            {
                // A la hora del corte de eleventa, pero nunca antes de abrirse aquí ni de lo último que pasó en él: si eleventa
                // cortó con la caja 2 apagada y en la mañana se cobró en este turno antes de que la lectura viera el corte, lo de
                // hoy sigue en el corte del día de hoy (revisión del 2026-10-17). La hora del corte de eleventa queda en
                // CortadoUtc.
                turno.CerradoUtc = new[] { adoptado.CortadoUtc.Value, turno.AbiertoUtc, await UltimoMovimientoAsync(db, turno.Id, ct) ?? DateTime.MinValue }.Max();
                var cajero = await db.Usuarios.Where(u => u.Id == turno.CajeroId).Select(u => u.Nombre).FirstOrDefaultAsync(ct) ?? "—";
                control.Registrar(turno.SucursalId, turno.CajaId, null, AccionControlada.CorteZ,
                    $"Corte del turno de {cajero} hecho en eleventa ({termino:dd/MM HH:mm}, turno {fila.Id} de eleventa). {CorteEnElPiloto.CorteTomado(adoptado)}",
                    adoptado.DiferenciaCentavos);
                cortes++;
            }
        }
        await db.SaveChangesAsync(ct);

        // Los turnos abiertos aquí que todavía no tienen su turno de eleventa toman el que eleventa tiene abierto.
        var abiertosAlla = datos.Turnos.Where(t => t.CajaId == cajaEleventa && t.Termino is null).ToList();
        if (abiertosAlla.Count > 1)
            avisos.Add(AvisoDosTurnos);
        var adoptados = 0;
        if (abiertosAlla.Count == 1)
        {
            var sinTurno = await db.Turnos.Where(t => t.CerradoUtc == null && !db.Set<TurnoEnEleventa>().Any(x => x.TurnoId == t.Id))
                .Select(t => t.Id).ToListAsync(ct);
            foreach (var turnoId in sinTurno)
            {
                db.Set<TurnoEnEleventa>().Add(Nuevo(turnoId, abiertosAlla[0].Id, abiertosAlla[0].Inicio, ahoraUtc));
                adoptados++;
            }
            await db.SaveChangesAsync(ct);
        }
        return new ResultadoTurnos(adoptados, cortes, avisos);
    }

    /// <summary>
    /// La bandeja escribió algo de un turno de la caja nueva en un turno abierto de eleventa: si ese turno todavía no tiene
    /// el suyo y sigue abierto, lo adopta. Quien llama guarda.
    /// </summary>
    public static async Task AdoptarAsync(PosDbContext db, Guid turnoId, TurnoDeEleventa deEleventa, DateTime ahoraUtc, CancellationToken ct = default)
    {
        if (db.ChangeTracker.Entries<TurnoEnEleventa>().Any(e => e.Entity.TurnoId == turnoId)
            || await db.Set<TurnoEnEleventa>().AnyAsync(t => t.TurnoId == turnoId, ct)
            || !await db.Turnos.AnyAsync(t => t.Id == turnoId && t.CerradoUtc == null, ct))
            return;
        db.Set<TurnoEnEleventa>().Add(Nuevo(turnoId, deEleventa.Id, deEleventa.InicioLocal, ahoraUtc));
    }

    /// <summary>
    /// Lo último que pasó en un turno de la caja nueva y cuenta en el corte del día: una venta cobrada, una devolución (no
    /// las que se hicieron en eleventa y pasaron a la venta con la lectura), una entrada o salida, o un abono.
    /// </summary>
    private static async Task<DateTime?> UltimoMovimientoAsync(PosDbContext db, Guid turnoId, CancellationToken ct) => new[]
    {
        await db.Ventas.Where(v => v.TurnoId == turnoId && v.PagadaUtc != null).MaxAsync(v => v.PagadaUtc, ct),
        await db.Devoluciones.Where(d => d.TurnoId == turnoId && !db.Set<DevolucionDesdeEleventa>().Any(x => x.DevolucionId == d.Id && !x.Propia))
            .MaxAsync(d => (DateTime?)d.FechaUtc, ct),
        await db.MovimientosCaja.Where(m => m.TurnoId == turnoId).MaxAsync(m => (DateTime?)m.FechaUtc, ct),
        await db.Abonos.Where(a => a.TurnoId == turnoId).MaxAsync(a => (DateTime?)a.FechaUtc, ct),
    }.Max();

    private static TurnoEnEleventa Nuevo(Guid turnoId, int turnoEleventa, DateTime inicioLocal, DateTime ahoraUtc) => new()
    {
        TurnoId = turnoId, TurnoEleventa = turnoEleventa, InicioUtc = HoraMexico.AUtc(inicioLocal), AdoptadoUtc = ahoraUtc,
    };

    private static long Centavos(decimal pesos) => (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero);
}
