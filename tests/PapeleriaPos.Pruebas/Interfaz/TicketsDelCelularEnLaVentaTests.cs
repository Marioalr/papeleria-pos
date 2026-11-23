using System.Net.Http.Json;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El ticket que manda el celular llega a la espera de la caja que tiene la venta abierta (docs/celular-como-caja.md, «Mandar el
/// ticket a una caja»): con el nombre del celular en su ficha, sin tocar el ticket que se atiende, con los precios de la caja, y
/// se retoma con F5 como cualquier otro. Mandarlo otra vez no lo duplica.
/// </summary>
public sealed class TicketsDelCelularEnLaVentaTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-celular-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Llega_a_la_espera_con_el_nombre_del_celular_sin_tocar_el_ticket_y_F5_lo_retoma()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, conCelular: true);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        var atendido = await VentaConCopia.RenglonesAsync(v.Pantalla);

        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", v.Caja.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        // La venta abierta ya pregunta por sus tickets: en el celular, la caja sale lista, con quién atiende y cuántos esperan.
        CajaParaMandarDto? caja = null;
        await VentaConCopia.EsperarAsync(() =>
        {
            caja = celular.GetFromJsonAsync<List<CajaParaMandarDto>>($"api/cajas/{alta.Caja.Id}/para-mandar").Result!.Single(c => c.Id == v.Caja.Id);
            return caja.Recibe;
        }, "la caja lista para recibir");
        Assert.Equal(0, caja!.Esperan);
        Assert.NotNull(caja.Atiende);

        var solicitud = new SolicitudMandarTicket(Guid.NewGuid(), v.Caja.Id, [new RenglonMandado(b.VarianteId, b.Presentacion.Id, null, 2000)]);
        var mandado = await Leer<TicketMandadoDto>(await celular.PostAsJsonAsync($"api/cajas/{alta.Caja.Id}/mandar", solicitud));
        Assert.True(mandado.Recibido);

        await v.Pantalla.EsperarAsync(t => t.Contains("Llegó un ticket de Celular de Luis"), "el aviso del ticket del celular");
        var texto = PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync());
        Assert.Contains($"Llegó un ticket de Celular de Luis: 2 artículos, {Formato.Dinero(mandado.TotalCentavos)}. Retómalo arriba o con F5.", texto);
        Assert.Contains($"Celular de Luis · {Formato.Dinero(mandado.TotalCentavos)}", texto);
        // El que se atendía sigue igual.
        Assert.Equal(atendido, await VentaConCopia.RenglonesAsync(v.Pantalla));

        // F5, con el teclado: el que se atendía pasa a la espera y el del celular queda con sus 2, al precio de la caja.
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F5", null));
        var renglon = Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.StartsWith("2 ", renglon);
        Assert.Contains(b.Descripcion, renglon);
        Assert.Contains("Retomaste el ticket de Celular de Luis.", await v.Pantalla.TextoAsync());

        // Otra vez el mismo (la respuesta no llegó al celular): contesta igual y no sale otra ficha del celular.
        Assert.Equal(mandado, await Leer<TicketMandadoDto>(await celular.PostAsJsonAsync($"api/cajas/{alta.Caja.Id}/mandar", solicitud)));
        await Task.Delay(TicketsDelCelular.Cada * 2);
        Assert.DoesNotContain(await Fichas(v), f => f.Texto.Contains("Celular de Luis", StringComparison.Ordinal));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Las fichas de la espera y «Más», con su texto y su clase.</summary>
    private static async Task<List<ElementoDibujado>> Fichas(VentaConCopia v) =>
        (await v.Pantalla.LeerAsync()).Elementos.Where(e => e.Etiqueta == "button" && e.Clase?.Split(' ').Contains("vn-ficha") == true).ToList();

    private static bool Brilla(ElementoDibujado e) => e.Clase?.Split(' ').Contains("llego") == true;

    /// <summary>Un celular dado de alta en la sucursal de la caja, con su llave.</summary>
    private static async Task<(HttpClient Celular, AltaCajaDto Alta)> CelularAsync(ServicioDePrueba servicio, VentaConCopia v)
    {
        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", v.Caja.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        await VentaConCopia.EsperarAsync(() =>
            celular.GetFromJsonAsync<List<CajaParaMandarDto>>($"api/cajas/{alta.Caja.Id}/para-mandar").Result!.Single(c => c.Id == v.Caja.Id).Recibe,
            "la caja lista para recibir");
        return (celular, alta);
    }

    /// <summary>
    /// El dueño mandó un ticket y en la caja no se vio que llegó (2026-11-22): el brillo de la ficha nunca se hizo (#190 lo dejó pendiente) y el
    /// aviso era el renglón de la venta, que con una página abierta está oculto. Ahora la ficha brilla unos segundos y abajo sale un aviso, en la
    /// venta y en cualquier página; si llegó con una página abierta, la ficha brilla al volver. F5 desde la página lleva a elegirlo.
    /// </summary>
    [Fact]
    public async Task Al_llegar_brilla_su_ficha_y_sale_el_aviso_tambien_con_una_pagina_abierta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, conCelular: true);
        var (a, b) = v.DosDeLaCopia();
        var (celular, alta) = await CelularAsync(servicio, v);

        // En la venta: la ficha brilla y el aviso de abajo dice quién, cuántos artículos y cuánto, con F5.
        var primero = await Leer<TicketMandadoDto>(await celular.PostAsJsonAsync($"api/cajas/{alta.Caja.Id}/mandar",
            new SolicitudMandarTicket(Guid.NewGuid(), v.Caja.Id, [new RenglonMandado(b.VarianteId, b.Presentacion.Id, null, 2000)])));
        var aviso = await v.Pantalla.EsperarElementoAsync("aviso-llegada");
        Assert.Equal("status", aviso.Atributos["role"]?.ToString());
        Assert.Equal($"Llegó un ticket de Celular de Luis · 2 art. · {Formato.Dinero(primero.TotalCentavos)}F5 para elegirlo", PantallaSinNavegador.Limpio(aviso.Texto));
        Assert.True(Brilla(Assert.Single(await Fichas(v))));

        // Se quitan solos: el aviso y el brillo no se quedan.
        await VentaConCopia.EsperarAsync(() => v.Pantalla.LeerAsync().Result.Elementos.All(e => e.Id != "aviso-llegada" && !Brilla(e)), "que se quiten solos");

        // Con Clientes abierto (la venta oculta): el aviso sale arriba igual, pero la ficha no brilla hasta volver a la venta.
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Clientes"));
        var segundo = await Leer<TicketMandadoDto>(await celular.PostAsJsonAsync($"api/cajas/{alta.Caja.Id}/mandar",
            new SolicitudMandarTicket(Guid.NewGuid(), v.Caja.Id, [new RenglonMandado(a.VarianteId, a.Presentacion.Id, null, 1000)])));
        aviso = await v.Pantalla.EsperarElementoAsync("aviso-llegada");
        Assert.StartsWith($"Llegó un ticket de Celular de Luis · 1 art. · {Formato.Dinero(segundo.TotalCentavos)}", PantallaSinNavegador.Limpio(aviso.Texto));
        Assert.DoesNotContain(await Fichas(v), Brilla);

        // F5 desde la página: a la venta (la ficha nueva brilla) y, con los dos del celular en espera, el menú para elegir.
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F5", null));
        Assert.Contains(await Fichas(v), f => Brilla(f) && f.Texto.Contains(Formato.Dinero(segundo.TotalCentavos), StringComparison.Ordinal));
        var renglones = (await v.Pantalla.LeerAsync()).Elementos.Where(e => e.Id?.StartsWith("elegir-espera-", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(2, renglones.Count);
        Assert.All(renglones, r => Assert.StartsWith("Celular de Luis ", PantallaSinNavegador.Limpio(r.Texto)));
        Assert.Matches(@"· 1 producto \$", PantallaSinNavegador.Limpio(renglones[1].Texto));
        await v.Pantalla.TeclaAsync("elegir-espera-1", "Enter");
        Assert.Contains("Retomaste el ticket de Celular de Luis.", await v.Pantalla.TextoAsync());
        Assert.Empty(v.Pantalla.Errores);
    }
}
