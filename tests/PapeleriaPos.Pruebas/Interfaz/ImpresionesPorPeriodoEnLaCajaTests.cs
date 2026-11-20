using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Reportes › Impresiones por período en la caja (2026-11-20), contra el servicio de prueba: el botón de los reportes, los totales y
/// las tablas de hoy con el costo (sin el permiso no va: ImpresionesPorPeriodoTests), Esc de regreso y el Excel con cada hoja.
/// </summary>
public sealed class ImpresionesPorPeriodoEnLaCajaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, GuardadoDePrueba Guardado, SesionDto Dueno);

    private static async Task<Caja> PrepararAsync(ServicioDePrueba servicio)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5080/") };
        var cliente = new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var guardado = new GuardadoDePrueba();
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IGuardarArchivo>(guardado)
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, guardado, await cliente.EntrarAsync("dueno", "3333"));
    }

    private static DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));

    /// <summary>
    /// Dos impresiones de hoy, hace una hora (para que no pasen de la medianoche): una cobrada ($25, cuesta $7) y una no vendida porque
    /// salió mal ($10, cuesta $3), las dos de la PC «CAJA-PRUEBA».
    /// </summary>
    private static async Task ImprimirAsync(ServicioDePrueba servicio, CajaDto caja)
    {
        await using var db = servicio.Base();
        var cuando = DateTime.UtcNow.AddMinutes(-1);
        if (DateOnly.FromDateTime(HoraMexico.ALocal(cuando)) != Hoy)
            cuando = DateTime.UtcNow;
        await db.Set<TrabajoImpresion>().Where(t => t.Pc == "CAJA-PRUEBA").ExecuteDeleteAsync();
        foreach (var (precio, costo, cobro, motivo) in new[]
                 {
                     (2500L, 700L, EstadoCobroImpresion.Cobrada, (MotivoNoVendida?)null),
                     (1000L, 300L, EstadoCobroImpresion.NoVendida, MotivoNoVendida.SalioMal),
                 })
            db.Set<TrabajoImpresion>().Add(new TrabajoImpresion
            {
                SucursalId = caja.SucursalId, CajaId = caja.Id, Pc = "CAJA-PRUEBA", Cola = "Cola", Documento = "Tarea.pdf", ImpresoUtc = cuando, Paginas = 2,
                Copias = 1, Caras = 2, Hojas = 2, Tamano = "Carta", TipoPapel = "Bond", Calidad = "Normal", DetalleJson = "{}", PrecioCentavos = precio,
                CostoCentavos = costo, Cobro = cobro, MotivoNoVendida = motivo,
            });
        await db.SaveChangesAsync();
    }

    private static async Task<(PantallaSinNavegador Pantalla, Reportes Reportes)> AbrirAsync(Caja c, SesionDto quien)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        var reportes = await p.AbrirAsync<Reportes>(new()
        {
            [nameof(Reportes.Caja)] = c.Datos,
            [nameof(Reportes.Sesion)] = quien,
            [nameof(Reportes.Estado)] = new ReportesVistos(),
        });
        await p.EsperarElementoAsync("rep-kpi-venta");
        await p.OprimirAsync(await p.PorIdAsync("rep-impresiones"));
        await p.EsperarElementoAsync("ipp-totales");
        return (p, reportes);
    }

    private static async Task<string> TextoDeAsync(PantallaSinNavegador p, string id) => PantallaSinNavegador.Limpio((await p.PorIdAsync(id)).Texto);

    [Fact]
    public async Task Desde_los_reportes_se_ve_lo_que_se_imprimio_hoy_con_su_costo_y_Esc_regresa()
    {
        var c = await PrepararAsync(servicio);
        await ImprimirAsync(servicio, c.Datos);
        var deHoy = await c.Cliente.ImpresionesPorPeriodoAsync(c.Datos.Id, c.Dueno.UsuarioId, null, null);

        var (p, reportes) = await AbrirAsync(c, c.Dueno);
        await using (p)
        {
            Assert.Equal(TextosDeReportes.Periodo(Hoy, Hoy, Hoy), await TextoDeAsync(p, "ipp-titulo"));
            Assert.Contains("on", (await p.PorIdAsync("ipp-Hoy")).Clase);
            var totales = await TextoDeAsync(p, "ipp-totales");
            Assert.Contains($"Impresiones {Formato.Numero(deHoy.Totales.Trabajos)}", totales);
            Assert.Contains($"Cobrado {Formato.Dinero(deHoy.Totales.CobradoCentavos)}", totales);
            Assert.Contains($"Utilidad {Formato.Dinero(deHoy.Totales.UtilidadCentavos!.Value)}", totales);
            Assert.Contains("CAJA-PRUEBA", await TextoDeAsync(p, "ipp-por-pc"));
            Assert.Contains("Costo", await TextoDeAsync(p, "ipp-por-pc"));
            Assert.Contains("Salió mal", await TextoDeAsync(p, "ipp-no-vendidas"));
            Assert.Contains(TextosDeImpresionesPorPeriodo.ComoCuentaElCosto, await TextoDeAsync(p, "ipp-notas"));

            // Esc regresa al resumen, con el cursor en su botón.
            Assert.True(await p.Dispatcher.InvokeAsync(() => reportes.CerrarAsync()));
            await p.EsperarElementoAsync("rep-kpi-venta");
            Assert.False(await p.Dispatcher.InvokeAsync(() => reportes.CerrarAsync()));
            Assert.Empty(p.Errores);
        }
    }

    [Fact]
    public async Task Exportar_a_excel_lleva_cada_tabla_y_cada_impresion()
    {
        var c = await PrepararAsync(servicio);
        await ImprimirAsync(servicio, c.Datos);
        var (p, _) = await AbrirAsync(c, c.Dueno);
        await using var __ = p;

        await p.OprimirAsync(await p.PorIdAsync("ipp-excel"));
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (c.Guardado.Guardados.Count == 0 && DateTime.UtcNow < limite)
            await Task.Delay(50);

        var archivo = Assert.Single(c.Guardado.Guardados);
        Assert.Equal(($"Impresiones {Hoy:yyyy-MM-dd}.xlsx", ArchivosDeReportes.TipoExcel), (archivo.Nombre, archivo.Tipo));
        using var libro = new XLWorkbook(new MemoryStream(archivo.Datos));
        Assert.Equal(["Resumen", "Por día", "Por quién atendió", "Por PC", "Por impresora", "No vendidas", "Hojas que faltaron", "Impresiones"],
            libro.Worksheets.Select(h => h.Name));
        var impresiones = libro.Worksheet("Impresiones").Table("TablaImpresiones");
        Assert.Contains("Costo", impresiones.Fields.Select(f => f.Name));
        var deLaPrueba = impresiones.DataRange.Rows().Where(r => r.Field("PC").GetString() == "CAJA-PRUEBA").ToList();
        Assert.Equal(["Cobrada", "No vendida"], deLaPrueba.Select(r => r.Field("Cobro").GetString()).Order());
        Assert.Contains(libro.Worksheet("No vendidas").Table("TablaNoVendidas").DataRange.Rows(), r => r.Field("Motivo").GetString() == "Salió mal");
        Assert.Empty(p.Errores);
    }
}
