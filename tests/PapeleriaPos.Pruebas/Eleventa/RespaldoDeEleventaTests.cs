using PapeleriaPos.Eleventa;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// El respaldo propio de eleventa con gbak (docs/migracion-eleventa.md, «Los respaldos de eleventa»), con un gbak de
/// mentiras: nunca se corre gbak contra nada real.
/// </summary>
public class RespaldoDeEleventaTests
{
    private static readonly ServidorEleventa Principal = new("10.0.0.10", @"C:\eleventa\PDVDATA.FDB", "SYSDBA", "secreta");
    private static readonly DateOnly Hoy = new(2026, 10, 16);

    [Fact]
    public void Por_omision_los_respaldos_de_eleventa_van_junto_a_la_base_fuera_de_OneDrive()
    {
        var carpeta = global::PapeleriaPos.Servicio.RutasServicio.CarpetaRespaldosEleventa(new global::PapeleriaPos.Servicio.OpcionesEleventa(),
            @"Data Source=C:\ProgramData\PapeleriaPos\pos.db");

        Assert.EndsWith("respaldos-eleventa", carpeta);
        Assert.False(RespaldoDeEleventa.EnOneDrive(carpeta));
        Assert.True(RespaldoDeEleventa.EnOneDrive(@"C:\Users\caja2\OneDrive\Documentos\respaldos"));
        Assert.True(RespaldoDeEleventa.EnOneDrive(@"C:\Users\caja2\OneDrive - Papelería\respaldos"));
    }

    [Fact]
    public async Task El_respaldo_del_dia_queda_con_su_fecha_y_la_clave_va_en_el_entorno_no_en_la_linea_de_comandos()
    {
        using var gbak = new GbakDePrueba();
        var respaldo = new RespaldoDeEleventa(new OpcionesRespaldoEleventa(gbak.Ruta, gbak.Respaldos), gbak);

        var hecho = await respaldo.HacerAsync(Principal, Hoy);
        // El mismo día ya no se vuelve a hacer.
        var otraVez = await respaldo.HacerAsync(Principal, Hoy);

        var archivo = Path.Combine(gbak.Respaldos, "eleventa-2026-10-16.fbk");
        Assert.Equal((archivo, archivo), (hecho.Archivo, otraVez.Archivo));
        Assert.True(respaldo.HayDelDia(Hoy));
        // Con su .origen: de qué base es, sin la clave.
        Assert.Equal(["eleventa-2026-10-16.fbk", "eleventa-2026-10-16.fbk.origen"], Directory.GetFiles(gbak.Respaldos).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(@"10.0.0.10:3050/C:\eleventa\PDVDATA.FDB", File.ReadAllText(archivo + ".origen"));
        var llamada = Assert.Single(gbak.Llamadas);
        Assert.Equal(gbak.Ruta, llamada.Gbak);
        // Respaldo, sin limpiar la base mientras eleventa trabaja, la base de la principal por la red y el archivo aquí (a medias, .tmp).
        Assert.Equal(["-b", "-g", @"10.0.0.10:C:\eleventa\PDVDATA.FDB", archivo + ".tmp"], llamada.Argumentos);
        Assert.Equal(("SYSDBA", "secreta"), (llamada.Entorno["ISC_USER"], llamada.Entorno["ISC_PASSWORD"]));
        Assert.DoesNotContain(llamada.Argumentos, a => a.Contains("secreta"));
        // En otro puerto, como lo escribe Firebird 2.0.
        Assert.Equal(@"10.0.0.10/3055:C:\eleventa\PDVDATA.FDB", RespaldoDeEleventa.Argumentos(Principal with { Puerto = 3055 }, "x.fbk")[2]);
    }

    [Fact]
    public async Task Si_gbak_no_puede_no_queda_respaldo_a_medias_y_dice_por_que()
    {
        using var gbak = new GbakDePrueba { Bien = false };
        var respaldo = new RespaldoDeEleventa(new OpcionesRespaldoEleventa(gbak.Ruta, gbak.Respaldos), gbak);
        // gbak alcanzó a escribir algo antes de fallar.
        gbak.AlRespaldar = () => File.WriteAllTextAsync(Path.Combine(gbak.Respaldos, "eleventa-2026-10-16.fbk.tmp"), "a medias");

        var hecho = await respaldo.HacerAsync(Principal, Hoy);

        Assert.False(hecho.Listo);
        Assert.Equal("gbak no pudo respaldar eleventa (código 1): gbak: ERROR:Unable to complete network request to host \"10.0.0.10\".", hecho.Error);
        Assert.False(respaldo.HayDelDia(Hoy));
        Assert.Empty(Directory.GetFiles(gbak.Respaldos));
    }

    [Fact]
    public async Task Sin_gbak_o_con_la_carpeta_en_OneDrive_no_se_respalda()
    {
        using var gbak = new GbakDePrueba();

        var sinRuta = await new RespaldoDeEleventa(new OpcionesRespaldoEleventa(null, gbak.Respaldos), gbak).HacerAsync(Principal, Hoy);
        var sinArchivo = await new RespaldoDeEleventa(new OpcionesRespaldoEleventa(gbak.Ruta + "x", gbak.Respaldos), gbak).HacerAsync(Principal, Hoy);
        var enOneDrive = await new RespaldoDeEleventa(new OpcionesRespaldoEleventa(gbak.Ruta, Path.Combine(gbak.Respaldos, "OneDrive", "eleventa")), gbak)
            .HacerAsync(Principal, Hoy);

        Assert.Equal("Falta la ruta de gbak.exe en la configuración (Eleventa:Gbak).", sinRuta.Error);
        Assert.Equal($"No está gbak.exe en {gbak.Ruta}x (Eleventa:Gbak).", sinArchivo.Error);
        Assert.StartsWith("La carpeta de los respaldos de eleventa está en OneDrive", enOneDrive.Error);
        Assert.Empty(gbak.Llamadas);
    }

    [Fact]
    public async Task Se_guardan_los_ultimos_dias_y_no_se_toca_nada_mas()
    {
        using var gbak = new GbakDePrueba();
        Directory.CreateDirectory(gbak.Respaldos);
        for (var dia = 8; dia <= 15; dia++)
            await File.WriteAllTextAsync(Path.Combine(gbak.Respaldos, $"eleventa-2026-10-{dia:00}.fbk"), "de antes");
        foreach (var otro in new[] { "eleventa-2026-10-10.fbk.tmp", "eleventa-notas.txt", "otro.fbk" })
            await File.WriteAllTextAsync(Path.Combine(gbak.Respaldos, otro), "");
        var respaldo = new RespaldoDeEleventa(new OpcionesRespaldoEleventa(gbak.Ruta, gbak.Respaldos, Dias: 5), gbak);

        await respaldo.HacerAsync(Principal, Hoy);

        Assert.Equal(
            ["eleventa-2026-10-12.fbk", "eleventa-2026-10-13.fbk", "eleventa-2026-10-14.fbk", "eleventa-2026-10-15.fbk", "eleventa-2026-10-16.fbk",
             "eleventa-2026-10-16.fbk.origen", "eleventa-notas.txt", "otro.fbk"],
            Directory.GetFiles(gbak.Respaldos).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// El piloto alfa (2026-10-25): el ensayo escribe en una copia de la base de eleventa y después se vuelve a la real, a veces el
    /// mismo día. El respaldo del día de la copia no cuenta para la real: antes de escribirle a la real, se respalda la real.
    /// </summary>
    [Fact]
    public async Task El_respaldo_del_dia_de_la_copia_del_ensayo_no_cuenta_para_la_base_real()
    {
        using var gbak = new GbakDePrueba();
        var respaldo = new RespaldoDeEleventa(new OpcionesRespaldoEleventa(gbak.Ruta, gbak.Respaldos), gbak);
        var copia = Principal with { Base = @"C:\eleventa\PDVDATA-ENSAYO.FDB" };

        await respaldo.HacerAsync(copia, Hoy);
        Assert.True(respaldo.HayDelDia(Hoy, copia));
        Assert.False(respaldo.HayDelDia(Hoy, Principal));

        // De vuelta en la real, el mismo día: se respalda la real (encima del de la copia).
        var real = await respaldo.HacerAsync(Principal, Hoy);
        Assert.True(real.Listo);
        Assert.Equal([@"10.0.0.10:C:\eleventa\PDVDATA-ENSAYO.FDB", @"10.0.0.10:C:\eleventa\PDVDATA.FDB"], gbak.Llamadas.Select(l => l.Argumentos[2]));
        Assert.True(respaldo.HayDelDia(Hoy, Principal));
        Assert.False(respaldo.HayDelDia(Hoy, copia));
        // Uno de una versión de antes (sin su .origen) cuenta para cualquier base, como antes.
        File.Delete(respaldo.Archivo(Hoy) + ".origen");
        Assert.True(respaldo.HayDelDia(Hoy, copia));
    }
}
