namespace PapeleriaPos.Datos;

/// <summary>Contadores de la base. "catalogo" da el número de versión de cada cambio al catálogo.</summary>
public class Secuencia
{
    public const string Catalogo = "catalogo";

    public required string Nombre { get; set; }
    public long Valor { get; set; }

    /// <summary>El siguiente número de un contador ("compras:…", "ordenes:…"); se guarda con la operación.</summary>
    public static async Task<int> SiguienteAsync(PosDbContext db, string nombre, CancellationToken ct = default)
    {
        var secuencia = await db.Secuencias.FindAsync([nombre], ct);
        if (secuencia is null)
        {
            secuencia = new Secuencia { Nombre = nombre };
            db.Secuencias.Add(secuencia);
        }
        return checked((int)++secuencia.Valor);
    }
}
