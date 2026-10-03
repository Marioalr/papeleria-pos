using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PapeleriaPos.Nucleo.Usuarios;

/// <summary>PIN de 4 a 6 dígitos, guardado con PBKDF2 (nunca en claro): "pbkdf2-sha256$iteraciones$sal$hash".</summary>
public static class Pin
{
    private const int Iteraciones = 100_000;
    private const int BytesSal = 16;
    private const int BytesHash = 32;

    public static bool EsValido(string pin) => pin.Length is >= 4 and <= 6 && pin.All(char.IsAsciiDigit);

    public static string Hash(string pin)
    {
        if (!EsValido(pin))
            throw new ArgumentException("El PIN debe tener de 4 a 6 dígitos.", nameof(pin));

        var sal = RandomNumberGenerator.GetBytes(BytesSal);
        var hash = Derivar(pin, sal, Iteraciones);
        return $"pbkdf2-sha256${Iteraciones}${Convert.ToBase64String(sal)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verificar(string pin, string guardado)
    {
        var partes = guardado.Split('$');
        if (partes.Length != 4 || partes[0] != "pbkdf2-sha256" || !EsValido(pin))
            return false;

        var iteraciones = int.Parse(partes[1], CultureInfo.InvariantCulture);
        var esperado = Convert.FromBase64String(partes[3]);
        var calculado = Derivar(pin, Convert.FromBase64String(partes[2]), iteraciones);
        return CryptographicOperations.FixedTimeEquals(esperado, calculado);
    }

    private static byte[] Derivar(string pin, byte[] sal, int iteraciones) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), sal, iteraciones, HashAlgorithmName.SHA256, BytesHash);
}
