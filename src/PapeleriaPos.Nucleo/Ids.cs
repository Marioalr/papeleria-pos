namespace PapeleriaPos.Nucleo;

/// <summary>IDs UUIDv7: se generan donde nace el registro y no chocan entre cajas ni sucursales.</summary>
public static class Ids
{
    public static Guid Nuevo() => Guid.CreateVersion7();
}
