using System.Text.Json;
using System.Text.Json.Serialization;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Pruebas.ControlPrint;

// Los datos como los tiene Control Print 1.12 (ControlPrint.Core: JobRecord, QuoteRecord, PrinterStatus), con sus nombres en
// inglés: el servidor falso los manda en camelCase y con los enum como texto, como su API. Se escriben aparte del cliente a
// propósito: si el cliente leyera mal un nombre o un enum, las pruebas lo notan. Todo es inventado.

public enum JobState { Detected, Analyzed, Printed, Cancelled, NotAnalyzed }

public enum PaymentState { Pending, Paid, Unsold }

public enum UnsoldReason { NotWanted = 1, BadPrint = 2, PaperJam = 3, Other = 9 }

public enum PriceKind { Calculated, Manual, Special, Partial, Copies, Quoted }

public enum JobKind { Print, PanelCopy }

public enum AnalysisLevel { Estimated = 1, Measured = 2 }

public sealed class JobRecordCp
{
    public long Id { get; set; }
    public string SourcePc { get; set; } = "";
    public JobKind Kind { get; set; }
    public string Printer { get; set; } = "";
    public int SpoolJobId { get; set; }
    public DateTime SubmittedUtc { get; set; }
    public string Document { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Datatype { get; set; } = "";
    public DateTime DetectedUtc { get; set; }
    public DateTime? AnalyzedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public JobState State { get; set; }
    public int Pages { get; set; }
    public int Copies { get; set; } = 1;
    public int Sides { get; set; }
    public int ColorSides { get; set; }
    public int Sheets { get; set; }
    public bool Color { get; set; }
    public string Quality { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string MediaName { get; set; } = "";
    public string PaperSize { get; set; } = "";
    public bool Duplex { get; set; }
    public int PagesPerSheet { get; set; } = 1;
    public double InkC { get; set; }
    public double InkM { get; set; }
    public double InkY { get; set; }
    public double InkK { get; set; }
    public decimal CostInk { get; set; }
    public decimal CostPaper { get; set; }
    public decimal CostMaintenance { get; set; }
    public decimal CostPower { get; set; }
    public decimal CostExtras { get; set; }
    public decimal CostDepreciation { get; set; }
    public decimal CostTotal { get; set; }
    public decimal MarginPercent { get; set; }
    public decimal? PaperMarginPercent { get; set; }
    public decimal Price { get; set; }
    public decimal? PriceOverride { get; set; }
    public PriceKind PriceKind { get; set; }
    public DateTime? PriceSetUtc { get; set; }
    public string? PriceSetBy { get; set; }
    public string? SpecialName { get; set; }
    public AnalysisLevel? Level { get; set; }
    public string? Note { get; set; }
    public string? DetailJson { get; set; }
    public byte[]? Thumbnail { get; set; }
    public string? Label { get; set; }
    public PaymentState Payment { get; set; }
    public string? Notes { get; set; }
    public string? PaymentBy { get; set; }
    public DateTime? PaymentUtc { get; set; }
    public UnsoldReason? UnsoldReason { get; set; }
    public string? UnsoldNote { get; set; }
    public long? OwnerId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerSetBy { get; set; }
    public DateTime? OwnerSetUtc { get; set; }
    public bool IsChargeable => State is JobState.Analyzed or JobState.Printed;
    public int? CountedSides { get; set; }
    public bool HasPrintShortfall => CountedSides is { } c && c < Sides;
    public decimal EffectivePrice => PriceOverride ?? Price;
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Document : Label!;

    /// <summary>Lo que mandaría una versión más nueva de Control Print.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Un estado que esta versión no conoce, en lugar de <see cref="State"/> (lo pone el servidor falso en el JSON).</summary>
    [JsonIgnore] public string? EstadoQueNoConoce { get; set; }

    public JobRecordCp SinMiniatura()
    {
        var copia = (JobRecordCp)MemberwiseClone();
        copia.Thumbnail = null;
        return copia;
    }
}

public sealed class QuoteRecordCp
{
    public long Id { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? CreatedBy { get; set; }
    public long? OwnerId { get; set; }
    public string SourcePc { get; set; } = "";
    public string FileName { get; set; } = "";
    public int PageCount { get; set; }
    public string? PageRange { get; set; }
    public int Pages { get; set; }
    public int Copies { get; set; } = 1;
    public int Sides { get; set; }
    public int Sheets { get; set; }
    public int ColorSides { get; set; }
    public object Options { get; set; } = new { mediaType = "Plain", size = "Carta", color = true, quality = "Standard", duplex = false, copies = 1, pagesPerSheet = 1 };
    public decimal CostTotal { get; set; }
    public decimal Price { get; set; }
    public decimal Charged { get; set; }
    public bool Special { get; set; }
    public string? ClientName { get; set; }
    public string Summary { get; set; } = "";
    public DateTime ValidUntilUtc { get; set; }
    public long? JobId { get; set; }
}

public sealed record PrinterStatusCp
{
    public DateTime TakenUtc { get; init; }
    public int? InkK { get; init; }
    public int? InkC { get; init; }
    public int? InkM { get; init; }
    public int? InkY { get; init; }
    public int? Maintenance { get; init; }
    public long? LifeCount { get; init; }
    public long? ColorCount { get; init; }
    public long? MonoCount { get; init; }
    public string? Serial { get; init; }
    public List<string> Errors { get; init; } = [];
}

/// <summary>Una papelería inventada vista desde Control Print: trabajos de tres PC, copias del panel, no vendidas, cotizaciones…</summary>
public static class DatosDeControlPrint
{
    /// <summary>Un PNG de 1×1 de verdad (la firma y sus bloques), como las miniaturas de Control Print.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");

    public const long Dueno = 1, Cajera = 2, Invitado = 3;

    /// <summary>
    /// <paramref name="cuantos"/> trabajos repartidos entre <paramref name="desdeUtc"/> y <paramref name="hastaUtc"/>, en horas de
    /// la tienda; el número sube con la hora, como en Control Print. Uno de cada 8 es de copias del panel.
    /// </summary>
    public static List<JobRecordCp> Trabajos(int cuantos, DateTime desdeUtc, DateTime hastaUtc, int semilla = 2026)
    {
        var azar = new Random(semilla);
        var pcs = new[] { "PRINCIPAL", "CAJA2", "DISENO" };
        var trabajos = new List<JobRecordCp>();
        var paso = (hastaUtc - desdeUtc) / cuantos;
        for (var i = 0; i < cuantos; i++)
        {
            var detectado = desdeUtc + paso * i + TimeSpan.FromMinutes(azar.Next(0, Math.Max(1, (int)paso.TotalMinutes / 2)));
            var copia = i % 8 == 7;
            var caras = copia ? azar.Next(1, 30) : azar.Next(1, 12);
            var color = azar.Next(3) == 0;
            var costo = Math.Round(0.35m * caras + (color ? 0.9m * caras : 0), 4);
            var precio = Math.Max(2m, Math.Ceiling(costo * 2));
            var estado = (i % 23) switch { 5 => JobState.Cancelled, 11 => JobState.NotAnalyzed, 17 => JobState.Analyzed, _ => JobState.Printed };
            var cobro = (i % 13) switch { 4 => PaymentState.Unsold, 9 or 10 => PaymentState.Pending, _ => PaymentState.Paid };
            var dueno = (i % 5) switch { 0 => (Dueno, "Dueño de prueba"), 1 or 2 => (Cajera, "Cajera de prueba"), _ => (Invitado, "Sin sesión") };
            var trabajo = new JobRecordCp
            {
                Id = i + 1,
                SourcePc = copia ? "PRINCIPAL" : pcs[i % pcs.Length],
                Kind = copia ? JobKind.PanelCopy : JobKind.Print,
                Printer = copia ? "Copias en la impresora" : "WF-6590 Series(Red)",
                SpoolJobId = copia ? 0 : 100 + i,
                SubmittedUtc = detectado.AddSeconds(-2),
                Document = copia ? $"{caras} copias {(color ? "a color" : "en B/N")}" : $"Documento de prueba {i + 1}.pdf",
                UserName = copia ? "" : "Mostrador",
                Machine = copia ? "" : pcs[i % pcs.Length],
                Datatype = copia ? "" : "NT EMF 1.008",
                DetectedUtc = detectado,
                AnalyzedUtc = estado is JobState.Detected or JobState.NotAnalyzed || copia ? null : detectado.AddSeconds(3),
                FinishedUtc = estado is JobState.Printed or JobState.Cancelled ? detectado.AddSeconds(40) : null,
                State = copia ? JobState.Printed : estado,
                Pages = caras,
                Copies = 1,
                Sides = caras,
                ColorSides = color ? caras : 0,
                Sheets = caras,
                Color = color,
                Quality = "Standard",
                MediaType = "Plain",
                MediaName = "Papel normal",
                PaperSize = i % 9 == 0 ? "Oficio" : "Carta",
                Duplex = false,
                InkC = color ? 0.12 * caras : 0,
                InkM = color ? 0.11 * caras : 0,
                InkY = color ? 0.10 * caras : 0,
                InkK = 0.2 * caras,
                CostInk = Math.Round(costo * 0.6m, 4),
                CostPaper = Math.Round(costo * 0.3m, 4),
                CostMaintenance = Math.Round(costo * 0.05m, 4),
                CostPower = Math.Round(costo * 0.02m, 4),
                CostDepreciation = Math.Round(costo * 0.03m, 4),
                CostTotal = costo,
                MarginPercent = 100,
                PaperMarginPercent = i % 4 == 0 ? 30 : null,
                Price = precio,
                PriceOverride = copia ? precio + 1 : i % 17 == 3 ? precio - 1 : null,
                PriceKind = copia ? PriceKind.Copies : i % 17 == 3 ? PriceKind.Special : PriceKind.Calculated,
                PriceSetUtc = copia || i % 17 == 3 ? detectado.AddMinutes(1) : null,
                PriceSetBy = copia || i % 17 == 3 ? "Cajera de prueba" : null,
                SpecialName = !copia && i % 17 == 3 ? "Escuela de prueba" : null,
                Level = copia ? null : i % 6 == 0 ? AnalysisLevel.Estimated : AnalysisLevel.Measured,
                Note = i % 6 == 0 ? "Sin el contenido: estimado por páginas." : null,
                DetailJson = copia ? null : $$"""{"cost":{"total":{{costo.ToString(System.Globalization.CultureInfo.InvariantCulture)}}},"pages":[{"C":0.1,"M":0.1,"Y":0.1,"K":0.2}]}""",
                Thumbnail = copia || i % 3 == 0 ? null : Png,
                Label = i % 7 == 0 ? $"Cliente {i}" : null,
                Payment = copia ? PaymentState.Paid : cobro,
                Notes = i % 10 == 0 ? "Nota de prueba" : null,
                PaymentBy = cobro == PaymentState.Pending && !copia ? null : "Cajera de prueba",
                PaymentUtc = cobro == PaymentState.Pending && !copia ? null : detectado.AddMinutes(2),
                UnsoldReason = !copia && cobro == PaymentState.Unsold ? (i % 2 == 0 ? UnsoldReason.PaperJam : UnsoldReason.Other) : null,
                UnsoldNote = !copia && cobro == PaymentState.Unsold && i % 2 != 0 ? "Se manchó" : null,
                OwnerId = dueno.Item1,
                OwnerName = dueno.Item2,
                CountedSides = i % 19 == 2 ? caras - 1 : null,
            };
            trabajos.Add(trabajo);
        }
        return trabajos;
    }

    public static List<QuoteRecordCp> Cotizaciones(DateTime desdeUtc) =>
    [
        new() { Id = 1, CreatedUtc = desdeUtc.AddDays(2), CreatedBy = "Cajera de prueba", OwnerId = Cajera, SourcePc = "CAJA2", FileName = "Tesis.pdf",
            PageCount = 80, Pages = 80, Sides = 80, Sheets = 80, ColorSides = 12, CostTotal = 42.5m, Price = 85, Charged = 85,
            Summary = "80 páginas, 12 a color: $85", ValidUntilUtc = desdeUtc.AddDays(17), JobId = 5 },
        new() { Id = 2, CreatedUtc = desdeUtc.AddDays(9), CreatedBy = "Dueño de prueba", OwnerId = Dueno, SourcePc = "PRINCIPAL", FileName = "Folletos.pdf",
            PageCount = 2, PageRange = "1", Pages = 1, Copies = 50, Sides = 50, Sheets = 50, ColorSides = 50, CostTotal = 60, Price = 120, Charged = 100,
            Special = true, ClientName = "Escuela de prueba", Summary = "50 folletos a color: $100", ValidUntilUtc = desdeUtc.AddDays(24) },
        new() { Id = 3, CreatedUtc = desdeUtc.AddDays(30), CreatedBy = "Sin sesión", OwnerId = Invitado, SourcePc = "CAJA2", FileName = "Acta.pdf",
            PageCount = 1, Pages = 1, Sides = 1, Sheets = 1, CostTotal = 0.4m, Price = 2, Charged = 2, Summary = "1 página en B/N: $2",
            ValidUntilUtc = desdeUtc.AddDays(45) },
    ];

    /// <summary>Una lectura cada 12 horas, con la tinta bajando y los contadores subiendo.</summary>
    public static List<PrinterStatusCp> Lecturas(DateTime desdeUtc, DateTime hastaUtc)
    {
        var lecturas = new List<PrinterStatusCp>();
        var i = 0;
        for (var tomada = desdeUtc; tomada < hastaUtc; tomada = tomada.AddHours(12), i++)
            lecturas.Add(new PrinterStatusCp
            {
                TakenUtc = tomada,
                InkK = Math.Max(5, 95 - i),
                InkC = Math.Max(5, 90 - i / 2),
                InkM = Math.Max(5, 88 - i / 2),
                InkY = Math.Max(5, 91 - i / 2),
                Maintenance = 70,
                LifeCount = 120_000 + i * 140,
                ColorCount = 40_000 + i * 40,
                MonoCount = 80_000 + i * 100,
                Errors = i == 3 ? ["Atasco de papel"] : [],
            });
        return lecturas;
    }

    /// <summary>Los ajustes de la app como los manda Control Print (PascalCase, de su SettingsStore) y un campo que parece un secreto.</summary>
    public static string Ajustes(string visibilidad = "All") => $$"""
        {
          "PrinterHost": "192.0.2.50",
          "StatusPollMinutes": 30,
          "AdminIdleMinutes": 5,
          "EmployeeIdleMinutes": 60,
          "EndAdminSessionOnClose": true,
          "SetupChecklistDismissed": true,
          "BackupFolder": "",
          "BackupKeepDays": 30,
          "JobsVisibility": "{{visibilidad}}",
          "Guest": { "CanCharge": true, "CanApplySpecial": true, "CanMarkUnsold": true, "CanChargePrintedOnly": true },
          "UpdateTokenProtected": "SECRETO-DE-PRUEBA-NO-DEBE-SALIR"
        }
        """;

    public const string Calibracion = """
        { "ComputedUtc": "2026-10-20T18:00:00Z", "Channels": [ { "Channel": "Black", "ObservedPoints": 12.5, "EstimatedPoints": 11.0 } ],
          "PrinterPages": 3200, "OurSides": 3050, "MaintenanceQuality": "Good" }
        """;

    /// <summary>El día de México de una fecha UTC (como Control Print en la caja principal).</summary>
    public static DateOnly DiaLocal(DateTime utc) => DateOnly.FromDateTime(HoraMexico.ALocal(utc));
}
