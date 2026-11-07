using System.Net.Http.Json;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Auditoría del 2026-11-04: si la lectura de eleventa fallaba, la caja seguía cobrando con precios viejos, horas o días, y nadie se
/// enteraba: el motivo solo quedaba en el registro. Seguir vendiendo es la decisión; que no se avise, no. Después de tres vueltas
/// sin leer (unos 15 minutos), el chip «Eleventa» lo dice, y no detiene la venta.
/// </summary>
public sealed class LecturaQueFallaTests
{
    private static async Task<PuenteEleventaDto> PantallaAsync(Caja c) => (await c.Http.GetFromJsonAsync<PuenteEleventaDto>("api/eleventa/pantalla"))!;


    [Fact]
    public async Task Tres_vueltas_sin_leer_y_el_chip_lo_dice()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        // La primera, la de cuando arranca el servicio.
        await EsperarAsync(async () => (await c.Http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!.Error, e => e is not null);
        Assert.DoesNotContain(await PantallaAsync(c) is var p ? p.Avisos : [], a => a.Contains("no se ha podido leer"));

        var pantalla = await EsperarAsync(async () =>
        {
            await c.Http.PostAsync("api/eleventa/leer", null);
            return await PantallaAsync(c);
        }, x => x.Avisos.Any(a => a.Contains("no se ha podido leer")));

        var aviso = pantalla.Avisos.Single(a => a.Contains("no se ha podido leer"));
        Assert.Contains("No contesta el eleventa", aviso);
        Assert.Contains("La caja sigue vendiendo", aviso);
        // No detiene la venta.
        Assert.NotNull(await CobrarAsync(c));
    }

    [FactConFirebird]
    public async Task Dice_de_que_hora_son_los_precios_y_se_quita_al_volver_a_leer()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        Assert.Empty((await PantallaAsync(c)).Avisos);

        // Algo que hace tronar la lectura (como un cambio en la base).
        await e.EjecutarAsync("ALTER TABLE PRODUCTOS ALTER COLUMN DESCRIPCION TO DESCRIPCION_ANTES");
        var pantalla = await EsperarAsync(async () =>
        {
            await c.Http.PostAsync("api/eleventa/leer", null);
            return await PantallaAsync(c);
        }, x => x.Avisos.Any(a => a.Contains("no se ha podido leer")));

        var aviso = pantalla.Avisos.Single(a => a.Contains("no se ha podido leer"));
        Assert.StartsWith("Los precios de esta caja son de las ", aviso);
        Assert.Contains(LectorEleventa.ContestoConError, aviso);

        await e.EjecutarAsync("ALTER TABLE PRODUCTOS ALTER COLUMN DESCRIPCION_ANTES TO DESCRIPCION");
        await LeerAsync(c);
        Assert.Empty((await PantallaAsync(c)).Avisos);
    }
}
