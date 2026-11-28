using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Avisos;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Las hojas en las charolas y su aviso en el «!» de la caja (2026-11-28). Las impresoras de la tienda no dicen cuántas hojas les quedan: se
/// anota cuántas se ponen al llenar y se restan las de lo que sale. Se avisa antes de que se acaben, con lo que se imprime a esas horas (y
/// hasta el final de la hora pico si viene pronto), y el aviso se quita al anotar el llenado.
/// </summary>
public sealed class PapelDeLasImpresorasTests : IAsyncLifetime
{
    private BaseDelModulo _base = null!;

    public async Task InitializeAsync() => _base = await BaseDelModulo.CrearAsync();

    public async Task DisposeAsync() => await _base.DisposeAsync();

    /// <summary>Esa hora de México (UTC-6) de un día de octubre de 2026.</summary>
    private static DateTime Local(int dia, int hora, int minuto = 0) => new DateTime(2026, 10, dia, hora, minuto, 0, DateTimeKind.Utc).AddHours(6);

    private static TrabajoImpresion Trabajo(Impresora impresora, DateTime utc, int hojas, string tamano = "Carta", int? caras = null) => new()
    {
        SucursalId = impresora.SucursalId, ImpresoraId = impresora.Id, Tipo = TipoTrabajoImpresion.Impresion, CajaId = Guid.NewGuid(), Pc = "CAJA1",
        Cola = "WF-6590", Documento = "Tarea.pdf", ImpresoUtc = utc, Estado = EstadoTrabajo.Impreso, Paginas = hojas, Copias = 1,
        Caras = caras ?? hojas, Hojas = hojas, Tamano = tamano, TipoPapel = "Plain", Calidad = "Standard", DetalleJson = "{}",
        Cobro = EstadoCobroImpresion.PorCobrar,
    };

    private PosDePrueba Pos => _base.Pos;

    [Fact]
    public async Task Sin_llenado_pide_anotar_el_proximo_y_al_anotarlo_se_quita()
    {
        await using var db = _base.Contexto();
        var impresora = (await _base.Impresora(db).PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        var papel = new PapelDeLasImpresoras(db, Pos);

        await papel.RevisarAvisosAsync(impresora);

        // Solo la charola principal, como informativo (el «!» se queda atenuado), con su acción para anotarlo.
        var (clave, aviso) = Assert.Single(Pos.EnLaCaja);
        Assert.Equal(PapelDeLasImpresoras.Clave(impresora, 1), clave);
        Assert.Equal(NivelAviso.Informativo, aviso.Nivel);
        Assert.Equal("WF-6590 · Charola: anota el próximo llenado", aviso.Titulo);
        Assert.Equal(new AccionDeAviso(PapelDeLasImpresoras.AccionAnotar, "Anotar el papel que pusiste", "1:1"), aviso.Accion);

        var (llenado, error) = await papel.AnotarAsync(impresora, 1, 0, llena: true, null, null);

        Assert.Null(error);
        Assert.Equal((500, 500, "Carta"), (llenado!.Puestas, llenado.Quedaron, llenado.Tamano));
        Assert.Empty(Pos.EnLaCaja);
    }

    [Fact]
    public async Task Avisa_antes_de_la_hora_pico_y_el_aviso_se_quita_al_llenar()
    {
        await using var db = _base.Contexto();
        var impresora = (await _base.Impresora(db).PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        // Dos semanas: 60 hojas de 18:00 a 19:00 (la hora pico) y 10 a mediodía.
        for (int dia = 1; dia <= 14; dia++)
        {
            db.Add(Trabajo(impresora, Local(dia, 18, 20), 60));
            db.Add(Trabajo(impresora, Local(dia, 12, 10), 10));
        }
        await db.SaveChangesAsync();
        var papel = new PapelDeLasImpresoras(db, Pos);
        Pos.Hora.Ahora = new DateTimeOffset(Local(15, 15, 0));
        Assert.Null((await papel.AnotarAsync(impresora, 1, 80, llena: false, "Carta", null)).Error);
        Assert.Empty(Pos.EnLaCaja);

        // Salen 30 hojas: quedan 50, que no alcanzan para la hora pico que empieza a las 18:00 (unas 57).
        db.Add(Trabajo(impresora, Local(15, 15, 10), 30));
        await db.SaveChangesAsync();
        Pos.Hora.Ahora = new DateTimeOffset(Local(15, 15, 30));
        await papel.RevisarAvisosAsync(impresora);

        var estado = (await papel.EstadoAsync(impresora))[0];
        Assert.Equal((50, true), (estado.Quedan, estado.Pico));
        Assert.Equal(new DateTime(2026, 10, 15, 19, 0, 0), estado.HastaLocal);
        var aviso = Assert.Single(Pos.EnLaCaja).Value;
        Assert.Equal(NivelAviso.Atencion, aviso.Nivel);
        Assert.Equal("WF-6590 · Charola: quedan ~50 hojas", aviso.Titulo);
        Assert.StartsWith($"De aquí a las 19:00, el final de la hora pico, se imprimen unas {estado.Esperadas}: llénala antes.", aviso.Texto);
        var desde = aviso.Desde;

        // Otra revisión: el mismo aviso, con su «desde».
        Pos.Hora.Ahora = new DateTimeOffset(Local(15, 15, 40));
        await papel.RevisarAvisosAsync(impresora);
        Assert.Equal(desde, Assert.Single(Pos.EnLaCaja).Value.Desde);

        // Se llena: lo que había más lo puesto, sin pasar de lo que le cabe; el aviso se quita.
        var (llenado, _) = await papel.AnotarAsync(impresora, 1, 300, llena: false, "Carta", null);
        Assert.Equal(350, llenado!.Quedaron);
        Assert.Empty(Pos.EnLaCaja);
    }

    [Fact]
    public async Task Sin_papel_es_urgente_y_lo_de_otro_tamano_va_a_su_charola()
    {
        await using var db = _base.Contexto();
        var impresora = (await _base.Impresora(db).PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        var papel = new PapelDeLasImpresoras(db, Pos);

        await papel.RevisarAvisosAsync(impresora, sinPapel: true);
        var urgente = Assert.Single(Pos.EnLaCaja).Value;
        Assert.Equal((NivelAviso.Urgente, "WF-6590 · Charola: sin papel"), (urgente.Nivel, urgente.Titulo));

        await papel.AnotarAsync(impresora, 1, 0, llena: true, "Carta", null);
        await papel.AnotarAsync(impresora, 2, 40, llena: false, "Oficio", null);
        Pos.Hora.Ahora = Pos.Hora.Ahora.AddMinutes(5);
        db.Add(Trabajo(impresora, Pos.Hora.Ahora.UtcDateTime.AddMinutes(-1), 34, "Oficio"));
        db.Add(Trabajo(impresora, Pos.Hora.Ahora.UtcDateTime.AddMinutes(-1), 20, "Carta"));
        await db.SaveChangesAsync();
        await papel.RevisarAvisosAsync(impresora);

        var estados = await papel.EstadoAsync(impresora);
        Assert.Equal([480, 6], estados.Select(e => e.Quedan));
        // Al alimentador trasero le quedan 6: a esta hora salen unas 34 de oficio por hora, así que no alcanzan ni para media hora.
        var (clave, aviso) = Assert.Single(Pos.EnLaCaja);
        Assert.Equal(PapelDeLasImpresoras.Clave(impresora, 2), clave);
        Assert.Equal((NivelAviso.Urgente, "WF-6590 · Alimentador trasero: quedan ~6 hojas", "1:2"), (aviso.Nivel, aviso.Titulo, aviso.Accion?.Datos));
        // Sin lo que se imprime a esa hora, 6 de 80 es poco (10 o menos), pero no urgente (5 o menos).
        var sinHistoria = CalculoDelPapel.Evaluar(PapelDeImpresoras.WF6590[1], estados[1].Llenado, 34, new double[24], Pos.Hora.Ahora.UtcDateTime);
        Assert.Equal((6, NivelAviso.Atencion), (sinHistoria.Quedan, sinHistoria.Nivel));
    }

    [Fact]
    public async Task Si_la_impresora_dice_cuantas_le_quedan_eso_manda()
    {
        await using var db = _base.Contexto();
        var impresora = await _base.Impresora(db).AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, null);
        var papel = new PapelDeLasImpresoras(db, Pos);

        var estado = Assert.Single(await papel.EstadoAsync(impresora, false, [new InputTray(1, 8, 100)]));

        Assert.Equal(("Bandeja", 8, true, NivelAviso.Atencion), (estado.Charola.Nombre, estado.Quedan, estado.Reportadas, estado.Nivel));
        Assert.Equal("L5590 · Bandeja: quedan 8 hojas", PapelDeLasImpresoras.Textos(impresora, estado).Titulo);
        // «-3» (hay al menos una) o «-2» (no se sabe) no son un número: se calcula.
        Assert.Null(Assert.Single(await papel.EstadoAsync(impresora, false, [new InputTray(1, -3, 100)])).Quedan);
    }

    [Fact]
    public async Task Lo_que_no_se_puede_anotar_lo_dice()
    {
        await using var db = _base.Contexto();
        var impresora = await _base.Impresora(db).AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, null);
        var papel = new PapelDeLasImpresoras(db, Pos);

        Assert.Equal("La L5590 no tiene la charola 2.", (await papel.AnotarAsync(impresora, 2, 50, false, null, null)).Error);
        Assert.Equal("Escribe cuántas hojas pusiste (o marca que la llenaste).", (await papel.AnotarAsync(impresora, 1, 0, false, null, null)).Error);
        Assert.Equal("A la bandeja le caben unas 100 hojas: revisa el número.", (await papel.AnotarAsync(impresora, 1, 500, false, null, null)).Error);
        Assert.Equal("El tamaño es Carta, Oficio o A4.", (await papel.AnotarAsync(impresora, 1, 50, false, "Otro", null)).Error);
        Assert.Empty(await papel.UltimosLlenadosAsync(impresora.Id));
    }

    [Fact]
    public void Las_hojas_de_un_trabajo_son_las_que_salieron()
    {
        var impresora = new Impresora { Nombre = "WF-6590" };
        Assert.Equal(10, CalculoDelPapel.HojasDe(Trabajo(impresora, DateTime.UtcNow, 10)));
        // Un atasco: de 10 caras salieron 4.
        var atasco = Trabajo(impresora, DateTime.UtcNow, 10);
        atasco.AplicarCarasContadas(4);
        Assert.Equal(4, CalculoDelPapel.HojasDe(atasco));
        // A doble cara, 5 hojas con 10 caras; salieron 4 caras: 2 hojas.
        var duplex = Trabajo(impresora, DateTime.UtcNow, 5, caras: 10);
        duplex.CarasContadas = 4;
        Assert.Equal(2, CalculoDelPapel.HojasDe(duplex));
        // Una tarjeta «Copias o celular» sin hojas: una por cara.
        var contador = Trabajo(impresora, DateTime.UtcNow, 0, caras: 7);
        contador.Tipo = TipoTrabajoImpresion.Contador;
        Assert.Equal(7, CalculoDelPapel.HojasDe(contador));
    }

    [Fact]
    public void Lo_esperado_cuenta_la_parte_de_la_hora_que_va_y_la_hora_pico_alarga_el_plazo()
    {
        var uso = new double[24];
        uso[10] = 12;
        uso[11] = 12;
        uso[19] = 40;

        // 10:30 → 12:30: media hora a 12, una a 12 y media a nada; el pico (19:00) empieza en más de 3 horas.
        var (esperadas, hasta, pico) = CalculoDelPapel.Esperadas(uso, new DateTime(2026, 10, 15, 10, 30, 0), CalculoDelPapel.Plazo);
        Assert.Equal((18.0, new DateTime(2026, 10, 15, 12, 30, 0), false), (esperadas, hasta, pico));

        // 16:30: el pico empieza en 2 h 30 min: hasta que termina (20:00).
        (esperadas, hasta, pico) = CalculoDelPapel.Esperadas(uso, new DateTime(2026, 10, 15, 16, 30, 0), CalculoDelPapel.Plazo);
        Assert.Equal((40.0, new DateTime(2026, 10, 15, 20, 0, 0), true), (esperadas, hasta, pico));

        Assert.Equal([19], CalculoDelPapel.HorasPico(uso).Select((p, h) => (p, h)).Where(x => x.p).Select(x => x.h));
    }

    [Fact]
    public void Lo_que_queda_al_llenar_no_pasa_de_lo_que_le_cabe()
    {
        var charola = PapelDeImpresoras.WF6590[0];
        Assert.Equal(500, CalculoDelPapel.QuedaronAlLlenar(charola, 0, 30, llena: true));
        Assert.Equal(230, CalculoDelPapel.QuedaronAlLlenar(charola, 200, 30, llena: false));
        Assert.Equal(500, CalculoDelPapel.QuedaronAlLlenar(charola, 480, 30, llena: false));
        Assert.Equal(200, CalculoDelPapel.QuedaronAlLlenar(charola, 200, null, llena: false));
    }

    [Fact]
    public void El_st2_de_epson_dice_sin_papel_solo_con_la_impresora_en_error()
    {
        static byte[] St2(params (byte Codigo, byte[] Datos)[] campos)
        {
            var cuerpo = campos.SelectMany(c => new[] { c.Codigo, (byte)c.Datos.Length }.Concat(c.Datos)).ToArray();
            return [.. "@BDC ST2\r\n"u8.ToArray(), (byte)cuerpo.Length, 0, .. cuerpo];
        }

        Assert.True(EpsonSt2.TryParse(St2((0x01, [0x00]), (0x02, [0x06])), out var sinPapel));
        Assert.Equal(EpsonSt2.ErrorSinPapel, sinPapel.ErrorCode);
        Assert.True(new PrinterStatus { EpsonError = sinPapel.ErrorCode }.PaperOut);

        // Lista (0x04): el campo de error no cuenta.
        Assert.True(EpsonSt2.TryParse(St2((0x01, [0x04]), (0x02, [0x06])), out var lista));
        Assert.Null(lista.ErrorCode);
        Assert.Null(new PrinterStatus().PaperOut);
        Assert.False(new PrinterStatus { Inputs = [new InputTray(1, -3, -2)] }.PaperOut);
        Assert.True(new PrinterStatus { Inputs = [new InputTray(1, 0, 500)] }.PaperOut);
    }
}
