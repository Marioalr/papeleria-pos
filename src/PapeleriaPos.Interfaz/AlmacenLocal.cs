using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Nucleo;
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
/// <param name="SaldoDevuelto">El saldo a favor que se les devolvió en efectivo a los clientes (parte 3): resta del esperado.</param>
public sealed record AnotacionDiario(
    Guid Id, Guid TurnoId, int Ventas = 0, long Efectivo = 0, long Tarjeta = 0, long Transferencia = 0, long Entradas = 0, long Salidas = 0,
    long Devoluciones = 0, int Cancelaciones = 0, long Redondeo = 0, long Credito = 0, int Abonos = 0, long AbonosEfectivo = 0, long AbonosTarjeta = 0,
    long AbonosTransferencia = 0, long SaldoDevuelto = 0);

/// <summary>Un renglón de la cuenta propia de la caja: lo que fió o abonó y el servicio todavía no le confirma.</summary>
public sealed record CreditoPorConfirmar(Guid Id, Guid ClienteId, CreditoLocal Local);

/// <summary>
/// La base local de la caja (una SQLite propia, junto a su configuración): la copia del catálogo y demás datos para vender
/// sin conexión, la bandeja de salida, el diario del turno y la cuenta propia del crédito (lo fiado y abonado en esta
/// caja que la caja principal todavía no le confirma; no es del turno: el corte no la toca).
/// <para>
/// Un solo escritor (entrega 5, docs/propuesta-interfaz.md, «Rendimiento»): todo lo que se escribe pasa por una cola, en su
/// propio hilo, una escritura tras otra (<see cref="EscribirAsync"/>). La venta y la sincronización ya no se esperan entre
/// sí en la base: la pantalla deja su escritura en la cola y sigue (la espera sin detenerse), y la copia de la caja
/// principal, que es grande, se escribe en pedazos (<see cref="GuardarCopiaAsync"/>): lo que la venta escribe espera a lo
/// más uno. Las lecturas siguen a la par (WAL), cada una con su conexión, y ven todo lo que se pidió escribir antes de
/// ellas (<see cref="AlDia"/>). Cada conexión, con <c>busy_timeout</c> y <c>synchronous=FULL</c> (entrega 10).
/// </para>
/// </summary>
public sealed class AlmacenLocal
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _cadena;

    /// <summary>Lo que se escribe, en orden, y quién lo espera.</summary>
    private sealed class Escritura(Action<Lote> escribir, long numero, string que)
    {
        public Action<Lote> Escribir { get; } = escribir;
        public long Numero { get; } = numero;
        public string Que { get; } = que;
        public TaskCompletionSource Hecha { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Sin nada que escribir este rato, el hilo del escritor se va; vuelve con lo siguiente.</summary>
    private static readonly TimeSpan Reposo = TimeSpan.FromSeconds(10);

    private readonly object _cola = new();
    private readonly Queue<Escritura> _pendientes = new();
    private Thread? _escritor;

    /// <summary>Cuántas se han pedido, el número de la última que ya quedó (en orden) y el de la última que leen las lecturas.</summary>
    private long _pedidas, _hechas, _ultimaQueSeLee;

    /// <summary>En el hilo del escritor, el almacén de quien escribe: ahí las lecturas no esperan a la cola.</summary>
    [ThreadStatic]
    private static AlmacenLocal? _escribiendo;

    /// <summary>
    /// Cada escritura que tardó más de lo normal: qué era y cuánto tardó (la ve el vigía de la pantalla, que dice qué pasaba
    /// cuando se trabó).
    /// </summary>
    public event Action<string, TimeSpan>? EscrituraLarga;

    /// <summary>Desde cuánto una escritura cuenta como larga.</summary>
    public static readonly TimeSpan Larga = TimeSpan.FromMilliseconds(20);

    /// <summary>La copia vigente de la caja principal: con qué número se guardó y en cuántos pedazos.</summary>
    internal sealed record CopiaGuardada(string Copia, int Pedazos);

    /// <summary>
    /// Cuánto de la copia de la caja principal va en cada escritura (revisión de la entrega 5). La copia pesa varios MB (5.7
    /// con 6,000 productos ficticios): de un jalón, lo que la venta pedía escribir detrás de ella la esperaba completa (un
    /// cobro, los tickets en espera). En pedazos, a lo más uno.
    /// </summary>
    public const int TamanoDelPedazo = 256 * 1024;

    /// <summary>Una copia a la vez: dos bajadas juntas no se pisan los pedazos.</summary>
    private readonly SemaphoreSlim _guardandoCopia = new(1, 1);

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
            CREATE TABLE IF NOT EXISTS CopiaPedazos (
                Copia TEXT NOT NULL, Indice INTEGER NOT NULL, Datos BLOB NOT NULL, PRIMARY KEY (Copia, Indice));
            CREATE TABLE IF NOT EXISTS Fotos (VarianteId TEXT PRIMARY KEY, Huella TEXT NOT NULL, Datos BLOB NOT NULL);
            """);
        // Bases creadas antes de las impresiones (entrega 5): sin la columna, todo bloquea.
        using (var columnas = Comando(conexion, null, "SELECT COUNT(*) FROM pragma_table_info('Bandeja') WHERE name = 'Bloquea'"))
        {
            if (Convert.ToInt32(columnas.ExecuteScalar()) == 0)
                Ejecutar(conexion, null, "ALTER TABLE Bandeja ADD COLUMN Bloquea INTEGER NOT NULL DEFAULT 1");
        }
        // Bases creadas antes del redondeo al peso y del crédito (fase 3): lo anotado antes no tiene esas columnas.
        foreach (var columna in new[] { "Redondeo", "Credito", "Abonos", "AbonosEfectivo", "AbonosTarjeta", "AbonosTransferencia", "SaldoDevuelto" })
        {
            using var columnas = Comando(conexion, null, "SELECT COUNT(*) FROM pragma_table_info('Diario') WHERE name = $columna", ("$columna", columna));
            if (Convert.ToInt32(columnas.ExecuteScalar()) == 0)
                Ejecutar(conexion, null, $"ALTER TABLE Diario ADD COLUMN {columna} INTEGER NOT NULL DEFAULT 0");
        }
        // Se lee de vuelta al abrir la caja: si no quedó en FULL, una venta guardada aquí podría perderse en un apagón.
        var (sincronizacion, esperaMs) = Ajustes();
        ProblemaDeAjustes = AjustesContraApagones.Problema(sincronizacion, esperaMs);
    }

    /// <summary>
    /// Si al abrir la caja una conexión nueva no quedó con los ajustes contra apagones (entrega 10); nada si está bien. La
    /// franja de la caja lo muestra (<see cref="Contingencia.AvisoBaseLocal"/>): en la ventana de Windows no hay registro.
    /// </summary>
    public string? ProblemaDeAjustes { get; }

    /// <summary>Los ajustes con que queda una conexión nueva: <c>synchronous</c> (2 es FULL) y <c>busy_timeout</c> en milisegundos.</summary>
    public (int Sincronizacion, int EsperaMs) Ajustes()
    {
        using var conexion = Abrir();
        using var sincronizacion = Comando(conexion, null, "PRAGMA synchronous");
        using var espera = Comando(conexion, null, "PRAGMA busy_timeout");
        return (Convert.ToInt32(sincronizacion.ExecuteScalar()), Convert.ToInt32(espera.ExecuteScalar()));
    }

    public T? Leer<T>(string clave)
    {
        AlDia();
        using var conexion = Abrir();
        return Lote.Leer<T>(conexion, null, clave);
    }

    /// <summary>
    /// Varias escrituras juntas, en una transacción: o quedan todas o ninguna (una venta, su bitácora, su anotación y el
    /// folio). Va a la cola del escritor, en su hilo; termina cuando ya está en el disco. Quien la pide no se detiene mientras.
    /// </summary>
    /// <param name="que">Qué es, para el vigía de la pantalla si tarda.</param>
    /// <param name="laLeen">
    /// Si lo que se escribe se vuelve a leer de aquí mientras la caja está abierta (casi todo). La copia de la caja principal
    /// no: se lee al abrir; las lecturas no la esperan.
    /// </param>
    public Task EscribirAsync(Action<Lote> escribir, string que = "la base de la caja", bool laLeen = true)
    {
        if (_escribiendo == this)
            throw new InvalidOperationException("Dentro de una escritura no se pide otra: se escribiría en otra transacción.");
        Escritura escritura;
        lock (_cola)
        {
            escritura = new Escritura(escribir, ++_pedidas, que);
            if (laLeen)
                _ultimaQueSeLee = escritura.Numero;
            _pendientes.Enqueue(escritura);
            if (_escritor is null)
            {
                _escritor = new Thread(EscribirEnSuHilo) { IsBackground = true, Name = "Escritor de caja-local.db" };
                _escritor.Start();
            }
            else
                Monitor.PulseAll(_cola);
        }
        return escritura.Hecha.Task;
    }

    /// <summary>
    /// Lo mismo, esperando aquí a que quede. Para lo que se hace sin conexión (no hay sincronización con qué toparse) y fuera
    /// de la pantalla; la pantalla usa <see cref="EscribirAsync"/>.
    /// </summary>
    public void Escribir(Action<Lote> escribir, string que = "la base de la caja") => EscribirAsync(escribir, que).GetAwaiter().GetResult();

    /// <summary>Termina cuando quedó todo lo que se ha pedido escribir (al cerrar la caja: nada se queda en la cola).</summary>
    public bool Vaciar(TimeSpan espera)
    {
        var limite = DateTime.UtcNow + espera;
        lock (_cola)
        {
            var hasta = _pedidas;
            while (_hechas < hasta)
            {
                var falta = limite - DateTime.UtcNow;
                if (falta <= TimeSpan.Zero || !Monitor.Wait(_cola, falta))
                    return _hechas >= hasta;
            }
            return true;
        }
    }

    /// <summary>
    /// Se cierra la caja: lo que se pidió escribir queda en el disco (<see cref="Vaciar"/>) y se sueltan las conexiones que se
    /// guardaban abiertas para la siguiente lectura (el pool de Microsoft.Data.Sqlite): caja-local.db ya no queda abierta, y
    /// se puede mover o borrar. Lo que se lea o escriba después la vuelve a abrir.
    /// </summary>
    /// <returns>Si quedó todo lo que se pidió escribir.</returns>
    public bool Cerrar(TimeSpan espera)
    {
        var quedo = Vaciar(espera);
        using var conexion = new SqliteConnection(_cadena);
        SqliteConnection.ClearPool(conexion);
        return quedo;
    }

    /// <summary>El hilo del escritor: una escritura tras otra, cada una en su transacción; sin nada que hacer un rato, se va.</summary>
    private void EscribirEnSuHilo()
    {
        _escribiendo = this;
        while (true)
        {
            Escritura escritura;
            lock (_cola)
            {
                while (_pendientes.Count == 0)
                {
                    if (!Monitor.Wait(_cola, Reposo) && _pendientes.Count == 0)
                    {
                        _escritor = null;
                        return;
                    }
                }
                escritura = _pendientes.Dequeue();
            }
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            Exception? error = null;
            try
            {
                using var conexion = Abrir();
                using var transaccion = conexion.BeginTransaction();
                escritura.Escribir(new Lote(conexion, transaccion));
                transaccion.Commit();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            lock (_cola)
            {
                _hechas = escritura.Numero;
                Monitor.PulseAll(_cola);
            }
            if (error is null)
                escritura.Hecha.SetResult();
            else
                escritura.Hecha.SetException(error);
            if (reloj.Elapsed >= Larga)
            {
                try
                {
                    EscrituraLarga?.Invoke(escritura.Que, reloj.Elapsed);
                }
                catch (Exception)
                {
                    // Solo es para medir: el escritor sigue (un error aquí, en su propio hilo, cerraría la caja).
                }
            }
        }
    }

    /// <summary>
    /// Una lectura ve todo lo que se pidió escribir antes de ella: si algo sigue en la cola, lo espera (casi nunca: la cola
    /// casi siempre está vacía). No espera a la copia de la caja principal, que no se lee mientras la caja está abierta.
    /// </summary>
    private void AlDia()
    {
        if (_escribiendo == this)
            return;
        lock (_cola)
        {
            var hasta = _ultimaQueSeLee;
            while (_hechas < hasta)
                Monitor.Wait(_cola);
        }
    }

    /// <summary>
    /// La copia de la caja principal (el catálogo, los usuarios…), en pedazos, cada uno en su escritura de la cola: lo que la
    /// venta pide escribir mientras tanto entra entre un pedazo y otro. Al final queda vigente de un jalón; hasta entonces
    /// vale la de antes (también si la caja se cierra a medias). Lo que ya no se usa (la copia de antes, y la de antes de los
    /// pedazos, entera) se quita después, también de a poco. Las lecturas no esperan a nada de esto: la copia solo se lee al
    /// abrir la caja.
    /// </summary>
    /// <param name="utf8">La copia en JSON (UTF-8), convertida fuera de la cola.</param>
    public async Task GuardarCopiaAsync(byte[] utf8)
    {
        await _guardandoCopia.WaitAsync();
        try
        {
            var copia = Guid.NewGuid().ToString("N");
            var pedazos = Math.Max(1, (utf8.Length + TamanoDelPedazo - 1) / TamanoDelPedazo);
            for (var i = 0; i < pedazos; i++)
            {
                var indice = i;
                var datos = utf8.AsSpan(i * TamanoDelPedazo, Math.Min(TamanoDelPedazo, utf8.Length - i * TamanoDelPedazo)).ToArray();
                await EscribirAsync(l => l.PedazoDeCopia(copia, indice, datos), "un pedazo de la copia de la caja principal", laLeen: false);
            }
            await EscribirAsync(l => l.Guardar("copia", new CopiaGuardada(copia, pedazos)), "la copia de la caja principal", laLeen: false);
            foreach (var (vieja, indice) in PedazosQueSobran(copia))
                await EscribirAsync(l => l.QuitarPedazoDeCopia(vieja, indice), "lo que sobra de la copia de antes", laLeen: false);
            if (Existe("paquete"))
                await EscribirAsync(l => l.Borrar("paquete"), "la copia de antes de los pedazos", laLeen: false);
        }
        finally
        {
            _guardandoCopia.Release();
        }
    }

    /// <summary>
    /// La copia vigente de la caja principal, junta otra vez; si no hay (una caja de antes de los pedazos), la que se guardaba
    /// entera. Se lee de una vez, en una sola lectura de la base: la que estaba vigente al empezar, completa.
    /// </summary>
    public T? LeerCopia<T>()
    {
        AlDia();
        using var conexion = Abrir();
        using var lectura = conexion.BeginTransaction(deferred: true);
        if (Lote.Leer<CopiaGuardada>(conexion, lectura, "copia") is { } vigente)
        {
            using var comando = Comando(conexion, lectura, "SELECT Datos FROM CopiaPedazos WHERE Copia = $copia ORDER BY Indice", ("$copia", vigente.Copia));
            using var lector = comando.ExecuteReader();
            using var junta = new MemoryStream();
            var pedazos = 0;
            while (lector.Read())
            {
                junta.Write(lector.GetFieldValue<byte[]>(0));
                pedazos++;
            }
            if (pedazos == vigente.Pedazos)
                return JsonSerializer.Deserialize<T>(junta.GetBuffer().AsSpan(0, (int)junta.Length), Json);
        }
        return Lote.Leer<T>(conexion, lectura, "paquete");
    }

    /// <summary>Si hay algo guardado con esa clave, sin leerlo.</summary>
    private bool Existe(string clave)
    {
        AlDia();
        using var conexion = Abrir();
        using var comando = Comando(conexion, null, "SELECT EXISTS (SELECT 1 FROM Ajustes WHERE Clave = $clave)", ("$clave", clave));
        return Convert.ToInt64(comando.ExecuteScalar()) != 0;
    }

    /// <summary>Los pedazos de las copias que ya no están vigentes.</summary>
    private List<(string Copia, int Indice)> PedazosQueSobran(string vigente)
    {
        AlDia();
        using var conexion = Abrir();
        using var comando = Comando(conexion, null, "SELECT Copia, Indice FROM CopiaPedazos WHERE Copia <> $vigente ORDER BY Copia, Indice", ("$vigente", vigente));
        using var lector = comando.ExecuteReader();
        var sobran = new List<(string, int)>();
        while (lector.Read())
            sobran.Add((lector.GetString(0), lector.GetInt32(1)));
        return sobran;
    }

    /// <summary>
    /// Las fotos de los productos que bajó esta caja (entrega 13, parte 2): de qué producto es cada una, su huella y la foto. Se
    /// leen una vez, al abrir la caja; después se enseñan de memoria y lo que baja se escribe aquí por el escritor de siempre.
    /// </summary>
    public List<(Guid VarianteId, string Huella, byte[] Datos)> LeerFotos()
    {
        AlDia();
        using var conexion = Abrir();
        using var comando = Comando(conexion, null, "SELECT VarianteId, Huella, Datos FROM Fotos");
        using var lector = comando.ExecuteReader();
        var fotos = new List<(Guid, string, byte[])>();
        while (lector.Read())
        {
            if (Guid.TryParse(lector.GetString(0), out var varianteId))
                fotos.Add((varianteId, lector.GetString(1), lector.GetFieldValue<byte[]>(2)));
        }
        return fotos;
    }

    /// <summary>Lo que falta subir y lo que no se pudo subir (para reintentarlo), en orden.</summary>
    public List<ElementoBandeja> PorSubir(bool conErrores)
    {
        AlDia();
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

    public Task MarcarSubidoAsync(long orden, DateTime ahoraUtc) => EscribirAsync(l => l.Subido(orden, ahoraUtc), "la bandeja");

    public Task MarcarErrorAsync(long orden, string error) => EscribirAsync(l => l.ConError(orden, error), "la bandeja");

    /// <summary>Cuántas faltan por subir, cuántas no se pudieron subir y el último motivo.</summary>
    /// <summary>
    /// Cuántas faltan por subir (las que bloquean: mientras haya, la caja sigue por su cuenta), cuántas no se pudieron
    /// subir, el último motivo y cuántas esperan sin bloquear (las impresiones).
    /// </summary>
    public (int Pendientes, int ConError, string? UltimoError, int SinBloquear) Contar()
    {
        AlDia();
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
        AlDia();
        using var conexion = Abrir();
        using var comando = Comando(conexion, null, """
            SELECT COALESCE(SUM(Ventas), 0), COALESCE(SUM(Efectivo), 0), COALESCE(SUM(Tarjeta), 0), COALESCE(SUM(Transferencia), 0),
                   COALESCE(SUM(Entradas), 0), COALESCE(SUM(Salidas), 0), COALESCE(SUM(Devoluciones), 0), COALESCE(SUM(Cancelaciones), 0),
                   COALESCE(SUM(Redondeo), 0), COALESCE(SUM(Credito), 0), COALESCE(SUM(Abonos), 0), COALESCE(SUM(AbonosEfectivo), 0),
                   COALESCE(SUM(AbonosTarjeta), 0), COALESCE(SUM(AbonosTransferencia), 0), COALESCE(SUM(SaldoDevuelto), 0)
            FROM Diario WHERE TurnoId = $turno
            """, ("$turno", turnoId.ToString()));
        using var lector = comando.ExecuteReader();
        lector.Read();
        return new AnotacionDiario(Guid.Empty, turnoId, lector.GetInt32(0), lector.GetInt64(1), lector.GetInt64(2), lector.GetInt64(3),
            lector.GetInt64(4), lector.GetInt64(5), lector.GetInt64(6), lector.GetInt32(7), lector.GetInt64(8), lector.GetInt64(9), lector.GetInt32(10),
            lector.GetInt64(11), lector.GetInt64(12), lector.GetInt64(13), lector.GetInt64(14));
    }

    /// <summary>Lo propio sin confirmar de un cliente (o de todos), en el orden en que pasó.</summary>
    /// <summary>Si la caja tiene algo del cliente (fiado o abonado) en la bandeja que todavía no sube.</summary>
    public bool CreditoSinSubir(Guid clienteId)
    {
        AlDia();
        using var conexion = Abrir();
        using var comando = Comando(conexion, null, """
            SELECT EXISTS (SELECT 1 FROM Credito JOIN Bandeja ON Bandeja.Id = Credito.Id
                WHERE Credito.ClienteId = $cliente AND Credito.Confirmado = 0 AND Bandeja.SubidoUtc IS NULL)
            """, ("$cliente", clienteId.ToString()));
        return Convert.ToInt64(comando.ExecuteScalar()) != 0;
    }

    public List<CreditoPorConfirmar> CuentaPropia(Guid? clienteId = null)
    {
        AlDia();
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
        AlDia();
        using var conexion = Abrir();
        using var comando = Comando(conexion, null, "SELECT COUNT(*) FROM Credito WHERE Id = $id AND Confirmado = 1", ("$id", id.ToString()));
        return Convert.ToInt32(comando.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Cada conexión, con los ajustes contra apagones de la base del servicio (<see cref="AjustesContraApagones"/>):
    /// <c>synchronous=FULL</c>, porque aquí también se guardan ventas y no hay no-break, y <c>busy_timeout</c>, para que la
    /// venta y la sincronización a la vez esperen milisegundos y no 150 ms (entrega 10).
    /// </summary>
    private SqliteConnection Abrir()
    {
        var conexion = new SqliteConnection(_cadena);
        try
        {
            conexion.Open();
            Ejecutar(conexion, null, AjustesContraApagones.Pragmas);
            return conexion;
        }
        catch
        {
            conexion.Dispose();
            throw;
        }
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

/// <summary>
/// Escrituras en una sola transacción, en el hilo del escritor (<see cref="AlmacenLocal.EscribirAsync"/>): quedan todas al
/// terminar, o ninguna si algo falla.
/// </summary>
public sealed class Lote
{
    private readonly SqliteConnection _conexion;
    private readonly SqliteTransaction _transaccion;

    internal Lote(SqliteConnection conexion, SqliteTransaction transaccion)
    {
        _conexion = conexion;
        _transaccion = transaccion;
    }

    /// <summary>Lo guardado con esa clave, dentro de esta misma transacción (el folio antes de subirlo, por ejemplo).</summary>
    public T? Leer<T>(string clave) => Leer<T>(_conexion, _transaccion, clave);

    internal static T? Leer<T>(SqliteConnection conexion, SqliteTransaction? transaccion, string clave)
    {
        using var comando = AlmacenLocal.Comando(conexion, transaccion, "SELECT Valor FROM Ajustes WHERE Clave = $clave", ("$clave", clave));
        return comando.ExecuteScalar() is string valor ? JsonSerializer.Deserialize<T>(valor, AlmacenLocal.Json) : default;
    }

    public Lote Guardar<T>(string clave, T valor) => GuardarJson(clave, JsonSerializer.Serialize(valor, AlmacenLocal.Json));

    /// <summary>Lo que ya viene en JSON: la copia de la caja principal se convierte antes, fuera de la cola.</summary>
    public Lote GuardarJson(string clave, string json)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, "INSERT INTO Ajustes (Clave, Valor) VALUES ($clave, $valor) ON CONFLICT(Clave) DO UPDATE SET Valor = excluded.Valor",
            ("$clave", clave), ("$valor", json));
        return this;
    }

    /// <summary>Un pedazo de una copia de la caja principal (<see cref="AlmacenLocal.GuardarCopiaAsync"/>).</summary>
    public Lote PedazoDeCopia(string copia, int indice, byte[] datos)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, "INSERT OR REPLACE INTO CopiaPedazos (Copia, Indice, Datos) VALUES ($copia, $indice, $datos)",
            ("$copia", copia), ("$indice", indice), ("$datos", datos));
        return this;
    }

    /// <summary>Un pedazo de una copia que ya no está vigente; el de la vigente no se toca.</summary>
    public Lote QuitarPedazoDeCopia(string copia, int indice)
    {
        if (Leer<AlmacenLocal.CopiaGuardada>("copia")?.Copia != copia)
            AlmacenLocal.Ejecutar(_conexion, _transaccion, "DELETE FROM CopiaPedazos WHERE Copia = $copia AND Indice = $indice", ("$copia", copia), ("$indice", indice));
        return this;
    }

    /// <summary>La foto de un producto que bajó esta caja (entrega 13, parte 2): nueva, o en lugar de la que tenía.</summary>
    public Lote GuardarFoto(Guid varianteId, string huella, byte[] datos)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, "INSERT OR REPLACE INTO Fotos (VarianteId, Huella, Datos) VALUES ($variante, $huella, $datos)",
            ("$variante", varianteId.ToString()), ("$huella", huella), ("$datos", datos));
        return this;
    }

    /// <summary>Las fotos de los productos que ya no tienen (se les quitó en la caja principal).</summary>
    public Lote QuitarFotos(IEnumerable<Guid> variantes)
    {
        foreach (var varianteId in variantes)
            AlmacenLocal.Ejecutar(_conexion, _transaccion, "DELETE FROM Fotos WHERE VarianteId = $variante", ("$variante", varianteId.ToString()));
        return this;
    }

    /// <summary>Ya subió: no se vuelve a mandar.</summary>
    public Lote Subido(long orden, DateTime ahoraUtc)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, "UPDATE Bandeja SET SubidoUtc = $fecha, Error = NULL WHERE Orden = $orden",
            ("$fecha", ahoraUtc.ToString("O", CultureInfo.InvariantCulture)), ("$orden", orden));
        return this;
    }

    /// <summary>El servicio no lo aceptó: queda con su motivo y se reintenta más tarde.</summary>
    public Lote ConError(long orden, string error)
    {
        AlmacenLocal.Ejecutar(_conexion, _transaccion, "UPDATE Bandeja SET Error = $error WHERE Orden = $orden", ("$error", error), ("$orden", orden));
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
                Credito, Abonos, AbonosEfectivo, AbonosTarjeta, AbonosTransferencia, SaldoDevuelto)
            VALUES ($id, $turno, $ventas, $efectivo, $tarjeta, $transferencia, $entradas, $salidas, $devoluciones, $cancelaciones, $redondeo,
                $credito, $abonos, $abonosEfectivo, $abonosTarjeta, $abonosTransferencia, $saldoDevuelto)
            """,
            ("$id", a.Id.ToString()), ("$turno", a.TurnoId.ToString()), ("$ventas", a.Ventas), ("$efectivo", a.Efectivo), ("$tarjeta", a.Tarjeta),
            ("$transferencia", a.Transferencia), ("$entradas", a.Entradas), ("$salidas", a.Salidas), ("$devoluciones", a.Devoluciones),
            ("$cancelaciones", a.Cancelaciones), ("$redondeo", a.Redondeo), ("$credito", a.Credito), ("$abonos", a.Abonos),
            ("$abonosEfectivo", a.AbonosEfectivo), ("$abonosTarjeta", a.AbonosTarjeta), ("$abonosTransferencia", a.AbonosTransferencia),
            ("$saldoDevuelto", a.SaldoDevuelto));
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

}
