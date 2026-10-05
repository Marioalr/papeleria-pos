using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Descuentos en una caja sin conexión: la regla se revisa con la copia local y el descuento sube con la venta.</summary>
public sealed class DescuentosSinConexionTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-local-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    [Fact]
    public async Task Sin_conexion_la_regla_del_descuento_sigue_y_al_volver_sube_con_la_venta()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var maestros = new ClienteEspecialDto(Guid.NewGuid(), "Maestros de la secundaria", 2000, null, true);
        (await local.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/clientes-especiales",
            new SolicitudClientesEspeciales("dueno", "3333", [maestros]))).EnsureSuccessStatusCode();

        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);
        await contingencia.LatidoAsync();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        var busqueda = (await cliente.BuscarAsync("pluma punto fino", caja.SucursalId)).First(r => r.Codigo is not null);
        var pluma = (await cliente.PorCodigoAsync(busqueda.Codigo!, caja.SucursalId))!;
        SolicitudCobro Cobro(int centesimas, string motivo, Guid? cliente = null, Guid? autorizacion = null) =>
            new(caja.Id, turno.Id, cajero.UsuarioId,
                [new LineaSolicitada(pluma.VarianteId, pluma.Presentacion.Id, 4000, pluma.Descripcion, DescuentoCentesimas: centesimas, MotivoDescuento: motivo)],
                [new CobroSolicitado(FormaPago.Efectivo, 100000)], ClienteEspecialId: cliente, AutorizacionDescuentoId: autorizacion);

        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        Assert.False(contingencia.EnLinea);

        var clientes = await cliente.ClientesEspecialesAsync(caja.SucursalId);
        var requiere = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CobrarAsync(Cobro(1500, "Pasta doblada")));
        var autorizacion = await cliente.AutorizarAsync(new SolicitudAutorizacion(AccionControlada.Descuento, caja.SucursalId, caja.Id, cajero.UsuarioId,
            "encargado", "2222", 1500, "Descuento"));
        var conDescuento = await cliente.CobrarAsync(Cobro(1500, "Pasta doblada", autorizacion: autorizacion.Id));
        var deCliente = await cliente.CobrarAsync(Cobro(2000, "Cliente especial: Maestros de la secundaria", cliente: maestros.Id));
        var sinCosto = await cliente.BajoCostoAsync(new SolicitudBajoCosto(caja.Id, [new LineaSolicitada(pluma.VarianteId, pluma.Presentacion.Id, 1000)]));

        Assert.Contains(clientes, c => c.Id == maestros.Id);
        Assert.Equal("requiere-autorizacion", requiere.Codigo);
        Assert.Equal(1500, Assert.Single(conDescuento.Lineas).DescuentoCentavos * 10000 / (conDescuento.TotalCentavos + conDescuento.DescuentoCentavos));
        Assert.True(deCliente.DescuentoCentavos > 0);
        Assert.Equal(0, sinCosto.FaltanteCentavos);           // sin conexión no se sabe el costo

        interruptor.Caido = false;
        await contingencia.LatidoAsync();
        Assert.Equal(0, contingencia.Pendientes);

        using var alcance = servicio.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        var subida = await db.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == deCliente.VentaId);
        Assert.Equal(deCliente.TotalCentavos, subida.TotalCentavos);
        Assert.Equal(maestros.Id, subida.ClienteEspecialId);
        Assert.Equal(2000, subida.Lineas.Single().DescuentoCentesimas);
        Assert.True(await db.Bitacora.AnyAsync(b => b.VentaId == conDescuento.VentaId && b.Accion == AccionControlada.Descuento
            && b.Resultado == ResultadoBitacora.Autorizado && b.EnContingencia));
    }
}
