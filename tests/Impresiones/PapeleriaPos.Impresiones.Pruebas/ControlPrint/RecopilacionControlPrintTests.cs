using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Impresiones.Datos;

namespace PapeleriaPos.Impresiones.Pruebas.ControlPrint;

/// <summary>
/// recopilar-control-print contra el Control Print falso: lo que deja en la carpeta, que el código nunca aparece, que solo
/// lee y que se puede detener y seguir.
/// </summary>
public sealed class RecopilacionControlPrintTests : IAsyncLifetime
{
    /// <summary>Inventado, con el formato de Control Print.</summary>
    private const string Codigo = "Q4WE-8RTY-2UPA-5SDF";
    private static readonly DateTime Desde = new(2026, 8, 3, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Hasta = new(2026, 10, 24, 23, 0, 0, DateTimeKind.Utc);
    /// <summary>El 25 de octubre de 2026 a mediodía en México: la carpeta es «2026-10-25».</summary>
    private static readonly DateTimeOffset Ahora = new(2026, 10, 25, 18, 0, 0, TimeSpan.Zero);
    /// <summary>Uno que una PC mandó tarde: número alto y fecha anterior al primer mes.</summary>
    private static readonly DateTime DeJulio = new(2026, 7, 20, 17, 0, 0, DateTimeKind.Utc);

    private readonly string _salida = Path.Combine(Path.GetTempPath(), $"recopilacion-cp-{Guid.NewGuid():N}");
    private ServidorControlPrintFalso _servidor = null!;

    private string Carpeta => Path.Combine(_salida, "2026-10-25");

    public async Task InitializeAsync() => _servidor = await ServidorControlPrintFalso.IniciarAsync(Codigo, s =>
    {
        s.Trabajos.AddRange(DatosDeControlPrint.Trabajos(130, Desde, Hasta));
        var tarde = DatosDeControlPrint.Trabajos(1, DeJulio, DeJulio.AddHours(1), semilla: 5)[0];
        tarde.Id = 131;
        s.Trabajos.Add(tarde);
        s.Cotizaciones.AddRange(DatosDeControlPrint.Cotizaciones(Desde));
        s.Lecturas.AddRange(DatosDeControlPrint.Lecturas(Desde, Hasta));
    });

    public async Task DisposeAsync()
    {
        await _servidor.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_salida))
            Directory.Delete(_salida, recursive: true);
    }

    private OpcionesRecopilacion Opciones(bool miniaturas = true, DateOnly? desde = null) =>
        new("127.0.0.1", _servidor.Puerto, _salida, desde, miniaturas, null);

    private Task<int> RecopilarAsync(TextWriter salida, OpcionesRecopilacion? opciones = null, CancellationToken ct = default, CodigoDeConexion? codigo = null) =>
        RecopilacionControlPrint.EjecutarAsync(opciones ?? Opciones(), codigo ?? new CodigoDeConexion(() => Codigo, false, "de la prueba"), salida,
            new RelojDePrueba(Ahora), ct, porPagina: 20, esperas: [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)]);

    private static List<object?[]> Filas(string carpeta, string consulta)
    {
        using var conexion = new SqliteConnection($"Data Source={Path.Combine(carpeta, "control-print.db")};Mode=ReadOnly;Pooling=False");
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = consulta;
        using var lector = comando.ExecuteReader();
        var filas = new List<object?[]>();
        while (lector.Read())
        {
            var fila = new object?[lector.FieldCount];
            for (var i = 0; i < fila.Length; i++)
                fila[i] = lector.IsDBNull(i) ? null : lector.GetValue(i);
            filas.Add(fila);
        }
        return filas;
    }

    private object? Valor(string consulta, string? carpeta = null) => Filas(carpeta ?? Carpeta, consulta).Single()[0];

    private void SoloLeyo()
    {
        Assert.Empty(_servidor.Escrituras);
        Assert.All(_servidor.Peticiones, p => Assert.True(p.EsLectura, $"{p.Metodo} {p.Ruta}"));
    }

    [Fact]
    public async Task Deja_todo_en_la_carpeta_de_hoy()
    {
        // Un campo que mandaría un Control Print más nuevo: no se pierde y el README lo dice.
        _servidor.Trabajos[3].Extra = new() { ["printerSerial"] = System.Text.Json.JsonSerializer.SerializeToElement("X3ZT001234") };
        var salida = new StringWriter();

        var resultado = await RecopilarAsync(salida);

        Assert.True(resultado == 0, salida.ToString());
        Assert.True(File.Exists(Path.Combine(Carpeta, "control-print.db")));
        Assert.Equal("terminada", Valor("SELECT valor FROM recopilacion WHERE clave = 'estado'"));
        Assert.Equal("PRINCIPAL", Valor("SELECT valor FROM recopilacion WHERE clave = 'nombre_servidor'"));
        Assert.Equal(_servidor.Huella, Valor("SELECT valor FROM recopilacion WHERE clave = 'huella'"));

        // Los trabajos, todos y con todos sus campos (también el que llegó tarde con una fecha de antes).
        Assert.Equal(131L, Valor("SELECT COUNT(*) FROM trabajos"));
        Assert.Equal(1L, Valor("SELECT COUNT(*) FROM trabajos WHERE id = 131 AND dia = '2026-07-20'"));
        var noVendida = _servidor.Trabajos.First(t => t.Payment == PaymentState.Unsold && t.UnsoldReason == UnsoldReason.Other && t.Thumbnail is not null);
        var fila = Filas(Carpeta, $"""
            SELECT tipo, pc_origen, documento, estado, cobro, motivo_no_vendida, nota_no_vendida, costo_total, precio, precio_a_cobrar,
                   caras, caras_color, color, dia, hora_local, detectado_utc, desglose_json, atendio_nombre, miniatura, cobro_por
            FROM trabajos WHERE id = {noVendida.Id}
            """).Single();
        Assert.Equal("Impresion", fila[0]);
        Assert.Equal(noVendida.SourcePc, fila[1]);
        Assert.Equal(noVendida.Document, fila[2]);
        Assert.Equal(noVendida.State == JobState.Printed ? "Impreso" : "Analizado", fila[3]);
        Assert.Equal("NoVendido", fila[4]);
        Assert.Equal("Otro", fila[5]);
        Assert.Equal("Se manchó", fila[6]);
        Assert.Equal((double)noVendida.CostTotal, (double)fila[7]!, 6);
        Assert.Equal((double)noVendida.Price, (double)fila[8]!, 6);
        Assert.Equal((double)noVendida.EffectivePrice, (double)fila[9]!, 6);
        Assert.Equal((long)noVendida.Sides, fila[10]);
        Assert.Equal((long)noVendida.ColorSides, fila[11]);
        Assert.Equal(noVendida.Color ? 1L : 0L, fila[12]);
        Assert.Equal(DatosDeControlPrint.DiaLocal(noVendida.DetectedUtc).ToString("yyyy-MM-dd"), fila[13]);
        Assert.Equal(noVendida.DetectedUtc, DateTime.Parse((string)fila[15]!, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        Assert.Equal(noVendida.DetailJson, fila[16]);
        Assert.Equal(noVendida.OwnerName, fila[17]);
        Assert.Equal($"miniaturas/{noVendida.Id}.png", fila[18]);
        Assert.Equal(DatosDeControlPrint.Png, File.ReadAllBytes(Path.Combine(Carpeta, "miniaturas", $"{noVendida.Id}.png")));
        Assert.Equal(noVendida.PaymentBy, fila[19]);
        Assert.Equal((long)_servidor.Trabajos.Count(t => t.Thumbnail is not null), Valor("SELECT COUNT(*) FROM trabajos WHERE miniatura LIKE 'miniaturas/%'"));
        Assert.Equal((long)_servidor.Trabajos.Count(t => t.Thumbnail is null), Valor("SELECT COUNT(*) FROM trabajos WHERE miniatura = ''"));
        Assert.Equal(_servidor.Trabajos.Count(t => t.Thumbnail is not null), Directory.GetFiles(Path.Combine(Carpeta, "miniaturas"), "*.png").Length);

        // Las copias del panel, el resumen y las caras de cada día.
        Assert.Equal((long)_servidor.Trabajos.Count(t => t.Kind == JobKind.PanelCopy), Valor("SELECT COUNT(*) FROM copias"));
        var dias = _servidor.Trabajos.Select(t => DatosDeControlPrint.DiaLocal(t.DetectedUtc)).Distinct().ToList();
        Assert.Equal((long)dias.Count, Valor("SELECT COUNT(*) FROM resumen_por_dia"));
        Assert.Equal((long)dias.Count, Valor("SELECT COUNT(*) FROM caras_por_dia"));
        var dia = DatosDeControlPrint.DiaLocal(noVendida.DetectedUtc);
        var delDia = _servidor.Trabajos.Where(t => DatosDeControlPrint.DiaLocal(t.DetectedUtc) == dia && t.IsChargeable).ToList();
        var resumen = Filas(Carpeta, $"SELECT trabajos, costo, no_vendidas FROM resumen_por_dia WHERE dia = '{dia:yyyy-MM-dd}'").Single();
        Assert.Equal((long)delDia.Count, resumen[0]);
        Assert.Equal((double)delDia.Sum(t => t.CostTotal), (double)resumen[1]!, 6);
        Assert.Equal((long)delDia.Count(t => t.Payment == PaymentState.Unsold), resumen[2]);

        // Cotizaciones, lecturas, usuarios (sin secretos) y la configuración (sin lo que parezca un secreto).
        Assert.Equal(3L, Valor("SELECT COUNT(*) FROM cotizaciones"));
        Assert.Equal("Escuela de prueba", Valor("SELECT cliente FROM cotizaciones WHERE id = 2"));
        Assert.Equal((long)_servidor.Lecturas.Count, Valor("SELECT COUNT(*) FROM lecturas_impresora"));
        Assert.Equal(new[] { "id", "nombre", "rol", "activo" }, Filas(Carpeta, "SELECT name FROM pragma_table_info('usuarios')").Select(f => (string)f[0]!));
        Assert.Equal(4L, Valor("SELECT COUNT(*) FROM usuarios"));
        Assert.Equal("Invitado", Valor("SELECT rol FROM usuarios WHERE id = 3"));
        Assert.Contains("\"MarginPercent\": 85", (string)Valor("SELECT json FROM configuracion WHERE clave = 'costos'")!);
        var ajustes = (string)Valor("SELECT json FROM configuracion WHERE clave = 'ajustes'")!;
        Assert.Contains("\"PrinterHost\": \"192.0.2.50\"", ajustes);
        Assert.Contains("EndAdminSessionOnClose", ajustes);
        Assert.DoesNotContain("UpdateTokenProtected", ajustes);
        Assert.Equal(1L, Valor("SELECT COUNT(*) FROM configuracion WHERE clave = 'calibracion'"));

        // El README dice qué hay y de cuándo.
        var leame = File.ReadAllText(Path.Combine(Carpeta, "README.md"));
        Assert.Contains("**Completa:**", leame);
        Assert.Contains("PRINCIPAL (127.0.0.1:", leame);
        Assert.Contains("| Trabajos | 131 |", leame);
        Assert.Contains("hay del 2026-07-20 al 2026-10-24", leame);
        Assert.Contains("hasta las 12:00:00 del 2026-10-25", leame);
        Assert.Contains("| Cotizaciones | 3 |", leame);
        Assert.Contains("ajustes.UpdateTokenProtected", leame);
        Assert.Contains(ProtocoloControlPrint.HuellaLegible(_servidor.Huella), leame);
        Assert.Contains("no conoce (en `otros_json`): trabajos.printerSerial", leame);
        Assert.Contains("X3ZT001234", (string)Valor($"SELECT otros_json FROM trabajos WHERE id = {_servidor.Trabajos[3].Id}")!);
        Assert.Equal(1L, Valor("SELECT COUNT(*) FROM trabajos WHERE otros_json IS NOT NULL"));

        var texto = salida.ToString();
        Assert.Contains("Contestó Control Print 1.12.0 en PRINCIPAL", texto);
        Assert.Contains("antes del 2026-08-01: 1", texto);
        Assert.Contains($"Listo en", texto);
        Assert.Contains(Carpeta, texto);
        SoloLeyo();
    }

    [Fact]
    public async Task El_codigo_nunca_sale_en_pantalla_ni_en_los_archivos()
    {
        var salida = new StringWriter();
        Assert.Equal(0, await RecopilarAsync(salida));
        // Otra vez, con un código equivocado (al teclado): tampoco se repite.
        var equivocado = new Queue<string?>(["ZXCV-BNMA-SDFG-HJKL", "zxcv bnma sdfg hjk", null]);
        Assert.Equal(2, await RecopilarAsync(salida, codigo: new CodigoDeConexion(equivocado.Dequeue, true)));

        // Que sí se usó: cada lectura lo llevó en su encabezado (si no, la prueba no probaría nada).
        Assert.Contains(_servidor.Peticiones, p => p.Codigo == "Q4WE8RTY2UPA5SDF");
        var formas = new[] { Codigo, "Q4WE8RTY2UPA5SDF", "ZXCV-BNMA-SDFG-HJKL", "ZXCVBNMASDFGHJKL", "ZXCVBNMASDFGHJK" }
            .SelectMany(c => new[] { c, c.ToLowerInvariant() })
            .Concat(["Q4WE", "q4we8rty", "ZXCV", "zxcv"])
            .ToList();
        var texto = salida.ToString();
        Assert.All(formas, f => Assert.DoesNotContain(f, texto, StringComparison.OrdinalIgnoreCase));
        var archivos = Directory.GetFiles(_salida, "*", SearchOption.AllDirectories);
        Assert.Contains(archivos, a => a.EndsWith("control-print.db", StringComparison.Ordinal));
        foreach (var archivo in archivos)
        {
            var bytes = File.ReadAllBytes(archivo);
            foreach (var forma in formas)
                foreach (var codificado in new[] { Encoding.UTF8.GetBytes(forma), Encoding.Unicode.GetBytes(forma) })
                    Assert.True(bytes.AsSpan().IndexOf(codificado) < 0, $"{Path.GetFileName(archivo)} tiene «{forma}».");
        }
        SoloLeyo();
    }

    [Fact]
    public async Task Se_puede_detener_y_sigue_donde_se_quedo()
    {
        using var detener = new CancellationTokenSource();
        var busquedas = 0;
        _servidor.Antes = (_, p) =>
        {
            if (p.Ruta == "/api/jobs/search" && Interlocked.Increment(ref busquedas) == 5)
                detener.Cancel();
            return Task.FromResult(false);
        };
        var primera = new StringWriter();

        Assert.Equal(1, await RecopilarAsync(primera, ct: detener.Token));

        Assert.Contains("Se detuvo. Lo recopilado se quedó en", primera.ToString());
        Assert.Equal("en curso", Valor("SELECT valor FROM recopilacion WHERE clave = 'estado'"));
        var guardados = (long)Valor("SELECT COUNT(*) FROM trabajos")!;
        Assert.InRange(guardados, 1, 130);
        Assert.Contains("**Incompleta:** se detuvo a petición (Ctrl+C).", File.ReadAllText(Path.Combine(Carpeta, "README.md")));
        var terminadas = (long)Valor("SELECT COUNT(*) FROM progreso")!;

        _servidor.Antes = null;
        var yaPedidas = _servidor.Busquedas.Count;
        var segunda = new StringWriter();
        Assert.Equal(0, await RecopilarAsync(segunda));

        Assert.Contains("sigue la recopilación que se detuvo", segunda.ToString());
        Assert.False(Directory.Exists(Carpeta + "-2"));
        Assert.Equal(131L, Valor("SELECT COUNT(*) FROM trabajos"));
        Assert.Equal("terminada", Valor("SELECT valor FROM recopilacion WHERE clave = 'estado'"));
        Assert.Contains("**Completa:**", File.ReadAllText(Path.Combine(Carpeta, "README.md")));
        // Lo que ya estaba no se volvió a pedir: ni la búsqueda del trabajo más viejo (de uno en uno) ni la ventana «antes» (sin
        // fecha de inicio), que la primera vez se terminaron antes de detenerse; y llega hasta la misma hora que la primera vez.
        Assert.True(terminadas >= 1);
        var deLaSegunda = _servidor.Busquedas.Skip(yaPedidas).ToList();
        Assert.NotEmpty(deLaSegunda);
        Assert.DoesNotContain(deLaSegunda, q => q.Limit == 1 || q.FromUtc is null);
        Assert.Equal(Ahora.UtcDateTime, deLaSegunda.Max(q => q.ToUtc!.Value.ToUniversalTime()));
        SoloLeyo();
    }

    [Fact]
    public async Task Una_recopilacion_terminada_no_se_toca()
    {
        Assert.Equal(0, await RecopilarAsync(new StringWriter(), Opciones(miniaturas: false)));
        var leame = File.ReadAllText(Path.Combine(Carpeta, "README.md"));

        Assert.Equal(0, await RecopilarAsync(new StringWriter(), Opciones(miniaturas: false)));

        Assert.Equal(leame, File.ReadAllText(Path.Combine(Carpeta, "README.md")));
        Assert.Equal(131L, Valor("SELECT COUNT(*) FROM trabajos", Carpeta + "-2"));
        Assert.Contains("| Miniaturas | no se pidieron |", leame);
        Assert.Equal(131L, Valor("SELECT COUNT(*) FROM trabajos WHERE miniatura IS NULL"));
        Assert.False(Directory.Exists(Path.Combine(Carpeta, "miniaturas")));
    }

    [Fact]
    public async Task Con_desde_solo_lo_de_ese_dia_en_adelante()
    {
        var desde = new DateOnly(2026, 9, 15);

        Assert.Equal(0, await RecopilarAsync(new StringWriter(), Opciones(miniaturas: false, desde: desde)));

        var esperados = _servidor.Trabajos.Count(t => DatosDeControlPrint.DiaLocal(t.DetectedUtc) >= desde);
        Assert.Equal((long)esperados, Valor("SELECT COUNT(*) FROM trabajos"));
        Assert.Equal(0L, Valor("SELECT COUNT(*) FROM trabajos WHERE dia < '2026-09-15'"));
        Assert.Equal(0L, Valor("SELECT COUNT(*) FROM cotizaciones"));
        Assert.Equal(0L, Valor("SELECT COUNT(*) FROM lecturas_impresora WHERE hora_local < '2026-09-15'"));
        Assert.True((long)Valor("SELECT COUNT(*) FROM lecturas_impresora")! > 0);
        Assert.Contains("desde el 2026-09-15 (`--desde`)", File.ReadAllText(Path.Combine(Carpeta, "README.md")));
        SoloLeyo();
    }

    [Fact]
    public async Task Si_Control_Print_muestra_a_cada_quien_lo_suyo_lo_avisa()
    {
        _servidor.Visibilidad = "OwnOnly";
        var salida = new StringWriter();

        Assert.Equal(0, await RecopilarAsync(salida, Opciones(miniaturas: true)));

        Assert.Equal((long)_servidor.Trabajos.Count(t => t.OwnerId == DatosDeControlPrint.Invitado), Valor("SELECT COUNT(*) FROM trabajos"));
        Assert.Equal(0L, Valor($"SELECT COUNT(*) FROM trabajos WHERE atendio_id <> {DatosDeControlPrint.Invitado}"));
        Assert.Contains("«cada quien ve sus trabajos»", salida.ToString());
        Assert.Contains("«cada quien ve sus trabajos»", File.ReadAllText(Path.Combine(Carpeta, "README.md")));
        SoloLeyo();
    }

    [Fact]
    public async Task Si_Control_Print_deja_de_contestar_reintenta_se_detiene_y_se_puede_seguir()
    {
        var busquedas = 0;
        _servidor.Antes = (ctx, p) =>
        {
            if (p.Ruta != "/api/jobs/search" || Interlocked.Increment(ref busquedas) <= 3)
                return Task.FromResult(false);
            ctx.Abort();
            return Task.FromResult(true);
        };
        var salida = new StringWriter();

        Assert.Equal(1, await RecopilarAsync(salida));

        var texto = salida.ToString();
        Assert.Contains("Control Print no contestó; otro intento", texto);
        Assert.Contains("No se pudo hablar con Control Print en 127.0.0.1:", texto);
        Assert.Contains("Lo recopilado se quedó en", texto);
        Assert.Contains("**Incompleta:** No se pudo hablar con Control Print", File.ReadAllText(Path.Combine(Carpeta, "README.md")));

        _servidor.Antes = null;
        Assert.Equal(0, await RecopilarAsync(new StringWriter()));
        Assert.Equal(131L, Valor("SELECT COUNT(*) FROM trabajos"));
        SoloLeyo();
    }

    [Fact]
    public async Task Un_error_de_Control_Print_fuera_de_los_trabajos_no_detiene_la_recopilacion()
    {
        // Si Control Print fallara siempre en algo (un registro dañado), volver a correrlo fallaría igual: se salta y se avisa.
        var sinMiniatura = _servidor.Trabajos.First(t => t.Thumbnail is not null);
        var sinResumen = DatosDeControlPrint.DiaLocal(_servidor.Trabajos[40].DetectedUtc);
        _servidor.Antes = async (ctx, p) =>
        {
            if (p.Ruta != "/api/calibration" && p.Ruta != $"/api/jobs/{sinMiniatura.Id}/thumbnail" && p.Consulta != $"?day={sinResumen:yyyy-MM-dd}")
                return false;
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new { kind = "server", message = "Error en el servidor: database is locked" });
            return true;
        };
        var salida = new StringWriter();

        Assert.Equal(0, await RecopilarAsync(salida));

        Assert.Equal(3, _servidor.Pedidas("/api/calibration"));
        Assert.Equal(0L, Valor("SELECT COUNT(*) FROM configuracion WHERE clave = 'calibracion'"));
        Assert.Null(Valor($"SELECT miniatura FROM trabajos WHERE id = {sinMiniatura.Id}"));
        Assert.Equal(0L, Valor($"SELECT COUNT(*) FROM resumen_por_dia WHERE dia = '{sinResumen:yyyy-MM-dd}'"));
        Assert.Equal(131L, Valor("SELECT COUNT(*) FROM trabajos"));
        var leame = File.ReadAllText(Path.Combine(Carpeta, "README.md"));
        Assert.Contains("**Completa:**", leame);
        Assert.Contains("No se pudo leer la calibración: Control Print contestó 500 a «api/calibration»: Error en el servidor: database is locked", leame);
        Assert.Contains("Control Print no dio la miniatura de 1 trabajo (", leame);
        Assert.Contains("Control Print no dio el resumen o las caras de 1 día (", leame);
        Assert.Contains("Aviso: no se pudo leer la calibración", salida.ToString());
        SoloLeyo();
    }

    [Fact]
    public async Task Si_Control_Print_no_da_las_miniaturas_se_dejan_de_pedir()
    {
        _servidor.Antes = async (ctx, p) =>
        {
            if (!p.Ruta.EndsWith("/thumbnail", StringComparison.Ordinal))
                return false;
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new { kind = "server", message = "Error en el servidor." });
            return true;
        };

        Assert.Equal(0, await RecopilarAsync(new StringWriter()));

        Assert.Equal(20, _servidor.Peticiones.Count(p => p.Ruta.EndsWith("/thumbnail", StringComparison.Ordinal)));
        Assert.Equal(131L, Valor("SELECT COUNT(*) FROM trabajos WHERE miniatura IS NULL"));
        Assert.Contains("Control Print no dio la miniatura de 20 trabajos seguidos y se dejó de pedir", File.ReadAllText(Path.Combine(Carpeta, "README.md")));
    }

    [Fact]
    public async Task Un_codigo_que_no_coincide_se_vuelve_a_pedir()
    {
        var tecleados = new Queue<string?>(["AAAA-BBBB-CCCC-DDDD", "Q4WE-8RTY", Codigo]);
        var salida = new StringWriter();

        Assert.Equal(0, await RecopilarAsync(salida, Opciones(miniaturas: false), codigo: new CodigoDeConexion(tecleados.Dequeue, true)));

        var texto = salida.ToString();
        Assert.Contains("Ajustes → Red", texto);
        Assert.Contains("El código no coincide con el de Control Print en PRINCIPAL", texto);
        Assert.Contains("16 letras y números", texto);
        Assert.Contains("Escríbelo otra vez.", texto);
        Assert.DoesNotContain("AAAA", texto);
        Assert.DoesNotContain("Q4WE", texto);

        // Sin teclado (la variable), una sola vez.
        Assert.Equal(1, await RecopilarAsync(new StringWriter(), codigo: new CodigoDeConexion(() => "AAAA-BBBB-CCCC-DDDD", false)));
        var sinCodigo = new StringWriter();
        Assert.Equal(2, await RecopilarAsync(sinCodigo, codigo: new CodigoDeConexion(() => null, false)));
        Assert.Contains(OpcionesRecopilacion.VariableCodigo, sinCodigo.ToString());
        SoloLeyo();
    }

    [Fact]
    public async Task Sin_trabajos_tambien_deja_su_carpeta()
    {
        _servidor.Trabajos.Clear();
        var salida = new StringWriter();

        Assert.Equal(0, await RecopilarAsync(salida));

        Assert.Contains("Control Print no tiene trabajos", salida.ToString());
        Assert.Contains("no hay ninguno", File.ReadAllText(Path.Combine(Carpeta, "README.md")));
        Assert.Equal(3L, Valor("SELECT COUNT(*) FROM cotizaciones"));
    }

    [Fact]
    public async Task Sin_Control_Print_no_deja_nada()
    {
        var salida = new StringWriter();
        await _servidor.DisposeAsync();

        Assert.Equal(1, await RecopilarAsync(salida));

        Assert.Contains("No se pudo hablar con Control Print", salida.ToString());
        Assert.False(Directory.Exists(_salida));
    }

    [Fact]
    public void Las_opciones()
    {
        Assert.Null(OpcionesRecopilacion.Leer([], out var error));
        Assert.Contains("--servidor", error);

        var minimas = OpcionesRecopilacion.Leer(["--servidor", "192.168.1.106"], out error);
        Assert.Null(error);
        Assert.Equal(new OpcionesRecopilacion("192.168.1.106", 47815, OpcionesRecopilacion.SalidaPorOmision, null, false, null), minimas);
        Assert.EndsWith("control-print-datos", OpcionesRecopilacion.SalidaPorOmision);

        var todas = OpcionesRecopilacion.Leer(
            ["--servidor", "PRINCIPAL", "--puerto", "4000", "--salida", "datos", "--desde", "2026-01-31", "--miniaturas", "--huella", "7b:d8:39:26:f8:86:b2:2e:b0:35:99:e8:c4:ba:08:85:51:85:e8:39:a9:64:ac:4b:00:c8:e6:34:56:d9:a5:62"],
            out error);
        Assert.Equal(new OpcionesRecopilacion("PRINCIPAL", 4000, "datos", new DateOnly(2026, 1, 31), true,
            "7BD83926F886B22EB03599E8C4BA08855185E839A964AC4B00C8E63456D9A562"), todas);

        Assert.Null(OpcionesRecopilacion.Leer(["--servidor", "x", "--desde", "31/01/2026"], out error));
        Assert.Contains("AAAA-MM-DD", error);
        Assert.Null(OpcionesRecopilacion.Leer(["--servidor", "x", "--puerto", "0"], out error));
        Assert.Contains("1 a 65535", error);
        Assert.Null(OpcionesRecopilacion.Leer(["--servidor", "x", "--huella", "7B:D8"], out error));
        Assert.Contains("64 cifras", error);
        Assert.Null(OpcionesRecopilacion.Leer(["--servidor", "x", "--miniatura"], out error));
        Assert.Contains("«--miniatura»", error);
        Assert.Null(OpcionesRecopilacion.Leer(["--servidor", "x", "--puerto"], out error));
        Assert.Contains("--puerto", error);
        Assert.Null(OpcionesRecopilacion.Leer(["--servidor", "dos palabras"], out error));

        // El código nunca va en la línea de comandos, y si alguien lo pone, no se repite.
        Assert.Null(OpcionesRecopilacion.Leer(["--servidor", "x", "--codigo", "WXYZ-2345-6789-ABCD"], out error));
        Assert.Contains(OpcionesRecopilacion.VariableCodigo, error);
        Assert.DoesNotContain("WXYZ", error);
        Assert.Null(OpcionesRecopilacion.Leer(["--servidor", "x", "WXYZ-2345-6789-ABCD"], out error));
        Assert.DoesNotContain("WXYZ", error);
        Assert.Null(OpcionesRecopilacion.Leer(["--servidor", "WXYZ-2345-6789-ABCD"], out error));
        Assert.Contains("parece el código", error);
        Assert.DoesNotContain("WXYZ", error);
    }
}
