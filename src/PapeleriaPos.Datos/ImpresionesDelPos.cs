using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Telegram;

namespace PapeleriaPos.Datos;

/// <summary>
/// Los servicios del módulo de impresión (src/Impresiones) sobre la base del POS, cada uno con el puerto
/// (<see cref="PosParaImpresiones"/>) en el mismo contexto: lo que guarda el módulo y los avisos que da quedan en la misma
/// transacción. La venta, las devoluciones y el resumen del día usan <see cref="EnLaVenta"/>, nunca las tablas del módulo.
/// </summary>
public static class ImpresionesDelPos
{
    /// <summary>Lo que la venta hace con las impresiones: cobrarlas, regresarlas a por cobrar y el resumen del día.</summary>
    public static ImpresionesEnLaVenta EnLaVenta(PosDbContext db) => new(db, Pos(db));

    /// <summary>Los trabajos de impresión: registrarlos con su precio, lo que falta cobrar, cotizar y las copias.</summary>
    public static RegistroImpresiones Registro(PosDbContext db) => new(db, Pos(db));

    /// <summary>La impresora de cada sucursal: su configuración, sus lecturas, el contador y la calibración.</summary>
    public static ServicioImpresora Impresora(PosDbContext db) => new(db, Pos(db));

    /// <summary>El puerto solo, sobre esa base (para lo que el módulo hace sin sus servicios, como la vigilancia).</summary>
    public static IPosParaImpresiones Pos(PosDbContext db) => new PosParaImpresiones(db);
}

/// <summary>
/// El POS visto desde el módulo de impresión (<see cref="IPosParaImpresiones"/>), sobre su base: la hora de la base, las
/// cajas, el horario de la sucursal y los avisos, que salen como las alertas de Telegram de siempre (tinta, impresora sin
/// responder y hojas faltantes), con lo que la sucursal tenga apagado. Los avisos quedan en el contexto, sin guardar: se
/// guardan con lo que guarde el módulo, que usa este mismo contexto.
/// </summary>
public sealed class PosParaImpresiones(PosDbContext db) : IPosParaImpresiones
{
    public TimeProvider Reloj => db.Reloj;

    public async Task<CajaDelPos?> CajaAsync(Guid cajaId, CancellationToken ct = default) =>
        await db.Cajas.FindAsync([cajaId], ct) is { } caja ? new CajaDelPos(caja.Id, caja.SucursalId, caja.Nombre) : null;

    public bool Avisar(Guid sucursalId, AvisoImpresiones tipo, string texto, string? clave = null) =>
        AlertasTelegram.Avisar(db, sucursalId, Alerta(tipo), texto, clave);

    public bool FueraDeHorario(Guid sucursalId, DateTime utc) =>
        AlertasTelegram.Configuracion(db, sucursalId).FueraDeHorario(HoraMexico.ALocal(utc));

    /// <summary>Cada aviso del módulo con su tipo de alerta, el que la sucursal puede apagar.</summary>
    private static TipoAlerta Alerta(AvisoImpresiones tipo) => tipo switch
    {
        AvisoImpresiones.HojasFaltantes => TipoAlerta.HojasFaltantes,
        // Es dinero, no la impresora (2026-10-29): apagar los avisos de la impresora no lo apaga.
        AvisoImpresiones.CobradaDosVeces => TipoAlerta.CobradaDosVeces,
        // La impresora (tinta, caja de mantenimiento, sin responder) y lo que el módulo agregue sin su propio tipo.
        _ => TipoAlerta.Impresora,
    };
}
