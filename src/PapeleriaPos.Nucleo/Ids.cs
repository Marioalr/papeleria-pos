namespace PapeleriaPos.Nucleo;

/// <summary>IDs UUIDv7: se generan donde nace el registro y no chocan entre cajas ni sucursales.</summary>
public static class Ids
{
    private static readonly AsyncLocal<Func<Guid>?> Generador = new();

    public static Guid Nuevo() => Generador.Value?.Invoke() ?? Guid.CreateVersion7();

    /// <summary>
    /// Cambia de dónde salen los IDs mientras dure el bloque <c>using</c>. Lo usa el generador de datos ficticios para
    /// que la misma semilla dé siempre los mismos IDs.
    /// </summary>
    public static IDisposable Usar(Func<Guid> generador)
    {
        var anterior = Generador.Value;
        Generador.Value = generador;
        return new Restaurar(anterior);
    }

    /// <summary>Un UUIDv7 armado con la hora y bytes dados: 48 bits de milisegundos, versión 7 y variante RFC 9562.</summary>
    public static Guid Version7(DateTimeOffset momento, ReadOnlySpan<byte> aleatorios)
    {
        if (aleatorios.Length < 10)
            throw new ArgumentException("Se necesitan 10 bytes aleatorios.", nameof(aleatorios));

        Span<byte> bytes = stackalloc byte[16];
        var ms = momento.ToUnixTimeMilliseconds();
        for (var i = 0; i < 6; i++)
            bytes[i] = (byte)(ms >> (8 * (5 - i)));
        aleatorios[..10].CopyTo(bytes[6..]);
        bytes[6] = (byte)(0x70 | (bytes[6] & 0x0F));
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));
        return new Guid(bytes, bigEndian: true);
    }

    private sealed class Restaurar(Func<Guid>? anterior) : IDisposable
    {
        public void Dispose() => Generador.Value = anterior;
    }
}

/// <summary>
/// La misma solicitud (el mismo Id, que pone quien la manda para que un reintento no la repita) con otro contenido: no es un
/// reintento, y no se hace nada (revisión del PR #138). El servicio contesta 409 con el mensaje y <see cref="Codigo"/>.
/// </summary>
public sealed class SolicitudDistinta(string mensaje) : InvalidOperationException(mensaje)
{
    public const string Codigo = "otra-solicitud";
}
