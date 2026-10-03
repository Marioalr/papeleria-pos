using ClosedXML.Excel;
using PapeleriaPos.Herramientas.Diagnostico;
using PapeleriaPos.Herramientas.Ficticios;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Pruebas.Datos;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>El año ficticio, guardado en SQLite y cargado como lo hace el comando <c>diagnostico</c>.</summary>
public sealed class DiagnosticoFicticio : IAsyncLifetime
{
    public DatosFicticios Ficticios { get; private set; } = null!;
    public DatosDiagnostico Datos { get; private set; } = null!;
    public Analisis Analisis { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Ficticios = GeneradorFicticios.Generar();
        await using var baseEnMemoria = await BaseEnMemoria.CrearAsync();
        await using (var db = baseEnMemoria.Contexto())
            await GuardadoLote.GuardarAsync(Ficticios, db);
        await using (var db = baseEnMemoria.Contexto())
            Datos = await DatosDiagnostico.CargarAsync(db);
        Analisis = new Analisis(Datos);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

public class DiagnosticoTests(DiagnosticoFicticio diagnostico) : IClassFixture<DiagnosticoFicticio>
{
    private Analisis Analisis => diagnostico.Analisis;
    private AnomaliasFicticias Puestas => diagnostico.Ficticios.Resumen;

    [Fact]
    public void Encuentra_toda_la_mercancia_sin_movimiento()
    {
        var parado = Analisis.DineroParado();
        var conExistencia = Puestas.SinMovimiento.Where(diagnostico.Datos.Existencias.ContainsKey).ToList();

        Assert.NotEmpty(conExistencia);
        Assert.All(conExistencia, id => Assert.Contains(parado, f => f.Articulo.VarianteId == id && f.UltimaVenta is null));
        Assert.Equal(parado.OrderByDescending(f => f.Valor).Select(f => f.Valor), parado.Select(f => f.Valor));
    }

    [Fact]
    public void Lo_que_se_vendio_hace_poco_no_es_dinero_parado()
    {
        var limite = Analisis.UltimoDia.AddDays(-Analisis.DiasSinVenta).ToDateTime(TimeOnly.MaxValue);

        Assert.All(Analisis.DineroParado(), f => Assert.True(f.UltimaVenta is null || f.UltimaVenta <= limite));
    }

    [Fact]
    public void Abc_reparte_toda_la_venta_y_las_clases_van_en_orden()
    {
        var abc = Analisis.Abc();

        Assert.Equal(1m, Math.Round(abc[^1].PorcentajeAcumulado, 6));
        Assert.Equal(abc.OrderBy(f => f.Clase).Select(f => f.Rango), abc.Select(f => f.Rango));
        var a = abc.Where(f => f.Clase == 'A').ToList();
        Assert.InRange(a.Sum(f => f.PorcentajeVenta), 0.80m, 0.80m + a[^1].PorcentajeVenta);
        Assert.DoesNotContain(abc, f => f.Articulo.EsCobroTerceros);
    }

    [Fact]
    public void Los_departamentos_suman_toda_la_venta_y_las_recargas_van_aparte()
    {
        var departamentos = Analisis.MargenPorDepartamento();
        var total = diagnostico.Datos.Lineas.Sum(l => l.ImporteCentavos) / 100m;

        Assert.Equal(total, departamentos.Sum(d => d.Venta));
        Assert.Equal(1m, Math.Round(departamentos.Where(d => !d.CobroTerceros).Sum(d => d.PorcentajeVenta), 6));
        Assert.Single(departamentos, d => d.CobroTerceros);
    }

    [Fact]
    public void Encuentra_los_productos_sin_codigo()
    {
        var sinCodigo = Analisis.RevisarCatalogo().Where(f => f.Problema == Problemas.SinCodigo).Select(f => f.Articulo.VarianteId);

        Assert.Equal(Puestas.SinCodigo.Order(), sinCodigo.Order());
    }

    [Fact]
    public void Encuentra_los_precios_debajo_del_costo()
    {
        var bajoCosto = Analisis.RevisarCatalogo().Where(f => f.Problema == Problemas.PrecioBajoCosto).Select(f => f.Articulo.VarianteId).ToHashSet();

        Assert.Equal(Puestas.PrecioBajoCosto.ToHashSet(), bajoCosto);
    }

    [Fact]
    public void Encuentra_los_duplicados_sin_falsos_positivos()
    {
        var duplicados = Analisis.RevisarCatalogo().Where(f => f.Problema == Problemas.Duplicado).ToList();
        var marcados = duplicados.Select(f => f.Articulo.VarianteId).ToHashSet();
        var puestos = Puestas.Duplicados.SelectMany(d => new[] { d.Original, d.Duplicado }).ToHashSet();

        var sobrantes = duplicados.Where(f => !puestos.Contains(f.Articulo.VarianteId)).Select(f => $"{f.Articulo.Nombre} → {f.Detalle}");
        Assert.True(puestos.SetEquals(marcados), "No esperados:\n" + string.Join("\n", sobrantes));
    }

    [Fact]
    public void El_regreso_a_clases_pesa_mas_que_sus_dias()
    {
        var meses = Analisis.Meses();
        var agosto = meses.Single(m => m.Mes.Month == 8);
        var marzo = meses.Single(m => m.Mes.Month == 3);

        Assert.True(agosto.VentaPorDia > 1.5m * marzo.VentaPorDia);
        Assert.Equal(1m, Math.Round(meses.Sum(m => m.PorcentajeVenta), 6));
        Assert.NotEmpty(Analisis.ProductosDeTemporada());
        Assert.All(Analisis.ProductosDeTemporada(), p => Assert.True(p.Indice >= 1.5m));
    }

    [Fact]
    public void El_libro_trae_todas_las_hojas_con_sus_tablas()
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"diagnostico-{Guid.NewGuid():N}.xlsx");
        try
        {
            LibroDiagnostico.Guardar(Analisis, ruta, "ficticios.db");

            using var libro = new XLWorkbook(ruta);
            Assert.Equal(
                [LibroDiagnostico.HojaResumen, LibroDiagnostico.HojaDineroParado, LibroDiagnostico.HojaAbc,
                 LibroDiagnostico.HojaDepartamentos, LibroDiagnostico.HojaRevisar, LibroDiagnostico.HojaTemporada],
                libro.Worksheets.Select(h => h.Name));
            Assert.Equal(Analisis.Abc().Count, libro.Worksheet(LibroDiagnostico.HojaAbc).Table("Abc").DataRange.RowCount());
            Assert.Equal(Analisis.RevisarCatalogo().Count, libro.Worksheet(LibroDiagnostico.HojaRevisar).Table("Revisar").DataRange.RowCount());
            Assert.Equal("Venta propia", libro.Worksheet(LibroDiagnostico.HojaResumen).Cell(9, 1).GetString());
        }
        finally
        {
            File.Delete(ruta);
        }
    }
}
