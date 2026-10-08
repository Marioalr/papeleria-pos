using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
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
    public async Task<Dictionary<Guid, CreditoClienteDto>> CuentasAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
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
        return condiciones.ToDictionary(c => c.Id, c =>
        {
            var cuenta = CuentaCredito.Aplicar(movimientos[c.Id]);
            return new CreditoClienteDto(c.Id, cuenta.SaldoCentavos, porVerificar.GetValueOrDefault(c.Id), c.LimiteCreditoCentavos, c.PlazoCreditoDias,
                cuenta.Cargos.Where(p => p.PendienteCentavos > 0).Select(p => new CargoPendienteDto(p.Vence, p.PendienteCentavos)).ToList());
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
        var ventas = movimientos.Where(m => m.VentaId is not null).Select(m => m.VentaId!.Value).Distinct().ToList();
        var folios = await db.Ventas.AsNoTracking().Where(v => ventas.Contains(v.Id)).Select(v => new { v.Id, v.Serie, v.Folio })
            .ToDictionaryAsync(v => v.Id, v => $"{v.Serie}-{v.Folio:000000}", ct);
        long saldo = 0;
        var renglones = movimientos.OrderBy(m => m.FechaUtc).ThenBy(m => m.Id).Select(m =>
        {
            saldo += m.MontoCentavos;
            var folio = m.Tipo is TipoMovimientoCredito.Abono or TipoMovimientoCredito.AbonoCancelado or TipoMovimientoCredito.AbonoRechazado
                ? m.AbonoId is { } abono ? Abono.FolioDe(abono) : null
                : m.VentaId is { } venta ? folios.GetValueOrDefault(venta) : null;
            return new MovimientoCreditoDto(m.Id, HoraMexico.ALocal(m.FechaUtc), m.Tipo, Concepto(m), folio, m.MontoCentavos, saldo, m.Vence, m.EnContingencia);
        }).ToList();
        renglones.Reverse();
        return new CuentaClienteDto(cuenta, renglones.Take(ultimos).ToList());
    }

    private static string Concepto(MovimientoCredito m) => m.Tipo switch
    {
        TipoMovimientoCredito.Cargo => "Compra a crédito",
        TipoMovimientoCredito.Abono => "Abono",
        TipoMovimientoCredito.Devolucion => "Devolución de lo fiado",
        TipoMovimientoCredito.Cancelacion => "Cancelación de una compra a crédito",
        TipoMovimientoCredito.AbonoCancelado => $"Abono cancelado{(m.Motivo is { } motivo ? $": {motivo}" : "")}",
        TipoMovimientoCredito.AbonoRechazado => "La transferencia del abono no llegó",
        TipoMovimientoCredito.Ajuste => $"Ajuste: {m.Motivo}",
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
    /// Después de <see cref="Venta.Pagar"/>, en la misma transacción: el cargo con lo fiado, cuándo vence (con el plazo de
    /// hoy) y el saldo que dice el ticket. Nunca rechaza por reglas del crédito (eso ya se revisó, o vino de sin conexión).
    /// </summary>
    internal async Task CargarAsync(Venta venta, CancellationToken ct)
    {
        if (venta.CreditoCentavos == 0)
            return;
        var cliente = await db.ClientesRegistrados.FindAsync([venta.ClienteId!.Value], ct) ?? throw new InvalidOperationException("El cliente ya no existe.");
        var saldo = await SaldoAsync(cliente.Id, ct);
        db.MovimientosCredito.Add(new MovimientoCredito
        {
            Id = venta.Id, ClienteId = cliente.Id, SucursalId = venta.SucursalId, CajaId = venta.CajaId, TurnoId = venta.TurnoId, UsuarioId = venta.CajeroId,
            Tipo = TipoMovimientoCredito.Cargo, MontoCentavos = venta.CreditoCentavos, FechaUtc = venta.PagadaUtc!.Value,
            Vence = CuentaCredito.Vence(venta.PagadaUtc.Value, cliente.PlazoCreditoDias), VentaId = venta.Id,
            SaldoCentavos = saldo + venta.CreditoCentavos, EnContingencia = venta.EnContingencia,
        });
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
            .Select(x => new AbonoDelDiaDto(x.Abono.Id, x.Abono.Folio, HoraMexico.ALocal(x.Abono.FechaUtc), x.Cliente, x.Abono.Forma, x.Abono.MontoCentavos,
                x.Usuario, x.Abono.CanceladoUtc is not null, x.Abono.TurnoId == turnoAbierto))
            .ToList();
    }

    internal static string Pesos(long centavos) => AlertasTelegram.Dinero(centavos);
}
