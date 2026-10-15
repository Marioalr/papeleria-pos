using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Impresiones;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos;

/// <summary>
/// Lo que se le avisa al dueño por Telegram (docs/fase-1-telegram.md). Casi todo sale de la bitácora: al guardar un
/// registro que interesa (una cancelación, un corte con diferencia…), en la misma transacción queda el mensaje en la
/// cola. Así también avisan las cosas que llegan después con la bandeja de una caja que estuvo sin conexión.
/// </summary>
public static class AlertasTelegram
{
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    public static string Dinero(long centavos) => (centavos / 100m).ToString("$#,##0.00", Mexico);

    /// <summary>Lo llama <see cref="PosDbContext"/> antes de guardar.</summary>
    internal static void RevisarBitacora(PosDbContext db)
    {
        var nuevas = db.ChangeTracker.Entries<Bitacora>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        if (nuevas.Count == 0)
            return;
        var chats = Chats(db);
        if (chats.Count == 0)
            return;
        foreach (var b in nuevas)
        {
            var configuracion = Configuracion(db, b.SucursalId);
            if (Tipo(b, configuracion) is not { } tipo || !configuracion.Avisa(tipo))
                continue;
            // Lo que cambia el dueño en el catálogo, el inventario o los clientes no se le avisa a él mismo.
            if (tipo is TipoAlerta.CambioPrecioCatalogo or TipoAlerta.AjusteInventario or TipoAlerta.DiferenciaConteo or TipoAlerta.ConteoCompleto
                    or TipoAlerta.PrivilegiosCliente or TipoAlerta.Credito
                && b.UsuarioId is { } quien
                && db.Usuarios.Find(quien)?.Rol == Rol.Dueno)
                continue;
            Agregar(db, chats, tipo, Texto(db, b, tipo), $"bitacora:{b.Id}");
        }
    }

    /// <summary>Una alerta que no sale de la bitácora (caja sin conexión, fuera de horario…). Quien llama guarda.</summary>
    public static bool Avisar(PosDbContext db, Guid sucursalId, TipoAlerta tipo, string texto, string? clave = null)
    {
        if (!Configuracion(db, sucursalId).Avisa(tipo))
            return false;
        return Agregar(db, Chats(db), tipo, texto, clave);
    }

    /// <summary>Un mensaje a todos los chats vinculados (el resumen, una respuesta del bot…). Quien llama guarda.</summary>
    public static bool Agregar(PosDbContext db, IReadOnlyList<long> chats, TipoAlerta? tipo, string texto, string? clave = null, Guid? solicitudId = null)
    {
        if (chats.Count == 0)
            return false;
        if (clave is not null && (db.ChangeTracker.Entries<MensajeTelegram>().Any(e => e.Entity.Clave == clave) || db.MensajesTelegram.Any(m => m.Clave == clave)))
            return false;
        var ahora = db.Reloj.GetUtcNow().UtcDateTime;
        foreach (var chat in chats)
            db.MensajesTelegram.Add(new MensajeTelegram { ChatId = chat, Texto = texto, Tipo = tipo, Clave = clave, SolicitudId = solicitudId, CreadoUtc = ahora });
        return true;
    }

    /// <summary>Los chats del dueño que están vinculados.</summary>
    public static List<long> Chats(PosDbContext db) =>
        db.ChatsTelegram.AsNoTracking().Where(c => c.Activo).Select(c => c.ChatId).Distinct().ToList();

    public static ConfiguracionSucursal Configuracion(PosDbContext db, Guid sucursalId) =>
        db.ConfiguracionesSucursal.Find(sucursalId) ?? new ConfiguracionSucursal { SucursalId = sucursalId };

    private static TipoAlerta? Tipo(Bitacora b, ConfiguracionSucursal c)
    {
        if (b.Resultado == ResultadoBitacora.Negado)
            // Lo que el mismo dueño rechazó desde Telegram no se le vuelve a avisar.
            return b.Detalle.StartsWith("Rechazada por Telegram", StringComparison.Ordinal) ? null : TipoAlerta.AutorizacionNegada;
        return b.Accion switch
        {
            AccionControlada.CorteZ when Math.Abs(b.MontoCentavos ?? 0) > c.UmbralCorteCentavos => TipoAlerta.DiferenciaCorte,
            AccionControlada.CancelarTicket => TipoAlerta.Cancelacion,
            AccionControlada.Devolucion when b.MontoCentavos > c.UmbralDevolucionCentavos => TipoAlerta.Devolucion,
            AccionControlada.CambiarPrecio => TipoAlerta.CambioPrecio,
            AccionControlada.VenderBajoCosto => TipoAlerta.VentaBajoCosto,
            AccionControlada.Descuento when b.Resultado == ResultadoBitacora.Autorizado => TipoAlerta.Descuento,
            // RegistroCatalogo escribe "Precio de «…»: antes → después" por cada precio que cambia.
            AccionControlada.EditarCatalogo when b.Detalle.StartsWith("Precio de", StringComparison.Ordinal) => TipoAlerta.CambioPrecioCatalogo,
            AccionControlada.SalidaEfectivo when b.MontoCentavos > c.UmbralSalidaCentavos => TipoAlerta.SalidaEfectivo,
            AccionControlada.AjustarInventario or AccionControlada.Merma when b.MontoCentavos >= c.UmbralAjusteCentavos => TipoAlerta.AjusteInventario,
            AccionControlada.ContarInventario when b.MontoCentavos >= c.UmbralAjusteCentavos => TipoAlerta.DiferenciaConteo,
            // RegistroConteoCompleto.CerrarAsync escribe "Cerró el conteo de…"; abrir y cancelar no se avisan.
            AccionControlada.ConteoCompleto when b.Detalle.StartsWith("Cerró", StringComparison.Ordinal) => TipoAlerta.ConteoCompleto,
            // RegistroClientes y RegistroCredito escriben "Privilegios de «…»" cuando se dan o cambian (también el crédito).
            AccionControlada.AdministrarClientes or AccionControlada.DarCredito when b.Detalle.StartsWith("Privilegios de", StringComparison.Ordinal)
                => TipoAlerta.PrivilegiosCliente,
            AccionControlada.ExcederLimiteCredito or AccionControlada.AjustarCredito or AccionControlada.DevolverSaldoAFavor => TipoAlerta.Credito,
            AccionControlada.CancelarAbono => TipoAlerta.Cancelacion,
            // RegistroCaja.VerificarTransferenciaAsync escribe "Verificó…" o "Rechazó…".
            AccionControlada.VerificarTransferencia when b.Detalle.StartsWith("Rechazó", StringComparison.Ordinal) => TipoAlerta.TransferenciaRechazada,
            _ => null,
        };
    }

    public static string Titulo(TipoAlerta tipo) => tipo switch
    {
        TipoAlerta.DiferenciaCorte => "Diferencia en el corte",
        TipoAlerta.Cancelacion => "Cancelación",
        TipoAlerta.Devolucion => "Devolución",
        TipoAlerta.CambioPrecio => "Cambio de precio",
        TipoAlerta.VentaBajoCosto => "Venta debajo del costo",
        TipoAlerta.SalidaEfectivo => "Salida de efectivo",
        TipoAlerta.AutorizacionNegada => "Autorización negada",
        TipoAlerta.TransferenciaRechazada => "Transferencia rechazada",
        TipoAlerta.CajaSinConexion => "Caja sin conexión",
        TipoAlerta.FueraDeHorario => "Caja abierta fuera de horario",
        TipoAlerta.Impresora => "Impresora",
        TipoAlerta.HojasFaltantes => "Hojas que no salieron",
        TipoAlerta.Descuento => "Descuento",
        TipoAlerta.CambioPrecioCatalogo => "Precio del catálogo",
        TipoAlerta.Compra => "Compra con diferencias o margen bajo",
        TipoAlerta.AjusteInventario => "Ajuste o merma de inventario",
        TipoAlerta.DiferenciaConteo => "Diferencia en el conteo del día",
        TipoAlerta.ConteoCompleto => "Conteo completo de un departamento",
        TipoAlerta.PrivilegiosCliente => "Privilegios de un cliente",
        TipoAlerta.Credito => "Crédito de un cliente",
        TipoAlerta.FueraDeCorte => "Llegó después del corte",
        TipoAlerta.CreditoVencido => "Crédito vencido",
        TipoAlerta.BaseDanada => "La base no pasó la revisión",
        _ => "Resumen del día",
    };

    private static string Texto(PosDbContext db, Bitacora b, TipoAlerta tipo)
    {
        var caja = b.CajaId is { } c ? db.Cajas.Find(c)?.Nombre : null;
        var usuario = b.UsuarioId is { } u ? db.Usuarios.Find(u)?.Nombre : null;
        var autorizo = b.AutorizoId is { } a ? db.Usuarios.Find(a)?.Nombre : null;
        var texto = new StringBuilder(Titulo(tipo));
        if (caja is not null)
            texto.Append($" · {caja}");
        texto.AppendLine();
        texto.AppendLine(usuario is null ? b.Detalle : $"{usuario}: {b.Detalle}");
        if (b.MontoCentavos is { } monto)
            texto.AppendLine(tipo switch
            {
                TipoAlerta.DiferenciaCorte => $"{(monto < 0 ? "Faltante" : "Sobrante")}: {Dinero(Math.Abs(monto))}",
                TipoAlerta.ConteoCompleto => monto == 0 ? "Cuadra a costo" : $"{(monto < 0 ? "Faltó" : "Sobró")} a costo: {Dinero(Math.Abs(monto))}",
                _ => Dinero(monto),
            });
        if (autorizo is not null)
            texto.AppendLine(tipo == TipoAlerta.AutorizacionNegada ? $"Intentó autorizar: {autorizo}" : $"Autorizó: {autorizo}");
        if (b.EnContingencia)
            texto.AppendLine("(pasó en la caja sin conexión; llegó al volver)");
        return texto.ToString().TrimEnd();
    }

    /// <summary>Lo vencido se avisa si venció en estos últimos días; lo de antes lo dice el resumen del día.</summary>
    public const int DiasParaAvisarVencido = 7;

    /// <summary>
    /// Lo que se fió (o lo que debía de la libreta) y pasó su plazo sin pagarse: un aviso por cargo, en el horario de su
    /// sucursal (lo que vence en la noche se avisa al abrir). Solo lo que venció en los últimos <see cref="DiasParaAvisarVencido"/>
    /// días: al vincular Telegram o al actualizar no llega de golpe lo de hace meses. Quien llama guarda.
    /// </summary>
    public static async Task RevisarVencidosAsync(PosDbContext db, CancellationToken ct = default)
    {
        // Sin chat vinculado no hay a quién avisar (al vincularlo, llega lo de la última semana).
        if (Chats(db).Count == 0)
            return;
        var local = HoraMexico.ALocal(db.Reloj.GetUtcNow().UtcDateTime);
        var hoy = DateOnly.FromDateTime(local);
        var desde = hoy.AddDays(-DiasParaAvisarVencido);
        // Los que podrían estar vencidos sin avisar (lo que sube la cuenta y venció en esos días): de esos se arma su cuenta.
        var candidatos = await db.MovimientosCredito.AsNoTracking()
            .Where(m => m.MontoCentavos > 0 && m.Vence >= desde && m.Vence < hoy)
            .Select(m => new { m.Id, m.ClienteId }).ToListAsync(ct);
        if (candidatos.Count == 0)
            return;
        var claves = candidatos.Select(c => ClaveVencido(c.Id)).ToList();
        var avisados = (await db.MensajesTelegram.AsNoTracking().Where(m => m.Clave != null && claves.Contains(m.Clave))
            .Select(m => m.Clave!).ToListAsync(ct)).ToHashSet();
        var clientes = candidatos.Where(c => !avisados.Contains(ClaveVencido(c.Id))).Select(c => c.ClienteId).Distinct().ToList();
        if (clientes.Count == 0)
            return;
        var movimientos = (await db.MovimientosCredito.AsNoTracking().Where(m => clientes.Contains(m.ClienteId)).ToListAsync(ct)).ToLookup(m => m.ClienteId);
        var registrados = await db.ClientesRegistrados.AsNoTracking().Where(c => clientes.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var ventas = movimientos.SelectMany(g => g).Where(m => m.VentaId is not null).Select(m => m.VentaId!.Value).Distinct().ToList();
        var folios = await db.Ventas.AsNoTracking().Where(v => ventas.Contains(v.Id)).Select(v => new { v.Id, v.Serie, v.Folio })
            .ToDictionaryAsync(v => v.Id, v => $"{v.Serie}-{v.Folio:000000}", ct);
        foreach (var clienteId in clientes)
        {
            var lista = movimientos[clienteId].ToList();
            var porId = lista.ToDictionary(m => m.Id);
            var cuenta = CuentaCredito.Aplicar(lista);
            foreach (var cargo in cuenta.Cargos.Where(c => c.PendienteCentavos > 0 && c.Vence >= desde && c.Vence < hoy))
            {
                var m = porId[cargo.MovimientoId];
                if (avisados.Contains(ClaveVencido(m.Id)) || Configuracion(db, m.SucursalId).FueraDeHorario(local))
                    continue;
                var cliente = registrados[clienteId];
                // De lo que se devolvió de esa venta no se debe nada: «faltan $300» de una compra de $500 con $200 devueltos.
                var total = cargo.MontoCentavos - cargo.DevueltoCentavos;
                var que = m.Tipo switch
                {
                    TipoMovimientoCredito.Cargo => $"Compra a crédito{(m.VentaId is { } venta && folios.TryGetValue(venta, out var folio) ? $" {folio}" : "")}",
                    TipoMovimientoCredito.Ajuste => $"Lo que debía ({m.Motivo})",
                    _ => "Lo que quedó a deber",
                };
                Avisar(db, m.SucursalId, TipoAlerta.CreditoVencido,
                    $"{Titulo(TipoAlerta.CreditoVencido)} · «{cliente.Nombre}»\n"
                    + $"{que} del {HoraMexico.ALocal(m.FechaUtc).ToString("dd/MM/yyyy", Mexico)}: faltan {Dinero(cargo.PendienteCentavos)}"
                    + (cargo.PendienteCentavos < total ? $" de {Dinero(total)}" : "")
                    + $" y vencía el {cargo.Vence.ToString("dd/MM/yyyy", Mexico)}.\n"
                    + $"Debe en total {Dinero(cuenta.SaldoCentavos)}, vencido {Dinero(cuenta.Vencido(hoy))}."
                    + (cliente.Telefono is { Length: > 0 } telefono ? $" Tel. {telefono}" : ""),
                    ClaveVencido(m.Id));
            }
        }
    }

    private static string ClaveVencido(Guid cargoId) => $"vencido:{cargoId}";

    /// <summary>Después de un corte Z: si ya no queda turno abierto en la sucursal, sale el resumen del día. Quien llama guarda.</summary>
    public static async Task ResumenSiEsElUltimoAsync(PosDbContext db, Corte corte, Guid sucursalId, CancellationToken ct = default)
    {
        if (await db.Turnos.AnyAsync(t => t.SucursalId == sucursalId && t.CerradoUtc == null && t.Id != corte.TurnoId, ct))
            return;
        if (!Configuracion(db, sucursalId).Avisa(TipoAlerta.ResumenDiario))
            return;
        var texto = await ResumenAsync(db, sucursalId, DateOnly.FromDateTime(HoraMexico.ALocal(corte.FechaUtc)), ct);
        Agregar(db, Chats(db), TipoAlerta.ResumenDiario, texto, ClaveResumen(corte.Id));
    }

    /// <summary>
    /// El resumen del día al cerrarlo (corte del día, entrega 6). Es el mismo que sale solo con el último corte Z, con la
    /// misma clave: si ya salió con alguno de los cortes Z del día (<paramref name="cortesZ"/>), no se manda dos veces. No
    /// basta con ver el último por la hora: el de una caja que cortó sin conexión sube después con la hora de entonces, y
    /// el resumen sale con él al subir. Un día sin cortes tiene su propia clave. Quien llama guarda.
    /// </summary>
    /// <param name="cortesZ">Los cortes Z de los turnos del día, del primero al último por su hora.</param>
    public static async Task<EnvioResumen> ResumenAlCerrarElDiaAsync(
        PosDbContext db, Guid sucursalId, DateOnly fecha, IReadOnlyList<Guid> cortesZ, CancellationToken ct = default)
    {
        if (!Configuracion(db, sucursalId).Avisa(TipoAlerta.ResumenDiario))
            return EnvioResumen.Apagado;
        var chats = Chats(db);
        if (chats.Count == 0)
            return EnvioResumen.SinTelegram;
        var delDia = $"resumen:{sucursalId:N}:{fecha:yyyyMMdd}";
        var claves = cortesZ.Select(ClaveResumen).Append(delDia).ToList();
        if (await db.MensajesTelegram.AnyAsync(m => m.Clave != null && claves.Contains(m.Clave), ct))
            return EnvioResumen.YaHabiaSalido;
        Agregar(db, chats, TipoAlerta.ResumenDiario, await ResumenAsync(db, sucursalId, fecha, ct), cortesZ.Count > 0 ? ClaveResumen(cortesZ[^1]) : delDia);
        return EnvioResumen.Enviado;
    }

    private static string ClaveResumen(Guid corteId) => $"resumen:{corteId}";

    /// <summary>
    /// El umbral de la sucursal si la diferencia de un corte pasa de él y el aviso sale por Telegram (está prendido y hay
    /// un chat vinculado); vacío si no. Para que la caja lo diga al cerrar el turno.
    /// </summary>
    public static long? UmbralSiAvisa(PosDbContext db, Guid sucursalId, long diferenciaCentavos)
    {
        var configuracion = Configuracion(db, sucursalId);
        return Math.Abs(diferenciaCentavos) > configuracion.UmbralCorteCentavos && configuracion.Avisa(TipoAlerta.DiferenciaCorte) && Chats(db).Count > 0
            ? configuracion.UmbralCorteCentavos
            : null;
    }

    /// <summary>Ventas, utilidad, cortes y lo que pasó en el día en una sucursal.</summary>
    public static async Task<string> ResumenAsync(PosDbContext db, Guid sucursalId, DateOnly fecha, CancellationToken ct = default)
    {
        var desde = HoraMexico.AUtc(fecha.ToDateTime(TimeOnly.MinValue));
        var hasta = HoraMexico.AUtc(fecha.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var sucursal = await db.Sucursales.FindAsync([sucursalId], ct);
        var cajas = await db.Cajas.Where(c => c.SucursalId == sucursalId).ToDictionaryAsync(c => c.Id, c => c.Nombre, ct);
        var usuarios = await db.Usuarios.ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);

        var ventas = await db.Ventas.AsNoTracking().Include(v => v.Lineas).Include(v => v.Cobros)
            .Where(v => v.SucursalId == sucursalId && v.PagadaUtc >= desde && v.PagadaUtc < hasta).AsSplitQuery().ToListAsync(ct);
        var vigentes = ventas.Where(v => v.Estado == EstadoVenta.Pagada).ToList();
        var deCatalogo = vigentes.SelectMany(v => v.Lineas).Where(l => l.VarianteId != null || l.EsImpresiones).ToList();
        var utilidad = deCatalogo.Sum(l => l.ImporteCentavos - l.CostoCentavos);

        var turnos = await db.Turnos.AsNoTracking().Where(t => t.SucursalId == sucursalId).Select(t => new { t.Id, t.CajaId, t.CajeroId }).ToListAsync(ct);
        var idsTurnos = turnos.Select(t => t.Id).ToList();
        var cortes = await db.Cortes.AsNoTracking()
            .Where(c => c.Tipo == TipoCorte.Z && c.FechaUtc >= desde && c.FechaUtc < hasta && idsTurnos.Contains(c.TurnoId))
            .OrderBy(c => c.FechaUtc).ToListAsync(ct);
        var devoluciones = await db.Devoluciones.AsNoTracking()
            .Where(d => d.SucursalId == sucursalId && d.FechaUtc >= desde && d.FechaUtc < hasta).ToListAsync(ct);
        var salidas = await db.MovimientosCaja.AsNoTracking()
            .Where(m => idsTurnos.Contains(m.TurnoId) && m.Tipo == TipoMovimientoCaja.Salida && m.FechaUtc >= desde && m.FechaUtc < hasta)
            .SumAsync(m => (long?)m.MontoCentavos, ct) ?? 0;
        var precios = await db.Bitacora.AsNoTracking()
            .CountAsync(b => b.SucursalId == sucursalId && b.Accion == AccionControlada.CambiarPrecio && b.Resultado != ResultadoBitacora.Negado
                && b.FechaUtc >= desde && b.FechaUtc < hasta, ct);
        var porVerificar = await (from c in db.Cobros.AsNoTracking()
                                  join v in db.Ventas on c.VentaId equals v.Id
                                  where v.SucursalId == sucursalId && c.Forma == FormaPago.Transferencia && c.Verificacion == Verificacion.PorVerificar
                                  select c.MontoCentavos).ToListAsync(ct);
        // También las de los abonos de los clientes (fase 3).
        porVerificar.AddRange(await db.Abonos.AsNoTracking()
            .Where(a => a.SucursalId == sucursalId && a.Forma == FormaPago.Transferencia && a.Verificacion == Verificacion.PorVerificar && a.CanceladoUtc == null)
            .Select(a => a.MontoCentavos).ToListAsync(ct));

        var t = new StringBuilder();
        t.AppendLine($"Resumen del día · {sucursal?.Nombre} · {fecha.ToString("dddd d 'de' MMMM", Mexico)}");
        t.AppendLine($"Ventas: {Dinero(vigentes.Sum(v => v.TotalCentavos))} en {vigentes.Count} tickets");
        t.AppendLine($"Utilidad bruta: {Dinero(utilidad)} (sin artículo común)");
        foreach (var g in vigentes.GroupBy(v => v.CajaId).OrderBy(g => cajas.GetValueOrDefault(g.Key)))
            t.AppendLine($"  {cajas.GetValueOrDefault(g.Key, "Caja")}: {Dinero(g.Sum(v => v.TotalCentavos))} · {g.Count()} tickets");
        if (cortes.Count > 0)
        {
            t.AppendLine("Cortes:");
            foreach (var c in cortes)
            {
                var turno = turnos.First(x => x.Id == c.TurnoId);
                var diferencia = c.DiferenciaCentavos ?? 0;
                t.AppendLine($"  {cajas.GetValueOrDefault(turno.CajaId, "Caja")} · {usuarios.GetValueOrDefault(turno.CajeroId)}: esperado {Dinero(c.EsperadoCentavos)}, "
                    + $"contado {Dinero(c.ContadoCentavos ?? 0)}"
                    + (diferencia == 0 ? " · cuadra" : $" · {(diferencia < 0 ? "faltante" : "sobrante")} {Dinero(Math.Abs(diferencia))}")
                    + (c.EnContingencia ? " (sin conexión)" : ""));
            }
        }
        var canceladas = devoluciones.Where(d => d.EsCancelacion).ToList();
        var devueltas = devoluciones.Where(d => !d.EsCancelacion).ToList();
        if (canceladas.Count > 0)
            t.AppendLine($"Cancelaciones: {canceladas.Count} · {Dinero(canceladas.Sum(d => d.TotalCentavos))}");
        if (devueltas.Count > 0)
            t.AppendLine($"Devoluciones: {devueltas.Count} · {Dinero(devueltas.Sum(d => d.TotalCentavos))}");
        // El redondeo al peso del efectivo: lo de las ventas del día menos lo de sus reembolsos (no es venta ni utilidad).
        var redondeo = vigentes.Sum(v => v.RedondeoCentavos) - devoluciones.Where(d => !d.EsCancelacion).Sum(d => d.RedondeoCentavos);
        if (redondeo != 0)
            t.AppendLine($"Redondeo en efectivo: {(redondeo > 0 ? "+" : "-")}{Dinero(Math.Abs(redondeo))}");
        if (precios > 0)
            t.AppendLine($"Cambios de precio: {precios}");
        if (vigentes.Where(v => v.Lineas.Any(l => l.TieneDescuento)).ToList() is { Count: > 0 } conDescuento)
            t.AppendLine($"Descuentos: {conDescuento.Count} tickets · {Dinero(conDescuento.Sum(v => v.DescuentoCentavos))}"
                + (conDescuento.Count(v => v.ClienteId != null) is var aClientes and > 0 ? $" ({aClientes} a clientes registrados)" : ""));
        if (salidas > 0)
            t.AppendLine($"Salidas de efectivo: {Dinero(salidas)}");
        await CreditoDelDiaAsync(db, sucursalId, desde, hasta, vigentes, t, ct);
        var ajustes = await db.AjustesInventario.AsNoTracking()
            .Where(a => a.SucursalId == sucursalId && a.FechaUtc >= desde && a.FechaUtc < hasta).Select(a => new { a.Tipo, a.CostoCentavos }).ToListAsync(ct);
        if (ajustes.Where(a => a.Tipo == TipoMovimiento.Merma).ToList() is { Count: > 0 } mermas)
            t.AppendLine($"Mermas: {mermas.Count} · costo {Dinero(-mermas.Sum(m => m.CostoCentavos))}");
        if (ajustes.Where(a => a.Tipo == TipoMovimiento.Ajuste).ToList() is { Count: > 0 } ajustados)
            t.AppendLine($"Ajustes de inventario: {ajustados.Count} · neto {Dinero(ajustados.Sum(a => a.CostoCentavos))}");
        if (await db.ConteosCiclicos.AsNoTracking().Include(c => c.Renglones)
                .FirstOrDefaultAsync(c => c.SucursalId == sucursalId && c.Fecha == fecha, ct) is { Renglones.Count: > 0 } conteo)
        {
            var contados = conteo.Renglones.Where(r => r.ContadoUtc != null).ToList();
            var conDiferencia = contados.Where(r => r.CostoDiferenciaCentavos is not (null or 0)).ToList();
            t.AppendLine($"Conteo del día: {contados.Count} de {conteo.Renglones.Count}"
                + (conDiferencia.Count > 0 ? $" · con diferencia {conDiferencia.Count} (neto {Dinero(conDiferencia.Sum(r => r.CostoDiferenciaCentavos ?? 0))})" : " · todo cuadra"));
        }
        foreach (var cerrado in await (from c in db.ConteosDepartamento.AsNoTracking()
                                       join d in db.Departamentos on c.DepartamentoId equals d.Id
                                       where c.SucursalId == sucursalId && c.Estado == EstadoConteoDepartamento.Cerrado
                                           && c.TerminadoUtc >= desde && c.TerminadoUtc < hasta
                                       select new { c.Id, d.Nombre }).ToListAsync(ct))
        {
            var neto = await db.ConteosDepartamentoRenglones.Where(r => r.ConteoId == cerrado.Id).SumAsync(r => (long?)r.CostoDiferenciaCentavos, ct) ?? 0;
            t.AppendLine($"Conteo completo de «{cerrado.Nombre}» cerrado · neto {(neto < 0 ? "-" : "+")}{Dinero(Math.Abs(neto))}");
        }
        if (porVerificar.Count > 0)
            t.AppendLine($"Transferencias por verificar: {porVerificar.Count} · {Dinero(porVerificar.Sum())}");
        if (vigentes.Count(v => v.EnContingencia) is var sinConexion and > 0)
            t.AppendLine($"Ventas hechas sin conexión: {sinConexion}");
        // Lo de una caja sin conexión que llegó hoy cuando su turno ya tenía corte Z (cada cosa ya se avisó al llegar).
        var ventasFuera = await db.Ventas.AsNoTracking()
            .Where(v => v.SucursalId == sucursalId && v.FueraDeCorteUtc >= desde && v.FueraDeCorteUtc < hasta)
            .Select(v => v.TotalCentavos + v.RedondeoCentavos).ToListAsync(ct);
        var movimientosFuera = await db.MovimientosCaja.AsNoTracking()
            .Where(m => idsTurnos.Contains(m.TurnoId) && m.FueraDeCorteUtc >= desde && m.FueraDeCorteUtc < hasta)
            .Select(m => new { m.Tipo, m.MontoCentavos }).ToListAsync(ct);
        var abonosFuera = await db.Abonos.AsNoTracking()
            .Where(a => a.SucursalId == sucursalId && a.FueraDeCorteUtc >= desde && a.FueraDeCorteUtc < hasta)
            .Select(a => a.MontoCentavos + a.RedondeoCentavos).ToListAsync(ct);
        var fueraDeCorte = new List<string>();
        if (ventasFuera.Count > 0)
            fueraDeCorte.Add($"{Cuantas(ventasFuera.Count, "venta")} · {Dinero(ventasFuera.Sum())}");
        foreach (var g in movimientosFuera.GroupBy(m => m.Tipo).OrderBy(g => g.Key))
            fueraDeCorte.Add($"{Cuantas(g.Count(), g.Key == TipoMovimientoCaja.Entrada ? "entrada" : "salida")} · {Dinero(g.Sum(m => m.MontoCentavos))}");
        if (abonosFuera.Count > 0)
            fueraDeCorte.Add($"{Cuantas(abonosFuera.Count, "abono")} · {Dinero(abonosFuera.Sum())}");
        if (fueraDeCorte.Count > 0)
            t.AppendLine($"Llegaron después de su corte: {string.Join("; ", fueraDeCorte)} (esos cortes no los incluyen)");
        // Lo del crédito sin conexión que no cuadró y nadie ha revisado (cada una ya se avisó al llegar).
        if (await db.RevisionesCredito.CountAsync(r => r.SucursalId == sucursalId && r.RevisadaUtc == null, ct) is var porRevisar and > 0)
            t.AppendLine($"Crédito por revisar (en Clientes): {porRevisar}");
        // Lo que se cobró de impresiones en el día (la línea «Impresiones» de cada ticket).
        if (vigentes.SelectMany(v => v.Lineas).Where(l => l.EsImpresiones).Sum(l => l.ImporteCentavos) is var impresiones and > 0)
            t.AppendLine($"Impresiones cobradas: {Dinero(impresiones)}");
        var noVendidas = await db.TrabajosImpresion.AsNoTracking()
            .Where(i => i.SucursalId == sucursalId && i.Cobro == EstadoCobroImpresion.NoVendida && i.CobroUtc >= desde && i.CobroUtc < hasta)
            .Select(i => i.CostoCentavos).ToListAsync(ct);
        if (noVendidas.Count > 0)
            t.AppendLine($"Impresiones no vendidas: {noVendidas.Count} · costo {Dinero(noVendidas.Sum())}");
        if (await new ServicioImpresora(db).ComparacionAsync(sucursalId, fecha, ct) is { } contador)
            t.AppendLine($"Impresora: contó {contador.Counted:N0} caras, registradas {contador.RegisteredTotal:N0}"
                + (contador.Unregistered > 0 ? $" · sin registrar {contador.Unregistered:N0} (copias sin cobrar, celular, pruebas)" : " · todo registrado"));
        return t.ToString().TrimEnd();
    }

    /// <summary>
    /// El crédito en el resumen (fase 3): lo fiado del día (ya cuenta en las ventas), los abonos (no son venta) por forma de
    /// pago con lo que entró, el saldo a favor que se devolvió, y lo que hay por cobrar (ahora) con lo vencido.
    /// </summary>
    private static async Task CreditoDelDiaAsync(PosDbContext db, Guid sucursalId, DateTime desde, DateTime hasta, List<Venta> vigentes, StringBuilder t,
        CancellationToken ct)
    {
        if (vigentes.Where(v => v.CreditoCentavos > 0).ToList() is { Count: > 0 } fiadas)
            t.AppendLine($"Fiado: {Dinero(fiadas.Sum(v => v.CreditoCentavos))} en {Cuantas(fiadas.Count, "ticket")}");
        var delDia = await db.Abonos.AsNoTracking()
            .Where(a => a.SucursalId == sucursalId && a.FechaUtc >= desde && a.FechaUtc < hasta && a.CanceladoUtc == null)
            .Select(a => new { a.Tipo, a.Forma, a.MontoCentavos, a.RedondeoCentavos }).ToListAsync(ct);
        // Lo que entró: en efectivo, con su redondeo (como en el corte).
        var abonos = delDia.Where(a => a.Tipo == TipoAbono.Abono)
            .Select(a => (a.Forma, Entro: a.Forma == FormaPago.Efectivo ? a.MontoCentavos + a.RedondeoCentavos : a.MontoCentavos)).ToList();
        if (abonos.Count > 0)
            t.AppendLine($"Abonos: {Dinero(abonos.Sum(a => a.Entro))} en {Cuantas(abonos.Count, "abono")} ("
                + string.Join(", ", abonos.GroupBy(a => a.Forma).OrderBy(g => g.Key).Select(g => g.Key switch
                {
                    FormaPago.Tarjeta => "tarjeta",
                    FormaPago.Transferencia => "transferencia",
                    _ => "efectivo",
                } + $" {Dinero(g.Sum(a => a.Entro))}")) + ")");
        if (delDia.Where(a => a.Tipo == TipoAbono.SaldoDevuelto).ToList() is { Count: > 0 } devueltos)
            t.AppendLine($"Saldo a favor devuelto: {Dinero(devueltos.Sum(a => a.MontoCentavos + a.RedondeoCentavos))}");
        // Lo que deben ahora (aunque el resumen sea de un día anterior), con lo vencido a hoy. Los clientes son de todas las
        // sucursales (salvo los que tienen una): con varias sucursales en la base, la línea lo dice.
        var deben = (await new RegistroCredito(db).CuentasDeLaSucursalAsync(sucursalId, ct)).Where(c => c.SaldoCentavos > 0).ToList();
        if (deben.Count == 0)
            return;
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(db.Reloj.GetUtcNow().UtcDateTime));
        var vencidos = deben.Where(c => c.Vencido(hoy) > 0).ToList();
        var varias = await db.Sucursales.CountAsync(ct) > 1;
        t.AppendLine($"Por cobrar{(varias ? " (con los clientes de todas las sucursales)" : "")}: {Dinero(deben.Sum(c => c.SaldoCentavos))} "
            + $"de {Cuantas(deben.Count, "cliente")}"
            + (vencidos.Count > 0 ? $" · vencido {Dinero(vencidos.Sum(c => c.Vencido(hoy)))} de {Cuantas(vencidos.Count, "cliente")}" : ""));
    }

    private static string Cuantas(int cuantas, string que) => $"{cuantas} {que}{(cuantas == 1 ? "" : "s")}";
}
