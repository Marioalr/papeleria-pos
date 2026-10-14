using System.Text.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// La prueba con una persona real (docs/plan.md): la caja de prueba arranca este servicio con <c>Prueba:Activa</c>. La
/// base empieza como copia de la base de prueba (<c>Prueba:BaseInicial</c>, la que arma <c>preparar-prueba</c>) y se
/// puede volver a ella; las respuestas de la guía se guardan junto a la base y se exportan al escritorio.
/// </summary>
/// <remarks>
/// Las respuestas de cada versión de la guía van en su propio archivo: las de otra versión no se cargan (el mismo Id puede
/// ser otra tarea, y la guía nueva saldría ya contestada) y su archivo se queda como estaba. Las de la primera prueba
/// (2026-10-10, versión 1) se guardaron sin versión en respuestas-prueba.json, y ahí siguen.
/// </remarks>
/// <param name="carpetaRespuestas">Dónde se guardan las respuestas exportadas; vacío, el escritorio de quien prueba.</param>
public sealed class ModoPrueba(bool activo, string? baseInicial, string cadenaConexion, string? carpetaRespuestas = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _candado = new();

    public bool Activo => activo;

    private string RutaBase => Path.GetFullPath(new SqliteConnectionStringBuilder(cadenaConexion).DataSource);

    private string RutaRespuestas(int version) =>
        Path.Combine(Path.GetDirectoryName(RutaBase)!, version <= 1 ? "respuestas-prueba.json" : $"respuestas-prueba-guia-{version}.json");

    public static ModoPrueba Crear(IConfiguration configuracion, string cadenaConexion) =>
        new(configuracion.GetValue("Prueba:Activa", false), configuracion["Prueba:BaseInicial"] is { Length: > 0 } b ? b : null, cadenaConexion,
            configuracion["Prueba:CarpetaRespuestas"] is { Length: > 0 } c ? c : null);

    /// <summary>La primera vez, la base de la prueba es una copia de la inicial.</summary>
    public void PrepararBase()
    {
        if (!activo || baseInicial is null || File.Exists(RutaBase))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(RutaBase)!);
        File.Copy(baseInicial, RutaBase);
    }

    /// <summary>Vuelve a la base inicial sin detener el servicio: SQLite la copia página por página sobre la que se usa.</summary>
    public void EmpezarDeCero()
    {
        if (baseInicial is null || !File.Exists(baseInicial))
            throw new InvalidOperationException("No está la base inicial de la prueba: reinstala la prueba.");
        using var origen = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = baseInicial, Mode = SqliteOpenMode.ReadOnly }.ToString());
        using var destino = new SqliteConnection(cadenaConexion);
        origen.Open();
        destino.Open();
        BaseDeDatos.Ajustar(destino);
        origen.BackupDatabase(destino);
    }

    /// <summary>
    /// Las de esa versión de la guía; sin ellas (o si el archivo es de otra), en blanco. Un archivo dañado se aparta con la
    /// hora, no se borra: si se quedara, la guía no cargaría y lo primero que se marcara lo taparía con unas cuantas
    /// respuestas.
    /// </summary>
    public RespuestasPruebaDto Respuestas(int version)
    {
        lock (_candado)
        {
            var ruta = RutaRespuestas(version);
            if (!File.Exists(ruta))
                return Vacias(version);
            try
            {
                var guardadas = JsonSerializer.Deserialize<RespuestasPruebaDto>(File.ReadAllText(ruta), Json);
                return guardadas is not null && guardadas.Version == version ? guardadas : Vacias(version);
            }
            catch (JsonException)
            {
                File.Move(ruta, Path.Combine(Path.GetDirectoryName(ruta)!,
                    $"{Path.GetFileNameWithoutExtension(ruta)} (dañadas {DateTime.Now:yyyy-MM-dd HH.mm.ss}).json"));
                return Vacias(version);
            }
        }
    }

    /// <summary>
    /// Se escriben aparte, ya en el disco, y luego se renombran (como los respaldos): si se va la luz a media escritura, se
    /// quedan las de antes y no un archivo a medias.
    /// </summary>
    public void Guardar(RespuestasPruebaDto respuestas)
    {
        lock (_candado)
        {
            var ruta = RutaRespuestas(respuestas.Version);
            var temporal = ruta + ".nuevo";
            using (var archivo = new FileStream(temporal, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(archivo, respuestas, Json);
                archivo.Flush(flushToDisk: true);
            }
            File.Move(temporal, ruta, overwrite: true);
        }
    }

    /// <summary>
    /// Las respuestas como documento en el escritorio de quien prueba (o junto a la base, si no hay escritorio). Nunca encima
    /// de otro: dos en el mismo minuto (guardar y luego dejar la guía en blanco, o una guía ya en blanco) llevan un número.
    /// </summary>
    public string Exportar(string texto)
    {
        var escritorio = carpetaRespuestas ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var carpeta = escritorio.Length > 0 && Directory.Exists(escritorio) ? escritorio : Path.GetDirectoryName(RutaBase)!;
        var nombre = $"Respuestas de la prueba {DateTime.Now:yyyy-MM-dd HH.mm}";
        var ruta = Path.Combine(carpeta, $"{nombre}.md");
        for (var n = 2; File.Exists(ruta); n++)
            ruta = Path.Combine(carpeta, $"{nombre} ({n}).md");
        File.WriteAllText(ruta, texto);
        return ruta;
    }

    /// <summary>
    /// Para que pruebe otra persona (el sábado una y el domingo otra): el documento queda en el escritorio, como al
    /// exportar, y el archivo de las respuestas de esa versión se aparta con la hora en el nombre (no se borra), así que
    /// la guía vuelve a abrir en blanco. Devuelve dónde quedó el documento.
    /// </summary>
    public string DejarEnBlanco(int version, string texto)
    {
        lock (_candado)
        {
            var documento = Exportar(texto);
            var ruta = RutaRespuestas(version);
            var apartadas = $"{Path.GetFileNameWithoutExtension(ruta)} (guardadas {DateTime.Now:yyyy-MM-dd HH.mm.ss}).json";
            if (File.Exists(ruta))
                File.Move(ruta, Path.Combine(Path.GetDirectoryName(ruta)!, apartadas));
            return documento;
        }
    }

    /// <summary>
    /// La caja de la prueba arranca este servicio (con <c>Prueba:DetenerAlCerrarEntrada</c>) y le conecta la entrada. Al
    /// cerrarse la caja, aunque sea a la fuerza, la entrada se cierra y el servicio se detiene bien: quita la marca de la
    /// base contra apagones y, al volver a abrir la caja, no parece que se fue la luz. Devuelve el hilo que espera.
    /// </summary>
    public static Thread DetenerAlCerrarEntrada(Stream entrada, Action detener)
    {
        var hilo = new Thread(() =>
        {
            try
            {
                var bufer = new byte[256];
                while (entrada.Read(bufer) > 0)
                {
                }
            }
            catch (IOException)
            {
                // La caja se cerró de golpe: es lo mismo que cerrar la entrada.
            }
            finally
            {
                entrada.Dispose();
            }
            detener();
        }) { IsBackground = true, Name = "Detener al cerrar la caja de la prueba" };
        hilo.Start();
        return hilo;
    }

    private static RespuestasPruebaDto Vacias(int version) => new(null, new Dictionary<string, RespuestaTareaDto>(), null, version);
}
