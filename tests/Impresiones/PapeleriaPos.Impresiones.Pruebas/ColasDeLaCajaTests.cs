using System.Text.Json;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Las colas que vigila el agente de una caja, de su caja.json (2026-11-03): «ColaImpresion» sigue igual (una cola, de la primera
/// impresora; la caja 2 del piloto no cambia nada) y «ColasImpresion» agrega varias, cada una con el número de su impresora. Un error de
/// dedo no impide abrir la caja: se ignora y se dice.
/// </summary>
public class ColasDeLaCajaTests
{
    private static JsonElement Json(string texto) => JsonDocument.Parse(texto).RootElement;

    [Fact]
    public void Con_solo_ColaImpresion_vigila_esa_cola_de_la_primera_impresora_como_siempre()
    {
        var (colas, avisos) = ColasDeLaCaja.Leer(" EPSON WF-6590 Series ", null);

        Assert.Equal([new ColaVigilada("EPSON WF-6590 Series")], colas);
        Assert.Empty(avisos);
        Assert.Empty(ColasDeLaCaja.Leer(null, null).Colas);
        Assert.Empty(ColasDeLaCaja.Leer("  ", Json("null")).Colas);
    }

    [Fact]
    public void Con_varias_cada_una_lleva_su_impresora_y_ColaImpresion_va_primero()
    {
        var (colas, avisos) = ColasDeLaCaja.Leer("EPSON WF-6590 Series", Json("""
            [
              { "Cola": "EPSON L5590 Series", "Impresora": 3 },
              { "cola": "WF-6590 (2)", "impresora": "2" },
              { "Cola": "epson wf-6590 series", "Impresora": 1 }
            ]
            """));

        // La de ColaImpresion también estaba en la lista: con su número, y primero (la usan los trámites).
        Assert.Equal([new ColaVigilada("EPSON WF-6590 Series", 1), new ColaVigilada("EPSON L5590 Series", 3), new ColaVigilada("WF-6590 (2)", 2)], colas);
        Assert.Empty(avisos);
    }

    [Fact]
    public void Lo_que_no_se_entiende_se_ignora_y_se_dice_sin_tronar()
    {
        var (colas, avisos) = ColasDeLaCaja.Leer(null, Json("""
            [
              "EPSON L5590 Series",
              { "Impresora": 2 },
              { "Cola": "WF-6590 (2)", "Impresora": "dos" },
              { "Cola": "WF-6590 (3)", "Impresora": 0 },
              { "Cola": "Otra", "Impresora": 4 },
              { "Cola": "otra", "Impresora": 5 }
            ]
            """));

        Assert.Equal([new ColaVigilada("WF-6590 (2)"), new ColaVigilada("WF-6590 (3)"), new ColaVigilada("Otra", 4)], colas);
        Assert.Equal(5, avisos.Count);
        Assert.Contains(avisos, a => a.Contains("renglón 1"));
        Assert.Contains(avisos, a => a.Contains("falta el nombre de la cola"));
        Assert.Contains(avisos, a => a.Contains("«WF-6590 (2)»: «Impresora» es el número"));
        Assert.Contains(avisos, a => a.Contains("dos veces"));
        Assert.Single(ColasDeLaCaja.Leer("Una", Json("""{ "Cola": "Otra" }""")).Avisos, a => a.Contains("no es una lista"));
    }
}
