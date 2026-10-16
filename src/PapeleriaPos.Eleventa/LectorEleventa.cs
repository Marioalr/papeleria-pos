using System.Data.Common;
using System.Diagnostics;
using System.Text;
using FirebirdSql.Data.FirebirdClient;
using FirebirdSql.Data.Services;

namespace PapeleriaPos.Eleventa;

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
/// El Firebird de la caja principal de eleventa (el que abren sus cajas por la red). La base es la ruta en esa PC
/// («C:\…\PDVDATA.FDB»). Los datos reales van en la configuración de la caja, nunca en el repositorio.
/// </summary>
public sealed record ServidorEleventa(string Servidor, string Base, string Usuario, string Clave, int Puerto = 3050)
{
    // WIN1252 no viene en .NET: sin esto, Firebird no abre la conexión («Invalid character set specified»).
    static ServidorEleventa() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Cadena() => new FbConnectionStringBuilder
    {
        DataSource = Servidor,
        Port = Puerto,
        Database = Base,
        UserID = Usuario,
        Password = Clave,
        // Como la copia: charset NONE con texto en Windows-1252.
        Charset = "WIN1252",
        Pooling = false,
        ConnectionTimeout = 10,
        // Firebird 2.0 no cifra la conexión.
        WireCrypt = FbWireCrypt.Disabled,
    }.ToString();

    public override string ToString() => $"{Servidor}:{Puerto}/{Base}";
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

    public static async Task<DatosEleventa> LeerAsync(FbConnection c, CancellationToken ct = default)
    {
        var datos = await LeerCatalogoAsync(c, ct);
        return new DatosEleventa
        {
            Departamentos = datos.Departamentos,
            Productos = datos.Productos,
            Existencias = datos.Existencias,
            Promociones = datos.Promociones,
            Tickets = await Leer(c, null, "SELECT ID, FOLIO, VENDIDO_EN, TOTAL, ESTA_CANCELADO FROM VENTATICKETS",
                r => new FilaTicket(Entero(r, 0), EnteroONulo(r, 1), r.IsDBNull(2) ? null : r.GetDateTime(2), Doble(r, 3),
                    Texto(r, 4) is "t" or "T"), ct),
            Lineas = await Leer(c, null,
                "SELECT TICKET_ID, PRODUCTO_CODIGO, PRODUCTO_NOMBRE, CANTIDAD, TOTAL_ARTICULO, PRECIO_FINAL, GANANCIA, DEPARTAMENTO_ID, USA_MAYOREO FROM VENTATICKETS_ARTICULOS",
                r => new FilaLinea(Entero(r, 0), Texto(r, 1), Texto(r, 2), Doble(r, 3), r.IsDBNull(4) ? null : Doble(r, 4), Doble(r, 5),
                    Doble(r, 6), EnteroONulo(r, 7), Texto(r, 8) is "t" or "T"), ct),
        };
    }

    /// <summary>
    /// El eleventa de la caja principal, por la red, como lo abre una caja de eleventa: solo para leer el catálogo cada
    /// pocos minutos (docs/migracion-eleventa.md, «El piloto»). No escribe nada.
    /// </summary>
    public static async Task<DatosEleventa> LeerCatalogoAsync(ServidorEleventa servidor, CancellationToken ct = default) =>
        await LeerCatalogoAsync(servidor, null, ct);

    /// <param name="piloto">Lo del piloto que se lee en la misma foto (los turnos de la caja 2); vacío fuera del piloto.</param>
    public static async Task<DatosEleventa> LeerCatalogoAsync(ServidorEleventa servidor, ConsultaDelPiloto? piloto, CancellationToken ct = default)
    {
        await using var conexion = new FbConnection(servidor.Cadena());
        await conexion.OpenAsync(ct);
        return await LeerCatalogoAsync(conexion, piloto, ct);
    }

    public static Task<DatosEleventa> LeerCatalogoAsync(FbConnection c, CancellationToken ct = default) => LeerCatalogoAsync(c, null, ct);

    /// <summary>
    /// Lo que cambia durante el día y hace falta para vender: departamentos, productos (con precios y costo), existencias
    /// y promociones; sin las ventas, que pesan y no hacen falta cada pocos minutos. En una transacción de solo lectura y
    /// con foto fija: todo es del mismo momento, y eleventa ni lo nota. En el piloto, en la misma foto, lo que el puente
    /// necesita saber (<see cref="ConsultaDelPiloto"/>).
    /// </summary>
    public static async Task<DatosEleventa> LeerCatalogoAsync(FbConnection c, ConsultaDelPiloto? piloto, CancellationToken ct = default)
    {
        await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        }, ct);
        var datos = new DatosEleventa
        {
            Piloto = piloto is null ? null : await LeerDelPilotoAsync(c, tx, piloto, ct),
            Departamentos = await Leer(c, tx, "SELECT ID, NOMBRE, ACTIVO FROM DEPARTAMENTOS",
                r => new FilaDepartamento(Entero(r, 0), Texto(r, 1), Texto(r, 2) is "1" or "t" or "T"), ct),
            Productos = await Leer(c, tx,
                "SELECT ID, CODIGO, DESCRIPCION, TVENTA, MEDIDA_ID, PCOSTO, PFINAL, PMAYOREOFINAL, DEPT, ELIMINADO_EN FROM PRODUCTOS",
                r => new FilaProducto(Entero(r, 0), Texto(r, 1), Texto(r, 2), Texto(r, 3) is { Length: > 0 } tv ? tv[0] : 'U',
                    EnteroONulo(r, 4), Doble(r, 5), Doble(r, 6), Doble(r, 7), EnteroONulo(r, 8), !r.IsDBNull(9)), ct),
            Existencias = await Leer(c, tx, "SELECT PRODUCTO_ID, CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES",
                r => new FilaExistencia(Entero(r, 0), Doble(r, 1)), ct),
            Promociones = await Leer(c, tx, "SELECT PRODUCTO_CODIGO, DESDE, PRECIO_PROMOCION_CON_IMPUESTOS FROM PROMOCIONES_POR_CANTIDAD",
                r => new FilaPromocion(Texto(r, 0), Doble(r, 1), Doble(r, 2)), ct),
        };
        await tx.CommitAsync(ct);
        return datos;
    }

    /// <summary>
    /// Lo del piloto: los turnos abiertos de la caja 2 y los que adoptó la caja nueva (si eleventa ya los cerró, con lo que
    /// se contó y lo que faltó o sobró al cerrar), y las devoluciones de los tickets que mandó la caja 2. Solo lee.
    /// </summary>
    private static async Task<DatosDelPiloto> LeerDelPilotoAsync(FbConnection c, FbTransaction tx, ConsultaDelPiloto consulta, CancellationToken ct)
    {
        static FilaTurno Turno(DbDataReader r) => new(Entero(r, 0), Entero(r, 1), r.GetDateTime(2), r.IsDBNull(3) ? null : r.GetDateTime(3),
            r.IsDBNull(4) ? null : Convert.ToDecimal(r.GetValue(4), System.Globalization.CultureInfo.InvariantCulture));
        const string columnas = "SELECT ID, ID_CAJA, INICIO_EN, TERMINO_EN, EFECTIVO_AL_CIERRE FROM TURNOS";

        var turnos = await Leer(c, tx, $"{columnas} WHERE ID_CAJA = {consulta.CajaId} AND TERMINO_EN IS NULL", Turno, ct);
        foreach (var grupo in ListasIn.Grupos(consulta.Turnos))
            turnos.AddRange((await Leer(c, tx, $"{columnas} WHERE ID IN ({grupo})", Turno, ct)).Where(t => turnos.All(x => x.Id != t.Id)));

        // Eleventa anota lo que faltó o sobró al cerrar como una salida o entrada del corte: «Efectivo faltante al cerrar
        // turno de…» (docs/migracion-eleventa.md, «Cancelar, devolver y cerrar el turno»).
        var cierres = new List<FilaCierreDeTurno>();
        foreach (var grupo in ListasIn.Grupos(turnos.Where(t => t.Termino is not null).Select(t => t.Id)))
            cierres.AddRange(await Leer(c, tx, $"""
                SELECT ID_TURNO, TIPO, MONTO FROM CORTE_MOVIMIENTOS
                WHERE ID_TURNO IN ({grupo}) AND (DESCRIPCION STARTING WITH 'Efectivo faltante al cerrar turno'
                    OR DESCRIPCION STARTING WITH 'Efectivo sobrante al cerrar turno')
                """,
                r => new FilaCierreDeTurno(Entero(r, 0), Texto(r, 1) == "Salida", Convert.ToDecimal(r.GetValue(2), System.Globalization.CultureInfo.InvariantCulture)), ct));
        // La vuelta: lo que se devolvió o canceló en eleventa de los tickets que mandó la caja 2.
        var devoluciones = consulta.Tickets is { Count: > 0 } tickets ? await TicketsDeEleventa.DevolucionesAsync(c, tx, tickets, ct) : [];
        return new DatosDelPiloto { Turnos = turnos, Cierres = cierres, Devoluciones = devoluciones };
    }

    /// <summary>Por qué no se pudo leer, dicho para la tienda (Firebird lo dice en inglés y con códigos).</summary>
    public static string Explicar(Exception ex, ServidorEleventa servidor) => ex switch
    {
        // isc_network_error: la PC apagada, otro nombre o IP, el firewall, o la red caída.
        FbException { ErrorCode: 335544721 } => $"No contesta el eleventa de {servidor.Servidor}: revisa que la caja principal esté prendida y en la red.",
        // isc_login
        FbException { ErrorCode: 335544472 } => "Eleventa no aceptó el usuario o la clave de Firebird.",
        // isc_io_error: la ruta de la base no existe en esa PC.
        FbException { ErrorCode: 335544344 } => $"En {servidor.Servidor} no está la base {servidor.Base}.",
        _ => ex.Message,
    };

    private static async Task<List<T>> Leer<T>(FbConnection c, FbTransaction? tx, string sql, Func<DbDataReader, T> fila, CancellationToken ct)
    {
        await using var comando = new FbCommand(sql, c, tx);
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
