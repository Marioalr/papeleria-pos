using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

public class UtilidadesTests
{
    [Theory]
    [InlineData("750103131130", 9)]
    [InlineData("400638133393", 1)]
    public void Digito_verificador_ean13(string doce, int esperado)
    {
        Assert.Equal(esperado, Ean13.DigitoVerificador(doce));
        Assert.True(Ean13.EsValido(Ean13.Completar(doce)));
    }

    [Theory]
    [InlineData("7501031311300")]
    [InlineData("750103131130")]
    [InlineData("75010313113A9")]
    public void Ean13_invalidos(string codigo)
    {
        Assert.False(Ean13.EsValido(codigo));
    }

    [Fact]
    public void Hora_de_mexico_sin_horario_de_verano_desde_2022()
    {
        var utc = HoraMexico.AUtc(new DateTime(2026, 8, 15, 12, 0, 0));

        Assert.Equal(new DateTime(2026, 8, 15, 18, 0, 0, DateTimeKind.Utc), utc);
        Assert.Equal(new DateTime(2026, 8, 15, 12, 0, 0), HoraMexico.ALocal(utc));
    }

    [Fact]
    public void Hora_de_mexico_con_horario_de_verano_antes_de_2022()
    {
        Assert.Equal(new DateTime(2020, 7, 1, 17, 0, 0, DateTimeKind.Utc), HoraMexico.AUtc(new DateTime(2020, 7, 1, 12, 0, 0)));
    }

    [Fact]
    public void Ids_version7_conserva_el_orden_por_hora()
    {
        var bytes = new byte[10];
        var antes = Ids.Version7(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), bytes);
        var despues = Ids.Version7(new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero), bytes);

        Assert.Equal(7, antes.Version);
        Assert.True(string.CompareOrdinal(antes.ToString(), despues.ToString()) < 0);
    }

    [Fact]
    public void Ids_usar_cambia_el_generador_solo_dentro_del_bloque()
    {
        var fijo = Guid.Parse("01900000-0000-7000-8000-000000000001");

        using (Ids.Usar(() => fijo))
            Assert.Equal(fijo, Ids.Nuevo());

        Assert.NotEqual(fijo, Ids.Nuevo());
    }
}
