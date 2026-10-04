// Traídas de Control Print v1.12 (ControlPrint.Core.Tests).
namespace PapeleriaPos.Medicion.Pruebas;

/// <summary>Archivos de prueba generados con la cola de laboratorio (páginas sintéticas CPLab-*).</summary>
static class Fixture
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    public static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));
    public static string Text(string name) => File.ReadAllText(Path(name));
}
