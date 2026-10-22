using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La API de los reportes de ventas (entrega 9, parte 1): «Ver reportes de ventas» para verlos (403 sin él, con quién lo tiene),
/// los costos solo para quien ve la utilidad (a los demás ni viajan), lo que no se entiende es 400, y en el piloto con eleventa
/// solo lo de la caja 2, sin el historial ni el año pasado. Las cifras, en ReportesDeVentasTests.
/// </summary>
public sealed class ReportesApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Puesto(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno)
    {
        public string Ruta(SesionDto quien, string resto = "") => $"api/cajas/{Caja.Id}/reportes/ventas?usuarioId={quien.UsuarioId}{resto}";
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<Puesto> PuestoAsync(ServicioDePrueba servicio) => PuestoAsync(servicio.Fabrica.CreateClient());

    private static async Task<Puesto> PuestoAsync(HttpClient http)
    {
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Puesto(http, caja, await Entrar("cajero", "1111"), await Entrar("encargado", "2222"), await Entrar("dueno", "3333"));
    }

    /// <summary>Una venta de hoy en la caja (un artículo común) y un ticket del historial de hoy hace 364 días, el mismo día de la semana.</summary>
    private static async Task VentasAsync(ServicioDePrueba servicio, Puesto p)
    {
        var turno = await p.Http.GetAsync($"api/cajas/{p.Caja.Id}/turno") is { StatusCode: HttpStatusCode.OK } abierto
            ? await Leer<TurnoDto>(abierto)
            : await Leer<TurnoDto>(await p.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(p.Caja.Id, p.Dueno.UsuarioId, 50000)));
        await Leer<TicketDto>(await p.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(p.Caja.Id, turno.Id, turno.CajeroId,
            [new LineaSolicitada(null, null, 1000, "Recuerdo", 2500)], [new CobroSolicitado(FormaPago.Efectivo, 2500)])));
        await using var db = servicio.Base();
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
        var variante = db.Variantes.OrderBy(v => v.Id).First().Id;
        var historica = new VentaHistorica
        {
            SucursalId = p.Caja.SucursalId, FolioOrigen = $"api-{Guid.NewGuid():N}", TotalCentavos = 4000,
            FechaUtc = HoraMexico.AUtc(hoy.AddDays(-364).ToDateTime(new TimeOnly(0, 1))),
        };
        historica.Lineas.Add(new VentaHistoricaLinea { VentaHistoricaId = historica.Id, VarianteId = variante, Cantidad = Cantidad.Piezas(1), ImporteCentavos = 4000, CostoCentavos = 1500 });
        db.VentasHistoricas.Add(historica);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sin_ver_reportes_de_ventas_no_se_ven_y_lo_que_no_se_entiende_es_400()
    {
        var p = await PuestoAsync(servicio);

        var delCajero = await p.Http.GetAsync(p.Ruta(p.Cajero));
        var delDueno = await p.Http.GetAsync(p.Ruta(p.Dueno, "&periodo=Mes"));
        var otroPeriodo = await p.Http.GetAsync(p.Ruta(p.Dueno, "&periodo=quincena"));
        var manana = await p.Http.GetAsync(p.Ruta(p.Dueno, $"&fecha={DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow)).AddDays(2):yyyy-MM-dd}"));

        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        var rechazo = (await delCajero.Content.ReadFromJsonAsync<ErrorDto>())!;
        // Con quién lo tiene (otra prueba de esta clase se lo da también al encargado).
        Assert.Equal("no-permitido", rechazo.Codigo);
        Assert.StartsWith("Esto lo hace quien tiene el permiso «Ver reportes de ventas»: ", rechazo.Mensaje);
        Assert.Contains("Dueño de prueba", rechazo.Mensaje);
        Assert.Equal(PeriodoReporte.Mes, (await Leer<ReporteVentasDto>(delDueno)).Periodo);
        Assert.Equal(HttpStatusCode.BadRequest, otroPeriodo.StatusCode);
        Assert.Equal("«quincena» no es un periodo: hoy, la semana, el mes o el año.", (await otroPeriodo.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal(HttpStatusCode.BadRequest, manana.StatusCode);
        Assert.Equal(ReportesDeVentas.TodaviaNoLlega, (await manana.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    [Fact]
    public async Task Los_costos_solo_viajan_para_quien_ve_la_utilidad()
    {
        var p = await PuestoAsync(servicio);
        await VentasAsync(servicio, p);
        // El encargado, con los reportes y sin «Ver costos, utilidad y margen».
        await Leer<UsuariosYPermisosDto>(await p.Http.PutAsJsonAsync($"api/cajas/{p.Caja.Id}/usuarios-y-permisos/{p.Encargado.UsuarioId}",
            new SolicitudPermisoUsuario(p.Dueno.UsuarioId, AccionControlada.VerReportes, NivelPermiso.Permitido)));

        var delEncargado = await p.Http.GetStringAsync(p.Ruta(p.Encargado));
        var delDueno = await p.Http.GetStringAsync(p.Ruta(p.Dueno));

        Assert.DoesNotContain("\"utilidad\"", delEncargado);
        Assert.DoesNotContain("costoCentavos", delEncargado);
        Assert.Contains("\"conUtilidad\":false", delEncargado);
        Assert.Contains("\"utilidad\":{", delDueno);
        Assert.Contains("costoCentavos", delDueno);
        // Lo demás, igual para los dos.
        var (deUno, deOtro) = (System.Text.Json.JsonSerializer.Deserialize<ReporteVentasDto>(delEncargado, System.Text.Json.JsonSerializerOptions.Web)!,
            System.Text.Json.JsonSerializer.Deserialize<ReporteVentasDto>(delDueno, System.Text.Json.JsonSerializerOptions.Web)!);
        Assert.Equal((deOtro.Actual.VentaCentavos, deOtro.Actual.Tickets), (deUno.Actual.VentaCentavos, deUno.Actual.Tickets));
        Assert.NotNull(deUno.Anterior);
    }

    [Fact]
    public async Task En_el_piloto_solo_lo_de_la_caja_2_sin_el_historial_ni_el_anio_pasado()
    {
        await using var conPiloto = await ServicioDePrueba.CrearAsync(
            ("Eleventa:Escribir", "true"), ("Eleventa:Activo", "false"), ("Eleventa:Servidor", "127.0.0.1"), ("Eleventa:Puerto", "1"),
            ("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"), ("Eleventa:Usuario", "SYSDBA"), ("Eleventa:Clave", "masterkey"), ("Eleventa:Caja", "4"),
            ("Eleventa:Cajero", "7"), ("Eleventa:SegundosAlCobrar", "10"), ("Eleventa:SegundosEntreEnvios", "3600"), ("Eleventa:Minutos", "600"),
            ("Eleventa:RespaldoObligatorio", "false"));
        var p = await PuestoAsync(conPiloto);
        await VentasAsync(conPiloto, p);

        var hoy = await Leer<ReporteVentasDto>(await p.Http.GetAsync(p.Ruta(p.Dueno)));
        // La papelería de prueba trae 30 días del historial de eleventa (de toda la tienda): en el piloto no cuenta.
        var anio = await Leer<ReporteVentasDto>(await p.Http.GetAsync(p.Ruta(p.Dueno, "&periodo=Anio")));

        Assert.True(hoy.Piloto);
        Assert.Equal((2500L, 1, 0L), (hoy.Actual.VentaCentavos, hoy.Actual.Tickets, hoy.Actual.DelHistorialCentavos));
        Assert.Null(hoy.Anterior);
        Assert.All(hoy.Serie, punto => Assert.Null(punto.AnteriorCentavos));
        Assert.Equal(0L, anio.Actual.DelHistorialCentavos);
        Assert.Null(anio.Anterior);
    }

    /// <summary>
    /// Una caja 2 que solo lee el catálogo de eleventa (la lectura prendida, sin mandarle sus ventas) también es del piloto: la
    /// principal sigue vendiendo en eleventa y el historial es de toda la tienda (revisión del PR #131, 2026-10-22). La lectura
    /// marca el piloto, como en las promociones y la trastienda; mandar las ventas, también.
    /// </summary>
    [Fact]
    public async Task Con_solo_la_lectura_de_eleventa_tambien_es_el_piloto()
    {
        await using var conLectura = servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true").UseSetting("Eleventa:Escribir", "false"));
        var p = await PuestoAsync(conLectura.CreateClient());
        await VentasAsync(servicio, p);

        var hoy = await Leer<ReporteVentasDto>(await p.Http.GetAsync(p.Ruta(p.Dueno)));

        Assert.True(hoy.Piloto);
        Assert.Equal(0L, hoy.Actual.DelHistorialCentavos);
        Assert.Null(hoy.Anterior);
        Assert.All(hoy.Departamentos, d => Assert.Null(d.AnteriorCentavos));
    }
}
