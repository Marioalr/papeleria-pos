using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La pantalla «Usuarios y permisos» (entrega 1) contra el servicio de verdad, sin navegador: lo que enseña, como en el
/// prototipo, y que cada clic se guarda al momento.
/// </summary>
public sealed class UsuariosYPermisosTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
#pragma warning disable BL0006 // El renderizador de las pruebas: sin navegador.
    /// <summary>Dibuja un componente y deja leer su texto y oprimir sus botones, como lo haría quien la usa.</summary>
    private sealed record Control(string Elemento, string Texto, string? Clase, ulong Clic, ulong Cambio);

    /// <summary>Sin navegador: lo que la pantalla le pida a la página (subir un logotipo, el foco) no hace nada.</summary>
    private sealed class SinNavegador : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }

    private sealed class Pantalla(IServiceProvider servicios) : Renderer(servicios, NullLoggerFactory.Instance)
    {
        private int _raiz;

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public Task AbrirAsync<T>(Dictionary<string, object?> parametros) where T : IComponent => Dispatcher.InvokeAsync(async () =>
        {
            _raiz = AssignRootComponentId(InstantiateComponent(typeof(T)));
            await RenderRootComponentAsync(_raiz, ParameterView.FromDictionary(parametros));
        });

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch lote) => Task.CompletedTask;

        private RenderTreeFrame[] Marcos()
        {
            var marcos = GetCurrentRenderTreeFrames(_raiz);
            return marcos.Array[..marcos.Count];
        }

        /// <summary>El texto de un marco: el de texto tal cual y el de marcado sin etiquetas.</summary>
        private static string? TextoDe(RenderTreeFrame m) => m.FrameType switch
        {
            RenderTreeFrameType.Text => m.TextContent,
            RenderTreeFrameType.Markup => WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(m.MarkupContent, "<[^>]*>", "")),
            _ => null,
        };

        /// <summary>Todo el texto de la pantalla; cada elemento empieza renglón.</summary>
        public string Texto()
        {
            var texto = new StringBuilder();
            foreach (var m in Marcos())
            {
                if (m.FrameType == RenderTreeFrameType.Element)
                    texto.Append('\n');
                else if (TextoDe(m) is { } t)
                    texto.Append(t);
            }
            return texto.ToString();
        }

        /// <summary>Los botones y los campos, en orden, con su texto, su clase y los manejadores de su clic y de su cambio.</summary>
        public List<Control> Controles()
        {
            var marcos = Marcos();
            var controles = new List<Control>();
            for (var i = 0; i < marcos.Length; i++)
            {
                var m = marcos[i];
                if (m.FrameType != RenderTreeFrameType.Element || m.ElementName is not ("button" or "input" or "select"))
                    continue;
                string? clase = null;
                ulong clic = 0, cambio = 0;
                var j = i + 1;
                // Sus atributos van primero; después, lo que tiene adentro.
                for (; j < i + m.ElementSubtreeLength && marcos[j].FrameType == RenderTreeFrameType.Attribute; j++)
                {
                    var a = marcos[j];
                    if (a.AttributeName == "onclick")
                        clic = a.AttributeEventHandlerId;
                    else if (a.AttributeName == "onchange")
                        cambio = a.AttributeEventHandlerId;
                    else if (a.AttributeName == "class")
                        clase = a.AttributeValue?.ToString();
                }
                var texto = new StringBuilder();
                for (; j < i + m.ElementSubtreeLength; j++)
                {
                    if (TextoDe(marcos[j]) is { } t)
                        texto.Append(t);
                }
                controles.Add(new Control(m.ElementName, texto.ToString().Trim(), clase, clic, cambio));
            }
            return controles;
        }

        public Task OprimirAsync(ulong manejador) => Dispatcher.InvokeAsync(() => DispatchEventAsync(manejador, null, new MouseEventArgs()));

        public Task CambiarAsync(ulong manejador, string valor) =>
            Dispatcher.InvokeAsync(() => DispatchEventAsync(manejador, null, new ChangeEventArgs { Value = valor }));
    }
#pragma warning restore BL0006

    private async Task<(Pantalla Pantalla, CajaDto Caja, SesionDto Sesion)> AbrirAsync(string usuario, string pin) =>
        await AbrirAsync<UsuariosYPermisos>(usuario, pin);

    private async Task<(Pantalla Pantalla, CajaDto Caja, SesionDto Sesion)> AbrirAsync<T>(string usuario, string pin) where T : IComponent
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var sesion = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin))).Content.ReadFromJsonAsync<SesionDto>())!;
        var servicios = new ServiceCollection()
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id }))
            .AddSingleton<PapeleriaPos.Impresion.IImpresora>(new PapeleriaPos.Impresion.SinImpresora())
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new SinNavegador())
            .BuildServiceProvider();
        var pantalla = new Pantalla(servicios);
        await pantalla.AbrirAsync<T>(new() { ["Caja"] = caja, ["Sesion"] = sesion });
        return (pantalla, caja, sesion);
    }

    /// <summary>En la Configuración de antes, «Usuarios y permisos» toma el lugar de «Reglas», y solo para quien lo tiene.</summary>
    [Theory]
    [InlineData("dueno", "3333", true)]
    [InlineData("encargado", "2222", false)]
    [InlineData("cajero", "1111", false)]
    public async Task La_pestana_de_usuarios_y_permisos_es_de_quien_lo_tiene(string usuario, string pin, bool laVe)
    {
        var (p, _, _) = await AbrirAsync<Configuracion>(usuario, pin);

        var pestanas = p.Controles().Where(c => c.Clase?.StartsWith("pestana") == true).Select(c => c.Texto).ToList();
        Assert.Equal(laVe, pestanas.Contains("Usuarios y permisos"));
        Assert.DoesNotContain("Reglas", pestanas);
        Assert.Contains("Bitácora", pestanas);
    }

    /// <summary>El botón cuyo texto es ese.</summary>
    private static Control Boton(Pantalla p, string texto) => p.Controles().First(c => c.Elemento == "button" && c.Texto == texto);

    /// <summary>El de la lista de la izquierda que dice ese nombre.</summary>
    private static Control DeLaLista(Pantalla p, string nombre) => p.Controles().First(c => c.Elemento == "button" && c.Texto.Contains(nombre));

    /// <summary>Los tres botones de «Puede / Con autorización / No» del renglón de esa acción (los renglones van en el orden de la pantalla).</summary>
    /// <summary>Los botones del renglón de una acción: tres, o dos en Administración (solo «Puede» y «No»).</summary>
    private static List<Control> Segmento(Pantalla p, AccionControlada accion)
    {
        static int Botones(AccionControlada a) => Permisos.SoloPuedeONo(a) ? 2 : 3;
        var antes = Permisos.EnPantalla.TakeWhile(a => a != accion).Sum(Botones);
        return [.. p.Controles().Where(c => c.Elemento == "button" && c.Texto is "Puede" or "Con autorización" or "No").Skip(antes).Take(Botones(accion))];
    }

    /// <summary>Los campos del límite («hasta $…», «hasta …%»), en el orden de la pantalla.</summary>
    private static List<Control> Limites(Pantalla p) => [.. p.Controles().Where(c => c.Elemento == "input" && c.Cambio != 0)];

    [Fact]
    public async Task Ensena_la_leyenda_los_usuarios_y_los_de_defecto_por_grupos_como_el_prototipo()
    {
        var (p, _, _) = await AbrirAsync("dueno", "3333");

        var texto = p.Texto();
        Assert.Contains("Sin roles: cada usuario tiene sus permisos. Lo que cambies aquí cambia la caja al momento.", texto);
        Assert.Contains("Se guarda solo", texto);
        Assert.Contains("se ve con candado y el nombre de quien autoriza", texto);
        Assert.Contains("Permisos por defecto", texto);
        Assert.Contains("Los recibe cada usuario nuevo", texto);
        Assert.Contains("Dueño de prueba", texto);
        Assert.Contains("Todos los permisos · primera cuenta", texto);
        Assert.Contains("Corte de turno, productos y clientes", texto);
        Assert.Contains("Nuevo usuario", texto);
        Assert.Contains("Al instalar se ponen estos (los de un cajero). Cambiarlos no toca a los usuarios que ya existen.", texto);
        foreach (var grupo in new[] { "Venta", "Efectivo y corte", "Inventario", "Clientes y crédito", "Reportes", "Administración" })
            Assert.Contains($"\n{grupo}", texto);
        // Un renglón por acción de la pantalla, cada uno con sus tres niveles (en Administración solo «Puede» y «No», dueño
        // 2026-10-15), y el elegido marcado.
        var administracion = Permisos.EnPantalla.Count(Permisos.SoloPuedeONo);
        Assert.Equal(Permisos.EnPantalla.Count * 3 - administracion, p.Controles().Count(c => c.Texto is "Puede" or "Con autorización" or "No"));
        Assert.Equal(["Puede", "No"], Segmento(p, AccionControlada.ConfigurarTicket).Select(b => b.Texto));
        Assert.Equal("on p", Segmento(p, AccionControlada.Descuento)[0].Clase);
        Assert.Equal("on a", Segmento(p, AccionControlada.CambiarPrecio)[1].Clase);
        Assert.Equal("on n", Segmento(p, AccionControlada.CorteZ)[2].Clase);
        // El límite del descuento en por ciento y el de la devolución en pesos.
        Assert.Contains("hasta %", texto.Replace("\n", ""));
        Assert.Contains("hasta $", texto.Replace("\n", ""));
        // En los de defecto no van «Todos», «Cambiar PIN» ni «Copiar…».
        Assert.DoesNotContain(p.Controles(), c => c.Texto is "Todos" or "Cambiar PIN");
    }

    [Fact]
    public async Task Sin_el_permiso_dice_quien_lo_tiene()
    {
        var (p, _, _) = await AbrirAsync("encargado", "2222");

        Assert.Contains("Esto lo hace quien tiene el permiso «Usuarios y permisos»: Dueño de prueba.", p.Texto());
        Assert.DoesNotContain(p.Controles(), c => c.Texto == "Puede");
    }

    [Fact]
    public async Task Cada_clic_se_guarda_al_momento_y_dice_a_que_hora()
    {
        // Uno para esta prueba: los de prueba se quedan como están para las demás.
        var http = servicio.Fabrica.CreateClient();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        (await http.PostAsJsonAsync($"api/cajas/{caja.Id}/usuarios-y-permisos/usuarios", new SolicitudNuevoUsuario(dueno.UsuarioId, "Beto Clics", "5151")))
            .EnsureSuccessStatusCode();
        async Task<UsuarioYPermisosDto> BetoAsync() =>
            (await http.GetFromJsonAsync<UsuariosYPermisosDto>($"api/cajas/{caja.Id}/usuarios-y-permisos?usuarioId={dueno.UsuarioId}"))!
                .Usuarios.Single(u => u.Nombre == "Beto Clics");
        var (p, _, _) = await AbrirAsync("dueno", "3333");

        // Lo elige y le quita «Reimprimir»: se guarda solo, y la pantalla lo dice.
        await p.OprimirAsync(DeLaLista(p, "Beto Clics").Clic);
        Assert.Contains("el suyo es «beto»", p.Texto());
        await p.OprimirAsync(Segmento(p, AccionControlada.Reimprimir)[2].Clic);
        Assert.Contains("Guardado a las", p.Texto());
        Assert.Equal("on n", Segmento(p, AccionControlada.Reimprimir)[2].Clase);
        Assert.Equal(NivelPermiso.NoPermitido, (await BetoAsync()).Permisos.Single(x => x.Accion == AccionControlada.Reimprimir).Nivel);

        // «Todos», «Los de defecto» y el límite del descuento.
        await p.OprimirAsync(Boton(p, "Todos").Clic);
        Assert.Equal("Todos los permisos", (await BetoAsync()).Resumen);
        await p.OprimirAsync(Boton(p, "Los de defecto").Clic);
        Assert.Equal("Permisos por defecto", (await BetoAsync()).Resumen);
        await p.CambiarAsync(Limites(p)[0].Cambio, "15");
        Assert.Equal(1500, (await BetoAsync()).Permisos.Single(x => x.Accion == AccionControlada.Descuento).UmbralCentavos);
        // Un límite que no es número no se guarda, y lo dice.
        await p.CambiarAsync(Limites(p)[0].Cambio, "mucho");
        Assert.Contains("«mucho» no es un límite", p.Texto());
        Assert.Equal(1500, (await BetoAsync()).Permisos.Single(x => x.Accion == AccionControlada.Descuento).UmbralCentavos);
        // A «Con autorización» el campo se va; de vuelta en «Puede» regresa con su límite, no sin límite.
        var campos = Limites(p).Count;
        await p.OprimirAsync(Segmento(p, AccionControlada.Descuento)[1].Clic);
        Assert.Equal(campos - 1, Limites(p).Count);
        await p.OprimirAsync(Segmento(p, AccionControlada.Descuento)[0].Clic);
        Assert.Equal(campos, Limites(p).Count);
        Assert.Equal(new Permiso(AccionControlada.Descuento, NivelPermiso.Permitido, 1500),
            (await BetoAsync()).Permisos.Single(x => x.Accion == AccionControlada.Descuento));

        // «Copiar los permisos de…» el encargado de prueba.
        var encargado = (await http.GetFromJsonAsync<UsuariosYPermisosDto>($"api/cajas/{caja.Id}/usuarios-y-permisos?usuarioId={dueno.UsuarioId}"))!
            .Usuarios.Single(u => u.Usuario == "encargado");
        await p.CambiarAsync(p.Controles().First(c => c.Elemento == "select").Cambio, encargado.Id.ToString());
        Assert.Equal(encargado.Permisos, (await BetoAsync()).Permisos);

        // De baja y otra vez de alta.
        await p.OprimirAsync(Boton(p, "Dar de baja").Clic);
        Assert.False((await BetoAsync()).Activo);
        Assert.Contains("Está dado de baja: no entra ni autoriza.", p.Texto());
        await p.OprimirAsync(Boton(p, "Volver a dar de alta").Clic);
        Assert.True((await BetoAsync()).Activo);
    }

    [Fact]
    public async Task Un_usuario_nuevo_con_su_pin_dos_veces_y_el_que_no_coincide_se_dice()
    {
        var (p, _, _) = await AbrirAsync("dueno", "3333");

        // Cada vez que se vuelve a dibujar, los manejadores cambian: se buscan otra vez, como el navegador.
        async Task EscribirAsync(int campo, string valor) =>
            await p.CambiarAsync(p.Controles().Where(c => c.Elemento == "input").ElementAt(campo).Cambio, valor);

        await p.OprimirAsync(DeLaLista(p, "Nuevo usuario").Clic);
        Assert.Contains("Empieza con los permisos por defecto", p.Texto());
        Assert.Equal(3, p.Controles().Count(c => c.Elemento == "input"));
        await EscribirAsync(0, "Zoe Prueba");
        await EscribirAsync(1, "4321");
        await EscribirAsync(2, "4320");
        await p.OprimirAsync(Boton(p, "Dar de alta").Clic);
        Assert.Contains("Los dos PIN no son iguales: escríbelo otra vez.", p.Texto());

        await EscribirAsync(1, "4321");
        await EscribirAsync(2, "4321");
        await p.OprimirAsync(Boton(p, "Dar de alta").Clic);

        var texto = p.Texto();
        Assert.Contains("Guardado a las", texto);
        Assert.Contains("Zoe Prueba", texto);
        Assert.Contains("el suyo es «zoe»", texto);
        Assert.Contains(p.Controles(), c => c.Texto == "Dar de baja");

        // Su PIN, dos veces.
        await p.OprimirAsync(Boton(p, "Cambiar PIN").Clic);
        await EscribirAsync(0, "1234");
        await EscribirAsync(1, "1234");
        await p.OprimirAsync(p.Controles().Last(c => c.Texto == "Cambiar PIN").Clic);
        Assert.DoesNotContain("no son iguales", p.Texto());
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var fichas = (await http.GetFromJsonAsync<List<FichaUsuarioDto>>($"api/cajas/{caja.Id}/usuarios"))!;
        Assert.True((await http.PostAsJsonAsync($"api/cajas/{caja.Id}/sesion",
            new SolicitudEntradaFicha(fichas.Single(f => f.Nombre == "Zoe Prueba").UsuarioId, "1234"))).IsSuccessStatusCode);
    }
}
