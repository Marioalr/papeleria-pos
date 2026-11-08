using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones;

/// <summary>
/// El modelo de una impresora de inyección (2026-11-03): qué tinta usa, si tiene caja de mantenimiento y cómo se lee. Lo que trae
/// cada uno está en <see cref="PerfilesImpresora"/>.
/// </summary>
public enum PerfilImpresora
{
    /// <summary>Epson WorkForce Pro WF-6590: cartuchos y caja de mantenimiento (la de Control Print; la matriz tiene dos).</summary>
    WF6590,

    /// <summary>Epson EcoTank L5590: botellas; sin caja de mantenimiento (por verificar en la tienda: la ficha de Epson lista la C12C934461).</summary>
    L5590,
}

/// <summary>
/// Una impresora de inyección de la sucursal (2026-11-03: la matriz tiene dos WF-6590 y una L5590). Cada una se lee por la red con
/// su dirección, tiene sus lecturas, su contador, su calibración y su cola de lo que salió de ella, y le pone precio a lo suyo con sus
/// costos (<see cref="CostosDeImpresora"/>). La que había antes, cuando la sucursal tenía una sola, es la número 1.
/// <para>
/// No se borra: se da de baja (<see cref="Activa"/>), y su historia (trabajos y lecturas) se queda con ella.
/// </para>
/// </summary>
public class Impresora
{
    public const int AvisoTintaPorOmision = 10;
    public const int MinutosEntreLecturasPorOmision = 5;

    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }

    /// <summary>
    /// Su número en la sucursal: 1, 2, 3… Es el que se escribe en caja.json para decir qué impresora es cada cola de una PC
    /// (<c>ColasImpresion</c>), y no se vuelve a usar aunque se dé de baja.
    /// </summary>
    public int Numero { get; set; }

    /// <summary>Cómo se le dice en la tienda («WF-6590 de la entrada», «L5590»): sale en F2 y en la Configuración.</summary>
    public required string Nombre { get; set; }

    public PerfilImpresora Perfil { get; set; }

    /// <summary>IP o nombre de la impresora en la red; vacío, no se lee.</summary>
    public string? Host { get; set; }

    /// <summary>Con este porcentaje o menos de una tinta (o de la caja de mantenimiento), se avisa.</summary>
    public int AvisoTintaPorcentaje { get; set; } = AvisoTintaPorOmision;

    public int MinutosEntreLecturas { get; set; } = MinutosEntreLecturasPorOmision;

    /// <summary>
    /// Lo propio de esta impresora encima de los costos de la sucursal (<see cref="CostosDeImpresora"/>, en JSON): su tinta, su caja de
    /// mantenimiento, su luz y su depreciación (la L5590), y su calibración. Vacío: todo es de la sucursal, como la impresora de antes.
    /// </summary>
    public string? CostosJson { get; set; }

    public bool Activa { get; set; } = true;
    public DateTime CreadaUtc { get; set; }

    /// <summary>
    /// Su calibración es la de los costos de la sucursal: la número 1 mientras use los consumibles de la sucursal (la impresora de antes;
    /// en la caja 2, la que mide Control Print). Las demás guardan la suya en <see cref="CostosJson"/>.
    /// </summary>
    public bool CalibraEnLaSucursal(CostosDeImpresora? propios) => Numero == 1 && propios?.Consumibles is null;
}
