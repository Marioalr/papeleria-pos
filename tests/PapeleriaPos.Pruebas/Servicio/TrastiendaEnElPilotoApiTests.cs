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

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Productos e Inventario en el piloto con eleventa (Eleventa:Activo, su lectura cada pocos minutos; dueño, 2026-10-10:
/// las altas y los precios, en eleventa). De un producto de eleventa no se cambia lo que pone la lectura (lo demás sí), y lo
/// que mueve existencias no se hace: la siguiente lectura lo regresaría sin decir nada. El servicio lo rechaza con el mismo
/// aviso que enseñan las páginas; el permiso va primero. Fuera del piloto todo sigue igual.
/// </summary>
public sealed class TrastiendaEnElPilotoApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, Guid Sucursal, SesionDto Cajero, SesionDto Encargado);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<string> RechazoAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    private static async Task<Caja> PrepararAsync(HttpClient http)
    {
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        return new Caja(http, caja.SucursalId, await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))),
            await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222"))));
    }

    /// <summary>El mismo servicio, con la lectura de eleventa prendida (sin servidor configurado: no lee nada).</summary>
    private WebApplicationFactory<Program> Piloto() => servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));

    private static readonly Cantidad Doce = Cantidad.Unidades(12);

    /// <summary>
    /// Un lápiz como los que llegan de eleventa: pieza con su código, caja de 12, menudeo de $15, la promoción de eleventa
    /// desde 12 a $12 y una escala capturada aquí desde 24 a $11. Con <paramref name="deEleventa"/>, con su ID de eleventa
    /// (y la promoción, como la anota la lectura).
    /// </summary>
    private async Task<FichaProductoDto> LapizAsync(Caja c, string nombre, bool deEleventa = true)
    {
        var departamento = (await c.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        var ficha = await Leer<FichaProductoDto>(await c.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Sucursal,
            new FichaProductoDto(null, nombre, departamento, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                    [
                        new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto($"ELE{Guid.NewGuid():N}"[..16])]),
                        new PresentacionFichaDto(null, "Caja 12", 12000, false, []),
                    ],
                    [new PrecioFichaDto(null, 1000, 1500), new PrecioFichaDto(null, 12000, 1200), new PrecioFichaDto(null, 24000, 1100)]),
            ]))));
        if (deEleventa)
        {
            var variante = ficha.Variantes[0].Id!.Value;
            var idEleventa = Random.Shared.Next(100_000, 999_999).ToString();
            await using var db = servicio.Base();
            var promocion = await db.Precios.SingleAsync(p => p.VarianteId == variante && p.CantidadMinima == Doce);
            db.MapeosImportacion.AddRange(
                new MapeoImportacion { Origen = "eleventa", Entidad = nameof(Variante), IdOrigen = idEleventa, IdNuevo = variante },
                new MapeoImportacion { Origen = "eleventa", Entidad = "Promocion", IdOrigen = $"{idEleventa}@{Doce.Milesimas}", IdNuevo = promocion.Id });
            await db.SaveChangesAsync();
        }
        return ficha;
    }

    private static async Task<FichaProductoDto> FichaAsync(Caja c, FichaProductoDto producto) =>
        await Leer<FichaProductoDto>(await c.Http.GetAsync($"api/catalogo/productos/{producto.Id}?sucursalId={c.Sucursal}&usuarioId={c.Encargado.UsuarioId}"));

    private static Task<HttpResponseMessage> GuardarAsync(Caja c, FichaProductoDto ficha, SesionDto? quien = null) =>
        c.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto((quien ?? c.Encargado).UsuarioId, c.Sucursal, ficha));

    /// <summary>La única variante con otros precios.</summary>
    private static FichaProductoDto ConPrecios(FichaProductoDto f, params PrecioFichaDto[] precios) =>
        f with { Variantes = [f.Variantes[0] with { Precios = precios }] };

    /// <summary>La única variante con otros códigos en su pieza.</summary>
    private static FichaProductoDto ConCodigos(FichaProductoDto f, params string[] codigos) =>
        f with
        {
            Variantes =
            [
                f.Variantes[0] with
                {
                    Presentaciones = f.Variantes[0].Presentaciones.Select(p => p.EsBase ? p with { Codigos = codigos.Select(x => new CodigoFichaDto(x)).ToList() } : p).ToList(),
                },
            ],
        };

    [Fact]
    public async Task De_un_producto_de_eleventa_no_se_cambia_lo_que_pone_la_lectura_y_lo_demas_si()
    {
        var normal = await PrepararAsync(servicio.Fabrica.CreateClient());
        var lapiz = await LapizAsync(normal, "Lápiz de eleventa");
        var codigo = lapiz.Variantes[0].Presentaciones.Single(p => p.EsBase).Codigos.Single().Codigo;
        var otroDepartamento = (await normal.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[1].Id;
        await using var piloto = Piloto();
        var c = await PrepararAsync(piloto.CreateClient());

        var f = await FichaAsync(c, lapiz);
        var v = f.Variantes[0];
        // La ficha dice qué pone la lectura: el producto (una sola variante, de eleventa), la variante, el menudeo y la promoción.
        Assert.Equal(TrastiendaEnElPiloto.ProductosEnEleventa, f.EnEleventa);
        Assert.True(f.ProductoDeEleventa);
        Assert.True(v.DeEleventa);
        Assert.Equal([(1000L, true), (12000L, true), (24000L, false)], v.Precios.Select(p => (p.CantidadMinimaMilesimas, p.DeEleventa)));

        var cambios = new (string Que, FichaProductoDto Ficha)[]
        {
            ("el nombre", f with { Nombre = "Lápiz renombrado" }),
            ("el departamento", f with { DepartamentoId = otroDepartamento }),
            ("si se vende", f with { Activo = false }),
            ("si la variante está activa", f with { Variantes = [v with { Activa = false }] }),
            ("el menudeo", ConPrecios(f, new(null, 1000, 1600), new(null, 12000, 1200), new(null, 24000, 1100))),
            ("la promoción", ConPrecios(f, new(null, 1000, 1500), new(null, 12000, 1250), new(null, 24000, 1100))),
            ("quitar la promoción", ConPrecios(f, new(null, 1000, 1500), new(null, 24000, 1100))),
            ("el código de la pieza", ConCodigos(f, $"OTRO{Guid.NewGuid():N}"[..16])),
        };
        foreach (var (que, ficha) in cambios)
            Assert.True(await RechazoAsync(await GuardarAsync(c, ficha)) == TrastiendaEnElPiloto.ProductosEnEleventa, $"Dejó cambiar {que}.");

        // Lo que la lectura no toca: otro código en la pieza, la escala capturada aquí, una escala nueva, la caja y la marca.
        var extra = $"EXT{Guid.NewGuid():N}"[..16];
        var permitido = ConCodigos(ConPrecios(f, new(null, 1000, 1500), new(null, 12000, 1200), new(null, 24000, 1050), new(null, 50000, 1000)), codigo, extra)
            with { Marca = "Marca del piloto" };
        permitido = permitido with
        {
            Variantes = [permitido.Variantes[0] with { Presentaciones = permitido.Variantes[0].Presentaciones.Select(p => p.EsBase ? p : p with { Nombre = "Caja de 12" }).ToList() }],
        };
        var guardada = await Leer<FichaProductoDto>(await GuardarAsync(c, permitido));
        Assert.Equal("Lápiz de eleventa", guardada.Nombre);
        Assert.Equal("Marca del piloto", guardada.Marca);
        Assert.Equal(new[] { codigo, extra }.Order(StringComparer.Ordinal),
            guardada.Variantes[0].Presentaciones.Single(p => p.EsBase).Codigos.Select(x => x.Codigo).Order(StringComparer.Ordinal));
        Assert.Contains(guardada.Variantes[0].Presentaciones, p => p.Nombre == "Caja de 12");
        Assert.Equal([(1000L, 1500L, true), (12000L, 1200L, true), (24000L, 1050L, false), (50000L, 1000L, false)],
            guardada.Variantes[0].Precios.Select(p => (p.CantidadMinimaMilesimas, p.PrecioCentavos, p.DeEleventa)));

        // El permiso va primero: el cajero oye lo de siempre.
        Assert.Equal(HttpStatusCode.Forbidden, (await GuardarAsync(c, f with { Nombre = "Del cajero" }, c.Cajero)).StatusCode);

        // Fuera del piloto, el mismo producto se cambia todo, como siempre.
        var fuera = await FichaAsync(normal, lapiz);
        Assert.Null(fuera.EnEleventa);
        Assert.False(fuera.ProductoDeEleventa || fuera.Variantes[0].DeEleventa || fuera.Variantes[0].Precios.Any(p => p.DeEleventa));
        var renombrado = await Leer<FichaProductoDto>(await GuardarAsync(normal, ConPrecios(fuera with { Nombre = "Lápiz renombrado" },
            new(null, 1000, 1600), new(null, 12000, 1250), new(null, 24000, 1050), new(null, 50000, 1000))));
        Assert.Equal(("Lápiz renombrado", 1600L), (renombrado.Nombre, renombrado.Variantes[0].Precios.Single(p => p.CantidadMinimaMilesimas == 1000).PrecioCentavos));
    }

    [Fact]
    public async Task En_el_piloto_el_menudeo_y_la_depuracion_de_lo_de_eleventa_tampoco_y_lo_de_aqui_si()
    {
        var normal = await PrepararAsync(servicio.Fabrica.CreateClient());
        var deEleventa = await LapizAsync(normal, "Goma de eleventa");
        var deAqui = await LapizAsync(normal, "Goma dada de alta aquí", deEleventa: false);
        var otraDeAqui = await LapizAsync(normal, "Otra goma de aquí", deEleventa: false);
        var varianteEleventa = deEleventa.Variantes[0].Id!.Value;
        var varianteAqui = deAqui.Variantes[0].Id!.Value;
        await using var piloto = Piloto();
        var c = await PrepararAsync(piloto.CreateClient());

        var menudeo = await RechazoAsync(await c.Http.PostAsJsonAsync("api/catalogo/precio-menudeo",
            new SolicitudPrecioMenudeo(c.Encargado.UsuarioId, c.Sucursal, varianteEleventa, 1700)));
        var baja = await RechazoAsync(await c.Http.PostAsJsonAsync("api/catalogo/depuracion/baja", new SolicitudBaja(c.Encargado.UsuarioId, c.Sucursal, varianteEleventa)));
        var unidad = await RechazoAsync(await c.Http.PostAsJsonAsync("api/catalogo/depuracion/unidad",
            new SolicitudCambiarUnidad(c.Encargado.UsuarioId, c.Sucursal, varianteEleventa, UnidadBase.Metro, null, 500)));
        var juntar = await RechazoAsync(await c.Http.PostAsJsonAsync("api/catalogo/depuracion/juntar",
            new SolicitudJuntar(c.Encargado.UsuarioId, c.Sucursal, varianteAqui, varianteEleventa)));
        var bajaDelCajero = await c.Http.PostAsJsonAsync("api/catalogo/depuracion/baja", new SolicitudBaja(c.Cajero.UsuarioId, c.Sucursal, varianteEleventa));

        Assert.All(new[] { menudeo, baja, unidad, juntar }, m => Assert.Equal(TrastiendaEnElPiloto.ProductosEnEleventa, m));
        Assert.Equal(HttpStatusCode.Forbidden, bajaDelCajero.StatusCode);

        // Lo dado de alta aquí (sin ID de eleventa) se cambia y se depura como siempre: va a eleventa como artículo común.
        var fichaAqui = await FichaAsync(c, deAqui);
        Assert.Null(fichaAqui.EnEleventa);
        Assert.False(fichaAqui.ProductoDeEleventa || fichaAqui.Variantes[0].DeEleventa);
        Assert.Equal("Goma de aquí, renombrada", (await Leer<FichaProductoDto>(await GuardarAsync(c, fichaAqui with { Nombre = "Goma de aquí, renombrada" }))).Nombre);
        Assert.Equal(HttpStatusCode.NoContent, (await c.Http.PostAsJsonAsync("api/catalogo/precio-menudeo",
            new SolicitudPrecioMenudeo(c.Encargado.UsuarioId, c.Sucursal, varianteAqui, 1700))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.Http.PostAsJsonAsync("api/catalogo/depuracion/baja",
            new SolicitudBaja(c.Encargado.UsuarioId, c.Sucursal, otraDeAqui.Variantes[0].Id!.Value))).StatusCode);

        // Fuera del piloto, el menudeo del de eleventa se cambia.
        Assert.Equal(HttpStatusCode.NoContent, (await normal.Http.PostAsJsonAsync("api/catalogo/precio-menudeo",
            new SolicitudPrecioMenudeo(normal.Encargado.UsuarioId, normal.Sucursal, varianteEleventa, 1700))).StatusCode);
    }

    [Fact]
    public async Task En_el_piloto_lo_que_mueve_existencias_no_se_hace_y_lo_abierto_se_cancela()
    {
        var normal = await PrepararAsync(servicio.Fabrica.CreateClient());
        var pluma = (await LapizAsync(normal, "Pluma de eleventa")).Variantes[0];
        var variante = pluma.Id!.Value;
        var caja12 = pluma.Presentaciones.Single(p => !p.EsBase).Id!.Value;
        var departamento = (await normal.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        var proveedor = await Leer<ProveedorDto>(await normal.Http.PostAsJsonAsync("api/proveedores", new SolicitudGuardarProveedor(normal.Encargado.UsuarioId, normal.Sucursal,
            new ProveedorDto(null, $"Proveedor del piloto {Guid.NewGuid():N}"[..28], null, null, null))));
        // Lo que quedó abierto de antes del piloto: una compra, un conteo completo y el conteo del día.
        var compra = await Leer<CompraDto>(await normal.Http.PostAsJsonAsync("api/compras",
            new SolicitudAbrirCompra(normal.Encargado.UsuarioId, normal.Sucursal, proveedor.Id!.Value, null)));
        var abierto = await Leer<ConteoDepartamentoDto>(await normal.Http.PostAsJsonAsync($"api/sucursales/{normal.Sucursal}/conteos-completos",
            new SolicitudAbrirConteo(normal.Encargado.UsuarioId, departamento)));
        await using var piloto = Piloto();
        var c = await PrepararAsync(piloto.CreateClient());

        // El conteo del día no se arma: la venta no enseña su chip.
        var delDia = (await c.Http.GetFromJsonAsync<ConteoCiclicoDto>($"api/sucursales/{c.Sucursal}/conteo-del-dia?usuarioId={c.Cajero.UsuarioId}"))!;
        await using (var db = servicio.Base())
            Assert.False(await db.ConteosCiclicos.AnyAsync());
        var hoy = (await normal.Http.GetFromJsonAsync<ConteoCiclicoDto>($"api/sucursales/{c.Sucursal}/conteo-del-dia?usuarioId={c.Cajero.UsuarioId}"))!;
        var aContar = hoy.Renglones[0].VarianteId;

        var otroDepartamento = (await normal.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[1].Id;
        var rechazos = new (string Que, Func<Task<HttpResponseMessage>> Pedir)[]
        {
            ("un ajuste", () => c.Http.PostAsJsonAsync("api/inventario/ajustes", new SolicitudAjuste(c.Encargado.UsuarioId, c.Sucursal, variante, 5000, "Conté"))),
            ("una merma", () => c.Http.PostAsJsonAsync("api/inventario/mermas",
                new SolicitudMerma(c.Encargado.UsuarioId, c.Sucursal, variante, 1000, CausaMerma.Danado, null))),
            ("abrir un paquete", () => c.Http.PostAsJsonAsync("api/aperturas",
                new SolicitudAbrirPaquete(c.Encargado.UsuarioId, c.Sucursal, variante, 1000, aContar, 12000))),
            ("el conteo del día", () => c.Http.PostAsJsonAsync($"api/conteos/{hoy.Id}/contar", new SolicitudContar(c.Cajero.UsuarioId, aContar, 3000))),
            ("abrir un conteo completo", () => c.Http.PostAsJsonAsync($"api/sucursales/{c.Sucursal}/conteos-completos",
                new SolicitudAbrirConteo(c.Encargado.UsuarioId, otroDepartamento))),
            ("capturar en el conteo completo", () => c.Http.PostAsJsonAsync($"api/conteos-completos/{abierto.Id}/capturas",
                new SolicitudCaptura(Ids.Nuevo(), c.Cajero.UsuarioId, variante, 2000))),
            ("cerrar el conteo completo", () => c.Http.PostAsJsonAsync($"api/conteos-completos/{abierto.Id}/cerrar",
                new SolicitudConteoCompleto(c.Encargado.UsuarioId))),
            ("abrir una compra", () => c.Http.PostAsJsonAsync("api/compras", new SolicitudAbrirCompra(c.Cajero.UsuarioId, c.Sucursal, proveedor.Id!.Value, null))),
            ("contar lo recibido", () => c.Http.PostAsJsonAsync($"api/compras/{compra.Id}/conteo",
                new SolicitudConteo(c.Encargado.UsuarioId, variante, caja12, 2000))),
            ("cerrar la compra", () => c.Http.PostAsJsonAsync($"api/compras/{compra.Id}/cerrar", new SolicitudCompra(c.Encargado.UsuarioId))),
            // El cajero: el ajuste lo pediría autorizado y la merma la puede; tampoco.
            ("un ajuste del cajero", () => c.Http.PostAsJsonAsync("api/inventario/ajustes",
                new SolicitudAjuste(c.Cajero.UsuarioId, c.Sucursal, variante, 5000, "Conté"))),
            ("una merma del cajero", () => c.Http.PostAsJsonAsync("api/inventario/mermas",
                new SolicitudMerma(c.Cajero.UsuarioId, c.Sucursal, variante, 1000, CausaMerma.Danado, null))),
        };
        foreach (var (que, pedir) in rechazos)
            Assert.True(await RechazoAsync(await pedir()) == TrastiendaEnElPiloto.InventarioEnEleventa, $"Dejó hacer {que}.");

        // Lo abierto se cancela. El permiso va primero: el cajero no abre el conteo completo ni cierra la compra.
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Http.PostAsJsonAsync($"api/sucursales/{c.Sucursal}/conteos-completos",
            new SolicitudAbrirConteo(c.Cajero.UsuarioId, departamento))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Http.PostAsJsonAsync($"api/compras/{compra.Id}/cerrar", new SolicitudCompra(c.Cajero.UsuarioId))).StatusCode);
        Assert.Equal(EstadoConteoDepartamento.Cancelado, (await Leer<ConteoDepartamentoDto>(await c.Http.PostAsJsonAsync($"api/conteos-completos/{abierto.Id}/cancelar",
            new SolicitudConteoCompleto(c.Encargado.UsuarioId)))).Estado);
        Assert.Equal(EstadoCompra.Cancelada, (await Leer<CompraDto>(await c.Http.PostAsJsonAsync($"api/compras/{compra.Id}/cancelar",
            new SolicitudCancelarCompra(c.Encargado.UsuarioId, "Se recibe en eleventa")))).Estado);
        Assert.Empty(delDia.Renglones);
        Assert.Equal(0, delDia.Total);
        await using (var db = servicio.Base())
            Assert.False(await db.MovimientosInventario.AnyAsync(m => m.VarianteId == variante));

        // Fuera del piloto, todo como siempre.
        await Leer<AjusteInventarioDto>(await normal.Http.PostAsJsonAsync("api/inventario/ajustes",
            new SolicitudAjuste(normal.Encargado.UsuarioId, normal.Sucursal, variante, 5000, "Conté")));
        await Leer<AjusteInventarioDto>(await normal.Http.PostAsJsonAsync("api/inventario/mermas",
            new SolicitudMerma(normal.Encargado.UsuarioId, normal.Sucursal, variante, 1000, CausaMerma.Danado, null)));
        await using (var db = servicio.Base())
            Assert.Equal(2, await db.MovimientosInventario.CountAsync(m => m.VarianteId == variante));
    }

    [Fact]
    public async Task Sin_el_permiso_del_ajuste_se_oye_lo_del_permiso_antes_que_lo_del_piloto()
    {
        var normal = await PrepararAsync(servicio.Fabrica.CreateClient());
        var variante = (await LapizAsync(normal, "Corrector de eleventa")).Variantes[0].Id!.Value;
        await using var piloto = Piloto();
        var c = await PrepararAsync(piloto.CreateClient());
        async Task NivelDelCajeroAsync(NivelPermiso nivel)
        {
            await using var db = servicio.Base();
            (await db.PermisosUsuario.SingleAsync(p => p.UsuarioId == c.Cajero.UsuarioId && p.Accion == AccionControlada.AjustarInventario)).Nivel = nivel;
            await db.SaveChangesAsync();
        }

        await NivelDelCajeroAsync(NivelPermiso.NoPermitido);
        try
        {
            var sinPermiso = await c.Http.PostAsJsonAsync("api/inventario/ajustes", new SolicitudAjuste(c.Cajero.UsuarioId, c.Sucursal, variante, 5000, "Conté"));

            Assert.Equal(HttpStatusCode.Forbidden, sinPermiso.StatusCode);
            Assert.Equal("No tienes permiso para esto.", (await sinPermiso.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        }
        finally
        {
            await NivelDelCajeroAsync(NivelPermiso.RequiereAutorizacion);
        }
    }

    [Fact]
    public async Task Las_paginas_saben_si_es_el_piloto()
    {
        await using var piloto = Piloto();

        var enElPiloto = (await piloto.CreateClient().GetFromJsonAsync<TrastiendaEnElPilotoDto>("api/catalogo/piloto"))!;
        var fuera = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<TrastiendaEnElPilotoDto>("api/catalogo/piloto"))!;

        Assert.Equal(new TrastiendaEnElPilotoDto(TrastiendaEnElPiloto.ProductosEnEleventa, TrastiendaEnElPiloto.InventarioEnEleventa), enElPiloto);
        Assert.Equal(new TrastiendaEnElPilotoDto(null, null), fuera);
    }
}
