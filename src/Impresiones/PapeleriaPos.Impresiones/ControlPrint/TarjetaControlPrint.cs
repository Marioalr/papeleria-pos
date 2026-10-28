using System.Globalization;
using System.Text;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.ControlPrint;

/// <summary>
/// Cómo se ve un trabajo de Control Print en su tarjeta de F2, con los textos de Control Print 1.12 (Labels.Summary y
/// JobItemViewModel: el resumen, los chips y «Revisar hojas»). Solo arma lo que se muestra: nada de aquí habla con Control Print.
/// </summary>
public static class TarjetaControlPrint
{
    public static TrabajoControlPrintDto De(TrabajoControlPrint t, bool conCosto)
    {
        var detectado = Utc(t.DetectadoUtc);
        return new TrabajoControlPrintDto(
            t.Id, detectado, Titulo(t), Resumen(t), HoraMexico.ALocal(detectado), t.PcOrigen.Trim(),
            string.IsNullOrWhiteSpace(t.AtendioNombre) ? null : t.AtendioNombre.Trim(), Centavos(PrecioACobrar(t)),
            Especial: t.TipoPrecio == TipoPrecioControlPrint.Especial
                ? string.IsNullOrWhiteSpace(t.ClienteDelPrecio) ? "precio especial" : t.ClienteDelPrecio.Trim()
                : null,
            Cotizado: t.TipoPrecio == TipoPrecioControlPrint.Cotizado,
            CopiasEnImpresora: t.Tipo == TipoTrabajoControlPrint.CopiaEnElPanel,
            RevisarHojas: RevisarHojas(t),
            CostoCentavos: conCosto ? Centavos(t.CostoTotal) : null);
    }

    /// <summary>Lo que se cobra: el precio puesto (especial, cotizado, solo lo que salió…) o, si no hay, el calculado.</summary>
    public static decimal PrecioACobrar(TrabajoControlPrint t) => t.PrecioPuesto ?? t.Precio;

    /// <summary>La etiqueta que le pusieron o, si no hay, el documento (su DisplayName).</summary>
    public static string Titulo(TrabajoControlPrint t) =>
        !string.IsNullOrWhiteSpace(t.Etiqueta) ? t.Etiqueta.Trim()
        : !string.IsNullOrWhiteSpace(t.Documento) ? t.Documento.Trim()
        : $"Trabajo #{t.Id}";

    /// <summary>«3 págs × 2 copias · Color · Carta · Papel normal · Dúplex», como Labels.Summary de Control Print.</summary>
    public static string Resumen(TrabajoControlPrint t)
    {
        // Lo que importa para cobrar es el contenido: un trabajo en modo color sin tinta de color es B/N (como en Control Print).
        var usaColor = t.Color && t.TintaC + t.TintaM + t.TintaY > 0;
        var paginas = t.Paginas == 1 ? "1 pág" : $"{t.Paginas} págs";
        var partes = new List<string> { t.Copias > 1 ? $"{paginas} × {t.Copias} copias" : paginas, usaColor ? "Color" : "B/N" };
        if (!string.IsNullOrWhiteSpace(t.TamanoPapel))
            partes.Add(t.TamanoPapel.Trim());
        if (!string.IsNullOrWhiteSpace(t.NombrePapel))
            partes.Add(t.NombrePapel.Trim());
        if (Calidad(t.Calidad) is { } calidad)
            partes.Add(calidad);
        if (t.DobleCara)
            partes.Add("Dúplex");
        if (t.PaginasPorHoja > 1)
            partes.Add($"{t.PaginasPorHoja} por hoja");
        return string.Join(" · ", partes);
    }

    /// <summary>La calidad solo si no es la de siempre, como en Control Print.</summary>
    private static string? Calidad(string calidad) => calidad switch
    {
        "Draft" => "Borrador",
        "HighQuality" => "Alta",
        "StandardVivid" => "Estándar vívido",
        _ => null,
    };

    /// <summary>
    /// «Revisar hojas»: la impresora contó menos hojas y todavía no se resolvió (ni «solo lo que salió» ni no vendida), con lo
    /// que contó (ShortfallText de Control Print). Vacío si no hace falta.
    /// </summary>
    public static string? RevisarHojas(TrabajoControlPrint t)
    {
        if (t.CarasContadas is not { } contadas || contadas >= t.Caras || t.Cobro == CobroControlPrint.NoVendido
            || t.TipoPrecio == TipoPrecioControlPrint.SoloLoQueSalio)
            return null;
        return contadas == 0
            ? $"La impresora no contó ninguna de las {t.Caras} hojas: ¿se canceló o se atascó antes de salir?"
            : $"La impresora contó {contadas} de {t.Caras} hojas: ¿atasco o cancelación en la impresora?";
    }

    /// <summary>
    /// Un número que cambia cuando cambia algo de lo que se ve (FNV-1a de 64 bits sobre las tarjetas): con el mismo, la caja no
    /// vuelve a pedir ni a pintar la lista. No cuenta el costo: es el mismo para quien lo ve y para quien no.
    /// </summary>
    public static long Version(IEnumerable<TrabajoControlPrintDto> tarjetas, params object?[] ademas)
    {
        var texto = new StringBuilder();
        foreach (var t in tarjetas)
            texto.Append(CultureInfo.InvariantCulture, $"{t.Id}|{t.DetectadoUtc.Ticks}|{t.Titulo}|{t.Resumen}|{t.Pc}|{t.Atendio}|{t.PrecioCentavos}|")
                .Append(CultureInfo.InvariantCulture, $"{t.Especial}|{t.Cotizado}|{t.CopiasEnImpresora}|{t.RevisarHojas}\n");
        foreach (var a in ademas)
            texto.Append(CultureInfo.InvariantCulture, $"{a}\n");
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(texto.ToString()))
            hash = (hash ^ b) * 1099511628211UL;
        // Positivo y distinto de cero: cero es «la caja no tiene ninguna».
        return (long)(hash >> 1) | 1;
    }

    public static long Centavos(decimal pesos) => (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero);

    /// <summary>Control Print manda las fechas con su «Z»; si alguna llega sin ella, también es UTC.</summary>
    public static DateTime Utc(DateTime fecha) => fecha.Kind switch
    {
        DateTimeKind.Utc => fecha,
        DateTimeKind.Local => fecha.ToUniversalTime(),
        _ => DateTime.SpecifyKind(fecha, DateTimeKind.Utc),
    };
}
