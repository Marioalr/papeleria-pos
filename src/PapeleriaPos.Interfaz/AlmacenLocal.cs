using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PapeleriaPos.Interfaz;

/// <summary>Algo que subir a la caja principal: la ruta de la API y el cuerpo en JSON, en el orden en que pasó.</summary>
public sealed record ElementoBandeja(long Orden, Guid Id, string Ruta, string Json, string? Error);

/// <summary>
/// Lo que cambia el efectivo y los totales del turno. La caja anota todo lo de su turno (en línea o sin conexión) para
/// poder hacer el corte aunque la caja principal no conteste.
/// </summary>
public sealed record AnotacionDiario(
    Guid Id, Guid TurnoId, int Ventas = 0, long Efectivo = 0, long Tarjeta = 0, long Transferencia = 0, long Entradas = 0, long Salidas = 0,
    long Devoluciones = 0, int Cancelaciones = 0);

/// <summary>
/// La base local de la caja (una SQLite propia, junto a su configuración): la copia del catálogo y demás datos para vender
/// sin conexión, la bandeja de salida y el diario del turno.
/// </summary>
public sealed class AlmacenLocal
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _cadena;

    public AlmacenLocal(string ruta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ruta))!);
        _cadena = new SqliteConnectionStringBuilder { DataSource = ruta }.ToString();
        using var conexion = Abrir();
        Ejecutar(conexion, null, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Ajustes (Clave TEXT PRIMARY KEY, Valor TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Bandeja (
                Orden INTEGER PRIMARY KEY AUTOINCREMENT, Id TEXT NOT NULL UNIQUE, Ruta TEXT NOT NULL, Json TEXT NOT NULL,
                CreadoUtc TEXT NOT NULL, SubidoUtc TEXT, Error TEXT);
            CREATE TABLE IF NOT EXISTS Diario (
                Id TEXT PRIMARY KEY, TurnoId TEXT NOT NULL, Ventas INTEGER NOT NULL, Efectivo INTEGER NOT NULL, Tarjeta INTEGER NOT NULL,
                Transferencia INTEGER NOT NULL, Entradas INTEGER NOT NULL, Salidas INTEGER NOT NULL, Devoluciones INTEGER NOT NULL,
                Cancelaciones INTEGER NOT NULL);
            """);
    }

    public T? Leer<T>(string clave)
    {
        using var conexion = Abrir();
        using var comando = Comando(conexion, null, "SELECT Valor FROM Ajustes WHERE Clave = $clave", ("$clave", clave));
        return comando.ExecuteScalar() is string valor ? JsonSerializer.Deserialize<T>(valor, Json) : default;
    }

    /// <summary>Varias escrituras juntas: o quedan todas o ninguna (una venta, su bitácora, su anotación y el folio).</summary>
    public Lote Lote() => new(Abrir());

    /// <summary>Lo que falta subir y lo que no se pudo subir (para reintentarlo), en orden.</summary>
    public List<ElementoBandeja> PorSubir(bool conErrores)
    {
        using var conexion = Abrir();
        using var comando = Comando(conexion, null,
            $"SELECT Orden, Id, Ruta, Json, Error FROM Bandeja WHERE SubidoUtc IS NULL {(conErrores ? "" : "AND Error IS NULL")} ORDER BY Orden");
        using var lector = comando.ExecuteReader();
        var elementos = new List<ElementoBandeja>();
        while (lector.Read())
            elementos.Add(new ElementoBandeja(lector.GetInt64(0), Guid.Parse(lector.GetString(1)), lector.GetString(2), lector.GetString(3),
                lector.IsDBNull(4) ? null : lector.GetString(4)));
        return elementos;
    }

    public void MarcarSubido(long orden, DateTime ahoraUtc)
    {
        using var conexion = Abrir();
        Ejecutar(conexion, null, "UPDATE Bandeja SET SubidoUtc = $fecha, Error = NULL WHERE Orden = $orden",
            ("$fecha", ahoraUtc.ToString("O", CultureInfo.InvariantCulture)), ("$orden", orden));
    }

    public void MarcarError(long orden, string error)
    {
        using var conexion = Abrir();
        Ejecutar(conexion, null, "UPDATE Bandeja SET Error = $error WHERE Orden = $orden", ("$error", error), ("$orden", orden));
    }

    /// <summary>Cuántas faltan por subir, cuántas no se pudieron subir y el último motivo.</summary>
    public (int Pendientes, int ConError, string? UltimoError) Contar()
    {
        using var conexion = Abrir();
        using var comando = Comando(conexion, null, """
            SELECT
                (SELECT COUNT(*) FROM Bandeja WHERE SubidoUtc IS NULL AND Error IS NULL),
                (SELECT COUNT(*) FROM Bandeja WHERE SubidoUtc IS NULL AND Error IS NOT NULL),
                (SELECT Error FROM Bandeja WHERE SubidoUtc IS NULL AND Error IS NOT NULL ORDER BY Orden DESC LIMIT 1)
            """);
        using var lector = comando.ExecuteReader();
        lector.Read();
        return (lector.GetInt32(0), lector.GetInt32(1), lector.IsDBNull(2) ? null : lector.GetString(2));
    }

    /// <summary>Lo que se ha anotado de un turno, sumado.</summary>
    public AnotacionDiario Diario(Guid turnoId)
    {
        using var conexion = Abrir();
        using var comando = Comando(conexion, null, """
            SELECT COALESCE(SUM(Ventas), 0), COALESCE(SUM(Efectivo), 0), COALESCE(SUM(Tarjeta), 0), COALESCE(SUM(Transferencia), 0),
                   COALESCE(SUM(Entradas), 0), COALESCE(SUM(Salidas), 0), COALESCE(SUM(Devoluciones), 0), COALESCE(SUM(Cancelaciones), 0)
            FROM Diario WHERE TurnoId = $turno
            """, ("$turno", turnoId.ToString()));
        using var lector = comando.ExecuteReader();
        lector.Read();
        return new AnotacionDiario(Guid.Empty, turnoId, lector.GetInt32(0), lector.GetInt64(1), lector.GetInt64(2), lector.GetInt64(3),
            lector.GetInt64(4), lector.GetInt64(5), lector.GetInt64(6), lector.GetInt32(7));
    }

    private SqliteConnection Abrir()
    {
        var conexion = new SqliteConnection(_cadena);
        conexion.Open();
        return conexion;
    }

    internal static SqliteCommand Comando(SqliteConnection conexion, SqliteTransaction? transaccion, string sql, params (string Nombre, object? Valor)[] parametros)
    {
        var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = sql;
        foreach (var (nombre, valor) in parametros)
            comando.Parameters.AddWithValue(nombre, valor ?? DBNull.Value);
        return comando;
    }

    internal static void Ejecutar(SqliteConnection conexion, SqliteTransaction? transaccion, string sql, params (string Nombre, object? Valor)[] parametros)
    {
        using var comando = Comando(conexion, transaccion, sql, parametros);
        comando.ExecuteNonQuery();
    }
}

/// <summary>Escrituras en una sola transacción; sin <see cref="Confirmar"/>, no queda ninguna.</summary>
public sealed class Lote : IDisposable
{
    private readonly SqliteConnection _conexion;
    private readonly SqliteTransaction _transaccion;

    internal Lote(SqliteConnection conexion)
    {
        _conexion = conexion;
        _transaccion = conexion.BeginTransaction();
    }

    public Lote Guardar<T>(string clave, T valor)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, "INSERT INTO Ajustes (Clave, Valor) VALUES ($clave, $valor) ON CONFLICT(Clave) DO UPDATE SET Valor = excluded.Valor",
            ("$clave", clave), ("$valor", JsonSerializer.Serialize(valor, AlmacenLocal.Json)));
        return this;
    }

    public Lote Borrar(string clave)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, "DELETE FROM Ajustes WHERE Clave = $clave", ("$clave", clave));
        return this;
    }

    /// <summary>A la bandeja: se sube en este orden a <paramref name="ruta"/>.</summary>
    public Lote Encolar<T>(Guid id, string ruta, T cuerpo)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, "INSERT INTO Bandeja (Id, Ruta, Json, CreadoUtc) VALUES ($id, $ruta, $json, $fecha)",
            ("$id", id.ToString()), ("$ruta", ruta), ("$json", JsonSerializer.Serialize(cuerpo, AlmacenLocal.Json)),
            ("$fecha", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        return this;
    }

    /// <summary>Al diario del turno. Si ya estaba (la misma operación otra vez), no se suma dos veces.</summary>
    public Lote Anotar(AnotacionDiario a)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, """
            INSERT OR IGNORE INTO Diario (Id, TurnoId, Ventas, Efectivo, Tarjeta, Transferencia, Entradas, Salidas, Devoluciones, Cancelaciones)
            VALUES ($id, $turno, $ventas, $efectivo, $tarjeta, $transferencia, $entradas, $salidas, $devoluciones, $cancelaciones)
            """,
            ("$id", a.Id.ToString()), ("$turno", a.TurnoId.ToString()), ("$ventas", a.Ventas), ("$efectivo", a.Efectivo), ("$tarjeta", a.Tarjeta),
            ("$transferencia", a.Transferencia), ("$entradas", a.Entradas), ("$salidas", a.Salidas), ("$devoluciones", a.Devoluciones),
            ("$cancelaciones", a.Cancelaciones));
        return this;
    }

    /// <summary>Sin turno, borra todo el diario.</summary>
    public Lote LimpiarDiario(Guid? turnoId = null)
    {
        if (turnoId is { } id)
            AlmacenLocal.Ejecutar(_conexion, _transaccion, "DELETE FROM Diario WHERE TurnoId = $turno", ("$turno", id.ToString()));
        else
            AlmacenLocal.Ejecutar(_conexion, _transaccion, "DELETE FROM Diario");
        return this;
    }

    public void Confirmar() => _transaccion.Commit();

    public void Dispose()
    {
        _transaccion.Dispose();
        _conexion.Dispose();
    }
}
