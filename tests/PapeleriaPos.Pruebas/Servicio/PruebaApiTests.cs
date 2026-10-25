using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Datos.Documentos;
using PapeleriaPos.Herramientas;
using PapeleriaPos.Herramientas.Ficticios;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Herramientas.Prueba;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Telegram;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El servicio como lo arranca la caja de la prueba con una persona real: la base inicial la arma <c>preparar-prueba</c>
/// y la de trabajo todavía no existe (se copia al arrancar).
/// </summary>
public sealed class ServicioEnPrueba : IAsyncLifetime
{
    public string Carpeta { get; } = Path.Combine(Path.GetTempPath(), $"pos-prueba-real-{Guid.NewGuid():N}");
    public string BaseInicial => Path.Combine(Carpeta, "prueba.db");
    public string Ejemplos => Path.Combine(Carpeta, "Ejemplos");
    public WebApplicationFactory<Program> Fabrica { get; private set; } = null!;
    public List<string> Hecho { get; private set; } = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Carpeta);
        await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={BaseInicial};Pooling=False")))
        {
            await BaseDeDatos.PrepararAsync(db);
            await GuardadoLote.GuardarAsync(GeneradorFicticios.Generar(new OpcionesFicticios { Dias = 30 }), db);
            Hecho = await PreparacionPrueba.PrepararAsync(db, Ejemplos);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");
        }
        Fabrica = new FabricaDePrueba(b => b
            .UseSetting("ConnectionStrings:Pos", $"Data Source={Path.Combine(Carpeta, "pos.db")};Pooling=False")
            .UseSetting("Prueba:Activa", "true").UseSetting("Prueba:BaseInicial", BaseInicial).UseSetting("Prueba:CarpetaRespuestas", Carpeta)
            .UseSetting("Respaldos:Activo", "false").UseSetting("Telegram:Activo", "false"));
    }

    /// <summary>
    /// Detiene el servicio y borra la carpeta (las bases locales de caja que dejaron las pruebas sueltan sus conexiones al
    /// borrarla). «Empezar de cero» lee la base inicial con su propia cadena, de solo lectura (<c>ModoPrueba</c>): esas
    /// conexiones también son de esta prueba y se sueltan con esa cadena.
    /// </summary>
    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        ArchivosDePrueba.Soltar(new SqliteConnectionStringBuilder { DataSource = BaseInicial, Mode = SqliteOpenMode.ReadOnly }.ToString());
        ArchivosDePrueba.BorrarCarpeta(Carpeta);
    }
}

public class PruebaApiTests(ServicioEnPrueba prueba) : IClassFixture<ServicioEnPrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>
    /// La base de preparar-prueba ya trae los permisos de cada usuario (entrega 1): los de su rol de antes, como al migrar, y
    /// al dueño como primera cuenta. «Empezar de cero» la copia tal cual, y la guía sigue igual.
    /// </summary>
    [Fact]
    public async Task La_base_de_prueba_trae_los_permisos_de_cada_usuario_como_con_los_roles()
    {
        await using var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={prueba.BaseInicial};Mode=ReadOnly;Pooling=False"));
        var usuarios = await db.Usuarios.AsNoTracking().ToListAsync();
        var permisos = await db.PermisosUsuario.AsNoTracking().ToListAsync();
        var reglas = await db.ReglasPermiso.AsNoTracking().ToListAsync();
        var sucursal = (await db.Cajas.AsNoTracking().FirstAsync()).SucursalId;
        PermisosDeUsuario De(Guid? usuarioId) => new(permisos.Where(p => p.UsuarioId == usuarioId).Select(p => p.Permiso));

        Assert.Equal(["cajero", "dueno", "encargado"], usuarios.Select(u => u.NombreUsuario).Order());
        Assert.All(usuarios, u => Assert.Equal(PermisosDeRoles.De(u.Rol, reglas, u.SucursalId ?? sucursal), De(u.Id).Lista()));
        Assert.Equal(PermisosDeRoles.PorDefecto(reglas), De(null).Lista());
        Assert.Equal(["dueno"], usuarios.Where(u => u.PrimeraCuenta).Select(u => u.NombreUsuario));
        Assert.Equal(Permisos.Acciones.Count * 4, permisos.Count);
    }

    [Fact]
    public async Task La_base_de_prueba_trae_avisos_simulados_impresiones_y_documentos_que_se_reconocen()
    {
        var http = prueba.Fabrica.CreateClient();
        var estado = await Leer<EstadoServicioDto>(await http.GetAsync("api/estado"));
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var factura = LectorDocumentos.Leer(await File.ReadAllBytesAsync(Path.Combine(prueba.Ejemplos, "Factura de ejemplo.xml")));
        var nota = LectorDocumentos.Leer(await File.ReadAllBytesAsync(Path.Combine(prueba.Ejemplos, "Nota de ejemplo.pdf")));
        var leeme = await File.ReadAllTextAsync(Path.Combine(prueba.Ejemplos, "Léeme.txt"));

        Assert.True(estado.Prueba);
        Assert.True(File.Exists(Path.Combine(prueba.Carpeta, "pos.db")));
        using var alcance = prueba.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        Assert.True(await db.ChatsTelegram.AnyAsync(c => c.ChatId == ChatTelegram.Simulado));
        Assert.Equal(4, await db.Set<TrabajoImpresion>().CountAsync(t => t.Cobro == EstadoCobroImpresion.PorCobrar && t.CajaId == caja.Id && t.PrecioCentavos > 0));
        // La factura trae tres códigos de barras de la base (llegan ligados), una clave del proveedor (se confirma) y un
        // producto que no está en el catálogo (llega como nuevo).
        Assert.Equal(5, factura.Lineas.Count);
        var codigos = factura.Lineas.Select(l => l.Codigo!).ToList();
        Assert.Equal(3, await db.CodigosBarras.CountAsync(c => codigos.Contains(c.Codigo)));
        Assert.Equal(3, nota.Lineas.Count);
        Assert.Contains(nota.Avisos, a => a.Contains("repartió"));
        // Se entra con la ficha (su letra, la misma que reparte la caja) y el PIN, sin escribir el usuario (entrega 6).
        Assert.Contains("Cajero de prueba (C) · PIN 1111", leeme);
        Assert.Contains("Encargado de prueba (E) · PIN 2222", leeme);
        Assert.Contains("Dueño de prueba (D) · PIN 3333", leeme);
        Assert.DoesNotContain("cajero / 1111", leeme);
        Assert.Contains(factura.Lineas[0].Codigo!, leeme);
        Assert.Contains("no está en el catálogo", leeme);
        // Fiar es con «Crédito» junto a «Pagó con»; «A crédito» era la ventana de cobro de la pantalla de antes, que ya no existe.
        Assert.Contains("«Crédito» junto a «Pagó con»", leeme);
        Assert.DoesNotContain("«A crédito»", leeme);
        Assert.Contains("dejar la guía en blanco", leeme);
        // Los códigos para vender (la tarea «escanear») son los de la factura que ya están en el catálogo, no el nuevo.
        var paraVender = leeme.ReplaceLineEndings("\n").Split('\n').SkipWhile(l => !l.StartsWith("Códigos para vender", StringComparison.Ordinal))
            .Skip(1).TakeWhile(l => l.StartsWith("     ", StringComparison.Ordinal)).Select(l => l.Trim().Split(' ')[0]).ToList();
        Assert.Equal(4, paraVender.Count);
        Assert.Equal(4, await db.CodigosBarras.CountAsync(c => paraVender.Contains(c.Codigo)));
    }

    /// <summary>
    /// Lo que la guía de la prueba (v5, 2026-10-24) toma de esta base, para que pase lo que dice: cada código que nombra es de
    /// «Léeme.txt» (salvo los dos que no deben existir: el del alta rápida y el renglón nuevo del Excel); el block de las
    /// promociones cuesta $36 en sus tres variantes y su 3×2 no las deja debajo del costo; su «Cuadro» a $20 sí queda debajo con
    /// el 3×2, y no sin él; el cuaderno pautado tiene una sola variante, con costo. Los permisos de cada usuario que las tareas
    /// suponen, y el historial de un año hasta el 30 de septiembre de 2026 con que comparan los reportes. Si el generador
    /// cambia, también la guía (GuiaPrueba).
    /// </summary>
    [Fact]
    public async Task La_base_de_prueba_trae_lo_que_nombra_la_guia()
    {
        var leeme = (await File.ReadAllTextAsync(Path.Combine(prueba.Ejemplos, "Léeme.txt"))).ReplaceLineEndings("\n");
        var paraVender = leeme.Split('\n').SkipWhile(l => !l.StartsWith("Códigos para vender", StringComparison.Ordinal)).Skip(1)
            .TakeWhile(l => l.StartsWith("     ", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
        var nombrados = GuiaPrueba.Tareas.SelectMany(t => System.Text.RegularExpressions.Regex.Matches($"{t.Hacer} {t.Esperado}", @"\d{13}"))
            .Select(m => m.Value).Distinct().ToList();
        string[] queNoExisten = ["7500000000017", "2000000099991"];
        await using var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={prueba.BaseInicial};Mode=ReadOnly;Pooling=False"));
        var sucursal = (await db.Cajas.AsNoTracking().FirstAsync()).SucursalId;

        Assert.All(nombrados.Except(queNoExisten), c => Assert.Contains(paraVender, l => l.StartsWith($"{c} ", StringComparison.Ordinal)));
        Assert.False(await db.CodigosBarras.AnyAsync(c => queNoExisten.Contains(c.Codigo)));
        Assert.Equal(["7502380890866  Block de hojas Blanco", "7504798804251  Acuarelas 12 colores", "7506303647469  Block de hojas Cuadro",
            "7509208072725  Acuarelas 24 colores"], paraVender.Order(StringComparer.Ordinal));

        // El block del 3×2 («Block de hojas» Estrella): tres variantes de $36, y con dos de cada tres ninguna queda debajo del costo.
        var producto = await db.CodigosBarras.AsNoTracking().Where(c => c.Codigo == "7502380890866")
            .Select(c => c.Presentacion!.Variante!.ProductoId).SingleAsync();
        var block = await db.Variantes.AsNoTracking().Where(v => v.ProductoId == producto && v.Activa).Select(v => new { v.Id, v.Descripcion })
            .ToListAsync();
        var ids = block.Select(v => v.Id).ToList();
        var menudeo = (await db.Precios.AsNoTracking().Where(p => ids.Contains(p.VarianteId) && p.PresentacionId == null && p.SucursalId == null)
            .ToListAsync()).GroupBy(p => p.VarianteId).ToDictionary(g => g.Key, g => g.MinBy(p => p.CantidadMinima)!.PrecioCentavos);
        var costos = await RegistroPromociones.CostosPorUnidadAsync(db, sucursal, ids);
        Assert.Equal(["Blanco", "Cuadro", "Raya"], block.Select(v => v.Descripcion).Order());
        Assert.All(block, v => Assert.Equal(3600, menudeo[v.Id]));
        Assert.All(block, v => Assert.True(costos[v.Id] * 3 <= 3600 * 2, $"{v.Descripcion} cuesta {costos[v.Id]}"));
        // Su «Cuadro» a $20 («promo-fuera»): el 3×2 lo deja en $13.33, debajo de su costo; sin él, arriba.
        Assert.InRange(costos[block.Single(v => v.Descripcion == "Cuadro").Id], 2000 * 2 / 3m, 1999m);
        // El cuaderno pautado («promocion-debajo-del-costo»): una sola variante, con costo (un precio especial de $1 queda debajo).
        var pautado = await db.Variantes.AsNoTracking().Where(v => v.Producto!.Nombre == "Cuaderno pautado de música" && v.Activa)
            .Select(v => v.Id).ToListAsync();
        Assert.Single(pautado);
        Assert.True((await RegistroPromociones.CostosPorUnidadAsync(db, sucursal, pautado))[pautado[0]] > 100);
        Assert.True(await db.Productos.AnyAsync(p => p.Departamento!.Nombre == "Cuadernos" && p.Nombre.StartsWith("Cuaderno") && p.Activo));

        // Los permisos que las tareas suponen: el cajero agrega inventario, pero no cancela una salida ni ve los reportes; el
        // encargado ve el costo al agregar (por las compras), no la utilidad, y vende debajo del costo; el dueño cancela.
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.NombreUsuario, u => u.Id);
        var permisos = await db.PermisosUsuario.AsNoTracking().ToListAsync();
        NivelPermiso Nivel(string usuario, AccionControlada accion) => permisos.Single(p => p.UsuarioId == usuarios[usuario] && p.Accion == accion).Nivel;
        Assert.Equal(
            new[]
            {
                NivelPermiso.Permitido, NivelPermiso.NoPermitido, NivelPermiso.NoPermitido, NivelPermiso.Permitido, NivelPermiso.Permitido,
                NivelPermiso.NoPermitido, NivelPermiso.Permitido, NivelPermiso.Permitido,
            },
            new[]
            {
                Nivel("cajero", AccionControlada.AgregarInventario), Nivel("cajero", AccionControlada.CancelarMovimiento),
                Nivel("cajero", AccionControlada.VerReportes), Nivel("encargado", AccionControlada.AgregarInventario),
                Nivel("encargado", AccionControlada.CostearCompra), Nivel("encargado", AccionControlada.VerUtilidad),
                Nivel("encargado", AccionControlada.VenderBajoCosto), Nivel("dueno", AccionControlada.CancelarMovimiento),
            });

        // Los reportes comparan con el historial de un año que termina el 30 de septiembre de 2026 (la base de preparar-prueba).
        Assert.Equal((new DateOnly(2026, 9, 30), 365), (new OpcionesFicticios().UltimoDia, new OpcionesFicticios().Dias));
    }

    [Fact]
    public async Task Lo_que_se_le_avisaria_al_dueno_se_ve_en_la_caja()
    {
        var http = prueba.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var producto = (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=pluma&sucursalId={caja.SucursalId}"))!.First();

        await http.PostAsJsonAsync("api/catalogo/precio-menudeo", new SolicitudPrecioMenudeo(encargado.UsuarioId, caja.SucursalId, producto.VarianteId,
            producto.PrecioCentavos + 500));
        var avisos = await Leer<List<AvisoPruebaDto>>(await http.GetAsync("api/prueba/avisos"));

        Assert.Contains(avisos, a => a.Tipo == TipoAlerta.CambioPrecioCatalogo && a.Texto.Contains("Encargado de prueba"));
    }

    [Fact]
    public async Task Las_respuestas_se_guardan_y_se_exportan_a_un_archivo()
    {
        var http = prueba.Fabrica.CreateClient();
        var respuestas = new RespuestasPruebaDto("Ana", new Dictionary<string, RespuestaTareaDto>
        {
            ["cobrar"] = new(true, null),
            ["corte"] = new(false, "No encontré dónde se cuenta el efectivo"),
        }, "Todo claro", GuiaPrueba.Version);

        await Leer<RespuestasPruebaDto>(await http.PutAsJsonAsync("api/prueba/respuestas", respuestas));
        var leidas = await Leer<RespuestasPruebaDto>(await http.GetAsync($"api/prueba/respuestas?version={GuiaPrueba.Version}"));
        var texto = GuiaPrueba.Documento(leidas, new DateTime(2026, 10, 7, 12, 0, 0));
        var archivo = await Leer<ArchivoGuardadoDto>(await http.PostAsJsonAsync("api/prueba/respuestas/exportar", new SolicitudExportarRespuestas(texto)));
        var otraVez = await Leer<ArchivoGuardadoDto>(await http.PostAsJsonAsync("api/prueba/respuestas/exportar", new SolicitudExportarRespuestas("otra")));

        Assert.Equal(("Ana", GuiaPrueba.Version), (leidas.Nombre, leidas.Version));
        Assert.False(leidas.Tareas["corte"].SalioBien);
        Assert.Contains("Contestadas: 2 de", texto);
        Assert.Contains($"- Guía: versión {GuiaPrueba.Version}\n", texto.ReplaceLineEndings("\n"));
        Assert.Contains("- Lo que vio: No encontré dónde se cuenta el efectivo", texto);
        Assert.Equal(prueba.Carpeta, Path.GetDirectoryName(archivo.Ruta));
        Assert.Equal(texto, await File.ReadAllTextAsync(archivo.Ruta));
        // Otro en el mismo minuto no se encima en el primero.
        Assert.NotEqual(archivo.Ruta, otraVez.Ruta);
        File.Delete(archivo.Ruta);
        File.Delete(otraVez.Ruta);
    }

    [Fact]
    public void Los_registros_de_la_caja_salen_con_las_respuestas()
    {
        // Como en la PC de prueba: la base y sus registros en una carpeta, las respuestas en el escritorio.
        var datos = Path.Combine(Path.GetTempPath(), $"pos-prueba-registros-{Guid.NewGuid():N}");
        var escritorio = Path.Combine(datos, "Escritorio");
        Directory.CreateDirectory(Path.Combine(datos, "registros"));
        Directory.CreateDirectory(escritorio);
        try
        {
            File.WriteAllText(Path.Combine(datos, "registros", "caja-2026-10-17.log"), "Algo falló al cobrar");
            // La caja lo tiene abierto mientras se exporta.
            using var abierto = new FileStream(Path.Combine(datos, "registros", "servicio-2026-10-17.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
            abierto.Write("Arrancó"u8);
            abierto.Flush();
            var modo = new PapeleriaPos.Servicio.ModoPrueba(true, null, $"Data Source={Path.Combine(datos, "pos.db")}", escritorio);

            var documento = modo.Exportar("Respuestas");

            var copiados = Path.Combine(escritorio, $"{Path.GetFileNameWithoutExtension(documento)} - registros");
            Assert.Equal(["caja-2026-10-17.log", "servicio-2026-10-17.log"], Directory.GetFiles(copiados).Select(Path.GetFileName).Order());
            Assert.Equal("Algo falló al cobrar", File.ReadAllText(Path.Combine(copiados, "caja-2026-10-17.log")));
            Assert.Equal("Arrancó", File.ReadAllText(Path.Combine(copiados, "servicio-2026-10-17.log")));
        }
        finally
        {
            Directory.Delete(datos, recursive: true);
        }
    }

    [Fact]
    public async Task Las_respuestas_de_otra_version_de_la_guia_no_se_cargan_y_su_archivo_se_queda()
    {
        // Como las dejó la caja de la primera prueba (2026-10-10): sin versión y con los Id de la guía de entonces.
        var deAntes = Path.Combine(prueba.Carpeta, "respuestas-prueba.json");
        const string json = """
            { "nombre": "Quien probó primero", "tareas": { "vender": { "salioBien": false, "nota": "La pantalla de hoy" } }, "comentarios": "Horrible" }
            """;
        await File.WriteAllTextAsync(deAntes, json);
        var http = prueba.Fabrica.CreateClient();

        var nueva = await Leer<RespuestasPruebaDto>(await http.GetAsync($"api/prueba/respuestas?version={GuiaPrueba.Version}"));
        await Leer<RespuestasPruebaDto>(await http.PutAsJsonAsync("api/prueba/respuestas",
            new RespuestasPruebaDto("Beto", new Dictionary<string, RespuestaTareaDto> { ["entrar"] = new(true, null) }, null, GuiaPrueba.Version)));
        var laPrimera = await Leer<RespuestasPruebaDto>(await http.GetAsync("api/prueba/respuestas"));

        Assert.True(GuiaPrueba.Version > 1);
        Assert.Equal(GuiaPrueba.Version, nueva.Version);
        Assert.NotEqual("Quien probó primero", nueva.Nombre);
        Assert.DoesNotContain("vender", nueva.Tareas.Keys);
        // Las de la primera guía siguen en su archivo, tal cual: guardar las nuevas no las toca.
        Assert.Equal(json, await File.ReadAllTextAsync(deAntes));
        Assert.Equal((1, "Quien probó primero", "La pantalla de hoy"), (laPrimera.Version, laPrimera.Nombre, laPrimera.Tareas["vender"].Nota));
    }

    [Fact]
    public async Task Al_dejar_la_guia_en_blanco_se_guardan_las_respuestas_y_la_siguiente_persona_empieza_sin_ellas()
    {
        var cliente = new PapeleriaPos.Interfaz.ClienteCaja(new HttpClient(prueba.Fabrica.Server.CreateHandler()),
            new PapeleriaPos.Interfaz.ConexionCaja { Servicio = new Uri("http://localhost/") });
        var delSabado = new RespuestasPruebaDto("Persona del sábado", new Dictionary<string, RespuestaTareaDto>
        {
            ["buscar"] = new(true, null),
            ["familias"] = new(false, "No supe cómo abrir los colores"),
        }, null, GuiaPrueba.Version);
        await cliente.GuardarRespuestasPruebaAsync(delSabado);
        var texto = GuiaPrueba.Documento(await cliente.RespuestasPruebaAsync(GuiaPrueba.Version), new DateTime(2026, 10, 17, 18, 0, 0));

        var archivo = await cliente.DejarGuiaEnBlancoAsync(GuiaPrueba.Version, texto);
        var delDomingo = await cliente.RespuestasPruebaAsync(GuiaPrueba.Version);
        // La guía ya en blanco, en el mismo minuto: no se encima en el documento del sábado.
        var enBlanco = await cliente.DejarGuiaEnBlancoAsync(GuiaPrueba.Version, GuiaPrueba.Documento(delDomingo, new DateTime(2026, 10, 17, 18, 1, 0)));
        var apartadas = Directory.GetFiles(prueba.Carpeta, $"respuestas-prueba-guia-{GuiaPrueba.Version} (guardadas *).json");

        Assert.Equal(prueba.Carpeta, Path.GetDirectoryName(archivo.Ruta));
        Assert.Equal(texto, await File.ReadAllTextAsync(archivo.Ruta));
        Assert.Contains("Persona del sábado", texto);
        Assert.Contains("- Lo que vio: No supe cómo abrir los colores", texto);
        Assert.Equal((null, 0, GuiaPrueba.Version), (delDomingo.Nombre, delDomingo.Tareas.Count, delDomingo.Version));
        Assert.NotEqual(archivo.Ruta, enBlanco.Ruta);
        Assert.Equal(texto, await File.ReadAllTextAsync(archivo.Ruta));
        // El archivo de las respuestas no se borra: se aparta con la hora.
        Assert.Contains(apartadas, a => System.Text.Json.JsonSerializer.Deserialize<RespuestasPruebaDto>(File.ReadAllText(a),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Nombre == "Persona del sábado");
        File.Delete(archivo.Ruta);
        File.Delete(enBlanco.Ruta);
    }

    [Fact]
    public async Task Un_archivo_de_respuestas_danado_se_aparta_y_la_guia_abre_en_blanco()
    {
        // Una versión que no usa otra prueba: el archivo es solo de esta. Así quedaba si se iba la luz a media escritura.
        const int version = 99;
        const string aMedias = """{ "nombre": "Se fue la luz", "tareas": { "entr""";
        await File.WriteAllTextAsync(Path.Combine(prueba.Carpeta, $"respuestas-prueba-guia-{version}.json"), aMedias);
        var http = prueba.Fabrica.CreateClient();

        var leidas = await Leer<RespuestasPruebaDto>(await http.GetAsync($"api/prueba/respuestas?version={version}"));
        var apartado = Assert.Single(Directory.GetFiles(prueba.Carpeta, $"respuestas-prueba-guia-{version} (dañadas *).json"));
        await Leer<RespuestasPruebaDto>(await http.PutAsJsonAsync("api/prueba/respuestas",
            new RespuestasPruebaDto("Ana", new Dictionary<string, RespuestaTareaDto> { ["entrar"] = new(true, null) }, null, version)));
        var guardadas = await Leer<RespuestasPruebaDto>(await http.GetAsync($"api/prueba/respuestas?version={version}"));

        Assert.Equal((null, 0, version), (leidas.Nombre, leidas.Tareas.Count, leidas.Version));
        // No se borra: lo que se alcance a leer de ahí sigue en el archivo apartado.
        Assert.Equal(aMedias, await File.ReadAllTextAsync(apartado));
        Assert.Equal(("Ana", true), (guardadas.Nombre, guardadas.Tareas["entrar"].SalioBien));
        // Se escriben aparte y se renombran: no queda el temporal.
        Assert.Empty(Directory.GetFiles(prueba.Carpeta, "*.nuevo"));
    }

    [Fact]
    public async Task Empezar_de_cero_regresa_a_la_base_con_que_se_instalo()
    {
        var http = prueba.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        await Leer<ProveedorDto>(await http.PostAsJsonAsync("api/proveedores",
            new SolicitudGuardarProveedor(encargado.UsuarioId, caja.SucursalId, new ProveedorDto(null, "Proveedor creado en la prueba", null, null, null))));

        var reinicio = await http.PostAsync("api/prueba/empezar-de-cero", null);
        var proveedores = await http.GetFromJsonAsync<List<ProveedorDto>>("api/proveedores");
        var buscados = await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=pluma&sucursalId={caja.SucursalId}");

        Assert.Equal(HttpStatusCode.NoContent, reinicio.StatusCode);
        Assert.DoesNotContain(proveedores!, p => p.Nombre == "Proveedor creado en la prueba");
        Assert.NotEmpty(buscados!);
    }

    [Fact]
    public async Task La_caja_de_la_prueba_trabaja_sin_conexion_simulada_y_la_guia_sigue_contestando()
    {
        // Como la arma la caja de Windows de la prueba: sin alta (la primera caja) y con su base local junto a los datos.
        var http = new HttpClient(prueba.Fabrica.Server.CreateHandler());
        var conexion = new PapeleriaPos.Interfaz.ConexionCaja { Servicio = new Uri("http://localhost/") };
        var baseLocal = Path.Combine(prueba.Carpeta, $"caja-local-{Guid.NewGuid():N}.db");
        var contingencia = new PapeleriaPos.Interfaz.Contingencia(new PapeleriaPos.Interfaz.AlmacenLocal(baseLocal), http, conexion);
        var cliente = new PapeleriaPos.Interfaz.ClienteCaja(http, conexion, contingencia);
        await contingencia.LatidoAsync();
        var caja = (await cliente.CajasAsync()).First();
        var ramirez = (await cliente.ClientesRegistradosAsync(caja.SucursalId)).Single(c => c.Nombre == PreparacionPrueba.ConDeuda);

        conexion.SimularSinConexion = true;
        await contingencia.LatidoAsync();
        var cuenta = await cliente.CreditoAsync(ramirez.Id);
        var avisos = await cliente.AvisosPruebaAsync();
        var respuestas = await cliente.RespuestasPruebaAsync(GuiaPrueba.Version);
        var reiniciar = await Assert.ThrowsAsync<PapeleriaPos.Interfaz.ErrorCaja>(() => cliente.EmpezarDeCeroAsync());

        Assert.Equal(caja.Id, conexion.CajaId);
        Assert.False(contingencia.EnLinea);
        Assert.Equal((PreparacionPrueba.DeudaCentavos, true), (cuenta.SaldoCentavos, cuenta.Estimado));
        Assert.NotNull(avisos);
        Assert.NotNull(respuestas);
        Assert.StartsWith("Primero vuelve a conectar", reiniciar.Message);
        // Su base local está en la carpeta de la prueba, que se borra al final: la caja se cierra y la suelta (solo la suya).
        contingencia.Cerrar(TimeSpan.FromSeconds(20));
    }
}

public class SinPruebaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    [Fact]
    public async Task En_la_tienda_no_existen_las_rutas_de_la_prueba()
    {
        var http = servicio.Fabrica.CreateClient();

        var estado = await http.GetFromJsonAsync<EstadoServicioDto>("api/estado");
        var avisos = await http.GetAsync("api/prueba/avisos");
        var reinicio = await http.PostAsync("api/prueba/empezar-de-cero", null);

        Assert.False(estado!.Prueba);
        Assert.Equal(HttpStatusCode.NotFound, avisos.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reinicio.StatusCode);
    }
}
