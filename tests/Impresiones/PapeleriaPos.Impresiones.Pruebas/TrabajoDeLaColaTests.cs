using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Lo que el agente hace con lo que Windows guardó en la cola mientras la caja estuvo cerrada (docs/plan.md, «Recomendaciones»;
/// 2026-11-09): va con la hora en que entró a la cola, no con la hora en que el agente lo vio, y lo de más de un día no se sube (se
/// cuenta como visto para quitarlo de la cola). Lo que el agente vio en la cola antes de que saliera va como siempre. El agente es solo de
/// Windows: la decisión es <see cref="TrabajoDeLaCola.Decidir"/>.
/// </summary>
public sealed class TrabajoDeLaColaTests : IAsyncLifetime
{
    /// <summary>Lunes 9 de noviembre de 2026, 10:00 en la Ciudad de México: la caja 2 se acaba de abrir.</summary>
    private static readonly DateTime Ahora = new(2026, 11, 9, 16, 0, 0, DateTimeKind.Utc);

    private BaseDelModulo _base = null!;

    public async Task InitializeAsync() => _base = await BaseDelModulo.CrearAsync();

    public async Task DisposeAsync() => await _base.DisposeAsync();

    [Fact]
    public void Lo_que_el_agente_vio_en_la_cola_sale_con_la_hora_en_que_lo_vio_salir_aunque_haya_esperado_dias()
    {
        // La impresora estuvo apagada dos días con el trabajo en la cola y la caja abierta: salió ahora.
        Assert.Equal(new DecisionDeLaCola(true, Ahora), TrabajoDeLaCola.Decidir(Ahora.AddDays(-2), Ahora, salioSinVerlo: false));
        Assert.Equal(new DecisionDeLaCola(true, Ahora), TrabajoDeLaCola.Decidir(Ahora.AddSeconds(-20), Ahora, salioSinVerlo: false));
    }

    [Fact]
    public void Lo_que_salio_con_la_caja_cerrada_va_con_la_hora_en_que_entro_a_la_cola()
    {
        var anoche = Ahora.AddHours(-15);
        Assert.Equal(new DecisionDeLaCola(true, anoche), TrabajoDeLaCola.Decidir(anoche, Ahora, salioSinVerlo: true));
        // Recién salido (la caja se abrió enseguida): casi la misma hora.
        Assert.Equal(new DecisionDeLaCola(true, Ahora.AddSeconds(-5)), TrabajoDeLaCola.Decidir(Ahora.AddSeconds(-5), Ahora, salioSinVerlo: true));
    }

    [Fact]
    public void Justo_un_dia_se_sube_y_un_segundo_mas_ya_no()
    {
        var unDia = Ahora - TrabajoDeLaCola.Vigencia;
        Assert.Equal(TimeSpan.FromDays(1), TrabajoDeLaCola.Vigencia);
        Assert.Equal(new DecisionDeLaCola(true, unDia), TrabajoDeLaCola.Decidir(unDia, Ahora, salioSinVerlo: true));
        Assert.False(TrabajoDeLaCola.Decidir(unDia.AddSeconds(-1), Ahora, salioSinVerlo: true).Subir);
        Assert.False(TrabajoDeLaCola.Decidir(Ahora.AddDays(-9), Ahora, salioSinVerlo: true).Subir);
    }

    [Fact]
    public void Una_hora_de_envio_que_no_sirve_va_con_la_hora_en_que_se_vio_como_siempre()
    {
        // Del futuro (el reloj de la PC se atrasó): no se sabe cuándo salió.
        Assert.Equal(new DecisionDeLaCola(true, Ahora), TrabajoDeLaCola.Decidir(Ahora.AddHours(3), Ahora, salioSinVerlo: true));
        // Un poco adelantada (los relojes no coinciden): la de ahora, nunca una del futuro.
        Assert.Equal(new DecisionDeLaCola(true, Ahora), TrabajoDeLaCola.Decidir(Ahora.AddSeconds(30), Ahora, salioSinVerlo: true));
        // Vacía o dañada: no es «de hace más de un día», no se pierde.
        Assert.Equal(new DecisionDeLaCola(true, Ahora), TrabajoDeLaCola.Decidir(default, Ahora, salioSinVerlo: true));
        Assert.Equal(new DecisionDeLaCola(true, Ahora), TrabajoDeLaCola.Decidir(new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc), Ahora, salioSinVerlo: true));
        // Inválida en Windows: la cola pone la hora en que la leyó (PrintQueueClient.ToJob), un momento antes de decidir.
        Assert.Equal(new DecisionDeLaCola(true, Ahora.AddMilliseconds(-3)), TrabajoDeLaCola.Decidir(Ahora.AddMilliseconds(-3), Ahora, salioSinVerlo: true));
    }

    [Fact]
    public void La_linea_del_registro_no_lleva_el_nombre_del_documento()
    {
        var linea = TrabajoDeLaCola.NoSeSube("EPSON WF-6590 Series", 37, Ahora.AddDays(-3), 2, seQuita: true);

        Assert.Equal("El trabajo 37 de «EPSON WF-6590 Series» (2 pág.) entró a la cola el 06/11/2026 10:00, hace más de un día: no se sube a la " +
            "caja principal ni sale en F2; se quita de la cola.", linea);
        Assert.DoesNotContain("se quita", TrabajoDeLaCola.NoSeSube("EPSON WF-6590 Series", 37, Ahora.AddDays(-3), 2, seQuita: false));
    }

    /// <summary>Una hoja en blanco y negro que salió de la caja 2 con la caja cerrada.</summary>
    private static TrabajoMedidoDto Trabajo(CajaDelPos caja, int spoolId, DateTime enviadoUtc) =>
        new(Guid.NewGuid(), caja.Id, null, "CAJA2", "EPSON WF-6590 Series", spoolId, $"tarea-{spoolId}.pdf", "caja",
            TrabajoDeLaCola.Decidir(enviadoUtc, Ahora, salioSinVerlo: true).ImpresoUtc, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            [new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)], true, null, null, enviadoUtc);

    [Fact]
    public async Task Lo_de_anoche_sale_en_F2_con_su_dia_y_en_su_lote_y_en_el_desglose_de_ayer()
    {
        var caja = _base.Pos.NuevaCaja("Caja 2");
        // Dos clientes anoche (19:00 y 20:30) con la caja cerrada; el agente los manda a las 10:00 de hoy.
        var primero = Trabajo(caja, 11, Ahora.AddHours(-15));
        var segundo = Trabajo(caja, 12, Ahora.AddHours(-13.5));
        await using (var db = _base.Contexto())
        {
            await _base.Registro(db).RegistrarAsync(primero);
            await _base.Registro(db).RegistrarAsync(segundo);
        }

        await using var otro = _base.Contexto();
        var registro = _base.Registro(otro);
        var porCobrar = await registro.PorCobrarAsync(PosDePrueba.Sucursal);
        var lotes = LotesDeImpresiones.Agrupar(porCobrar, t => t.ImpresoUtc, t => t.PrecioCentavos);
        // Cada cliente en su lote, y con su día: no «Hoy 10:00» los dos juntos.
        Assert.Equal(["dom 08/11 20:30", "dom 08/11 19:00"], lotes.Select(l => l.Cuando(HoraMexico.ALocal(Ahora))));
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(Ahora));
        Assert.Empty(await registro.DelDiaAsync(PosDePrueba.Sucursal, hoy));
        Assert.Equal(2, (await registro.DelDiaAsync(PosDePrueba.Sucursal, hoy.AddDays(-1))).Count);
    }
}
