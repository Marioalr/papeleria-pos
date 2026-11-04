using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Auditoría del 2026-11-04: un respaldo que no sirve o que no sale se decía solo en el registro. El dueño se enteraba el día que
/// necesitaba restaurar, y una base dañada sin apagón iba reemplazando a los respaldos buenos.
/// </summary>
public sealed class RespaldosQueFallanTests : IDisposable
{
    private readonly string _carpeta = Directory.CreateTempSubdirectory("pos-respaldos-fallan-").FullName;

    public void Dispose() => ArchivosDePrueba.BorrarCarpeta(_carpeta);

    private string Ruta => Path.Combine(_carpeta, "pos.db");
    private string Cadena => $"Data Source={Ruta};Pooling=False";
    private string CarpetaRespaldos => Path.Combine(_carpeta, "respaldos");
    private string Hoy => $"{HoraMexico.ALocal(DateTime.UtcNow):yyyy-MM-dd}";

    private sealed class DiscoCon(long? libres) : IEspacioEnDisco
    {
        public long? Libres { get; set; } = libres;

        public long? Libre(string carpeta) => Libres;
    }

    private void CrearBase()
    {
        using var conexion = new SqliteConnection(Cadena);
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = """
            CREATE TABLE Ventas (Id INTEGER PRIMARY KEY, Detalle TEXT NOT NULL);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000)
            INSERT INTO Ventas (Detalle) SELECT printf('Venta %d con su detalle para llenar varias páginas de la base', i) FROM n;
            """;
        comando.ExecuteNonQuery();
    }

    /// <summary>Un sector malo del disco, sin apagón: nadie marcó la base como dañada.</summary>
    private void DanarPaginas()
    {
        using var archivo = new FileStream(Ruta, FileMode.Open, FileAccess.Write);
        archivo.Position = 4096 * 5;
        archivo.Write(new byte[4096 * 3]);
    }

    private ServicioRespaldos Servicio(IEspacioEnDisco? disco = null) =>
        new(Cadena, CarpetaRespaldos, 30, NullLogger<ServicioRespaldos>.Instance) { Disco = disco };

    private void Respaldo(string nombre)
    {
        Directory.CreateDirectory(CarpetaRespaldos);
        File.WriteAllText(Path.Combine(CarpetaRespaldos, nombre), "");
    }

    private string[] LosRespaldos() => Directory.GetFiles(CarpetaRespaldos).Select(a => Path.GetFileName(a)!).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Una_base_danada_sin_apagon_no_deja_respaldo_ni_borra_los_buenos_y_se_avisa()
    {
        CrearBase();
        DanarPaginas();
        // Vencidos todos, y más de los 5 que siempre se quedan.
        foreach (var dia in Enumerable.Range(1, 7))
            Respaldo($"pos-2020-01-0{dia}.db");
        var respaldos = Servicio();

        respaldos.Hacer();

        Assert.Equal(7, LosRespaldos().Length);
        Assert.DoesNotContain(LosRespaldos(), a => a.Contains(Hoy));
        Assert.Contains("puede estar dañada", respaldos.Aviso);
        Assert.Contains("malformed", respaldos.Fallo);
    }

    [Fact]
    public void Respaldar_ahora_con_la_base_danada_dice_por_que_no_salio()
    {
        CrearBase();
        DanarPaginas();

        Assert.Throws<CopiaDanadaException>(() => Respaldos.Hacer(Cadena, CarpetaRespaldos, new DateTime(2026, 11, 4, 13, 0, 0)));

        Assert.Empty(Directory.GetFiles(CarpetaRespaldos));
    }

    /// <summary>Un índice que no cuadra con su tabla: la revisión rápida lo da por bueno y al restaurarlo no pasa la completa.</summary>
    [Fact]
    public void Un_indice_que_no_cuadra_tampoco_se_guarda_como_respaldo()
    {
        using (var conexion = new SqliteConnection(Cadena))
        {
            conexion.Open();
            using var comando = conexion.CreateCommand();
            comando.CommandText = """
                CREATE TABLE T (Id INTEGER PRIMARY KEY, A TEXT, B TEXT);
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500)
                INSERT INTO T SELECT i, hex(randomblob(20)), hex(randomblob(20)) FROM n;
                CREATE INDEX IX_A ON T (A);
                CREATE INDEX IX_B ON T (B);
                """;
            comando.ExecuteNonQuery();
            comando.CommandText = "SELECT rootpage FROM sqlite_master WHERE name = 'IX_A'";
            var a = (long)comando.ExecuteScalar()!;
            comando.CommandText = "SELECT rootpage FROM sqlite_master WHERE name = 'IX_B'";
            var b = (long)comando.ExecuteScalar()!;
            comando.CommandText = $"PRAGMA writable_schema=ON; UPDATE sqlite_master SET rootpage={b} WHERE name='IX_A'; UPDATE sqlite_master SET rootpage={a} WHERE name='IX_B'; PRAGMA writable_schema=OFF;";
            comando.ExecuteNonQuery();
        }
        Assert.Equal(["ok"], BaseDeDatos.RevisionRapida(Cadena));

        Assert.Throws<CopiaDanadaException>(() => Respaldos.Hacer(Cadena, CarpetaRespaldos, new DateTime(2026, 11, 4, 13, 0, 0)));

        Assert.Empty(Directory.GetFiles(CarpetaRespaldos));
    }

    [Fact]
    public void Un_respaldo_que_no_sale_se_avisa_hasta_que_sale_uno()
    {
        CrearBase();
        var disco = new DiscoCon(1);
        var respaldos = Servicio(disco);
        Assert.Null(respaldos.Aviso);
        Assert.Null(respaldos.Fallo);

        respaldos.Hacer();

        Assert.Contains("no cabe en el disco", respaldos.Aviso);
        Assert.Contains("avisa al dueño", respaldos.Aviso);
        Assert.Contains("hacen falta", respaldos.Fallo);
        Assert.False(Directory.Exists(CarpetaRespaldos) && Directory.GetFiles(CarpetaRespaldos).Length > 0);

        disco.Libres = null;
        respaldos.Hacer();

        Assert.Null(respaldos.Aviso);
        Assert.Null(respaldos.Fallo);
        Assert.Single(LosRespaldos());
    }

    [Fact]
    public void Si_no_cabe_primero_se_borra_lo_vencido()
    {
        CrearBase();
        foreach (var dia in Enumerable.Range(1, 6))
            Respaldo($"pos-2020-01-0{dia}.db");

        Servicio(new DiscoCon(1)).Hacer();

        Assert.Equal(["pos-2020-01-02.db", "pos-2020-01-03.db", "pos-2020-01-04.db", "pos-2020-01-05.db", "pos-2020-01-06.db"], LosRespaldos());
    }

    [Fact]
    public void Si_al_borrar_lo_vencido_ya_cabe_la_copia_sale()
    {
        CrearBase();
        Respaldo("pos-2020-01-01.db");
        var disco = new DiscoCon(1);
        var respaldos = new ServicioRespaldos(Cadena, CarpetaRespaldos, 30, NullLogger<ServicioRespaldos>.Instance)
        {
            Disco = disco,
            Limpiar = (carpeta, hoy, dias) =>
            {
                disco.Libres = null;
                return 0;
            },
        };

        respaldos.Hacer();

        Assert.Null(respaldos.Aviso);
        Assert.Contains($"pos-{Hoy}.db", LosRespaldos());
    }

    /// <summary>La copia del corte Z se hacía una vez: con la del día ya hecha, nadie la volvía a intentar.</summary>
    [Fact]
    public void Si_fallo_la_del_corte_la_revision_de_cada_hora_vuelve_a_intentar()
    {
        CrearBase();
        Directory.CreateDirectory(CarpetaRespaldos);
        var disco = new DiscoCon(null);
        var respaldos = Servicio(disco);
        respaldos.DiarioSiFalta();
        var hecha = Path.Combine(CarpetaRespaldos, $"pos-{Hoy}.db");
        var antes = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(hecha, antes);

        respaldos.DiarioSiFalta();
        Assert.Equal(antes, File.GetLastWriteTimeUtc(hecha));

        disco.Libres = 1;
        respaldos.Hacer("z-2100");
        Assert.NotNull(respaldos.Aviso);

        disco.Libres = null;
        respaldos.DiarioSiFalta();

        Assert.Null(respaldos.Aviso);
        Assert.NotEqual(antes, File.GetLastWriteTimeUtc(hecha));
    }

    /// <summary>Si la limpieza encuentra un archivo ocupado, sigue con los demás y al final lo dice.</summary>
    [Fact]
    public void Un_archivo_ocupado_no_detiene_la_limpieza_de_los_demas()
    {
        foreach (var dia in Enumerable.Range(1, 8))
            Respaldo($"pos-2020-01-0{dia}.db");
        var ocupado = Path.Combine(CarpetaRespaldos, "pos-2020-01-01.db");

        var error = Assert.Throws<IOException>(() => Respaldos.Limpiar(CarpetaRespaldos, new DateOnly(2026, 11, 4), 30, archivo =>
        {
            if (archivo == ocupado)
                throw new IOException("ocupado");
            File.Delete(archivo);
        }));

        Assert.Equal("ocupado", error.Message);
        Assert.Equal(["pos-2020-01-01.db", "pos-2020-01-04.db", "pos-2020-01-05.db", "pos-2020-01-06.db", "pos-2020-01-07.db", "pos-2020-01-08.db"], LosRespaldos());
    }

    /// <summary>
    /// El aviso de respaldos y el de poco disco van aparte del de la base: actualizar.ps1 y restaurar.ps1 toman <c>avisoBase</c> por una
    /// base dañada, y con poco disco se negaban a actualizar o regresaban la versión.
    /// </summary>
    [Fact]
    public async Task La_franja_de_la_caja_dice_que_el_respaldo_no_sale_y_no_es_el_aviso_de_la_base()
    {
        var disco = new DiscoCon(1);
        await using var servicio = await ServicioDePrueba.CrearAsync(s => s.Replace(ServiceDescriptor.Singleton<IEspacioEnDisco>(disco)),
            ("Respaldos:Activo", "true"), ("Respaldos:Carpeta", CarpetaRespaldos));
        var http = servicio.Fabrica.CreateClient();

        servicio.Fabrica.Services.GetRequiredService<ServicioRespaldos>().Hacer();
        var estado = await http.GetFromJsonAsync<EstadoServicioDto>("api/estado");

        Assert.Null(estado!.AvisoBase);
        Assert.Contains("no cabe en el disco", estado.AvisoRespaldos);

        var cajaLocal = Path.Combine(_carpeta, "caja-local.db");
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var contingencia = new Contingencia(new AlmacenLocal(cajaLocal), new HttpClient(servicio.Fabrica.Server.CreateHandler()),
            new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id });
        await contingencia.LatidoAsync();

        Assert.Equal(estado.AvisoRespaldos, contingencia.AvisoRespaldos);
        Assert.Null(contingencia.AvisoBase);
    }
}
