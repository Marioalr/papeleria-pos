using System.Net;
using System.Net.Http.Json;
using System.Text;
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

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Inventario › «Traspasos» (2026-11-26, provisional mientras no exista el servidor), desde el servicio y con dos bases, una por
/// sucursal, como en la tienda: la que lo tramita arma la lista y la exporta (ahí sale su mercancía, con su movimiento y la
/// bitácora) y la que recibe importa el archivo (empata por código; nada entra todavía), lo revisa y lo recibe (ahí entra). El
/// mismo archivo no entra dos veces, ni en la sucursal de donde salió, ni alterado. Quien no tiene «Traspasos: modificar lo que
/// llegó» recibe lo que dice el archivo o deja los cambios marcados para revisión; quien lo tiene lo cambia y lo recibe así. En el
/// piloto con eleventa es solo el documento.
/// </summary>
public sealed class TraspasosApiTests(ServicioDePrueba matriz) : IClassFixture<ServicioDePrueba>
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

    private static string CodigoNuevo() => $"TR{Guid.NewGuid():N}"[..14];

    /// <summary>Un producto nuevo por pieza con ese código, con <paramref name="existencia"/> a <paramref name="costo"/> cada uno. Regresa su variante.</summary>
    private static async Task<Guid> ProductoAsync(Escenario e, string nombre, string codigo, decimal existencia, long costo)
    {
        var departamento = (await e.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        var ficha = await Leer<FichaProductoDto>(await e.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(e.Encargado.UsuarioId,
            e.Sucursal, new FichaProductoDto(null, nombre, departamento, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])],
                    [new PrecioFichaDto(null, 1000, 2000)]),
            ]))));
        var id = ficha.Variantes[0].Id!.Value;
        if (existencia != 0)
        {
            await using var db = e.Servicio.Base();
            var variante = await db.Variantes.SingleAsync(v => v.Id == id);
            var cantidad = Cantidad.Unidades(existencia);
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(e.Sucursal, variante, TipoMovimiento.InventarioInicial, cantidad,
                cantidad.Importe(costo), DateTime.UtcNow, documentoTipo: OrigenDelMovimiento.ImportacionEleventa));
        }
        return id;
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

    private static Task<HttpResponseMessage> GuardarAsync(Escenario e, SesionDto quien, Guid id, string? destino, params (Guid Variante, long Milesimas)[] renglones) =>
        e.Http.PostAsJsonAsync("api/traspasos", new SolicitudGuardarTraspaso(id, quien.UsuarioId, e.Sucursal, destino, null,
            renglones.Select(r => new RenglonPorEnviar(r.Variante, r.Milesimas)).ToList(), e.Caja.Id));

    private static Task<HttpResponseMessage> ExportarAsync(Escenario e, SesionDto quien, Guid id) =>
        e.Http.PostAsJsonAsync($"api/traspasos/{id}/exportar", new SolicitudDeTraspaso(quien.UsuarioId, e.Sucursal, e.Caja.Id));

    private static async Task<byte[]> ArchivoAsync(Escenario e, SesionDto quien, Guid id) => Convert.FromBase64String(
        (await Leer<ArchivoDeTraspasoDto>(await e.Http.GetAsync($"api/sucursales/{e.Sucursal}/traspasos/{id}/archivo?usuarioId={quien.UsuarioId}"))).ContenidoBase64);

    private static Task<HttpResponseMessage> ImportarAsync(Escenario e, SesionDto quien, byte[] archivo) =>
        e.Http.PostAsJsonAsync("api/traspasos/importar",
            new SolicitudImportarTraspaso(quien.UsuarioId, e.Sucursal, "prueba.traspaso.json", Convert.ToBase64String(archivo), e.Caja.Id));

    private static Task<HttpResponseMessage> RecibirAsync(Escenario e, SesionDto quien, Guid id, PasoDeRecepcion paso, params RenglonRecibido[] renglones) =>
        e.Http.PostAsJsonAsync($"api/traspasos/{id}/recibir", new SolicitudRecepcion(quien.UsuarioId, e.Sucursal, renglones, paso, e.Caja.Id));

    /// <summary>Un traspaso exportado en la matriz con esos productos; regresa su Id y su archivo.</summary>
    private static async Task<(Guid Id, byte[] Archivo, TraspasoDto Exportado)> ExportadoAsync(Escenario e, params (Guid Variante, long Milesimas)[] renglones)
    {
        var id = Ids.Nuevo();
        await Leer<TraspasoDto>(await GuardarAsync(e, e.Cajero, id, "Sucursal 2", renglones));
        var exportado = await Leer<TraspasoDto>(await ExportarAsync(e, e.Cajero, id));
        return (id, await ArchivoAsync(e, e.Cajero, id), exportado);
    }

    [Fact]
    public async Task De_una_sucursal_a_otra_sale_al_exportar_y_entra_al_recibir_con_su_kardex_y_su_bitacora()
    {
        await using var sucursal2 = await ServicioDePrueba.CrearAsync();
        var (a, b) = (await PrepararAsync(matriz), await PrepararAsync(sucursal2));
        var codigo = CodigoNuevo();
        var enA = await ProductoAsync(a, "Cuaderno del traspaso", codigo, 20, 1000);
        var enB = await ProductoAsync(b, "Cuaderno (como se llama en la 2)", codigo, 3, 800);

        // El borrador no mueve nada; lo repetido se junta, y se guarda completo cada vez.
        var id = Ids.Nuevo();
        var borrador = await Leer<TraspasoDto>(await GuardarAsync(a, a.Cajero, id, "Sucursal 2", (enA, 5_000), (enA, 7_000)));
        Assert.Equal((EstadoTraspaso.Borrador, SentidoTraspaso.Salida, 12m, 20m, (string?)null),
            (borrador.Estado, borrador.Sentido, borrador.Renglones.Single().Enviada, borrador.Renglones.Single().Existencia!.Value, borrador.Clave));
        Assert.Equal((20m, 20_000L), await ExistenciaAsync(a, enA));

        // Al exportar sale la mercancía a su costo promedio, y otra vez (se perdió la respuesta) no sale dos veces.
        var exportado = await Leer<TraspasoDto>(await ExportarAsync(a, a.Cajero, id));
        Assert.Equal((EstadoTraspaso.Enviado, true, "Cajero de prueba"), (exportado.Estado, exportado.MovioExistencias, exportado.Cerro));
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}$", exportado.Clave!);
        Assert.Equal(exportado.Clave, (await Leer<TraspasoDto>(await ExportarAsync(a, a.Cajero, id))).Clave);
        Assert.Equal((8m, 8_000L), await ExistenciaAsync(a, enA));
        await using (var db = matriz.Base())
        {
            var salida = await db.MovimientosInventario.AsNoTracking().SingleAsync(m => m.DocumentoId == id);
            Assert.Equal((TipoMovimiento.TraspasoSalida, -12_000L, -12_000L, nameof(Traspaso)), (salida.Tipo, salida.Cantidad.Milesimas, salida.CostoCentavos, salida.DocumentoTipo));
            var bitacora = await db.Bitacora.AsNoTracking().Where(x => x.Accion == AccionControlada.Traspaso && x.Detalle.Contains(exportado.Clave!)).SingleAsync();
            Assert.Equal(($"Exportó el traspaso {exportado.Clave} a «Sucursal 2»: 1 producto", (long?)12_000, a.Cajero.UsuarioId), (bitacora.Detalle, bitacora.MontoCentavos, bitacora.UsuarioId!.Value));
        }
        // Ya exportado no se cambia ni se descarta.
        Assert.Contains("ya se exportó", await RechazoAsync(await GuardarAsync(a, a.Cajero, id, "Sucursal 2", (enA, 1_000)), HttpStatusCode.BadRequest));
        Assert.Contains("Solo se descarta un borrador",
            await RechazoAsync(await a.Http.PostAsJsonAsync($"api/traspasos/{id}/descartar", new SolicitudDeTraspaso(a.Cajero.UsuarioId, a.Sucursal)), HttpStatusCode.BadRequest));

        // El archivo: se lee a simple vista, sin costos, y es el mismo cada vez.
        var archivo = await ArchivoAsync(a, a.Cajero, id);
        var texto = Encoding.UTF8.GetString(archivo);
        Assert.Contains("\"formato\": \"papeleria-pos.traspaso\"", texto);
        Assert.Contains("Cuaderno del traspaso", texto);
        Assert.DoesNotContain("costo", texto, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(archivo, await ArchivoAsync(a, a.Cajero, id));

        // En la sucursal de donde salió no se importa.
        Assert.Contains("salió de esta sucursal", await RechazoAsync(await ImportarAsync(a, a.Cajero, archivo), HttpStatusCode.BadRequest));

        // En la que recibe: se empata por código, con los dos nombres, y todavía no entra nada. Otra vez, el que ya estaba.
        var importado = await Leer<TraspasoDto>(await ImportarAsync(b, b.Cajero, archivo));
        var renglon = importado.Renglones.Single();
        Assert.Equal((id, EstadoTraspaso.PorRecibir, SentidoTraspaso.Entrada, exportado.Clave, false, false),
            (importado.Id, importado.Estado, importado.Sentido, importado.Clave, importado.YaEstaba, importado.PuedeModificar));
        Assert.Equal((enB, "Cuaderno (como se llama en la 2)", "Cuaderno del traspaso", 12m, 12m, false, 3m),
            ((Guid)renglon.VarianteId!, renglon.Producto, renglon.Nombre, renglon.Enviada, renglon.Recibida, renglon.Cambio, renglon.Existencia!.Value));
        Assert.True((await Leer<TraspasoDto>(await ImportarAsync(b, b.Encargado, archivo))).YaEstaba);
        Assert.Equal((3m, 2_400L), await ExistenciaAsync(b, enB));

        // Sin cambios no hay nada que mandar a revisión.
        Assert.Contains("No marcaste ningún cambio", await RechazoAsync(await RecibirAsync(b, b.Cajero, id, PasoDeRecepcion.MandarARevision), HttpStatusCode.BadRequest));

        // Como dice el archivo lo recibe el cajero: entra al costo de aquí ($8.00), y otra vez no entra dos veces.
        var recibido = await Leer<TraspasoDto>(await RecibirAsync(b, b.Cajero, id, PasoDeRecepcion.Recibir));
        Assert.Equal((EstadoTraspaso.Recibido, true, "Cajero de prueba"), (recibido.Estado, recibido.MovioExistencias, recibido.Cerro));
        await Leer<TraspasoDto>(await RecibirAsync(b, b.Cajero, id, PasoDeRecepcion.Recibir));
        Assert.Equal((15m, 12_000L), await ExistenciaAsync(b, enB));
        Assert.Contains("ya se recibió", await RechazoAsync(await RecibirAsync(b, b.Encargado, id, PasoDeRecepcion.Guardar), HttpStatusCode.BadRequest));
        await using (var db = sucursal2.Base())
        {
            var entrada = await db.MovimientosInventario.AsNoTracking().SingleAsync(m => m.DocumentoId == id);
            Assert.Equal((TipoMovimiento.TraspasoEntrada, 12_000L, 9_600L), (entrada.Tipo, entrada.Cantidad.Milesimas, entrada.CostoCentavos));
            var bitacora = await db.Bitacora.AsNoTracking().Where(x => x.Detalle.Contains(exportado.Clave!)).OrderBy(x => x.FechaUtc).ThenBy(x => x.Id)
                .Select(x => new { x.Accion, x.Detalle }).ToListAsync();
            Assert.Equal(2, bitacora.Count);
            Assert.StartsWith($"Importó el traspaso {exportado.Clave} de «", bitacora[0].Detalle);
            Assert.Contains("(lo exportó Cajero de prueba; archivo «prueba.traspaso.json»): 1 producto", bitacora[0].Detalle);
            Assert.Equal(AccionControlada.Traspaso, bitacora[1].Accion);
            Assert.EndsWith(": 1 producto, como venía", bitacora[1].Detalle);
        }

        // En las dos listas, y en el kardex de cada lado con a dónde fue o de dónde vino.
        var deA = await Leer<TraspasosDto>(await a.Http.GetAsync($"api/sucursales/{a.Sucursal}/traspasos?usuarioId={a.Cajero.UsuarioId}"));
        Assert.Contains(deA.Traspasos, t => t.Id == id && t.Estado == EstadoTraspaso.Enviado && t.OtraSucursal == "Sucursal 2");
        var deB = await Leer<TraspasosDto>(await b.Http.GetAsync($"api/sucursales/{b.Sucursal}/traspasos?usuarioId={b.Encargado.UsuarioId}"));
        Assert.True(deB.PuedeModificar);
        Assert.Contains(deB.Traspasos, t => t.Id == id && t.Estado == EstadoTraspaso.Recibido && t.Cambios == 0);
    }

    [Fact]
    public async Task Quien_no_puede_modificar_marca_los_cambios_para_revision_y_quien_puede_lo_recibe_cambiado()
    {
        await using var sucursal2 = await ServicioDePrueba.CrearAsync();
        var (a, b) = (await PrepararAsync(matriz), await PrepararAsync(sucursal2));
        var (lapiz, goma, soloEnA, extra) = (CodigoNuevo(), CodigoNuevo(), CodigoNuevo(), CodigoNuevo());
        var (lapizA, gomaA, soloA) = (await ProductoAsync(a, "Lápiz del traspaso", lapiz, 50, 300), await ProductoAsync(a, "Goma del traspaso", goma, 50, 200),
            await ProductoAsync(a, "Solo en la matriz", soloEnA, 50, 100));
        var (lapizB, gomaB, extraB) = (await ProductoAsync(b, "Lápiz del traspaso", lapiz, 0, 0), await ProductoAsync(b, "Goma del traspaso", goma, 0, 0),
            await ProductoAsync(b, "Llegó y no venía", extra, 0, 0));
        var (id, archivo, _) = await ExportadoAsync(a, (lapizA, 10_000), (gomaA, 6_000), (soloA, 4_000));

        // Lo que no está en el catálogo de aquí queda sin empatar.
        var importado = await Leer<TraspasoDto>(await ImportarAsync(b, b.Cajero, archivo));
        var (rLapiz, rGoma, rSolo) = (importado.Renglones[0], importado.Renglones[1], importado.Renglones[2]);
        Assert.Equal((lapizB, gomaB, null, "Solo en la matriz"), (rLapiz.VarianteId, rGoma.VarianteId, rSolo.VarianteId, rSolo.Nombre));

        // El cajero: llegaron 8 lápices, las gomas completas, lo otro no llegó, y algo que no venía. Recibirlo así no puede.
        RenglonRecibido[] marcado =
        [
            new(rLapiz.Id, lapizB, 8_000, "venían 2 de menos"), new(rGoma.Id, gomaB, 6_000, null), new(rSolo.Id, null, 0, null), new(null, extraB, 2_000, null),
        ];
        var rechazo = await RechazoAsync(await RecibirAsync(b, b.Cajero, id, PasoDeRecepcion.Recibir, marcado), HttpStatusCode.Forbidden);
        Assert.Contains("déjalo marcado para revisión", rechazo);
        Assert.Contains("Encargado de prueba", rechazo);
        Assert.Equal((0m, 0L), await ExistenciaAsync(b, lapizB));
        // El rechazo no dejó nada guardado: sigue como llegó.
        var sigue = await Leer<TraspasoDto>(await b.Http.GetAsync($"api/sucursales/{b.Sucursal}/traspasos/{id}?usuarioId={b.Cajero.UsuarioId}"));
        Assert.Equal((EstadoTraspaso.PorRecibir, 3, 10m), (sigue.Estado, sigue.Renglones.Count, sigue.Renglones[0].Recibida));

        // Con los cambios marcados queda «en revisión» con quién lo marcó, y sigue sin entrar nada.
        var enRevision = await Leer<TraspasoDto>(await RecibirAsync(b, b.Cajero, id, PasoDeRecepcion.MandarARevision, marcado));
        Assert.Equal((EstadoTraspaso.EnRevision, "Cajero de prueba", 4), (enRevision.Estado, enRevision.Marco, enRevision.Renglones.Count));
        Assert.Equal([true, false, true, true], enRevision.Renglones.Select(x => x.Cambio));
        Assert.True(enRevision.Renglones[3].Agregado);
        Assert.Equal((0m, 0L), await ExistenciaAsync(b, lapizB));

        // El encargado lo revisa: deja 9 lápices (no 8) y lo recibe así. Entra lo que él dijo.
        var agregado = enRevision.Renglones[3];
        var recibido = await Leer<TraspasoDto>(await RecibirAsync(b, b.Encargado, id, PasoDeRecepcion.Recibir,
            new RenglonRecibido(rLapiz.Id, lapizB, 9_000, "venía 1 de menos"), new RenglonRecibido(agregado.Id, extraB, 2_000, null)));
        Assert.Equal((EstadoTraspaso.Recibido, "Encargado de prueba", "Cajero de prueba"), (recibido.Estado, recibido.Cerro, recibido.Marco));
        Assert.Equal((9m, 6m, 2m), ((await ExistenciaAsync(b, lapizB)).Cantidad, (await ExistenciaAsync(b, gomaB)).Cantidad, (await ExistenciaAsync(b, extraB)).Cantidad));
        await using var db = sucursal2.Base();
        Assert.Equal(3, await db.MovimientosInventario.CountAsync(m => m.DocumentoId == id && m.Tipo == TipoMovimiento.TraspasoEntrada));
        var bitacora = await db.Bitacora.AsNoTracking().Where(x => x.Detalle.Contains(recibido.Clave!)).OrderBy(x => x.FechaUtc).ThenBy(x => x.Id)
            .Select(x => new { x.Accion, x.Detalle, x.UsuarioId }).ToListAsync();
        Assert.Equal([AccionControlada.Traspaso, AccionControlada.Traspaso, AccionControlada.ModificarTraspaso], bitacora.Select(x => x.Accion));
        Assert.Contains("Marcó para revisión", bitacora[1].Detalle);
        Assert.Contains("«Lápiz del traspaso» 10 → 8 [venían 2 de menos]", bitacora[1].Detalle);
        Assert.Contains("«Solo en la matriz» 4 → 0", bitacora[1].Detalle);
        Assert.Contains("+2 «Llegó y no venía» (no venía)", bitacora[1].Detalle);
        Assert.Contains("con cambios. «Lápiz del traspaso» 10 → 9 [venía 1 de menos]", bitacora[2].Detalle);
        Assert.Equal(b.Encargado.UsuarioId, bitacora[2].UsuarioId);
    }

    [Fact]
    public async Task Lo_que_no_esta_en_el_catalogo_se_empata_a_mano_o_no_se_recibe()
    {
        await using var sucursal2 = await ServicioDePrueba.CrearAsync();
        var (a, b) = (await PrepararAsync(matriz), await PrepararAsync(sucursal2));
        var enA = await ProductoAsync(a, "Con otro código allá", CodigoNuevo(), 10, 500);
        var enB = await ProductoAsync(b, "El mismo, con su código de aquí", CodigoNuevo(), 0, 0);
        var (id, archivo, _) = await ExportadoAsync(a, (enA, 3_000));
        var renglon = (await Leer<TraspasoDto>(await ImportarAsync(b, b.Encargado, archivo))).Renglones.Single();
        Assert.Null(renglon.VarianteId);

        // Sin empatar y con lo que llegó en más de cero no se recibe, ni quien puede modificar.
        Assert.Contains("no está en el catálogo de esta sucursal", await RechazoAsync(await RecibirAsync(b, b.Encargado, id, PasoDeRecepcion.Recibir), HttpStatusCode.BadRequest));
        // El cajero no lo empata: lo manda a revisión.
        Assert.Contains("déjalo marcado para revisión", await RechazoAsync(await RecibirAsync(b, b.Cajero, id, PasoDeRecepcion.Recibir), HttpStatusCode.Forbidden));
        // El encargado lo empata con el de aquí y entra.
        var recibido = await Leer<TraspasoDto>(await RecibirAsync(b, b.Encargado, id, PasoDeRecepcion.Recibir, new RenglonRecibido(renglon.Id, enB, 3_000, null)));
        Assert.Equal((EstadoTraspaso.Recibido, true, "El mismo, con su código de aquí"), (recibido.Estado, recibido.Renglones.Single().Cambio, recibido.Renglones.Single().Producto));
        Assert.Equal(3m, (await ExistenciaAsync(b, enB)).Cantidad);
    }

    [Fact]
    public async Task Un_archivo_alterado_o_que_no_es_un_traspaso_no_se_importa()
    {
        await using var sucursal2 = await ServicioDePrueba.CrearAsync();
        var (a, b) = (await PrepararAsync(matriz), await PrepararAsync(sucursal2));
        var codigo = CodigoNuevo();
        var enA = await ProductoAsync(a, "Para alterar", codigo, 10, 500);
        await ProductoAsync(b, "Para alterar", codigo, 0, 0);
        var (id, archivo, _) = await ExportadoAsync(a, (enA, 3_000));

        var alterado = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(archivo).Replace("\"milesimas\": 3000", "\"milesimas\": 30000"));
        Assert.NotEqual(archivo, alterado);
        Assert.Equal(ArchivoDeTraspaso.Alterado, await RechazoAsync(await ImportarAsync(b, b.Encargado, alterado), HttpStatusCode.BadRequest));
        Assert.Equal(ArchivoDeTraspaso.NoEs, await RechazoAsync(await ImportarAsync(b, b.Encargado, "{\"hola\": 1}"u8.ToArray()), HttpStatusCode.BadRequest));
        Assert.Equal(ArchivoDeTraspaso.NoEs, await RechazoAsync(await ImportarAsync(b, b.Encargado, "esto no es nada"u8.ToArray()), HttpStatusCode.BadRequest));
        await using (var db = sucursal2.Base())
            Assert.False(await db.Traspasos.AnyAsync(t => t.Id == id));
        // El bueno sí entra, aunque el Bloc de notas le haya puesto su marca al principio.
        await Leer<TraspasoDto>(await ImportarAsync(b, b.Encargado, [.. Encoding.UTF8.Preamble, .. archivo]));
    }

    [Fact]
    public async Task Sin_el_permiso_no_se_tramita_ni_se_recibe_y_el_borrador_se_revisa_y_se_descarta()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var e = await PrepararAsync(propio);
        var variante = await ProductoAsync(e, "Del borrador", CodigoNuevo(), 5, 500);
        var id = Ids.Nuevo();

        // Lo que no se manda: cero, fracciones de una pieza, un producto que no existe, y exportar sin destino o sin lista.
        Assert.Contains("más de cero", await RechazoAsync(await GuardarAsync(e, e.Cajero, id, "Sucursal 2", (variante, 0)), HttpStatusCode.BadRequest));
        Assert.Contains("piezas enteras", await RechazoAsync(await GuardarAsync(e, e.Cajero, id, "Sucursal 2", (variante, 1_500)), HttpStatusCode.BadRequest));
        Assert.Contains("ya no existe", await RechazoAsync(await GuardarAsync(e, e.Cajero, id, "Sucursal 2", (Guid.NewGuid(), 1_000)), HttpStatusCode.BadRequest));
        await Leer<TraspasoDto>(await GuardarAsync(e, e.Cajero, id, null, (variante, 2_000)));
        Assert.Contains("a qué sucursal va", await RechazoAsync(await ExportarAsync(e, e.Cajero, id), HttpStatusCode.BadRequest));
        await Leer<TraspasoDto>(await GuardarAsync(e, e.Cajero, id, "Sucursal 2"));
        Assert.Contains("La lista está vacía", await RechazoAsync(await ExportarAsync(e, e.Cajero, id), HttpStatusCode.BadRequest));

        // Descartado ya no sale en la lista ni se exporta; no salió nada.
        Assert.Equal(HttpStatusCode.NoContent,
            (await e.Http.PostAsJsonAsync($"api/traspasos/{id}/descartar", new SolicitudDeTraspaso(e.Cajero.UsuarioId, e.Sucursal))).StatusCode);
        Assert.DoesNotContain((await Leer<TraspasosDto>(await e.Http.GetAsync($"api/sucursales/{e.Sucursal}/traspasos?usuarioId={e.Cajero.UsuarioId}"))).Traspasos, t => t.Id == id);
        Assert.Contains("se descartó", await RechazoAsync(await ExportarAsync(e, e.Cajero, id), HttpStatusCode.BadRequest));
        Assert.Equal(5m, (await ExistenciaAsync(e, variante)).Cantidad);

        // Sin «Traspasos entre sucursales»: nada, y el error dice quién lo tiene.
        await NivelAsync(e, e.Cajero, AccionControlada.Traspaso, NivelPermiso.NoPermitido);
        var sinPermiso = await RechazoAsync(await GuardarAsync(e, e.Cajero, Ids.Nuevo(), "Sucursal 2", (variante, 1_000)), HttpStatusCode.Forbidden);
        Assert.Contains("«Traspasos entre sucursales»", sinPermiso);
        Assert.Contains("Encargado de prueba", sinPermiso);
        await RechazoAsync(await e.Http.GetAsync($"api/sucursales/{e.Sucursal}/traspasos?usuarioId={e.Cajero.UsuarioId}"), HttpStatusCode.Forbidden);
        await RechazoAsync(await ImportarAsync(e, e.Cajero, "{}"u8.ToArray()), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task En_el_piloto_con_eleventa_el_traspaso_es_solo_el_documento_y_no_mueve_existencias()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        await using var sucursal2 = await ServicioDePrueba.CrearAsync();
        using var pilotoA = propio.Fabrica.WithWebHostBuilder(x => x.UseSetting("Eleventa:Activo", "true"));
        using var pilotoB = sucursal2.Fabrica.WithWebHostBuilder(x => x.UseSetting("Eleventa:Activo", "true"));
        var (a, b) = (await PrepararAsync(propio, pilotoA.CreateClient()), await PrepararAsync(sucursal2, pilotoB.CreateClient()));
        var codigo = CodigoNuevo();
        // El producto se da de alta antes del piloto (en el piloto, las altas son en eleventa).
        var (enA, enB) = (await ProductoAsync(await PrepararAsync(propio), "Del piloto", codigo, 10, 500), await ProductoAsync(await PrepararAsync(sucursal2), "Del piloto", codigo, 1, 500));

        var (id, archivo, exportado) = await ExportadoAsync(a, (enA, 4_000));
        Assert.Equal((EstadoTraspaso.Enviado, false, RegistroTraspasos.EnElPiloto), (exportado.Estado, exportado.MovioExistencias, exportado.Piloto));
        await Leer<TraspasoDto>(await ImportarAsync(b, b.Cajero, archivo));
        var recibido = await Leer<TraspasoDto>(await RecibirAsync(b, b.Cajero, id, PasoDeRecepcion.Recibir));
        Assert.Equal((EstadoTraspaso.Recibido, false), (recibido.Estado, recibido.MovioExistencias));
        Assert.Equal((10m, 1m), ((await ExistenciaAsync(a, enA)).Cantidad, (await ExistenciaAsync(b, enB)).Cantidad));
        await using var db = propio.Base();
        Assert.False(await db.MovimientosInventario.AnyAsync(m => m.DocumentoId == id));
        Assert.EndsWith("(piloto con eleventa: sin mover existencias)", (await db.Bitacora.AsNoTracking().SingleAsync(x => x.Detalle.Contains(exportado.Clave!))).Detalle);
    }
}
