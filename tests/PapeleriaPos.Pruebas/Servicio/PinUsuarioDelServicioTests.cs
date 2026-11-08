using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// pin-usuario desde el programa del servicio (revisión del PR #144): el paquete de la tienda no trae las herramientas, y en la caja 2
/// el dueño que olvidó su PIN se lo pone con <c>PapeleriaPos.Servicio.exe pin-usuario --usuario …</c>, en la base de la configuración
/// del servicio, con la misma regla que «Usuarios y permisos».
/// </summary>
public sealed class PinUsuarioDelServicioTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"pin-usuario-{Guid.NewGuid():N}.db");

    private string Cadena => ArchivosDePrueba.Cadena(_ruta);

    public void Dispose() => ArchivosDePrueba.BorrarBase(_ruta);

    private static Func<string, string?> Teclado(params string[] respuestas)
    {
        var cola = new Queue<string>(respuestas);
        return _ => cola.Count > 0 ? cola.Dequeue() : null;
    }

    private static List<string> Renglones(StringWriter escrito) =>
        escrito.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();

    [Fact]
    public async Task Con_la_base_del_servicio_le_cambia_el_pin_al_duenio_y_queda_en_la_bitacora_sin_el_pin()
    {
        await using (var db = new PosDbContext(BaseDeDatos.Opciones(Cadena)))
        {
            await BaseDeDatos.PrepararAsync(db);
            db.Sucursales.Add(new Sucursal { Clave = "M", Nombre = "Matriz" });
            var duenio = new Usuario { Nombre = "Luis Pérez", NombreUsuario = "luis", EleventaId = 7, PrimeraCuenta = true };
            duenio.EstablecerPin("1111");
            db.Usuarios.Add(duenio);
            await db.SaveChangesAsync();
        }
        var (salida, errores) = (new StringWriter(), new StringWriter());

        Assert.Equal(0, await PinUsuarioDelServicio.CorrerAsync(Cadena, ["--usuario", "7"], Teclado("2468", "2468"), 3, salida, errores));

        Assert.Empty(errores.ToString());
        Assert.Equal($"Base del servicio: {_ruta}", Renglones(salida)[0]);
        Assert.Contains("Luis Pérez («luis», número 7 de eleventa): ya tiene PIN; se le cambia.", salida.ToString());
        await using var datos = new PosDbContext(BaseDeDatos.Opciones(Cadena));
        Assert.Equal(ResultadoPin.Correcto, (await datos.Usuarios.SingleAsync()).VerificarPin("2468", DateTime.UtcNow));
        Assert.Equal(["Se le cambió el PIN a Luis Pérez con pin-usuario (en la PC del servicio)"], await datos.Bitacora.Select(b => b.Detalle).ToListAsync());
    }

    [Fact]
    public async Task Solo_con_usuario_y_con_la_base_del_servicio_ya_al_dia()
    {
        var (salida, errores) = (new StringWriter(), new StringWriter());

        // Sin --usuario, o con otra base: va la del servicio.
        Assert.Equal(1, await PinUsuarioDelServicio.CorrerAsync(Cadena, [], Teclado(), 3, salida, errores));
        Assert.Equal(1, await PinUsuarioDelServicio.CorrerAsync(Cadena, ["--base", "otra.db", "--usuario", "luis"], Teclado(), 3, salida, errores));
        // Sin la base no la crea.
        Assert.Equal(1, await PinUsuarioDelServicio.CorrerAsync(Cadena, ["--usuario", "luis"], Teclado(), 3, salida, errores));
        Assert.False(File.Exists(_ruta));
        // Una base que no está al día con esta versión: la pone al día el servicio al arrancar (con su respaldo), no esto.
        await File.WriteAllBytesAsync(_ruta, []);
        Assert.Equal(1, await PinUsuarioDelServicio.CorrerAsync(Cadena, ["--usuario", "luis"], Teclado("2468", "2468"), 3, salida, errores));

        Assert.Equal([PinUsuarioDelServicio.Uso, PinUsuarioDelServicio.Uso, $"No está la base del servicio ({_ruta}): se arma al instalarlo (docs/pruebas-en-tienda.md).",
            $"La base {_ruta} todavía no está al día con esta versión: arranca el servicio (la pone al día, con su respaldo) y vuelve a correr pin-usuario."],
            Renglones(errores));
        Assert.Empty(salida.ToString());
    }
}
