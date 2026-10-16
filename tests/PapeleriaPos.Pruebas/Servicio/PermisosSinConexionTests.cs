using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Los permisos por usuario sin la caja principal (entrega 1): la copia de la caja trae los de cada usuario y los de
/// defecto, y con ella se decide igual que en línea: la sesión, las fichas, cada permiso, quién autoriza y quién hace el
/// corte. También con lo que se cambió en «Usuarios y permisos».
/// </summary>
public sealed class PermisosSinConexionTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-local-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    internal static readonly long?[] Montos = [null, 0, 1000, 1001, 20000, 20001, 99_999_999];

    internal static readonly (string Usuario, string Pin)[] DePrueba = [("cajero", "1111"), ("encargado", "2222"), ("dueno", "3333")];

    /// <summary>Lo que la caja sabe de cada quien: su sesión (con sus permisos), cada decisión, las fichas y quién hace el corte.</summary>
    internal static async Task<(List<SesionDto> Sesiones, List<DecisionDto> Decisiones, List<FichaUsuarioDto> Fichas, List<QuienAutoriza> Cortan)> LoQueSabeAsync(
        ClienteCaja cliente, CajaDto caja, TurnoDto turno)
    {
        var fichas = await cliente.FichasAsync(caja.Id);
        var sesiones = new List<SesionDto>();
        var decisiones = new List<DecisionDto>();
        foreach (var (usuario, pin) in DePrueba)
        {
            var sesion = await cliente.EntrarAsync(usuario, pin);
            sesiones.Add(sesion);
            foreach (var accion in Permisos.Acciones)
            foreach (var monto in Montos)
                decisiones.Add(await cliente.PermisoAsync(new SolicitudPermiso(sesion.UsuarioId, caja.SucursalId, accion, monto)));
        }
        var corte = await cliente.CorteInformativoAsync(turno.Id, sesiones[0].UsuarioId);
        return (sesiones, decisiones, fichas, [.. corte.QuienesCortan]);
    }

    [Fact]
    public async Task Sin_la_caja_principal_cada_quien_puede_lo_mismo_tambien_lo_que_se_cambio()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);
        await contingencia.LatidoAsync();
        var dueno = await cliente.EntrarAsync("dueno", "3333");
        var encargado = await cliente.EntrarAsync("encargado", "2222");
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);

        // En «Usuarios y permisos»: el encargado ya puede cambiar precios y el cajero ya no reimprime. La caja baja la copia
        // nueva con el siguiente latido.
        foreach (var (quien, accion, nivel) in new[]
                 {
                     (encargado.UsuarioId, AccionControlada.CambiarPrecio, NivelPermiso.Permitido),
                     (cajero.UsuarioId, AccionControlada.Reimprimir, NivelPermiso.NoPermitido),
                 })
        {
            (await servicio.Fabrica.CreateClient().PutAsJsonAsync($"api/cajas/{caja.Id}/usuarios-y-permisos/{quien}",
                new SolicitudPermisoUsuario(dueno.UsuarioId, accion, nivel))).EnsureSuccessStatusCode();
        }
        await contingencia.LatidoAsync();
        Assert.NotNull(contingencia.Paquete!.Permisos);
        var enLinea = await LoQueSabeAsync(cliente, caja, turno);

        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        Assert.False(contingencia.EnLinea);
        var sinConexion = await LoQueSabeAsync(cliente, caja, turno);

        Assert.Equal(enLinea.Sesiones, sinConexion.Sesiones);
        Assert.Equal(enLinea.Decisiones, sinConexion.Decisiones);
        Assert.Equal(enLinea.Fichas, sinConexion.Fichas);
        Assert.Equal(enLinea.Cortan, sinConexion.Cortan);
        // Lo que se cambió también vale sin conexión: el encargado autoriza el cambio de precio y el cajero ya no reimprime.
        Assert.Equal(NivelPermiso.NoPermitido, sinConexion.Sesiones[0].Permiso(AccionControlada.Reimprimir)!.Nivel);
        Assert.Equal(["Encargado de prueba", "Dueño de prueba"], sinConexion.Sesiones[0].Permiso(AccionControlada.CambiarPrecio)!.Autorizan.Select(a => a.Nombre));
        var autorizacion = await cliente.AutorizarAsync(new SolicitudAutorizacion(AccionControlada.CambiarPrecio, caja.SucursalId, caja.Id, cajero.UsuarioId,
            "encargado", "2222", 1500, "Precio"));
        Assert.Equal("Encargado de prueba", autorizacion.Autorizo);
        var negada = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.AutorizarAsync(new SolicitudAutorizacion(AccionControlada.CambiarPrecio, caja.SucursalId,
            caja.Id, encargado.UsuarioId, "cajero", "1111", 1500, "Precio")));
        Assert.Equal("Cajero de prueba no puede autorizar esto.", negada.Message);
    }
}

/// <summary>
/// Una caja que se actualiza con su copia de antes de la entrega 1 (con las reglas por rol y sin los permisos): no se cae,
/// decide como lo dejaría la migración (igual que en línea) y la vuelve a bajar en cuanto puede.
/// </summary>
public sealed class CopiaDeAntesTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-local-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    [Fact]
    public async Task Una_copia_de_antes_decide_igual_y_se_vuelve_a_bajar()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var almacen = new AlmacenLocal(_baseLocal);
        var nueva = new Contingencia(almacen, http, conexion);
        var cliente = new ClienteCaja(http, conexion, nueva);
        await nueva.LatidoAsync();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        var enLinea = await PermisosSinConexionTests.LoQueSabeAsync(cliente, caja, turno);

        // La copia como la guardaba una caja de antes: con las reglas por rol de la sucursal, sin los permisos.
        await almacen.GuardarCopiaAsync(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(nueva.Paquete! with { Permisos = null }, AlmacenLocal.Json));
        interruptor.Caido = true;
        var deAntes = new Contingencia(almacen, http, conexion);
        var conCopiaDeAntes = new ClienteCaja(http, conexion, deAntes);
        await deAntes.LatidoAsync();
        Assert.Null(deAntes.Paquete!.Permisos);
        var sinConexion = await PermisosSinConexionTests.LoQueSabeAsync(conCopiaDeAntes, caja, turno);

        Assert.Equal(enLinea.Sesiones, sinConexion.Sesiones);
        Assert.Equal(enLinea.Decisiones, sinConexion.Decisiones);
        Assert.Equal(enLinea.Fichas, sinConexion.Fichas);
        Assert.Equal(enLinea.Cortan, sinConexion.Cortan);

        // Vuelve la caja principal: baja la copia nueva.
        interruptor.Caido = false;
        await deAntes.LatidoAsync();
        Assert.NotNull(deAntes.Paquete!.Permisos);
    }

    /// <summary>Lo que contesta un servicio de antes de la entrega 1: la copia, sin los permisos.</summary>
    private sealed class ServicioDeAntes(HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        public int Copias { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var respuesta = await base.SendAsync(request, cancellationToken);
            if (!request.RequestUri!.AbsolutePath.EndsWith("/paquete", StringComparison.Ordinal))
                return respuesta;
            Copias++;
            var json = System.Text.Json.Nodes.JsonNode.Parse(await respuesta.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            json.Remove("permisos");
            respuesta.Content = new StringContent(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            return respuesta;
        }
    }

    /// <summary>
    /// Una caja ya actualizada con la caja principal todavía de antes: su copia no trae los permisos y la caja decide con las
    /// reglas, sin volverla a bajar en cada latido (solo cuando cambia, como siempre).
    /// </summary>
    [Fact]
    public async Task Con_un_servicio_de_antes_no_baja_la_copia_en_cada_latido()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var deAntes = new ServicioDeAntes(servicio.Fabrica.Server.CreateHandler());
        var interruptor = new Interruptor(deAntes);
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);

        for (var i = 0; i < 3; i++)
            await contingencia.LatidoAsync();

        Assert.Equal(1, deAntes.Copias);
        Assert.Null(contingencia.Paquete!.Permisos);

        // Sin la caja principal decide con las reglas de esa copia, como antes: el encargado hace el corte y el cajero no.
        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        Assert.False(contingencia.EnLinea);
        var cliente = new ClienteCaja(http, conexion, contingencia);
        Assert.Equal(NivelPermiso.NoPermitido, (await cliente.EntrarAsync("cajero", "1111")).Permiso(AccionControlada.CorteZ)!.Nivel);
        Assert.Equal(NivelPermiso.Permitido, (await cliente.EntrarAsync("encargado", "2222")).Permiso(AccionControlada.CorteZ)!.Nivel);
    }
}
