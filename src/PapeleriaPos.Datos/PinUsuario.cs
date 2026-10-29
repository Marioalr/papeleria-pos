using System.Text;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos;

/// <summary>
/// <c>pin-usuario</c> (el piloto alfa, 2026-10-25): le pone (o le cambia) el PIN a un usuario desde la línea de comandos, al instalar
/// o si al dueño se le olvidó el suyo. Lo corren el programa del servicio (<c>PapeleriaPos.Servicio.exe pin-usuario --usuario …</c>,
/// con la base de su configuración: es lo que queda instalado en la caja 2, revisión del PR #144) y las herramientas
/// (<c>--base</c>). El PIN se pide dos veces y sin que se vea, con la misma regla que «Usuarios y permisos», y queda en la bitácora
/// sin el PIN.
/// </summary>
public static class PinUsuario
{
    public const string Verbo = "pin-usuario";

    /// <summary>El PIN en una variable de entorno: solo para pruebas o para automatizar (queda en el entorno). Si no, se pide.</summary>
    public const string VariablePin = "POS_PIN";

    public const string SinTeclado =
        $"Falta el PIN: escríbelo al correrlo (sin que se vea). Solo para pruebas o para automatizar, va en la variable {VariablePin}.";

    /// <summary>
    /// El PIN nuevo de alguien, dos veces y sin que se vea (<paramref name="pedir"/> recibe lo que se pregunta y regresa lo
    /// escrito, o vacío si no hay teclado), con la regla de «Usuarios y permisos» (<see cref="FichasEntrada.RevisarPinNuevo"/>).
    /// Si no está bien, lo dice y lo vuelve a pedir, hasta <paramref name="intentos"/> veces. Vacío si no se pudo.
    /// </summary>
    public static string? PedirPin(string nombre, Func<string, string?> pedir, TextWriter errores, int intentos = 3)
    {
        for (var vez = 1; vez <= intentos; vez++)
        {
            if (pedir($"PIN de {nombre} ({FichasEntrada.CifrasPin} cifras): ") is not { } pin || pedir("El PIN otra vez: ") is not { } otraVez)
            {
                errores.WriteLine(SinTeclado);
                return null;
            }
            if (FichasEntrada.RevisarPinNuevo(pin, otraVez) is not { } mal)
                return pin;
            errores.WriteLine(mal);
        }
        errores.WriteLine("No se puso el PIN.");
        return null;
    }

    /// <summary>
    /// El usuario de <c>pin-usuario --usuario</c>: por su número de eleventa, por su usuario (con el que se entra escribiéndolo) o
    /// por su nombre completo, sin acentos ni mayúsculas. Solo si es uno: si no, por qué.
    /// </summary>
    public static async Task<(Usuario? Usuario, string? Error)> BuscarAsync(PosDbContext db, string texto, CancellationToken ct = default)
    {
        var buscado = texto.Trim();
        var usuarios = await db.Usuarios.ToListAsync(ct);
        var porNumero = int.TryParse(buscado, out var numero) ? usuarios.Where(u => u.EleventaId == numero).ToList() : [];
        var encontrados = porNumero.Count > 0 ? porNumero
            : usuarios.Where(u => string.Equals(u.NombreUsuario, buscado, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } porUsuario ? porUsuario
            : usuarios.Where(u => Texto.Normalizar(u.Nombre) == Texto.Normalizar(buscado)).ToList();
        return encontrados.Count switch
        {
            1 => (encontrados[0], null),
            0 => (null, $"No hay ningún usuario «{buscado}»."),
            _ => (null, $"Hay {encontrados.Count} usuarios «{buscado}»: di cuál con su usuario o su número de eleventa."),
        };
    }

    /// <summary>Los usuarios de la base, para elegir: su nombre, su usuario, su número de eleventa y si tiene PIN (nunca el PIN).</summary>
    public static async Task<List<string>> ListaAsync(PosDbContext db, CancellationToken ct = default) =>
        (await db.Usuarios.AsNoTracking().OrderBy(u => u.Nombre).ToListAsync(ct))
            .Select(u => $"  {u.Nombre,-40}  «{u.NombreUsuario}»{(u.EleventaId is { } n ? $", número {n} de eleventa" : "")}"
                + $"{(u.TienePin ? "" : ", sin PIN")}{(u.Activo ? "" : ", dado de baja")}")
            .ToList();

    /// <summary>
    /// <c>pin-usuario</c>, ya con la base abierta y al día: busca al usuario, pide su PIN dos veces (<paramref name="pedir"/>) con la
    /// regla de «Usuarios y permisos» y lo pone, en la bitácora sin el PIN. Lo que pasa lo dice en <paramref name="salida"/> y
    /// <paramref name="errores"/>. Regresa el código de salida: 0 si quedó, 1 si no.
    /// </summary>
    public static async Task<int> PonerAsync(PosDbContext db, string buscado, Func<string, string?> pedir, int intentos, TextWriter salida,
        TextWriter errores, CancellationToken ct = default)
    {
        var (usuario, noEs) = await BuscarAsync(db, buscado, ct);
        if (usuario is null)
        {
            errores.WriteLine(noEs);
            errores.WriteLine("Los usuarios de la base:");
            foreach (var linea in await ListaAsync(db, ct))
                errores.WriteLine(linea);
            return 1;
        }
        salida.WriteLine($"{usuario.Nombre} («{usuario.NombreUsuario}»{(usuario.EleventaId is { } n ? $", número {n} de eleventa" : "")}): "
            + (usuario.TienePin ? "ya tiene PIN; se le cambia." : "todavía no tiene PIN."));
        if (PedirPin(usuario.Nombre, pedir, errores, intentos) is not { } pin)
            return 1;
        await PonerPinAsync(db, usuario, pin, ct);
        await db.SaveChangesAsync(ct);
        salida.WriteLine($"Listo: {usuario.Nombre} entra con su ficha y ese PIN; donde se escribe el usuario, el suyo es «{usuario.NombreUsuario}».");
        if (!usuario.Activo)
            salida.WriteLine("Está dado de baja: no entra hasta que lo vuelvan a dar de alta en Configuración › Usuarios y permisos.");
        return 0;
    }

    /// <summary>
    /// El PIN que se pone desde la línea de comandos (al instalar, o si el dueño olvidó el suyo): empieza sin intentos fallidos,
    /// y queda en la bitácora (sin el PIN). Quien llama guarda.
    /// </summary>
    public static async Task PonerPinAsync(PosDbContext db, Usuario usuario, string pin, CancellationToken ct = default)
    {
        var primero = !usuario.TienePin;
        usuario.EstablecerPin(pin);
        var sucursalId = usuario.SucursalId ?? await db.Sucursales.OrderBy(s => s.Clave != "M").ThenBy(s => s.Nombre).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (sucursalId is { } sucursal)
            new ServicioControl(db).Registrar(sucursal, null, null, AccionControlada.EditarReglas,
                $"{(primero ? "Se le puso PIN" : "Se le cambió el PIN")} a {usuario.Nombre} con pin-usuario (en la PC del servicio)");
    }

    // ── La consola ──

    /// <summary>El PIN de la variable <see cref="VariablePin"/>, si la hay (solo para pruebas o para automatizar).</summary>
    public static string? DeLaVariable() => Environment.GetEnvironmentVariable(VariablePin) is { Length: > 0 } pin ? pin : null;

    /// <summary>Cuántas veces se pide: con la variable, una (no cambia al volver a pedirlo).</summary>
    public static int Intentos => DeLaVariable() is null ? 3 : 1;

    /// <summary>Un PIN: el de la variable si la hay; si no, tecleado sin que se vea. Sin teclado (la entrada redirigida), vacío.</summary>
    public static string? Leer(string pregunta) => DeLaVariable() ?? LeerSinVerse(pregunta);

    /// <summary>Lo que se teclea sin que se vea (asteriscos), después de la pregunta. Sin teclado (la entrada redirigida), vacío.</summary>
    public static string? LeerSinVerse(string pregunta)
    {
        if (Console.IsInputRedirected)
            return null;
        Console.Write(pregunta);
        var escrito = new StringBuilder();
        while (Console.ReadKey(intercept: true) is var tecla && tecla.Key != ConsoleKey.Enter)
        {
            if (tecla.Key == ConsoleKey.Backspace)
            {
                if (escrito.Length > 0)
                {
                    escrito.Length--;
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(tecla.KeyChar))
            {
                escrito.Append(tecla.KeyChar);
                Console.Write('*');
            }
        }
        Console.WriteLine();
        return escrito.Length > 0 ? escrito.ToString() : null;
    }
}
