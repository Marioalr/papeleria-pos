using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Telegram;

namespace PapeleriaPos.Pruebas.Servicio;

public class ConteoCompletoApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private long Existencia(Guid variante)
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        return alcance.ServiceProvider.GetRequiredService<PosDbContext>().Existencias.AsNoTracking()
            .Where(e => e.VarianteId == variante).Select(e => e.Cantidad.Milesimas).FirstOrDefault();
    }

    [Fact]
    public async Task Varios_cuentan_un_departamento_y_al_cerrarlo_lo_contado_reemplaza_la_existencia()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));

        // El departamento con más productos por pieza con existencia, y uno de otro departamento.
        Guid departamento, a, b, c, d, ajeno;
        using (var alcance = servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = dueno.UsuarioId, ChatId = 8282, VinculadoUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var conExistencia = await (from v in db.Variantes
                                       join e in db.Existencias on v.Id equals e.VarianteId
                                       where v.Activa && v.Producto!.Activo && v.Producto.Tipo == TipoProducto.Mercancia && v.UnidadBase == UnidadBase.Pieza
                                           && e.SucursalId == caja.SucursalId && e.Cantidad > Cantidad.Piezas(3)
                                       select new { v.Id, v.Producto!.DepartamentoId }).ToListAsync();
            var grupo = conExistencia.GroupBy(x => x.DepartamentoId).OrderByDescending(g => g.Count()).First();
            departamento = grupo.Key;
            (a, b, c, d) = (grupo.ElementAt(0).Id, grupo.ElementAt(1).Id, grupo.ElementAt(2).Id, grupo.ElementAt(3).Id);
            ajeno = conExistencia.First(x => x.DepartamentoId != departamento).Id;
        }
        var (existenciaA, existenciaB, existenciaC) = (Existencia(a), Existencia(b), Existencia(c));

        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync($"api/sucursales/{caja.SucursalId}/conteos-completos",
            new SolicitudAbrirConteo(cajero.UsuarioId, departamento))).StatusCode);
        var abierto = await Leer<ConteoDepartamentoDto>(await http.PostAsJsonAsync($"api/sucursales/{caja.SucursalId}/conteos-completos",
            new SolicitudAbrirConteo(encargado.UsuarioId, departamento)));
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync($"api/sucursales/{caja.SucursalId}/conteos-completos",
            new SolicitudAbrirConteo(encargado.UsuarioId, departamento))).StatusCode);
        var id = abierto.Id;

        async Task<ConteoDepartamentoDto> Capturar(SesionDto quien, Guid variante, long piezas, Guid? captura = null) =>
            await Leer<ConteoDepartamentoDto>(await http.PostAsJsonAsync($"api/conteos-completos/{id}/capturas",
                new SolicitudCaptura(captura ?? Guid.NewGuid(), quien.UsuarioId, variante, piezas * 1000)));

        // A: el anaquel (cajero) y la bodega (encargado); la misma captura dos veces cuenta una.
        var anaquel = Guid.NewGuid();
        await Capturar(cajero, a, 2, anaquel);
        await Capturar(cajero, a, 2, anaquel);
        await Capturar(encargado, a, 5);
        // Después de contar A se vende una: al cerrar sigue contando.
        using (var alcance = servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            var inventario = new RegistroInventario(db);
            var variante = await db.Variantes.SingleAsync(v => v.Id == a);
            await inventario.RegistrarAsync(Kardex.Salida(caja.SucursalId, variante, TipoMovimiento.Venta, Cantidad.Piezas(1),
                await inventario.ExistenciaAsync(caja.SucursalId, a), 0, DateTime.UtcNow));
        }
        await Capturar(cajero, b, existenciaB / 1000);
        var equivocada = await Capturar(cajero, c, 99);
        var deEncargado = (await Capturar(encargado, c, 1)).Capturas.First(x => x.UsuarioId == encargado.UsuarioId && x.VarianteId == c);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync($"api/conteos-completos/{id}/capturas/{deEncargado.Id}/quitar",
            new SolicitudConteoCompleto(cajero.UsuarioId))).StatusCode);
        var sinError = await Leer<ConteoDepartamentoDto>(await http.PostAsJsonAsync(
            $"api/conteos-completos/{id}/capturas/{equivocada.Capturas.First(x => x.VarianteId == c && x.Cantidad == 99).Id}/quitar",
            new SolicitudConteoCompleto(cajero.UsuarioId)));
        var deOtro = await http.PostAsJsonAsync($"api/conteos-completos/{id}/capturas", new SolicitudCaptura(Guid.NewGuid(), cajero.UsuarioId, ajeno, 1000));

        // A ciegas: ni el cajero ni el encargado (contando) ven lo que dice el sistema.
        var delCajero = await Leer<ConteoDepartamentoDto>(await http.GetAsync($"api/conteos-completos/{id}?usuarioId={cajero.UsuarioId}&revision=true"));
        var contando = await Leer<ConteoDepartamentoDto>(await http.GetAsync($"api/conteos-completos/{id}?usuarioId={encargado.UsuarioId}"));
        var revision = await Leer<ConteoDepartamentoDto>(await http.GetAsync($"api/conteos-completos/{id}?usuarioId={encargado.UsuarioId}&revision=true"));
        var lista = await Leer<List<DepartamentoConteoDto>>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/conteos-completos"));

        Assert.Equal(1m, sinError.Renglones.Single(r => r.VarianteId == c).Contado);
        Assert.Equal(HttpStatusCode.BadRequest, deOtro.StatusCode);
        Assert.Contains("se cuenta con ese departamento", (await deOtro.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.False(delCajero.Revision);
        Assert.All(delCajero.Renglones.Concat(contando.Renglones), r => Assert.Null(r.Sistema));
        Assert.Equal(7m, contando.Renglones.Single(r => r.VarianteId == a).Contado);
        Assert.Equal(3, contando.Contados);
        Assert.True(revision.Revision);
        Assert.Equal((existenciaA / 1000m, 7m - existenciaA / 1000m), (revision.Renglones.Single(r => r.VarianteId == a).Sistema!.Value,
            revision.Renglones.Single(r => r.VarianteId == a).Diferencia!.Value));
        Assert.Equal(0m, revision.Renglones.Single(r => r.VarianteId == b).Diferencia);
        Assert.Null(revision.Renglones.Single(r => r.VarianteId == d).Contado);
        Assert.NotNull(revision.NetoCentavos);
        var enLista = lista.Single(x => x.DepartamentoId == departamento);
        Assert.Equal((3, 2), (enLista.Abierto!.Contados, enLista.Abierto.Personas.Count));

        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync($"api/conteos-completos/{id}/cerrar", new SolicitudConteoCompleto(cajero.UsuarioId))).StatusCode);
        var cerrado = await Leer<ConteoDepartamentoDto>(await http.PostAsJsonAsync($"api/conteos-completos/{id}/cerrar", new SolicitudConteoCompleto(encargado.UsuarioId)));
        var tarde = await http.PostAsJsonAsync($"api/conteos-completos/{id}/capturas", new SolicitudCaptura(Guid.NewGuid(), cajero.UsuarioId, d, 1000));

        Assert.Equal(EstadoConteoDepartamento.Cerrado, cerrado.Estado);
        Assert.Equal(HttpStatusCode.BadRequest, tarde.StatusCode);
        // A: 7 contadas menos la que se vendió después de contar. B: igual. C: 1. D: nadie la contó, queda en 0.
        Assert.Equal(6000, Existencia(a));
        Assert.Equal(existenciaB, Existencia(b));
        Assert.Equal(1000, Existencia(c));
        Assert.Equal(0, Existencia(d));
        Assert.True(existenciaC != 1000);
        Assert.Equal(cerrado.NetoCentavos, cerrado.Renglones.Sum(r => r.CostoDiferenciaCentavos ?? 0));

        using var otro = servicio.Fabrica.Services.CreateScope();
        var base_ = otro.ServiceProvider.GetRequiredService<PosDbContext>();
        var movimientos = await base_.MovimientosInventario.AsNoTracking().Where(m => m.Tipo == TipoMovimiento.Conteo && m.DocumentoId == id).ToListAsync();
        Assert.Contains(movimientos, m => m.VarianteId == d && m.Cantidad.Milesimas < 0);
        Assert.DoesNotContain(movimientos, m => m.VarianteId == b);
        Assert.Equal(cerrado.NetoCentavos, movimientos.Sum(m => m.CostoCentavos));
        Assert.Empty(await new RegistroInventario(base_).VerificarExistenciasAsync());
        Assert.True(await base_.Bitacora.AnyAsync(b => b.Accion == AccionControlada.ConteoCompleto && b.Detalle.StartsWith("Cerró el conteo")));
        var aviso = Assert.Single(await base_.MensajesTelegram.AsNoTracking().Where(m => m.ChatId == 8282 && m.Tipo == TipoAlerta.ConteoCompleto).ToListAsync());
        Assert.Contains("quedaron en 0", aviso.Texto);

        var despues = await Leer<List<DepartamentoConteoDto>>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/conteos-completos"));
        var dep = despues.Single(x => x.DepartamentoId == departamento);
        Assert.Null(dep.Abierto);
        Assert.Equal((cerrado.Id, "Encargado de prueba"), (dep.UltimoCerrado!.Id, dep.UltimoCerrado.Usuario));
    }

    [Fact]
    public async Task Cancelar_descarta_lo_contado_y_la_pagina_del_celular_esta_en_la_red()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var departamentos = await Leer<List<DepartamentoConteoDto>>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/conteos-completos"));
        var departamento = departamentos.Where(x => x.Abierto is null).OrderBy(x => x.Productos).First();
        var abierto = await Leer<ConteoDepartamentoDto>(await http.PostAsJsonAsync($"api/sucursales/{caja.SucursalId}/conteos-completos",
            new SolicitudAbrirConteo(encargado.UsuarioId, departamento.DepartamentoId)));
        var producto = abierto.Renglones.First(r => r.Unidad == UnidadBase.Pieza);
        var antes = Existencia(producto.VarianteId);

        await Leer<ConteoDepartamentoDto>(await http.PostAsJsonAsync($"api/conteos-completos/{abierto.Id}/capturas",
            new SolicitudCaptura(Guid.NewGuid(), encargado.UsuarioId, producto.VarianteId, antes + 5000)));
        var cancelado = await Leer<ConteoDepartamentoDto>(await http.PostAsJsonAsync($"api/conteos-completos/{abierto.Id}/cancelar",
            new SolicitudConteoCompleto(encargado.UsuarioId)));
        var celular = await Leer<DireccionCelularDto>(await http.GetAsync("api/conteo-celular"));
        var pagina = await http.GetAsync("conteo");

        Assert.Equal(EstadoConteoDepartamento.Cancelado, cancelado.Estado);
        Assert.Equal(antes, Existencia(producto.VarianteId));
        Assert.All(celular.Direcciones, d => Assert.EndsWith("/conteo", d));
        if (celular.Direcciones.Count > 0)
            Assert.StartsWith("<svg", celular.QrSvg!.TrimStart());
        Assert.Equal(HttpStatusCode.OK, pagina.StatusCode);
    }
}
