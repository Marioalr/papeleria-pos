using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Nucleo;

public class CajaTests
{
    private static readonly DateTime Ahora = new(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("1234", true)]
    [InlineData("123456", true)]
    [InlineData("123", false)]
    [InlineData("1234567", false)]
    [InlineData("12a4", false)]
    public void El_pin_es_de_4_a_6_digitos(string pin, bool valido)
    {
        Assert.Equal(valido, Pin.EsValido(pin));
    }

    [Fact]
    public void El_pin_se_guarda_con_hash_y_sal()
    {
        var a = Pin.Hash("4821");
        var b = Pin.Hash("4821");

        Assert.DoesNotContain("4821", a);
        Assert.NotEqual(a, b);
        Assert.True(Pin.Verificar("4821", a));
        Assert.False(Pin.Verificar("4822", a));
    }

    [Fact]
    public void Cinco_intentos_fallidos_bloquean_cinco_minutos()
    {
        var ana = new Usuario { Nombre = "Ana", NombreUsuario = "ana" };
        ana.EstablecerPin("4821");

        for (var i = 0; i < Usuario.IntentosPermitidos - 1; i++)
            Assert.Equal(ResultadoPin.Incorrecto, ana.VerificarPin("0000", Ahora));
        Assert.Equal(ResultadoPin.Bloqueado, ana.VerificarPin("0000", Ahora));

        Assert.Equal(ResultadoPin.Bloqueado, ana.VerificarPin("4821", Ahora.AddMinutes(4)));
        Assert.Equal(ResultadoPin.Correcto, ana.VerificarPin("4821", Ahora.AddMinutes(6)));
    }

    [Fact]
    public void Un_usuario_inactivo_no_entra()
    {
        var ana = new Usuario { Nombre = "Ana", NombreUsuario = "ana", Activo = false };
        ana.EstablecerPin("4821");

        Assert.Equal(ResultadoPin.Incorrecto, ana.VerificarPin("4821", Ahora));
    }

    [Fact]
    public void Corte_z_compara_lo_contado_con_lo_esperado()
    {
        var turno = new Turno { FondoCentavos = 50000 };
        var venta = new Venta { TurnoId = turno.Id, Serie = "A" };
        var producto = new ProductoEnVenta(Ids.Nuevo(), "Cuaderno", Impuesto.Iva16, UnidadBase.Pieza, true);
        venta.AgregarProducto(producto, new Presentacion { VarianteId = producto.VarianteId, Nombre = "Pieza" }, Cantidad.Piezas(2),
            [new Precio { VarianteId = producto.VarianteId, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 4500 }]);
        venta.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 10000)], Ahora); // $90 con cambio de $10
        MovimientoCaja[] movimientos =
        [
            new() { TurnoId = turno.Id, Tipo = TipoMovimientoCaja.Salida, MontoCentavos = 20000, Motivo = "Pago a proveedor" },
            new() { TurnoId = turno.Id, Tipo = TipoMovimientoCaja.Entrada, MontoCentavos = 5000, Motivo = "Cambio" },
        ];
        // Se cuenta y se escribe el total (2026-10-14): ya no billete por billete.
        var corte = CalculoCorte.Calcular(turno, [venta], movimientos, TipoCorte.Z, Ids.Nuevo(), Ahora, contadoCentavos: 32000);

        Assert.Equal(9000, corte.EfectivoVentasCentavos);
        Assert.Equal(50000 + 9000 + 5000 - 20000, corte.EsperadoCentavos);
        Assert.Equal(32000, corte.ContadoCentavos);
        Assert.Equal(32000 - 44000, corte.DiferenciaCentavos);
        Assert.Empty(corte.Conteo);
    }

    [Fact]
    public void Corte_x_no_lleva_lo_contado_y_z_lo_exige()
    {
        var turno = new Turno { FondoCentavos = 10000 };

        var x = CalculoCorte.Calcular(turno, [], [], TipoCorte.X, Ids.Nuevo(), Ahora);

        Assert.Null(x.DiferenciaCentavos);
        Assert.Equal(10000, x.EsperadoCentavos);
        Assert.Throws<ArgumentException>(() => CalculoCorte.Calcular(turno, [], [], TipoCorte.Z, Ids.Nuevo(), Ahora));
        Assert.Throws<ArgumentException>(() => CalculoCorte.Calcular(turno, [], [], TipoCorte.Z, Ids.Nuevo(), Ahora, contadoCentavos: -1));
    }

    [Fact]
    public void El_conteo_de_una_caja_de_antes_se_vuelve_su_total()
    {
        // Lo que sube en la bandeja una caja de antes del 2026-10-14 que hizo el corte sin conexión.
        Assert.Equal(32000, Denominaciones.Total(new Dictionary<long, int> { [20000] = 1, [5000] = 2, [1000] = 2 }));
        Assert.Throws<ArgumentException>(() => Denominaciones.Total(new Dictionary<long, int> { [300] = 1 }));
        Assert.Throws<ArgumentException>(() => Denominaciones.Total(new Dictionary<long, int> { [1000] = -1 }));
    }
}
