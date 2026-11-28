namespace PapeleriaPos.Nucleo.Imagenes;

/// <summary>Cómo se dice el tamaño en «Juntar imágenes para Word» (docs/propuestas/juntar-imagenes.md).</summary>
public enum ModoDelTamano
{
    /// <summary>Todas del mismo alto, en cm; el ancho sale de cada una. El que más se usa.</summary>
    Alto,

    /// <summary>Todas del mismo ancho, en cm; el alto sale de cada una.</summary>
    Ancho,

    /// <summary>El alto más grande con el que caben todas en tantas hojas.</summary>
    EnHojas,
}

public enum TamanoDeHoja
{
    Carta,
    Oficio,
    A4,
}

/// <summary>Una hoja de Word, en centímetros: su tamaño y sus márgenes.</summary>
public sealed record Hoja(double Ancho, double Alto, double Arriba, double Abajo, double Izquierda, double Derecha)
{
    /// <summary>Lo que trae un documento nuevo de Word en español: carta, 2.5 cm arriba y abajo, 3 cm a los lados.</summary>
    public static Hoja Carta { get; } = De(TamanoDeHoja.Carta, horizontal: false, 2.5, 2.5, 3, 3);

    public static Hoja De(TamanoDeHoja tamano, bool horizontal, double arriba, double abajo, double izquierda, double derecha)
    {
        var (ancho, alto) = tamano switch
        {
            TamanoDeHoja.Oficio => (21.59, 34.0),
            TamanoDeHoja.A4 => (21.0, 29.7),
            _ => (21.59, 27.94),
        };
        if (horizontal)
            (ancho, alto) = (alto, ancho);
        return new Hoja(ancho, alto, Math.Max(0, arriba), Math.Max(0, abajo), Math.Max(0, izquierda), Math.Max(0, derecha));
    }

    /// <summary>Lo que queda entre los márgenes (al menos 1 cm, aunque los márgenes no dejen nada).</summary>
    public double AnchoUtil => Math.Max(1, Ancho - Izquierda - Derecha);

    public double AltoUtil => Math.Max(1, Alto - Arriba - Abajo);
}

/// <summary>Una imagen por acomodar: sus píxeles como se van a ver (ya girada, si se giró).</summary>
public sealed record ImagenPorAcomodar(int AnchoPx, int AltoPx)
{
    /// <summary>Ancho ÷ alto.</summary>
    public double Proporcion => AltoPx <= 0 ? 1 : (double)Math.Max(1, AnchoPx) / AltoPx;
}

/// <summary>Dónde queda una imagen: en qué hoja y renglón, su lugar y su tamaño dentro de lo útil de la hoja (cm).</summary>
/// <param name="PuntosPorPulgada">Los píxeles que le tocan a cada pulgada impresa: abajo de 150 se ve borrosa.</param>
/// <param name="Achicada">No cabía a lo ancho (o a lo alto) de la hoja con el tamaño pedido: se achicó a lo que da la hoja.</param>
public sealed record PiezaAcomodada(int Indice, int HojaNumero, int Renglon, double X, double Y, double Ancho, double Alto, double PuntosPorPulgada, bool Achicada)
{
    public bool Borrosa => PuntosPorPulgada < AcomodoDeImagenes.Borrosa;

    public bool MuyBorrosa => PuntosPorPulgada < AcomodoDeImagenes.MuyBorrosa;
}

/// <summary>Cómo quedaron todas.</summary>
/// <param name="Piezas">En el orden de las imágenes.</param>
/// <param name="Hojas">Cuántas hojas salen (al menos 1).</param>
/// <param name="AltoCalculado">En «En hojas», el alto que salió (cm); en los otros modos, null.</param>
/// <param name="NoCaben">En «En hojas»: ni con el alto más chico caben en esas hojas.</param>
/// <param name="LlenadoDeLaUltima">Qué tanto de lo alto de la última hoja se usa (0 a 1).</param>
public sealed record Acomodo(IReadOnlyList<PiezaAcomodada> Piezas, int Hojas, double? AltoCalculado, bool NoCaben, double LlenadoDeLaUltima)
{
    /// <summary>Las piezas de cada renglón, en orden: cada renglón va a Word como un párrafo.</summary>
    public IReadOnlyList<IReadOnlyList<PiezaAcomodada>> Renglones() =>
        Piezas.GroupBy(p => p.Renglon).OrderBy(g => g.Key).Select(g => (IReadOnlyList<PiezaAcomodada>)g.ToList()).ToList();
}

/// <summary>
/// El acomodo de «Juntar imágenes para Word»: las imágenes en renglones, de izquierda a derecha, en el orden en que se juntaron, y a la
/// siguiente hoja cuando ya no cabe un renglón. Es lo mismo que hace Word con imágenes en línea: cada renglón va como un párrafo, con la
/// separación entre imágenes y debajo de cada renglón. Lo que se ve en la ventanita y lo que se pega salen de aquí.
/// </summary>
public static class AcomodoDeImagenes
{
    /// <summary>Puntos por pulgada abajo de los cuales una imagen se ve borrosa al imprimirla.</summary>
    public const double Borrosa = 150;

    /// <summary>Abajo de esto se ve muy mal (el aviso va en rojo).</summary>
    public const double MuyBorrosa = 100;

    /// <summary>
    /// Lo que Word deja de más bajo cada renglón de imágenes en línea (el renglón lleva letra de 1 punto: queda casi nada). Se cuenta para
    /// que «En hojas» no se pase por poco.
    /// </summary>
    public const double HolguraPorRenglon = 0.05;

    /// <summary>El alto más chico que se prueba en «En hojas».</summary>
    public const double AltoMinimo = 0.5;

    /// <summary>
    /// Acomoda las imágenes. <paramref name="valor"/> son cm en Alto y Ancho, y hojas en «En hojas» (se redondea; al menos 1). Sin
    /// imágenes o con un valor que no sirve (0, negativo), null.
    /// </summary>
    public static Acomodo? Acomodar(IReadOnlyList<ImagenPorAcomodar> imagenes, Hoja hoja, ModoDelTamano modo, double valor, double separacion)
    {
        if (imagenes.Count == 0 || double.IsNaN(valor) || valor <= 0)
            return null;
        separacion = Math.Max(0, separacion);
        if (modo != ModoDelTamano.EnHojas)
            return EnRenglones(imagenes, hoja, modo, valor, separacion, altoCalculado: null, noCaben: false);
        var hojas = Math.Max(1, (int)Math.Round(valor, MidpointRounding.AwayFromZero));
        if (EnRenglones(imagenes, hoja, ModoDelTamano.Alto, AltoMinimo, separacion, null, false).Hojas > hojas)
            return EnRenglones(imagenes, hoja, ModoDelTamano.Alto, AltoMinimo, separacion, AltoMinimo, noCaben: true);
        // El alto más grande con el que caben: más alto, más hojas (casi siempre; donde no, se queda con uno que sí cabe).
        double bajo = AltoMinimo, alto = hoja.AltoUtil;
        for (var i = 0; i < 40; i++)
        {
            var medio = (bajo + alto) / 2;
            if (EnRenglones(imagenes, hoja, ModoDelTamano.Alto, medio, separacion, null, false).Hojas <= hojas)
                bajo = medio;
            else
                alto = medio;
        }
        // A décimas, hacia abajo: «Salen de 6.3 cm» es lo que se le dice al cliente.
        var redondo = Math.Max(AltoMinimo, Math.Floor(bajo * 10 + 1e-9) / 10);
        return EnRenglones(imagenes, hoja, ModoDelTamano.Alto, redondo, separacion, redondo, noCaben: false);
    }

    private static Acomodo EnRenglones(IReadOnlyList<ImagenPorAcomodar> imagenes, Hoja hoja, ModoDelTamano modo, double valor, double separacion,
        double? altoCalculado, bool noCaben)
    {
        const double Tolerancia = 1e-6;
        double anchoUtil = hoja.AnchoUtil, altoUtil = hoja.AltoUtil;
        var piezas = new List<PiezaAcomodada>(imagenes.Count);
        int numeroHoja = 1, renglon = 0;
        double x = 0, y = 0, altoDelRenglon = 0;
        for (var i = 0; i < imagenes.Count; i++)
        {
            var imagen = imagenes[i];
            var proporcion = imagen.Proporcion;
            double ancho, alto;
            if (modo == ModoDelTamano.Ancho)
                (ancho, alto) = (valor, valor / proporcion);
            else
                (ancho, alto) = (valor * proporcion, valor);
            var achicada = false;
            if (ancho > anchoUtil + Tolerancia)
                (ancho, alto, achicada) = (anchoUtil, anchoUtil / proporcion, true);
            if (alto > altoUtil + Tolerancia)
                (ancho, alto, achicada) = (altoUtil * proporcion, altoUtil, true);
            // Al siguiente renglón si ya no cabe a lo ancho.
            if (x > 0 && x + ancho > anchoUtil + Tolerancia)
            {
                y += altoDelRenglon + separacion + HolguraPorRenglon;
                (x, altoDelRenglon) = (0, 0);
                renglon++;
            }
            // A la siguiente hoja si el renglón (con esta imagen) ya no cabe a lo alto.
            if (y > 0 && y + Math.Max(altoDelRenglon, alto) > altoUtil + Tolerancia)
            {
                if (x > 0)
                {
                    // Una más alta que las de su renglón ya no cabe: el renglón entero baja con ella, como en Word.
                    var delRenglon = piezas.Where(p => p.Renglon == renglon).ToList();
                    piezas.RemoveRange(piezas.Count - delRenglon.Count, delRenglon.Count);
                    numeroHoja++;
                    piezas.AddRange(delRenglon.Select(p => p with { HojaNumero = numeroHoja, Y = 0 }));
                    y = 0;
                }
                else
                {
                    numeroHoja++;
                    y = 0;
                }
            }
            var ppp = (modo == ModoDelTamano.Ancho ? imagen.AnchoPx / (ancho / 2.54) : imagen.AltoPx / (alto / 2.54));
            piezas.Add(new PiezaAcomodada(i, numeroHoja, renglon, x, y, ancho, alto, ppp, achicada));
            x += ancho + separacion;
            altoDelRenglon = Math.Max(altoDelRenglon, alto);
        }
        var llenado = Math.Clamp((y + altoDelRenglon) / altoUtil, 0, 1);
        return new Acomodo(piezas, numeroHoja, altoCalculado, noCaben, llenado);
    }
}
