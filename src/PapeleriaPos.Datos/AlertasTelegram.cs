using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Impresiones;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

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
            if (Tipo(b, configuracion) is { } tipo && configuracion.Avisa(tipo))
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
            AccionControlada.SalidaEfectivo when b.MontoCentavos > c.UmbralSalidaCentavos => TipoAlerta.SalidaEfectivo,
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
            texto.AppendLine(tipo == TipoAlerta.DiferenciaCorte ? $"{(monto < 0 ? "Faltante" : "Sobrante")}: {Dinero(Math.Abs(monto))}" : Dinero(monto));
        if (autorizo is not null)
            texto.AppendLine(tipo == TipoAlerta.AutorizacionNegada ? $"Intentó autorizar: {autorizo}" : $"Autorizó: {autorizo}");
        if (b.EnContingencia)
            texto.AppendLine("(pasó en la caja sin conexión; llegó al volver)");
        return texto.ToString().TrimEnd();
    }

    /// <summary>Después de un corte Z: si ya no queda turno abierto en la sucursal, sale el resumen del día. Quien llama guarda.</summary>
    public static async Task ResumenSiEsElUltimoAsync(PosDbContext db, Corte corte, Guid sucursalId, CancellationToken ct = default)
    {
        if (await db.Turnos.AnyAsync(t => t.SucursalId == sucursalId && t.CerradoUtc == null && t.Id != corte.TurnoId, ct))
            return;
        if (!Configuracion(db, sucursalId).Avisa(TipoAlerta.ResumenDiario))
            return;
        var texto = await ResumenAsync(db, sucursalId, DateOnly.FromDateTime(HoraMexico.ALocal(corte.FechaUtc)), ct);
        Agregar(db, Chats(db), TipoAlerta.ResumenDiario, texto, $"resumen:{corte.Id}");
    }

    /// <summary>Ventas, utilidad, cortes y lo que pasó en el día en una sucursal.</summary>
    public static async Task<string> ResumenAsync(PosDbContext db, Guid sucursalId, DateOnly fecha, CancellationToken ct = default)
    {
        var desde = HoraMexico.AUtc(fecha.ToDateTime(TimeOnly.MinValue));
        var hasta = HoraMexico.AUtc(fecha.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var sucursal = await db.Sucursales.FindAsync([sucursalId], ct);
        var cajas = await db.Cajas.Where(c => c.SucursalId == sucursalId).ToDictionaryAsync(c => c.Id, c => c.Nombre, ct);
        var usuarios = await db.Usuarios.ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);

        var ventas = await db.Ventas.AsNoTracking().Include(v => v.Lineas)
            .Where(v => v.SucursalId == sucursalId && v.PagadaUtc >= desde && v.PagadaUtc < hasta).ToListAsync(ct);
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
        if (precios > 0)
            t.AppendLine($"Cambios de precio: {precios}");
        if (vigentes.Where(v => v.Lineas.Any(l => l.TieneDescuento)).ToList() is { Count: > 0 } conDescuento)
            t.AppendLine($"Descuentos: {conDescuento.Count} tickets · {Dinero(conDescuento.Sum(v => v.DescuentoCentavos))}"
                + (conDescuento.Count(v => v.ClienteEspecialId != null) is var especiales and > 0 ? $" ({especiales} a clientes especiales)" : ""));
        if (salidas > 0)
            t.AppendLine($"Salidas de efectivo: {Dinero(salidas)}");
        if (porVerificar.Count > 0)
            t.AppendLine($"Transferencias por verificar: {porVerificar.Count} · {Dinero(porVerificar.Sum())}");
        if (vigentes.Count(v => v.EnContingencia) is var sinConexion and > 0)
            t.AppendLine($"Ventas hechas sin conexión: {sinConexion}");
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
}
