using System.Text.Json;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Pruebas.ControlPrint;

/// <summary>
/// La fase 1 del itinerario de los datos de Control Print (2026-11-20): por qué no coincide el precio, la tinta que bajó, lo que contó la
/// impresora sin registrarse, cuándo se imprime y lo cobrado en eleventa. Con una recopilación inventada.
/// </summary>
public sealed class AnalisisControlPrintUsoTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"cp-uso-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    /// <summary>Los costos de hoy en Control Print: sin calibración (todo en 1).</summary>
    private static readonly CostSettings Hoy = new();

    /// <summary>Una impresión a color de una página con mucho cian, con el precio que salió con la calibración de su día.</summary>
    private static TrabajoControlPrint AColor(long id, DateTime utc, double calibracionCian = 1, int caras = 1)
    {
        var ajustes = new JobSettings { Color = true };
        var paginas = Enumerable.Range(0, caras).Select(_ => new PageCoverage(0.5, 0.02, 0.02, 0.02, 0.1, 93.5, true)).ToList();
        var deSuDia = Hoy.Clone();
        deSuDia.Calibration.C = calibracionCian;
        var costo = CostCalculator.Calculate(new JobAnalysis(ajustes, paginas, Medicion.AnalysisLevel.Measured, null, null, TimeSpan.Zero), deSuDia);
        return new TrabajoControlPrint
        {
            Id = id, PcOrigen = "PRINCIPAL", Tipo = TipoTrabajoControlPrint.Impresion, Impresora = "NUEVA", Documento = "Tarea de un cliente.pdf",
            DetectadoUtc = utc, EnviadoUtc = utc, TerminadoUtc = utc, Estado = EstadoTrabajoControlPrint.Impreso, Paginas = caras, Copias = 1,
            Caras = caras, CarasColor = caras, Hojas = caras, Color = true, CostoTotal = costo.Total, Precio = costo.Price, PrecioACobrar = costo.Price,
            Cobrable = true, Cobro = CobroControlPrint.PorCobrar, NivelAnalisis = NivelAnalisisControlPrint.Medido,
            DesgloseJson = JsonSerializer.Serialize(new
            {
                cost = costo, settings = ajustes, pages = paginas.Select(p => new { p.C, p.M, p.Y, p.K, p.Gray, p.AreaSqIn, p.HasColor }), elapsedMs = 0,
            }, CostSettings.Json),
        };
    }

    private static LecturaImpresoraControlPrint Lectura(DateTime utc, int cian, long color, long bn) => new()
    {
        TomadaUtc = utc, TintaK = 80, TintaC = cian, TintaM = 80, TintaY = 80, CajaMantenimiento = 90, ContadorColor = color, ContadorBN = bn,
        ContadorTotal = color + bn,
    };

    /// <summary>Lunes 2026-10-05 a las 12:00 de la tienda (18:00 UTC).</summary>
    private static readonly DateTime Lunes = new(2026, 10, 5, 18, 0, 0, DateTimeKind.Utc);

    private string Recopilacion(Action<ArchivoRecopilacion>? ademas = null)
    {
        Directory.CreateDirectory(_carpeta);
        using var archivo = ArchivoRecopilacion.Abrir(_carpeta);
        archivo.GuardarValor("estado", "terminada");
        archivo.GuardarConfiguracion("costos", JsonSerializer.Serialize(Hoy, CostSettings.Json));
        archivo.GuardarTrabajos(
        [
            // Con la calibración de hoy.
            AColor(1, Lunes),
            // Salió cuando Control Print tenía el cian en 3: con la de hoy no coincide, con la suya sí.
            AColor(2, Lunes.AddMinutes(5), calibracionCian: 3),
            AColor(3, Lunes.AddMinutes(10), caras: 2),
            // El martes, en la noche.
            AColor(4, Lunes.AddDays(1).AddHours(8)),
        ]);
        ademas?.Invoke(archivo);
        return Path.Combine(_carpeta, ArchivoRecopilacion.NombreArchivo);
    }

    [Fact]
    public void Dice_que_no_coincidia_por_la_calibracion_de_su_dia()
    {
        var informe = AnalisisControlPrint.Analizar(Recopilacion());

        Assert.Contains("### Por qué no coincide", informe);
        Assert.Contains("- Coinciden con la calibración de hoy: 3 de 4 (75 %).", informe);
        Assert.Contains("(Control Print la cambia cada pocos días): 1 más. Con esas, 4 de 4 (100 %) dan el mismo precio.", informe);
        Assert.Contains("- Siguen sin coincidir: 0. El cálculo del módulo es el de Control Print.", informe);
        Assert.Contains("| 2026-10-05 | 4 | 75 % | 100 % |", informe);
        Assert.DoesNotContain("Tarea de un cliente", informe);
    }

    [Fact]
    public void Compara_la_tinta_y_el_contador_con_lo_registrado()
    {
        var informe = AnalisisControlPrint.Analizar(Recopilacion(archivo => archivo.CambiarLecturas(null,
        [
            Lectura(Lunes.AddMinutes(-30), cian: 60, color: 1000, bn: 2000),
            // Entre las dos: los trabajos 1 a 3 (4 caras a color) y lo que no pasó por Control Print: 6 a color y 50 en B/N.
            Lectura(Lunes.AddMinutes(30), cian: 40, color: 1010, bn: 2050),
        ])));

        Assert.Contains("## La tinta: lo que estimó el modelo contra lo que bajó", informe);
        Assert.Contains("| Cian | 20 |", informe);
        Assert.Contains("**El tope esconde parte de la diferencia**", informe);
        Assert.Contains("Si la tinta se cobrara como bajó de verdad", informe);
        Assert.Contains("## Lo que contó la impresora y no se registró", informe);
        Assert.Contains("Las lecturas no traen la serie de la impresora", informe);
        Assert.Contains("- La impresora contó 10 caras a color y 50 en B/N; Control Print registró 4 y 0.", informe);
        Assert.Contains("- Sin registrar: 6 a color (60 %) y 50 en B/N (100 %)", informe);
        Assert.Contains("| 2026-10-05 | 1 | 10 | 4 | 50 | 0 | 50 |", informe);
    }

    [Fact]
    public void Sin_lecturas_lo_dice_y_sigue()
    {
        var informe = AnalisisControlPrint.Analizar(Recopilacion());

        Assert.Contains("No hay lecturas de la impresora para comparar.", informe);
        Assert.Contains("No hay lecturas con los contadores de color y B/N.", informe);
        Assert.Contains("## Cuándo se imprime", informe);
    }

    [Fact]
    public void Dice_cuando_se_imprime()
    {
        var informe = AnalisisControlPrint.Analizar(Recopilacion());

        // Del lunes al martes: un lunes y un martes en el periodo.
        Assert.Contains("| Lunes | 3 | 4 | 3 | 4 |", informe);
        Assert.Contains("| Martes | 1 | 1 | 1 | 1 |", informe);
        Assert.Contains("| 12:00 | 3 | 4 | 1.5 |", informe);
        Assert.Contains("| 20:00 | 1 | 1 | 0.5 |", informe);
        Assert.Contains("Los 15 minutos con más trabajos: 3, desde el 2026-10-05 12:00 (lunes).", informe);
    }

    [Fact]
    public void Cruza_lo_cobrado_en_eleventa_sin_el_ultimo_dia()
    {
        var csv = Path.Combine(_carpeta, "ventas.csv");
        var archivo = Recopilacion();
        // Como lo deja isql: con espacios, un renglón vacío y otro que no es de ventas.
        File.WriteAllLines(csv,
        [
            "",
            "2026-10-05,IMP,3,6.000,60.00                       ",
            "2026-10-05,COP,1,0.500,5.00",
            "2026-10-06,IMP,1,1.000,10.00",
            "Records affected: 3",
            // La copia de eleventa se sacó ese día: no cuenta.
            "2026-10-07,IMP,9,90.000,900.00",
        ]);
        var ventas = AnalisisControlPrint.LeerVentasDeEleventa(csv);
        Assert.Equal(4, ventas.Count);

        var informe = AnalisisControlPrint.Analizar(archivo, ventasDeEleventa: ventas);

        Assert.Contains("## Lo cobrado en eleventa contra lo que calculó Control Print", informe);
        Assert.Contains("Del 2026-10-05 al 2026-10-06 (el 2026-10-07 no cuenta", informe);
        Assert.Contains("| Semana | COP en eleventa | IMP en eleventa | Control Print calculó | eleventa ÷ Control Print |", informe);
        Assert.Contains("en eleventa $75.00 (COP $5.00, IMP $70.00)", informe);
        Assert.DoesNotContain("$900.00", informe);
    }

    [Fact]
    public void Sin_el_archivo_de_eleventa_no_sale_esa_parte()
    {
        var informe = AnalisisControlPrint.Analizar(Recopilacion());

        Assert.DoesNotContain("Lo cobrado en eleventa", informe);
    }
}
