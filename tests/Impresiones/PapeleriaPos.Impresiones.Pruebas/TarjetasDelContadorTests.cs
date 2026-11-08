using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Las tarjetas «Copias o celular» en la base (2026-11-08): salen solas por impresora con su precio estimado, se encogen con lo que llega
/// tarde y con las copias que se registran, se parten y se ligan, no salen con Control Print en las que comparte con él y dan lo del día.
/// </summary>
public sealed class TarjetasDelContadorTests : IAsyncLifetime
{
    private BaseDelModulo _base = null!;
    private CajaDelPos _caja = null!;

    private DateTime Ahora => _base.Pos.Hora.Ahora.UtcDateTime;

    public async Task InitializeAsync()
    {
        _base = await BaseDelModulo.CrearAsync();
        _caja = _base.Pos.NuevaCaja("CAJA1");
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private async Task<Impresora> ImpresoraAsync(string nombre = "WF-6590", PerfilImpresora perfil = PerfilImpresora.WF6590, string host = "192.0.2.10")
    {
        await using var db = _base.Contexto();
        return await _base.Impresora(db).AgregarAsync(PosDePrueba.Sucursal, nombre, perfil, host);
    }

    /// <summary>Una lectura del contador después de <paramref name="despuesDe"/> segundos, como la toma la vigilancia, y la revisión de sus tarjetas.</summary>
    private async Task<bool> LeerAsync(Impresora impresora, long contador, long? color = null, int despuesDe = 10, string? serie = "X1",
        bool conControlPrint = false)
    {
        _base.Pos.Hora.Ahora += TimeSpan.FromSeconds(despuesDe);
        var lectura = new PrinterStatus
        {
            TakenUtc = Ahora, InkK = 80, InkC = 80, InkM = 80, InkY = 80, LifeCount = contador, ColorCount = color, MonoCount = contador - color,
            Serial = serie,
        };
        await using (var db = _base.Contexto())
        {
            var servicio = _base.Impresora(db);
            await servicio.GuardarLecturaAsync((await servicio.PorIdAsync(impresora.Id))!, lectura);
        }
        await using var otra = _base.Contexto();
        return await new TarjetasDelContador(otra, _base.Pos).RevisarAsync(impresora.Id, lectura, conControlPrint);
    }

    /// <summary>Lecturas sin caras nuevas hasta que pasan la pausa (60 s) y lo que se espera a las PC (30 s).</summary>
    private async Task CerrarAsync(Impresora impresora, long contador, long? color = null, bool conControlPrint = false)
    {
        await LeerAsync(impresora, contador, color, 70, conControlPrint: conControlPrint);
        await LeerAsync(impresora, contador, color, 30, conControlPrint: conControlPrint);
    }

    private async Task<List<TrabajoImpresion>> TarjetasAsync(Impresora? impresora = null)
    {
        await using var db = _base.Contexto();
        return await db.Set<TrabajoImpresion>().AsNoTracking()
            .Where(t => t.Tipo == TipoTrabajoImpresion.Contador && (impresora == null || t.ImpresoraId == impresora.Id)).ToListAsync();
    }

    private TrabajoMedidoDto Trabajo(Impresora impresora, int paginas, DateTime impresoUtc) =>
        new(Guid.NewGuid(), _caja.Id, null, "CAJA1", "WF-6590", 7, "Tarea.pdf", "cajero", impresoUtc, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            Enumerable.Range(0, paginas).Select(_ => new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)).ToList(), true, null, null,
            impresoUtc.AddSeconds(-20), Impresora: impresora.Numero);

    private async Task<TrabajoImpresion> CopiasAsync(Impresora impresora, int cantidad, bool color = false)
    {
        await using var db = _base.Contexto();
        return await _base.Registro(db).RegistrarCopiasAsync(new SolicitudCopias(Guid.NewGuid(), _caja.Id, null, cantidad, color, "Carta", false, null, null,
            impresora.Numero));
    }

    [Fact]
    public async Task La_primera_lectura_guarda_lo_de_su_modelo_y_una_racha_sin_registrar_sale_con_el_precio_de_unas_copias()
    {
        var wf = await ImpresoraAsync();
        var l5590 = await ImpresoraAsync("L5590", PerfilImpresora.L5590, "192.0.2.11");
        // Lo que contó antes de la primera lectura no es tarjeta.
        Assert.False(await LeerAsync(wf, 1000, 400));
        await LeerAsync(l5590, 5000, 100);
        Assert.Empty(await TarjetasAsync());

        Assert.True(await LeerAsync(wf, 1002, 400));
        Assert.True(await LeerAsync(wf, 1003, 400));
        await LeerAsync(l5590, 5004, 100);
        // Antes de la pausa no sale.
        Assert.Empty(await TarjetasAsync());
        await CerrarAsync(wf, 1003, 400);
        await CerrarAsync(l5590, 5004, 100);

        // La WF-6590 las tiene puestas (su contador y su ST2 se vieron en la tienda); la L5590 no, hasta confirmarlo.
        var tarjeta = Assert.Single(await TarjetasAsync());
        Assert.Equal(wf.Id, tarjeta.ImpresoraId);
        Assert.Equal("Copias o celular · 3 caras (B/N)", tarjeta.Documento);
        Assert.Equal((3, 3, 0, EstadoCobroImpresion.PorCobrar, TarjetasDelContador.Pc, false), (tarjeta.Caras, tarjeta.CarasContadas, tarjeta.CarasColor,
            tarjeta.Cobro, tarjeta.Pc, tarjeta.Medido));
        Assert.Equal(_caja.Id, tarjeta.CajaId);
        await using var db = _base.Contexto();
        var (copias, _) = await _base.Registro(db).CalcularCopiasAsync(PosDePrueba.Sucursal, 3, false, "Carta", false, wf.Numero);
        Assert.Equal((long)Math.Round(copias.Price * 100), tarjeta.PrecioCentavos);
        Assert.Equal((long)Math.Round(copias.Total * 100), tarjeta.CostoCentavos);
        Assert.True(AjustesDelContador.De((await _base.Impresora(db).PorIdAsync(wf.Id))!).Activas);
        Assert.False(AjustesDelContador.De((await _base.Impresora(db).PorIdAsync(l5590.Id))!).Activas);
        // A la hora en que pasó: la de la lectura de la última cara.
        Assert.True(tarjeta.ImpresoUtc < Ahora.AddSeconds(-90));
    }

    [Fact]
    public async Task Cada_impresora_con_su_contador_y_lo_que_mando_una_PC_no_sale()
    {
        var una = await ImpresoraAsync();
        var otra = await ImpresoraAsync("WF-6590 2", host: "192.0.2.12");
        await LeerAsync(una, 1000, 400);
        await LeerAsync(otra, 2000, 900);
        await using (var db = _base.Contexto())
            await _base.Registro(db).RegistrarAsync(Trabajo(una, 4, Ahora));
        await LeerAsync(una, 1004, 400);
        await LeerAsync(otra, 2002, 901);
        await CerrarAsync(una, 1004, 400);
        await CerrarAsync(otra, 2002, 901);

        var tarjeta = Assert.Single(await TarjetasAsync());
        Assert.Equal(otra.Id, tarjeta.ImpresoraId);
        Assert.Equal("Copias o celular · 2 caras (1 B/N, 1 color)", tarjeta.Documento);
        Assert.Equal(1, tarjeta.CarasColor);
    }

    [Fact]
    public async Task Lo_de_una_PC_que_llega_tarde_encoge_la_tarjeta_y_si_ya_se_cobro_lo_dice_su_nota()
    {
        var wf = await ImpresoraAsync();
        await LeerAsync(wf, 1000, 400);
        await LeerAsync(wf, 1005, 400);
        var cuando = Ahora;
        await CerrarAsync(wf, 1005, 400);
        var antes = Assert.Single(await TarjetasAsync());
        Assert.Equal(5, antes.Caras);

        // El agente de la PC lo midió tarde: 3 de esas caras eran suyas.
        TrabajoImpresion aTiempo;
        await using (var db = _base.Contexto())
            aTiempo = await _base.Registro(db).RegistrarAsync(Trabajo(wf, 3, cuando.AddSeconds(-5)));
        await CerrarAsync(wf, 1005, 400);
        var despues = Assert.Single(await TarjetasAsync());
        Assert.Equal((2, 2, "Copias o celular · 2 caras (B/N)"), (despues.Caras, despues.CarasContadas, despues.Documento));
        Assert.True(despues.PrecioCentavos <= antes.PrecioCentavos);

        // Si la tarjeta ya se cobró, lo que llega tarde no se toca: su nota lo dice.
        await using (var db = _base.Contexto())
        {
            var cobrar = await _base.EnLaVenta(db).ParaCobrarAsync(PosDePrueba.Sucursal, [despues.Id]);
            _base.EnLaVenta(db).Cobrar(cobrar, Guid.NewGuid(), Ahora);
            await db.SaveChangesAsync();
        }
        TrabajoImpresion tarde;
        await using (var db = _base.Contexto())
            tarde = await _base.Registro(db).RegistrarAsync(Trabajo(wf, 2, cuando.AddSeconds(-4)));
        await CerrarAsync(wf, 1005, 400);
        await using (var db = _base.Contexto())
        {
            Assert.Contains(TarjetasDelContador.YaContada, (await db.Set<TrabajoImpresion>().FindAsync(tarde.Id))!.Nota);
            // La que llegó cuando la tarjeta seguía por cobrar ya la encogió: esa no lo dice.
            Assert.Null((await db.Set<TrabajoImpresion>().FindAsync(aTiempo.Id))!.Nota);
        }
        Assert.Equal(EstadoCobroImpresion.Cobrada, Assert.Single(await TarjetasAsync()).Cobro);
    }

    [Fact]
    public async Task Las_copias_que_se_registran_se_toman_de_la_tarjeta_y_no_se_cobran_dos_veces()
    {
        var wf = await ImpresoraAsync();
        await LeerAsync(wf, 1000, 400);
        await LeerAsync(wf, 1004, 400);
        await CerrarAsync(wf, 1004, 400);
        Assert.Equal(4, Assert.Single(await TarjetasAsync()).Caras);

        // El cajero registra las copias después: al momento, sin esperar otra lectura.
        await CopiasAsync(wf, 3);
        Assert.Equal(1, Assert.Single(await TarjetasAsync()).Caras);
        await CopiasAsync(wf, 1);
        Assert.Empty(await TarjetasAsync());
        // Y no vuelve a salir.
        await CerrarAsync(wf, 1004, 400);
        Assert.Empty(await TarjetasAsync());

        // Unas registradas antes de hacerlas: cuando la impresora las cuenta no sale tarjeta.
        await CopiasAsync(wf, 2);
        await LeerAsync(wf, 1006, 400);
        await CerrarAsync(wf, 1006, 400);
        Assert.Empty(await TarjetasAsync());
    }

    [Fact]
    public async Task Partirla_deja_las_copias_en_su_linea_y_cobra_del_celular_lo_que_se_diga()
    {
        var wf = await ImpresoraAsync();
        await LeerAsync(wf, 1000, 400);
        await LeerAsync(wf, 1006, 402);
        await CerrarAsync(wf, 1006, 402);
        var tarjeta = Assert.Single(await TarjetasAsync());
        Assert.Equal("Copias o celular · 6 caras (4 B/N, 2 color)", tarjeta.Documento);

        await using (var db = _base.Contexto())
        {
            var partir = new TarjetasDelContador(db, _base.Pos);
            // No más de lo que contó, ni más color del que contó.
            await Assert.ThrowsAsync<ArgumentException>(() => partir.PartirAsync(tarjeta.Id, new SolicitudPartirTarjeta(Guid.NewGuid(), _caja.Id, 4, 0, 0, 3)));
            await Assert.ThrowsAsync<ArgumentException>(() => partir.PartirAsync(tarjeta.Id, new SolicitudPartirTarjeta(Guid.NewGuid(), _caja.Id, 2, 0, 0, 0)));
            var quedan = await partir.PartirAsync(tarjeta.Id, new SolicitudPartirTarjeta(Guid.NewGuid(), _caja.Id, 2, 0, 1, 2));
            Assert.Equal(2, quedan.Count);
            var copias = Assert.Single(quedan, t => t.Tipo == TipoTrabajoImpresion.Copia);
            Assert.Equal((2, 0, wf.Id, tarjeta.ImpresoUtc), (copias.Caras, copias.CarasColor, copias.ImpresoraId, copias.ImpresoUtc));
        }
        var queda = Assert.Single(await TarjetasAsync());
        // Se le cuentan 4 (2 a color) y se cobran 3: una no se cobra.
        Assert.Equal((4, 3, 2, "Copias o celular · 3 de 4 caras (1 B/N, 2 color)"), (queda.CarasContadas, queda.Caras, queda.CarasColor, queda.Documento));
        // Lo que se le cuenta más las copias es lo que contó la impresora: no sale otra tarjeta.
        await CerrarAsync(wf, 1006, 402);
        Assert.Single(await TarjetasAsync());

        // Todo como copias: se quita.
        await using (var db = _base.Contexto())
            Assert.Equal(2, (await new TarjetasDelContador(db, _base.Pos).PartirAsync(queda.Id, new SolicitudPartirTarjeta(Guid.NewGuid(), _caja.Id, 2, 2, 0, 0))).Count);
        Assert.Empty(await TarjetasAsync());
    }

    [Fact]
    public async Task Ya_se_cobro_como_copias_la_junta_con_ellas_aunque_se_hayan_registrado_en_otra_impresora()
    {
        var una = await ImpresoraAsync();
        var otra = await ImpresoraAsync("WF-6590 2", host: "192.0.2.12");
        await LeerAsync(una, 1000, 400);
        await LeerAsync(otra, 2000, 900);
        await LeerAsync(otra, 2003, 900);
        await CerrarAsync(otra, 2003, 900);
        await CerrarAsync(una, 1000, 400);
        var tarjeta = Assert.Single(await TarjetasAsync());
        // Las registraron en la impresora equivocada: 2 copias en la 1.
        var copias = await CopiasAsync(una, 2);

        await using (var db = _base.Contexto())
        {
            var tarjetas = new TarjetasDelContador(db, _base.Pos);
            Assert.Contains(await tarjetas.CopiasParaLigarAsync(tarjeta.Id), c => c.Id == copias.Id);
            await tarjetas.LigarAsync(tarjeta.Id, new SolicitudLigarTarjeta(Guid.NewGuid(), copias.Id));
        }

        Assert.Empty(await TarjetasAsync());
        await using (var db = _base.Contexto())
        {
            var ligadas = (await db.Set<TrabajoImpresion>().FindAsync(copias.Id))!;
            // Se pasaron a la otra y se les sumó la cara que faltaba (su precio no cambia).
            Assert.Equal((otra.Id, 3, copias.PrecioCentavos), (ligadas.ImpresoraId, ligadas.Caras, ligadas.PrecioCentavos));
            Assert.Contains("Se pasaron de la WF-6590", ligadas.Nota);
            Assert.Contains("1 cara más", ligadas.Nota);
        }
        // Ninguna de las dos vuelve a sacar tarjeta.
        await CerrarAsync(otra, 2003, 900);
        await CerrarAsync(una, 1000, 400);
        Assert.Empty(await TarjetasAsync());
    }

    [Fact]
    public async Task Con_Control_Print_no_salen_en_las_que_comparte_con_el_y_la_L5590_solo_si_se_ponen()
    {
        var wf = await ImpresoraAsync();
        var l5590 = await ImpresoraAsync("L5590", PerfilImpresora.L5590, "192.0.2.11");
        await LeerAsync(wf, 1000, 400, conControlPrint: true);
        await LeerAsync(l5590, 5000, 100, conControlPrint: true);
        await using (var db = _base.Contexto())
        {
            var impresora = (await _base.Impresora(db).PorIdAsync(l5590.Id))!;
            new TarjetasDelContador(db, _base.Pos).Cambiar(impresora, new AjustesContadorDto(true, 5, 5, 60));
            await db.SaveChangesAsync();
        }
        // Al ponerlas se cuenta desde ahí: desde la siguiente lectura.
        await LeerAsync(l5590, 5000, 100, conControlPrint: true);
        await LeerAsync(wf, 1003, 400, conControlPrint: true);
        await LeerAsync(l5590, 5002, 100, conControlPrint: true);
        await CerrarAsync(wf, 1003, 400, conControlPrint: true);
        await CerrarAsync(l5590, 5002, 100, conControlPrint: true);

        // Lo que imprime la caja principal en la WF-6590 lo registra Control Print: aquí saldría como sin registrar.
        Assert.Equal(l5590.Id, Assert.Single(await TarjetasAsync()).ImpresoraId);
    }

    [Fact]
    public async Task Si_el_contador_vuelve_a_empezar_o_contesta_otra_serie_no_se_inventan_caras()
    {
        var wf = await ImpresoraAsync();
        await LeerAsync(wf, 1000, 400);
        // Se cambió la tarjeta de la impresora: el contador empieza de nuevo.
        await LeerAsync(wf, 10, 4);
        await LeerAsync(wf, 12, 4);
        await CerrarAsync(wf, 12, 4);
        Assert.Equal(2, Assert.Single(await TarjetasAsync()).Caras);

        // Otra serie en su IP: la lectura no se guarda (#155) y lo que diga no es tarjeta.
        await using (var db = _base.Contexto())
        {
            var servicio = _base.Impresora(db);
            var (guardada, otraSerie) = await servicio.GuardarLecturaAsync((await servicio.PorIdAsync(wf.Id))!,
                new PrinterStatus { TakenUtc = Ahora.AddSeconds(5), LifeCount = 90_000, ColorCount = 100, Serial = "OTRA" });
            Assert.Null(guardada);
            Assert.NotNull(otraSerie);
        }
        await CerrarAsync(wf, 12, 4);
        Assert.Single(await TarjetasAsync());
    }

    [Fact]
    public async Task Al_cambiar_su_IP_se_cuenta_desde_ahi()
    {
        var wf = await ImpresoraAsync();
        await LeerAsync(wf, 1000, 400);
        await using (var db = _base.Contexto())
        {
            var servicio = _base.Impresora(db);
            await servicio.CambiarAsync((await servicio.PorIdAsync(wf.Id))!, new ConfiguracionImpresoraDto("192.0.2.99", 10, 5), null, null);
        }
        // En la IP nueva contesta otra impresora sin serie, con otro contador: no es tarjeta.
        await LeerAsync(wf, 1500, 400, serie: null);
        await LeerAsync(wf, 1501, 400, serie: null);
        await CerrarAsync(wf, 1501, 400);
        Assert.Equal(1, Assert.Single(await TarjetasAsync()).Caras);
    }

    [Fact]
    public async Task Lo_del_dia_dice_lo_que_conto_lo_registrado_y_lo_que_fue_a_tarjetas()
    {
        var wf = await ImpresoraAsync();
        await LeerAsync(wf, 1000, 400);
        await LeerAsync(wf, 1003, 400);
        await CerrarAsync(wf, 1003, 400);
        await LeerAsync(wf, 1005, 400, despuesDe: 120);
        await CerrarAsync(wf, 1005, 400);
        var tarjetas = (await TarjetasAsync()).OrderBy(t => t.ImpresoUtc).ToList();
        Assert.Equal([3, 2], tarjetas.Select(t => t.Caras));
        await using (var db = _base.Contexto())
            await _base.Registro(db).NoVendidaAsync(tarjetas[1].Id, new SolicitudNoVendida(Guid.NewGuid(), MotivoNoVendida.Otro, "Hoja de prueba"));
        // Unas copias registradas se toman de la que sigue por cobrar.
        await CopiasAsync(wf, 1);

        await using (var db = _base.Contexto())
        {
            var servicio = _base.Impresora(db);
            var impresora = (await servicio.PorIdAsync(wf.Id))!;
            var hoy = DateOnly.FromDateTime(Nucleo.HoraMexico.ALocal(Ahora));
            var dia = (await new TarjetasDelContador(db, _base.Pos).DeLaImpresoraAsync(impresora, await servicio.ComparacionAsync(impresora, hoy), hoy)).Hoy!;
            Assert.Equal((5L, 0, 1, 4, 0, 2, 2, 0L), (dia.Contadas, dia.Impresas, dia.Copias, dia.EnTarjetas, dia.Cobradas, dia.PorCobrar, dia.NoVendidas,
                dia.SinTarjeta));
        }
    }
}
