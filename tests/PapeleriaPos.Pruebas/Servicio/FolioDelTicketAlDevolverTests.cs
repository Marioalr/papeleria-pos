using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El ensayo de la matriz (2026-11-27): la caja daba sus folios desde 1 en una base recién importada (eleventa iba en miles), y al devolver
/// con el folio «2» salía la venta del ensayo junto al ticket 2 de eleventa de 2022. Regla del dueño: «el POS no puede generar el folio 2
/// porque actualmente ninguna sucursal empieza en 0». Los folios de la caja siguen a eleventa, y la venta tiene uno solo.
/// </summary>
public sealed class FolioDelTicketAlDevolverTests
{
    /// <summary>El historial de eleventa que deja la importación (un ticket con su folio y su mapeo), hasta <paramref name="ultimo"/>.</summary>
    private static async Task HistorialDeEleventaAsync(ServicioDePrueba servicio, int ultimo, int? consecutivoYaUsado = null)
    {
        await using var db = servicio.Base();
        var sucursal = await db.Sucursales.OrderBy(s => s.Clave != "M").Select(s => s.Id).FirstAsync();
        foreach (var folio in new[] { ultimo - 1, ultimo })
        {
            var venta = new VentaHistorica { SucursalId = sucursal, FechaUtc = DateTime.UtcNow.AddDays(-1), FolioOrigen = folio.ToString() };
            db.VentasHistoricas.Add(venta);
            db.MapeosImportacion.Add(new MapeoImportacion { Origen = RegistroCaja.DeEleventa, Entidad = nameof(VentaHistorica), IdOrigen = (folio + 7).ToString(), IdNuevo = venta.Id });
        }
        // Una base instalada antes de esto: su consecutivo ya iba en 1 o 2.
        if (consecutivoYaUsado is { } usado)
            db.Secuencias.Add(new Secuencia { Nombre = $"folio:{sucursal}:", Valor = usado });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task Despues_de_importar_eleventa_la_siguiente_venta_sigue_su_folio_aunque_eleventa_no_conteste(int? consecutivoYaUsado)
    {
        // Nadie escucha en el puerto de eleventa: el ticket sale con el folio de la caja.
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        await HistorialDeEleventaAsync(servicio, ultimo: 4812, consecutivoYaUsado);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        var primera = await CobrarAsync(c);
        var segunda = await CobrarAsync(c);

        Assert.Equal([("4813", (int?)null), ("4814", null)], new[] { primera, segunda }.Select(t => (t.FolioImpreso, t.FolioEleventa)));
        // Y con ese folio se encuentra para devolver.
        var abierta = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/4813?cajaId={c.Datos.Id}"));
        Assert.Equal((primera.VentaId, "4813"), ((Guid?)abierta.Id, abierta.Folio));
    }

    [FactConFirebird]
    public async Task La_venta_tiene_el_folio_de_eleventa_y_el_folio_2_es_solo_el_de_eleventa()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        // Un ticket de los primeros días de eleventa en la principal, con el folio 2 (el último de eleventa es el 41).
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN, TOTAL) VALUES (2, 1, 1, -1, 'f', @cuando, 20)",
            ("cuando", new DateTime(2022, 6, 11, 22, 5, 0)));
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        var primera = await CobrarAsync(c);
        var segunda = await CobrarAsync(c);

        // Un solo folio: el de la venta es el de eleventa, el que sigue a su último.
        Assert.Equal([("42", 42), ("43", 43)], new[] { primera, segunda }.Select(t => (t.Folio, t.FolioEleventa ?? 0)));
        Assert.Equal([42, 43], await FoliosEnEleventaAsync(e, [primera, segunda], servicio));

        // Con el folio del papel se encuentra, y dice ese.
        var con43 = await Leer<FolioEleventaDto>(await c.Http.GetAsync("api/eleventa/folio/43"));
        Assert.Equal("43", Assert.Single(con43.Tickets).FolioCaja);
        var abierta = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/-43?cajaId={c.Datos.Id}"));
        Assert.Equal((segunda.VentaId, "43"), ((Guid?)abierta.Id, abierta.Folio));

        // Con el 2 solo sale el ticket de eleventa de 2022.
        var con2 = await Leer<FolioEleventaDto>(await c.Http.GetAsync("api/eleventa/folio/2"));
        var viejo = Assert.Single(con2.Tickets);
        Assert.Equal(("Principal", (string?)null, new DateTime(2022, 6, 11, 22, 5, 0), 2000L), (viejo.Caja, viejo.FolioCaja, viejo.FechaLocal, viejo.TotalCentavos));
    }

    [FactConFirebird]
    public async Task Si_otra_caja_de_eleventa_ya_uso_el_siguiente_la_venta_toma_el_que_le_da_eleventa_antes_de_imprimir()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var primera = await CobrarAsync(c);
        // Mientras, la principal cobra en eleventa el 43, el que la caja iba a dar.
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN, TOTAL) VALUES (43, 1, 1, -1, 'f', @cuando, 20)",
            ("cuando", HoraLocal(1)));

        var segunda = await CobrarAsync(c);
        var tercera = await CobrarAsync(c);

        Assert.Equal(["42", "44", "45"], new[] { primera, segunda, tercera }.Select(t => t.FolioImpreso));
        Assert.Equal([42, 44, 45], new[] { primera, segunda, tercera }.Select(t => t.FolioEleventa ?? 0));
        await using var db = servicio.Base();
        Assert.Equal(44, (await db.Ventas.SingleAsync(v => v.Id == segunda.VentaId)).Folio);
    }

    [FactConFirebird]
    public async Task El_ticket_que_salio_sin_eleventa_lleva_su_folio_a_eleventa_y_si_ya_lo_uso_otra_caja_se_eligen_por_la_hora()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await CobrarAsync(c);
        await MandarAsync(c);

        // La bandeja está ocupada: el cobro no espera y los tickets salen con el folio de la caja, que sigue a eleventa.
        TicketDto libre, ocupado;
        using (await servicio.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.EnvioVentasEleventa>().CandadoAsync(CancellationToken.None))
        {
            libre = await CobrarAsync(c);
            ocupado = await CobrarAsync(c);
        }
        Assert.Equal([("43", (int?)null), ("44", null)], new[] { libre, ocupado }.Select(t => (t.FolioImpreso, t.FolioEleventa)));
        // Antes de que lleguen, la principal cobra en eleventa el 44.
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN, TOTAL) VALUES (44, 1, 1, -1, 'f', @cuando, 20)",
            ("cuando", HoraLocal(0)));
        await MandarAsync(c);

        // El 43 llega a eleventa con su folio; el 44 ya lo tenía la principal: allá toma el 45 y su papel sigue diciendo 44.
        Assert.Equal([43, 45], await FoliosEnEleventaAsync(e, [libre, ocupado], servicio));
        var con44 = await Leer<FolioEleventaDto>(await c.Http.GetAsync("api/eleventa/folio/44"));
        Assert.Equal([("Principal", (string?)null), ("Caja 1", "44")],
            con44.Tickets.Select(t => (t.Caja, t.FolioCaja)).OrderBy(t => t.FolioCaja ?? ""));
        // La siguiente ya va después de los dos.
        Assert.Equal("46", (await CobrarAsync(c)).FolioImpreso);
    }

    [FactConFirebird]
    public async Task La_lectura_de_eleventa_lleva_el_folio_de_la_caja_despues_de_lo_que_cobraron_sus_cajas()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        // La principal cobra en eleventa hasta el 60.
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN, TOTAL) VALUES (60, 1, 1, -1, 'f', @cuando, 20)",
            ("cuando", HoraLocal(0)));
        await LeerAsync(c);

        // Con la bandeja ocupada el ticket sale sin esperar a eleventa: con el folio de la caja, que ya va después.
        using (await servicio.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.EnvioVentasEleventa>().CandadoAsync(CancellationToken.None))
            Assert.Equal(("61", (int?)null), await CobrarAsync(c) is var t ? (t.FolioImpreso, t.FolioEleventa) : default);
    }

    /// <summary>El folio que tiene en eleventa cada venta (el de su envío).</summary>
    private static async Task<List<int>> FoliosEnEleventaAsync(EleventaDePrueba e, IEnumerable<TicketDto> tickets, ServicioDePrueba servicio)
    {
        await using var db = servicio.Base();
        var folios = new List<int>();
        foreach (var t in tickets)
        {
            var envio = await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == t.VentaId);
            folios.Add(Convert.ToInt32(await e.ValorAsync("SELECT FOLIO FROM VENTATICKETS WHERE ID = @id", ("id", envio.TicketEleventa!.Value))));
        }
        return folios;
    }
}
