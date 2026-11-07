using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

public class TextoTests
{
    [Theory]
    [InlineData("Bic", "BIC")]
    [InlineData("  pelikán  ", "PELIKAN")]
    [InlineData("Baco   Ñandú", "BACO ÑANDU")]
    // Los acentos sueltos que quedan en un nombre (2026-10-24): sin ellos, y sin dejar dos espacios.
    [InlineData("Lapiz¨ Azul", "LAPIZ AZUL")]
    [InlineData("Mu´ñeca  ´ grande", "MUÑECA GRANDE")]
    [InlineData("Cinta`  Canela^", "CINTA CANELA")]
    public void Normalizar_ignora_mayusculas_acentos_y_espacios(string texto, string esperado)
    {
        Assert.Equal(esperado, Texto.Normalizar(texto));
    }

    // Un emoji o letras de adorno pegadas de WhatsApp: tronaban y tiraban todo el catálogo del servicio (auditoría, 2026-11-04).
    [Theory]
    [InlineData("Mochila 🎒 escolar", "MOCHILA 🎒 ESCOLAR")]
    [InlineData("𝓒𝓾𝓪𝓭𝓮𝓻𝓷𝓸", "𝓒𝓾𝓪𝓭𝓮𝓻𝓷𝓸")]
    [InlineData("Medio \ud83c par", "MEDIO \ud83c PAR")]
    [InlineData("Raro \ufffe\uffff", "RARO \ufffe\uffff")]
    public void Normalizar_no_truena_con_lo_que_no_es_una_letra_sola(string texto, string esperado)
    {
        Assert.Equal(esperado, Texto.Normalizar(texto));
    }

    /// <summary>Un solo nombre no tira el índice, que necesita todo el catálogo para cobrar: ni con emojis, ni con un número de 30 cifras.</summary>
    [Theory]
    [InlineData("Mochila 🎒 escolar")]
    [InlineData("Cuaderno 𝓒𝓾𝓪𝓭𝓻𝓸 100 h")]
    [InlineData("Medio \ud83c par")]
    [InlineData("Cinta 123456789012345678901234567890 cm")]
    [InlineData("Hoja 123456789012345678901234567890x99999999999999999999999999999999 mm")]
    [InlineData("123456789012345678901234567890")]
    [InlineData("Lona 99999999999999999999999999999999999999.5m")]
    public void Un_nombre_raro_no_tira_el_indice_de_busqueda(string nombre)
    {
        var id = Guid.NewGuid();
        var indice = IndiceDeBusqueda.Armar(
            [new ProductoParaBuscar(id, nombre, "C1", ["C1"]), new ProductoParaBuscar(Guid.NewGuid(), "Lápiz 2B", "C2", ["C2"])], null, null);

        Assert.Contains(indice.Buscar("C1", 5), a => a.Articulo.VarianteId == id);
    }

    /// <summary>Con la semilla fija: nombres con cualquier carácter y las formas de número que lee el lector de nombres.</summary>
    [Fact]
    public void Ningun_nombre_tira_el_indice_de_busqueda()
    {
        var azar = new Random(20261104);
        string[] pedazos = ["100", "1/2", "3.5", "99999999999999999999999999999999", "x", "cm", "mm", "kg", "h", "n", "raya", "🎒", " ", " ", " ", "#", "/", "-", ".", ","];
        var productos = Enumerable.Range(0, 2000).Select(i =>
        {
            var nombre = new System.Text.StringBuilder();
            for (var n = azar.Next(1, 8); n > 0; n--)
                nombre.Append(azar.Next(4) == 0 ? ((char)azar.Next(0x10000)).ToString() : pedazos[azar.Next(pedazos.Length)]);
            return new ProductoParaBuscar(Guid.NewGuid(), nombre.ToString(), $"C{i}", [$"C{i}"]);
        }).ToList();

        Assert.NotNull(IndiceDeBusqueda.Armar(productos, null, null));
    }

    [Fact]
    public void La_marca_guarda_su_nombre_normalizado()
    {
        var marca = new Marca { Nombre = " Pelikán " };

        Assert.Equal("Pelikán", marca.Nombre);
        Assert.Equal("PELIKAN", marca.NombreNormalizado);
    }
}
