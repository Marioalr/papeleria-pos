using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Los tiempos de la caja en su registro del día (el piloto alfa, 2026-10-25: juntar datos sin cambiar lo que ve el cajero): de
/// cada venta, del primer artículo al cobro, sus renglones, lo que tardó el cobro y lo que esperó a eleventa, y con qué folio salió;
/// y de los escaneos, un resumen cada hora y al terminar el turno (la mitad, el 95 %, el 99 % y el más lento, con cuántos fueron).
/// </summary>
public sealed class TiemposDeLaCajaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"tiempos-caja-{Guid.NewGuid():N}");
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-tiempos-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private static TicketDto Ticket(string folio = "B-000123", int? folioEleventa = null, int? espera = null, bool sinConexion = false, bool yaCobrada = false) =>
        new(folio, new DateTime(2026, 10, 31, 14, 3, 0), "Caja 2", "Ana", [], 2, 4500, 0, [], [], [], FolioEleventa: folioEleventa, YaCobrada: yaCobrada,
            EnContingencia: sinConexion, EsperaEleventaMs: espera);

    [Fact]
    public void Cada_venta_en_una_linea_siempre_igual()
    {
        // En el piloto, con el folio de eleventa a tiempo.
        Assert.Equal("Venta B-000123 · 5 renglones · del primer artículo al cobro 48.2 s · el cobro tardó 0.62 s, 0.41 s esperando a eleventa · "
                     + "salió con el folio 4521 de eleventa",
            TiemposDeLaCaja.LineaDeVenta(Ticket(folioEleventa: 4521, espera: 410), 5, TimeSpan.FromSeconds(48.2), TimeSpan.FromMilliseconds(620)));
        // En el piloto, sin el folio de eleventa (no contestó a tiempo): el de la caja.
        Assert.Equal("Venta B-000123 · 1 renglón · del primer artículo al cobro 3.0 s · el cobro tardó 2.05 s, 2.00 s esperando a eleventa · "
                     + "salió con el folio de la caja",
            TiemposDeLaCaja.LineaDeVenta(Ticket(espera: 2000), 1, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(2050)));
        // Sin conexión, con la copia de la caja.
        Assert.Equal("Venta B-000124 · 2 renglones · del primer artículo al cobro 12.0 s · el cobro tardó 0.05 s, sin conexión · salió con el folio de la caja",
            TiemposDeLaCaja.LineaDeVenta(Ticket("B-000124", sinConexion: true), 2, TimeSpan.FromSeconds(12), TimeSpan.FromMilliseconds(50)));
        // Fuera del piloto, de un ticket que volvió de la espera (de antes de abrir la caja).
        Assert.Equal("Venta B-000125 · 2 renglones · del primer artículo al cobro: no se sabe (volvió de la espera) · el cobro tardó 0.20 s · "
                     + "salió con el folio de la caja",
            TiemposDeLaCaja.LineaDeVenta(Ticket("B-000125"), 2, null, TimeSpan.FromMilliseconds(200)));
        Assert.EndsWith(" · ya había quedado cobrada (se mandó otra vez con el mismo número)",
            TiemposDeLaCaja.LineaDeVenta(Ticket(yaCobrada: true), 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Cada_hora_lo_que_tardaron_los_escaneos_y_nada_si_no_hubo()
    {
        var reloj = RelojDeLaRed.Nuevo();
        var registro = new RegistroDiario(_carpeta, "caja", reloj: reloj);
        var rendimiento = new RendimientoCaja();
        using var tiempos = new TiemposDeLaCaja(rendimiento, registro, reloj);

        foreach (var ms in Enumerable.Range(1, 100).OrderBy(x => x * 37 % 101))
            rendimiento.AnotarEscaneo(TimeSpan.FromMilliseconds(ms));
        reloj.Avanzar(TimeSpan.FromHours(1));
        tiempos.DeLaHora();
        // Una hora sin escaneos: ninguna línea.
        reloj.Avanzar(TimeSpan.FromHours(1));
        tiempos.DeLaHora();
        rendimiento.AnotarEscaneo(TimeSpan.FromMilliseconds(7));
        reloj.Avanzar(TimeSpan.FromMinutes(30));
        tiempos.DeLaHora();

        Assert.Equal(
        [
            "2026-10-17 15:00:00  Escaneos de 14:00 a 15:00 · 100 escaneos · la mitad en 50 ms · el 95 % en 95 ms · el 99 % en 99 ms · el más lento en 100 ms",
            "2026-10-17 16:30:00  Escaneos de 16:00 a 16:30 · 1 escaneo · la mitad en 7 ms · el 95 % en 7 ms · el 99 % en 7 ms · el más lento en 7 ms",
        ], File.ReadAllLines(registro.ArchivoDeHoy));
        // Lo de Configuración › Acerca de (los últimos escaneos) no cambia.
        Assert.Equal(101, rendimiento.Escaneos.Cuantos);
    }

    [Fact]
    public void Al_terminar_cada_turno_lo_de_sus_escaneos()
    {
        var reloj = RelojDeLaRed.Nuevo();
        var registro = new RegistroDiario(_carpeta, "caja", reloj: reloj);
        var rendimiento = new RendimientoCaja();
        using var tiempos = new TiemposDeLaCaja(rendimiento, registro, reloj);
        var (deAna, deLuis, otroDeAna) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        tiempos.EnElTurno(deAna, "Ana", new DateTime(2026, 10, 17, 8, 0, 0));
        rendimiento.AnotarEscaneo(TimeSpan.FromMilliseconds(10));
        rendimiento.AnotarEscaneo(TimeSpan.FromMilliseconds(20));
        tiempos.TurnoTerminado("con su corte Z");
        // El de Luis lo cierra el corte de eleventa: la caja lo sabe cuando entra al siguiente turno.
        tiempos.EnElTurno(deLuis, "Luis", new DateTime(2026, 10, 17, 14, 0, 0));
        rendimiento.AnotarEscaneo(TimeSpan.FromMilliseconds(30));
        tiempos.EnElTurno(deLuis, "Luis", new DateTime(2026, 10, 17, 14, 0, 0));
        tiempos.EnElTurno(otroDeAna, "Ana", new DateTime(2026, 10, 17, 14, 5, 0));
        rendimiento.AnotarEscaneo(TimeSpan.FromMilliseconds(40));
        // Al cerrar la caja: lo de la hora que iba y lo del turno hasta ahí.
        tiempos.Cerrar();

        Assert.Equal(
        [
            "2026-10-17 14:00:00  Escaneos del turno de Ana (abierto el 17/10 08:00), con su corte Z · 2 escaneos · la mitad en 10 ms · el 95 % en 20 ms · "
            + "el 99 % en 20 ms · el más lento en 20 ms",
            "2026-10-17 14:00:00  Escaneos del turno de Luis (abierto el 17/10 14:00), ya terminó (la caja pasó a otro turno) · 1 escaneo · la mitad en 30 ms · "
            + "el 95 % en 30 ms · el 99 % en 30 ms · el más lento en 30 ms",
            "2026-10-17 14:00:00  Escaneos de 14:00 a 14:00 · 4 escaneos · la mitad en 20 ms · el 95 % en 40 ms · el 99 % en 40 ms · el más lento en 40 ms",
            "2026-10-17 14:00:00  Escaneos del turno de Ana (abierto el 17/10 14:05), hasta que se cerró la caja (el turno sigue abierto) · 1 escaneo · "
            + "la mitad en 40 ms · el 95 % en 40 ms · el 99 % en 40 ms · el más lento en 40 ms",
        ], File.ReadAllLines(registro.ArchivoDeHoy));
    }

    [Fact]
    public void Sin_quien_saque_los_resumenes_no_se_guardan_los_escaneos_de_la_hora()
    {
        // La caja del navegador no tiene registro: lo de la hora y del turno no crece sin fin.
        var rendimiento = new RendimientoCaja();
        rendimiento.AnotarEscaneo(TimeSpan.FromMilliseconds(5));

        Assert.Equal(0, rendimiento.SacarDeLaHora().Cuantos);
        Assert.Equal(1, rendimiento.Escaneos.Cuantos);
    }

    [Fact]
    public async Task La_caja_anota_cada_venta_y_los_escaneos_de_su_turno()
    {
        var registro = new RegistroDiario(_carpeta, "caja");
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, registro: registro, conTiempos: true);
        var (a, b) = v.DosDeLaCopia();

        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync(b.Codigo!);
        // Lo que tardaron en verse las líneas (lo mide caja.js, en la página).
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Pintados([9, 14]));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto")).WaitAsync(TimeSpan.FromSeconds(20));
        var folio = Regex.Match(await v.Pantalla.TextoAsync(), @"Venta (\S+) cobrada").Groups[1].Value;

        // La línea de la venta se escribe en otro hilo: la venta no espera al disco (se lee sin chocar con quien escribe).
        string Registro() => File.Exists(registro.ArchivoDeHoy) ? RegistroDelServicioTests.LeerCompartido(registro.ArchivoDeHoy) : "";
        await VentaConCopia.EsperarAsync(() => Registro().Contains($"Venta {folio} · "), "la línea de la venta");
        var linea = Registro().Split(Environment.NewLine).Single(l => l.Contains($"Venta {folio} · "));
        Assert.Matches($@"  Venta {folio} · 2 renglones · del primer artículo al cobro \d+\.\d s · el cobro tardó \d+\.\d\d s · salió con el folio de la caja$", linea);

        // Al cerrar la caja: lo de la hora y lo del turno, con los dos escaneos.
        v.Servicios.GetRequiredService<TiemposDeLaCaja>().Cerrar();
        var texto = Registro();
        Assert.Matches(@"Escaneos de \d\d:\d\d a \d\d:\d\d · 2 escaneos · la mitad en 9 ms · el 95 % en 14 ms · el 99 % en 14 ms · el más lento en 14 ms", texto);
        Assert.Matches(@"Escaneos del turno de .+ \(abierto el \d\d/\d\d \d\d:\d\d\), hasta que se cerró la caja \(el turno sigue abierto\) · 2 escaneos", texto);
        Assert.Empty(v.Pantalla.Errores);
    }
}
