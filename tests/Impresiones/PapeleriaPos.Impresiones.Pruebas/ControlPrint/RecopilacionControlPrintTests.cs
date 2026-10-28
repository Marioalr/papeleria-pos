using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Nucleo;

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

    /// <summary>Un trabajo impreso y cobrado (el primero que arma DatosDeControlPrint), con ese número y detectado a esa hora UTC.</summary>
    private static JobRecordCp Impreso(long id, DateTime detectadoUtc)
    {
        var trabajo = DatosDeControlPrint.Trabajos(1, detectadoUtc, detectadoUtc.AddMinutes(1), semilla: (int)id)[0];
        trabajo.Id = id;
        return trabajo;
    }

    /// <summary>Lo que dejó la recopilación en todos sus archivos: la base, el README, las miniaturas, lo temporal.</summary>
    private IEnumerable<(string Archivo, byte[] Bytes)> Archivos() =>
        Directory.GetFiles(_salida, "*", SearchOption.AllDirectories).Select(a => (Path.GetFileName(a), File.ReadAllBytes(a)));

    /// <summary>Si ya se está leyendo octubre (en hora de México): lo que se cambie desde ahí cae en meses ya leídos.</summary>
    private bool YaLeeOctubre() =>
        _servidor.Busquedas.Any(q => q.FromUtc?.ToUniversalTime() == HoraMexico.AUtc(new DateTime(2026, 10, 1)));

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

    /// <summary>
    /// Todas las tablas llegan hasta la misma hora, la de corte (revisión del PR #143). El resumen y las caras del último día se
    /// pedían del día entero, después de los trabajos: contaban lo que se imprimió mientras corría, que no está en «trabajos» (y al
    /// seguirla otro rato el mismo día, horas). Las caras de Control Print aceptan la hora (api/jobs/sides?from=&amp;to=); su
    /// resumen es de días enteros (api/summary?day=), así que el de ese día se calcula de los trabajos, como lo calcula él, y queda
    /// marcado y dicho en el README.
    /// </summary>
    [Fact]
    public async Task El_resumen_y_las_caras_del_ultimo_dia_llegan_hasta_la_hora_de_corte()
    {
        // Hoy, 25 de octubre en México: tres antes del corte (9:00, 10:00 y 11:00; el de las 10 se canceló, el de las 11 salió
        // incompleto y no se vendió) y dos que se imprimieron mientras corría (14:00 y 16:00).
        var cancelado = Impreso(136, new DateTime(2026, 10, 25, 16, 0, 0, DateTimeKind.Utc));
        cancelado.State = JobState.Cancelled;
        var noVendido = Impreso(133, new DateTime(2026, 10, 25, 17, 0, 0, DateTimeKind.Utc));
        (noVendido.Payment, noVendido.UnsoldReason, noVendido.CountedSides) = (PaymentState.Unsold, UnsoldReason.PaperJam, noVendido.Sides - 1);
        var deHoy = new[]
        {
            Impreso(132, new DateTime(2026, 10, 25, 15, 0, 0, DateTimeKind.Utc)), cancelado, noVendido,
            Impreso(134, new DateTime(2026, 10, 25, 20, 0, 0, DateTimeKind.Utc)),
            Impreso(135, new DateTime(2026, 10, 25, 22, 0, 0, DateTimeKind.Utc)),
        };
        _servidor.Trabajos.AddRange(deHoy);

        Assert.Equal(0, await RecopilarAsync(new StringWriter(), Opciones(miniaturas: false)));

        const string hoy = "2026-10-25";
        var trabajos = (long)Valor($"SELECT COUNT(*) FROM trabajos WHERE dia = '{hoy}' AND cobrable = 1")!;
        var caras = (long)Valor($"SELECT SUM(COALESCE(caras_contadas, caras)) FROM trabajos WHERE dia = '{hoy}' AND cobrable = 1 AND tipo = 'Impresion'")!;
        Assert.Equal(2L, trabajos);
        Assert.Equal(
            (trabajos, caras),
            ((long)Valor($"SELECT trabajos FROM resumen_por_dia WHERE dia = '{hoy}'")!, (long)Valor($"SELECT caras_impresas FROM caras_por_dia WHERE dia = '{hoy}'")!));

        // Lo demás del resumen de hoy, como lo cuenta Control Print (los analizados o impresos; lo vendido sin las no vendidas),
        // con lo de antes del corte; no se le pidió el suyo, que sería del día entero.
        var cobrables = deHoy.Where(t => t.DetectedUtc < Ahora.UtcDateTime && t.IsChargeable).ToList();
        var resumen = Filas(Carpeta, $"""
            SELECT caras, costo, vendido, cobrado, por_cobrar, no_vendidas, costo_no_vendidas, calculado FROM resumen_por_dia WHERE dia = '{hoy}'
            """).Single();
        Assert.Equal((long)cobrables.Sum(t => t.CountedSides ?? t.Sides), resumen[0]);
        Assert.Equal((double)cobrables.Sum(t => t.CostTotal), (double)resumen[1]!, 6);
        Assert.Equal((double)cobrables.Where(t => t.Payment != PaymentState.Unsold).Sum(t => t.EffectivePrice), (double)resumen[2]!, 6);
        Assert.Equal((double)cobrables.Where(t => t.Payment == PaymentState.Paid).Sum(t => t.EffectivePrice), (double)resumen[3]!, 6);
        Assert.Equal((double)resumen[2]! - (double)resumen[3]!, (double)resumen[4]!, 6);
        Assert.Equal(1L, resumen[5]);
        Assert.Equal((double)noVendido.CostTotal, (double)resumen[6]!, 6);
        Assert.Equal(1L, resumen[7]);
        Assert.DoesNotContain(_servidor.Peticiones, p => p.Ruta == "/api/summary" && p.Consulta.Contains($"day={hoy}", StringComparison.Ordinal));
        // Los días enteros, el de Control Print.
        Assert.Equal(0L, Valor($"SELECT COUNT(*) FROM resumen_por_dia WHERE dia <> '{hoy}' AND calculado = 1"));
        Assert.Contains(_servidor.Peticiones, p => p.Ruta == "/api/summary" && p.Consulta.Contains("day=2026-10-24", StringComparison.Ordinal));
        var leame = File.ReadAllText(Path.Combine(Carpeta, "README.md"));
        Assert.Contains($"el del {hoy}, calculado de `trabajos`", leame);
        Assert.Contains("(12:00:00 del 2026-10-25): lo de después no está.", leame);
        Assert.Contains("**Completa:** empezó a las 12:00:00 del 2026-10-25 y terminó a las 12:00:00 del 2026-10-25 (hora de México); cada mes " +
                        "cuadra con lo que cuenta Control Print.", leame);
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

    /// <summary>
    /// Lo que parece un secreto no se guarda aunque un Control Print más nuevo lo mande con otro nombre (revisión del PR #143; el
    /// 1.12 no manda ninguno en api/settings/*): por el nombre, con una regla más amplia (el código de conexión, «…Pass», «…Key»,
    /// un certificado «Pfx»), y por el valor: un texto que lleve el código de conexión, escrito como sea y se llame como se llame,
    /// tampoco; ni en la configuración ni en un trabajo. La «Key» de cada papel de los costos se queda, y otro código también.
    /// </summary>
    [Fact]
    public async Task Los_secretos_no_se_guardan_aunque_la_configuracion_los_traiga_con_otro_nombre()
    {
        _servidor.Antes = async (ctx, p) =>
        {
            if (p.Ruta != "/api/settings/app")
                return false;
            var ajustes = JsonNode.Parse(DatosDeControlPrint.Ajustes())!.AsObject();
            ajustes["ConnectionCode"] = Codigo;
            ajustes["SmtpPass"] = "clave-del-correo-de-prueba";
            ajustes["TelegramBotKey"] = "123456:clave-del-bot-de-prueba";
            ajustes["Pfx"] = "MIIKcertificado-con-su-llave-de-prueba";
            // Y el código con nombres que no lo dicen, escrito de otras formas.
            ajustes["Red"] = new JsonObject { ["Nota"] = "q4we 8rty 2upa 5sdf", ["Anteriores"] = new JsonArray("AAAA-BBBB-CCCC-DDDD", "Q4WE8RTY2UPA5SDF") };
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(ajustes.ToJsonString());
            return true;
        };
        // También en un trabajo: en sus notas y en un campo que esta versión no conoce.
        var conNota = _servidor.Trabajos[7];
        conNota.Notes = $"Para conectar la caja 2: {Codigo.ToLowerInvariant()}";
        var conCampo = _servidor.Trabajos[8];
        conCampo.Extra = new()
        {
            ["pairing"] = System.Text.Json.JsonSerializer.SerializeToElement(Codigo),
            ["printerSerial"] = System.Text.Json.JsonSerializer.SerializeToElement("X3ZT001234"),
        };

        Assert.Equal(0, await RecopilarAsync(new StringWriter(), Opciones(miniaturas: false)));

        var guardados = (string)Valor("SELECT json FROM configuracion WHERE clave = 'ajustes'")!;
        Assert.All(["Q4WE-8RTY", "clave-del-correo", "clave-del-bot", "MIIKcertificado"], secreto => Assert.DoesNotContain(secreto, guardados));
        Assert.All(Archivos(), a => Assert.True(a.Bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Codigo)) < 0, $"{a.Archivo} tiene el código."));
        var formas = new[] { "Q4WE8RTY2UPA5SDF", "q4we 8rty 2upa 5sdf", Codigo.ToLowerInvariant() };
        Assert.All(Archivos(), a => Assert.All(formas, f => Assert.True(a.Bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(f)) < 0, $"{a.Archivo} tiene «{f}».")));
        // Lo demás se queda: lo que no es secreto, otro código (no el de este Control Print) y la «Key» de cada papel.
        Assert.Contains("\"PrinterHost\": \"192.0.2.50\"", guardados);
        Assert.Contains("AAAA-BBBB-CCCC-DDDD", guardados);
        Assert.Contains("\"Key\": \"Plain\"", (string)Valor("SELECT json FROM configuracion WHERE clave = 'costos'")!);
        Assert.Equal("", Valor($"SELECT notas FROM trabajos WHERE id = {conNota.Id}"));
        Assert.Contains("X3ZT001234", (string)Valor($"SELECT otros_json FROM trabajos WHERE id = {conCampo.Id}")!);
        // El README dice qué campos se quitaron, sin sus valores.
        Assert.Contains("ajustes.UpdateTokenProtected, ajustes.ConnectionCode, ajustes.SmtpPass, ajustes.TelegramBotKey, ajustes.Pfx, ajustes.Red.Nota, " +
                        "ajustes.Red.Anteriores[]", File.ReadAllText(Path.Combine(Carpeta, "README.md")));
        SoloLeyo();
    }

    /// <summary>
    /// Ni los hashes ni las sesiones de los usuarios (que el falso manda de más) ni el token de los ajustes llegan a ningún archivo
    /// de la recopilación: la base, el README, las miniaturas o lo temporal (la revisión del PR #143 lo comprobó).
    /// </summary>
    [Fact]
    public async Task Ningun_archivo_lleva_hashes_sesiones_ni_el_token_de_los_ajustes()
    {
        Assert.Equal(0, await RecopilarAsync(new StringWriter()));

        var secretos = new[] { "HASH-SECRETO", "TOKEN-SECRETO", "SAL-DE-PRUEBA", "pbkdf2", "SECRETO-DE-PRUEBA-NO-DEBE-SALIR" };
        Assert.All(Archivos(), a => Assert.All(secretos, s => Assert.True(a.Bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(s)) < 0, $"{a.Archivo} tiene «{s}».")));
        Assert.Equal(4L, Valor("SELECT COUNT(*) FROM usuarios"));
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
        // Lo que ya estaba no se volvió a pedir: ni la búsqueda del trabajo más viejo (de uno en uno, hasta la hora de corte) ni la
        // ventana «antes» (sin fecha de inicio), que la primera vez se terminaron antes de detenerse: de esa solo se contó cuántos
        // tiene Control Print (de uno en uno), como de cada mes al final. Y llega hasta la misma hora que la primera vez.
        Assert.True(terminadas >= 1);
        var deLaSegunda = _servidor.Busquedas.Skip(yaPedidas).ToList();
        Assert.NotEmpty(deLaSegunda);
        Assert.DoesNotContain(deLaSegunda, q => q.FromUtc is null && (q.Limit != 1 || q.ToUtc!.Value.ToUniversalTime() == Ahora.UtcDateTime));
        Assert.Equal(Ahora.UtcDateTime, deLaSegunda.Max(q => q.ToUtc!.Value.ToUniversalTime()));
        SoloLeyo();
    }

    /// <summary>
    /// Un trabajo que llega mientras se lee el mes en curso (número nuevo: el primero de la lista) recorre las páginas y la
    /// primera vuelta no lo ve; la cuenta del mes no cuadra y la segunda vuelta lo trae (la revisión del PR #143 lo comprobó).
    /// </summary>
    [Fact]
    public async Task Un_trabajo_que_llega_al_mes_que_se_esta_leyendo_no_se_pierde()
    {
        var nuevo = Impreso(132, new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc));
        _servidor.Antes = (_, p) =>
        {
            if (p.Ruta == "/api/jobs/search" && !_servidor.Trabajos.Contains(nuevo) && YaLeeOctubre())
                _servidor.Trabajos.Add(nuevo);
            return Task.FromResult(false);
        };

        Assert.Equal(0, await RecopilarAsync(new StringWriter(), Opciones(miniaturas: false)));

        Assert.Equal(1L, Valor("SELECT COUNT(*) FROM trabajos WHERE id = 132"));
        Assert.Equal(132L, Valor("SELECT COUNT(*) FROM trabajos"));
        Assert.Contains("**Completa:**", File.ReadAllText(Path.Combine(Carpeta, "README.md")));
        SoloLeyo();
    }

    /// <summary>
    /// Un trabajo que una PC manda tarde (estuvo sin red: lo sube al volver, con la fecha en que se imprimió) a un mes que ya se
    /// leyó, mientras la recopilación sigue con los de después (revisión del PR #143): se perdía, ningún aviso lo decía y el
    /// README decía «Completa». Al final se compara cada mes con lo que cuenta Control Print y el que cambió se vuelve a leer; el
    /// resumen de sus días se pide después, ya con él.
    /// </summary>
    [Fact]
    public async Task Un_trabajo_que_llega_tarde_a_un_mes_ya_leido_se_recopila_o_se_avisa()
    {
        var tarde = Impreso(132, new DateTime(2026, 9, 20, 17, 0, 0, DateTimeKind.Utc));
        _servidor.Antes = (_, p) =>
        {
            // Cuando ya se está leyendo octubre (septiembre ya terminó), llega el del 20 de septiembre.
            if (p.Ruta == "/api/jobs/search" && !_servidor.Trabajos.Contains(tarde) && YaLeeOctubre())
                _servidor.Trabajos.Add(tarde);
            return Task.FromResult(false);
        };
        var salida = new StringWriter();

        Assert.Equal(0, await RecopilarAsync(salida, Opciones(miniaturas: false)));

        Assert.Contains(tarde, _servidor.Trabajos);
        var leame = File.ReadAllText(Path.Combine(Carpeta, "README.md"));
        Assert.True(Valor("SELECT COUNT(*) FROM trabajos WHERE id = 132") is 1L || leame.Contains("no cuadra", StringComparison.Ordinal),
            $"El trabajo 132 (del 20 de septiembre, llegó tarde) no está en trabajos y el README no lo avisa:\n{leame}");
        // Se recopiló (se volvió a leer septiembre), y con él el resumen de su día: cuadra.
        Assert.Equal(1L, Valor("SELECT COUNT(*) FROM trabajos WHERE id = 132"));
        Assert.Equal(132L, Valor("SELECT COUNT(*) FROM trabajos"));
        Assert.Equal(Valor("SELECT COUNT(*) FROM trabajos WHERE dia = '2026-09-20' AND cobrable = 1"), Valor("SELECT trabajos FROM resumen_por_dia WHERE dia = '2026-09-20'"));
        Assert.Contains("**Completa:**", leame);
        Assert.Contains("Cambió en Control Print mientras se leía: 2026-09.", salida.ToString());
        SoloLeyo();
    }

    /// <summary>
    /// Lo mismo cuando llega entre una vez y otra: la primera recopilación ya tenía el resumen de sus días y se detuvo después.
    /// Al seguirla, septiembre se vuelve a leer y el resumen y las caras de sus días se vuelven a pedir, ya con él.
    /// </summary>
    [Fact]
    public async Task Al_seguirla_un_mes_que_cambio_se_vuelve_a_leer_con_el_resumen_de_sus_dias()
    {
        var dia = DatosDeControlPrint.DiaLocal(_servidor.Trabajos.First(t => t.IsChargeable && t.Kind == JobKind.Print
                                                                             && DatosDeControlPrint.DiaLocal(t.DetectedUtc).Month == 9).DetectedUtc);
        using var detener = new CancellationTokenSource();
        _servidor.Antes = (_, p) =>
        {
            // Después de los días, en las cotizaciones: Ctrl+C.
            if (p.Ruta == "/api/quotes")
                detener.Cancel();
            return Task.FromResult(false);
        };
        Assert.Equal(1, await RecopilarAsync(new StringWriter(), Opciones(miniaturas: false), detener.Token));
        var delDia = $"FROM trabajos WHERE dia = '{dia:yyyy-MM-dd}' AND cobrable = 1";
        var (resumen, caras) = ((long)Valor($"SELECT trabajos FROM resumen_por_dia WHERE dia = '{dia:yyyy-MM-dd}'")!,
            (long)Valor($"SELECT caras_impresas FROM caras_por_dia WHERE dia = '{dia:yyyy-MM-dd}'")!);
        Assert.Equal(Valor($"SELECT COUNT(*) {delDia}"), resumen);

        _servidor.Antes = null;
        var tarde = Impreso(132, HoraMexico.AUtc(dia.ToDateTime(new TimeOnly(12, 0))));
        _servidor.Trabajos.Add(tarde);
        var segunda = new StringWriter();
        Assert.Equal(0, await RecopilarAsync(segunda, Opciones(miniaturas: false)));

        Assert.Contains("sigue la recopilación que se detuvo", segunda.ToString());
        Assert.Equal(132L, Valor("SELECT COUNT(*) FROM trabajos"));
        Assert.Equal((resumen + 1, caras + tarde.Sides),
            ((long)Valor($"SELECT trabajos FROM resumen_por_dia WHERE dia = '{dia:yyyy-MM-dd}'")!, (long)Valor($"SELECT caras_impresas FROM caras_por_dia WHERE dia = '{dia:yyyy-MM-dd}'")!));
        Assert.Equal(Valor($"SELECT COUNT(*) {delDia}"), Valor($"SELECT trabajos FROM resumen_por_dia WHERE dia = '{dia:yyyy-MM-dd}'"));
        Assert.Contains("**Completa:**", File.ReadAllText(Path.Combine(Carpeta, "README.md")));
        SoloLeyo();
    }

    /// <summary>
    /// Si aun después de volver a leerlo no cuadra (siguen llegando trabajos tarde, o se borró uno), el README no dice «Completa»:
    /// dice cuántos, en qué mes y qué hacer. Volver a leer es una vez y nada más: termina aunque sigan llegando.
    /// </summary>
    [Fact]
    public async Task Si_aun_asi_no_cuadra_el_README_no_dice_completa_y_dice_que_hacer()
    {
        var siguiente = 300L;
        var borrado = _servidor.Trabajos.First(t => DatosDeControlPrint.DiaLocal(t.DetectedUtc).Month == 8);
        _servidor.Antes = (_, p) =>
        {
            // Desde que se lee octubre, en cada búsqueda llega otro de septiembre; y uno de agosto se borró.
            if (p.Ruta == "/api/jobs/search" && YaLeeOctubre())
            {
                _servidor.Trabajos.Add(Impreso(siguiente++, new DateTime(2026, 9, 20, 17, 0, 0, DateTimeKind.Utc)));
                _servidor.Trabajos.Remove(borrado);
            }
            return Task.FromResult(false);
        };
        var salida = new StringWriter();

        Assert.Equal(0, await RecopilarAsync(salida, Opciones(miniaturas: false)));

        var septiembre = (Desde: HoraMexico.AUtc(new DateTime(2026, 9, 1)), Hasta: HoraMexico.AUtc(new DateTime(2026, 10, 1)));
        var faltan = _servidor.Trabajos.Count(t => t.DetectedUtc >= septiembre.Desde && t.DetectedUtc < septiembre.Hasta)
                     - (long)Valor("SELECT COUNT(*) FROM trabajos WHERE dia LIKE '2026-09-%'")!;
        Assert.True(faltan > 0);
        var leame = File.ReadAllText(Path.Combine(Carpeta, "README.md"));
        Assert.DoesNotContain("Completa", leame);
        Assert.Contains("- **Terminó, pero no cuadra con Control Print:** Control Print tiene " +
                        (faltan == 1 ? "1 trabajo que no se guardó (2026-09)" : $"{faltan} trabajos que no se guardaron (2026-09)"), leame);
        Assert.Contains("Se guardó 1 trabajo que Control Print ya no tiene (2026-08): se borró mientras se leía.", leame);
        Assert.Contains("vuelve a correr `recopilar-control-print`: hace otra recopilación desde cero, en otra carpeta.", leame);
        Assert.Equal("terminada", Valor("SELECT valor FROM recopilacion WHERE clave = 'estado'"));
        Assert.Contains("Ojo: no cuadra con lo que cuenta Control Print", salida.ToString());
        // Septiembre se leyó dos veces, nada más: la de su mes y la del final.
        Assert.Equal(2, _servidor.Busquedas.Count(q => q.FromUtc?.ToUniversalTime() == septiembre.Desde && q.Limit == 20 && q.Offset == 0));
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

    /// <summary>Escribe lo que le llega y se detiene en la primera línea: así la recopilación no llega a conectarse (ni a la red).</summary>
    private sealed class PrimeraLinea : StringWriter
    {
        public sealed class Detenida : Exception;

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            throw new Detenida();
        }
    }

    /// <summary>
    /// El código de conexión puesto por error en --servidor, escrito como sea (revisión del PR #143): sin guiones, en minúsculas,
    /// con puntos, guiones bajos o rayas en lugar de guiones (al copiarlo de un mensaje o un documento). Solo se reconocía con sus
    /// guiones, y el propio aviso del código dice que «los guiones y las mayúsculas dan igual»: se tomaba como el nombre de una PC,
    /// la primera línea lo escribía («Control Print en Q4WE8RTY2UPA5SDF:47815») y se buscaba en la red (DNS; en Windows también
    /// LLMNR y NetBIOS, que le preguntan a toda la red). Un nombre de PC de Windows tiene hasta 15 letras: 16 letras y números del
    /// alfabeto de Control Print son el código. Ni se acepta ni se repite, y la recopilación tampoco lo escribe si le llega así.
    /// </summary>
    [Theory]
    [InlineData("Q4WE8RTY2UPA5SDF")]
    [InlineData("q4we8rty2upa5sdf")]
    [InlineData("Q4WE.8RTY.2UPA.5SDF")]
    [InlineData("Q4WE_8RTY_2UPA_5SDF")]
    [InlineData("Q4WE–8RTY–2UPA–5SDF")]
    public async Task El_codigo_sin_guiones_en_servidor_no_se_toma_como_nombre_de_la_pc(string escrito)
    {
        var opciones = OpcionesRecopilacion.Leer(["--servidor", escrito], out var error);
        var pantalla = new PrimeraLinea();
        if (opciones is not null)
            await Assert.ThrowsAsync<PrimeraLinea.Detenida>(() =>
                RecopilacionControlPrint.EjecutarAsync(opciones, new CodigoDeConexion(() => null, false), pantalla));

        Assert.DoesNotContain(ProtocoloControlPrint.NormalizarCodigo(Codigo), ProtocoloControlPrint.NormalizarCodigo(pantalla + (error ?? "")));
        Assert.Null(opciones);

        // Aunque llegara así a la recopilación (sin pasar por Leer): se detiene antes de escribirlo o de buscarlo.
        var directa = new StringWriter();
        Assert.Equal(2, await RecopilacionControlPrint.EjecutarAsync(new OpcionesRecopilacion(escrito, _servidor.Puerto, _salida, null, false, null),
            new CodigoDeConexion(() => Codigo, false), directa));
        Assert.DoesNotContain(ProtocoloControlPrint.NormalizarCodigo(Codigo), ProtocoloControlPrint.NormalizarCodigo(directa.ToString()));
        Assert.Contains("parece el código", directa.ToString());
        Assert.False(Directory.Exists(_salida));
        Assert.Empty(_servidor.Peticiones);
    }

    /// <summary>Con espacios (entre comillas) no es un nombre de PC, y el error no repite lo escrito: el código (revisión del PR #143).</summary>
    [Fact]
    public void El_codigo_con_espacios_en_servidor_no_se_repite_en_el_error()
    {
        Assert.Null(OpcionesRecopilacion.Leer(["--servidor", "Q4WE 8RTY 2UPA 5SDF"], out var error));

        Assert.DoesNotContain("Q4WE", error);
        Assert.Contains("parece el código", error);
    }

    /// <summary>
    /// Lo que sí es una IP o el nombre de una PC se sigue aceptando (una IPv6 que solo tiene letras del alfabeto del código, un
    /// nombre de 15); lo que no, se rechaza sin repetirlo: un nombre de más de 15 sin puntos (no es una PC con Windows) o el código
    /// mal copiado (una O por un 0, en cuatro grupos de cuatro).
    /// </summary>
    [Fact]
    public void En_servidor_va_una_IP_o_el_nombre_de_una_PC_y_lo_demas_no_se_repite()
    {
        foreach (var servidor in new[] { "PRINCIPAL", "192.168.1.106", "principal.local", "localhost", "CAJA-2", "ABCDEFGHJKLMNPQ", "fe8a::2a3b:4c5d:6e7f" })
        {
            Assert.Equal(servidor, OpcionesRecopilacion.Leer(["--servidor", servidor], out var error)?.Servidor);
            Assert.Null(error);
        }
        foreach (var (servidor, dice) in new[]
                 {
                     ("ABCDEFGHJKLMNPQRS", "no es una IP ni el nombre de una PC"), ("Q4WE-8RTY-2UPA-5SD0", "parece el código"),
                     ("q4we 8rty", "no es una IP ni el nombre de una PC"), ("dos palabras", "no es una IP ni el nombre de una PC"),
                 })
        {
            Assert.Null(OpcionesRecopilacion.Leer(["--servidor", servidor], out var error));
            Assert.Contains(dice, error);
            Assert.DoesNotContain(servidor, error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Q4WE", error, StringComparison.OrdinalIgnoreCase);
        }
    }
}
