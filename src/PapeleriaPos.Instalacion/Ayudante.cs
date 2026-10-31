using System.Globalization;
using System.Net;

namespace PapeleriaPos.Instalacion;

/// <summary>
/// PapeleriaPos.Instalacion.exe, el ayudante del instalador (docs/instalacion.md). Lo corre el asistente mientras pregunta, y
/// instalar-pc.ps1 al instalar; cada orden deja lo que encontró en <c>--salida</c> («clave=valor», <see cref="ArchivoDeClaves"/>):
/// <code>
/// leer     --salida archivo [--datos carpeta] [--programa carpeta]   lo que hay en esta PC (LecturaDeLaPc)
/// buscar   --salida archivo [--segundos 2]                           las cajas principales que contestan en la red
/// probar   --direccion IP --salida archivo                           si esa caja principal contesta (api/estado)
/// escribir --respuestas archivo --salida archivo [--datos carpeta]   servicio.json y caja.json con las respuestas (solo lo que cambió)
/// </code>
/// Termina con 0 si hizo lo suyo (lo que encontró va en la salida), con 2 si algo no se entiende (un archivo de la PC mal escrito o una
/// respuesta: «error=» dice qué, y no se cambió nada) y con 1 si la orden está mal dada. Nunca recibe ni escribe una clave.
/// </summary>
public static class Ayudante
{
    public static Task<int> Main(string[] argumentos) => CorrerAsync(argumentos, null);

    /// <param name="equipo">El de las pruebas; sin él, esta PC (en Windows) o una sin nada (para probarlo en otro sistema).</param>
    public static async Task<int> CorrerAsync(string[] argumentos, IEquipo? equipo, HttpMessageHandler? red = null)
    {
        if (argumentos.Length == 0 || Opciones(argumentos[1..]) is not { } opciones || !opciones.TryGetValue("salida", out var salida))
        {
            await Console.Error.WriteLineAsync("Uso: PapeleriaPos.Instalacion.exe leer|buscar|probar|escribir --salida <archivo> [...] (docs/instalacion.md).");
            return 1;
        }
        equipo ??= Equipo(opciones);
        try
        {
            switch (argumentos[0])
            {
                case "leer":
                    var leido = LecturaDeLaPc.Leer(equipo);
                    leido.Guardar(salida);
                    return leido["error"] is { Length: > 0 } ? 2 : 0;
                case "buscar":
                    var segundos = opciones.TryGetValue("segundos", out var texto) && double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                        ? s
                        : 2;
                    (await BuscarAsync(TimeSpan.FromSeconds(Math.Clamp(segundos, 0.5, 10)), red)).Guardar(salida);
                    return 0;
                case "probar":
                    (await ProbarAsync(opciones.GetValueOrDefault("direccion"), red)).Guardar(salida);
                    return 0;
                case "escribir" when opciones.TryGetValue("respuestas", out var respuestas):
                    var escrito = Escribir(ArchivoDeClaves.Leer(respuestas), equipo.Datos);
                    escrito.Guardar(salida);
                    return escrito["error"] is { Length: > 0 } ? 2 : 0;
                default:
                    await Console.Error.WriteLineAsync($"No conozco la orden «{argumentos[0]}» (o le falta algo).");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            new ArchivoDeClaves().Poner("error", ex.Message).Guardar(salida);
            return 2;
        }
    }

    /// <summary>«--clave valor» de la línea de órdenes; vacío si alguna no trae su valor.</summary>
    private static Dictionary<string, string>? Opciones(string[] argumentos)
    {
        var opciones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < argumentos.Length; i += 2)
        {
            if (!argumentos[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= argumentos.Length)
                return null;
            opciones[argumentos[i][2..]] = argumentos[i + 1];
        }
        return opciones;
    }

    private static IEquipo Equipo(Dictionary<string, string> opciones)
    {
        if (OperatingSystem.IsWindows())
        {
            var windows = new EquipoWindows();
            return new EquipoWindows
            {
                Datos = opciones.GetValueOrDefault("datos") ?? windows.Datos,
                Programa = opciones.GetValueOrDefault("programa") ?? windows.Programa,
            };
        }
        return new EquipoSinWindows(
            opciones.GetValueOrDefault("datos") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PapeleriaPos"),
            opciones.GetValueOrDefault("programa") ?? Path.Combine(Path.GetTempPath(), "PapeleriaPos-sin-programa"));
    }

    /// <summary>Las cajas principales que contestan: por UDP y después su api/estado (solo cuentan las que dicen ser Papelería POS).</summary>
    public static async Task<ArchivoDeClaves> BuscarAsync(TimeSpan espera, HttpMessageHandler? red = null, IReadOnlyList<IPEndPoint>? destinos = null)
    {
        var encontradas = await BusquedaDeLaPrincipal.BuscarAsync(espera, destinos);
        var estados = await Task.WhenAll(encontradas.Select(uri => BusquedaDeLaPrincipal.ProbarAsync(uri, red)));
        var contestan = estados.Where(e => e.Contesta).ToList();
        var salida = new ArchivoDeClaves().Poner("encontradas", contestan.Count.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < contestan.Count; i++)
            salida.Poner($"encontrada.{i + 1}", BusquedaDeLaPrincipal.Corta(contestan[i].Direccion))
                .Poner($"encontrada.{i + 1}.texto", contestan[i].Texto);
        return salida.Poner("sin-contestar", (estados.Length - contestan.Count).ToString(CultureInfo.InvariantCulture));
    }

    public static async Task<ArchivoDeClaves> ProbarAsync(string? direccion, HttpMessageHandler? red = null)
    {
        var salida = new ArchivoDeClaves();
        if (BusquedaDeLaPrincipal.Direccion(direccion) is not { } uri)
            return salida.Poner("contesta", false).Poner("error", $"«{direccion}» no es una dirección: va la IP de la caja principal, como 192.168.1.10.");
        var estado = await BusquedaDeLaPrincipal.ProbarAsync(uri, red);
        return salida.Poner("contesta", estado.Contesta)
            .Poner("direccion", BusquedaDeLaPrincipal.Corta(uri))
            .Poner("version", estado.Version)
            .Poner("sucursales", string.Join(", ", estado.Sucursales))
            .Poner("texto", estado.Texto)
            .Poner("error", estado.Error);
    }

    /// <summary>servicio.json (si esta PC tiene el servicio) y caja.json con las respuestas: solo lo que cambió, y dice qué.</summary>
    public static ArchivoDeClaves Escribir(ArchivoDeClaves archivo, string datos)
    {
        var salida = new ArchivoDeClaves();
        Respuestas respuestas;
        try
        {
            respuestas = Respuestas.De(archivo);
        }
        catch (FormatException ex)
        {
            return salida.Poner("error", $"Una respuesta no se entiende: {ex.Message}");
        }
        // Primero los dos, y solo si los dos se entienden se escriben: no queda uno cambiado y el otro no.
        Pendiente? servicio, caja;
        try
        {
            servicio = respuestas.ConServicio ? ServicioJson.Preparar(Path.Combine(datos, ServicioJson.Archivo), respuestas) : null;
            caja = CajaJson.Preparar(datos, respuestas);
        }
        catch (FormatException ex)
        {
            return salida.Poner("error", $"{ex.Message} Corrígelo o quítalo y vuelve a correr el instalador; no se cambió nada.");
        }
        var cambios = new List<string>();
        if (servicio?.Guardar() is { } enServicio)
        {
            salida.Poner("servicio-json", enServicio.Cambio ? "cambio" : "igual").Poner("servicio-json-completo", enServicio.Completo);
            cambios.AddRange(enServicio.Cambios);
        }
        else
            salida.Poner("servicio-json", "no-aplica");
        var enCaja = caja.Guardar();
        salida.Poner("caja-json", enCaja.Cambio ? "cambio" : "igual");
        cambios.AddRange(enCaja.Cambios);
        return salida.Poner("cambios", string.Join("; ", cambios));
    }
}

/// <summary>Una PC sin Windows (las pruebas, o correrlo en Linux): sin servicio registrado, impresoras ni puertos.</summary>
public sealed class EquipoSinWindows(string datos, string programa) : IEquipo
{
    public string Datos => datos;

    public string Programa => programa;

    public string? ExeDelServicio() => null;

    public string? Version(string exe) => null;

    public IReadOnlyList<ImpresoraDeWindows> Impresoras() => [];

    public IReadOnlyList<string> PuertosCom() => [];

    public IReadOnlyList<string> Ips() => [];

    public IReadOnlyList<string> Gbak() => [];

    public string? IniDeEleventa() => null;
}
