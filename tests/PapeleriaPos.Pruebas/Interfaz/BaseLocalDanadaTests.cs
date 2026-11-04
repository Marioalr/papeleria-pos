using Microsoft.Data.Sqlite;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// caja-local.db después de un apagón: dañada, la caja abre igual con una nueva, con lo que se pudo rescatar y diciéndolo; y lo que ya
/// subió de la bandeja no se queda para siempre.
/// </summary>
public sealed class BaseLocalDanadaTests : IDisposable
{
    private readonly string _carpeta = Directory.CreateTempSubdirectory("caja-danada-").FullName;

    private string Ruta => Path.Combine(_carpeta, "caja-local.db");

    public void Dispose() => ArchivosDePrueba.BorrarCarpeta(_carpeta);

    private static void Cerrar(AlmacenLocal almacen) => Assert.True(almacen.Cerrar(TimeSpan.FromSeconds(5)));

    private static void Encolar(AlmacenLocal almacen, int cuantas, string? relleno = null)
    {
        for (var i = 0; i < cuantas; i++)
            almacen.Escribir(l => l.Encolar(Guid.NewGuid(), "api/bandeja/ventas", new { Numero = i, Relleno = relleno }).Guardar("folio", i));
    }

    private void Ejecutar(string sql)
    {
        using var conexion = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Ruta, Pooling = false }.ToString());
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.ExecuteNonQuery();
    }

    /// <summary>Lo que no es una base (el archivo quedó en ceros o con basura): se aparta y la caja abre con una nueva, y lo dice.</summary>
    [Fact]
    public void Un_archivo_que_no_es_una_base_se_aparta_y_abre_con_una_nueva()
    {
        File.WriteAllBytes(Ruta, Enumerable.Repeat((byte)7, 8192).ToArray());

        var almacen = new AlmacenLocal(Ruta);

        Assert.Equal((0, 0), (almacen.Contar().Pendientes, almacen.Contar().ConError));
        Assert.Contains("se apartó", almacen.BaseApartada);
        Assert.Single(Directory.GetFiles(_carpeta, "caja-local.danada-*.db"));
        Cerrar(almacen);
    }

    /// <summary>
    /// Dañada a media base (un pedazo en ceros, como lo deja un disco que miente): lo que falta por subir que se pueda leer pasa a la base
    /// nueva, con su folio; lo que ya subió, no. La base dañada se queda apartada.
    /// </summary>
    [Fact]
    public async Task Danada_a_medias_se_rescata_lo_que_falta_por_subir()
    {
        var almacen = new AlmacenLocal(Ruta);
        Encolar(almacen, 5);
        var subida = almacen.PorSubir(conErrores: false)[0];
        await almacen.MarcarSubidoAsync(subida.Orden, DateTime.UtcNow);
        // Mucho en otra tabla, para dañar lejos de los renglones que se rescatan.
        almacen.Escribir(l => l.Guardar("relleno", new string('x', 200_000)));
        Cerrar(almacen);
        Ejecutar("PRAGMA wal_checkpoint(TRUNCATE)");
        Dañar(Ruta, desde: new FileInfo(Ruta).Length - 32_768, cuanto: 8192);
        // La marca de que no se cerró bien: con ella se revisa.
        File.WriteAllText(Ruta + ".en-uso", "se fue la luz");

        var otra = new AlmacenLocal(Ruta);

        Assert.Contains("se apartó", otra.BaseApartada);
        Assert.Equal(4, otra.Contar().Pendientes);
        Assert.Equal(4, otra.Leer<int?>("folio"));
        Assert.Single(Directory.GetFiles(_carpeta, "caja-local.danada-*.db"));
        Cerrar(otra);
    }

    /// <summary>
    /// La caja se cerró a la fuerza mientras rescataba: el archivo ya estaba apartado y la base nueva está sana. Al abrir se retoma, una
    /// sola vez: lo que después suba no vuelve a entrar.
    /// </summary>
    [Fact]
    public async Task El_rescate_que_no_termino_se_retoma_al_abrir_una_sola_vez()
    {
        var almacen = new AlmacenLocal(Ruta);
        Encolar(almacen, 3);
        Cerrar(almacen);
        Ejecutar("PRAGMA wal_checkpoint(TRUNCATE)");
        File.Move(Ruta, Path.Combine(_carpeta, "caja-local.danada-20261104-120000.db"));

        var otra = new AlmacenLocal(Ruta);

        Assert.Equal(3, otra.Contar().Pendientes);
        Assert.Contains("se apartó", otra.BaseApartada);
        foreach (var elemento in otra.PorSubir(conErrores: false))
            await otra.MarcarSubidoAsync(elemento.Orden, DateTime.UtcNow);
        Cerrar(otra);

        var tercera = new AlmacenLocal(Ruta);

        Assert.Equal(0, tercera.Contar().Pendientes);
        Assert.Null(tercera.BaseApartada);
        Cerrar(tercera);
    }

    /// <summary>Lo que no se pudo leer no se olvida al volver a abrir (una PC que se apaga cada noche): el aviso sigue hasta que se quita.</summary>
    [Fact]
    public async Task Lo_que_no_se_pudo_leer_sigue_avisando_al_abrir_hasta_que_se_quita()
    {
        File.WriteAllBytes(Ruta, Enumerable.Repeat((byte)7, 8192).ToArray());
        var almacen = new AlmacenLocal(Ruta);
        Assert.Contains("no se pudieron leer", almacen.BaseApartada);
        Cerrar(almacen);

        var otra = new AlmacenLocal(Ruta);
        Assert.Contains("no se pudieron leer", otra.BaseApartada);
        Assert.True(otra.BaseApartadaSeQueda);
        await otra.QuitarAvisoDeBaseAsync();
        Assert.Null(otra.BaseApartada);
        Cerrar(otra);

        var tercera = new AlmacenLocal(Ruta);
        Assert.Null(tercera.BaseApartada);
        Cerrar(tercera);
    }

    /// <summary>Si todo se rescató, el aviso es de esa vez: no hay nada que siga faltando.</summary>
    [Fact]
    public void Si_todo_se_rescato_el_aviso_no_se_queda()
    {
        var almacen = new AlmacenLocal(Ruta);
        Encolar(almacen, 2);
        Cerrar(almacen);
        Ejecutar("PRAGMA wal_checkpoint(TRUNCATE)");
        File.Move(Ruta, Path.Combine(_carpeta, "caja-local.danada-20261104-120000.db"));

        var otra = new AlmacenLocal(Ruta);
        Assert.False(otra.BaseApartadaSeQueda);
        Cerrar(otra);
    }

    /// <summary>Lo que no alcanzó a copiarse por el tope de tiempo también cuenta como no leído.</summary>
    [Fact]
    public void Lo_que_no_alcanzo_el_tope_del_rescate_cuenta_como_no_leido()
    {
        var almacen = new AlmacenLocal(Ruta);
        Encolar(almacen, 3);
        Cerrar(almacen);
        Ejecutar("PRAGMA wal_checkpoint(TRUNCATE)");
        File.Move(Ruta, Path.Combine(_carpeta, "caja-local.danada-20261104-120000.db"));

        var otra = new AlmacenLocal(Ruta, topeDelRescate: TimeSpan.Zero);

        Assert.Contains("3 no se pudieron leer", otra.BaseApartada);
        Assert.Equal(0, otra.Contar().Pendientes);
        Cerrar(otra);
    }

    /// <summary>Con un cierre bueno no queda marca, y sin marca no hay revisión; con la marca y la base sana, abre sin avisar.</summary>
    [Fact]
    public void La_marca_se_quita_al_cerrar_bien_y_una_base_sana_no_avisa()
    {
        var almacen = new AlmacenLocal(Ruta);
        Assert.True(File.Exists(Ruta + ".en-uso"));
        Cerrar(almacen);
        Assert.False(File.Exists(Ruta + ".en-uso"));

        File.WriteAllText(Ruta + ".en-uso", "se fue la luz");
        var otra = new AlmacenLocal(Ruta);

        Assert.Null(otra.BaseApartada);
        Cerrar(otra);
    }

    /// <summary>Un ajuste que otra versión guardó de otra forma no deja a la caja sin abrir.</summary>
    [Fact]
    public void Un_ajuste_guardado_de_otra_forma_no_deja_sin_abrir()
    {
        var almacen = new AlmacenLocal(Ruta);
        almacen.Escribir(l => l.Guardar("saldos", 1).Guardar("cobros-sin-confirmar", 1).Guardar("piloto-eleventa", "no").Guardar("turno-sin-alta", 1));
        Ejecutar("UPDATE Ajustes SET Valor = '{' ");

        var contingencia = new Contingencia(almacen, new HttpClient(), new ConexionCaja { Servicio = new Uri("http://localhost/") });

        Assert.Null(contingencia.Saldos);
        contingencia.Cerrar();
    }

    /// <summary>
    /// Lo que subió pierde su cuerpo (las miniaturas de las impresiones) de una vez, y el renglón se va a los 30 días; lo que falta por
    /// subir no se toca.
    /// </summary>
    [Fact]
    public async Task Lo_que_subio_no_se_queda_para_siempre()
    {
        var almacen = new AlmacenLocal(Ruta);
        Encolar(almacen, 3, relleno: new string('m', 100_000));
        var elementos = almacen.PorSubir(conErrores: false);
        var ahora = DateTime.UtcNow;
        await almacen.MarcarSubidoAsync(elementos[0].Orden, ahora.AddDays(-31));
        await almacen.MarcarSubidoAsync(elementos[1].Orden, ahora.AddDays(-1));
        // Como lo que subió antes de este arreglo: con su cuerpo.
        Ejecutar($"UPDATE Bandeja SET Json = '{{\"miniatura\":1}}' WHERE Orden = {elementos[1].Orden}");

        await almacen.LimpiarBandejaAsync(ahora);

        Assert.Equal(2L, Escalar("SELECT COUNT(*) FROM Bandeja"));
        Assert.Equal(0L, Escalar($"SELECT COUNT(*) FROM Bandeja WHERE Orden = {elementos[0].Orden}"));
        Assert.Equal(2L, Escalar($"SELECT LENGTH(Json) FROM Bandeja WHERE Orden = {elementos[1].Orden}"));
        Assert.Equal(elementos[2].Json, Assert.Single(almacen.PorSubir(conErrores: false)).Json);
        Cerrar(almacen);
    }

    /// <summary>Lo que se marcó como ya anotado en eleventa conserva su cuerpo hasta la limpieza: un renglón equivocado se puede recuperar a mano.</summary>
    [Fact]
    public async Task Lo_que_ya_se_anoto_en_eleventa_conserva_su_cuerpo_hasta_la_limpieza()
    {
        var almacen = new AlmacenLocal(Ruta);
        Encolar(almacen, 2);
        var elementos = almacen.PorSubir(conErrores: false);
        var ahora = DateTime.UtcNow;
        await almacen.EscribirAsync(l => l.YaAnotado(elementos[0].Orden, ahora, "ya anotado"));
        await almacen.EscribirAsync(l => l.YaAnotado(elementos[1].Orden, ahora.AddDays(-31), "ya anotado"));
        Assert.Equal((0, 0), (almacen.Contar().Pendientes, almacen.Contar().ConError));

        await almacen.LimpiarBandejaAsync(ahora);

        Assert.Equal(elementos[0].Json.Length, Escalar($"SELECT LENGTH(Json) FROM Bandeja WHERE Orden = {elementos[0].Orden}"));
        Assert.Equal(0L, Escalar($"SELECT COUNT(*) FROM Bandeja WHERE Orden = {elementos[1].Orden}"));
        Assert.Empty(almacen.PorSubir(conErrores: true));
        Cerrar(almacen);
    }

    private long Escalar(string sql)
    {
        using var conexion = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Ruta, Pooling = false }.ToString());
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        return Convert.ToInt64(comando.ExecuteScalar());
    }

    private static void Dañar(string ruta, long desde, int cuanto)
    {
        using var archivo = File.OpenWrite(ruta);
        archivo.Seek(desde, SeekOrigin.Begin);
        archivo.Write(new byte[cuanto]);
    }
}
