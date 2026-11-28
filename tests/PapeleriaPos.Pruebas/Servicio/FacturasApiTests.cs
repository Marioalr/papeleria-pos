using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Pruebas.Datos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Inventario › «Recibir con factura» (2026-11-27), desde el servicio y con un CFDI ficticio con la forma del de ejemplo: al cargar el
/// XML el proveedor sale de su RFC, cada concepto es un renglón (el flete no: es servicio), lo que el POS reconoce llega emparejado y
/// nada entra todavía. La misma factura no se carga ni se recibe dos veces. El cajero la recibe como dice, o deja los cambios marcados
/// para revisión; el encargado la recibe con cambios. Al recibirla entra con el costo de la factura (con descuento e IVA; lo que faltó
/// no entra), el proveedor guarda su último costo y lo emparejado a mano llega emparejado en la siguiente factura. En el piloto con
/// eleventa es solo el documento.
/// </summary>
public sealed class FacturasApiTests(ServicioDePrueba matriz) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<string> RechazoAsync(HttpResponseMessage r, HttpStatusCode codigo)
    {
        Assert.Equal(codigo, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    private sealed record Escenario(ServicioDePrueba Servicio, HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado)
    {
        public Guid Sucursal => Caja.SucursalId;
    }

    private static async Task<Escenario> PrepararAsync(ServicioDePrueba servicio, HttpClient? http = null)
    {
        http ??= servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Escenario(servicio, http, caja, await Entrar("cajero", "1111"), await Entrar("encargado", "2222"));
    }

    private static string CodigoNuevo() => $"FA{Guid.NewGuid():N}"[..14];

    /// <summary>Un producto por pieza con ese código (y, si se dice, su caja de 12 con el otro), con 2 piezas a $10. Regresa su variante.</summary>
    private static async Task<(Guid Variante, Guid Pieza, Guid? Caja)> ProductoAsync(Escenario e, string nombre, string codigo, string? codigoDeLaCaja = null)
    {
        var departamento = (await e.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        List<PresentacionFichaDto> presentaciones = [new(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])];
        if (codigoDeLaCaja is not null)
            presentaciones.Add(new(null, "Caja 12", 12_000, false, [new CodigoFichaDto(codigoDeLaCaja)]));
        var ficha = await Leer<FichaProductoDto>(await e.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(e.Encargado.UsuarioId,
            e.Sucursal, new FichaProductoDto(null, nombre, departamento, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
                [new VarianteFichaDto(null, "", UnidadBase.Pieza, true, presentaciones, [new PrecioFichaDto(null, 1000, 2500)])]))));
        var variante = ficha.Variantes[0];
        await using var db = e.Servicio.Base();
        var v = await db.Variantes.SingleAsync(x => x.Id == variante.Id);
        await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(e.Sucursal, v, TipoMovimiento.InventarioInicial, Cantidad.Piezas(2), 2000, DateTime.UtcNow,
            documentoTipo: OrigenDelMovimiento.ImportacionEleventa));
        return (variante.Id!.Value, variante.Presentaciones.Single(p => p.EsBase).Id!.Value, variante.Presentaciones.SingleOrDefault(p => !p.EsBase)?.Id);
    }

    private static async Task<(decimal Cantidad, long Costo)> ExistenciaAsync(Escenario e, Guid variante)
    {
        await using var db = e.Servicio.Base();
        var existencia = await db.Existencias.AsNoTracking().SingleOrDefaultAsync(x => x.VarianteId == variante);
        return (existencia?.Cantidad.EnUnidades ?? 0, existencia?.CostoCentavos ?? 0);
    }

    private static async Task NivelAsync(Escenario e, SesionDto quien, AccionControlada accion, NivelPermiso nivel)
    {
        await using var db = e.Servicio.Base();
        (await db.PermisosUsuario.SingleAsync(p => p.UsuarioId == quien.UsuarioId && p.Accion == accion)).Nivel = nivel;
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> CargarAsync(Escenario e, SesionDto quien, byte[] xml, string archivo = "PGF20431.xml") =>
        e.Http.PostAsJsonAsync("api/facturas", new SolicitudImportarFactura(quien.UsuarioId, e.Sucursal, archivo, Convert.ToBase64String(xml), e.Caja.Id));

    private static Task<HttpResponseMessage> RecibirAsync(Escenario e, SesionDto quien, Guid id, PasoDeRecepcion paso, params RenglonDeFactura[] renglones) =>
        e.Http.PostAsJsonAsync($"api/facturas/{id}/recibir", new SolicitudRecibirFactura(quien.UsuarioId, e.Sucursal, renglones, paso, e.Caja.Id));

    /// <summary>Todos los renglones como están, con lo que se cambia de algunos.</summary>
    private static RenglonDeFactura[] Como(FacturaDto f, Func<FacturaRenglonDto, RenglonDeFactura?>? cambio = null) =>
        [.. f.Renglones.Select(r => cambio?.Invoke(r) ?? new RenglonDeFactura(r.Id, r.VarianteId, r.PresentacionId, (long)(r.Llego * 1000), r.Nota))];

    [Fact]
    public async Task Se_carga_el_xml_se_empareja_se_marca_y_se_recibe_con_el_costo_de_la_factura()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var e = await PrepararAsync(servicio);
        var (pistola, _, _) = await ProductoAsync(e, "Pistola de silicón chica", "06260007");
        var (cartulina, cartulinaPieza, _) = await ProductoAsync(e, "Cartulina fluorescente verde", CodigoNuevo());
        var (marcador, _, marcadorCaja) = await ProductoAsync(e, "Marcador para pizarrón negro", CodigoNuevo(), CodigoNuevo());
        var uuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var xml = CfdiDePrueba.Factura(uuid,
        [
            // Su clave es el código de barras de aquí: llega emparejada.
            new("06260007", "PISTOLA P/SILICON CHICA NEGRA BLT", 25, 40m),
            new("02080025", "CARTULINA FLUOR 66X48C VERDE", 50, 4m, Descuento: 20m),
            new("08350095", "MARCADOR P/PIZARRON GRSO NEG CJA/12", 1, 184.31m),
            new("08000087", "FLETE", 1, 125.01m, Unidad: "Unidad de servicio", ClaveUnidad: "E48", ClaveProdServ: "78121502"),
        ]);

        // ── El cajero la carga: el proveedor sale del RFC, el flete no se toma y nada entra ──
        var cargada = await Leer<FacturaDto>(await CargarAsync(e, e.Cajero, xml));
        Assert.Equal((EstadoFactura.PorRecibir, "PAPELERA DEL GOLFO", CfdiDePrueba.Rfc, "PGF20431", uuid, false),
            (cargada.Estado, cargada.Proveedor, cargada.Rfc, cargada.Documento, cargada.Uuid, cargada.PuedeModificar));
        Assert.Contains(cargada.Avisos, a => a.StartsWith("«FLETE» es un servicio, no mercancía ($145.01)", StringComparison.Ordinal));
        Assert.Contains(cargada.Avisos, a => a.Contains("Se dio de alta el proveedor «PAPELERA DEL GOLFO»"));
        Assert.Equal(3, cargada.Renglones.Count);
        var (rPistola, rCartulina, rMarcador) = (cargada.Renglones[0], cargada.Renglones[1], cargada.Renglones[2]);
        Assert.Equal((pistola, true, 25m, 25m), (rPistola.VarianteId!.Value, rPistola.Reconocido, rPistola.Cantidad, rPistola.Llego));
        Assert.Equal(((Guid?)null, (Guid?)null), (rCartulina.VarianteId, rMarcador.VarianteId));
        // El cajero no ve los costos.
        Assert.Equal(((long?)null, (long?)null, false), (cargada.TotalCentavos, rPistola.ImporteCentavos, cargada.VeCostos));
        Assert.Equal((2m, 2000L), await ExistenciaAsync(e, pistola));

        // La misma factura otra vez: es la que ya estaba.
        var otraVez = await Leer<FacturaDto>(await CargarAsync(e, e.Cajero, xml, "copia.xml"));
        Assert.Equal((true, cargada.Id), (otraVez.YaEstaba, otraVez.Id));

        // ── El cajero empareja la cartulina y el marcador (por cajas), y de pistolas llegaron 20: no la puede recibir así ──
        var revisado = Como(cargada, r =>
            r.Id == rPistola.Id ? new RenglonDeFactura(r.Id, pistola, r.PresentacionId, 20_000, "venían 5 de menos")
            : r.Id == rCartulina.Id ? new RenglonDeFactura(r.Id, cartulina, cartulinaPieza, 50_000, null)
            : r.Id == rMarcador.Id ? new RenglonDeFactura(r.Id, marcador, marcadorCaja, 1_000, null)
            : null);
        var sinPermiso = await RechazoAsync(await RecibirAsync(e, e.Cajero, cargada.Id, PasoDeRecepcion.Recibir, revisado), HttpStatusCode.Forbidden);
        Assert.Contains("«Recibir con factura: modificar lo que llegó»", sinPermiso);
        Assert.Contains("Encargado de prueba", sinPermiso);
        var marcada = await Leer<FacturaDto>(await RecibirAsync(e, e.Cajero, cargada.Id, PasoDeRecepcion.MandarARevision, revisado));
        Assert.Equal((EstadoFactura.EnRevision, "Cajero de prueba"), (marcada.Estado, marcada.Marco));
        Assert.Equal([true, true, true], marcada.Renglones.Select(r => r.Cambio));
        Assert.Equal((2m, 2000L), await ExistenciaAsync(e, pistola));

        // ── El encargado la recibe así: entra lo que llegó, al costo de la factura ──
        var lista = await e.Http.GetFromJsonAsync<FacturasDto>($"api/sucursales/{e.Sucursal}/facturas?usuarioId={e.Encargado.UsuarioId}");
        var resumen = Assert.Single(lista!.Facturas);
        Assert.Equal((EstadoFactura.EnRevision, 3, true), (resumen.Estado, resumen.Cambios, lista.PuedeModificar));
        Assert.Equal(116000 + 20880 + 21380 + 14501, resumen.TotalCentavos);
        var abierta = await Leer<FacturaDto>(await e.Http.GetAsync($"api/sucursales/{e.Sucursal}/facturas/{cargada.Id}?usuarioId={e.Encargado.UsuarioId}"));
        Assert.Equal(("venían 5 de menos", 116000L), (abierta.Renglones[0].Nota, abierta.Renglones[0].ImporteCentavos!.Value));
        var recibida = await Leer<FacturaDto>(await RecibirAsync(e, e.Encargado, cargada.Id, PasoDeRecepcion.Recibir, Como(abierta)));
        Assert.Equal((EstadoFactura.Recibida, true, "Encargado de prueba"), (recibida.Estado, recibida.MovioExistencias, recibida.Cerro));
        // De las pistolas, 20 de 25 de $1,160.00: lo que faltó no entra (se reclama). La cartulina, con su descuento y su IVA. El
        // marcador, una caja de 12.
        Assert.Equal((22m, 2000L + 92800), await ExistenciaAsync(e, pistola));
        Assert.Equal((52m, 2000L + 20880), await ExistenciaAsync(e, cartulina));
        Assert.Equal((14m, 2000L + 21380), await ExistenciaAsync(e, marcador));
        // Otra vez (se perdió la respuesta): la misma, sin entrar dos veces.
        Assert.Equal(EstadoFactura.Recibida, (await Leer<FacturaDto>(await RecibirAsync(e, e.Encargado, cargada.Id, PasoDeRecepcion.Recibir, Como(abierta)))).Estado);
        Assert.Equal(22m, (await ExistenciaAsync(e, pistola)).Cantidad);

        await using (var db = servicio.Base())
        {
            var compra = await db.Compras.AsNoTracking().SingleAsync(c => c.Id == cargada.Id);
            Assert.Equal((EstadoCompra.Cerrada, true), (compra.Estado, compra.ConFactura));
            Assert.Equal(3, await db.MovimientosInventario.CountAsync(m => m.DocumentoId == compra.Id && m.Tipo == TipoMovimiento.Compra));
            // El último costo con el proveedor, por pieza; y la clave.
            var costos = await db.ProveedoresVariantes.AsNoTracking().Where(p => p.ProveedorId == compra.ProveedorId).ToDictionaryAsync(p => p.VarianteId);
            Assert.Equal((4640L, "06260007"), (costos[pistola].UltimoCostoCentavos!.Value, costos[pistola].CodigoProveedor));
            Assert.Equal((418L, 1782L), (costos[cartulina].UltimoCostoCentavos!.Value, costos[marcador].UltimoCostoCentavos!.Value));
            // La bitácora: cargarla, marcarla y recibirla con sus cambios (y lo que faltó, para reclamarlo).
            var bitacora = await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.RecibirConFactura || b.Accion == AccionControlada.ModificarRecepcionConFactura)
                .OrderBy(b => b.FechaUtc).ThenBy(b => b.Id).Select(b => b.Detalle).ToListAsync();
            Assert.Contains(bitacora, d => d.StartsWith("Alta del proveedor «PAPELERA DEL GOLFO» (RFC PGO050505AB1)", StringComparison.Ordinal));
            Assert.Contains(bitacora, d => d.StartsWith($"Cargó la factura PGF20431 de «PAPELERA DEL GOLFO» («PGF20431.xml», folio fiscal {uuid})", StringComparison.Ordinal));
            Assert.Contains(bitacora, d => d.StartsWith("Marcó para revisión la factura PGF20431", StringComparison.Ordinal) && d.Contains("llegaron 20 de 25"));
            Assert.Contains(bitacora, d => d.StartsWith("Recibió con la factura PGF20431 la compra", StringComparison.Ordinal) && d.Contains("faltó 5 de «Pistola de silicón chica»"));
            Assert.Contains(bitacora, d => d.StartsWith("Recibió con cambios la factura PGF20431", StringComparison.Ordinal)
                && d.Contains("se emparejó con «Marcador para pizarrón negro» (Caja 12)"));
        }

        // ── La siguiente factura del proveedor: lo emparejado a mano ya llega emparejado, y el cajero la recibe como dice ──
        var siguiente = await Leer<FacturaDto>(await CargarAsync(e, e.Cajero, CfdiDePrueba.Factura(Guid.NewGuid().ToString(),
        [
            new("02080025", "CARTULINA FLUOR 66X48C VERDE", 10, 4m),
            new("08350095", "MARCADOR P/PIZARRON GRSO NEG CJA/12", 2, 184.31m),
        ], folio: "20500")));
        Assert.Equal([(cartulina, true), (marcador, true)], siguiente.Renglones.Select(r => (r.VarianteId!.Value, r.Reconocido)));
        Assert.Equal(marcadorCaja, siguiente.Renglones[1].PresentacionId);
        Assert.False(siguiente.Renglones.Any(r => r.Cambio));
        var comoDice = await Leer<FacturaDto>(await RecibirAsync(e, e.Cajero, siguiente.Id, PasoDeRecepcion.Recibir, Como(siguiente)));
        Assert.Equal(EstadoFactura.Recibida, comoDice.Estado);
        Assert.Equal((62m, 38m), ((await ExistenciaAsync(e, cartulina)).Cantidad, (await ExistenciaAsync(e, marcador)).Cantidad));
        // Ya recibida, la misma factura otra vez es la que ya estaba (recibida): no se carga ni se recibe dos veces.
        var repetida = await Leer<FacturaDto>(await CargarAsync(e, e.Cajero, xml));
        Assert.Equal((true, EstadoFactura.Recibida, cargada.Id), (repetida.YaEstaba, repetida.Estado, repetida.Id));
    }

    [Fact]
    public async Task Lo_que_llego_y_no_venia_se_agrega_y_lo_que_no_llego_no_hace_falta_emparejarlo()
    {
        var e = await PrepararAsync(matriz);
        var codigo = CodigoNuevo();
        var (pegamento, pegamentoPieza, _) = await ProductoAsync(e, "Pegamento blanco de 1 kg", codigo);
        var (tijera, tijeraPieza, _) = await ProductoAsync(e, "Tijera de oficina", CodigoNuevo());
        var cargada = await Leer<FacturaDto>(await CargarAsync(e, e.Encargado, CfdiDePrueba.Factura(Guid.NewGuid().ToString(),
        [
            new(codigo, "PEGAMENTO BLANCO 1KG", 6, 46.22m, Unidad: "PIEZA"),
            new("99990001", "PRODUCTO QUE NO TENEMOS", 3, 10m),
        ], folio: "30001")));
        Assert.Null(cargada.Renglones[1].VarianteId);
        // Sin emparejar y con algo que llegó no se recibe; con 0 en lo que llegó, sí. Las tijeras no venían: llegaron 2.
        var sinEmparejar = await RechazoAsync(await RecibirAsync(e, e.Encargado, cargada.Id, PasoDeRecepcion.Recibir, Como(cargada)), HttpStatusCode.BadRequest);
        Assert.StartsWith("Falta emparejar «PRODUCTO QUE NO TENEMOS» con su producto de aquí", sinEmparejar);
        var revisado = Como(cargada, r => r.VarianteId is null ? new RenglonDeFactura(r.Id, null, null, 0, "no llegó") : null)
            .Append(new RenglonDeFactura(null, tijera, tijeraPieza, 2_000, "venían en la caja")).ToArray();
        var guardada = await Leer<FacturaDto>(await RecibirAsync(e, e.Encargado, cargada.Id, PasoDeRecepcion.Guardar, revisado));
        var agregada = Assert.Single(guardada.Renglones, r => r.Agregado);
        Assert.Equal((tijera, 2m, 0m, true), (agregada.VarianteId!.Value, agregada.Llego, agregada.Cantidad, agregada.Cambio));
        var recibida = await Leer<FacturaDto>(await RecibirAsync(e, e.Encargado, cargada.Id, PasoDeRecepcion.Recibir, Como(guardada)));
        Assert.Equal(EstadoFactura.Recibida, recibida.Estado);
        Assert.Equal(8m, (await ExistenciaAsync(e, pegamento)).Cantidad);
        // Lo que no venía entra a su costo de aquí ($10 la pieza).
        Assert.Equal((4m, 4000L), await ExistenciaAsync(e, tijera));
        Assert.Equal(pegamentoPieza, recibida.Renglones[0].PresentacionId);
    }

    [Fact]
    public async Task Lo_que_no_es_la_factura_de_una_compra_no_se_carga_y_sin_permiso_nada()
    {
        var e = await PrepararAsync(matriz);
        var concepto = new ConceptoDePrueba("11110000", "LAPIZ", 1, 5m);
        Assert.Equal(RegistroFacturas.SoloElXml, await RechazoAsync(await CargarAsync(e, e.Cajero, DocumentoProveedorTests.Pdf((700, [(50, "Nota 1")]))),
            HttpStatusCode.BadRequest));
        Assert.StartsWith("La factura no trae su timbre", await RechazoAsync(await CargarAsync(e, e.Cajero,
            CfdiDePrueba.Factura("x", [concepto], conTimbre: false)), HttpStatusCode.BadRequest));
        Assert.Contains("nota de crédito", await RechazoAsync(await CargarAsync(e, e.Cajero, CfdiDePrueba.Factura(Guid.NewGuid().ToString(), [concepto], tipo: "E")),
            HttpStatusCode.BadRequest));
        Assert.Equal("La factura no trae mercancía: todos sus conceptos son servicios.", await RechazoAsync(await CargarAsync(e, e.Cajero,
            CfdiDePrueba.Factura(Guid.NewGuid().ToString(), [new("1", "FLETE", 1, 100m, ClaveUnidad: "E48")])), HttpStatusCode.BadRequest));

        // Descartada, no entra nada y se puede cargar otra vez; las de la factura no se tocan desde «Compras y notas».
        var xml = CfdiDePrueba.Factura(Guid.NewGuid().ToString(), [concepto], folio: "40001");
        var cargada = await Leer<FacturaDto>(await CargarAsync(e, e.Cajero, xml));
        Assert.Equal(RegistroCompras.DeLaFactura, await RechazoAsync(await e.Http.PostAsJsonAsync($"api/compras/{cargada.Id}/cerrar",
            new SolicitudCompra(e.Encargado.UsuarioId)), HttpStatusCode.BadRequest));
        Assert.True((await e.Http.PostAsJsonAsync($"api/facturas/{cargada.Id}/descartar", new SolicitudDeFactura(e.Cajero.UsuarioId, e.Sucursal, e.Caja.Id))).IsSuccessStatusCode);
        var otraVez = await Leer<FacturaDto>(await CargarAsync(e, e.Cajero, xml));
        Assert.Equal((false, EstadoFactura.PorRecibir), (otraVez.YaEstaba, otraVez.Estado));
        Assert.NotEqual(cargada.Id, otraVez.Id);
        Assert.Contains("se descartó", await RechazoAsync(await RecibirAsync(e, e.Cajero, cargada.Id, PasoDeRecepcion.Recibir), HttpStatusCode.BadRequest));

        // Sin «Recibir con la factura (XML)»: nada, y el error dice quién lo tiene.
        await NivelAsync(e, e.Cajero, AccionControlada.RecibirConFactura, NivelPermiso.NoPermitido);
        var sinPermiso = await RechazoAsync(await CargarAsync(e, e.Cajero, xml), HttpStatusCode.Forbidden);
        Assert.Contains("«Recibir con la factura (XML)»", sinPermiso);
        Assert.Contains("Encargado de prueba", sinPermiso);
        await RechazoAsync(await e.Http.GetAsync($"api/sucursales/{e.Sucursal}/facturas?usuarioId={e.Cajero.UsuarioId}"), HttpStatusCode.Forbidden);
        await NivelAsync(e, e.Cajero, AccionControlada.RecibirConFactura, NivelPermiso.Permitido);
    }

    [Fact]
    public async Task En_el_piloto_con_eleventa_la_factura_es_solo_el_documento_y_no_mueve_existencias()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        using var piloto = propio.Fabrica.WithWebHostBuilder(x => x.UseSetting("Eleventa:Activo", "true"));
        // El producto se da de alta antes del piloto (en el piloto, las altas son en eleventa).
        var codigo = CodigoNuevo();
        var (lapiz, _, _) = await ProductoAsync(await PrepararAsync(propio), "Lápiz del piloto", codigo);
        var e = await PrepararAsync(propio, piloto.CreateClient());

        var cargada = await Leer<FacturaDto>(await CargarAsync(e, e.Cajero, CfdiDePrueba.Factura(Guid.NewGuid().ToString(), [new(codigo, "LAPIZ", 12, 3m)])));
        Assert.Equal(RegistroFacturas.EnElPiloto, cargada.Piloto);
        var recibida = await Leer<FacturaDto>(await RecibirAsync(e, e.Cajero, cargada.Id, PasoDeRecepcion.Recibir, Como(cargada)));
        Assert.Equal((EstadoFactura.Recibida, false), (recibida.Estado, recibida.MovioExistencias));
        Assert.Equal(2m, (await ExistenciaAsync(e, lapiz)).Cantidad);
        await using var db = propio.Base();
        Assert.False(await db.MovimientosInventario.AnyAsync(m => m.DocumentoId == cargada.Id));
        // Queda lo que sirve al dejar eleventa: el último costo con el proveedor (12 de $3 + IVA: $3.48 la pieza).
        Assert.Equal(348L, (await db.ProveedoresVariantes.AsNoTracking().SingleAsync(p => p.VarianteId == lapiz)).UltimoCostoCentavos);
        Assert.EndsWith("(piloto con eleventa: sin mover existencias)",
            (await db.Bitacora.AsNoTracking().SingleAsync(b => b.Detalle.StartsWith("Recibió con la factura"))).Detalle);
    }
}
