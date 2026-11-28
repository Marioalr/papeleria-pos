using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El conteo del celular mandado a eleventa, de punta a punta (docs/propuestas/conteo-del-celular-a-eleventa.md, entrega 3; 2026-11-27),
/// con un eleventa de verdad (el Firebird de prueba), la lectura de verdad y la bandeja de verdad: el celular manda la lista, en la caja
/// se acepta y cada producto con diferencia queda en eleventa como un ajuste de inventario igual al que eleventa hace a mano (la
/// diferencia, nunca el total). Con el interruptor apagado o el puente sin escribir, nada llega a eleventa. Los datos son inventados.
/// </summary>
public sealed class ConteoMandadoAEleventaTests
{
    private const int Pluma = 10;
    private const int Cuaderno = 11;
    private const int Goma = 12;

    private sealed record Tienda(EleventaDePrueba E, ServicioDePrueba Servicio, HttpClient Http, CajaDto Caja, SesionDto Encargado, SesionDto Dueno,
        HttpClient Celular, Guid CelularId, Dictionary<int, Guid> Variantes) : IAsyncDisposable
    {
        public Guid De(int producto) => Variantes[producto];

        public async ValueTask DisposeAsync()
        {
            await Servicio.DisposeAsync();
            await E.DisposeAsync();
        }
    }

    /// <summary>
    /// El piloto completo (lee y manda) con la pluma (50), el cuaderno (12) y la goma (8) de eleventa, un turno del encargado y el
    /// celular. <paramref name="mandar"/>: «Mandar los conteos a eleventa», prendido por el dueño en la configuración.
    /// </summary>
    private static async Task<Tienda> TiendaAsync(bool mandar = true, bool escribir = true)
    {
        var (e, _) = await EleventaAsync();
        await e.ProductoAsync(Cuaderno, "CUAD-PRO", "Cuaderno profesional", depto: 5, costo: 18.5, precio: 30, existencia: 12);
        await e.ProductoAsync(Goma, "GOMA-BL", "Goma blanca", depto: 5, costo: 2, precio: 5, existencia: 8);
        await e.EjecutarAsync("SET GENERATOR GEN_INVENTARIO_AJUSTES_ID TO 411");
        var ajustes = Ajustes(e.Servidor, leer: true).Select(a => a.Item1 == "Eleventa:Escribir" ? (a.Item1, escribir ? "true" : "false") : a).ToArray();
        var servicio = await ServicioDePrueba.CrearAsync(ajustes);
        var pluma = await MapearAsync(servicio);
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        await EsperarAsync(async () => (await http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!, x => x.Lecturas >= 1 && x.Bien);
        await LeerEleventaAsync(http);
        await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, encargado.UsuarioId, 50000)));
        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        Dictionary<int, Guid> variantes;
        await using (var db = servicio.Base())
            variantes = (await db.MapeosImportacion.AsNoTracking().Where(m => m.Origen == SincronizacionEleventa.Origen && m.Entidad == nameof(Variante)).ToListAsync())
                .Where(m => m.IdOrigen is "10" or "11" or "12").ToDictionary(m => int.Parse(m.IdOrigen), m => m.IdNuevo);
        Assert.Equal(pluma, variantes[Pluma]);
        await servicio.CatalogoAlDiaAsync();
        var t = new Tienda(e, servicio, http, caja, encargado, dueno, celular, alta.Caja.Id, variantes);
        if (mandar)
            Assert.True((await Leer<MandarConteosDto>(await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion/mandar-conteos",
                new SolicitudMandarConteos(dueno.UsuarioId, true)))).Mandar);
        return t;
    }

    /// <summary>La lectura de eleventa, ya, esperándola.</summary>
    private static async Task LeerEleventaAsync(HttpClient http)
    {
        var antes = (await http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!.Lecturas;
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsync("api/eleventa/leer", null)).StatusCode);
        var despues = await EsperarAsync(async () => (await http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!, x => x.Lecturas > antes);
        Assert.True(despues.Lecturas > antes && despues.Bien, despues.Error);
    }

    private static async Task<decimal> HayAsync(Tienda t, int producto)
    {
        await using var db = t.Servicio.Base();
        return (await db.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == t.De(producto))).Cantidad.EnUnidades;
    }

    private static double Doble(object? valor) => Convert.ToDouble(valor);

    private static async Task<double> EnEleventaAsync(Tienda t, int producto) =>
        Doble(await t.E.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = @p", ("p", producto)));

    private static async Task<int> AjustesAsync(Tienda t) => Convert.ToInt32(await t.E.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_AJUSTES"));

    /// <summary>Una venta de eleventa (de otra caja de allá): su renglón del kardex, con la cantidad en positivo, y la existencia que baja.</summary>
    private static async Task EleventaVendeAsync(Tienda t, int producto, double piezas, DateTime cuandoLocal, int ticket = 900)
    {
        var antes = await EnEleventaAsync(t, producto);
        await t.E.EjecutarAsync("""
            INSERT INTO INVENTARIO_HISTORIAL (PRODUCTO_ID, CUANDO_FUE, CANTIDAD_ANTERIOR, CANTIDAD, DESCRIPCION, COSTO_UNITARIO, COSTO_DESPUES, VENTA_ID,
                CAJA_ID, VENTA_POR_KIT, USUARIO_ID, ALMACEN_ID)
            VALUES (@p, @cuando, @antes, @c, @que, 4, 4, @ticket, 1, 'f', 1, 1)
            """, ("p", producto), ("cuando", cuandoLocal), ("antes", antes), ("c", piezas), ("que", $"Venta #{ticket}"), ("ticket", ticket));
        await t.E.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = CANTIDAD_ACTUAL - @c WHERE PRODUCTO_ID = @p", ("c", piezas), ("p", producto));
    }

    /// <summary>Un ajuste hecho a mano en eleventa (Inventario › Ajustes): lo deja en <paramref name="nueva"/>.</summary>
    private static async Task AjusteAManoAsync(Tienda t, int producto, double nueva, DateTime cuandoLocal)
    {
        var habia = await EnEleventaAsync(t, producto);
        var id = Convert.ToInt32(await t.E.ValorAsync("SELECT GEN_ID(GEN_INVENTARIO_AJUSTES_ID, 1) FROM RDB$DATABASE"));
        await t.E.EjecutarAsync("""
            INSERT INTO INVENTARIO_AJUSTES (ID, FOLIO, CUANDO_FUE, PRODUCTO_ID, CANTIDAD, COSTO_UNITARIO, DESCRIPCION, MOTIVO, CAJA_ID, USUARIO_ID, ALMACEN_ID)
            VALUES (@id, @id, @cuando, @p, @c, 4, 'A mano', @motivo, 1, 1, 1)
            """, ("id", id), ("cuando", cuandoLocal), ("p", producto), ("c", nueva - habia), ("motivo", $"Ajuste de inventario #{id}"));
        await t.E.EjecutarAsync("""
            INSERT INTO INVENTARIO_HISTORIAL (PRODUCTO_ID, CUANDO_FUE, CANTIDAD_ANTERIOR, CANTIDAD, DESCRIPCION, COSTO_UNITARIO, COSTO_DESPUES, AJUSTE_ID,
                CAJA_ID, VENTA_POR_KIT, USUARIO_ID, ALMACEN_ID)
            VALUES (@p, @cuando, @habia, @c, @motivo, 4, 4, @id, 1, 'f', 1, 1)
            """, ("p", producto), ("cuando", cuandoLocal), ("habia", habia), ("c", nueva - habia), ("motivo", $"Ajuste de inventario #{id}"), ("id", id));
        await t.E.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = @n WHERE PRODUCTO_ID = @p", ("n", nueva), ("p", producto));
    }

    /// <summary>El celular manda la lista: cada producto con lo que hay en total.</summary>
    private static async Task<Guid> ContarAsync(Tienda t, DateTime? contadoUtc, params (int Producto, int Piezas)[] contado)
    {
        var lista = new SolicitudMandarConteo(Guid.NewGuid(), t.Caja.Id, contadoUtc, null,
            contado.Select(c => new RenglonMandado(t.De(c.Producto), null, null, c.Piezas * 1000L)).ToList());
        Assert.Equal("recibida", (await Leer<EstadoConteoDto>(await t.Celular.PostAsJsonAsync($"api/cajas/{t.CelularId}/mandar-conteo", lista))).Estado);
        return lista.Id;
    }

    private static Task<HttpResponseMessage> AceptarAsync(Tienda t, Guid lista, params (int Producto, int Piezas)[] aceptado) =>
        t.Http.PostAsJsonAsync($"api/cajas/{t.Caja.Id}/conteos-recibidos/{lista}/aceptar",
            new SolicitudAceptarConteo(t.Encargado.UsuarioId, aceptado.Select(a => new RenglonAceptado(t.De(a.Producto), a.Piezas * 1000L)).ToList()));

    /// <summary>Lo que ve el celular de su lista.</summary>
    private static async Task<EstadoConteoDto> DelCelularAsync(Tienda t, Guid lista) =>
        await Leer<EstadoConteoDto>(await t.Celular.GetAsync($"api/cajas/{t.CelularId}/conteos/{lista}"));

    /// <summary>Espera a que la bandeja termine con esa lista: ningún renglón «va a eleventa».</summary>
    private static async Task<EstadoConteoDto> YaNoVaAsync(Tienda t, Guid lista)
    {
        await t.Http.PostAsync("api/eleventa/enviar", null);
        return await EsperarAsync(() => DelCelularAsync(t, lista), x => x.Renglones.All(r => r.Eleventa != "pendiente"));
    }

    private static RenglonDeConteoDto De(Tienda t, EstadoConteoDto estado, int producto) => estado.Renglones.Single(r => r.VarianteId == t.De(producto));

    private static async Task<int> MovimientosDeLaLecturaAsync(Tienda t, int producto)
    {
        await using var db = t.Servicio.Base();
        return await db.MovimientosInventario.CountAsync(m => m.VarianteId == t.De(producto) && m.DocumentoTipo == OrigenDelMovimiento.LecturaEleventa);
    }

    [FactConFirebird]
    public async Task Aceptar_manda_cada_diferencia_como_ajuste_campo_por_campo_y_la_lectura_no_la_deshace_ni_la_duplica()
    {
        await using var t = await TiendaAsync();
        // Antes de contar, eleventa vendió 1 pluma que la lectura todavía no traía (aquí dice 50, allá 49): quien cuenta ya no la ve.
        await EleventaVendeAsync(t, Pluma, 1, HoraLocal(12), ticket: 901);
        Assert.Equal((50m, 49d), (await HayAsync(t, Pluma), await EnEleventaAsync(t, Pluma)));
        // Se cuentan 45 plumas, 14 cuadernos y 8 gomas hace 10 minutos. Después eleventa vende 2 plumas (allá quedan 47).
        var lista = await ContarAsync(t, DateTime.UtcNow.AddMinutes(-10), (Pluma, 45), (Cuaderno, 14), (Goma, 8));
        await EleventaVendeAsync(t, Pluma, 2, HoraLocal(5), ticket: 902);

        // La pantalla, al abrirla: lo vendido DESPUÉS de contar cuenta como movido (−2); lo vendido antes, no (ya no estaba al contar).
        var porRevisar = await Leer<ConteoPorRevisarDto>(await t.Http.GetAsync($"api/cajas/{t.Caja.Id}/conteos-recibidos/{lista}"));
        Assert.Equal((true, PapeleriaPos.Servicio.ApiConteosDelCelular.SeMandaAEleventa, null), (porRevisar.SeManda, porRevisar.Eleventa, porRevisar.SinLectura));
        var plumaPorRevisar = porRevisar.Renglones.Single(r => r.VarianteId == t.De(Pluma));
        Assert.Equal((45m, -2m, 47m, null), (plumaPorRevisar.Contado, plumaPorRevisar.Desde, plumaPorRevisar.Hoy, plumaPorRevisar.Superado));

        // Otra lista contada justo antes de aceptar la primera (para después): 40 plumas.
        var contadaAntesDeAceptar = DateTime.UtcNow;

        // Aceptar: cada producto con diferencia «va a eleventa», la existencia de aquí ya la tomó, y lo que cuadra no se manda.
        var aceptada = await Leer<EstadoConteoDto>(await AceptarAsync(t, lista, (Pluma, 45), (Cuaderno, 14), (Goma, 8)));
        Assert.Equal("aceptada", aceptada.Estado);
        Assert.All(new[] { Pluma, Cuaderno }, p => Assert.Contains(De(t, aceptada, p).Eleventa, new[] { "pendiente", "enviado" }));
        Assert.Equal((null, true, null), (De(t, aceptada, Goma).Eleventa, De(t, aceptada, Goma).Aplicado, De(t, aceptada, Goma).Nota));
        Assert.Equal((43m, 14m, 8m), (await HayAsync(t, Pluma), await HayAsync(t, Cuaderno), await HayAsync(t, Goma)));
        DateTime marcaPluma, marcaCuaderno;
        await using (var db = t.Servicio.Base())
        {
            var renglones = await db.ConteosDelCelularRenglones.AsNoTracking().Where(r => r.ConteoId == lista).ToListAsync();
            var pluma = renglones.Single(r => r.VarianteId == t.De(Pluma));
            Assert.Equal((Cantidad.Piezas(45), Cantidad.Piezas(-2), Cantidad.Piezas(47), Cantidad.Piezas(-4)), (pluma.Aceptado, pluma.Desde, pluma.Sistema, pluma.Diferencia));
            (marcaPluma, marcaCuaderno) = (pluma.MarcaUtc!.Value, renglones.Single(r => r.VarianteId == t.De(Cuaderno)).MarcaUtc!.Value);
            // Cada renglón con su propio segundo, al segundo; lo que cuadra no lleva marca ni va a la bandeja.
            Assert.Equal(TimeSpan.FromSeconds(1), (marcaCuaderno - marcaPluma).Duration());
            Assert.Equal(0, marcaPluma.Ticks % TimeSpan.TicksPerSecond);
            Assert.Null(renglones.Single(r => r.VarianteId == t.De(Goma)).MarcaUtc);
            Assert.Equal(2, await db.EnviosAEleventa.CountAsync(x => x.Tipo == TipoEnvioEleventa.AjusteDeConteo));
        }

        // La bandeja los manda: el celular ve «enviado» con su nota, y la caja, su lista «mandada a eleventa».
        var enviada = await YaNoVaAsync(t, lista);
        Assert.Equal(("enviado", true, "Ya quedó en eleventa: -4 (ajuste #412)."), (De(t, enviada, Pluma).Eleventa, De(t, enviada, Pluma).Aplicado, De(t, enviada, Pluma).Nota));
        Assert.Equal(("enviado", "Ya quedó en eleventa: +2 (ajuste #413)."), (De(t, enviada, Cuaderno).Eleventa, De(t, enviada, Cuaderno).Nota));
        var enLaCaja = Assert.Single(await Leer<List<ConteoEnEleventaDto>>(await t.Http.GetAsync($"api/cajas/{t.Caja.Id}/conteos-en-eleventa")));
        Assert.Equal((1, 2), (enLaCaja.Cuadran, enLaCaja.Renglones.Count));
        Assert.Equal(("enviado", "Ya quedó en eleventa (ajuste #412)", 45m, -2m, 47m, -4m), (enLaCaja.Renglones[0].Eleventa, enLaCaja.Renglones[0].Nota,
            enLaCaja.Renglones[0].Contado, enLaCaja.Renglones[0].Desde, enLaCaja.Renglones[0].Habia, enLaCaja.Renglones[0].Diferencia));
        Assert.Empty(await Leer<List<ConteoPorCapturarDto>>(await t.Http.GetAsync($"api/cajas/{t.Caja.Id}/conteos-por-capturar")));

        // En eleventa, campo por campo como un ajuste hecho a mano: la diferencia (no el total), el folio igual al ID, el costo y el
        // nombre de allá, el motivo con su folio, la caja y el cajero de la configuración, y la hora de la marca.
        var ajustes = await t.E.FilasAsync("SELECT * FROM INVENTARIO_AJUSTES ORDER BY ID");
        Assert.Equal(2, ajustes.Count);
        var a = ajustes[0];
        Assert.Equal((412, 412, HoraMexico.ALocal(marcaPluma), Pluma, CajaEleventa, 7, 1), (Convert.ToInt32(a["ID"]), Convert.ToInt32(a["FOLIO"]),
            (DateTime)a["CUANDO_FUE"]!, Convert.ToInt32(a["PRODUCTO_ID"]), Convert.ToInt32(a["CAJA_ID"]), Convert.ToInt32(a["USUARIO_ID"]), Convert.ToInt32(a["ALMACEN_ID"])));
        Assert.Equal((-4d, 4d, "Pluma punto fino", "Ajuste de inventario #412"),
            (Doble(a["CANTIDAD"]), Doble(a["COSTO_UNITARIO"]), a["DESCRIPCION"]!.ToString()!.Trim(), a["MOTIVO"]!.ToString()!.Trim()));
        var b = ajustes[1];
        Assert.Equal((413, 413, HoraMexico.ALocal(marcaCuaderno), Cuaderno, 2d, 18.5, "Cuaderno profesional"), (Convert.ToInt32(b["ID"]), Convert.ToInt32(b["FOLIO"]),
            (DateTime)b["CUANDO_FUE"]!, Convert.ToInt32(b["PRODUCTO_ID"]), Doble(b["CANTIDAD"]), Math.Round(Doble(b["COSTO_UNITARIO"]), 4), b["DESCRIPCION"]!.ToString()!.Trim()));
        // Su renglón del kardex: lo que había, la misma diferencia con su signo y el motivo. Y la existencia: lo que había MÁS la diferencia.
        var k = Assert.Single(await t.E.FilasAsync("SELECT * FROM INVENTARIO_HISTORIAL WHERE AJUSTE_ID = 412"));
        Assert.Equal((Pluma, HoraMexico.ALocal(marcaPluma), 47d, -4d, "Ajuste de inventario #412", 4d, 4d, CajaEleventa, 7, "f", 1),
            (Convert.ToInt32(k["PRODUCTO_ID"]), (DateTime)k["CUANDO_FUE"]!, Doble(k["CANTIDAD_ANTERIOR"]), Doble(k["CANTIDAD"]), k["DESCRIPCION"]!.ToString()!.Trim(),
                Doble(k["COSTO_UNITARIO"]), Doble(k["COSTO_DESPUES"]), Convert.ToInt32(k["CAJA_ID"]), Convert.ToInt32(k["USUARIO_ID"]), k["VENTA_POR_KIT"]!.ToString()!.Trim(),
                Convert.ToInt32(k["ALMACEN_ID"])));
        Assert.Equal((null, null, null), (k["VENTA_ID"], k["RECIBO_INVENTARIO_ID"], k["TRANSFERENCIA_ID"]));
        var k2 = Assert.Single(await t.E.FilasAsync("SELECT * FROM INVENTARIO_HISTORIAL WHERE AJUSTE_ID = 413"));
        Assert.Equal((12d, 2d), (Doble(k2["CANTIDAD_ANTERIOR"]), Doble(k2["CANTIDAD"])));
        Assert.Equal((43d, 14d, 8d), (await EnEleventaAsync(t, Pluma), await EnEleventaAsync(t, Cuaderno), await EnEleventaAsync(t, Goma)));
        // El contador de folios que eleventa no usa no se movió.
        Assert.Equal(0L, Convert.ToInt64(await t.E.ValorAsync("SELECT GEN_ID(INVENTARIO_AJUSTE_FOLIO, 0) FROM RDB$DATABASE")));

        // La lectura siguiente: eleventa ya trae la existencia nueva y aquí no se mueve nada (ni la deshace ni la duplica).
        var (lecturasPluma, lecturasCuaderno) = (await MovimientosDeLaLecturaAsync(t, Pluma), await MovimientosDeLaLecturaAsync(t, Cuaderno));
        await LeerEleventaAsync(t.Http);
        Assert.Equal((43m, 14m, 8m), (await HayAsync(t, Pluma), await HayAsync(t, Cuaderno), await HayAsync(t, Goma)));
        Assert.Equal((lecturasPluma, lecturasCuaderno), (await MovimientosDeLaLecturaAsync(t, Pluma), await MovimientosDeLaLecturaAsync(t, Cuaderno)));

        // Otro conteo del mismo producto el mismo día, contado ANTES de que el primero quedara en eleventa: su ajuste es posterior a esa
        // hora, pero es del propio POS y no cuenta ni como movido ni como «lo ajustaron a mano». 40 contra 43: −3.
        var otra = await ContarAsync(t, contadaAntesDeAceptar, (Pluma, 40));
        var segunda = await Leer<ConteoPorRevisarDto>(await t.Http.GetAsync($"api/cajas/{t.Caja.Id}/conteos-recibidos/{otra}"));
        Assert.Equal((40m, 0m, 43m, null), (segunda.Renglones[0].Contado, segunda.Renglones[0].Desde, segunda.Renglones[0].Hoy, segunda.Renglones[0].Superado));
        await Leer<EstadoConteoDto>(await AceptarAsync(t, otra, (Pluma, 40)));
        var segundaEnviada = await YaNoVaAsync(t, otra);
        Assert.Equal(("enviado", "Ya quedó en eleventa: -3 (ajuste #414)."), (segundaEnviada.Renglones[0].Eleventa, segundaEnviada.Renglones[0].Nota));
        Assert.Equal((40d, 40m, 3), (await EnEleventaAsync(t, Pluma), await HayAsync(t, Pluma), await AjustesAsync(t)));
        // Dos ajustes del mismo producto nunca con la misma hora.
        var horas = (await t.E.FilasAsync("SELECT CUANDO_FUE FROM INVENTARIO_AJUSTES WHERE PRODUCTO_ID = 10")).Select(f => (DateTime)f["CUANDO_FUE"]!).ToList();
        Assert.Equal(2, horas.Distinct().Count());

        // Y un tercero que cuadra: nada que mandar.
        var tercera = await ContarAsync(t, null, (Pluma, 40));
        var cuadra = await Leer<EstadoConteoDto>(await AceptarAsync(t, tercera, (Pluma, 40)));
        Assert.Equal((true, null), (cuadra.Renglones[0].Aplicado, cuadra.Renglones[0].Eleventa));
        Assert.Equal(3, await AjustesAsync(t));
        var bandeja = (await t.Http.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;
        Assert.Equal((0, 0, 3), (bandeja.Pendientes, bandeja.PorRevisar, bandeja.Enviadas));
    }

    [FactConFirebird]
    public async Task Con_el_interruptor_apagado_no_se_le_escribe_nada_a_eleventa_y_solo_lo_prende_quien_puede()
    {
        await using var t = await TiendaAsync(mandar: false);
        var ruta = $"api/cajas/{t.Caja.Id}/configuracion/mandar-conteos";
        // De fábrica, apagado; el encargado (sin «Configuración de la tienda») no lo prende.
        Assert.Equal(new MandarConteosDto(EnElPiloto: true, Mandar: false, PuenteEscribe: true), await t.Http.GetFromJsonAsync<MandarConteosDto>(ruta));
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Http.PutAsJsonAsync(ruta, new SolicitudMandarConteos(t.Encargado.UsuarioId, true))).StatusCode);
        Assert.False((await t.Http.GetFromJsonAsync<MandarConteosDto>(ruta))!.Mandar);
        var historial = await t.E.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_HISTORIAL");

        // Apagado: aceptar deja la lista por capturar, como siempre. Ni la bandeja ni eleventa se enteran.
        var lista = await ContarAsync(t, null, (Pluma, 45));
        var aceptada = await Leer<EstadoConteoDto>(await AceptarAsync(t, lista, (Pluma, 45)));
        Assert.Equal(("por-capturar", "Por capturar en eleventa: -5."), (aceptada.Renglones[0].Eleventa, aceptada.Renglones[0].Nota));
        await t.Http.PostAsync("api/eleventa/enviar", null);
        await LeerEleventaAsync(t.Http);
        Assert.Equal((0, 50d, 50m), (await AjustesAsync(t), await EnEleventaAsync(t, Pluma), await HayAsync(t, Pluma)));
        Assert.Equal(historial, await t.E.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_HISTORIAL"));
        await using (var db = t.Servicio.Base())
            Assert.False(await db.EnviosAEleventa.AnyAsync());
        Assert.Empty(await Leer<List<ConteoEnEleventaDto>>(await t.Http.GetAsync($"api/cajas/{t.Caja.Id}/conteos-en-eleventa")));

        // El dueño lo prende (queda en la bitácora) y lo apaga: la lista que ya estaba por capturar no se manda sola.
        Assert.True((await Leer<MandarConteosDto>(await t.Http.PutAsJsonAsync(ruta, new SolicitudMandarConteos(t.Dueno.UsuarioId, true)))).Mandar);
        await t.Http.PostAsync("api/eleventa/enviar", null);
        Assert.Equal(0, await AjustesAsync(t));
        await using (var db = t.Servicio.Base())
            Assert.Contains(await db.Bitacora.Select(x => x.Detalle).ToListAsync(), d => d != null && d.StartsWith("Prendió «Mandar los conteos a eleventa»"));
        Assert.False((await Leer<MandarConteosDto>(await t.Http.PutAsJsonAsync(ruta, new SolicitudMandarConteos(t.Dueno.UsuarioId, false)))).Mandar);
    }

    [FactConFirebird]
    public async Task Si_se_apaga_el_interruptor_lo_que_ya_estaba_en_la_bandeja_tampoco_se_escribe_hasta_prenderlo()
    {
        await using var t = await TiendaAsync();
        var ruta = $"api/cajas/{t.Caja.Id}/configuracion/mandar-conteos";
        var lista = await ContarAsync(t, null, (Pluma, 45));
        await Leer<EstadoConteoDto>(await AceptarAsync(t, lista, (Pluma, 45)));
        Assert.Equal("enviado", (await YaNoVaAsync(t, lista)).Renglones[0].Eleventa);
        // Como si no hubiera alcanzado a salir: en eleventa no está, y aquí sigue en la bandeja. Y el dueño apaga el interruptor.
        await t.E.EjecutarAsync("DELETE FROM INVENTARIO_HISTORIAL WHERE AJUSTE_ID = 412");
        await t.E.EjecutarAsync("DELETE FROM INVENTARIO_AJUSTES WHERE ID = 412");
        await t.E.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = 50 WHERE PRODUCTO_ID = 10");
        await using (var db = t.Servicio.Base())
        {
            var envio = await db.EnviosAEleventa.SingleAsync();
            var renglon = await db.ConteosDelCelularRenglones.SingleAsync(r => r.ConteoId == lista);
            (envio.Estado, envio.IdEleventa, envio.EnviadaUtc, envio.Intentos) = (EstadoEnvioEleventa.Pendiente, null, null, 0);
            (renglon.Eleventa, renglon.AjusteEleventa) = (ConteoEnEleventa.Pendiente, null);
            await db.SaveChangesAsync();
        }
        Assert.False((await Leer<MandarConteosDto>(await t.Http.PutAsJsonAsync(ruta, new SolicitudMandarConteos(t.Dueno.UsuarioId, false)))).Mandar);

        // La bandeja lo intenta y no escribe: espera, dicho en la bandeja, y el renglón sigue «va a eleventa».
        await t.Http.PostAsync("api/eleventa/enviar", null);
        var espera = await EsperarAsync(async () => (await t.Http.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!,
            x => x.PorAtender.Any(e => e.Error == ConteosParaEleventa.Apagado));
        Assert.Equal((1, ConteosParaEleventa.Apagado), (espera.Pendientes, Assert.Single(espera.PorAtender).Error));
        Assert.Equal((0, 50d, "pendiente"), (await AjustesAsync(t), await EnEleventaAsync(t, Pluma), (await DelCelularAsync(t, lista)).Renglones[0].Eleventa));
        // La lectura respeta lo que espera: la existencia de aquí no rebota a la de eleventa.
        await LeerEleventaAsync(t.Http);
        Assert.Equal(45m, await HayAsync(t, Pluma));

        // Al prenderlo otra vez, sale: la diferencia, sobre lo que eleventa tenga.
        Assert.True((await Leer<MandarConteosDto>(await t.Http.PutAsJsonAsync(ruta, new SolicitudMandarConteos(t.Dueno.UsuarioId, true)))).Mandar);
        Assert.Equal("enviado", (await YaNoVaAsync(t, lista)).Renglones[0].Eleventa);
        Assert.Equal((1, 45d, 45m), (await AjustesAsync(t), await EnEleventaAsync(t, Pluma), await HayAsync(t, Pluma)));
    }

    [FactConFirebird]
    public async Task Con_el_puente_sin_escribir_el_interruptor_prendido_tampoco_manda_nada()
    {
        // La lectura prendida, «Escribir» en false, y el interruptor prendido.
        await using var t = await TiendaAsync(mandar: true, escribir: false);
        Assert.Equal(new MandarConteosDto(EnElPiloto: true, Mandar: true, PuenteEscribe: false),
            await t.Http.GetFromJsonAsync<MandarConteosDto>($"api/cajas/{t.Caja.Id}/configuracion/mandar-conteos"));
        var historial = await t.E.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_HISTORIAL");

        var lista = await ContarAsync(t, null, (Pluma, 45), (Cuaderno, 14));
        var porRevisar = await Leer<ConteoPorRevisarDto>(await t.Http.GetAsync($"api/cajas/{t.Caja.Id}/conteos-recibidos/{lista}"));
        Assert.Equal((false, PapeleriaPos.Servicio.ApiConteosDelCelular.SeCapturaEnEleventa), (porRevisar.SeManda, porRevisar.Eleventa));
        var aceptada = await Leer<EstadoConteoDto>(await AceptarAsync(t, lista, (Pluma, 45), (Cuaderno, 14)));
        Assert.All(aceptada.Renglones, r => Assert.Equal("por-capturar", r.Eleventa));
        await t.Http.PostAsync("api/eleventa/enviar", null);
        await LeerEleventaAsync(t.Http);
        Assert.Equal((0, 50d, 12d, 50m, 12m), (await AjustesAsync(t), await EnEleventaAsync(t, Pluma), await EnEleventaAsync(t, Cuaderno), await HayAsync(t, Pluma),
            await HayAsync(t, Cuaderno)));
        Assert.Equal(historial, await t.E.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_HISTORIAL"));
        await using var db = t.Servicio.Base();
        Assert.False(await db.EnviosAEleventa.AnyAsync());
    }

    [FactConFirebird]
    public async Task Aceptar_dos_veces_o_reintentar_tras_un_corte_no_duplica_el_ajuste()
    {
        await using var t = await TiendaAsync();
        var lista = await ContarAsync(t, null, (Pluma, 45));
        await Leer<EstadoConteoDto>(await AceptarAsync(t, lista, (Pluma, 45)));
        Assert.Equal("enviado", (await YaNoVaAsync(t, lista)).Renglones[0].Eleventa);
        Assert.Equal((1, 45d), (await AjustesAsync(t), await EnEleventaAsync(t, Pluma)));

        // Aceptarla otra vez: ya se aplicó; nada nuevo en la bandeja ni en eleventa.
        var otraVez = await AceptarAsync(t, lista, (Pluma, 45));
        Assert.Equal((HttpStatusCode.Conflict, "ya-resuelta"), (otraVez.StatusCode, (await otraVez.Content.ReadFromJsonAsync<ErrorDto>())!.Codigo));

        // El corte: eleventa guardó el ajuste, pero aquí no se supo (la red se cayó al confirmar). El envío sigue pendiente, con su
        // intento anotado, y el renglón «va a eleventa», con la MISMA marca.
        DateTime marca;
        await using (var db = t.Servicio.Base())
        {
            var envio = await db.EnviosAEleventa.SingleAsync();
            var renglon = await db.ConteosDelCelularRenglones.SingleAsync(r => r.ConteoId == lista);
            marca = renglon.MarcaUtc!.Value;
            (envio.Estado, envio.IdEleventa, envio.EnviadaUtc) = (EstadoEnvioEleventa.Pendiente, null, null);
            (renglon.Eleventa, renglon.AjusteEleventa) = (ConteoEnEleventa.Pendiente, null);
            await db.SaveChangesAsync();
        }
        // Mientras no se sabe si entró, no se acepta otro conteo de ese producto (su existencia puede estar vieja)…
        var mientras = await ContarAsync(t, null, (Pluma, 44));
        var enCamino = await AceptarAsync(t, mientras, (Pluma, 44));
        Assert.Equal((HttpStatusCode.Conflict, "en-camino"), (enCamino.StatusCode, (await enCamino.Content.ReadFromJsonAsync<ErrorDto>())!.Codigo));
        // …y el reintento lo encuentra por su marca: queda «enviado» con el mismo ajuste, sin escribir otro.
        var reintento = await YaNoVaAsync(t, lista);
        Assert.Equal(("enviado", "Ya quedó en eleventa: -5 (ajuste #412)."), (reintento.Renglones[0].Eleventa, reintento.Renglones[0].Nota));
        Assert.Equal((1, 45d, 45m), (await AjustesAsync(t), await EnEleventaAsync(t, Pluma), await HayAsync(t, Pluma)));
        Assert.Equal(1, Convert.ToInt32(await t.E.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_HISTORIAL WHERE AJUSTE_ID IS NOT NULL")));
        await using (var db = t.Servicio.Base())
        {
            var envio = await db.EnviosAEleventa.SingleAsync(e => e.Estado == EstadoEnvioEleventa.Enviada);
            Assert.Equal((412, marca), (envio.IdEleventa, (await db.ConteosDelCelularRenglones.SingleAsync(r => r.ConteoId == lista)).MarcaUtc));
        }
        // Ya se sabe: el otro conteo se acepta, y manda solo su diferencia (44 contra 45).
        await Leer<EstadoConteoDto>(await AceptarAsync(t, mientras, (Pluma, 44)));
        Assert.Equal("Ya quedó en eleventa: -1 (ajuste #413).", (await YaNoVaAsync(t, mientras)).Renglones[0].Nota);
        Assert.Equal((2, 44d), (await AjustesAsync(t), await EnEleventaAsync(t, Pluma)));
    }

    [FactConFirebird]
    public async Task Un_producto_que_eleventa_no_toma_queda_por_revisar_con_su_motivo_y_los_demas_siguen()
    {
        await using var t = await TiendaAsync();
        var lista = await ContarAsync(t, null, (Pluma, 45), (Cuaderno, 14), (Goma, 6));
        // Eleventa borra el cuaderno (allá, borrar es marcarlo): aquí todavía no se sabe, y su ajuste no va a entrar.
        await t.E.EjecutarAsync("UPDATE PRODUCTOS SET ELIMINADO_EN = @ahora WHERE ID = @p", ("ahora", HoraLocal(0)), ("p", Cuaderno));
        await Leer<EstadoConteoDto>(await AceptarAsync(t, lista, (Pluma, 45), (Cuaderno, 14), (Goma, 6)));

        var estado = await YaNoVaAsync(t, lista);
        // El cuaderno: «eleventa no lo tomó», con su porqué; no se aplicó, y su existencia de aquí regresó a la de antes.
        var cuaderno = De(t, estado, Cuaderno);
        Assert.Equal(("por-revisar", false), (cuaderno.Eleventa, cuaderno.Aplicado));
        Assert.Equal("Eleventa no lo tomó: Eleventa ya no tiene el producto 11 (se borró allá): su conteo no se le puede mandar.", cuaderno.Nota);
        Assert.Equal((12m, 12d), (await HayAsync(t, Cuaderno), await EnEleventaAsync(t, Cuaderno)));
        // Los otros dos sí entraron, cada uno en su transacción.
        Assert.Equal(("enviado", "enviado"), (De(t, estado, Pluma).Eleventa, De(t, estado, Goma).Eleventa));
        Assert.Equal((45d, 6d, 45m, 6m, 2), (await EnEleventaAsync(t, Pluma), await EnEleventaAsync(t, Goma), await HayAsync(t, Pluma), await HayAsync(t, Goma),
            await AjustesAsync(t)));
        Assert.Equal(0, Convert.ToInt32(await t.E.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_AJUSTES WHERE PRODUCTO_ID = 11")));
        // En la bandeja queda por revisar, dicho con su producto; y en la lista de la caja, con su motivo.
        var bandeja = (await t.Http.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;
        var porRevisar = Assert.Single(bandeja.PorAtender);
        Assert.Equal((0, 1, EstadoEnvioEleventa.PorRevisar, "Ajuste de inventario de «Cuaderno profesional»: +2 (conteo del celular)"),
            (bandeja.Pendientes, bandeja.PorRevisar, porRevisar.Estado, porRevisar.Descripcion));
        var enLaCaja = Assert.Single(await Leer<List<ConteoEnEleventaDto>>(await t.Http.GetAsync($"api/cajas/{t.Caja.Id}/conteos-en-eleventa")));
        Assert.Equal(["enviado", "por-revisar", "enviado"], enLaCaja.Renglones.Select(r => r.Eleventa));
        Assert.StartsWith("Eleventa no lo tomó: Eleventa ya no tiene el producto 11", enLaCaja.Renglones[1].Nota);
        // El kardex de aquí: la diferencia del cuaderno y su regreso, las dos del mismo conteo (no son movimientos de mercancía).
        await using (var db = t.Servicio.Base())
            Assert.Equal([2000L, -2000L], await db.MovimientosInventario.Where(m => m.VarianteId == t.De(Cuaderno) && m.DocumentoId == lista)
                .OrderBy(m => m.FechaUtc).ThenBy(m => m.Id).Select(m => m.Cantidad.Milesimas).ToListAsync());

        // Alguien lo captura a mano en eleventa y lo dice en la bandeja («Ya lo anoté»): el renglón queda como capturado.
        Assert.True((await t.Http.PostAsJsonAsync($"api/eleventa/envios/{porRevisar.Id}/a-mano", new SolicitudAnotadoAMano(t.Dueno.UsuarioId, null, null))).IsSuccessStatusCode);
        Assert.Equal("capturado", De(t, await DelCelularAsync(t, lista), Cuaderno).Eleventa);
    }

    [FactConFirebird]
    public async Task Lo_que_alguien_ajusto_a_mano_en_eleventa_despues_de_contarlo_no_se_manda_y_se_dice_al_aceptar()
    {
        await using var t = await TiendaAsync();
        var lista = await ContarAsync(t, DateTime.UtcNow.AddMinutes(-10), (Pluma, 45), (Goma, 6));
        // Cinco minutos después de contar, alguien ajusta la pluma a mano en eleventa (la deja en 46).
        await AjusteAManoAsync(t, Pluma, 46, HoraLocal(5));

        var porRevisar = await Leer<ConteoPorRevisarDto>(await t.Http.GetAsync($"api/cajas/{t.Caja.Id}/conteos-recibidos/{lista}"));
        var pluma = porRevisar.Renglones.Single(r => r.VarianteId == t.De(Pluma));
        Assert.Matches(@"^Se ajustó en eleventa a las \d\d:\d\d, después de contarlo: vale ese ajuste\. Cuéntalo otra vez si hace falta\.$", pluma.Superado);

        var aceptada = await Leer<EstadoConteoDto>(await AceptarAsync(t, lista, (Pluma, 45), (Goma, 6)));
        Assert.Equal((null, false), (De(t, aceptada, Pluma).Eleventa, De(t, aceptada, Pluma).Aplicado));
        Assert.StartsWith("No se aplicó. Se ajustó en eleventa a las ", De(t, aceptada, Pluma).Nota);
        var estado = await YaNoVaAsync(t, lista);
        Assert.Equal("enviado", De(t, estado, Goma).Eleventa);
        // De la pluma, en eleventa solo está el ajuste a mano; aquí, lo que dice eleventa.
        Assert.Equal((46d, 46m, 6d, 6m), (await EnEleventaAsync(t, Pluma), await HayAsync(t, Pluma), await EnEleventaAsync(t, Goma), await HayAsync(t, Goma)));
        Assert.Equal(1, Convert.ToInt32(await t.E.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_AJUSTES WHERE PRODUCTO_ID = 10")));
    }

    [FactConFirebird]
    public async Task Si_eleventa_se_restaura_el_puente_se_detiene_y_al_reanudar_el_ajuste_perdido_queda_por_revisar()
    {
        await using var t = await TiendaAsync();
        var lista = await ContarAsync(t, null, (Pluma, 45));
        await Leer<EstadoConteoDto>(await AceptarAsync(t, lista, (Pluma, 45)));
        Assert.Equal("enviado", (await YaNoVaAsync(t, lista)).Renglones[0].Eleventa);

        // Eleventa regresa a un respaldo de antes del ajuste: ya no está, la existencia volvió a 50 y su contador regresó.
        await t.E.EjecutarAsync("DELETE FROM INVENTARIO_HISTORIAL WHERE AJUSTE_ID = 412");
        await t.E.EjecutarAsync("DELETE FROM INVENTARIO_AJUSTES WHERE ID = 412");
        await t.E.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = 50 WHERE PRODUCTO_ID = 10");
        await t.E.EjecutarAsync("SET GENERATOR GEN_INVENTARIO_AJUSTES_ID TO 411");

        // El siguiente conteo no se acepta: la lectura ve que los números de los ajustes regresaron y el puente se detiene. Nada se escribe.
        var otra = await ContarAsync(t, null, (Pluma, 44));
        Assert.Equal(HttpStatusCode.Conflict, (await AceptarAsync(t, otra, (Pluma, 44))).StatusCode);
        await using (var db = t.Servicio.Base())
            Assert.Contains("los números de los ajustes de inventario regresaron", (await db.Set<DetencionDelPuente>().SingleAsync()).Pruebas);
        Assert.Equal((0, 50d, 45m), (await AjustesAsync(t), await EnEleventaAsync(t, Pluma), await HayAsync(t, Pluma)));

        // Al reanudar: el ajuste perdido queda por revisar (para capturarlo a mano) y la existencia de aquí vuelve a la de eleventa.
        await Leer<PuenteEleventaDto>(await t.Http.PostAsJsonAsync("api/eleventa/reanudar", new SolicitudReanudarPuente(t.Dueno.UsuarioId, t.Caja.Id, null)));
        var perdido = (await DelCelularAsync(t, lista)).Renglones[0];
        Assert.Equal(("por-revisar", false, $"Eleventa no lo tomó: {ConteosParaEleventa.SePerdio}"), (perdido.Eleventa, perdido.Aplicado, perdido.Nota));
        await LeerEleventaAsync(t.Http);
        Assert.Equal((50m, 0), (await HayAsync(t, Pluma), await AjustesAsync(t)));
        // Ya reanudado, el otro conteo se acepta y se manda contra lo que eleventa tiene ahora.
        await Leer<EstadoConteoDto>(await AceptarAsync(t, otra, (Pluma, 44)));
        Assert.Equal("enviado", (await YaNoVaAsync(t, otra)).Renglones[0].Eleventa);
        Assert.Equal((44d, 44m), (await EnEleventaAsync(t, Pluma), await HayAsync(t, Pluma)));
    }
}
