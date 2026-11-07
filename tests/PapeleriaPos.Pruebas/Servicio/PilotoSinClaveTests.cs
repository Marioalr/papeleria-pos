using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Auditoría del 2026-11-04: con <c>Escribir</c> prendido y sin la clave o el cajero, la caja se porta como piloto (solo efectivo,
/// corte en eleventa) y cobra, pero lo vendido no entraba a la bandeja: eleventa nunca lo recibía, ni al corregirlo, y nadie lo
/// decía. Ahora entra a la bandeja, el chip lo dice y se manda al corregir la configuración.
/// </summary>
public sealed class PilotoSinClaveTests
{
    [Fact]
    public async Task Sin_la_clave_lo_vendido_espera_en_la_bandeja_y_el_chip_lo_dice()
    {
        var ajustes = Ajustes().Where(a => a.Item1 != "Eleventa:Clave").ToArray();
        await using var servicio = await ServicioDePrueba.CrearAsync(ajustes);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        var ticket = await CobrarAsync(c);

        Assert.Null(ticket.FolioEleventa);
        var envios = await EnviosAsync(c);
        Assert.False(envios.Activo);
        Assert.Equal(1, envios.Pendientes);
        await using (var db = servicio.Base())
            Assert.Equal(EstadoEnvioEleventa.Pendiente, (await db.EnviosAEleventa.SingleAsync(e => e.VentaId == ticket.VentaId)).Estado);
        var pantalla = (await c.Http.GetFromJsonAsync<PuenteEleventaDto>("api/eleventa/pantalla"))!;
        Assert.Contains(pantalla.Avisos, a => a.Contains("Falta el servidor, la base, la clave, la caja o el cajero") && a.Contains("se manda solo"));
    }

    [Fact]
    public async Task Sin_Escribir_nada_entra_a_la_bandeja()
    {
        var ajustes = Ajustes().Where(a => a.Item1 != "Eleventa:Escribir").ToArray();
        await using var servicio = await ServicioDePrueba.CrearAsync(ajustes);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        await CobrarAsync(c);

        Assert.Equal(0, (await EnviosAsync(c)).Pendientes);
        Assert.Empty((await c.Http.GetFromJsonAsync<PuenteEleventaDto>("api/eleventa/pantalla"))!.Avisos);
    }
}
