using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// El registro de la caja: lo que falló, con fecha y hora, en archivos de texto chicos, uno por día
/// (<c>caja-2026-10-17.log</c>). Hasta la segunda prueba con una persona real (2026-10-17) la caja no tenía registro: si
/// «se cerró sola» o «dio un error», no quedaba nada para saber por qué. También guarda lo que escribe el servicio de la
/// prueba (<c>servicio-….log</c>). Nunca truena: lo llaman los avisos de los errores, y un registro que no se puede
/// escribir (disco lleno, sin permiso) no puede tumbar la venta ni causar otro error.
/// </summary>
public sealed class RegistroDiario
{
    /// <summary>Dos fines de semana de prueba: el dueño revisa los del sábado y el domingo después.</summary>
    public const int DiasPorOmision = 14;

    /// <summary>Lo normal es una línea por cada vez que se abre la caja; un error que se repite sin parar no llena el disco.</summary>
    public const long TopePorOmision = 1024 * 1024;

    /// <summary>
    /// Pasado el tope, todavía se anota una línea de cada cosa hasta esta reserva: que se abrió o se cerró la caja, que se
    /// cierra por un error, y de cada error su tipo y su mensaje. Así un error que se repite no se lleva el final del día,
    /// con lo que dice si la caja se cerró sola (revisión del 2026-10-14).
    /// </summary>
    public const long ReservaPorOmision = 64 * 1024;

    /// <summary>Lo más largo de una línea pasado el tope.</summary>
    private const int LargoDeUnaLinea = 300;

    /// <summary>Cuántos textos distintos recuerda <see cref="AnotarSinRepetir"/>; los que siguen se anotan completos.</summary>
    private const int TextosQueRecuerda = 200;

    private readonly int _dias;
    private readonly long _tope;
    private readonly long _reserva;
    private readonly TimeProvider _reloj;
    private readonly Lock _candado = new();

    /// <summary>
    /// WPF y las tareas avisan dos veces del mismo error (la ventana de Blazor lo vuelve a lanzar en el hilo de WPF y la
    /// tarea que falló llega después, cuando el recolector la recoge): se anota una vez.
    /// </summary>
    private readonly ConditionalWeakTable<Exception, object> _anotados = new();

    /// <summary>Cuántas veces llegó cada texto a <see cref="AnotarSinRepetir"/>.</summary>
    private readonly Dictionary<string, int> _vistos = new(StringComparer.Ordinal);

    private DateOnly? _llenoEl;

    /// <summary>El día en que ya se revisó si el archivo, pasado el tope, ya decía que se llenó.</summary>
    private DateOnly? _revisadoEl;

    private const string SeLleno = "El registro de hoy se llenó";

    /// <param name="carpeta">Ver <see cref="CarpetaDeLaCaja"/>.</param>
    /// <param name="nombre">Con qué empieza cada archivo: «caja» o «servicio».</param>
    /// <param name="dias">Cuántos días se guardan, contando hoy; <see cref="Limpiar"/> quita los de antes.</param>
    /// <param name="tope">Hasta cuánto crece el archivo de un día con todo el detalle.</param>
    /// <param name="reserva">Lo que crece después del tope, una línea de cada cosa; lo que sigue ya no se anota.</param>
    public RegistroDiario(string carpeta, string nombre, int dias = DiasPorOmision, long tope = TopePorOmision, TimeProvider? reloj = null,
        long reserva = ReservaPorOmision)
    {
        Carpeta = carpeta;
        Nombre = nombre;
        _dias = dias;
        _tope = tope;
        _reserva = reserva;
        _reloj = reloj ?? TimeProvider.System;
    }

    public string Carpeta { get; }

    public string Nombre { get; }

    /// <summary>
    /// En la prueba, en «registros» junto a sus datos y sus respuestas (<c>C:\ProgramData\PapeleriaPos-Prueba</c>): lo que
    /// se manda al terminar va en una sola carpeta. Si no, en la carpeta del usuario de Windows
    /// (<c>%LocalAppData%\PapeleriaPos\registros</c>).
    /// </summary>
    public static string CarpetaDeLaCaja(string? carpetaDeLaPrueba) =>
        carpetaDeLaPrueba is { Length: > 0 } prueba
            ? Path.Combine(prueba, "registros")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PapeleriaPos", "registros");

    /// <summary>El archivo en que se anota hoy (hora de la PC).</summary>
    public string ArchivoDeHoy => Archivo(Hoy);

    private DateTimeOffset Ahora => _reloj.GetLocalNow();

    private DateOnly Hoy => DateOnly.FromDateTime(Ahora.DateTime);

    private string Archivo(DateOnly dia) => Path.Combine(Carpeta, $"{Nombre}-{dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");

    private string Marca() => Ahora.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Una línea con la fecha y la hora.</summary>
    public void Anotar(string texto) => Escribir(Marca(), texto, texto);

    /// <summary>
    /// Lo que puede llegar una y otra vez, como un error de JavaScript en una tecla (cada vez que se aprieta): se anota
    /// completo la primera vez y después solo una línea, a las 2, 10, 100… veces, para que no llene el registro.
    /// </summary>
    /// <param name="titulo">Qué es, para el que lo revisa: «Error en la página».</param>
    public void AnotarSinRepetir(string titulo, string texto)
    {
        int veces;
        lock (_candado)
        {
            veces = _vistos.TryGetValue(texto, out var antes) ? antes + 1 : 1;
            if (veces > 1 || _vistos.Count < TextosQueRecuerda)
                _vistos[texto] = veces;
        }
        if (veces == 1)
            Escribir(Marca(), $"{titulo}:{Environment.NewLine}{texto}{Environment.NewLine}", $"{titulo}: {texto}");
        else if (veces == 2 || EsPotenciaDeDiez(veces))
            Anotar($"{titulo}, el mismo otra vez (ya van {veces}): {PrimeraLinea(texto)}");
    }

    private static bool EsPotenciaDeDiez(int numero)
    {
        var potencia = 10;
        while (potencia < numero && potencia <= int.MaxValue / 10)
            potencia *= 10;
        return potencia == numero;
    }

    private static string PrimeraLinea(string texto)
    {
        var linea = texto.AsSpan().TrimStart();
        if (linea.IndexOfAny('\r', '\n') is >= 0 and var fin)
            linea = linea[..fin];
        return linea.Length > LargoDeUnaLinea ? $"{linea[..LargoDeUnaLinea]}…" : linea.ToString();
    }

    /// <summary>
    /// El error completo (tipo, mensaje, los errores de adentro y dónde pasó), con la fecha y la hora.
    /// </summary>
    /// <param name="donde">Qué lo atrapó, para el que lo revisa: «Algo falló en la pantalla», «Error en la ventana»…</param>
    /// <returns>Si lo anotó: uno que ya estaba anotado no se repite.</returns>
    public bool AnotarError(string donde, Exception error)
    {
        IReadOnlyList<Exception> dentro = error is AggregateException agregado ? agregado.Flatten().InnerExceptions : [error];
        if (dentro.Count > 0 && dentro.All(e => _anotados.TryGetValue(e, out _)))
            return false;
        foreach (var e in dentro.Append(error))
            _anotados.AddOrUpdate(e, this);
        string texto;
        try
        {
            texto = error.ToString();
        }
        catch (Exception ex)
        {
            // Uno que ni se deja leer: al menos su tipo, y sin causar otro error dentro del aviso.
            texto = $"{error.GetType().FullName} (no se pudo leer: {ex.GetType().Name})";
        }
        string resumen;
        try
        {
            resumen = $"{donde}: {error.GetType().FullName}: {error.Message}";
        }
        catch (Exception ex)
        {
            resumen = $"{donde}: {error.GetType().FullName} (no se pudo leer: {ex.GetType().Name})";
        }
        Escribir(Marca(), $"{donde}:{Environment.NewLine}{texto}{Environment.NewLine}", resumen);
        return true;
    }

    /// <summary>Quita los archivos de este registro de hace más de los días que se guardan. Los demás archivos no los toca.</summary>
    public void Limpiar()
    {
        try
        {
            if (!Directory.Exists(Carpeta))
                return;
            var limite = Hoy.AddDays(-_dias);
            foreach (var archivo in Directory.EnumerateFiles(Carpeta, $"{Nombre}-*.log").ToList())
            {
                var fecha = Path.GetFileNameWithoutExtension(archivo)[(Nombre.Length + 1)..];
                if (!DateOnly.TryParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia) || dia > limite)
                    continue;
                try
                {
                    File.Delete(archivo);
                }
                catch (Exception ex) when (EsDelDisco(ex))
                {
                    // Abierto en otro lado: se quita la siguiente vez que se abra la caja.
                }
            }
        }
        catch (Exception ex) when (EsDelDisco(ex))
        {
            // Sin poder leer la carpeta, se quitan la siguiente vez.
        }
    }

    /// <param name="completo">Con todo el detalle, mientras quepa en el tope.</param>
    /// <param name="corto">Pasado el tope, en una línea (la primera, recortada), mientras quepa en la reserva.</param>
    private void Escribir(string marca, string completo, string corto)
    {
        lock (_candado)
        {
            try
            {
                var hoy = Hoy;
                if (_llenoEl == hoy)
                    return;
                Directory.CreateDirectory(Carpeta);
                var ruta = Archivo(hoy);
                var archivo = new FileInfo(ruta);
                var ocupado = archivo.Exists ? archivo.Length : 0;
                var bytes = Encoding.UTF8.GetBytes($"{marca}  {completo}{Environment.NewLine}");
                if (ocupado + bytes.Length > _tope)
                {
                    // La caja se volvió a abrir el mismo día con el archivo ya lleno: ya se dijo, y ya no se anota nada.
                    if (_revisadoEl != hoy)
                    {
                        _revisadoEl = hoy;
                        if (ocupado > _tope && YaDiceQueSeLleno(ruta))
                        {
                            _llenoEl = hoy;
                            return;
                        }
                    }
                    var linea = PrimeraLinea(corto);
                    var detalle = linea.Length < completo.Trim().Length ? " (sin el detalle: el registro de hoy llegó a su tope)" : "";
                    bytes = Encoding.UTF8.GetBytes($"{marca}  {linea}{detalle}{Environment.NewLine}");
                    if (ocupado + bytes.Length > _tope + _reserva)
                    {
                        _llenoEl = hoy;
                        bytes = Encoding.UTF8.GetBytes(
                            $"{marca}  {SeLleno} ({(_tope + _reserva) / 1024} KB): lo que sigue ya no se anota.{Environment.NewLine}");
                    }
                }
                // Se abre y se cierra en cada entrada: se puede leer (o copiar) mientras la caja sigue abierta.
                using var flujo = new FileStream(ruta, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                flujo.Write(bytes);
            }
            catch (Exception ex) when (EsDelDisco(ex))
            {
                // Sin registro la caja sigue igual: no hay a quién avisar sin interrumpir la venta.
            }
        }
    }

    /// <summary>Lo último del archivo: si ahí dice que se llenó.</summary>
    private static bool YaDiceQueSeLleno(string ruta)
    {
        using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var final = new byte[(int)Math.Min(flujo.Length, 512)];
        flujo.Seek(-final.Length, SeekOrigin.End);
        flujo.ReadExactly(final);
        return Encoding.UTF8.GetString(final).Contains(SeLleno, StringComparison.Ordinal);
    }

    private static bool EsDelDisco(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException;
}

/// <summary>
/// Si la caja puede seguir después de un error que nadie atrapó (en la ventana de Windows). Sigue, con el aviso y «Volver a
/// cargar», salvo que no tenga remedio: sin memoria, o más de <see cref="Tope"/> errores en un minuto, que ya no es un
/// error suelto sino uno que se repite sin parar. Entonces la caja se cierra en orden (y el servicio de la prueba se
/// detiene bien), con el registro.
/// </summary>
public sealed class ErroresSeguidos(TimeProvider? reloj = null)
{
    public const int Tope = 20;

    private static readonly TimeSpan Ventana = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;
    private readonly Queue<DateTimeOffset> _recientes = new();

    public bool SePuedeSeguir(Exception error)
    {
        if (error is OutOfMemoryException or AccessViolationException or System.Runtime.InteropServices.SEHException)
            return false;
        var ahora = _reloj.GetUtcNow();
        while (_recientes.TryPeek(out var antes) && ahora - antes >= Ventana)
            _recientes.Dequeue();
        _recientes.Enqueue(ahora);
        return _recientes.Count <= Tope;
    }
}
