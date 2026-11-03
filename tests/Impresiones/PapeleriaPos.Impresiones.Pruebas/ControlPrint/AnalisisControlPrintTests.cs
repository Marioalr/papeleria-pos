using System.Text.Json;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Pruebas.ControlPrint;

/// <summary>
/// analizar-control-print (2026-11-03): el informe de una recopilación de Control Print para leerlo con el dueño el sábado. Aquí con una
/// recopilación inventada, escrita con las mismas tablas que deja recopilar-control-print (ArchivoRecopilacion).
/// </summary>
public sealed class AnalisisControlPrintTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"cp-analisis-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    private static readonly CostSettings CostosDeControlPrint = new();

    /// <summary>Una impresión de páginas carta en B/N al 5 %, con su desglose y su precio como los guarda Control Print.</summary>
    private static TrabajoControlPrint Impresion(long id, string dia, string pc, string cola, int paginas, decimal? precio = null,
        decimal? puesto = null, TipoPrecioControlPrint tipoPrecio = TipoPrecioControlPrint.Calculado, CobroControlPrint cobro = CobroControlPrint.Cobrado,
        MotivoNoVendidaControlPrint? motivo = null, int? contadas = null, string documento = "Tarea.pdf", string? cliente = null)
    {
        var ajustes = new JobSettings { Color = false };
        var paginasMedidas = Enumerable.Range(0, paginas).Select(_ => new PageCoverage(0, 0, 0, 0.05, 0.05, 93.5, false)).ToList();
        var costo = CostCalculator.Calculate(new JobAnalysis(ajustes, paginasMedidas, Medicion.AnalysisLevel.Measured, null, null, TimeSpan.Zero), CostosDeControlPrint);
        var detectado = DateTime.SpecifyKind(DateTime.Parse($"{dia}T17:00:00"), DateTimeKind.Utc);
        return new TrabajoControlPrint
        {
            Id = id, PcOrigen = pc, Tipo = TipoTrabajoControlPrint.Impresion, Impresora = cola, Documento = documento, DetectadoUtc = detectado,
            EnviadoUtc = detectado, Estado = EstadoTrabajoControlPrint.Impreso, Paginas = paginas, Copias = 1, Caras = paginas, Hojas = paginas,
            CarasContadas = contadas, FaltaronCaras = contadas < paginas, CostoTotal = costo.Total, Precio = precio ?? costo.Price,
            PrecioPuesto = puesto, TipoPrecio = tipoPrecio, ClienteDelPrecio = cliente, PrecioACobrar = puesto ?? precio ?? costo.Price,
            Cobrable = true, Cobro = cobro, MotivoNoVendida = motivo, NivelAnalisis = NivelAnalisisControlPrint.Medido,
            PrecioPuestoPor = puesto is null ? null : "Ana",
            DesgloseJson = JsonSerializer.Serialize(new
            {
                cost = costo, settings = ajustes, pages = paginasMedidas.Select(p => new { p.C, p.M, p.Y, p.K, p.Gray, p.AreaSqIn, p.HasColor }),
                elapsedMs = 0,
            }, CostSettings.Json),
        };
    }

    private string Recopilacion()
    {
        Directory.CreateDirectory(_carpeta);
        using var archivo = ArchivoRecopilacion.Abrir(_carpeta);
        archivo.GuardarValor("estado", "terminada");
        archivo.GuardarValor("nombre_servidor", "PRINCIPAL");
        archivo.GuardarValor("version_control_print", "1.12.0");
        archivo.GuardarValor("hasta_utc", "2026-11-07T20:00:00.0000000Z");
        archivo.GuardarConfiguracion("costos", JsonSerializer.Serialize(CostosDeControlPrint, CostSettings.Json));
        archivo.GuardarTrabajos(
        [
            Impresion(1, "2026-10-05", "PRINCIPAL", "WF-6590 Series(Red)", 2),
            // Los costos cambiaron después: Control Print guardó otro precio.
            Impresion(2, "2026-10-06", "PRINCIPAL", "WF-6590 Series(Red)", 3, precio: 40),
            Impresion(3, "2026-11-02", "CAJA2", "EPSON L5590 Series", 1, puesto: 1, tipoPrecio: TipoPrecioControlPrint.Especial,
                documento: "CURP de un cliente.pdf", cliente: "Un cliente"),
            Impresion(4, "2026-11-03", "PRINCIPAL", "WF-6590 Series(Red)", 4, cobro: CobroControlPrint.NoVendido,
                motivo: MotivoNoVendidaControlPrint.SalioMal, contadas: 2),
            Impresion(5, "2026-11-03", "PRINCIPAL", "WF-6590 Series(Red)", 2, tipoPrecio: TipoPrecioControlPrint.Cotizado, puesto: 6),
            new TrabajoControlPrint
            {
                Id = 6, PcOrigen = "PRINCIPAL", Tipo = TipoTrabajoControlPrint.CopiaEnElPanel, Impresora = "Copias en la impresora",
                Documento = "10 copias en B/N", DetectadoUtc = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc), Caras = 10, Paginas = 10,
                Copias = 1, Hojas = 10, PrecioACobrar = 10, Precio = 10, Cobrable = true, Cobro = CobroControlPrint.Cobrado,
                Estado = EstadoTrabajoControlPrint.Impreso, TipoPrecio = TipoPrecioControlPrint.Copias,
            },
        ]);
        archivo.GuardarCotizaciones(
        [
            new CotizacionControlPrint
            {
                Id = 1, CreadaUtc = new DateTime(2026, 11, 3, 16, 0, 0, DateTimeKind.Utc), PcOrigen = "PRINCIPAL", Archivo = "Proyecto.pdf",
                Paginas = 2, Copias = 1, Caras = 2, Hojas = 2, Precio = 8, Cotizado = 6, Resumen = "2 págs", TrabajoId = 5,
                ValeHastaUtc = new DateTime(2026, 11, 18, 16, 0, 0, DateTimeKind.Utc),
            },
            new CotizacionControlPrint
            {
                Id = 2, CreadaUtc = new DateTime(2026, 10, 1, 16, 0, 0, DateTimeKind.Utc), PcOrigen = "PRINCIPAL", Archivo = "Otro.pdf",
                Paginas = 1, Copias = 1, Caras = 1, Hojas = 1, Precio = 2, Cotizado = 2, Resumen = "1 pág",
                ValeHastaUtc = new DateTime(2026, 10, 16, 16, 0, 0, DateTimeKind.Utc),
            },
        ]);
        return Path.Combine(_carpeta, ArchivoRecopilacion.NombreArchivo);
    }

    [Fact]
    public void El_informe_contesta_las_preguntas_sin_documentos_ni_clientes()
    {
        var informe = AnalisisControlPrint.Analizar(Recopilacion());

        Assert.StartsWith("# Lo que dicen los datos de Control Print", informe);
        Assert.Contains("Recopilación de PRINCIPAL (Control Print 1.12.0): 6 trabajos del 2026-10-05 al 2026-11-03", informe);
        // Por mes, PC y cola: las dos colas, y lo de cada una.
        Assert.Contains("| 2026-11 | CAJA2 | EPSON L5590 Series | 1 | 1 |", informe);
        Assert.Contains("Se imprimió en 2 colas", informe);
        // Las copias del panel.
        Assert.Contains("| 2026-11 | 1 | 10 | 0 |", informe);
        // Las cotizaciones: dos guardadas, una usada y cobrada con su precio, una vencida.
        Assert.Contains("- Guardadas: 2 (0 con precio especial).", informe);
        Assert.Contains("- Usadas (ligadas a un trabajo impreso): 1, 50 %.", informe);
        Assert.Contains("- Cobradas con el precio cotizado: 1; trabajos con precio «cotizado»: 1.", informe);
        Assert.Contains("- Vencidas sin usarse: 1.", informe);
        // Los precios puestos y quién los puso; de los clientes, solo cuántos.
        Assert.Contains("| Especial (con nombre) | 1 |", informe);
        Assert.Contains("Ana (1)", informe);
        // Las no vendidas y las hojas que faltaron.
        Assert.Contains("| Salió mal | 1 |", informe);
        Assert.Contains("Con hojas que faltaron (atasco o cancelación): 1", informe);
        // El precio: el módulo calcula lo mismo que Control Print, salvo el que guardó otro precio (los costos cambiaron).
        Assert.Contains("- El mismo precio que calculó Control Print: 4 de 5 (80 %).", informe);
        Assert.Contains("| #2 | 2026-10-06 | 3 |", informe);
        // Nada de los clientes: ni sus documentos ni a quién se le puso el precio.
        Assert.DoesNotContain("CURP", informe);
        Assert.DoesNotContain("Un cliente", informe);
        Assert.DoesNotContain("Tarea.pdf", informe);
    }

    [Fact]
    public void Con_los_costos_del_POS_dice_cuanto_habria_cobrado_el_modulo()
    {
        var delPos = new CostSettings { MarginPercent = 200, Scaling = ScalingMode.None };

        var informe = AnalisisControlPrint.Analizar(Recopilacion(), delPos, "pos.db, sucursal M");

        Assert.Contains("### Con los costos que tiene Control Print hoy", informe);
        Assert.Contains("### Con los costos del POS (pos.db, sucursal M)", informe);
        Assert.Contains("Es lo que el módulo habría cobrado por lo mismo", informe);
    }

    [Fact]
    public void Una_recopilacion_vacia_o_que_no_existe_no_truena()
    {
        Directory.CreateDirectory(_carpeta);
        using (ArchivoRecopilacion.Abrir(_carpeta))
        {
        }

        var informe = AnalisisControlPrint.Analizar(Path.Combine(_carpeta, ArchivoRecopilacion.NombreArchivo));

        Assert.Contains("0 trabajos", informe);
        Assert.Contains("No hay cotizaciones guardadas", informe);
        Assert.Contains("No se registraron copias del panel", informe);
        Assert.Contains("no trae los costos de Control Print", informe);
        Assert.Throws<FileNotFoundException>(() => AnalisisControlPrint.Analizar(Path.Combine(_carpeta, "no-existe.db")));
    }
}
