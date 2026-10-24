using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Configuración › Respaldos (2026-10-22), con el servicio de verdad y su propia carpeta de respaldos: el último, dónde se
/// guardan y «Respaldar ahora», que dice dónde quedó y cuánto mide. Sin la copia diaria de fondo, así lo que se ve no depende
/// de cuándo corre esa. Quién ve el grupo lo revisa ConfiguracionTests.
/// </summary>
public sealed class RespaldosEnConfiguracionTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"respaldos-cfg-{Guid.NewGuid():N}");

    public void Dispose() => ArchivosDePrueba.BorrarCarpeta(_carpeta);

    [Fact]
    public async Task El_dueno_respalda_ahora_y_ve_donde_quedo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(
            s => s.Remove(s.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RespaldoDiario))),
            ("Respaldos:Activo", "true"), ("Respaldos:Carpeta", _carpeta));
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .BuildServiceProvider();
        await using var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<Configuracion>(new()
        {
            [nameof(Configuracion.Caja)] = caja,
            [nameof(Configuracion.Sesion)] = dueno,
            [nameof(Configuracion.AlCambiar)] = new EventCallback(null, (Action)(() => { })),
            [nameof(Configuracion.AlNoGuardar)] = new EventCallback<string>(null, (Action<string>)(_ => { })),
        });
        await pantalla.EsperarAsync(t => !t.Contains("Cargando…"), "lo guardado");

        await pantalla.OprimirAsync(await pantalla.ElementoAsync(
            e => e.Atributos.GetValueOrDefault("role") as string == "tab" && PantallaSinNavegador.Limpio(e.Texto) == "Respaldos", "el grupo «Respaldos»"));
        var antes = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        Assert.Contains("Último respaldo", antes);
        Assert.Contains("Todavía no hay ninguno", antes);
        Assert.Contains($"{_carpeta} · los de los últimos 30 días", antes);
        // Restaurar, como en el prototipo: su renglón con «Elegir respaldo…»; la lista no se ve hasta abrirla.
        Assert.Contains("Restaurar un respaldo", antes);
        Assert.Contains("Elegir respaldo…", antes);
        Assert.DoesNotContain("Restaurar este", antes);

        await pantalla.OprimirAsync(await pantalla.PorIdAsync("aj-respaldar"));
        await pantalla.EsperarAsync(t => t.Contains("Se guardó en"), "el respaldo");

        var archivo = Assert.Single(Directory.GetFiles(_carpeta, "pos-*-ahora-*.db"));
        var despues = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        Assert.Contains($"Se guardó en {archivo} (", despues);
        Assert.Contains(" a las ", despues);
        Assert.DoesNotContain("Todavía no hay ninguno", despues);
        Assert.Empty(pantalla.Errores);
    }

    /// <summary>
    /// Restaurar un respaldo (2026-10-23), con el teclado: «Elegir respaldo…» abre la lista (el más reciente primero, de qué es y
    /// cuánto mide), «Restaurar este» dice lo que se pierde y pide el PIN otra vez; con otro PIN lo dice y deja volver a escribir.
    /// Con el bueno, la caja principal se reinicia: la pantalla espera sola a que vuelva y dice cómo quedó y dónde quedó la base de
    /// antes.
    /// </summary>
    [Fact]
    public async Task El_dueno_elige_un_respaldo_lo_restaura_con_su_PIN_y_la_pantalla_espera_a_que_vuelva_la_caja_principal()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var http = p.Arrancar();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var ahoraLocal = HoraMexico.ALocal(DateTime.UtcNow);
        p.Respaldar(ahoraLocal.AddMinutes(-120), "z-2131");
        p.Respaldar(ahoraLocal.AddMinutes(-60));
        var masReciente = p.Respaldar(ahoraLocal.AddMinutes(-30), "ahora-1010");
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(new HttpClient(p.ParaLasCajas, disposeHandler: false), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .BuildServiceProvider();
        await using var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<Configuracion>(new()
        {
            [nameof(Configuracion.Caja)] = caja,
            [nameof(Configuracion.Sesion)] = dueno,
            [nameof(Configuracion.AlCambiar)] = new EventCallback(null, (Action)(() => { })),
            [nameof(Configuracion.AlNoGuardar)] = new EventCallback<string>(null, (Action<string>)(_ => { })),
        });
        await pantalla.EsperarAsync(t => !t.Contains("Cargando…"), "lo guardado");
        await pantalla.OprimirAsync(await pantalla.ElementoAsync(
            e => e.Atributos.GetValueOrDefault("role") as string == "tab" && PantallaSinNavegador.Limpio(e.Texto) == "Respaldos", "el grupo «Respaldos»"));

        await pantalla.OprimirAsync(await pantalla.PorIdAsync("aj-restaurar"));
        await pantalla.EsperarAsync(t => t.Contains("Restaurar este"), "la lista de los respaldos");
        var tabla = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        // Desde el encabezado de la tabla (es marcado fijo: sale junto, «CuándoDe quéTamaño»).
        Assert.Contains("Tamaño", tabla);
        tabla = tabla[tabla.IndexOf("Tamaño", StringComparison.Ordinal)..];
        Assert.True(tabla.IndexOf("Respaldar ahora", StringComparison.Ordinal) < tabla.IndexOf("Diario", StringComparison.Ordinal)
            && tabla.IndexOf("Diario", StringComparison.Ordinal) < tabla.IndexOf("Corte Z", StringComparison.Ordinal), tabla);
        Assert.Matches(@"Respaldar ahora \d+(\.\d)? (KB|MB) Restaurar este", tabla);

        // El más reciente: la advertencia, y el PIN.
        await pantalla.OprimirAsync(await pantalla.PorIdAsync("restaurar-0"));
        await pantalla.EsperarAsync(t => t.Contains("Tu PIN"), "la advertencia");
        var advertencia = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        Assert.Contains("Lo que se vendió o se cambió después de ese respaldo se pierde", advertencia);
        Assert.Contains("La base de ahora no se borra: se guarda aparte en la caja principal", advertencia);
        Assert.Contains("La caja principal se reinicia sola (unos segundos): mientras, las cajas venden sin conexión.", advertencia);
        Assert.Contains("Lo que las cajas tengan por subir sube después, ya al respaldo.", advertencia);
        // Lo que queda «con error» y el turno de después del respaldo, y que revisarlo tarda (revisión del PR #136).
        Assert.Contains("Lo que use algo que se dio de alta después del respaldo (un producto, un cliente, un usuario o una caja) no puede subir: "
            + "queda «con error» en esa caja, para revisarlo.", advertencia);
        Assert.Contains("la caja lo dice: primero se cierra ese turno y luego el suyo se da de alta solo.", advertencia);
        Assert.Contains("Antes de pedirlo, la caja principal revisa el respaldo completo: con una base grande tarda unos segundos.", advertencia);
        Assert.DoesNotContain("Eleventa no se toca", advertencia);

        // Otro PIN, con Enter: lo dice y deja escribir otra vez.
        await pantalla.EscribirAsync("restaurar-pin", "1111");
        await pantalla.TeclaAsync("restaurar-pin", "Enter");
        await pantalla.EsperarAsync(t => t.Contains("Ese no es tu PIN."), "el PIN equivocado");
        Assert.Equal(0, p.Reinicio.Veces);
        Assert.Equal("", (await pantalla.PorIdAsync("restaurar-pin")).Valor ?? "");

        await pantalla.EscribirAsync("restaurar-pin", "3333");
        await pantalla.OprimirAsync(await pantalla.PorIdAsync("restaurar-si"));
        await pantalla.EsperarAsync(t => t.Contains("La caja principal se está reiniciando"), "el reinicio");
        await p.Reinicio.Pedido.WaitAsync(TimeSpan.FromSeconds(20));

        // Windows la vuelve a arrancar: la pantalla lo nota sola y dice cómo quedó.
        await p.DetenerAsync();
        p.Arrancar();
        await pantalla.EsperarAsync(t => t.Contains("Se restauró el respaldo del"), "la restauración hecha");
        var hecha = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        var delRespaldo = HoraMexico.ALocal(File.GetLastWriteTimeUtc(Path.Combine(p.CarpetaRespaldos, masReciente)));
        Assert.Contains($"Se restauró el respaldo del {delRespaldo.ToString("d 'de' MMMM 'a las' HH:mm", Formato.Mexico)} (respaldar ahora)", hecha);
        Assert.Contains($"La base de antes quedó en {p.BaseDeAntes()}: no se borra.", hecha);
        Assert.Empty(pantalla.Errores);
    }

    /// <summary>
    /// Revisión del PR #136: quien restauró no está en el respaldo (se dio de alta después). La caja principal vuelve, pero ya no le
    /// contesta cómo quedó: la pantalla deja de esperar y dice qué pasó y qué hacer. Antes se quedaba diciendo que se estaba reiniciando,
    /// para siempre.
    /// </summary>
    [Fact]
    public async Task Si_quien_restauro_no_esta_en_el_respaldo_la_pantalla_deja_de_esperar_y_lo_dice()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var http = p.Arrancar();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var archivo = p.Respaldar(HoraMexico.ALocal(DateTime.UtcNow).AddMinutes(-30), "ahora-1010");
        // En el respaldo, ese usuario no está: como si se hubiera dado de alta después (lo que lo nombra se queda así).
        using (var conexionRespaldo = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(p.CarpetaRespaldos, archivo)};Pooling=False;Foreign Keys=False"))
        {
            conexionRespaldo.Open();
            using var comando = conexionRespaldo.CreateCommand();
            comando.CommandText = "UPDATE Usuarios SET Id = $otro WHERE Id = $suyo";
            comando.Parameters.AddWithValue("$otro", Guid.NewGuid().ToString().ToUpperInvariant());
            comando.Parameters.AddWithValue("$suyo", dueno.UsuarioId.ToString().ToUpperInvariant());
            Assert.Equal(1, comando.ExecuteNonQuery());
        }
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(new HttpClient(p.ParaLasCajas, disposeHandler: false), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .BuildServiceProvider();
        await using var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<Configuracion>(new()
        {
            [nameof(Configuracion.Caja)] = caja,
            [nameof(Configuracion.Sesion)] = dueno,
            [nameof(Configuracion.AlCambiar)] = new EventCallback(null, (Action)(() => { })),
            [nameof(Configuracion.AlNoGuardar)] = new EventCallback<string>(null, (Action<string>)(_ => { })),
        });
        await pantalla.EsperarAsync(t => !t.Contains("Cargando…"), "lo guardado");
        await pantalla.OprimirAsync(await pantalla.ElementoAsync(
            e => e.Atributos.GetValueOrDefault("role") as string == "tab" && PantallaSinNavegador.Limpio(e.Texto) == "Respaldos", "el grupo «Respaldos»"));
        await pantalla.OprimirAsync(await pantalla.PorIdAsync("aj-restaurar"));
        await pantalla.EsperarAsync(t => t.Contains("Restaurar este"), "la lista de los respaldos");
        await pantalla.OprimirAsync(await pantalla.PorIdAsync("restaurar-0"));
        await pantalla.EsperarAsync(t => t.Contains("Tu PIN"), "la advertencia");
        await pantalla.EscribirAsync("restaurar-pin", "3333");
        await pantalla.OprimirAsync(await pantalla.PorIdAsync("restaurar-si"));
        await pantalla.EsperarAsync(t => t.Contains("La caja principal se está reiniciando"), "el reinicio");
        await p.Reinicio.Pedido.WaitAsync(TimeSpan.FromSeconds(20));

        await p.DetenerAsync();
        p.Arrancar();
        await pantalla.EsperarAsync(t => t.Contains("La caja principal ya volvió, pero no contestó cómo quedó la restauración:"), "que ya volvió");
        var texto = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        Assert.Contains("Si tu usuario se dio de alta después del respaldo, ya no está en él: sal y entra con otro.", texto);
        Assert.DoesNotContain("La caja principal se está reiniciando", texto);
        Assert.Empty(pantalla.Errores);
    }
}
