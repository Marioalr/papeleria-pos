using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Lo que imprime un trámite (docs/tramites.md, «Imprimir sin cobrar dos veces»; 2026-10-31). La caja manda el PDF a la cola como
/// «T-0042 · CURP» y, si se piden más copias, aparte como «T-0042 · CURP · copias extra», y lo anota (<see cref="TrabajosDeTramites"/>).
/// Su agente de impresión reconoce sus trabajos por ese nombre exacto y por la hora en que entraron a la cola, y los manda ya marcados
/// (<see cref="TrabajoMedidoDto.Tramite"/>): la copia incluida se cobra con el trámite (no sale en F2 ni lleva precio, pero se queda su
/// costo) y las de más se cobran en F2 como cualquier impresión. De ninguno se guarda la miniatura. El servicio nunca lo adivina por el
/// nombre (revisión de servicios/curp): lo de un cliente que se llame como un folio se cobra como siempre.
/// </summary>
public sealed class TrabajosDeTramitesTests : IAsyncLifetime
{
    private BaseDelModulo _base = null!;

    private DateTime Ahora => _base.Pos.Hora.Ahora.UtcDateTime;

    public async Task InitializeAsync() => _base = await BaseDelModulo.CrearAsync();

    public async Task DisposeAsync() => await _base.DisposeAsync();

    /// <summary>Una página carta en blanco y negro, como la CURP, con la miniatura que sacaría el agente.</summary>
    private static TrabajoMedidoDto Trabajo(CajaDelPos caja, string documento, DateTime impresoUtc, int copias = 1, TrabajoDeTramite? tramite = null) =>
        new(Guid.NewGuid(), caja.Id, null, "CAJA2", "EPSON WF-6590 Series", 12, documento, "caja", impresoUtc, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, copias, 1, true),
            [new CoberturaPaginaDto(0, 0, 0, 0.08, 0.08, 93.5, false)], true, null, Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]),
            impresoUtc.AddSeconds(-15), tramite);

    // ── Lo que mandó a imprimir esta caja ──

    [Fact]
    public void El_nombre_que_pone_el_panel_lleva_el_folio_y_nunca_al_cliente()
    {
        Assert.Equal("T-0042 · CURP", TrabajoDeTramite.Nombre("T-0042", "CURP"));
        Assert.Equal("T-0042 · CURP · copias extra", TrabajoDeTramite.NombreDeCopiasExtra("T-0042", "CURP"));
    }

    [Fact]
    public void Solo_es_de_un_tramite_lo_que_esta_caja_mando_con_ese_nombre_exacto_y_entro_a_la_cola_enseguida()
    {
        var reloj = new RelojFijo(new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero));
        var enviados = new TrabajosDeTramites(reloj);
        enviados.Enviado("T-0042 · CURP", new TrabajoDeTramite("T-0042", false));
        enviados.Enviado("T-0042 · CURP · copias extra", new TrabajoDeTramite("T-0042", true));
        var enLaCola = reloj.Ahora.UtcDateTime.AddSeconds(4);

        Assert.Equal(new TrabajoDeTramite("T-0042", false), enviados.De("T-0042 · CURP", enLaCola));
        Assert.Equal(new TrabajoDeTramite("T-0042", true), enviados.De("T-0042 · CURP · copias extra", enLaCola));
        // Lo de un cliente que se llama parecido, o como el archivo del trámite: una impresión como cualquiera.
        foreach (var delCliente in new[] { "T-0042.pdf", "T-0042", "t-0042 · curp", "T-0042 · CURP ", "T-0043 · CURP", "CURP_PEPJ800101HPLRRN09.pdf", "" })
            Assert.Null(enviados.De(delCliente, enLaCola));
        Assert.Null(enviados.De(null, enLaCola));
        // El mismo nombre, pero entró a la cola mucho después (o antes) de que esta caja lo mandara: no es el suyo.
        Assert.Null(enviados.De("T-0042 · CURP", reloj.Ahora.UtcDateTime + TrabajosDeTramites.Plazo + TimeSpan.FromSeconds(1)));
        Assert.Null(enviados.De("T-0042 · CURP", reloj.Ahora.UtcDateTime.AddMinutes(-2)));
        // Otra caja (otro proceso) no lo mandó: no sabe de él.
        Assert.Null(new TrabajosDeTramites(reloj).De("T-0042 · CURP", enLaCola));
    }

    [Fact]
    public void Lo_de_hace_mas_de_una_hora_ya_no_se_recuerda()
    {
        var reloj = new RelojFijo(new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero));
        var enviados = new TrabajosDeTramites(reloj);
        enviados.Enviado("T-0001 · CURP", new TrabajoDeTramite("T-0001", false));
        var antes = reloj.Ahora.UtcDateTime;

        reloj.Ahora = reloj.Ahora.AddHours(2);
        enviados.Enviado("T-0002 · CURP", new TrabajoDeTramite("T-0002", false));

        Assert.Null(enviados.De("T-0001 · CURP", antes.AddSeconds(3)));
        Assert.NotNull(enviados.De("T-0002 · CURP", reloj.Ahora.UtcDateTime.AddSeconds(3)));
    }

    // ── Lo que hace el servicio con lo que llega marcado ──

    [Fact]
    public async Task La_copia_incluida_no_queda_por_cobrar_ni_guarda_su_miniatura_y_se_queda_su_costo()
    {
        var caja = _base.Pos.NuevaCaja("Caja 2");
        var incluida = Trabajo(caja, "T-0042 · CURP", Ahora, tramite: new TrabajoDeTramite("T-0042", false));
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
        var extra = Trabajo(caja, "T-0042 · CURP · copias extra", Ahora, copias: 2, tramite: new TrabajoDeTramite("T-0042", true));
        await using (var db = _base.Contexto())
            await _base.Registro(db).RegistrarAsync(extra);

        await using var otro = _base.Contexto();
        var trabajo = Assert.Single(await _base.Registro(otro).PorCobrarAsync(PosDePrueba.Sucursal));
        Assert.Equal(extra.Id, trabajo.Id);
        Assert.Null(trabajo.Miniatura);
        Assert.Equal(2, trabajo.Copias);
        Assert.True(trabajo.PrecioCentavos > 0 && trabajo.PrecioCentavos % 100 == 0, "Al precio de la impresión, en pesos completos.");
    }

    [Theory]
    [InlineData("T-0042 · CURP")]
    [InlineData("T-0042.pdf")]
    [InlineData("CURP_PEPJ800101HPLRRN09.pdf")]
    public async Task Sin_la_marca_de_la_caja_es_una_impresion_como_cualquiera_aunque_se_llame_como_un_tramite(string documento)
    {
        var caja = _base.Pos.NuevaCaja("Caja 2");
        var sinMarca = Trabajo(caja, documento, Ahora);
        await using (var db = _base.Contexto())
            await _base.Registro(db).RegistrarAsync(sinMarca);

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
            await _base.Registro(db).RegistrarAsync(Trabajo(caja, "T-0042 · CURP", Ahora, tramite: new TrabajoDeTramite("T-0042", false)));
            await _base.Registro(db).RegistrarAsync(Trabajo(caja, "T-0042 · CURP · copias extra", Ahora, copias: 2,
                tramite: new TrabajoDeTramite("T-0042", true)));
        }

        await using var otro = _base.Contexto();
        // Las tres hojas salieron de la impresora: la que va con el trámite también.
        Assert.Equal(3, _base.Registro(otro).CarasRegistradas(PosDePrueba.Sucursal, Ahora.AddMinutes(-1), Ahora.AddMinutes(1)).PrintSides);
    }
}
