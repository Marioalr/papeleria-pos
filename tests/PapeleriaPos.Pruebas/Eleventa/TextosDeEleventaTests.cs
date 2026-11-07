using System.Reflection;
using FirebirdSql.Data.FirebirdClient;
using PapeleriaPos.Eleventa;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// Auditoría del 2026-11-04: el panel de eleventa de la caja 2 enseñaba lo que dice Firebird, en inglés. Quien atiende tiene que
/// saber si se arregla solo o a quién avisar; el texto de Firebird va al registro del servicio.
/// </summary>
public sealed class TextosDeEleventaTests
{
    private static readonly ServidorEleventa Servidor = new("192.0.2.1", @"C:\x\PDVDATA.FDB", "SYSDBA", "x");

    /// <summary>Una excepción como las que arma FirebirdClient (sus constructores no son públicos).</summary>
    private static FbException Fb(string fabrica, params object?[] argumentos)
    {
        var isc = typeof(FbConnection).Assembly.GetType("FirebirdSql.Data.Common.IscException")!;
        var metodo = isc.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).First(x => x.Name == fabrica && x.GetParameters().Length == argumentos.Length);
        var crear = typeof(FbException).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .First(x => x.Name == "Create" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType.IsAssignableFrom(isc));
        return (FbException)crear.Invoke(null, [metodo.Invoke(null, argumentos)])!;
    }

    public static TheoryData<int> CortesDeRed => new() { 335544726, 335544727 };

    [Theory]
    [MemberData(nameof(CortesDeRed))]
    public void Un_corte_de_red_a_media_llamada_se_dice_en_espanol(int codigo) =>
        Assert.Equal(LectorEleventa.SeCorto(Servidor), LectorEleventa.Explicar(Fb("ForErrorCode", codigo, null), Servidor));

    [Fact]
    public void Lo_que_se_corta_sin_codigo_de_firebird_tambien()
    {
        Assert.Equal(LectorEleventa.SeCorto(Servidor), LectorEleventa.Explicar(Fb("ForIOException", new IOException("Unable to read data")), Servidor));
        Assert.Equal(LectorEleventa.SeCorto(Servidor), LectorEleventa.Explicar(new IOException("Unable to read data"), Servidor));
        Assert.Equal(LectorEleventa.SeCorto(Servidor), LectorEleventa.Explicar(new System.Net.Sockets.SocketException(10054), Servidor));
    }

    [Theory]
    [InlineData(335544345)]
    [InlineData(335544336)]
    public void Un_choque_de_candados_dice_que_se_reintenta_solo(int codigo) =>
        Assert.Equal(LectorEleventa.Ocupado, LectorEleventa.Explicar(Fb("ForErrorCode", codigo, null), Servidor));

    [Fact]
    public void Otro_error_de_firebird_no_se_ensena_y_dice_a_quien_avisar()
    {
        var error = Fb("ForErrorCode", 335544665, null);

        Assert.Equal(LectorEleventa.ContestoConError, LectorEleventa.Explicar(error, Servidor));
        // El texto de Firebird, para el registro y las herramientas de consola.
        Assert.Contains(error.Message.Split('\n')[0].Trim(), LectorEleventa.DetalleParaElRegistro(error, Servidor));
        Assert.Equal(error.Message, LectorEleventa.ExplicarEnConsola(error, Servidor));
    }

    [Fact]
    public void Lo_que_escribio_el_puente_en_espanol_se_dice_tal_cual_y_lo_demas_no()
    {
        Assert.Equal("En eleventa no está el departamento «Comunes».", LectorEleventa.Explicar(new InvalidOperationException("En eleventa no está el departamento «Comunes»."), Servidor));
        Assert.Equal(LectorEleventa.ContestoConError, LectorEleventa.Explicar(new OverflowException("Value was either too large or too small for an Int64."), Servidor));
    }

    [Fact]
    public void Lo_que_ya_se_decia_sigue_igual()
    {
        Assert.StartsWith("No contesta el eleventa de 192.0.2.1", LectorEleventa.Explicar(Fb("ForErrorCode", 335544721, null), Servidor));
        Assert.Equal("", LectorEleventa.DetalleParaElRegistro(new TimeoutException(), Servidor));
    }
}
