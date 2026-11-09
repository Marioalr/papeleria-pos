using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// La regla de las variantes de UPC (decidida el 2026-10-11, la de la app del celular): tal cual; el UPC-A de 12 que empieza
/// con 0 sin ese 0; el EAN-13 que empieza con 0 como UPC-A de 12 y de 11; el UPC-E expandido. Nada más.
/// </summary>
public sealed class VariantesDeCodigoTests
{
    private static readonly Guid A = Ids.Nuevo(), B = Ids.Nuevo(), C = Ids.Nuevo();
    private static readonly Guid PiezaA = Ids.Nuevo(), PiezaB = Ids.Nuevo(), PiezaC = Ids.Nuevo(), CajaA = Ids.Nuevo();

    private static CodigosDeEscaneo Catalogo(params (string Codigo, Guid Variante, Guid Presentacion)[] codigos) =>
        new(codigos.Select(c => new CodigoLocalDto(c.Codigo, c.Variante, c.Presentacion)));

    [Theory]
    // UPC-A de 12 que empieza con 0: sin ese 0.
    [InlineData("036000291452", "36000291452", VarianteDeCodigo.UpcA11)]
    // EAN-13 que empieza con 0: como UPC-A de 12…
    [InlineData("0036000291452", "036000291452", VarianteDeCodigo.Ean13AUpcA)]
    // …y, si ese empieza con 0, de 11.
    [InlineData("0036000291452", "36000291452", VarianteDeCodigo.Ean13AUpcA11)]
    // UPC-E expandido a su UPC-A de 12.
    [InlineData("04252614", "042100005264", VarianteDeCodigo.UpcEAUpcA)]
    public void Cada_variante_encuentra_el_codigo_del_catalogo(string escaneado, string enElCatalogo, VarianteDeCodigo variante)
    {
        var coincidencia = Catalogo((enElCatalogo, A, PiezaA)).Escanear(escaneado);

        Assert.NotNull(coincidencia);
        Assert.Equal(enElCatalogo, coincidencia.Codigo!.Codigo);
        Assert.Equal(variante, coincidencia.Variante);
        Assert.False(coincidencia.EsAmbigua);
    }

    [Fact]
    public void Las_variantes_salen_en_orden()
    {
        Assert.Equal([("036000291452", VarianteDeCodigo.Ean13AUpcA), ("36000291452", VarianteDeCodigo.Ean13AUpcA11)],
            VariantesDeCodigo.De("0036000291452").ToList());
        Assert.Equal([("12345678905", VarianteDeCodigo.UpcA11)], VariantesDeCodigo.De("012345678905").ToList());
    }

    [Theory]
    // Con el verificador mal no se prueba ninguna variante.
    [InlineData("036000291453")]
    [InlineData("0036000291453")]
    // Un EAN-13 que no empieza con 0 (los de México, 750…), un EAN-8 que no es UPC-E, un código de la tienda y uno corto.
    [InlineData("7501031311309")]
    [InlineData("96385074")]
    [InlineData("LAP-AZ")]
    [InlineData("0012345")]
    // Un UPC-E cuyo verificador no es el de su expansión.
    [InlineData("04252615")]
    public void Sin_variantes_lo_que_no_es_un_upc_valido(string codigo) => Assert.Empty(VariantesDeCodigo.De(codigo));

    [Fact]
    public void No_completa_digitos_ni_quita_ceros_a_otros_codigos()
    {
        // El catálogo tiene el UPC-A de 12 («0…»); escanear el de 11 no lo encuentra: la regla no completa dígitos.
        Assert.Null(Catalogo(("036000291452", A, PiezaA)).Escanear("36000291452"));
        // Ni lo guardado con 13 desde una lectura de 12.
        Assert.Null(Catalogo(("0036000291452", A, PiezaA)).Escanear("036000291452"));
        // Un código de la tienda con ceros adelante no se encuentra sin ellos.
        Assert.Null(Catalogo(("123", A, PiezaA)).Escanear("000123"));
        // Ni un código del catálogo con el verificador mal por una variante.
        Assert.Null(Catalogo(("36000291453", A, PiezaA)).Escanear("036000291453"));
    }

    [Fact]
    public void El_codigo_tal_cual_siempre_gana()
    {
        var catalogo = Catalogo(("0036000291452", C, PiezaC), ("036000291452", A, PiezaA), ("36000291452", B, PiezaB));

        var coincidencia = catalogo.Escanear("0036000291452");

        Assert.Equal(C, coincidencia!.Codigo!.VarianteId);
        Assert.Equal(VarianteDeCodigo.TalCual, coincidencia.Variante);
        // Y el de 12, tal cual, aunque el de 11 sea de otro producto.
        Assert.Equal(A, catalogo.BuscarAlEscanear("036000291452")!.VarianteId);
    }

    [Fact]
    public void Si_las_variantes_dan_con_dos_productos_no_se_adivina()
    {
        var catalogo = Catalogo(("036000291452", A, PiezaA), ("36000291452", B, PiezaB));

        var coincidencia = catalogo.Escanear("0036000291452");

        Assert.NotNull(coincidencia);
        Assert.True(coincidencia.EsAmbigua);
        Assert.Null(coincidencia.Codigo);
        Assert.Equal([A, B], coincidencia.Ambiguos.Select(c => c.VarianteId).ToList());
        Assert.Null(catalogo.BuscarAlEscanear("0036000291452"));
    }

    [Fact]
    public void Si_dan_con_otra_presentacion_del_mismo_producto_tampoco()
    {
        // La pieza y la caja de 12 del mismo producto: cobrar una por otra cambia la cantidad.
        Assert.Null(Catalogo(("036000291452", A, PiezaA), ("36000291452", A, CajaA)).BuscarAlEscanear("0036000291452"));
        // Con la misma presentación por los dos caminos, es ese.
        Assert.Equal(PiezaA, Catalogo(("036000291452", A, PiezaA), ("36000291452", A, PiezaA)).BuscarAlEscanear("0036000291452")!.PresentacionId);
    }

    [Fact]
    public void Gtin_y_upc_e()
    {
        Assert.True(Gtin.EsValido("7501031311309"));
        Assert.True(Gtin.EsValido("96385074"));
        Assert.False(Gtin.EsValido("7501031311308"));
        Assert.False(Gtin.EsValido("75010313113A9"));
        Assert.Equal("042100005264", Gtin.UpcEAUpcA("04252614"));
        // Sistema 2: no es UPC-E.
        Assert.Null(Gtin.UpcEAUpcA("24252614"));
    }
}
