using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// Lo que se señala (entrega 13) en la base: lo que deja la migración (lo que dijo el dueño el 2026-10-15), los cambios con
/// «Productos, precios y etiquetas», y que cada cambio sube la versión del catálogo (va en la copia de las cajas), con lo
/// quitado anotado para los cambios de la copia.
/// </summary>
public class MostradorTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _cajera = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private readonly Usuario _dueno = new() { Nombre = "Don", NombreUsuario = "don", Rol = Rol.Dueno };
    private readonly Variante _roja = new() { ProductoId = Guid.Empty, Descripcion = "Roja" };
    private readonly Variante _negra = new() { ProductoId = Guid.Empty, Descripcion = "Negra" };

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _cajera.EstablecerPin("1111");
        _dueno.EstablecerPin("3333");
        (_roja.ProductoId, _negra.ProductoId) = (_catalogo.Pluma.Id, _catalogo.Pluma.Id);
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        db.AddRange(_cajera, _dueno, _roja, _negra);
        await db.SaveChangesAsync();
        await MigracionPermisos.AplicarAsync(db);
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private Guid Sucursal => _catalogo.Matriz.Id;

    private async Task<T> ConAsync<T>(Func<RegistroMostrador, Task<T>> hacer)
    {
        await using var db = _base.Contexto();
        return await hacer(new RegistroMostrador(db));
    }

    private async Task<long> VersionAsync()
    {
        await using var db = _base.Contexto();
        return await db.Secuencias.Where(s => s.Nombre == Secuencia.Catalogo).Select(s => s.Valor).FirstOrDefaultAsync();
    }

    private static ExhibidorDto Del(MostradorDto m, string nombre) => m.Exhibidores.Single(e => e.Nombre == nombre);

    [Fact]
    public async Task La_migracion_deja_el_mostrador_y_las_temporadas_del_duenio()
    {
        var mostrador = await ConAsync(r => r.LeerAsync());

        // Primero el exhibidor principal, con sus dos repisas vacías; luego las temporadas, cada una con la mesa de la entrada.
        Assert.Equal(["Mostrador", "15 de septiembre", "Todos Santos", "Navidad", "Primavera"], mostrador.Exhibidores.Select(e => e.Nombre));
        var principal = mostrador.Exhibidores[0];
        Assert.Equal(TipoExhibidor.Exhibidor, principal.Tipo);
        Assert.Equal(["Repisa de arriba", "Repisa de abajo"], principal.Lugares.Select(l => l.Nombre));
        Assert.All(principal.Lugares, l => Assert.Empty(l.Productos));
        Assert.Equal((new DiaDelAnio(9, 1), new DiaDelAnio(9, 16)), (Del(mostrador, "15 de septiembre").Desde, Del(mostrador, "15 de septiembre").Hasta));
        Assert.Equal((new DiaDelAnio(9, 16), new DiaDelAnio(11, 5)), (Del(mostrador, "Todos Santos").Desde, Del(mostrador, "Todos Santos").Hasta));
        Assert.Equal((new DiaDelAnio(11, 20), new DiaDelAnio(1, 1)), (Del(mostrador, "Navidad").Desde, Del(mostrador, "Navidad").Hasta));
        Assert.Equal((null, null), (Del(mostrador, "Primavera").Desde, Del(mostrador, "Primavera").Hasta));
        Assert.All(mostrador.Exhibidores.Skip(1), t =>
        {
            Assert.Equal(TipoExhibidor.Temporada, t.Tipo);
            Assert.Equal(["Mesa de la entrada"], t.Lugares.Select(l => l.Nombre));
        });
        // Navidad cruza el fin de año.
        Assert.True(Del(mostrador, "Navidad").CorreEl(new DateOnly(2027, 1, 1)));
        Assert.False(Del(mostrador, "Primavera").CorreEl(new DateOnly(2027, 4, 1)));
    }

    [Fact]
    public async Task Cada_cambio_sube_la_version_del_catalogo_y_lo_quitado_queda_anotado()
    {
        var antes = await VersionAsync();
        var mostrador = await ConAsync(r => r.LeerAsync());
        var arriba = mostrador.Exhibidores[0].Lugares[0];
        var abajo = mostrador.Exhibidores[0].Lugares[1];

        var nuevo = await ConAsync(r => r.NuevoAsync(new SolicitudNuevoExhibidor(_dueno.Id, Sucursal, " Regreso   a clases ", TipoExhibidor.Temporada)));
        Assert.Equal(antes + 1, await VersionAsync());
        var regreso = Del(nuevo, "Regreso a clases");
        await ConAsync(r => r.CambiarAsync(regreso.Id, new SolicitudCambiarExhibidor(_dueno.Id, Sucursal,
            Fechas: new FechasTemporadaDto(new DiaDelAnio(7, 15), new DiaDelAnio(8, 31)))));
        await ConAsync(r => r.AgregarLugarAsync(regreso.Id, new SolicitudLugar(_dueno.Id, Sucursal, "Mesa de la entrada")));
        var acomodado = await ConAsync(r => r.AcomodarAsync(new SolicitudAcomodo(_dueno.Id, Sucursal,
            [new LugarConProductosDto(arriba.Id, [_roja.Id, _catalogo.Azul.Id]), new LugarConProductosDto(abajo.Id, [_negra.Id])])));
        Assert.Equal(antes + 4, await VersionAsync());
        Assert.Equal([_roja.Id, _catalogo.Azul.Id], acomodado.Exhibidores[0].Lugares[0].Productos);
        Assert.Equal([_negra.Id], acomodado.Exhibidores[0].Lugares[1].Productos);
        Assert.Equal(antes + 4, acomodado.Version);

        // Cambiar el orden, pasar uno a la otra repisa y quitar otro: una versión, y lo quitado anotado.
        await ConAsync(r => r.AcomodarAsync(new SolicitudAcomodo(_dueno.Id, Sucursal,
            [new LugarConProductosDto(arriba.Id, [_catalogo.Azul.Id]), new LugarConProductosDto(abajo.Id, [_negra.Id, _roja.Id])])));
        Assert.Equal(antes + 5, await VersionAsync());
        await using (var db = _base.Contexto())
        {
            var posiciones = await db.ProductosExhibidos.AsNoTracking().ToListAsync();
            Assert.Equal(3, posiciones.Count);
            Assert.Equal(antes + 5, posiciones.Single(p => p.VarianteId == _roja.Id).Version);
            Assert.Equal(1, posiciones.Single(p => p.VarianteId == _catalogo.Azul.Id).Posicion);
            var quitado = Assert.Single(await db.Borrados.AsNoTracking().Where(b => b.Entidad == nameof(ProductoExhibido)).ToListAsync());
            Assert.Equal(antes + 5, quitado.Version);
            // No dice que cambió un producto del catálogo.
            Assert.Null(quitado.VarianteId);
        }

        // Lo que no cambia nada no sube la versión: el mismo nombre, las mismas fechas, el mismo acomodo.
        await ConAsync(r => r.CambiarAsync(regreso.Id, new SolicitudCambiarExhibidor(_dueno.Id, Sucursal, Nombre: "Regreso  a clases",
            Fechas: new FechasTemporadaDto(new DiaDelAnio(7, 15), new DiaDelAnio(8, 31)))));
        await ConAsync(r => r.AcomodarAsync(new SolicitudAcomodo(_dueno.Id, Sucursal, [new LugarConProductosDto(arriba.Id, [_catalogo.Azul.Id])])));
        Assert.Equal(antes + 5, await VersionAsync());

        // Quitarla la desactiva: ya no sale, y lo acomodado en ella no se borra.
        var sinElla = await ConAsync(r => r.QuitarAsync(regreso.Id, new SolicitudMostrador(_dueno.Id, Sucursal)));
        Assert.DoesNotContain(sinElla.Exhibidores, e => e.Id == regreso.Id);
        Assert.Equal(antes + 6, await VersionAsync());
        await using (var db = _base.Contexto())
            Assert.False((await db.Exhibidores.SingleAsync(e => e.Id == regreso.Id)).Activo);
    }

    [Fact]
    public async Task Solo_con_productos_precios_y_etiquetas_y_lo_del_exhibidor_queda_en_la_bitacora()
    {
        var mostrador = await ConAsync(r => r.LeerAsync());
        var principal = mostrador.Exhibidores[0];
        var antes = await VersionAsync();

        await Assert.ThrowsAsync<PermisoRequerido>(() => ConAsync(r => r.NuevoAsync(new SolicitudNuevoExhibidor(_cajera.Id, Sucursal, "La pared", TipoExhibidor.Exhibidor))));
        await Assert.ThrowsAsync<PermisoRequerido>(() => ConAsync(r => r.CambiarAsync(principal.Id, new SolicitudCambiarExhibidor(_cajera.Id, Sucursal, "Vitrina"))));
        await Assert.ThrowsAsync<PermisoRequerido>(() => ConAsync(r => r.QuitarAsync(principal.Id, new SolicitudMostrador(_cajera.Id, Sucursal))));
        await Assert.ThrowsAsync<PermisoRequerido>(() => ConAsync(r => r.AgregarLugarAsync(principal.Id, new SolicitudLugar(_cajera.Id, Sucursal, "Gancho"))));
        await Assert.ThrowsAsync<PermisoRequerido>(() => ConAsync(r => r.QuitarLugarAsync(principal.Lugares[1].Id, new SolicitudMostrador(_cajera.Id, Sucursal))));
        await Assert.ThrowsAsync<PermisoRequerido>(() => ConAsync(r => r.AcomodarAsync(new SolicitudAcomodo(_cajera.Id, Sucursal,
            [new LugarConProductosDto(principal.Lugares[0].Id, [_roja.Id])]))));
        Assert.Equal(antes, await VersionAsync());

        await ConAsync(r => r.CambiarAsync(principal.Id, new SolicitudCambiarExhibidor(_dueno.Id, Sucursal, "Vitrina del mostrador")));
        await using var db = _base.Contexto();
        var renglon = Assert.Single(await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.EditarCatalogo).ToListAsync());
        Assert.Equal("Cambió el nombre de «Mostrador» a «Vitrina del mostrador» (Ctrl+M)", renglon.Detalle);
        Assert.Equal(_dueno.Id, renglon.UsuarioId);
    }

    [Fact]
    public async Task Lo_que_no_se_puede_dice_por_que()
    {
        var mostrador = await ConAsync(r => r.LeerAsync());
        var principal = mostrador.Exhibidores[0];
        var navidad = Del(mostrador, "Navidad");
        var arriba = principal.Lugares[0].Id;

        async Task<string> Error(Func<RegistroMostrador, Task<MostradorDto>> hacer) =>
            (await Assert.ThrowsAsync<ArgumentException>(() => ConAsync(hacer))).Message;

        Assert.Contains("Ya hay una temporada que se llama «Navidad»", await Error(r => r.NuevoAsync(new SolicitudNuevoExhibidor(_dueno.Id, Sucursal, "  navidád ", TipoExhibidor.Exhibidor))));
        Assert.Contains("Escribe el nombre", await Error(r => r.NuevoAsync(new SolicitudNuevoExhibidor(_dueno.Id, Sucursal, "   ", TipoExhibidor.Temporada))));
        Assert.Contains("va hasta 40 letras", await Error(r => r.NuevoAsync(new SolicitudNuevoExhibidor(_dueno.Id, Sucursal, new string('x', 41), TipoExhibidor.Temporada))));
        Assert.Contains("las dos fechas", await Error(r => r.CambiarAsync(navidad.Id, new SolicitudCambiarExhibidor(_dueno.Id, Sucursal,
            Fechas: new FechasTemporadaDto(new DiaDelAnio(11, 20), null)))));
        Assert.Contains("Solo las temporadas", await Error(r => r.CambiarAsync(principal.Id, new SolicitudCambiarExhibidor(_dueno.Id, Sucursal,
            Fechas: new FechasTemporadaDto(new DiaDelAnio(11, 20), new DiaDelAnio(1, 1))))));
        Assert.Contains("ya tiene un lugar que se llama «Repisa de abajo»", await Error(r => r.AgregarLugarAsync(principal.Id, new SolicitudLugar(_dueno.Id, Sucursal, "repisa de ABAJO"))));
        Assert.Contains("una sola vez en cada lugar", await Error(r => r.AcomodarAsync(new SolicitudAcomodo(_dueno.Id, Sucursal,
            [new LugarConProductosDto(arriba, [_roja.Id, _roja.Id])]))));
        Assert.Contains("ya no está en el catálogo", await Error(r => r.AcomodarAsync(new SolicitudAcomodo(_dueno.Id, Sucursal,
            [new LugarConProductosDto(arriba, [Guid.NewGuid()])]))));
        Assert.Contains("ya no está", await Error(r => r.AcomodarAsync(new SolicitudAcomodo(_dueno.Id, Sucursal,
            [new LugarConProductosDto(Guid.NewGuid(), [_roja.Id])]))));
        Assert.Contains("viene dos veces", await Error(r => r.AcomodarAsync(new SolicitudAcomodo(_dueno.Id, Sucursal,
            [new LugarConProductosDto(arriba, [_roja.Id]), new LugarConProductosDto(arriba, [_negra.Id])]))));
        // El mismo producto sí puede estar en dos lugares.
        var enDos = await ConAsync(r => r.AcomodarAsync(new SolicitudAcomodo(_dueno.Id, Sucursal,
            [new LugarConProductosDto(arriba, [_roja.Id]), new LugarConProductosDto(navidad.Lugares[0].Id, [_roja.Id])])));
        Assert.Equal([_roja.Id], Del(enDos, "Navidad").Lugares[0].Productos);
    }

    /// <summary>Quitar un lugar no pierde lo acomodado en él: pasa al final del lugar de al lado (sin repetir lo que ya estaba ahí).</summary>
    [Fact]
    public async Task Quitar_un_lugar_pasa_sus_productos_al_de_al_lado_y_subirlo_o_bajarlo_lo_reordena()
    {
        var principal = (await ConAsync(r => r.LeerAsync())).Exhibidores[0];
        var (arriba, abajo) = (principal.Lugares[0].Id, principal.Lugares[1].Id);
        var conMesa = await ConAsync(r => r.AgregarLugarAsync(principal.Id, new SolicitudLugar(_dueno.Id, Sucursal, "La mesa de la entrada")));
        var mesa = conMesa.Exhibidores[0].Lugares[2].Id;
        await ConAsync(r => r.AcomodarAsync(new SolicitudAcomodo(_dueno.Id, Sucursal,
            [new LugarConProductosDto(arriba, [_roja.Id]), new LugarConProductosDto(abajo, [_negra.Id, _roja.Id, _catalogo.Azul.Id])])));

        var movido = await ConAsync(r => r.MoverLugarAsync(mesa, new SolicitudMoverLugar(_dueno.Id, Sucursal, -1)));
        Assert.Equal(["Repisa de arriba", "La mesa de la entrada", "Repisa de abajo"], movido.Exhibidores[0].Lugares.Select(l => l.Nombre));
        var enLaOrilla = await ConAsync(r => r.MoverLugarAsync(arriba, new SolicitudMoverLugar(_dueno.Id, Sucursal, -1)));
        Assert.Equal(movido.Exhibidores[0].Lugares.Select(l => l.Id), enLaOrilla.Exhibidores[0].Lugares.Select(l => l.Id));

        // La repisa de abajo (la última) pasa lo suyo a la de arriba de ella: la mesa.
        var sinAbajo = await ConAsync(r => r.QuitarLugarAsync(abajo, new SolicitudMostrador(_dueno.Id, Sucursal)));
        Assert.Equal(["Repisa de arriba", "La mesa de la entrada"], sinAbajo.Exhibidores[0].Lugares.Select(l => l.Nombre));
        Assert.Equal([_negra.Id, _roja.Id, _catalogo.Azul.Id], sinAbajo.Exhibidores[0].Lugares[1].Productos);
        // La de arriba (la primera) pasa lo suyo a la de abajo de ella, sin repetir la roja.
        var sinArriba = await ConAsync(r => r.QuitarLugarAsync(arriba, new SolicitudMostrador(_dueno.Id, Sucursal)));
        var unico = Assert.Single(sinArriba.Exhibidores[0].Lugares);
        Assert.Equal([_negra.Id, _roja.Id, _catalogo.Azul.Id], unico.Productos);
        // El único lugar, con productos, no se quita.
        var error = await Assert.ThrowsAsync<ArgumentException>(() => ConAsync(r => r.QuitarLugarAsync(mesa, new SolicitudMostrador(_dueno.Id, Sucursal))));
        Assert.Contains("es el único lugar", error.Message);
        await using var db = _base.Contexto();
        Assert.Equal(2, await db.Borrados.CountAsync(b => b.Entidad == nameof(LugarExhibidor)));
    }
}
