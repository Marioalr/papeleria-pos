using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Nucleo.Clientes;

namespace PapeleriaPos.Interfaz;

/// <summary>Algo que subir a la caja principal: la ruta de la API y el cuerpo en JSON, en el orden en que pasó.</summary>
public sealed record ElementoBandeja(long Orden, Guid Id, string Ruta, string Json, string? Error);

/// <summary>
/// Lo que cambia el efectivo y los totales del turno. La caja anota todo lo de su turno (en línea o sin conexión) para
/// poder hacer el corte aunque la caja principal no conteste.
/// </summary>
/// <param name="Redondeo">El del efectivo al peso: + el de una venta, − el de un reembolso (informativo, ya va en el efectivo).</param>
/// <param name="Credito">Lo fiado en las ventas (fase 3): informativo, no es dinero.</param>
/// <param name="AbonosEfectivo">Lo que entró al cajón por abonos de clientes: suma al esperado.</param>
public sealed record AnotacionDiario(
    Guid Id, Guid TurnoId, int Ventas = 0, long Efectivo = 0, long Tarjeta = 0, long Transferencia = 0, long Entradas = 0, long Salidas = 0,
    long Devoluciones = 0, int Cancelaciones = 0, long Redondeo = 0, long Credito = 0, int Abonos = 0, long AbonosEfectivo = 0, long AbonosTarjeta = 0,
    long AbonosTransferencia = 0);

/// <summary>Un renglón de la cuenta propia de la caja: lo que fió o abonó y el servicio todavía no le confirma.</summary>
public sealed record CreditoPorConfirmar(Guid Id, Guid ClienteId, CreditoLocal Local);

/// <summary>
/// La base local de la caja (una SQLite propia, junto a su configuración): la copia del catálogo y demás datos para vender
/// sin conexión, la bandeja de salida, el diario del turno y la cuenta propia del crédito (lo fiado y abonado en esta
/// caja que la caja principal todavía no le confirma; no es del turno: el corte no la toca).
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
            CREATE TABLE IF NOT EXISTS Credito (
                Id TEXT PRIMARY KEY, ClienteId TEXT NOT NULL, Monto INTEGER NOT NULL, FechaUtc TEXT NOT NULL, Vence TEXT,
                PorVerificar INTEGER NOT NULL DEFAULT 0, Confirmado INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS IX_Credito_Cliente ON Credito (ClienteId);
            """);
        // Bases creadas antes de las impresiones (entrega 5): sin la columna, todo bloquea.
        using (var columnas = Comando(conexion, null, "SELECT COUNT(*) FROM pragma_table_info('Bandeja') WHERE name = 'Bloquea'"))
        {
            if (Convert.ToInt32(columnas.ExecuteScalar()) == 0)
                Ejecutar(conexion, null, "ALTER TABLE Bandeja ADD COLUMN Bloquea INTEGER NOT NULL DEFAULT 1");
        }
        // Bases creadas antes del redondeo al peso y del crédito (fase 3): lo anotado antes no tiene esas columnas.
        foreach (var columna in new[] { "Redondeo", "Credito", "Abonos", "AbonosEfectivo", "AbonosTarjeta", "AbonosTransferencia" })
        {
            using var columnas = Comando(conexion, null, "SELECT COUNT(*) FROM pragma_table_info('Diario') WHERE name = $columna", ("$columna", columna));
            if (Convert.ToInt32(columnas.ExecuteScalar()) == 0)
                Ejecutar(conexion, null, $"ALTER TABLE Diario ADD COLUMN {columna} INTEGER NOT NULL DEFAULT 0");
        }
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
    /// <summary>
    /// Cuántas faltan por subir (las que bloquean: mientras haya, la caja sigue por su cuenta), cuántas no se pudieron
    /// subir, el último motivo y cuántas esperan sin bloquear (las impresiones).
    /// </summary>
    public (int Pendientes, int ConError, string? UltimoError, int SinBloquear) Contar()
    {
        using var conexion = Abrir();
        using var comando = Comando(conexion, null, """
            SELECT
                (SELECT COUNT(*) FROM Bandeja WHERE SubidoUtc IS NULL AND Error IS NULL AND Bloquea = 1),
                (SELECT COUNT(*) FROM Bandeja WHERE SubidoUtc IS NULL AND Error IS NOT NULL),
                (SELECT Error FROM Bandeja WHERE SubidoUtc IS NULL AND Error IS NOT NULL ORDER BY Orden DESC LIMIT 1),
                (SELECT COUNT(*) FROM Bandeja WHERE SubidoUtc IS NULL AND Error IS NULL AND Bloquea = 0)
            """);
        using var lector = comando.ExecuteReader();
        lector.Read();
        return (lector.GetInt32(0), lector.GetInt32(1), lector.IsDBNull(2) ? null : lector.GetString(2), lector.GetInt32(3));
    }

    /// <summary>Lo que se ha anotado de un turno, sumado.</summary>
    public AnotacionDiario Diario(Guid turnoId)
    {
        using var conexion = Abrir();
        using var comando = Comando(conexion, null, """
            SELECT COALESCE(SUM(Ventas), 0), COALESCE(SUM(Efectivo), 0), COALESCE(SUM(Tarjeta), 0), COALESCE(SUM(Transferencia), 0),
                   COALESCE(SUM(Entradas), 0), COALESCE(SUM(Salidas), 0), COALESCE(SUM(Devoluciones), 0), COALESCE(SUM(Cancelaciones), 0),
                   COALESCE(SUM(Redondeo), 0), COALESCE(SUM(Credito), 0), COALESCE(SUM(Abonos), 0), COALESCE(SUM(AbonosEfectivo), 0),
                   COALESCE(SUM(AbonosTarjeta), 0), COALESCE(SUM(AbonosTransferencia), 0)
            FROM Diario WHERE TurnoId = $turno
            """, ("$turno", turnoId.ToString()));
        using var lector = comando.ExecuteReader();
        lector.Read();
        return new AnotacionDiario(Guid.Empty, turnoId, lector.GetInt32(0), lector.GetInt64(1), lector.GetInt64(2), lector.GetInt64(3),
            lector.GetInt64(4), lector.GetInt64(5), lector.GetInt64(6), lector.GetInt32(7), lector.GetInt64(8), lector.GetInt64(9), lector.GetInt32(10),
            lector.GetInt64(11), lector.GetInt64(12), lector.GetInt64(13));
    }

    /// <summary>Lo propio sin confirmar de un cliente (o de todos), en el orden en que pasó.</summary>
    public List<CreditoPorConfirmar> CuentaPropia(Guid? clienteId = null)
    {
        using var conexion = Abrir();
        using var comando = Comando(conexion, null,
            $"SELECT Id, ClienteId, Monto, Vence, PorVerificar FROM Credito WHERE Confirmado = 0 {(clienteId is null ? "" : "AND ClienteId = $cliente")} ORDER BY FechaUtc, Id",
            ("$cliente", clienteId?.ToString()));
        using var lector = comando.ExecuteReader();
        var filas = new List<CreditoPorConfirmar>();
        while (lector.Read())
            filas.Add(new CreditoPorConfirmar(Guid.Parse(lector.GetString(0)), Guid.Parse(lector.GetString(1)),
                new CreditoLocal(lector.GetInt64(2), lector.IsDBNull(3) ? null : DateOnly.ParseExact(lector.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    lector.GetInt64(4) != 0)));
        return filas;
    }

    /// <summary>Si el servicio ya dijo que eso está en sus saldos: con el mismo Id no se vuelve a hacer ni a contar.</summary>
    public bool CreditoConfirmado(Guid id)
    {
        using var conexion = Abrir();
        using var comando = Comando(conexion, null, "SELECT COUNT(*) FROM Credito WHERE Id = $id AND Confirmado = 1", ("$id", id.ToString()));
        return Convert.ToInt32(comando.ExecuteScalar()) > 0;
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

    /// <summary>
    /// A la bandeja: se sube en este orden a <paramref name="ruta"/>. Sin <paramref name="bloquea"/> (las impresiones), la
    /// caja sigue en línea aunque esté pendiente: no lleva folio que pueda chocar.
    /// </summary>
    public Lote Encolar<T>(Guid id, string ruta, T cuerpo, bool bloquea = true)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, "INSERT OR IGNORE INTO Bandeja (Id, Ruta, Json, CreadoUtc, Bloquea) VALUES ($id, $ruta, $json, $fecha, $bloquea)",
            ("$id", id.ToString()), ("$ruta", ruta), ("$json", JsonSerializer.Serialize(cuerpo, AlmacenLocal.Json)),
            ("$fecha", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)), ("$bloquea", bloquea ? 1 : 0));
        return this;
    }

    /// <summary>Al diario del turno. Si ya estaba (la misma operación otra vez), no se suma dos veces.</summary>
    public Lote Anotar(AnotacionDiario a)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, """
            INSERT OR IGNORE INTO Diario (Id, TurnoId, Ventas, Efectivo, Tarjeta, Transferencia, Entradas, Salidas, Devoluciones, Cancelaciones, Redondeo,
                Credito, Abonos, AbonosEfectivo, AbonosTarjeta, AbonosTransferencia)
            VALUES ($id, $turno, $ventas, $efectivo, $tarjeta, $transferencia, $entradas, $salidas, $devoluciones, $cancelaciones, $redondeo,
                $credito, $abonos, $abonosEfectivo, $abonosTarjeta, $abonosTransferencia)
            """,
            ("$id", a.Id.ToString()), ("$turno", a.TurnoId.ToString()), ("$ventas", a.Ventas), ("$efectivo", a.Efectivo), ("$tarjeta", a.Tarjeta),
            ("$transferencia", a.Transferencia), ("$entradas", a.Entradas), ("$salidas", a.Salidas), ("$devoluciones", a.Devoluciones),
            ("$cancelaciones", a.Cancelaciones), ("$redondeo", a.Redondeo), ("$credito", a.Credito), ("$abonos", a.Abonos),
            ("$abonosEfectivo", a.AbonosEfectivo), ("$abonosTarjeta", a.AbonosTarjeta), ("$abonosTransferencia", a.AbonosTransferencia));
        return this;
    }

    /// <summary>
    /// A la cuenta propia: lo fiado (+) o abonado (−) que el servicio todavía no le confirma. Con el mismo Id se reemplaza
    /// (lo anotado antes de mandarlo, y otra vez si se hizo sin conexión), salvo que ya esté confirmado: mandarlo otra vez
    /// no lo vuelve a guardar en el servicio, así que tampoco se vuelve a contar aquí.
    /// </summary>
    public Lote Credito(Guid id, Guid clienteId, long montoCentavos, DateTime fechaUtc, DateOnly? vence, bool porVerificar = false)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, """
            INSERT INTO Credito (Id, ClienteId, Monto, FechaUtc, Vence, PorVerificar) VALUES ($id, $cliente, $monto, $fecha, $vence, $verificar)
            ON CONFLICT(Id) DO UPDATE SET ClienteId = excluded.ClienteId, Monto = excluded.Monto, FechaUtc = excluded.FechaUtc, Vence = excluded.Vence,
                PorVerificar = excluded.PorVerificar
            WHERE Credito.Confirmado = 0
            """,
            ("$id", id.ToString()), ("$cliente", clienteId.ToString()), ("$monto", montoCentavos), ("$fecha", fechaUtc.ToString("O", CultureInfo.InvariantCulture)),
            ("$vence", vence?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$verificar", porVerificar ? 1 : 0));
        return this;
    }

    /// <summary>Con lo que dijo el servicio (el monto real del abono, el vencimiento del cargo). Si ya se confirmó, no revive.</summary>
    public Lote CorregirCredito(Guid id, long? montoCentavos, DateOnly? vence)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, """
            UPDATE Credito SET Monto = COALESCE($monto, Monto), Vence = COALESCE($vence, Vence) WHERE Id = $id
            """,
            ("$id", id.ToString()), ("$monto", montoCentavos), ("$vence", vence?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return this;
    }

    /// <summary>Lo que el servicio rechazó (por una regla): no pasó, deja de contarse aquí.</summary>
    public Lote QuitarCredito(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
            AlmacenLocal.Ejecutar(_conexion, _transaccion, "DELETE FROM Credito WHERE Id = $id AND Confirmado = 0", ("$id", id.ToString()));
        return this;
    }

    /// <summary>
    /// Lo que el servicio ya tiene sumado en sus saldos: deja de contarse aquí, pero se recuerda un día (si se vuelve a
    /// mandar con el mismo Id, el servicio regresa lo que ya tenía: no se debe volver a contar).
    /// </summary>
    public Lote ConfirmarCredito(IEnumerable<Guid> ids, DateTime olvidarAntesUtc)
    {
        foreach (var id in ids)
            AlmacenLocal.Ejecutar(_conexion, _transaccion, "UPDATE Credito SET Confirmado = 1 WHERE Id = $id", ("$id", id.ToString()));
        AlmacenLocal.Ejecutar(_conexion, _transaccion, "DELETE FROM Credito WHERE Confirmado = 1 AND FechaUtc < $antes",
            ("$antes", olvidarAntesUtc.ToString("O", CultureInfo.InvariantCulture)));
        return this;
    }

    /// <summary>
    /// Lo que el servicio no tiene, ni va a tener: nunca pasó por la bandeja (lo que pasa por ella el servicio lo acepta
    /// siempre, aunque tarde en subir) y lleva un rato sin aparecer en sus saldos: un fiado que se mandó en línea y nunca
    /// se guardó, de un ticket que ya no se cobró.
    /// </summary>
    public Lote QuitarCreditoHuerfano(IEnumerable<Guid> ids, DateTime anotadoAntesUtc)
    {
        foreach (var id in ids)
            AlmacenLocal.Ejecutar(_conexion, _transaccion, """
                DELETE FROM Credito WHERE Id = $id AND FechaUtc < $antes AND Confirmado = 0
                    AND NOT EXISTS (SELECT 1 FROM Bandeja WHERE Bandeja.Id = Credito.Id)
                """,
                ("$id", id.ToString()), ("$antes", anotadoAntesUtc.ToString("O", CultureInfo.InvariantCulture)));
        return this;
    }

    /// <summary>Empezar de cero (la prueba): la cuenta propia ya no corresponde a ninguna base.</summary>
    public Lote LimpiarCredito()
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, "DELETE FROM Credito");
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
