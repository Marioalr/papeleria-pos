using System.Text;
using System.Text.Json.Nodes;
using PapeleriaPos.Instalacion;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// servicio.json cambiado sin volverlo a escribir (el instalador, docs/instalacion.md): solo lo que cambió, y lo demás —los comentarios, el
/// orden, los espacios, el BOM y los saltos del Bloc de notas— se queda tal cual.
/// </summary>
public sealed class TextoJsoncTests
{
    private static TextoJsonc Texto(string texto, bool bom = false) =>
        new(bom ? [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(texto)] : Encoding.UTF8.GetBytes(texto));

    private static JsonObject Objeto(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>Lo que queda después de igualarlo, ya revisado: dice lo mismo que lo pedido.</summary>
    private static string Igualar(string texto, Func<JsonObject, JsonObject> cambiar)
    {
        var archivo = Texto(texto);
        var objetivo = cambiar((JsonObject)archivo.Objeto().DeepClone());
        archivo.Igualar(objetivo);
        Assert.True(Json.Iguales(archivo.Objeto(), objetivo));
        return archivo.Texto;
    }

    [Fact]
    public void En_la_plantilla_las_secciones_nuevas_van_antes_de_la_llave_y_sus_comentarios_se_quedan()
    {
        const string plantilla = """
            // La configuración de ESTA PC.
            {
              // El puente con eleventa:
              // "Eleventa": {
              //   "Activo": true
              // }
            }

            """;

        var quedo = Igualar(plantilla, o =>
        {
            o["Eleventa"] = new JsonObject { ["Activo"] = true, ["Servidor"] = "192.168.1.106" };
            o["Instalacion"] = new JsonObject { ["UnaSolaCaja"] = true };
            return o;
        });

        Assert.Equal("""
            // La configuración de ESTA PC.
            {
              // El puente con eleventa:
              // "Eleventa": {
              //   "Activo": true
              // }
              "Eleventa": {
                "Activo": true,
                "Servidor": "192.168.1.106"
              },
              "Instalacion": {
                "UnaSolaCaja": true
              }
            }

            """, quedo);
    }

    [Fact]
    public void Cambiar_un_valor_deja_todo_lo_demas_igual_byte_por_byte_con_el_bom_y_los_saltos_del_bloc_de_notas()
    {
        var antes = "// Lo de la caja 2.\r\n{\r\n  \"Eleventa\": {\r\n    \"Activo\": true, // leer\r\n    \"Servidor\": \"192.168.1.106\",\r\n"
            + "    /* la base real */ \"Base\": \"C:\\\\Program Files (x86)\\\\AbarrotesMultiCaja\\\\db\\\\PDVDATA.FDB\"\r\n  },\r\n"
            + "  \"Registro\": { \"Dias\": 45 }\r\n}\r\n";
        var archivo = Texto(antes, bom: true);
        var objetivo = (JsonObject)archivo.Objeto().DeepClone();
        objetivo["Eleventa"]!["Servidor"] = "192.168.1.50";

        archivo.Igualar(objetivo);

        Assert.Equal(antes.Replace("192.168.1.106", "192.168.1.50"), archivo.Texto);
        Assert.Equal([0xEF, 0xBB, 0xBF], archivo.Bytes[..3]);
    }

    [Fact]
    public void Una_clave_nueva_va_al_final_de_su_seccion_con_la_coma_antes_del_comentario_del_renglon()
    {
        var quedo = Igualar("{\n  \"Eleventa\": {\n    \"Activo\": true, // leer\n    \"Escribir\": false // todavía no\n  }\n}\n", o =>
        {
            o["Eleventa"]!["Gbak"] = @"C:\ProgramData\PapeleriaPos\firebird20\bin\gbak.exe";
            return o;
        });

        Assert.Equal("{\n  \"Eleventa\": {\n    \"Activo\": true, // leer\n    \"Escribir\": false, // todavía no\n"
            + "    \"Gbak\": \"C:\\\\ProgramData\\\\PapeleriaPos\\\\firebird20\\\\bin\\\\gbak.exe\"\n  }\n}\n", quedo);
    }

    [Fact]
    public void Si_la_ultima_ya_trae_una_coma_de_mas_la_nueva_va_despues_de_ella()
    {
        var quedo = Igualar("{\n  \"Registro\": { \"Dias\": 45 },\n}\n", o =>
        {
            o["ControlPrint"] = new JsonObject { ["Servidor"] = "192.168.1.106" };
            return o;
        });

        Assert.Equal("{\n  \"Registro\": { \"Dias\": 45 },\n  \"ControlPrint\": {\n    \"Servidor\": \"192.168.1.106\"\n  }\n}\n", quedo);
    }

    [Fact]
    public void En_una_seccion_vacia_de_un_renglon_la_clave_va_adentro()
    {
        var quedo = Igualar("{ \"Instalacion\": {} }", o =>
        {
            o["Instalacion"]!["UnaSolaCaja"] = true;
            return o;
        });

        Assert.Equal("{ \"Instalacion\": {\n  \"UnaSolaCaja\": true\n} }", quedo);
    }

    [Theory]
    [InlineData("ControlPrint", "{\n  \"Eleventa\": { \"Activo\": true },\n  // Control Print de la caja principal\n  \"Registro\": { \"Dias\": 45 }\n}\n")]
    [InlineData("Registro", "{\n  \"Eleventa\": { \"Activo\": true },\n  // Control Print de la caja principal\n  \"ControlPrint\": { \"Servidor\": \"192.168.1.106\" }\n}\n")]
    [InlineData("Eleventa", "{\n  // Control Print de la caja principal\n  \"ControlPrint\": { \"Servidor\": \"192.168.1.106\" },\n  \"Registro\": { \"Dias\": 45 }\n}\n")]
    public void Quitar_una_seccion_se_lleva_su_renglon_y_su_coma(string seccion, string queda)
    {
        const string antes = "{\n  \"Eleventa\": { \"Activo\": true },\n  // Control Print de la caja principal\n"
            + "  \"ControlPrint\": { \"Servidor\": \"192.168.1.106\" },\n  \"Registro\": { \"Dias\": 45 }\n}\n";

        var quedo = Igualar(antes, o =>
        {
            o.Remove(seccion);
            return o;
        });

        // El comentario de arriba de «ControlPrint» se queda también al quitarla a ella: está antes de su renglón.
        Assert.Equal(queda, quedo);
    }

    [Fact]
    public void Quitar_la_unica_seccion_deja_las_llaves()
    {
        var quedo = Igualar("{\r\n  \"ControlPrint\": { \"Servidor\": \"192.168.1.106\" }\r\n}\r\n", o =>
        {
            o.Remove("ControlPrint");
            return o;
        });

        Assert.Equal("{\r\n}\r\n", quedo);
    }

    [Fact]
    public void Las_claves_no_distinguen_mayusculas_se_cambian_con_el_nombre_que_tienen()
    {
        var archivo = Texto("{ \"eleventa\": { \"servidor\": \"192.168.1.106\" } }");
        var objetivo = (JsonObject)archivo.Objeto().DeepClone();
        Json.Poner(Json.Seccion(objetivo, "Eleventa"), "Servidor", "192.168.1.50");

        archivo.Igualar(objetivo);

        Assert.Equal("{ \"eleventa\": { \"servidor\": \"192.168.1.50\" } }", archivo.Texto);
    }

    [Fact]
    public void Si_nada_cambia_no_hay_cambios()
    {
        var actual = Objeto("""{ "Eleventa": { "Activo": true, "Caja": 3 }, "ControlPrint": { "Servidor": "192.168.1.106" } }""");
        var objetivo = Objeto("""{ "ControlPrint": { "Servidor": "192.168.1.106" }, "eleventa": { "caja": 3, "activo": true } }""");

        Assert.Empty(TextoJsonc.Cambios(actual, objetivo));
    }

    [Fact]
    public void Los_cambios_se_dicen_por_su_nombre_nunca_con_su_valor()
    {
        var actual = Objeto("""{ "Eleventa": { "Servidor": "192.168.1.106" }, "ControlPrint": { "Servidor": "192.168.1.106" } }""");
        var objetivo = Objeto("""{ "Eleventa": { "Servidor": "192.168.1.50", "Escribir": false }, "Instalacion": { "UnaSolaCaja": true } }""");

        Assert.Equal(["Eleventa:Servidor", "Eleventa:Escribir (se agrega)", "ControlPrint (se quita)", "Instalacion (se agrega)"],
            TextoJsonc.Cambios(actual, objetivo));
    }

    [Theory]
    [InlineData("{ \"Eleventa\": { \"Activo\": true, } ", "renglón 1")]
    [InlineData("{\n  \"Eleventa\": { \"Activo\": tru }\n}", "renglón 2")]
    [InlineData("[ 1, 2 ]", "no es un objeto")]
    [InlineData("{ \"Eleventa\": {}, \"eleventa\": {} }", "dos veces «eleventa»")]
    public void Mal_escrito_se_dice_sin_el_contenido_del_archivo(string texto, string dice)
    {
        var error = Assert.Throws<FormatException>(() => Texto(texto).Objeto());

        Assert.Contains(dice, error.Message);
    }

    [Fact]
    public void Escrito_entero_lleva_su_comentario_arriba_y_se_lee_igual()
    {
        var objetivo = Objeto("""{ "Eleventa": { "Activo": true, "Base": "C:\\eleventa\\PDVDATA.FDB" } }""");

        var completo = TextoJsonc.Completo(objetivo, ["La escribió el instalador."], bom: false, "\r\n");

        Assert.StartsWith("// La escribió el instalador.\r\n{\r\n  \"Eleventa\": {\r\n    \"Activo\": true,", completo.Texto);
        Assert.True(Json.Iguales(objetivo, completo.Objeto()));
    }
}
