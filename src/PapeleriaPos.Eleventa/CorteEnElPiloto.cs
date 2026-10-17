using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>
/// El corte de la caja 2 en el piloto se hace en eleventa (decisión del dueño, 2026-10-12): eleventa sigue abierto en la
/// caja 2 y las dos venden en el mismo turno de eleventa, y la caja nueva toma ese corte (<see cref="TurnosDelPiloto"/>):
/// adopta el turno que eleventa tiene abierto y, cuando eleventa lo cierra, cierra el suyo como «corte hecho en eleventa».
/// Mientras esta caja manda sus ventas a eleventa (<c>Eleventa:Escribir</c>):
/// <list type="bullet">
/// <item>el servicio no hace el corte Z de sus cajas: antes de que eleventa cierre su turno, la caja avisa que el corte se
/// hace allá;</item>
/// <item>el puente no cierra el turno de eleventa: un cierre en la bandeja (de antes del piloto, o de un corte Z que hizo
/// sin conexión una caja de antes) no se manda, queda por revisar. Ese cierre (<c>EnvioEleventa.CerrarTurnoAsync</c>)
/// termina el turno de eleventa que sigue abierto en la caja 2 y borra sus tickets abiertos, con lo que la cajera tenga
/// escaneado allá;</item>
/// <item>el puente tampoco abre el turno de eleventa (<see cref="CajaEnEleventa.AbreTurno"/>): lo abre eleventa con su
/// fondo y lo cierra su corte. Si eleventa no tiene turno abierto (después de su corte y antes de volver a abrir la caja
/// allá), lo que se manda espera en la bandeja y entra en cuanto lo abra;</item>
/// <item>lo que se hizo antes del corte de eleventa y no alcanzó a llegar ya se contó allá: queda por revisar
/// (<see cref="YaSeContoEnEleventa"/>); lo de después va al turno siguiente;</item>
/// <item>el turno de la caja nueva puede durar días si eleventa no hace su corte: solo se cancela un ticket del mismo día;
/// uno de otro día se devuelve.</item>
/// </list>
/// Un corte Z que hizo la caja sin conexión y llega ya en el piloto ya pasó: se guarda, como lo demás de la contingencia
/// (si se rechazara, el turno que esa caja abrió después y sus ventas no entrarían), y su cierre queda por revisar.
/// La caja dice lo mismo con los mismos textos (PilotoConEleventa, en la interfaz, que no conoce el puente): al intentar
/// el corte, con o sin conexión, al salir, al cerrar la ventana y al cancelar. Solo sirve para convivir con eleventa: se
/// quita al dejarlo.
/// </summary>
public static class CorteEnElPiloto
{
    public const string CorteEnEleventa = "Mientras dure el piloto, el corte de esta caja se hace en eleventa.";

    /// <summary>
    /// El corte del día (2026-10-14) se ve, pero no se cierra: el turno de esta caja no se cierra aquí (lo cierra el corte
    /// de eleventa) y el día lo cierra el corte de eleventa. Cerrarlo aquí no tocaría eleventa, pero no diría nada.
    /// </summary>
    public const string CorteDelDiaEnEleventa =
        "Mientras dure el piloto, el día se cierra con el corte de eleventa: aquí solo se ve lo de esta caja.";

    /// <summary>Por qué un cierre del turno que está en la bandeja queda por revisar.</summary>
    public const string NoSeCierraEleventa =
        "No se manda: mientras dure el piloto, el corte de esta caja se hace en eleventa y la caja nueva no cierra el turno "
        + "de eleventa (borraría lo que esté escaneado allá).";

    /// <summary>Por qué lo que va a eleventa espera en la bandeja: allá no hay turno abierto para esta caja.</summary>
    public const string AbreLaCajaEnEleventa =
        "Abre la caja en eleventa (con su fondo): mientras dure el piloto, el turno de esta caja lo abre y lo cierra "
        + "eleventa. Esto se manda en cuanto haya turno.";

    public const string SoloSeCancelaElMismoDia =
        "Mientras dure el piloto, el corte de esta caja se hace en eleventa: solo se cancela un ticket del mismo día; después, es devolución.";

    /// <summary>
    /// Por qué algo de antes del corte de eleventa no se manda: su efectivo ya estaba en el cajón cuando se contó allá, y
    /// mandarlo después lo pondría en el turno siguiente, que no lo tiene.
    /// </summary>
    public static string YaSeContoEnEleventa(DateTime cortadoLocal) =>
        $"Se hizo antes del corte de eleventa ({cortadoLocal:dd/MM HH:mm}) y no alcanzó a llegar: su efectivo ya se contó allá. "
        + "Si hace falta, anótalo a mano en eleventa.";

    /// <summary>Por qué no se escribe en eleventa con dos turnos abiertos para la caja: no se sabe cuál es (el puente nunca abre otro).</summary>
    public const string DosTurnosAbiertos =
        "En eleventa hay dos turnos abiertos para esta caja: no se sabe en cuál va. Cierra el que sobra en eleventa y anota esto a mano.";

    /// <summary>
    /// Lo que se dice del turno de la caja nueva que cerró el corte de eleventa (la caja toma ese corte, sin conteo aquí):
    /// en la bitácora y en el corte del día.
    /// </summary>
    public static string CorteTomado(TurnoEnEleventa corte) =>
        $"Corte en eleventa: contado {AlertasTelegram.Dinero(corte.ContadoCentavos ?? 0)}"
        + corte.DiferenciaCentavos switch
        {
            < 0 and var falto => $", faltaron {AlertasTelegram.Dinero(-falto)}",
            > 0 and var sobro => $", sobraron {AlertasTelegram.Dinero(sobro)}",
            _ => "",
        };

    /// <summary>
    /// Cancelar es solo en el mismo turno en que se cobró; después, es devolución. En el piloto el turno de la caja nueva
    /// se cierra cuando eleventa hace su corte, y si allá no lo hacen puede durar días: solo se cancela lo cobrado el mismo
    /// día (hora local).
    /// Así no se cancela entero en eleventa un ticket de un turno que allá ya se cortó.
    /// </summary>
    public static bool SeCancela(DateTime pagadaUtc, DateTime ahoraUtc) => HoraMexico.ALocal(pagadaUtc).Date == HoraMexico.ALocal(ahoraUtc).Date;

    /// <summary>
    /// El corte del día en el piloto: a cada turno que cerró el corte de eleventa (<see cref="TurnosDelPiloto"/>) le pone lo
    /// que se contó allá. No va en «Contado» ni en las diferencias del día: es el cajón de las dos cajas, no lo de esta.
    /// </summary>
    public static async Task<CorteDelDiaDto> ConLoDeEleventaAsync(PosDbContext db, CorteDelDiaDto dia, CancellationToken ct = default)
    {
        var ids = dia.Turnos.Select(t => t.Id).ToList();
        var cortes = await db.Set<TurnoEnEleventa>().AsNoTracking().Where(t => ids.Contains(t.TurnoId) && t.TomadoUtc != null).ToListAsync(ct);
        if (cortes.Count == 0)
            return dia;
        return dia with
        {
            Turnos = dia.Turnos.Select(t => cortes.FirstOrDefault(c => c.TurnoId == t.Id) is { } corte ? t with { CorteEnEleventa = CorteTomado(corte) } : t).ToList(),
        };
    }

    /// <summary>La cancelación en el piloto: solo la de un ticket cobrado hoy. Lo demás lo revisa la cancelación de siempre.</summary>
    public static async Task RevisarCancelacionAsync(PosDbContext db, Guid ventaId, CancellationToken ct = default)
    {
        var pagada = await db.Ventas.AsNoTracking().Where(v => v.Id == ventaId).Select(v => v.PagadaUtc).SingleOrDefaultAsync(ct);
        if (pagada is { } cobrada && !SeCancela(cobrada, db.Reloj.GetUtcNow().UtcDateTime))
            throw new InvalidOperationException(SoloSeCancelaElMismoDia);
    }
}
