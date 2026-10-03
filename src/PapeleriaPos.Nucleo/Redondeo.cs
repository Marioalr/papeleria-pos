namespace PapeleriaPos.Nucleo;

public static class Redondeo
{
    /// <summary>Divide redondeando al entero más cercano; los medios se alejan de cero (1.5 → 2, −1.5 → −2).</summary>
    public static long Dividir(Int128 numerador, Int128 denominador)
    {
        if (denominador == 0)
            throw new DivideByZeroException();

        var negativo = (numerador < 0) != (denominador < 0);
        var n = Int128.Abs(numerador);
        var d = Int128.Abs(denominador);
        var resultado = (n + d / 2) / d;
        return checked((long)(negativo ? -resultado : resultado));
    }
}
