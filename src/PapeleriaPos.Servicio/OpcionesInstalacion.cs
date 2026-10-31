namespace PapeleriaPos.Servicio;

/// <summary>
/// «Instalacion» en servicio.json (C:\ProgramData\PapeleriaPos\servicio.json): el papel de esta PC, como se eligió en el instalador
/// (2026-10-31). Sin la sección, o con <c>false</c>, es la caja principal, como hasta hoy: las demás cajas y los celulares se conectan
/// a ella. «Otra caja» no tiene servicio, así que no tiene servicio.json.
/// </summary>
public sealed class OpcionesInstalacion
{
    /// <summary>
    /// «La única caja»: el servicio y la caja en esta PC, y ninguna otra caja se da de alta desde otra PC. No contesta a quien busca
    /// el servicio en la red (UDP 5081) y <c>/api/alta</c> no acepta otras PC; la caja de esta PC trabaja como siempre, y los celulares
    /// (TCP 5080) también. Para conectar otra caja, se corre otra vez el instalador y se elige «Caja principal».
    /// </summary>
    public bool UnaSolaCaja { get; set; }
}
