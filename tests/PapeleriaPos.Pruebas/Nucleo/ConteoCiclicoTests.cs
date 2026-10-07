using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Inventario;
using C = PapeleriaPos.Nucleo.Inventario.SeleccionConteo.Candidato;

namespace PapeleriaPos.Pruebas.Nucleo;

public class ConteoCiclicoTests
{
    private static readonly Guid Negativo = Guid.NewGuid(), Caro = Guid.NewGuid(), Barato = Guid.NewGuid(), RecienContado = Guid.NewGuid(),
        SinNada = Guid.NewGuid(), BaratoOlvidado = Guid.NewGuid();

    [Fact]
    public void Primero_lo_negativo_y_luego_lo_que_mas_pesa_por_los_dias_sin_contar()
    {
        var candidatos = new[]
        {
            new C(Caro, 500_000, 200_000, 30, false),
            new C(Barato, 2_000, 1_000, 30, false),
            new C(BaratoOlvidado, 2_000, 1_000, null, false),
            new C(RecienContado, 900_000, 900_000, 0, false),
            new C(SinNada, 0, 0, null, false),
            new C(Negativo, -5_000, 3_000, 2, true),
        };

        var elegidos = SeleccionConteo.Elegir(candidatos, 4);

        // Lo recién contado (0 días) y lo que no vale ni se vende no se cuentan; lo que nunca se contó sube.
        Assert.Equal([Negativo, Caro, BaratoOlvidado, Barato], elegidos);
    }

    [Fact]
    public void Un_producto_se_cuenta_una_vez_al_dia()
    {
        var renglon = new ConteoCiclicoRenglon { VarianteId = Caro };
        renglon.Contar(Cantidad.Piezas(8), Cantidad.Piezas(10), -2000, Guid.NewGuid(), DateTime.UtcNow);

        Assert.Equal(Cantidad.Piezas(-2), renglon.Diferencia);
        Assert.Throws<InvalidOperationException>(() => renglon.Contar(Cantidad.Piezas(10), Cantidad.Piezas(10), 0, Guid.NewGuid(), DateTime.UtcNow));
    }
}
