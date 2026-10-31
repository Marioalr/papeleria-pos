using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Lo que imprime un trámite (docs/tramites.md, «Imprimir sin cobrar dos veces»; 2026-10-31). La caja manda el PDF a la cola como
/// «T-0042 · CURP» y, si se piden más copias, aparte como «T-0042 · CURP · copias extra». El módulo los reconoce por el nombre:
/// la copia incluida se cobra con el trámite (no sale en F2 ni lleva precio, pero se queda su costo) y las de más se cobran en F2
/// como cualquier impresión. De ninguno se guarda la miniatura: sería la CURP de un cliente guardada en la base.
/// </summary>
public sealed class TrabajosDeTramitesTests : IAsyncLifetime
{
    private BaseDelModulo _base = null!;

    private DateTime Ahora => _base.Pos.Hora.Ahora.UtcDateTime;

    public async Task InitializeAsync() => _base = await BaseDelModulo.CrearAsync();

    public async Task DisposeAsync() => await _base.DisposeAsync();

    /// <summary>Una página carta en blanco y negro, como la CURP, con la miniatura que sacaría el agente.</summary>
    private static TrabajoMedidoDto Trabajo(CajaDelPos caja, string documento, DateTime impresoUtc, int copias = 1) =>
        new(Guid.NewGuid(), caja.Id, null, "CAJA2", "EPSON WF-6590 Series", 12, documento, "caja", impresoUtc, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, copias, 1, true),
            [new CoberturaPaginaDto(0, 0, 0, 0.08, 0.08, 93.5, false)], true, null, Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]),
            impresoUtc.AddSeconds(-15));

    [Theory]
    [InlineData("T-0042 · CURP", "T-0042", false)]
    [InlineData("T-0042 · CURP · copias extra", "T-0042", true)]
    [InlineData("T-0042.pdf", "T-0042", false)]
    [InlineData("t-12345 · Cita de verificación", "T-12345", false)]
    [InlineData("  T-0007 ·  CURP  ·  Copias extra ", "T-0007", true)]
    public void Se_reconoce_por_su_nombre(string documento, string folio, bool extra)
    {
        var tramite = TrabajoDeTramite.De(documento);

        Assert.NotNull(tramite);
        Assert.Equal(folio, tramite.Folio);
        Assert.Equal(extra, tramite.CopiasExtra);
    }

    [Theory]
    [InlineData("CURP_PEPJ800101HPLRRN09.pdf")]
    [InlineData("Tarea de historia.docx")]
    [InlineData("T-42 · CURP")]
    [InlineData("T-2026 - examen.pdf")]
    [InlineData("Mi T-0042 · CURP")]
    [InlineData("T-0042.docx")]
    [InlineData("")]
    [InlineData(null)]
    public void Lo_de_un_cliente_no_es_de_un_tramite(string? documento) => Assert.Null(TrabajoDeTramite.De(documento));

    [Fact]
    public void El_nombre_que_arma_la_caja_es_el_que_se_reconoce()
    {
        Assert.Equal("T-0042 · CURP", TrabajoDeTramite.Nombre("T-0042", "CURP"));
        Assert.Equal("T-0042 · CURP · copias extra", TrabajoDeTramite.NombreDeCopiasExtra("T-0042", "CURP"));
        Assert.Equal(new TrabajoDeTramite("T-0042", false), TrabajoDeTramite.De(TrabajoDeTramite.Nombre("T-0042", "CURP")));
        Assert.Equal(new TrabajoDeTramite("T-0042", true), TrabajoDeTramite.De(TrabajoDeTramite.NombreDeCopiasExtra("T-0042", "CURP")));
    }

    [Fact]
    public async Task La_copia_incluida_no_queda_por_cobrar_ni_guarda_su_miniatura_y_se_queda_su_costo()
    {
        var caja = _base.Pos.NuevaCaja("Caja 2");
        var incluida = Trabajo(caja, "T-0042 · CURP", Ahora);
        await using (var db = _base.Contexto())
            await _base.Registro(db).RegistrarAsync(incluida);

        await using var otro = _base.Contexto();
        var trabajo = await otro.Set<TrabajoImpresion>().SingleAsync(t => t.Id == incluida.Id);
        Assert.Equal(EstadoCobroImpresion.EnTramite, trabajo.Cobro);
        Assert.Null(trabajo.Miniatura);
        Assert.Equal(0, trabajo.PrecioCentavos);
        Assert.True(trabajo.CostoCentavos > 0, "Su costo (tinta y papel) se queda: es lo que le cuesta a la tienda el trámite.");
        Assert.Equal("T-0042 · CURP", trabajo.Documento);
        var registro = _base.Registro(otro);
        Assert.Empty(await registro.PorCobrarAsync(PosDePrueba.Sucursal));
        Assert.Equal(0, (await registro.CuantosPorCobrarAsync(PosDePrueba.Sucursal)).Cuantos);
        // No se puede poner en un ticket: se cobró con el trámite.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _base.EnLaVenta(otro).ParaCobrarAsync(PosDePrueba.Sucursal, [incluida.Id]));
        Assert.Equal("«T-0042 · CURP» ya no está por cobrar: quítala del ticket.", error.Message);
    }

    [Fact]
    public async Task Las_copias_extra_se_cobran_en_F2_con_su_precio_y_sin_miniatura()
    {
        var caja = _base.Pos.NuevaCaja("Caja 2");
        var extra = Trabajo(caja, "T-0042 · CURP · copias extra", Ahora, copias: 2);
        await using (var db = _base.Contexto())
            await _base.Registro(db).RegistrarAsync(extra);

        await using var otro = _base.Contexto();
        var trabajo = Assert.Single(await _base.Registro(otro).PorCobrarAsync(PosDePrueba.Sucursal));
        Assert.Equal(extra.Id, trabajo.Id);
        Assert.Null(trabajo.Miniatura);
        Assert.Equal(2, trabajo.Copias);
        Assert.True(trabajo.PrecioCentavos > 0 && trabajo.PrecioCentavos % 100 == 0, "Al precio de la impresión, en pesos completos.");
    }

    [Fact]
    public async Task El_archivo_del_tramite_impreso_desde_otro_programa_tambien_va_incluido()
    {
        var caja = _base.Pos.NuevaCaja("Caja 2");
        var desdeElVisor = Trabajo(caja, "T-0042.pdf", Ahora);
        await using (var db = _base.Contexto())
            await _base.Registro(db).RegistrarAsync(desdeElVisor);

        await using var otro = _base.Contexto();
        var trabajo = await otro.Set<TrabajoImpresion>().SingleAsync(t => t.Id == desdeElVisor.Id);
        Assert.Equal(EstadoCobroImpresion.EnTramite, trabajo.Cobro);
        Assert.Null(trabajo.Miniatura);
    }

    [Fact]
    public async Task Lo_que_no_es_de_un_tramite_sigue_como_siempre_con_su_miniatura()
    {
        var caja = _base.Pos.NuevaCaja("Caja 2");
        var deUnCliente = Trabajo(caja, "CURP_PEPJ800101HPLRRN09.pdf", Ahora);
        await using (var db = _base.Contexto())
            await _base.Registro(db).RegistrarAsync(deUnCliente);

        await using var otro = _base.Contexto();
        var trabajo = Assert.Single(await _base.Registro(otro).PorCobrarAsync(PosDePrueba.Sucursal));
        Assert.Equal(EstadoCobroImpresion.PorCobrar, trabajo.Cobro);
        Assert.NotNull(trabajo.Miniatura);
        Assert.True(trabajo.PrecioCentavos > 0);
    }

    [Fact]
    public async Task Las_caras_del_tramite_cuentan_contra_el_contador_de_la_impresora()
    {
        var caja = _base.Pos.NuevaCaja("Caja 2");
        await using (var db = _base.Contexto())
        {
            await _base.Registro(db).RegistrarAsync(Trabajo(caja, "T-0042 · CURP", Ahora));
            await _base.Registro(db).RegistrarAsync(Trabajo(caja, "T-0042 · CURP · copias extra", Ahora, copias: 2));
        }

        await using var otro = _base.Contexto();
        // Las tres hojas salieron de la impresora: la que va con el trámite también.
        Assert.Equal(3, _base.Registro(otro).CarasRegistradas(PosDePrueba.Sucursal, Ahora.AddMinutes(-1), Ahora.AddMinutes(1)).PrintSides);
    }
}
