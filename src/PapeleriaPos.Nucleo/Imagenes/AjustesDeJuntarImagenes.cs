using System.Text.Json.Serialization;

namespace PapeleriaPos.Nucleo.Imagenes;

/// <summary>
/// Lo que la ventanita de «Juntar imágenes para Word» recuerda en cada caja: la siguiente vez abre igual. Cada modo guarda su número
/// (cambiar de Alto a En hojas y regresar no pierde los 5 cm).
/// </summary>
public sealed record AjustesDeJuntarImagenes
{
    public ModoDelTamano Modo { get; init; } = ModoDelTamano.Alto;
    public double Alto { get; init; } = 5;
    public double Ancho { get; init; } = 8;
    public int Hojas { get; init; } = 1;
    public TamanoDeHoja Tamano { get; init; } = TamanoDeHoja.Carta;
    public bool Horizontal { get; init; }
    public double MargenArriba { get; init; } = 2.5;
    public double MargenAbajo { get; init; } = 2.5;
    public double MargenIzquierda { get; init; } = 3;
    public double MargenDerecha { get; init; } = 3;
    public double Separacion { get; init; } = 0.3;
    public bool LineasDeCorte { get; init; }
    public bool BlancoYNegro { get; init; }

    [JsonIgnore]
    public Hoja Hoja => Imagenes.Hoja.De(Tamano, Horizontal, MargenArriba, MargenAbajo, MargenIzquierda, MargenDerecha);

    /// <summary>El número del modo de ahora: cm en Alto y Ancho, hojas en En hojas.</summary>
    [JsonIgnore]
    public double Valor => Modo switch
    {
        ModoDelTamano.Ancho => Ancho,
        ModoDelTamano.EnHojas => Hojas,
        _ => Alto,
    };

    public AjustesDeJuntarImagenes ConValor(double valor) => Modo switch
    {
        ModoDelTamano.Ancho => this with { Ancho = valor },
        ModoDelTamano.EnHojas => this with { Hojas = Math.Max(1, (int)Math.Round(valor, MidpointRounding.AwayFromZero)) },
        _ => this with { Alto = valor },
    };

    public Acomodo? Acomodar(IReadOnlyList<ImagenPorAcomodar> imagenes) =>
        AcomodoDeImagenes.Acomodar(imagenes, Hoja, Modo, Valor, Separacion);
}
