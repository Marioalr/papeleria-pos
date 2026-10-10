using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Telegram;

namespace PapeleriaPos.Pruebas.Servicio;

public class ComprasApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record Escenario(
        HttpClient Http, Guid Sucursal, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno, Guid Departamento, ProveedorDto Proveedor);

    private sealed record Articulo(Guid Variante, Guid Pieza, Guid Caja, string Nombre);

    private async Task<Escenario> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var opciones = (await http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!;
        var proveedor = await Leer<ProveedorDto>(await http.PostAsJsonAsync("api/proveedores", new SolicitudGuardarProveedor(encargado.UsuarioId, caja.SucursalId,
            new ProveedorDto(null, $"Distribuidora {Guid.NewGuid():N}"[..24], "555 123 4567", "dpa010101aaa", null))));
        return new Escenario(http, caja.SucursalId, cajero, encargado, dueno, opciones.Departamentos[0].Id, proveedor);
    }

    /// <summary>Un producto nuevo (sin existencia ni proveedores) con pieza y caja de 12, a $15.00 la pieza.</summary>
    private static async Task<Articulo> ProductoAsync(Escenario e, string nombre)
    {
        var codigo = $"CMP{Guid.NewGuid():N}"[..16];
        var ficha = await Leer<FichaProductoDto>(await e.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(e.Encargado.UsuarioId, e.Sucursal,
            new FichaProductoDto(null, nombre, e.Departamento, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                [
                    new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)]),
                    new PresentacionFichaDto(null, "Caja 12", 12000, false, [new CodigoFichaDto(codigo + "C")]),
                ],
                [new PrecioFichaDto(null, 1000, 1500)]),
            ]))));
        var v = ficha.Variantes[0];
        return new Articulo(v.Id!.Value, v.Presentaciones.Single(p => p.EsBase).Id!.Value, v.Presentaciones.Single(p => !p.EsBase).Id!.Value, nombre);
    }

    private static async Task<CompraDto> AbrirAsync(Escenario e, SesionDto quien, string? documento = "N-123") =>
        await Leer<CompraDto>(await e.Http.PostAsJsonAsync("api/compras", new SolicitudAbrirCompra(quien.UsuarioId, e.Sucursal, e.Proveedor.Id!.Value, documento)));

    private static Task<HttpResponseMessage> ContarAsync(Escenario e, CompraDto compra, SesionDto quien, Articulo a, Guid presentacion, long piezas) =>
        e.Http.PostAsJsonAsync($"api/compras/{compra.Id}/conteo", new SolicitudConteo(quien.UsuarioId, a.Variante, presentacion, piezas * 1000));

    private static Task<HttpResponseMessage> RenglonAsync(Escenario e, CompraDto compra, SesionDto quien, Articulo a, Guid presentacion, long piezas, long importe) =>
        e.Http.PostAsJsonAsync($"api/compras/{compra.Id}/renglones",
            new SolicitudRenglonCompra(quien.UsuarioId, null, a.Variante, presentacion, piezas * 1000, importe, "PROV-1"));

    private static Task<HttpResponseMessage> AccionAsync(Escenario e, CompraDto compra, string accion, SesionDto quien) =>
        e.Http.PostAsJsonAsync($"api/compras/{compra.Id}/{accion}", new SolicitudCompra(quien.UsuarioId));

    private async Task<FichaProductoDto> FichaAsync(Escenario e, Articulo a)
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        var productoId = await db.Variantes.Where(v => v.Id == a.Variante).Select(v => v.ProductoId).SingleAsync();
        return await Leer<FichaProductoDto>(await e.Http.GetAsync($"api/catalogo/productos/{productoId}?sucursalId={e.Sucursal}&usuarioId={e.Encargado.UsuarioId}"));
    }

    [Fact]
    public async Task Quien_recibe_cuenta_a_ciegas_y_al_cerrar_entra_al_inventario_con_el_costo_de_la_nota()
    {
        var e = await PrepararAsync();
        var a = await ProductoAsync(e, "Cuaderno de prueba compras");
        var compra = await AbrirAsync(e, e.Cajero);

        await Leer<CompraDto>(await ContarAsync(e, compra, e.Cajero, a, a.Caja, 2));
        var contada = await Leer<CompraDto>(await ContarAsync(e, compra, e.Cajero, a, a.Pieza, 3));
        var antesDeTerminar = await RenglonAsync(e, compra, e.Encargado, a, a.Caja, 3, 27000);
        var porCajero = await RenglonAsync(e, compra, e.Cajero, a, a.Caja, 3, 27000);
        await Leer<CompraDto>(await AccionAsync(e, compra, "terminar-conteo", e.Cajero));
        var conNota = await Leer<CompraDto>(await RenglonAsync(e, compra, e.Encargado, a, a.Caja, 3, 27000));
        var vistaDelCajero = await Leer<CompraDto>(await e.Http.GetAsync($"api/compras/{compra.Id}?usuarioId={e.Cajero.UsuarioId}"));
        var cerrada = await Leer<CompraDto>(await AccionAsync(e, compra, "cerrar", e.Encargado));
        var ficha = await FichaAsync(e, a);

        Assert.Equal(2, contada.Conteo.Count);
        Assert.Equal(27m, contada.Conteo.Sum(c => c.CantidadBase));
        Assert.Null(contada.Renglones);
        Assert.Equal(HttpStatusCode.BadRequest, antesDeTerminar.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
        // Quien cuenta nunca ve la nota ni los costos.
        Assert.Null(vistaDelCajero.Renglones);
        Assert.Null(vistaDelCajero.Comparacion);
        Assert.Null(vistaDelCajero.TotalCentavos);
        var comparada = Assert.Single(conNota.Comparacion!);
        Assert.Equal((36m, 27m, DiferenciaCompra.Falto, 750m), (comparada.Nota, comparada.Contado, comparada.Diferencia, comparada.CostoUnitarioCentavos));
        Assert.Equal(5000, comparada.MargenCentesimas);
        Assert.Null(comparada.PrecioSugeridoCentavos);

        Assert.Equal(EstadoCompra.Cerrada, cerrada.Estado);
        Assert.Equal("Encargado de prueba", cerrada.Costeo);
        var variante = Assert.Single(ficha.Variantes);
        Assert.Equal(27m, variante.Existencia);
        Assert.Equal(750m, variante.CostoPromedioCentavos);
        var proveedor = Assert.Single(variante.Proveedores!);
        Assert.Equal((e.Proveedor.Id!.Value, (long?)750, "PROV-1"), (proveedor.ProveedorId, proveedor.UltimoCostoCentavos, proveedor.CodigoProveedor));

        using var alcance = servicio.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        var movimiento = await db.MovimientosInventario.AsNoTracking().SingleAsync(m => m.VarianteId == a.Variante);
        Assert.Equal((TipoMovimiento.Compra, 20250L, nameof(Compra), compra.Id), (movimiento.Tipo, movimiento.CostoCentavos, movimiento.DocumentoTipo, movimiento.DocumentoId));
        Assert.Empty(await new RegistroInventario(db).VerificarExistenciasAsync());
        var bitacora = await db.Bitacora.AsNoTracking().Where(b => b.Detalle.Contains(compra.Folio)).ToListAsync();
        Assert.Contains(bitacora, b => b.Accion == AccionControlada.RecibirMercancia && b.UsuarioId == e.Cajero.UsuarioId);
        Assert.Contains(bitacora, b => b.Accion == AccionControlada.CostearCompra && b.Detalle.Contains("faltó 9 de «Cuaderno de prueba compras»") && b.MontoCentavos == 20250);
    }

    [Fact]
    public async Task Un_costo_que_deja_el_margen_bajo_avisa_y_sugiere_precio_pero_el_precio_no_cambia_solo()
    {
        var e = await PrepararAsync();
        using (var alcance = servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            if (!db.ChatsTelegram.Any(c => c.ChatId == 5151))
            {
                db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = e.Dueno.UsuarioId, ChatId = 5151, VinculadoUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
        }
        var a = await ProductoAsync(e, "Folder de prueba margen");
        var compra = await AbrirAsync(e, e.Encargado);
        await Leer<CompraDto>(await ContarAsync(e, compra, e.Encargado, a, a.Pieza, 10));
        await Leer<CompraDto>(await AccionAsync(e, compra, "terminar-conteo", e.Encargado));
        await Leer<CompraDto>(await RenglonAsync(e, compra, e.Encargado, a, a.Pieza, 10, 12000));

        var cerrada = await Leer<CompraDto>(await AccionAsync(e, compra, "cerrar", e.Encargado));
        var antes = await FichaAsync(e, a);
        var aplicado = await e.Http.PostAsJsonAsync("api/catalogo/precio-menudeo", new SolicitudPrecioMenudeo(e.Encargado.UsuarioId, e.Sucursal, a.Variante, 1750));
        var despues = await FichaAsync(e, a);
        var otraVez = await Leer<CompraDto>(await e.Http.GetAsync($"api/compras/{compra.Id}?usuarioId={e.Encargado.UsuarioId}"));

        var comparada = Assert.Single(cerrada.Comparacion!);
        Assert.Equal((2000, 3000, 1750L), (comparada.MargenCentesimas, comparada.MargenMinimoCentesimas, comparada.PrecioSugeridoCentavos));
        Assert.Equal(1500, antes.Variantes[0].Precios.Single().PrecioCentavos);
        Assert.Equal(HttpStatusCode.NoContent, aplicado.StatusCode);
        Assert.Equal(1750, despues.Variantes[0].Precios.Single().PrecioCentavos);
        Assert.Null(Assert.Single(otraVez.Comparacion!).PrecioSugeridoCentavos);

        using var otro = servicio.Fabrica.Services.CreateScope();
        var base_ = otro.ServiceProvider.GetRequiredService<PosDbContext>();
        var aviso = await base_.MensajesTelegram.AsNoTracking().SingleAsync(m => m.ChatId == 5151 && m.Tipo == TipoAlerta.Compra && m.Texto.Contains(compra.Folio));
        Assert.Contains("«Folder de prueba margen»: margen 20 % (mínimo 30 %), precio $15.00 → sugerido $17.50", aviso.Texto);
        Assert.Contains(await base_.Bitacora.AsNoTracking().Select(b => b.Detalle).ToListAsync(), d => d == "Precio de «Folder de prueba margen»: 15.00 → 17.50");
    }

    [Fact]
    public async Task El_margen_minimo_lo_pone_el_dueno_y_cada_departamento_puede_tener_el_suyo()
    {
        var e = await PrepararAsync();
        var margenes = await Leer<MargenesDto>(await e.Http.GetAsync($"api/sucursales/{e.Sucursal}/margenes"));
        var conDepartamento = margenes with
        {
            Departamentos = margenes.Departamentos.Select(d => d.DepartamentoId == e.Departamento ? d with { MargenMinimoCentesimas = 1500 } : d).ToList(),
        };

        var porEncargado = await e.Http.PutAsJsonAsync($"api/sucursales/{e.Sucursal}/margenes", new SolicitudMargenes(e.Encargado.UsuarioId, conDepartamento));
        var guardados = await Leer<MargenesDto>(await e.Http.PutAsJsonAsync($"api/sucursales/{e.Sucursal}/margenes", new SolicitudMargenes(e.Dueno.UsuarioId, conDepartamento)));
        var a = await ProductoAsync(e, "Engrapadora de prueba margen");
        var compra = await AbrirAsync(e, e.Encargado);
        await Leer<CompraDto>(await ContarAsync(e, compra, e.Encargado, a, a.Pieza, 1));
        await Leer<CompraDto>(await AccionAsync(e, compra, "terminar-conteo", e.Encargado));
        var conNota = await Leer<CompraDto>(await RenglonAsync(e, compra, e.Encargado, a, a.Pieza, 1, 1200));
        await e.Http.PutAsJsonAsync($"api/sucursales/{e.Sucursal}/margenes", new SolicitudMargenes(e.Dueno.UsuarioId, margenes));

        Assert.Equal(3000, margenes.MargenMinimoCentesimas);
        Assert.Equal(HttpStatusCode.Forbidden, porEncargado.StatusCode);
        Assert.Equal(1500, guardados.Departamentos.Single(d => d.DepartamentoId == e.Departamento).MargenMinimoCentesimas);
        var comparada = Assert.Single(conNota.Comparacion!);
        Assert.Equal((2000, 1500), (comparada.MargenCentesimas, comparada.MargenMinimoCentesimas));
        Assert.Null(comparada.PrecioSugeridoCentavos);
    }

    [Fact]
    public async Task Lo_que_llega_sin_estar_en_la_nota_entra_a_su_ultimo_costo()
    {
        var e = await PrepararAsync();
        var conCosto = await ProductoAsync(e, "Lápiz de prueba compras");
        var sinCosto = await ProductoAsync(e, "Borrador de prueba compras");

        // Una primera compra le da al lápiz su último costo con este proveedor: $2.00.
        var primera = await AbrirAsync(e, e.Encargado);
        await Leer<CompraDto>(await ContarAsync(e, primera, e.Encargado, conCosto, conCosto.Pieza, 5));
        await Leer<CompraDto>(await AccionAsync(e, primera, "terminar-conteo", e.Encargado));
        await Leer<CompraDto>(await RenglonAsync(e, primera, e.Encargado, conCosto, conCosto.Pieza, 5, 1000));
        await Leer<CompraDto>(await AccionAsync(e, primera, "cerrar", e.Encargado));

        var compra = await AbrirAsync(e, e.Cajero, documento: null);
        await Leer<CompraDto>(await ContarAsync(e, compra, e.Cajero, conCosto, conCosto.Pieza, 4));
        await Leer<CompraDto>(await ContarAsync(e, compra, e.Cajero, sinCosto, sinCosto.Pieza, 6));
        await Leer<CompraDto>(await AccionAsync(e, compra, "terminar-conteo", e.Cajero));
        await Leer<CompraDto>(await RenglonAsync(e, compra, e.Encargado, sinCosto, sinCosto.Caja, 1, 3000));
        var nota = await Leer<CompraDto>(await e.Http.GetAsync($"api/compras/{compra.Id}?usuarioId={e.Encargado.UsuarioId}"));
        var cerrada = await Leer<CompraDto>(await AccionAsync(e, compra, "cerrar", e.Encargado));
        var lapiz = await FichaAsync(e, conCosto);
        var borrador = await FichaAsync(e, sinCosto);

        Assert.Equal(DiferenciaCompra.NoVieneEnLaNota, nota.Comparacion!.Single(c => c.VarianteId == conCosto.Variante).Diferencia);
        Assert.Equal(DiferenciaCompra.Falto, nota.Comparacion!.Single(c => c.VarianteId == sinCosto.Variante).Diferencia);
        Assert.Equal(EstadoCompra.Cerrada, cerrada.Estado);
        // El lápiz entra a su último costo con este proveedor; del borrador entra lo contado (6 de 12) a $2.50.
        Assert.Equal((9m, 200m), (lapiz.Variantes[0].Existencia, lapiz.Variantes[0].CostoPromedioCentavos));
        Assert.Equal((6m, 250m), (borrador.Variantes[0].Existencia, borrador.Variantes[0].CostoPromedioCentavos));
    }

    [Fact]
    public async Task Sin_costo_conocido_lo_que_no_viene_en_la_nota_no_deja_cerrar()
    {
        var e = await PrepararAsync();
        var a = await ProductoAsync(e, "Regla de prueba compras");
        var b = await ProductoAsync(e, "Escuadra de prueba compras");
        var compra = await AbrirAsync(e, e.Cajero);
        await Leer<CompraDto>(await ContarAsync(e, compra, e.Cajero, a, a.Pieza, 2));
        await Leer<CompraDto>(await ContarAsync(e, compra, e.Cajero, b, b.Pieza, 2));
        await Leer<CompraDto>(await AccionAsync(e, compra, "terminar-conteo", e.Cajero));
        await Leer<CompraDto>(await RenglonAsync(e, compra, e.Encargado, a, a.Pieza, 2, 1000));

        var sinCosto = await AccionAsync(e, compra, "cerrar", e.Encargado);
        await Leer<CompraDto>(await RenglonAsync(e, compra, e.Encargado, b, b.Pieza, 2, 1400));
        var cerrada = await Leer<CompraDto>(await AccionAsync(e, compra, "cerrar", e.Encargado));
        var otraVez = await AccionAsync(e, compra, "cerrar", e.Encargado);

        Assert.Equal(HttpStatusCode.BadRequest, sinCosto.StatusCode);
        Assert.Contains("«Escuadra de prueba compras» llegó sin estar en la nota y no tiene costo", (await sinCosto.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal(2400, cerrada.TotalCentavos);
        Assert.Equal(HttpStatusCode.BadRequest, otraVez.StatusCode);
    }

    [Fact]
    public async Task La_factura_llena_la_nota_lo_que_no_reconoce_se_liga_una_vez_y_no_se_carga_dos_veces()
    {
        var e = await PrepararAsync();
        var conCodigo = await ProductoAsync(e, "Marcador de prueba documento");
        var otro = await ProductoAsync(e, "Pegamento de prueba documento");
        string codigoDe(Articulo a)
        {
            using var alcance = servicio.Fabrica.Services.CreateScope();
            return alcance.ServiceProvider.GetRequiredService<PosDbContext>().CodigosBarras.Single(c => c.PresentacionId == a.Pieza).Codigo;
        }
        var clave = $"PROV-{Guid.NewGuid():N}"[..14];
        var uuid = Guid.NewGuid().ToString();
        var factura = Datos.DocumentoProveedorTests.Factura(uuid, rfc: "DPA010101AAA", conceptos:
        [
            Datos.DocumentoProveedorTests.Concepto(codigoDe(conCodigo), "MARCADOR NEGRO", "4", "PIEZA", "40.00", "6.40"),
            Datos.DocumentoProveedorTests.Concepto(clave, "PEGAMENTO BARRA C/12", "1", "CAJA", "100.00", "16.00"),
        ]);
        async Task<CompraDto> ContadaAsync()
        {
            var compra = await AbrirAsync(e, e.Cajero, documento: null);
            await Leer<CompraDto>(await ContarAsync(e, compra, e.Cajero, conCodigo, conCodigo.Pieza, 4));
            await Leer<CompraDto>(await ContarAsync(e, compra, e.Cajero, otro, otro.Caja, 1));
            return await Leer<CompraDto>(await AccionAsync(e, compra, "terminar-conteo", e.Cajero));
        }
        Task<HttpResponseMessage> CargarAsync(CompraDto compra, byte[] contenido) =>
            e.Http.PostAsJsonAsync($"api/compras/{compra.Id}/documentos", new SolicitudDocumentoCompra(e.Encargado.UsuarioId, "factura.xml", Convert.ToBase64String(contenido)));

        var compra = await ContadaAsync();
        var porCajero = await e.Http.PostAsJsonAsync($"api/compras/{compra.Id}/documentos", new SolicitudDocumentoCompra(e.Cajero.UsuarioId, "f.xml", Convert.ToBase64String(factura)));
        var cargada = await Leer<CompraDto>(await CargarAsync(compra, factura));
        var sinLigar = await AccionAsync(e, compra, "cerrar", e.Encargado);
        var pendiente = cargada.Renglones!.Single(r => r.VarianteId is null);
        var ligada = await Leer<CompraDto>(await e.Http.PostAsJsonAsync($"api/compras/{compra.Id}/renglones",
            new SolicitudRenglonCompra(e.Encargado.UsuarioId, pendiente.Id, otro.Variante, otro.Caja, pendiente.CantidadMilesimas, pendiente.ImporteCentavos)));
        var cerrada = await Leer<CompraDto>(await AccionAsync(e, compra, "cerrar", e.Encargado));
        var proveedor = (await e.Http.GetFromJsonAsync<List<ProveedorDto>>("api/proveedores"))!.Single(p => p.Id == e.Proveedor.Id);

        var siguiente = await ContadaAsync();
        var repetida = await CargarAsync(siguiente, factura);
        var otraFactura = Datos.DocumentoProveedorTests.Factura(Guid.NewGuid().ToString(), rfc: "OTR020202BB2", conceptos:
            [Datos.DocumentoProveedorTests.Concepto(clave, "PEGAMENTO BARRA C/12", "1", "CAJA", "110.00", "17.60")]);
        var aprendida = await Leer<CompraDto>(await CargarAsync(siguiente, otraFactura));

        Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
        Assert.Equal("F1234", cargada.Documento);
        var documento = Assert.Single(cargada.Documentos!);
        Assert.DoesNotContain(documento.Avisos, a => a.Contains("RFC"));
        var reconocido = cargada.Renglones!.Single(r => r.VarianteId is not null);
        Assert.Equal((conCodigo.Pieza, "MARCADOR NEGRO", 4640L), (reconocido.PresentacionId!.Value, reconocido.Descripcion, reconocido.ImporteCentavos));
        Assert.Equal(("PEGAMENTO BARRA C/12", "CAJA", (string?)null), (pendiente.Descripcion, pendiente.Unidad, pendiente.Producto));
        Assert.Equal(HttpStatusCode.BadRequest, sinLigar.StatusCode);
        Assert.Contains("«PEGAMENTO BARRA C/12»", (await sinLigar.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.All(ligada.Comparacion!, c => Assert.Equal(DiferenciaCompra.Ninguna, c.Diferencia));
        Assert.Equal("PEGAMENTO BARRA C/12", ligada.Renglones!.Single(r => r.Id == pendiente.Id).Descripcion);
        Assert.Equal((EstadoCompra.Cerrada, 16240L), (cerrada.Estado, cerrada.TotalCentavos!.Value));
        Assert.Equal("DPA010101AAA", proveedor.Rfc);

        Assert.Equal(HttpStatusCode.BadRequest, repetida.StatusCode);
        Assert.Contains($"ya se cargó en la compra {compra.Folio}", (await repetida.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        var recordado = Assert.Single(aprendida.Renglones!);
        Assert.Equal((otro.Caja, 12760L), (recordado.PresentacionId!.Value, recordado.ImporteCentavos));
        Assert.Contains(Assert.Single(aprendida.Documentos!).Avisos, a => a.Contains("RFC OTR020202BB2") && a.Contains("RFC DPA010101AAA"));
        // El marcador se contó, pero esta factura no lo trae.
        Assert.Equal(DiferenciaCompra.NoVieneEnLaNota, aprendida.Comparacion!.Single(c => c.VarianteId == conCodigo.Variante).Diferencia);
    }

    [Fact]
    public async Task Contar_cero_quita_la_presentacion_y_un_renglon_de_la_nota_se_quita()
    {
        var e = await PrepararAsync();
        var a = await ProductoAsync(e, "Tijeras de prueba compras");
        var compra = await AbrirAsync(e, e.Encargado);
        await Leer<CompraDto>(await ContarAsync(e, compra, e.Encargado, a, a.Caja, 1));
        await Leer<CompraDto>(await ContarAsync(e, compra, e.Encargado, a, a.Pieza, 2));
        var sinPiezas = await Leer<CompraDto>(await ContarAsync(e, compra, e.Encargado, a, a.Pieza, 0));
        var mediaPieza = await e.Http.PostAsJsonAsync($"api/compras/{compra.Id}/conteo", new SolicitudConteo(e.Encargado.UsuarioId, a.Variante, a.Pieza, 500));
        await Leer<CompraDto>(await AccionAsync(e, compra, "terminar-conteo", e.Encargado));
        var conDos = await Leer<CompraDto>(await RenglonAsync(e, compra, e.Encargado, a, a.Pieza, 3, 600));
        await Leer<CompraDto>(await RenglonAsync(e, compra, e.Encargado, a, a.Caja, 1, 2400));
        var renglon = conDos.Renglones!.Single();
        var sinRenglon = await Leer<CompraDto>(await e.Http.PostAsJsonAsync($"api/compras/{compra.Id}/renglones/{renglon.Id}/quitar",
            new SolicitudCompra(e.Encargado.UsuarioId)));

        var conteo = Assert.Single(sinPiezas.Conteo);
        Assert.Equal((a.Caja, 12m), (conteo.PresentacionId, conteo.CantidadBase));
        Assert.Equal(HttpStatusCode.BadRequest, mediaPieza.StatusCode);
        var queda = Assert.Single(sinRenglon.Renglones!);
        Assert.Equal((a.Caja, 2400L), (queda.PresentacionId, queda.ImporteCentavos));
        Assert.Equal(DiferenciaCompra.Ninguna, Assert.Single(sinRenglon.Comparacion!).Diferencia);
    }

    [Fact]
    public async Task Quien_abre_cancela_mientras_cuenta_y_despues_solo_quien_captura_costos()
    {
        var e = await PrepararAsync();
        var a = await ProductoAsync(e, "Compás de prueba compras");
        var propia = await AbrirAsync(e, e.Cajero);
        var otra = await AbrirAsync(e, e.Cajero);
        await Leer<CompraDto>(await ContarAsync(e, otra, e.Cajero, a, a.Pieza, 1));
        await Leer<CompraDto>(await AccionAsync(e, otra, "terminar-conteo", e.Cajero));

        var cancelada = await Leer<CompraDto>(await e.Http.PostAsJsonAsync($"api/compras/{propia.Id}/cancelar", new SolicitudCancelarCompra(e.Cajero.UsuarioId, "Era de otro proveedor")));
        var porCajero = await e.Http.PostAsJsonAsync($"api/compras/{otra.Id}/cancelar", new SolicitudCancelarCompra(e.Cajero.UsuarioId, "Ya no"));
        var porEncargado = await Leer<CompraDto>(await e.Http.PostAsJsonAsync($"api/compras/{otra.Id}/cancelar", new SolicitudCancelarCompra(e.Encargado.UsuarioId, "Se devolvió todo")));
        var lista = await Leer<List<CompraResumenDto>>(await e.Http.GetAsync($"api/sucursales/{e.Sucursal}/compras?usuarioId={e.Cajero.UsuarioId}"));

        Assert.Equal((EstadoCompra.Cancelada, "Era de otro proveedor"), (cancelada.Estado, cancelada.Motivo));
        Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
        Assert.Equal(EstadoCompra.Cancelada, porEncargado.Estado);
        Assert.All(lista.Where(c => c.Id == propia.Id || c.Id == otra.Id), c => Assert.Equal(EstadoCompra.Cancelada, c.Estado));
        Assert.All(lista, c => Assert.Null(c.TotalCentavos));
    }
}
