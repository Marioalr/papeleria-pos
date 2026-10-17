using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El puente con el Firebird de la tienda (2.0.1, protocolo 10), por un intermediario delante del Firebird de prueba: con
/// ese protocolo FirebirdClient no sabe cancelar una llamada, y pedírselo truena el proceso del servicio (revisión del
/// 2026-10-17). Si vuelve a pasar, esta prueba tumba la corrida entera.
/// </summary>
public sealed class FirebirdDeLaTiendaTests
{
    [FactConFirebird]
    public async Task Con_el_protocolo_de_Firebird_2_0_una_escritura_que_se_queda_callada_no_truena_el_servicio()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        using var proxy = new ProxyFirebird(e.Servidor.Servidor, e.Servidor.Puerto) { SoloProtocolo10 = true };
        await using var servicio = await ServicioDePrueba.CrearAsync(
            [.. Ajustes(e.Servidor with { Servidor = "127.0.0.1", Puerto = proxy.Puerto }), ("Eleventa:SegundosMaximos", "2"), ("Eleventa:SegundosAlCobrar", "5")]);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        // Con el protocolo de Firebird 2.0, una venta entra como siempre.
        var primera = await CobrarAsync(c);
        Assert.NotNull(primera.FolioEleventa);
        Assert.NotEmpty(proxy.Aceptados);
        Assert.All(proxy.Aceptados, v => Assert.Equal(10, v));

        // La red se cae a media escritura de la siguiente: se deja de esperar (sin cancelarla) y se vuelve a intentar.
        proxy.Disparador = "INSERT INTO VENTATICKETS_ARTICULOS";
        var segunda = await CobrarAsync(c);
        var esperando = await EsperarAsync(() => EnviosAsync(c), x => x.PorAtender.Any(p => p.Intentos >= 1 && p.Error is not null));
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        var otraVez = await EsperarAsync(() => EnviosAsync(c), x => x.PorAtender.Any(p => p.Intentos >= 2));

        Assert.True(proxy.Callado);
        Assert.Null(segunda.FolioEleventa);
        Assert.Equal("El eleventa de 127.0.0.1 no terminó de contestar a tiempo: se vuelve a intentar.", esperando.PorAtender.Single().Error);
        Assert.Equal((EstadoEnvioEleventa.Pendiente, 2), otraVez.PorAtender.Select(p => (p.Estado, p.Intentos)).Single());
    }

    [FactConFirebird]
    public async Task Con_el_protocolo_de_Firebird_2_0_una_lectura_en_vivo_que_se_queda_callada_no_detiene_la_devolucion_ni_la_bandeja()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        using var proxy = new ProxyFirebird(e.Servidor.Servidor, e.Servidor.Puerto) { SoloProtocolo10 = true };
        await using var servicio = await ServicioDePrueba.CrearAsync(
            [.. Ajustes(e.Servidor with { Servidor = "127.0.0.1", Puerto = proxy.Puerto }), ("Eleventa:SegundosMaximos", "2"), ("Eleventa:SegundosAlCobrar", "5")]);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await CobrarAsync(c);
        Assert.NotNull(ticket.FolioEleventa);

        // La red se cae justo cuando la ventana de devoluciones revisa en vivo lo devuelto allá: se deja de esperar a los 3
        // segundos y se deja pasar, como cuando eleventa no contesta. Cerrar su conexión esperaba sin fin, con el candado de
        // la bandeja tomado.
        proxy.Disparador = "FROM DEVOLUCIONES";
        var buscado = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}")
            .WaitAsync(TimeSpan.FromMinutes(1)));
        Assert.True(proxy.Callado);
        var comprobante = await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No pinta", [new LineaDevolucionSolicitada(buscado.Lineas[0].LineaId, 1000)]))
            .WaitAsync(TimeSpan.FromMinutes(1)));

        // Y la bandeja sigue: intenta mandarla y espera a la red.
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        var esperando = await EsperarAsync(() => EnviosAsync(c), x => x.PorAtender.Any(p => p.Tipo == TipoEnvioEleventa.Devolucion && p.Intentos >= 1));
        Assert.NotEqual(0, comprobante.TotalCentavos);
        Assert.Equal("El eleventa de 127.0.0.1 no terminó de contestar a tiempo: se vuelve a intentar.",
            esperando.PorAtender.Single(p => p.Tipo == TipoEnvioEleventa.Devolucion).Error);
    }

    [FactConFirebird]
    public async Task Una_venta_que_entro_a_eleventa_sin_que_aqui_se_supiera_y_se_cancelo_aqui_se_cancela_alla_y_no_se_descarta()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        using var proxy = new ProxyFirebird(e.Servidor.Servidor, e.Servidor.Puerto) { SoloProtocolo10 = true };
        await using var servicio = await ServicioDePrueba.CrearAsync(
            [.. Ajustes(e.Servidor with { Servidor = "127.0.0.1", Puerto = proxy.Puerto }), ("Eleventa:SegundosMaximos", "2"), ("Eleventa:SegundosAlCobrar", "5")]);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        async Task<double> ExistenciaAsync() => Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10"));
        var antes = await ExistenciaAsync();

        // Eleventa guarda la venta, y la red se cae justo después (al revisar su folio): aquí no se sabe si entró.
        proxy.Disparador = "SELECT COUNT(*) FROM VENTATICKETS WHERE FOLIO";
        var ticket = await CobrarAsync(c, plumas: 2);
        await EsperarAsync(() => EnviosAsync(c), x => x.PorAtender.Any(p => p.Tipo == TipoEnvioEleventa.Venta && p.Intentos >= 1 && p.Error is not null));
        Assert.Null(ticket.FolioEleventa);
        Assert.Equal(antes - 2, await ExistenciaAsync(), 3);

        // Aquí se cancela. Mientras eleventa no contesta, no se sabe si la venta llegó: no se descarta, se vuelve a buscar.
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion",
            new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió")));
        for (var vuelta = 2; vuelta <= 3; vuelta++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
            var intentos = vuelta;
            var sinSaber = await EsperarAsync(() => EnviosAsync(c), x => x.PorAtender.Any(p => p.Tipo == TipoEnvioEleventa.Venta && p.Intentos >= intentos));
            Assert.Equal(EstadoEnvioEleventa.Pendiente, sinSaber.PorAtender.Single(p => p.Tipo == TipoEnvioEleventa.Venta).Estado);
        }

        // Vuelve la red.
        proxy.Reanudar();
        await EsperarAsync(async () =>
        {
            await c.Http.PostAsync("api/eleventa/enviar", null);
            return await EnviosAsync(c);
        }, x => x.Pendientes == 0);

        // Sí estaba en eleventa: no se descarta, y su cancelación llega como la de cualquier venta que ya llegó.
        await using var db = servicio.Base();
        var envios = await db.EnviosAEleventa.Where(x => x.VentaId == ticket.VentaId).OrderBy(x => x.CreadoUtc).ToListAsync();
        Assert.Equal([(TipoEnvioEleventa.Venta, EstadoEnvioEleventa.Enviada), (TipoEnvioEleventa.Cancelacion, EstadoEnvioEleventa.Enviada)],
            envios.Select(x => (x.Tipo, x.Estado)));
        var enEleventa = envios[0].TicketEleventa;
        Assert.NotNull(enEleventa);
        Assert.Equal("t", ((string)(await e.ValorAsync("SELECT ESTA_CANCELADO FROM VENTATICKETS WHERE ID = @t", ("t", enEleventa)))!).Trim());
        Assert.Equal(antes, await ExistenciaAsync(), 3);
    }
}
