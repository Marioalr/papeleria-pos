using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// El módulo sin el resto del POS: su mapeo y sus servicios (el registro, la impresora y la fachada de la venta) sobre una
/// base con solo sus tablas, pidiéndole al POS solo lo del puerto. Si alguien hace que el módulo dependa de algo del POS
/// por fuera del puerto, esto deja de compilar o de pasar.
/// </summary>
public sealed class ModuloSinElPosTests : IAsyncLifetime
{
    private static readonly DateOnly Hoy = new(2026, 10, 15);
    private BaseDelModulo _base = null!;

    private DateTime Ahora => _base.Pos.Hora.Ahora.UtcDateTime;

    public async Task InitializeAsync() => _base = await BaseDelModulo.CrearAsync();

    public async Task DisposeAsync() => await _base.DisposeAsync();

    /// <summary>Lo que mandaría el agente: hojas carta con 5 % de negro por página (o un poco de cada color).</summary>
    private static TrabajoMedidoDto Trabajo(CajaDelPos caja, string documento, DateTime impresoUtc, int paginas = 1, bool color = false) =>
        new(Guid.NewGuid(), caja.Id, null, "PC-PRUEBA", "ControlPrint Lab (WF-6590)", 7, documento, "cajero", impresoUtc, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, color, false, 1, 1, true),
            Enumerable.Range(0, paginas).Select(_ => color
                ? new CoberturaPaginaDto(0.05, 0.05, 0.05, 0.05, 0.1, 93.5, true)
                : new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)).ToList(),
            true, null, null, impresoUtc.AddSeconds(-20));

    [Fact]
    public async Task Su_mapeo_basta_para_sus_tablas_sin_las_del_POS()
    {
        await using var db = _base.Contexto();

        Assert.Equal(["ConfiguracionesImpresion", "LecturasImpresora", "TrabajosImpresion"],
            db.Model.GetEntityTypes().Select(t => t.GetTableName()).Order());
        // Las llaves foráneas hacia sucursales, cajas y ventas las pone el POS encima.
        Assert.Empty(db.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()));
    }

    [Fact]
    public async Task Un_trabajo_medido_queda_por_cobrar_con_su_precio_y_no_se_repite()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        var medido = Trabajo(caja, "Tarea de historia.docx", Ahora, paginas: 3, color: true);
        await using (var db = _base.Contexto())
        {
            var registro = _base.Registro(db);
            await registro.RegistrarAsync(medido);
            await registro.RegistrarAsync(medido);   // el agente lo volvió a mandar
        }

        await using var otro = _base.Contexto();
        var trabajo = Assert.Single(await _base.Registro(otro).PorCobrarAsync(PosDePrueba.Sucursal));
        Assert.Equal(medido.Id, trabajo.Id);
        Assert.Equal(caja.Id, trabajo.CajaId);
        Assert.Equal(3, trabajo.Paginas);
        Assert.Equal(EstadoTrabajo.Impreso, trabajo.Estado);
        Assert.True(trabajo.PrecioCentavos >= 100 && trabajo.PrecioCentavos % 100 == 0, "Precio redondeado a pesos completos.");
        Assert.True(trabajo.CostoCentavos > 0 && trabajo.CostoCentavos < trabajo.PrecioCentavos);
        Assert.True(await _base.Registro(otro).HayTrabajosAsync());
    }

    [Fact]
    public async Task Sin_la_caja_en_el_POS_no_se_registra()
    {
        var deOtroLado = new CajaDelPos(Guid.NewGuid(), PosDePrueba.Sucursal, "Caja que no existe");
        await using var db = _base.Contexto();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _base.Registro(db).RegistrarAsync(Trabajo(deOtroLado, "Hoja.pdf", Ahora)));

        Assert.Equal("La caja no existe.", error.Message);
        Assert.False(await _base.Registro(db).HayTrabajosAsync());
    }

    [Fact]
    public async Task Una_no_vendida_lleva_motivo_y_sale_de_lo_que_falta_cobrar()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        var medido = Trabajo(caja, "Salió corrida.pdf", Ahora);
        var quien = Guid.NewGuid();
        await using var db = _base.Contexto();
        var registro = _base.Registro(db);
        await registro.RegistrarAsync(medido);

        await Assert.ThrowsAsync<ArgumentException>(() => registro.NoVendidaAsync(medido.Id, new SolicitudNoVendida(quien, MotivoNoVendida.Otro, null)));
        var trabajo = await registro.NoVendidaAsync(medido.Id, new SolicitudNoVendida(quien, MotivoNoVendida.SalioMal, null));

        Assert.Equal(EstadoCobroImpresion.NoVendida, trabajo.Cobro);
        Assert.Equal(quien, trabajo.MarcoId);
        Assert.Equal(Ahora, trabajo.CobroUtc);
        Assert.Empty(await registro.PorCobrarAsync(PosDePrueba.Sucursal));
    }

    [Fact]
    public async Task Se_cobran_con_la_venta_y_al_cancelarla_vuelven_a_estar_por_cobrar()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        var uno = Trabajo(caja, "Credencial.pdf", Ahora);
        var dos = Trabajo(caja, "Mapa.pdf", Ahora, color: true);
        var venta = Guid.NewGuid();
        await using (var db = _base.Contexto())
        {
            await _base.Registro(db).RegistrarAsync(uno);
            await _base.Registro(db).RegistrarAsync(dos);
        }

        // El cobro: la fachada no guarda, lo guarda la venta en su transacción.
        await using (var db = _base.Contexto())
        {
            var enLaVenta = _base.EnLaVenta(db);
            enLaVenta.Cobrar(await enLaVenta.ParaCobrarAsync(PosDePrueba.Sucursal, [uno.Id, dos.Id]), venta, Ahora);
            await db.SaveChangesAsync();
        }
        await using (var db = _base.Contexto())
        {
            var cobradas = await db.Set<TrabajoImpresion>().Where(t => t.VentaId == venta).ToListAsync();
            Assert.Equal(2, cobradas.Count);
            Assert.All(cobradas, t => Assert.Equal(EstadoCobroImpresion.Cobrada, t.Cobro));
            Assert.Empty(await _base.Registro(db).PorCobrarAsync(PosDePrueba.Sucursal));
            var otraVez = await Assert.ThrowsAsync<InvalidOperationException>(() => _base.EnLaVenta(db).ParaCobrarAsync(PosDePrueba.Sucursal, [uno.Id]));
            Assert.Equal("«Credencial.pdf» ya no está por cobrar: quítala del ticket.", otraVez.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => _base.EnLaVenta(db).ParaCobrarAsync(PosDePrueba.Sucursal, [Guid.NewGuid()]));
        }

        // Se cancela la venta (o se devuelve su línea «Impresiones»).
        await using (var db = _base.Contexto())
        {
            await _base.EnLaVenta(db).LiberarAsync(venta);
            await db.SaveChangesAsync();
        }
        await using (var db = _base.Contexto())
        {
            var porCobrar = await _base.Registro(db).PorCobrarAsync(PosDePrueba.Sucursal);
            Assert.Equal(new[] { uno.Id, dos.Id }.Order(), porCobrar.Select(t => t.Id).Order());
            Assert.All(porCobrar, t => Assert.Null(t.VentaId));
        }
    }

    [Fact]
    public async Task Las_copias_del_panel_quedan_por_cobrar_con_el_nombre_de_la_caja()
    {
        var caja = _base.Pos.NuevaCaja("Caja 2");
        await using var db = _base.Contexto();
        var registro = _base.Registro(db);

        var copias = await registro.RegistrarCopiasAsync(new SolicitudCopias(Guid.NewGuid(), caja.Id, null, 3, false, "Carta", false, null, null));

        Assert.Equal(TipoTrabajoImpresion.Copia, copias.Tipo);
        Assert.Equal("Caja 2", copias.Pc);
        Assert.Equal("3 copias en B/N", copias.Documento);
        Assert.Equal(Ahora, copias.ImpresoUtc);
        Assert.Equal(copias.Id, Assert.Single(await registro.PorCobrarAsync(PosDePrueba.Sucursal)).Id);
    }

    [Fact]
    public async Task El_resumen_del_dia_trae_las_no_vendidas_con_su_costo_y_el_contador()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        var t0 = Ahora.AddHours(-2);
        PrinterStatus Lectura(int minutos, long contador) =>
            new() { TakenUtc = t0.AddMinutes(minutos), InkK = 50, InkC = 50, InkM = 50, InkY = 50, LifeCount = contador };
        await using var db = _base.Contexto();
        var impresora = _base.Impresora(db);
        var registro = _base.Registro(db);
        await impresora.GuardarLecturaAsync(PosDePrueba.Sucursal, Lectura(0, 1000));
        await registro.RegistrarAsync(Trabajo(caja, "Tarea.pdf", t0.AddMinutes(10), paginas: 2));
        var mal = await registro.RegistrarAsync(Trabajo(caja, "Salió corrida.pdf", t0.AddMinutes(20)));
        await registro.NoVendidaAsync(mal.Id, new SolicitudNoVendida(Guid.NewGuid(), MotivoNoVendida.SalioMal, null));
        // La impresora contó 5 caras: 3 de las PC (también la que salió mal) y 2 que nadie registró.
        await impresora.GuardarLecturaAsync(PosDePrueba.Sucursal, Lectura(30, 1005));

        var dia = await _base.EnLaVenta(db).ResumenDelDiaAsync(PosDePrueba.Sucursal, Hoy);

        Assert.Equal(1, dia.NoVendidas);
        Assert.Equal(mal.CostoCentavos, dia.CostoNoVendidasCentavos);
        Assert.NotNull(dia.Contador);
        Assert.Equal(5, dia.Contador.Counted);
        Assert.Equal(3, dia.Contador.RegisteredTotal);
        Assert.Equal(2, dia.Contador.Unregistered);
        Assert.Empty(_base.Pos.Avisos);
    }

    [Fact]
    public async Task Los_avisos_de_tinta_salen_por_el_puerto_una_vez()
    {
        var t0 = Ahora.AddHours(-1);
        PrinterStatus Nivel(int minutos, int cian) => new() { TakenUtc = t0.AddMinutes(minutos), InkK = 50, InkC = cian, InkM = 50, InkY = 50, LifeCount = 5000 };
        await using var db = _base.Contexto();
        var impresora = _base.Impresora(db);

        await impresora.GuardarLecturaAsync(PosDePrueba.Sucursal, Nivel(0, 12));
        await impresora.GuardarLecturaAsync(PosDePrueba.Sucursal, Nivel(5, 9));
        await impresora.GuardarLecturaAsync(PosDePrueba.Sucursal, Nivel(10, 8));
        await impresora.GuardarLecturaAsync(PosDePrueba.Sucursal, Nivel(15, 100));

        Assert.Collection(_base.Pos.Avisos,
            a =>
            {
                Assert.Equal((PosDePrueba.Sucursal, AvisoImpresiones.Impresora), (a.SucursalId, a.Tipo));
                Assert.Contains("cartucho cian bajo", a.Texto);
                Assert.Contains("Queda 9 %", a.Texto);
            },
            a =>
            {
                Assert.Equal(AvisoImpresiones.Impresora, a.Tipo);
                Assert.Contains("cartucho cian nuevo", a.Texto);
            });
    }

    [Fact]
    public async Task Si_la_impresora_conto_menos_caras_el_costo_baja_y_se_avisa_por_el_puerto()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        var medido = Trabajo(caja, "Prueba atasco.pdf", Ahora, paginas: 6);
        await using var db = _base.Contexto();
        var trabajo = await _base.Registro(db).RegistrarAsync(medido);
        var costoCompleto = trabajo.CostoCentavos;

        await _base.Impresora(db).AplicarVerificacionAsync([new PageAuditor.JobCount(medido.Id, 6, 1)]);

        Assert.Equal(1, trabajo.CarasContadas);
        Assert.True(trabajo.CostoCentavos < costoCompleto);
        var aviso = Assert.Single(_base.Pos.Avisos);
        Assert.Equal(AvisoImpresiones.HojasFaltantes, aviso.Tipo);
        Assert.Equal($"hojas:{medido.Id}", aviso.Clave);
        Assert.NotNull(await _base.Registro(db).SoloLoQueSalioAsync(medido.Id));
        Assert.True(trabajo.PrecioCentavos < trabajo.PrecioCompletoCentavos);
    }

    [Fact]
    public async Task La_configuracion_de_la_impresora_se_valida_y_se_guarda()
    {
        await using var db = _base.Contexto();
        var impresora = _base.Impresora(db);

        await Assert.ThrowsAsync<ArgumentException>(() => impresora.GuardarConfiguracionAsync(PosDePrueba.Sucursal, new ConfiguracionImpresoraDto("10.0.0.7", 101, 5)));
        await impresora.GuardarConfiguracionAsync(PosDePrueba.Sucursal, new ConfiguracionImpresoraDto(" 10.0.0.7 ", 15, 5));

        var guardada = Assert.Single(await impresora.ConImpresoraAsync());
        Assert.Equal(("10.0.0.7", 15, 5), (guardada.ImpresoraHost, guardada.AvisoTintaPorcentaje, guardada.MinutosEntreLecturas));
    }
}
