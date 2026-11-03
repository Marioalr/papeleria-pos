using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Varias impresoras por sucursal (2026-11-03; la matriz tiene dos WF-6590 y una L5590): cada una con su modelo, sus costos, sus lecturas,
/// su contador y su calibración, y lo de antes (una por sucursal) sigue igual como su impresora 1.
/// </summary>
public sealed class VariasImpresorasTests : IAsyncLifetime
{
    private BaseDelModulo _base = null!;

    private DateTime Ahora => _base.Pos.Hora.Ahora.UtcDateTime;

    public async Task InitializeAsync() => _base = await BaseDelModulo.CrearAsync();

    public async Task DisposeAsync() => await _base.DisposeAsync();

    /// <summary>Lo que mandaría el agente desde la cola de esa impresora: hojas carta con 5 % de negro por página.</summary>
    private static TrabajoMedidoDto Trabajo(CajaDelPos caja, string documento, DateTime impresoUtc, int? impresora = null, int paginas = 10) =>
        new(Guid.NewGuid(), caja.Id, null, "CAJA2", impresora is { } n ? $"Cola {n}" : "WF-6590", 7, documento, "cajero", impresoUtc, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            Enumerable.Range(0, paginas).Select(_ => new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)).ToList(), true, null, null,
            impresoUtc.AddSeconds(-20), Impresora: impresora);

    private static SolicitudConsumiblesImpresora Botellas(decimal precio, decimal? caja = null, int? paginasPorCaja = null) =>
        new(Guid.NewGuid(), [new("Negro", precio, 4300), new("Cian", precio, 7300), new("Magenta", precio, 7300), new("Amarillo", precio, 7300)],
            caja, paginasPorCaja);

    [Fact]
    public async Task Sin_impresoras_el_primer_trabajo_da_de_alta_la_1_y_lo_que_no_dice_su_impresora_es_de_la_primera()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        Assert.Null(await impresoras.PrimeraAsync(PosDePrueba.Sucursal));

        var trabajo = await _base.Registro(db).RegistrarAsync(Trabajo(caja, "Tarea.pdf", Ahora));

        var primera = Assert.Single(await impresoras.ImpresorasAsync(PosDePrueba.Sucursal));
        Assert.Equal((1, "WF-6590", PerfilImpresora.WF6590, (string?)null), (primera.Numero, primera.Nombre, primera.Perfil, primera.CostosJson));
        Assert.Equal(primera.Id, trabajo.ImpresoraId);
        Assert.Null(trabajo.Nota);
    }

    [Fact]
    public async Task Cada_trabajo_se_cobra_con_los_costos_de_la_impresora_de_donde_salio()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        var wf1 = (await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        var l5590 = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, "192.0.2.23");
        var wf2 = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "WF-6590 2", PerfilImpresora.WF6590, null);
        var registro = _base.Registro(db);
        // Sin los precios de sus botellas todavía: cobra como la WF-6590, no gratis.
        var sinPrecios = await registro.RegistrarAsync(Trabajo(caja, "Antes de los precios.pdf", Ahora, l5590.Numero));
        await impresoras.GuardarConsumiblesAsync(l5590, Botellas(250));

        var enLa1 = await registro.RegistrarAsync(Trabajo(caja, "Uno.pdf", Ahora, 1));
        var enLaL5590 = await registro.RegistrarAsync(Trabajo(caja, "Dos.pdf", Ahora, l5590.Numero));
        var enLa3 = await registro.RegistrarAsync(Trabajo(caja, "Tres.pdf", Ahora, wf2.Numero));
        var enUnaQueNoExiste = await registro.RegistrarAsync(Trabajo(caja, "Cuatro.pdf", Ahora, 9));

        Assert.Equal([wf1.Id, l5590.Id, wf2.Id, wf1.Id], new[] { enLa1, enLaL5590, enLa3, enUnaQueNoExiste }.Select(t => t.ImpresoraId));
        // Las dos WF-6590 usan los mismos cartuchos: el mismo documento cuesta lo mismo.
        Assert.Equal((enLa1.CostoCentavos, enLa1.PrecioCentavos), (enLa3.CostoCentavos, enLa3.PrecioCentavos));
        // En la L5590, con sus botellas, sin caja de mantenimiento y sin depreciación todavía, cuesta menos.
        Assert.True(enLaL5590.CostoTintaCentavos < enLa1.CostoTintaCentavos, $"{enLaL5590.CostoTintaCentavos} contra {enLa1.CostoTintaCentavos}");
        Assert.True(enLaL5590.CostoOtrosCentavos < enLa1.CostoOtrosCentavos);
        Assert.Equal((enLa1.CostoCentavos, enLa1.PrecioCentavos), (sinPrecios.CostoCentavos, sinPrecios.PrecioCentavos));
        Assert.Equal(l5590.Id, sinPrecios.ImpresoraId);
        // Uno de una impresora que no existe no se pierde: se anota en la primera y lo dice.
        Assert.Contains("impresora 9", enUnaQueNoExiste.Nota);
        Assert.Equal(enLa1.PrecioCentavos, enUnaQueNoExiste.PrecioCentavos);
    }

    [Fact]
    public async Task La_L5590_trae_sus_botellas_sin_caja_de_mantenimiento_y_dice_lo_que_le_falta()
    {
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        var wf = (await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        var l5590 = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, null);

        var propios = Assert.Single(await impresoras.ConsumiblesPropiosAsync(PosDePrueba.Sucursal));
        Assert.Equal(["Botella negra de la L5590", "Botella cian de la L5590", "Botella magenta de la L5590", "Botella amarilla de la L5590"], propios.Faltan);
        Assert.Equal([4300, 7300, 7300, 7300], propios.Tintas.Select(t => t.Rendimiento));
        Assert.Null(propios.CajaMantenimiento);
        // Mientras le falten precios cobra con los cartuchos de la sucursal (como una WF-6590), nunca gratis.
        Assert.Equal("T748XXL120-AL", (await impresoras.CostosAsync(l5590)).Black.Sku);

        var cambios = await impresoras.GuardarConsumiblesAsync(l5590, Botellas(259));

        var costos = await impresoras.CostosAsync(l5590);
        Assert.Equal(["T544120-AL", "T544220-AL", "T544320-AL", "T544420-AL"], new[] { costos.Black, costos.Cyan, costos.Magenta, costos.Yellow }.Select(c => c.Sku));
        Assert.Equal((259m, 0, 0m), (costos.Black.Price, costos.Maintenance.PagesPerBox, costos.Maintenance.BoxPrice));
        // Sin caja no le falta su precio; lo demás (el papel, el margen) es de la sucursal.
        Assert.DoesNotContain("Caja de mantenimiento", costos.MissingPrices());
        Assert.Equal((await _base.Registro(db).CostosAsync(PosDePrueba.Sucursal)).MarginPercent, costos.MarginPercent);
        Assert.Contains("el precio de la botella negra de la L5590", cambios);
        Assert.Empty(Assert.Single(await impresoras.ConsumiblesPropiosAsync(PosDePrueba.Sucursal)).Faltan);
        // Lo de antes se queda en el historial, de esa impresora.
        var antes = Assert.Single(await db.Set<HistorialCostosImpresion>().ToListAsync());
        Assert.Equal((l5590.Id, "Configuración"), (antes.ImpresoraId, antes.PorLosDe));
        // Una caja de mantenimiento sin decir cuánto dura no se le pone; diciéndolo, sí (si en la tienda resulta que tiene la C9344).
        await Assert.ThrowsAsync<ArgumentException>(() => impresoras.GuardarConsumiblesAsync(l5590, Botellas(259, caja: 180)));
        await impresoras.GuardarConsumiblesAsync(l5590, Botellas(259, caja: 180, paginasPorCaja: 30000));
        Assert.Equal((180m, 30000), ((await impresoras.CostosAsync(l5590)).Maintenance.BoxPrice, (await impresoras.CostosAsync(l5590)).Maintenance.PagesPerBox));
        // Una WF-6590 usa los cartuchos de la sucursal: esos se cambian en los costos de la sucursal.
        await Assert.ThrowsAsync<InvalidOperationException>(() => impresoras.GuardarConsumiblesAsync(wf, Botellas(1)));
    }

    [Fact]
    public async Task Las_lecturas_y_el_contador_son_de_cada_impresora_y_el_dia_de_la_sucursal_los_suma()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        var t0 = Ahora.AddHours(-2);
        PrinterStatus Lectura(int minutos, long contador) => new() { TakenUtc = t0.AddMinutes(minutos), InkK = 50, InkC = 50, InkM = 50, InkY = 50, LifeCount = contador };
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        var wf = (await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        var l5590 = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, null);
        var registro = _base.Registro(db);
        await impresoras.GuardarLecturaAsync(wf, Lectura(0, 1000));
        await impresoras.GuardarLecturaAsync(l5590, Lectura(1, 70_000));
        await registro.RegistrarAsync(Trabajo(caja, "En la WF.pdf", t0.AddMinutes(10), wf.Numero, paginas: 3));
        await registro.RegistrarAsync(Trabajo(caja, "En la L5590.pdf", t0.AddMinutes(11), l5590.Numero, paginas: 2));
        // La WF-6590 contó sus 3 y 1 que nadie registró; la L5590, sus 2 y 4 más (el celular, por ejemplo).
        await impresoras.GuardarLecturaAsync(wf, Lectura(30, 1004));
        await impresoras.GuardarLecturaAsync(l5590, Lectura(31, 70_006));

        var dia = DateOnly.FromDateTime(HoraMexico.ALocal(Ahora));
        var enLaWf = (await impresoras.ComparacionAsync(wf, dia))!;
        var enLaL5590 = (await impresoras.ComparacionAsync(l5590, dia))!;
        Assert.Equal((4L, 3, 1L), (enLaWf.Counted, enLaWf.RegisteredTotal, enLaWf.Unregistered));
        Assert.Equal((6L, 2, 4L), (enLaL5590.Counted, enLaL5590.RegisteredTotal, enLaL5590.Unregistered));
        var sucursal = (await impresoras.ComparacionDeLaSucursalAsync(PosDePrueba.Sucursal, dia))!;
        Assert.Equal((10L, 5, 5L), (sucursal.Counted, sucursal.RegisteredTotal, sucursal.Unregistered));
        Assert.Equal(sucursal.Counted, (await _base.EnLaVenta(db).ResumenDelDiaAsync(PosDePrueba.Sucursal, dia)).Contador!.Counted);
        Assert.Equal(70_006, (await impresoras.UltimaAsync(l5590))!.Contador);
        Assert.Equal(1004, (await impresoras.UltimaAsync(wf))!.Contador);
    }

    [Fact]
    public async Task Con_varias_los_avisos_dicen_de_que_impresora_y_la_L5590_habla_de_su_tinta()
    {
        var t0 = Ahora.AddHours(-1);
        PrinterStatus Nivel(int minutos, int cian) => new() { TakenUtc = t0.AddMinutes(minutos), InkK = 50, InkC = cian, InkM = 50, InkY = 50, LifeCount = 5000 };
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        var wf = (await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        await impresoras.GuardarLecturaAsync(wf, Nivel(0, 9));
        var l5590 = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, null);
        await impresoras.GuardarLecturaAsync(l5590, Nivel(1, 9));
        await impresoras.GuardarLecturaAsync(l5590, Nivel(2, 100));

        Assert.Equal(
        [
            "Impresora: cartucho cian bajo\nQueda 9 % (el aviso es desde 10 %).",
            "Impresora L5590: tinta cian baja\nQueda 9 % (el aviso es desde 10 %).",
            "Impresora L5590: tinta cian nueva\nPasó de 9 % a 100 %.",
        ], _base.Pos.Avisos.Select(a => a.Texto));
    }

    [Fact]
    public async Task La_calibracion_de_la_1_va_a_los_costos_de_la_sucursal_y_la_de_las_demas_a_cada_una()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        var t0 = Ahora.AddHours(-3);
        PrinterStatus Nivel(int minutos, int negro, long contador) =>
            new() { TakenUtc = t0.AddMinutes(minutos), InkK = negro, InkC = 50, InkM = 50, InkY = 50, LifeCount = contador };
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        var registro = _base.Registro(db);
        var wf1 = (await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        var wf2 = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "WF-6590 2", PerfilImpresora.WF6590, null);
        foreach (var impresora in new[] { wf1, wf2 })
        {
            // El negro bajó 10 puntos con 100 páginas: el cálculo se quedó corto.
            await impresoras.GuardarLecturaAsync(impresora, Nivel(0, 80, 1000));
            await registro.RegistrarAsync(Trabajo(caja, $"Libro {impresora.Numero}.pdf", t0.AddMinutes(30), impresora.Numero, paginas: 100));
            await impresoras.GuardarLecturaAsync(impresora, Nivel(60, 70, 1100));
        }
        var antesDeLaSucursal = (await registro.CostosAsync(PosDePrueba.Sucursal)).Calibration.K;

        await impresoras.AplicarCalibracionAsync(wf2);

        // La de la 2 se queda en ella: la sucursal (y la 1) siguen como estaban.
        var propios = ServicioImpresora.Propios((await impresoras.PorIdAsync(wf2.Id))!);
        Assert.NotNull(propios?.Calibracion);
        Assert.Null(propios!.Consumibles);
        Assert.True((await impresoras.CostosAsync(wf2)).Calibration.K > 1);
        Assert.Equal(antesDeLaSucursal, (await registro.CostosAsync(PosDePrueba.Sucursal)).Calibration.K);
        Assert.Equal(antesDeLaSucursal, (await impresoras.CostosAsync(wf1)).Calibration.K);
        Assert.Equal(wf2.Id, Assert.Single(await db.Set<HistorialCostosImpresion>().ToListAsync()).ImpresoraId);

        await impresoras.AplicarCalibracionAsync(wf1);

        // La de la 1 va a los costos de la sucursal, como siempre (así la calibra Control Print en la caja 2).
        Assert.True((await registro.CostosAsync(PosDePrueba.Sucursal)).Calibration.K > 1);
        Assert.Null((await impresoras.PorIdAsync(wf1.Id))!.CostosJson);
    }

    [Fact]
    public async Task Se_agregan_con_el_numero_que_sigue_con_nombre_propio_y_la_ultima_activa_no_se_da_de_baja()
    {
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        var wf = (await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        await Assert.ThrowsAsync<ArgumentException>(() => impresoras.AgregarAsync(PosDePrueba.Sucursal, " wf-6590 ", PerfilImpresora.WF6590, null));
        await Assert.ThrowsAsync<ArgumentException>(() => impresoras.AgregarAsync(PosDePrueba.Sucursal, "  ", PerfilImpresora.L5590, null));
        var segunda = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "  WF-6590   de la  entrada ", PerfilImpresora.WF6590, " 192.0.2.22 ");
        Assert.Equal((2, "WF-6590 de la entrada", "192.0.2.22"), (segunda.Numero, segunda.Nombre, segunda.Host));

        Assert.Contains(segunda.Id, (await impresoras.ConImpresoraAsync()).Select(i => i.Id));

        await impresoras.CambiarAsync(segunda, null, null, activa: false);

        // De baja no se lee ni sale entre las activas, pero sigue en la sucursal y su número no se vuelve a usar.
        Assert.DoesNotContain(segunda.Id, (await impresoras.ConImpresoraAsync()).Select(i => i.Id));
        Assert.Equal([wf.Id], (await impresoras.ImpresorasAsync(PosDePrueba.Sucursal)).Select(i => i.Id));
        Assert.Equal(2, (await impresoras.ImpresorasAsync(PosDePrueba.Sucursal, tambienDeBaja: true)).Count);
        Assert.Equal(3, (await impresoras.AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, null)).Numero);
        var ultima = await impresoras.CambiarAsync(wf, null, null, activa: false);
        Assert.False(ultima.Activa);
        var l5590 = (await impresoras.ImpresoraAsync(PosDePrueba.Sucursal, 3))!;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => impresoras.CambiarAsync(l5590, null, null, activa: false));
        Assert.Contains("única impresora activa", error.Message);
    }

    [Fact]
    public async Task Con_una_sola_impresora_las_tarjetas_no_dicen_de_cual_y_con_varias_si()
    {
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        var wf = (await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        Assert.Null((await impresoras.NombresAsync(PosDePrueba.Sucursal))(wf.Id));

        var l5590 = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, null);

        var nombres = await impresoras.NombresAsync(PosDePrueba.Sucursal);
        Assert.Equal(("WF-6590", "L5590", "WF-6590"), (nombres(wf.Id), nombres(l5590.Id), nombres(null)));
    }

    [Fact]
    public void Las_tarjetas_de_Control_Print_dicen_su_cola_cuando_lo_que_se_ve_viene_de_varias()
    {
        TrabajoControlPrint De(long id, string cola, TipoTrabajoControlPrint tipo = TipoTrabajoControlPrint.Impresion) =>
            new() { Id = id, Impresora = cola, Tipo = tipo, Documento = $"Doc {id}", PcOrigen = "PRINCIPAL", DetectadoUtc = Ahora, Paginas = 1, Copias = 1 };
        var deUna = new[] { De(1, "WF-6590 Series(Red)"), De(2, " wf-6590 series(red) "), De(3, "Copias en la impresora", TipoTrabajoControlPrint.CopiaEnElPanel) };
        var deDos = new[] { De(1, "WF-6590 Series(Red)"), De(2, "EPSON L5590 Series"), De(3, "Copias en la impresora", TipoTrabajoControlPrint.CopiaEnElPanel) };

        Assert.False(TarjetaControlPrint.DeVariasColas(deUna));
        Assert.True(TarjetaControlPrint.DeVariasColas(deDos));
        Assert.Equal(["WF-6590 Series(Red)", "EPSON L5590 Series", null], deDos.Select(t => TarjetaControlPrint.De(t, false, conImpresora: true).Impresora));
        Assert.All(deUna, t => Assert.Null(TarjetaControlPrint.De(t, false).Impresora));
        // Si cambia la cola que se dice, la caja vuelve a pintar.
        Assert.NotEqual(TarjetaControlPrint.Version([TarjetaControlPrint.De(deDos[1], false)]),
            TarjetaControlPrint.Version([TarjetaControlPrint.De(deDos[1], false, conImpresora: true)]));
    }

    [Fact]
    public async Task Las_copias_del_panel_son_de_la_impresora_donde_se_hicieron()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true);
        var l5590 = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, null);
        await impresoras.GuardarConsumiblesAsync(l5590, Botellas(250));
        var registro = _base.Registro(db);

        var enLaWf = await registro.RegistrarCopiasAsync(new SolicitudCopias(Guid.NewGuid(), caja.Id, null, 20, false, "Carta", false, null, null));
        var enLaL5590 = await registro.RegistrarCopiasAsync(new SolicitudCopias(Guid.NewGuid(), caja.Id, null, 20, false, "Carta", false, null, null,
            Impresora: l5590.Numero));

        Assert.Equal(l5590.Id, enLaL5590.ImpresoraId);
        Assert.NotEqual(l5590.Id, enLaWf.ImpresoraId);
        Assert.True(enLaL5590.CostoCentavos < enLaWf.CostoCentavos);
        var (precio, _) = await registro.CalcularCopiasAsync(PosDePrueba.Sucursal, 20, false, "Carta", false, l5590.Numero);
        Assert.Equal(enLaL5590.PrecioCentavos, (long)Math.Round(precio.Price * 100));
    }
}
