using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Pruebas.Datos;
using PapeleriaPos.Pruebas.Eleventa;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Como <see cref="DesdeLaRed"/>, pero desde la dirección que se diga (IPv6, IPv4 dentro de IPv6, la de la propia PC…).</summary>
internal sealed class DesdeLaIp(string ip) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> siguiente) => app =>
    {
        app.Use((contexto, resto) =>
        {
            contexto.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            return resto(contexto);
        });
        siguiente(app);
    };
}

/// <summary>
/// La instalación desde cero (2026-10-31): en una base nueva, sin sucursal ni usuarios, la caja de la misma PC del servicio crea la
/// tienda, la cuenta del dueño y su caja de una vez; otra PC espera a que se termine en la caja principal. Y «La única caja»: ninguna
/// otra PC se da de alta, y nadie la encuentra en la red.
/// </summary>
public sealed class TiendaNuevaApiTests
{
    private static SolicitudTiendaNueva Solicitud(string pin = "4321", string usuario = "mario", string sucursal = "Matriz") =>
        new(sucursal, ["PAPELERÍA LA ESQUINA", "Calle 5 número 12"], ["Gracias por su compra"], "Mario Álvarez", usuario, pin, "Caja 1", "a",
            "PC-PRINCIPAL");

    private static HttpClient DesdeOtraPc(ServicioDePrueba servicio, string? llave = null)
    {
        var http = servicio.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, DesdeLaRed>())).CreateClient();
        if (llave is not null)
            http.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, llave);
        return http;
    }

    private static HttpClient Desde(ServicioDePrueba servicio, string ip) =>
        servicio.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new DesdeLaIp(ip)))).CreateClient();

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ErrorDto> RechazoAsync(HttpResponseMessage r, HttpStatusCode estado)
    {
        Assert.Equal(estado, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!;
    }

    [Fact]
    public async Task Una_base_nueva_lo_dice_y_otra_pc_espera_a_que_se_termine_en_la_caja_principal()
    {
        await using var servicio = await ServicioDePrueba.CrearSinTiendaAsync();

        var estado = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<EstadoServicioDto>("api/estado"))!;
        var otra = DesdeOtraPc(servicio);
        var opciones = await otra.PostAsJsonAsync("api/alta/opciones", new SolicitudCredenciales("mario", "4321"));
        var alta = await otra.PostAsJsonAsync("api/alta", new SolicitudAltaCaja("mario", "4321", Guid.NewGuid(), null, "Caja 2", "B", "PC-2"));
        var crear = await otra.PostAsJsonAsync("api/alta/tienda-nueva", Solicitud());

        Assert.True(estado.TiendaNueva);
        Assert.False(estado.UnaSolaCaja);
        Assert.Empty(estado.Sucursales);
        Assert.Equal(new ErrorDto(TiendaNueva.TerminaEnLaPrincipal, TiendaNueva.CodigoTerminaEnLaPrincipal), await RechazoAsync(opciones, HttpStatusCode.Conflict));
        Assert.Equal(TiendaNueva.CodigoTerminaEnLaPrincipal, (await RechazoAsync(alta, HttpStatusCode.Conflict)).Codigo);
        // El PIN del dueño no pasa por la red: desde otra PC, la tienda no se crea.
        Assert.Equal(new ErrorDto(TiendaNueva.TerminaEnLaPrincipal, TiendaNueva.CodigoTerminaEnLaPrincipal), await RechazoAsync(crear, HttpStatusCode.Forbidden));
        await using var db = servicio.Base();
        Assert.False(await db.Sucursales.AnyAsync());
        Assert.False(await db.Usuarios.AnyAsync());
    }

    [Fact]
    public async Task La_caja_de_la_misma_pc_crea_la_tienda_la_cuenta_del_dueno_y_su_caja_de_una_vez()
    {
        await using var servicio = await ServicioDePrueba.CrearSinTiendaAsync();
        var local = servicio.Fabrica.CreateClient();

        var creada = await Leer<TiendaNuevaDto>(await local.PostAsJsonAsync("api/alta/tienda-nueva", Solicitud()));

        // La caja, dada de alta como con /api/alta: su llave entra desde la red (la de esta PC no la necesita, pero la guarda igual).
        Assert.Equal(("Caja 1", "A", "Matriz", "PC-PRINCIPAL"), (creada.Alta.Caja.Nombre, creada.Alta.Caja.Serie, creada.Alta.Caja.Sucursal, creada.Alta.Caja.Equipo));
        Assert.NotNull(creada.Alta.LlaveDelServicio);
        Assert.Equal(HttpStatusCode.OK, (await DesdeOtraPc(servicio, creada.Alta.Llave).GetAsync("api/cajas")).StatusCode);
        // El dueño ya adentro, con todo; y entra con su PIN en su ficha.
        Assert.Equal("Mario Álvarez", creada.Sesion.Nombre);
        Assert.All(Permisos.Acciones, a => Assert.True(creada.Sesion.Puede(a), $"Sin «{Permisos.Nombre(a)}»"));
        var fichas = await Leer<List<FichaUsuarioDto>>(await local.GetAsync($"api/cajas/{creada.Alta.Caja.Id}/usuarios"));
        Assert.Equal("Mario Álvarez", Assert.Single(fichas).Nombre);
        await Leer<SesionDto>(await local.PostAsJsonAsync($"api/cajas/{creada.Alta.Caja.Id}/sesion", new SolicitudEntradaFicha(creada.Sesion.UsuarioId, "4321")));
        // El ticket, como se escribió.
        var ticket = (await local.GetFromJsonAsync<ConfiguracionTicketDto>($"api/sucursales/{creada.Alta.Caja.SucursalId}/ticket"))!;
        Assert.Equal(["PAPELERÍA LA ESQUINA", "Calle 5 número 12"], ticket.Encabezado);
        Assert.Equal(["Gracias por su compra"], ticket.Pie);
        // Ya no es nueva.
        var estado = (await local.GetFromJsonAsync<EstadoServicioDto>("api/estado"))!;
        Assert.False(estado.TiendaNueva);
        Assert.Equal(["Matriz"], estado.Sucursales);

        await using (var db = servicio.Base())
        {
            var sucursal = await db.Sucursales.SingleAsync();
            Assert.Equal(TiendaNueva.Clave, sucursal.Clave);
            var dueno = await db.Usuarios.SingleAsync();
            Assert.Equal(("mario", true, (Guid?)null), (dueno.NombreUsuario, dueno.PrimeraCuenta, dueno.SucursalId));
            Assert.Equal(Permisos.Acciones.Count, await db.PermisosUsuario.CountAsync(p => p.UsuarioId == dueno.Id && p.Nivel == NivelPermiso.Permitido));
            Assert.True((await db.Cajas.SingleAsync()).EsPrincipal);
            // En la bitácora, lo que se creó y quién quedó como primera cuenta; nunca el PIN.
            var bitacora = await db.Bitacora.Where(b => b.SucursalId == sucursal.Id).ToListAsync();
            Assert.Equal(new[] { AccionControlada.ConfigurarTienda, AccionControlada.EditarReglas, AccionControlada.AdministrarCajas }.Order(),
                bitacora.Select(b => b.Accion).Order());
            Assert.All(bitacora, b => Assert.Equal(dueno.Id, b.UsuarioId));
            Assert.Contains(bitacora, b => b.Detalle == "Primera cuenta: Mario Álvarez (usuario «mario»), con todos los permisos");
            Assert.DoesNotContain(bitacora, b => b.Detalle.Contains("4321", StringComparison.Ordinal));
        }

        // Una sola vez: ni desde esta PC ni desde otra.
        Assert.Equal(new ErrorDto(TiendaNueva.YaExiste, TiendaNueva.CodigoYaExiste),
            await RechazoAsync(await local.PostAsJsonAsync("api/alta/tienda-nueva", Solicitud(usuario: "otro")), HttpStatusCode.Conflict));
        Assert.Equal(TiendaNueva.CodigoYaExiste,
            (await RechazoAsync(await DesdeOtraPc(servicio).PostAsJsonAsync("api/alta/tienda-nueva", Solicitud()), HttpStatusCode.Conflict)).Codigo);
        // Las otras cajas, como siempre: con el usuario y el PIN del dueño.
        var opciones = await Leer<List<SucursalAltaDto>>(await DesdeOtraPc(servicio).PostAsJsonAsync("api/alta/opciones", new SolicitudCredenciales("mario", "4321")));
        Assert.Equal("A", Assert.Single(Assert.Single(opciones).Cajas).Serie);
    }

    // ── Solo desde la misma PC, y una sola vez (revisión independiente del 2026-10-31) ──

    /// <summary>
    /// Desde otra PC no se crea, ni diciendo en un encabezado que viene de esta (no hay ForwardedHeaders), ni con IPv4 dentro de IPv6,
    /// ni con la de enlace local, ni con la IP de la red de la propia PC (el servicio la ve igual que la de otra).
    /// </summary>
    [Theory]
    [InlineData("192.168.1.50", "127.0.0.1")]
    [InlineData("192.168.1.50", "::1")]
    [InlineData("::ffff:192.168.1.50", null)]
    [InlineData("fe80::1", null)]
    [InlineData("192.168.1.10", null)]
    public async Task Desde_otra_pc_no_se_crea_la_tienda_aunque_diga_que_es_esta(string ip, string? reenviadoPor)
    {
        await using var servicio = await ServicioDePrueba.CrearSinTiendaAsync();
        var http = Desde(servicio, ip);
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "api/alta/tienda-nueva") { Content = JsonContent.Create(Solicitud()) };
        if (reenviadoPor is not null)
        {
            pedido.Headers.Add("X-Forwarded-For", reenviadoPor);
            pedido.Headers.Add("X-Real-IP", reenviadoPor);
            pedido.Headers.Add("Forwarded", $"for=\"{reenviadoPor}\"");
        }

        var respuesta = await http.SendAsync(pedido);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        await using var db = servicio.Base();
        Assert.False(await db.Sucursales.AnyAsync());
        Assert.False(await db.Usuarios.AnyAsync());
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Desde_esta_pc_se_crea_por_ipv4_ipv6_o_ipv4_en_ipv6(string ip)
    {
        await using var servicio = await ServicioDePrueba.CrearSinTiendaAsync();

        var creada = await Leer<TiendaNuevaDto>(await Desde(servicio, ip).PostAsJsonAsync("api/alta/tienda-nueva", Solicitud()));

        Assert.Equal("Caja 1", creada.Alta.Caja.Nombre);
    }

    /// <summary>Cuatro a la vez (dos ventanas de la caja, la del navegador…): una sola tienda, una sola cuenta y una sola caja.</summary>
    [Fact]
    public async Task Cuatro_a_la_vez_dejan_una_sola_tienda()
    {
        await using var servicio = await ServicioDePrueba.CrearSinTiendaAsync();
        var http = servicio.Fabrica.CreateClient();
        Assert.True((await http.GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.TiendaNueva);

        var respuestas = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => http.PostAsJsonAsync("api/alta/tienda-nueva", Solicitud(usuario: $"mario{i}"))));

        Assert.Single(respuestas, r => r.IsSuccessStatusCode);
        await using var db = servicio.Base();
        Assert.Equal((1, 1, 1), (await db.Sucursales.CountAsync(), await db.Usuarios.CountAsync(), await db.Cajas.CountAsync()));
    }

    /// <summary>
    /// La otra la creó mientras esta esperaba el candado de la base: la revisión de adentro de la transacción la detiene (no se duplica
    /// nada) y se le contesta como a una que llega después: 409 «tienda-creada» (antes, 400 sin código).
    /// </summary>
    [Fact]
    public async Task Si_otra_la_crea_mientras_tanto_no_se_duplica_y_se_contesta_con_el_codigo_de_ya_creada()
    {
        await using var servicio = await ServicioDePrueba.CrearSinTiendaAsync();
        var http = servicio.Fabrica.CreateClient();
        Assert.True((await http.GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.TiendaNueva);
        HttpResponseMessage segunda;
        await using (var otra = servicio.Base())
        {
            // La que gana: con el candado de escritura (BEGIN IMMEDIATE) y su tienda todavía sin confirmar.
            await using var transaccion = await otra.Database.BeginTransactionAsync();
            otra.Sucursales.Add(new Sucursal { Clave = "M", Nombre = "La primera" });
            await otra.SaveChangesAsync();
            var pedida = http.PostAsJsonAsync("api/alta/tienda-nueva", Solicitud());
            // La de la caja ya pasó la revisión de afuera (sin confirmar no se ve) y espera el candado.
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.False(pedida.IsCompleted);
            await transaccion.CommitAsync();
            segunda = await pedida;
        }

        await using (var db = servicio.Base())
            Assert.Equal((1, 0, 0), (await db.Sucursales.CountAsync(), await db.Usuarios.CountAsync(), await db.Cajas.CountAsync()));
        var error = await segunda.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal(TiendaNueva.YaExiste, error!.Mensaje);
        Assert.Equal((HttpStatusCode.Conflict, TiendaNueva.CodigoYaExiste), (segunda.StatusCode, error.Codigo));
    }

    /// <summary>
    /// «Tienda nueva» es sin sucursal Y sin usuarios: con una sucursal sin usuarios, o usuarios sin sucursal (lo que no deja ninguna
    /// instalación), no se ofrece ni se crea encima.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Con_solo_la_sucursal_o_solo_usuarios_no_es_tienda_nueva_y_no_se_crea_encima(bool conSucursal, bool conUsuario)
    {
        await using var servicio = await ServicioDePrueba.CrearSinTiendaAsync();
        await using (var db = servicio.Base())
        {
            if (conSucursal)
                db.Sucursales.Add(new Sucursal { Clave = "M", Nombre = "De antes" });
            if (conUsuario)
                db.Usuarios.Add(new Usuario { Nombre = "De eleventa", NombreUsuario = "eleventa", Rol = Rol.Cajero });
            await db.SaveChangesAsync();
        }
        var http = servicio.Fabrica.CreateClient();

        var estado = (await http.GetFromJsonAsync<EstadoServicioDto>("api/estado"))!;
        var crear = await http.PostAsJsonAsync("api/alta/tienda-nueva", Solicitud());

        Assert.False(estado.TiendaNueva);
        Assert.Equal(HttpStatusCode.Conflict, crear.StatusCode);
        Assert.Equal(TiendaNueva.CodigoYaExiste, (await crear.Content.ReadFromJsonAsync<ErrorDto>())!.Codigo);
        await using var lectura = servicio.Base();
        Assert.Equal((conSucursal ? 1 : 0, conUsuario ? 1 : 0, 0),
            (await lectura.Sucursales.CountAsync(), await lectura.Usuarios.CountAsync(), await lectura.Cajas.CountAsync()));
    }

    [Theory]
    [InlineData("12", "mario", "Matriz", "El PIN es de 4 cifras.")]
    [InlineData("4321", "ma rio", "Matriz", "El usuario va de 2 a 20 letras o números, sin espacios ni acentos: «mario», por ejemplo.")]
    [InlineData("4321", "mario", " ", "Escribe el nombre de la sucursal: «Matriz», «Sucursal 2»…")]
    public async Task Lo_que_no_sirve_se_dice_y_no_crea_nada(string pin, string usuario, string sucursal, string mensaje)
    {
        await using var servicio = await ServicioDePrueba.CrearSinTiendaAsync();
        var http = servicio.Fabrica.CreateClient();

        var respuesta = await http.PostAsJsonAsync("api/alta/tienda-nueva", Solicitud(pin, usuario, sucursal));

        Assert.Equal(mensaje, (await RechazoAsync(respuesta, HttpStatusCode.BadRequest)).Mensaje);
        Assert.True((await http.GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.TiendaNueva);
    }

    [Fact]
    public async Task Con_la_unica_caja_otra_pc_no_se_da_de_alta_ni_la_encuentra_en_la_red_y_esta_pc_y_los_celulares_siguen()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(("Instalacion:UnaSolaCaja", "true"), ("Descubrimiento:Activo", "true"));
        var otra = DesdeOtraPc(servicio);
        var sucursal = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))![0].SucursalId;

        var estado = (await otra.GetFromJsonAsync<EstadoServicioDto>("api/estado"))!;
        var opciones = await otra.PostAsJsonAsync("api/alta/opciones", new SolicitudCredenciales("dueno", "3333"));
        var alta = await otra.PostAsJsonAsync("api/alta", new SolicitudAltaCaja("dueno", "3333", sucursal, null, "Caja 2", "B", "PC-2"));

        Assert.True(estado.UnaSolaCaja);
        Assert.Equal(new ErrorDto(TiendaNueva.UnaSolaCaja, TiendaNueva.CodigoUnaSolaCaja), await RechazoAsync(opciones, HttpStatusCode.Forbidden));
        Assert.Equal(TiendaNueva.CodigoUnaSolaCaja, (await RechazoAsync(alta, HttpStatusCode.Forbidden)).Codigo);
        // No contesta a quien busca el servicio en la red (UDP 5081), aunque el descubrimiento esté prendido.
        Assert.DoesNotContain(servicio.Fabrica.Services.GetServices<IHostedService>(), h => h is DescubrimientoEnRed);
        // La caja de esta PC, como siempre.
        Assert.Single(await Leer<List<SucursalAltaDto>>(await servicio.Fabrica.CreateClient().PostAsJsonAsync("api/alta/opciones",
            new SolicitudCredenciales("dueno", "3333"))));
        // Y los celulares, por el 5080: el conteo se abre desde la red.
        Assert.Equal(HttpStatusCode.OK, (await otra.GetAsync("conteo")).StatusCode);
    }

    /// <summary>«true» como texto (o «True») vale; lo que no es un booleano no deja arrancar el servicio (como dice plantilla-servicio.json).</summary>
    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task La_unica_caja_se_lee_como_booleano(string valor, bool unaSola)
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(("Instalacion:UnaSolaCaja", valor));

        Assert.Equal(unaSola, (await servicio.Fabrica.CreateClient().GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.UnaSolaCaja);
    }

    [Theory]
    [InlineData("sí")]
    [InlineData("1")]
    public async Task La_unica_caja_mal_escrita_no_deja_arrancar_el_servicio(string valor)
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(("Instalacion:UnaSolaCaja", valor));

        var error = Assert.ThrowsAny<Exception>(() => servicio.Fabrica.CreateClient());

        Assert.Contains("UnaSolaCaja", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Con «La única caja», las cajas que ya estaban dadas de alta en otras PC siguen, pero sin la búsqueda en la red ya no encuentran
    /// solas a la caja principal si le cambia la IP (revisión del 2026-10-31): el servicio lo avisa al arrancar. Las de esta misma PC,
    /// las que no tienen PC y las dadas de baja no cuentan.
    /// </summary>
    [Fact]
    public async Task Con_la_unica_caja_se_avisa_si_hay_cajas_dadas_de_alta_en_otras_pc()
    {
        await using var memoria = await BaseEnMemoria.CrearAsync();
        await using var db = memoria.Contexto();
        var sucursal = new Sucursal { Clave = "M", Nombre = "Matriz" };
        Caja NuevaCaja(string nombre, string serie, string? equipo, bool activa = true)
        {
            var caja = new Caja { SucursalId = sucursal.Id, Nombre = nombre, Serie = serie, Activa = activa };
            if (equipo is not null)
                caja.DarDeAlta(equipo, DateTime.UtcNow);
            return caja;
        }
        db.Sucursales.Add(sucursal);
        db.Cajas.AddRange(NuevaCaja("Caja 1", "A", "PRINCIPAL"), NuevaCaja("Caja 3", "C", null), NuevaCaja("Caja 4", "D", "VIEJA", activa: false));
        await db.SaveChangesAsync();
        Assert.Null(await OpcionesInstalacion.AvisoDeOtrasCajasAsync(db, "principal"));

        db.Cajas.Add(NuevaCaja("Caja 2", "B", "CAJA2"));
        await db.SaveChangesAsync();
        var aviso = await OpcionesInstalacion.AvisoDeOtrasCajasAsync(db, "PRINCIPAL");

        Assert.Equal("Esta caja principal se instaló como «La única caja», pero hay una caja dada de alta en otras PC (Caja 2 en «CAJA2»): siguen "
            + "vendiendo, pero sin la búsqueda en la red ya no encuentran solas a esta si le cambia la IP. Si se van a seguir usando, corre otra vez el "
            + "instalador y elige «Caja principal».", aviso);
    }

    /// <summary>
    /// Con el puente (Eleventa:Activo), la tienda nueva es la que llena la lectura (2026-10-31): mientras no hay tienda, la lectura
    /// espera sin preguntarle nada a eleventa; al crearla, se lee al momento y llegan el catálogo y los usuarios, sin errores.
    /// </summary>
    [FactConFirebird]
    public async Task Con_el_puente_la_tienda_nueva_es_la_que_llena_la_lectura_de_eleventa()
    {
        await using var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO) VALUES (3, 'María López', 'cajera2', '1')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO) VALUES (7, 'Luis Pérez', 'luis', '1')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        await e.ProductoAsync(10, "7501035102446", "Pluma punto fino", depto: 5, costo: 4, precio: 9, existencia: 50);
        await e.ProductoAsync(11, "7501035102447", "Lápiz del 2", depto: 5, costo: 2, precio: 5, existencia: 30);
        // Solo leer: en una tienda nueva no se le escribe nada a eleventa (Escribir sigue apagado).
        await using var servicio = await ServicioDePrueba.CrearSinTiendaAsync(
            [.. PilotoDePrueba.Ajustes(e.Servidor, leer: true).Where(a => a.Item1 != "Eleventa:Escribir"), ("Eleventa:Escribir", "false")]);
        var http = servicio.Fabrica.CreateClient();
        var antes = await PilotoDePrueba.EsperarAsync(async () => (await http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!,
            x => x.Error is not null);
        Assert.Equal(LecturaEleventa.SinTienda, antes.Error);

        var creada = await Leer<TiendaNuevaDto>(await http.PostAsJsonAsync("api/alta/tienda-nueva", Solicitud()));

        var despues = await PilotoDePrueba.EsperarAsync(async () => (await http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!,
            x => x.Bien || (x.Error is { } error && error != LecturaEleventa.SinTienda));
        Assert.True(despues.Bien, despues.Error);
        await servicio.CatalogoAlDiaAsync();
        var encontrados = (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>(
            $"api/productos/buscar?q=pluma%20punto%20fino&sucursalId={creada.Alta.Caja.SucursalId}"))!;
        Assert.Contains(encontrados, r => r.Codigo == "7501035102446");
        // Los usuarios de eleventa llegan sin PIN a la tienda nueva; el dueño se queda como la primera cuenta, con su PIN.
        var fichas = await Leer<List<FichaUsuarioDto>>(await http.GetAsync($"api/cajas/{creada.Alta.Caja.Id}/usuarios"));
        Assert.Equal(new[] { "Luis Pérez", "María López", "Mario Álvarez" }.Order(StringComparer.Ordinal), fichas.Select(f => f.Nombre).Order(StringComparer.Ordinal));
        await using var db = servicio.Base();
        Assert.Equal(2, await db.Usuarios.CountAsync(u => u.EleventaId != null && u.SucursalId == creada.Alta.Caja.SucursalId && u.PinHash == ""));
        Assert.True((await db.Usuarios.SingleAsync(u => u.Id == creada.Sesion.UsuarioId)).PrimeraCuenta);
    }
}
