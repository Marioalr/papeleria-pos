namespace PapeleriaPos.Impresiones;

/// <summary>
/// Lo que el módulo de impresiones necesita del POS, y nada más (el puerto): la hora, las cajas, el horario de la tienda
/// y a quién avisar. El módulo no conoce las tablas ni los servicios del POS: el POS lo implementa sobre su base
/// (PapeleriaPos.Datos, <c>PosParaImpresiones</c>) y las pruebas del módulo con uno falso. Si al módulo le hace falta algo
/// más del POS, se agrega aquí, con su porqué.
/// </summary>
public interface IPosParaImpresiones
{
    /// <summary>La hora del POS: las pruebas la fijan.</summary>
    TimeProvider Reloj { get; }

    /// <summary>La caja de la PC que imprimió (o en la que se registraron unas copias), con su sucursal; vacío si no existe.</summary>
    Task<CajaDelPos?> CajaAsync(Guid cajaId, CancellationToken ct = default);

    /// <summary>
    /// Avisa al dueño. Regresa falso si no se manda: la sucursal tiene apagado ese aviso, no hay a quién mandarlo, o ya se
    /// mandó uno con la misma <paramref name="clave"/> (así un aviso no sale dos veces). Cómo y por dónde le llega lo decide
    /// el POS. No guarda: el aviso queda pendiente en la misma unidad de trabajo que usa el módulo, y se guarda con lo
    /// siguiente que este guarde (junto con lo que lo causó).
    /// </summary>
    bool Avisar(Guid sucursalId, AvisoImpresiones tipo, string texto, string? clave = null);

    /// <summary>Si a esa hora (UTC) la tienda de la sucursal está cerrada: con la tienda cerrada no se avisa que la impresora no responde.</summary>
    bool FueraDeHorario(Guid sucursalId, DateTime utc);
}

/// <summary>Los avisos del módulo, por tipo: el POS decide si sale cada uno (la sucursal los apaga por separado) y por dónde.</summary>
public enum AvisoImpresiones
{
    /// <summary>Una tinta o la caja de mantenimiento en el umbral, un consumible nuevo, o la impresora que no responde.</summary>
    Impresora,

    /// <summary>La impresora contó menos caras de las que se mandaron: un atasco o una cancelación en su panel.</summary>
    HojasFaltantes,
}

/// <summary>Una caja del POS, vista desde el módulo.</summary>
/// <param name="Nombre">En las copias registradas en esa caja va como su «PC».</param>
public sealed record CajaDelPos(Guid Id, Guid SucursalId, string Nombre);
