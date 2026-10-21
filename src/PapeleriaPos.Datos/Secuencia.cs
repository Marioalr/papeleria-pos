namespace PapeleriaPos.Datos;

/// <summary>
/// Contadores de la base. "catalogo" da el número de versión de cada cambio a lo que lleva la copia de las cajas
/// (<see cref="LoQueLlevaLaCopia"/>); "existencias", el de cada cambio de existencia (una venta sube esta y no la del
/// catálogo); "credito" cambia con cada fiado, abono o cambio del crédito (así las cajas saben cuándo volver a pedir los
/// saldos sin bajar el catálogo). Las tres suben dentro de la transacción del cambio.
/// </summary>
public class Secuencia
{
    public const string Catalogo = "catalogo";
    public const string Credito = "credito";
    public const string Existencias = "existencias";

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

    /// <summary>
    /// Sube uno de los contadores de versión en la base, en la transacción abierta (la que tiene el candado de escritura), y
    /// dice cuánto quedó: dos cambios a la vez dan dos versiones, y ninguna caja ve un cambio sin su versión.
    /// </summary>
    internal static string Subir(string nombre) =>
        $"INSERT INTO \"Secuencias\" (\"Nombre\", \"Valor\") VALUES ('{nombre}', 1) ON CONFLICT (\"Nombre\") DO UPDATE SET \"Valor\" = \"Valor\" + 1 RETURNING \"Valor\"";
}
