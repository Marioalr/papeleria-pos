using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Telegram;

namespace PapeleriaPos.Pruebas.Servicio;

public class CatalogoApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno, Guid Departamento)> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var opciones = (await http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!;
        return (http, caja, cajero, encargado, dueno, opciones.Departamentos[0].Id);
    }

    private static FichaProductoDto Ficha(Guid departamento, string nombre, string codigo, string? codigoCaja = null) =>
        new(null, nombre, departamento, "Marca Nueva de Prueba", TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
        [
            new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
            [
                new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)]),
                new PresentacionFichaDto(null, "Caja 12", 12000, false, codigoCaja is null ? [] : [new CodigoFichaDto(codigoCaja)]),
            ],
            [new PrecioFichaDto(null, 1000, 1500), new PrecioFichaDto(null, 12000, 1200)]),
        ]);

    [Fact]
    public async Task El_encargado_da_de_alta_un_producto_y_la_caja_lo_vende_enseguida()
    {
        var (http, caja, _, encargado, _, departamento) = await PrepararAsync();
        var interno = (await Leer<CodigoInternoRespuesta>(await http.PostAsync("api/catalogo/codigo-interno", null))).Codigo;
        var otroInterno = (await Leer<CodigoInternoRespuesta>(await http.PostAsync("api/catalogo/codigo-interno", null))).Codigo;

        var ficha = await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId, Ficha(departamento, "Marcatextos fluorescente", interno, otroInterno))));
        var enCaja = await Leer<ProductoVentaDto>(await http.GetAsync($"api/productos/codigo/{otroInterno}?sucursalId={caja.SucursalId}"));
        var buscado = await http.GetFromJsonAsync<List<ProductoCatalogoDto>>($"api/catalogo/productos?sucursalId={caja.SucursalId}&q=marcatextos%20fluor");

        Assert.True(Ean13.EsValido(interno) && interno.StartsWith('2'));
        Assert.NotEqual(interno, otroInterno);
        Assert.Equal("Marca Nueva de Prueba", ficha.Marca);
        var variante = Assert.Single(ficha.Variantes);
        Assert.Equal(OrigenCodigo.Interno, variante.Presentaciones[0].Codigos.Single().Origen);
        Assert.Equal(2, variante.Precios.Count);
        Assert.Equal("Caja 12", enCaja.Presentaciones.Single(p => !p.EsBase).Nombre);
        Assert.Equal(1500, Assert.Single(buscado!).PrecioCentavos);
    }

    /// <summary>Un UPC-A de 12 que empieza con 0, distinto en cada prueba, con su verificador bien.</summary>
    private static string UpcAConCero()
    {
        var cuerpo = "0" + string.Concat(Enumerable.Range(0, 10).Select(_ => (char)('0' + Random.Shared.Next(10))));
        return Enumerable.Range(0, 10).Select(d => cuerpo + d).Single(Gtin.EsValido);
    }

    /// <summary>
    /// La regla de las variantes de UPC (2026-10-11, la de la app del celular) en <c>/api/productos/codigo</c>: el catálogo lo
    /// tiene de 11 dígitos (como lo dejó eleventa) y el lector lo manda de 12 o de 13; el tal cual gana, y si las variantes dan
    /// con dos productos, no se adivina.
    /// </summary>
    [Fact]
    public async Task El_codigo_de_fabrica_se_encuentra_con_sus_variantes_de_upc_sin_adivinar()
    {
        var (http, caja, _, encargado, _, departamento) = await PrepararAsync();
        var upcA = UpcAConCero();
        var once = upcA[1..];
        await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId, Ficha(departamento, "Borrador de once dígitos", once))));
        Task<HttpResponseMessage> Escanear(string codigo) => http.GetAsync($"api/productos/codigo/{codigo}?sucursalId={caja.SucursalId}");

        Assert.Equal(once, (await Leer<ProductoVentaDto>(await Escanear(upcA))).Codigo);
        Assert.Equal(once, (await Leer<ProductoVentaDto>(await Escanear("0" + upcA))).Codigo);
        // Con el verificador mal, ninguna variante.
        var malo = upcA[..11] + (char)('0' + (upcA[11] - '0' + 1) % 10);
        Assert.Equal(HttpStatusCode.NotFound, (await Escanear(malo)).StatusCode);

        // Otro producto con el de 12 tal cual: escanear el de 12 lo encuentra a él; el de 13 da con los dos y no se adivina.
        await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId, Ficha(departamento, "Sacapuntas de doce dígitos", upcA))));
        Assert.Equal(upcA, (await Leer<ProductoVentaDto>(await Escanear(upcA))).Codigo);
        Assert.Equal(once, (await Leer<ProductoVentaDto>(await Escanear(once))).Codigo);
        var ambiguo = await Escanear("0" + upcA);
        Assert.Equal(HttpStatusCode.NotFound, ambiguo.StatusCode);
        Assert.Contains("más de un producto", (await ambiguo.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    [Fact]
    public async Task El_cajero_no_edita_el_catalogo_y_un_codigo_no_se_repite()
    {
        var (http, caja, cajero, encargado, _, departamento) = await PrepararAsync();
        var codigo = $"PRUEBA-{Guid.NewGuid():N}"[..20];

        var porCajero = await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(cajero.UsuarioId, caja.SucursalId, Ficha(departamento, "Goma de prueba", codigo)));
        await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId, Ficha(departamento, "Goma de prueba", codigo))));
        var repetido = await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId, Ficha(departamento, "Otra goma", codigo)));
        var sinMenudeo = Ficha(departamento, "Sin menudeo", codigo + "X") is var f
            ? f with { Variantes = [f.Variantes[0] with { Precios = [new PrecioFichaDto(null, 12000, 1200)] }] }
            : null;
        var faltaMenudeo = await http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId, sinMenudeo!));

        Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, repetido.StatusCode);
        Assert.Contains("ya es de «Goma de prueba»", await repetido.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, faltaMenudeo.StatusCode);
        Assert.Contains("precio de menudeo", await faltaMenudeo.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Un_precio_que_cambia_el_encargado_avisa_y_el_del_dueno_no()
    {
        var (http, caja, _, encargado, dueno, departamento) = await PrepararAsync();
        using (var alcance = servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            if (!db.ChatsTelegram.Any(c => c.ChatId == 4242))
            {
                db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = dueno.UsuarioId, ChatId = 4242, VinculadoUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
        }
        var ficha = await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId, Ficha(departamento, "Corrector cinta", $"CINTA-{Guid.NewGuid():N}"[..18]))));
        FichaProductoDto ConPrecio(FichaProductoDto f, long menudeo) =>
            f with { Variantes = [f.Variantes[0] with { Precios = [new PrecioFichaDto(null, 1000, menudeo), new PrecioFichaDto(null, 12000, 1200)] }] };

        await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId, ConPrecio(ficha, 1800))));
        var editada = await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(dueno.UsuarioId, caja.SucursalId, ConPrecio(ficha, 1900))));

        Assert.Equal(1900, editada.Variantes[0].Precios.Single(p => p.CantidadMinimaMilesimas == 1000).PrecioCentavos);
        using var otro = servicio.Fabrica.Services.CreateScope();
        var base_ = otro.ServiceProvider.GetRequiredService<PosDbContext>();
        var bitacora = await base_.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.EditarCatalogo && b.Detalle.Contains("Corrector cinta")).ToListAsync();
        Assert.Contains(bitacora, b => b.Detalle == "Precio de «Corrector cinta»: $15.00 → $18.00" && b.UsuarioId == encargado.UsuarioId);
        Assert.Contains(bitacora, b => b.Detalle == "Precio de «Corrector cinta»: $18.00 → $19.00" && b.UsuarioId == dueno.UsuarioId);
        var mensajes = await base_.MensajesTelegram.AsNoTracking().Where(m => m.ChatId == 4242 && m.Tipo == TipoAlerta.CambioPrecioCatalogo).Select(m => m.Texto).ToListAsync();
        Assert.Single(mensajes, m => m.Contains("$15.00 → $18.00"));
        Assert.DoesNotContain(mensajes, m => m.Contains("$18.00 → $19.00"));
        Assert.Equal(2, await base_.PreciosHistorial.CountAsync(h => h.PrecioAnteriorCentavos != null && h.VarianteId == ficha.Variantes[0].Id));
    }

    /// <summary>
    /// Con los permisos por usuario también vincula su chat quien no es el dueño («Configurar Telegram»): lo que cambia el
    /// encargado le llega al dueño y no a él, y lo que cambia el dueño, al revés.
    /// </summary>
    [Fact]
    public async Task Lo_que_cambia_cada_quien_no_se_le_avisa_a_el_pero_si_a_los_demas_chats()
    {
        var (http, caja, _, encargado, dueno, departamento) = await PrepararAsync();
        using (var alcance = servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            if (!db.ChatsTelegram.Any(c => c.ChatId == 4242))
                db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = dueno.UsuarioId, ChatId = 4242, VinculadoUtc = DateTime.UtcNow });
            if (!db.ChatsTelegram.Any(c => c.ChatId == 4343))
                db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = encargado.UsuarioId, ChatId = 4343, VinculadoUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var ficha = await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId, Ficha(departamento, "Marcatextos verde", $"MARCA-{Guid.NewGuid():N}"[..18]))));
        FichaProductoDto ConPrecio(FichaProductoDto f, long menudeo) =>
            f with { Variantes = [f.Variantes[0] with { Precios = [new PrecioFichaDto(null, 1000, menudeo), new PrecioFichaDto(null, 12000, 1200)] }] };

        await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId, ConPrecio(ficha, 1700))));
        await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(dueno.UsuarioId, caja.SucursalId, ConPrecio(ficha, 1600))));

        using var otro = servicio.Fabrica.Services.CreateScope();
        var base_ = otro.ServiceProvider.GetRequiredService<PosDbContext>();
        var mensajes = await base_.MensajesTelegram.AsNoTracking()
            .Where(m => m.Tipo == TipoAlerta.CambioPrecioCatalogo && m.Texto.Contains("Marcatextos verde"))
            .Select(m => new { m.ChatId, m.Texto }).ToListAsync();
        Assert.Single(mensajes, m => m.Texto.Contains("$15.00 → $17.00") && m.ChatId == 4242);
        Assert.DoesNotContain(mensajes, m => m.Texto.Contains("$15.00 → $17.00") && m.ChatId == 4343);
        Assert.Single(mensajes, m => m.Texto.Contains("$17.00 → $16.00") && m.ChatId == 4343);
        Assert.DoesNotContain(mensajes, m => m.Texto.Contains("$17.00 → $16.00") && m.ChatId == 4242);
    }

    [Fact]
    public async Task El_cajero_da_de_alta_desde_la_caja_lo_que_no_existe_y_queda_por_completar()
    {
        var (http, caja, cajero, encargado, _, departamento) = await PrepararAsync();
        var codigo = "7509999" + Random.Shared.Next(100000, 999999);

        var producto = await Leer<ProductoVentaDto>(await http.PostAsJsonAsync("api/catalogo/alta-rapida",
            new SolicitudAltaRapida(cajero.UsuarioId, caja.SucursalId, codigo, "Sacapuntas de metal", 900, departamento)));
        var otraVez = await http.PostAsJsonAsync("api/catalogo/alta-rapida",
            new SolicitudAltaRapida(cajero.UsuarioId, caja.SucursalId, codigo, "Sacapuntas", 900, departamento));
        var porCompletar = await http.GetFromJsonAsync<List<ProductoCatalogoDto>>($"api/catalogo/productos?sucursalId={caja.SucursalId}&porCompletar=true");
        var renglon = porCompletar!.Single(p => p.Codigo == codigo);
        var ficha = await Leer<FichaProductoDto>(await http.GetAsync($"api/catalogo/productos/{renglon.ProductoId}?sucursalId={caja.SucursalId}&usuarioId={encargado.UsuarioId}"));
        var completada = await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId, ficha with { Marca = "Maped" })));

        Assert.Equal(900, producto.Precios.Single().PrecioCentavos);
        Assert.Equal(HttpStatusCode.BadRequest, otraVez.StatusCode);
        Assert.True(ficha.PorCompletar);
        Assert.False(completada.PorCompletar);
        Assert.Equal(OrigenCodigo.Fabrica, completada.Variantes[0].Presentaciones[0].Codigos[0].Origen);
    }

    /// <summary>Un emoji pegado en un nombre (auditoría, 2026-11-04): se guarda, y la copia de las cajas y la búsqueda siguen.</summary>
    [Fact]
    public async Task Un_emoji_en_el_nombre_no_deja_al_servicio_sin_catalogo()
    {
        var (http, caja, cajero, _, _, departamento) = await PrepararAsync();
        var codigo = "7509999" + Random.Shared.Next(100000, 999999);

        var alta = await http.PostAsJsonAsync("api/catalogo/alta-rapida",
            new SolicitudAltaRapida(cajero.UsuarioId, caja.SucursalId, codigo, "Mochila 🎒 escolar", 9000, departamento));
        var paquete = await http.GetAsync($"api/cajas/{caja.Id}/paquete");

        Assert.Equal(HttpStatusCode.OK, alta.StatusCode);
        Assert.Equal(HttpStatusCode.OK, paquete.StatusCode);
    }

    private sealed record CodigoInternoRespuesta(string Codigo);
}
