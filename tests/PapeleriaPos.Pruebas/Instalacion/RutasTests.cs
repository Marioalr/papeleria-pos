using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>Lo del ayudante que lee Windows (el servicio registrado, los puertos COM, el gbak de Firebird), sin Windows.</summary>
public sealed class RutasTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\PapeleriaPos\\Servicio\\PapeleriaPos.Servicio.exe\"", @"C:\Program Files\PapeleriaPos\Servicio\PapeleriaPos.Servicio.exe")]
    [InlineData("\"C:\\Program Files\\PapeleriaPos\\Servicio\\PapeleriaPos.Servicio.exe\" --contentRoot x", @"C:\Program Files\PapeleriaPos\Servicio\PapeleriaPos.Servicio.exe")]
    [InlineData(@"C:\PapeleriaPos-de-antes\Servicio\PapeleriaPos.Servicio.exe", @"C:\PapeleriaPos-de-antes\Servicio\PapeleriaPos.Servicio.exe")]
    [InlineData(@"C:\PapeleriaPos\PapeleriaPos.Servicio.EXE -x", @"C:\PapeleriaPos\PapeleriaPos.Servicio.EXE")]
    public void El_exe_del_servicio_se_lee_como_lo_lee_actualizar(string linea, string exe) =>
        Assert.Equal(exe, Rutas.ExeDeLaLinea(linea));

    [Fact]
    public void Los_puertos_van_en_orden_de_su_numero_sin_repetir() =>
        Assert.Equal(["COM1", "COM3", "COM10", "Otro"], Rutas.Puertos(["COM10", "COM3", " COM1 ", "com3", "Otro", ""]));

    [Fact]
    public void El_gbak_se_busca_en_la_copia_del_instructivo_y_en_las_carpetas_de_firebird_y_de_eleventa()
    {
        using var carpeta = new CarpetaDePrueba();
        var datos = Path.Combine(carpeta.Ruta, "ProgramData", "PapeleriaPos");
        var raiz = Path.Combine(carpeta.Ruta, "Program Files (x86)");
        string Gbak(params string[] partes)
        {
            var ruta = Path.Combine([.. partes, "bin", "gbak.exe"]);
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            File.WriteAllText(ruta, "");
            return ruta;
        }
        var copia = Gbak(datos, "firebird20");
        var firebird20 = Gbak(raiz, "Firebird", "Firebird_2_0");
        var deEleventa = Gbak(raiz, "AbarrotesMultiCaja", "FirebirdSQL20");
        var sql20 = Gbak(raiz, "FirebirdSQL20");
        Gbak(raiz, "OtroPrograma", "Firebird");
        Gbak(raiz, "Firebird", "Firebird_2_0", "muy", "abajo");

        var encontrados = Rutas.Gbak(datos, [raiz, Path.Combine(carpeta.Ruta, "no-existe")]);

        Assert.Equal(copia, encontrados[0]);
        Assert.Equal(new[] { firebird20, deEleventa, sql20 }.Order(), encontrados.Skip(1).Order());
    }

    [Fact]
    public void Del_ini_de_eleventa_se_lee_solo_esa_clave_de_esa_seccion()
    {
        const string ini = "[EnRed]\nIPServidor=\n[Otra]\nIPServidor=10.0.0.1\n";

        Assert.Null(Ini.Valor(ini, "EnRed", "IPServidor"));
        Assert.Equal("10.0.0.1", Ini.Valor(ini, "otra", "ipservidor"));
        Assert.Null(Ini.Valor(null, "EnRed", "IPServidor"));
    }
}
