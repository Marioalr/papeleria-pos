using System.Globalization;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que se escribe en Inventario › Agregar inventario (como en eleventa, 2026-10-24): «Cuántos llegaron» y «Costo de cada
/// uno». Los dos con la regla de los números del POS, la de los productos en Excel (<see cref="LecturaDeCeldas.NumeroDeTexto"/>,
/// revisión del PR #138): el punto es el decimal y la coma solo separa los miles («1,000», «1,250.50»). Una coma que podría ser
/// de decimales («12,50», «2,5») no se adivina: se pide con punto, para que nunca entre otro número. El servicio vuelve a revisar
/// todo; esto es para decirlo antes.
/// </summary>
public static class AgregarInventarioEnPantalla
{
    /// <summary>
    /// Lo escrito en «Cuántos llegaron», en milésimas de la unidad base: más de cero, hasta <see cref="EntradaInventario.CantidadMaxima"/>
    /// (más es un error de dedo, o un código que cayó ahí) y con hasta tres decimales; lo que se cuenta por pieza, entero y sin
    /// punto. Vacío si no sirve, con el porqué en <paramref name="problema"/>.
    /// </summary>
    /// <param name="producto">Su nombre, para decir que se cuenta en piezas enteras.</param>
    public static long? Cantidad(string? texto, UnidadBase unidad, string producto, out string? problema)
    {
        texto = (texto ?? "").Trim();
        var fracciones = unidad.AdmiteFracciones();
        var comaDecimal = false;
        // Con «$» es dinero, no cuántos.
        var numero = texto.Length == 0 || texto.Contains('$') ? null : LecturaDeCeldas.NumeroDeTexto(texto, out comaDecimal);
        problema = texto.Length == 0 ? "Escribe cuántos llegaron."
            : comaDecimal && fracciones ? $"Los decimales van con punto: {ConPunto(texto, "2.5")} (la coma solo separa los miles: 1,000)."
            : comaDecimal ? $"«{producto}» se cuenta en piezas enteras: escribe cuántos, sin decimales (la coma solo separa los miles: 1,000)."
            : numero is null ? NoEsCantidad(texto, fracciones)
            : !fracciones && texto.Contains('.') ? $"«{producto}» se cuenta en piezas enteras: escribe cuántos, sin punto."
            : numero is not (> 0 and <= EntradaInventario.CantidadMaxima) || decimal.Round(numero.Value, 3) != numero ? NoEsCantidad(texto, fracciones)
            : null;
        return problema is null ? Nucleo.Cantidad.Unidades(numero!.Value).Milesimas : null;
    }

    private static string NoEsCantidad(string texto, bool fracciones) =>
        $"«{texto}» no es una cantidad: escribe cuántos llegaron, más de cero y hasta {EntradaInventario.CantidadMaxima.ToString("N0", Formato.Mexico)}"
        + (fracciones ? ", con hasta tres decimales." : ", enteros.");

    /// <summary>
    /// Lo escrito en «Costo de cada uno», en centavos y redondeado al centavo («$12.50», «12.5», «1,250»), de $0.00 a
    /// <see cref="EntradaInventario.CostoMaximoCentavos"/>. Vacío si no sirve, con el porqué en <paramref name="problema"/>:
    /// «12,50» no se toma como $1,250.00 ni como $12.50, se pide con punto.
    /// </summary>
    public static long? Costo(string? texto, out string? problema)
    {
        texto = (texto ?? "").Trim();
        var pesos = LecturaDeCeldas.NumeroDeTexto(texto, out var comaDecimal);
        problema = comaDecimal ? $"Los centavos van con punto: {ConPunto(texto, "12.50")} (la coma solo separa los miles: 1,250.00)."
            // El tope se revisa antes de pasar a centavos: un número de 28 cifras no cabe al multiplicarlo.
            : pesos is not { } valor || valor < 0 || valor > EntradaInventario.CostoMaximoCentavos / 100m
                ? $"Revisa el costo de cada uno: «{texto}» no es dinero de $0.00 a {Formato.Dinero(EntradaInventario.CostoMaximoCentavos)}."
            : null;
        return problema is null ? (long)Math.Round(pesos!.Value * 100, MidpointRounding.AwayFromZero) : null;
    }

    /// <summary>
    /// Lo escrito con la coma de decimales, como va: «12,50» → «12.50». Solo si trae una coma con una a tres cifras después y
    /// ningún punto; si no, <paramref name="ejemplo"/>.
    /// </summary>
    private static string ConPunto(string texto, string ejemplo)
    {
        var coma = texto.IndexOf(',');
        var despues = texto.Length - coma - 1;
        return texto.Count(c => c == ',') == 1 && !texto.Contains('.') && despues is >= 1 and <= 3 && coma > 0 ? texto.Replace(',', '.') : ejemplo;
    }

    /// <summary>Un costo como se escribe en su cuadro: «12.50».</summary>
    public static string TextoDeCosto(long centavos) => (centavos / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>La unidad en plural, para «Cuántos llegaron (piezas)» y «Hay 6 piezas».</summary>
    public static string Unidades(UnidadBase unidad) => unidad switch
    {
        UnidadBase.Metro => "metros",
        UnidadBase.Kilogramo => "kilos",
        UnidadBase.Litro => "litros",
        _ => "piezas",
    };

    /// <summary>La unidad en singular, para «Costo de cada pieza».</summary>
    public static string Unidad(UnidadBase unidad) => unidad switch
    {
        UnidadBase.Metro => "metro",
        UnidadBase.Kilogramo => "kilo",
        UnidadBase.Litro => "litro",
        _ => "pieza",
    };
}
