namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>
/// Una hoja de etiquetas adhesivas tamaño carta (docs/fase-2-inventario.md, entrega 4): cuántas columnas y filas, la
/// medida de cada etiqueta, dónde empieza la primera y de cuánto en cuánto se repiten. Todo en milímetros.
/// </summary>
public sealed record FormatoEtiquetas(
    string Id, string Nombre, int Columnas, int Filas, decimal AnchoMm, decimal AltoMm, decimal MargenSuperiorMm, decimal MargenIzquierdoMm,
    decimal PasoHorizontalMm, decimal PasoVerticalMm)
{
    public const decimal AnchoCartaMm = 215.9m;
    public const decimal AltoCartaMm = 279.4m;
    public const string Propio = "propio";

    public int PorHoja => Columnas * Filas;

    /// <summary>Las hojas carta más comunes en las papelerías (las medidas de Avery; Janel y otras usan las mismas).</summary>
    public static readonly IReadOnlyList<FormatoEtiquetas> Comunes =
    [
        new("5160", "30 por hoja · 2.5 × 6.7 cm (5160)", 3, 10, 66.68m, 25.4m, 12.7m, 4.76m, 69.85m, 25.4m),
        new("5167", "80 por hoja · 1.3 × 4.4 cm (5167)", 4, 20, 44.45m, 12.7m, 12.7m, 7.62m, 52.07m, 12.7m),
        new("5161", "20 por hoja · 2.5 × 10.2 cm (5161)", 2, 10, 101.6m, 25.4m, 12.7m, 3.97m, 106.36m, 25.4m),
        new("5163", "10 por hoja · 5.1 × 10.2 cm (5163)", 2, 5, 101.6m, 50.8m, 12.7m, 3.97m, 106.36m, 50.8m),
    ];

    public static FormatoEtiquetas PorOmision => Comunes[0];

    /// <summary>Qué está mal en una hoja propia (no cabe en la carta, se enciman); vacío si está bien.</summary>
    public string? Problema()
    {
        if (Columnas < 1 || Filas < 1)
            return "Pon cuántas columnas y filas tiene la hoja.";
        if (AnchoMm < 10 || AltoMm < 8)
            return "La etiqueta es demasiado chica para un código de barras (mínimo 1 × 0.8 cm).";
        if (MargenSuperiorMm < 0 || MargenIzquierdoMm < 0)
            return "Los márgenes no pueden ser negativos.";
        if (Columnas > 1 && PasoHorizontalMm < AnchoMm || Filas > 1 && PasoVerticalMm < AltoMm)
            return "Las etiquetas se enciman: de una a la siguiente tiene que haber al menos lo que mide una.";
        if (MargenIzquierdoMm + (Columnas - 1) * PasoHorizontalMm + AnchoMm > AnchoCartaMm + 0.5m
            || MargenSuperiorMm + (Filas - 1) * PasoVerticalMm + AltoMm > AltoCartaMm + 0.5m)
            return "Con esas medidas las etiquetas no caben en una hoja carta.";
        return null;
    }
}
