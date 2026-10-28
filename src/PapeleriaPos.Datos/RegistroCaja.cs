using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// El ticket ya salió (o se canceló o se devolvió algo de él): su impresión es una reimpresión, con su motivo (2026-10-22).
/// </summary>
public sealed class TicketYaSalio(string mensaje) : InvalidOperationException(mensaje);

/// <summary>
/// Lo que pasa en una caja y se guarda: turnos, cobros, entradas y salidas de efectivo y cortes. Cada operación es una
/// sola transacción: o queda todo o no queda nada.
/// </summary>
public sealed class RegistroCaja(PosDbContext db)
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    public async Task<Turno?> TurnoAbiertoAsync(Guid cajaId, CancellationToken ct = default) =>
        await db.Turnos.SingleOrDefaultAsync(t => t.CajaId == cajaId && t.CerradoUtc == null, ct);

    /// <summary>
    /// Un turno por cajero, y una caja no puede tener dos turnos abiertos. Con <paramref name="id"/> (lo pone la caja), si la
    /// respuesta se perdió y lo vuelve a pedir, regresa el mismo turno.
    /// </summary>
    public async Task<Turno> AbrirTurnoAsync(Guid cajaId, Guid cajeroId, long fondoCentavos, Guid? id = null, CancellationToken ct = default)
    {
        if (id is { } yaPedido && await db.Turnos.FindAsync([yaPedido], ct) is { } existente)
            return existente;
        if (fondoCentavos < 0)
            throw new ArgumentException("El fondo no puede ser negativo.");
        var caja = await db.Cajas.FindAsync([cajaId], ct) ?? throw new InvalidOperationException("La caja no existe.");
        if (await TurnoAbiertoAsync(cajaId, ct) is not null)
            throw new InvalidOperationException($"La {caja.Nombre} ya tiene un turno abierto: primero el corte del turno anterior.");

        var turno = new Turno { Id = id ?? Ids.Nuevo(), SucursalId = caja.SucursalId, CajaId = cajaId, CajeroId = cajeroId, FondoCentavos = fondoCentavos, AbiertoUtc = Ahora };
        db.Turnos.Add(turno);
        await db.SaveChangesAsync(ct);
        return turno;
    }

    /// <summary>F7 y F8: entrada o salida de efectivo, siempre con motivo.</summary>
    /// <param name="aEleventa">El piloto en la caja 2: esta caja manda a eleventa (ver <see cref="EncolarParaEleventaAsync"/>).</param>
    public async Task<MovimientoCaja> RegistrarMovimientoAsync(
        Guid turnoId, TipoMovimientoCaja tipo, long montoCentavos, string motivo, Guid usuarioId, Guid? id = null, CancellationToken ct = default,
        bool aEleventa = false)
    {
        if (id is { } yaPedido && await db.MovimientosCaja.FindAsync([yaPedido], ct) is { } existente)
            return existente;
        if (montoCentavos <= 0)
            throw new ArgumentException("El monto debe ser mayor a cero.");
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Toda entrada o salida de efectivo lleva motivo.");
        var turno = await TurnoAbiertoPorIdAsync(turnoId, ct);
        var accion = tipo == TipoMovimientoCaja.Entrada ? AccionControlada.EntradaEfectivo : AccionControlada.SalidaEfectivo;
        await new ServicioControl(db).RequerirAsync(usuarioId, turno.SucursalId, turno.CajaId, accion, montoCentavos,
            $"{(tipo == TipoMovimientoCaja.Entrada ? "Entrada" : "Salida")} de efectivo", motivo: motivo);

        var movimiento = new MovimientoCaja { Id = id ?? Ids.Nuevo(), TurnoId = turno.Id, Tipo = tipo, MontoCentavos = montoCentavos, Motivo = motivo.Trim(), UsuarioId = usuarioId, FechaUtc = Ahora };
        db.MovimientosCaja.Add(movimiento);
        db.AperturasCajon.Add(new AperturaCajon { CajaId = turno.CajaId, TurnoId = turno.Id, UsuarioId = usuarioId, Motivo = MotivoApertura.SinVenta, Nota = $"{tipo}: {motivo.Trim()}", FechaUtc = Ahora });
        await EncolarParaEleventaAsync(movimiento, aEleventa, ct);
        await db.SaveChangesAsync(ct);
        return movimiento;
    }

    /// <summary>
    /// Lo que dice la caja en el piloto con eleventa al cancelar una entrada o salida (2026-10-22): la misma que dice su pantalla
    /// (PilotoConEleventa, en la interfaz; una prueba lo revisa).
    /// </summary>
    public const string NoSeCancelaEnElPiloto =
        "Mientras dure el piloto con eleventa, las entradas y salidas van a eleventa y aquí no se cancelan: no se sabe cómo anota eleventa "
        + "una cancelada. Si te equivocaste, haz la contraria (F7 o F8).";

    /// <summary>
    /// Las entradas y salidas de efectivo de un turno (F7/F8), las más nuevas primero, y si cada una se puede cancelar (2026-10-22):
    /// la de un turno abierto (antes de su corte) que no se ha cancelado y no va a eleventa. En el piloto, ninguna; fuera de él,
    /// tampoco la que ya fue a eleventa ni la salida de una devolución hecha en eleventa, que va con ella.
    /// </summary>
    public async Task<MovimientosDelTurnoDto> MovimientosDelTurnoAsync(Guid turnoId, bool piloto, CancellationToken ct = default)
    {
        var turno = await TurnoPorIdAsync(turnoId, ct);
        var movimientos = await db.MovimientosCaja.AsNoTracking().Where(m => m.TurnoId == turnoId).OrderByDescending(m => m.FechaUtc).ToListAsync(ct);
        var deEleventa = await DeEleventaAsync(movimientos.Select(m => m.Id).ToList(), ct);
        var personas = movimientos.Select(m => m.UsuarioId).Concat(movimientos.Select(m => m.CanceladoPorId).OfType<Guid>()).Distinct().ToList();
        var nombres = await db.Usuarios.AsNoTracking().Where(u => personas.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        return new MovimientosDelTurnoDto(movimientos.Select(m => new MovimientoDelTurnoDto(m.Id, FolioDe(m), m.Tipo, m.MontoCentavos, m.Motivo,
                HoraMexico.ALocal(m.FechaUtc), nombres.GetValueOrDefault(m.UsuarioId, "—"), !m.Cancelado && turno.Abierto && !piloto && !deEleventa.Contains(m.Id),
                m.CanceladoUtc is { } cancelado ? HoraMexico.ALocal(cancelado) : null, m.CanceladoPorId is { } quien ? nombres.GetValueOrDefault(quien, "—") : null,
                m.MotivoCancelacion)).ToList(),
            piloto);
    }

    /// <summary>El folio de su comprobante: el final de su Id, como lo imprime la salida.</summary>
    public static string FolioDe(MovimientoCaja m) => m.Id.ToString()[^6..].ToUpperInvariant();

    /// <summary>
    /// Cuántas entradas y cuántas salidas de cada turno se cancelaron (2026-10-22): el corte ya no las cuenta y lo dice, al
    /// cerrarlo y en el historial. Como solo se cancelan antes del corte, después no cambia.
    /// </summary>
    public static async Task<Dictionary<Guid, (int Entradas, int Salidas)>> CanceladasAsync(
        PosDbContext db, IReadOnlyCollection<Guid> turnos, CancellationToken ct = default)
    {
        var lista = turnos.ToList();
        var canceladas = await db.MovimientosCaja.AsNoTracking().Where(m => lista.Contains(m.TurnoId) && m.CanceladoUtc != null)
            .Select(m => new { m.TurnoId, m.Tipo }).ToListAsync(ct);
        return canceladas.GroupBy(m => m.TurnoId).ToDictionary(g => g.Key,
            g => (g.Count(m => m.Tipo == TipoMovimientoCaja.Entrada), g.Count(m => m.Tipo == TipoMovimientoCaja.Salida)));
    }

    /// <summary>
    /// Cancelar una entrada o salida equivocada (F7/F8, como en eleventa; 2026-10-22; antes se compensaba con la contraria): solo
    /// en su caja y en su mismo turno, antes de su corte, con motivo y con «Cancelar una entrada o salida» (el monto es el del
    /// movimiento; si pide autorización, se gasta la que se mande). Queda en la bitácora y el corte ya no la cuenta. En el
    /// piloto con eleventa no (<see cref="NoSeCancelaEnElPiloto"/>), ni la que ya va a eleventa.
    /// </summary>
    /// <param name="piloto">El piloto: esta caja manda sus entradas y salidas a eleventa.</param>
    /// <remarks>
    /// La misma solicitud (<see cref="SolicitudCancelarMovimiento.Id"/>) otra vez, porque se perdió la respuesta, ya está hecha:
    /// regresa la misma, sin otra bitácora ni otra apertura del cajón (revisión del PR #133).
    /// </remarks>
    public async Task<MovimientoCaja> CancelarMovimientoAsync(Guid movimientoId, SolicitudCancelarMovimiento s, bool piloto = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(s.Motivo))
            throw new ArgumentException("La cancelación lleva motivo.");
        // Lo que decide se lee dentro de la transacción: una cancelación y un corte a la vez no pasan los dos (el corte Z también
        // lee dentro de la suya).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var movimiento = await db.MovimientosCaja.FindAsync([movimientoId], ct) ?? throw new InvalidOperationException("Esa entrada o salida no existe.");
            await db.Entry(movimiento).ReloadAsync(ct);
            if (movimiento.Cancelado && movimiento.CancelacionId == s.Id)
                return movimiento;
            var turno = await TurnoPorIdAsync(movimiento.TurnoId, ct);
            await db.Entry(turno).ReloadAsync(ct);
            if (!turno.Abierto || turno.CajaId != s.CajaId)
                throw new InvalidOperationException("Una entrada o salida se cancela en su caja y en su mismo turno, antes de su corte.");
            if (piloto || (await DeEleventaAsync([movimiento.Id], ct)).Count > 0)
                throw new InvalidOperationException(NoSeCancelaEnElPiloto);
            var entrada = movimiento.Tipo == TipoMovimientoCaja.Entrada;
            // Antes del permiso: una ya cancelada no pide autorización para luego decir que ya se canceló.
            if (movimiento.Cancelado)
                throw new InvalidOperationException($"Esa {(entrada ? "entrada" : "salida")} ya se canceló.");
            await new ServicioControl(db).RequerirAsync(s.UsuarioId, turno.SucursalId, s.CajaId, AccionControlada.CancelarMovimiento, movimiento.MontoCentavos,
                $"Canceló la {(entrada ? "entrada" : "salida")} de efectivo {FolioDe(movimiento)} de las {HoraMexico.ALocal(movimiento.FechaUtc):HH:mm} "
                + $"({movimiento.Motivo})", s.AutorizacionId, s.Motivo.Trim(), ct: ct);
            var ahora = Ahora;
            movimiento.Cancelar(s.UsuarioId, s.Motivo, ahora, s.Id);
            // El efectivo regresa (o sale otra vez) del cajón.
            db.AperturasCajon.Add(new AperturaCajon
            {
                CajaId = turno.CajaId, TurnoId = turno.Id, UsuarioId = s.UsuarioId, Motivo = MotivoApertura.SinVenta,
                Nota = $"Cancelación de la {(entrada ? "entrada" : "salida")} {FolioDe(movimiento)}: {movimiento.MotivoCancelacion}", FechaUtc = ahora,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return movimiento;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>De esas, las que van (o ya fueron) a eleventa: por su bandeja, o la salida de una devolución hecha en eleventa.</summary>
    private async Task<HashSet<Guid>> DeEleventaAsync(List<Guid> ids, CancellationToken ct)
    {
        var conIds = ids.Select(id => (Guid?)id).ToList();
        var enLaBandeja = await db.EnviosAEleventa.AsNoTracking().Where(e => conIds.Contains(e.MovimientoId)).Select(e => e.MovimientoId!.Value).ToListAsync(ct);
        var deDevoluciones = await db.Set<DevolucionDeEleventa>().AsNoTracking().Where(d => conIds.Contains(d.MovimientoId))
            .Select(d => d.MovimientoId!.Value).ToListAsync(ct);
        return [.. enLaBandeja, .. deDevoluciones];
    }

    /// <summary>
    /// El piloto en la caja 2 (docs/migracion-eleventa.md, «Entradas y salidas de dinero»): la entrada o salida también
    /// va a eleventa, en la misma transacción, si esta caja manda a eleventa o si su turno ya mandó algo. También la que
    /// se hace antes de la primera venta: eleventa abre el turno para una entrada, igual que para una venta. Quien llama
    /// guarda.
    /// </summary>
    private async Task EncolarParaEleventaAsync(MovimientoCaja movimiento, bool aEleventa, CancellationToken ct)
    {
        if (aEleventa || await TurnoVaAEleventaAsync(movimiento.TurnoId, ct))
            db.EnviosAEleventa.Add(new EnvioAEleventa { Tipo = TipoEnvioEleventa.MovimientoDeCaja, MovimientoId = movimiento.Id, CreadoUtc = Ahora });
    }

    /// <summary>
    /// F12: asigna el folio de la serie, aplica los cobros, descuenta del inventario lo vendido (guardando su costo en
    /// cada línea) y registra la apertura del cajón si hubo efectivo.
    /// </summary>
    /// <param name="impresiones">Los trabajos que van en la línea "Impresiones": quedan cobrados con la venta.</param>
    /// <param name="deControlPrint">
    /// Los de la cola de Control Print que van en la misma línea, ya revisados allá: quedan cobrados aquí con la venta (allá siguen
    /// «por cobrar»: el POS nunca le escribe a Control Print).
    /// </param>
    public Task<Venta> CobrarAsync(
        Venta venta, IReadOnlyList<CobroSolicitado> cobros, CancellationToken ct = default, IReadOnlyList<TrabajoImpresion>? impresiones = null,
        IReadOnlyList<TrabajoControlPrintParaCobrar>? deControlPrint = null) =>
        GuardarCobroAsync(venta, cobros, null, Ahora, ct, impresiones, deControlPrint: deControlPrint);

    /// <summary>
    /// Una venta que cobró una caja sin conexión y llega con la bandeja: trae su folio y su hora. Las reglas ya se
    /// revisaron en la caja (su bitácora sube aparte), así que aquí no se vuelven a pedir autorizaciones. Ya está cobrada:
    /// si su turno ya tiene corte Z (la primera vez no subió y el corte sí), se guarda igual, fuera de ese corte.
    /// </summary>
    /// <param name="redondear">Falso para lo que la caja cobró antes de que existiera el redondeo al peso: se cobra exacto.</param>
    /// <param name="fiado">Si fió: lo que sabía la caja (el cargo guarda lo que se imprimió y lo que no cuadre queda por revisar).</param>
    public Task<Venta> CobroDeContingenciaAsync(
        Venta venta, IReadOnlyList<CobroSolicitado> cobros, int folio, DateTime pagadaUtc, CancellationToken ct = default, bool redondear = true,
        FiadoSinConexion? fiado = null) =>
        GuardarCobroAsync(venta, cobros, folio, pagadaUtc, ct, redondear: redondear, fiado: fiado);

    private async Task<Venta> GuardarCobroAsync(
        Venta venta, IReadOnlyList<CobroSolicitado> cobros, int? folioDeLaCaja, DateTime ahora, CancellationToken ct, IReadOnlyList<TrabajoImpresion>? impresiones = null,
        bool redondear = true, FiadoSinConexion? fiado = null, IReadOnlyList<TrabajoControlPrintParaCobrar>? deControlPrint = null)
    {
        var turno = folioDeLaCaja is null ? await TurnoAbiertoPorIdAsync(venta.TurnoId, ct) : await TurnoPorIdAsync(venta.TurnoId, ct);
        if (turno.CajaId != venta.CajaId || turno.SucursalId != venta.SucursalId)
            throw new InvalidOperationException("La venta no es del turno abierto de esta caja.");

        venta.ValidarCobros(cobros, redondear);

        // Lo que el cobro solo lee va antes de tomar el candado de escritura (entrega 5, parte 2): dos cobros a la vez ya no se
        // esperan mientras se leen los productos y sus costos. Dentro queda lo que se lee para escribir: el folio y la existencia.
        var conInventario = venta.Lineas.Where(l => l.LlevaInventario && l.VarianteId is not null).Select(l => l.VarianteId!.Value).Distinct().ToList();
        var variantes = await db.Variantes.Where(v => conInventario.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var ultimosCostos = await UltimosCostosAsync(conInventario, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var control = new ServicioControl(db);
            int folio;
            if (folioDeLaCaja is { } dado)
            {
                if (await db.Ventas.AnyAsync(v => v.SucursalId == venta.SucursalId && v.Serie == venta.Serie && v.Folio == dado, ct))
                    throw new InvalidOperationException($"El folio {venta.Serie}-{dado:000000} ya existe: hay que revisarlo.");
                await AlcanzarFolioAsync(venta.SucursalId, venta.Serie, dado, ct);
                folio = dado;
                venta.EnContingencia = true;
            }
            else
            {
                // Precio cambiado en la caja: la regla se revisa aquí y se gasta la autorización que se mandó.
                foreach (var cambiada in venta.Lineas.Where(l => l.PrecioCambiado))
                    await control.RequerirAsync(venta.CajeroId, venta.SucursalId, venta.CajaId, AccionControlada.CambiarPrecio, cambiada.ImporteCentavos,
                        $"Precio de «{cambiada.Descripcion}»: {cambiada.PrecioOriginalCentavos / 100m:N2} → {cambiada.PrecioCentavos / 100m:N2}",
                        cambiada.AutorizacionPrecioId, ventaId: venta.Id, ct: ct);
                await RevisarDescuentosAsync(venta, control, ct);
                // Lo fiado: el límite se revisa con el saldo de dentro de esta transacción.
                if (cobros.Where(c => c.Forma == FormaPago.Credito).Sum(c => c.MontoCentavos) is var aCredito and > 0)
                    await new RegistroCredito(db).RevisarFiadoAsync(venta, aCredito, control, ct);
                folio = await SiguienteFolioAsync(venta.SucursalId, venta.Serie, ct);
            }

            var inventario = new RegistroInventario(db);
            foreach (var linea in venta.Lineas.Where(l => l.LlevaInventario && l.VarianteId is not null))
            {
                var variante = variantes.GetValueOrDefault(linea.VarianteId!.Value)
                    ?? throw new InvalidOperationException($"«{linea.Descripcion}» ya no está en el catálogo.");
                var existencia = await inventario.ExistenciaAsync(venta.SucursalId, variante.Id, ct);
                var salida = Kardex.Salida(venta.SucursalId, variante, TipoMovimiento.Venta, linea.CantidadBase, existencia,
                    ultimosCostos.GetValueOrDefault(variante.Id), ahora, documentoTipo: nameof(Venta), documentoId: venta.Id);
                linea.CostoCentavos = -salida.CostoCentavos;
                await inventario.AgregarAsync(salida, ct);
            }

            venta.Pagar(folio, cobros, ahora, redondear);
            await new RegistroCredito(db).CargarAsync(venta, turno, fiado, ct);
            ImpresionesDelPos.EnLaVenta(db).Cobrar(impresiones ?? [], venta.Id, ahora);
            ImpresionesDelPos.EnLaVenta(db).CobrarControlPrint(venta.SucursalId, deControlPrint ?? [], venta.Id, ahora);
            foreach (var comun in venta.Lineas.Where(l => l.VarianteId is null && !l.EsImpresiones))
                control.Registrar(venta.SucursalId, venta.CajaId, venta.CajeroId, AccionControlada.ArticuloComun,
                    $"{comun.Descripcion} × {comun.Cantidad}", comun.ImporteCentavos, venta.Id);
            db.Ventas.Add(venta);
            if (venta.LlevaEfectivo)
                db.AperturasCajon.Add(new AperturaCajon { CajaId = venta.CajaId, TurnoId = turno.Id, UsuarioId = venta.CajeroId, Motivo = MotivoApertura.Venta, VentaId = venta.Id, FechaUtc = ahora });
            if (!turno.Abierto)
            {
                venta.FueraDeCorteUtc = Ahora;
                await AvisarFueraDeCorteAsync(turno, venta.Id,
                    $"La venta {venta.FolioTexto} de las {HoraMexico.ALocal(ahora):HH:mm} por {AlertasTelegram.Dinero(venta.TotalCobradoCentavos)}",
                    venta.Cobros.Where(c => c.Forma == FormaPago.Efectivo).Sum(c => c.MontoCentavos), ct);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return venta;
        }
        catch
        {
            // Que no queden en memoria el folio, las existencias ni la venta a medias.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// El cliente y los descuentos del ticket (docs/fase-1-impresion.md): el cliente registrado tiene que seguir activo;
    /// el descuento más alto se revisa con la regla (el cajero da hasta 10 % solo; más, con autorización) salvo el del
    /// cliente, que ya está aprobado. Y ningún descuento deja una línea debajo del costo sin autorización. Todo queda en
    /// la bitácora.
    /// </summary>
    private async Task RevisarDescuentosAsync(Venta venta, ServicioControl control, CancellationToken ct)
    {
        ClienteRegistrado? cliente = null;
        if (venta.ClienteId is { } id && (cliente = await db.ClientesRegistrados.FindAsync([id], ct)) is not { Activo: true })
            throw new InvalidOperationException("Ese cliente ya no está activo: quítalo del ticket (CTRL+L).");
        // Su descuento cambió mientras el ticket estaba abierto: mejor decirlo que pedir una autorización sin explicar por qué.
        if (cliente is not null && venta.Lineas.Any(l => l.MotivoDescuento == ClienteRegistrado.MotivoDescuento(cliente.Nombre) && l.DescuentoCentesimas != cliente.DescuentoCentesimas))
            throw new InvalidOperationException("El descuento del cliente cambió: vuelve a elegirlo con CTRL+L.");
        if (venta.DescuentoCentavos > 0)
        {
            var revisar = venta.DescuentoARevisar(cliente?.DescuentoCentesimas is > 0 and var d ? d : null);
            var detalle = DetalleDescuento(venta, cliente?.Nombre);
            if (revisar > 0)
                await control.RequerirAsync(venta.CajeroId, venta.SucursalId, venta.CajaId, AccionControlada.Descuento, revisar, detalle,
                    venta.AutorizacionDescuentoId, ventaId: venta.Id, ct: ct, montoBitacora: venta.DescuentoCentavos);
            else
                control.Registrar(venta.SucursalId, venta.CajaId, venta.CajeroId, AccionControlada.Descuento, detalle, venta.DescuentoCentavos, venta.Id);
        }

        var bajoCosto = await BajoCostoAsync(venta, ct);
        if (bajoCosto.Count > 0)
            await control.RequerirAsync(venta.CajeroId, venta.SucursalId, venta.CajaId, AccionControlada.VenderBajoCosto,
                bajoCosto.Sum(b => b.Costo - b.Linea.ImporteCentavos),
                $"Con descuento quedan debajo del costo: {string.Join(", ", bajoCosto.Select(b => b.Linea.Descripcion))}",
                venta.AutorizacionBajoCostoId, ventaId: venta.Id, ct: ct);
    }

    /// <summary>"Descuento de $25.00 (hasta 15 %): motivo" (o a qué cliente registrado), para la bitácora y Telegram.</summary>
    public static string DetalleDescuento(Venta venta, string? cliente)
    {
        var maximo = venta.Lineas.Where(l => l.TieneDescuento).Max(l => l.PorcentajeDescuento);
        var motivos = string.Join("; ", venta.Lineas.Where(l => l.TieneDescuento).Select(l => l.MotivoDescuento).Distinct());
        return $"Descuento de {AlertasTelegram.Dinero(venta.DescuentoCentavos)} (hasta {Permisos.TextoMonto(AccionControlada.Descuento, maximo)})"
            + (cliente is null ? "" : $" a {cliente}, cliente registrado") + $": {motivos}";
    }

    /// <summary>
    /// Las líneas con descuento que quedan debajo de lo que cuesta sacarlas del inventario (el mismo cálculo que al
    /// cobrar). El costo no sale de aquí hacia la caja: solo cuáles y cuánto falta en total.
    /// </summary>
    public async Task<List<(VentaLinea Linea, long Costo)>> BajoCostoAsync(Venta venta, CancellationToken ct = default)
    {
        var resultado = new List<(VentaLinea, long)>();
        var inventario = new RegistroInventario(db);
        foreach (var linea in venta.Lineas.Where(l => l.TieneDescuento && l.LlevaInventario && l.VarianteId is not null))
        {
            if (await db.Variantes.FindAsync([linea.VarianteId!.Value], ct) is not { } variante)
                continue;
            var existencia = await inventario.ExistenciaAsync(venta.SucursalId, variante.Id, ct);
            var costo = -Kardex.Salida(venta.SucursalId, variante, TipoMovimiento.Venta, linea.CantidadBase, existencia,
                await UltimoCostoAsync(variante.Id, ct), Ahora).CostoCentavos;
            if (costo > 0 && linea.ImporteCentavos < costo)
                resultado.Add((linea, costo));
        }
        return resultado;
    }

    /// <summary>
    /// Corte parcial: guarda la cuenta del turno, que sigue abierto. La pantalla ya no lo enseña (2026-10-14): antes de
    /// cerrar, el corte es a ciegas (<see cref="InformativoAsync"/>).
    /// </summary>
    public async Task<Corte> CorteXAsync(Guid turnoId, Guid usuarioId, CancellationToken ct = default)
    {
        var turno = await TurnoAbiertoPorIdAsync(turnoId, ct);
        var corte = CalculoCorte.Calcular(turno, await VentasAsync(turno.Id, ct), await MovimientosAsync(turno.Id, ct), TipoCorte.X, usuarioId, Ahora,
            devoluciones: await DevolucionesAsync(turno.Id, ct), abonos: await AbonosAsync(turno.Id, ct));
        db.Cortes.Add(corte);
        await db.SaveChangesAsync(ct);
        return corte;
    }

    /// <summary>
    /// El corte X (entrega 6, 2026-10-14): lo que se ve del turno antes de cerrarlo, sin el efectivo; y si
    /// <paramref name="usuarioId"/> puede hacer el corte o ver el del día, y quién puede hacerlo. No guarda nada.
    /// </summary>
    public async Task<CorteInformativoDto> InformativoAsync(Guid turnoId, Guid usuarioId, CancellationToken ct = default)
    {
        var turno = await TurnoAbiertoPorIdAsync(turnoId, ct);
        var ventas = await VentasAsync(turno.Id, ct);
        var corte = CalculoCorte.Calcular(turno, ventas, await MovimientosAsync(turno.Id, ct), TipoCorte.X, usuarioId, Ahora,
            devoluciones: await DevolucionesAsync(turno.Id, ct), abonos: await AbonosAsync(turno.Id, ct));
        var porVerificar = ventas.Where(v => v.Estado == EstadoVenta.Pagada).SelectMany(v => v.Cobros)
            .Count(c => c.Forma == FormaPago.Transferencia && c.Verificacion == Verificacion.PorVerificar)
            + await db.Abonos.CountAsync(a => a.TurnoId == turno.Id && a.Forma == FormaPago.Transferencia && a.Verificacion == Verificacion.PorVerificar
                && a.CanceladoUtc == null, ct);
        var control = new ServicioControl(db);
        var cajero = await db.Usuarios.Where(u => u.Id == turno.CajeroId).Select(u => u.Nombre).SingleAsync(ct);
        return new CorteInformativoDto(cajero, HoraMexico.ALocal(turno.AbiertoUtc), corte.Ventas, corte.Cancelaciones, corte.TarjetaCentavos,
            corte.TransferenciaCentavos, porVerificar, corte.CreditoCentavos, corte.Abonos, corte.AbonosTarjetaCentavos, corte.AbonosTransferenciaCentavos,
            (await control.EvaluarAsync(usuarioId, turno.SucursalId, AccionControlada.CorteZ, ct: ct)).Nivel == NivelPermiso.Permitido,
            await control.QuienesPuedenAsync(turno.SucursalId, AccionControlada.CorteZ, ct),
            (await control.EvaluarAsync(usuarioId, turno.SucursalId, AccionControlada.CorteDelDia, ct: ct)).Nivel == NivelPermiso.Permitido);
    }

    /// <summary>
    /// El corte del turno (entrega 6, 2026-10-14): quien ya entró con su PIN y tiene el permiso escribe el total que
    /// contó, a ciegas, sin volver a escribir su PIN. El permiso se revisa aquí, no en la caja; si no lo tiene, queda en la
    /// bitácora como negado. La diferencia queda a nombre del cajero del turno y, si pasa del umbral, el aviso sale de la
    /// bitácora (AlertasTelegram). El turno se cierra.
    /// </summary>
    /// <param name="id">
    /// El que le dio la caja (entrega 5, parte 2): si ya está (la caja lo mandó otra vez, o subió el que hizo sin conexión porque
    /// no le llegó la respuesta), es ese; no se cierra otra vez.
    /// </param>
    /// <remarks>
    /// Todo dentro de una transacción, desde antes de leer el turno (revisión del PR #133): lo que entra al corte (las ventas,
    /// las entradas y salidas, las devoluciones, los abonos) se lee con el candado de escritura ya tomado, así que una
    /// cancelación de una entrada o salida (o un cobro) que llega a la vez espera a que el corte termine, y ya no lo encuentra
    /// abierto. Antes el corte leía fuera de ella y una cancelación podía entrar entre sus lecturas y su guardado: el corte
    /// contaba la entrada cancelada y le marcaba al cajero un faltante que no era.
    /// </remarks>
    public async Task<Corte> CorteZAsync(Guid turnoId, Guid usuarioId, long contadoCentavos, Guid? id = null, CancellationToken ct = default)
    {
        await using var tx = await TransaccionPropiaAsync(ct);
        try
        {
            if (id is { } dado && await db.Cortes.FindAsync([dado], ct) is { } existente)
                return existente;
            var turno = await TurnoAbiertoAlDiaAsync(turnoId, ct);
            var control = new ServicioControl(db);
            if ((await control.EvaluarAsync(usuarioId, turno.SucursalId, AccionControlada.CorteZ, ct: ct)).Nivel != NivelPermiso.Permitido)
            {
                control.Registrar(turno.SucursalId, turno.CajaId, usuarioId, AccionControlada.CorteZ, "Intento sin permiso", resultado: ResultadoBitacora.Negado);
                await db.SaveChangesAsync(ct);
                if (tx is not null)
                    await tx.CommitAsync(ct);
                throw new PermisoRequerido(AccionControlada.CorteZ, NivelPermiso.NoPermitido,
                    $"El corte del turno lo hace quien tiene ese permiso: {Permisos.Nombres(await control.QuienesPuedenAsync(turno.SucursalId, AccionControlada.CorteZ, ct))}.");
            }

            var ahora = Ahora;
            var corte = CalculoCorte.Calcular(turno, await VentasAsync(turno.Id, ct), await MovimientosAsync(turno.Id, ct), TipoCorte.Z, usuarioId, ahora,
                contadoCentavos, await DevolucionesAsync(turno.Id, ct), await AbonosAsync(turno.Id, ct));
            if (id is { } suyo)
                corte.Id = suyo;
            turno.CerradoUtc = ahora;
            control.Registrar(turno.SucursalId, turno.CajaId, usuarioId, AccionControlada.CorteZ,
                $"Corte del turno de {await NombreAsync(turno.CajeroId, ct)}: esperado {corte.EsperadoCentavos / 100m:N2}, contado {corte.ContadoCentavos / 100m:N2}",
                corte.DiferenciaCentavos);
            db.Cortes.Add(corte);
            await CerrarEnEleventaAsync(turno, ct);
            await db.SaveChangesAsync(ct);
            // Si era el último turno abierto de la sucursal, sale el resumen del día por Telegram.
            await AlertasTelegram.ResumenSiEsElUltimoAsync(db, corte, turno.SucursalId, ct);
            await db.SaveChangesAsync(ct);
            if (tx is not null)
                await tx.CommitAsync(ct);
            return corte;
        }
        catch (Exception ex) when (ex is not PermisoRequerido)
        {
            // Lo que se quedó a medias en el contexto no se guarda después por otro camino.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// La transacción de una operación que decide con lo que lee (el corte Z): la abre si quien llama no trae una. Con
    /// Microsoft.Data.Sqlite empieza con el candado de escritura (BEGIN IMMEDIATE): otra que también escribe espera a que
    /// termine. Vacía si ya había una: entonces es de quien llama, y él la confirma.
    /// </summary>
    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> TransaccionPropiaAsync(CancellationToken ct) =>
        db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;

    /// <summary>El turno como está en la base (no como lo trae el contexto) y abierto; si no, el error de siempre.</summary>
    private async Task<Turno> TurnoAbiertoAlDiaAsync(Guid turnoId, CancellationToken ct)
    {
        var turno = await TurnoPorIdAsync(turnoId, ct);
        await db.Entry(turno).ReloadAsync(ct);
        return turno.Abierto ? turno : throw new InvalidOperationException("El turno ya está cerrado: cambia de usuario (Ctrl+U) para abrir otro.");
    }

    /// <summary>
    /// El piloto en la caja 2: si algo del turno fue a eleventa (una venta, una devolución que salió de esta caja o una
    /// entrada o salida de efectivo), el corte también cierra el turno de eleventa, después de que lleguen. Mientras dure
    /// el piloto (2026-10-12) el corte de la caja 2 se hace en eleventa: el servicio no hace el corte Z, y el cierre que
    /// encola un corte Z de la contingencia no se manda (CorteEnElPiloto).
    /// </summary>
    private async Task CerrarEnEleventaAsync(Turno turno, CancellationToken ct)
    {
        if (await TurnoVaAEleventaAsync(turno.Id, ct))
            db.EnviosAEleventa.Add(new EnvioAEleventa { Tipo = TipoEnvioEleventa.CierreDeTurno, TurnoId = turno.Id, CreadoUtc = Ahora });
    }

    /// <summary>Si algo del turno está en la bandeja hacia eleventa: una venta, una devolución o una entrada o salida.</summary>
    private async Task<bool> TurnoVaAEleventaAsync(Guid turnoId, CancellationToken ct)
    {
        var ventas = from e in db.EnviosAEleventa
                     join v in db.Ventas on e.VentaId equals v.Id
                     where e.Tipo == TipoEnvioEleventa.Venta && v.TurnoId == turnoId
                     select e.Id;
        var devoluciones = from e in db.EnviosAEleventa
                           join d in db.Devoluciones on e.DevolucionId equals d.Id
                           where d.TurnoId == turnoId
                           select e.Id;
        var movimientos = from e in db.EnviosAEleventa
                          join m in db.MovimientosCaja on e.MovimientoId equals m.Id
                          where m.TurnoId == turnoId
                          select e.Id;
        return await ventas.AnyAsync(ct) || await devoluciones.AnyAsync(ct) || await movimientos.AnyAsync(ct);
    }

    /// <summary>Lo que debería haber en efectivo en la caja ahora, y si ya pasó del límite de la sucursal.</summary>
    public async Task<(long Esperado, long? Limite)> EfectivoAsync(Guid turnoId, CancellationToken ct = default)
    {
        var turno = await TurnoAbiertoPorIdAsync(turnoId, ct);
        var corte = CalculoCorte.Calcular(turno, await VentasAsync(turno.Id, ct), await MovimientosAsync(turno.Id, ct), TipoCorte.X, turno.CajeroId, Ahora,
            devoluciones: await DevolucionesAsync(turno.Id, ct), abonos: await AbonosAsync(turno.Id, ct));
        var limite = await db.ConfiguracionesSucursal.Where(c => c.SucursalId == turno.SucursalId).Select(c => c.LimiteEfectivoCentavos).FirstOrDefaultAsync(ct);
        return (corte.EsperadoCentavos, limite);
    }

    /// <summary>
    /// La primera impresión del ticket de una venta que no salió al cobrar (2026-10-22): «Al cobrar» en «Ofrecerlo» o «No
    /// imprimirlo», o la impresora no lo sacó y se dejó con «No imprimirlo». No es una reimpresión, así que no pide motivo ni el
    /// permiso de reimprimir, y queda en la bitácora como primera impresión. Una sola vez, aunque dos cajas la pidan a la vez:
    /// si ya salió (o se canceló o se devolvió algo de ella, y saldría completo), <see cref="TicketYaSalio"/>: se reimprime con
    /// su motivo.
    /// </summary>
    public async Task<Venta> PrimeraImpresionAsync(Guid ventaId, Guid usuarioId, Guid cajaId, CancellationToken ct = default)
    {
        if (!await db.Usuarios.AnyAsync(u => u.Id == usuarioId && u.Activo, ct))
            throw new InvalidOperationException("El usuario no existe o está inactivo.");
        // Lo que decide se lee dentro de la transacción: dos cajas a la vez no la imprimen las dos.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var venta = await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).SingleOrDefaultAsync(v => v.Id == ventaId, ct)
                ?? throw new InvalidOperationException("La venta no existe.");
            if (venta.Estado != EstadoVenta.Pagada || venta.Lineas.Any(l => l.CantidadDevuelta.EsPositiva))
                throw new TicketYaSalio($"El ticket {venta.FolioTexto} se canceló o se devolvió algo de él: se reimprime, con su motivo.");
            if (!venta.TicketSinImprimir)
                throw new TicketYaSalio($"El ticket {venta.FolioTexto} ya salió: si lo vuelven a pedir, se reimprime con su motivo.");
            venta.TicketSinImprimir = false;
            venta.TicketImpresiones++;
            new ServicioControl(db).Registrar(venta.SucursalId, cajaId, usuarioId, AccionControlada.Reimprimir,
                $"Primera impresión del ticket {venta.FolioTexto}: no salió al cobrar (no es una reimpresión)", venta.TotalCentavos, venta.Id);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return venta;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// El ticket de una venta no salió y se dejó así («No imprimirlo» en la cola de la impresora de la caja): sigue sin salir y
    /// su primera impresión no pedirá motivo (<see cref="PrimeraImpresionAsync"/>). Solo si de verdad no salió otro (revisión
    /// del PR #133): si después de ese trabajo se registró otra impresión (se reimprimió, u otra caja la imprimió), si ya está
    /// marcada, o si se canceló o se devolvió algo, no cambia nada. Cuando la pone, queda en la bitácora.
    /// </summary>
    /// <returns>Si la puso.</returns>
    public async Task<bool> TicketSinImprimirAsync(Guid ventaId, SolicitudTicketSinImprimir s, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var venta = await db.Ventas.Include(v => v.Lineas).SingleOrDefaultAsync(v => v.Id == ventaId, ct)
                ?? throw new InvalidOperationException("La venta no existe.");
            if (venta.TicketSinImprimir || venta.TicketImpresiones > s.ImpresionesAntes || venta.Estado != EstadoVenta.Pagada
                || venta.Lineas.Any(l => l.CantidadDevuelta.EsPositiva))
                return false;
            venta.TicketSinImprimir = true;
            new ServicioControl(db).Registrar(venta.SucursalId, s.CajaId, s.UsuarioId, AccionControlada.Reimprimir,
                $"El ticket {venta.FolioTexto} no salió de la impresora y se dejó sin imprimir: su primera impresión no pedirá motivo", ventaId: venta.Id);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>El encargado confirma que una transferencia llegó a la cuenta, o la rechaza. Queda en la bitácora.</summary>
    public async Task<Cobro> VerificarTransferenciaAsync(Guid cobroId, string usuario, string pin, bool llego, CancellationToken ct = default)
    {
        var cobro = await db.Cobros.FindAsync([cobroId], ct) ?? throw new InvalidOperationException("El cobro no existe.");
        if (cobro.Forma != FormaPago.Transferencia)
            throw new InvalidOperationException("Solo se verifican transferencias.");
        if (cobro.Verificacion != Verificacion.PorVerificar)
            throw new InvalidOperationException("Esa transferencia ya se revisó.");
        var venta = await db.Ventas.FindAsync([cobro.VentaId], ct) ?? throw new InvalidOperationException("La venta no existe.");

        var control = new ServicioControl(db);
        var encargado = await control.IdentificarAsync(usuario, pin, venta.SucursalId, AccionControlada.VerificarTransferencia, ct);
        cobro.Verificacion = llego ? Verificacion.Verificada : Verificacion.Rechazada;
        cobro.VerificadoPorId = encargado.Id;
        cobro.VerificadoUtc = Ahora;
        control.Registrar(venta.SucursalId, null, encargado.Id, AccionControlada.VerificarTransferencia,
            $"{(llego ? "Verificó" : "Rechazó")} la transferencia del ticket {venta.FolioTexto}" + (cobro.Referencia is { } r ? $" (ref. {r})" : ""),
            cobro.MontoCentavos, venta.Id);
        await db.SaveChangesAsync(ct);
        return cobro;
    }

    /// <summary>Lo que lleva el turno hasta ahora, sin guardar un corte: la caja lo usa de base para su cuenta local.</summary>
    public async Task<Corte> ResumenAsync(Guid turnoId, CancellationToken ct = default)
    {
        var turno = await db.Turnos.FindAsync([turnoId], ct) ?? throw new InvalidOperationException("El turno no existe.");
        return CalculoCorte.Calcular(turno, await VentasAsync(turno.Id, ct), await MovimientosAsync(turno.Id, ct), TipoCorte.X, turno.CajeroId, Ahora,
            devoluciones: await DevolucionesAsync(turno.Id, ct), abonos: await AbonosAsync(turno.Id, ct));
    }

    /// <summary>El último folio usado en la serie de una caja (0 si todavía no vende).</summary>
    public async Task<int> UltimoFolioAsync(Guid sucursalId, string serie, CancellationToken ct = default) =>
        (int)(await db.Secuencias.Where(s => s.Nombre == $"folio:{sucursalId}:{serie}").Select(s => (long?)s.Valor).FirstOrDefaultAsync(ct) ?? 0);

    // ── Lo que sube con la bandeja de una caja que estuvo sin conexión ──────────
    // Cada cosa trae el ID que le dio la caja: si ya está, no se vuelve a guardar.

    public async Task<Turno> TurnoDeContingenciaAsync(TurnoContingenciaDto t, CancellationToken ct = default)
    {
        if (await db.Turnos.FindAsync([t.Id], ct) is { } existente)
            return existente;
        var caja = await db.Cajas.FindAsync([t.CajaId], ct) ?? throw new InvalidOperationException("La caja no existe.");
        if (await TurnoAbiertoAsync(t.CajaId, ct) is not null)
            throw new InvalidOperationException($"La {caja.Nombre} ya tiene otro turno abierto: hay que revisarlo.");
        var turno = new Turno
        {
            Id = t.Id, SucursalId = caja.SucursalId, CajaId = caja.Id, CajeroId = t.CajeroId, FondoCentavos = t.FondoCentavos,
            AbiertoUtc = t.AbiertoUtc, AbiertoEnContingencia = true,
        };
        db.Turnos.Add(turno);
        await db.SaveChangesAsync(ct);
        return turno;
    }

    /// <summary>
    /// Un F7 o F8 de la caja sin conexión. Como la venta, si su turno ya tiene corte Z se guarda fuera de ese corte. En
    /// el piloto también va a eleventa, como la que se hace en línea.
    /// </summary>
    public async Task<MovimientoCaja> MovimientoDeContingenciaAsync(Guid turnoId, MovimientoContingenciaDto m, CancellationToken ct = default, bool aEleventa = false)
    {
        if (await db.MovimientosCaja.FindAsync([m.Id], ct) is { } existente)
            return existente;
        var turno = await TurnoPorIdAsync(turnoId, ct);
        var movimiento = new MovimientoCaja
        {
            Id = m.Id, TurnoId = turno.Id, Tipo = m.Tipo, MontoCentavos = m.MontoCentavos, Motivo = m.Motivo.Trim(), UsuarioId = m.UsuarioId,
            FechaUtc = m.FechaUtc, EnContingencia = true, FueraDeCorteUtc = turno.Abierto ? null : Ahora,
        };
        db.MovimientosCaja.Add(movimiento);
        db.AperturasCajon.Add(new AperturaCajon { CajaId = turno.CajaId, TurnoId = turno.Id, UsuarioId = m.UsuarioId, Motivo = MotivoApertura.SinVenta, Nota = $"{m.Tipo}: {movimiento.Motivo}", FechaUtc = m.FechaUtc });
        if (!turno.Abierto)
        {
            var entrada = m.Tipo == TipoMovimientoCaja.Entrada;
            await AvisarFueraDeCorteAsync(turno, movimiento.Id,
                $"La {(entrada ? "entrada" : "salida")} de efectivo de las {HoraMexico.ALocal(m.FechaUtc):HH:mm} por {AlertasTelegram.Dinero(m.MontoCentavos)} ({movimiento.Motivo})",
                entrada ? m.MontoCentavos : -m.MontoCentavos, ct);
        }
        await EncolarParaEleventaAsync(movimiento, aEleventa, ct);
        await db.SaveChangesAsync(ct);
        return movimiento;
    }

    /// <summary>
    /// Lo de una caja sin conexión que llegó cuando su turno ya tenía corte Z: se le avisa al dueño qué fue y cuánto
    /// efectivo movió que ese corte no tomó en cuenta. Quien llama guarda.
    /// </summary>
    /// <param name="efectivoCentavos">Lo que entró al cajón (negativo, lo que salió).</param>
    internal async Task AvisarFueraDeCorteAsync(Turno turno, Guid id, string que, long efectivoCentavos, CancellationToken ct)
    {
        var caja = await db.Cajas.FindAsync([turno.CajaId], ct);
        var cajero = await db.Usuarios.FindAsync([turno.CajeroId], ct);
        var efectivo = efectivoCentavos switch
        {
            > 0 => $"Al cajón entraron {AlertasTelegram.Dinero(efectivoCentavos)} que ese corte no esperaba: pueden explicar un sobrante.",
            < 0 => $"Del cajón salieron {AlertasTelegram.Dinero(-efectivoCentavos)} que ese corte no restó: pueden explicar un faltante.",
            _ => "No movió efectivo: el conteo de ese corte no cambia.",
        };
        AlertasTelegram.Avisar(db, turno.SucursalId, TipoAlerta.FueraDeCorte,
            $"{AlertasTelegram.Titulo(TipoAlerta.FueraDeCorte)} · {caja?.Nombre}\n"
            + $"{que} se hizo sin conexión en el turno de {cajero?.Nombre} y llegó cuando ese turno ya tenía su corte Z "
            + $"(de las {HoraMexico.ALocal(turno.CerradoUtc!.Value):HH:mm}). Se guardó con su turno; el corte no se cambió.\n{efectivo}",
            $"fuera-de-corte:{id}");
    }

    /// <summary>
    /// El corte Z que hizo la caja sin conexión. Se recalcula con lo que ya llegó (sus ventas suben antes que el corte)
    /// y con lo que se contó; si no cuadra con el ticket que imprimió la caja, aquí se ve. Una caja de antes del
    /// 2026-10-14 manda el conteo billete por billete: se acepta (nada se atora en la bandeja) y se guarda su total.
    /// </summary>
    /// <remarks>Como el corte Z en línea, dentro de una transacción desde antes de leer el turno (<see cref="CorteZAsync"/>).</remarks>
    public async Task<Corte> CorteZDeContingenciaAsync(Guid turnoId, CorteContingenciaDto c, CancellationToken ct = default)
    {
        await using var tx = await TransaccionPropiaAsync(ct);
        try
        {
            if (await db.Cortes.FindAsync([c.Id], ct) is { } existente)
                return existente;
            var turno = await TurnoAbiertoAlDiaAsync(turnoId, ct);
            var contado = c.ContadoCentavos
                ?? (c.Conteo is { } conteo ? Denominaciones.Total(conteo) : throw new ArgumentException("El corte sin conexión no trae lo que se contó."));
            var corte = CalculoCorte.Calcular(turno, await VentasAsync(turno.Id, ct), await MovimientosAsync(turno.Id, ct), TipoCorte.Z, c.RealizadoPorId,
                c.FechaUtc, contado, await DevolucionesAsync(turno.Id, ct), await AbonosAsync(turno.Id, ct));
            corte.Id = c.Id;
            corte.EnContingencia = true;
            turno.CerradoUtc = c.FechaUtc;
            db.Bitacora.Add(new Bitacora
            {
                SucursalId = turno.SucursalId, CajaId = turno.CajaId, UsuarioId = c.RealizadoPorId, Accion = AccionControlada.CorteZ,
                Detalle = $"Corte del turno de {await NombreAsync(turno.CajeroId, ct)} sin conexión: esperado {corte.EsperadoCentavos / 100m:N2}, "
                    + $"contado {corte.ContadoCentavos / 100m:N2}",
                MontoCentavos = corte.DiferenciaCentavos, Resultado = ResultadoBitacora.Hecho, FechaUtc = c.FechaUtc, EnContingencia = true,
            });
            db.Cortes.Add(corte);
            await CerrarEnEleventaAsync(turno, ct);
            await db.SaveChangesAsync(ct);
            // Si era el último turno abierto de la sucursal, sale el resumen del día por Telegram.
            await AlertasTelegram.ResumenSiEsElUltimoAsync(db, corte, turno.SucursalId, ct);
            await db.SaveChangesAsync(ct);
            if (tx is not null)
                await tx.CommitAsync(ct);
            return corte;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>La caja avisa el último folio que usó sin conexión: aunque una venta no haya subido, ese folio no se repite.</summary>
    public async Task FolioUsadoAsync(Guid cajaId, int folio, CancellationToken ct = default)
    {
        var caja = await db.Cajas.FindAsync([cajaId], ct) ?? throw new InvalidOperationException("La caja no existe.");
        await AlcanzarFolioAsync(caja.SucursalId, caja.Serie, folio, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Si la caja usó folios sin conexión, la serie sigue después del más alto.</summary>
    private async Task AlcanzarFolioAsync(Guid sucursalId, string serie, int folio, CancellationToken ct)
    {
        var nombre = $"folio:{sucursalId}:{serie}";
        var secuencia = await db.Secuencias.FindAsync([nombre], ct);
        if (secuencia is null)
        {
            secuencia = new Secuencia { Nombre = nombre };
            db.Secuencias.Add(secuencia);
        }
        secuencia.Valor = Math.Max(secuencia.Valor, folio);
    }

    private async Task<string> NombreAsync(Guid usuarioId, CancellationToken ct) =>
        await db.Usuarios.Where(u => u.Id == usuarioId).Select(u => u.Nombre).FirstOrDefaultAsync(ct) ?? "—";

    private async Task<Turno> TurnoPorIdAsync(Guid turnoId, CancellationToken ct) =>
        await db.Turnos.FindAsync([turnoId], ct) ?? throw new InvalidOperationException("El turno no existe.");

    private async Task<Turno> TurnoAbiertoPorIdAsync(Guid turnoId, CancellationToken ct)
    {
        var turno = await TurnoPorIdAsync(turnoId, ct);
        // Lo pudo cerrar otra caja o, en el piloto con eleventa, el corte de eleventa: se entra otra vez y se abre otro.
        return turno.Abierto ? turno : throw new InvalidOperationException("El turno ya está cerrado: cambia de usuario (Ctrl+U) para abrir otro.");
    }

    private async Task<int> SiguienteFolioAsync(Guid sucursalId, string serie, CancellationToken ct)
    {
        var nombre = $"folio:{sucursalId}:{serie}";
        var secuencia = await db.Secuencias.FindAsync([nombre], ct);
        if (secuencia is null)
        {
            secuencia = new Secuencia { Nombre = nombre };
            db.Secuencias.Add(secuencia);
        }
        secuencia.Valor++;
        return checked((int)secuencia.Valor);
    }

    /// <summary>El último costo de cada variante (el de <see cref="UltimoCostoAsync"/>), en una sola lectura.</summary>
    private Task<Dictionary<Guid, long>> UltimosCostosAsync(IReadOnlyCollection<Guid> variantes, CancellationToken ct) =>
        UltimosCostos.DeAsync(db, variantes, ct);

    /// <summary>
    /// El último costo de la variante (<see cref="UltimosCostos"/>: el de un proveedor o el de lo último agregado con «Agregar
    /// inventario», el más reciente); 0 si no tiene ninguno. Con él sale lo que no tiene existencia.
    /// </summary>
    internal async Task<long> UltimoCostoAsync(Guid varianteId, CancellationToken ct) => await UltimosCostos.DeAsync(db, varianteId, ct) ?? 0;

    private async Task<List<Venta>> VentasAsync(Guid turnoId, CancellationToken ct) =>
        await db.Ventas.Include(v => v.Cobros).Where(v => v.TurnoId == turnoId).ToListAsync(ct);

    private async Task<List<Devolucion>> DevolucionesAsync(Guid turnoId, CancellationToken ct) =>
        await db.Devoluciones.Where(d => d.TurnoId == turnoId).ToListAsync(ct);

    private async Task<List<MovimientoCaja>> MovimientosAsync(Guid turnoId, CancellationToken ct) =>
        await db.MovimientosCaja.Where(m => m.TurnoId == turnoId).ToListAsync(ct);

    /// <summary>Los abonos de clientes recibidos en el turno (el corte deja fuera los cancelados).</summary>
    private async Task<List<Abono>> AbonosAsync(Guid turnoId, CancellationToken ct) =>
        await db.Abonos.Where(a => a.TurnoId == turnoId).ToListAsync(ct);
}
