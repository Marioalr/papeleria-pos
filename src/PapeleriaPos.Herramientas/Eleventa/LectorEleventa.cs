using System.Data.Common;
using System.Diagnostics;
using System.Text;
using FirebirdSql.Data.FirebirdClient;
using FirebirdSql.Data.Services;

namespace PapeleriaPos.Herramientas.Eleventa;

/// <summary>Dónde están las dos versiones de Firebird 2.5 que hacen falta (ver docs/migracion-eleventa.md).</summary>
/// <param name="Carpeta32">Firebird 2.5 embebido de 32 bits con gbak.exe y fbembed.dll copiado como fbclient.dll.</param>
/// <param name="Carpeta64">Firebird 2.5 embebido de 64 bits (fbembed.dll).</param>
public sealed record FirebirdLocal(string Carpeta32, string Carpeta64)
{
    public string Gbak32 => Path.Combine(Carpeta32, "gbak.exe");
    public string Embebido64 => Path.Combine(Carpeta64, "fbembed.dll");

    public void Verificar()
    {
        foreach (var archivo in new[] { Gbak32, Path.Combine(Carpeta32, "fbclient.dll"), Embebido64 })
        {
            if (!File.Exists(archivo))
                throw new FileNotFoundException($"Falta {archivo}. Ver «Leer la base» en docs/migracion-eleventa.md.", archivo);
        }
    }
}

/// <summary>
/// Lee la base de eleventa. Nunca abre el archivo original: lo copia a una carpeta de trabajo, lo respalda con el
/// gbak de 32 bits (la base es de Firebird 1.5 de 32 bits y el Firebird de 64 bits no la abre) y restaura el respaldo
/// como base de 64 bits, que es la que se lee.
/// </summary>
public static class LectorEleventa
{
    static LectorEleventa() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<DatosEleventa> LeerAsync(string fdb, FirebirdLocal firebird, Action<string> avance, CancellationToken ct = default)
    {
        firebird.Verificar();
        var trabajo = Directory.CreateTempSubdirectory("papeleria-pos-eleventa-");
        try
        {
            avance("Copiando la base…");
            var copia = Path.Combine(trabajo.FullName, "copia.fdb");
            File.Copy(fdb, copia);

            avance("Respaldando con Firebird de 32 bits…");
            var respaldo = Path.Combine(trabajo.FullName, "respaldo.fbk");
            await RespaldarAsync(firebird, copia, respaldo, ct);

            avance("Restaurando como base de 64 bits…");
            var base64 = Path.Combine(trabajo.FullName, "eleventa64.fdb");
            Restaurar(firebird, respaldo, base64);

            avance("Leyendo datos…");
            await using var conexion = new FbConnection(Cadena(firebird, base64));
            await conexion.OpenAsync(ct);
            return await LeerAsync(conexion, ct);
        }
        finally
        {
            FbConnection.ClearAllPools();
            try
            {
                trabajo.Delete(recursive: true);
            }
            catch (IOException)
            {
                avance($"No se pudo borrar la carpeta de trabajo {trabajo.FullName}; se puede borrar a mano.");
            }
        }
    }

    private static string Cadena(FirebirdLocal firebird, string fdb, bool conClave = false) => new FbConnectionStringBuilder
    {
        ServerType = FbServerType.Embedded,
        Database = fdb,
        ClientLibrary = firebird.Embebido64,
        UserID = "SYSDBA",
        // El modo embebido no revisa credenciales, pero la API de servicios (restaurar) exige el campo.
        Password = conClave ? "embebido" : null,
        // La base es charset NONE con texto en Windows-1252 (la ñ es el byte F1).
        Charset = "WIN1252",
        Pooling = false,
    }.ToString();

    private static async Task RespaldarAsync(FirebirdLocal firebird, string fdb, string fbk, CancellationToken ct)
    {
        var inicio = new ProcessStartInfo(firebird.Gbak32)
        {
            WorkingDirectory = firebird.Carpeta32,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argumento in new[] { "-b", "-g", "-user", "SYSDBA", fdb, fbk })
            inicio.ArgumentList.Add(argumento);

        using var gbak = Process.Start(inicio)!;
        var salida = gbak.StandardOutput.ReadToEndAsync(ct);
        var errores = gbak.StandardError.ReadToEndAsync(ct);
        await gbak.WaitForExitAsync(ct);
        if (gbak.ExitCode != 0 || !File.Exists(fbk))
            throw new InvalidOperationException($"gbak no pudo respaldar la base (código {gbak.ExitCode}):\n{await errores}{await salida}");
    }

    private static void Restaurar(FirebirdLocal firebird, string fbk, string fdb)
    {
        // Con Verbose el cliente lee la salida del servicio hasta el final; sin él, Execute regresa antes de terminar.
        var restaurar = new FbRestore(Cadena(firebird, fdb, conClave: true)) { PageSize = 16384, Options = FbRestoreFlags.Create, Verbose = true };
        restaurar.BackupFiles.Add(new FbBackupFile(fbk));
        var errores = new List<string>();
        restaurar.ServiceOutput += (_, e) =>
        {
            if (e.Message.Replace(" ", "").StartsWith("gbak:ERROR", StringComparison.OrdinalIgnoreCase))
                errores.Add(e.Message);
        };
        restaurar.Execute();
        if (errores.Count > 0 || !File.Exists(fdb))
            throw new InvalidOperationException($"No se pudo restaurar el respaldo de eleventa:\n{string.Join('\n', errores)}");
    }

    public static async Task<DatosEleventa> LeerAsync(FbConnection c, CancellationToken ct = default) => new()
    {
        Departamentos = await Leer(c, "SELECT ID, NOMBRE, ACTIVO FROM DEPARTAMENTOS",
            r => new FilaDepartamento(Entero(r, 0), Texto(r, 1), Texto(r, 2) is "1" or "t" or "T"), ct),
        Productos = await Leer(c,
            "SELECT ID, CODIGO, DESCRIPCION, TVENTA, MEDIDA_ID, PCOSTO, PFINAL, PMAYOREOFINAL, DEPT, ELIMINADO_EN FROM PRODUCTOS",
            r => new FilaProducto(Entero(r, 0), Texto(r, 1), Texto(r, 2), Texto(r, 3) is { Length: > 0 } tv ? tv[0] : 'U',
                EnteroONulo(r, 4), Doble(r, 5), Doble(r, 6), Doble(r, 7), EnteroONulo(r, 8), !r.IsDBNull(9)), ct),
        Existencias = await Leer(c, "SELECT PRODUCTO_ID, CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES",
            r => new FilaExistencia(Entero(r, 0), Doble(r, 1)), ct),
        Promociones = await Leer(c, "SELECT PRODUCTO_CODIGO, DESDE, PRECIO_PROMOCION_CON_IMPUESTOS FROM PROMOCIONES_POR_CANTIDAD",
            r => new FilaPromocion(Texto(r, 0), Doble(r, 1), Doble(r, 2)), ct),
        Tickets = await Leer(c, "SELECT ID, FOLIO, VENDIDO_EN, TOTAL, ESTA_CANCELADO FROM VENTATICKETS",
            r => new FilaTicket(Entero(r, 0), EnteroONulo(r, 1), r.IsDBNull(2) ? null : r.GetDateTime(2), Doble(r, 3),
                Texto(r, 4) is "t" or "T"), ct),
        Lineas = await Leer(c,
            "SELECT TICKET_ID, PRODUCTO_CODIGO, PRODUCTO_NOMBRE, CANTIDAD, TOTAL_ARTICULO, PRECIO_FINAL, GANANCIA, DEPARTAMENTO_ID, USA_MAYOREO FROM VENTATICKETS_ARTICULOS",
            r => new FilaLinea(Entero(r, 0), Texto(r, 1), Texto(r, 2), Doble(r, 3), r.IsDBNull(4) ? null : Doble(r, 4), Doble(r, 5),
                Doble(r, 6), EnteroONulo(r, 7), Texto(r, 8) is "t" or "T"), ct),
    };

    private static async Task<List<T>> Leer<T>(FbConnection c, string sql, Func<DbDataReader, T> fila, CancellationToken ct)
    {
        await using var comando = new FbCommand(sql, c);
        await using var lector = await comando.ExecuteReaderAsync(ct);
        var filas = new List<T>();
        while (await lector.ReadAsync(ct))
            filas.Add(fila(lector));
        return filas;
    }

    private static string Texto(DbDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i).Trim();
    private static int Entero(DbDataReader r, int i) => Convert.ToInt32(r.GetValue(i));
    private static int? EnteroONulo(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));
    private static double Doble(DbDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToDouble(r.GetValue(i));
}
