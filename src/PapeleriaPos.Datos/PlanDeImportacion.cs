using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Datos;

/// <summary>
/// Lo que pasaría al importar el Excel de los productos (Productos › Productos en Excel, 2026-10-22), renglón por renglón: qué
/// cambia, qué es nuevo y qué se ignora y por qué. Al aplicar se arma igual, con los productos cargados para cambiarlos, y lo
/// que se vio se aplica todo junto (<see cref="RegistroProductosEnExcel.AplicarAsync"/>).
/// <list type="bullet">
/// <item><b>Cada renglón de «Productos» es una variante</b> y se reconoce por su código: el de «Código», uno de su pieza (también
/// sin importar mayúsculas, como al escanear). Un código que no tiene nadie es un producto nuevo, con una variante. Un renglón sin
/// código (casi siempre un servicio) se reconoce por la clave escondida que trae del Excel que se exportó; uno escrito a mano,
/// por su nombre y su departamento, entre los que no tienen código en su pieza (como los deja el Excel). Lo de «Presentaciones»
/// se reconoce por el código de su producto (o su clave) y el nombre de la presentación; una que no tiene se da de alta.</item>
/// <item><b>Lo que cambió después de exportar el Excel no se regresa sin verlo:</b> el Excel trae la versión del catálogo con que
/// se exportó; un producto que cambió después (en su ficha, en otra caja, con «Cambiar precios») se enseña aparte, con lo que
/// cambió, y lo que el Excel le cambiaría no se aplica hasta elegir «Aplicarlos de todos modos». Un Excel que no trae su versión
/// (de antes, o hecho en otro lado) pone así a todos los que ya existen.</item>
/// <item><b>Una columna que no viene no cambia nada;</b> una celda vacía quita lo que se puede quitar (la marca, el mínimo, el
/// máximo, una escala de mayoreo, un código de «Otros códigos», el precio propio de una presentación). El nombre, el
/// departamento y el precio no pueden quedar vacíos (el precio sí, en un producto que no tenía: se queda sin él).</item>
/// <item><b>Lo que se cobra queda en orden, como en la ficha y en «Cambiar precios»:</b> el mayoreo más barato que el menudeo
/// (y una escala de más piezas, igual o más barata que la de menos), un paquete nunca más caro que sus piezas sueltas, y nada que
/// cambie queda debajo de su costo promedio. Si algo de eso no se cumple, se ignoran los renglones de esa variante y se dice
/// por qué. El cobro, además, nunca cobra una escala más cara que el menudeo (<see cref="CalculoPrecio"/>).</item>
/// <item><b>«Se vende» es de cada variante, y el producto se vende si alguna se vende:</b> si todas quedan en «No», el producto
/// deja de venderse; si una de un producto que no se vendía dice «Sí», el producto vuelve a venderse con solo las que dicen «Sí»
/// (las demás, como diga su renglón; sin renglón, siguen sin venderse).</item>
/// <item><b>Un código es de un solo producto:</b> uno que ya tiene otro (u otra variante, u otra presentación) o que se repite
/// en el archivo hace que se ignoren esos renglones. Dos que solo cambian en mayúsculas y que ya existen así, cada uno en su
/// producto, son dos códigos.</item>
/// <item><b>Los departamentos:</b> uno que no existe se da de alta si quien importa puede darlos de alta (Productos ›
/// Departamentos, #104); si no, el renglón se ignora. Uno desactivado no recibe productos activos, como en la ficha.</item>
/// <item><b>No se cambia desde Excel</b> lo que tiene su propio camino: la existencia y el costo (las compras, los conteos y los
/// ajustes), la unidad (la depuración), lo que contiene una presentación, los kits y los cobros de terceros (su ficha, también
/// sus presentaciones).</item>
/// </list>
/// </summary>
internal sealed class PlanDeImportacion
{
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");
    private static readonly Cantidad Una = Cantidad.Unidades(1);
    private const string Productos = ProductosEnExcel.HojaProductos;
    private const string Presentaciones = ProductosEnExcel.HojaPresentaciones;

    private readonly PosDbContext _db;
    private readonly SolicitudImportarProductos _s;
    private readonly LibroDeProductos _libro;
    private readonly bool _paraAplicar;
    private readonly List<Action> _acciones = [];

    /// <summary>
    /// Con qué catálogo se exportó el Excel; vacío si no lo dice (o es de otra sucursal): entonces todo lo que ya existe pudo
    /// cambiar después de exportarlo.
    /// </summary>
    private readonly ExportacionDelLibro? _exportado;

    private bool _conMargen;
    private bool _puedeDepartamentos;

    private PlanDeImportacion(PosDbContext db, SolicitudImportarProductos s, LibroDeProductos libro, bool paraAplicar)
    {
        (_db, _s, _libro, _paraAplicar) = (db, s, libro, paraAplicar);
        _exportado = libro.Exportado is { } exportado && exportado.SucursalId == s.SucursalId ? exportado : null;
    }

    /// <summary>Lo que se le enseña a quien importa.</summary>
    public VistaImportacionDto Vista { get; private set; } = null!;

    /// <summary>Lo que se aplica, completo, para guardarlo con su renglón de la bitácora.</summary>
    public DetalleDeImportacionDto Detalle { get; private set; } = null!;

    /// <summary>
    /// Arma la vista. Con <paramref name="paraAplicar"/>, los productos quedan cargados para cambiarlos con <see cref="Aplicar"/>
    /// (dentro de la transacción de quien llama).
    /// </summary>
    public static async Task<PlanDeImportacion> ArmarAsync(PosDbContext db, SolicitudImportarProductos s, LibroDeProductos libro, bool paraAplicar,
        CancellationToken ct)
    {
        var plan = new PlanDeImportacion(db, s, libro, paraAplicar);
        await plan.ArmarAsync(ct);
        return plan;
    }

    /// <summary>Lo que se vio, en los productos cargados: lo guarda quien llama.</summary>
    public void Aplicar()
    {
        if (!_paraAplicar)
            throw new InvalidOperationException("La vista no se aplica: se arma otra vez para aplicarla.");
        foreach (var accion in _acciones)
            accion();
    }

    /// <summary>
    /// Lo que se va a cambiar, seguido por la base para que lo guarde: solo eso (el catálogo se leyó sin seguirlo), y sin lo que
    /// trae colgado.
    /// </summary>
    private T Seguido<T>(T entidad) where T : class
    {
        var entrada = _db.Entry(entidad);
        if (entrada.State == EntityState.Detached)
            entrada.State = EntityState.Unchanged;
        return entidad;
    }

    /// <summary>Lo que se quita (un código, un precio): solo eso.</summary>
    private void Quitar<T>(T entidad) where T : class => _db.Entry(entidad).State = EntityState.Deleted;

    // ── Los renglones ────────────────────────────────────────────────────────

    /// <summary>Un renglón de una de las hojas: su número, y por qué se ignora (el primer motivo que se encontró).</summary>
    private abstract class Renglon
    {
        public abstract string Hoja { get; }
        public abstract int Numero { get; }
        public Rechazo? Rechazo { get; private set; }
        public bool Va => Rechazo is null;

        /// <summary>Si ya tenía un motivo, se queda ese: es el primero que se le dice.</summary>
        public void Ignorar(Rechazo? rechazo) => Rechazo ??= rechazo;

        public void Ignorar(PorQueSeIgnora porQue, string detalle) => Ignorar(new Rechazo(porQue, detalle));

        /// <summary>Si no cambió nada en él.</summary>
        public bool SinCambios { get; set; } = true;

        /// <summary>La clave escondida que trae del Excel que se exportó (la de su variante); vacía en uno escrito a mano.</summary>
        public Guid? Clave { get; set; }
    }

    /// <summary>Un renglón de «Productos», ya leído.</summary>
    private sealed class Fila(int numero) : Renglon
    {
        public override string Hoja => Productos;
        public override int Numero => numero;

        public string? Codigo { get; set; }

        /// <summary>Si el código venía como número de Excel: así se le pudieron perder los ceros de adelante.</summary>
        public bool CodigoNumerico { get; set; }

        /// <summary>Vacío si la hoja no trae «Otros códigos».</summary>
        public List<string>? Otros { get; set; }

        public bool OtrosNumerico { get; set; }
        public string? Producto { get; set; }
        public string? Descripcion { get; set; }
        public string? Departamento { get; set; }
        public string? Marca { get; set; }
        public UnidadBase? Unidad { get; set; }
        public long? Precio { get; set; }

        /// <summary>Vacío si la hoja no trae escalas de mayoreo: se quedan las que tiene.</summary>
        public List<(int Numero, decimal Desde, long Precio)>? Mayoreo { get; set; }

        public decimal? Minimo { get; set; }
        public decimal? Maximo { get; set; }
        public bool? SeVende { get; set; }

        /// <summary>La variante que cambia; vacía si es nuevo (o si no se supo cuál).</summary>
        public Guid? VarianteId { get; set; }

        public Variante? Existente { get; set; }
        public Producto? Suyo { get; set; }

        /// <summary>Si es un producto nuevo: un código que no tenía nadie.</summary>
        public bool EsNuevo { get; set; }

        /// <summary>De un producto nuevo: lo que conviene saber (que se llama como otro, que el código parece otro sin sus ceros…).</summary>
        public List<string> Avisos { get; } = [];

        /// <summary>A qué departamento va: uno que ya existe…</summary>
        public Departamento? Destino { get; set; }

        /// <summary>…o el nombre de uno que se da de alta.</summary>
        public string? DepartamentoNuevo { get; set; }

        /// <summary>Todos los códigos de su pieza que trae: el de «Código» y los de «Otros códigos», sin repetir.</summary>
        public List<string> Codigos =>
            (Codigo is null ? Enumerable.Empty<string>() : [Codigo]).Concat(Otros ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public string Nombre => Descripcion is { Length: > 0 } d ? $"{Producto} {d}" : Producto ?? "";
    }

    /// <summary>Un renglón de «Presentaciones», ya leído.</summary>
    private sealed class FilaDePresentacion(int numero) : Renglon
    {
        public override string Hoja => Presentaciones;
        public override int Numero => numero;

        public string? CodigoDelProducto { get; set; }
        public bool CodigoNumerico { get; set; }
        public string? Nombre { get; set; }
        public decimal? Contiene { get; set; }

        /// <summary>Vacío si la hoja no trae «Códigos»: se quedan los que tiene.</summary>
        public List<string>? Codigos { get; set; }

        public bool CodigosNumerico { get; set; }
        public long? Precio { get; set; }
        public List<(int Numero, decimal Desde, long Precio)>? Mayoreo { get; set; }

        /// <summary>De qué variante es: una que existe…</summary>
        public Variante? Variante { get; set; }

        /// <summary>…o un producto nuevo de la hoja «Productos».</summary>
        public Fila? DeNuevo { get; set; }

        /// <summary>La presentación que cambia; vacía si es nueva.</summary>
        public Presentacion? Existente { get; set; }
    }

    private Fila LeerFila(RenglonLeido<CampoDeProducto> r)
    {
        var hoja = _libro.Productos;
        var f = new Fila(r.Fila);
        static string T(CampoDeProducto campo) => ProductosEnExcel.Titulo(campo);
        f.Codigo = LecturaDeCeldas.Codigo(r[CampoDeProducto.Codigo], T(CampoDeProducto.Codigo), out var rechazo);
        f.Ignorar(rechazo);
        f.CodigoNumerico = r[CampoDeProducto.Codigo].Tipo == TipoCelda.Numero;
        if (hoja.Trae(CampoDeProducto.OtrosCodigos))
        {
            f.Otros = [.. LecturaDeCeldas.Codigos(r[CampoDeProducto.OtrosCodigos], T(CampoDeProducto.OtrosCodigos), out rechazo)];
            f.Ignorar(rechazo);
            f.OtrosNumerico = r[CampoDeProducto.OtrosCodigos].Tipo == TipoCelda.Numero;
        }
        f.Producto = LecturaDeCeldas.TextoDe(r[CampoDeProducto.Producto], T(CampoDeProducto.Producto), ProductosEnExcel.LargoNombre, out rechazo);
        f.Ignorar(rechazo);
        f.Descripcion = LecturaDeCeldas.TextoDe(r[CampoDeProducto.Variante], T(CampoDeProducto.Variante), ProductosEnExcel.LargoNombre, out rechazo);
        f.Ignorar(rechazo);
        f.Departamento = LecturaDeCeldas.TextoDe(r[CampoDeProducto.Departamento], T(CampoDeProducto.Departamento), ProductosEnExcel.LargoNombre, out rechazo);
        f.Ignorar(rechazo);
        f.Marca = LecturaDeCeldas.TextoDe(r[CampoDeProducto.Marca], T(CampoDeProducto.Marca), ProductosEnExcel.LargoCorto, out rechazo);
        f.Ignorar(rechazo);
        f.Unidad = LecturaDeCeldas.Unidad(r[CampoDeProducto.Unidad], T(CampoDeProducto.Unidad), out rechazo);
        f.Ignorar(rechazo);
        f.Precio = LecturaDeCeldas.Centavos(r[CampoDeProducto.Precio], T(CampoDeProducto.Precio), out rechazo);
        f.Ignorar(rechazo);
        f.Mayoreo = Mayoreo(r.Mayoreo, hoja.Mayoreo, f);
        f.Minimo = LecturaDeCeldas.Cantidad(r[CampoDeProducto.Minimo], T(CampoDeProducto.Minimo), out rechazo);
        f.Ignorar(rechazo);
        f.Maximo = LecturaDeCeldas.Cantidad(r[CampoDeProducto.Maximo], T(CampoDeProducto.Maximo), out rechazo);
        f.Ignorar(rechazo);
        f.SeVende = LecturaDeCeldas.SiNo(r[CampoDeProducto.SeVende], T(CampoDeProducto.SeVende), out rechazo);
        f.Ignorar(rechazo);
        f.Clave = LecturaDeCeldas.Clave(r[CampoDeProducto.Clave], T(CampoDeProducto.Clave), out rechazo);
        f.Ignorar(rechazo);
        return f;
    }

    private FilaDePresentacion LeerPresentacion(RenglonLeido<CampoDePresentacion> r)
    {
        var hoja = _libro.Presentaciones!;
        var p = new FilaDePresentacion(r.Fila);
        static string T(CampoDePresentacion campo) => ProductosEnExcel.Titulo(campo);
        p.CodigoDelProducto = LecturaDeCeldas.Codigo(r[CampoDePresentacion.CodigoDelProducto], T(CampoDePresentacion.CodigoDelProducto), out var rechazo);
        p.Ignorar(rechazo);
        p.CodigoNumerico = r[CampoDePresentacion.CodigoDelProducto].Tipo == TipoCelda.Numero;
        p.Nombre = LecturaDeCeldas.TextoDe(r[CampoDePresentacion.Presentacion], T(CampoDePresentacion.Presentacion), ProductosEnExcel.LargoCorto, out rechazo);
        p.Ignorar(rechazo);
        p.Contiene = LecturaDeCeldas.Cantidad(r[CampoDePresentacion.Contiene], T(CampoDePresentacion.Contiene), out rechazo);
        p.Ignorar(rechazo);
        if (hoja.Trae(CampoDePresentacion.Codigos))
        {
            p.Codigos = [.. LecturaDeCeldas.Codigos(r[CampoDePresentacion.Codigos], T(CampoDePresentacion.Codigos), out rechazo)];
            p.Ignorar(rechazo);
            p.CodigosNumerico = r[CampoDePresentacion.Codigos].Tipo == TipoCelda.Numero;
        }
        p.Precio = LecturaDeCeldas.Centavos(r[CampoDePresentacion.Precio], T(CampoDePresentacion.Precio), out rechazo);
        p.Ignorar(rechazo);
        p.Mayoreo = Mayoreo(r.Mayoreo, hoja.Mayoreo, p);
        p.Clave = LecturaDeCeldas.Clave(r[CampoDePresentacion.Clave], T(CampoDePresentacion.Clave), out rechazo);
        p.Ignorar(rechazo);
        return p;
    }

    /// <summary>Las escalas de mayoreo de un renglón (vacío si la hoja no trae ninguna): cada una con su «desde» y su precio.</summary>
    private static List<(int Numero, decimal Desde, long Precio)>? Mayoreo(
        IReadOnlyDictionary<int, (CeldaLeida Desde, CeldaLeida Precio)> celdas, IReadOnlyList<int> escalas, Renglon renglon)
    {
        if (escalas.Count == 0)
            return null;
        var mayoreo = new List<(int, decimal, long)>();
        foreach (var numero in escalas)
        {
            var (celdaDesde, celdaPrecio) = celdas.TryGetValue(numero, out var par) ? par : (CeldaLeida.Vacia, CeldaLeida.Vacia);
            var desde = LecturaDeCeldas.Cantidad(celdaDesde, ProductosEnExcel.MayoreoDesde(numero), out var rechazo);
            renglon.Ignorar(rechazo);
            var precio = LecturaDeCeldas.Centavos(celdaPrecio, ProductosEnExcel.MayoreoPrecio(numero), out rechazo);
            renglon.Ignorar(rechazo);
            if (rechazo is null && (desde is null) != (precio is null) && (celdaDesde.EsVacia || celdaPrecio.EsVacia))
                renglon.Ignorar(PorQueSeIgnora.FaltaDato, desde is null
                    ? $"«{ProductosEnExcel.MayoreoPrecio(numero)}» tiene precio pero «{ProductosEnExcel.MayoreoDesde(numero)}» está vacío: van juntos."
                    : $"«{ProductosEnExcel.MayoreoDesde(numero)}» dice desde cuántas pero «{ProductosEnExcel.MayoreoPrecio(numero)}» está vacío: van juntos.");
            if (desde is { } d && precio is { } p)
                mayoreo.Add((numero, d, p));
        }
        return mayoreo;
    }

    // ── El catálogo ──────────────────────────────────────────────────────────

    private sealed record PresentacionLigera(Guid Id, Guid VarianteId, bool EsBase, string Nombre);

    /// <summary>De cada presentación del catálogo: de qué variante es, si es la base y cómo se llama.</summary>
    private Dictionary<Guid, PresentacionLigera> _presentacionesLigeras = [];

    /// <summary>El nombre completo de cada variante (el del producto y el suyo), también de las juntadas: para decir de quién es algo.</summary>
    private Dictionary<Guid, (Guid ProductoId, string Nombre)> _nombres = [];

    /// <summary>Las variantes que siguen en el catálogo (sin las que se juntaron con otra).</summary>
    private HashSet<Guid> _vivas = [];

    /// <summary>Las que se juntaron con otra: con cuál.</summary>
    private Dictionary<Guid, Guid> _juntadas = [];

    /// <summary>El primer código de la pieza de cada variante (para decir cuál es la que ya se llama igual).</summary>
    private Dictionary<Guid, string> _codigoDe = [];

    /// <summary>Todos los códigos, como los busca el escaneo: sin importar mayúsculas.</summary>
    private CodigosDeEscaneo _escaneo = CodigosDeEscaneo.Ninguno;

    /// <summary>Las variantes cuya pieza no tiene código (como las deja el Excel: «Código» vacío).</summary>
    private HashSet<Guid> _piezaSinCodigo = [];

    /// <summary>Esas, por su nombre completo normalizado: así se reconoce un renglón sin código escrito a mano.</summary>
    private ILookup<string, Guid> _sinCodigo = Enumerable.Empty<Guid>().ToLookup(x => "");

    /// <summary>Cada producto por su nombre normalizado: para avisar de un producto nuevo que se llama como uno que ya existe.</summary>
    private ILookup<string, Guid> _porNombre = Enumerable.Empty<Guid>().ToLookup(x => "");

    /// <summary>El departamento de cada producto.</summary>
    private Dictionary<Guid, Guid> _departamentoDe = [];

    /// <summary>
    /// Los códigos de puros números que empiezan con cero, por cómo quedan sin sus ceros («00123» → «123»): para avisar de un
    /// producto nuevo cuyo código es uno de esos al que Excel le quitó los ceros.
    /// </summary>
    private Dictionary<string, string> _sinSusCeros = [];

    // Lo que cambia, completo (cargado para cambiarlo, al aplicar).
    private Dictionary<Guid, Producto> _productos = [];
    private Dictionary<Guid, Variante> _variantes = [];
    private Dictionary<Guid, List<Precio>> _precios = [];
    private Dictionary<Guid, decimal> _costos = [];
    private List<Departamento> _departamentos = [];
    private Dictionary<string, Marca> _marcas = [];
    private Dictionary<Guid, int?> _margenesMinimos = [];
    private int _margenGeneral;

    /// <summary>Lo que cambió de precio después de exportar el Excel (solo los precios de todas las sucursales), por variante.</summary>
    private ILookup<Guid, PrecioHistorial> _preciosDespues = Enumerable.Empty<PrecioHistorial>().ToLookup(x => Guid.Empty);

    /// <summary>Lo que se le quitó a cada variante después de exportar el Excel: «CodigoBarras», «Presentacion»…</summary>
    private ILookup<Guid, string> _quitadoDespues = Enumerable.Empty<string>().ToLookup(x => Guid.Empty);

    private async Task CargarCatalogoAsync(CancellationToken ct)
    {
        var productos = await _db.Productos.AsNoTracking().Select(p => new { p.Id, p.Nombre, p.DepartamentoId }).ToListAsync(ct);
        var nombresDeProductos = productos.ToDictionary(p => p.Id, p => p.Nombre);
        _departamentoDe = productos.ToDictionary(p => p.Id, p => p.DepartamentoId);
        _departamentos = await _db.Departamentos.AsNoTracking().ToListAsync(ct);
        var todas = await _db.Variantes.AsNoTracking().Select(v => new { v.Id, v.ProductoId, v.Descripcion, v.JuntadaEnId }).ToListAsync(ct);
        _nombres = todas.ToDictionary(v => v.Id, v => (v.ProductoId, Completo(nombresDeProductos.GetValueOrDefault(v.ProductoId) ?? "", v.Descripcion)));
        _juntadas = todas.Where(v => v.JuntadaEnId is not null).ToDictionary(v => v.Id, v => v.JuntadaEnId!.Value);
        var variantes = todas.Where(v => v.JuntadaEnId is null).ToList();
        _vivas = variantes.Select(v => v.Id).ToHashSet();
        _presentacionesLigeras = (await _db.Presentaciones.AsNoTracking().Select(p => new { p.Id, p.VarianteId, p.EsBase, p.Nombre }).ToListAsync(ct))
            .ToDictionary(p => p.Id, p => new PresentacionLigera(p.Id, p.VarianteId, p.EsBase, p.Nombre));
        var codigos = await _db.CodigosBarras.AsNoTracking().Select(c => new { c.Codigo, c.PresentacionId, c.Origen }).ToListAsync(ct);
        _escaneo = new CodigosDeEscaneo(codigos.Where(c => _presentacionesLigeras.ContainsKey(c.PresentacionId))
            .Select(c => new CodigoLocalDto(c.Codigo, _presentacionesLigeras[c.PresentacionId].VarianteId, c.PresentacionId)));
        var conCodigo = codigos.Where(c => _presentacionesLigeras.ContainsKey(c.PresentacionId))
            .Select(c => (Presentacion: _presentacionesLigeras[c.PresentacionId], c.Codigo, c.Origen)).ToList();
        _codigoDe = conCodigo.Where(c => c.Presentacion.EsBase).GroupBy(c => c.Presentacion.VarianteId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Origen == OrigenCodigo.Fabrica ? 0 : 1).ThenBy(c => c.Codigo, StringComparer.Ordinal).First().Codigo);
        // Como el Excel: sin código es que su pieza no tiene (aunque su caja sí tenga).
        _piezaSinCodigo = variantes.Where(v => !_codigoDe.ContainsKey(v.Id)).Select(v => v.Id).ToHashSet();
        _sinCodigo = variantes.Where(v => _piezaSinCodigo.Contains(v.Id)).ToLookup(v => Texto.Normalizar(_nombres[v.Id].Nombre), v => v.Id);
        _porNombre = variantes.ToLookup(v => Texto.Normalizar(_nombres[v.Id].Nombre), v => v.Id);
        _sinSusCeros = codigos.Select(c => c.Codigo).Where(c => c.Length > 1 && c[0] == '0' && c.All(char.IsAsciiDigit) && c.TrimStart('0').Length > 0)
            .GroupBy(c => c.TrimStart('0'), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Min(StringComparer.Ordinal)!, StringComparer.Ordinal);
    }

    /// <summary>Los productos que trae el archivo, completos, con sus precios, su costo, las marcas y lo que cambió después de exportarlo.</summary>
    private async Task CargarProductosAsync(List<Fila> filas, List<FilaDePresentacion> presentaciones, CancellationToken ct)
    {
        var variantes = filas.Select(f => f.VarianteId).Concat(presentaciones.Select(p => p.Variante?.Id)).OfType<Guid>()
            .Concat(_variantesDePresentaciones).Distinct().ToList();
        var ids = variantes.Where(_nombres.ContainsKey).Select(v => _nombres[v].ProductoId).Distinct().ToList();
        var deEstos = ids.ToHashSet();
        // Con muchos, todo el catálogo y se filtra aquí (LecturaPorId). Sin seguirlos: al aplicar, la base sigue solo lo que cambia
        // (Seguido), así al guardar no revisa miles de productos que no cambian con la base apartada.
        var muchos = ids.Count > LecturaPorId.Maximo;
        var consulta = _db.Productos.AsNoTracking().Include(p => p.Variantes).ThenInclude(v => v.Presentaciones).ThenInclude(p => p.Codigos).AsSplitQuery();
        var productos = (await (muchos ? consulta : consulta.Where(p => ids.Contains(p.Id))).ToListAsync(ct)).Where(p => deEstos.Contains(p.Id)).ToList();
        _productos = productos.ToDictionary(p => p.Id);
        _variantes = productos.SelectMany(p => p.Variantes).ToDictionary(v => v.Id);
        var cargadas = _variantes.Keys.ToList();
        var precios = _db.Precios.AsNoTracking().Where(p => p.SucursalId == null);
        _precios = (await (muchos ? precios : precios.Where(p => cargadas.Contains(p.VarianteId))).ToListAsync(ct))
            .Where(p => _variantes.ContainsKey(p.VarianteId)).GroupBy(p => p.VarianteId).ToDictionary(g => g.Key, g => g.ToList());
        var existencias = _db.Existencias.AsNoTracking().Where(e => e.SucursalId == _s.SucursalId);
        _costos = (await (muchos ? existencias : existencias.Where(e => cargadas.Contains(e.VarianteId))).ToListAsync(ct))
            .Where(e => _variantes.ContainsKey(e.VarianteId) && e.CostoPromedio is not null).ToDictionary(e => e.VarianteId, e => e.CostoPromedio!.Value);
        _marcas = (await _db.Marcas.AsNoTracking().ToListAsync(ct)).GroupBy(m => m.NombreNormalizado).ToDictionary(g => g.Key, g => g.First());
        _margenesMinimos = _departamentos.ToDictionary(d => d.Id, d => d.MargenMinimoCentesimas);
        _margenGeneral = AlertasTelegram.Configuracion(_db, _s.SucursalId).MargenMinimoCentesimas;
        if (_exportado is { } exportado)
        {
            // Lo que cambió después de exportar: los precios dejan su historial con la versión del catálogo; lo quitado, su rastro.
            _preciosDespues = (await _db.PreciosHistorial.AsNoTracking().Where(h => h.VersionCatalogo > exportado.Version && h.SucursalId == null).ToListAsync(ct))
                .Where(h => _variantes.ContainsKey(h.VarianteId)).ToLookup(h => h.VarianteId);
            _quitadoDespues = (await _db.Borrados.AsNoTracking().Where(b => b.Version > exportado.Version && b.VarianteId != null)
                    .Select(b => new { b.VarianteId, b.Entidad }).ToListAsync(ct))
                .Where(b => _variantes.ContainsKey(b.VarianteId!.Value)).ToLookup(b => b.VarianteId!.Value, b => b.Entidad);
        }

        foreach (var f in filas)
        {
            if (f.VarianteId is not { } id)
                continue;
            if (_variantes.TryGetValue(id, out var variante) && variante.JuntadaEnId is null)
                (f.Existente, f.Suyo) = (variante, _productos[variante.ProductoId]);
            else
                f.Ignorar(PorQueSeIgnora.ProductoNoExiste, "Ese producto ya no está en el catálogo (se juntó con otro): cámbialo en el producto en que quedó.");
        }
        foreach (var p in presentaciones)
        {
            if (p.Variante is { } ligera && _variantes.TryGetValue(ligera.Id, out var variante))
                p.Variante = variante;
        }
    }

    /// <summary>Las variantes de «Presentaciones» que se reconocieron por su código (o su clave), antes de cargarlas.</summary>
    private readonly List<Guid> _variantesDePresentaciones = [];

    // ── Armar ────────────────────────────────────────────────────────────────

    private async Task ArmarAsync(CancellationToken ct)
    {
        var control = new ServicioControl(_db);
        _conMargen = await control.PuedeAsync(_s.UsuarioId, AccionControlada.VerUtilidad, ct);
        // Los departamentos los da de alta quien tiene «Productos, precios y etiquetas» en «Puede» (Productos › Departamentos, #104).
        _puedeDepartamentos = await control.PuedeAsync(_s.UsuarioId, AccionControlada.EditarCatalogo, ct);

        var filas = _libro.Productos.Renglones.Select(LeerFila).ToList();
        var presentaciones = (_libro.Presentaciones?.Renglones ?? []).Select(LeerPresentacion).ToList();
        await CargarCatalogoAsync(ct);
        Identificar(filas, presentaciones);
        Repetidos(filas, presentaciones);
        await CargarProductosAsync(filas, presentaciones, ct);
        RevisarFilas(filas);
        RevisarIguales(filas);
        RevisarPresentaciones(presentaciones);
        CodigosDeOtros(filas, presentaciones);
        Departamentos(filas);
        Precios(filas, presentaciones);
        foreach (var p in presentaciones)
        {
            if (p.DeNuevo is { Rechazo: { } suyo } nuevo)
                p.Ignorar(PorQueSeIgnora.SuProductoSeIgnoro, $"Su producto nuevo (renglón {nuevo.Numero} de «{Productos}») se ignoró: {suyo.Detalle}");
        }
        Resultado(filas, presentaciones);
    }

    /// <summary>Si la clave es de una variante que sigue en el catálogo.</summary>
    private bool Viva(Guid? clave) => clave is { } c && _vivas.Contains(c);

    /// <summary>
    /// Lo que se dice cuando el código de un renglón ya es de otro producto que el de su clave: el que era al exportar se juntó
    /// con otro, o el código pasó a otro producto después.
    /// </summary>
    private Rechazo? OtroQueElDeSuClave(Guid? clave, Guid hallada, string codigo)
    {
        if (clave is not { } era || era == hallada || !_nombres.ContainsKey(era))
            return null;
        return _juntadas.TryGetValue(era, out var en)
            ? new Rechazo(PorQueSeIgnora.ProductoNoExiste, $"Este renglón era de «{NombreDe(era)}», que se juntó con «{NombreDe(en)}» después de exportar el "
                + "Excel: cambia ese en su propio renglón.")
            : new Rechazo(PorQueSeIgnora.CodigoDeOtroProducto, $"Este renglón era de «{NombreDe(era)}» al exportar el Excel, y el código «{codigo}» ahora es de "
                + $"«{NombreDe(hallada)}»: revísalos en sus fichas.");
    }

    /// <summary>
    /// De qué variante es cada renglón: por su código (uno de su pieza); sin código, por su clave escondida o, si se escribió a
    /// mano, por su nombre y su departamento. Un código que no tiene nadie es un producto nuevo (o el primer código de un producto
    /// que no tenía, si su clave lo dice), salvo que sea uno con los ceros de adelante perdidos por Excel.
    /// </summary>
    private void Identificar(List<Fila> filas, List<FilaDePresentacion> presentaciones)
    {
        foreach (var f in filas)
        {
            if (f.Codigo is { } codigo)
            {
                if (_escaneo.Buscar(codigo) is { } hallado)
                {
                    var presentacion = _presentacionesLigeras[hallado.PresentacionId];
                    if (!presentacion.EsBase)
                        f.Ignorar(PorQueSeIgnora.CodigoDeOtroProducto, $"El código «{codigo}» es de la presentación «{presentacion.Nombre}» de "
                            + $"«{NombreDe(presentacion.VarianteId)}»: en esta hoja va el de su pieza; las presentaciones van en la hoja «{Presentaciones}».");
                    else if (OtroQueElDeSuClave(f.Clave, presentacion.VarianteId, codigo) is { } otro)
                        f.Ignorar(otro);
                    else
                        f.VarianteId = presentacion.VarianteId;
                }
                else if (f.CodigoNumerico && LecturaDeCeldas.ConLosCeros(codigo, _escaneo.Contiene) is { } conCeros)
                    f.Ignorar(SinCeros(codigo, conCeros));
                else if (Viva(f.Clave) && _piezaSinCodigo.Contains(f.Clave!.Value))
                    // El producto no tenía código: este es el primero de su pieza.
                    f.VarianteId = f.Clave;
                else
                {
                    f.EsNuevo = true;
                    if (Viva(f.Clave))
                        f.Avisos.Add($"Este renglón era de «{NombreDe(f.Clave!.Value)}» ({_codigoDe.GetValueOrDefault(f.Clave!.Value)}) al exportar el Excel: si "
                            + $"querías cambiarle el código, deja el suyo en «{ProductosEnExcel.Titulo(CampoDeProducto.Codigo)}» y pon el nuevo en "
                            + $"«{ProductosEnExcel.Titulo(CampoDeProducto.OtrosCodigos)}».");
                    if (f.CodigoNumerico && _sinSusCeros.TryGetValue(codigo, out var conSusCeros))
                        f.Avisos.Add($"Ya hay un producto con el código «{conSusCeros}» («{NombreDe(_escaneo.Buscar(conSusCeros)!.VarianteId)}»): si Excel le "
                            + "quitó los ceros de adelante, escríbelo como texto (con un apóstrofo ' antes) y vuelve a importarlo.");
                }
                continue;
            }
            if (!f.Va)
                continue;
            // Sin código: el renglón exportado trae su clave.
            if (f.Clave is { } clave)
            {
                if (_vivas.Contains(clave))
                {
                    f.VarianteId = clave;
                    continue;
                }
                if (_juntadas.TryGetValue(clave, out var en))
                {
                    f.Ignorar(PorQueSeIgnora.ProductoNoExiste, $"«{NombreDe(clave)}» se juntó con «{NombreDe(en)}» después de exportar el Excel: cambia ese en su "
                        + "propio renglón.");
                    continue;
                }
                // Una clave que este catálogo no conoce (otro Excel): como uno escrito a mano.
            }
            // Escrito a mano: entre los que no tienen código en su pieza, el que se llama así en ese departamento.
            if (f.Producto is null)
            {
                f.Ignorar(PorQueSeIgnora.SinCodigo, "Sin código y sin nombre: no se sabe de qué producto es.");
                continue;
            }
            if (f.Departamento is null)
            {
                f.Ignorar(PorQueSeIgnora.SinCodigo, $"Sin código: para reconocer «{f.Nombre}» por su nombre también va su departamento. Un producto nuevo lleva su "
                    + "código.");
                continue;
            }
            var iguales = _sinCodigo[Texto.Normalizar(f.Nombre)]
                .Where(v => _departamentos.FirstOrDefault(d => d.Id == _departamentoDe.GetValueOrDefault(_nombres[v].ProductoId)) is { } d
                    && Departamento.MismoNombre(d.Nombre, f.Departamento))
                .ToList();
            if (iguales.Count == 1)
                f.VarianteId = iguales[0];
            else
                f.Ignorar(PorQueSeIgnora.SinCodigo, iguales.Count == 0
                    ? $"Sin código, y ningún producto sin código se llama «{f.Nombre}» en «{f.Departamento}». Un producto nuevo lleva su código."
                    : $"Sin código, y hay {iguales.Count} productos sin código que se llaman «{f.Nombre}» en «{f.Departamento}»: cámbialo en su ficha.");
        }

        var nuevos = filas.Where(f => f.EsNuevo).GroupBy(f => f.Codigo!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var p in presentaciones)
        {
            if (!p.Va)
                continue;
            if (p.CodigoDelProducto is not { } codigo)
            {
                if (Viva(p.Clave))
                    DeLaVariante(p, p.Clave!.Value);
                else if (p.Clave is { } clave && _juntadas.TryGetValue(clave, out var en))
                    p.Ignorar(PorQueSeIgnora.ProductoNoExiste, $"«{NombreDe(clave)}» se juntó con «{NombreDe(en)}» después de exportar el Excel.");
                else
                    p.Ignorar(PorQueSeIgnora.SinCodigo, $"Sin «{ProductosEnExcel.Titulo(CampoDePresentacion.CodigoDelProducto)}»: va el código de la pieza del "
                        + $"producto, el de la columna «Código» de la hoja «{Productos}».");
                continue;
            }
            if (_escaneo.Buscar(codigo) is { } hallado)
            {
                var presentacion = _presentacionesLigeras[hallado.PresentacionId];
                if (!presentacion.EsBase)
                    p.Ignorar(PorQueSeIgnora.CodigoDeOtroProducto, $"«{codigo}» es el código de la presentación «{presentacion.Nombre}» de "
                        + $"«{NombreDe(presentacion.VarianteId)}»: en «{ProductosEnExcel.Titulo(CampoDePresentacion.CodigoDelProducto)}» va el de su pieza.");
                else if (OtroQueElDeSuClave(p.Clave, presentacion.VarianteId, codigo) is { } otro)
                    p.Ignorar(otro);
                else
                    DeLaVariante(p, presentacion.VarianteId);
            }
            else if (nuevos.TryGetValue(codigo, out var nuevo))
                p.DeNuevo = nuevo;
            else if (Viva(p.Clave))
                // El código nuevo de un producto que no tenía (su renglón de «Productos» se lo pone).
                DeLaVariante(p, p.Clave!.Value);
            else if (p.CodigoNumerico && LecturaDeCeldas.ConLosCeros(codigo, _escaneo.Contiene) is { } conCeros)
                p.Ignorar(SinCeros(codigo, conCeros));
            else
                p.Ignorar(PorQueSeIgnora.ProductoNoExiste, $"Ningún producto tiene el código «{codigo}», ni es uno nuevo de la hoja «{Productos}».");
        }
    }

    /// <summary>El renglón de «Presentaciones» es de esa variante (se carga completa después).</summary>
    private void DeLaVariante(FilaDePresentacion p, Guid varianteId)
    {
        _variantesDePresentaciones.Add(varianteId);
        p.Variante = new Variante { Id = varianteId };
    }

    private static Rechazo SinCeros(string codigo, string conCeros) =>
        new(PorQueSeIgnora.CodigoSinCeros, $"El código «{codigo}» parece «{conCeros}» sin sus ceros: Excel se los quitó al leerlo como número. "
            + "Escríbelo como texto (con un apóstrofo ' antes) o cópialo del Excel que se exportó.");

    /// <summary>Si el código existe escrito tal cual (con sus mayúsculas) en el catálogo.</summary>
    private bool ExisteTalCual(string codigo) => _escaneo.Buscar(codigo) is { } hallado && string.Equals(hallado.Codigo, codigo, StringComparison.Ordinal);

    /// <summary>
    /// Lo que viene dos veces: la misma variante en dos renglones, un código en dos renglones (de las dos hojas) y la misma
    /// presentación dos veces. Se ignoran todos esos renglones: no se sabe cuál vale. Dos códigos que solo cambian en mayúsculas
    /// cuentan como el mismo (al escanear lo son), salvo que ya existan los dos así, cada uno en su renglón: son de dos productos.
    /// </summary>
    private void Repetidos(List<Fila> filas, List<FilaDePresentacion> presentaciones)
    {
        foreach (var grupo in filas.Where(f => f.VarianteId is not null).GroupBy(f => f.VarianteId!.Value).Where(g => g.Count() > 1))
        {
            var renglones = Lista(grupo.Select(f => f.Numero.ToString(CultureInfo.InvariantCulture)));
            var mismoCodigo = grupo.Select(f => f.Codigo).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 && grupo.First().Codigo is not null;
            foreach (var f in grupo)
                f.Ignorar(mismoCodigo
                    ? new Rechazo(PorQueSeIgnora.CodigoRepetido, $"El código «{f.Codigo}» está en los renglones {renglones}: déjalo en uno solo.")
                    : new Rechazo(PorQueSeIgnora.Repetido, $"«{NombreDe(grupo.Key)}» viene en los renglones {renglones}: déjalo en uno solo."));
        }

        var donde = new Dictionary<string, List<(string Escrito, Renglon Renglon)>>(StringComparer.OrdinalIgnoreCase);
        void Anotar(string codigo, Renglon renglon)
        {
            if (!donde.TryGetValue(codigo, out var lugares))
                donde[codigo] = lugares = [];
            if (!lugares.Contains((codigo, renglon)))
                lugares.Add((codigo, renglon));
        }
        foreach (var f in filas)
        {
            foreach (var codigo in f.Codigos)
                Anotar(codigo, f);
        }
        foreach (var p in presentaciones)
        {
            foreach (var codigo in p.Codigos ?? [])
                Anotar(codigo, p);
        }
        foreach (var (codigo, lugares) in donde)
        {
            var renglones = lugares.Select(l => l.Renglon).Distinct().ToList();
            if (renglones.Count < 2)
                continue;
            var escritos = lugares.GroupBy(l => l.Escrito, StringComparer.Ordinal).ToList();
            if (escritos.Count == renglones.Count && escritos.All(g => g.Select(l => l.Renglon).Distinct().Count() == 1 && ExisteTalCual(g.Key)))
                continue;
            foreach (var (escrito, renglon) in lugares)
                renglon.Ignorar(PorQueSeIgnora.CodigoRepetido, $"El código «{escrito}» está también en "
                    + $"{Lista(renglones.Where(l => l != renglon).Select(l => Lugar(l, renglon.Hoja)))}: un código es de un solo producto.");
        }

        foreach (var grupo in presentaciones.Where(p => p.Nombre is not null && (p.Variante is not null || p.DeNuevo is not null))
                     .GroupBy(p => (Variante: p.Variante?.Id, Nuevo: p.DeNuevo?.Numero, Nombre: Texto.Normalizar(p.Nombre!))).Where(g => g.Count() > 1))
        {
            var renglones = Lista(grupo.Select(p => p.Numero.ToString(CultureInfo.InvariantCulture)));
            foreach (var p in grupo)
                p.Ignorar(PorQueSeIgnora.Repetido, $"«{p.Nombre}» de este producto viene en los renglones {renglones} de «{Presentaciones}»: déjala en uno solo.");
        }
    }

    /// <summary>«el renglón 9», o «el renglón 4 de «Presentaciones»» si es de la otra hoja.</summary>
    private static string Lugar(Renglon renglon, string desde) =>
        renglon.Hoja == desde ? $"el renglón {renglon.Numero}" : $"el renglón {renglon.Numero} de «{renglon.Hoja}»";

    /// <summary>Si la variante tiene precio de menudeo (uno por unidad, sin presentación).</summary>
    private bool TieneMenudeo(Guid varianteId) => _precios.GetValueOrDefault(varianteId)?.Any(p => p.PresentacionId is null) == true;

    /// <summary>Lo de cada renglón que se puede revisar solo: lo que no puede faltar, la unidad, el mínimo y el máximo, las escalas.</summary>
    private void RevisarFilas(List<Fila> filas)
    {
        var hoja = _libro.Productos;
        foreach (var f in filas.Where(f => f.Va))
        {
            if (f.Existente is { } v && f.Suyo is { } p)
            {
                var nombre = Completo(p.Nombre, v.Descripcion);
                if (p.Tipo is not (TipoProducto.Mercancia or TipoProducto.Servicio))
                    f.Ignorar(PorQueSeIgnora.NoSeCambiaDesdeExcel, $"«{nombre}» es un kit o un cobro de terceros: se cambia en su ficha.");
                if (hoja.Trae(CampoDeProducto.Producto) && f.Producto is null)
                    f.Ignorar(PorQueSeIgnora.FaltaDato, $"Le falta su nombre, en «{ProductosEnExcel.Titulo(CampoDeProducto.Producto)}».");
                if (hoja.Trae(CampoDeProducto.Departamento) && f.Departamento is null)
                    f.Ignorar(PorQueSeIgnora.FaltaDato, $"Le falta su departamento, en «{ProductosEnExcel.Titulo(CampoDeProducto.Departamento)}».");
                // Uno que no tiene precio (de eleventa con precio 0) se queda sin él: lo demás sí se cambia.
                if (hoja.Trae(CampoDeProducto.Precio) && f.Precio is null && TieneMenudeo(v.Id))
                    f.Ignorar(PorQueSeIgnora.FaltaDato, $"Le falta su precio, en «{ProductosEnExcel.Titulo(CampoDeProducto.Precio)}».");
                if (f.Unidad is { } unidad && unidad != v.UnidadBase)
                    f.Ignorar(PorQueSeIgnora.NoSeCambiaDesdeExcel, $"«{nombre}» se vende en {ProductosEnExcel.Unidad(v.UnidadBase)}: la unidad no se cambia "
                        + "desde Excel (se cambia en su ficha, en la depuración).");
                if (p.Tipo != TipoProducto.Mercancia && (f.Minimo is not null || f.Maximo is not null))
                    f.Ignorar(PorQueSeIgnora.NoSeCambiaDesdeExcel, $"«{nombre}» es un servicio: no lleva mínimo ni máximo de inventario.");
                if (hoja.Trae(CampoDeProducto.Variante) && f.Descripcion is null && p.Variantes.Count(x => x.JuntadaEnId is null) > 1)
                    f.Ignorar(PorQueSeIgnora.FaltaDato, $"«{p.Nombre}» tiene varias variantes: cada una lleva su descripción, en "
                        + $"«{ProductosEnExcel.Titulo(CampoDeProducto.Variante)}».");
                RevisarCantidades(f, v.UnidadBase, nombre,
                    hoja.Trae(CampoDeProducto.Minimo) ? f.Minimo : v.InventarioMinimo?.EnUnidades,
                    hoja.Trae(CampoDeProducto.Maximo) ? f.Maximo : v.InventarioMaximo?.EnUnidades);
            }
            else if (f.EsNuevo)
            {
                if (f.Producto is null)
                    f.Ignorar(PorQueSeIgnora.FaltaDato, $"Es nuevo (nadie tiene el código «{f.Codigo}») y le falta su nombre, en "
                        + $"«{ProductosEnExcel.Titulo(CampoDeProducto.Producto)}».");
                if (f.Departamento is null)
                    f.Ignorar(PorQueSeIgnora.FaltaDato, $"Es nuevo (nadie tiene el código «{f.Codigo}») y le falta su departamento, en "
                        + $"«{ProductosEnExcel.Titulo(CampoDeProducto.Departamento)}».");
                if (f.Precio is null)
                    f.Ignorar(PorQueSeIgnora.FaltaDato, $"Es nuevo (nadie tiene el código «{f.Codigo}») y le falta su precio, en "
                        + $"«{ProductosEnExcel.Titulo(CampoDeProducto.Precio)}».");
                RevisarCantidades(f, f.Unidad ?? UnidadBase.Pieza, f.Nombre, f.Minimo, f.Maximo);
            }
        }
    }

    /// <summary>Las escalas (desde más de una unidad, sin repetir, en piezas enteras si es por pieza) y el mínimo y el máximo como quedan.</summary>
    private static void RevisarCantidades(Fila f, UnidadBase unidad, string nombre, decimal? minimo, decimal? maximo)
    {
        foreach (var (numero, desde, _) in f.Mayoreo ?? [])
        {
            if (desde <= 1)
                f.Ignorar(PorQueSeIgnora.NumeroInvalido, $"«{ProductosEnExcel.MayoreoDesde(numero)}» dice {Numero(desde)}: el mayoreo va desde más de una "
                    + "(desde 1 es el precio de menudeo).");
            f.Ignorar(LecturaDeCeldas.Enteras(desde, unidad, ProductosEnExcel.MayoreoDesde(numero)));
        }
        if (f.Mayoreo?.GroupBy(m => m.Desde).FirstOrDefault(g => g.Count() > 1) is { } doble)
            f.Ignorar(PorQueSeIgnora.NumeroInvalido, $"Trae dos escalas de mayoreo desde {Numero(doble.Key)}.");
        if (minimo is { } min)
            f.Ignorar(LecturaDeCeldas.Enteras(min, unidad, ProductosEnExcel.Titulo(CampoDeProducto.Minimo)));
        if (maximo is { } max)
            f.Ignorar(LecturaDeCeldas.Enteras(max, unidad, ProductosEnExcel.Titulo(CampoDeProducto.Maximo)));
        if (!f.Va)
            return;
        try
        {
            Variante.RevisarMinimoYMaximo(unidad, minimo is { } a ? Cantidad.Unidades(a) : null, maximo is { } b ? Cantidad.Unidades(b) : null, nombre);
        }
        catch (ArgumentException ex)
        {
            f.Ignorar(PorQueSeIgnora.NumeroInvalido, ex.Message);
        }
    }

    /// <summary>Las variantes de un mismo producto llevan el mismo nombre, el mismo departamento y la misma marca (son del producto).</summary>
    private void RevisarIguales(List<Fila> filas)
    {
        var hoja = _libro.Productos;
        foreach (var grupo in filas.Where(f => f.Va && f.Suyo is not null).GroupBy(f => f.Suyo!.Id).Where(g => g.Count() > 1))
        {
            string? distinto = null;
            if (hoja.Trae(CampoDeProducto.Producto) && grupo.Select(f => LecturaDeCeldas.Limpio(f.Producto ?? "")).Distinct(StringComparer.Ordinal).Count() > 1)
                distinto = $"nombre, en «{ProductosEnExcel.Titulo(CampoDeProducto.Producto)}»";
            else if (hoja.Trae(CampoDeProducto.Departamento) && grupo.Select(f => Texto.Normalizar(f.Departamento ?? "")).Distinct().Count() > 1)
                distinto = "departamento";
            else if (hoja.Trae(CampoDeProducto.Marca) && grupo.Select(f => Texto.Normalizar(f.Marca ?? "")).Distinct().Count() > 1)
                distinto = "marca";
            if (distinto is null)
                continue;
            var renglones = Lista(grupo.Select(f => f.Numero.ToString(CultureInfo.InvariantCulture)));
            foreach (var f in grupo)
                f.Ignorar(PorQueSeIgnora.DatosDistintos, $"Las variantes de «{f.Suyo!.Nombre}» (renglones {renglones}) traen distinto {distinto}: es del "
                    + "producto, así que va igual en todas.");
        }
    }

    /// <summary>
    /// De qué presentación es cada renglón de «Presentaciones» (por su nombre, sin importar mayúsculas ni acentos), y lo que no se
    /// cambia desde Excel: lo que contiene una que ya existe, usar el nombre de su pieza, o las de un kit o un cobro de terceros.
    /// </summary>
    private void RevisarPresentaciones(List<FilaDePresentacion> presentaciones)
    {
        foreach (var p in presentaciones.Where(p => p.Va))
        {
            if (p.Variante is { Presentaciones.Count: 0 })
            {
                // No se pudo cargar (otra caja la quitó mientras tanto): como si no existiera.
                p.Ignorar(PorQueSeIgnora.ProductoNoExiste, $"Ningún producto tiene el código «{p.CodigoDelProducto}».");
                continue;
            }
            if (p.Variante is { } variante && _productos.TryGetValue(variante.ProductoId, out var suyo) && suyo.Tipo is not (TipoProducto.Mercancia or TipoProducto.Servicio))
            {
                p.Ignorar(PorQueSeIgnora.NoSeCambiaDesdeExcel, $"«{Completo(suyo.Nombre, variante.Descripcion)}» es un kit o un cobro de terceros: sus "
                    + "presentaciones se cambian en su ficha.");
                continue;
            }
            if (p.Nombre is not { } nombre)
            {
                p.Ignorar(PorQueSeIgnora.FaltaDato, $"Le falta el nombre de la presentación, en «{ProductosEnExcel.Titulo(CampoDePresentacion.Presentacion)}».");
                continue;
            }
            var unidad = p.Variante?.UnidadBase ?? p.DeNuevo?.Unidad ?? UnidadBase.Pieza;
            var pieza = p.Variante?.Presentaciones.FirstOrDefault(x => x.EsBase)?.Nombre ?? ProductosEnExcel.PresentacionBase(unidad);
            if (Texto.Normalizar(nombre) == Texto.Normalizar(pieza))
            {
                p.Ignorar(PorQueSeIgnora.NoSeCambiaDesdeExcel, $"«{pieza}» es su presentación base: su precio y sus códigos van en la hoja «{Productos}».");
                continue;
            }
            p.Existente = p.Variante?.Presentaciones.FirstOrDefault(x => !x.EsBase && Texto.Normalizar(x.Nombre) == Texto.Normalizar(nombre));
            if (p.Existente is { } existente)
            {
                if (p.Contiene is { } contiene && contiene != existente.Factor.EnUnidades)
                    p.Ignorar(PorQueSeIgnora.NoSeCambiaDesdeExcel, $"«{existente.Nombre}» trae {existente.Factor} {ProductosEnExcel.Unidad(unidad)}: lo que "
                        + "trae una presentación se cambia en su ficha.");
            }
            else if (p.Contiene is not { } contiene)
                p.Ignorar(PorQueSeIgnora.FaltaDato, $"«{nombre}» es nueva: le falta cuántas {ProductosEnExcel.Unidad(unidad)} trae, en "
                    + $"«{ProductosEnExcel.Titulo(CampoDePresentacion.Contiene)}».");
            else if (contiene <= 0)
                p.Ignorar(PorQueSeIgnora.NumeroInvalido, $"«{ProductosEnExcel.Titulo(CampoDePresentacion.Contiene)}» va mayor que cero.");
            else
                p.Ignorar(LecturaDeCeldas.Enteras(contiene, unidad, ProductosEnExcel.Titulo(CampoDePresentacion.Contiene)));
            foreach (var (numero, desde, _) in p.Mayoreo ?? [])
            {
                if (desde <= 1 || desde != decimal.Truncate(desde))
                    p.Ignorar(PorQueSeIgnora.NumeroInvalido, $"«{ProductosEnExcel.MayoreoDesde(numero)}» dice {Numero(desde)}: el mayoreo de una presentación va "
                        + "desde 2 o más, en presentaciones enteras.");
            }
            if (p.Mayoreo?.GroupBy(m => m.Desde).FirstOrDefault(g => g.Count() > 1) is { } doble)
                p.Ignorar(PorQueSeIgnora.NumeroInvalido, $"Trae dos escalas de mayoreo desde {Numero(doble.Key)}.");
        }
    }

    /// <summary>Un código es de un solo producto: el que ya tiene otra variante (u otra presentación) no se puede poner aquí.</summary>
    private void CodigosDeOtros(List<Fila> filas, List<FilaDePresentacion> presentaciones)
    {
        foreach (var f in filas.Where(f => f.Va))
        {
            foreach (var codigo in f.Codigos)
            {
                if (_escaneo.Buscar(codigo) is { } hallado)
                {
                    var presentacion = _presentacionesLigeras[hallado.PresentacionId];
                    if (f.Existente?.Id == presentacion.VarianteId)
                    {
                        if (!presentacion.EsBase)
                            f.Ignorar(PorQueSeIgnora.CodigoDeOtroProducto, $"El código «{codigo}» es de su presentación «{presentacion.Nombre}»: los códigos de "
                                + $"las presentaciones van en la hoja «{Presentaciones}».");
                    }
                    else
                        f.Ignorar(PorQueSeIgnora.CodigoDeOtroProducto, $"El código «{codigo}» ya es de «{NombreDe(presentacion.VarianteId)}»: un código es de un solo producto.");
                }
                else if (codigo != f.Codigo && f.OtrosNumerico && LecturaDeCeldas.ConLosCeros(codigo, _escaneo.Contiene) is { } conCeros)
                    f.Ignorar(SinCeros(codigo, conCeros));
            }
        }
        foreach (var p in presentaciones.Where(p => p.Va))
        {
            foreach (var codigo in p.Codigos ?? [])
            {
                if (_escaneo.Buscar(codigo) is { } hallado)
                {
                    if (p.Existente?.Id != hallado.PresentacionId)
                        p.Ignorar(PorQueSeIgnora.CodigoDeOtroProducto, hallado.VarianteId == p.Variante?.Id
                            ? $"El código «{codigo}» es de «{_presentacionesLigeras[hallado.PresentacionId].Nombre}» de este mismo producto."
                            : $"El código «{codigo}» ya es de «{NombreDe(hallado.VarianteId)}»: un código es de un solo producto.");
                }
                else if (p.CodigosNumerico && LecturaDeCeldas.ConLosCeros(codigo, _escaneo.Contiene) is { } conCeros)
                    p.Ignorar(SinCeros(codigo, conCeros));
            }
        }
    }

    /// <summary>
    /// A qué departamento va cada renglón (por su nombre, sin importar mayúsculas, acentos ni espacios de más). Uno que no existe se
    /// da de alta, si quien importa puede (y el nombre cabe). Uno desactivado no recibe productos activos, como en la ficha: ni
    /// nuevos, ni que se pasen a él, ni que se reactiven en él.
    /// </summary>
    private void Departamentos(List<Fila> filas)
    {
        var quiere = filas.Where(f => f.Va && f.Existente is not null && f.SeVende is not null).GroupBy(f => f.Existente!.Id)
            .ToDictionary(g => g.Key, g => g.First().SeVende!.Value);
        foreach (var f in filas.Where(f => f.Va && (f.Existente is not null || f.EsNuevo)))
        {
            var actual = f.Suyo is { } producto ? _departamentos.FirstOrDefault(d => d.Id == producto.DepartamentoId) : null;
            var destino = actual;
            if (f.Departamento is { } nombre)
            {
                var limpio = Departamento.NombreLimpio(nombre);
                var iguales = _departamentos.Where(d => Departamento.MismoNombre(d.Nombre, limpio)).ToList();
                destino = actual is not null && iguales.Contains(actual) ? actual : iguales.OrderByDescending(d => d.Activo).FirstOrDefault();
                if (destino is null)
                {
                    if (!_puedeDepartamentos)
                        f.Ignorar(PorQueSeIgnora.DepartamentoNoExiste, $"No hay un departamento «{limpio}», y lo da de alta quien tiene «Productos, precios y etiquetas».");
                    else if (limpio.Length > Departamento.LargoNombre)
                        f.Ignorar(PorQueSeIgnora.DepartamentoNoExiste, $"No hay un departamento «{limpio}», y no se puede dar de alta: su nombre va de hasta "
                            + $"{Departamento.LargoNombre} letras.");
                    else
                        f.DepartamentoNuevo = limpio;
                    continue;
                }
            }
            f.Destino = destino;
            var activo = f.Suyo is { } suyo ? SeVendeAlFinal(suyo, v => quiere.TryGetValue(v.Id, out var q) ? q : null) : f.SeVende ?? true;
            if (destino is { Activo: false } && activo && (f.Suyo is null || f.Suyo.DepartamentoId != destino.Id || !f.Suyo.Activo))
                f.Ignorar(PorQueSeIgnora.DepartamentoDesactivado, $"El departamento «{destino.Nombre}» está desactivado: reactívalo en Productos › "
                    + "Departamentos o elige otro.");
        }
    }

    /// <summary>
    /// Si el producto se vende al final: alguna de sus variantes se vende, cada una como lo pida su renglón (<paramref name="pide"/>,
    /// vacío si no dice nada) o como está.
    /// </summary>
    private static bool SeVendeAlFinal(Producto producto, Func<Variante, bool?> pide) =>
        producto.Variantes.Where(v => v.JuntadaEnId is null).Any(v => pide(v) ?? (producto.Activo && v.Activa));

    // ── Los precios, en orden ────────────────────────────────────────────────

    /// <summary>Un precio como queda: desde cuántas, a cuánto, y si cambia (o es nuevo).</summary>
    private sealed record PrecioFinal(Cantidad Desde, long Precio, bool Cambia);

    /// <summary>
    /// Una presentación con su precio propio (y sus escalas propias) como quedan. <paramref name="SinPrecio"/>: el renglón le quitó
    /// su precio pero le dejó escalas.
    /// </summary>
    private sealed record PaqueteFinal(string Nombre, Cantidad Factor, List<PrecioFinal> Propios, bool SinPrecio = false);

    /// <summary>
    /// Con todo lo que trae el Excel de cada variante (sus dos hojas), cómo quedan sus precios; si algo de eso cambia, que quede en
    /// orden. Si no, se ignoran los renglones de esa variante que cambian sus precios, con el porqué.
    /// </summary>
    private void Precios(List<Fila> filas, List<FilaDePresentacion> presentaciones)
    {
        var hojaPresentaciones = _libro.Presentaciones;
        var filaDe = DeCadaVariante(filas);
        var presentacionesDe = presentaciones.Where(x => x.Va && x.Variante is not null).ToLookup(x => x.Variante!.Id);
        foreach (var variante in _variantes.Values)
        {
            var f = filaDe.GetValueOrDefault(variante.Id);
            var suyas = presentacionesDe[variante.Id].ToList();
            if (f is null && suyas.Count == 0)
                continue;
            var actuales = _precios.GetValueOrDefault(variante.Id) ?? [];
            var porUnidad = actuales.Where(p => p.PresentacionId is null).OrderBy(p => p.CantidadMinima).ToList();
            var menudeoActual = porUnidad.FirstOrDefault();
            PrecioFinal? menudeo = f?.Precio is { } precio
                ? new PrecioFinal(menudeoActual?.CantidadMinima ?? Una, precio, precio != menudeoActual?.PrecioCentavos)
                : menudeoActual is null ? null : new PrecioFinal(menudeoActual.CantidadMinima, menudeoActual.PrecioCentavos, false);
            var escalas = f?.Mayoreo is { } mayoreo
                ? mayoreo.Select(m => Final(Cantidad.Unidades(m.Desde), m.Precio, porUnidad.Skip(1))).ToList()
                : porUnidad.Skip(1).Select(p => new PrecioFinal(p.CantidadMinima, p.PrecioCentavos, false)).ToList();
            var paquetes = new List<PaqueteFinal>();
            foreach (var presentacion in variante.Presentaciones.Where(p => !p.EsBase))
            {
                var propios = actuales.Where(p => p.PresentacionId == presentacion.Id).OrderBy(p => p.CantidadMinima).ToList();
                var fila = suyas.FirstOrDefault(x => x.Existente == presentacion);
                paquetes.Add(fila is null || hojaPresentaciones is null
                    ? new PaqueteFinal(presentacion.Nombre, presentacion.Factor, propios.Select(p => new PrecioFinal(p.CantidadMinima, p.PrecioCentavos, false)).ToList())
                    : Paquete(presentacion.Nombre, presentacion.Factor, fila, hojaPresentaciones, propios));
            }
            foreach (var nueva in suyas.Where(x => x.Existente is null && x.Contiene is > 0))
                paquetes.Add(Paquete(nueva.Nombre!, Cantidad.Unidades(nueva.Contiene!.Value), nueva, hojaPresentaciones!, []));
            // Lo que cambia sus precios: el renglón de «Productos» (el menudeo o el mayoreo) y los de «Presentaciones» (su precio propio).
            var cambiaLaFila = menudeo is { Cambia: true } || escalas.Any(e => e.Cambia)
                || (f?.Mayoreo is not null && porUnidad.Skip(1).Any(p => escalas.All(e => e.Desde != p.CantidadMinima)));
            var cambianLasSuyas = suyas.Where(x => x.Existente is null
                || paquetes.FirstOrDefault(p => p.Nombre == x.Existente.Nombre)?.Propios.Any(y => y.Cambia) == true
                || (hojaPresentaciones?.Trae(CampoDePresentacion.Precio) == true && x.Precio is null
                    && actuales.Any(p => p.PresentacionId == x.Existente.Id))).ToList();
            if (!cambiaLaFila && cambianLasSuyas.Count == 0)
                continue;
            var costo = _costos.TryGetValue(variante.Id, out var c) ? c : (decimal?)null;
            if (RevisarOrden(menudeo, escalas, paquetes, costo, variante.UnidadBase) is { } rechazo)
            {
                // Se ignoran los renglones que cambian sus precios; lo que no los toca no tiene la culpa.
                if (cambiaLaFila)
                    f?.Ignorar(rechazo);
                foreach (var p in cambianLasSuyas)
                    p.Ignorar(rechazo);
            }
        }
        var deLosNuevos = presentaciones.Where(x => x.Va && x.DeNuevo is not null).ToLookup(x => x.DeNuevo!);
        foreach (var f in filas.Where(x => x.Va && x.EsNuevo))
        {
            var suyas = deLosNuevos[f].ToList();
            var paquetes = suyas.Where(x => x.Contiene is > 0)
                .Select(x => Paquete(x.Nombre!, Cantidad.Unidades(x.Contiene!.Value), x, hojaPresentaciones!, [])).ToList();
            var menudeo = f.Precio is { } precio ? new PrecioFinal(Una, precio, true) : null;
            var escalas = (f.Mayoreo ?? []).Select(m => new PrecioFinal(Cantidad.Unidades(m.Desde), m.Precio, true)).ToList();
            if (RevisarOrden(menudeo, escalas, paquetes, null, f.Unidad ?? UnidadBase.Pieza) is { } rechazo)
            {
                f.Ignorar(rechazo);
                foreach (var p in suyas)
                    p.Ignorar(rechazo);
            }
        }
    }

    /// <summary>Un precio como lo pide el archivo: cambia si no había uno desde esa cantidad, o si era otro.</summary>
    private static PrecioFinal Final(Cantidad desde, long precio, IEnumerable<Precio> actuales) =>
        new(desde, precio, actuales.FirstOrDefault(p => p.CantidadMinima == desde)?.PrecioCentavos != precio);

    /// <summary>El renglón de «Productos» que se importa de cada variante que existe (uno solo: los repetidos ya se ignoraron).</summary>
    private static Dictionary<Guid, Fila> DeCadaVariante(List<Fila> filas) =>
        filas.Where(x => x.Va && x.Existente is not null).GroupBy(x => x.Existente!.Id).ToDictionary(g => g.Key, g => g.First());

    private static PaqueteFinal Paquete(string nombre, Cantidad factor, FilaDePresentacion fila, HojaLeida<CampoDePresentacion> hoja, List<Precio> propios)
    {
        var cadena = CadenaDeseada(fila, hoja, propios);
        var sinPrecio = hoja.Trae(CampoDePresentacion.Precio) && fila.Precio is null && cadena.Count > 0;
        return new PaqueteFinal(nombre, factor, cadena, sinPrecio);
    }

    /// <summary>
    /// El precio propio de una presentación (y sus escalas propias) como lo pide su renglón: una columna que no viene deja lo que
    /// tenía; «Precio» vacío quita su precio propio (se cobra por sus piezas). «Precio» es el más bajo de los suyos, desde las que
    /// diga (casi siempre una: así lo exporta el Excel; si el suyo va desde 2, sigue desde 2), y las escalas, lo demás.
    /// </summary>
    private static List<PrecioFinal> CadenaDeseada(FilaDePresentacion fila, HojaLeida<CampoDePresentacion> hoja, List<Precio> propios)
    {
        var cadena = new List<PrecioFinal>();
        var primero = propios.MinBy(p => p.CantidadMinima);
        // El de «Precio» sigue desde las mismas que el suyo, salvo que una escala del renglón empiece antes: entonces va desde una.
        var desde = primero is { } p1 && (fila.Mayoreo ?? []).All(m => Cantidad.Unidades(m.Desde) > p1.CantidadMinima) ? p1.CantidadMinima : Una;
        if (hoja.Trae(CampoDePresentacion.Precio))
        {
            if (fila.Precio is { } precio)
                cadena.Add(new PrecioFinal(desde, precio, propios.FirstOrDefault(p => p.CantidadMinima == desde)?.PrecioCentavos != precio));
        }
        else if (primero is not null)
            cadena.Add(new PrecioFinal(primero.CantidadMinima, primero.PrecioCentavos, false));
        if (fila.Mayoreo is { } mayoreo)
            cadena.AddRange(mayoreo.Select(m => Final(Cantidad.Unidades(m.Desde), m.Precio, propios)));
        else
            cadena.AddRange(propios.Where(p => p != primero).Select(p => new PrecioFinal(p.CantidadMinima, p.PrecioCentavos, false)));
        // Lo que se quita también cambia.
        if (propios.Any(p => cadena.All(x => x.Desde != p.CantidadMinima)) && cadena.Count > 0)
            cadena[0] = cadena[0] with { Cambia = true };
        return [.. cadena.OrderBy(p => p.Desde)];
    }

    /// <summary>
    /// Que lo que se cobra quede en orden: el mayoreo más barato que el menudeo (y cada escala, igual o más barata que la de menos
    /// piezas); el precio propio de una presentación, nunca más caro que sus piezas sueltas (y sus escalas, igual o más baratas);
    /// y lo que cambia, nunca debajo de su costo promedio. Vacío si todo cabe.
    /// </summary>
    private Rechazo? RevisarOrden(PrecioFinal? menudeo, List<PrecioFinal> escalas, List<PaqueteFinal> paquetes, decimal? costo, UnidadBase unidad)
    {
        if (menudeo is null)
            return escalas.Count > 0 || paquetes.Any(p => p.Propios.Count > 0)
                ? new Rechazo(PorQueSeIgnora.FaltaDato, "Le falta su precio de menudeo (desde una), y su mayoreo se compara con él.")
                : null;
        var ordenadas = escalas.OrderBy(e => e.Desde).ToList();
        foreach (var escala in ordenadas)
        {
            if (escala.Precio >= menudeo.Precio)
                return new Rechazo(PorQueSeIgnora.MayoreoMasCaro, $"Su mayoreo desde {escala.Desde} ({Pesos(escala.Precio)}) quedaría igual o más caro que su "
                    + $"precio de {Pesos(menudeo.Precio)}: el mayoreo va más barato. Cámbialo también en las columnas del mayoreo.");
        }
        for (var i = 1; i < ordenadas.Count; i++)
        {
            if (ordenadas[i].Precio > ordenadas[i - 1].Precio)
                return new Rechazo(PorQueSeIgnora.MayoreoMasCaro, $"Su mayoreo desde {ordenadas[i].Desde} ({Pesos(ordenadas[i].Precio)}) quedaría más caro que "
                    + $"el de desde {ordenadas[i - 1].Desde} ({Pesos(ordenadas[i - 1].Precio)}): con más, igual o más barato.");
        }
        foreach (var paquete in paquetes.Where(p => p.Propios.Count > 0))
        {
            var cadena = paquete.Propios.OrderBy(p => p.Desde).ToList();
            // Sin «Precio» pero con escalas: el cobro tomaría la escala más baja también para una sola (CalculoPrecio).
            if (paquete.SinPrecio && cadena.Any(p => p.Cambia))
                return new Rechazo(PorQueSeIgnora.FaltaDato, $"«{paquete.Nombre}» tiene mayoreo propio pero no su precio, en «Precio».");
            // La primera es la de una sola presentación (el cobro la toma también para menos de las que dice).
            var tope = paquete.Factor.Importe(menudeo.Precio);
            if (cadena[0].Precio > tope)
                return new Rechazo(PorQueSeIgnora.MayoreoMasCaro, $"«{paquete.Nombre}» a {Pesos(cadena[0].Precio)} quedaría más cara que sus {paquete.Factor} "
                    + $"{ProductosEnExcel.Unidad(unidad)} por separado ({Pesos(tope)}).");
            for (var i = 1; i < cadena.Count; i++)
            {
                if (cadena[i].Precio > cadena[i - 1].Precio)
                    return new Rechazo(PorQueSeIgnora.MayoreoMasCaro, $"El mayoreo de «{paquete.Nombre}» desde {cadena[i].Desde} ({Pesos(cadena[i].Precio)}) "
                        + $"quedaría más caro que el de desde {cadena[i - 1].Desde} ({Pesos(cadena[i - 1].Precio)}).");
            }
        }
        if (costo is not { } c)
            return null;
        Rechazo Debajo(string que, long precio) =>
            new(PorQueSeIgnora.DebajoDelCosto, $"{que} de {Pesos(precio)} quedaría debajo de su costo promedio"
                + (_conMargen ? $" ({Pesos((long)Math.Round(c, MidpointRounding.AwayFromZero))} por {Singular(unidad)})" : "")
                + ": para venderlo debajo del costo, cámbialo en su ficha.");
        if (menudeo.Cambia && menudeo.Precio < c)
            return Debajo("Su precio", menudeo.Precio);
        if (ordenadas.FirstOrDefault(e => e.Cambia && e.Precio < c) is { } baja)
            return Debajo($"Su mayoreo desde {baja.Desde}", baja.Precio);
        foreach (var paquete in paquetes)
        {
            if (paquete.Propios.FirstOrDefault(p => p.Cambia && p.Precio < c * paquete.Factor.EnUnidades) is { } barato)
                return Debajo($"«{paquete.Nombre}»{(barato.Desde == Una ? "" : $" desde {barato.Desde}")}", barato.Precio);
        }
        return null;
    }

    // ── Lo que cambia ────────────────────────────────────────────────────────

    /// <summary>Los departamentos y las marcas que se darían de alta (uno por nombre), y los que de verdad se dan: los que usa lo que se aplica.</summary>
    private readonly Dictionary<string, Departamento> _departamentosNuevos = [];

    private readonly Dictionary<string, Marca> _marcasNuevas = [];
    private readonly HashSet<Departamento> _departamentosQueVan = [];
    private readonly HashSet<Marca> _marcasQueVan = [];

    /// <summary>Lo que cambia de una variante (de sus renglones de las dos hojas), con lo que lo cambia, antes de saber si se aplica.</summary>
    private sealed class CambiosDeVariante(Variante variante, Fila? fila, List<FilaDePresentacion> presentaciones)
    {
        public Variante Variante => variante;
        public Fila? Fila => fila;
        public List<FilaDePresentacion> Presentaciones => presentaciones;
        public List<CambioDeDatoDto> Cambios { get; } = [];
        public List<Action> Acciones { get; } = [];
        public HashSet<Departamento> Departamentos { get; } = [];
        public HashSet<Marca> Marcas { get; } = [];

        /// <summary>Dónde va «Se vende» entre lo que cambia: después de la marca, como en el Excel.</summary>
        public int LugarDeSeVende { get; set; }

        public long? MenudeoAntes { get; set; }
        public long? MenudeoDespues { get; set; }

        /// <summary>Si cambió después de exportar el Excel: qué (vacío si no).</summary>
        public IReadOnlyList<string>? CambioDespues { get; set; }

        /// <summary>Si se aplica: no cambió después de exportar, o se pidió aplicarlo de todos modos.</summary>
        public bool SeAplica { get; set; } = true;

        public void Agregar(CambioDeDatoDto cambio, Action accion)
        {
            Cambios.Add(cambio);
            Acciones.Add(accion);
        }
    }

    /// <summary>El departamento al que va el renglón: el que existe o, si se da de alta, el nuevo (uno solo por nombre).</summary>
    private Departamento DestinoDe(Fila f, HashSet<Departamento> nuevos)
    {
        if (f.DepartamentoNuevo is not { } nombre)
            return f.Destino!;
        var clave = Texto.Normalizar(nombre);
        if (!_departamentosNuevos.TryGetValue(clave, out var nuevo))
            _departamentosNuevos[clave] = nuevo = new Departamento { Nombre = nombre };
        nuevos.Add(nuevo);
        return nuevo;
    }

    /// <summary>La marca, por su nombre (sin importar mayúsculas ni acentos): la que existe o una nueva (una sola por nombre).</summary>
    private Guid? MarcaDe(string? nombre, HashSet<Marca> nuevas)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            return null;
        var clave = Texto.Normalizar(nombre);
        if (_marcas.TryGetValue(clave, out var marca))
            return marca.Id;
        if (!_marcasNuevas.TryGetValue(clave, out var nueva))
            _marcasNuevas[clave] = nueva = new Marca { Nombre = nombre };
        nuevas.Add(nueva);
        return nueva.Id;
    }

    private string? NombreDeMarca(Guid? id) =>
        id is { } m ? _marcas.Values.FirstOrDefault(x => x.Id == m)?.Nombre ?? _marcasNuevas.Values.FirstOrDefault(x => x.Id == m)?.Nombre : null;

    private void Resultado(List<Fila> filas, List<FilaDePresentacion> presentaciones)
    {
        var cambian = new List<(int Orden, ProductoQueCambiaDto Dto)>();
        var despues = new List<(int Orden, ProductoQueCambiaDto Dto)>();
        var nuevos = new List<ProductoNuevoDeExcelDto>();
        var huella = new StringBuilder();

        // Lo que cambia, por variante: primero en el orden de «Productos», luego lo que solo trae «Presentaciones». Cada producto
        // junto: «Se vende» es de todas sus variantes.
        var variantes = filas.Where(f => f.Va && f.Existente is not null).Select(f => f.Existente!)
            .Concat(presentaciones.Where(p => p.Va && p.Variante is not null && _variantes.ContainsKey(p.Variante.Id)).Select(p => _variantes[p.Variante!.Id]))
            .Distinct().ToList();
        var orden = variantes.Select((v, i) => (v.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var filaDe = DeCadaVariante(filas);
        var presentacionesDe = presentaciones.Where(x => x.Va && x.Variante is not null).ToLookup(x => x.Variante!.Id);
        foreach (var delProducto in variantes.GroupBy(v => v.ProductoId))
        {
            var producto = _productos[delProducto.Key];
            var deCada = delProducto.Select(v => new CambiosDeVariante(v, filaDe.GetValueOrDefault(v.Id), presentacionesDe[v.Id].ToList())).ToList();
            foreach (var c in deCada)
            {
                if (c.Fila is { } f)
                {
                    CambiosDeLaFila(f, producto, c);
                    f.SinCambios = c.Cambios.Count == 0;
                }
                foreach (var p in c.Presentaciones)
                {
                    var antes = c.Cambios.Count;
                    CambiosDeLaPresentacion(p, c);
                    p.SinCambios = c.Cambios.Count == antes;
                }
            }
            // Si cambió después de exportar el Excel (lo que trae «Se vende», también cuenta); sin pedirlo, eso no se aplica.
            bool Ahora(Variante v) => producto.Activo && v.Activa;
            foreach (var c in deCada)
            {
                var cambiaSiSeVende = c.Fila?.SeVende is { } quiere && quiere != Ahora(c.Variante);
                if (c.Cambios.Count == 0 && !cambiaSiSeVende)
                    continue;
                c.CambioDespues = CambioDespues(producto, c);
                c.SeAplica = c.CambioDespues is null || _s.ConLosQueCambiaron;
            }
            SeVende(producto, deCada);

            foreach (var c in deCada)
            {
                if (c.Cambios.Count == 0)
                    continue;
                if (c.SeAplica && producto.PorCompletar && _completados.Add(producto.Id))
                    c.Agregar(new CambioDeDatoDto("Por completar", "Sí", "No"), () => Seguido(producto).PorCompletar = false);
                int? margenAntes = null, margenDespues = null, minimo = null;
                if (_conMargen && c.MenudeoAntes is > 0 && c.MenudeoDespues is > 0 && _costos.TryGetValue(c.Variante.Id, out var costo))
                    (margenAntes, margenDespues, minimo) = (Margen.Calcular(c.MenudeoAntes.Value, costo), Margen.Calcular(c.MenudeoDespues.Value, costo),
                        _margenesMinimos.GetValueOrDefault(producto.DepartamentoId) ?? _margenGeneral);
                var (hoja, numero) = c.Fila is { } fila ? (Productos, fila.Numero) : (Presentaciones, c.Presentaciones[0].Numero);
                var codigo = c.Fila?.Codigo ?? c.Presentaciones.FirstOrDefault()?.CodigoDelProducto ?? _codigoDe.GetValueOrDefault(c.Variante.Id);
                var dto = new ProductoQueCambiaDto(producto.Id, c.Variante.Id, hoja, numero, codigo, Completo(producto.Nombre, c.Variante.Descripcion),
                    c.Cambios, margenAntes, margenDespues, minimo, c.CambioDespues);
                huella.Append(c.SeAplica ? "C|" : "S|").Append(c.Variante.Id.ToString("N"));
                foreach (var x in c.Cambios)
                    huella.Append('|').Append(x.Dato).Append('=').Append(x.Antes).Append('>').Append(x.Despues);
                huella.Append('\n');
                if (!c.SeAplica)
                {
                    despues.Add((orden[c.Variante.Id], dto));
                    continue;
                }
                cambian.Add((orden[c.Variante.Id], dto));
                _acciones.AddRange(c.Acciones);
                _departamentosQueVan.UnionWith(c.Departamentos);
                _marcasQueVan.UnionWith(c.Marcas);
            }
        }

        // Los productos nuevos, en el orden del archivo.
        var deLosNuevos = presentaciones.Where(x => x.Va && x.DeNuevo is not null).ToLookup(x => x.DeNuevo!);
        foreach (var f in filas.Where(x => x.Va && x.EsNuevo))
        {
            var suyas = deLosNuevos[f].ToList();
            var (nuevo, firma) = Nuevo(f, suyas);
            nuevos.Add(nuevo);
            f.SinCambios = false;
            foreach (var p in suyas)
                p.SinCambios = false;
            huella.Append("N|").Append(firma).Append('\n');
        }

        // Los departamentos y las marcas nuevos, solo los que usa lo que se aplica.
        foreach (var departamento in _departamentosQueVan)
            _acciones.Add(() => _db.Departamentos.Add(departamento));
        foreach (var marca in _marcasQueVan)
            _acciones.Add(() => _db.Marcas.Add(marca));

        var ignorados = filas.Cast<Renglon>().Concat(presentaciones).Where(r => !r.Va)
            .Select(r => r switch
            {
                Fila f => new RenglonIgnoradoDto(r.Hoja, r.Numero, f.Codigo, f.Existente is { } v ? NombreDe(v.Id) : f.Producto is null ? null : f.Nombre,
                    r.Rechazo!.PorQue, r.Rechazo.Detalle),
                FilaDePresentacion p => new RenglonIgnoradoDto(r.Hoja, r.Numero, p.CodigoDelProducto, p.Nombre, r.Rechazo!.PorQue, r.Rechazo.Detalle),
                _ => throw new InvalidOperationException(),
            })
            .OrderBy(r => r.Hoja == Productos ? 0 : 1).ThenBy(r => r.Renglon).ToList();
        var sinCambios = filas.Count(f => f.Va && f.SinCambios) + presentaciones.Count(p => p.Va && p.SinCambios);
        var departamentosNuevos = _departamentosQueVan.Select(d => d.Nombre).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        var marcasNuevas = _marcasQueVan.Select(m => m.Nombre).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var d in departamentosNuevos)
            huella.Append("D|").Append(d).Append('\n');
        foreach (var m in marcasNuevas)
            huella.Append("M|").Append(m).Append('\n');

        // Primero lo que conviene revisar (lo que baja o sube a más del doble, lo que regresa lo de antes, lo que deja de venderse…),
        // cada grupo en el orden del archivo.
        var listos = cambian.OrderBy(x => x.Dto.Revisar ? 0 : 1).ThenBy(x => x.Orden).Select(x => x.Dto).ToList();
        var aparte = despues.OrderBy(x => x.Orden).Select(x => x.Dto).ToList();
        Vista = new VistaImportacionDto(_s.Archivo, filas.Count + presentaciones.Count, listos, listos.Count, nuevos, nuevos.Count, ignorados, ignorados.Count,
            sinCambios, departamentosNuevos, marcasNuevas, Avisos(listos, aparte), _conMargen,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(huella.ToString())))[..32], aparte, _s.ConLosQueCambiaron);
        Detalle = new DetalleDeImportacionDto(_s.Archivo, _exportado?.Version, listos, nuevos, departamentosNuevos, marcasNuevas, ignorados.Count, aparte.Count);
    }

    private readonly HashSet<Guid> _completados = [];

    /// <summary>
    /// «Se vende», de todo el producto: cada variante como lo pide su renglón (si se aplica) o como está. Si alguna se vende, el
    /// producto se vende, con cada variante como quedó (las que no tienen renglón y no se vendían, siguen sin venderse); si
    /// ninguna, el producto deja de venderse (y sus variantes se quedan como estaban, para cuando se reactive en su ficha).
    /// </summary>
    private void SeVende(Producto producto, List<CambiosDeVariante> deCada)
    {
        var vivas = producto.Variantes.Where(v => v.JuntadaEnId is null).ToList();
        bool Ahora(Variante v) => producto.Activo && v.Activa;
        bool Queda(Variante v) => deCada.FirstOrDefault(c => c.Variante == v) is { SeAplica: true, Fila.SeVende: { } quiere } ? quiere : Ahora(v);
        var cambian = vivas.Where(v => Queda(v) != Ahora(v)).ToList();
        if (cambian.Count == 0)
            return;
        var seVende = vivas.Any(Queda);
        foreach (var v in cambian)
        {
            var c = deCada.First(x => x.Variante == v);
            c.Cambios.Insert(c.LugarDeSeVende, new CambioDeDatoDto("Se vende", SiNo(Ahora(v)), SiNo(Queda(v)), PorQueSeRevisa.SeVende));
            c.Fila!.SinCambios = false;
        }
        var primera = deCada.First(c => c.Variante == cambian[0]);
        if (!seVende)
        {
            primera.Acciones.Add(() => Seguido(producto).Activo = false);
            return;
        }
        // Las que siguen sin venderse al volver a vender el producto: se dice en la primera que vuelve.
        var siguenSin = producto.Activo ? [] : vivas.Where(v => !Queda(v)).ToList();
        if (siguenSin.Count > 0)
            primera.Cambios.Insert(primera.LugarDeSeVende + 1, new CambioDeDatoDto("Sus otras variantes", "no se venden",
                $"siguen sin venderse: {Lista(siguenSin.Select(v => Dato(v.Descripcion)))}"));
        var estados = vivas.Where(v => v.Activa != Queda(v)).Select(v => (v, Queda(v))).ToList();
        primera.Acciones.Add(() =>
        {
            Seguido(producto).Activo = true;
            foreach (var (v, queda) in estados)
                Seguido(v).Activa = queda;
        });
    }

    /// <summary>
    /// Si el producto cambió después de exportar el Excel (en lo que trae este renglón), qué cambió; vacío si no. Sin la versión
    /// del Excel, no se sabe: puede que sí.
    /// </summary>
    private List<string>? CambioDespues(Producto producto, CambiosDeVariante c)
    {
        if (_exportado is not { } exportado)
            return [ProductosEnExcel.NoSeSabeSiCambio];
        var (version, cuando) = (exportado.Version, exportado.ExportadoUtc);
        var variante = c.Variante;
        var lista = new List<string>();
        // Los precios, con lo que tenían al exportar y lo que tienen ahora.
        foreach (var grupo in _preciosDespues[variante.Id].GroupBy(h => (h.PresentacionId, h.CantidadMinima)))
        {
            var cambios = grupo.OrderBy(h => h.VersionCatalogo).ThenBy(h => h.FechaUtc).ToList();
            var (antes, ahora) = (cambios[0].PrecioAnteriorCentavos, cambios[^1].PrecioNuevoCentavos);
            if (antes != ahora)
                lista.Add($"{CualPrecio(grupo.Key.PresentacionId, grupo.Key.CantidadMinima)}: {Pesos(antes)} → {Pesos(ahora)}");
        }
        if (producto.Version > version || producto.ModificadoUtc > cuando)
            lista.Add("su nombre, su departamento, su marca o si se vende (en su ficha)");
        if (variante.Version > version)
            lista.Add("su variante: su descripción, si se vende, su mínimo o su máximo");
        if (variante.Presentaciones.SelectMany(p => p.Codigos).Any(x => x.Version > version) || _quitadoDespues[variante.Id].Contains(nameof(CodigoBarras)))
            lista.Add("sus códigos");
        if (variante.Presentaciones.Any(p => p.Version > version) || _quitadoDespues[variante.Id].Contains(nameof(Presentacion)))
            lista.Add("sus presentaciones");
        // El nombre del departamento o de la marca (si el renglón los cambia: quizá trae el nombre de antes).
        if (c.Cambios.Any(x => x.Dato == "Departamento") && _departamentos.FirstOrDefault(d => d.Id == producto.DepartamentoId) is { } departamento
            && departamento.ModificadoUtc > cuando)
            lista.Add($"su departamento, «{departamento.Nombre}» (cambió de nombre o se desactivó)");
        if (c.Cambios.Any(x => x.Dato == "Marca") && producto.MarcaId is { } marca && _marcas.Values.FirstOrDefault(m => m.Id == marca) is { } suya
            && suya.Version > version)
            lista.Add($"su marca, «{suya.Nombre}» (cambió de nombre)");
        return lista.Count == 0 ? null : lista;
    }

    /// <summary>Cómo se dice un precio de la variante: «Precio», «Mayoreo desde 12», «Caja 12: precio», «Caja 12: mayoreo desde 2».</summary>
    private string CualPrecio(Guid? presentacionId, Cantidad desde)
    {
        if (presentacionId is not { } id)
            return desde == Una ? "Precio" : $"Mayoreo desde {desde}";
        var nombre = _presentacionesLigeras.TryGetValue(id, out var p) ? p.Nombre : "Una presentación";
        return desde == Una ? $"{nombre}: precio" : $"{nombre}: mayoreo desde {desde}";
    }

    /// <summary>
    /// El precio que tenía al exportar el Excel (si cambió después): un precio que el Excel deja igual a ese regresa lo de antes.
    /// Vacío si no cambió después.
    /// </summary>
    private long? AntesDeExportar(Guid varianteId, Guid? presentacionId, Cantidad desde, out bool cambioDespues)
    {
        var cambios = _preciosDespues[varianteId].Where(h => h.PresentacionId == presentacionId && h.CantidadMinima == desde)
            .OrderBy(h => h.VersionCatalogo).ThenBy(h => h.FechaUtc).ToList();
        cambioDespues = cambios.Count > 0;
        return cambioDespues ? cambios[0].PrecioAnteriorCentavos : null;
    }

    /// <summary>
    /// Por qué conviene revisar un precio que cambia: si regresa a como estaba al exportar el Excel, si baja, o si sube a más del
    /// doble (<see cref="ProductosEnExcel.SubeMucho"/>: casi siempre centavos escritos con coma, que Excel tomó como miles).
    /// </summary>
    private PorQueSeRevisa? RevisarPrecio(Guid varianteId, Guid? presentacionId, Cantidad desde, long? antes, long? despues)
    {
        if (AntesDeExportar(varianteId, presentacionId, desde, out var cambio) == despues && cambio)
            return PorQueSeRevisa.RegresaLoDeAntes;
        if (antes is { } a && despues is { } d && d < a)
            return PorQueSeRevisa.BajaDePrecio;
        return ProductosEnExcel.SubeMucho(antes, despues) ? PorQueSeRevisa.SubeMucho : null;
    }

    /// <summary>
    /// Lo que cambia de una variante con su renglón de «Productos» (y las acciones que lo cambian), menos «Se vende», que es de todo
    /// el producto (<see cref="SeVende"/>). Anota su menudeo de antes y el que queda, para el margen.
    /// </summary>
    private void CambiosDeLaFila(Fila f, Producto producto, CambiosDeVariante c)
    {
        var hoja = _libro.Productos;
        var variante = c.Variante;
        if (f.Producto is { } nombre && LecturaDeCeldas.Limpio(nombre) != LecturaDeCeldas.Limpio(producto.Nombre))
            c.Agregar(new CambioDeDatoDto("Nombre", producto.Nombre, nombre), () => Seguido(producto).Nombre = nombre);
        if (hoja.Trae(CampoDeProducto.Variante) && LecturaDeCeldas.Limpio(f.Descripcion ?? "") != LecturaDeCeldas.Limpio(variante.Descripcion))
        {
            var descripcion = f.Descripcion ?? "";
            c.Agregar(new CambioDeDatoDto("Variante", Dato(variante.Descripcion), Dato(descripcion)), () => Seguido(variante).Descripcion = descripcion);
        }
        if (f.Departamento is not null)
        {
            var destino = DestinoDe(f, c.Departamentos);
            if (destino.Id != producto.DepartamentoId)
            {
                var antes = _departamentos.FirstOrDefault(d => d.Id == producto.DepartamentoId)?.Nombre;
                c.Agregar(new CambioDeDatoDto("Departamento", Dato(antes), destino.Nombre, PorQueSeRevisa.Departamento), () => Seguido(producto).DepartamentoId = destino.Id);
            }
            else
                c.Departamentos.Remove(destino);
        }
        if (hoja.Trae(CampoDeProducto.Marca))
        {
            var antes = NombreDeMarca(producto.MarcaId);
            if (Texto.Normalizar(f.Marca ?? "") != Texto.Normalizar(antes ?? ""))
            {
                var marca = MarcaDe(f.Marca, c.Marcas);
                c.Agregar(new CambioDeDatoDto("Marca", Dato(antes), Dato(f.Marca)), () => Seguido(producto).MarcaId = marca);
            }
        }
        c.LugarDeSeVende = c.Cambios.Count;
        if (producto.Tipo == TipoProducto.Mercancia)
        {
            if (hoja.Trae(CampoDeProducto.Minimo) && EnCantidad(f.Minimo) is var minimo && minimo != variante.InventarioMinimo)
                c.Agregar(new CambioDeDatoDto("Mínimo", Dato(variante.InventarioMinimo), Dato(minimo)), () => Seguido(variante).InventarioMinimo = minimo);
            if (hoja.Trae(CampoDeProducto.Maximo) && EnCantidad(f.Maximo) is var maximo && maximo != variante.InventarioMaximo)
                c.Agregar(new CambioDeDatoDto("Máximo", Dato(variante.InventarioMaximo), Dato(maximo)), () => Seguido(variante).InventarioMaximo = maximo);
        }
        if (f.Otros is not null && variante.Presentaciones.FirstOrDefault(p => p.EsBase) is { } pieza)
            Codigos(pieza, f.Codigos, "Códigos", c);
        else if (f.Codigo is { } nuevo && variante.Presentaciones.FirstOrDefault(p => p.EsBase) is { } sinOtros && sinOtros.Codigos.Count == 0)
            // Sin la columna «Otros códigos», un producto que no tenía código recibe el de «Código».
            Codigos(sinOtros, [nuevo], "Códigos", c);

        var actuales = _precios.GetValueOrDefault(variante.Id) ?? [];
        var porUnidad = actuales.Where(p => p.PresentacionId is null).OrderBy(p => p.CantidadMinima).ToList();
        var menudeo = porUnidad.FirstOrDefault();
        long? despues = menudeo?.PrecioCentavos;
        if (f.Precio is { } precio && precio != menudeo?.PrecioCentavos)
        {
            var desde = menudeo?.CantidadMinima ?? Una;
            var cambio = new CambioDeDatoDto("Precio", Pesos(menudeo?.PrecioCentavos), Pesos(precio), RevisarPrecio(variante.Id, null, desde, menudeo?.PrecioCentavos, precio));
            despues = precio;
            if (menudeo is null)
                c.Agregar(cambio, () => _db.Precios.Add(new Precio { VarianteId = variante.Id, CantidadMinima = Una, PrecioCentavos = precio }));
            else
                c.Agregar(cambio, () => Seguido(menudeo).PrecioCentavos = precio);
        }
        if (f.Mayoreo is { } mayoreo)
            Escalas(variante.Id, null, porUnidad.Skip(1).ToList(), mayoreo.Select(m => (Cantidad.Unidades(m.Desde), m.Precio)).ToList(), "Mayoreo", c);
        (c.MenudeoAntes, c.MenudeoDespues) = (menudeo?.PrecioCentavos, despues);
    }

    /// <summary>Lo que cambia de una presentación con su renglón de «Presentaciones»: sus códigos y su precio propio; o es nueva.</summary>
    private void CambiosDeLaPresentacion(FilaDePresentacion p, CambiosDeVariante c)
    {
        var hoja = _libro.Presentaciones!;
        var variante = c.Variante;
        var actuales = _precios.GetValueOrDefault(variante.Id) ?? [];
        if (p.Existente is { } presentacion)
        {
            if (p.Codigos is { } codigos)
                Codigos(presentacion, codigos, $"{presentacion.Nombre}: códigos", c);
            var propios = actuales.Where(x => x.PresentacionId == presentacion.Id).OrderBy(x => x.CantidadMinima).ToList();
            var deseada = CadenaDeseada(p, hoja, propios).Select(x => (x.Desde, x.Precio)).ToList();
            Escalas(variante.Id, presentacion.Id, propios, deseada, presentacion.Nombre, c);
            return;
        }
        var nueva = new Presentacion { VarianteId = variante.Id, Nombre = p.Nombre!, Factor = Cantidad.Unidades(p.Contiene!.Value), EsBase = false };
        c.Agregar(new CambioDeDatoDto(p.Nombre!, "—", DetalleDeNueva(p, variante.UnidadBase)), () => _db.Presentaciones.Add(nueva));
        c.Acciones.AddRange(DeLaNueva(p, variante.Id, nueva));
    }

    /// <summary>Lo que da de alta los códigos y el precio propio de una presentación nueva (de un producto que ya existe o de uno nuevo).</summary>
    private IEnumerable<Action> DeLaNueva(FilaDePresentacion p, Guid varianteId, Presentacion nueva)
    {
        foreach (var codigo in p.Codigos ?? [])
        {
            var nuevo = new CodigoBarras { Codigo = codigo, PresentacionId = nueva.Id, Origen = CodigoBarras.OrigenDe(codigo) };
            yield return () => _db.CodigosBarras.Add(nuevo);
        }
        foreach (var (desde, precio, _) in CadenaDeseada(p, _libro.Presentaciones!, []))
            yield return () => _db.Precios.Add(new Precio { VarianteId = varianteId, PresentacionId = nueva.Id, CantidadMinima = desde, PrecioCentavos = precio });
    }

    /// <summary>«nueva: trae 12 piezas · 750… · $55.00».</summary>
    private string DetalleDeNueva(FilaDePresentacion p, UnidadBase unidad)
    {
        var partes = new List<string> { $"nueva: trae {Numero(p.Contiene!.Value)} {ProductosEnExcel.Unidad(unidad)}" };
        if (p.Codigos is { Count: > 0 } codigos)
            partes.Add(string.Join(", ", codigos));
        var cadena = CadenaDeseada(p, _libro.Presentaciones!, []);
        if (cadena.Count > 0)
            partes.Add(string.Join(", ", cadena.Select(c => c.Desde == Una ? Pesos(c.Precio) : $"desde {c.Desde} a {Pesos(c.Precio)}")));
        return string.Join(" · ", partes);
    }

    /// <summary>
    /// Los códigos de una presentación como los pide el archivo: se agregan los que faltan y se quitan los que ya no vienen. Uno que
    /// trae coma o punto y coma no va en el Excel (se partiría): ese no se quita.
    /// </summary>
    private void Codigos(Presentacion presentacion, IReadOnlyList<string> deseados, string dato, CambiosDeVariante c)
    {
        var actuales = presentacion.Codigos.ToList();
        var quitar = actuales.Where(x => !ProductosEnExcel.NoCabeEnUnaLista(x.Codigo) && !deseados.Contains(x.Codigo, StringComparer.OrdinalIgnoreCase)).ToList();
        var agregar = deseados.Where(x => actuales.All(a => !string.Equals(a.Codigo, x, StringComparison.OrdinalIgnoreCase))).ToList();
        if (quitar.Count + agregar.Count == 0)
            return;
        var quedan = actuales.Except(quitar).Select(x => x.Codigo).Concat(agregar).ToList();
        c.Cambios.Add(new CambioDeDatoDto(dato, Dato(string.Join(", ", RegistroProductosEnExcel.Ordenados(actuales))), Dato(string.Join(", ", quedan)),
            quitar.Count > 0 ? PorQueSeRevisa.QuitaCodigos : null));
        foreach (var codigo in quitar)
            c.Acciones.Add(() => Quitar(codigo));
        foreach (var codigo in agregar)
        {
            var nuevo = new CodigoBarras { Codigo = codigo, PresentacionId = presentacion.Id, Origen = CodigoBarras.OrigenDe(codigo) };
            c.Acciones.Add(() => _db.CodigosBarras.Add(nuevo));
        }
    }

    /// <summary>
    /// Escalas de precio como las pide el archivo, comparadas por desde cuántas: cambia el precio de la que ya estaba, se agrega la
    /// nueva y se quita la que ya no viene.
    /// </summary>
    /// <param name="presentacionId">Vacío: el mayoreo por unidad; con valor, los precios propios de esa presentación.</param>
    private void Escalas(Guid varianteId, Guid? presentacionId, List<Precio> actuales, List<(Cantidad Desde, long Precio)> deseadas, string que,
        CambiosDeVariante c)
    {
        string Cual(Cantidad desde) => presentacionId is null ? $"{que} desde {desde}" : desde == Una ? $"{que}: precio" : $"{que}: mayoreo desde {desde}";
        foreach (var (desde, precio) in deseadas.OrderBy(d => d.Desde))
        {
            if (actuales.FirstOrDefault(a => a.CantidadMinima == desde) is { } actual)
            {
                if (actual.PrecioCentavos == precio)
                    continue;
                c.Agregar(new CambioDeDatoDto(Cual(desde), Pesos(actual.PrecioCentavos), Pesos(precio),
                    RevisarPrecio(varianteId, presentacionId, desde, actual.PrecioCentavos, precio)), () => Seguido(actual).PrecioCentavos = precio);
            }
            else
                c.Agregar(new CambioDeDatoDto(Cual(desde), "—", Pesos(precio), RevisarPrecio(varianteId, presentacionId, desde, null, precio)),
                    () => _db.Precios.Add(new Precio { VarianteId = varianteId, PresentacionId = presentacionId, CantidadMinima = desde, PrecioCentavos = precio }));
        }
        foreach (var quitada in actuales.Where(a => deseadas.All(d => d.Desde != a.CantidadMinima)))
            c.Agregar(new CambioDeDatoDto(Cual(quitada.CantidadMinima), Pesos(quitada.PrecioCentavos), "—",
                RevisarPrecio(varianteId, presentacionId, quitada.CantidadMinima, quitada.PrecioCentavos, null)), () => Quitar(quitada));
    }

    /// <summary>Un producto nuevo, con una variante, su pieza, sus códigos y sus precios (y sus presentaciones de la otra hoja).</summary>
    private (ProductoNuevoDeExcelDto Dto, string Firma) Nuevo(Fila f, List<FilaDePresentacion> presentaciones)
    {
        var unidad = f.Unidad ?? UnidadBase.Pieza;
        var departamentos = new HashSet<Departamento>();
        var marcas = new HashSet<Marca>();
        var departamento = DestinoDe(f, departamentos);
        var marca = MarcaDe(f.Marca, marcas);
        _departamentosQueVan.UnionWith(departamentos);
        _marcasQueVan.UnionWith(marcas);
        var activo = f.SeVende ?? true;
        var producto = new Producto { Nombre = f.Producto!, DepartamentoId = departamento.Id, MarcaId = marca, Activo = activo };
        var variante = new Variante
        {
            ProductoId = producto.Id, Descripcion = f.Descripcion ?? "", UnidadBase = unidad, InventarioMinimo = EnCantidad(f.Minimo), InventarioMaximo = EnCantidad(f.Maximo),
        };
        var pieza = new Presentacion { VarianteId = variante.Id, Nombre = ProductosEnExcel.PresentacionBase(unidad), EsBase = true };
        var codigos = f.Codigos;
        var escalas = (f.Mayoreo ?? []).OrderBy(m => m.Desde).Select(m => (Desde: Cantidad.Unidades(m.Desde), m.Precio)).ToList();
        _acciones.Add(() =>
        {
            _db.Productos.Add(producto);
            _db.Variantes.Add(variante);
            _db.Presentaciones.Add(pieza);
            foreach (var codigo in codigos)
                _db.CodigosBarras.Add(new CodigoBarras { Codigo = codigo, PresentacionId = pieza.Id, Origen = CodigoBarras.OrigenDe(codigo) });
            _db.Precios.Add(new Precio { VarianteId = variante.Id, CantidadMinima = Una, PrecioCentavos = f.Precio!.Value });
            foreach (var (desde, precio) in escalas)
                _db.Precios.Add(new Precio { VarianteId = variante.Id, CantidadMinima = desde, PrecioCentavos = precio });
        });
        foreach (var p in presentaciones)
        {
            var nueva = new Presentacion { VarianteId = variante.Id, Nombre = p.Nombre!, Factor = Cantidad.Unidades(p.Contiene!.Value), EsBase = false };
            _acciones.Add(() => _db.Presentaciones.Add(nueva));
            _acciones.AddRange(DeLaNueva(p, variante.Id, nueva));
        }

        var detalle = new List<string>();
        if (unidad != UnidadBase.Pieza)
            detalle.Add($"por {Singular(unidad)}");
        if (escalas.Count > 0)
            detalle.Add("mayoreo " + string.Join(", ", escalas.Select(e => $"desde {e.Desde} a {Pesos(e.Precio)}")));
        if (codigos.Count > 1)
            detalle.Add($"también {string.Join(", ", codigos.Skip(1))}");
        detalle.AddRange(presentaciones.Select(p => $"{p.Nombre} ({DetalleDeNueva(p, unidad)})"));
        if (f.Marca is { } nombreMarca)
            detalle.Add($"marca {nombreMarca}");
        if (f.Minimo is { } minimo)
            detalle.Add($"mínimo {Numero(minimo)}");
        if (f.Maximo is { } maximo)
            detalle.Add($"máximo {Numero(maximo)}");
        if (!activo)
            detalle.Add("no se vende");
        var avisos = new List<string>();
        if (_porNombre[Texto.Normalizar(f.Nombre)].FirstOrDefault() is var parecido && parecido != Guid.Empty)
            avisos.Add($"Ya hay uno que se llama igual: «{NombreDe(parecido)}»{(_codigoDe.GetValueOrDefault(parecido) is { } suyo ? $" ({suyo})" : "")}. Si "
                + "querías cambiarle el código, ponlo en «Otros códigos» de su renglón.");
        avisos.AddRange(f.Avisos);
        var dto = new ProductoNuevoDeExcelDto(f.Numero, f.Codigo!, f.Nombre, departamento.Nombre, f.Precio!.Value, detalle.Count == 0 ? null : string.Join(" · ", detalle),
            avisos.Count == 0 ? null : string.Join(" ", avisos));
        var firma = $"{f.Numero}|{string.Join(",", codigos)}|{f.Nombre}|{departamento.Nombre}|{f.Marca}|{unidad}|{f.Precio}|"
            + $"{string.Join(",", escalas.Select(e => $"{e.Desde}:{e.Precio}"))}|{f.Minimo}|{f.Maximo}|{activo}|{dto.Detalle}";
        return (dto, firma);
    }

    /// <summary>Lo demás que conviene saber de ese archivo.</summary>
    private List<string> Avisos(List<ProductoQueCambiaDto> cambian, List<ProductoQueCambiaDto> aparte)
    {
        var avisos = new List<string>();
        var hoja = _libro.Productos;
        if (_exportado is null && (aparte.Count > 0 || cambian.Any(x => x.CambioDespues is not null)))
            avisos.Add(ProductosEnExcel.SinVersion);
        if (hoja.Trae(CampoDeProducto.Existencia) || hoja.Trae(CampoDeProducto.CostoPromedio) || hoja.Trae(CampoDeProducto.Margen))
            avisos.Add("La existencia, el costo y el margen no se importan: la existencia cambia con las compras, los conteos y los ajustes de "
                + "Inventario, y el costo sale de las compras.");
        var sinUsar = hoja.SinUsar.Select(t => $"«{t}»").Concat(_libro.Presentaciones?.SinUsar.Select(t => $"«{t}» (en «{Presentaciones}»)") ?? []).ToList();
        if (sinUsar.Count > 0)
            avisos.Add($"No se usan {(sinUsar.Count == 1 ? "la columna" : "las columnas")} {Lista(sinUsar)}: no {(sinUsar.Count == 1 ? "es" : "son")} de este Excel.");
        return avisos;
    }

    // ── Cómo se dice cada cosa ───────────────────────────────────────────────

    private string NombreDe(Guid varianteId) => _nombres.TryGetValue(varianteId, out var nombre) ? nombre.Nombre : "otro producto";

    private static string Completo(string producto, string descripcion) =>
        string.IsNullOrWhiteSpace(descripcion) ? producto : $"{producto} {descripcion}";

    private static Cantidad? EnCantidad(decimal? unidades) => unidades is { } u ? Cantidad.Unidades(u) : null;

    private static string Pesos(long? centavos) => centavos is { } c ? (c / 100m).ToString("$#,##0.00", Mexico) : "—";

    private static string Numero(decimal valor) => valor.ToString("#,##0.###", Mexico);

    private static string Dato(string? texto) => string.IsNullOrWhiteSpace(texto) ? "—" : texto;

    private static string Dato(Cantidad? cantidad) => cantidad is { } c ? Numero(c.EnUnidades) : "—";

    private static string SiNo(bool valor) => valor ? "Sí" : "No";

    private static string Singular(UnidadBase unidad) => unidad switch
    {
        UnidadBase.Metro => "metro",
        UnidadBase.Kilogramo => "kilo",
        UnidadBase.Litro => "litro",
        _ => "pieza",
    };

    /// <summary>«5», «5 y 9», «5, 9 y 12».</summary>
    private static string Lista(IEnumerable<string> partes)
    {
        var todas = partes.ToList();
        return todas.Count <= 1 ? string.Concat(todas) : $"{string.Join(", ", todas.Take(todas.Count - 1))} y {todas[^1]}";
    }
}
