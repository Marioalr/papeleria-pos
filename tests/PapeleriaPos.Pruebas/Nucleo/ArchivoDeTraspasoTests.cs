using System.Text;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// El archivo con que viaja un traspaso (2026-11-26, provisional): se escribe y se lee igual, su huella cambia con cualquier cosa
/// que cambie (y no con los espacios), y lo que no es un traspaso, lo de una versión más nueva o lo alterado no se lee.
/// </summary>
public sealed class ArchivoDeTraspasoTests
{
    private static ContenidoDeTraspaso Contenido(long milesimas = 12_000, string? nota = "Para la «apertura»") => new(
        Guid.Parse("01a21a50-f550-722d-a3b5-0b80a8b1ea52"), "Matriz", "Sucursal 2", new DateTime(2026, 11, 26, 18, 30, 15, 250, DateTimeKind.Utc), "Luis", nota,
        [new RenglonDeArchivo(["7501234567890", "LAP-01"], "Lápiz del n.º 2 «Mirado»", UnidadBase.Pieza, milesimas), new RenglonDeArchivo([], "Listón", UnidadBase.Metro, 1_500)]);

    [Fact]
    public void Se_escribe_y_se_lee_igual_con_su_huella_y_su_clave()
    {
        var texto = ArchivoDeTraspaso.Escribir(Contenido());
        var (leido, huella) = ArchivoDeTraspaso.Leer(Encoding.UTF8.GetBytes(texto));

        Assert.Equal(ArchivoDeTraspaso.Huella(Contenido()), huella);
        Assert.Equal(64, huella.Length);
        Assert.Equal($"{huella[..4]}-{huella[4..8]}", ArchivoDeTraspaso.Clave(huella));
        // La fecha, al segundo; lo demás, tal cual (también los acentos y las comillas, que se leen a simple vista).
        Assert.Equal(new DateTime(2026, 11, 26, 18, 30, 15, DateTimeKind.Utc), leido.Fecha);
        Assert.Equal(("Matriz", "Sucursal 2", "Luis", "Para la «apertura»", 2), (leido.Origen, leido.Destino, leido.Quien, leido.Nota, leido.Renglones.Count));
        Assert.Equal(["7501234567890", "LAP-01"], leido.Renglones[0].Codigos);
        Assert.Equal(("Lápiz del n.º 2 «Mirado»", UnidadBase.Pieza, 12_000L), (leido.Renglones[0].Nombre, leido.Renglones[0].Unidad, leido.Renglones[0].Milesimas));
        Assert.Contains("Lápiz del n.º 2 «Mirado»", texto);
        Assert.Contains("\"unidad\": \"Metro\"", texto);
        Assert.Equal($"Traspaso a Sucursal 2 2026-11-26 {ArchivoDeTraspaso.Clave(huella)}.traspaso.json", ArchivoDeTraspaso.Nombre(Contenido(), new DateOnly(2026, 11, 26)));
    }

    [Fact]
    public void La_huella_no_cambia_con_los_espacios_y_si_con_lo_que_dice()
    {
        var texto = ArchivoDeTraspaso.Escribir(Contenido());
        // Guardado otra vez con otros espacios (o con la marca del Bloc de notas) es el mismo.
        var sinSangria = texto.Replace("\n", "").Replace("  ", "");
        Assert.Equal(ArchivoDeTraspaso.Huella(Contenido()), ArchivoDeTraspaso.Leer(Encoding.UTF8.GetBytes(sinSangria)).Huella);
        Assert.Equal(ArchivoDeTraspaso.Huella(Contenido()), ArchivoDeTraspaso.Leer([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(texto)]).Huella);

        Assert.NotEqual(ArchivoDeTraspaso.Huella(Contenido()), ArchivoDeTraspaso.Huella(Contenido(milesimas: 13_000)));
        Assert.NotEqual(ArchivoDeTraspaso.Huella(Contenido()), ArchivoDeTraspaso.Huella(Contenido(nota: null)));
        Assert.Equal(ArchivoDeTraspaso.Huella(Contenido(nota: null)), ArchivoDeTraspaso.Huella(Contenido(nota: "  ")));
    }

    [Theory]
    [InlineData("\"milesimas\": 12000", "\"milesimas\": 120000")]
    [InlineData("\"destino\": \"Sucursal 2\"", "\"destino\": \"Sucursal 3\"")]
    [InlineData("7501234567890", "7501234567891")]
    [InlineData("\"milesimas\": 1500", "\"milesimas\": -1500")]
    public void Lo_alterado_no_se_lee(string antes, string despues)
    {
        var texto = ArchivoDeTraspaso.Escribir(Contenido());
        Assert.Contains(antes, texto);
        var error = Assert.Throws<ArchivoDeTraspasoInvalido>(() => ArchivoDeTraspaso.Leer(Encoding.UTF8.GetBytes(texto.Replace(antes, despues))));
        Assert.Equal(ArchivoDeTraspaso.Alterado, error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es json")]
    [InlineData("null")]
    [InlineData("[1, 2]")]
    [InlineData("{\"formato\": \"otra-cosa\", \"version\": 1}")]
    public void Lo_que_no_es_un_traspaso_se_dice(string texto) =>
        Assert.Equal(ArchivoDeTraspaso.NoEs, Assert.Throws<ArchivoDeTraspasoInvalido>(() => ArchivoDeTraspaso.Leer(Encoding.UTF8.GetBytes(texto))).Message);

    [Fact]
    public void Una_version_mas_nueva_pide_actualizar_y_uno_sin_renglones_no_sirve()
    {
        var texto = ArchivoDeTraspaso.Escribir(Contenido());
        var nueva = Assert.Throws<ArchivoDeTraspasoInvalido>(() => ArchivoDeTraspaso.Leer(Encoding.UTF8.GetBytes(texto.Replace("\"version\": 1", "\"version\": 2"))));
        Assert.Contains("actualiza el POS de esta sucursal", nueva.Message);

        var vacio = ArchivoDeTraspaso.Escribir(Contenido() with { Renglones = [] });
        Assert.Equal(ArchivoDeTraspaso.Alterado, Assert.Throws<ArchivoDeTraspasoInvalido>(() => ArchivoDeTraspaso.Leer(Encoding.UTF8.GetBytes(vacio))).Message);
        Assert.Contains("pesa demasiado", Assert.Throws<ArchivoDeTraspasoInvalido>(() => ArchivoDeTraspaso.Leer(new byte[ArchivoDeTraspaso.BytesMaximos + 1])).Message);
    }
}
