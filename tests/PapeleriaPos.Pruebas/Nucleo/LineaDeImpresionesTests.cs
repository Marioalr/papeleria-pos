using System.Text.RegularExpressions;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Lo que dice la línea de impresiones (en el ticket y, en el piloto, en el renglón de eleventa): los números de Control Print, los
/// seguidos juntos y, si no cabe en sus 120 letras, «y N más» al final (2026-10-29: un lote de más de unos 14 trabajos perdía
/// números). Las copias del panel, en su propia línea.
/// </summary>
public class LineaDeImpresionesTests
{
    [Fact]
    public void Dice_cuales_son_con_los_numeros_seguidos_juntos()
    {
        Assert.Equal("Impresiones", LineaDeImpresiones.Descripcion([], ["CAJA2"], conReferencias: false));
        Assert.Equal("Impresión #1234", LineaDeImpresiones.Descripcion([1234], [], conReferencias: false));
        Assert.Equal("Impresiones #1234, #1235 y 1 de CAJA2", LineaDeImpresiones.Descripcion([1235, 1234], ["CAJA2"], conReferencias: true));
        Assert.Equal("Impresiones #1234–#1240, #1250",
            LineaDeImpresiones.Descripcion([.. Enumerable.Range(1234, 7).Select(n => (long)n), 1250], [], conReferencias: false));
        Assert.Equal("Impresiones de CAJA2 (2)", LineaDeImpresiones.Descripcion([], ["CAJA2", "CAJA2"], conReferencias: true));
    }

    [Fact]
    public void Lo_que_no_cabe_termina_en_y_N_mas()
    {
        // Cuarenta sueltos (de dos en dos) y uno de la caja 2: no caben.
        var numeros = Enumerable.Range(0, 40).Select(i => 2000L + i * 2).ToList();

        var texto = LineaDeImpresiones.Descripcion(numeros, ["CAJA2"], conReferencias: true);

        Assert.True(texto.Length <= LineaDeImpresiones.Largo, texto);
        Assert.StartsWith("Impresiones #2000, #2002, ", texto);
        var mas = Regex.Match(texto, @" y (\d+) más$");
        Assert.True(mas.Success, texto);
        // Los que se ven y los que se cuentan son todos (la caja guarda la lista completa).
        Assert.Equal(41, texto.Count(c => c == '#') + int.Parse(mas.Groups[1].Value));
    }

    [Fact]
    public void Las_copias_del_panel_van_en_su_propia_linea()
    {
        Assert.Equal("Copias", LineaDeImpresiones.Descripcion([], ["CAJA2"], conReferencias: false, copias: true));
        Assert.Equal("Copias #1240", LineaDeImpresiones.Descripcion([1240], [], conReferencias: false, copias: true));
        Assert.Equal("Copias de CAJA2", LineaDeImpresiones.Descripcion([], ["CAJA2"], conReferencias: true, copias: true));

        var venta = new Venta { TurnoId = Guid.NewGuid(), Serie = "A" };
        venta.FijarImpresiones(1500, 300);
        venta.FijarImpresiones(600, 100, copias: true);
        venta.FijarImpresiones(1800, 400);

        Assert.Equal([("Impresiones", false, 1800L), ("Copias", true, 600L)], venta.Lineas.Select(l => (l.Descripcion, l.EsCopias, l.ImporteCentavos)).OrderBy(l => l.EsCopias));
        Assert.All(venta.Lineas, l => Assert.True(l.EsImpresiones));
        venta.FijarImpresiones(0, 0, copias: true);
        Assert.Equal(["Impresiones"], venta.Lineas.Select(l => l.Descripcion));
    }
}
