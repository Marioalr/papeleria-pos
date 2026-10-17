namespace PapeleriaPos.Nucleo.Catalogo;

public class Departamento : IVersionado
{
    /// <summary>Lo más largo que puede ser su nombre al darlo de alta o cambiárselo (Productos › Departamentos).</summary>
    public const int LargoNombre = 60;

    public Guid Id { get; set; } = Ids.Nuevo();
    public required string Nombre { get; set; }
    public bool Activo { get; set; } = true;

    /// <summary>Margen mínimo propio del departamento (centésimas: 3000 = 30 %). Vacío: el general de la sucursal.</summary>
    public int? MargenMinimoCentesimas { get; set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    /// <summary>El nombre sin espacios de más: ni al principio, ni al final, ni dos seguidos. Vacío si no trae nada.</summary>
    public static string NombreLimpio(string? nombre) => string.Join(' ', (nombre ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Si dos nombres son el mismo departamento, sin importar mayúsculas, acentos ni espacios de más: «Útiles» y « utiles ».</summary>
    public static bool MismoNombre(string uno, string otro) => Texto.Normalizar(uno) == Texto.Normalizar(otro);
}
