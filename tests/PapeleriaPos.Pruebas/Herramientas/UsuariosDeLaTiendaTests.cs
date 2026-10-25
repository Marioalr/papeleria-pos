using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Herramientas.Usuarios;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Pruebas.Datos;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>
/// Los usuarios al instalar y para recuperar la entrada, desde la línea de comandos (el piloto alfa, 2026-10-25):
/// <c>importar-eleventa --duenio</c> deja al dueño con todos los permisos y su PIN y a los demás sin PIN, y <c>pin-usuario</c>
/// le pone el PIN a alguien con la misma regla que «Usuarios y permisos».
/// </summary>
public sealed class UsuariosDeLaTiendaTests : IAsyncDisposable
{
    private BaseEnMemoria? _base;

    public async ValueTask DisposeAsync()
    {
        if (_base is not null)
            await _base.DisposeAsync();
    }

    private static readonly List<FilaUsuario> DeEleventa =
    [
        new(3, "cajera2", "María López", Activo: true, Eliminado: false),
        new(7, "luis", "Luis Pérez", Activo: true, Eliminado: false),
        new(4, "pedro", "Pedro Viejo", Activo: false, Eliminado: true),
    ];

    /// <summary>Una base como la deja importar-eleventa con esos usuarios (y el dueño, si se dice).</summary>
    private async Task<(BaseEnMemoria Base, Guid Matriz)> ImportadaAsync(FilaUsuario? duenio, string? pin)
    {
        _base = await BaseEnMemoria.CrearAsync();
        var datos = ImportadorEleventa.Importar(new DatosEleventa { Departamentos = [new(2, "Papelería", true)], Usuarios = DeEleventa });
        await using (var db = _base.Contexto())
            await GuardadoLote.GuardarAsync(datos, db);
        await using (var db = _base.Contexto())
            await UsuariosDeLaTienda.ImportarAsync(db, datos.Matriz.Id, DeEleventa, duenio, pin);
        return (_base, datos.Matriz.Id);
    }

    /// <summary>Contesta lo que se le diga, en orden; y anota lo que se preguntó.</summary>
    private sealed class Teclado(params string?[] respuestas)
    {
        private int _siguiente;
        public List<string> Preguntas { get; } = [];

        public string? Pedir(string pregunta)
        {
            Preguntas.Add(pregunta);
            return _siguiente < respuestas.Length ? respuestas[_siguiente++] : null;
        }
    }

    [Theory]
    [InlineData("luis", 7)]
    [InlineData("LUIS", 7)]
    [InlineData("7", 7)]
    [InlineData(" cajera2 ", 3)]
    public void El_duenio_se_elige_por_su_usuario_o_su_numero_de_eleventa(string texto, int esperado)
    {
        var (fila, error) = UsuariosDeLaTienda.Elegir(DeEleventa, texto);

        Assert.Null(error);
        Assert.Equal(esperado, fila!.Id);
    }

    [Fact]
    public void El_duenio_tiene_que_ser_un_usuario_activo_de_eleventa()
    {
        Assert.Contains("dado de baja o borrado", UsuariosDeLaTienda.Elegir(DeEleventa, "pedro").Error);
        Assert.Contains("no hay ningún usuario «ana»", UsuariosDeLaTienda.Elegir(DeEleventa, "ana").Error);
        // La lista para elegir: número, usuario y nombre de los activos; nada más.
        Assert.Equal(2, UsuariosDeLaTienda.Lista(DeEleventa).Count());
        Assert.All(UsuariosDeLaTienda.Lista(DeEleventa), l => Assert.DoesNotContain("Pedro", l));
    }

    [Fact]
    public async Task La_base_nueva_deja_al_duenio_con_todos_los_permisos_y_su_pin_y_a_los_demas_sin_pin()
    {
        var (b, _) = await ImportadaAsync(DeEleventa[1], "4821");

        await using var db = b.Contexto();
        var luis = await db.Usuarios.SingleAsync(u => u.EleventaId == 7);
        Assert.Equal(ResultadoPin.Correcto, luis.VerificarPin("4821", DateTime.UtcNow));
        Assert.True(luis.PrimeraCuenta);
        Assert.Equal(Rol.Dueno, luis.Rol);
        Assert.True((await new ServicioControl(db).PermisosAsync(luis.Id)).TieneTodos);
        Assert.True(await UsuariosDeLaTienda.TieneDuenioAsync(db));
        // María llegó sin PIN y con los permisos por defecto; Pedro (borrado en eleventa) no llegó.
        var maria = await db.Usuarios.SingleAsync(u => u.EleventaId == 3);
        Assert.False(maria.TienePin);
        Assert.Equal("Permisos por defecto",
            FichasEntrada.Resumen(await new ServicioControl(db).PermisosAsync(maria.Id), await new ServicioControl(db).DefectoAsync()));
        Assert.False(await db.Usuarios.AnyAsync(u => u.EleventaId == 4));
        Assert.Contains(await db.Bitacora.Select(x => x.Detalle).ToListAsync(), d => d.StartsWith("Dueño de la tienda al importar eleventa"));
    }

    [Fact]
    public async Task Sin_duenio_nadie_podria_entrar()
    {
        var (b, _) = await ImportadaAsync(duenio: null, pin: null);

        await using var db = b.Contexto();
        Assert.False(await UsuariosDeLaTienda.TieneDuenioAsync(db));
        Assert.Contains("--duenio", UsuariosDeLaTienda.FaltaElDuenio);
    }

    [Fact]
    public async Task Repetir_la_importacion_no_le_cambia_el_pin_ni_los_permisos_a_nadie()
    {
        var (b, matriz) = await ImportadaAsync(DeEleventa[1], "4821");
        await using (var db = b.Contexto())
        {
            var maria = await db.Usuarios.SingleAsync(u => u.EleventaId == 3);
            maria.EstablecerPin("1111");
            await db.SaveChangesAsync();
        }

        // La importación final, sin --duenio (la base ya tiene dueño).
        await using (var db = b.Contexto())
        {
            var (r, duenio) = await UsuariosDeLaTienda.ImportarAsync(db, matriz, DeEleventa, null, null);
            Assert.False(r.HuboCambios);
            Assert.Null(duenio);
        }

        await using var datos = b.Contexto();
        Assert.Equal(2, await datos.Usuarios.CountAsync());
        Assert.Equal(ResultadoPin.Correcto, (await datos.Usuarios.SingleAsync(u => u.EleventaId == 7)).VerificarPin("4821", DateTime.UtcNow));
        Assert.Equal(ResultadoPin.Correcto, (await datos.Usuarios.SingleAsync(u => u.EleventaId == 3)).VerificarPin("1111", DateTime.UtcNow));
    }

    [Fact]
    public async Task Con_duenio_otro_duenio_no_se_pone_por_la_linea_de_comandos()
    {
        var (b, matriz) = await ImportadaAsync(DeEleventa[1], "4821");

        await using var db = b.Contexto();
        // El mismo: no hay nada que hacer. Otro: no se le dan todos los permisos sin entrar a «Usuarios y permisos».
        Assert.Null(await UsuariosDeLaTienda.OtroDuenioAsync(db, matriz, DeEleventa[1]));
        Assert.Contains("ya tiene dueño", await UsuariosDeLaTienda.OtroDuenioAsync(db, matriz, DeEleventa[0]));
    }

    [Fact]
    public void El_pin_se_pide_dos_veces_con_la_regla_de_usuarios_y_permisos()
    {
        var errores = new StringWriter();
        var teclado = new Teclado("12345", "12345", "1234", "1235", "4821", "4821");

        Assert.Equal("4821", UsuariosDeLaTienda.PedirPin("Luis Pérez", teclado.Pedir, errores));

        Assert.Equal(["El PIN es de 4 cifras.", "Los dos PIN no son iguales: escríbelo otra vez."], Renglones(errores));
        Assert.Equal(6, teclado.Preguntas.Count);
        Assert.StartsWith("PIN de Luis Pérez (4 cifras)", teclado.Preguntas[0]);
    }

    [Fact]
    public void Sin_teclado_o_sin_un_pin_bueno_no_se_pone()
    {
        var sinTeclado = new StringWriter();
        Assert.Null(UsuariosDeLaTienda.PedirPin("Luis", new Teclado().Pedir, sinTeclado));
        Assert.Equal([UsuariosDeLaTienda.SinTeclado], Renglones(sinTeclado));

        var malos = new StringWriter();
        Assert.Null(UsuariosDeLaTienda.PedirPin("Luis", new Teclado("abcd", "abcd").Pedir, malos, intentos: 1));
        Assert.Equal(["El PIN es de 4 cifras.", "No se puso el PIN."], Renglones(malos));
    }

    [Fact]
    public async Task Pin_usuario_le_pone_el_pin_a_quien_vino_de_eleventa_y_queda_en_la_bitacora_sin_el_pin()
    {
        var (b, _) = await ImportadaAsync(DeEleventa[1], "4821");
        var (salida, errores) = (new StringWriter(), new StringWriter());

        await using (var db = b.Contexto())
            Assert.Equal(0, await UsuariosDeLaTienda.PinUsuarioAsync(db, "3", new Teclado("2468", "2468").Pedir, 3, salida, errores));

        Assert.Empty(errores.ToString());
        Assert.Contains("María López («maria», número 3 de eleventa): todavía no tiene PIN.", salida.ToString());
        Assert.Contains("Listo: María López entra con su ficha y ese PIN", salida.ToString());
        await using var datos = b.Contexto();
        Assert.Equal(ResultadoPin.Correcto, (await datos.Usuarios.SingleAsync(u => u.EleventaId == 3)).VerificarPin("2468", DateTime.UtcNow));
        var registro = await datos.Bitacora.Where(x => x.Detalle.Contains("pin-usuario")).Select(x => x.Detalle).SingleAsync();
        Assert.Equal("Se le puso PIN a María López con pin-usuario (en la PC del servicio)", registro);
        Assert.DoesNotContain(await datos.Bitacora.Select(x => x.Detalle).ToListAsync(), d => d.Contains("2468"));
    }

    [Fact]
    public async Task Pin_usuario_no_pone_un_pin_que_la_pantalla_no_aceptaria()
    {
        var (b, _) = await ImportadaAsync(DeEleventa[1], "4821");
        var (salida, errores) = (new StringWriter(), new StringWriter());

        await using (var db = b.Contexto())
            Assert.Equal(1, await UsuariosDeLaTienda.PinUsuarioAsync(db, "maria", new Teclado("12a4", "12a4", "123", "123", "1234", "4321").Pedir, 3,
                salida, errores));

        Assert.Equal(["El PIN es de 4 cifras.", "El PIN es de 4 cifras.", "Los dos PIN no son iguales: escríbelo otra vez.", "No se puso el PIN."],
            Renglones(errores));
        await using var datos = b.Contexto();
        Assert.False((await datos.Usuarios.SingleAsync(u => u.EleventaId == 3)).TienePin);
    }

    [Fact]
    public async Task Pin_usuario_busca_por_su_nombre_su_usuario_o_su_numero_de_eleventa_y_si_no_dice_quienes_hay()
    {
        var (b, _) = await ImportadaAsync(DeEleventa[1], "4821");

        await using var db = b.Contexto();
        Assert.Equal(3, (await UsuariosDeLaTienda.BuscarAsync(db, "3")).Usuario!.EleventaId);
        Assert.Equal(3, (await UsuariosDeLaTienda.BuscarAsync(db, "MARIA")).Usuario!.EleventaId);
        Assert.Equal(3, (await UsuariosDeLaTienda.BuscarAsync(db, "maria lopez")).Usuario!.EleventaId);
        Assert.Equal(7, (await UsuariosDeLaTienda.BuscarAsync(db, "Luis Pérez")).Usuario!.EleventaId);

        var (salida, errores) = (new StringWriter(), new StringWriter());
        Assert.Equal(1, await UsuariosDeLaTienda.PinUsuarioAsync(db, "ana", new Teclado().Pedir, 3, salida, errores));
        var lista = errores.ToString();
        Assert.Contains("No hay ningún usuario «ana».", lista);
        Assert.Contains("«maria», número 3 de eleventa, sin PIN", lista);
        Assert.Contains("«luis», número 7 de eleventa", lista);
        // Nunca el PIN ni su hash.
        Assert.DoesNotContain("pbkdf2", lista);
        Assert.DoesNotContain("4821", lista);
    }

    private static List<string> Renglones(StringWriter escrito) =>
        escrito.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
}
