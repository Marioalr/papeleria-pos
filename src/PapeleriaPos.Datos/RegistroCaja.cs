using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Impresiones;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

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
    public async Task<MovimientoCaja> RegistrarMovimientoAsync(
        Guid turnoId, TipoMovimientoCaja tipo, long montoCentavos, string motivo, Guid usuarioId, Guid? id = null, CancellationToken ct = default)
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
        await db.SaveChangesAsync(ct);
        return movimiento;
    }

    /// <summary>
    /// F12: asigna el folio de la serie, aplica los cobros, descuenta del inventario lo vendido (guardando su costo en
    /// cada línea) y registra la apertura del cajón si hubo efectivo.
    /// </summary>
    /// <param name="impresiones">Los trabajos que van en la línea "Impresiones": quedan cobrados con la venta.</param>
    public Task<Venta> CobrarAsync(
        Venta venta, IReadOnlyList<CobroSolicitado> cobros, CancellationToken ct = default, IReadOnlyList<TrabajoImpresion>? impresiones = null) =>
        GuardarCobroAsync(venta, cobros, null, Ahora, ct, impresiones);

    /// <summary>
    /// Una venta que cobró una caja sin conexión y llega con la bandeja: trae su folio y su hora. Las reglas ya se
    /// revisaron en la caja (su bitácora sube aparte), así que aquí no se vuelven a pedir autorizaciones.
    /// </summary>
    /// <param name="redondear">Falso para lo que la caja cobró antes de que existiera el redondeo al peso: se cobra exacto.</param>
    public Task<Venta> CobroDeContingenciaAsync(
        Venta venta, IReadOnlyList<CobroSolicitado> cobros, int folio, DateTime pagadaUtc, CancellationToken ct = default, bool redondear = true) =>
        GuardarCobroAsync(venta, cobros, folio, pagadaUtc, ct, redondear: redondear);

    private async Task<Venta> GuardarCobroAsync(
        Venta venta, IReadOnlyList<CobroSolicitado> cobros, int? folioDeLaCaja, DateTime ahora, CancellationToken ct, IReadOnlyList<TrabajoImpresion>? impresiones = null,
        bool redondear = true)
    {
        var turno = await TurnoAbiertoPorIdAsync(venta.TurnoId, ct);
        if (turno.CajaId != venta.CajaId || turno.SucursalId != venta.SucursalId)
            throw new InvalidOperationException("La venta no es del turno abierto de esta caja.");

        venta.ValidarCobros(cobros, redondear);

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
                folio = await SiguienteFolioAsync(venta.SucursalId, venta.Serie, ct);
            }

            var inventario = new RegistroInventario(db);
            foreach (var linea in venta.Lineas.Where(l => l.LlevaInventario && l.VarianteId is not null))
            {
                var variante = await db.Variantes.FindAsync([linea.VarianteId!.Value], ct)
                    ?? throw new InvalidOperationException($"«{linea.Descripcion}» ya no está en el catálogo.");
                var existencia = await inventario.ExistenciaAsync(venta.SucursalId, variante.Id, ct);
                var salida = Kardex.Salida(venta.SucursalId, variante, TipoMovimiento.Venta, linea.CantidadBase, existencia,
                    await UltimoCostoAsync(variante.Id, ct), ahora, documentoTipo: nameof(Venta), documentoId: venta.Id);
                linea.CostoCentavos = -salida.CostoCentavos;
                await inventario.AgregarAsync(salida, ct);
            }

            venta.Pagar(folio, cobros, ahora, redondear);
            foreach (var trabajo in impresiones ?? [])
                trabajo.Cobrar(venta.Id, ahora);
            foreach (var comun in venta.Lineas.Where(l => l.VarianteId is null && !l.EsImpresiones))
                control.Registrar(venta.SucursalId, venta.CajaId, venta.CajeroId, AccionControlada.ArticuloComun,
                    $"{comun.Descripcion} × {comun.Cantidad}", comun.ImporteCentavos, venta.Id);
            db.Ventas.Add(venta);
            if (venta.LlevaEfectivo)
                db.AperturasCajon.Add(new AperturaCajon { CajaId = venta.CajaId, TurnoId = turno.Id, UsuarioId = venta.CajeroId, Motivo = MotivoApertura.Venta, VentaId = venta.Id, FechaUtc = ahora });
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

    /// <summary>Corte parcial: informativo, el turno sigue abierto.</summary>
    public async Task<Corte> CorteXAsync(Guid turnoId, Guid usuarioId, CancellationToken ct = default)
    {
        var turno = await TurnoAbiertoPorIdAsync(turnoId, ct);
        var corte = CalculoCorte.Calcular(turno, await VentasAsync(turno.Id, ct), await MovimientosAsync(turno.Id, ct), TipoCorte.X, usuarioId, Ahora,
            devoluciones: await DevolucionesAsync(turno.Id, ct));
        db.Cortes.Add(corte);
        await db.SaveChangesAsync(ct);
        return corte;
    }

    /// <summary>Corte de cierre: lo hace el encargado contando a ciegas; el turno se cierra.</summary>
    public async Task<Corte> CorteZAsync(Guid turnoId, Guid encargadoId, IReadOnlyDictionary<long, int> conteo, CancellationToken ct = default)
    {
        var turno = await TurnoAbiertoPorIdAsync(turnoId, ct);
        var decision = await new ServicioControl(db).EvaluarAsync(encargadoId, turno.SucursalId, AccionControlada.CorteZ, ct: ct);
        if (decision.Nivel != NivelPermiso.Permitido)
            throw new InvalidOperationException("El corte de cierre lo hace el encargado.");

        var ahora = Ahora;
        var corte = CalculoCorte.Calcular(turno, await VentasAsync(turno.Id, ct), await MovimientosAsync(turno.Id, ct), TipoCorte.Z, encargadoId, ahora, conteo,
            await DevolucionesAsync(turno.Id, ct));
        turno.CerradoUtc = ahora;
        new ServicioControl(db).Registrar(turno.SucursalId, turno.CajaId, encargadoId, AccionControlada.CorteZ,
            $"Corte de cierre: esperado {corte.EsperadoCentavos / 100m:N2}, contado {corte.ContadoCentavos / 100m:N2}", corte.DiferenciaCentavos);
        db.Cortes.Add(corte);
        await db.SaveChangesAsync(ct);
        // Si era el último turno abierto de la sucursal, sale el resumen del día por Telegram.
        await AlertasTelegram.ResumenSiEsElUltimoAsync(db, corte, turno.SucursalId, ct);
        await db.SaveChangesAsync(ct);
        return corte;
    }

    /// <summary>Lo que debería haber en efectivo en la caja ahora, y si ya pasó del límite de la sucursal.</summary>
    public async Task<(long Esperado, long? Limite)> EfectivoAsync(Guid turnoId, CancellationToken ct = default)
    {
        var turno = await TurnoAbiertoPorIdAsync(turnoId, ct);
        var corte = CalculoCorte.Calcular(turno, await VentasAsync(turno.Id, ct), await MovimientosAsync(turno.Id, ct), TipoCorte.X, turno.CajeroId, Ahora,
            devoluciones: await DevolucionesAsync(turno.Id, ct));
        var limite = await db.ConfiguracionesSucursal.Where(c => c.SucursalId == turno.SucursalId).Select(c => c.LimiteEfectivoCentavos).FirstOrDefaultAsync(ct);
        return (corte.EsperadoCentavos, limite);
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
            devoluciones: await DevolucionesAsync(turno.Id, ct));
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

    public async Task<MovimientoCaja> MovimientoDeContingenciaAsync(Guid turnoId, MovimientoContingenciaDto m, CancellationToken ct = default)
    {
        if (await db.MovimientosCaja.FindAsync([m.Id], ct) is { } existente)
            return existente;
        var turno = await TurnoAbiertoPorIdAsync(turnoId, ct);
        var movimiento = new MovimientoCaja
        {
            Id = m.Id, TurnoId = turno.Id, Tipo = m.Tipo, MontoCentavos = m.MontoCentavos, Motivo = m.Motivo.Trim(), UsuarioId = m.UsuarioId,
            FechaUtc = m.FechaUtc, EnContingencia = true,
        };
        db.MovimientosCaja.Add(movimiento);
        db.AperturasCajon.Add(new AperturaCajon { CajaId = turno.CajaId, TurnoId = turno.Id, UsuarioId = m.UsuarioId, Motivo = MotivoApertura.SinVenta, Nota = $"{m.Tipo}: {movimiento.Motivo}", FechaUtc = m.FechaUtc });
        await db.SaveChangesAsync(ct);
        return movimiento;
    }

    /// <summary>
    /// El corte Z que hizo la caja sin conexión. Se recalcula con lo que ya llegó (sus ventas suben antes que el corte)
    /// y con el conteo del encargado; si no cuadra con el ticket que imprimió la caja, aquí se ve.
    /// </summary>
    public async Task<Corte> CorteZDeContingenciaAsync(Guid turnoId, CorteContingenciaDto c, CancellationToken ct = default)
    {
        if (await db.Cortes.FindAsync([c.Id], ct) is { } existente)
            return existente;
        var turno = await TurnoAbiertoPorIdAsync(turnoId, ct);
        var corte = CalculoCorte.Calcular(turno, await VentasAsync(turno.Id, ct), await MovimientosAsync(turno.Id, ct), TipoCorte.Z, c.EncargadoId, c.FechaUtc,
            c.Conteo, await DevolucionesAsync(turno.Id, ct));
        corte.Id = c.Id;
        corte.EnContingencia = true;
        foreach (var conteo in corte.Conteo)
            conteo.CorteId = corte.Id;
        turno.CerradoUtc = c.FechaUtc;
        db.Bitacora.Add(new Bitacora
        {
            SucursalId = turno.SucursalId, CajaId = turno.CajaId, UsuarioId = c.EncargadoId, Accion = AccionControlada.CorteZ,
            Detalle = $"Corte de cierre sin conexión: esperado {corte.EsperadoCentavos / 100m:N2}, contado {corte.ContadoCentavos / 100m:N2}",
            MontoCentavos = corte.DiferenciaCentavos, Resultado = ResultadoBitacora.Hecho, FechaUtc = c.FechaUtc, EnContingencia = true,
        });
        db.Cortes.Add(corte);
        await db.SaveChangesAsync(ct);
        // Si era el último turno abierto de la sucursal, sale el resumen del día por Telegram.
        await AlertasTelegram.ResumenSiEsElUltimoAsync(db, corte, turno.SucursalId, ct);
        await db.SaveChangesAsync(ct);
        return corte;
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

    private async Task<Turno> TurnoAbiertoPorIdAsync(Guid turnoId, CancellationToken ct)
    {
        var turno = await db.Turnos.FindAsync([turnoId], ct) ?? throw new InvalidOperationException("El turno no existe.");
        return turno.Abierto ? turno : throw new InvalidOperationException("El turno ya está cerrado.");
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

    internal async Task<long> UltimoCostoAsync(Guid varianteId, CancellationToken ct) =>
        await db.ProveedoresVariantes
            .Where(pv => pv.VarianteId == varianteId && pv.UltimoCostoCentavos != null)
            .OrderByDescending(pv => pv.UltimaCompraUtc)
            .Select(pv => pv.UltimoCostoCentavos!.Value)
            .FirstOrDefaultAsync(ct);

    private async Task<List<Venta>> VentasAsync(Guid turnoId, CancellationToken ct) =>
        await db.Ventas.Include(v => v.Cobros).Where(v => v.TurnoId == turnoId).ToListAsync(ct);

    private async Task<List<Devolucion>> DevolucionesAsync(Guid turnoId, CancellationToken ct) =>
        await db.Devoluciones.Where(d => d.TurnoId == turnoId).ToListAsync(ct);

    private async Task<List<MovimientoCaja>> MovimientosAsync(Guid turnoId, CancellationToken ct) =>
        await db.MovimientosCaja.Where(m => m.TurnoId == turnoId).ToListAsync(ct);
}
