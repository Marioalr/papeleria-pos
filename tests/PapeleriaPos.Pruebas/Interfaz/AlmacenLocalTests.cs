using Microsoft.Data.Sqlite;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>La base local de una caja ya instalada se pone al día sin perder lo que tenía.</summary>
public sealed class AlmacenLocalTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"almacen-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _ruta, _ruta + "-wal", _ruta + "-shm" })
            File.Delete(archivo);
    }

    [Fact]
    public void Una_base_de_antes_del_credito_gana_su_cuenta_propia_y_conserva_el_diario()
    {
        var turno = Ids.Nuevo();
        using (var conexion = new SqliteConnection($"Data Source={_ruta}"))
        {
            conexion.Open();
            using var comando = conexion.CreateCommand();
            comando.CommandText = $"""
                CREATE TABLE Ajustes (Clave TEXT PRIMARY KEY, Valor TEXT NOT NULL);
                CREATE TABLE Bandeja (Orden INTEGER PRIMARY KEY AUTOINCREMENT, Id TEXT NOT NULL UNIQUE, Ruta TEXT NOT NULL, Json TEXT NOT NULL,
                    CreadoUtc TEXT NOT NULL, SubidoUtc TEXT, Error TEXT);
                CREATE TABLE Diario (Id TEXT PRIMARY KEY, TurnoId TEXT NOT NULL, Ventas INTEGER NOT NULL, Efectivo INTEGER NOT NULL, Tarjeta INTEGER NOT NULL,
                    Transferencia INTEGER NOT NULL, Entradas INTEGER NOT NULL, Salidas INTEGER NOT NULL, Devoluciones INTEGER NOT NULL,
                    Cancelaciones INTEGER NOT NULL);
                INSERT INTO Diario VALUES ('{Ids.Nuevo()}', '{turno}', 1, 5000, 0, 0, 0, 0, 0, 0);
                """;
            comando.ExecuteNonQuery();
        }

        var almacen = new AlmacenLocal(_ruta);
        var cliente = Ids.Nuevo();
        almacen.Escribir(lote => lote.Credito(Ids.Nuevo(), cliente, 2500, DateTime.UtcNow, new DateOnly(2026, 11, 7))
            .Anotar(new AnotacionDiario(Ids.Nuevo(), turno, Redondeo: 45, SaldoDevuelto: 12300)));

        var diario = almacen.Diario(turno);
        Assert.Equal((1, 5000L, 0L, 45L, 12300L), (diario.Ventas, diario.Efectivo, diario.Credito, diario.Redondeo, diario.SaldoDevuelto));
        var fila = Assert.Single(almacen.CuentaPropia(cliente));
        Assert.Equal((2500L, new DateOnly(2026, 11, 7), false), (fila.Local.MontoCentavos, fila.Local.Vence, fila.Local.PorVerificar));
    }

    [Fact]
    public async Task Lo_anotado_hace_rato_que_nunca_paso_por_la_bandeja_se_quita_y_lo_demas_se_queda()
    {
        var almacen = new AlmacenLocal(_ruta);
        var cliente = Ids.Nuevo();
        var (huerfano, enBandeja, subido, reciente) = (Ids.Nuevo(), Ids.Nuevo(), Ids.Nuevo(), Ids.Nuevo());
        var antes = DateTime.UtcNow.AddMinutes(-5);
        almacen.Escribir(lote => lote.Credito(huerfano, cliente, 1000, antes, null).Credito(enBandeja, cliente, 2000, antes, null)
            .Credito(subido, cliente, 4000, antes, null)
            .Credito(reciente, cliente, 3000, DateTime.UtcNow, null)
            .Encolar(enBandeja, "api/bandeja/ventas", new { Id = enBandeja }).Encolar(subido, "api/bandeja/abonos", new { Id = subido }));
        // Lo que ya subió (el servicio lo tiene, aunque la foto de sus saldos fuera de antes) tampoco es huérfano.
        await almacen.MarcarSubidoAsync(almacen.PorSubir(conErrores: true).Single(e => e.Id == subido).Orden, DateTime.UtcNow);

        almacen.Escribir(lote => lote.QuitarCreditoHuerfano([huerfano, enBandeja, subido, reciente], DateTime.UtcNow.AddMinutes(-2)));

        Assert.Equal(new[] { enBandeja, subido, reciente }.Order(), almacen.CuentaPropia(cliente).Select(f => f.Id).Order());
    }

    [Fact]
    public void Lo_confirmado_deja_de_contarse_y_no_revive_si_se_vuelve_a_anotar()
    {
        var almacen = new AlmacenLocal(_ruta);
        var cliente = Ids.Nuevo();
        var id = Ids.Nuevo();
        almacen.Escribir(lote => lote.Credito(id, cliente, 5000, DateTime.UtcNow, null));

        almacen.Escribir(lote => lote.ConfirmarCredito([id], DateTime.UtcNow.AddDays(-1)));
        almacen.Escribir(lote => lote.Credito(id, cliente, 5000, DateTime.UtcNow, null));

        Assert.Empty(almacen.CuentaPropia(cliente));
        Assert.True(almacen.CreditoConfirmado(id));
    }

    /// <summary>
    /// Un solo escritor (entrega 5): muchas escrituras a la vez, desde varios hilos y también esperando aquí, van una tras
    /// otra en el hilo del escritor, y ninguna falla porque la base esté ocupada (SQLITE_BUSY). Las lecturas, a la par.
    /// </summary>
    [Fact]
    public async Task Las_escrituras_a_la_vez_van_una_tras_otra_en_su_hilo_y_ninguna_falla_por_ocupada()
    {
        var almacen = new AlmacenLocal(_ruta);
        var (enCurso, maximo) = (0, 0);
        var candado = new object();
        var hilos = new System.Collections.Concurrent.ConcurrentBag<string?>();
        void Escribir(Lote lote, Guid id)
        {
            var ahora = Interlocked.Increment(ref enCurso);
            lock (candado)
                maximo = Math.Max(maximo, ahora);
            hilos.Add(Thread.CurrentThread.Name);
            lote.Encolar(id, "api/bandeja/bitacora", new { Id = id });
            Thread.SpinWait(2000);
            Interlocked.Decrement(ref enCurso);
        }

        var escrituras = Enumerable.Range(0, 8).Select(n => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
            {
                var id = Ids.Nuevo();
                if (i % 5 == 0)
                    almacen.Escribir(l => Escribir(l, id));
                else
                    await almacen.EscribirAsync(l => Escribir(l, id));
            }
        }));
        var lecturas = Enumerable.Range(0, 4).Select(n => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
                Assert.InRange(almacen.Contar().Pendientes + almacen.PorSubir(conErrores: true).Count, 0, 400);
        }));
        await Task.WhenAll(escrituras.Concat(lecturas));

        Assert.Equal(200, almacen.Contar().Pendientes);
        Assert.Equal(1, maximo);
        Assert.All(hilos, nombre => Assert.Equal("Escritor de caja-local.db", nombre));
    }

    /// <summary>
    /// La pantalla no espera a la sincronización: mientras se escribe la copia de la caja principal (grande), lo que pide
    /// escribir la pantalla queda en la cola sin detenerla, y lo que lee no espera a la copia. Al terminar, todo quedó en
    /// orden; al cerrar la caja, nada se queda en la cola.
    /// </summary>
    [Fact]
    public async Task La_pantalla_no_espera_a_que_se_escriba_la_copia()
    {
        var almacen = new AlmacenLocal(_ruta);
        almacen.Escribir(l => l.Guardar("folio", 7));
        using var empezo = new ManualResetEventSlim();
        using var soltar = new ManualResetEventSlim();
        var copia = Task.Run(() => almacen.EscribirAsync(l =>
        {
            l.GuardarJson("paquete", "{}");
            empezo.Set();
            soltar.Wait();
        }, "la copia de la caja principal", laLeen: false));
        Assert.True(empezo.Wait(TimeSpan.FromSeconds(20)), "La copia no se empezó a escribir.");

        // Lo que lee la pantalla no espera a la copia (WAL: la escritura no detiene las lecturas).
        var folio = await Task.Run(() => almacen.Leer<int?>("folio")).WaitAsync(TimeSpan.FromSeconds(20));
        // Lo que escribe la pantalla queda en la cola: la llamada regresa enseguida y termina después de la copia.
        var espera = almacen.EscribirAsync(l => l.Guardar("espera", new[] { "ticket" }), "los tickets en espera");

        Assert.Equal(7, folio);
        Assert.False(espera.IsCompleted);
        soltar.Set();
        await Task.WhenAll(copia, espera).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(["ticket"], almacen.Leer<string[]>("espera")!);
        Assert.True(almacen.Vaciar(TimeSpan.FromSeconds(20)));
        Assert.Equal("{}", System.Text.Json.JsonSerializer.Serialize(new AlmacenLocal(_ruta).Leer<object>("paquete")));
    }

    [Fact]
    public async Task Si_una_escritura_falla_no_queda_nada_de_ella_y_las_demas_siguen()
    {
        var almacen = new AlmacenLocal(_ruta);

        var falla = almacen.EscribirAsync(l =>
        {
            l.Guardar("folio", 9);
            throw new InvalidOperationException("Falla de prueba");
        });
        var sigue = almacen.EscribirAsync(l => l.Guardar("turno", "abierto"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => falla);
        await sigue;
        Assert.Null(almacen.Leer<int?>("folio"));
        Assert.Equal("abierto", almacen.Leer<string>("turno"));
    }
}
