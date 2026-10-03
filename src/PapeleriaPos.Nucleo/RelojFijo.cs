namespace PapeleriaPos.Nucleo;

/// <summary>Un reloj que marca la hora que se le diga: para pruebas y para datos reproducibles.</summary>
public sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
{
    public DateTimeOffset Ahora { get; set; } = ahora;

    public override DateTimeOffset GetUtcNow() => Ahora;
}
