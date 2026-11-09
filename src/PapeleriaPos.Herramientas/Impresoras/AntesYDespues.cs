using System.Globalization;
using System.Text;

namespace PapeleriaPos.Herramientas.Impresoras;

/// <summary>Un trabajo que se vio por IPP mientras se hacía una acción (nuevo o con otro estado).</summary>
public sealed record TrabajoVisto(DateTimeOffset Hora, string Host, string Id, IReadOnlyList<Ipp.Atributo> Atributos)
{
    public string Texto => string.Join(", ", Atributos.Where(a => a.Nombre != "job-id").Select(a => $"{a.Nombre}={a.Texto}"));
}

/// <summary>Una acción de la prueba: lo que cambió en cada impresora y lo que se vio por IPP mientras se hacía.</summary>
public sealed record AccionMedida(int Numero, string Etiqueta, string Carpeta, DateTimeOffset Inicio, DateTimeOffset Fin,
    IReadOnlyList<ComparacionImpresora> Comparaciones, IReadOnlyList<TrabajoVisto> Vigilados, string? Nota);

public sealed record OpcionesAntesYDespues
{
    /// <summary>Preguntar Get-Jobs cada <see cref="PausaVigilancia"/> mientras se hace la acción (solo a las que contestan IPP).</summary>
    public bool VigilarIpp { get; init; }
    public TimeSpan PausaVigilancia { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Cuánto esperar después del segundo Enter, antes de la foto de después: la impresora sube sus contadores al terminar.</summary>
    public TimeSpan Espera { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Lo más que puede tardar una foto de una impresora: si se pasa, esa foto se da por perdida y las demás siguen.</summary>
    public TimeSpan LimiteFoto { get; init; } = TimeSpan.FromMinutes(4);

    /// <summary>Los nombres de los árboles SNMP para los archivos.</summary>
    public IReadOnlyList<(string Raiz, string Nombre)> Arboles { get; init; } = new OpcionesFoto().Arboles;
}

/// <summary>
/// El modo «antes y después» de diagnosticar-impresoras (docs/pruebas-en-tienda.md, «Antes y después: celular, copias y escaneos»): por
/// cada acción que escribe quien prueba, una foto de cada impresora antes y otra después, y lo que cambió. Guarda todo en
/// <c>carpeta</c> (las fotos tal cual, los cambios y <c>resumen.md</c>, que se reescribe tras cada acción: Ctrl+C no pierde nada).
/// <b>Solo lee</b> (ver <see cref="LectorImpresora"/>).
/// </summary>
public sealed class AntesYDespues(IReadOnlyList<LectorImpresora> lectores, string carpeta, TextWriter salida, TimeProvider reloj,
    OpcionesAntesYDespues? opciones = null)
{
    private readonly OpcionesAntesYDespues _opciones = opciones ?? new();
    private readonly Dictionary<string, FotoImpresora> _primeras = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AccionMedida> _acciones = [];
    private DateTimeOffset _inicio;
    private string? _interrumpida;

    public IReadOnlyList<AccionMedida> Acciones => _acciones;

    public string Resumen => Path.Combine(carpeta, "resumen.md");

    /// <summary>
    /// La sesión: la foto inicial y luego una acción tras otra hasta «salir», una línea vacía al final de la entrada o
    /// <paramref name="ct"/> (Ctrl+C). <paramref name="leer"/> da el siguiente renglón que escribe quien prueba (vacío: se acabó).
    /// </summary>
    public async Task<int> EjecutarAsync(Func<CancellationToken, Task<string?>> leer, CancellationToken ct)
    {
        Directory.CreateDirectory(carpeta);
        _inicio = reloj.GetLocalNow();
        salida.WriteLine($"Antes y después en {lectores.Count} impresora(s). Solo se lee: nada cambia en las impresoras ni se manda a imprimir.");
        salida.WriteLine($"Todo queda en {carpeta}.");
        try
        {
            salida.WriteLine("Foto inicial de cada impresora (para ver qué contesta)…");
            var iniciales = await FotosAsync(Path.Combine(carpeta, "00-inicio"), "inicio", ct);
            foreach (var (lector, foto) in lectores.Zip(iniciales))
            {
                if (foto.Foto is { } f)
                    _primeras[lector.Host] = f;
                salida.WriteLine($"  {lector.Host}: {(foto.Foto is { } ff ? QueContesto(ff) : $"sin foto ({foto.Error})")}");
            }
            await EscribirResumenAsync();

            while (!ct.IsCancellationRequested)
            {
                salida.WriteLine();
                salida.WriteLine("Escribe qué vas a hacer (p. ej. \"copia color panel\") y Enter; «salir» para terminar:");
                var etiqueta = await leer(ct);
                if (etiqueta is null || etiqueta.Trim().Equals("salir", StringComparison.OrdinalIgnoreCase))
                    break;
                etiqueta = etiqueta.Trim();
                if (etiqueta.Length == 0)
                    continue;
                await AccionAsync(etiqueta, leer, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            salida.WriteLine();
            salida.WriteLine("Detenido: lo medido hasta aquí se guarda.");
        }
        await EscribirResumenAsync();
        salida.WriteLine($"Quedó en {Resumen}. Son datos de la tienda: no van al repositorio.");
        return 0;
    }

    private async Task AccionAsync(string etiqueta, Func<CancellationToken, Task<string?>> leer, CancellationToken ct)
    {
        var numero = _acciones.Count + 1;
        var nombre = $"{numero:00}-{Carpeta(etiqueta)}";
        var dir = Path.Combine(carpeta, nombre);
        _interrumpida = $"{numero}. «{etiqueta}»";
        salida.WriteLine("Foto de antes…");
        var inicio = reloj.GetLocalNow();
        var antes = await FotosAsync(dir, "antes", ct);
        salida.WriteLine(_opciones.VigilarIpp && lectores.Any(l => l.UrlIpp is not null)
            ? $"Listo: haz «{etiqueta}» ahora (vigilando IPP cada {_opciones.PausaVigilancia.TotalSeconds:0.#} s). Enter otra vez cuando termine."
            : $"Listo: haz «{etiqueta}» ahora. Enter otra vez cuando termine.");

        var vistos = new List<TrabajoVisto>();
        using var dejarDeVigilar = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var vigilancia = _opciones.VigilarIpp
            ? Task.WhenAll(lectores.Zip(antes).Where(x => x.First.UrlIpp is not null)
                .Select(x => VigilarAsync(x.First, x.Second.Foto, vistos, dejarDeVigilar.Token)))
            : Task.CompletedTask;
        await leer(ct);
        await dejarDeVigilar.CancelAsync();
        await vigilancia;
        var fin = reloj.GetLocalNow();

        if (_opciones.Espera > TimeSpan.Zero)
        {
            salida.WriteLine($"Esperando {_opciones.Espera.TotalSeconds:0} s a que las impresoras suban sus contadores…");
            await Task.Delay(_opciones.Espera, reloj, ct);
        }
        salida.WriteLine("Foto de después…");
        var despues = await FotosAsync(dir, "despues", ct);

        var comparaciones = new List<ComparacionImpresora>();
        foreach (var ((lector, a), d) in lectores.Zip(antes).Zip(despues))
        {
            ComparacionImpresora c;
            if (a.Foto is { } fa && d.Foto is { } fd)
            {
                c = CambiosEntreFotos.Comparar(fa, fd);
                _primeras.TryAdd(lector.Host, fa);
            }
            else
            {
                c = new ComparacionImpresora(lector.Host, inicio, fin, [],
                    [a.Foto is null ? $"sin la foto de antes ({a.Error})" : $"sin la foto de después ({d.Error})"]);
            }
            comparaciones.Add(c);
            await File.WriteAllTextAsync(Path.Combine(dir, $"{Archivo(lector.Host)}-cambios.txt"), CambiosEntreFotos.Completo(c), CancellationToken.None);
            salida.WriteLine($"  {lector.Host}: {Corto(c)}");
        }
        if (vistos.Count > 0)
            await File.WriteAllLinesAsync(Path.Combine(dir, "vigilancia-ipp.txt"),
                vistos.Select(v => $"{v.Hora:HH:mm:ss}\t{v.Host}\ttrabajo {v.Id}\t{v.Texto}"), CancellationToken.None);
        _acciones.Add(new AccionMedida(numero, etiqueta, nombre, inicio, fin, comparaciones, vistos,
            _opciones.VigilarIpp && !lectores.Any(l => l.UrlIpp is not null) ? "No se vigiló IPP: ninguna contestó IPP en la foto de antes." : null));
        _interrumpida = null;
        await EscribirResumenAsync();
    }

    private sealed record FotoTomada(FotoImpresora? Foto, string? Error);

    /// <summary>Una foto de cada impresora, todas a la vez; la que tarda de más se pierde sola. Se guardan en <paramref name="dir"/>.</summary>
    private async Task<IReadOnlyList<FotoTomada>> FotosAsync(string dir, string cual, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        return await Task.WhenAll(lectores.Select(async lector =>
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(_opciones.LimiteFoto);
            FotoTomada tomada;
            try
            {
                tomada = new FotoTomada(await lector.FotoAsync(limite.Token), null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tomada = new FotoTomada(null, $"tardó más de {_opciones.LimiteFoto.TotalMinutes:0} min");
            }
            if (tomada.Foto is { } foto)
                await ArchivosDeLaFoto.GuardarAsync(foto, Path.Combine(dir, $"{Archivo(lector.Host)}-{cual}"), _opciones.Arboles);
            return tomada;
        }));
    }

    /// <summary>Get-Jobs cada segundo mientras se hace la acción: cada trabajo nuevo, o que cambió, se anota con la hora.</summary>
    private async Task VigilarAsync(LectorImpresora lector, FotoImpresora? antes, List<TrabajoVisto> vistos, CancellationToken ct)
    {
        var conocidos = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in antes?.Ipp.Trabajos ?? [])
            conocidos[t["job-id"] ?? "?"] = Firma(t);
        string? ultimoError = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limite.CancelAfter(TimeSpan.FromSeconds(3));
                foreach (var t in await lector.TrabajosAsync(limite.Token))
                {
                    var id = t["job-id"] ?? "?";
                    var firma = Firma(t);
                    if (conocidos.TryGetValue(id, out var anterior) && anterior == firma)
                        continue;
                    conocidos[id] = firma;
                    var visto = new TrabajoVisto(reloj.GetLocalNow(), lector.Host, id, t.Atributos);
                    lock (vistos)
                        vistos.Add(visto);
                    lock (salida)
                        salida.WriteLine($"  IPP {lector.Host}: trabajo {id}: {visto.Texto}");
                }
                ultimoError = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (ultimoError != ex.Message)
                {
                    lock (salida)
                        salida.WriteLine($"  IPP {lector.Host}: no contestó Get-Jobs ({ex.Message}); se sigue intentando.");
                }
                ultimoError = ex.Message;
            }
            try
            {
                await Task.Delay(_opciones.PausaVigilancia, reloj, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static string Firma(Ipp.Grupo t) => string.Join("|", t.Atributos.Where(a => a.Nombre != "job-printer-up-time").Select(a => $"{a.Nombre}={a.Texto}"));

    private static string QueContesto(FotoImpresora f)
    {
        var snmp = f.Snmp.Sum(c => c.Entradas.Count);
        var version = f.Snmp.Select(c => c.Version).FirstOrDefault(v => v is not null);
        var partes = new List<string>
        {
            f.Modelo ?? "no dijo su modelo",
            f.Serie is { } s ? $"serie {s}" : "sin serie",
            snmp > 0 ? $"SNMP {snmp:N0} datos ({version})" : "SNMP no contestó",
            f.St2 is { Error: null, Color: { } c, BlancoYNegro: { } b } ? $"ST2 color {c:N0} / B/N {b:N0}" : f.St2 is { Error: null } ? "ST2 sin color/B/N" : "ST2 no",
            f.Ipp.Atributos is not null ? $"IPP sí ({(f.Ipp.Url!.StartsWith("https", StringComparison.Ordinal) ? "ipps" : "ipp")}, {f.Ipp.Trabajos.Count()} trabajo(s) guardados)" : "IPP no",
            f.Escl.FirstOrDefault(e => e.Nombre == "ScannerCapabilities") is { Codigo: 200 } ? "eSCL sí" : $"eSCL no ({f.Escl.FirstOrDefault()?.Estado})",
            $"Web Config {f.Web.Count(w => w.Codigo == 200)} de {f.Web.Count} páginas",
        };
        return string.Join(" · ", partes);
    }

    private static string Corto(ComparacionImpresora c)
    {
        if (c.Cambios.Count == 0)
            return c.SinComparar.Count > 0 ? $"sin comparar: {string.Join("; ", c.SinComparar)}" : "no cambió nada";
        var partes = new List<string>();
        partes.Add(c.Caras is { } caras ? $"caras {caras:+0;-0;0}" : "sin contador de caras");
        if (c.Color is not null || c.BlancoYNegro is not null)
            partes.Add($"(color {c.Color ?? 0:+0;-0;0}, B/N {c.BlancoYNegro ?? 0:+0;-0;0})");
        var otros = c.Cambios.Count(x => !x.EsReloj && !x.EsDeLosContadores && x.Parte != "ipp trabajos");
        if (otros > 0)
            partes.Add($"{otros} dato(s) más cambiaron");
        if (c.TrabajosIpp.Count > 0)
            partes.Add($"IPP: {c.TrabajosIpp.Count} trabajo(s)");
        return string.Join(" · ", partes);
    }

    private Task EscribirResumenAsync() =>
        File.WriteAllTextAsync(Resumen, ResumenAntesYDespues.Escribir(_inicio, _primeras, lectores.Select(l => l.Host).ToList(), _acciones, _interrumpida));

    /// <summary>El nombre de la carpeta de una acción: sin acentos ni signos, corto.</summary>
    public static string Carpeta(string etiqueta)
    {
        var sinAcentos = new string(etiqueta.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        var limpio = new StringBuilder();
        foreach (var c in sinAcentos)
            limpio.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        var texto = System.Text.RegularExpressions.Regex.Replace(limpio.ToString(), "-+", "-").Trim('-');
        return texto.Length == 0 ? "accion" : texto[..Math.Min(40, texto.Length)].TrimEnd('-');
    }

    public static string Archivo(string host) => string.Concat(host.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}
