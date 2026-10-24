using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
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
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Inventario › «Agregar inventario», como en eleventa (2026-10-24), desde el servicio: lo que llegó de un producto entra a la
/// existencia con su costo, el costo promedio queda como lo deja una compra, con su movimiento del kardex («Agregar
/// inventario», con quién), la bitácora (con el costo de antes y el de después si cambió) y la versión de las existencias. Con
/// el mismo Id, una sola vez. Solo con el permiso «Agregar inventario»; el costo, solo quien ve los costos (si otro lo manda, se
/// rechaza). En el piloto con eleventa, no (el permiso va primero). Las fracciones, según la unidad, y los límites. De la
/// revisión del PR #138: lo agregado con su costo es el «último costo» de todo el POS (la venta sin existencia, los ajustes),
/// lo agregado a $0 sin costo conocido no cuenta como costo, y el mismo Id con otro contenido se rechaza (409).
/// </summary>
public sealed class AgregarInventarioApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
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

    private sealed record Escenario(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno)
    {
        public Guid Sucursal => Caja.SucursalId;
    }

    private async Task<Escenario> PrepararAsync(HttpClient? http = null)
    {
        http ??= servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Escenario(http, caja, await Entrar("cajero", "1111"), await Entrar("encargado", "2222"), await Entrar("dueno", "3333"));
    }

    /// <summary>
    /// Un producto nuevo con su código, por <paramref name="unidad"/> y a $20, con <paramref name="existencia"/> a
    /// <paramref name="costo"/> cada uno como inventario inicial (como al importar). Regresa su ficha.
    /// </summary>
    private async Task<FichaProductoDto> ProductoAsync(Escenario e, string nombre, decimal existencia, long costo, UnidadBase unidad = UnidadBase.Pieza,
        TipoProducto tipo = TipoProducto.Mercancia)
    {
        var departamento = (await e.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        var ficha = await Leer<FichaProductoDto>(await e.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(e.Encargado.UsuarioId,
            e.Sucursal, new FichaProductoDto(null, nombre, departamento, null, tipo, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", unidad, true,
                    [new PresentacionFichaDto(null, unidad == UnidadBase.Pieza ? "Pieza" : "Metro", 1000, true, [new CodigoFichaDto($"AI{Guid.NewGuid():N}"[..14])])],
                    [new PrecioFichaDto(null, 1000, 2000)]),
            ]))));
        if (existencia != 0)
        {
            await using var db = servicio.Base();
            var variante = await db.Variantes.SingleAsync(v => v.Id == ficha.Variantes[0].Id);
            var cantidad = Cantidad.Unidades(existencia);
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(e.Sucursal, variante, TipoMovimiento.InventarioInicial, cantidad,
                cantidad.Importe(costo), DateTime.UtcNow, documentoTipo: OrigenDelMovimiento.ImportacionEleventa));
        }
        return ficha;
    }

    private static Guid Variante(FichaProductoDto ficha) => ficha.Variantes[0].Id!.Value;

    private static Task<HttpResponseMessage> AgregarAsync(Escenario e, SesionDto quien, Guid variante, long milesimas, long? costo = null, Guid? id = null) =>
        e.Http.PostAsJsonAsync("api/inventario/agregar",
            new SolicitudAgregarInventario(id ?? Ids.Nuevo(), quien.UsuarioId, e.Sucursal, variante, milesimas, costo, e.Caja.Id));

    private static async Task<ProductoParaAgregarDto> ParaAgregarAsync(Escenario e, SesionDto quien, Guid variante) =>
        await Leer<ProductoParaAgregarDto>(await e.Http.GetAsync($"api/sucursales/{e.Sucursal}/inventario/agregar/{variante}?usuarioId={quien.UsuarioId}"));

    private static async Task<AgregadoHoyDto> HoyAsync(Escenario e, SesionDto quien) =>
        await Leer<AgregadoHoyDto>(await e.Http.GetAsync($"api/sucursales/{e.Sucursal}/inventario/agregado-hoy?usuarioId={quien.UsuarioId}"));

    private async Task<Existencia> ExistenciaAsync(Guid variante)
    {
        await using var db = servicio.Base();
        return await db.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == variante);
    }

    private static DateOnly HoyEnMexico => DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));

    /// <summary>Le pone al usuario ese nivel de un permiso (en la base, como lo dejaría «Usuarios y permisos»).</summary>
    private async Task NivelAsync(SesionDto quien, AccionControlada accion, NivelPermiso nivel)
    {
        await using var db = servicio.Base();
        (await db.PermisosUsuario.SingleAsync(p => p.UsuarioId == quien.UsuarioId && p.Accion == accion)).Nivel = nivel;
        await db.SaveChangesAsync();
    }

    /// <summary>El mismo servicio, con la lectura de eleventa prendida (sin servidor configurado: no lee nada).</summary>
    private WebApplicationFactory<Program> Piloto() => servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));

    [Fact]
    public async Task Agrega_a_la_existencia_con_el_promedio_como_una_compra_su_movimiento_la_bitacora_y_la_version()
    {
        var e = await PrepararAsync();
        var agregado = await ProductoAsync(e, "Agregar promedio de prueba", 10, 1000);
        var comprado = await ProductoAsync(e, "Comprar promedio de prueba", 10, 1000);
        long versionAntes;
        await using (var db = servicio.Base())
            versionAntes = await db.Secuencias.Where(s => s.Nombre == Secuencia.Existencias).Select(s => s.Valor).SingleAsync();
        var id = Ids.Nuevo();

        // Llegaron 10 a $13.00 cada uno: con «Agregar inventario» y, al otro, con una compra de lo mismo.
        var hecho = await Leer<AgregadoDto>(await AgregarAsync(e, e.Encargado, Variante(agregado), 10_000, 1300, id));
        var proveedor = (await e.Http.GetFromJsonAsync<List<ProveedorDto>>("api/proveedores"))!.First();
        var compra = await Leer<CompraDto>(await e.Http.PostAsJsonAsync("api/compras", new SolicitudAbrirCompra(e.Encargado.UsuarioId, e.Sucursal, proveedor.Id!.Value, null)));
        var pieza = comprado.Variantes[0].Presentaciones[0].Id!.Value;
        await Leer<CompraDto>(await e.Http.PostAsJsonAsync($"api/compras/{compra.Id}/conteo", new SolicitudConteo(e.Encargado.UsuarioId, Variante(comprado), pieza, 10_000)));
        await Leer<CompraDto>(await e.Http.PostAsJsonAsync($"api/compras/{compra.Id}/terminar-conteo", new SolicitudCompra(e.Encargado.UsuarioId)));
        await Leer<CompraDto>(await e.Http.PostAsJsonAsync($"api/compras/{compra.Id}/renglones",
            new SolicitudRenglonCompra(e.Encargado.UsuarioId, null, Variante(comprado), pieza, 10_000, 13_000)));
        await Leer<CompraDto>(await e.Http.PostAsJsonAsync($"api/compras/{compra.Id}/cerrar", new SolicitudCompra(e.Encargado.UsuarioId)));

        // Lo que regresa: lo agregado, con quién, el costo (el encargado ve costos) y lo que quedó.
        Assert.Equal((id, 10m, "Encargado de prueba", 1300L, 20m, false),
            (hecho.Entrada.Id, hecho.Entrada.Cantidad, hecho.Entrada.Usuario, hecho.Entrada.CostoUnitarioCentavos!.Value, hecho.Existencia, hecho.YaEstaba));
        Assert.Equal("Agregar promedio de prueba", hecho.Entrada.Producto);
        // El promedio, como lo deja la compra: (10 × $10 + 10 × $13) / 20 = $11.50.
        var existencia = await ExistenciaAsync(Variante(agregado));
        var deLaCompra = await ExistenciaAsync(Variante(comprado));
        Assert.Equal((Cantidad.Piezas(20), 23_000L), (existencia.Cantidad, existencia.CostoCentavos));
        Assert.Equal((deLaCompra.Cantidad, deLaCompra.CostoCentavos), (existencia.Cantidad, existencia.CostoCentavos));
        Assert.Equal(1150m, existencia.CostoPromedio);

        await using var lectura = servicio.Base();
        // Su movimiento del kardex: «Agregar inventario» (no una compra), con lo que vale y su documento.
        var movimiento = await lectura.MovimientosInventario.AsNoTracking().SingleAsync(m => m.VarianteId == Variante(agregado) && m.Tipo != TipoMovimiento.InventarioInicial);
        Assert.Equal((TipoMovimiento.AgregarInventario, Cantidad.Piezas(10), 13_000L, nameof(EntradaInventario), (Guid?)id),
            (movimiento.Tipo, movimiento.Cantidad, movimiento.CostoCentavos, movimiento.DocumentoTipo, movimiento.DocumentoId));
        var entrada = await lectura.EntradasInventario.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal((Cantidad.Piezas(10), 1300L, 13_000L, true, e.Encargado.UsuarioId), (entrada.Cantidad, entrada.CostoUnitarioCentavos, entrada.CostoCentavos,
            entrada.CostoEscrito, entrada.UsuarioId));
        // La bitácora: quién, qué, cuántos y el costo de antes y el de después.
        var bitacora = await lectura.Bitacora.AsNoTracking().SingleAsync(b => b.Accion == AccionControlada.AgregarInventario && b.Detalle.Contains("Agregar promedio de prueba"));
        Assert.Equal("Agregó 10 «Agregar promedio de prueba» (había 10, quedan 20). Costo: $10.00 → $11.50", bitacora.Detalle);
        Assert.Equal(((Guid?)e.Encargado.UsuarioId, (Guid?)e.Caja.Id, (long?)13_000, ResultadoBitacora.Hecho),
            (bitacora.UsuarioId, bitacora.CajaId, bitacora.MontoCentavos, bitacora.Resultado));
        // La existencia nueva lleva una versión de las existencias más nueva: así llega a las cajas con su aviso.
        var version = await lectura.Secuencias.Where(s => s.Nombre == Secuencia.Existencias).Select(s => s.Valor).SingleAsync();
        Assert.True(existencia.Version > versionAntes, $"La versión de la existencia ({existencia.Version}) no subió de {versionAntes}.");
        Assert.True(version >= existencia.Version);
        Assert.Empty(await new RegistroInventario(lectura).VerificarExistenciasAsync());
    }

    [Fact]
    public async Task Con_el_mismo_Id_se_agrega_una_sola_vez_tambien_a_la_vez()
    {
        var e = await PrepararAsync();
        var producto = await ProductoAsync(e, "Agregar una vez de prueba", 2, 500);
        var id = Ids.Nuevo();

        var primera = await Leer<AgregadoDto>(await AgregarAsync(e, e.Cajero, Variante(producto), 5_000, id: id));
        // Se perdió la respuesta y la caja la manda otra vez: regresa lo que ya se agregó.
        var otraVez = await Leer<AgregadoDto>(await AgregarAsync(e, e.Cajero, Variante(producto), 5_000, id: id));
        // Y tres a la vez con otro Id: una sola entra.
        var otro = Ids.Nuevo();
        var aLaVez = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => AgregarAsync(e, e.Cajero, Variante(producto), 1_000, id: otro)));
        var respuestas = new List<AgregadoDto>();
        foreach (var r in aLaVez)
            respuestas.Add(await Leer<AgregadoDto>(r));

        Assert.False(primera.YaEstaba);
        Assert.True(otraVez.YaEstaba);
        Assert.Equal((primera.Entrada.Id, primera.Entrada.Cantidad), (otraVez.Entrada.Id, otraVez.Entrada.Cantidad));
        Assert.Single(respuestas, r => !r.YaEstaba);
        Assert.All(respuestas, r => Assert.Equal(otro, r.Entrada.Id));
        Assert.Equal(Cantidad.Piezas(8), (await ExistenciaAsync(Variante(producto))).Cantidad);
        await using var db = servicio.Base();
        Assert.Equal(2, await db.EntradasInventario.CountAsync(x => x.VarianteId == Variante(producto)));
        Assert.Equal(2, await db.MovimientosInventario.CountAsync(m => m.VarianteId == Variante(producto) && m.Tipo == TipoMovimiento.AgregarInventario));
        Assert.Equal(2, await db.Bitacora.CountAsync(b => b.Accion == AccionControlada.AgregarInventario && b.Detalle.Contains("Agregar una vez de prueba")));
    }

    [Fact]
    public async Task Quien_no_ve_los_costos_agrega_al_costo_de_ahora_y_si_manda_uno_se_rechaza()
    {
        var e = await PrepararAsync();
        var producto = await ProductoAsync(e, "Agregar sin costos de prueba", 4, 1000);
        var dia = HoyEnMexico;

        var delCajero = await ParaAgregarAsync(e, e.Cajero, Variante(producto));
        var delEncargado = await ParaAgregarAsync(e, e.Encargado, Variante(producto));
        var conCosto = await AgregarAsync(e, e.Cajero, Variante(producto), 6_000, costo: 1500);
        var hecho = await Leer<AgregadoDto>(await AgregarAsync(e, e.Cajero, Variante(producto), 6_000));
        var hoyCajero = await HoyAsync(e, e.Cajero);
        var hoyEncargado = await HoyAsync(e, e.Encargado);

        // El cajero ve lo que hay, sin el costo; el encargado, también el costo de ahora (el promedio).
        Assert.Equal(("Agregar sin costos de prueba", (string?)null, 4m, UnidadBase.Pieza, (long?)null, false),
            (delCajero.Producto, delCajero.Variante, delCajero.Existencia, delCajero.Unidad, delCajero.CostoCentavos, delCajero.ConCostos));
        Assert.Equal(producto.Variantes[0].Presentaciones[0].Codigos[0].Codigo, delCajero.Codigo);
        Assert.Equal(((long?)1000, true, true), (delEncargado.CostoCentavos, delEncargado.Promedio, delEncargado.ConCostos));
        // El cajero no manda costo: el servicio lo rechaza, no solo la pantalla lo esconde.
        Assert.Equal(RegistroAgregarInventario.SinPermisoDelCosto, await RechazoAsync(conCosto, HttpStatusCode.Forbidden));
        // Sin costo, entra al de ahora: el promedio no se mueve.
        Assert.Equal((10m, (long?)null), (hecho.Existencia, hecho.Entrada.CostoUnitarioCentavos));
        var existencia = await ExistenciaAsync(Variante(producto));
        Assert.Equal((Cantidad.Piezas(10), 10_000L), (existencia.Cantidad, existencia.CostoCentavos));
        await using (var db = servicio.Base())
        {
            var entrada = await db.EntradasInventario.AsNoTracking().SingleAsync(x => x.VarianteId == Variante(producto));
            Assert.Equal((1000L, 6_000L, false), (entrada.CostoUnitarioCentavos, entrada.CostoCentavos, entrada.CostoEscrito));
            // Sin cambio del costo, la bitácora no lo dice.
            Assert.Equal("Agregó 6 «Agregar sin costos de prueba» (había 4, quedan 10)",
                await db.Bitacora.Where(b => b.Accion == AccionControlada.AgregarInventario && b.Detalle.Contains("Agregar sin costos de prueba"))
                    .Select(b => b.Detalle).SingleAsync());
        }
        // Lo agregado hoy: el costo, solo para quien lo ve.
        Assert.False(hoyCajero.ConCostos);
        Assert.True(hoyEncargado.ConCostos);
        if (HoyEnMexico == dia)
        {
            var delDia = Assert.Single(hoyCajero.Entradas, x => x.Id == hecho.Entrada.Id);
            Assert.Equal(("Agregar sin costos de prueba", 6m, "Cajero de prueba", (long?)null), (delDia.Producto, delDia.Cantidad, delDia.Usuario, delDia.CostoUnitarioCentavos));
            Assert.Equal(1000L, Assert.Single(hoyEncargado.Entradas, x => x.Id == hecho.Entrada.Id).CostoUnitarioCentavos);
        }
    }

    [Fact]
    public async Task Sin_existencia_el_costo_de_ahora_es_el_ultimo_que_se_conoce()
    {
        var e = await PrepararAsync();
        var producto = await ProductoAsync(e, "Agregar sin existencia de prueba", 0, 0);
        await using (var db = servicio.Base())
        {
            var proveedor = await db.Proveedores.FirstAsync();
            db.ProveedoresVariantes.Add(new ProveedorVariante
            {
                ProveedorId = proveedor.Id, VarianteId = Variante(producto), UltimoCostoCentavos = 900, UltimaCompraUtc = DateTime.UtcNow.AddDays(-30),
            });
            await db.SaveChangesAsync();
        }

        // Sin existencia: el último de un proveedor.
        var alPrincipio = await ParaAgregarAsync(e, e.Dueno, Variante(producto));
        // El dueño agrega 5 a $12.00; se venden las 5 y ya no hay existencia: el último es el de lo que agregó, más nuevo.
        await Leer<AgregadoDto>(await AgregarAsync(e, e.Dueno, Variante(producto), 5_000, 1200));
        await using (var db = servicio.Base())
        {
            var variante = await db.Variantes.SingleAsync(v => v.Id == Variante(producto));
            var inventario = new RegistroInventario(db);
            await inventario.RegistrarAsync(Kardex.Salida(e.Sucursal, variante, TipoMovimiento.Venta, Cantidad.Piezas(5),
                await inventario.ExistenciaAsync(e.Sucursal, variante.Id), 900, DateTime.UtcNow));
        }
        var sinExistencia = await ParaAgregarAsync(e, e.Dueno, Variante(producto));
        // El cajero agrega 3 sin ver costos: al último que se conoce.
        await Leer<AgregadoDto>(await AgregarAsync(e, e.Cajero, Variante(producto), 3_000));

        Assert.Equal(((long?)900, false, 0m), (alPrincipio.CostoCentavos, alPrincipio.Promedio, alPrincipio.Existencia));
        Assert.Equal(((long?)1200, false, 0m), (sinExistencia.CostoCentavos, sinExistencia.Promedio, sinExistencia.Existencia));
        var existencia = await ExistenciaAsync(Variante(producto));
        Assert.Equal((Cantidad.Piezas(3), 3_600L), (existencia.Cantidad, existencia.CostoCentavos));
        await using var lectura = servicio.Base();
        Assert.Equal("Agregó 3 «Agregar sin existencia de prueba» (había 0, quedan 3)",
            await lectura.Bitacora.Where(b => b.Accion == AccionControlada.AgregarInventario && b.Detalle.StartsWith("Agregó 3 «Agregar sin existencia"))
                .Select(b => b.Detalle).SingleAsync());
        Assert.Equal("Agregó 5 «Agregar sin existencia de prueba» (había 0, quedan 5). Costo: $9.00 → $12.00",
            await lectura.Bitacora.Where(b => b.Accion == AccionControlada.AgregarInventario && b.Detalle.StartsWith("Agregó 5 «Agregar sin existencia"))
                .Select(b => b.Detalle).SingleAsync());
    }

    [Fact]
    public async Task Sin_el_permiso_no_se_agrega_ni_se_consulta_y_es_puede_o_no()
    {
        var e = await PrepararAsync();
        var producto = await ProductoAsync(e, "Agregar sin permiso de prueba", 3, 700);
        Task<HttpResponseMessage> PonerAsync(NivelPermiso nivel) => e.Http.PutAsJsonAsync($"api/cajas/{e.Caja.Id}/usuarios-y-permisos/{e.Cajero.UsuarioId}",
            new SolicitudPermisoUsuario(e.Dueno.UsuarioId, AccionControlada.AgregarInventario, nivel));

        // En «Usuarios y permisos» es «Puede» o «No»: no se pide autorización.
        var conAutorizacion = await PonerAsync(NivelPermiso.RequiereAutorizacion);
        await Leer<UsuariosYPermisosDto>(await PonerAsync(NivelPermiso.NoPermitido));
        try
        {
            var agregar = await AgregarAsync(e, e.Cajero, Variante(producto), 1_000);
            var ver = await e.Http.GetAsync($"api/sucursales/{e.Sucursal}/inventario/agregar/{Variante(producto)}?usuarioId={e.Cajero.UsuarioId}");
            var hoy = await e.Http.GetAsync($"api/sucursales/{e.Sucursal}/inventario/agregado-hoy?usuarioId={e.Cajero.UsuarioId}");

            Assert.Equal("«Agregar inventario» es «Puede» o «No»: quien no puede le pide a quien sí que entre a la caja.",
                await RechazoAsync(conAutorizacion, HttpStatusCode.BadRequest));
            Assert.Equal("Esto lo hace quien tiene el permiso «Agregar inventario»: Encargado de prueba o Dueño de prueba.",
                await RechazoAsync(agregar, HttpStatusCode.Forbidden));
            Assert.Equal(HttpStatusCode.Forbidden, ver.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, hoy.StatusCode);
            Assert.Equal(Cantidad.Piezas(3), (await ExistenciaAsync(Variante(producto))).Cantidad);
        }
        finally
        {
            await Leer<UsuariosYPermisosDto>(await PonerAsync(NivelPermiso.Permitido));
        }
    }

    [Fact]
    public async Task En_el_piloto_no_se_agrega_y_el_permiso_va_primero()
    {
        var normal = await PrepararAsync();
        var producto = await ProductoAsync(normal, "Agregar en el piloto de prueba", 3, 700);
        await using var piloto = Piloto();
        var e = await PrepararAsync(piloto.CreateClient());

        var delEncargado = await AgregarAsync(e, e.Encargado, Variante(producto), 2_000, 800);
        var delCajero = await AgregarAsync(e, e.Cajero, Variante(producto), 2_000);
        await NivelAsync(e.Cajero, AccionControlada.AgregarInventario, NivelPermiso.NoPermitido);
        HttpResponseMessage sinPermiso;
        try
        {
            sinPermiso = await AgregarAsync(e, e.Cajero, Variante(producto), 2_000);
        }
        finally
        {
            await NivelAsync(e.Cajero, AccionControlada.AgregarInventario, NivelPermiso.Permitido);
        }

        Assert.Equal(TrastiendaEnElPiloto.InventarioEnEleventa, await RechazoAsync(delEncargado, HttpStatusCode.BadRequest));
        Assert.Equal(TrastiendaEnElPiloto.InventarioEnEleventa, await RechazoAsync(delCajero, HttpStatusCode.BadRequest));
        // Sin el permiso se oye lo del permiso antes que lo del piloto.
        Assert.Contains("«Agregar inventario»", await RechazoAsync(sinPermiso, HttpStatusCode.Forbidden));
        await using (var db = servicio.Base())
            Assert.False(await db.MovimientosInventario.AnyAsync(m => m.VarianteId == Variante(producto) && m.Tipo == TipoMovimiento.AgregarInventario));
        // Fuera del piloto, como siempre.
        Assert.Equal(5m, (await Leer<AgregadoDto>(await AgregarAsync(normal, normal.Cajero, Variante(producto), 2_000))).Existencia);
    }

    [Fact]
    public async Task Las_fracciones_segun_la_unidad_y_los_limites()
    {
        var e = await PrepararAsync();
        var piezas = await ProductoAsync(e, "Agregar por pieza de prueba", 1, 1000);
        var metros = await ProductoAsync(e, "Agregar por metro de prueba", 1.5m, 400, UnidadBase.Metro);
        var servicioSinInventario = await ProductoAsync(e, "Agregar un servicio de prueba", 0, 0, tipo: TipoProducto.Servicio);

        var mediaPieza = await AgregarAsync(e, e.Encargado, Variante(piezas), 2_500);
        var dosMetrosYMedio = await Leer<AgregadoDto>(await AgregarAsync(e, e.Encargado, Variante(metros), 2_500));
        var cero = await AgregarAsync(e, e.Encargado, Variante(piezas), 0);
        var negativo = await AgregarAsync(e, e.Encargado, Variante(piezas), -1_000);
        var demasiados = await AgregarAsync(e, e.Encargado, Variante(piezas), (EntradaInventario.CantidadMaxima + 1) * 1000L);
        var costoNegativo = await AgregarAsync(e, e.Encargado, Variante(piezas), 1_000, -1);
        var costoDeMas = await AgregarAsync(e, e.Encargado, Variante(piezas), 1_000, EntradaInventario.CostoMaximoCentavos + 1);
        var unServicio = await AgregarAsync(e, e.Encargado, Variante(servicioSinInventario), 1_000);
        var queNoExiste = await AgregarAsync(e, e.Encargado, Ids.Nuevo(), 1_000);
        var lomas = await Leer<AgregadoDto>(await AgregarAsync(e, e.Encargado, Variante(piezas), EntradaInventario.CantidadMaxima * 1000L, 1));

        Assert.Equal("«Agregar por pieza de prueba» se cuenta en piezas enteras.", await RechazoAsync(mediaPieza, HttpStatusCode.BadRequest));
        Assert.Equal((2.5m, 4m), (dosMetrosYMedio.Entrada.Cantidad, dosMetrosYMedio.Existencia));
        Assert.Equal(UnidadBase.Metro, dosMetrosYMedio.Entrada.Unidad);
        Assert.Equal("Escribe cuántos llegaron: más de cero.", await RechazoAsync(cero, HttpStatusCode.BadRequest));
        Assert.Equal("Escribe cuántos llegaron: más de cero.", await RechazoAsync(negativo, HttpStatusCode.BadRequest));
        Assert.Equal("De una vez se agregan hasta 99,999: revisa cuántos llegaron.", await RechazoAsync(demasiados, HttpStatusCode.BadRequest));
        Assert.Equal("Revisa el costo de cada uno: va de $0.00 a $100,000.00.", await RechazoAsync(costoNegativo, HttpStatusCode.BadRequest));
        Assert.Equal("Revisa el costo de cada uno: va de $0.00 a $100,000.00.", await RechazoAsync(costoDeMas, HttpStatusCode.BadRequest));
        Assert.Equal("«Agregar un servicio de prueba» es un servicio: no lleva inventario.", await RechazoAsync(unServicio, HttpStatusCode.BadRequest));
        Assert.Equal("El producto ya no existe.", await RechazoAsync(queNoExiste, HttpStatusCode.BadRequest));
        Assert.Equal(1m + EntradaInventario.CantidadMaxima, lomas.Existencia);
        // Lo rechazado no dejó nada.
        await using var db = servicio.Base();
        Assert.Equal(1, await db.EntradasInventario.CountAsync(x => x.VarianteId == Variante(piezas)));
        Assert.Empty(await new RegistroInventario(db).VerificarExistenciasAsync());
    }

    [Fact]
    public async Task Movimientos_su_excel_el_kardex_de_la_ficha_y_lo_que_vale_lo_dicen_agregar_inventario()
    {
        var e = await PrepararAsync();
        var producto = await ProductoAsync(e, "Agregar en movimientos de prueba", 5, 800);
        var url = $"api/sucursales/{e.Sucursal}/inventario";
        var antes = await Leer<ValorInventarioDto>(await e.Http.GetAsync($"{url}/valor?usuarioId={e.Dueno.UsuarioId}"));
        var dias = $"&desde={HoyEnMexico.AddDays(-1):yyyy-MM-dd}&hasta={HoyEnMexico:yyyy-MM-dd}&q={Uri.EscapeDataString("agregar en movimientos")}";

        await Leer<AgregadoDto>(await AgregarAsync(e, e.Dueno, Variante(producto), 7_000, 900));

        var movimientos = await Leer<MovimientosInventarioDto>(await e.Http.GetAsync($"{url}/movimientos?usuarioId={e.Dueno.UsuarioId}{dias}"));
        var deLaClase = await Leer<MovimientosInventarioDto>(await e.Http.GetAsync($"{url}/movimientos?usuarioId={e.Dueno.UsuarioId}{dias}&clase=AgregarInventario"));
        var comoCompra = await Leer<MovimientosInventarioDto>(await e.Http.GetAsync($"{url}/movimientos?usuarioId={e.Dueno.UsuarioId}{dias}&clase=Compra"));
        var delDueno = await Leer<MovimientosInventarioDto>(await e.Http.GetAsync($"{url}/movimientos?usuarioId={e.Dueno.UsuarioId}{dias}&quienId={e.Dueno.UsuarioId}"));
        var excel = await e.Http.GetAsync($"{url}/movimientos/excel?usuarioId={e.Dueno.UsuarioId}{dias}");
        var kardex = (await e.Http.GetFromJsonAsync<List<MovimientoKardexDto>>($"api/catalogo/variantes/{Variante(producto)}/kardex?sucursalId={e.Sucursal}"))!;
        var ultimos = (await e.Http.GetFromJsonAsync<List<MovimientoProductoDto>>($"api/catalogo/variantes/{Variante(producto)}/movimientos?sucursalId={e.Sucursal}"))!;
        var despues = await Leer<ValorInventarioDto>(await e.Http.GetAsync($"{url}/valor?usuarioId={e.Dueno.UsuarioId}"));

        // En Movimientos: «Agregar inventario», con cuántos, lo que quedó, quién y su costo; aparte de las compras.
        var agregado = movimientos.Movimientos[0];
        Assert.Equal((ClaseMovimiento.AgregarInventario, 7m, 12m, "Dueño de prueba", (string?)null, (long?)6_300),
            (agregado.Clase, agregado.Cantidad, agregado.Existencia, agregado.Quien, agregado.Documento, agregado.CostoCentavos));
        Assert.Equal([ClaseMovimiento.AgregarInventario, ClaseMovimiento.InventarioInicial], movimientos.Movimientos.Select(m => m.Clase));
        Assert.Equal("Agregar inventario", PapeleriaPos.Interfaz.Textos.Clase(agregado.Clase));
        Assert.Contains(PapeleriaPos.Interfaz.Textos.ClasesParaFiltrar, c => c.Clase == ClaseMovimiento.AgregarInventario && c.Nombre == "Agregar inventario");
        Assert.Equal([agregado.Id], deLaClase.Movimientos.Select(m => m.Id));
        Assert.Empty(comoCompra.Movimientos);
        Assert.Equal([agregado.Id], delDueno.Movimientos.Select(m => m.Id));
        // En su Excel, igual.
        Assert.True(excel.IsSuccessStatusCode, await excel.Content.ReadAsStringAsync());
        using (var libro = new XLWorkbook(new MemoryStream(await excel.Content.ReadAsByteArrayAsync())))
        {
            var tabla = libro.Worksheet("Movimientos").Tables.Single();
            var titulos = tabla.Fields.Select(f => f.Name).ToList();
            var renglon = tabla.DataRange.Rows().First();
            string Celda(string titulo) => renglon.Cell(titulos.IndexOf(titulo) + 1).GetFormattedString();
            Assert.Equal(("Agregar inventario", 7m, "Dueño de prueba"),
                (Celda("Movimiento"), (decimal)renglon.Cell(titulos.IndexOf("Cantidad") + 1).GetDouble(), Celda("Quién")));
        }
        // En el kardex de la ficha (con quién) y en los últimos movimientos del producto.
        Assert.Equal((TipoMovimiento.AgregarInventario, 7m, 12m, "Dueño de prueba"), (kardex[0].Tipo, kardex[0].Cantidad, kardex[0].Existencia, kardex[0].Quien));
        Assert.Equal((TipoMovimiento.AgregarInventario, 7m, 12m), (ultimos[0].Tipo, ultimos[0].Cantidad, ultimos[0].Existencia));
        // Lo que vale el inventario sube lo que vale lo que llegó.
        Assert.Equal(6_300L, despues.Total.ValorCentavos - antes.Total.ValorCentavos);
    }

    // ── Revisión del PR #138 ─────────────────────────────────────────────────

    /// <summary>El turno abierto de la caja, o uno nuevo del cajero, para vender.</summary>
    private static async Task<TurnoDto> TurnoAsync(Escenario e)
    {
        var respuesta = await e.Http.GetAsync($"api/cajas/{e.Caja.Id}/turno");
        return respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await e.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(e.Caja.Id, e.Cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
    }

    /// <summary>Una venta de verdad (api/ventas) de piezas del producto, en efectivo.</summary>
    private static async Task<HttpResponseMessage> VenderAsync(Escenario e, FichaProductoDto f, int piezas)
    {
        var turno = await TurnoAsync(e);
        return await e.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(e.Caja.Id, turno.Id, e.Cajero.UsuarioId,
            [new LineaSolicitada(Variante(f), f.Variantes[0].Presentaciones[0].Id!.Value, piezas * 1000L)], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)]));
    }

    /// <summary>
    /// Lo agregado con su costo es el «último costo» de todo el POS, no solo de esta pantalla (R1): un producto que solo entra por
    /// aquí, sin proveedor (como casi todo en la tienda), y se vende ya sin existencia, sale a ese costo; y lo que se agrega
    /// después «al costo de ahora» entra a ese mismo, sin inflar el promedio. No se inventa un proveedor.
    /// </summary>
    [Fact]
    public async Task Lo_agregado_con_su_costo_es_el_ultimo_costo_al_vender_sin_existencia()
    {
        var e = await PrepararAsync();
        var producto = await ProductoAsync(e, "Agregar último costo al vender de prueba", 0, 0);
        await servicio.CatalogoAlDiaAsync();

        // El dueño agrega 5 a $12.00; se venden las 5 y luego 1 más, ya sin existencia.
        await Leer<AgregadoDto>(await AgregarAsync(e, e.Dueno, Variante(producto), 5_000, 1200));
        await Leer<TicketDto>(await VenderAsync(e, producto, 5));
        await Leer<TicketDto>(await VenderAsync(e, producto, 1));
        var paraAgregar = await ParaAgregarAsync(e, e.Dueno, Variante(producto));
        // El cajero agrega 3 al costo de ahora.
        await Leer<AgregadoDto>(await AgregarAsync(e, e.Cajero, Variante(producto), 3_000));

        long costoDeLaPiezaSinExistencia;
        bool conProveedor;
        await using (var db = servicio.Base())
        {
            costoDeLaPiezaSinExistencia = (await db.MovimientosInventario.AsNoTracking()
                    .Where(m => m.VarianteId == Variante(producto) && m.Tipo == TipoMovimiento.Venta).ToListAsync())
                .Single(m => m.Cantidad == -Cantidad.Piezas(1)).CostoCentavos;
            conProveedor = await db.ProveedoresVariantes.AnyAsync(p => p.VarianteId == Variante(producto));
        }
        var existencia = await ExistenciaAsync(Variante(producto));

        Assert.Equal(((long?)1200, false), (paraAgregar.CostoCentavos, paraAgregar.Promedio));
        // La pieza vendida sin existencia salió a $12.00, y quedan 2 a $12.00 (no a $18.00).
        Assert.Equal((-1200L, Cantidad.Piezas(2), 2400L), (costoDeLaPiezaSinExistencia, existencia.Cantidad, existencia.CostoCentavos));
        Assert.False(conProveedor);
    }

    /// <summary>
    /// La corrección que pide la página («corrígelo con un ajuste») también ve el costo de lo agregado (R1b): un ajuste hacia arriba
    /// sin existencia entra a ese costo, no a $0.
    /// </summary>
    [Fact]
    public async Task Un_ajuste_sin_existencia_entra_al_costo_de_lo_ultimo_agregado()
    {
        var e = await PrepararAsync();
        var producto = await ProductoAsync(e, "Agregar último costo al ajustar de prueba", 0, 0);

        await Leer<AgregadoDto>(await AgregarAsync(e, e.Dueno, Variante(producto), 10_000, 1200));
        // Se contó y no había ninguna; luego aparecieron 2.
        await Leer<AjusteInventarioDto>(await e.Http.PostAsJsonAsync("api/inventario/ajustes",
            new SolicitudAjuste(e.Dueno.UsuarioId, e.Sucursal, Variante(producto), 0, "No había ninguna")));
        await Leer<AjusteInventarioDto>(await e.Http.PostAsJsonAsync("api/inventario/ajustes",
            new SolicitudAjuste(e.Dueno.UsuarioId, e.Sucursal, Variante(producto), 2_000, "Aparecieron 2")));

        var existencia = await ExistenciaAsync(Variante(producto));
        Assert.Equal((Cantidad.Piezas(2), 2400L), (existencia.Cantidad, existencia.CostoCentavos));
        Assert.Equal((long?)1200, (await ParaAgregarAsync(e, e.Dueno, Variante(producto))).CostoCentavos);
    }

    /// <summary>
    /// Lo que el cajero agregó a $0 porque no había ningún costo no se vuelve un costo conocido de $0 (R2): a quien ve los costos se
    /// le sigue pidiendo escribirlo, y el «último costo» del POS sigue vacío. Un costo escrito, aunque sea $0, sí cuenta.
    /// </summary>
    [Fact]
    public async Task Lo_agregado_a_cero_sin_costo_conocido_no_se_vuelve_un_costo_conocido()
    {
        var e = await PrepararAsync();
        var sinCosto = await ProductoAsync(e, "Agregar sin costo conocido de prueba", 0, 0);
        var regalado = await ProductoAsync(e, "Agregar regalado de prueba", 0, 0);

        var antes = await ParaAgregarAsync(e, e.Dueno, Variante(sinCosto));
        await Leer<AgregadoDto>(await AgregarAsync(e, e.Cajero, Variante(sinCosto), 5_000));
        var despues = await ParaAgregarAsync(e, e.Dueno, Variante(sinCosto));
        // El dueño escribe $0.00 (lo regaló el proveedor): eso sí es un costo.
        await Leer<AgregadoDto>(await AgregarAsync(e, e.Dueno, Variante(regalado), 5_000, 0));

        Assert.Equal(((long?)null, (long?)null, false), (antes.CostoCentavos, despues.CostoCentavos, despues.Promedio));
        await using var db = servicio.Base();
        Assert.Null(await UltimosCostos.DeAsync(db, Variante(sinCosto)));
        Assert.Equal(0L, await UltimosCostos.DeAsync(db, Variante(regalado)));
    }

    /// <summary>
    /// El mismo Id con otro contenido no es un reintento (R3): se rechaza con 409 y su código, dice qué se había mandado con ese Id
    /// y no agrega nada. Lo mismo otra vez sigue regresando lo que ya estaba.
    /// </summary>
    [Fact]
    public async Task El_mismo_Id_con_otro_producto_otra_cantidad_otro_costo_u_otro_usuario_se_rechaza_sin_agregar_nada()
    {
        var e = await PrepararAsync();
        var uno = await ProductoAsync(e, "Agregar Id uno de prueba", 1, 1000);
        var otro = await ProductoAsync(e, "Agregar Id otro de prueba", 1, 1000);
        var id = Ids.Nuevo();

        await Leer<AgregadoDto>(await AgregarAsync(e, e.Encargado, Variante(uno), 5_000, 900, id));
        var otroProducto = await AgregarAsync(e, e.Encargado, Variante(otro), 7_000, 900, id);
        var otraCantidad = await AgregarAsync(e, e.Encargado, Variante(uno), 6_000, 900, id);
        var otroCosto = await AgregarAsync(e, e.Encargado, Variante(uno), 5_000, 950, id);
        var sinCosto = await AgregarAsync(e, e.Encargado, Variante(uno), 5_000, null, id);
        var otroUsuario = await AgregarAsync(e, e.Cajero, Variante(uno), 5_000, null, id);
        var igual = await Leer<AgregadoDto>(await AgregarAsync(e, e.Encargado, Variante(uno), 5_000, 900, id));

        foreach (var rechazo in new[] { otroProducto, otraCantidad, otroCosto, sinCosto, otroUsuario })
        {
            Assert.Equal(HttpStatusCode.Conflict, rechazo.StatusCode);
            var error = (await rechazo.Content.ReadFromJsonAsync<ErrorDto>())!;
            Assert.Equal((SolicitudDistinta.Codigo, "Esto ya se había mandado con otro producto o con otra cantidad (+5 «Agregar Id uno de prueba»): "
                + "no se agregó nada. Escanea el producto y vuelve a escribir cuántos llegaron."), (error.Codigo, error.Mensaje));
        }
        Assert.True(igual.YaEstaba);
        Assert.Equal((Cantidad.Piezas(6), Cantidad.Piezas(1)), ((await ExistenciaAsync(Variante(uno))).Cantidad, (await ExistenciaAsync(Variante(otro))).Cantidad));
        await using var db = servicio.Base();
        Assert.Equal(1, await db.EntradasInventario.CountAsync(x => x.VarianteId == Variante(uno) || x.VarianteId == Variante(otro)));
    }

    /// <summary>Agregados a la vez (cajero y dueño, con y sin costo) y ventas a la vez del mismo producto: todo cuadra.</summary>
    [Fact]
    public async Task Agregados_y_ventas_a_la_vez_del_mismo_producto_cuadran()
    {
        var e = await PrepararAsync();
        var producto = await ProductoAsync(e, "Agregar a la vez con ventas de prueba", 10, 1000);
        await servicio.CatalogoAlDiaAsync();
        await TurnoAsync(e);

        var respuestas = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => AgregarAsync(e, i % 2 == 0 ? e.Cajero : e.Dueno, Variante(producto), 2_000, i % 2 == 0 ? null : 1300))
            .Concat(Enumerable.Range(0, 3).Select(_ => VenderAsync(e, producto, 1))));
        foreach (var r in respuestas)
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());

        await using var db = servicio.Base();
        Assert.Equal(Cantidad.Piezas(10 + 16 - 3), (await db.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == Variante(producto))).Cantidad);
        Assert.Equal(8, await db.EntradasInventario.CountAsync(x => x.VarianteId == Variante(producto)));
        Assert.Empty(await new RegistroInventario(db).VerificarExistenciasAsync());
        Assert.Equal(8, await db.Bitacora.CountAsync(b => b.Accion == AccionControlada.AgregarInventario && b.Detalle.Contains("Agregar a la vez con ventas de prueba")));
    }
}
