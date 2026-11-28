using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El conteo del celular en el piloto, con un eleventa de verdad (el Firebird de prueba) y la lectura de verdad, no la de mentiras de
/// <see cref="ConteosDelCelularTests"/> (2026-11-27): aceptar pide la lectura en ese momento, así que lo que eleventa vendió entre
/// contar y aceptar ya cuenta sin esperar la vuelta de cada 5 minutos; no se le escribe nada a eleventa; y después de capturar la
/// diferencia a mano allá, la lectura la trae y el siguiente conteo cuadra.
/// </summary>
public sealed class ConteoDelCelularConEleventaTests
{
    private static async Task<decimal> HayAsync(ServicioDePrueba servicio, Guid variante)
    {
        await using var db = servicio.Base();
        return (await db.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == variante)).Cantidad.EnUnidades;
    }

    [FactConFirebird]
    public async Task Aceptar_lee_eleventa_en_el_momento_no_le_escribe_y_lo_capturado_a_mano_llega_con_su_lectura()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        // El piloto completo: lee y manda las ventas. Lee solo cuando se le pide (cada 600 minutos).
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var pluma = await MapearAsync(servicio);
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        await EsperarAsync(async () => (await http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!, x => x.Lecturas >= 1 && x.Bien);
        Assert.Equal(50m, await HayAsync(servicio, pluma));
        // El turno, de quien puede aceptar conteos; y el celular.
        await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, encargado.UsuarioId, 50000)));
        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        var historial = await e.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_HISTORIAL");

        // Se cuentan 45 y la lista llega. Después eleventa vende 2 (su existencia baja a 48): aquí todavía dice 50.
        var lista = new SolicitudMandarConteo(Guid.NewGuid(), caja.Id, null, null, [new RenglonMandado(pluma, null, null, 45_000)]);
        Assert.Equal("recibida", (await Leer<EstadoConteoDto>(await celular.PostAsJsonAsync($"api/cajas/{alta.Caja.Id}/mandar-conteo", lista))).Estado);
        await e.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = 48 WHERE PRODUCTO_ID = 10");
        Assert.Equal(50m, await HayAsync(servicio, pluma));

        // Aceptar lee eleventa ya: lo que vendió cuenta como movido desde que se contó (45 − 2 = 43) y la diferencia es contra sus 48.
        var aceptada = await Leer<EstadoConteoDto>(await http.PostAsJsonAsync($"api/cajas/{caja.Id}/conteos-recibidos/{lista.Id}/aceptar",
            new SolicitudAceptarConteo(encargado.UsuarioId, [new RenglonAceptado(pluma, 45_000)])));
        Assert.Equal(("aceptada", "por-capturar", "Por capturar en eleventa: -5."), (aceptada.Estado, aceptada.Renglones[0].Eleventa, aceptada.Renglones[0].Nota));
        var renglon = Assert.Single(Assert.Single(await Leer<List<ConteoPorCapturarDto>>(await http.GetAsync($"api/cajas/{caja.Id}/conteos-por-capturar"))).Renglones);
        Assert.Equal((45m, -2m, 48m, -5m, 43m), (renglon.Contado, renglon.Desde, renglon.Habia, renglon.Diferencia, renglon.DebeQuedar));
        // Aquí quedó lo que dice eleventa (por la lectura, no por el conteo), y a eleventa no se le escribió nada.
        Assert.Equal(48m, await HayAsync(servicio, pluma));
        Assert.Equal(48d, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")));
        Assert.Equal(historial, await e.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_HISTORIAL"));
        await using (var db = servicio.Base())
        {
            Assert.False(await db.EnviosAEleventa.AnyAsync());
            Assert.False(await db.MovimientosInventario.AnyAsync(m => m.DocumentoId == lista.Id));
        }

        // Se captura a mano en eleventa (48 − 5 = 43) y «Ya lo capturé»: pide la lectura, que trae la existencia nueva.
        await e.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = 43 WHERE PRODUCTO_ID = 10");
        var cerrada = await Leer<EstadoConteoDto>(await http.PostAsJsonAsync($"api/cajas/{caja.Id}/conteos-por-capturar/{lista.Id}/capturado",
            new SolicitudConteoCapturado(encargado.UsuarioId)));
        Assert.Equal(("capturado", true), (cerrada.Renglones[0].Eleventa, cerrada.Renglones[0].Aplicado));
        Assert.Equal(43m, await EsperarAsync(() => HayAsync(servicio, pluma), hay => hay == 43m));

        // Otro conteo después: hay 43 y eleventa dice 43. El ajuste que anotó la lectura no lo estorba: cuadra, nada que capturar.
        var otra = new SolicitudMandarConteo(Guid.NewGuid(), caja.Id, null, null, [new RenglonMandado(pluma, null, null, 43_000)]);
        await Leer<EstadoConteoDto>(await celular.PostAsJsonAsync($"api/cajas/{alta.Caja.Id}/mandar-conteo", otra));
        var revisar = await Leer<ConteoPorRevisarDto>(await http.GetAsync($"api/cajas/{caja.Id}/conteos-recibidos/{otra.Id}"));
        Assert.Equal((43m, 0m, 43m, null, null), (revisar.Renglones[0].Contado, revisar.Renglones[0].Desde, revisar.Renglones[0].Hoy, revisar.Renglones[0].Superado,
            revisar.SinLectura));
        var cuadra = await Leer<EstadoConteoDto>(await http.PostAsJsonAsync($"api/cajas/{caja.Id}/conteos-recibidos/{otra.Id}/aceptar",
            new SolicitudAceptarConteo(encargado.UsuarioId, [new RenglonAceptado(pluma, 43_000)])));
        Assert.Equal((true, null, null), (cuadra.Renglones[0].Aplicado, cuadra.Renglones[0].Eleventa, cuadra.Renglones[0].Nota));
        Assert.Empty(await Leer<List<ConteoPorCapturarDto>>(await http.GetAsync($"api/cajas/{caja.Id}/conteos-por-capturar")));
    }
}
