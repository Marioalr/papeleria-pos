using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;
using Xunit.Abstractions;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>Solo corre con la variable <c>MEDIR_COPIA</c>: mide, no prueba (y tarda).</summary>
public sealed class FactDeMedicionAttribute : FactAttribute
{
    public FactDeMedicionAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEDIR_COPIA")))
            Skip = "Solo mide con la variable MEDIR_COPIA.";
    }
}

/// <summary>
/// Cuánto pesa y cuánto tarda la copia completa contra solo lo que cambió (entrega 5, parte 2), con los datos ficticios de
/// siempre (<c>GeneradorFicticios</c>: 1,458 productos en la copia) copiados hasta unos 6,000 (los de la medición de la
/// propuesta), en esta PC y sin red de por medio (el servicio en el mismo proceso): lo que mide es lo que cuesta armarla,
/// mandarla, leerla y aplicarla. Para el plan; no revisa nada.
/// </summary>
public sealed class MedicionCopiaTests(ITestOutputHelper salida) : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-medicion-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static double Mediana(IEnumerable<double> valores) => valores.Order().ToList() is var l ? l[l.Count / 2] : 0;

    /// <summary>Lo que anotó la caja que tardó (RendimientoCaja), por lo que dice, en ms.</summary>
    private static Dictionary<string, List<double>> Actividades(RendimientoCaja rendimiento)
    {
        var porQue = new Dictionary<string, List<double>>();
        foreach (var a in rendimiento.Entre(0, long.MaxValue))
        {
            var m = Regex.Match(a, @"^(.*), (\d+) ms$");
            if (!m.Success)
                continue;
            var que = m.Groups[1].Value;
            if (!porQue.TryGetValue(que, out var lista))
                porQue[que] = lista = [];
            lista.Add(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        return porQue;
    }

    /// <summary>El catálogo ficticio, <paramref name="veces"/> veces (como el simulador de cajas con --catalogo-por).</summary>
    private static async Task MultiplicarAsync(ServicioDePrueba servicio, int veces)
    {
        await using var db = servicio.Base();
        await PapeleriaPos.Herramientas.Simulador.BaseDePrueba.MultiplicarCatalogoAsync(db, veces);
    }

    [FactDeMedicion]
    public async Task La_copia_completa_contra_solo_lo_que_cambio()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await MultiplicarAsync(servicio, int.TryParse(Environment.GetEnvironmentVariable("MEDIR_COPIA"), out var veces) && veces > 1 ? veces : 4);
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var cajero = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var turno = (await (await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000))).Content.ReadFromJsonAsync<TurnoDto>())!;

        // La copia completa: bytes, lo que tarda en llegar entera (armarla y mandarla) y en leerla.
        var (tamanos, bajar, leer) = (new List<long>(), new List<double>(), new List<double>());
        PaqueteContingenciaDto copia = null!;
        for (var i = 0; i < 5; i++)
        {
            var reloj = Stopwatch.StartNew();
            var bytes = await http.GetByteArrayAsync($"api/cajas/{caja.Id}/paquete");
            bajar.Add(reloj.Elapsed.TotalMilliseconds);
            reloj.Restart();
            copia = JsonSerializer.Deserialize<PaqueteContingenciaDto>(bytes, JsonSerializerOptions.Web)!;
            leer.Add(reloj.Elapsed.TotalMilliseconds);
            tamanos.Add(bytes.Length);
        }
        salida.WriteLine($"Productos en la copia: {copia.Productos.Count:N0}; códigos: {copia.Codigos.Count:N0}.");
        salida.WriteLine($"Copia completa: {tamanos[0] / 1024.0 / 1024.0:N2} MB; armarla y mandarla {Mediana(bajar):N0} ms; leerla {Mediana(leer):N0} ms.");

        // Lo que cambió: un precio, una venta (solo existencias) y 50 precios.
        var conPrecio = copia.Productos.Where(p => p.Precios.Count == 1 && p.Precios[0].PresentacionId is null && p.LlevaInventario)
            .OrderBy(p => p.VarianteId).Take(60).ToList();
        // Como lo pide la caja (Contingencia): con las versiones de lo más vendido y lo elegido que ya tiene, para que no
        // vuelvan enteras con cada cambio.
        async Task<(long Bytes, double Ms, CambiosCopiaDto Cambios)> CambiosAsync(PaqueteContingenciaDto desde)
        {
            var reloj = Stopwatch.StartNew();
            var bytes = await http.GetByteArrayAsync(
                $"api/cajas/{caja.Id}/cambios?desde={desde.Version}&existencias={desde.VersionExistencias}&base={desde.Base}&formato={CopiaAlDia.Formato}"
                + $"&promociones={desde.HuellaPromociones}"
                + $"&vendidos={desde.VersionVendidos ?? 0}&elegidos={desde.VersionElegidos ?? 0}");
            var ms = reloj.Elapsed.TotalMilliseconds;
            return (bytes.Length, ms, JsonSerializer.Deserialize<CambiosCopiaDto>(bytes, JsonSerializerOptions.Web)!);
        }
        async Task PrecioAsync(ProductoVentaDto p, long mas) =>
            Assert.True((await http.PostAsJsonAsync("api/catalogo/precio-menudeo",
                new SolicitudPrecioMenudeo(dueno.UsuarioId, caja.SucursalId, p.VarianteId, p.Precios[0].PrecioCentavos + mas))).IsSuccessStatusCode);

        var mediciones = new List<string>();
        for (var vuelta = 0; vuelta < 3; vuelta++)
        {
            var antes = JsonSerializer.Deserialize<PaqueteContingenciaDto>(await http.GetByteArrayAsync($"api/cajas/{caja.Id}/paquete"), JsonSerializerOptions.Web)!;
            await PrecioAsync(conPrecio[vuelta], 100 + vuelta);
            await servicio.CatalogoAlDiaAsync();
            var unPrecio = await CambiosAsync(antes);
            var reloj = Stopwatch.StartNew();
            var aplicada = CopiaAlDia.Aplicar(antes, unPrecio.Cambios);
            var foto = FotoCatalogo.Armar(aplicada);
            var aplicar = reloj.Elapsed.TotalMilliseconds;
            mediciones.Add($"Un precio: {unPrecio.Bytes:N0} bytes, {unPrecio.Ms:N1} ms; aplicarlo y armar la foto {aplicar:N0} ms (foto v{foto.Version}).");

            var despuesDelPrecio = aplicada;
            await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
                [new LineaSolicitada(conPrecio[10 + vuelta].VarianteId, conPrecio[10 + vuelta].Presentacion.Id, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)]));
            var unaVenta = await CambiosAsync(despuesDelPrecio);
            mediciones.Add($"Una venta (existencias): {unaVenta.Bytes:N0} bytes, {unaVenta.Ms:N1} ms ({unaVenta.Cambios.Existencias.Count} existencias).");
        }
        var antesDe50 = JsonSerializer.Deserialize<PaqueteContingenciaDto>(await http.GetByteArrayAsync($"api/cajas/{caja.Id}/paquete"), JsonSerializerOptions.Web)!;
        foreach (var p in conPrecio.Skip(20).Take(40))
            await PrecioAsync(p, 300);
        await servicio.CatalogoAlDiaAsync();
        var cuarenta = await CambiosAsync(antesDe50);
        mediciones.Add($"40 precios: {cuarenta.Bytes:N0} bytes, {cuarenta.Ms:N1} ms ({cuarenta.Cambios.Productos.Count} productos).");
        foreach (var m in mediciones)
            salida.WriteLine(m);

        // En la caja de verdad (contingencia y base local): lo que anota que tardó al bajar la copia y al aplicar los cambios.
        var rendimiento = new RendimientoCaja();
        var red = new RedDePrueba(servicio.Fabrica.Server.CreateHandler());
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(red), new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id },
            rendimiento: rendimiento);
        await contingencia.LatidoAsync();
        for (var i = 0; i < 3; i++)
        {
            await PrecioAsync(conPrecio[50 + i], 700);
            await contingencia.LatidoAsync();
            await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
                [new LineaSolicitada(conPrecio[55 + i].VarianteId, conPrecio[55 + i].Presentacion.Id, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)]));
            await contingencia.LatidoAsync();
        }
        contingencia.Cerrar(TimeSpan.FromSeconds(10));
        foreach (var (que, ms) in Actividades(rendimiento))
            salida.WriteLine($"En la caja: {que}: {string.Join(", ", ms.Select(x => $"{x:N0}"))} ms.");
        salida.WriteLine($"Copias completas que pidió la caja: {red.Cuantas($"/api/cajas/{caja.Id}/paquete")}; cambios: {red.Cuantas($"/api/cajas/{caja.Id}/cambios")}.");
    }
}
