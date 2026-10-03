namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>Códigos de fábrica EAN-13: 12 dígitos más uno de verificación.</summary>
public static class Ean13
{
    public static int DigitoVerificador(ReadOnlySpan<char> doceDigitos)
    {
        if (doceDigitos.Length != 12)
            throw new ArgumentException("Se necesitan 12 dígitos.", nameof(doceDigitos));

        var suma = 0;
        for (var i = 0; i < 12; i++)
        {
            if (!char.IsAsciiDigit(doceDigitos[i]))
                throw new ArgumentException("Solo se admiten dígitos.", nameof(doceDigitos));
            suma += (doceDigitos[i] - '0') * (i % 2 == 0 ? 1 : 3);
        }
        return (10 - suma % 10) % 10;
    }

    public static string Completar(string doceDigitos) => doceDigitos + DigitoVerificador(doceDigitos);

    public static bool EsValido(string codigo) =>
        codigo.Length == 13 && codigo.All(char.IsAsciiDigit) && DigitoVerificador(codigo.AsSpan(0, 12)) == codigo[12] - '0';
}
