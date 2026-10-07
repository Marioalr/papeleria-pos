using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Nucleo;

public class ConteoDepartamentoTests
{
    private static readonly DateTime Ahora = new(2026, 10, 7, 17, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Ana = Guid.NewGuid(), Luis = Guid.NewGuid(), Cuaderno = Guid.NewGuid(), Pluma = Guid.NewGuid();

    private static ConteoDepartamento Abierto() =>
        new() { SucursalId = Guid.NewGuid(), DepartamentoId = Guid.NewGuid(), AbiertoPorId = Ana, AbiertoUtc = Ahora };

    [Fact]
    public void Lo_que_cuenta_cada_quien_se_suma_y_se_compara_contra_la_existencia_de_la_primera_captura()
    {
        var conteo = Abierto();
        conteo.Capturar(Guid.NewGuid(), Cuaderno, Cantidad.Piezas(12), Cantidad.Piezas(20), Ana, Ahora);
        var bodega = conteo.Capturar(Guid.NewGuid(), Cuaderno, Cantidad.Piezas(6), Cantidad.Piezas(19), Luis, Ahora.AddMinutes(5));
        conteo.Capturar(Guid.NewGuid(), Pluma, Cantidad.Piezas(3), Cantidad.Piezas(3), Luis, Ahora.AddMinutes(6));

        Assert.Equal(Cantidad.Piezas(18), conteo.Contado()[Cuaderno]);
        // Entre las dos capturas se vendió uno: la existencia de referencia es la de cuando se empezó a contar.
        Assert.Equal(Cantidad.Piezas(20), conteo.EsperadoDe(Cuaderno));

        conteo.Quitar(bodega, Luis, puedeQuitarDeOtros: false, Ahora.AddMinutes(7));
        Assert.Equal(Cantidad.Piezas(12), conteo.Contado()[Cuaderno]);
        Assert.Null(conteo.EsperadoDe(Guid.NewGuid()));
    }

    [Fact]
    public void Lo_de_otro_solo_lo_quita_el_encargado_y_ya_cerrado_no_se_captura()
    {
        var conteo = Abierto();
        var deAna = conteo.Capturar(Guid.NewGuid(), Cuaderno, Cantidad.Piezas(4), Cantidad.Piezas(4), Ana, Ahora);

        Assert.Throws<InvalidOperationException>(() => conteo.Quitar(deAna, Luis, puedeQuitarDeOtros: false, Ahora));
        Assert.Throws<ArgumentException>(() => conteo.Capturar(Guid.NewGuid(), Pluma, Cantidad.Piezas(-1), Cantidad.Cero, Luis, Ahora));
        conteo.Quitar(deAna, Luis, puedeQuitarDeOtros: true, Ahora);
        Assert.Throws<InvalidOperationException>(() => conteo.Quitar(deAna, Ana, puedeQuitarDeOtros: true, Ahora));

        conteo.Cerrar([], Ana, Ahora);
        Assert.Equal(EstadoConteoDepartamento.Cerrado, conteo.Estado);
        Assert.Throws<InvalidOperationException>(() => conteo.Capturar(Guid.NewGuid(), Pluma, Cantidad.Piezas(1), Cantidad.Cero, Luis, Ahora));
        Assert.Throws<InvalidOperationException>(() => conteo.Cancelar(Ana, Ahora));
    }

    [Fact]
    public void Lo_que_nadie_conto_queda_en_cero()
    {
        var renglon = new ConteoDepartamentoRenglon { VarianteId = Pluma, Contado = null, Esperado = Cantidad.Piezas(7) };

        Assert.Equal(Cantidad.Piezas(-7), renglon.Diferencia);
    }
}
