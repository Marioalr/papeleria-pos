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
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Productos › «Nuevo producto» con «¿Cuántas hay?» (2026-11-21; en eleventa, «Hay»), desde el servicio: lo que ya hay de una variante
/// nueva (<see cref="VarianteFichaDto.HayMilesimas"/>) entra en la misma llamada y la misma transacción que el alta, como un «Agregar
/// inventario» (su entrada, su movimiento del kardex al costo escrito, la existencia, la bitácora y la versión de las existencias). Con
/// el permiso «Agregar inventario» en «Puede»; solo de mercancía y de una variante nueva; en el piloto con eleventa, no. Si lo que hay
/// no se puede agregar, el producto tampoco se da de alta: se guardan los dos o ninguno.
/// </summary>
public sealed class InventarioAlDarDeAltaApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
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

    private sealed record Escenario(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno, Guid Departamento)
    {
        public Guid Sucursal => Caja.SucursalId;
    }

    private async Task<Escenario> PrepararAsync(HttpClient? http = null)
    {
        http ??= servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        var departamento = (await http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        return new Escenario(http, caja, await Entrar("cajero", "1111"), await Entrar("encargado", "2222"), await Entrar("dueno", "3333"), departamento);
    }

    /// <summary>Una variante nueva por pieza (o la unidad), con su código y a $20, con lo que hay y su costo.</summary>
    private static VarianteFichaDto Variante(string descripcion, long? hay, long? costo, UnidadBase unidad = UnidadBase.Pieza) =>
        new(null, descripcion, unidad, true,
            [new PresentacionFichaDto(null, unidad == UnidadBase.Pieza ? "Pieza" : "Metro", 1000, true, [new CodigoFichaDto($"HA{Guid.NewGuid():N}"[..14])])],
            [new PrecioFichaDto(null, 1000, 2000)], CostoCentavos: costo, HayMilesimas: hay);

    private static FichaProductoDto Ficha(Escenario e, string nombre, TipoProducto tipo, params VarianteFichaDto[] variantes) =>
        new(null, nombre, e.Departamento, null, tipo, Impuesto.Iva16, null, true, false, variantes);

    private static Task<HttpResponseMessage> GuardarAsync(Escenario e, SesionDto quien, FichaProductoDto ficha) =>
        e.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(quien.UsuarioId, e.Sucursal, ficha));

    private async Task<bool> ExisteAsync(string nombre)
    {
        await using var db = servicio.Base();
        return await db.Productos.AnyAsync(p => p.Nombre == nombre);
    }

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
    public async Task Lo_que_hay_entra_con_el_alta_como_agregar_inventario_con_su_costo_kardex_bitacora_y_version()
    {
        var e = await PrepararAsync();
        var nombre = $"Alta con hay {Guid.NewGuid():N}"[..24];
        long versionAntes;
        await using (var db = servicio.Base())
            versionAntes = await db.Secuencias.Where(s => s.Nombre == Secuencia.Existencias).Select(s => s.Valor).SingleAsync();

        var ficha = await Leer<FichaProductoDto>(await GuardarAsync(e, e.Encargado, Ficha(e, nombre, TipoProducto.Mercancia, Variante("", 12_000, 2850))));

        // La ficha que regresa ya dice lo que hay.
        var variante = Assert.Single(ficha.Variantes);
        Assert.Equal(12m, variante.Existencia);
        Assert.Null(variante.HayMilesimas);
        var id = variante.Id!.Value;
        await using var lectura = servicio.Base();
        var entrada = await lectura.EntradasInventario.AsNoTracking().SingleAsync(x => x.VarianteId == id);
        Assert.Equal((Cantidad.Piezas(12), 2850L, 34_200L, true, e.Encargado.UsuarioId, e.Sucursal),
            (entrada.Cantidad, entrada.CostoUnitarioCentavos, entrada.CostoCentavos, entrada.CostoEscrito, entrada.UsuarioId, entrada.SucursalId));
        // Su movimiento del kardex: «Agregar inventario», el único, con lo que vale y su documento.
        var movimiento = await lectura.MovimientosInventario.AsNoTracking().SingleAsync(m => m.VarianteId == id);
        Assert.Equal((TipoMovimiento.AgregarInventario, Cantidad.Piezas(12), 34_200L, nameof(EntradaInventario), (Guid?)entrada.Id),
            (movimiento.Tipo, movimiento.Cantidad, movimiento.CostoCentavos, movimiento.DocumentoTipo, movimiento.DocumentoId));
        var existencia = await lectura.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == id);
        Assert.Equal((Cantidad.Piezas(12), 34_200L), (existencia.Cantidad, existencia.CostoCentavos));
        Assert.True(existencia.Version > versionAntes, $"La versión de la existencia ({existencia.Version}) no subió de {versionAntes}.");
        // La bitácora: el alta y lo agregado, con quién y lo que vale.
        Assert.True(await lectura.Bitacora.AnyAsync(b => b.Accion == AccionControlada.EditarCatalogo && b.Detalle == $"Alta de «{nombre}»"));
        var agregado = await lectura.Bitacora.AsNoTracking().SingleAsync(b => b.Accion == AccionControlada.AgregarInventario && b.Detalle.Contains(nombre));
        Assert.StartsWith($"Agregó 12 «{nombre}» (había 0, quedan 12)", agregado.Detalle);
        Assert.Equal(((Guid?)e.Encargado.UsuarioId, (long?)34_200), (agregado.UsuarioId, agregado.MontoCentavos));
        // Y su costo es el escrito.
        Assert.Equal(2850, ficha.Variantes[0].CostoCentavos);
        Assert.Empty(await new RegistroInventario(lectura).VerificarExistenciasAsync());
    }

    [Fact]
    public async Task Con_varias_variantes_cada_una_con_lo_suyo_y_sin_hay_ninguna_entrada()
    {
        var e = await PrepararAsync();
        var nombre = $"Alta de colores {Guid.NewGuid():N}"[..26];

        var ficha = await Leer<FichaProductoDto>(await GuardarAsync(e, e.Encargado,
            Ficha(e, nombre, TipoProducto.Mercancia, Variante("Azul", 4_000, 1000), Variante("Rojo", null, 1000), Variante("Verde", 0, 1000),
                Variante("Negro", 9_000, 1100))));

        Assert.Equal([("Azul", 4m), ("Negro", 9m), ("Rojo", 0m), ("Verde", 0m)],
            ficha.Variantes.OrderBy(v => v.Descripcion).Select(v => (v.Descripcion, v.Existencia ?? 0m)));
        await using var db = servicio.Base();
        var ids = ficha.Variantes.Select(v => v.Id!.Value).ToList();
        Assert.Equal(2, await db.EntradasInventario.CountAsync(x => ids.Contains(x.VarianteId)));
        Assert.Equal(2, await db.MovimientosInventario.CountAsync(m => ids.Contains(m.VarianteId) && m.Tipo == TipoMovimiento.AgregarInventario));
        var negro = ficha.Variantes.Single(v => v.Descripcion == "Negro").Id!.Value;
        Assert.Equal(9_900L, (await db.EntradasInventario.SingleAsync(x => x.VarianteId == negro)).CostoCentavos);
    }

    [Fact]
    public async Task Sin_el_permiso_de_agregar_inventario_no_se_da_de_alta_nada_y_sin_hay_si()
    {
        var e = await PrepararAsync();
        var conHay = $"Alta sin permiso {Guid.NewGuid():N}"[..26];
        var sinHay = $"Alta sin hay {Guid.NewGuid():N}"[..24];
        await NivelAsync(e.Encargado, AccionControlada.AgregarInventario, NivelPermiso.NoPermitido);
        try
        {
            var rechazo = await GuardarAsync(e, e.Encargado, Ficha(e, conHay, TipoProducto.Mercancia, Variante("", 5_000, 1000)));
            Assert.StartsWith("Esto lo hace quien tiene el permiso «Agregar inventario»", await RechazoAsync(rechazo, HttpStatusCode.Forbidden));
            Assert.False(await ExisteAsync(conHay));
            // Sin lo que hay, el alta es la de siempre.
            await Leer<FichaProductoDto>(await GuardarAsync(e, e.Encargado, Ficha(e, sinHay, TipoProducto.Mercancia, Variante("", null, 1000))));
            Assert.True(await ExisteAsync(sinHay));
        }
        finally
        {
            await NivelAsync(e.Encargado, AccionControlada.AgregarInventario, NivelPermiso.Permitido);
        }
        // Y quien no edita el catálogo oye lo del catálogo, antes que lo del inventario.
        var delCajero = await GuardarAsync(e, e.Cajero, Ficha(e, conHay, TipoProducto.Mercancia, Variante("", 5_000, null)));
        Assert.Equal("El catálogo lo editan el dueño y el encargado.", await RechazoAsync(delCajero, HttpStatusCode.Forbidden));
        Assert.False(await ExisteAsync(conHay));
    }

    [Fact]
    public async Task En_el_piloto_con_eleventa_no_se_da_de_alta_con_lo_que_hay()
    {
        await using var piloto = Piloto();
        var e = await PrepararAsync(piloto.CreateClient());
        var conHay = $"Alta piloto hay {Guid.NewGuid():N}"[..26];
        var sinHay = $"Alta piloto sin {Guid.NewGuid():N}"[..26];

        var rechazo = await GuardarAsync(e, e.Encargado, Ficha(e, conHay, TipoProducto.Mercancia, Variante("", 5_000, 1000)));

        Assert.Equal(TrastiendaEnElPiloto.InventarioEnEleventa, await RechazoAsync(rechazo, HttpStatusCode.BadRequest));
        Assert.False(await ExisteAsync(conHay));
        // El alta sin lo que hay, como siempre (lo dado de alta aquí va a eleventa como artículo común).
        await Leer<FichaProductoDto>(await GuardarAsync(e, e.Encargado, Ficha(e, sinHay, TipoProducto.Mercancia, Variante("", null, 1000))));
        Assert.True(await ExisteAsync(sinHay));
    }

    [Fact]
    public async Task Si_lo_que_hay_no_se_puede_agregar_tampoco_queda_el_alta()
    {
        var e = await PrepararAsync();
        async Task<string> RechazadoAsync(string nombre, TipoProducto tipo, params VarianteFichaDto[] variantes)
        {
            var mensaje = await RechazoAsync(await GuardarAsync(e, e.Encargado, Ficha(e, nombre, tipo, variantes)), HttpStatusCode.BadRequest);
            Assert.False(await ExisteAsync(nombre), $"«{nombre}» se dio de alta sin lo que hay.");
            return mensaje;
        }

        // Media pieza: lo revisa «Agregar inventario» después de guardar el alta, dentro de la transacción.
        var media = $"Alta media pieza {Guid.NewGuid():N}"[..26];
        Assert.Equal($"«{media}» se cuenta en piezas enteras.", await RechazadoAsync(media, TipoProducto.Mercancia, Variante("", 2_500, 1000)));
        // La segunda variante no se puede: la primera tampoco queda.
        var segunda = $"Alta segunda mal {Guid.NewGuid():N}"[..26];
        Assert.Equal("Escribe cuántos llegaron: más de cero.",
            await RechazadoAsync(segunda, TipoProducto.Mercancia, Variante("Azul", 3_000, 1000), Variante("Rojo", -1_000, 1000)));
        var tope = $"Alta de más {Guid.NewGuid():N}"[..24];
        Assert.StartsWith("De una vez se agregan hasta 99,999", await RechazadoAsync(tope, TipoProducto.Mercancia, Variante("", 100_000_000, 1000)));
        // Un servicio no lleva inventario.
        var servicioConHay = $"Alta servicio hay {Guid.NewGuid():N}"[..26];
        Assert.Equal("Un servicio no lleva inventario: quita lo que escribiste en «¿Cuántas hay?».",
            await RechazadoAsync(servicioConHay, TipoProducto.Servicio, Variante("", 1_000, null)));
        await using var db = servicio.Base();
        Assert.False(await db.Variantes.AnyAsync(v => v.Producto!.Nombre == segunda));
        // En metros sí van fracciones.
        var metros = $"Alta en metros {Guid.NewGuid():N}"[..24];
        var ficha = await Leer<FichaProductoDto>(await GuardarAsync(e, e.Encargado, Ficha(e, metros, TipoProducto.Mercancia, Variante("", 2_500, 400, UnidadBase.Metro))));
        Assert.Equal(2.5m, ficha.Variantes[0].Existencia);
    }

    [Fact]
    public async Task Al_editar_lo_que_hay_no_se_escribe_en_la_ficha_sino_en_agregar_inventario()
    {
        var e = await PrepararAsync();
        var nombre = $"Editar con hay {Guid.NewGuid():N}"[..24];
        var ficha = await Leer<FichaProductoDto>(await GuardarAsync(e, e.Encargado, Ficha(e, nombre, TipoProducto.Mercancia, Variante("", 2_000, 1000))));

        var cambiada = ficha with { Variantes = [ficha.Variantes[0] with { HayMilesimas = 5_000, CostoCentavos = null }] };
        Assert.Equal("Lo que ya hay se escribe al darlo de alta; después, en Inventario › Agregar inventario.",
            await RechazoAsync(await GuardarAsync(e, e.Encargado, cambiada), HttpStatusCode.BadRequest));
        await using var db = servicio.Base();
        Assert.Equal(Cantidad.Piezas(2), (await db.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == ficha.Variantes[0].Id)).Cantidad);
    }
}
