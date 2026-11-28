using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Avisos;

namespace PapeleriaPos.Impresiones;

/// <summary>
/// Una charola (o bandeja, o alimentador) de un modelo de impresora, con lo que le cabe de papel normal según la ficha de Epson
/// (consultada el 2026-11-28): la WF-6590 trae la charola de 500 hojas y el alimentador trasero de 80; la L5590, una bandeja de 100.
/// </summary>
/// <param name="Numero">1 es la principal: ahí se cuenta lo que no dice su tamaño o lo que no es del tamaño de ninguna otra.</param>
/// <param name="TamanoPorOmision">Lo que se ofrece al anotar un llenado si nunca se ha anotado otro.</param>
public sealed record CharolaDeImpresora(int Numero, string Nombre, int Capacidad, PaperSize TamanoPorOmision);

/// <summary>
/// Cuántas hojas se pusieron en una charola (2026-11-28): lo anota la caja al llenarla, con el teclado, desde el «!» de arriba. Desde ahí se
/// restan las hojas de lo que salió de esa impresora (<see cref="CalculoDelPapel"/>): las impresoras de la tienda no dicen cuántas les
/// quedan, a lo más que se quedaron sin papel (lo encontrado, en docs/impresion.md, «Las hojas en la charola»).
/// </summary>
public class LlenadoPapel
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }
    public Guid ImpresoraId { get; set; }

    /// <summary>El número de su charola en <see cref="PapelDeImpresoras.CharolasDe"/>.</summary>
    public int Charola { get; set; }

    /// <summary>Lo que se le puso, como lo dijo quien la llenó.</summary>
    public int Puestas { get; set; }

    /// <summary>Lo que quedó en la charola: lo puesto más lo que se calculaba que tenía (sin pasar de lo que le cabe), o lo que le cabe si «la llené».</summary>
    public int Quedaron { get; set; }

    /// <summary>El tamaño de lo que se puso (<see cref="PaperSize"/>, como en los trabajos): a esa charola van los trabajos de ese tamaño.</summary>
    public required string Tamano { get; set; }

    public DateTime AnotadoUtc { get; set; }

    /// <summary>Quien lo anotó (vacío si no se sabe).</summary>
    public Guid? UsuarioId { get; set; }
}

/// <summary>Las charolas de cada modelo.</summary>
public static class PapelDeImpresoras
{
    public static readonly IReadOnlyList<CharolaDeImpresora> WF6590 =
        [new(1, "Charola", 500, PaperSize.Carta), new(2, "Alimentador trasero", 80, PaperSize.Oficio)];

    public static readonly IReadOnlyList<CharolaDeImpresora> L5590 = [new(1, "Bandeja", 100, PaperSize.Carta)];

    public static IReadOnlyList<CharolaDeImpresora> CharolasDe(PerfilImpresora perfil) => perfil == PerfilImpresora.L5590 ? L5590 : WF6590;

    public static CharolaDeImpresora? Charola(PerfilImpresora perfil, int numero) => CharolasDe(perfil).FirstOrDefault(c => c.Numero == numero);

    /// <summary>«Carta», «Oficio»…: como se le dice en la tienda (son los nombres de <see cref="PaperSize"/>).</summary>
    public static string Tamano(string tamano) => string.IsNullOrEmpty(tamano) ? "Carta" : tamano;
}

/// <summary>Un trabajo visto por lo que gastó de papel: cuándo salió, de qué tamaño y cuántas hojas.</summary>
public readonly record struct HojasDeUnTrabajo(DateTime ImpresoUtc, string Tamano, int Hojas);

/// <summary>Lo que se calcula de una charola y, si toca, el aviso.</summary>
/// <param name="Quedan">Las que se calcula que le quedan (nunca menos de cero); vacío si nunca se anotó un llenado.</param>
/// <param name="Esperadas">Las que se espera que salgan de ella de aquí a <paramref name="HastaLocal"/>, por lo que se imprime a esas horas.</param>
/// <param name="Pico">Si el plazo se alargó para cubrir la hora pico que viene.</param>
/// <param name="SinPapel">La impresora dijo que se quedó sin papel.</param>
/// <param name="Reportadas">Si lo que queda lo dijo la impresora (su Printer-MIB) en lugar de calcularse.</param>
/// <param name="Nivel">Vacío: no hay aviso.</param>
public sealed record EstadoDeLaCharola(
    CharolaDeImpresora Charola, LlenadoPapel? Llenado, int Usadas, int? Quedan, int Esperadas, DateTime HastaLocal, bool Pico, bool SinPapel, NivelAviso? Nivel,
    bool Reportadas = false);

/// <summary>
/// El cálculo de las hojas que le quedan a cada charola (2026-11-28): lo que se puso en el último llenado menos las hojas de lo que salió
/// desde entonces de esa impresora y a esa charola (por su tamaño), y cuándo avisar: antes de que se acaben, con lo que se imprime a esas
/// horas, para que no se acaben en la hora pico.
/// <list type="bullet">
/// <item><see cref="NivelAviso.Atencion"/> cuando lo que queda no alcanza para lo que se espera imprimir en las próximas
/// <see cref="Plazo"/> horas (alargadas hasta el final de la hora pico si empieza en menos de <see cref="AntesDelPico"/>), o es poco
/// (<see cref="Minimo"/>).</item>
/// <item><see cref="NivelAviso.Urgente"/> cuando no alcanza para la próxima media hora, quedan 5 o menos, o la impresora dijo que no tiene
/// papel.</item>
/// <item><see cref="NivelAviso.Informativo"/> si nunca se ha anotado un llenado en la charola principal: «la próxima vez que la llenes,
/// anota cuántas pusiste».</item>
/// </list>
/// </summary>
public static class CalculoDelPapel
{
    /// <summary>Cuánto adelante se mira lo que se va a imprimir.</summary>
    public static readonly TimeSpan Plazo = TimeSpan.FromHours(2);

    /// <summary>Si la hora pico empieza antes de esto, el plazo se alarga hasta que termine: así se llena antes y no a media hora pico.</summary>
    public static readonly TimeSpan AntesDelPico = TimeSpan.FromHours(3);

    /// <summary>Hasta dónde se alarga el plazo por la hora pico.</summary>
    public static readonly TimeSpan PlazoMaximo = TimeSpan.FromHours(6);

    /// <summary>Lo urgente: lo que se espera en esta media hora.</summary>
    public static readonly TimeSpan PlazoUrgente = TimeSpan.FromMinutes(30);

    /// <summary>Una hora es pico si en ella se imprime al menos esto contra el promedio de las horas en que se imprime.</summary>
    public const double FactorPico = 1.5;

    /// <summary>Los días de historia con que se calcula lo que se imprime a cada hora.</summary>
    public const int DiasDeHistoria = 28;

    /// <summary>Con esto o menos siempre se avisa: el 5 % de lo que le cabe, al menos 10.</summary>
    public static int Minimo(CharolaDeImpresora charola) => Math.Max(10, charola.Capacidad / 20);

    public const int MinimoUrgente = 5;

    /// <summary>
    /// Las hojas de un trabajo: las suyas; si faltaron caras (atasco, cancelación), solo la parte que salió; las tarjetas «Copias o celular»
    /// no saben si fueron a doble cara: una hoja por cara.
    /// </summary>
    public static int HojasDe(TrabajoImpresion t)
    {
        int hojas = t.Hojas > 0 ? t.Hojas : t.Caras;
        if (t.Tipo != TipoTrabajoImpresion.Contador && t.CarasContadas is { } contadas && t.Caras > 0 && contadas < t.Caras)
            hojas = (int)Math.Ceiling(hojas * (double)Math.Max(0, contadas) / t.Caras);
        return Math.Max(0, hojas);
    }

    /// <summary>
    /// A qué charola va un trabajo de ese tamaño: a la que tiene ese papel según su último llenado (la de menor número si son varias); si
    /// ninguna, a la principal.
    /// </summary>
    public static int CharolaPara(string tamano, IReadOnlyDictionary<int, LlenadoPapel> llenados)
    {
        var t = PapelDeImpresoras.Tamano(tamano);
        foreach (var (numero, llenado) in llenados.OrderBy(l => l.Key))
            if (string.Equals(PapelDeImpresoras.Tamano(llenado.Tamano), t, StringComparison.OrdinalIgnoreCase))
                return numero;
        return 1;
    }

    /// <summary>
    /// Las hojas que salen, en promedio, a cada hora del día (hora de México, 0 a 23), de lo que salió de la impresora en los últimos
    /// <see cref="DiasDeHistoria"/> días: la suma de cada hora entre los días en que se imprimió algo (un día cerrado no baja el promedio).
    /// </summary>
    public static double[] UsoPorHora(IEnumerable<HojasDeUnTrabajo> historia, DateTime ahoraUtc)
    {
        var desde = ahoraUtc.AddDays(-DiasDeHistoria);
        var porHora = new double[24];
        var dias = new HashSet<DateOnly>();
        foreach (var t in historia)
        {
            if (t.ImpresoUtc < desde || t.ImpresoUtc > ahoraUtc)
                continue;
            var local = HoraMexico.ALocal(t.ImpresoUtc);
            dias.Add(DateOnly.FromDateTime(local));
            porHora[local.Hour] += t.Hojas;
        }
        if (dias.Count > 0)
            for (int h = 0; h < 24; h++)
                porHora[h] /= dias.Count;
        return porHora;
    }

    /// <summary>Las horas pico: las que imprimen al menos <see cref="FactorPico"/> veces el promedio de las horas en que se imprime algo.</summary>
    public static bool[] HorasPico(double[] uso)
    {
        var activas = uso.Where(u => u > 0).ToList();
        var promedio = activas.Count == 0 ? 0 : activas.Average();
        return [.. uso.Select(u => promedio > 0 && u >= promedio * FactorPico)];
    }

    /// <summary>
    /// Lo que se espera que salga de aquí a <paramref name="plazo"/>, por hora y en proporción de la hora que va; y si el plazo se alargó
    /// por la hora pico (cuando empieza en menos de <see cref="AntesDelPico"/>, hasta que termine, sin pasar de <see cref="PlazoMaximo"/>).
    /// </summary>
    public static (double Esperadas, DateTime HastaLocal, bool Pico) Esperadas(double[] uso, DateTime ahoraLocal, TimeSpan plazo, bool alargarPorElPico = true)
    {
        var hasta = ahoraLocal + plazo;
        bool pico = false;
        if (alargarPorElPico)
        {
            var picos = HorasPico(uso);
            var hora = new DateTime(ahoraLocal.Year, ahoraLocal.Month, ahoraLocal.Day, ahoraLocal.Hour, 0, 0);
            // La primera hora pico que empieza antes de AntesDelPico (o la que ya corre), y de ahí hasta que se acabe.
            for (var h = hora; h < ahoraLocal + AntesDelPico; h = h.AddHours(1))
            {
                if (!picos[h.Hour])
                    continue;
                var fin = h;
                while (picos[fin.Hour] && fin < ahoraLocal + PlazoMaximo)
                    fin = fin.AddHours(1);
                if (fin > hasta)
                {
                    hasta = fin > ahoraLocal + PlazoMaximo ? ahoraLocal + PlazoMaximo : fin;
                    pico = true;
                }
                break;
            }
        }
        double total = 0;
        for (var desde = ahoraLocal; desde < hasta;)
        {
            var finDeLaHora = new DateTime(desde.Year, desde.Month, desde.Day, desde.Hour, 0, 0).AddHours(1);
            var corte = finDeLaHora < hasta ? finDeLaHora : hasta;
            total += uso[desde.Hour] * (corte - desde).TotalHours;
            desde = corte;
        }
        return (total, hasta, pico);
    }

    /// <summary>
    /// Cómo está una charola: con su último llenado (vacío si nunca), las hojas de lo que salió a ella desde entonces, lo que se imprime a cada
    /// hora y si la impresora dijo que no tiene papel. Si la impresora dice cuántas le quedan (<paramref name="reportadas"/>, su Printer-MIB), eso
    /// manda sobre lo calculado.
    /// </summary>
    public static EstadoDeLaCharola Evaluar(CharolaDeImpresora charola, LlenadoPapel? llenado, int usadas, double[] uso, DateTime ahoraUtc, bool sinPapel = false,
        int? reportadas = null)
    {
        var ahoraLocal = HoraMexico.ALocal(ahoraUtc);
        var (esperadas, hasta, pico) = Esperadas(uso, ahoraLocal, Plazo);
        int esperadasEnteras = (int)Math.Ceiling(esperadas);
        if (llenado is null && reportadas is null)
        {
            // Sin llenado no se sabe cuánto queda: si dijo que no tiene papel, es urgente; si no, en la principal se pide anotarlo.
            NivelAviso? nivel = sinPapel ? NivelAviso.Urgente : charola.Numero == 1 ? NivelAviso.Informativo : null;
            return new(charola, null, usadas, null, esperadasEnteras, hasta, pico, sinPapel, nivel);
        }
        int quedan = reportadas is { } r ? Math.Max(0, r) : Math.Max(0, llenado!.Quedaron - usadas);
        sinPapel |= quedan == 0 && reportadas is not null;
        var (urgentes, _, _) = Esperadas(uso, ahoraLocal, PlazoUrgente, alargarPorElPico: false);
        NivelAviso? n = sinPapel || quedan <= MinimoUrgente || (urgentes > 0 && quedan < urgentes) ? NivelAviso.Urgente
            : quedan <= Minimo(charola) || quedan < esperadas ? NivelAviso.Atencion
            : null;
        return new(charola, llenado, usadas, quedan, esperadasEnteras, hasta, pico, sinPapel, n, reportadas is not null);
    }

    /// <summary>Lo que quedó al anotar un llenado: lo puesto más lo que se calculaba que tenía, sin pasar de lo que le cabe; con «la llené», lo que le cabe.</summary>
    public static int QuedaronAlLlenar(CharolaDeImpresora charola, int puestas, int? quedaban, bool llena) =>
        llena ? charola.Capacidad : Math.Clamp(puestas + Math.Max(0, quedaban ?? 0), 0, Math.Max(charola.Capacidad, puestas));
}
