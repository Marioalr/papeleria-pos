using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>Lo que el servicio sabe de una caja por su latido.</summary>
/// <param name="VistoUtc">La última vez que se supo de ella; vacío si no se ha sabido desde que arrancó el servicio.</param>
/// <param name="PorSubir">Lo que dijo en su último latido que tenía por subir de cuando estuvo sin conexión; vacío si no lo ha dicho (una caja de antes).</param>
/// <param name="ConError">Lo que no pudo subir y se queda en ella para revisarlo.</param>
/// <param name="BandejaUtc">Cuándo dijo <paramref name="PorSubir"/>: si dejó de decirlo (se apagó o perdió la conexión), ya no se sabe.</param>
public sealed record PresenciaDeCaja(DateTime? VistoUtc, int? PorSubir, int? ConError, DateTime? BandejaUtc = null);

/// <summary>
/// El corte del día (entrega 6, docs/propuesta-interfaz.md «Corte», 2026-10-14): todas las cajas y turnos de la
/// sucursal en un día, para quien tiene «ver y cerrar el corte del día». Los turnos del día son los que tocan ese día:
/// los que se abrieron, los que se cerraron (uno olvidado de ayer que se corta hoy entra hoy, con su diferencia) y los que
/// siguen abiertos. Lo vendido, las formas de pago, las salidas, las devoluciones, lo fiado, los abonos y las impresiones
/// son lo de esos turnos que pasó ese día, como en el resumen de Telegram: un turno que viene de otro día cuenta aquí solo
/// lo de hoy (el de la caja 2 en el piloto suma días: si no, «Vendido hoy» diría lo de todo el piloto). Lo esperado, lo
/// contado y la diferencia de un turno son los de todo el turno, como en su corte.
/// <para>
/// «Cerrar el día» se puede cuando no queda ningún turno abierto ni nada por subir de otra caja. Lo que una caja tiene
/// por subir solo se sabe por su latido (<see cref="PresenciaDeCaja"/>), cada 5 segundos: de una caja sin conexión, o que
/// dejó de avisar, no se sabe; el corte lo dice como aviso y no detiene el cierre (si se apagó al terminar el día, no se
/// podría cerrar). El cierre queda en la bitácora (sin tabla nueva) y manda el resumen del día por Telegram, el mismo que
/// sale con el último corte Z: si ya salió con algún corte del día, no se manda dos veces.
/// </para>
/// </summary>
public sealed class RegistroCorteDelDia(PosDbContext db)
{
    /// <summary>
    /// Lo que una caja dijo en su latido que tiene por subir detiene el cierre mientras lo siga diciendo (cada 5 s). Si
    /// dejó de avisar (se apagó justo después, o perdió la conexión), ya no se sabe: solo se avisa.
    /// </summary>
    public static readonly TimeSpan VigenciaBandeja = TimeSpan.FromMinutes(1);

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Hoy en la tienda (hora de México).</summary>
    public DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(Ahora));

    /// <param name="presencias">Lo que se sabe del latido de cada caja (por su ID).</param>
    /// <param name="piloto">El piloto con eleventa: el turno de esta caja no se cierra aquí, así que lo esperado de un turno abierto (que suma días) no se enseña.</param>
    public async Task<CorteDelDiaDto> ArmarAsync(
        Guid sucursalId, DateOnly fecha, IReadOnlyDictionary<Guid, PresenciaDeCaja> presencias, bool piloto = false, CancellationToken ct = default)
    {
        var desde = HoraMexico.AUtc(fecha.ToDateTime(TimeOnly.MinValue));
        var hasta = HoraMexico.AUtc(fecha.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var ahora = Ahora;
        // Los que tocan el día: se abrieron, se cerraron o siguen abiertos en él.
        var turnos = await db.Turnos.AsNoTracking()
            .Where(t => t.SucursalId == sucursalId && t.AbiertoUtc < hasta && (t.CerradoUtc == null || t.CerradoUtc >= desde))
            .OrderBy(t => t.AbiertoUtc).ToListAsync(ct);
        var ids = turnos.Select(t => t.Id).ToList();
        var ventas = await db.Ventas.AsNoTracking().Include(v => v.Lineas).Include(v => v.Cobros).Where(v => ids.Contains(v.TurnoId)).AsSplitQuery().ToListAsync(ct);
        var movimientos = await db.MovimientosCaja.AsNoTracking().Where(m => ids.Contains(m.TurnoId)).OrderBy(m => m.FechaUtc).ToListAsync(ct);
        var devoluciones = await db.Devoluciones.AsNoTracking().Where(d => ids.Contains(d.TurnoId)).ToListAsync(ct);
        var abonos = await db.Abonos.AsNoTracking().Where(a => ids.Contains(a.TurnoId)).ToListAsync(ct);
        var cortes = (await db.Cortes.AsNoTracking().Where(c => c.Tipo == TipoCorte.Z && ids.Contains(c.TurnoId)).ToListAsync(ct))
            .GroupBy(c => c.TurnoId).ToDictionary(g => g.Key, g => g.MaxBy(c => c.FechaUtc)!);
        var cajas = await db.Cajas.AsNoTracking().Where(c => c.SucursalId == sucursalId).ToListAsync(ct);
        var nombreCaja = cajas.ToDictionary(c => c.Id, c => c.Nombre);
        var nombres = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);

        // Lo que pasó ese día (de un turno que viene de otro día, solo lo de hoy); lo esperado se calcula con todo el turno.
        bool DelDia(DateTime fechaUtc) => fechaUtc >= desde && fechaUtc < hasta;
        var vigentesDelDia = ventas.Where(v => v.Estado == EstadoVenta.Pagada && v.PagadaUtc is { } pagada && DelDia(pagada)).ToList();
        var movimientosDelDia = movimientos.Where(m => DelDia(m.FechaUtc)).ToList();
        var devolucionesDelDia = devoluciones.Where(d => DelDia(d.FechaUtc)).ToList();
        var abonosDelDia = abonos.Where(a => DelDia(a.FechaUtc)).ToList();

        var filas = turnos.Select(t =>
        {
            var vigentes = vigentesDelDia.Where(v => v.TurnoId == t.Id).ToList();
            var cobros = vigentes.SelectMany(v => v.Cobros).ToList();
            long? esperado = null, contado = null, diferencia = null;
            var enContingencia = t.AbiertoEnContingencia;
            if (cortes.TryGetValue(t.Id, out var z))
            {
                // Lo que dijo su corte: lo que llegó después de él (sin conexión) no lo cambia.
                (esperado, contado, diferencia) = (z.EsperadoCentavos, z.ContadoCentavos, z.DiferenciaCentavos);
                enContingencia |= z.EnContingencia;
            }
            else if (t.Abierto && !piloto)
            {
                esperado = CalculoCorte.Calcular(t, ventas, movimientos, TipoCorte.X, t.CajeroId, ahora, devoluciones: devoluciones, abonos: abonos)
                    .EsperadoCentavos;
            }
            return new TurnoDelDiaDto(t.Id, t.CajaId, nombreCaja.GetValueOrDefault(t.CajaId, "Caja"), nombres.GetValueOrDefault(t.CajeroId, "—"),
                HoraMexico.ALocal(t.AbiertoUtc), t.CerradoUtc is { } cerrado ? HoraMexico.ALocal(cerrado) : null, vigentes.Count,
                vigentes.Sum(v => v.TotalCentavos), Forma(cobros, FormaPago.Tarjeta), Forma(cobros, FormaPago.Transferencia), esperado, contado, diferencia,
                enContingencia);
        }).ToList();

        var cobrosDelDia = vigentesDelDia.SelectMany(v => v.Cobros).ToList();
        var deClientes = abonosDelDia.Where(a => a.Tipo == TipoAbono.Abono && a.CanceladoUtc == null).ToList();
        var porVerificar = cobrosDelDia.Count(c => c.Forma == FormaPago.Transferencia && c.Verificacion == Verificacion.PorVerificar)
            + deClientes.Count(a => a.Forma == FormaPago.Transferencia && a.Verificacion == Verificacion.PorVerificar);
        // El motivo de una salida se escribe libre: las que dicen lo mismo (sin importar mayúsculas ni espacios) van juntas.
        var salidas = movimientosDelDia.Where(m => m.Tipo == TipoMovimientoCaja.Salida)
            .GroupBy(m => string.Join(' ', m.Motivo.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant())
            .Select(g => new SalidaDelDiaDto(g.First().Motivo.Trim(), g.Count(), g.Sum(m => m.MontoCentavos)))
            .OrderByDescending(s => s.MontoCentavos).ToList();
        var canceladas = devolucionesDelDia.Where(d => d.EsCancelacion).ToList();
        var devueltas = devolucionesDelDia.Where(d => !d.EsCancelacion).ToList();

        var abiertos = await db.Turnos.AsNoTracking().Where(t => t.SucursalId == sucursalId && t.CerradoUtc == null).OrderBy(t => t.AbiertoUtc).ToListAsync(ct);
        var (porQueNo, avisos) = Revisar(abiertos, cajas, presencias, nombres, ahora, AlertasTelegram.Configuracion(db, sucursalId).MinutosSinConexion);

        // El último cierre del día cuenta mientras no haya turno abierto ni se haya abierto otro después: uno nuevo, o uno que
        // subió tarde de una caja que estaba sin conexión (se abrió antes del cierre, pero no se sabía). Hay que cerrar otra vez.
        var cierre = await db.Bitacora.AsNoTracking()
            .Where(b => b.SucursalId == sucursalId && b.Accion == AccionControlada.CorteDelDia && b.Resultado == ResultadoBitacora.Hecho
                && b.FechaUtc >= desde && b.FechaUtc < hasta)
            .OrderByDescending(b => b.FechaUtc).FirstOrDefaultAsync(ct);
        var cerrado = cierre is not null && abiertos.Count == 0 && !turnos.Any(t => t.AbiertoUtc > cierre.FechaUtc)
            ? new CierreDelDiaDto(HoraMexico.ALocal(cierre.FechaUtc), cierre.UsuarioId is { } quien ? nombres.GetValueOrDefault(quien, "—") : "—")
            : null;

        return new CorteDelDiaDto(fecha, filas, vigentesDelDia.Sum(v => v.TotalCentavos), vigentesDelDia.Count, Forma(cobrosDelDia, FormaPago.Efectivo),
            Forma(cobrosDelDia, FormaPago.Tarjeta), Forma(cobrosDelDia, FormaPago.Transferencia), Forma(cobrosDelDia, FormaPago.Credito), porVerificar, salidas,
            movimientosDelDia.Where(m => m.Tipo == TipoMovimientoCaja.Entrada).Sum(m => m.MontoCentavos),
            devueltas.Count, devueltas.Sum(d => d.TotalCentavos), canceladas.Count, canceladas.Sum(d => d.TotalCentavos),
            // Lo que entró por abonos: en efectivo con su redondeo, como en el corte y en el resumen.
            deClientes.Count, deClientes.Sum(a => a.Forma == FormaPago.Efectivo ? a.MontoCentavos + a.RedondeoCentavos : a.MontoCentavos),
            vigentesDelDia.SelectMany(v => v.Lineas).Where(l => l.EsImpresiones).Sum(l => l.ImporteCentavos),
            porQueNo, avisos, piloto, cerrado);
    }

    /// <summary>
    /// Cierra el día: queda en la bitácora (con la suma de las diferencias de corte) y manda el resumen por Telegram, si no
    /// salió ya con algún corte Z del día. Solo con el permiso, sin turnos abiertos ni nada por subir de otra caja. En el
    /// piloto con eleventa no se llama: el corte del día se hace allá (lo detiene el servicio).
    /// </summary>
    /// <param name="cajaId">La caja desde la que se cerró, para la bitácora.</param>
    public async Task<CierreDelDiaDto> CerrarAsync(
        Guid sucursalId, Guid? cajaId, Guid usuarioId, DateOnly fecha, IReadOnlyDictionary<Guid, PresenciaDeCaja> presencias, CancellationToken ct = default)
    {
        var control = new ServicioControl(db);
        await control.ExigirAsync(usuarioId, sucursalId, AccionControlada.CorteDelDia, "El corte del día lo cierra quien tiene ese permiso.", ct);
        var dia = await ArmarAsync(sucursalId, fecha, presencias, ct: ct);
        if (dia.Cierre is { } ya)
            throw new InvalidOperationException($"El día ya se cerró a las {ya.CerradoLocal:HH:mm} ({ya.CerradoPor}).");
        if (dia.PorQueNoSeCierra.Count > 0)
            throw new InvalidOperationException(dia.PorQueNoSeCierra[0]);

        var diferencias = dia.Turnos.Sum(t => t.DiferenciaCentavos ?? 0);
        control.Registrar(sucursalId, cajaId, usuarioId, AccionControlada.CorteDelDia,
            $"Cerró el día {fecha:dd/MM/yyyy}: {dia.Turnos.Count} {(dia.Turnos.Count == 1 ? "turno" : "turnos")}, vendido {dia.VendidoCentavos / 100m:N2}, "
            + $"diferencias de corte {diferencias / 100m:N2}",
            diferencias);
        var ids = dia.Turnos.Select(t => t.Id).ToList();
        var cortesZ = await db.Cortes.AsNoTracking().Where(c => c.Tipo == TipoCorte.Z && ids.Contains(c.TurnoId))
            .OrderBy(c => c.FechaUtc).Select(c => c.Id).ToListAsync(ct);
        var resumen = await AlertasTelegram.ResumenAlCerrarElDiaAsync(db, sucursalId, fecha, cortesZ, ct);
        await db.SaveChangesAsync(ct);
        var nombre = await db.Usuarios.Where(u => u.Id == usuarioId).Select(u => u.Nombre).SingleAsync(ct);
        return new CierreDelDiaDto(HoraMexico.ALocal(Ahora), nombre, resumen);
    }

    /// <summary>
    /// Lo que detiene el cierre (los turnos abiertos, lo que una caja sigue diciendo que tiene por subir) y lo que solo se
    /// avisa (lo que no pudo subir y se queda en su caja para revisarlo; lo que tenía por subir una caja que dejó de avisar;
    /// una caja de la que no se sabe hace rato).
    /// </summary>
    private static (List<string> PorQueNo, List<string> Avisos) Revisar(
        List<Turno> abiertos, List<Caja> cajas, IReadOnlyDictionary<Guid, PresenciaDeCaja> presencias, Dictionary<Guid, string> nombres, DateTime ahora,
        int minutosSinConexion)
    {
        var porQueNo = new List<string>();
        var avisos = new List<string>();
        string NombreCaja(Guid id) => cajas.FirstOrDefault(c => c.Id == id)?.Nombre ?? "Caja";
        string Cuando(DateTime utc)
        {
            var local = HoraMexico.ALocal(utc);
            return local.Date == HoraMexico.ALocal(ahora).Date ? $"las {local:HH:mm}" : $"el {local:dd/MM} a las {local:HH:mm}";
        }
        if (abiertos.Count > 0)
            porQueNo.Add($"Falta cerrar el turno de {string.Join(", ", abiertos.Select(t => $"{nombres.GetValueOrDefault(t.CajeroId, "—")} ({NombreCaja(t.CajaId)})"))}.");
        foreach (var caja in cajas.Where(c => c.Activa).OrderBy(c => c.Serie))
        {
            if (!presencias.TryGetValue(caja.Id, out var p))
                continue;
            if (p.PorSubir is > 0 and var porSubir)
            {
                // Mientras lo siga diciendo en su latido, espera; si dejó de avisar (se apagó justo después de reconectarse),
                // ya no se sabe: detenerlo dejaría el día sin cerrar hasta que esa caja vuelva.
                if (p.BandejaUtc is { } dicho && ahora - dicho <= VigenciaBandeja)
                    porQueNo.Add($"La {caja.Nombre} todavía sube lo que hizo sin conexión ({porSubir} por subir): el cierre espera a que termine.");
                else
                    avisos.Add($"La {caja.Nombre} tenía {porSubir} por subir de cuando estuvo sin conexión y no avisa desde "
                        + $"{Cuando(p.BandejaUtc ?? p.VistoUtc ?? ahora)}: lo suyo sube cuando se conecte, y este corte no lo incluye.");
            }
            if (p.ConError is > 0 and var conError)
                avisos.Add($"La {caja.Nombre} tiene {conError} {(conError == 1 ? "cosa" : "cosas")} de cuando estuvo sin conexión que no "
                    + $"{(conError == 1 ? "pudo" : "pudieron")} subir: se queda en esa caja para revisarlo y este corte no lo incluye.");
            if (caja.TieneEquipo && p.VistoUtc is { } visto && ahora - visto > TimeSpan.FromMinutes(minutosSinConexion))
                avisos.Add($"No se sabe de la {caja.Nombre} desde {Cuando(visto)}: si vendió sin conexión, lo suyo sube cuando se conecte.");
        }
        return (porQueNo, avisos);
    }

    private static long Forma(IEnumerable<Cobro> cobros, FormaPago forma) => cobros.Where(c => c.Forma == forma).Sum(c => c.MontoCentavos);
}
