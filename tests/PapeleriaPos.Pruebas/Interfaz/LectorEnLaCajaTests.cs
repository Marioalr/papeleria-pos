using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El lector por puerto COM en la pantalla (piloto alfa): el chip de arriba solo cuando hay un problema («Lector ocupado: ¿eleventa
/// abierto?», «Lector sin conectar»), con lo que dice completo en su aria-label y la explicación en su title, como los demás avisos de
/// la barra; y Configuración › Acerca de › Lector con cómo está, los puertos de la PC y lo último que leyó, con sus bytes.
/// </summary>
public sealed class LectorEnLaCajaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"lector-caja-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_carpeta))
                Directory.Delete(_carpeta, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Lo quita la siguiente limpieza de la carpeta temporal.
        }
    }

    private async Task<(CajaDto Caja, SesionDto Sesion, ServiceProvider Servicios)> PrepararAsync(LectorPorPuerto? lector, string usuario = "cajero", string pin = "1111")
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var sesion = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin))).Content.ReadFromJsonAsync<SesionDto>())!;
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .AddSingleton(new RegistroDiario(_carpeta, "caja"));
        if (lector is not null)
            servicios.AddSingleton(lector);
        return (caja, sesion, servicios.BuildServiceProvider());
    }

    private static async Task<ElementoDibujado?> ChipDelLectorAsync(PantallaSinNavegador pantalla) =>
        (await pantalla.LeerAsync()).Elementos.FirstOrDefault(e =>
            e.Clase?.StartsWith("vn-chip-estado", StringComparison.Ordinal) == true && e.Texto.Contains("Lector", StringComparison.Ordinal));

    [Fact]
    public async Task El_chip_de_arriba_dice_ocupado_o_sin_conectar_y_se_quita_cuando_el_lector_esta_listo()
    {
        var puertos = new PuertosDeMentira { AlAbrir = PuertosDeMentira.Ocupado };
        await using var lector = new LectorPorPuerto(new AjustesLector("COM3"), null, puertos, reintento: TimeSpan.FromMilliseconds(30));
        var (caja, sesion, servicios) = await PrepararAsync(lector);
        await using var _ = servicios;
        await using var arriba = new PantallaSinNavegador(servicios);
        await arriba.AbrirAsync<EstadoCaja>(new() { [nameof(EstadoCaja.Caja)] = caja, [nameof(EstadoCaja.Sesion)] = sesion });
        Assert.Null(await ChipDelLectorAsync(arriba));

        // eleventa abierto en la caja 2 tiene el puerto.
        lector.Iniciar();
        await arriba.EsperarAsync(t => t.Contains("Lector ocupado", StringComparison.Ordinal), "el chip de ocupado");
        var ocupado = (await ChipDelLectorAsync(arriba))!;
        Assert.Equal("Lector ocupado: ¿eleventa abierto?", PantallaSinNavegador.Limpio(ocupado.Texto));
        Assert.Equal("vn-chip-estado aviso", ocupado.Clase);
        // Si arriba no cabe todo, se corta el final de su texto (caja.css): completo en su aria-label, y en su title qué pasa.
        Assert.Equal("Lector ocupado: ¿eleventa abierto?", ocupado.Atributos["aria-label"]?.ToString());
        Assert.StartsWith("El lector de códigos (COM3) lo tiene abierto otro programa, quizá eleventa. Mientras, escribe los códigos a mano. "
            + "La caja lo toma sola en cuanto se suelte", ocupado.Atributos["title"]?.ToString());

        // Se cierra eleventa: la caja toma el puerto y el chip se quita solo.
        puertos.AlAbrir = () => null;
        await arriba.EsperarAsync(t => !t.Contains("Lector", StringComparison.Ordinal), "que se quite el chip");

        // Se desconecta el lector USB: «Lector sin conectar», hasta que regresa.
        puertos.Quitar("COM3");
        puertos.AlAbrir = PuertosDeMentira.NoExiste;
        puertos.Abierto!.Desconectar();
        await arriba.EsperarAsync(t => t.Contains("Lector sin conectar", StringComparison.Ordinal), "el chip de sin conectar");
        var sinConectar = (await ChipDelLectorAsync(arriba))!;
        Assert.Equal("Lector sin conectar", sinConectar.Atributos["aria-label"]?.ToString());
        Assert.StartsWith("No se encuentra el lector de códigos en COM3: está desconectado o en otro puerto.", sinConectar.Atributos["title"]?.ToString());
        puertos.Poner("COM3");
        puertos.AlAbrir = () => null;
        await arriba.EsperarAsync(t => !t.Contains("Lector", StringComparison.Ordinal), "que se quite otra vez");
        Assert.Empty(arriba.Errores);
    }

    [Fact]
    public async Task Acerca_de_dice_como_esta_el_lector_los_puertos_de_la_PC_y_lo_ultimo_que_leyo_con_sus_bytes()
    {
        var puertos = new PuertosDeMentira();
        await using var lector = new LectorPorPuerto(new AjustesLector("COM3") { Fin = FinDelCodigo.CR }, null, puertos, reintento: TimeSpan.FromMilliseconds(30));
        var (caja, sesion, servicios) = await PrepararAsync(lector, "dueno", "3333");
        await using var _ = servicios;
        await using var pantalla = await AbrirAcercaDeAsync(caja, sesion, servicios);
        lector.Iniciar();
        await pantalla.EsperarAsync(t => t.Contains("Listo desde las", StringComparison.Ordinal), "el lector listo");

        var texto = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        Assert.Contains("Lector El lector de códigos por puerto COM de esta caja: cómo está, los puertos de la PC y lo último que leyó, con sus bytes.", texto);
        Assert.Contains("En caja.json: COM3 a 9,600 baudios (8N1), el código termina con CR.", texto);
        Assert.Contains("no llegó ningún código. Escanea un código: aquí sale lo que llegó, con sus bytes.", texto);
        Assert.Contains("Puertos COM en esta PC: COM1, COM3.", texto);
        Assert.Contains($"queda en el registro de la caja: {_carpeta} (caja-AAAA-MM-DD.log).", texto);

        // Un código, y algo que llegó a otra velocidad (basura): los dos, con sus bytes, lo que tardaron y con qué terminaron.
        puertos.Abierto!.Mandar("7501234567890\r\n");
        puertos.Abierto.Mandar(0xF8, 0x00, 0x0D);
        await pantalla.EsperarAsync(t => t.Contains("se descartó", StringComparison.Ordinal), "las dos lecturas");
        var tabla = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        Assert.Contains("HoraLlegóCódigoTardóTerminó conFue", tabla);
        Assert.Contains("7501234567890<CR><LF> 7501234567890", tabla);
        Assert.Contains("CRLF", tabla);
        Assert.Contains("<F8><00><CR> se descartó: trae caracteres que no son de un código", tabla);
        Assert.Contains("llegó 1 código y se descartó 1 lectura que no era un código.", tabla);
        var crudo = await pantalla.ElementoAsync(e => e.Etiqueta == "code" && e.Texto.Contains("7501234567890", StringComparison.Ordinal), "los bytes del código");
        Assert.Equal("37 35 30 31 32 33 34 35 36 37 38 39 30 0D 0A", crudo.Atributos["title"]?.ToString());

        // Lo que hizo la pantalla con él (la entrega lo dice).
        lector.Entregado(lector.Ultimas.Single(l => l.Codigo is not null), EntregaDelLector.Describir("sin-lugar:Motivo").Texto, usada: false);
        await pantalla.EsperarAsync(t => t.Contains("que no es para códigos", StringComparison.Ordinal), "a dónde fue");
        Assert.Contains("no se usó: el cursor estaba en «Motivo», que no es para códigos", PantallaSinNavegador.Limpio(await pantalla.TextoAsync()));

        // eleventa lo abre: ocupado, y que se vuelve a intentar solo.
        puertos.AlAbrir = PuertosDeMentira.Ocupado;
        puertos.Abierto.Desconectar();
        await pantalla.EsperarAsync(t => t.Contains("Ocupado desde las", StringComparison.Ordinal), "ocupado");
        Assert.Contains("otro programa tiene abierto COM3 (¿eleventa?). Se vuelve a intentar cada 0 s: al cerrarlo, la caja lo toma sola.",
            PantallaSinNavegador.Limpio(await pantalla.TextoAsync()));
        Assert.Empty(pantalla.Errores);
    }

    [Fact]
    public async Task Sin_Lector_en_caja_json_Acerca_de_dice_los_puertos_y_en_el_navegador_no_sale()
    {
        await using var lector = new LectorPorPuerto(null, null, new PuertosDeMentira());
        var (caja, sesion, servicios) = await PrepararAsync(lector, "dueno", "3333");
        await using (servicios)
        await using (var pantalla = await AbrirAcercaDeAsync(caja, sesion, servicios))
        {
            await pantalla.EsperarAsync(t => t.Contains("Puertos COM en esta PC", StringComparison.Ordinal), "el lector");
            var texto = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
            Assert.Contains("Esta caja no tiene lector por puerto («Lector» en caja.json): el lector se lee como teclado, como siempre.", texto);
            Assert.Contains("Puertos COM en esta PC: COM1, COM3.", texto);
        }

        // La caja del navegador (sin la ventana de Windows) no tiene lector por puerto: Acerca de no lo enseña.
        var (_, _, sinVentana) = await PrepararAsync(null, "dueno", "3333");
        await using (sinVentana)
        await using (var pantalla = await AbrirAcercaDeAsync(caja, sesion, sinVentana))
        {
            await pantalla.EsperarAsync(t => t.Contains("Escanear", StringComparison.Ordinal), "Acerca de");
            Assert.DoesNotContain("puerto COM", await pantalla.TextoAsync());
        }
    }

    private static async Task<PantallaSinNavegador> AbrirAcercaDeAsync(CajaDto caja, SesionDto sesion, ServiceProvider servicios)
    {
        var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<Configuracion>(new()
        {
            [nameof(Configuracion.Caja)] = caja,
            [nameof(Configuracion.Sesion)] = sesion,
            [nameof(Configuracion.AlCambiar)] = new EventCallback(null, (Action)(() => { })),
            [nameof(Configuracion.AlNoGuardar)] = new EventCallback<string>(null, (Action<string>)(_ => { })),
        });
        await pantalla.EsperarAsync(t => !t.Contains("Cargando…"), "lo guardado");
        await pantalla.OprimirAsync(await pantalla.PorIdAsync("cfg-acerca"));
        return pantalla;
    }
}
