using System.Globalization;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Nucleo.Usuarios;

/// <summary>
/// La entrada de la caja (docs/propuesta-interfaz.md, «Usuarios y permisos»; entrega 6): una ficha por cada usuario activo
/// de la sucursal, con su letra y lo que puede hacer, y luego solo el PIN, sin escribir el usuario. Las arman igual el
/// servicio y la caja sin conexión (con su copia local de los usuarios y sus permisos): las fichas y sus letras son las
/// mismas con o sin la caja principal.
/// </summary>
public static class FichasEntrada
{
    /// <summary>
    /// Las cifras del PIN: al teclear la cuarta, entra solo, como en el prototipo. «Usuarios y permisos» da de alta y cambia
    /// los PIN con estas cifras (el largo del PIN en Configuración › Seguridad llega con la entrega 7).
    /// </summary>
    public const int CifrasPin = 4;

    /// <summary>Lo más largo que cabe de un nombre en su ficha (y en «Usuarios y permisos»).</summary>
    public const int LargoNombre = 40;

    /// <summary>
    /// Lo que dice la entrada al tocar la ficha de quien todavía no tiene PIN (vino de eleventa, 2026-10-25): no hay otro camino,
    /// el dueño se lo pone. Lo mismo dice el servicio, y la caja sin conexión, si se intenta entrar.
    /// </summary>
    public const string SinPin = "Todavía no tienes PIN: pídele al dueño que te lo ponga en Configuración › Usuarios y permisos.";

    /// <summary>
    /// El PIN nuevo, escrito dos veces, como lo revisa «Usuarios y permisos» antes de mandarlo: de <see cref="CifrasPin"/> cifras
    /// y los dos iguales. Vacío si está bien. Lo usan la pantalla, el servicio (con un solo PIN) y <c>pin-usuario</c>.
    /// </summary>
    public static string? RevisarPinNuevo(string? pin, string? otraVez)
    {
        if (pin is null || pin.Length != CifrasPin || !pin.All(char.IsAsciiDigit))
            return $"El PIN es de {CifrasPin} cifras.";
        return pin == otraVez ? null : "Los dos PIN no son iguales: escríbelo otra vez.";
    }

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>
    /// Lo que se dice de lo que tiene de más que los de defecto, en este orden (de lo que más se busca a lo que menos). Con
    /// tres basta para reconocerlo: «Corte de turno, productos y clientes».
    /// </summary>
    private static readonly (AccionControlada Accion, string Texto)[] Notables =
    [
        (AccionControlada.CorteZ, "corte de turno"),
        (AccionControlada.EditarCatalogo, "productos"),
        (AccionControlada.AdministrarClientes, "clientes"),
        (AccionControlada.CostearCompra, "compras"),
        (AccionControlada.ConteoCompleto, "conteo completo"),
        (AccionControlada.ConsultarBitacora, "bitácora"),
        (AccionControlada.VerReportes, "reportes"),
    ];

    /// <summary>
    /// La letra de cada uno, sin repetirse en la caja: la inicial de su nombre; si ya es de otro, la de su otra palabra (el
    /// apellido) o la siguiente letra de su nombre; si tampoco, la primera libre del abecedario. Se reparten en el orden en
    /// que se dieron de alta (los Id son UUIDv7, con la fecha al principio): dar de alta a alguien no le cambia la letra a
    /// nadie, y la misma lista da siempre las mismas letras. Solo letras: los números son del PIN. Con más de 26 usuarios,
    /// los que ya no alcanzan letra se tocan.
    /// <para>
    /// Una baja tampoco mueve letras: se reparten también entre los dados de baja (<see cref="Armar"/>), en el servicio y
    /// en la copia de la caja, que los trae sin el hash de su PIN.
    /// </para>
    /// </summary>
    public static Dictionary<Guid, string> Teclas(IEnumerable<(Guid Id, string Nombre)> usuarios)
    {
        var libres = new HashSet<char>(Enumerable.Range('A', 26).Select(c => (char)c));
        var teclas = new Dictionary<Guid, string>();
        foreach (var (id, nombre) in usuarios.DistinctBy(u => u.Id).OrderBy(u => u.Id))
        {
            var letra = Candidatas(nombre).FirstOrDefault(libres.Contains);
            if (letra == default)
                continue;
            libres.Remove(letra);
            teclas[id] = letra.ToString();
        }
        return teclas;
    }

    /// <summary>Las letras que le quedarían bien, de la que más se recuerda a la que menos.</summary>
    private static IEnumerable<char> Candidatas(string nombre)
    {
        var normal = Texto.Normalizar(nombre);
        // La primera letra de cada palabra («3D» da la D).
        foreach (var palabra in normal.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (palabra.FirstOrDefault(EsLetra) is var inicial and not default(char))
                yield return inicial;
        }
        foreach (var c in normal.Where(EsLetra))
            yield return c;
        for (var c = 'A'; c <= 'Z'; c++)
            yield return c;
    }

    private static bool EsLetra(char c) => c is >= 'A' and <= 'Z';

    /// <summary>
    /// Las fichas, por nombre (como se buscan con la vista). <paramref name="usuarios"/> son los activos que entran en esa
    /// caja (los de su sucursal y los que no tienen sucursal), con sus permisos. Las letras se reparten entre
    /// <paramref name="paraLasLetras"/>: todos los que entran en la caja, también los dados de baja, para que una baja no le
    /// cambie la letra a nadie. Quien todavía no tiene PIN (vino de eleventa) también tiene su ficha y su letra: al tocarla, la
    /// entrada le dice que se lo pida al dueño (<see cref="SinPin"/>).
    /// </summary>
    /// <param name="defecto">Los permisos por defecto: el resumen dice si alguien tiene esos o qué tiene de más.</param>
    /// <param name="cajeroDelTurno">Quien tiene abierto el turno de esa caja: su ficha lo dice.</param>
    public static List<FichaUsuarioDto> Armar(
        IEnumerable<UsuarioConPermisos> usuarios, IEnumerable<(Guid Id, string Nombre)> paraLasLetras, PermisosDeUsuario defecto, Guid? cajeroDelTurno)
    {
        var lista = usuarios.ToList();
        var teclas = Teclas(paraLasLetras.Concat(lista.Select(u => (u.Id, u.Nombre))));
        return lista
            .OrderBy(u => u.Nombre.Trim(), StringComparer.Create(Mexico, ignoreCase: true))
            .ThenBy(u => u.Id)
            .Select(u => new FichaUsuarioDto(u.Id, u.Nombre.Trim(), teclas.GetValueOrDefault(u.Id), Resumen(u.Permisos, defecto),
                u.Id == cajeroDelTurno, HaceCorte(u.Permisos), SinPin: !u.TienePin))
            .ToList();
    }

    /// <summary>
    /// Lo que puede hacer, en pocas palabras, como en el prototipo: «Todos los permisos», «Permisos por defecto» (los
    /// mismos que <paramref name="defecto"/>, con su límite) o lo que tiene de más que los de defecto («Corte de turno,
    /// productos y clientes»); si lo que tiene de más no es de lo que se nombra, «Permisos propios».
    /// </summary>
    public static string Resumen(PermisosDeUsuario permisos, PermisosDeUsuario defecto)
    {
        if (permisos.TieneTodos)
            return "Todos los permisos";
        if (Permisos.EnPantalla.All(a => permisos.De(a).SeVeIgual(defecto.De(a))))
            return "Permisos por defecto";
        var demas = Notables
            .Where(n => permisos.De(n.Accion).Nivel == NivelPermiso.Permitido && defecto.De(n.Accion).Nivel != NivelPermiso.Permitido)
            .Select(n => n.Texto)
            .Take(3)
            .ToList();
        if (demas.Count == 0)
            return "Permisos propios";
        var texto = demas.Count == 1 ? demas[0] : $"{string.Join(", ", demas.Take(demas.Count - 1))} y {demas[^1]}";
        return char.ToUpper(texto[0], Mexico) + texto[1..];
    }

    /// <summary>
    /// Puede hacer el corte de un turno («Puede» en el corte): si entra en una caja con el turno de otro cajero, lo hace
    /// ahí mismo, sin volver a escribir su PIN.
    /// </summary>
    public static bool HaceCorte(PermisosDeUsuario permisos) => permisos.De(AccionControlada.CorteZ).Nivel == NivelPermiso.Permitido;

    /// <summary>Un PIN equivocado en la entrada: cuántos intentos le quedan antes de tener que esperar unos minutos.</summary>
    public static string PinIncorrecto(int quedan) =>
        quedan == 1 ? "PIN incorrecto. Te queda 1 intento." : $"PIN incorrecto. Te quedan {quedan} intentos.";

    public const string DemasiadosIntentos = "Demasiados intentos: espera unos minutos.";
}
