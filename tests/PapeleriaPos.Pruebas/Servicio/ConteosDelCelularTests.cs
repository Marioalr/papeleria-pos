using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
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
/// se contó y lo vendido después sigue contando; lo más nuevo gana. En el piloto con eleventa no se manda ni se aplica.
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

    private static async Task<Tienda> TiendaAsync(params (string, string)[] ajustes)
    {
        var servicio = await ServicioDePrueba.CrearAsync(ajustes);
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
    public async Task En_el_piloto_con_eleventa_no_se_manda_ni_se_aplica_y_lo_que_llego_antes_se_rechaza()
    {
        await using var t = await TiendaAsync();
        var p = await ProductoAsync(t, "Marcador del piloto", 10);
        var antes = Lista(t, null, Piezas(p, 4));
        await Leer<EstadoConteoDto>(await MandarAsync(t, antes));

        await using var piloto = t.Servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));
        var celular = piloto.CreateClient();
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, t.Celular.DefaultRequestHeaders.GetValues(ClienteCaja.EncabezadoLlave).Single());
        var caja2 = piloto.CreateClient();
        caja2.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, t.Caja2.DefaultRequestHeaders.GetValues(ClienteCaja.EncabezadoLlave).Single());

        var enPiloto = Assert.Single(await Leer<List<CajaParaContarDto>>(await celular.GetAsync(t.Ruta("para-contar"))));
        Assert.Equal((false, "piloto-eleventa"), (enPiloto.Recibe, enPiloto.Codigo));
        Assert.Equal($"{TrastiendaEnElPiloto.InventarioEnEleventa} El conteo se captura allá.", enPiloto.PorQue);
        var mandar = await Error(await celular.PostAsJsonAsync(t.Ruta("mandar-conteo"), Lista(t, null, Piezas(p, 4))), HttpStatusCode.Conflict);
        Assert.Equal(("piloto-eleventa", enPiloto.PorQue), (mandar.Codigo, mandar.Mensaje));
        // La que llegó antes: aplicarla, no (el permiso va primero); rechazarla, sí.
        Assert.Equal("no-permitido", (await Error(await caja2.PostAsJsonAsync(t.DeLaCaja2($"conteos-recibidos/{antes.Id}/aceptar"),
            new SolicitudAceptarConteo(t.Cajero.UsuarioId, [Queda(p, 4)])), HttpStatusCode.Forbidden)).Codigo);
        var aplicar = await Error(await caja2.PostAsJsonAsync(t.DeLaCaja2($"conteos-recibidos/{antes.Id}/aceptar"),
            new SolicitudAceptarConteo(t.Encargado.UsuarioId, [Queda(p, 4)])), HttpStatusCode.Conflict);
        Assert.Equal(("piloto-eleventa", TrastiendaEnElPiloto.InventarioEnEleventa), (aplicar.Codigo, aplicar.Mensaje));
        Assert.Equal(10m, await HayAsync(t, p.VarianteId));
        Assert.Equal("rechazada", (await Leer<EstadoConteoDto>(await caja2.PostAsJsonAsync(t.DeLaCaja2($"conteos-recibidos/{antes.Id}/rechazar"),
            new SolicitudRechazarConteo(t.Encargado.UsuarioId, null)))).Estado);
    }
}
