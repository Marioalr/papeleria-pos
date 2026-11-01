using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Los costos de las impresiones en Configuración › Impresoras e impresiones (2026-10-31): son del negocio, así que el servicio solo
/// se los da a quien tiene «Ver costos, utilidad y margen», y los cambia quien además tiene «Configurar la impresora». Qué cambió queda
/// en la bitácora, sin los montos (revisión del 2026-10-31), y los de antes, en su historial; lo demás de los costos (la calibración,
/// la luz…) se queda como estaba.
/// </summary>
public sealed class CostosImpresionApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Dueno, SesionDto Cajero, SesionDto Encargado)> AbrirAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        return (http, caja, await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))),
            await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))),
            await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222"))));
    }

    private static string Ruta(CajaDto caja) => $"api/sucursales/{caja.SucursalId}/impresiones/costos";

    [Fact]
    public async Task Los_ve_solo_quien_tiene_ver_costos_y_los_cambia_quien_ademas_configura_la_impresora()
    {
        var (http, caja, dueno, cajero, encargado) = await AbrirAsync();
        // El encargado ve los costos, pero no configura la impresora.
        await using (var db = servicio.Base())
        {
            var permiso = await db.PermisosUsuario.SingleAsync(p => p.UsuarioId == encargado.UsuarioId && p.Accion == AccionControlada.VerUtilidad);
            permiso.Nivel = NivelPermiso.Permitido;
            await db.SaveChangesAsync();
        }

        var delCajero = await http.GetAsync($"{Ruta(caja)}?usuarioId={cajero.UsuarioId}");
        var delEncargado = await Leer<EstadoCostosImpresionDto>(await http.GetAsync($"{Ruta(caja)}?usuarioId={encargado.UsuarioId}"));
        var delDueno = await Leer<EstadoCostosImpresionDto>(await http.GetAsync($"{Ruta(caja)}?usuarioId={dueno.UsuarioId}"));
        var cambioDelEncargado = await http.PutAsJsonAsync(Ruta(caja), new SolicitudCostosImpresion(encargado.UsuarioId, delEncargado.Costos));

        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal("no-permitido", (await delCajero.Content.ReadFromJsonAsync<ErrorDto>())!.Codigo);
        Assert.False(delEncargado.PuedeCambiar);
        Assert.Equal(HttpStatusCode.Forbidden, cambioDelEncargado.StatusCode);
        Assert.True(delDueno.PuedeCambiar);
        Assert.False(delDueno.DeControlPrint);
        Assert.Equal(["Negro", "Cian", "Magenta", "Amarillo"], delDueno.Costos.Cartuchos.Select(c => c.Tinta));
        Assert.Equal([10000, 7000, 7000, 7000], delDueno.Costos.Cartuchos.Select(c => c.Rendimiento));
        Assert.Equal(delDueno.Costos.Cartuchos, delEncargado.Costos.Cartuchos);
    }

    [Fact]
    public async Task Al_cambiarlos_se_guardan_con_su_historial_y_su_bitacora_y_lo_demas_se_queda()
    {
        var (http, caja, dueno, _, _) = await AbrirAsync();
        var antes = await Leer<EstadoCostosImpresionDto>(await http.GetAsync($"{Ruta(caja)}?usuarioId={dueno.UsuarioId}"));
        var papeles = antes.Costos.Papeles.Select(p => p.Tamano == "Carta" && p.Grupo == "Normal" ? p with { PrecioPaquete = 120, HojasPaquete = 500 } : p).ToList();
        int historialAntes;
        await using (var db = servicio.Base())
            historialAntes = await db.Set<HistorialCostosImpresion>().CountAsync(h => h.SucursalId == caja.SucursalId);

        var primero = await Leer<EstadoCostosImpresionDto>(await http.PutAsJsonAsync(Ruta(caja), new SolicitudCostosImpresion(dueno.UsuarioId,
            antes.Costos with { Papeles = papeles, Margen = MargenDeImpresiones.Unico, MargenUnico = 90 })));
        var segundo = await Leer<EstadoCostosImpresionDto>(await http.PutAsJsonAsync(Ruta(caja), new SolicitudCostosImpresion(dueno.UsuarioId,
            primero.Costos with { Cartuchos = [primero.Costos.Cartuchos[0] with { Precio = 1150 }, .. primero.Costos.Cartuchos.Skip(1)] })));

        Assert.Equal(1150, segundo.Costos.Cartuchos[0].Precio);
        Assert.Equal((MargenDeImpresiones.Unico, 90m), (segundo.Costos.Margen, segundo.Costos.MargenUnico));
        Assert.DoesNotContain("Papel Carta normal", segundo.Faltan);
        Assert.NotNull(segundo.GuardadosLocal);
        await using var lectura = servicio.Base();
        // Los de antes de cada cambio, en su historial (el primer guardado no reemplaza a nadie si nunca se habían guardado).
        var historial = await lectura.Set<HistorialCostosImpresion>().Where(h => h.SucursalId == caja.SucursalId).ToListAsync();
        Assert.InRange(historial.Count - historialAntes, 1, 2);
        Assert.All(historial.Skip(historialAntes), h => Assert.Equal("Configuración", h.PorLosDe));
        // Lo demás se queda: la calibración, la luz, la depreciación y los tipos de papel.
        var guardados = JsonSerializer.Deserialize<CostSettings>(
            (await lectura.Set<ConfiguracionImpresion>().SingleAsync(c => c.SucursalId == caja.SucursalId)).CostosJson, CostSettings.Json)!;
        var deFabrica = new CostSettings();
        Assert.Equal((deFabrica.Calibration.K, deFabrica.Power.Watts, deFabrica.Depreciation.PurchasePrice, deFabrica.Media.Count),
            (guardados.Calibration.K, guardados.Power.Watts, guardados.Depreciation.PurchasePrice, guardados.Media.Count));
        Assert.Equal(ScalingMode.None, guardados.Scaling);
        // En la bitácora, qué cambió, sin los montos; uno por cada vez que se guardó.
        var bitacora = await lectura.Bitacora.Where(b => b.Accion == AccionControlada.ConfigurarImpresora).Select(b => b.Detalle).ToListAsync();
        Assert.Contains(bitacora, d => d == "Cambió los costos de las impresiones: el precio del cartucho negro");
        Assert.Contains(bitacora, d => d == "Cambió los costos de las impresiones: el precio del paquete de papel Carta normal, cómo se calcula el margen y el margen para todo");
    }

    /// <summary>
    /// Los costos son del negocio: el editor solo los da a quien tiene «Ver costos, utilidad y margen». Cada cambio queda en la bitácora,
    /// que lee quien tiene «Consultar la bitácora» (el encargado, de inicio) aunque no vea los costos: ahí no van los montos (revisión
    /// independiente del 2026-10-31).
    /// </summary>
    [Fact]
    public async Task Los_costos_no_se_leen_en_la_bitacora_sin_ver_costos()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var ruta = $"api/sucursales/{caja.SucursalId}/impresiones/costos";
        var antes = await Leer<EstadoCostosImpresionDto>(await http.GetAsync($"{ruta}?usuarioId={dueno.UsuarioId}"));
        await Leer<EstadoCostosImpresionDto>(await http.PutAsJsonAsync(ruta, new SolicitudCostosImpresion(dueno.UsuarioId,
            antes.Costos with { Cartuchos = [antes.Costos.Cartuchos[0] with { Precio = 1150 }, .. antes.Costos.Cartuchos.Skip(1)] })));

        var costosDelEncargado = await http.GetAsync($"{ruta}?usuarioId={encargado.UsuarioId}");
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow)).ToString("yyyy-MM-dd");
        var bitacora = await Leer<List<BitacoraDto>>(await http.GetAsync(
            $"api/cajas/{caja.Id}/bitacora?usuarioId={encargado.UsuarioId}&desde={hoy}&hasta={hoy}"));

        // El encargado no ve los costos en su editor…
        Assert.Equal(HttpStatusCode.Forbidden, costosDelEncargado.StatusCode);
        // …ni en la bitácora: ahí dice qué cambió, nada más.
        Assert.Contains(bitacora, b => b.Detalle == "Cambió los costos de las impresiones: el precio del cartucho negro");
        Assert.DoesNotContain(bitacora, b => b.Detalle.Contains("1,150.00", StringComparison.Ordinal) || b.Detalle.Contains("1,200.00", StringComparison.Ordinal));
    }

    /// <summary>
    /// El precio que se cobra con los costos cambiados en la Configuración es el del cálculo del módulo (CostCalculator) con esos
    /// costos: lo que se guarda es lo que se escribió, y lo demás se queda como estaba.
    /// </summary>
    [Fact]
    public async Task El_precio_de_una_impresion_sale_del_calculo_del_modulo_con_los_costos_cambiados()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var ruta = $"api/sucursales/{caja.SucursalId}/impresiones/costos";
        var antes = (await Leer<EstadoCostosImpresionDto>(await http.GetAsync($"{ruta}?usuarioId={dueno.UsuarioId}"))).Costos;
        var pedidos = antes with
        {
            Cartuchos = [antes.Cartuchos[0] with { Precio = 1150.50m, Rendimiento = 9000 }, .. antes.Cartuchos.Skip(1)],
            Papeles = antes.Papeles.Select(p => p.Tamano == "Carta" && p.Grupo == "Normal" ? p with { PrecioPaquete = 125, HojasPaquete = 500 } : p).ToList(),
            Margen = MargenDeImpresiones.Unico,
            MargenUnico = 90,
            MargenPapel = 40,
            CobroMinimo = 3,
            MinimoATodos = true,
            RedondeoA = 0.5m,
        };
        await Leer<EstadoCostosImpresionDto>(await http.PutAsJsonAsync(ruta, new SolicitudCostosImpresion(dueno.UsuarioId, pedidos)));
        var trabajo = new TrabajoMedidoDto(Guid.NewGuid(), caja.Id, null, "PC-PRUEBA", "Cola", 7, "Revisión de costos", "cajero", DateTime.UtcNow,
            EstadoTrabajo.Impreso, new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, true, false, 2, 1, true),
            [new CoberturaPaginaDto(0.05, 0.05, 0.05, 0.05, 0.1, 93.5, true), new CoberturaPaginaDto(0, 0, 0, 0.12, 0.12, 93.5, false)], true, null, null);

        var cobrado = await Leer<JsonElement>(await http.PostAsJsonAsync("api/impresiones", trabajo));

        await using var db = servicio.Base();
        var guardados = JsonSerializer.Deserialize<CostSettings>(
            (await db.Set<ConfiguracionImpresion>().SingleAsync(c => c.SucursalId == caja.SucursalId)).CostosJson, CostSettings.Json)!;
        Assert.Equal((1150.50m, 9000, 0.25m, ScalingMode.None, 90m, (decimal?)40m, 3m, MinimumScope.AllJobs, 0.5m),
            (guardados.Black.Price, guardados.Black.IsoYield, guardados.Papers.Single(p => p.Size == PaperSize.Carta && p.Group == "Normal").PricePerSheet,
                guardados.Scaling, guardados.MarginPercent, guardados.PaperMarginPercent, guardados.MinimumPrice, guardados.MinimumApplies, guardados.RoundTo));
        var esperado = CostCalculator.Calculate(RegistroImpresiones.Analisis(trabajo), guardados).Price;
        Assert.Equal((long)Math.Round(esperado * 100, MidpointRounding.AwayFromZero), cobrado.GetProperty("precioCentavos").GetInt64());
    }

    [Fact]
    public async Task Lo_que_no_sirve_se_dice_y_no_se_guarda()
    {
        var (http, caja, dueno, _, _) = await AbrirAsync();
        var antes = await Leer<EstadoCostosImpresionDto>(await http.GetAsync($"{Ruta(caja)}?usuarioId={dueno.UsuarioId}"));

        var respuesta = await http.PutAsJsonAsync(Ruta(caja), new SolicitudCostosImpresion(dueno.UsuarioId,
            antes.Costos with { Cartuchos = [antes.Costos.Cartuchos[0] with { Rendimiento = 0 }, .. antes.Costos.Cartuchos.Skip(1)] }));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("Lo que rinde el cartucho negro va de 1 a 1,000,000 páginas (lo dice su caja).",
            (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal(antes.Costos.Cartuchos[0],
            (await Leer<EstadoCostosImpresionDto>(await http.GetAsync($"{Ruta(caja)}?usuarioId={dueno.UsuarioId}"))).Costos.Cartuchos[0]);
    }
}
