namespace PapeleriaPos.Impresiones.Windows.Pruebas;

/// <summary>
/// Lo que el agente anota en el registro de la caja al empezar a vigilar la cola de la caja 2 (docs/pruebas-en-tienda.md,
/// «Control Print en la caja 2»): lo que le falta a la cola para medir la tinta.
/// </summary>
public class AvisosDeLaColaTests
{
    [Fact]
    public void La_cola_lista_no_tiene_avisos() =>
        Assert.Empty(AgenteImpresion.AvisosDeLaCola("WF-6590 Series(Red)", conserva: true, soloRaw: false));

    [Fact]
    public void Dice_lo_que_le_falta_para_medir_la_tinta()
    {
        var avisos = AgenteImpresion.AvisosDeLaCola("WF-6590 Series(Red)", conserva: false, soloRaw: true);

        Assert.Equal(2, avisos.Count);
        Assert.Contains("no conserva los documentos impresos", avisos[0]);
        Assert.Contains("«Características de impresión avanzadas»", avisos[1]);
        Assert.Contains("RAW", avisos[1]);
    }

    [Fact]
    public void La_compartida_desde_otra_PC_no_sirve_para_medir()
    {
        var aviso = Assert.Single(AgenteImpresion.AvisosDeLaCola(@"\\PRINCIPAL\EPSON WF-6590", conserva: true, soloRaw: false));
        Assert.Contains("compartida desde otra PC", aviso);
    }
}
