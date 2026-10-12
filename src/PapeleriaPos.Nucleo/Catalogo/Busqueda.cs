namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>
/// Lo que se buscó por nombre en una caja y qué se eligió (docs/propuesta-interfaz.md, «Lo que no se escanea»). Con esto,
/// lo que esa caja eligió antes con el mismo texto sale primero la vez siguiente. Lo que no se encontró queda anotado,
/// pero no arma los sinónimos: esos los da de alta la tienda (decisión del dueño, 2026-10-11). No guarda nada de clientes.
/// </summary>
public class Busqueda
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid CajaId { get; set; }
    public DateTime FechaUtc { get; set; }

    /// <summary>Lo escrito, sin la cantidad y normalizado (sin acentos ni mayúsculas): la clave de «lo elegido antes».</summary>
    public required string Texto { get; set; }

    /// <summary>Cuántos productos salieron.</summary>
    public int Resultados { get; set; }

    /// <summary>Lo que se eligió; vacío si no salió nada o no se eligió nada.</summary>
    public Guid? VarianteId { get; set; }
}
