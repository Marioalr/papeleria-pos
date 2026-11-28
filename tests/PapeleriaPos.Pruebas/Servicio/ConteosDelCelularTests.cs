using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Mandar un conteo del celular a una caja (docs/celular-como-caja.md, «Mandar un conteo a una caja»; dueño, 2026-11-22): el celular
/// manda la lista con lo que hay de cada producto; solo llega a una caja con un turno abierto de quien tiene «Conteo completo de un
/// departamento», y ahí se acepta (tal cual o con cambios) o se rechaza. Al aceptarla, lo contado reemplaza la existencia de cuando
/// se contó y lo vendido después sigue contando; lo más nuevo gana. En el piloto con eleventa (2026-11-27) también llega, pero
/// aceptarla no cambia existencias ni le escribe a eleventa: se compara contra lo que dice eleventa en ese momento, queda la lista de
/// diferencias «por capturar en eleventa» (con su Excel) y se cierra con «Ya lo capturé en eleventa».
/// </summary>
public sealed class ConteosDelCelularTests
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ErrorDto> Error(HttpResponseMessage r, HttpStatusCode estado)
    {
        Assert.True(estado == r.StatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!;
    }

    /// <summary>Un producto: la pieza con su código y, si se pide, su caja de 12 con el suyo.</summary>
    private sealed record Producto(Guid VarianteId, Guid Pieza, Guid? Caja, string Codigo, string? CodigoCaja, string Nombre);

    /// <summary>
    /// La tienda: la caja principal, la Caja 2 con un turno abierto del encargado (que puede aceptar conteos) y su llave, y el celular
    /// dado de alta con la suya.
    /// </summary>
    private sealed record Tienda(ServicioDePrueba Servicio, HttpClient Principal, CajaDto Caja1, HttpClient Caja2, CajaDto Caja2Dto, TurnoDto Turno2,
        HttpClient Celular, Guid CelularId, Guid SucursalId, SesionDto Encargado, SesionDto Cajero) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Servicio.DisposeAsync();

        public string Ruta(string que) => $"api/cajas/{CelularId}/{que}";

        public string DeLaCaja2(string que) => $"api/cajas/{Caja2Dto.Id}/{que}";
    }

    /// <summary>La misma tienda en el piloto con eleventa (su lectura prendida), con una lectura de mentiras en lugar de eleventa.</summary>
    private static Task<Tienda> PilotoAsync(LecturaDePrueba lectura) =>
        TiendaAsync(lectura, ("Eleventa:Activo", "true"));

    private static Task<Tienda> TiendaAsync(params (string, string)[] ajustes) => TiendaAsync(null, ajustes);

    private static async Task<Tienda> TiendaAsync(LecturaDePrueba? lectura, params (string, string)[] ajustes)
    {
        var servicio = lectura is null
            ? await ServicioDePrueba.CrearAsync(ajustes)
            : await ServicioDePrueba.CrearAsync(s => s.AddSingleton<PapeleriaPos.Servicio.ILecturaFresca>(lectura), ajustes);
        var principal = servicio.Fabrica.CreateClient();
        var caja1 = (await principal.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await principal.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var cajero = await Leer<SesionDto>(await principal.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var alta2 = await Leer<AltaCajaDto>(await principal.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja1.SucursalId, null, "Caja 2", "B", "PC-2")));
        var caja2 = servicio.Fabrica.CreateClient();
        caja2.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta2.Llave);
        var turno = await Leer<TurnoDto>(await caja2.PostAsJsonAsync("api/turnos", new SolicitudTurno(alta2.Caja.Id, encargado.UsuarioId, 0)));

        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja1.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        return new Tienda(servicio, principal, caja1, caja2, alta2.Caja, turno, celular, alta.Caja.Id, caja1.SucursalId, encargado, cajero);
    }

    /// <summary>
    /// Un producto nuevo por pieza (con su caja de 12, si se pide) y <paramref name="existencia"/> piezas de inventario inicial de hace
    /// una hora.
    /// </summary>
    private static async Task<Producto> ProductoAsync(Tienda t, string nombre, int existencia, bool conCaja = false, TipoProducto tipo = TipoProducto.Mercancia)
    {
        var departamento = (await t.Principal.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        var codigo = $"CC{Guid.NewGuid():N}"[..14];
        var codigoCaja = conCaja ? $"CJ{Guid.NewGuid():N}"[..14] : null;
        List<PresentacionFichaDto> presentaciones = [new(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])];
        if (codigoCaja is not null)
            presentaciones.Add(new PresentacionFichaDto(null, "Caja 12", 12_000, false, [new CodigoFichaDto(codigoCaja)]));
        var ficha = await Leer<FichaProductoDto>(await t.Principal.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(t.Encargado.UsuarioId,
            t.SucursalId, new FichaProductoDto(null, nombre, departamento, null, tipo, Impuesto.Iva16, null, true, false,
            [new VarianteFichaDto(null, "", UnidadBase.Pieza, true, presentaciones, [new PrecioFichaDto(null, 1000, 2000)])]))));
        var variante = ficha.Variantes[0];
        if (existencia > 0)
            await MoverAsync(t, variante.Id!.Value, TipoMovimiento.InventarioInicial, existencia, DateTime.UtcNow.AddHours(-1));
        await t.Servicio.CatalogoAlDiaAsync();
        return new Producto(variante.Id!.Value, variante.Presentaciones.Single(p => p.EsBase).Id!.Value,
            variante.Presentaciones.SingleOrDefault(p => !p.EsBase)?.Id, codigo, codigoCaja, nombre);
    }

    /// <summary>Un movimiento del kardex a cierta hora (lo vendido antes de contar, un ajuste después).</summary>
    private static async Task MoverAsync(Tienda t, Guid varianteId, TipoMovimiento tipo, int piezas, DateTime fechaUtc, string? documento = null)
    {
        await using var db = t.Servicio.Base();
        var variante = await db.Variantes.SingleAsync(v => v.Id == varianteId);
        var inventario = new RegistroInventario(db);
        await inventario.RegistrarAsync(piezas > 0
            ? Kardex.Entrada(t.SucursalId, variante, tipo, Cantidad.Piezas(piezas), piezas * 1000L, fechaUtc, documentoTipo: documento ?? OrigenDelMovimiento.Ficticios)
            : Kardex.Salida(t.SucursalId, variante, tipo, Cantidad.Piezas(-piezas), await inventario.ExistenciaAsync(t.SucursalId, varianteId), 1000, fechaUtc,
                motivo: tipo == TipoMovimiento.Ajuste ? "Prueba" : null, documentoTipo: documento ?? OrigenDelMovimiento.Ficticios, documentoId: Guid.NewGuid()));
    }

    /// <summary>El producto, como los que llegan con la lectura: con su ID de eleventa.</summary>
    private static async Task<Producto> DeEleventaAsync(Tienda t, Producto p)
    {
        await using var db = t.Servicio.Base();
        db.MapeosImportacion.Add(new MapeoImportacion
        {
            Origen = "eleventa", Entidad = nameof(Variante), IdOrigen = Random.Shared.Next(100_000, 999_999).ToString(), IdNuevo = p.VarianteId,
        });
        await db.SaveChangesAsync();
        return p;
    }

    /// <summary>Lo que eleventa vendió (−) o recibió (+), como lo anota su lectura en el kardex: un ajuste de tipo conteo a su nombre.</summary>
    private static Task EleventaMovioAsync(Tienda t, Producto p, int piezas, DateTime? fechaUtc = null) =>
        MoverAsync(t, p.VarianteId, TipoMovimiento.Conteo, piezas, fechaUtc ?? DateTime.UtcNow, OrigenDelMovimiento.LecturaEleventa);

    private static async Task<List<ConteoPorCapturarDto>> PorCapturarAsync(Tienda t) =>
        await Leer<List<ConteoPorCapturarDto>>(await t.Caja2.GetAsync(t.DeLaCaja2("conteos-por-capturar")));

    private static Task<HttpResponseMessage> CapturadoAsync(Tienda t, Guid conteo, Guid usuario) =>
        t.Caja2.PostAsJsonAsync(t.DeLaCaja2($"conteos-por-capturar/{conteo}/capturado"), new SolicitudConteoCapturado(usuario));

    /// <summary>Vende en la Caja 2 ahora (después de contar).</summary>
    private static async Task VenderAsync(Tienda t, Producto p, int piezas) =>
        await Leer<TicketDto>(await t.Caja2.PostAsJsonAsync("api/ventas", new SolicitudCobro(t.Caja2Dto.Id, t.Turno2.Id, t.Encargado.UsuarioId,
            [new LineaSolicitada(p.VarianteId, p.Pieza, piezas * 1000L)], [new CobroSolicitado(FormaPago.Efectivo, 100_000)])));

    private static async Task<decimal> HayAsync(Tienda t, Guid varianteId)
    {
        await using var db = t.Servicio.Base();
        return (await db.Existencias.AsNoTracking().SingleOrDefaultAsync(e => e.SucursalId == t.SucursalId && e.VarianteId == varianteId))?.Cantidad.EnUnidades ?? 0;
    }

    private static Task<HttpResponseMessage> MandarAsync(Tienda t, SolicitudMandarConteo s, HttpClient? quien = null) =>
        (quien ?? t.Celular).PostAsJsonAsync(t.Ruta("mandar-conteo"), s);

    private static SolicitudMandarConteo Lista(Tienda t, DateTime? contado, params RenglonMandado[] renglones) =>
        new(Guid.NewGuid(), t.Caja2Dto.Id, contado, null, renglones);

    private static RenglonMandado Piezas(Producto p, int piezas) => new(p.VarianteId, p.Pieza, null, piezas * 1000L);

    private static Task<HttpResponseMessage> AceptarAsync(Tienda t, Guid conteo, Guid usuario, params RenglonAceptado[] renglones) =>
        t.Caja2.PostAsJsonAsync(t.DeLaCaja2($"conteos-recibidos/{conteo}/aceptar"), new SolicitudAceptarConteo(usuario, renglones));

    private static RenglonAceptado Queda(Producto p, int piezas) => new(p.VarianteId, piezas * 1000L);

    private static async Task<List<Bitacora>> BitacoraAsync(Tienda t)
    {
        await using var db = t.Servicio.Base();
        return await db.Bitacora.AsNoTracking().OrderBy(b => b.FechaUtc).ToListAsync();
    }

    [Fact]
    public async Task Dice_que_cajas_reciben_y_por_que_las_demas_no()
    {
        await using var t = await TiendaAsync();
        // La caja principal con un turno del cajero (que no puede aceptar conteos); la Caja 2, con el del encargado.
        await Leer<TurnoDto>(await t.Principal.PostAsJsonAsync("api/turnos", new SolicitudTurno(t.Caja1.Id, t.Cajero.UsuarioId, 0)));

        var cajas = await Leer<List<CajaParaContarDto>>(await t.Celular.GetAsync(t.Ruta("para-contar")));
        Assert.DoesNotContain(cajas, c => c.Id == t.CelularId);
        var caja2 = Assert.Single(cajas, c => c.Id == t.Caja2Dto.Id);
        Assert.Equal((true, "Encargado de prueba", null, null, 0), (caja2.Recibe, caja2.Atiende, caja2.Codigo, caja2.PorQue, caja2.Pendientes));
        var caja1 = Assert.Single(cajas, c => c.Id == t.Caja1.Id);
        Assert.False(caja1.Recibe);
        Assert.Equal("sin-permiso", caja1.Codigo);
        Assert.Equal($"Cajero de prueba tiene el turno de la {t.Caja1.Nombre} y no puede aceptar conteos (le falta «Conteo completo de un departamento»): "
            + "que abra turno ahí quien puede, o mándala a otra caja.", caja1.PorQue);

        var p = await ProductoAsync(t, "Corrector para ver las cajas", 5);
        var sinPermiso = await Error(await MandarAsync(t, Lista(t, null, Piezas(p, 5)) with { Destino = t.Caja1.Id }), HttpStatusCode.Conflict);
        Assert.Equal(("sin-permiso", caja1.PorQue), (sinPermiso.Codigo, sinPermiso.Mensaje));

        // Sin turno abierto (se cerró), no recibe.
        await using (var db = t.Servicio.Base())
        {
            var turno = await db.Turnos.SingleAsync(x => x.Id == t.Turno2.Id);
            turno.CerradoUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        caja2 = (await Leer<List<CajaParaContarDto>>(await t.Celular.GetAsync(t.Ruta("para-contar")))).Single(c => c.Id == t.Caja2Dto.Id);
        Assert.Equal((false, "sin-turno", "La Caja 2 no tiene un turno abierto: que abran turno ahí y vuelve a mandarla."), (caja2.Recibe, caja2.Codigo, caja2.PorQue));
        var sinTurno = await Error(await MandarAsync(t, Lista(t, null, Piezas(p, 5))), HttpStatusCode.Conflict);
        Assert.Equal(("sin-turno", caja2.PorQue), (sinTurno.Codigo, sinTurno.Mensaje));
    }

    [Fact]
    public async Task La_caja_de_12_se_convierte_los_renglones_se_suman_y_mandarla_otra_vez_no_la_duplica()
    {
        await using var t = await TiendaAsync();
        var lapiz = await ProductoAsync(t, "Lápiz para contar por caja", 30, conCaja: true);
        var goma = await ProductoAsync(t, "Goma para contar", 4);
        var contado = DateTime.UtcNow.AddMinutes(-5);
        // Dos cajas por su variante y presentación, cinco sueltas por su código (con el 0 del EAN-13), la goma por su código.
        var s = new SolicitudMandarConteo(Guid.NewGuid(), t.Caja2Dto.Id, contado, t.Cajero.UsuarioId,
            [new RenglonMandado(lapiz.VarianteId, lapiz.Caja, null, 2000), new RenglonMandado(null, null, lapiz.Codigo, 5000), new RenglonMandado(null, null, goma.Codigo, 3000)]);

        var mandada = await Leer<EstadoConteoDto>(await MandarAsync(t, s));
        Assert.Equal((s.Id, t.Caja2Dto.Id, "Caja 2", "recibida", 2, 0), (mandada.Id, mandada.Destino, mandada.Caja, mandada.Estado, mandada.Productos, mandada.Aplicados));
        Assert.Equal([("Lápiz para contar por caja", 29m), ("Goma para contar", 3m)], mandada.Renglones.Select(r => (r.Descripcion, r.Contado)));
        Assert.All(mandada.Renglones, r => Assert.False(r.Aplicado));
        Assert.Equal(contado, mandada.ContadoUtc, TimeSpan.FromSeconds(1));

        // Otra vez (se perdió la respuesta): lo mismo, sin otra lista. Otro celular con el mismo Id: no.
        Assert.Equal(mandada.Renglones, (await Leer<EstadoConteoDto>(await MandarAsync(t, s))).Renglones);
        await using (var db = t.Servicio.Base())
            Assert.Equal(1, await db.ConteosDelCelular.CountAsync(c => c.Id == s.Id));
        var otro = t.Servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await otro.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", t.SucursalId, null, "Celular de Ana", "CA", "Moto G")));
        otro.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        var deOtro = await Error(await otro.PostAsJsonAsync($"api/cajas/{alta.Caja.Id}/mandar-conteo", s), HttpStatusCode.Conflict);
        Assert.Equal(("otro-celular", "Esa lista es de otro celular."), (deOtro.Codigo, deOtro.Mensaje));
        Assert.Equal(HttpStatusCode.NotFound, (await otro.GetAsync($"api/cajas/{alta.Caja.Id}/conteos/{s.Id}")).StatusCode);

        // El celular pregunta cómo va; la caja la ve, con quién contó y cómo llegó cada producto. Nada cambió todavía.
        Assert.Equal("recibida", (await Leer<EstadoConteoDto>(await t.Celular.GetAsync(t.Ruta($"conteos/{s.Id}")))).Estado);
        Assert.Equal(s.Id, Assert.Single(await Leer<List<EstadoConteoDto>>(await t.Celular.GetAsync(t.Ruta("conteos")))).Id);
        var recibida = Assert.Single(await Leer<List<ConteoRecibidoDto>>(await t.Caja2.GetAsync(t.DeLaCaja2("conteos-recibidos"))));
        Assert.Equal(("Celular de Luis", "Cajero de prueba", 2), (recibida.De, recibida.Conto, recibida.Productos));
        var revisar = await Leer<ConteoPorRevisarDto>(await t.Caja2.GetAsync(t.DeLaCaja2($"conteos-recibidos/{s.Id}")));
        Assert.Equal([("2 Caja 12 + 5 Pieza", 29m, 0m, 30m), ((string?)null, 3m, 0m, 4m)], revisar.Renglones.Select(r => (r.Detalle, r.Contado, r.Desde, r.Hoy)));
        Assert.Equal(lapiz.Codigo, revisar.Renglones[0].Codigo);
        Assert.Equal((30m, 4m), (await HayAsync(t, lapiz.VarianteId), await HayAsync(t, goma.VarianteId)));
        Assert.Equal(1, (await Leer<List<CajaParaContarDto>>(await t.Celular.GetAsync(t.Ruta("para-contar")))).Single(c => c.Id == t.Caja2Dto.Id).Pendientes);
        Assert.Contains(await BitacoraAsync(t), b => b.Accion == AccionControlada.ContarInventario && b.UsuarioId == t.Cajero.UsuarioId
            && b.Detalle.StartsWith("Mandó un conteo de Celular de Luis a la Caja 2: 2 productos (contó Cajero de prueba), contado a las ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lo_que_no_se_puede_mandar_lo_dice_en_espanol_con_su_codigo()
    {
        await using var t = await TiendaAsync();
        var p = await ProductoAsync(t, "Corrector de los errores", 5);
        var copia = await ProductoAsync(t, "Engargolado de prueba", 0, tipo: TipoProducto.Servicio);
        async Task<(string?, string)> Mandar(SolicitudMandarConteo s, HttpStatusCode estado = HttpStatusCode.BadRequest, HttpClient? quien = null)
        {
            var e = await Error(await MandarAsync(t, s, quien), estado);
            return (e.Codigo, e.Mensaje);
        }

        Assert.Equal(("vacio", "La lista está vacía."), await Mandar(Lista(t, null)));
        Assert.Equal(("sin-id", "La lista no trae su número: actualiza la app."), await Mandar(Lista(t, null, Piezas(p, 1)) with { Id = Guid.Empty }));
        Assert.Equal(("cantidad", "Una cantidad de la lista no es válida: va de 0 a 99,999."), await Mandar(Lista(t, null, Piezas(p, -1))));
        Assert.Equal(("cantidad", "«Corrector de los errores» se cuenta en piezas enteras."),
            await Mandar(Lista(t, null, new RenglonMandado(p.VarianteId, null, null, 1500))));
        Assert.Equal(("no-esta", "«7501234567890» ya no está en el catálogo de la caja principal: quítalo de la lista."),
            await Mandar(Lista(t, null, Piezas(p, 1), new RenglonMandado(null, null, "7501234567890", 1000))));
        Assert.Equal(("sin-inventario", "«Engargolado de prueba» no lleva inventario (es un servicio): quítalo de la lista."),
            await Mandar(Lista(t, null, Piezas(copia, 1))));
        Assert.Equal(("otra-caja-destino", "Esa caja ya no está en la tienda: elige otra."), await Mandar(Lista(t, null, Piezas(p, 1)) with { Destino = t.CelularId }));
        Assert.Equal(("contado-viejo", "Esta lista se contó hace más de 7 días: desde entonces se vendió mucho; cuenta otra vez."),
            await Mandar(Lista(t, DateTime.UtcNow.AddDays(-8), Piezas(p, 1))));
        Assert.Equal(("demasiados", "La lista trae 501 renglones; van hasta 500: pártela en dos."),
            await Mandar(Lista(t, null, [.. Enumerable.Repeat(Piezas(p, 1), 501)])));
        Assert.Equal(("sin-llave", "Solo un celular dado de alta manda conteos a las cajas."),
            await Mandar(Lista(t, null, Piezas(p, 1)), HttpStatusCode.Unauthorized, t.Servicio.Fabrica.CreateClient()));
        // Con la llave de otra caja: no es la suya.
        Assert.Equal("otra-caja", (await Error(await t.Caja2.PostAsJsonAsync(t.Ruta("mandar-conteo"), Lista(t, null, Piezas(p, 1))), HttpStatusCode.Forbidden)).Codigo);
        Assert.Equal(("no-esta", "Esa lista no llegó a ninguna caja: mándala otra vez."),
            (await Error(await t.Celular.GetAsync(t.Ruta($"conteos/{Guid.NewGuid()}")), HttpStatusCode.NotFound) is var e ? (e.Codigo, e.Mensaje) : default));
        // Contado «en el futuro» (el reloj del celular adelantado): cuenta desde que llegó.
        var adelantada = await Leer<EstadoConteoDto>(await MandarAsync(t, Lista(t, DateTime.UtcNow.AddHours(3), Piezas(p, 5))));
        Assert.Equal(adelantada.RecibidoUtc, adelantada.ContadoUtc);
        await using var db = t.Servicio.Base();
        Assert.Equal(1, await db.ConteosDelCelular.CountAsync());
    }

    [Fact]
    public async Task Al_aceptarla_lo_vendido_despues_de_contar_sigue_contando_y_lo_que_no_viene_no_se_toca()
    {
        await using var t = await TiendaAsync();
        var p = await ProductoAsync(t, "Cuaderno para aceptar", 20);
        var otro = await ProductoAsync(t, "Cuaderno que no se contó", 9);
        // Se vendieron 2 antes de contar (el anaquel ya no los tiene), se contaron 15 (faltaban 3) y después se vendieron 3 más.
        await MoverAsync(t, p.VarianteId, TipoMovimiento.Venta, -2, DateTime.UtcNow.AddMinutes(-30));
        var s = Lista(t, DateTime.UtcNow.AddMinutes(-10), Piezas(p, 15));
        await Leer<EstadoConteoDto>(await MandarAsync(t, s));
        await VenderAsync(t, p, 3);

        // La caja ve lo que se vendió desde que se contó: quedarán 12; hoy dice 15.
        var revisar = await Leer<ConteoPorRevisarDto>(await t.Caja2.GetAsync(t.DeLaCaja2($"conteos-recibidos/{s.Id}")));
        Assert.Equal((15m, -3m, 15m, null), (revisar.Renglones[0].Contado, revisar.Renglones[0].Desde, revisar.Renglones[0].Hoy, revisar.Renglones[0].Superado));

        var aceptada = await Leer<EstadoConteoDto>(await AceptarAsync(t, s.Id, t.Encargado.UsuarioId, Queda(p, 15)));
        Assert.Equal(("aceptada", "Encargado de prueba", 1), (aceptada.Estado, aceptada.Resolvio, aceptada.Aplicados));
        Assert.Equal((15m, (decimal?)15m, true, (string?)null), (aceptada.Renglones[0].Contado, aceptada.Renglones[0].Aceptado, aceptada.Renglones[0].Aplicado,
            aceptada.Renglones[0].Nota));
        Assert.Equal((12m, 9m), (await HayAsync(t, p.VarianteId), await HayAsync(t, otro.VarianteId)));
        Assert.Equal("aceptada", (await Leer<EstadoConteoDto>(await t.Celular.GetAsync(t.Ruta($"conteos/{s.Id}")))).Estado);
        Assert.Empty(await Leer<List<ConteoRecibidoDto>>(await t.Caja2.GetAsync(t.DeLaCaja2("conteos-recibidos"))));

        // En el kardex: la diferencia como conteo, de esta lista; en la bitácora: quién, con qué celular y lo que cambió.
        await using (var db = t.Servicio.Base())
        {
            var movimiento = await db.MovimientosInventario.AsNoTracking().SingleAsync(m => m.DocumentoId == s.Id);
            Assert.Equal((TipoMovimiento.Conteo, nameof(ConteoDelCelular), p.VarianteId, -3m), (movimiento.Tipo, movimiento.DocumentoTipo, movimiento.VarianteId,
                movimiento.Cantidad.EnUnidades));
            Assert.Empty(await new RegistroInventario(db).VerificarExistenciasAsync());
        }
        var aplico = Assert.Single(await BitacoraAsync(t), b => b.Accion == AccionControlada.ConteoCompleto);
        Assert.Equal((t.Encargado.UsuarioId, t.Caja2Dto.Id, -3000L), (aplico.UsuarioId!.Value, aplico.CajaId!.Value, aplico.MontoCentavos!.Value));
        Assert.Matches(@"^Aplicó el conteo de Celular de Luis, contado a las \d\d:\d\d: 1 producto, 1 con diferencia$", aplico.Detalle);
        // Inventario › Movimientos dice de dónde salió y quién lo aceptó.
        var movimientos = await Leer<MovimientosInventarioDto>(await t.Principal.GetAsync(
            $"api/sucursales/{t.SucursalId}/inventario/movimientos?usuarioId={t.Encargado.UsuarioId}&q={Uri.EscapeDataString(p.Nombre)}"));
        var renglon = Assert.Single(movimientos.Movimientos, r => r.Clase == ClaseMovimiento.Conteo);
        Assert.Equal(("del celular · Celular de Luis", "Encargado de prueba"), (renglon.Documento, renglon.Quien));

        // Otra vez: ya se aplicó.
        var otraVez = await Error(await AceptarAsync(t, s.Id, t.Encargado.UsuarioId, Queda(p, 15)), HttpStatusCode.Conflict);
        Assert.Equal(("ya-resuelta", "Esa lista ya se aplicó."), (otraVez.Codigo, otraVez.Mensaje));
    }

    [Fact]
    public async Task Con_cambios_en_la_caja_y_rechazada_y_sin_permiso()
    {
        await using var t = await TiendaAsync();
        var a = await ProductoAsync(t, "Regla de 30 cm", 10);
        var b = await ProductoAsync(t, "Regla de 15 cm", 10);
        var c = await ProductoAsync(t, "Regla de 20 cm", 10);
        var s = Lista(t, DateTime.UtcNow.AddMinutes(-1), Piezas(a, 8), Piezas(b, 7), Piezas(c, 10));
        await Leer<EstadoConteoDto>(await MandarAsync(t, s));

        // El cajero no puede: queda el intento; nada cambia.
        var sinPermiso = await Error(await AceptarAsync(t, s.Id, t.Cajero.UsuarioId, Queda(a, 8)), HttpStatusCode.Forbidden);
        Assert.Equal("no-permitido", sinPermiso.Codigo);
        Assert.Contains(await BitacoraAsync(t), x => x.UsuarioId == t.Cajero.UsuarioId && x.Accion == AccionControlada.ConteoCompleto
            && x.Resultado == ResultadoBitacora.Negado);
        // Desde otra caja, con su llave: no.
        Assert.Equal("otra-caja", (await Error(await t.Celular.PostAsJsonAsync(t.DeLaCaja2($"conteos-recibidos/{s.Id}/aceptar"),
            new SolicitudAceptarConteo(t.Encargado.UsuarioId, [Queda(a, 8)])), HttpStatusCode.Forbidden)).Codigo);
        Assert.Equal(("vacio", "No queda ningún producto: si no se aplica nada, recházala."),
            (await Error(await AceptarAsync(t, s.Id, t.Encargado.UsuarioId), HttpStatusCode.BadRequest) is var e ? (e.Codigo, e.Mensaje) : default));

        // La regla de 30 se corrige a 9, la de 15 se quita, la de 20 tal cual.
        var aceptada = await Leer<EstadoConteoDto>(await AceptarAsync(t, s.Id, t.Encargado.UsuarioId, Queda(a, 9), Queda(c, 10)));
        Assert.Equal(("aceptada-con-cambios", 2), (aceptada.Estado, aceptada.Aplicados));
        Assert.Equal([("En la caja lo cambiaron a 9.", true), ("Lo quitaron en la caja: no cambió.", false), ((string?)null, true)],
            aceptada.Renglones.Select(r => (r.Nota, r.Aplicado)));
        Assert.Equal((9m, 10m, 10m), (await HayAsync(t, a.VarianteId), await HayAsync(t, b.VarianteId), await HayAsync(t, c.VarianteId)));
        Assert.Contains(await BitacoraAsync(t), x => x.Accion == AccionControlada.ConteoCompleto && x.Resultado == ResultadoBitacora.Hecho
            && x.Detalle.EndsWith(": 2 productos, 1 con diferencia; cambió 1 producto; quitó 1 producto", StringComparison.Ordinal));

        // Otra lista, rechazada con su motivo: nada cambia.
        var r = Lista(t, null, Piezas(a, 1));
        await Leer<EstadoConteoDto>(await MandarAsync(t, r));
        var rechazada = await Leer<EstadoConteoDto>(await t.Caja2.PostAsJsonAsync(t.DeLaCaja2($"conteos-recibidos/{r.Id}/rechazar"),
            new SolicitudRechazarConteo(t.Encargado.UsuarioId, "  Contó solo el anaquel  ")));
        Assert.Equal(("rechazada", "Contó solo el anaquel", 0, "Encargado de prueba"), (rechazada.Estado, rechazada.Motivo, rechazada.Aplicados, rechazada.Resolvio));
        Assert.Equal(9m, await HayAsync(t, a.VarianteId));
        Assert.Contains(await BitacoraAsync(t), x => x.Detalle.StartsWith("Rechazó el conteo de Celular de Luis, contado a las ", StringComparison.Ordinal)
            && x.Detalle.EndsWith(": 1 producto, nada cambió. Motivo: Contó solo el anaquel", StringComparison.Ordinal));
        Assert.Equal("Esa lista ya se rechazó.", (await Error(await AceptarAsync(t, r.Id, t.Encargado.UsuarioId, Queda(a, 1)), HttpStatusCode.Conflict)).Mensaje);
        Assert.Equal("rechazada", (await Leer<EstadoConteoDto>(await t.Celular.GetAsync(t.Ruta($"conteos/{r.Id}")))).Estado);
    }

    [Fact]
    public async Task Con_dos_listas_del_mismo_producto_vale_la_que_se_conto_al_final_en_cualquier_orden()
    {
        await using var t = await TiendaAsync();
        var p = await ProductoAsync(t, "Pegamento en barra", 20);
        var q = await ProductoAsync(t, "Pegamento líquido", 20);
        var ajustado = await ProductoAsync(t, "Pegamento blanco", 20);
        // La vieja: 10 de cada uno hace 20 minutos. Se vende 1. La nueva: 7 hace 10 minutos. Se vende 1 más.
        var vieja = Lista(t, DateTime.UtcNow.AddMinutes(-20), Piezas(p, 10), Piezas(q, 10), Piezas(ajustado, 10));
        await Leer<EstadoConteoDto>(await MandarAsync(t, vieja));
        await MoverAsync(t, p.VarianteId, TipoMovimiento.Venta, -1, DateTime.UtcNow.AddMinutes(-15));
        await MoverAsync(t, q.VarianteId, TipoMovimiento.Venta, -1, DateTime.UtcNow.AddMinutes(-15));
        var nueva = Lista(t, DateTime.UtcNow.AddMinutes(-10), Piezas(p, 7), Piezas(q, 7));
        await Leer<EstadoConteoDto>(await MandarAsync(t, nueva));
        await VenderAsync(t, p, 1);
        await VenderAsync(t, q, 1);
        // Del blanco, un ajuste después de la vieja: vale el ajuste.
        await MoverAsync(t, ajustado.VarianteId, TipoMovimiento.Ajuste, -5, DateTime.UtcNow.AddMinutes(-5), nameof(AjusteInventario));

        // De la barra, primero la nueva y luego la vieja: la vieja ya no cambia nada de ella.
        await Leer<EstadoConteoDto>(await AceptarAsync(t, nueva.Id, t.Encargado.UsuarioId, Queda(p, 7)));
        Assert.Equal(6m, await HayAsync(t, p.VarianteId));
        var revisar = await Leer<ConteoPorRevisarDto>(await t.Caja2.GetAsync(t.DeLaCaja2($"conteos-recibidos/{vieja.Id}")));
        Assert.Matches(@"^Ya se contó después con Celular de Luis, a las \d\d:\d\d: vale ese\.$", revisar.Renglones[0].Superado);
        Assert.Null(revisar.Renglones[1].Superado);
        Assert.Matches(@"^Ya se contó o se ajustó después, a las \d\d:\d\d: vale eso\.$", revisar.Renglones[2].Superado);
        var vieja1 = await Leer<EstadoConteoDto>(await AceptarAsync(t, vieja.Id, t.Encargado.UsuarioId, Queda(p, 10), Queda(q, 10), Queda(ajustado, 10)));
        Assert.Equal("aceptada", vieja1.Estado);
        Assert.Equal([false, true, false], vieja1.Renglones.Select(r => r.Aplicado));
        Assert.Equal("No se aplicó: ya se había contado después.", vieja1.Renglones[0].Nota);
        Assert.Equal(6m, await HayAsync(t, p.VarianteId));
        Assert.Equal(15m, await HayAsync(t, ajustado.VarianteId));
        // Del líquido (en la nueva lo quitaron), la vieja quedó aplicada: 10, menos los 2 vendidos después, 8. Otra lista contada
        // después (hace 10 minutos, 7) y aceptada al final manda: lo de la vieja no cuenta como movimiento, solo lo vendido, 7 − 1 = 6.
        Assert.Equal(8m, await HayAsync(t, q.VarianteId));
        var delLiquido = await Leer<EstadoConteoDto>(await MandarAsync(t, Lista(t, DateTime.UtcNow.AddMinutes(-10), Piezas(q, 7))));
        await Leer<EstadoConteoDto>(await AceptarAsync(t, delLiquido.Id, t.Encargado.UsuarioId, Queda(q, 7)));
        Assert.Equal(6m, await HayAsync(t, q.VarianteId));
        await using var db = t.Servicio.Base();
        Assert.Empty(await new RegistroInventario(db).VerificarExistenciasAsync());
    }

    [Fact]
    public async Task Los_ajustes_de_la_lectura_de_eleventa_son_movimiento_y_no_otro_conteo()
    {
        // Fuera del piloto (una tienda que ya dejó eleventa trae esos movimientos en su kardex): lo que anotó la lectura después de
        // contar cuenta como movido; un conteo de verdad hecho después sigue ganando.
        await using var t = await TiendaAsync();
        var leido = await ProductoAsync(t, "Sacapuntas que movió eleventa", 20);
        var contado = await ProductoAsync(t, "Sacapuntas contado después", 20);
        var s = Lista(t, DateTime.UtcNow.AddMinutes(-10), Piezas(leido, 15), Piezas(contado, 15));
        await Leer<EstadoConteoDto>(await MandarAsync(t, s));
        await EleventaMovioAsync(t, leido, -3, DateTime.UtcNow.AddMinutes(-5));
        await MoverAsync(t, contado.VarianteId, TipoMovimiento.Conteo, -3, DateTime.UtcNow.AddMinutes(-5));

        var revisar = await Leer<ConteoPorRevisarDto>(await t.Caja2.GetAsync(t.DeLaCaja2($"conteos-recibidos/{s.Id}")));
        Assert.Equal((-3m, 17m, null), (revisar.Renglones[0].Desde, revisar.Renglones[0].Hoy, revisar.Renglones[0].Superado));
        Assert.Matches(@"^Ya se contó o se ajustó después, a las \d\d:\d\d: vale eso\.$", revisar.Renglones[1].Superado);
        Assert.Null(revisar.Eleventa);
        var aceptada = await Leer<EstadoConteoDto>(await AceptarAsync(t, s.Id, t.Encargado.UsuarioId, Queda(leido, 15), Queda(contado, 15)));
        Assert.Equal([true, false], aceptada.Renglones.Select(r => r.Aplicado));
        Assert.All(aceptada.Renglones, r => Assert.Null(r.Eleventa));
        // Contó 15 y eleventa vendió 3 después: quedan 12 (antes no se aplicaba). El otro se queda como lo dejó su conteo.
        Assert.Equal((12m, 17m), (await HayAsync(t, leido.VarianteId), await HayAsync(t, contado.VarianteId)));
        Assert.Empty(await PorCapturarAsync(t));
    }

    [Fact]
    public async Task La_hora_del_celular_se_corrige_y_cada_producto_cuenta_desde_su_ultimo_escaneo()
    {
        await using var t = await TiendaAsync();
        var primero = await ProductoAsync(t, "Tijeras contadas al principio", 10);
        var ultimo = await ProductoAsync(t, "Tijeras contadas al final", 10);
        // El reloj del celular va una hora atrasado. Contó las primeras hace 30 minutos y terminó hace 10; a los 20 se vendió una de cada una.
        var reloj = TimeSpan.FromHours(-1);
        var ahora = DateTime.UtcNow;
        var s = new SolicitudMandarConteo(Guid.NewGuid(), t.Caja2Dto.Id, ahora.AddMinutes(-10) + reloj, null,
            [Piezas(primero, 9) with { ContadoUtc = ahora.AddMinutes(-30) + reloj }, Piezas(ultimo, 9)], EnviadoUtc: ahora + reloj);
        await MoverAsync(t, primero.VarianteId, TipoMovimiento.Venta, -1, ahora.AddMinutes(-20));
        await MoverAsync(t, ultimo.VarianteId, TipoMovimiento.Venta, -1, ahora.AddMinutes(-20));

        var mandada = await Leer<EstadoConteoDto>(await MandarAsync(t, s));
        Assert.Equal(ahora.AddMinutes(-10), mandada.ContadoUtc, TimeSpan.FromSeconds(5));
        var revisar = await Leer<ConteoPorRevisarDto>(await t.Caja2.GetAsync(t.DeLaCaja2($"conteos-recibidos/{s.Id}")));
        // Las primeras: la venta fue después de contarlas (9 − 1 = 8). Las últimas: fue antes, ya venía en lo contado.
        Assert.Equal([-1m, 0m], revisar.Renglones.Select(r => r.Desde));
        await Leer<EstadoConteoDto>(await AceptarAsync(t, s.Id, t.Encargado.UsuarioId, Queda(primero, 9), Queda(ultimo, 9)));
        Assert.Equal((8m, 9m), (await HayAsync(t, primero.VarianteId), await HayAsync(t, ultimo.VarianteId)));
    }

    [Fact]
    public async Task En_el_piloto_las_cajas_reciben_y_aceptar_no_mueve_existencias_deja_la_lista_por_capturar()
    {
        var lectura = new LecturaDePrueba();
        await using var t = await PilotoAsync(lectura);
        var cuaderno = await DeEleventaAsync(t, await ProductoAsync(t, "Cuaderno del piloto", 20));
        var goma = await DeEleventaAsync(t, await ProductoAsync(t, "Goma del piloto", 9));
        var deAqui = await ProductoAsync(t, "Producto dado de alta aquí", 4);

        // El celular pregunta: la Caja 2 recibe, como fuera del piloto, y la lista llega.
        var caja2 = Assert.Single(await Leer<List<CajaParaContarDto>>(await t.Celular.GetAsync(t.Ruta("para-contar"))), c => c.Id == t.Caja2Dto.Id);
        Assert.Equal((true, (string?)null, (string?)null), (caja2.Recibe, caja2.Codigo, caja2.PorQue));
        var s = Lista(t, DateTime.UtcNow.AddMinutes(-10), Piezas(cuaderno, 15), Piezas(goma, 9), Piezas(deAqui, 3));
        Assert.Equal("recibida", (await Leer<EstadoConteoDto>(await MandarAsync(t, s))).Estado);
        // Después de contar: esta caja vendió 1 cuaderno, y eleventa vendió 3 que la lectura de cada 5 minutos todavía no trae.
        await VenderAsync(t, cuaderno, 1);
        lectura.AlLeer = () => EleventaMovioAsync(t, cuaderno, -3);

        // Al abrirla se lee eleventa: lo que vendió cuenta como movido (no como «ya se contó después») y se compara contra lo de allá.
        var revisar = await Leer<ConteoPorRevisarDto>(await t.Caja2.GetAsync(t.DeLaCaja2($"conteos-recibidos/{s.Id}")));
        Assert.Equal(1, lectura.Lecturas);
        Assert.Equal((PapeleriaPos.Servicio.ApiConteosDelCelular.SeCapturaEnEleventa, (string?)null), (revisar.Eleventa, revisar.SinLectura));
        Assert.Equal([(15m, -4m, 16m, null), (9m, 0m, 9m, null), (3m, 0m, 4m, "No está en eleventa: dalo de alta allá.")],
            revisar.Renglones.Select(r => (r.Contado, r.Desde, r.Hoy, r.Superado)));

        // Entre abrirla y aceptarla eleventa vende 1 más: aceptar vuelve a leer.
        lectura.AlLeer = () => EleventaMovioAsync(t, cuaderno, -1);
        var aceptada = await Leer<EstadoConteoDto>(await AceptarAsync(t, s.Id, t.Encargado.UsuarioId, Queda(cuaderno, 15), Queda(goma, 9), Queda(deAqui, 3)));
        Assert.Equal(2, lectura.Lecturas);
        Assert.Equal(("aceptada", "Encargado de prueba", 1), (aceptada.Estado, aceptada.Resolvio, aceptada.Aplicados));
        // Contó 15, desde entonces salieron 5 (1 aquí y 4 en eleventa): deben quedar 10, y eleventa dice 15. La diferencia es −5.
        Assert.Equal([(false, "por-capturar", "Por capturar en eleventa: -5."), (true, null, null), (false, null, "No está en eleventa: dalo de alta allá.")],
            aceptada.Renglones.Select(r => (r.Aplicado, r.Eleventa, r.Nota)));
        // Aquí no cambió ninguna existencia ni entró nada al kardex por la lista.
        Assert.Equal((15m, 9m, 4m), (await HayAsync(t, cuaderno.VarianteId), await HayAsync(t, goma.VarianteId), await HayAsync(t, deAqui.VarianteId)));
        await using (var db = t.Servicio.Base())
        {
            Assert.False(await db.MovimientosInventario.AnyAsync(m => m.DocumentoId == s.Id));
            Assert.Empty(await new RegistroInventario(db).VerificarExistenciasAsync());
            Assert.False(await db.EnviosAEleventa.AnyAsync());
        }
        var acepto = Assert.Single(await BitacoraAsync(t), b => b.Accion == AccionControlada.ConteoCompleto);
        Assert.Null(acepto.MontoCentavos);
        Assert.Matches(@"^Aceptó el conteo de Celular de Luis, contado a las \d\d:\d\d: 2 productos, 1 con diferencia por capturar en eleventa "
            + @"\(aquí no cambió ninguna existencia\); 1 producto no está en eleventa$", acepto.Detalle);
        Assert.Empty(await Leer<List<ConteoRecibidoDto>>(await t.Caja2.GetAsync(t.DeLaCaja2("conteos-recibidos"))));

        // La lista por capturar: solo lo que tiene diferencia, con lo que había, la diferencia y cómo debe quedar.
        var porCapturar = Assert.Single(await PorCapturarAsync(t));
        Assert.Equal((s.Id, "Celular de Luis", "Encargado de prueba", 1), (porCapturar.Id, porCapturar.De, porCapturar.Acepto, porCapturar.Cuadran));
        var renglon = Assert.Single(porCapturar.Renglones);
        Assert.Equal(("Cuaderno del piloto", cuaderno.Codigo, 15m, -5m, 15m, -5m, 10m),
            (renglon.Descripcion, renglon.Codigo, renglon.Contado, renglon.Desde, renglon.Habia, renglon.Diferencia, renglon.DebeQuedar));
        // El celular ve lo mismo al preguntar.
        Assert.Equal("por-capturar", (await Leer<EstadoConteoDto>(await t.Celular.GetAsync(t.Ruta($"conteos/{s.Id}")))).Renglones[0].Eleventa);

        // En Excel, con su permiso.
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Caja2.GetAsync(t.DeLaCaja2($"conteos-por-capturar/{s.Id}/excel?usuarioId={t.Cajero.UsuarioId}"))).StatusCode);
        var excel = await t.Caja2.GetAsync(t.DeLaCaja2($"conteos-por-capturar/{s.Id}/excel?usuarioId={t.Encargado.UsuarioId}"));
        Assert.True(excel.IsSuccessStatusCode, await excel.Content.ReadAsStringAsync());
        Assert.Matches(@"^Conteo por capturar en eleventa Celular de Luis \d{4}-\d\d-\d\d \d\d\.\d\d\.xlsx$", excel.Content.Headers.ContentDisposition!.FileNameStar
            ?? excel.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        using (var libro = new XLWorkbook(new MemoryStream(await excel.Content.ReadAsByteArrayAsync())))
        {
            var hoja = libro.Worksheet("Por capturar en eleventa");
            var titulos = hoja.RowsUsed().Single(f => f.Cell(1).GetString() == "Producto");
            Assert.Equal(["Producto", "Código", "Contado", "Desde que se contó", "Había", "Diferencia", "Debe quedar", "Unidad"],
                titulos.CellsUsed().Select(c => c.GetString()));
            var fila = hoja.Row(titulos.RowNumber() + 1);
            Assert.Equal(("Cuaderno del piloto", 15d, -5d, 15d, -5d, 10d), (fila.Cell(1).GetString(), fila.Cell(3).GetDouble(), fila.Cell(4).GetDouble(),
                fila.Cell(5).GetDouble(), fila.Cell(6).GetDouble(), fila.Cell(7).GetDouble()));
        }

        // «Ya lo capturé en eleventa»: con el permiso. Cierra la lista, pide una lectura y sigue sin mover nada aquí.
        Assert.Equal("no-permitido", (await Error(await CapturadoAsync(t, s.Id, t.Cajero.UsuarioId), HttpStatusCode.Forbidden)).Codigo);
        Assert.Single(await PorCapturarAsync(t));
        var cerrada = await Leer<EstadoConteoDto>(await CapturadoAsync(t, s.Id, t.Encargado.UsuarioId));
        Assert.Equal(("aceptada", 2), (cerrada.Estado, cerrada.Aplicados));
        Assert.Equal((true, "capturado", "Capturado en eleventa: -5."), (cerrada.Renglones[0].Aplicado, cerrada.Renglones[0].Eleventa, cerrada.Renglones[0].Nota));
        Assert.Empty(await PorCapturarAsync(t));
        Assert.Equal(1, lectura.Pedidas);
        Assert.Equal(15m, await HayAsync(t, cuaderno.VarianteId));
        Assert.Contains(await BitacoraAsync(t), b => b.Accion == AccionControlada.ConteoCompleto && b.UsuarioId == t.Encargado.UsuarioId
            && b.Detalle.StartsWith("Dijo que ya capturó en eleventa el conteo de Celular de Luis, contado a las ", StringComparison.Ordinal)
            && b.Detalle.EndsWith(": 1 producto con diferencia", StringComparison.Ordinal));
        var otraVez = await Error(await CapturadoAsync(t, s.Id, t.Encargado.UsuarioId), HttpStatusCode.Conflict);
        Assert.Equal(("nada-por-capturar", "Esa lista ya no tiene nada por capturar en eleventa."), (otraVez.Codigo, otraVez.Mensaje));
        // El Excel se puede sacar todavía (por si se cerró antes de guardarlo).
        Assert.True((await t.Caja2.GetAsync(t.DeLaCaja2($"conteos-por-capturar/{s.Id}/excel?usuarioId={t.Encargado.UsuarioId}"))).IsSuccessStatusCode);
    }

    [Fact]
    public async Task En_el_piloto_si_eleventa_no_contesta_no_se_acepta_y_rechazar_sigue_igual()
    {
        var lectura = new LecturaDePrueba { Falla = "La PC de eleventa no contesta." };
        await using var t = await PilotoAsync(lectura);
        var p = await DeEleventaAsync(t, await ProductoAsync(t, "Marcador del piloto", 10));
        var s = Lista(t, null, Piezas(p, 4));
        await Leer<EstadoConteoDto>(await MandarAsync(t, s));

        // Se puede ver (con lo de la última lectura, y lo dice), pero no aceptar.
        var revisar = await Leer<ConteoPorRevisarDto>(await t.Caja2.GetAsync(t.DeLaCaja2($"conteos-recibidos/{s.Id}")));
        Assert.Equal("Eleventa no contesta: lo que se ve es de su última lectura. Para aceptarla hace falta que conteste.", revisar.SinLectura);
        // El permiso va primero.
        Assert.Equal("no-permitido", (await Error(await AceptarAsync(t, s.Id, t.Cajero.UsuarioId, Queda(p, 4)), HttpStatusCode.Forbidden)).Codigo);
        var sinEleventa = await Error(await AceptarAsync(t, s.Id, t.Encargado.UsuarioId, Queda(p, 4)), HttpStatusCode.Conflict);
        Assert.Equal(("eleventa-no-contesta", "Eleventa no contesta: sin su existencia de ahora no se puede calcular la diferencia. Inténtalo en un momento."),
            (sinEleventa.Codigo, sinEleventa.Mensaje));
        Assert.Equal("recibida", (await Leer<EstadoConteoDto>(await t.Celular.GetAsync(t.Ruta($"conteos/{s.Id}")))).Estado);
        Assert.Equal(10m, await HayAsync(t, p.VarianteId));

        // Cuando contesta, se acepta. Otra lista, rechazada: como siempre, sin leer eleventa.
        lectura.Falla = null;
        Assert.Equal("aceptada", (await Leer<EstadoConteoDto>(await AceptarAsync(t, s.Id, t.Encargado.UsuarioId, Queda(p, 4)))).Estado);
        Assert.Equal(-6m, Assert.Single(Assert.Single(await PorCapturarAsync(t)).Renglones).Diferencia);
        var otra = Lista(t, null, Piezas(p, 1));
        await Leer<EstadoConteoDto>(await MandarAsync(t, otra));
        var lecturas = lectura.Lecturas;
        Assert.Equal("rechazada", (await Leer<EstadoConteoDto>(await t.Caja2.PostAsJsonAsync(t.DeLaCaja2($"conteos-recibidos/{otra.Id}/rechazar"),
            new SolicitudRechazarConteo(t.Encargado.UsuarioId, null)))).Estado);
        Assert.Equal(lecturas, lectura.Lecturas);
        // Ya aceptada: no se vuelve a leer eleventa para decirlo.
        Assert.Equal("ya-resuelta", (await Error(await AceptarAsync(t, s.Id, t.Encargado.UsuarioId, Queda(p, 4)), HttpStatusCode.Conflict)).Codigo);
        Assert.Equal(lecturas, lectura.Lecturas);
    }

    [Fact]
    public async Task En_el_piloto_con_dos_listas_del_mismo_producto_solo_se_captura_la_que_se_conto_al_final()
    {
        var lectura = new LecturaDePrueba();
        await using var t = await PilotoAsync(lectura);
        var p = await DeEleventaAsync(t, await ProductoAsync(t, "Pegamento del piloto", 20));
        var q = await DeEleventaAsync(t, await ProductoAsync(t, "Silicón del piloto", 20));
        // La vieja (hace 20 minutos): 17 y 17. Aceptada: −3 y −3 por capturar.
        var vieja = Lista(t, DateTime.UtcNow.AddMinutes(-20), Piezas(p, 17), Piezas(q, 17));
        await Leer<EstadoConteoDto>(await MandarAsync(t, vieja));
        await Leer<EstadoConteoDto>(await AceptarAsync(t, vieja.Id, t.Encargado.UsuarioId, Queda(p, 17), Queda(q, 17)));
        Assert.Equal([-3m, -3m], Assert.Single(await PorCapturarAsync(t)).Renglones.Select(r => r.Diferencia));

        // La nueva (hace 5 minutos) trae el pegamento otra vez, 16: la caja avisa que reemplaza a la otra, y al aceptarla aquella ya no se captura.
        var nueva = Lista(t, DateTime.UtcNow.AddMinutes(-5), Piezas(p, 16));
        await Leer<EstadoConteoDto>(await MandarAsync(t, nueva));
        var revisar = await Leer<ConteoPorRevisarDto>(await t.Caja2.GetAsync(t.DeLaCaja2($"conteos-recibidos/{nueva.Id}")));
        Assert.Null(revisar.Renglones[0].Superado);
        Assert.Matches(@"^Tiene una diferencia por capturar de otra lista \(Celular de Luis, contada a las \d\d:\d\d\): al aceptar esta, aquella ya no se captura\.$",
            revisar.Renglones[0].Aviso);
        await Leer<EstadoConteoDto>(await AceptarAsync(t, nueva.Id, t.Encargado.UsuarioId, Queda(p, 16)));
        var listas = await PorCapturarAsync(t);
        Assert.Equal([(vieja.Id, "Silicón del piloto", -3m), (nueva.Id, "Pegamento del piloto", -4m)],
            listas.Select(l => (l.Id, l.Renglones.Single().Descripcion, l.Renglones.Single().Diferencia)));
        var deLaVieja = await Leer<EstadoConteoDto>(await t.Celular.GetAsync(t.Ruta($"conteos/{vieja.Id}")));
        Assert.Equal((false, null, "No se aplicó: ya se había contado después."),
            (deLaVieja.Renglones[0].Aplicado, deLaVieja.Renglones[0].Eleventa, deLaVieja.Renglones[0].Nota));

        // Una tercera, contada ANTES de que se dijera «ya lo capturé» de la vieja: su silicón ya no se lleva (esa captura llegó con la
        // lectura como movimiento y no se puede separar de lo vendido).
        var tercera = Lista(t, DateTime.UtcNow.AddMinutes(-1), Piezas(q, 18));
        await Leer<EstadoConteoDto>(await MandarAsync(t, tercera));
        await Leer<EstadoConteoDto>(await CapturadoAsync(t, vieja.Id, t.Encargado.UsuarioId));
        revisar = await Leer<ConteoPorRevisarDto>(await t.Caja2.GetAsync(t.DeLaCaja2($"conteos-recibidos/{tercera.Id}")));
        Assert.Matches(@"^Después de contarlo se capturó en eleventa la diferencia de otra lista \(Celular de Luis, a las \d\d:\d\d\): vale eso\. "
            + @"Cuéntalo otra vez si hace falta\.$", revisar.Renglones[0].Superado);
        var aceptada = await Leer<EstadoConteoDto>(await AceptarAsync(t, tercera.Id, t.Encargado.UsuarioId, Queda(q, 18)));
        Assert.Equal((false, null), (aceptada.Renglones[0].Aplicado, aceptada.Renglones[0].Eleventa));
        Assert.Equal(nueva.Id, Assert.Single(await PorCapturarAsync(t)).Id);
        Assert.Equal((20m, 20m), (await HayAsync(t, p.VarianteId), await HayAsync(t, q.VarianteId)));
    }
}
