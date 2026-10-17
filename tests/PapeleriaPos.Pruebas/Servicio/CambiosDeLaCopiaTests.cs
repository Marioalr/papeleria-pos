using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Solo lo que cambió, desde la versión de la copia de la caja (entrega 5, parte 2): <c>api/cajas/{id}/cambios</c> deja la
/// copia igual que si se hubiera bajado completa, con lo quitado y las existencias; lo que no puede dar desde ahí lo dice; y el
/// aviso por SSE manda las versiones al conectarse, cuando cambian y un «vivo» cada tanto, solo a su caja.
/// </summary>
public sealed class CambiosDeLaCopiaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Dueno);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Caja> CajaAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        // La primera (la serie A): otra prueba da de alta una caja más.
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        return new Caja(http, caja, cajero, dueno);
    }

    private static Task<PaqueteContingenciaDto> CopiaAsync(Caja c) => Leer<PaqueteContingenciaDto>(c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete").Result);

    /// <summary>Lo que cambió desde esa copia, como lo pide la caja: con las versiones de lo que usa su búsqueda (entrega 5, parte 3).</summary>
    private static async Task<CambiosCopiaDto> CambiosAsync(Caja c, PaqueteContingenciaDto desde, Guid? base_ = null, int formato = CopiaAlDia.Formato,
        long? version = null) =>
        await Leer<CambiosCopiaDto>(await c.Http.GetAsync(
            $"api/cajas/{c.Datos.Id}/cambios?desde={version ?? desde.Version}&existencias={desde.VersionExistencias}&base={base_ ?? desde.Base}&formato={formato}"
            + $"&vendidos={desde.VersionVendidos}&elegidos={desde.VersionElegidos}"));

    /// <summary>Lo que eligió la caja en la lista de la búsqueda con ese texto (lo que manda la venta).</summary>
    private static async Task ElegirAsync(Caja c, string texto, Guid varianteId) =>
        Assert.True((await c.Http.PostAsJsonAsync("api/busquedas", new SolicitudBusqueda(c.Datos.Id, texto, 3, varianteId))).IsSuccessStatusCode);

    private async Task<TurnoDto> TurnoAsync(Caja c)
    {
        var respuesta = await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/turno");
        return respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await c.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(c.Datos.Id, c.Cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
    }

    private async Task<TicketDto> VenderAsync(Caja c, ProductoVentaDto producto, int piezas)
    {
        var turno = await TurnoAsync(c);
        return await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, turno.Id, c.Cajero.UsuarioId,
            [new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, piezas * 1000)], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)])));
    }

    /// <summary>La copia en un texto comparable: cada producto por su variante (sin su versión), y lo demás tal cual.</summary>
    private static string Comparable(PaqueteContingenciaDto p)
    {
        var json = JsonSerializerOptions.Web;
        var productos = p.Productos.OrderBy(x => x.VarianteId).Select(x => JsonSerializer.Serialize(x with { Version = null }, json));
        var codigos = p.Codigos.Select(x => $"{x.Codigo}|{x.VarianteId}|{x.PresentacionId}").Order(StringComparer.Ordinal);
        var resto = p with { Productos = [], Codigos = [], Marcas = p.Marcas?.Order(StringComparer.Ordinal).ToList() };
        return string.Join("\n", productos.Concat(codigos).Append(JsonSerializer.Serialize(resto, json)));
    }

    private static List<ProductoVentaDto> DeLaCopia(PaqueteContingenciaDto p, Func<ProductoVentaDto, bool> filtro) =>
        p.Productos.Where(filtro).OrderBy(x => x.VarianteId).ToList();

    /// <summary>La foto de un producto, por la API, como la pone el dueño (entrega 13, parte 2).</summary>
    private static async Task<FotoGuardadaDto> FotoAsync(Caja c, Guid varianteId, byte[] imagen) =>
        await Leer<FotoGuardadaDto>(await c.Http.PutAsJsonAsync($"api/fotos/{varianteId}",
            new SolicitudFoto(c.Dueno.UsuarioId, c.Datos.SucursalId, Convert.ToBase64String(imagen))));

    /// <summary>
    /// Con cambios de todo tipo (precio, alta, código quitado, baja, marca, usuario, PIN mal tecleado, la tienda, un sinónimo,
    /// un cliente, una venta, lo que la caja eligió en la búsqueda, una temporada nueva y lo acomodado en el mostrador, y fotos
    /// nuevas, cambiadas y quitadas), la copia de antes con los cambios encima queda igual que la copia completa de ahora, también
    /// en lo que usa su búsqueda (todos los códigos, las marcas, lo más vendido y lo elegido: entrega 5, parte 3), en lo que se
    /// señala (entrega 13) y en las fotos (parte 2), y los cambios traen solo los productos que cambiaron (una foto no trae su
    /// producto) y lo elegido después.
    /// </summary>
    [Fact]
    public async Task La_copia_con_los_cambios_encima_queda_igual_que_la_completa()
    {
        var c = await CajaAsync();
        await servicio.CatalogoAlDiaAsync();
        // Lo que la caja ya había elegido con un texto: viene en la copia de antes.
        var deAntes = DeLaCopia(await CopiaAsync(c), p => p.Presentacion.EsBase)[11];
        await ElegirAsync(c, "Separador De Antes", deAntes.VarianteId);
        // Dos fotos que ya trae la copia de antes: una va a cambiar y a la otra se le va a quitar.
        var conFotos = DeLaCopia(await CopiaAsync(c), p => p.Presentacion.EsBase);
        var (fotoCambiada, fotoQuitada) = (conFotos[13], conFotos[14]);
        await FotoAsync(c, fotoCambiada.VarianteId, PapeleriaPos.Pruebas.Impresion.ImagenesDePrueba.Foto(800, 600));
        await FotoAsync(c, fotoQuitada.VarianteId, PapeleriaPos.Pruebas.Impresion.ImagenesDePrueba.Foto(600, 800));
        var antes = await CopiaAsync(c);
        Assert.Contains(new ElegidoDto("SEPARADOR DE ANTES", deAntes.VarianteId), antes.Elegidos!);
        Assert.Contains(antes.Fotos!, f => f.VarianteId == fotoQuitada.VarianteId);
        var sinMayoreo = DeLaCopia(antes, p => p.LlevaInventario && p.Precios.Count == 1 && p.Precios[0].PresentacionId is null && p.Presentacion.EsBase);
        var (conPrecio, vendido, deBaja) = (sinMayoreo[0], sinMayoreo[1], sinMayoreo[2]);
        var conDosCodigos = antes.Codigos.GroupBy(x => x.VarianteId).Where(g => g.Count() >= 2).Select(g => g.Key)
            .First(id => id != conPrecio.VarianteId && id != vendido.VarianteId && id != deBaja.VarianteId);
        var codigoQuitado = antes.Codigos.Where(x => x.VarianteId == conDosCodigos).OrderBy(x => x.Codigo, StringComparer.Ordinal).Last();

        // El catálogo, por la API.
        Assert.True((await c.Http.PostAsJsonAsync("api/catalogo/precio-menudeo",
            new SolicitudPrecioMenudeo(c.Dueno.UsuarioId, c.Datos.SucursalId, conPrecio.VarianteId, conPrecio.Precios[0].PrecioCentavos + 250))).IsSuccessStatusCode);
        var departamento = (await c.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        var nuevo = await Leer<ProductoVentaDto>(await c.Http.PostAsJsonAsync("api/catalogo/alta-rapida",
            new SolicitudAltaRapida(c.Cajero.UsuarioId, c.Datos.SucursalId, "9900000777001", "Separadores recién dados de alta", 1990, departamento)));
        Assert.True((await c.Http.PostAsJsonAsync("api/catalogo/depuracion/baja", new SolicitudBaja(c.Dueno.UsuarioId, c.Datos.SucursalId, deBaja.VarianteId)))
            .IsSuccessStatusCode);
        await Leer<List<SinonimoDto>>(await c.Http.PostAsJsonAsync("api/catalogo/sinonimos",
            new SolicitudSinonimo(c.Dueno.UsuarioId, c.Datos.SucursalId, "separador", "divisor", false)));
        // Lo que se señala (entrega 13): una temporada nueva con sus fechas y su mesa, y productos en las repisas del mostrador.
        var mostrador = await Leer<MostradorDto>(await c.Http.PostAsJsonAsync("api/mostrador",
            new SolicitudNuevoExhibidor(c.Dueno.UsuarioId, c.Datos.SucursalId, "Regreso a clases", PapeleriaPos.Nucleo.Catalogo.TipoExhibidor.Temporada)));
        var regreso = mostrador.Exhibidores.Single(e => e.Nombre == "Regreso a clases");
        await Leer<MostradorDto>(await c.Http.PutAsJsonAsync($"api/mostrador/{regreso.Id}", new SolicitudCambiarExhibidor(c.Dueno.UsuarioId, c.Datos.SucursalId,
            Fechas: new FechasTemporadaDto(new PapeleriaPos.Nucleo.Catalogo.DiaDelAnio(7, 15), new PapeleriaPos.Nucleo.Catalogo.DiaDelAnio(8, 31)))));
        mostrador = await Leer<MostradorDto>(await c.Http.PostAsJsonAsync($"api/mostrador/{regreso.Id}/lugares", new SolicitudLugar(c.Dueno.UsuarioId, c.Datos.SucursalId, "Mesa")));
        var repisas = mostrador.Exhibidores.First(e => e.Nombre == "Mostrador").Lugares;
        await Leer<MostradorDto>(await c.Http.PutAsJsonAsync("api/mostrador/acomodo", new SolicitudAcomodo(c.Dueno.UsuarioId, c.Datos.SucursalId,
            [new LugarConProductosDto(repisas[0].Id, [conPrecio.VarianteId, nuevo.VarianteId]), new LugarConProductosDto(repisas[1].Id, [vendido.VarianteId]),
             new LugarConProductosDto(mostrador.Exhibidores.Single(e => e.Nombre == "Regreso a clases").Lugares[0].Id, [deAntes.VarianteId])])));
        // Las fotos (entrega 13, parte 2): otra para la que ya tenía, una para el producto recién dado de alta y otra para el
        // elegido de antes; a una se le quita.
        var otraFoto = await FotoAsync(c, fotoCambiada.VarianteId, PapeleriaPos.Pruebas.Impresion.ImagenesDePrueba.Mitades(800, 600));
        var fotoDelNuevo = await FotoAsync(c, nuevo.VarianteId, PapeleriaPos.Pruebas.Impresion.ImagenesDePrueba.Foto(1600, 1200));
        var fotoDeAntes = await FotoAsync(c, deAntes.VarianteId, PapeleriaPos.Pruebas.Impresion.ImagenesDePrueba.Mitades(500, 500));
        Assert.True((await c.Http.PostAsJsonAsync($"api/fotos/{fotoQuitada.VarianteId}/quitar", new SolicitudQuitarFoto(c.Dueno.UsuarioId, c.Datos.SucursalId)))
            .IsSuccessStatusCode);
        // Un PIN mal tecleado no cambia nada.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "9999"))).StatusCode);
        // Lo demás, en la base: un código quitado, una marca, un usuario nuevo, la tienda y un cliente nuevo.
        string marca;
        await using (var db = servicio.Base())
        {
            db.CodigosBarras.Remove(await db.CodigosBarras.SingleAsync(x => x.Codigo == codigoQuitado.Codigo));
            var unaMarca = await db.Marcas.OrderBy(m => m.Nombre).FirstAsync(m => db.Productos.Any(p => p.MarcaId == m.Id && p.Activo));
            unaMarca.Nombre += " Mx";
            marca = unaMarca.Nombre;
            var nueva = new Usuario { Nombre = "Cajera nueva", NombreUsuario = "nueva", SucursalId = c.Datos.SucursalId };
            nueva.EstablecerPin("4321");
            db.Usuarios.Add(nueva);
            var tienda = await db.ConfiguracionesSucursal.SingleOrDefaultAsync(x => x.SucursalId == c.Datos.SucursalId)
                ?? db.ConfiguracionesSucursal.Add(new PapeleriaPos.Nucleo.Configuracion.ConfiguracionSucursal { SucursalId = c.Datos.SucursalId }).Entity;
            tienda.NombreTienda = "Papelería de prueba";
            db.ClientesRegistrados.Add(new PapeleriaPos.Nucleo.Clientes.ClienteRegistrado { Nombre = "Escuela de prueba", DescuentoCentesimas = 700 });
            await db.SaveChangesAsync();
        }
        await servicio.CatalogoAlDiaAsync();
        // Y una venta: solo existencias.
        await VenderAsync(c, vendido, 2);
        // Lo que eligió después en la búsqueda: lo de un texto nuevo y, con el de antes, otro producto. Lo de otra caja no le toca.
        await ElegirAsync(c, "separadores", nuevo.VarianteId);
        await ElegirAsync(c, "Separador de antes", conPrecio.VarianteId);
        servicio.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.CatalogoEnMemoria>().Aprender(Guid.NewGuid(), "de otra caja", vendido.VarianteId);

        var cambios = await CambiosAsync(c, antes);
        var despues = await CopiaAsync(c);

        Assert.Null(cambios.PaqueteCompleto);
        Assert.Equal(Comparable(despues), Comparable(CopiaAlDia.Aplicar(antes, cambios)));
        // Lo de la búsqueda: lo elegido después, solo lo nuevo; lo más vendido no se calculó otra vez, así que no viene.
        Assert.Equal([new ElegidoDto("SEPARADOR DE ANTES", conPrecio.VarianteId), new ElegidoDto("SEPARADORES", nuevo.VarianteId)], cambios.Elegidos!);
        Assert.Null(cambios.Vendidos);
        Assert.Equal((antes.VersionVendidos, despues.VersionElegidos), (cambios.VersionVendidos, cambios.VersionElegidos));
        Assert.Contains(new ElegidoDto("SEPARADORES", nuevo.VarianteId), despues.Elegidos!);
        Assert.Contains(cambios.Codigos, x => x.Codigo == "9900000777001" && x.VarianteId == nuevo.VarianteId);
        // Solo lo que cambió: el del precio, el nuevo, el del código quitado y los de la marca; el dado de baja, quitado.
        var deLaMarca = despues.Productos.Where(p => p.Descripcion.Contains(marca, StringComparison.Ordinal)).Select(p => p.VarianteId).ToHashSet();
        Assert.Equal(deLaMarca.Union([conPrecio.VarianteId, nuevo.VarianteId, conDosCodigos]).Order(), cambios.Productos.Select(p => p.VarianteId).Order());
        Assert.Equal([deBaja.VarianteId], cambios.Quitados);
        Assert.Contains(cambios.Existencias, e => e.VarianteId == vendido.VarianteId && e.Cantidad == vendido.Existencia - 2);
        Assert.NotNull(cambios.Usuarios);
        Assert.NotNull(cambios.Configuracion);
        Assert.NotNull(cambios.Registrados);
        Assert.NotNull(cambios.Sinonimos);
        Assert.NotNull(cambios.Marcas);
        Assert.NotNull(cambios.Exhibidores);
        Assert.Equal([conPrecio.VarianteId, nuevo.VarianteId], despues.Exhibidores!.First(e => e.Nombre == "Mostrador").Lugares[0].Productos);
        Assert.Contains(despues.Exhibidores!, e => e.Nombre == "Regreso a clases" && e.Desde == new PapeleriaPos.Nucleo.Catalogo.DiaDelAnio(7, 15));
        // De las fotos, solo las que cambiaron y la quitada; la copia completa ya las trae así (y ninguna foto trajo su producto).
        Assert.Equal(new[]
            {
                new FotoDeProductoDto(fotoCambiada.VarianteId, otraFoto.Huella), new FotoDeProductoDto(nuevo.VarianteId, fotoDelNuevo.Huella),
                new FotoDeProductoDto(deAntes.VarianteId, fotoDeAntes.Huella),
            }.OrderBy(f => f.VarianteId), cambios.Fotos!);
        Assert.Equal([fotoQuitada.VarianteId], cambios.FotosQuitadas!);
        Assert.DoesNotContain(despues.Fotos!, f => f.VarianteId == fotoQuitada.VarianteId);
        Assert.DoesNotContain(cambios.Productos, p => p.VarianteId == fotoCambiada.VarianteId || p.VarianteId == deAntes.VarianteId);
        Assert.Null(cambios.Caja);
        Assert.Null(cambios.Ticket);
        Assert.True(cambios.Version > antes.Version && cambios.VersionExistencias > antes.VersionExistencias);
    }

    /// <summary>
    /// Más cambios que tocan a varios productos a la vez (de la revisión de la entrega 5, parte 2): juntar dos productos (el de
    /// origen con una presentación además de la pieza), quitar una escala de mayoreo, pasar un código a otro producto (solo el
    /// código) y dar de baja a un usuario. La copia de antes con los cambios encima queda igual que la completa. Con su propio
    /// servicio: la baja del cajero no les toca a las demás pruebas.
    /// </summary>
    [Fact]
    public async Task Juntar_quitar_una_escala_mover_un_codigo_y_dar_de_baja_un_usuario_quedan_igual_que_la_completa()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var http = propio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        await propio.CatalogoAlDiaAsync();
        var antes = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));
        var porPieza = DeLaCopia(antes, p => p.Unidad == PapeleriaPos.Nucleo.Catalogo.UnidadBase.Pieza);
        var conMayoreo = DeLaCopia(antes, p => p.Precios.Any(x => x.CantidadMinimaMilesimas > 1000));

        // Juntar: el de origen tiene otra presentación además de la pieza; el de destino, solo la pieza.
        var origen = porPieza.First(p => p.Presentaciones.Count >= 2);
        var destino = porPieza.First(p => p.VarianteId != origen.VarianteId && p.Presentaciones.Count == 1);
        Assert.True((await http.PostAsJsonAsync("api/catalogo/depuracion/juntar",
            new SolicitudJuntar(dueno.UsuarioId, caja.SucursalId, origen.VarianteId, destino.VarianteId))).IsSuccessStatusCode);
        await using (var db = propio.Base())
        {
            // Una escala de mayoreo que se quita.
            var conEscala = conMayoreo.First(p => p.VarianteId != origen.VarianteId && p.VarianteId != destino.VarianteId);
            var escala = conEscala.Precios.First(x => x.CantidadMinimaMilesimas > 1000);
            db.Precios.Remove(await db.Precios.SingleAsync(x => x.Id == escala.Id));
            // Un código que pasa a otro producto: solo se carga el código.
            var usados = new HashSet<Guid> { origen.VarianteId, destino.VarianteId, conEscala.VarianteId };
            var movido = antes.Codigos.First(c => !usados.Contains(c.VarianteId) && antes.Codigos.Count(x => x.VarianteId == c.VarianteId) >= 2);
            usados.Add(movido.VarianteId);
            var otro = porPieza.First(p => !usados.Contains(p.VarianteId));
            (await db.CodigosBarras.SingleAsync(x => x.Codigo == movido.Codigo)).PresentacionId = otro.Presentacion.Id;
            // Un usuario que se da de baja.
            (await db.Usuarios.FirstAsync(u => u.NombreUsuario == "cajero")).Activo = false;
            await db.SaveChangesAsync();
        }
        await propio.CatalogoAlDiaAsync();

        var cambios = await Leer<CambiosCopiaDto>(await http.GetAsync(
            $"api/cajas/{caja.Id}/cambios?desde={antes.Version}&existencias={antes.VersionExistencias}&base={antes.Base}&formato={CopiaAlDia.Formato}"
            + $"&vendidos={antes.VersionVendidos}&elegidos={antes.VersionElegidos}"));
        var despues = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));

        Assert.Null(cambios.PaqueteCompleto);
        Assert.Equal(Comparable(despues), Comparable(CopiaAlDia.Aplicar(antes, cambios)));
    }

    /// <summary>
    /// Lo acomodado que solo sale de un lugar (entrega 13): no cambia ningún producto del catálogo, pero los cambios traen los
    /// exhibidores (lo quitado queda anotado) y la copia con ellos encima queda igual que la completa. Se guarda ya en la caja
    /// (cambia lo de la copia), sin volver a armar su búsqueda (no cambia nada de lo que la arma).
    /// </summary>
    [Fact]
    public async Task Lo_que_sale_del_mostrador_llega_con_los_cambios()
    {
        var c = await CajaAsync();
        await servicio.CatalogoAlDiaAsync();
        var actual = await Leer<MostradorDto>(await c.Http.GetAsync("api/mostrador"));
        var navidad = actual.Exhibidores.Single(e => e.Nombre == "Navidad");
        var lista = DeLaCopia(await CopiaAsync(c), p => p.Presentacion.EsBase);
        var (uno, otro) = (lista[21], lista[22]);
        await Leer<MostradorDto>(await c.Http.PutAsJsonAsync("api/mostrador/acomodo", new SolicitudAcomodo(c.Dueno.UsuarioId, c.Datos.SucursalId,
            [new LugarConProductosDto(navidad.Lugares[0].Id, [uno.VarianteId, otro.VarianteId])])));
        await servicio.CatalogoAlDiaAsync();
        var antes = await CopiaAsync(c);

        await Leer<MostradorDto>(await c.Http.PutAsJsonAsync("api/mostrador/acomodo", new SolicitudAcomodo(c.Dueno.UsuarioId, c.Datos.SucursalId,
            [new LugarConProductosDto(navidad.Lugares[0].Id, [otro.VarianteId])])));
        await servicio.CatalogoAlDiaAsync();
        var cambios = await CambiosAsync(c, antes);
        var despues = await CopiaAsync(c);

        Assert.Null(cambios.PaqueteCompleto);
        Assert.Empty(cambios.Productos);
        Assert.Empty(cambios.Quitados);
        Assert.True(cambios.CambiaCatalogo);
        Assert.False(cambios.CambiaLaBusqueda);
        Assert.Equal([otro.VarianteId], cambios.Exhibidores!.Single(e => e.Nombre == "Navidad").Lugares[0].Productos);
        Assert.Equal(Comparable(despues), Comparable(CopiaAlDia.Aplicar(antes, cambios)));
        // Y sin más cambios, ya no los trae.
        Assert.Null((await CambiosAsync(c, despues)).Exhibidores);
    }

    /// <summary>
    /// Lo más vendido (entrega 5, parte 3): la caja principal lo calcula una vez al día, y cuando lo calcula otra vez, los
    /// cambios lo traen completo, con su versión; mientras no, no viene. La copia de antes con esos cambios queda igual que la
    /// completa de ahora, y después ya no hay nada que traer de la búsqueda.
    /// </summary>
    [Fact]
    public async Task Lo_mas_vendido_calculado_otra_vez_llega_con_los_cambios()
    {
        var c = await CajaAsync();
        await servicio.CatalogoAlDiaAsync();
        var antes = await CopiaAsync(c);
        var producto = DeLaCopia(antes, p => p.LlevaInventario && p.Presentacion.EsBase && p.Precios.Any(x => x.PresentacionId is null))[13];
        var veces = antes.Vendidos!.SingleOrDefault(v => v.VarianteId == producto.VarianteId)?.Veces ?? 0;
        await VenderAsync(c, producto, 1);
        Assert.Null((await CambiosAsync(c, antes)).Vendidos);

        // Un día después: lo más vendido se calcula otra vez (aquí, al volver a cargar el catálogo en memoria).
        servicio.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.CatalogoEnMemoria>().Invalidar();
        await servicio.CatalogoAlDiaAsync();
        var cambios = await CambiosAsync(c, antes);
        var despues = await CopiaAsync(c);
        var alDia = CopiaAlDia.Aplicar(antes, cambios);

        Assert.Equal(despues.Vendidos, cambios.Vendidos);
        Assert.True(Assert.Single(cambios.Vendidos!, v => v.VarianteId == producto.VarianteId).Veces > veces);
        Assert.True(cambios.VersionVendidos > antes.VersionVendidos);
        Assert.Equal(Comparable(despues), Comparable(alDia));
        var otraVez = await CambiosAsync(c, alDia);
        Assert.Null(otraVez.Vendidos);
        Assert.Null(otraVez.Elegidos);
    }

    [Fact]
    public async Task Sin_cambios_no_trae_nada_y_una_venta_solo_trae_sus_existencias()
    {
        var c = await CajaAsync();
        await servicio.CatalogoAlDiaAsync();
        var antes = await CopiaAsync(c);
        var sinCambios = await CambiosAsync(c, antes);
        var producto = DeLaCopia(antes, p => p.LlevaInventario && p.Presentacion.EsBase && p.Precios.Any(x => x.PresentacionId is null))[7];

        await VenderAsync(c, producto, 1);
        var trasLaVenta = await CambiosAsync(c, antes);

        Assert.Equal((antes.Version, antes.VersionExistencias), (sinCambios.Version, sinCambios.VersionExistencias));
        Assert.Empty(sinCambios.Productos);
        Assert.Empty(sinCambios.Existencias);
        Assert.False(sinCambios.CambiaCatalogo);
        // La venta no sube la versión del catálogo: solo la de existencias, con la del producto vendido.
        Assert.Equal(antes.Version, trasLaVenta.Version);
        Assert.False(trasLaVenta.CambiaCatalogo);
        Assert.True(trasLaVenta.VersionExistencias > antes.VersionExistencias);
        Assert.Equal(producto.Existencia - 1, Assert.Single(trasLaVenta.Existencias, e => e.VarianteId == producto.VarianteId).Cantidad);
    }

    /// <summary>Un PIN mal tecleado y abrir y cerrar un turno ya no cambian la versión que ve la caja en su latido.</summary>
    [Fact]
    public async Task Un_PIN_mal_tecleado_y_el_turno_no_cambian_la_version_del_latido()
    {
        var c = await CajaAsync();
        var antes = (await c.Http.GetFromJsonAsync<EstadoServicioDto>("api/estado"))!;

        await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "0000"));
        await c.Http.PostAsJsonAsync($"api/cajas/{c.Datos.Id}/sesion", new SolicitudEntradaFicha(c.Cajero.UsuarioId, "0000"));
        var turno = await TurnoAsync(c);
        Assert.True((await c.Http.PostAsJsonAsync($"api/turnos/{turno.Id}/corte-z", new SolicitudCorteZ(c.Dueno.UsuarioId, 50000))).IsSuccessStatusCode);
        var despues = (await c.Http.GetFromJsonAsync<EstadoServicioDto>("api/estado"))!;

        Assert.Equal(antes.VersionDatos, despues.VersionDatos);
        Assert.Equal(antes.Base, despues.Base);
        Assert.NotNull(despues.VersionExistencias);
    }

    [Fact]
    public async Task Lo_que_no_se_puede_dar_desde_esa_version_pide_la_copia_completa()
    {
        var c = await CajaAsync();
        var copia = await CopiaAsync(c);

        var otraBase = await CambiosAsync(c, copia, base_: Guid.NewGuid());
        var otroFormato = await CambiosAsync(c, copia, formato: CopiaAlDia.Formato + 1);
        var masNueva = await CambiosAsync(c, copia, version: copia.Version + 1000);

        Assert.All(new[] { otraBase, otroFormato, masNueva }, x => Assert.NotNull(x.PaqueteCompleto));
        Assert.Contains("otra base", otraBase.PaqueteCompleto);
        Assert.Contains("otro formato", otroFormato.PaqueteCompleto);
        Assert.Empty(masNueva.Productos);
        // La copia dice de qué base y formato es.
        Assert.Equal((CopiaAlDia.Formato, (await c.Http.GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.Base), (copia.Formato, copia.Base));
    }

    /// <summary>Lo que llega por el aviso, línea por línea, hasta que se cumpla lo que se espera (con límite).</summary>
    private static async Task<List<string>> LeerHastaAsync(StreamReader lector, Func<List<string>, bool> listo)
    {
        var lineas = new List<string>();
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!listo(lineas))
            lineas.Add(await lector.ReadLineAsync(limite.Token) ?? throw new Xunit.Sdk.XunitException("El aviso se cerró."));
        return lineas;
    }

    private static VersionesCopiaDto Versiones(string linea) =>
        JsonSerializer.Deserialize<VersionesCopiaDto>(linea["data: ".Length..], JsonSerializerOptions.Web)!;

    /// <summary>
    /// El aviso (text/event-stream): las versiones al conectarse, otra vez cuando cambia una (aquí, un precio) y, sin cambios,
    /// el comentario «vivo» (cada 15 s en la tienda; aquí, cada 0.2 s).
    /// </summary>
    [Fact]
    public async Task El_aviso_manda_las_versiones_al_conectarse_cuando_cambian_y_un_vivo()
    {
        await using var conVivo = servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Avisos:SegundosVivo", "0.2"));
        var http = conVivo.CreateClient();
        // La primera (la serie A): otra prueba da de alta una caja más.
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var producto = (await http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete"))!.Productos
            .First(p => p.Precios.Count == 1 && p.Precios[0].PresentacionId is null);

        using var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/avisos", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", respuesta.Content.Headers.ContentType?.MediaType);
        using var lector = new StreamReader(await respuesta.Content.ReadAsStreamAsync(), Encoding.UTF8);
        var primero = await LeerHastaAsync(lector, l => l.Any(x => x.StartsWith("data: ", StringComparison.Ordinal)));
        Assert.Contains("event: versiones", primero);
        var alConectarse = Versiones(primero.Last());
        // Con el formato de su copia: si no es el de la caja, la caja no le pide cambios.
        Assert.Equal(CopiaAlDia.Formato, alConectarse.Formato);
        var vivo = await LeerHastaAsync(lector, l => l.Contains(": vivo"));

        Assert.True((await http.PostAsJsonAsync("api/catalogo/precio-menudeo",
            new SolicitudPrecioMenudeo(dueno.UsuarioId, caja.SucursalId, producto.VarianteId, producto.Precios[0].PrecioCentavos + 10))).IsSuccessStatusCode);
        var trasElCambio = await LeerHastaAsync(lector, l => l.Any(x => x.StartsWith("data: ", StringComparison.Ordinal)));

        Assert.DoesNotContain(vivo, x => x.StartsWith("data: ", StringComparison.Ordinal));
        var nuevas = Versiones(trasElCambio.Last(x => x.StartsWith("data: ", StringComparison.Ordinal)));
        Assert.Equal(alConectarse.Base, nuevas.Base);
        Assert.True(nuevas.Catalogo > alConectarse.Catalogo);
    }

    /// <summary>Con llave, el aviso de otra caja no; sin llave desde otra PC, nada (como lo demás de las cajas).</summary>
    [Fact]
    public async Task El_aviso_es_solo_para_su_caja()
    {
        await using var red = servicio.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, DesdeLaRed>()));
        var principal = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var alta = await Leer<AltaCajaDto>(await red.CreateClient().PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", principal.SucursalId, null, "Caja Avisos", "AV", "PC-AV")));
        var conLlave = red.CreateClient();
        conLlave.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);

        var deOtra = await conLlave.GetAsync($"api/cajas/{principal.Id}/avisos", HttpCompletionOption.ResponseHeadersRead);
        var sinLlave = await red.CreateClient().GetAsync($"api/cajas/{alta.Caja.Id}/avisos", HttpCompletionOption.ResponseHeadersRead);
        var cambiosDeOtra = await conLlave.GetAsync($"api/cajas/{principal.Id}/cambios?desde=0&existencias=0&base={Guid.NewGuid()}&formato=1");
        using var propia = await conLlave.GetAsync($"api/cajas/{alta.Caja.Id}/avisos", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.Forbidden, deOtra.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, sinLlave.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, cambiosDeOtra.StatusCode);
        Assert.Equal(HttpStatusCode.OK, propia.StatusCode);
        Assert.Equal("text/event-stream", propia.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// La misma venta, a la vez, por el cobro en línea (que la caja dejó de esperar) y por la bandeja: queda una, con un folio,
    /// y las dos llamadas terminan bien.
    /// </summary>
    [Fact]
    public async Task La_misma_venta_por_el_cobro_y_por_la_bandeja_a_la_vez_queda_una()
    {
        var c = await CajaAsync();
        var turno = await TurnoAsync(c);
        var copia = await CopiaAsync(c);
        var producto = DeLaCopia(copia, p => p.Presentacion.EsBase && p.Precios.Count == 1 && p.Precios[0].PresentacionId is null)[9];
        var precio = producto.Precios[0].PrecioCentavos;

        for (var vuelta = 0; vuelta < 5; vuelta++)
        {
            var ventaId = Guid.NewGuid();
            var folio = (await c.Http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{c.Datos.Id}/paquete"))!.UltimoFolio + 1;
            var enLinea = c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, turno.Id, c.Cajero.UsuarioId,
                [new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000)], [new CobroSolicitado(FormaPago.Efectivo, precio)], ventaId));
            var porLaBandeja = c.Http.PostAsJsonAsync("api/bandeja/ventas", new VentaContingenciaDto(ventaId, c.Datos.Id, turno.Id, c.Cajero.UsuarioId, folio,
                DateTime.UtcNow, [new LineaContingenciaDto(producto.VarianteId, producto.Presentacion.Id, producto.Descripcion, 1000, precio)],
                [new CobroSolicitado(FormaPago.Efectivo, precio)], RedondeoCentavos: 0));
            var respuestas = await Task.WhenAll(enLinea, porLaBandeja);

            Assert.All(respuestas, r => Assert.True(r.IsSuccessStatusCode, r.Content.ReadAsStringAsync().Result));
            await using var db = servicio.Base();
            var venta = await db.Ventas.AsNoTracking().SingleAsync(v => v.Id == ventaId);
            Assert.Equal(1, await db.Ventas.CountAsync(v => v.SucursalId == venta.SucursalId && v.Serie == venta.Serie && v.Folio == venta.Folio));
        }
    }
}
