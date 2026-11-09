using System.Text.Json;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones;

/// <summary>
/// Las tarjetas «Copias o celular» de una impresora (2026-11-08, docs/impresion.md, «Celular y copias sin registrar»): si se ponen, con
/// qué tinta se estima su precio y cuánto tiempo sin caras nuevas cierra una. Se guarda en la impresora (<see cref="Impresora.ContadorJson"/>).
/// </summary>
public sealed record AjustesDelContador
{
    /// <summary>Por omisión, 5 % de tinta por cara (la negra en B/N, cada tinta a color), como las copias del panel.</summary>
    public const double CoberturaPorOmision = 5;

    /// <summary>Sin caras nuevas durante esto, se cierra la tarjeta (lo que siga sale en otra).</summary>
    public const int PausaPorOmision = 60;

    /// <summary>
    /// Lo que se espera después de la pausa antes de poner la tarjeta: lo que mandó una PC y su agente mide un poco después de que la
    /// impresora lo contó llega en ese tiempo y no sale como tarjeta (con la pausa, 90 s: lo mismo que la vigilancia espera para leer tras
    /// una impresión). Lo que llegue más tarde todavía encoge la tarjeta si sigue por cobrar.
    /// </summary>
    public static readonly TimeSpan Asentar = TimeSpan.FromSeconds(30);

    /// <summary>Mientras hay caras que acomodar, la impresora se lee cada esto (solo el contador).</summary>
    public static readonly TimeSpan MientrasSeMueve = TimeSpan.FromSeconds(10);

    /// <summary>Si el servicio pone las tarjetas de esta impresora.</summary>
    public bool Activas { get; init; }

    /// <summary>Tinta negra por cara en B/N, en porcentaje.</summary>
    public double CoberturaBN { get; init; } = CoberturaPorOmision;

    /// <summary>Cada tinta por cara a color, en porcentaje.</summary>
    public double CoberturaColor { get; init; } = CoberturaPorOmision;

    public int PausaSegundos { get; init; } = PausaPorOmision;

    /// <summary>
    /// Desde cuándo cuenta el contador: al ponerlas, al cambiar la dirección de la impresora o al darla de alta otra vez. Lo de antes no se
    /// vuelve tarjeta (sería de otra impresora, o de antes de ponerlas).
    /// </summary>
    public DateTime? DesdeUtc { get; init; }

    public TimeSpan Pausa => TimeSpan.FromSeconds(PausaSegundos);

    /// <summary>
    /// Lo de su modelo (decidido, por revisar, 2026-11-08): puestas en la WF-6590, cuyo contador y su color/B/N (ST2) ya se vieron en la
    /// tienda; quitadas en la L5590 hasta confirmar su contador ahí.
    /// </summary>
    public static AjustesDelContador PorOmision(PerfilImpresora perfil) => new() { Activas = perfil == PerfilImpresora.WF6590 };

    public static AjustesDelContador? Leer(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AjustesDelContador>(json, CostSettings.Json);

    public static string Escribir(AjustesDelContador ajustes) => JsonSerializer.Serialize(ajustes, CostSettings.Json);

    /// <summary>Los de esa impresora; sin guardar, los de su modelo.</summary>
    public static AjustesDelContador De(Impresora impresora) => Leer(impresora.ContadorJson) ?? PorOmision(impresora.Perfil);

    /// <summary>Que tengan sentido; si no, el error para el cajero.</summary>
    public void Revisar()
    {
        if (CoberturaBN is < 0.5 or > 50 || CoberturaColor is < 0.5 or > 50)
            throw new ArgumentException("La tinta por cara va de 0.5 a 50 %.");
        if (PausaSegundos is < 20 or > 600)
            throw new ArgumentException("La pausa que cierra una tarjeta va de 20 a 600 segundos.");
    }

    /// <summary>Si la impresora cambia de dirección o se da de alta otra vez, el contador vuelve a contar desde ahí.</summary>
    public static void ContarDesde(Impresora impresora, DateTime ahoraUtc)
    {
        if (Leer(impresora.ContadorJson) is { } ajustes)
            impresora.ContadorJson = Escribir(ajustes with { DesdeUtc = ahoraUtc });
    }
}

/// <summary>
/// Lo que la tarjeta «Copias o celular» guarda de su contador (en <see cref="TrabajoImpresion.DetalleJson"/>): las caras que se le
/// cuentan (las que nadie registró) y cuántas a color, si la impresora lo dice.
/// </summary>
/// <param name="Contadas">Las que se le cuentan al contador: las que se cobran pueden ser menos (el cajero las corrige).</param>
/// <param name="ContadasColor">De ellas, a color; vacío si la impresora no lo dice (sin el ST2).</param>
/// <param name="DesdeUtc">La lectura de antes de la primera cara: entre esa hora y la última cara pasó.</param>
/// <param name="CoberturaBN">Con qué tinta se estimó, para volver a calcular.</param>
/// <param name="Conocidos">
/// Las impresiones de las PC de esas horas que ya estaban registradas cuando se puso o se encogió: una que llegue después, con la tarjeta ya
/// cobrada, dice en su nota que la impresora ya la contó.
/// </param>
public sealed record DetalleDelContador(int Contadas, int? ContadasColor, DateTime DesdeUtc, double CoberturaBN, double CoberturaColor,
    IReadOnlyList<Guid>? Conocidos = null)
{
    /// <summary>Lo que se le da de más a sus horas para buscar lo de una PC (el reloj de esa PC puede no coincidir).</summary>
    public static readonly TimeSpan Margen = TimeSpan.FromMinutes(2);

    public static DetalleDelContador? Leer(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<DetalleDelContador>(json, CostSettings.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Escribir() => JsonSerializer.Serialize(this, CostSettings.Json);
}

/// <summary>
/// Las caras de una impresora que nadie registró, como tarjetas (2026-11-08). Lo que contó la impresora desde que se cuenta (el día, o desde
/// <see cref="AjustesDelContador.DesdeUtc"/>), menos lo registrado en ella (las impresiones de las PC con lo que de verdad salió y las copias
/// del panel) y menos lo que ya está en sus tarjetas, es lo que falta acomodar (<see cref="Resultado.Saldo"/>):
/// <list type="bullet">
/// <item>De más: cuando la impresora lleva <see cref="AjustesDelContador.Pausa"/> sin caras nuevas y pasó además
/// <see cref="AjustesDelContador.Asentar"/> desde lo último que se movió o se registró, sale una tarjeta con esas caras, a la hora en que
/// pasaron.</item>
/// <item>De menos (llegó tarde lo de una PC, o se registraron copias): se encogen las tarjetas que siguen por cobrar, la más nueva primero
/// (o la que se diga), y la que se queda sin caras se quita.</item>
/// </list>
/// Así nada se cobra dos veces: cada cara que contó la impresora está una sola vez, en un trabajo o en una tarjeta. Nunca se inventan caras:
/// si el contador bajó (se cambió o se reinició), si contesta otra serie o si salta más de lo que la impresora puede imprimir en ese tiempo,
/// se vuelve a contar desde esa lectura, sin tarjeta.
/// </summary>
public static class ConciliacionDelContador
{
    /// <summary>Más que esto por minuto no lo imprime ninguna (la WF-6590 saca unas 34 por minuto, y en borrador más): es otro contador.</summary>
    public const int CarasPorMinutoMaximas = 120;

    /// <summary>Lo que se le permite de más a un salto entre dos lecturas muy juntas.</summary>
    public const int SaltoMinimo = 30;

    /// <summary>Una lectura del contador (la total y, si la dice, la de color).</summary>
    public sealed record Lectura(DateTime TomadaUtc, long Contador, long? Color, string? Serie);

    /// <summary>Lo registrado en la impresora: una impresión de una PC (lo que salió) o unas copias del panel.</summary>
    public sealed record Registrado(DateTime Utc, int Caras, int CarasColor);

    /// <summary>Una tarjeta del contador con lo que se le cuenta.</summary>
    public sealed record Tarjeta(Guid Id, DateTime DesdeUtc, DateTime HastaUtc, int Contadas, int? ContadasColor, bool PorCobrar);

    /// <summary>Una tarjeta nueva: sus caras, cuántas a color (vacío si no se sabe) y entre qué horas pasaron.</summary>
    public sealed record Nueva(DateTime DesdeUtc, DateTime HastaUtc, int Caras, int? Color);

    /// <summary>Lo que se le cuenta ahora a una tarjeta que se encoge (cero: se quita).</summary>
    public sealed record Cambio(Guid Id, int Contadas, int? ContadasColor);

    /// <summary>Unas copias que se acaban de registrar: se toman primero de las tarjetas por cobrar (de <paramref name="Preferida"/>, si se dice).</summary>
    public sealed record Consumir(int Caras, int CarasColor, Guid? Preferida = null);

    /// <param name="Saldo">Lo que contó la impresora menos lo registrado y lo que está en tarjetas, antes de esta revisión.</param>
    /// <param name="Sobran">Lo registrado de más que ya no cupo en ninguna tarjeta por cobrar.</param>
    /// <param name="Activa">Hay caras que acomodar: conviene leerla pronto (<see cref="AjustesDelContador.MientrasSeMueve"/>).</param>
    /// <param name="BaseUtc">La lectura desde la que se cuenta.</param>
    /// <param name="Corte">Por qué se volvió a contar desde una lectura (vacío si no).</param>
    public sealed record Resultado(Nueva? Nueva, IReadOnlyList<Cambio> Encoger, bool Activa, int Saldo, int? SaldoColor, int Sobran, DateTime? BaseUtc,
        string? Corte)
    {
        public static readonly Resultado Nada = new(null, [], false, 0, null, 0, null, null);
    }

    /// <summary>
    /// Revisa una impresora: <paramref name="lecturas"/> con su contador, <paramref name="registrados"/> y <paramref name="tarjetas"/> desde
    /// <paramref name="desdeUtc"/>. Con <paramref name="consumir"/> (unas copias que se acaban de registrar), solo encoge, hasta esas caras y
    /// sin esperar la pausa.
    /// </summary>
    public static Resultado Revisar(IEnumerable<Lectura> lecturas, IEnumerable<Registrado> registrados, IEnumerable<Tarjeta> tarjetas,
        DateTime desdeUtc, DateTime ahoraUtc, TimeSpan pausa, Consumir? consumir = null)
    {
        var puntos = lecturas.Where(l => l.TomadaUtc >= desdeUtc).OrderBy(l => l.TomadaUtc).ToList();
        if (puntos.Count == 0)
            return Resultado.Nada;

        // Desde la última lectura en la que no se puede seguir sumando: otra serie, el contador que bajó o un salto imposible.
        var inicio = 0;
        string? corte = null;
        var serie = puntos[0].Serie;
        for (var i = 1; i < puntos.Count; i++)
        {
            var (a, b) = (puntos[i - 1], puntos[i]);
            if (b.Serie is { } s && serie is { } antes && !string.Equals(s, antes, StringComparison.OrdinalIgnoreCase))
                (inicio, corte, serie) = (i, "contesta otra serie", s);
            else if (b.Contador < a.Contador)
                (inicio, corte) = (i, "el contador bajó (se cambió o se reinició)");
            else if (b.Contador - a.Contador > CarasPorMinutoMaximas * Math.Max(0, (b.TomadaUtc - a.TomadaUtc).TotalMinutes) + SaltoMinimo)
                (inicio, corte) = (i, "el contador saltó más de lo que se puede imprimir");
            serie ??= b.Serie;
        }
        var tramo = puntos.GetRange(inicio, puntos.Count - inicio);
        var baseUtc = tramo[0].TomadaUtc;
        var contadas = tramo[^1].Contador - tramo[0].Contador;
        long? contadasColor = null;
        if (tramo.All(l => l.Color is not null) && tramo.Zip(tramo.Skip(1)).All(p => p.Second.Color >= p.First.Color))
            contadasColor = tramo[^1].Color - tramo[0].Color;

        var aumentos = Enumerable.Range(1, Math.Max(0, tramo.Count - 1)).Where(i => tramo[i].Contador > tramo[i - 1].Contador).ToList();
        var ultimoAumento = aumentos.Count > 0 ? tramo[aumentos[^1]].TomadaUtc : baseUtc;
        var r = registrados.Where(x => x.Utc > baseUtc).ToList();
        var k = tarjetas.Where(t => t.HastaUtc > baseUtc).ToList();
        var saldo = (int)Math.Clamp(contadas - r.Sum(x => (long)x.Caras) - k.Sum(t => (long)t.Contadas), int.MinValue, int.MaxValue);
        int? saldoColor = contadasColor is { } cc && k.All(t => t.ContadasColor is not null)
            ? (int)Math.Clamp(cc - r.Sum(x => (long)x.CarasColor) - k.Sum(t => (long)t.ContadasColor!.Value), int.MinValue, int.MaxValue)
            : null;

        var ultimaActividad = r.Count > 0 && r.Max(x => x.Utc) > ultimoAumento ? r.Max(x => x.Utc) : ultimoAumento;
        var espera = pausa + AjustesDelContador.Asentar;
        var cerrada = aumentos.Count == 0 || tramo[^1].TomadaUtc - ultimoAumento >= pausa;
        var lista = cerrada && ahoraUtc - ultimaActividad >= espera;
        var activa = !lista && ahoraUtc - ultimaActividad < espera + TimeSpan.FromMinutes(1);
        var porCobrar = k.Where(t => t.PorCobrar && t.Contadas > 0).OrderByDescending(t => t.HastaUtc).ToList();

        if (consumir is { } c)
        {
            var cuanto = Math.Min(Math.Max(0, c.Caras), Math.Max(0, -saldo));
            if (cuanto == 0)
                return new Resultado(null, [], activa, saldo, saldoColor, 0, baseUtc, corte);
            int? color = saldoColor is { } sc ? Math.Clamp(-sc, 0, cuanto) : Math.Clamp(c.CarasColor, 0, cuanto);
            var orden = porCobrar.Where(t => t.Id == c.Preferida).Concat(porCobrar.Where(t => t.Id != c.Preferida)).ToList();
            var (cambios, faltan) = Encoger(orden, cuanto, color);
            return new Resultado(null, cambios, activa, saldo, saldoColor, faltan, baseUtc, corte);
        }
        if (!lista || saldo == 0)
            return new Resultado(null, [], activa || (!lista && saldo != 0), saldo, saldoColor, 0, baseUtc, corte);
        if (saldo > 0)
        {
            // La racha: los aumentos sin una pausa entre ellos, después de la última tarjeta.
            var ultimaTarjeta = k.Count > 0 ? k.Max(t => t.HastaUtc) : DateTime.MinValue;
            var deLaRacha = aumentos.Where(i => tramo[i].TomadaUtc > ultimaTarjeta).ToList();
            DateTime desde, hasta;
            if (deLaRacha.Count == 0)
                (desde, hasta) = (aumentos.Count > 0 ? tramo[aumentos[^1] - 1].TomadaUtc : baseUtc, ultimoAumento);
            else
            {
                var primero = deLaRacha.Count - 1;
                while (primero > 0 && tramo[deLaRacha[primero] - 1].TomadaUtc - tramo[deLaRacha[primero - 1]].TomadaUtc < pausa)
                    primero--;
                (desde, hasta) = (tramo[deLaRacha[primero] - 1].TomadaUtc, tramo[deLaRacha[^1]].TomadaUtc);
            }
            return new Resultado(new Nueva(desde, hasta, saldo, saldoColor is { } scn ? Math.Clamp(scn, 0, saldo) : null), [], false, saldo, saldoColor, 0,
                baseUtc, corte);
        }
        {
            var cuanto = -saldo;
            int? color = saldoColor is { } sc ? Math.Clamp(-sc, 0, cuanto) : null;
            var (cambios, faltan) = Encoger(porCobrar, cuanto, color);
            return new Resultado(null, cambios, false, saldo, saldoColor, faltan, baseUtc, corte);
        }
    }

    /// <summary>
    /// Quita <paramref name="cuanto"/> caras de las tarjetas, en ese orden; de ellas, <paramref name="color"/> a color (vacío: primero las de
    /// B/N). Regresa lo que les queda a las que cambian y lo que no cupo.
    /// </summary>
    private static (List<Cambio> Cambios, int Faltan) Encoger(IEnumerable<Tarjeta> orden, int cuanto, int? color)
    {
        var cambios = new List<Cambio>();
        foreach (var t in orden)
        {
            if (cuanto <= 0)
                break;
            var quita = Math.Min(cuanto, t.Contadas);
            var bn = t.Contadas - (t.ContadasColor ?? 0);
            var quitaColor = 0;
            if (t.ContadasColor is { } tc)
            {
                quitaColor = color is { } c ? Math.Min(Math.Min(c, tc), quita) : 0;
                quitaColor = Math.Max(quitaColor, quita - bn);
            }
            cuanto -= quita;
            if (color is not null)
                color = Math.Max(0, color.Value - quitaColor);
            cambios.Add(new Cambio(t.Id, t.Contadas - quita, t.ContadasColor is { } cc ? cc - quitaColor : null));
        }
        return (cambios, Math.Max(0, cuanto));
    }
}
