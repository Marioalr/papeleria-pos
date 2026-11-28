using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Hace de la lectura de eleventa pedida en el momento (<see cref="ILecturaFresca"/>; el conteo del celular en el piloto): no hay
/// eleventa, así que «leer» es lo que la prueba diga (lo que eleventa vendió desde la última lectura, anotado en el kardex como lo
/// anota la de verdad), o falla como si eleventa no contestara.
/// </summary>
public sealed class LecturaDePrueba : ILecturaFresca
{
    /// <summary>Lo que trae la siguiente lectura (una vez): lo que eleventa movió y aquí todavía no se sabía.</summary>
    public Func<Task>? AlLeer { get; set; }

    /// <summary>Con esto, eleventa no contesta: por qué.</summary>
    public string? Falla { get; set; }

    /// <summary>Con esto, tarda (para probar la espera).</summary>
    public TimeSpan Demora { get; set; }

    /// <summary>Las que se pidieron y se esperaron.</summary>
    public int Lecturas { get; private set; }

    /// <summary>Las que se pidieron sin esperar (<see cref="LeerAhora"/>).</summary>
    public int Pedidas { get; private set; }

    public async Task<string?> LeerAsync(CancellationToken ct)
    {
        Lecturas++;
        if (Demora > TimeSpan.Zero)
            await Task.Delay(Demora, ct);
        if (Falla is { } falla)
            return falla;
        if (AlLeer is { } alLeer)
        {
            AlLeer = null;
            await alLeer();
        }
        return null;
    }

    public void LeerAhora() => Pedidas++;
}
