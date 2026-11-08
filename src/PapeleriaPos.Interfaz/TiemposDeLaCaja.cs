using System.Globalization;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que la caja anota de sus tiempos en su registro del día (<see cref="RegistroDiario"/>, <c>caja-AAAA-MM-DD.log</c>), para el
/// piloto alfa (2026-10-25: «recopilar datos en un entorno real», sin cambiar lo que ve el cajero):
/// <list type="bullet">
/// <item>de cada venta, cuánto pasó del primer artículo al cobro, cuántos renglones llevó, cuánto tardó el cobro y cuánto de eso
/// esperó a eleventa, y con qué folio salió el ticket (el de eleventa o el de la caja) (<see cref="Venta"/>);</item>
/// <item>cada hora y al terminar el turno, cuánto tardaron los escaneos, del Enter a la línea en pantalla: la mitad, el 95 %, el
/// 99 % y el más lento, con cuántos fueron (<see cref="DeLaHora"/>, <see cref="TurnoTerminado"/>). Antes eso solo vivía en
/// memoria (Configuración › Acerca de) y se perdía al cerrar la caja.</item>
/// </list>
/// Nada por cada escaneo ni por cada tecla: una línea por venta y una por hora. Lo usa la ventana de Windows, que tiene el
/// registro; la caja del navegador no.
/// </summary>
public sealed class TiemposDeLaCaja : IDisposable
{
    private readonly RendimientoCaja _rendimiento;
    private readonly RegistroDiario _registro;
    private readonly TimeProvider _reloj;
    private readonly Lock _candado = new();
    private ITimer? _cadaHora;
    private DateTimeOffset _desde;

    /// <summary>El turno en que vende la caja, para su resumen al terminar.</summary>
    private TurnoDeLaCaja? _turno;

    private sealed record TurnoDeLaCaja(Guid Id, string Cajero, DateTime AbiertoLocal);

    public TiemposDeLaCaja(RendimientoCaja rendimiento, RegistroDiario registro, TimeProvider? reloj = null)
    {
        _rendimiento = rendimiento;
        _registro = registro;
        _reloj = reloj ?? TimeProvider.System;
        _desde = _reloj.GetLocalNow();
        // Desde ahora, los escaneos también se juntan por hora y por turno.
        _rendimiento.JuntarPeriodos = true;
    }

    /// <summary>El resumen de cada hora, a la hora en punto (la primera vez, en la que sigue).</summary>
    public void Iniciar()
    {
        var ahora = _reloj.GetLocalNow();
        _desde = ahora;
        var enPunto = new DateTimeOffset(ahora.Year, ahora.Month, ahora.Day, ahora.Hour, 0, 0, ahora.Offset).AddHours(1);
        _cadaHora?.Dispose();
        _cadaHora = _reloj.CreateTimer(_ => DeLaHora(), null, enPunto - ahora, TimeSpan.FromHours(1));
    }

    // ── Cada venta ──

    /// <summary>La línea de una venta recién cobrada (en otro hilo: la venta no espera al disco).</summary>
    /// <param name="renglones">Los del ticket que se cobró.</param>
    /// <param name="desdeElPrimero">Del primer artículo al cobro; vacío si no se sabe (el ticket volvió de la espera, de antes de abrir la caja).</param>
    /// <param name="cobro">Lo que tardó el cobro, desde que se mandó hasta que llegó el ticket (con lo que esperó a eleventa).</param>
    public void Venta(TicketDto ticket, int renglones, TimeSpan? desdeElPrimero, TimeSpan cobro) =>
        _registro.Anotar(LineaDeVenta(ticket, renglones, desdeElPrimero, cobro));

    /// <summary>
    /// «Venta B-000123 · 5 renglones · del primer artículo al cobro 48.2 s · el cobro tardó 0.62 s, 0.41 s esperando a eleventa ·
    /// salió con el folio 4521 de eleventa». Siempre igual, para poder juntarlas después.
    /// </summary>
    public static string LineaDeVenta(TicketDto ticket, int renglones, TimeSpan? desdeElPrimero, TimeSpan cobro)
    {
        var primero = desdeElPrimero is { } tiempo
            ? $"del primer artículo al cobro {Segundos(tiempo, "0.0")}"
            : "del primer artículo al cobro: no se sabe (volvió de la espera)";
        var cobrado = $"el cobro tardó {Segundos(cobro, "0.00")}" + (ticket.EnContingencia ? ", sin conexión"
            : ticket.EsperaEleventaMs is { } espera ? $", {Segundos(TimeSpan.FromMilliseconds(espera), "0.00")} esperando a eleventa" : "");
        var folio = ticket.FolioEleventa is { } deEleventa ? $"salió con el folio {deEleventa} de eleventa" : "salió con el folio de la caja";
        return $"Venta {ticket.Folio} · {renglones} {(renglones == 1 ? "renglón" : "renglones")} · {primero} · {cobrado} · {folio}"
               + (ticket.YaCobrada ? " · ya había quedado cobrada (se mandó otra vez con el mismo número)" : "");
    }

    // ── Los escaneos, por hora y por turno ──

    /// <summary>
    /// El resumen de la hora que termina, si hubo escaneos: «Escaneos de 14:00 a 15:00 · 214 escaneos · la mitad en 9 ms · …». Lo
    /// llama el reloj a cada hora en punto (y al cerrar la caja, lo de la hora que iba).
    /// </summary>
    public void DeLaHora()
    {
        DateTimeOffset desde;
        var hasta = _reloj.GetLocalNow();
        lock (_candado)
            (desde, _desde) = (_desde, hasta);
        var medida = _rendimiento.SacarDeLaHora();
        if (medida.Cuantos > 0)
            _registro.Anotar($"Escaneos de {Hora(desde)} a {Hora(hasta)} · {Resumen(medida)}");
    }

    /// <summary>
    /// La caja vende en este turno (al entrar a la venta). Si antes vendía en otro, ese ya terminó (lo cerró el corte de eleventa,
    /// u otro cajero tomó la caja): su resumen va primero, con lo que se escaneó en él.
    /// </summary>
    public void EnElTurno(Guid turnoId, string cajero, DateTime abiertoLocal)
    {
        TurnoDeLaCaja? antes;
        lock (_candado)
        {
            if (_turno?.Id == turnoId)
                return;
            (antes, _turno) = (_turno, new TurnoDeLaCaja(turnoId, cajero, abiertoLocal));
        }
        if (antes is not null)
            ResumenDelTurno(antes, "ya terminó (la caja pasó a otro turno)");
        else
            // Lo que se haya escaneado antes de saber el turno (no debería haber) no se cuenta en este.
            _rendimiento.SacarDelTurno();
    }

    /// <summary>El turno terminó aquí (el corte Z): su resumen.</summary>
    /// <param name="como">«con su corte Z».</param>
    public void TurnoTerminado(string como)
    {
        TurnoDeLaCaja? turno;
        lock (_candado)
            (turno, _turno) = (_turno, null);
        if (turno is not null)
            ResumenDelTurno(turno, como);
    }

    private void ResumenDelTurno(TurnoDeLaCaja turno, string como)
    {
        var medida = _rendimiento.SacarDelTurno();
        if (medida.Cuantos > 0)
            _registro.Anotar($"Escaneos del turno de {turno.Cajero} (abierto el {turno.AbiertoLocal.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture)}), "
                             + $"{como} · {Resumen(medida)}");
    }

    /// <summary>
    /// Al cerrar la caja: lo de la hora que iba y lo del turno hasta ahora (si se vuelve a abrir en el mismo turno, lo que siga
    /// tiene su propio resumen).
    /// </summary>
    public void Cerrar()
    {
        DeLaHora();
        TurnoDeLaCaja? turno;
        lock (_candado)
            turno = _turno;
        if (turno is not null)
            ResumenDelTurno(turno, "hasta que se cerró la caja (el turno sigue abierto)");
    }

    /// <summary>«214 escaneos · la mitad en 9 ms · el 95 % en 18 ms · el 99 % en 31 ms · el más lento en 120 ms».</summary>
    public static string Resumen(MedidaEscaneos m) =>
        $"{m.Cuantos.ToString("#,##0", CultureInfo.InvariantCulture)} {(m.Cuantos == 1 ? "escaneo" : "escaneos")} · la mitad en {Ms(m.Mitad)} · "
        + $"el 95 % en {Ms(m.P95)} · el 99 % en {Ms(m.P99)} · el más lento en {Ms(m.Maximo)}";

    private static string Ms(double ms) => $"{Math.Round(ms).ToString("#,##0", CultureInfo.InvariantCulture)} ms";

    private static string Segundos(TimeSpan tiempo, string formato) => $"{tiempo.TotalSeconds.ToString(formato, CultureInfo.InvariantCulture)} s";

    private static string Hora(DateTimeOffset cuando) => cuando.ToString("HH:mm", CultureInfo.InvariantCulture);

    public void Dispose() => _cadaHora?.Dispose();
}
