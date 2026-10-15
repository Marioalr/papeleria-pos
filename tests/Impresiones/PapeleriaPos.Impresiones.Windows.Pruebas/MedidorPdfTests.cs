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
        var avances = new List<(int, int)>();

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
        Assert.Equal((2, 2), avances[^1]);
        Assert.Null(medido.Nota);
    }

    [Fact]
    public async Task Lo_que_no_es_pdf_se_explica()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MedidorPdfWindows().MedirAsync(new MemoryStream(Encoding.ASCII.GetBytes("hola, no soy un PDF"))));
        Assert.Contains("no es un PDF", ex.Message);
    }

    /// <summary>Progress&lt;T&gt; avisa en otro hilo; este anota en el mismo, para revisarlo al terminar.</summary>
    private sealed class Progreso(List<(int, int)> avances) : IProgress<(int Hechas, int Total)>
    {
        public void Report((int Hechas, int Total) valor) => avances.Add(valor);
    }
}
