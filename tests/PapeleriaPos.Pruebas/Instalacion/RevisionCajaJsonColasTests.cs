using System.Text.Json.Nodes;
using PapeleriaPos.Instalacion;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>Revisión de cb5f83f: el instalador deja ColasImpresion como está, y ColaImpresion sigue siendo lo único que escribe.</summary>
public sealed class RevisionCajaJsonColasTests
{
    [Fact]
    public void Mezclar_no_toca_ColasImpresion()
    {
        var actual = (JsonObject)JsonNode.Parse("""
            { "Servicio": "http://localhost:5080/", "CajaId": null, "Impresora": "", "ColaImpresion": "WF-6590",
              "ColasImpresion": [ { "Cola": "EPSON L5590 Series", "Impresora": 3 } ] }
            """)!;

        var objetivo = CajaJson.Mezclar(actual, new Respuestas { Rol = RolDeLaPc.Principal, Impresora = "", Cola = "Otra cola" });

        Assert.Equal("Otra cola", (string?)objetivo["ColaImpresion"]);
        Assert.Equal(actual["ColasImpresion"]!.ToJsonString(), objetivo["ColasImpresion"]!.ToJsonString());
    }
}
