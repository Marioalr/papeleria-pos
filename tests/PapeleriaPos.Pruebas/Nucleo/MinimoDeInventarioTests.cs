using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// El mínimo y el máximo de inventario de cada variante (2026-10-17, como en eleventa): «Poca existencia» y «Bajos en
/// inventario» usan el propio y, sin él, la regla de siempre; lo que se escribe en la ficha se revisa.
/// </summary>
public class MinimoDeInventarioTests
{
    [Theory]
    [InlineData(3000L, null, true)] // Sin mínimo propio: 3 o menos.
    [InlineData(4000L, null, false)]
    [InlineData(-2000L, null, true)] // Lo que quedó en negativo también.
    [InlineData(8000L, 10000L, true)] // Con el suyo: en su mínimo o debajo.
    [InlineData(10000L, 10000L, true)]
    [InlineData(11000L, 10000L, false)]
    [InlineData(0L, 0L, true)] // Mínimo en cero: sale cuando se acaba.
    [InlineData(2000L, 0L, false)] // … aunque tenga menos de 3.
    [InlineData(1500L, 1500L, true)] // Por metro, con fracciones.
    public void Poca_existencia_con_el_minimo_propio_o_sin_el_tres_o_menos(long existencia, long? minimo, bool poca) =>
        Assert.Equal(poca, PocaExistencia.Es(new Cantidad(existencia), minimo is { } m ? new Cantidad(m) : null));

    [Fact]
    public void El_minimo_de_siempre_es_tres()
    {
        Assert.Equal(Cantidad.Piezas(3), PocaExistencia.Minimo(null));
        Assert.Equal(Cantidad.Piezas(7), PocaExistencia.Minimo(Cantidad.Piezas(7)));
    }

    [Theory]
    [InlineData(UnidadBase.Pieza, null, null)]
    [InlineData(UnidadBase.Pieza, 0L, null)]
    [InlineData(UnidadBase.Pieza, 5000L, 5000L)]
    [InlineData(UnidadBase.Pieza, null, 20000L)]
    [InlineData(UnidadBase.Metro, 1500L, 2500L)]
    public void Se_pueden_guardar(UnidadBase unidad, long? minimo, long? maximo) =>
        Variante.RevisarMinimoYMaximo(unidad, minimo is { } m ? new Cantidad(m) : null, maximo is { } x ? new Cantidad(x) : null, "Pluma");

    [Theory]
    [InlineData(UnidadBase.Pieza, -1000L, null, "El mínimo de «Pluma» no puede ser negativo.")]
    [InlineData(UnidadBase.Pieza, 1500L, null, "El mínimo de «Pluma» va en piezas enteras.")]
    [InlineData(UnidadBase.Pieza, null, 0L, "El máximo de «Pluma» va mayor a cero.")]
    [InlineData(UnidadBase.Pieza, null, 2500L, "El máximo de «Pluma» va en piezas enteras.")]
    [InlineData(UnidadBase.Metro, 3000L, 2500L, "El máximo de «Pluma» no puede ser menor que su mínimo.")]
    [InlineData(UnidadBase.Pieza, 1_000_001_000L, null, "El mínimo de «Pluma» es demasiado grande.")]
    [InlineData(UnidadBase.Pieza, null, 1_000_001_000L, "El máximo de «Pluma» es demasiado grande.")]
    public void No_se_pueden_guardar(UnidadBase unidad, long? minimo, long? maximo, string error)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Variante.RevisarMinimoYMaximo(unidad, minimo is { } m ? new Cantidad(m) : null, maximo is { } x ? new Cantidad(x) : null, "Pluma"));
        Assert.Equal(error, ex.Message);
    }
}
