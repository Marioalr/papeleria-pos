using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas;

/// <summary>
/// Los PIN de los usuarios de la base de prueba (cajero 1111, encargado 2222, dueño 3333): el corte del turno se confirma con el
/// PIN de quien lo cierra (2026-11-20).
/// </summary>
public static class PinsDePrueba
{
    public static string De(SesionDto quien) => quien.Nombre switch
    {
        var n when n.StartsWith("Cajero", StringComparison.Ordinal) => "1111",
        var n when n.StartsWith("Encargado", StringComparison.Ordinal) => "2222",
        var n when n.StartsWith("Due", StringComparison.Ordinal) => "3333",
        var n => throw new ArgumentException($"No se sabe el PIN de prueba de «{n}».", nameof(quien)),
    };
}
