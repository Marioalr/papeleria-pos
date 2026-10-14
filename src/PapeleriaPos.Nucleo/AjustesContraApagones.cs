namespace PapeleriaPos.Nucleo;

/// <summary>
/// La base contra apagones (entrega 10; en la tienda no hay no-break): los ajustes de cada conexión de SQLite, los mismos
/// en la base del servicio (<c>BaseDeDatos</c>) y en la base local de la caja (<c>AlmacenLocal</c>). SQLite no los guarda
/// en el archivo: son de cada conexión, así que se ponen en cada una al abrirla.
/// <list type="bullet">
/// <item><c>synchronous=FULL</c>: cada venta guardada ya está en el disco; con NORMAL, en WAL, un apagón podría perder
/// las últimas. Hoy SQLite ya viene compilado así, pero no se deja a la suerte.</item>
/// <item><c>busy_timeout</c>: si otra conexión está escribiendo, SQLite reintenta solo en milisegundos. Sin él,
/// Microsoft.Data.Sqlite reintenta cada 150 ms (hasta el plazo del comando, 30 s): la venta y la sincronización a la vez
/// esperaban unos 150 ms; con él, 14 ms (medido, docs/propuesta-interfaz.md). Pasado este plazo vuelve el reintento de
/// 150 ms.</item>
/// </list>
/// </summary>
public static class AjustesContraApagones
{
    /// <summary>El valor que devuelve <c>PRAGMA synchronous</c> con FULL.</summary>
    public const int SincronizacionCompleta = 2;

    /// <summary>El <c>busy_timeout</c>, en milisegundos.</summary>
    public const int EsperaSiEstaOcupadaMs = 5000;

    /// <summary>Lo que se ejecuta en cada conexión al abrirla.</summary>
    public static readonly string Pragmas = $"PRAGMA synchronous=FULL; PRAGMA busy_timeout={EsperaSiEstaOcupadaMs};";

    /// <summary>Lo que no quedó como en <see cref="Pragmas"/>, leído de vuelta en una conexión; nada si está bien.</summary>
    public static string? Problema(int sincronizacion, int esperaMs)
    {
        var problemas = new List<string>();
        if (sincronizacion != SincronizacionCompleta)
            problemas.Add($"synchronous quedó en {sincronizacion} y no en FULL ({SincronizacionCompleta}): un apagón podría perder lo último que se guardó");
        if (esperaMs != EsperaSiEstaOcupadaMs)
            problemas.Add($"busy_timeout quedó en {esperaMs} ms y no en {EsperaSiEstaOcupadaMs}");
        return problemas.Count == 0 ? null : string.Join("; ", problemas) + ".";
    }
}
