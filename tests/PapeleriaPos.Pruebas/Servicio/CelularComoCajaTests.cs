using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Servicio;
using PapeleriaPos.Servicio.Telegram;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El celular dado de alta como una caja más (decisión del dueño, 2026-11-08, docs/celular-como-caja.md): se da de alta como
/// cualquier caja, baja la copia de la caja y lo que cambió, y busca por código y por nombre, con solo su llave y lo que manda
/// la app (nada de la ventana de Windows). Nunca abre un turno: no estorba en el corte del día ni en la bitácora de red; en
/// Configuración › Cajas sí sale (para quitarle la llave si se pierde).
/// </summary>
public sealed class CelularComoCajaTests
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static FichaProductoDto Ficha(Guid departamento, string nombre, string codigo) =>
        new(null, nombre, departamento, "Marca de Prueba", TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
        [
            new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])],
                [new PrecioFichaDto(null, 1000, 2350)]),
        ]);

    [Fact]
    public async Task El_celular_se_da_de_alta_como_caja_baja_la_copia_y_no_estorba()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var principal = servicio.Fabrica.CreateClient();
        var caja1 = (await principal.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = await Leer<SesionDto>(await principal.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var encargado = await Leer<SesionDto>(await principal.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var cajero = await Leer<SesionDto>(await principal.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        await Leer<TurnoDto>(await principal.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja1.Id, cajero.UsuarioId, 50000)));

        // 1. El alta, como la de una caja de Windows: la sucursal con el PIN del dueño, y la caja nueva con su nombre, su serie y el equipo.
        var celular = servicio.Fabrica.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await celular.GetAsync("api/estado")).StatusCode);
        var sucursal = Assert.Single(await Leer<List<SucursalAltaDto>>(await celular.PostAsJsonAsync("api/alta/opciones", new SolicitudCredenciales("dueno", "3333"))));
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", sucursal.Id, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        var id = alta.Caja.Id;

        // 2. El latido (sin nada por subir: el celular no vende) y la copia completa.
        var estado = await Leer<EstadoServicioDto>(await celular.GetAsync($"api/estado?caja={id}"));
        var paquete = await Leer<PaqueteContingenciaDto>(await celular.GetAsync($"api/cajas/{id}/paquete"));
        Assert.Equal(("Celular de Luis", "CEL"), (paquete.Caja.Nombre, paquete.Caja.Serie));
        Assert.Equal(estado.VersionDatos, paquete.Version);
        Assert.NotEmpty(paquete.Productos);

        // 3. Un producto nuevo en la caja principal, con su código de 11 dígitos (como lo dejó eleventa): llega en lo que cambió.
        var departamento = (await principal.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        await Leer<FichaProductoDto>(await principal.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(encargado.UsuarioId, caja1.SucursalId, Ficha(departamento, "Corrector de cinta", "36000291452"))));
        var cambios = await Leer<CambiosCopiaDto>(await celular.GetAsync($"api/cajas/{id}/cambios?desde={paquete.Version}&existencias={paquete.VersionExistencias}"
            + $"&base={paquete.Base}&formato={paquete.Formato}&vendidos={paquete.VersionVendidos ?? 0}&elegidos={paquete.VersionElegidos ?? 0}"
            + $"&promociones={paquete.HuellaPromociones}"));
        Assert.Null(cambios.PaqueteCompleto);
        Assert.Contains(cambios.Productos, p => p.Descripcion.StartsWith("Corrector de cinta", StringComparison.Ordinal));
        Assert.Contains(cambios.Codigos, c => c.Codigo == "36000291452");
        // Con la regla de las variantes de UPC, en su copia y en el servicio: el lector manda el de 13.
        var copia = new CodigosDeEscaneo(paquete.Codigos.Concat(cambios.Codigos));
        Assert.Equal("36000291452", copia.BuscarAlEscanear("0036000291452")!.Codigo);
        var porCodigo = await Leer<ProductoVentaDto>(await celular.GetAsync($"api/productos/codigo/0036000291452?sucursalId={sucursal.Id}"));
        Assert.Equal(2350, porCodigo.Precios.Single().PrecioCentavos);
        var porNombre = await Leer<List<ResultadoBusquedaDto>>(await celular.GetAsync($"api/productos/buscar?q=corrector&sucursalId={sucursal.Id}"));
        Assert.Contains(porNombre, r => r.VarianteId == porCodigo.VarianteId);

        // 4. Se fue de la tienda hace media hora: el corte del día no dice nada de él, ni lo lista.
        servicio.Fabrica.Services.GetRequiredService<PresenciaCajas>().Visto(id, DateTime.UtcNow.AddMinutes(-30));
        var dia = await Leer<CorteDelDiaDto>(await principal.GetAsync($"api/cajas/{caja1.Id}/corte-del-dia?usuarioId={dueno.UsuarioId}"));
        Assert.Empty(dia.Avisos);
        Assert.All(dia.Turnos, t => Assert.Equal(caja1.Id, t.CajaId));
        // En el historial de cortes no tiene turnos (en su filtro sí sale, como una caja recién dada de alta: elegirlo dice que no
        // hubo turnos); en Configuración › Cajas también sale, para quitarle la llave si se pierde.
        var cortes = await Leer<HistorialCortesDto>(await principal.GetAsync($"api/cajas/{caja1.Id}/cortes?usuarioId={dueno.UsuarioId}"));
        Assert.All(cortes.Turnos, t => Assert.NotEqual(id, t.CajaId));
        Assert.Contains((await principal.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!, c => c.Id == id);
        // Una caja de Windows que sí abrió turno sigue avisando si deja de verse; el celular, no.
        var alta2 = await Leer<AltaCajaDto>(await principal.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", sucursal.Id, null, "Caja 2", "B", "PC-2")));
        await Leer<TurnoDto>(await principal.PostAsJsonAsync("api/turnos", new SolicitudTurno(alta2.Caja.Id, encargado.UsuarioId, 0)));
        servicio.Fabrica.Services.GetRequiredService<PresenciaCajas>().Visto(alta2.Caja.Id, DateTime.UtcNow.AddMinutes(-30));
        var conCaja2 = await Leer<CorteDelDiaDto>(await principal.GetAsync($"api/cajas/{caja1.Id}/corte-del-dia?usuarioId={dueno.UsuarioId}"));
        Assert.StartsWith("No se sabe de la Caja 2 desde ", Assert.Single(conCaja2.Avisos));

        // En la bitácora de red de la caja principal, igual: la Caja 2 dejó de latir; del celular, que entra y sale, nada.
        using var carpeta = new CarpetaDePrueba();
        var bitacora = new BitacoraDeRed(carpeta.Ruta, "red-servicio");
        await new CajasQueLaten(servicio.Fabrica.Services.GetRequiredService<PresenciaCajas>(),
            servicio.Fabrica.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System).RevisarAsync(bitacora, CancellationToken.None);
        var lineas = carpeta.Lineas("red-servicio");
        Assert.Contains(lineas, l => l.StartsWith("«Caja 2» dejó de latir", StringComparison.Ordinal));
        Assert.DoesNotContain(lineas, l => l.Contains("Celular de Luis", StringComparison.Ordinal));
    }
}
