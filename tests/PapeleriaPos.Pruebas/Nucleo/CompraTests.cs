using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Nucleo;

public class CompraTests
{
    private static readonly DateTime Ahora = new(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Pluma = Guid.NewGuid(), PlumaPieza = Guid.NewGuid(), PlumaCaja = Guid.NewGuid();
    private static readonly Guid Goma = Guid.NewGuid(), GomaPieza = Guid.NewGuid();
    private static readonly Cantidad Una = Cantidad.Piezas(1), Doce = Cantidad.Piezas(12);

    private static Compra Contada()
    {
        var compra = new Compra { Folio = 1, AbiertaUtc = Ahora };
        compra.Contar(Pluma, PlumaCaja, Cantidad.Piezas(2), Doce);
        compra.Contar(Pluma, PlumaPieza, Cantidad.Piezas(3), Una);
        compra.Contar(Goma, GomaPieza, Cantidad.Piezas(5), Una);
        compra.TerminarConteo(Ahora);
        return compra;
    }

    [Fact]
    public void Lo_contado_en_cajas_y_piezas_se_junta_en_unidad_base_y_se_compara_con_la_nota()
    {
        var compra = Contada();
        compra.Renglon(null, Pluma, PlumaCaja, Cantidad.Piezas(3), Doce, 27000, "Pluma (Caja)", "P-100");

        var comparacion = ComparacionCompra.Comparar(compra.Conteo, compra.Renglones);

        var pluma = comparacion.Single(c => c.VarianteId == Pluma);
        Assert.Equal(Cantidad.Piezas(36), pluma.Nota);
        Assert.Equal(Cantidad.Piezas(27), pluma.Contado);
        Assert.Equal(DiferenciaCompra.Falto, pluma.Tipo);
        Assert.Equal(750m, pluma.CostoUnitario);
        // Lo que faltó no entra ni se paga: se reclama.
        Assert.Equal(20250, pluma.CostoDeEntrada(0));
        var goma = comparacion.Single(c => c.VarianteId == Goma);
        Assert.Equal(DiferenciaCompra.NoVieneEnLaNota, goma.Tipo);
        Assert.Null(goma.CostoUnitario);
        Assert.Equal(1500, goma.CostoDeEntrada(300));
    }

    [Fact]
    public void Quien_cuenta_no_puede_capturar_la_nota_y_cerrar_pide_la_nota()
    {
        var compra = new Compra { Folio = 1, AbiertaUtc = Ahora };
        compra.Contar(Pluma, PlumaPieza, Cantidad.Piezas(4), Una);

        Assert.Throws<InvalidOperationException>(() => compra.Renglon(null, Pluma, PlumaPieza, Una, Una, 100, "Pluma", null));
        Assert.Throws<InvalidOperationException>(() => compra.Cerrar(Guid.NewGuid(), Ahora));
        compra.TerminarConteo(Ahora);
        Assert.Throws<InvalidOperationException>(() => compra.Contar(Pluma, PlumaPieza, Una, Una));
        Assert.Throws<InvalidOperationException>(() => compra.Cerrar(Guid.NewGuid(), Ahora));

        compra.Renglon(null, Pluma, PlumaPieza, Cantidad.Piezas(4), Una, 1000, "Pluma", null);
        compra.Cerrar(Guid.NewGuid(), Ahora);
        Assert.Equal(EstadoCompra.Cerrada, compra.Estado);
        Assert.Throws<InvalidOperationException>(() => compra.Cancelar("Error", Ahora));
    }

    [Fact]
    public void Contar_cero_quita_la_presentacion_y_un_renglon_se_puede_corregir()
    {
        var compra = new Compra { Folio = 1, AbiertaUtc = Ahora };
        compra.Contar(Pluma, PlumaPieza, Cantidad.Piezas(4), Una);
        compra.Contar(Goma, GomaPieza, Cantidad.Piezas(1), Una);
        compra.Contar(Goma, GomaPieza, Cantidad.Cero, Una);
        compra.TerminarConteo(Ahora);

        var renglon = compra.Renglon(null, Pluma, PlumaPieza, Cantidad.Piezas(4), Una, 1000, "Pluma", null);
        compra.Renglon(renglon.Id, Pluma, PlumaPieza, Cantidad.Piezas(4), Una, 1200, "Pluma", null);

        Assert.Equal(Pluma, Assert.Single(compra.Conteo).VarianteId);
        Assert.Equal(1200, Assert.Single(compra.Renglones).ImporteCentavos);
        Assert.Equal(DiferenciaCompra.Ninguna, Assert.Single(ComparacionCompra.Comparar(compra.Conteo, compra.Renglones)).Tipo);
    }

    [Fact]
    public void Con_orden_se_compara_tambien_lo_pedido_y_lo_que_no_vino_queda_como_no_llego()
    {
        var orden = new OrdenCompra { Folio = 3, CreadaUtc = Ahora };
        orden.Pedir(Pluma, PlumaCaja, Cantidad.Piezas(3), Doce);
        orden.Pedir(Goma, GomaPieza, Cantidad.Piezas(10), Una);
        orden.Pedir(Goma, GomaPieza, Cantidad.Piezas(6), Una);
        var compra = new Compra { Folio = 1, AbiertaUtc = Ahora };
        compra.Contar(Pluma, PlumaCaja, Cantidad.Piezas(3), Doce);
        compra.TerminarConteo(Ahora);
        compra.Renglon(null, Pluma, PlumaCaja, Cantidad.Piezas(3), Doce, 27000, "Pluma", null);

        var comparacion = ComparacionCompra.Comparar(compra.Conteo, compra.Renglones, orden.Renglones);
        orden.Recibir(compra.Id);

        Assert.Equal((Cantidad.Piezas(36), DiferenciaCompra.Ninguna), (comparacion.Single(c => c.VarianteId == Pluma).Pedido, comparacion.Single(c => c.VarianteId == Pluma).Tipo));
        var goma = comparacion.Single(c => c.VarianteId == Goma);
        Assert.Equal((Cantidad.Piezas(6), DiferenciaCompra.NoLlego), (goma.Pedido, goma.Tipo));
        Assert.Equal((EstadoOrden.Recibida, compra.Id), (orden.Estado, orden.CompraId));
        Assert.Throws<InvalidOperationException>(() => orden.Pedir(Goma, GomaPieza, Una, Una));
        Assert.Throws<InvalidOperationException>(() => orden.Recibir(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(1500, 750, 5000)]
    [InlineData(1500, 1200, 2000)]
    [InlineData(1000, 1000, 0)]
    [InlineData(1000, 1100, -1000)]
    public void El_margen_es_sobre_el_precio(long precio, long costo, int centesimas) =>
        Assert.Equal(centesimas, Margen.Calcular(precio, costo));

    [Theory]
    [InlineData(1200, 3000, 1750)] // 12 ÷ 0.7 = 17.14 → 17.50
    [InlineData(700, 3000, 1000)] // 7 ÷ 0.7 = 10 exactos
    [InlineData(69.44, 3000, 100)] // una pieza de una caja de 144 por $100
    public void El_precio_sugerido_deja_el_margen_minimo_y_sube_a_50_centavos(decimal costo, int minimo, long sugerido)
    {
        Assert.Equal(sugerido, Margen.PrecioSugerido(costo, minimo));
        Assert.True(Margen.Calcular(sugerido, costo) >= minimo);
    }
}
