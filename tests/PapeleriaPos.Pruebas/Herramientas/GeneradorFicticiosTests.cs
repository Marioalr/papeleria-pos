using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Herramientas.Ficticios;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Pruebas.Datos;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>Un año completo, generado una sola vez para todas las pruebas de la clase.</summary>
public sealed class AnioFicticio
{
    public OpcionesFicticios Opciones { get; } = new();
    public DatosFicticios Datos { get; }

    public AnioFicticio() => Datos = GeneradorFicticios.Generar(Opciones);
}

public class GeneradorFicticiosTests(AnioFicticio anio) : IClassFixture<AnioFicticio>
{
    private DatosFicticios Datos => anio.Datos;

    [Fact]
    public void La_misma_semilla_da_los_mismos_datos()
    {
        var otra = GeneradorFicticios.Generar(anio.Opciones);

        Assert.Equal(Datos.Variantes.Select(v => v.Id), otra.Variantes.Select(v => v.Id));
        Assert.Equal(Datos.Ventas.Count, otra.Ventas.Count);
        Assert.Equal(Datos.VentasCentavos, otra.VentasCentavos);
        Assert.Equal(Datos.Movimientos.Select(m => (m.Id, m.Cantidad, m.CostoCentavos)),
            otra.Movimientos.Select(m => (m.Id, m.Cantidad, m.CostoCentavos)));
    }

    [Fact]
    public void Otra_semilla_da_otros_datos()
    {
        var otra = GeneradorFicticios.Generar(anio.Opciones with { Semilla = 7, Dias = 30 });

        Assert.NotEqual(Datos.Variantes[0].Id, otra.Variantes[0].Id);
    }

    [Fact]
    public void Los_ids_son_uuid_v7()
    {
        Assert.All(Datos.Productos, p => Assert.Equal(7, p.Id.Version));
    }

    [Fact]
    public void Volumen_parecido_al_negocio_real()
    {
        var porDia = Datos.Ventas.GroupBy(v => DateOnly.FromDateTime(HoraMexico.ALocal(v.FechaUtc))).Select(g => g.Count()).ToList();

        Assert.InRange(porDia.Average(), 50, 70);
        Assert.InRange(porDia.Max(), 100, 150);
        Assert.InRange(Datos.Variantes.Count, 1_000, 3_000);
    }

    [Fact]
    public void El_regreso_a_clases_casi_duplica_las_ventas()
    {
        double PromedioDiario(int mes)
        {
            var ventas = Datos.Ventas.Where(v => HoraMexico.ALocal(v.FechaUtc).Month == mes).ToList();
            return (double)ventas.Count / ventas.Select(v => HoraMexico.ALocal(v.FechaUtc).Date).Distinct().Count();
        }

        Assert.True(PromedioDiario(8) > 1.6 * PromedioDiario(3), $"Agosto {PromedioDiario(8):N0}, marzo {PromedioDiario(3):N0}");
    }

    [Fact]
    public void Las_ventas_son_en_horario_de_tienda()
    {
        Assert.All(Datos.Ventas, v => Assert.InRange(HoraMexico.ALocal(v.FechaUtc).Hour, 9, 20));
    }

    [Fact]
    public void El_inventario_vale_lo_pedido()
    {
        Assert.InRange(Datos.ValorInventarioCentavos, anio.Opciones.ValorInventarioCentavos * 0.99, anio.Opciones.ValorInventarioCentavos * 1.01);
    }

    [Fact]
    public void El_margen_es_el_de_una_papeleria()
    {
        var lineas = Datos.Ventas.Where(v => !v.Cancelada).SelectMany(v => v.Lineas).ToList();
        var margen = 1 - (double)lineas.Sum(l => l.CostoCentavos) / lineas.Sum(l => l.ImporteCentavos);

        Assert.InRange(margen, 0.30, 0.60);
    }

    [Fact]
    public void El_total_de_cada_venta_es_la_suma_de_sus_lineas()
    {
        Assert.All(Datos.Ventas, v => Assert.Equal(v.Lineas.Sum(l => l.ImporteCentavos), v.TotalCentavos));
    }

    [Fact]
    public void Cada_variante_tiene_presentacion_base_y_precio()
    {
        var bases = Datos.Presentaciones.Where(p => p.EsBase).ToLookup(p => p.VarianteId);
        var conPrecio = Datos.Precios.Select(p => p.VarianteId).ToHashSet();

        Assert.All(Datos.Variantes, v =>
        {
            Assert.Equal(Cantidad.Unidades(1), Assert.Single(bases[v.Id]).Factor);
            Assert.Contains(v.Id, conPrecio);
        });
    }

    [Fact]
    public void Las_piezas_se_venden_enteras_y_hay_ventas_con_fracciones()
    {
        var variantes = Datos.Variantes.ToDictionary(v => v.Id);
        var lineas = Datos.Ventas.SelectMany(v => v.Lineas).ToList();

        Assert.All(lineas, l => Assert.True(variantes[l.VarianteId].AdmiteCantidad(l.Cantidad)));
        Assert.Contains(lineas, l => !l.Cantidad.EsEntera);
    }

    [Fact]
    public void Los_codigos_de_fabrica_son_ean13_validos_y_no_se_repiten()
    {
        Assert.All(Datos.Codigos.Where(c => c.Origen == OrigenCodigo.Fabrica), c => Assert.True(Ean13.EsValido(c.Codigo), c.Codigo));
        Assert.Equal(Datos.Codigos.Count, Datos.Codigos.Select(c => c.Codigo).Distinct().Count());
    }

    [Fact]
    public void Trae_lo_que_el_diagnostico_debe_encontrar()
    {
        var vendidas = Datos.Ventas.SelectMany(v => v.Lineas).Select(l => l.VarianteId).ToHashSet();
        var conCodigo = Datos.Codigos.Join(Datos.Presentaciones, c => c.PresentacionId, p => p.Id, (_, p) => p.VarianteId).ToHashSet();

        Assert.NotEmpty(Datos.Resumen.SinMovimiento);
        Assert.All(Datos.Resumen.SinMovimiento, id => Assert.DoesNotContain(id, vendidas));
        Assert.NotEmpty(Datos.Resumen.SinCodigo);
        Assert.All(Datos.Resumen.SinCodigo, id => Assert.DoesNotContain(id, conCodigo));
        Assert.NotEmpty(Datos.Resumen.PrecioBajoCosto);
        Assert.Equal(15, Datos.Resumen.Duplicados.Count);
    }

    [Fact]
    public async Task Se_guarda_completo_y_las_existencias_cuadran_con_el_kardex()
    {
        var datos = GeneradorFicticios.Generar(anio.Opciones with { Dias = 30 });
        await using var baseEnMemoria = await BaseEnMemoria.CrearAsync();

        await using (var db = baseEnMemoria.Contexto())
            await GuardadoFicticios.GuardarAsync(datos, db);

        await using var lectura = baseEnMemoria.Contexto();
        Assert.Equal(datos.Variantes.Count, await lectura.Variantes.CountAsync());
        Assert.Equal(datos.Ventas.Count, await lectura.VentasHistoricas.CountAsync());
        Assert.Equal(datos.Ventas.Sum(v => v.Lineas.Count), await lectura.VentasHistoricasLineas.CountAsync());
        Assert.Equal(datos.Mapeos.Count, await lectura.MapeosImportacion.CountAsync());
        Assert.Equal(datos.Precios.Count, await lectura.PreciosHistorial.CountAsync());
        Assert.Equal(datos.ValorInventarioCentavos, await lectura.Existencias.SumAsync(e => e.CostoCentavos));
        Assert.Empty(await new RegistroInventario(lectura).VerificarExistenciasAsync());
    }
}
