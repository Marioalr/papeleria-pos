using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Auditoría (2026-11-04), arreglado con la revisión de #155: una lectura a medias (una tinta sin contestar, un paquete UDP perdido) hacía
/// que el aviso de «tinta baja» saliera otra vez con la siguiente lectura completa, aunque nada cambió.
/// </summary>
public sealed class AvisosDeLaImpresoraTests
{
    [Fact]
    public async Task Una_lectura_a_medias_no_repite_el_aviso_de_tinta_baja()
    {
        await using var modulo = await BaseDelModulo.CrearAsync();
        var sucursal = Guid.NewGuid();
        var t0 = DateTime.UtcNow;
        PrinterStatus L(int min, int? m) => new() { TakenUtc = t0.AddMinutes(min), InkK = 50, InkC = 50, InkM = m, InkY = 50, Maintenance = 50, LifeCount = 1000 };
        await using var db = modulo.Contexto();
        var impresora = modulo.Impresora(db);
        await impresora.GuardarLecturaAsync(sucursal, L(0, 20));
        await impresora.GuardarLecturaAsync(sucursal, L(5, 8));   // baja al umbral: un aviso
        await impresora.GuardarLecturaAsync(sucursal, L(10, 8));  // sigue igual: sin aviso
        await impresora.GuardarLecturaAsync(sucursal, L(15, null)); // magenta sin leer
        await impresora.GuardarLecturaAsync(sucursal, L(20, 8));    // ya contestó: nada cambió
        await impresora.GuardarLecturaAsync(sucursal, L(25, null));
        await impresora.GuardarLecturaAsync(sucursal, L(30, 8));

        Assert.Contains("magenta", Assert.Single(modulo.Pos.Avisos).Texto);
    }
}
