using System.Text.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// La prueba con una persona real (docs/plan.md): la caja de prueba arranca este servicio con <c>Prueba:Activa</c>. La
/// base empieza como copia de la base de prueba (<c>Prueba:BaseInicial</c>, la que arma <c>preparar-prueba</c>) y se
/// puede volver a ella; las respuestas de la guía se guardan junto a la base y se exportan al escritorio.
/// </summary>
/// <param name="carpetaRespuestas">Dónde se guardan las respuestas exportadas; vacío, el escritorio de quien prueba.</param>
public sealed class ModoPrueba(bool activo, string? baseInicial, string cadenaConexion, string? carpetaRespuestas = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _candado = new();

    public bool Activo => activo;

    private string RutaBase => Path.GetFullPath(new SqliteConnectionStringBuilder(cadenaConexion).DataSource);

    private string RutaRespuestas => Path.Combine(Path.GetDirectoryName(RutaBase)!, "respuestas-prueba.json");

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
        origen.BackupDatabase(destino);
    }

    public RespuestasPruebaDto Respuestas()
    {
        lock (_candado)
        {
            return File.Exists(RutaRespuestas)
                ? JsonSerializer.Deserialize<RespuestasPruebaDto>(File.ReadAllText(RutaRespuestas), Json) ?? Vacias()
                : Vacias();
        }
    }

    public void Guardar(RespuestasPruebaDto respuestas)
    {
        lock (_candado)
            File.WriteAllText(RutaRespuestas, JsonSerializer.Serialize(respuestas, Json));
    }

    /// <summary>Las respuestas como documento en el escritorio de quien prueba (o junto a la base, si no hay escritorio).</summary>
    public string Exportar(string texto)
    {
        var escritorio = carpetaRespuestas ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var carpeta = escritorio.Length > 0 && Directory.Exists(escritorio) ? escritorio : Path.GetDirectoryName(RutaBase)!;
        var ruta = Path.Combine(carpeta, $"Respuestas de la prueba {DateTime.Now:yyyy-MM-dd HH.mm}.md");
        File.WriteAllText(ruta, texto);
        return ruta;
    }

    private static RespuestasPruebaDto Vacias() => new(null, new Dictionary<string, RespuestaTareaDto>(), null);
}
