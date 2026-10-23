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
/// siguen abiertos; y el de una venta cobrada ese día que subió tarde (sin conexión, con la hora en que se cobró) después de
/// que su turno se cerró, en otro día: cuenta lo que vendió este día, sin su corte (revisión del PR #131, 2026-10-22). Lo
/// vendido, las formas de pago, las salidas, lo fiado, los abonos y las impresiones son lo de esos turnos que pasó ese día,
/// como en el resumen de Telegram y en los reportes de ventas (lo vendido, por la hora en que se cobró): un turno que viene
/// de otro día cuenta aquí solo lo de hoy (el de la caja 2 en el piloto suma días: si no, «Vendido hoy» diría lo de todo el
/// piloto). Las devoluciones son todas las de la
/// sucursal ese día, como en el resumen: una hecha en eleventa de un ticket de la caja 2 va en el turno de esa venta, que
/// pudo cerrarse antes (2026-10-17). Lo esperado, lo contado y la diferencia de un turno son los de todo el turno, como en
/// su corte, y van en el día en que se cortó: el día en que se abrió no los repite (2026-10-17).
/// <para>
/// «Cerrar el día» se puede cuando no queda ningún turno abierto ni nada por subir de otra caja. Lo que una caja tiene
/// por subir solo se sabe por su latido (<see cref="PresenciaDeCaja"/>), cada 5 segundos: de una caja sin conexión, o que
/// dejó de avisar, no se sabe; el corte lo dice como aviso y no detiene el cierre (si se apagó al terminar el día, no se
/// podría cerrar). El cierre queda en la bitácora (sin tabla nueva) y manda el resumen del día por Telegram, el mismo que
/// sale con el último corte Z: si ya salió con algún corte del día, no se manda dos veces.
/// </para>
/// <para>
/// Como el de eleventa (2026-10-17, docs/paridad-eleventa.md): se ve el de otro día (solo para verlo e imprimirlo; «Cerrar
/// el día» es el de hoy), lleva lo vendido por departamento (<see cref="VentasPorDepartamentoDto"/>) y, para quien tiene «Ver
/// costos, utilidad y margen», la utilidad del día y la de cada departamento (<see cref="UtilidadDelDiaDto"/>); y, a la hora
/// de cierre de la tienda, se le recuerda cerrarlo a quien lo cierra (<see cref="RecordatorioAsync"/>).
/// </para>
/// </summary>
public sealed class RegistroCorteDelDia(PosDbContext db)
{
    /// <summary>
    /// Lo que una caja dijo en su latido que tiene por subir detiene el cierre mientras lo siga diciendo (cada 5 s). Si
    /// dejó de avisar (se apagó justo después, o perdió la conexión), ya no se sabe: solo se avisa.
    /// </summary>
    public static readonly TimeSpan VigenciaBandeja = TimeSpan.FromMinutes(1);

    /// <summary>Al pedir el corte de un día que todavía no llega.</summary>
    public const string TodaviaNoLlega = "Ese día todavía no llega: el corte del día es el de hoy o el de un día que ya pasó.";

    /// <summary>Al querer cerrar otro día: el de un día que ya pasó solo se ve y se imprime.</summary>
    public const string SoloHoySeCierra = "Solo se cierra el día de hoy: el de un día que ya pasó solo se ve y se imprime.";

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Hoy en la tienda (hora de México).</summary>
    public DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(Ahora));

    /// <param name="fecha">Hoy o un día que ya pasó: el de otro día solo se ve (no dice qué falta para cerrarlo ni lo detiene).</param>
    /// <param name="presencias">Lo que se sabe del latido de cada caja (por su ID).</param>
    /// <param name="piloto">El piloto con eleventa: el turno de esta caja no se cierra aquí, así que lo esperado de un turno abierto (que suma días) no se enseña.</param>
    /// <param name="porDepartamento">Con lo vendido por departamento (lo ve todo el que ve el corte del día, como en eleventa).</param>
    /// <param name="conUtilidad">
    /// Con la utilidad del día y el costo de cada departamento (y lo vendido por departamento): solo para quien tiene «Ver
    /// costos, utilidad y margen».
    /// </param>
    public async Task<CorteDelDiaDto> ArmarAsync(
        Guid sucursalId, DateOnly fecha, IReadOnlyDictionary<Guid, PresenciaDeCaja> presencias, bool piloto = false, bool porDepartamento = false,
        bool conUtilidad = false, CancellationToken ct = default)
    {
        var (desde, hasta) = Rango(fecha);
        var ahora = Ahora;
        var deOtroDia = fecha < Hoy;
        // Los que tocan el día (se abrieron, se cerraron o siguen abiertos en él) y los de lo que se cobró ese día: una venta
        // sin conexión sube con la hora en que se cobró, aunque su turno se haya cerrado antes, en otro día. Así lo vendido del
        // día es lo cobrado ese día en la sucursal, como en los reportes de ventas y en el resumen del día.
        var cobradasEseDia = db.Ventas.Where(v => v.SucursalId == sucursalId && v.Estado == EstadoVenta.Pagada && v.PagadaUtc >= desde && v.PagadaUtc < hasta)
            .Select(v => v.TurnoId);
        var turnos = await db.Turnos.AsNoTracking()
            .Where(t => t.SucursalId == sucursalId
                && ((t.AbiertoUtc < hasta && (t.CerradoUtc == null || t.CerradoUtc >= desde)) || cobradasEseDia.Contains(t.Id)))
            .OrderBy(t => t.AbiertoUtc).ToListAsync(ct);
        var ids = turnos.Select(t => t.Id).ToList();
        var ventas = await db.Ventas.AsNoTracking().Include(v => v.Lineas).Include(v => v.Cobros).Where(v => ids.Contains(v.TurnoId)).AsSplitQuery().ToListAsync(ct);
        // Las canceladas (2026-10-22) no cuentan: eran equivocadas.
        var movimientos = await db.MovimientosCaja.AsNoTracking().Where(m => ids.Contains(m.TurnoId) && m.CanceladoUtc == null).OrderBy(m => m.FechaUtc)
            .ToListAsync(ct);
        // Las de los turnos, para el efectivo que se espera de cada uno (solo lo que salió de su caja).
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
        // Lo devuelto ese día en la sucursal, sea del turno que sea: una devolución que se hizo en eleventa de un ticket de la
        // caja 2 se guarda en el turno de esa venta, que pudo cerrarse antes (el piloto), y aun así es de este día y se resta de
        // lo vendido de este día. No cambia el efectivo de ningún turno: ese sale de las devoluciones de cada uno.
        var devolucionesDelDia = await db.Devoluciones.AsNoTracking()
            .Where(d => d.SucursalId == sucursalId && d.FechaUtc >= desde && d.FechaUtc < hasta).ToListAsync(ct);
        var abonosDelDia = abonos.Where(a => DelDia(a.FechaUtc)).ToList();

        var filas = turnos.Select(t =>
        {
            var vigentes = vigentesDelDia.Where(v => v.TurnoId == t.Id).ToList();
            var cobros = vigentes.SelectMany(v => v.Cobros).ToList();
            long? esperado = null, contado = null, diferencia = null;
            var enContingencia = t.AbiertoEnContingencia;
            // Su corte va en el día en que se hizo: el de un turno que se cortó otro día (después, o antes y aquí está por una
            // venta que subió tarde) no entra en este (su diferencia se contaría dos veces), y aquí se ve como cerrado ese otro día.
            if (cortes.TryGetValue(t.Id, out var z) && z.FechaUtc >= desde && z.FechaUtc < hasta)
            {
                // Lo que dijo su corte: lo que llegó después de él (sin conexión) no lo cambia.
                (esperado, contado, diferencia) = (z.EsperadoCentavos, z.ContadoCentavos, z.DiferenciaCentavos);
                enContingencia |= z.EnContingencia;
            }
            // Lo esperado de un turno abierto es el de ahora: en el de un día que ya pasó no dice nada de ese día.
            else if (t.Abierto && !piloto && !deOtroDia)
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

        // Lo que falta para cerrar y los avisos son de hoy: de un día que ya pasó solo se ve lo que pasó (no se cierra).
        List<string> porQueNo = [], avisos = [];
        bool hayAbiertos;
        if (deOtroDia)
            hayAbiertos = turnos.Any(t => t.Abierto);
        else
        {
            var abiertos = await db.Turnos.AsNoTracking().Where(t => t.SucursalId == sucursalId && t.CerradoUtc == null).OrderBy(t => t.AbiertoUtc).ToListAsync(ct);
            (porQueNo, avisos) = Revisar(abiertos, cajas, presencias, nombres, ahora, AlertasTelegram.Configuracion(db, sucursalId).MinutosSinConexion);
            hayAbiertos = abiertos.Count > 0;
        }

        var cierre = await UltimoCierreAsync(sucursalId, desde, hasta, ct);
        var cerrado = SigueCerrado(cierre, hayAbiertos, turnos.Select(t => t.AbiertoUtc))
            ? new CierreDelDiaDto(HoraMexico.ALocal(cierre!.FechaUtc), cierre.UsuarioId is { } quien ? nombres.GetValueOrDefault(quien, "—") : "—")
            : null;

        VentasPorDepartamentoDto? ventasPorDepartamento = null;
        UtilidadDelDiaDto? utilidad = null;
        if (porDepartamento || conUtilidad)
            (ventasPorDepartamento, utilidad) = await PorDepartamentoAsync(vigentesDelDia, devueltas, conUtilidad, ct);
        return new CorteDelDiaDto(fecha, filas, vigentesDelDia.Sum(v => v.TotalCentavos), vigentesDelDia.Count, Forma(cobrosDelDia, FormaPago.Efectivo),
            Forma(cobrosDelDia, FormaPago.Tarjeta), Forma(cobrosDelDia, FormaPago.Transferencia), Forma(cobrosDelDia, FormaPago.Credito), porVerificar, salidas,
            movimientosDelDia.Where(m => m.Tipo == TipoMovimientoCaja.Entrada).Sum(m => m.MontoCentavos),
            devueltas.Count, devueltas.Sum(d => d.TotalCentavos), canceladas.Count, canceladas.Sum(d => d.TotalCentavos),
            // Lo que entró por abonos: en efectivo con su redondeo, como en el corte y en el resumen.
            deClientes.Count, deClientes.Sum(a => a.Forma == FormaPago.Efectivo ? a.MontoCentavos + a.RedondeoCentavos : a.MontoCentavos),
            vigentesDelDia.SelectMany(v => v.Lineas).Where(l => l.EsImpresiones).Sum(l => l.ImporteCentavos),
            porQueNo, avisos, piloto, cerrado, utilidad, deOtroDia, ventasPorDepartamento);
    }

    /// <summary>
    /// El recordatorio de cerrar el día (2026-10-17, docs/propuesta-interfaz.md «Corte»): desde la hora de cierre de la
    /// tienda (Configuración › Tienda) y hasta que se cierre el día de hoy, a quien tiene «Ver y cerrar el corte del día».
    /// Vacío si no toca: antes de esa hora, ya cerrado, sin el permiso, sin hora de cierre o en el piloto con eleventa (el
    /// día se cierra con el corte de eleventa, no aquí). Lo cerrado se decide como en el corte del día: si se abre otro
    /// turno después de cerrarlo, se recuerda otra vez.
    /// </summary>
    /// <param name="ahoraUtc">La hora de la caja principal.</param>
    public async Task<RecordatorioCierreDto?> RecordatorioAsync(Guid sucursalId, Guid usuarioId, DateTime ahoraUtc, bool piloto = false, CancellationToken ct = default)
    {
        if (piloto)
            return null;
        var horaCierre = (await new RegistroConfiguracion(db).LeerAsync(sucursalId, ct)).HoraCierre;
        // La hora de cierre va después de la de apertura: las 00:00 solo pueden ser una que no se tiene (una caja principal de
        // antes, una configuración que no la trae). Sin hora, no hay recordatorio.
        if (horaCierre == TimeOnly.MinValue)
            return null;
        var local = HoraMexico.ALocal(ahoraUtc);
        if (TimeOnly.FromDateTime(local) < horaCierre)
            return null;
        if (!await new ServicioControl(db).PuedeAsync(usuarioId, AccionControlada.CorteDelDia, ct))
            return null;
        var hoy = DateOnly.FromDateTime(local);
        var (desde, hasta) = Rango(hoy);
        if (await UltimoCierreAsync(sucursalId, desde, hasta, ct) is { } cierre)
        {
            var hayAbiertos = await db.Turnos.AnyAsync(t => t.SucursalId == sucursalId && t.CerradoUtc == null, ct);
            var abiertosDespues = await db.Turnos.AsNoTracking()
                .Where(t => t.SucursalId == sucursalId && t.AbiertoUtc > cierre.FechaUtc && t.AbiertoUtc < hasta).Select(t => t.AbiertoUtc).ToListAsync(ct);
            if (SigueCerrado(cierre, hayAbiertos, abiertosDespues))
                return null;
        }
        return new RecordatorioCierreDto(hoy, horaCierre);
    }

    /// <summary>De la medianoche a la medianoche del día, en hora de México, como UTC.</summary>
    private static (DateTime Desde, DateTime Hasta) Rango(DateOnly fecha) =>
        (HoraMexico.AUtc(fecha.ToDateTime(TimeOnly.MinValue)), HoraMexico.AUtc(fecha.AddDays(1).ToDateTime(TimeOnly.MinValue)));

    private Task<Bitacora?> UltimoCierreAsync(Guid sucursalId, DateTime desde, DateTime hasta, CancellationToken ct) =>
        db.Bitacora.AsNoTracking()
            .Where(b => b.SucursalId == sucursalId && b.Accion == AccionControlada.CorteDelDia && b.Resultado == ResultadoBitacora.Hecho
                && b.FechaUtc >= desde && b.FechaUtc < hasta)
            .OrderByDescending(b => b.FechaUtc).FirstOrDefaultAsync(ct);

    /// <summary>
    /// El último cierre del día cuenta mientras no haya turno abierto ni se haya abierto otro después: uno nuevo, o uno que
    /// subió tarde de una caja que estaba sin conexión (se abrió antes del cierre, pero no se sabía). Hay que cerrar otra vez.
    /// De hoy, cualquier turno abierto de la sucursal; de un día que ya pasó, los de ese día (uno de hoy no lo vuelve a abrir).
    /// </summary>
    /// <param name="aperturas">Cuándo se abrieron los turnos del día (basta con los que se abrieron después del cierre).</param>
    private static bool SigueCerrado(Bitacora? cierre, bool hayAbiertos, IEnumerable<DateTime> aperturas) =>
        cierre is not null && !hayAbiertos && !aperturas.Any(abierto => abierto > cierre.FechaUtc);

    /// <summary>
    /// Lo vendido por departamento (<see cref="VentasPorDepartamentoDto"/>) y, con <paramref name="conUtilidad"/>, su costo y la
    /// utilidad del día (<see cref="UtilidadDelDiaDto"/>): cada renglón vendido ese día suma su importe y su costo (el que
    /// guardó al cobrar), y cada renglón devuelto ese día los resta, en el departamento de su producto. El artículo común y las
    /// impresiones van aparte. Sin <paramref name="conUtilidad"/>, ningún costo sale de aquí. Se cuenta como en los reportes de
    /// ventas (<see cref="VendidoPorDepartamento"/>).
    /// </summary>
    /// <param name="devueltas">Las devoluciones del día (sin las cancelaciones: un ticket cancelado ya no está en lo vendido).</param>
    private async Task<(VentasPorDepartamentoDto PorDepartamento, UtilidadDelDiaDto? Utilidad)> PorDepartamentoAsync(
        IReadOnlyList<Venta> vigentes, IReadOnlyList<Devolucion> devueltas, bool conUtilidad, CancellationToken ct)
    {
        var idsDevueltas = devueltas.Select(d => d.Id).ToList();
        var lineasDevueltas = await db.DevolucionesLineas.AsNoTracking().Where(l => idsDevueltas.Contains(l.DevolucionId)).ToListAsync(ct);
        // Lo devuelto puede ser de un ticket de otro día: sus renglones se leen aparte.
        var idsVendidas = lineasDevueltas.Select(l => l.VentaLineaId).Distinct().ToList();
        var deLoDevuelto = await db.VentasLineas.AsNoTracking().Where(l => idsVendidas.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        var renglones = vigentes.SelectMany(v => v.Lineas).Select(l => RenglonVendido.De(l, l.ImporteCentavos, l.CostoCentavos))
            .Concat(lineasDevueltas.Where(d => deLoDevuelto.ContainsKey(d.VentaLineaId))
                .Select(d => RenglonVendido.De(deLoDevuelto[d.VentaLineaId], -d.ImporteCentavos, -d.CostoCentavos)))
            .ToList();

        var variantes = renglones.Select(r => r.VarianteId).OfType<Guid>().Distinct().ToList();
        var departamentoDe = await db.Variantes.AsNoTracking().Where(v => variantes.Contains(v.Id))
            .Select(v => new { v.Id, v.Producto!.DepartamentoId }).ToDictionaryAsync(v => v.Id, v => v.DepartamentoId, ct);
        var nombres = await db.Departamentos.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Nombre, ct);
        var (porDepartamento, utilidad) = VendidoPorDepartamento.Armar(renglones, departamentoDe, nombres, conUtilidad);
        return (porDepartamento, conUtilidad ? utilidad : null);
    }

    /// <summary>
    /// Cierra el día: queda en la bitácora (con la suma de las diferencias de corte) y manda el resumen por Telegram, si no
    /// salió ya con algún corte Z del día. Solo con el permiso, sin turnos abiertos ni nada por subir de otra caja, y solo el
    /// de hoy: el de un día que ya pasó solo se ve. En el piloto con eleventa no se llama: el corte del día se hace allá (lo
    /// detiene el servicio).
    /// </summary>
    /// <param name="cajaId">La caja desde la que se cerró, para la bitácora.</param>
    public async Task<CierreDelDiaDto> CerrarAsync(
        Guid sucursalId, Guid? cajaId, Guid usuarioId, DateOnly fecha, IReadOnlyDictionary<Guid, PresenciaDeCaja> presencias, CancellationToken ct = default)
    {
        var control = new ServicioControl(db);
        await control.ExigirAsync(usuarioId, sucursalId, AccionControlada.CorteDelDia, "El corte del día lo cierra quien tiene ese permiso.", ct);
        if (fecha != Hoy)
            throw new InvalidOperationException(SoloHoySeCierra);
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
        // Los cortes Z del día: el de un turno que está aquí por una venta que subió tarde se hizo otro día.
        var ids = dia.Turnos.Select(t => t.Id).ToList();
        var (desde, hasta) = Rango(fecha);
        var cortesZ = await db.Cortes.AsNoTracking().Where(c => c.Tipo == TipoCorte.Z && ids.Contains(c.TurnoId) && c.FechaUtc >= desde && c.FechaUtc < hasta)
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
