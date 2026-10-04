// Traídas de Control Print v1.12 (ControlPrint.Core.Tests).
using PapeleriaPos.Medicion;

namespace PapeleriaPos.Medicion.Pruebas;

public class PrinterTests
{
    // Respuesta real "@BDC ST2" de la WF-6590 (número de serie reemplazado).
    static readonly byte[] St2 = Convert.FromHexString(
        "0040424443205354320D0A6500010104" +
        "060201010D01070E010F0F0D0301003B" +
        "05032704020D03014A100901084E0801" +
        "4E02194E130101190C0000000000756E" +
        "6B6E6F776E240200003614FFFFFFFFFF" +
        "FFFFFFE3610000E69A00000000000039" +
        "01003C020100400A" + "54455354303030303030");

    [Fact]
    public void Parses_epson_st2_status()
    {
        Assert.True(EpsonSt2.TryParse(St2, out var st));
        Assert.Equal("TEST000000", st.Serial);
        Assert.Equal(25059, st.ColorPages);
        Assert.Equal(39654, st.MonoPages);
        // Campo 0x0F: tinta [tipo, color, nivel] con K=59 %.
        Assert.Equal(new byte[] { 0x03, 0x01, 0x00, 0x3B }, st.Fields[0x0F][..4]);
    }

    [Theory]
    [InlineData("<img class='color' src='../../IMAGE/Ink_Waste.PNG' height='4' style=''>", 8)]
    [InlineData("<img class=\"color\" src=\"../../IMAGE/Ink_Waste.PNG\" height=\"50\">", 100)]
    [InlineData("<html>sin barra</html>", null)]
    public void Parses_maintenance_box_bar(string html, int? expected) =>
        Assert.Equal(expected, PrinterStatusReader.ParseMaintenance(html));

    [Fact]
    public void Builds_valid_snmp_get_request()
    {
        var packet = SnmpClient.BuildGet("public", "1.3.6.1.2.1.1.1.0", 1);
        Assert.Equal(0x30, packet[0]);
        Assert.True(packet.AsSpan().IndexOf("public"u8) > 0);
        // OID codificado: 06 08 2B 06 01 02 01 01 01 00
        var oid = Convert.FromHexString("06082B06010201010100");
        Assert.True(packet.AsSpan().IndexOf(oid) > 0);
    }
}

