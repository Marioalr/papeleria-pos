using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>Revisión independiente de cb5f83f: lo que la Configuración y las decisiones dicen, contra lo que hace el código.</summary>
public sealed class RevisionVariasImpresorasModuloTests : IAsyncLifetime
{
    private BaseDelModulo _base = null!;

    private DateTime Ahora => _base.Pos.Hora.Ahora.UtcDateTime;

    public async Task InitializeAsync() => _base = await BaseDelModulo.CrearAsync();

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private static TrabajoMedidoDto Trabajo(CajaDelPos caja, string documento, DateTime impresoUtc, int? impresora = null, int paginas = 10) =>
        new(Guid.NewGuid(), caja.Id, null, "CAJA2", impresora is { } n ? $"Cola {n}" : "WF-6590", 7, documento, "cajero", impresoUtc, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            Enumerable.Range(0, paginas).Select(_ => new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)).ToList(), true, null, null,
            impresoUtc.AddSeconds(-20), Impresora: impresora);

    private static SolicitudConsumiblesImpresora Botellas(decimal precio) =>
        new(Guid.NewGuid(), [new("Negro", precio, 4300), new("Cian", precio, 7300), new("Magenta", precio, 7300), new("Amarillo", precio, 7300)]);

    /// <summary>
    /// ConfiguracionImpresora.razor:70-71, 494 y 1067 le dicen a quien da de baja una impresora: «lo que llegue de su cola se anota en la primera».
    /// ServicioImpresora.ParaElTrabajoAsync busca el número sin mirar si está activa: se anota en la dada de baja, con sus costos, sin nota y
    /// sin contador que la verifique.
    /// </summary>
    [Fact]
    public async Task Lo_que_llega_de_la_cola_de_una_dada_de_baja_se_anota_en_la_primera_como_dice_la_Configuracion()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        var wf = (await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        var l5590 = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, "192.0.2.23");
        await impresoras.GuardarConsumiblesAsync(l5590, Botellas(1m));
        await impresoras.CambiarAsync(l5590, null, null, activa: false);

        var registro = _base.Registro(db);
        var trabajo = await registro.RegistrarAsync(Trabajo(caja, "Sigue en su cola.pdf", Ahora, impresora: l5590.Numero));
        var enLaPrimera = await registro.RegistrarAsync(Trabajo(caja, "En la primera.pdf", Ahora));

        Assert.Equal(wf.Id, trabajo.ImpresoraId);
        Assert.Equal(enLaPrimera.PrecioCentavos, trabajo.PrecioCentavos);
    }

    /// <summary>
    /// La decisión (docs/plan.md): «¿F2 dice siempre de qué impresora salió? Solo si la sucursal tiene más de una; con una sola se ve como antes».
    /// VariasAsync y la Configuración cuentan las activas; NombresAsync cuenta también las de baja: después del paso 5.6 de la guía del fin de
    /// semana (agregar la L5590 en la caja 2 y darla de baja), cada tarjeta de la caja 2 dice «WF-6590» para siempre.
    /// </summary>
    [Fact]
    public async Task Con_una_activa_y_otra_de_baja_la_tarjeta_de_la_activa_se_ve_como_antes()
    {
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        var wf = (await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        var l5590 = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, null);
        await impresoras.CambiarAsync(l5590, null, null, activa: false);

        var nombres = await impresoras.NombresAsync(PosDePrueba.Sucursal);

        Assert.Null(nombres(wf.Id));
    }
}

/// <summary>Revisión independiente de cb5f83f: analizar-control-print con lo que recopilar-control-print deja de verdad a medias.</summary>
public sealed class RevisionAnalisisControlPrintTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"cp-rev-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    private static TrabajoControlPrint Impresion(long id, string dia)
    {
        var ajustes = new JobSettings { Color = false };
        var paginas = new List<PageCoverage> { new(0, 0, 0, 0.05, 0.05, 93.5, false) };
        var costos = new CostSettings();
        var costo = CostCalculator.Calculate(new JobAnalysis(ajustes, paginas, AnalysisLevel.Measured, null, null, TimeSpan.Zero), costos);
        var detectado = DateTime.SpecifyKind(DateTime.Parse($"{dia}T17:00:00"), DateTimeKind.Utc);
        return new TrabajoControlPrint
        {
            Id = id, PcOrigen = "PRINCIPAL", Tipo = TipoTrabajoControlPrint.Impresion, Impresora = "WF-6590 Series(Red)", Documento = "Tarea.pdf",
            DetectadoUtc = detectado, EnviadoUtc = detectado, Estado = EstadoTrabajoControlPrint.Impreso, Paginas = 1, Copias = 1, Caras = 1, Hojas = 1,
            CostoTotal = costo.Total, Precio = costo.Price, PrecioACobrar = costo.Price, Cobrable = true, Cobro = CobroControlPrint.Cobrado,
            NivelAnalisis = NivelAnalisisControlPrint.Medido,
            DesgloseJson = JsonSerializer.Serialize(new { cost = costo, settings = ajustes, pages = paginas }, CostSettings.Json),
        };
    }

    /// <summary>
    /// Una recopilación que se cortó (Ctrl+C, la red): RecopilacionControlPrint deja estado «en curso», su motivo y todavía sin cotizaciones (se
    /// leen al final, línea 198). El informe solo dice «Estado: en curso.» y concluye «en Control Print casi no se usó “Guardar cotización”».
    /// </summary>
    [Fact]
    public void Una_recopilacion_sin_terminar_lo_avisa_y_no_concluye_que_casi_no_se_uso_cotizar()
    {
        Directory.CreateDirectory(_carpeta);
        string archivo;
        using (var escritura = ArchivoRecopilacion.Abrir(_carpeta))
        {
            escritura.GuardarValor("estado", "en curso");
            escritura.GuardarValor("motivo", "se detuvo a petición (Ctrl+C)");
            escritura.GuardarValor("nombre_servidor", "PRINCIPAL");
            escritura.GuardarValor("version_control_print", "1.12.0");
            escritura.GuardarValor("hasta_utc", "2026-11-07T20:00:00.0000000Z");
            escritura.GuardarConfiguracion("costos", JsonSerializer.Serialize(new CostSettings(), CostSettings.Json));
            escritura.GuardarTrabajos([Impresion(1, "2026-10-05")]);
            archivo = Path.Combine(_carpeta, ArchivoRecopilacion.NombreArchivo);
        }

        var informe = AnalisisControlPrint.Analizar(archivo);

        Assert.Contains("se detuvo a petición", informe);
        Assert.DoesNotContain("casi no se usó", informe);
        Assert.DoesNotContain("No se registraron copias del panel en Control Print", informe);
    }

    /// <summary>
    /// Terminada, pero Control Print no dio las cotizaciones (LeerSiSePuedeAsync, aviso «No se pudo leer las cotizaciones»): el informe enseña el
    /// aviso y dos párrafos abajo concluye lo contrario de lo que se sabe.
    /// </summary>
    [Fact]
    public void Si_no_se_pudieron_leer_las_cotizaciones_no_concluye_que_no_se_usaron()
    {
        Directory.CreateDirectory(_carpeta);
        string archivo;
        using (var escritura = ArchivoRecopilacion.Abrir(_carpeta))
        {
            escritura.GuardarValor("estado", "terminada");
            escritura.GuardarValor("avisos", JsonSerializer.Serialize(new List<string> { "No se pudo leer las cotizaciones: el servidor contestó 500." }));
            escritura.GuardarTrabajos([Impresion(1, "2026-10-05")]);
            archivo = Path.Combine(_carpeta, ArchivoRecopilacion.NombreArchivo);
        }

        var informe = AnalisisControlPrint.Analizar(archivo);

        Assert.Contains("No se pudo leer las cotizaciones", informe);
        Assert.DoesNotContain("casi no se usó", informe);
    }

    /// <summary>Solo lectura: la base de la recopilación queda igual, byte por byte.</summary>
    [Fact]
    public void El_analisis_no_cambia_la_recopilacion()
    {
        Directory.CreateDirectory(_carpeta);
        string archivo;
        using (var escritura = ArchivoRecopilacion.Abrir(_carpeta))
        {
            escritura.GuardarValor("estado", "terminada");
            escritura.GuardarTrabajos([Impresion(1, "2026-10-05")]);
            archivo = Path.Combine(_carpeta, ArchivoRecopilacion.NombreArchivo);
        }
        var antes = File.ReadAllBytes(archivo);

        AnalisisControlPrint.Analizar(archivo);
        SqliteConnection.ClearAllPools();

        Assert.Equal(antes, File.ReadAllBytes(archivo));
        Assert.Single(Directory.GetFiles(_carpeta));
    }

    /// <summary>
    /// Recopilar se cortó a media página (diario normal: queda el -journal): SQLite no puede deshacerlo en una conexión de solo lectura. Lo que
    /// importa: que lo diga una vez y claro. Hoy cada parte del informe sale «no se pudo leer» con «readonly database».
    /// </summary>
    [Fact]
    public void Con_el_diario_de_una_recopilacion_cortada_dice_que_vuelvas_a_correr_recopilar()
    {
        Directory.CreateDirectory(_carpeta);
        var archivo = Path.Combine(_carpeta, ArchivoRecopilacion.NombreArchivo);
        using (var escritura = ArchivoRecopilacion.Abrir(_carpeta))
        {
            escritura.GuardarValor("estado", "en curso");
            escritura.GuardarTrabajos(Enumerable.Range(1, 400).Select(i => Impresion(i, "2026-10-05")));
        }
        // El diario caliente: una transacción con el caché lleno (ya escribió al archivo), copiada a medias.
        var copia = Path.Combine(_carpeta, "copia");
        Directory.CreateDirectory(copia);
        using (var conexion = new SqliteConnection($"Data Source={archivo};Pooling=False"))
        {
            conexion.Open();
            using var comando = conexion.CreateCommand();
            comando.CommandText = "PRAGMA cache_size=1; BEGIN IMMEDIATE; UPDATE trabajos SET documento = documento || hex(randomblob(2000));";
            comando.ExecuteNonQuery();
            File.Copy(archivo, Path.Combine(copia, ArchivoRecopilacion.NombreArchivo));
            File.Copy(archivo + "-journal", Path.Combine(copia, ArchivoRecopilacion.NombreArchivo + "-journal"));
        }

        var informe = AnalisisControlPrint.Analizar(Path.Combine(copia, ArchivoRecopilacion.NombreArchivo));

        Assert.DoesNotContain("readonly", informe);
    }
}

/// <summary>Comprobaciones de la revisión que deben pasar: L5590 sin precios nunca gratis por ningún camino, y una recopilación para correr la herramienta.</summary>
public sealed class RevisionVariasImpresorasComprobacionesTests : IAsyncLifetime
{
    private BaseDelModulo _base = null!;

    private DateTime Ahora => _base.Pos.Hora.Ahora.UtcDateTime;

    public async Task InitializeAsync() => _base = await BaseDelModulo.CrearAsync();

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private static TrabajoMedidoDto Trabajo(CajaDelPos caja, DateTime impresoUtc, int? impresora, bool medido = true, TrabajoDeTramite? tramite = null) =>
        new(Guid.NewGuid(), caja.Id, null, "CAJA2", $"Cola {impresora}", 7, "Tarea.pdf", "cajero", impresoUtc, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            Enumerable.Range(0, 3).Select(_ => new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)).ToList(), medido, null, null,
            impresoUtc.AddSeconds(-20), tramite, impresora);

    private static SolicitudConsumiblesImpresora ConPrecios(decimal negro, decimal cian) =>
        new(Guid.NewGuid(), [new("Negro", negro, 4300), new("Cian", cian, 7300), new("Magenta", cian, 7300), new("Amarillo", cian, 7300)]);

    [Fact]
    public async Task Sin_todos_sus_precios_la_L5590_cuesta_lo_de_una_WF_6590_por_todos_los_caminos()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        var registro = _base.Registro(db);
        var wf = (await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        var l5590 = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, null);
        var costos = new CostSettings();
        costos.Papers[0].PackPrice = 250m;
        await registro.GuardarCostosAsync(PosDePrueba.Sucursal, costos);
        var opciones = new OpcionesCotizacionDto("Plain", "Carta", false, "Standard", false, 1, 1, null);
        var paginas = Enumerable.Range(0, 3).Select(_ => new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)).ToList();

        async Task<(long Medido, long Estimado, long Cotizado, long Copias, long Costo)> Precios(int numero)
        {
            var medido = await registro.RegistrarAsync(Trabajo(caja, Ahora, numero));
            var estimado = await registro.RegistrarAsync(Trabajo(caja, Ahora, numero, medido: false));
            var cotizado = await registro.CotizarAsync(new SolicitudCotizacion(PosDePrueba.Sucursal, null, paginas, opciones, numero), conCosto: true);
            var (copias, _) = await registro.CalcularCopiasAsync(PosDePrueba.Sucursal, 3, false, "Carta", false, numero);
            return (medido.PrecioCentavos, estimado.PrecioCentavos, cotizado.PrecioCentavos, (long)(copias.Price * 100), medido.CostoCentavos);
        }

        var deLaWf = await Precios(wf.Numero);
        Assert.True(deLaWf.Medido > 0 && deLaWf.Costo > 0);

        // Sin ningún precio.
        Assert.Equal(deLaWf, await Precios(l5590.Numero));
        // Con un solo precio en cero.
        await impresoras.GuardarConsumiblesAsync(l5590, ConPrecios(259, 0));
        Assert.Equal(deLaWf, await Precios(l5590.Numero));

        // Con todos: los suyos, sin caja de mantenimiento, más baratos y nunca gratis; el trámite incluido sin precio, y con su costo.
        await impresoras.GuardarConsumiblesAsync(l5590, ConPrecios(259, 259));
        var suyos = await Precios(l5590.Numero);
        Assert.True(suyos.Medido > 0 && suyos.Costo > 0 && suyos.Costo < deLaWf.Costo, suyos.ToString());
        var incluida = await registro.RegistrarAsync(Trabajo(caja, Ahora, l5590.Numero, tramite: new TrabajoDeTramite("T-0042", CopiasExtra: false)));
        var deMas = await registro.RegistrarAsync(Trabajo(caja, Ahora, l5590.Numero, tramite: new TrabajoDeTramite("T-0042", CopiasExtra: true)));
        Assert.Equal((0L, EstadoCobroImpresion.EnTramite, true), (incluida.PrecioCentavos, incluida.Cobro, incluida.CostoCentavos > 0));
        Assert.Equal((suyos.Medido, EstadoCobroImpresion.PorCobrar), (deMas.PrecioCentavos, deMas.Cobro));
    }

    /// <summary>Con REV_RECOPILACION: deja en esa carpeta una recopilación inventada, con los costos de research/dev-cost-settings.json de Control Print.</summary>
    [Fact]
    public void Una_recopilacion_para_correr_la_herramienta()
    {
        if (Environment.GetEnvironmentVariable("REV_RECOPILACION") is not { Length: > 0 } carpeta)
            return;
        Directory.CreateDirectory(carpeta);
        using var archivo = ArchivoRecopilacion.Abrir(carpeta);
        archivo.GuardarValor("estado", "en curso");
        archivo.GuardarValor("motivo", "se detuvo a petición (Ctrl+C)");
        archivo.GuardarValor("nombre_servidor", "PRINCIPAL");
        archivo.GuardarValor("version_control_print", "1.12.0");
        archivo.GuardarValor("hasta_utc", "2026-11-07T20:00:00.0000000Z");
        var costos = File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache/control-print-repo/control-print/research/dev-cost-settings.json"));
        archivo.GuardarConfiguracion("costos", costos);
        var ajustes = new JobSettings { Color = false };
        var paginas = new List<PageCoverage> { new(0, 0, 0, 0.05, 0.05, 93.5, false) };
        var deVerdad = JsonSerializer.Deserialize<CostSettings>(costos, CostSettings.Json)!;
        var costo = CostCalculator.Calculate(new JobAnalysis(ajustes, paginas, AnalysisLevel.Measured, null, null, TimeSpan.Zero), deVerdad);
        var detectado = new DateTime(2026, 10, 5, 17, 0, 0, DateTimeKind.Utc);
        archivo.GuardarTrabajos(
        [
            new TrabajoControlPrint
            {
                Id = 1, PcOrigen = "PRINCIPAL", Tipo = TipoTrabajoControlPrint.Impresion, Impresora = "WF-6590 Series(Red)", Documento = "CURP de Juana Pérez.pdf",
                DetectadoUtc = detectado, EnviadoUtc = detectado, Estado = EstadoTrabajoControlPrint.Impreso, Paginas = 1, Copias = 1, Caras = 1, Hojas = 1,
                CostoTotal = costo.Total, Precio = costo.Price, PrecioACobrar = costo.Price, Cobrable = true, Cobro = CobroControlPrint.Cobrado,
                NivelAnalisis = NivelAnalisisControlPrint.Medido, Notas = "Nota de Juana", Etiqueta = "Juana, tarea", ClienteDelPrecio = "Juana Pérez",
                DesgloseJson = JsonSerializer.Serialize(new
                {
                    cost = costo, settings = ajustes, pages = paginas.Select(p => new { p.C, p.M, p.Y, p.K, p.Gray, p.AreaSqIn, p.HasColor }), elapsedMs = 12.0,
                }, CostSettings.Json),
            },
        ]);
    }
}
