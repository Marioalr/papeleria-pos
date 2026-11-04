using System.Text;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// diagnosticar-impresoras (2026-11-03): lo que contesta cada impresora por la red, para el fin de semana. Aquí con respuestas inventadas
/// (nunca contra la tienda): una WF-6590 como la que leyó Control Print, una L5590 que no dice sus niveles y una que no contesta.
/// </summary>
public class DiagnosticoImpresoraTests
{
    private static SnmpClient.Value Texto(string s) => new(0x04, Encoding.ASCII.GetBytes(s));

    private static SnmpClient.Value Entero(long n)
    {
        var bytes = BitConverter.GetBytes(n).Reverse().ToArray();
        var i = 0;
        while (i < bytes.Length - 1 && ((bytes[i] == 0 && (bytes[i + 1] & 0x80) == 0) || (bytes[i] == 0xFF && (bytes[i + 1] & 0x80) != 0)))
            i++;
        return new SnmpClient.Value(0x02, bytes[i..]);
    }

    private static readonly SnmpClient.Value NoLoTiene = new(0x81, []);

    /// <summary>La respuesta «@BDC ST2» de las pruebas de Control Print: 25,059 a color y 39,654 en B/N, serie TEST000000.</summary>
    private static readonly byte[] St2 = Convert.FromHexString(
        "0040424443205354320D0A6500010104060201010D01070E010F0F0D0301003B05032704020D03014A100901084E08014E02194E130101190C0000000000756E"
        + "6B6E6F776E240200003614FFFFFFFFFFFFFFFFE3610000E69A0000000000003901003C020100400A" + "54455354303030303030");

    private static Func<string, CancellationToken, Task<SnmpClient.Value>> Contesta(Dictionary<string, SnmpClient.Value> respuestas) =>
        (oid, _) => Task.FromResult(respuestas.GetValueOrDefault(oid, NoLoTiene));

    [Fact]
    public async Task Una_WF_6590_dice_su_modelo_sus_niveles_su_contador_su_serie_y_su_caja()
    {
        var respuestas = new Dictionary<string, SnmpClient.Value>
        {
            ["1.3.6.1.2.1.25.3.2.1.3.1"] = Texto("EPSON WF-6590 Series"),
            ["1.3.6.1.2.1.43.10.2.1.4.1.1"] = Entero(64713),
            ["1.3.6.1.4.1.1248.1.2.2.44.1.1.2.1.115.116.1.0.1"] = new(0x04, St2),
        };
        foreach (var (i, tinta, nivel) in new[] { (1, "Black ink", 59), (2, "Cyan ink", 40), (3, "Magenta ink", 30), (4, "Yellow ink", 20) })
        {
            respuestas[$"1.3.6.1.2.1.43.11.1.1.6.1.{i}"] = Texto(tinta);
            respuestas[$"1.3.6.1.2.1.43.11.1.1.8.1.{i}"] = Entero(100);
            respuestas[$"1.3.6.1.2.1.43.11.1.1.9.1.{i}"] = Entero(nivel);
        }

        var r = await DiagnosticoImpresora.LeerAsync("192.0.2.21", Contesta(respuestas),
            _ => Task.FromResult("<img class='color' src='../../IMAGE/Ink_Waste.PNG' height='4' style=''>"));

        Assert.Equal(PerfilImpresora.WF6590, r.Perfil?.Clave);
        Assert.Equal((59, 40, 30, 20, 8, 64713L, "TEST000000"), (r.Lectura.InkK, r.Lectura.InkC, r.Lectura.InkM, r.Lectura.InkY, r.Lectura.Maintenance,
            r.Lectura.LifeCount, r.Lectura.Serial));
        Assert.Contains(r.Hallazgos, h => h.Contains("que suman su contador"));
        Assert.Contains(r.Hallazgos, h => h.Contains("le queda 8 %"));
        Assert.Empty(r.Lectura.Errors);
        var informe = DiagnosticoImpresora.Informe(r);
        Assert.Contains("negro 59 %", informe);
        Assert.Contains("| 1.3.6.1.2.1.43.10.2.1.4.1.1 | prtMarkerLifeCount (el contador de caras) | 64713 |", informe);
    }

    [Fact]
    public async Task Una_L5590_sin_niveles_ni_caja_lo_dice_para_anotarlo()
    {
        var respuestas = new Dictionary<string, SnmpClient.Value>
        {
            ["1.3.6.1.2.1.1.1.0"] = Texto("EPSON L5590 Series"),
            ["1.3.6.1.2.1.43.10.2.1.4.1.1"] = Entero(1234),
        };
        foreach (var (i, tinta) in new[] { (1, "Black ink"), (2, "Cyan ink"), (3, "Magenta ink"), (4, "Yellow ink") })
        {
            respuestas[$"1.3.6.1.2.1.43.11.1.1.6.1.{i}"] = Texto(tinta);
            respuestas[$"1.3.6.1.2.1.43.11.1.1.9.1.{i}"] = Entero(-3);
        }
        respuestas["1.3.6.1.2.1.43.11.1.1.6.1.5"] = Texto("Waste ink box");
        respuestas["1.3.6.1.2.1.43.11.1.1.8.1.5"] = Entero(250);
        respuestas["1.3.6.1.2.1.43.11.1.1.9.1.5"] = Entero(200);

        var r = await DiagnosticoImpresora.LeerAsync("192.0.2.23", Contesta(respuestas), _ => Task.FromResult("<html>sin barra</html>"));

        Assert.Equal(PerfilImpresora.L5590, r.Perfil?.Clave);
        Assert.Equal(((int?)null, 1234L), (r.Lectura.InkK, r.Lectura.LifeCount));
        Assert.Contains(r.Hallazgos, h => h.Contains("Sin nivel por SNMP") && h.Contains("-3: «queda algo»"));
        Assert.Contains(r.Hallazgos, h => h.Contains("«Waste ink box»") && h.Contains("no empieza con Black"));
        Assert.Contains(r.Hallazgos, h => h.Contains("capacidad 250, no 100") && h.Contains("80 %"));
        Assert.Contains(r.Hallazgos, h => h.Contains("Epson «st» no contestó"));
        Assert.Contains(r.Hallazgos, h => h.Contains("sin la barra de la caja de mantenimiento"));
        Assert.Contains(r.Hallazgos, h => h.Contains("No dijo su serie"));
    }

    [Fact]
    public async Task Una_que_no_contesta_lo_dice_y_no_truena()
    {
        var r = await DiagnosticoImpresora.LeerAsync("192.0.2.99", (oid, _) => throw new TimeoutException("SNMP sin respuesta de 192.0.2.99"),
            _ => throw new HttpRequestException("No se pudo conectar"));

        Assert.Contains(r.Hallazgos, h => h.StartsWith("No contestó nada por SNMP"));
        Assert.Contains(r.Hallazgos, h => h.Contains("Su página de estado no abrió (No se pudo conectar)"));
        Assert.Null(r.Perfil);
        Assert.All(r.Respuestas, x => Assert.Equal("SNMP sin respuesta de 192.0.2.99", x.Error));
        Assert.Equal(DiagnosticoImpresora.Preguntas.Count, r.Lectura.Errors.Count);
    }
}
