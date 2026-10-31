using PapeleriaPos.Instalacion;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>Las respuestas que el asistente le pasa al ayudante (respuestas.txt): lo que no se entiende se dice, nunca se escribe a medias.</summary>
public sealed class RespuestasTests
{
    private static ArchivoDeClaves Archivo(params (string Clave, string Valor)[] datos)
    {
        var archivo = new ArchivoDeClaves();
        foreach (var (clave, valor) in datos)
            archivo.Poner(clave, valor);
        return archivo;
    }

    [Fact]
    public void Las_de_una_caja_principal_con_todo()
    {
        var respuestas = Respuestas.De(Archivo(("rol", "principal"), ("impresora", "tcp://192.168.1.100:9100"), ("cola", "WF-6590"),
            ("lector", "com"), ("lector-puerto", "3"), ("lector-baudios", "19200"), ("controlprint", "si"), ("controlprint-servidor", "192.168.1.106"),
            ("eleventa", "sí"), ("eleventa-servidor", "192.168.1.106"), ("eleventa-base", @"C:\eleventa\PDVDATA.FDB"), ("eleventa-caja", "3"),
            ("eleventa-cajero", ""), ("eleventa-gbak", ""), ("eleventa-respaldos", @"C:\ProgramData\PapeleriaPos\respaldos-eleventa"),
            ("principal", "192.168.1.50")));

        Assert.Equal(RolDeLaPc.Principal, respuestas.Rol);
        Assert.Equal("COM3", respuestas.LectorPuerto);
        Assert.Equal(19200, respuestas.LectorBaudios);
        Assert.True(respuestas.ControlPrint);
        Assert.True(respuestas.Eleventa);
        Assert.Equal(3, respuestas.EleventaCaja);
        Assert.Null(respuestas.EleventaCajero);
        Assert.Null(respuestas.EleventaGbak);
        // La principal no se conecta a otra: lo de «Otra caja» no cuenta.
        Assert.Null(respuestas.Principal);
    }

    /// <summary>«Otra caja» no tiene servicio: lo de Control Print y eleventa no aplica aunque venga.</summary>
    [Fact]
    public void Otra_caja_no_lleva_control_print_ni_eleventa()
    {
        var respuestas = Respuestas.De(Archivo(("rol", "otra"), ("principal", " 192.168.1.50 "), ("controlprint", "si"), ("eleventa", "si")));

        Assert.False(respuestas.ConServicio);
        Assert.Equal("192.168.1.50", respuestas.Principal);
        Assert.Null(respuestas.ControlPrint);
        Assert.Null(respuestas.Eleventa);
        Assert.Null(respuestas.LectorPuerto);
    }

    [Theory]
    [InlineData("rol", "caja", "«rol» va como principal, unica u otra")]
    [InlineData("lector-puerto", "LPT1", "El puerto del lector va como COM1")]
    [InlineData("lector-baudios", "rápido", "La velocidad del lector va de 50 a 921600")]
    [InlineData("eleventa-servidor", "", "Falta la IP de la PC de eleventa")]
    [InlineData("eleventa-caja", "tres", "El número de la caja en eleventa va con cifras")]
    [InlineData("controlprint-servidor", " ", "Falta la IP de la PC de Control Print")]
    [InlineData("impresora", "tcp://", "La impresora de tickets por la red va como tcp://IP:9100")]
    public void Lo_que_no_se_entiende_se_dice(string clave, string valor, string dice)
    {
        var archivo = Archivo(("rol", "unica"), ("lector", "com"), ("lector-puerto", "COM3"), ("controlprint", "si"),
            ("controlprint-servidor", "192.168.1.106"), ("eleventa", "si"), ("eleventa-servidor", "192.168.1.106"), ("eleventa-base", @"C:\PDVDATA.FDB"));
        archivo.Poner(clave, valor);

        var error = Assert.Throws<FormatException>(() => Respuestas.De(archivo));

        Assert.StartsWith(dice, error.Message);
    }

    [Fact]
    public void La_ip_de_la_principal_que_no_es_una_direccion_se_dice()
    {
        var error = Assert.Throws<FormatException>(() => Respuestas.De(Archivo(("rol", "otra"), ("principal", "la de enfrente"))));

        Assert.Contains("va su IP", error.Message);
    }

    [Fact]
    public void El_archivo_se_lee_como_lo_escribe_inno_con_bom_y_comentarios()
    {
        var archivo = ArchivoDeClaves.Desde(["\uFEFF# del asistente", "rol=unica", "Impresora=Impresora de tickets", "sin igual", "=sin clave", "cola="]);

        Assert.Equal("unica", archivo["ROL"]);
        Assert.Equal("Impresora de tickets", archivo["impresora"]);
        Assert.Equal("", archivo["cola"]);
        Assert.Null(archivo["sin igual"]);
        Assert.Equal(3, archivo.Datos.Count);
    }

    [Fact]
    public void Un_valor_va_en_un_solo_renglon()
    {
        var archivo = new ArchivoDeClaves().Poner("error", "uno\r\ndos\nTres");
        using var carpeta = new PapeleriaPos.Pruebas.Interfaz.CarpetaDePrueba();
        var ruta = Path.Combine(carpeta.Ruta, "salida.txt");

        archivo.Guardar(ruta);

        Assert.Equal("uno dos Tres", ArchivoDeClaves.Leer(ruta)["error"]);
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'#'], File.ReadAllBytes(ruta)[..4]);
    }
}
