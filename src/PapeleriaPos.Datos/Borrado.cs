namespace PapeleriaPos.Datos;

/// <summary>
/// Algo que la copia de las cajas llevaba y se quitó (un código, una presentación, una escala de mayoreo, un permiso…) o
/// que pasó a otro producto, con la versión del catálogo con que pasó (entrega 5, parte 2). Lo que solo cambia se encuentra
/// por su propia versión; lo quitado ya no está, y sin esto los cambios de la copia (api/cajas/{id}/cambios) no lo dirían.
/// Lo escribe la base al guardar (<see cref="PosDbContext"/>): nadie más lo toca.
/// </summary>
public class Borrado
{
    public long Id { get; set; }

    /// <summary>Qué era: «CodigoBarras», «Presentacion», «Precio», «PermisoUsuario»…</summary>
    public required string Entidad { get; set; }

    /// <summary>Su llave, si es un Guid.</summary>
    public Guid? EntidadId { get; set; }

    /// <summary>El producto (la variante) del que era: la caja vuelve a recibir ese producto completo, con lo que le quedó.</summary>
    public Guid? VarianteId { get; set; }

    /// <summary>La versión del catálogo con que se quitó.</summary>
    public long Version { get; set; }

    public DateTime FechaUtc { get; set; }
}
