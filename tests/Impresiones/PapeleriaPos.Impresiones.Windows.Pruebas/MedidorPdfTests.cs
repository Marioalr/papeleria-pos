using System.Text;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Windows.Pruebas;

/// <summary>Cotizar: el PDF se mide con el motor de PDF de Windows, igual que una impresión.</summary>
public class MedidorPdfTests
{
    /// <summary>Un PDF carta mínimo; cada página es su lista de operaciones de dibujo (en puntos, 72 por pulgada).</summary>
    private static byte[] Pdf(params string[] paginas)
    {
        var objetos = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", paginas.Select((_, i) => $"{3 + i * 2} 0 R"))}] /Count {paginas.Length} >>",
        };
        foreach (var contenido in paginas)
        {
            objetos.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {objetos.Count + 2} 0 R >>");
            objetos.Add($"<< /Length {contenido.Length} >>\nstream\n{contenido}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
        var posiciones = new List<int>();
        for (var i = 0; i < objetos.Count; i++)
        {
            posiciones.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objetos[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objetos.Count + 1}\n0000000000 65535 f \n");
        foreach (var p in posiciones)
            pdf.Append($"{p:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objetos.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    [Fact]
    public async Task Mide_cada_pagina_como_una_impresion()
    {
        // Página 1: un cuadro negro de una pulgada. Página 2: un cuadro rojo de 2 × 2 pulgadas.
        var pdf = Pdf("0 0 0 rg 72 360 72 72 re f", "1 0 0 rg 72 360 144 144 re f");
        var avances = new List<AvanceMedicion>();

        var medido = await new MedidorPdfWindows().MedirAsync(new MemoryStream(pdf), new Progreso(avances));

        Assert.Equal(2, medido.Paginas.Count);
        var negro = medido.Paginas[0];
        Assert.InRange(negro.K * negro.AreaSqIn, 0.97, 1.03);
        Assert.False(negro.HasColor);
        var rojo = medido.Paginas[1];
        Assert.True(rojo.HasColor);
        Assert.InRange(rojo.M * rojo.AreaSqIn, 3.9, 4.1);
        Assert.InRange(rojo.C * rojo.AreaSqIn, 0, 0.05);
        Assert.NotNull(medido.MiniaturaPng);
        // El avance trae lo medido hasta ahí (2026-11-08): con eso Cotizar da el precio mientras sigue midiendo.
        Assert.Equal([(0, 2, 0), (1, 2, 1), (2, 2, 2)], avances.Select(a => (a.Hechas, a.Total, a.Medidas.Count)));
        Assert.Equal(negro, avances[1].Medidas[0]);
        Assert.Null(medido.Nota);
        // Con el tamaño de la página (carta), para ajustarla a la hoja como al imprimirla.
        Assert.Equal((8.5, 11.0), (Math.Round(negro.WidthIn!.Value, 2), Math.Round(negro.HeightIn!.Value, 2)));
    }

    /// <summary>
    /// Una página A4 se cotiza en carta como la imprime el programa con «Ajustar» (2026-11-08): reducida al área imprimible, su tinta es
    /// menos que la de la página completa (antes se suponía que llenaba la carta y salía de más).
    /// </summary>
    [Fact]
    public async Task Una_pagina_que_no_es_carta_se_ajusta_a_la_hoja()
    {
        // Un cuadro negro de 2 × 2 pulgadas en una página A4 (595 × 842 puntos).
        var pdf = Pdf("0 0 0 rg 72 360 144 144 re f").ToList();
        var texto = Encoding.ASCII.GetString(pdf.ToArray()).Replace("/MediaBox [0 0 612 792]", "/MediaBox [0 0 595 842]");
        var medido = await new MedidorPdfWindows().MedirAsync(new MemoryStream(Encoding.ASCII.GetBytes(Corregir(texto))));

        var pagina = medido.Paginas[0];
        Assert.InRange(pagina.K * pagina.WidthIn!.Value * pagina.HeightIn!.Value, 3.9, 4.1);
        var area = QuoteCalculator.PrintedArea(pagina, (215.9 - 6) / 25.4, (279.4 - 6) / 25.4);
        // Reducida a 273.4 / 297 del alto: el cuadro queda en unas 4 × 0.847 in².
        Assert.InRange(pagina.K * area, 3.25, 3.55);
    }

    /// <summary>Al cambiar la MediaBox cambian las posiciones de los objetos: se vuelve a escribir la tabla xref.</summary>
    private static string Corregir(string pdf)
    {
        var inicioXref = pdf.IndexOf("xref\n", StringComparison.Ordinal);
        var cuerpo = pdf[..inicioXref];
        var posiciones = new List<int>();
        for (var i = 1; ; i++)
        {
            var p = cuerpo.IndexOf($"{i} 0 obj\n", StringComparison.Ordinal);
            if (p < 0)
                break;
            posiciones.Add(p);
        }
        var resto = new StringBuilder(cuerpo);
        resto.Append($"xref\n0 {posiciones.Count + 1}\n0000000000 65535 f \n");
        foreach (var p in posiciones)
            resto.Append($"{p:D10} 00000 n \n");
        resto.Append($"trailer\n<< /Size {posiciones.Count + 1} /Root 1 0 R >>\nstartxref\n{cuerpo.Length}\n%%EOF\n");
        return resto.ToString();
    }

    [Fact]
    public async Task Lo_que_no_es_pdf_se_explica()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MedidorPdfWindows().MedirAsync(new MemoryStream(Encoding.ASCII.GetBytes("hola, no soy un PDF"))));
        Assert.Contains("no es un PDF", ex.Message);
    }

    /// <summary>Progress&lt;T&gt; avisa en otro hilo; este anota en el mismo, para revisarlo al terminar.</summary>
    private sealed class Progreso(List<AvanceMedicion> avances) : IProgress<AvanceMedicion>
    {
        public void Report(AvanceMedicion valor) => avances.Add(valor);
    }
}
