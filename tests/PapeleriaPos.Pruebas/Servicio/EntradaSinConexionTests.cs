using System.Net.Http.Json;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La entrada con fichas sin la caja principal (entrega 6): la copia local ya trae lo que necesitan las fichas (los usuarios
/// activos, las reglas y el turno de la caja), salen las mismas que en línea, y el PIN se revisa con la copia, con el mismo
/// bloqueo por intentos.
/// </summary>
public sealed class EntradaSinConexionTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-local-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    [Fact]
    public async Task Sin_la_caja_principal_salen_las_mismas_fichas_y_el_pin_se_revisa_con_la_copia()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);

        // En línea: baja su copia, el cajero entra con su ficha y abre su turno.
        await contingencia.LatidoAsync();
        var cajero = (await cliente.FichasAsync(caja.Id)).Single(f => f.Nombre == "Cajero de prueba");
        var sesion = await cliente.EntrarAsync(caja.Id, cajero.UsuarioId, "1111");
        _ = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, sesion.UsuarioId, 50000);
        var enLinea = await cliente.FichasAsync(caja.Id);

        // Se apaga la caja principal.
        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        Assert.False(contingencia.EnLinea);
        var sinConexion = await cliente.FichasAsync(caja.Id);

        // Las mismas fichas, letras, resúmenes y quién hace el corte; y el turno abierto, el de esta caja.
        Assert.Equal(enLinea, sinConexion);
        Assert.Equal([cajero.UsuarioId], sinConexion.Where(f => f.TurnoAbierto).Select(f => f.UsuarioId));
        Assert.Contains(sinConexion, f => f.Nombre == "Encargado de prueba" && f.HaceCorte);

        // El PIN, con la copia: el bueno entra; el equivocado dice cuántos intentos quedan, y al quinto hay que esperar.
        Assert.Equal(sesion, await cliente.EntrarAsync(caja.Id, cajero.UsuarioId, "1111"));
        var encargado = sinConexion.Single(f => f.Nombre == "Encargado de prueba");
        var mensajes = new List<string>();
        for (var i = 0; i < 5; i++)
            mensajes.Add((await Assert.ThrowsAsync<ErrorCaja>(() => cliente.EntrarAsync(caja.Id, encargado.UsuarioId, "0000"))).Message);
        Assert.Equal(
            ["PIN incorrecto. Te quedan 4 intentos.", "PIN incorrecto. Te quedan 3 intentos.", "PIN incorrecto. Te quedan 2 intentos.",
                "PIN incorrecto. Te queda 1 intento.", "Demasiados intentos: espera unos minutos."], mensajes);
        var bloqueado = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.EntrarAsync(caja.Id, encargado.UsuarioId, "2222"));
        Assert.Equal("Demasiados intentos: espera unos minutos.", bloqueado.Message);
        // Quien no está en la copia de esta caja no entra.
        var nadie = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.EntrarAsync(caja.Id, Guid.NewGuid(), "1111"));
        Assert.Equal("Ese usuario no entra en esta caja.", nadie.Message);
    }
}
