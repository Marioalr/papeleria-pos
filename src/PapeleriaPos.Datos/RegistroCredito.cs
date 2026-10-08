using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// La cuenta de los clientes registrados (docs/fase-3-precios-clientes.md, entrega 2): fiar hasta su límite, los
/// abonos, lo devuelto de lo fiado, los ajustes y sus condiciones. El saldo es siempre la suma de sus movimientos
/// (<see cref="MovimientoCredito"/>, de solo agregar) y lo que decide algo se lee dentro de la transacción que lo guarda:
/// dos cajas que fían al mismo cliente a la vez no pasan juntas el límite.
/// </summary>
public sealed class RegistroCredito(PosDbContext db)
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;
    private DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(Ahora));

    // ── La cuenta ────────────────────────────────────────────────────────────

    /// <summary>Lo que debe (la suma de su libro). Dentro de una transacción, lo que ya quedó guardado en ella.</summary>
    private Task<long> SaldoAsync(Guid clienteId, CancellationToken ct) =>
        db.MovimientosCredito.Where(m => m.ClienteId == clienteId).SumAsync(m => m.MontoCentavos, ct);

    public async Task<CreditoClienteDto> CuentaAsync(Guid clienteId, CancellationToken ct = default) =>
        (await CuentasAsync([clienteId], ct)).GetValueOrDefault(clienteId) ?? throw new InvalidOperationException("El cliente ya no existe.");

    /// <summary>Las cuentas de varios clientes de una vez (la lista de clientes, la caja).</summary>
    public async Task<Dictionary<Guid, CreditoClienteDto>> CuentasAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        (await EvaluarAsync(ids, ct)).ToDictionary(c => c.Key, c => c.Value.Cuenta);

    /// <summary>Las cuentas con qué tan puntual paga cada uno (la lista y la ficha de los clientes).</summary>
    public async Task<Dictionary<Guid, (CreditoClienteDto Cuenta, PuntualidadDto Puntualidad)>> EvaluarAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var clientes = ids.ToList();
        var condiciones = await db.ClientesRegistrados.AsNoTracking().Where(c => clientes.Contains(c.Id))
            .Select(c => new { c.Id, c.LimiteCreditoCentavos, c.PlazoCreditoDias }).ToListAsync(ct);
        var movimientos = (await db.MovimientosCredito.AsNoTracking().Where(m => clientes.Contains(m.ClienteId)).ToListAsync(ct)).ToLookup(m => m.ClienteId);
        var porVerificar = (await db.Abonos.AsNoTracking()
                .Where(a => clientes.Contains(a.ClienteId) && a.Forma == FormaPago.Transferencia && a.Verificacion == Verificacion.PorVerificar
                    && a.CanceladoUtc == null)
                .Select(a => new { a.ClienteId, a.MontoCentavos }).ToListAsync(ct))
            .GroupBy(a => a.ClienteId).ToDictionary(g => g.Key, g => g.Sum(a => a.MontoCentavos));
        var hoy = Hoy;
        return condiciones.ToDictionary(c => c.Id, c =>
        {
            var cuenta = CuentaCredito.Aplicar(movimientos[c.Id]);
            return (new CreditoClienteDto(c.Id, cuenta.SaldoCentavos, porVerificar.GetValueOrDefault(c.Id), c.LimiteCreditoCentavos, c.PlazoCreditoDias,
                cuenta.Cargos.Where(p => p.PendienteCentavos > 0).Select(p => new CargoPendienteDto(p.Vence, p.PendienteCentavos)).ToList()),
                CuentaCredito.Puntualidad(movimientos[c.Id], hoy));
        });
    }

    /// <summary>Los de la sucursal que tienen crédito, deben algo o tienen algo por verificar.</summary>
    public async Task<List<CreditoClienteDto>> CuentasDeLaSucursalAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var ids = await db.ClientesRegistrados.AsNoTracking().Where(c => c.SucursalId == sucursalId || c.SucursalId == null).Select(c => c.Id).ToListAsync(ct);
        return (await CuentasAsync(ids, ct)).Values
            .Where(c => c.LimiteCentavos > 0 || c.SaldoCentavos != 0 || c.PorVerificarCentavos > 0).ToList();
    }

    /// <summary>La cuenta con sus últimos movimientos, cada uno con el saldo que dejó.</summary>
    public async Task<CuentaClienteDto> MovimientosAsync(Guid clienteId, int ultimos = 20, CancellationToken ct = default)
    {
        var cuenta = await CuentaAsync(clienteId, ct);
        var movimientos = await db.MovimientosCredito.AsNoTracking().Where(m => m.ClienteId == clienteId).ToListAsync(ct);
        var renglones = Renglones(movimientos, await FoliosAsync(movimientos, ct));
        renglones.Reverse();
        return new CuentaClienteDto(cuenta, renglones.Take(ultimos).ToList());
    }

    /// <summary>Los movimientos del más viejo al más reciente, cada uno con su folio y el saldo que dejó.</summary>
    private static List<MovimientoCreditoDto> Renglones(List<MovimientoCredito> movimientos, Dictionary<Guid, string> folios)
    {
        long saldo = 0;
        return movimientos.OrderBy(m => m.FechaUtc).ThenBy(m => m.Id).Select(m =>
        {
            saldo += m.MontoCentavos;
            return new MovimientoCreditoDto(m.Id, HoraMexico.ALocal(m.FechaUtc), m.Tipo, Concepto(m), Folio(m, folios), m.MontoCentavos, saldo, m.Vence,
                m.EnContingencia);
        }).ToList();
    }

    /// <summary>Los folios de las ventas de esos movimientos ("A-000123").</summary>
    private async Task<Dictionary<Guid, string>> FoliosAsync(List<MovimientoCredito> movimientos, CancellationToken ct)
    {
        var ventas = movimientos.Where(m => m.VentaId is not null).Select(m => m.VentaId!.Value).Distinct().ToList();
        return await db.Ventas.AsNoTracking().Where(v => ventas.Contains(v.Id)).Select(v => new { v.Id, v.Serie, v.Folio })
            .ToDictionaryAsync(v => v.Id, v => $"{v.Serie}-{v.Folio:000000}", ct);
    }

    /// <summary>El del abono (o del saldo devuelto) o el de la venta.</summary>
    private static string? Folio(MovimientoCredito m, Dictionary<Guid, string> folios) => m.Tipo switch
    {
        TipoMovimientoCredito.Abono or TipoMovimientoCredito.AbonoCancelado or TipoMovimientoCredito.AbonoRechazado =>
            m.AbonoId is { } abono ? Abono.FolioDe(abono) : null,
        TipoMovimientoCredito.SaldoDevuelto => m.AbonoId is { } devuelto ? Abono.FolioDe(devuelto, TipoAbono.SaldoDevuelto) : null,
        _ => m.VentaId is { } venta ? folios.GetValueOrDefault(venta) : null,
    };

    /// <summary>
    /// El estado de cuenta: desde una fecha (por omisión, la de la compra más vieja que sigue sin pagarse, y al menos los
    /// últimos 30 días), lo que debía antes, cada movimiento con el saldo que dejó y lo que falta pagar con su vencimiento.
    /// </summary>
    public async Task<EstadoCuentaDto> EstadoDeCuentaAsync(Guid clienteId, DateOnly? desde = null, CancellationToken ct = default)
    {
        var cliente = await db.ClientesRegistrados.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clienteId, ct)
            ?? throw new InvalidOperationException("El cliente ya no existe.");
        // El saldo, lo vencido y lo pendiente salen de la misma lectura que los movimientos: así siempre cuadran.
        var movimientos = await db.MovimientosCredito.AsNoTracking().Where(m => m.ClienteId == clienteId).ToListAsync(ct);
        var porVerificar = await db.Abonos.AsNoTracking()
            .Where(a => a.ClienteId == clienteId && a.Forma == FormaPago.Transferencia && a.Verificacion == Verificacion.PorVerificar && a.CanceladoUtc == null)
            .SumAsync(a => (long?)a.MontoCentavos, ct) ?? 0;
        var aplicada = CuentaCredito.Aplicar(movimientos);
        var hoy = Hoy;
        var pendientes = aplicada.Cargos.Where(c => c.PendienteCentavos > 0).ToList();
        var inicio = desde ?? pendientes.Select(c => DateOnly.FromDateTime(HoraMexico.ALocal(c.FechaUtc))).Append(hoy.AddDays(-DiasDelEstadoDeCuenta)).Min();
        if (inicio > hoy)
            throw new ArgumentException("El estado de cuenta no puede empezar después de hoy.");
        var desdeUtc = HoraMexico.AUtc(inicio.ToDateTime(TimeOnly.MinValue));
        var folios = await FoliosAsync(movimientos, ct);
        var renglones = Renglones(movimientos, folios);
        var porId = movimientos.ToDictionary(m => m.Id);
        return new EstadoCuentaDto(cliente.Id, cliente.Nombre, cliente.Telefono, HoraMexico.ALocal(Ahora), inicio,
            movimientos.Where(m => m.FechaUtc < desdeUtc).Sum(m => m.MontoCentavos),
            renglones.Where(r => porId[r.Id].FechaUtc >= desdeUtc).ToList(), aplicada.SaldoCentavos, aplicada.Vencido(hoy), porVerificar,
            pendientes.Select(c => new PendienteEstadoDto(DateOnly.FromDateTime(HoraMexico.ALocal(c.FechaUtc)), Concepto(porId[c.MovimientoId]),
                Folio(porId[c.MovimientoId], folios), c.MontoCentavos, c.PendienteCentavos, c.Vence)).ToList(),
            cliente.LimiteCreditoCentavos, cliente.PlazoCreditoDias, []);
    }

    /// <summary>Si no debe nada, el estado de cuenta muestra al menos los últimos días.</summary>
    public const int DiasDelEstadoDeCuenta = 30;

    private static string Concepto(MovimientoCredito m) => m.Tipo switch
    {
        TipoMovimientoCredito.Cargo => "Compra a crédito",
        TipoMovimientoCredito.Abono => "Abono",
        TipoMovimientoCredito.Devolucion => "Devolución de lo fiado",
        TipoMovimientoCredito.Cancelacion => "Cancelación de una compra a crédito",
        TipoMovimientoCredito.AbonoCancelado => $"Abono cancelado{(m.Motivo is { } motivo ? $": {motivo}" : "")}",
        TipoMovimientoCredito.AbonoRechazado => "La transferencia del abono no llegó",
        TipoMovimientoCredito.Ajuste => $"Ajuste: {m.Motivo}",
        TipoMovimientoCredito.SaldoDevuelto => "Se le devolvió su saldo a favor",
        _ => m.Tipo.ToString(),
    };

    // ── Condiciones y ajustes (al empezar, solo el dueño) ────────────────────

    /// <summary>El límite (cero, sin crédito) y el plazo. No mueve lo ya fiado: cada cargo guarda cuándo vence.</summary>
    public async Task<ClienteRegistradoDto> GuardarCondicionesAsync(Guid clienteId, SolicitudCreditoCliente s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, s.SucursalId, AccionControlada.DarCredito, "El crédito de un cliente lo da el dueño.", ct);
        if (s.LimiteCentavos is < 0 or > ClienteRegistrado.LimiteMaximoCentavos)
            throw new ArgumentException($"El límite va de $0 (sin crédito) a {Pesos(ClienteRegistrado.LimiteMaximoCentavos)}.");
        if (s.PlazoDias is < 1 or > ClienteRegistrado.PlazoMaximoDias)
            throw new ArgumentException($"El plazo va de 1 a {ClienteRegistrado.PlazoMaximoDias} días.");
        var cliente = await db.ClientesRegistrados.FindAsync([clienteId], ct) ?? throw new InvalidOperationException("El cliente ya no existe.");
        var cambios = new List<string>();
        if (cliente.LimiteCreditoCentavos != s.LimiteCentavos)
            cambios.Add($"crédito {Pesos(cliente.LimiteCreditoCentavos)} → {Pesos(s.LimiteCentavos)}");
        if (cliente.PlazoCreditoDias != s.PlazoDias)
            cambios.Add($"plazo {cliente.PlazoCreditoDias} → {s.PlazoDias} días");
        if (cambios.Count > 0)
        {
            (cliente.LimiteCreditoCentavos, cliente.PlazoCreditoDias) = (s.LimiteCentavos, s.PlazoDias);
            new ServicioControl(db).Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.DarCredito,
                $"Privilegios de «{cliente.Nombre}»: {string.Join(", ", cambios)}");
            await db.SaveChangesAsync(ct);
        }
        return RegistroClientes.Dto(cliente);
    }

    /// <summary>Un ajuste del saldo: el saldo inicial de la libreta o una corrección. No mueve dinero ni entra al corte.</summary>
    public async Task<CuentaClienteDto> AjustarAsync(Guid clienteId, SolicitudAjusteCredito s, CancellationToken ct = default)
    {
        if (await db.MovimientosCredito.AnyAsync(m => m.Id == s.Id, ct))
            return await MovimientosAsync(clienteId, ct: ct);
        if (s.MontoCentavos == 0)
            throw new ArgumentException("Escribe cuánto sube (+) o baja (−) la cuenta.");
        if (string.IsNullOrWhiteSpace(s.Motivo))
            throw new ArgumentException("El ajuste lleva motivo.");
        var motivo = s.Motivo.Trim();
        if (motivo.Length > 200)
            throw new ArgumentException("El motivo va de hasta 200 letras.");
        if (s.Desde is { } desde && desde > Hoy)
            throw new ArgumentException("La fecha del ajuste no puede ser después de hoy.");
        var decision = await new ServicioControl(db).EvaluarAsync(s.UsuarioId, s.SucursalId, AccionControlada.AjustarCredito, Math.Abs(s.MontoCentavos), ct);
        if (decision.Nivel != NivelPermiso.Permitido)
            throw new PermisoRequerido(AccionControlada.AjustarCredito, NivelPermiso.NoPermitido, "El saldo de un cliente lo ajusta el dueño.");
        var cliente = await db.ClientesRegistrados.FindAsync([clienteId], ct) ?? throw new InvalidOperationException("El cliente ya no existe.");

        // El saldo inicial de la libreta lleva la fecha de lo que debía (a mediodía): desde ahí cuenta su plazo. Nunca
        // después de ahora: uno de hoy, hecho en la mañana, quedaría después de lo que pase en el resto del día.
        var ahora = Ahora;
        var fecha = s.Desde is { } dia && HoraMexico.AUtc(dia.ToDateTime(new TimeOnly(12, 0))) is var mediodia && mediodia < ahora ? mediodia : ahora;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Otra petición con el mismo Id pudo guardarlo mientras esta esperaba su turno.
        if (await db.MovimientosCredito.AnyAsync(m => m.Id == s.Id, ct))
            return await MovimientosAsync(clienteId, ct: ct);
        var saldo = await SaldoAsync(cliente.Id, ct);
        db.MovimientosCredito.Add(new MovimientoCredito
        {
            Id = s.Id, ClienteId = cliente.Id, SucursalId = s.SucursalId, UsuarioId = s.UsuarioId, Tipo = TipoMovimientoCredito.Ajuste,
            MontoCentavos = s.MontoCentavos, FechaUtc = fecha, Vence = s.MontoCentavos > 0 ? CuentaCredito.Vence(fecha, cliente.PlazoCreditoDias) : null,
            SaldoCentavos = saldo + s.MontoCentavos, Motivo = motivo,
        });
        new ServicioControl(db).Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.AjustarCredito,
            $"Ajuste del saldo de «{cliente.Nombre}»: {(s.MontoCentavos > 0 ? "+" : "−")}{Pesos(Math.Abs(s.MontoCentavos))} ({motivo})", Math.Abs(s.MontoCentavos));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await MovimientosAsync(cliente.Id, ct: ct);
    }

    // ── Fiar ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Antes de cobrar en línea, dentro de la transacción del cobro: lo fiado cabe en el límite, o lo que pasa lo autoriza
    /// el encargado (la autorización viaja en la venta). Sin crédito, solo se usa lo que tiene a favor. Lo vencido no
    /// bloquea: la caja lo avisa.
    /// </summary>
    internal async Task RevisarFiadoAsync(Venta venta, long creditoCentavos, ServicioControl control, CancellationToken ct)
    {
        var cliente = await db.ClientesRegistrados.FindAsync([venta.ClienteId!.Value], ct) ?? throw new InvalidOperationException("El cliente ya no existe.");
        var cuenta = await CuentaAsync(cliente.Id, ct);
        var exceso = cuenta.Exceso(creditoCentavos);
        if (exceso == 0)
            return;
        if (!cliente.TieneCredito)
            throw new InvalidOperationException(cuenta.DisponibleCentavos > 0
                ? $"«{cliente.Nombre}» no tiene crédito: solo puede usar su saldo a favor ({Pesos(cuenta.DisponibleCentavos)})."
                : cuenta.AFavorCentavos > 0
                    ? $"«{cliente.Nombre}» no tiene crédito y su saldo a favor está por verificar (viene de una transferencia)."
                    : $"«{cliente.Nombre}» no tiene crédito.");
        await control.RequerirAsync(venta.CajeroId, venta.SucursalId, venta.CajaId, AccionControlada.ExcederLimiteCredito, exceso,
            $"Fió {Pesos(creditoCentavos)} a «{cliente.Nombre}» pasando el límite por {Pesos(exceso)} (debía {Pesos(cuenta.SaldoCentavos)}, límite {Pesos(cuenta.LimiteCentavos)})",
            venta.AutorizacionCreditoId, ventaId: venta.Id, ct: ct);
    }

    /// <summary>
    /// Después de <see cref="Venta.Pagar"/>, en la misma transacción: el cargo con lo fiado, cuándo vence y el saldo que
    /// dice el ticket. Nunca rechaza por reglas del crédito: en línea ya se revisó, y lo de sin conexión ya se entregó. Lo
    /// de sin conexión guarda lo que se imprimió (con el saldo que conocía la caja) y, si no cuadró con lo que ya había
    /// llegado (pasó el límite más de lo autorizado, o ya no tenía crédito), queda por revisar.
    /// </summary>
    internal async Task CargarAsync(Venta venta, Turno turno, FiadoSinConexion? sinConexion, CancellationToken ct)
    {
        if (venta.CreditoCentavos == 0)
            return;
        var cliente = await db.ClientesRegistrados.FindAsync([venta.ClienteId!.Value], ct) ?? throw new InvalidOperationException("El cliente ya no existe.");
        var fiado = venta.CreditoCentavos;
        if (!venta.EnContingencia)
        {
            var saldo = await SaldoAsync(cliente.Id, ct);
            db.MovimientosCredito.Add(Cargo(venta, cliente.Id, venta.PagadaUtc!.Value, CuentaCredito.Vence(venta.PagadaUtc.Value, cliente.PlazoCreditoDias),
                saldo + fiado));
            return;
        }

        // Con el saldo real de antes de este cargo (dentro de la transacción del cobro).
        var cuenta = await CuentaAsync(cliente.Id, ct);
        sinConexion ??= new FiadoSinConexion(cuenta.SaldoCentavos, 0);
        var fecha = Acotar(venta.PagadaUtc!.Value, turno.AbiertoUtc);
        // Vence lo que se imprimió: el plazo que usó la caja, desde la fecha. Si el reloj de la caja estaba mal (la fecha se
        // acotó), ese mismo plazo desde la fecha acotada; y si no se entiende, el plazo del cliente.
        var plazoCaja = sinConexion.Vence is { } impreso ? impreso.DayNumber - DateOnly.FromDateTime(HoraMexico.ALocal(venta.PagadaUtc.Value)).DayNumber : 0;
        var vence = CuentaCredito.Vence(fecha, plazoCaja is >= 1 and <= ClienteRegistrado.PlazoMaximoDias ? plazoCaja : cliente.PlazoCreditoDias);
        var cargo = Cargo(venta, cliente.Id, fecha, vence, sinConexion.SaldoConocidoCentavos + fiado);
        db.MovimientosCredito.Add(cargo);

        var exceso = CuentaCredito.Exceso(cuenta.SaldoCentavos, cuenta.PorVerificarCentavos, fiado, cliente.LimiteCreditoCentavos);
        var caja = await db.Cajas.FindAsync([venta.CajaId], ct);
        var que = $"Fiado sin conexión en {caja?.Nombre} ({venta.FolioTexto}) a «{cliente.Nombre}» por {Pesos(fiado)}";
        var debe = cuenta.SaldoCentavos + fiado;
        if (!cliente.Activo)
            await RevisarAsync(cargo, MotivoRevisionCredito.SinCredito, fiado, $"{que}, que ya no estaba activo: ahora debe {Pesos(debe)}.", ct);
        else if (!cliente.TieneCredito && exceso > 0)
            await RevisarAsync(cargo, MotivoRevisionCredito.SinCredito, exceso,
                $"{que}, que ya no tenía crédito: usó {Pesos(exceso)} más de lo que tenía a favor y ahora debe {Pesos(debe)}.", ct);
        else if (exceso > sinConexion.ExcesoAutorizadoCentavos)
            await RevisarAsync(cargo, MotivoRevisionCredito.PasoLimite, exceso - sinConexion.ExcesoAutorizadoCentavos,
                $"{que}: la caja sabía que debía {Pesos(sinConexion.SaldoConocidoCentavos)}; con lo que llegó antes ahora debe {Pesos(debe)} "
                + $"y pasó su límite de {Pesos(cliente.LimiteCreditoCentavos)} por {Pesos(exceso)} (se autorizaron {Pesos(sinConexion.ExcesoAutorizadoCentavos)}).", ct);
    }

    private static MovimientoCredito Cargo(Venta venta, Guid clienteId, DateTime fechaUtc, DateOnly vence, long saldoCentavos) => new()
    {
        Id = venta.Id, ClienteId = clienteId, SucursalId = venta.SucursalId, CajaId = venta.CajaId, TurnoId = venta.TurnoId, UsuarioId = venta.CajeroId,
        Tipo = TipoMovimientoCredito.Cargo, MontoCentavos = venta.CreditoCentavos, FechaUtc = fechaUtc, Vence = vence, VentaId = venta.Id,
        SaldoCentavos = saldoCentavos, EnContingencia = venta.EnContingencia,
    };

    /// <summary>
    /// La hora de lo hecho sin conexión viene del reloj de la caja, y ordena lo pagado y los vencimientos: no puede ser
    /// antes de que abriera su turno ni después de que llegó.
    /// </summary>
    private DateTime Acotar(DateTime fechaUtc, DateTime desdeUtc)
    {
        var ahora = Ahora;
        return fechaUtc < desdeUtc ? desdeUtc : fechaUtc > ahora ? ahora : fechaUtc;
    }

    /// <summary>Lo que no cuadró de lo que subió una caja: queda por revisar y se le avisa al dueño. Quien llama guarda.</summary>
    private async Task RevisarAsync(MovimientoCredito movimiento, MotivoRevisionCredito motivo, long montoCentavos, string detalle, CancellationToken ct)
    {
        if (await db.RevisionesCredito.AnyAsync(r => r.MovimientoId == movimiento.Id && r.Motivo == motivo, ct))
            return;
        var revision = new RevisionCredito
        {
            SucursalId = movimiento.SucursalId, ClienteId = movimiento.ClienteId, MovimientoId = movimiento.Id, Motivo = motivo,
            Detalle = detalle.Length > 300 ? detalle[..300] : detalle, MontoCentavos = montoCentavos, CreadaUtc = Ahora,
        };
        db.RevisionesCredito.Add(revision);
        AlertasTelegram.Avisar(db, movimiento.SucursalId, TipoAlerta.Credito,
            $"{AlertasTelegram.Titulo(TipoAlerta.Credito)}\n{detalle}\nQueda en Clientes → Por revisar.", $"credito-revisar:{revision.Id}");
    }

    /// <summary>Lo que dijo el ticket de la cuenta: sale del cargo, así la reimpresión es igual aunque después haya abonos.</summary>
    public async Task<CreditoTicketDto?> TicketAsync(Guid ventaId, CancellationToken ct = default) =>
        await db.MovimientosCredito.AsNoTracking().Where(m => m.Id == ventaId && m.Tipo == TipoMovimientoCredito.Cargo)
            .Select(m => new CreditoTicketDto(m.MontoCentavos, m.SaldoCentavos, m.Vence, m.EnContingencia)).SingleOrDefaultAsync(ct);

    /// <summary>
    /// Lo devuelto de una venta fiada que baja la cuenta, en la misma transacción que la devolución. Si ya lo había pagado,
    /// le queda a favor: no sale efectivo por la parte fiada.
    /// </summary>
    internal async Task RevertirAsync(Venta venta, Devolucion devolucion, CancellationToken ct)
    {
        if (devolucion.AlCreditoCentavos == 0 || venta.ClienteId is not { } clienteId)
            return;
        var saldo = await SaldoAsync(clienteId, ct);
        db.MovimientosCredito.Add(new MovimientoCredito
        {
            Id = devolucion.Id, ClienteId = clienteId, SucursalId = venta.SucursalId, CajaId = devolucion.CajaId, TurnoId = devolucion.TurnoId,
            UsuarioId = devolucion.UsuarioId, Tipo = devolucion.EsCancelacion ? TipoMovimientoCredito.Cancelacion : TipoMovimientoCredito.Devolucion,
            MontoCentavos = -devolucion.AlCreditoCentavos, FechaUtc = devolucion.FechaUtc, VentaId = venta.Id, DevolucionId = devolucion.Id,
            SaldoCentavos = saldo - devolucion.AlCreditoCentavos,
        });
    }

    /// <summary>El saldo del cliente después de una devolución (para el comprobante).</summary>
    public async Task<long?> SaldoDespuesDeAsync(Guid devolucionId, CancellationToken ct = default) =>
        await db.MovimientosCredito.AsNoTracking().Where(m => m.Id == devolucionId).Select(m => (long?)m.SaldoCentavos).SingleOrDefaultAsync(ct);

    // ── Abonos ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Un abono en cualquier caja con su turno abierto; el dinero entra a ese turno. Se calcula con el saldo de dentro de
    /// la transacción. Con el mismo Id, regresa el que ya estaba.
    /// </summary>
    public async Task<Abono> AbonarAsync(SolicitudAbono s, CancellationToken ct = default)
    {
        if (await db.Abonos.FindAsync([s.Id], ct) is { } yaEsta)
            return yaEsta;
        var turno = await db.Turnos.FindAsync([s.TurnoId], ct) ?? throw new InvalidOperationException("El turno no existe.");
        if (!turno.Abierto || turno.CajaId != s.CajaId)
            throw new InvalidOperationException("El abono va en el turno abierto de esta caja.");
        var cliente = await db.ClientesRegistrados.FindAsync([s.ClienteId], ct) ?? throw new InvalidOperationException("El cliente ya no existe.");
        var referencia = string.IsNullOrWhiteSpace(s.Referencia) ? null : s.Referencia.Trim();
        if (referencia is { Length: > 60 })
            throw new ArgumentException("La referencia va de hasta 60 letras.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Otra petición con el mismo Id pudo guardarlo mientras esta esperaba su turno.
            if (await db.Abonos.AsNoTracking().SingleOrDefaultAsync(a => a.Id == s.Id, ct) is { } guardado)
                return guardado;
            var movimientos = await db.MovimientosCredito.AsNoTracking().Where(m => m.ClienteId == cliente.Id).ToListAsync(ct);
            var saldo = movimientos.Sum(m => m.MontoCentavos);
            // Lo que la caja cobró lo calculó con el saldo que vio: si otra caja le fió o le abonó mientras tanto, no cuadra.
            if (s.SaldoVistoCentavos is { } visto && visto != saldo)
                throw new InvalidOperationException($"La cuenta cambió mientras tanto: ahora debe {Pesos(saldo)}. Revísalo y abona otra vez.");
            var calculo = CalculoAbono.Calcular(saldo, s.Forma, s.MontoCentavos, s.RecibidoCentavos);
            var ahora = Ahora;
            var abono = new Abono
            {
                Id = s.Id, SucursalId = turno.SucursalId, CajaId = s.CajaId, TurnoId = turno.Id, ClienteId = cliente.Id, UsuarioId = s.UsuarioId, Forma = s.Forma,
                MontoCentavos = calculo.MontoCentavos, RedondeoCentavos = calculo.RedondeoCentavos,
                RecibidoCentavos = s.Forma == FormaPago.Efectivo ? s.RecibidoCentavos : calculo.ACobrarCentavos, CambioCentavos = calculo.CambioCentavos,
                Referencia = referencia, Verificacion = s.Forma == FormaPago.Transferencia ? Verificacion.PorVerificar : Verificacion.NoAplica,
                SaldoAnteriorCentavos = saldo, SaldoCentavos = saldo - calculo.MontoCentavos, FechaUtc = ahora,
            };
            var movimiento = new MovimientoCredito
            {
                Id = abono.Id, ClienteId = cliente.Id, SucursalId = abono.SucursalId, CajaId = abono.CajaId, TurnoId = abono.TurnoId, UsuarioId = abono.UsuarioId,
                Tipo = TipoMovimientoCredito.Abono, MontoCentavos = -abono.MontoCentavos, FechaUtc = ahora, AbonoId = abono.Id, SaldoCentavos = abono.SaldoCentavos,
            };
            // Lo que sigue por pagar y cuándo vence, como quedó con este abono: así el comprobante se reimprime igual.
            abono.Vence = abono.SaldoCentavos > 0 ? CuentaCredito.Aplicar(movimientos.Append(movimiento)).VenceMasAntiguo : null;
            db.Abonos.Add(abono);
            db.MovimientosCredito.Add(movimiento);
            if (abono.Forma == FormaPago.Efectivo)
                db.AperturasCajon.Add(new AperturaCajon
                {
                    CajaId = abono.CajaId, TurnoId = abono.TurnoId, UsuarioId = abono.UsuarioId, Motivo = MotivoApertura.Abono,
                    Nota = $"Abono {abono.Folio} de «{cliente.Nombre}»", FechaUtc = ahora,
                });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return abono;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Cancelar un abono: solo en el turno en que se recibió (sigue abierto) y con la regla «Cancelar un abono». Vuelve a
    /// deberse; si fue en efectivo, sale de la caja. Una transferencia ya verificada se corrige con un ajuste.
    /// </summary>
    public async Task<Abono> CancelarAbonoAsync(Guid abonoId, SolicitudCancelacion s, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(s.Motivo))
            throw new ArgumentException("La cancelación lleva motivo.");

        // Lo que decide se lee dentro de la transacción: una cancelación y una verificación a la vez no pasan las dos.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var abono = await db.Abonos.FindAsync([abonoId], ct) ?? throw new InvalidOperationException("El abono no existe.");
            await db.Entry(abono).ReloadAsync(ct);
            if (abono.Tipo == TipoAbono.SaldoDevuelto)
                throw new InvalidOperationException(
                    "Lo que se devolvió de un saldo a favor no se cancela: si fue un error, el efectivo regresa como entrada (F7) y el dueño ajusta su saldo.");
            if (abono.CanceladoUtc is not null)
                throw new InvalidOperationException("Ese abono ya se canceló.");
            if (abono.Verificacion == Verificacion.Verificada)
                throw new InvalidOperationException("Esa transferencia ya se verificó: se corrige con un ajuste del saldo.");
            if (abono.Verificacion == Verificacion.Rechazada)
                throw new InvalidOperationException("Esa transferencia ya se rechazó: ya no cuenta.");
            var turno = await db.Turnos.FindAsync([abono.TurnoId], ct);
            if (turno is not { Abierto: true } || turno.CajaId != s.CajaId)
                throw new InvalidOperationException("Un abono se cancela en su caja y en su mismo turno.");
            var cliente = await db.ClientesRegistrados.FindAsync([abono.ClienteId], ct);
            var control = new ServicioControl(db);
            await control.RequerirAsync(s.UsuarioId, abono.SucursalId, s.CajaId, AccionControlada.CancelarAbono, abono.MontoCentavos,
                $"Canceló el abono {abono.Folio} de «{cliente?.Nombre}»", s.AutorizacionId, s.Motivo.Trim(), ct: ct);
            var ahora = Ahora;
            (abono.CanceladoUtc, abono.CanceladoPorId, abono.MotivoCancelacion) = (ahora, s.UsuarioId, s.Motivo.Trim());
            abono.AutorizoCancelacionId = s.AutorizacionId is { } a ? (await db.Autorizaciones.FindAsync([a], ct))?.AutorizadorId : null;
            var saldo = await SaldoAsync(abono.ClienteId, ct);
            db.MovimientosCredito.Add(new MovimientoCredito
            {
                Id = Ids.Nuevo(), ClienteId = abono.ClienteId, SucursalId = abono.SucursalId, CajaId = abono.CajaId, TurnoId = abono.TurnoId,
                UsuarioId = s.UsuarioId, Tipo = TipoMovimientoCredito.AbonoCancelado, MontoCentavos = abono.MontoCentavos, FechaUtc = ahora, AbonoId = abono.Id,
                SaldoCentavos = saldo + abono.MontoCentavos, Motivo = abono.MotivoCancelacion,
            });
            if (abono.Forma == FormaPago.Efectivo)
                db.AperturasCajon.Add(new AperturaCajon
                {
                    CajaId = abono.CajaId, TurnoId = abono.TurnoId, UsuarioId = s.UsuarioId, Motivo = MotivoApertura.Abono,
                    Nota = $"Cancelación del abono {abono.Folio}", FechaUtc = ahora,
                });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return abono;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>El encargado confirma que la transferencia de un abono llegó, o la rechaza: entonces vuelve a deberse.</summary>
    public async Task<Abono> VerificarAbonoAsync(Guid abonoId, string usuario, string pin, bool llego, CancellationToken ct = default)
    {
        var abono = await db.Abonos.FindAsync([abonoId], ct) ?? throw new InvalidOperationException("El abono no existe.");
        if (abono.Forma != FormaPago.Transferencia)
            throw new InvalidOperationException("Solo se verifican transferencias.");
        if (abono.Verificacion != Verificacion.PorVerificar || abono.CanceladoUtc is not null)
            throw new InvalidOperationException("Esa transferencia ya se revisó.");
        var control = new ServicioControl(db);
        var encargado = await control.IdentificarAsync(usuario, pin, abono.SucursalId, AccionControlada.VerificarTransferencia, ct);
        var cliente = await db.ClientesRegistrados.FindAsync([abono.ClienteId], ct);

        // Se vuelve a revisar dentro de la transacción: otra caja pudo cancelarlo o revisarlo mientras tanto.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Entry(abono).ReloadAsync(ct);
        if (abono.Verificacion != Verificacion.PorVerificar || abono.CanceladoUtc is not null)
            throw new InvalidOperationException("Esa transferencia ya se revisó.");
        var ahora = Ahora;
        (abono.Verificacion, abono.VerificadoPorId, abono.VerificadoUtc) = (llego ? Verificacion.Verificada : Verificacion.Rechazada, encargado.Id, ahora);
        if (!llego)
        {
            var saldo = await SaldoAsync(abono.ClienteId, ct);
            db.MovimientosCredito.Add(new MovimientoCredito
            {
                Id = Ids.Nuevo(), ClienteId = abono.ClienteId, SucursalId = abono.SucursalId, UsuarioId = encargado.Id, Tipo = TipoMovimientoCredito.AbonoRechazado,
                MontoCentavos = abono.MontoCentavos, FechaUtc = ahora, AbonoId = abono.Id, SaldoCentavos = saldo + abono.MontoCentavos,
                Motivo = "La transferencia no llegó",
            });
        }
        control.Registrar(abono.SucursalId, null, encargado.Id, AccionControlada.VerificarTransferencia,
            $"{(llego ? "Verificó" : "Rechazó")} la transferencia del abono {abono.Folio} de «{cliente?.Nombre}»"
            + (abono.Referencia is { } r ? $" (ref. {r})" : ""), abono.MontoCentavos);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return abono;
    }

    // ── Devolver el saldo a favor (parte 3) ──────────────────────────────────

    /// <summary>
    /// Devolverle en efectivo lo que tiene a favor, en el turno abierto de una caja: el dinero sale de ese turno y la
    /// cuenta sube hasta cero. Solo lo que no viene de transferencias por verificar, y con la regla «Devolver saldo a favor»
    /// (el cajero, con la autorización del encargado). Se decide con el saldo de dentro de la transacción; con el mismo Id,
    /// regresa la que ya estaba. No se cancela.
    /// </summary>
    public async Task<Abono> DevolverSaldoAsync(Guid clienteId, SolicitudDevolverSaldo s, CancellationToken ct = default)
    {
        if (await db.Abonos.FindAsync([s.Id], ct) is { } yaEsta)
            return LaMisma(yaEsta, clienteId);
        var turno = await db.Turnos.FindAsync([s.TurnoId], ct) ?? throw new InvalidOperationException("El turno no existe.");
        if (!turno.Abierto || turno.CajaId != s.CajaId)
            throw new InvalidOperationException("El saldo a favor se devuelve en el turno abierto de esta caja.");
        var cliente = await db.ClientesRegistrados.FindAsync([clienteId], ct) ?? throw new InvalidOperationException("El cliente ya no existe.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (await db.Abonos.AsNoTracking().SingleOrDefaultAsync(a => a.Id == s.Id, ct) is { } guardado)
                return LaMisma(guardado, clienteId);
            var cuenta = await CuentaAsync(cliente.Id, ct);
            if (s.SaldoVistoCentavos is { } visto && visto != cuenta.SaldoCentavos)
                throw new InvalidOperationException(cuenta.SaldoCentavos switch
                {
                    < 0 => $"La cuenta cambió mientras tanto: ahora tiene {Pesos(cuenta.AFavorCentavos)} a favor. Revísalo y devuelve otra vez.",
                    0 => "La cuenta cambió mientras tanto: ya no tiene saldo a favor.",
                    _ => $"La cuenta cambió mientras tanto: ya no tiene saldo a favor (debe {Pesos(cuenta.SaldoCentavos)}).",
                });
            if (cuenta.AFavorCentavos > 0 && cuenta.DevolvibleCentavos == 0)
                throw new InvalidOperationException(
                    $"Su saldo a favor viene de transferencias por verificar ({Pesos(cuenta.PorVerificarCentavos)}): se le devuelve cuando se verifiquen.");
            var calculo = CalculoAbono.DevolverSaldo(cuenta.DevolvibleCentavos, s.MontoCentavos);
            var control = new ServicioControl(db);
            await control.RequerirAsync(s.UsuarioId, turno.SucursalId, s.CajaId, AccionControlada.DevolverSaldoAFavor, calculo.EntregaCentavos,
                $"Devolvió {Pesos(calculo.EntregaCentavos)} en efectivo del saldo a favor de «{cliente.Nombre}» (tenía {Pesos(cuenta.AFavorCentavos)} a favor)",
                s.AutorizacionId, ct: ct);
            var ahora = Ahora;
            var devolucion = new Abono
            {
                Id = s.Id, Tipo = TipoAbono.SaldoDevuelto, SucursalId = turno.SucursalId, CajaId = s.CajaId, TurnoId = turno.Id, ClienteId = cliente.Id,
                UsuarioId = s.UsuarioId, Forma = FormaPago.Efectivo, MontoCentavos = calculo.MontoCentavos, RedondeoCentavos = calculo.RedondeoCentavos,
                Verificacion = Verificacion.NoAplica, SaldoAnteriorCentavos = cuenta.SaldoCentavos, SaldoCentavos = cuenta.SaldoCentavos + calculo.MontoCentavos,
                FechaUtc = ahora,
            };
            db.Abonos.Add(devolucion);
            // Vence como un ajuste que sube la cuenta: si algo de antes que llega después (lo fiado sin conexión) se come lo que
            // tenía a favor, lo que quede por pagar no amanece vencido.
            db.MovimientosCredito.Add(new MovimientoCredito
            {
                Id = devolucion.Id, ClienteId = cliente.Id, SucursalId = devolucion.SucursalId, CajaId = devolucion.CajaId, TurnoId = devolucion.TurnoId,
                UsuarioId = devolucion.UsuarioId, Tipo = TipoMovimientoCredito.SaldoDevuelto, MontoCentavos = devolucion.MontoCentavos, FechaUtc = ahora,
                Vence = CuentaCredito.Vence(ahora, cliente.PlazoCreditoDias), AbonoId = devolucion.Id, SaldoCentavos = devolucion.SaldoCentavos,
            });
            db.AperturasCajon.Add(new AperturaCajon
            {
                CajaId = devolucion.CajaId, TurnoId = devolucion.TurnoId, UsuarioId = devolucion.UsuarioId, Motivo = MotivoApertura.Abono,
                Nota = $"Saldo a favor devuelto {devolucion.Folio} a «{cliente.Nombre}»", FechaUtc = ahora,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return devolucion;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Con el mismo Id ya se guardó algo: si es esta devolución, se regresa; si no, el Id ya se usó.</summary>
    private static Abono LaMisma(Abono guardado, Guid clienteId) =>
        guardado.ClienteId == clienteId && guardado.Tipo == TipoAbono.SaldoDevuelto
            ? guardado : throw new InvalidOperationException("Ese folio ya se usó para otra cosa.");

    // ── Sin conexión ─────────────────────────────────────────────────────────

    /// <summary>
    /// Un abono que hizo una caja sin conexión. Se guarda siempre y tal como se cobró (el dinero ya se recibió y el
    /// comprobante ya se entregó): con el saldo que conocía la caja, aunque su turno ya tenga corte Z (entonces queda fuera
    /// de ese corte y se avisa) o aunque deje la cuenta a favor (queda por revisar). Con el mismo Id, se guarda uno.
    /// </summary>
    public async Task<Abono> AbonoDeContingenciaAsync(AbonoContingenciaDto a, CancellationToken ct = default)
    {
        if (await db.Abonos.FindAsync([a.Id], ct) is { } yaEsta)
            return yaEsta;
        var turno = await db.Turnos.FindAsync([a.TurnoId], ct) ?? throw new InvalidOperationException("El turno no existe.");
        if (turno.CajaId != a.CajaId)
            throw new InvalidOperationException("El abono no es de un turno de esta caja.");
        var cliente = await db.ClientesRegistrados.FindAsync([a.ClienteId], ct) ?? throw new InvalidOperationException("El cliente ya no existe.");
        if (a.Forma is not (FormaPago.Efectivo or FormaPago.Tarjeta or FormaPago.Transferencia) || a.MontoCentavos <= 0)
            throw new ArgumentException("El abono no es válido.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (await db.Abonos.AsNoTracking().SingleOrDefaultAsync(x => x.Id == a.Id, ct) is { } guardado)
                return guardado;
            var saldo = await SaldoAsync(cliente.Id, ct);
            var fecha = Acotar(a.FechaUtc, turno.AbiertoUtc);
            var referencia = string.IsNullOrWhiteSpace(a.Referencia) ? null : a.Referencia.Trim() is var r && r.Length > 60 ? r[..60] : r;
            var abono = new Abono
            {
                Id = a.Id, SucursalId = turno.SucursalId, CajaId = a.CajaId, TurnoId = turno.Id, ClienteId = cliente.Id, UsuarioId = a.UsuarioId, Forma = a.Forma,
                MontoCentavos = a.MontoCentavos, RedondeoCentavos = a.RedondeoCentavos, RecibidoCentavos = a.RecibidoCentavos, CambioCentavos = a.CambioCentavos,
                Referencia = referencia, Verificacion = a.Forma == FormaPago.Transferencia ? Verificacion.PorVerificar : Verificacion.NoAplica,
                SaldoAnteriorCentavos = a.SaldoConocidoCentavos, SaldoCentavos = a.SaldoConocidoCentavos - a.MontoCentavos, Vence = a.Vence,
                FechaUtc = fecha, EnContingencia = true, FueraDeCorteUtc = turno.Abierto ? null : Ahora,
            };
            db.Abonos.Add(abono);
            var movimiento = new MovimientoCredito
            {
                Id = abono.Id, ClienteId = cliente.Id, SucursalId = abono.SucursalId, CajaId = abono.CajaId, TurnoId = abono.TurnoId, UsuarioId = abono.UsuarioId,
                Tipo = TipoMovimientoCredito.Abono, MontoCentavos = -abono.MontoCentavos, FechaUtc = fecha, AbonoId = abono.Id,
                SaldoCentavos = abono.SaldoCentavos, EnContingencia = true,
            };
            db.MovimientosCredito.Add(movimiento);
            if (abono.Forma == FormaPago.Efectivo)
                db.AperturasCajon.Add(new AperturaCajon
                {
                    CajaId = abono.CajaId, TurnoId = abono.TurnoId, UsuarioId = abono.UsuarioId, Motivo = MotivoApertura.Abono,
                    Nota = $"Abono {abono.Folio} de «{cliente.Nombre}» (sin conexión)", FechaUtc = fecha,
                });
            if (saldo - abono.MontoCentavos < 0)
            {
                var caja = await db.Cajas.FindAsync([abono.CajaId], ct);
                await RevisarAsync(movimiento, MotivoRevisionCredito.SaldoAFavor, abono.MontoCentavos - saldo,
                    $"Abono sin conexión en {caja?.Nombre} ({abono.Folio}) de «{cliente.Nombre}» por {Pesos(abono.MontoCentavos)}: la caja sabía que debía "
                    + $"{Pesos(a.SaldoConocidoCentavos)}; con lo que llegó antes debía {Pesos(saldo)} y le quedan {Pesos(abono.MontoCentavos - saldo)} a favor.", ct);
            }
            if (!turno.Abierto)
                await new RegistroCaja(db).AvisarFueraDeCorteAsync(turno, abono.Id,
                    $"El abono {abono.Folio} de «{cliente.Nombre}» de las {HoraMexico.ALocal(fecha):HH:mm} por {Pesos(abono.CobradoCentavos)}"
                    + (abono.Forma == FormaPago.Efectivo ? "" : abono.Forma == FormaPago.Tarjeta ? " con tarjeta" : " por transferencia"),
                    abono.Forma == FormaPago.Efectivo ? abono.CobradoCentavos : 0, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return abono;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Los saldos de la sucursal para una caja, en una sola foto (una transacción de lectura: no detiene a los que
    /// cobran): la versión, las cuentas y cuáles de los pendientes de la caja ya están sumados en ellas.
    /// </summary>
    public async Task<SaldosCreditoDto> SaldosAsync(Guid sucursalId, IReadOnlyList<Guid> pendientes, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var tx = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await db.Database.UseTransactionAsync(tx, ct);
            var version = await db.Secuencias.AsNoTracking().Where(x => x.Nombre == Secuencia.Credito).Select(x => x.Valor).FirstOrDefaultAsync(ct);
            var cuentas = await CuentasDeLaSucursalAsync(sucursalId, ct);
            var incluidos = new List<Guid>();
            foreach (var trozo in pendientes.Distinct().Chunk(500))
                incluidos.AddRange(await db.MovimientosCredito.AsNoTracking().Where(m => trozo.Contains(m.Id)).Select(m => m.Id).ToListAsync(ct));
            await db.Database.UseTransactionAsync(null, ct);
            return new SaldosCreditoDto(version, cuentas, incluidos);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Lo que no cuadró de lo que subieron las cajas sin conexión y nadie ha revisado, lo más viejo primero.</summary>
    public async Task<List<RevisionCreditoDto>> PorRevisarAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var revisiones = await (from r in db.RevisionesCredito.AsNoTracking()
                                join c in db.ClientesRegistrados on r.ClienteId equals c.Id
                                where r.SucursalId == sucursalId && r.RevisadaUtc == null
                                select new { Revision = r, Cliente = c.Nombre }).ToListAsync(ct);
        return revisiones.OrderBy(x => x.Revision.CreadaUtc)
            .Select(x => new RevisionCreditoDto(x.Revision.Id, x.Revision.ClienteId, x.Cliente, HoraMexico.ALocal(x.Revision.CreadaUtc), x.Revision.Motivo,
                x.Revision.Detalle, x.Revision.MontoCentavos))
            .ToList();
    }

    /// <summary>El encargado o el dueño (la regla de los clientes) la da por revisada, con una nota de qué se hizo.</summary>
    public async Task MarcarRevisadaAsync(Guid revisionId, SolicitudRevision s, CancellationToken ct = default)
    {
        var revision = await db.RevisionesCredito.FindAsync([revisionId], ct) ?? throw new InvalidOperationException("La revisión no existe.");
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, revision.SucursalId, AccionControlada.AdministrarClientes,
            "Lo de crédito por revisar lo revisan el encargado y el dueño.", ct);
        if (revision.RevisadaUtc is not null)
            return;
        var nota = string.IsNullOrWhiteSpace(s.Nota) ? null : s.Nota.Trim();
        (revision.RevisadaUtc, revision.RevisoId, revision.Nota) = (Ahora, s.UsuarioId, nota is { Length: > 200 } ? nota[..200] : nota);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Los abonos de hoy en una caja, los más recientes primero.</summary>
    public async Task<List<AbonoDelDiaDto>> AbonosDelDiaAsync(Guid cajaId, CancellationToken ct = default)
    {
        var desde = HoraMexico.AUtc(HoraMexico.ALocal(Ahora).Date);
        var turnoAbierto = await db.Turnos.Where(t => t.CajaId == cajaId && t.CerradoUtc == null).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        var abonos = await (from a in db.Abonos.AsNoTracking()
                            join c in db.ClientesRegistrados on a.ClienteId equals c.Id
                            join u in db.Usuarios on a.UsuarioId equals u.Id
                            where a.CajaId == cajaId && a.FechaUtc >= desde
                            select new { Abono = a, Cliente = c.Nombre, Usuario = u.Nombre }).ToListAsync(ct);
        return abonos.OrderByDescending(x => x.Abono.FechaUtc)
            .Select(x => new AbonoDelDiaDto(x.Abono.Id, x.Abono.Folio, HoraMexico.ALocal(x.Abono.FechaUtc), x.Cliente, x.Abono.Forma,
                // De un saldo devuelto, el efectivo que salió (con su redondeo), como en el corte.
                x.Abono.Tipo == TipoAbono.SaldoDevuelto ? x.Abono.CobradoCentavos : x.Abono.MontoCentavos,
                x.Usuario, x.Abono.CanceladoUtc is not null, x.Abono.TurnoId == turnoAbierto, x.Abono.Tipo))
            .ToList();
    }

    internal static string Pesos(long centavos) => AlertasTelegram.Dinero(centavos);
}
