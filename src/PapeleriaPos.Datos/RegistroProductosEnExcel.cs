using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Datos;

/// <summary>Una escala de mayoreo en el Excel de los productos: desde cuántas y a qué precio.</summary>
public sealed record EscalaParaExcel(decimal Desde, long PrecioCentavos);

/// <summary>Un renglón de la hoja «Productos»: una variante que se vende, como la ve quien lo exporta.</summary>
/// <param name="Codigo">El primero de su pieza (los de fábrica antes); vacío si no tiene.</param>
/// <param name="OtrosCodigos">Los demás de su pieza (sin los que traen coma o punto y coma: se partirían).</param>
/// <param name="Existencia">En la sucursal de la caja; vacía en un servicio.</param>
/// <param name="CostoPromedioCentavos">Solo para quien ve costos, utilidad y margen; vacío sin existencia.</param>
/// <param name="MargenCentesimas">Del menudeo contra el costo promedio, solo para quien lo ve.</param>
/// <param name="Clave">La de la variante, en la columna escondida: con ella se reconoce el renglón al importar, aunque no tenga código.</param>
public sealed record VarianteParaExcel(
    string? Codigo, IReadOnlyList<string> OtrosCodigos, string Producto, string Variante, string Departamento, string? Marca, UnidadBase Unidad,
    long? PrecioCentavos, IReadOnlyList<EscalaParaExcel> Mayoreo, decimal? Existencia, decimal? Minimo, decimal? Maximo, bool SeVende,
    decimal? CostoPromedioCentavos, int? MargenCentesimas, Guid Clave);

/// <summary>Un renglón de la hoja «Presentaciones»: una caja o un paquete de una variante, con sus códigos y su precio propio.</summary>
/// <param name="CodigoDelProducto">El de la columna «Código» de su variante, para reconocerla al importar.</param>
/// <param name="Producto">El nombre de su producto (con su variante), para leerlo.</param>
/// <param name="Contiene">Cuántas unidades trae (piezas, metros…).</param>
/// <param name="PrecioCentavos">
/// Su precio propio, por presentación: el más bajo de los suyos (casi siempre desde una; el cobro lo toma también para una). Vacío:
/// se cobra por sus piezas.
/// </param>
/// <param name="Mayoreo">Sus demás escalas propias (desde cuántas presentaciones), que casi nunca hay.</param>
/// <param name="Clave">La de la variante de su producto, escondida: así se reconocen las de un producto sin código.</param>
public sealed record PresentacionParaExcel(
    string? CodigoDelProducto, string Producto, string Presentacion, decimal Contiene, UnidadBase Unidad, IReadOnlyList<string> Codigos,
    long? PrecioCentavos, IReadOnlyList<EscalaParaExcel> Mayoreo, Guid Clave);

/// <summary>Lo que lleva el Excel de los productos.</summary>
/// <param name="Filtros">Los de la lista, en palabras, para el encabezado de cada hoja.</param>
/// <param name="Departamento">Si la lista era de un solo departamento: va en el nombre del archivo.</param>
/// <param name="SinExportar">Los kits y los cobros de terceros de la lista: no van (se cambian en su ficha).</param>
/// <param name="Exportacion">Con qué catálogo se exportó (va escondido en el libro): al importarlo se sabe qué cambió después.</param>
/// <param name="CodigosFuera">Los códigos con coma o punto y coma que no van en «Otros códigos» ni en «Códigos» (se partirían).</param>
public sealed record ProductosParaExcel(
    IReadOnlyList<VarianteParaExcel> Variantes, IReadOnlyList<PresentacionParaExcel> Presentaciones, bool ConCostos, string Filtros, string? Departamento,
    int SinExportar, ExportacionDelLibro Exportacion, int CodigosFuera = 0);

/// <summary>
/// Leer lo de muchos productos a la vez (el Excel de los productos trae miles): con pocos, se piden por su Id; con más de
/// <see cref="Maximo"/>, se lee todo y se filtra en memoria. Con 10,000 Id en una consulta («Id IN (…)», que EF arma con un
/// parámetro por Id), leer los productos con sus variantes, presentaciones y códigos tardaba casi 40 s; todo el catálogo, ~1 s.
/// </summary>
internal static class LecturaPorId
{
    public const int Maximo = 500;
}

/// <summary>
/// Productos (F3) › «Productos en Excel» (2026-10-22; en la paridad, «Importar y exportar productos en Excel», como el catálogo de
/// eleventa): exportar la lista como se ve y volver a importarla ya cambiada en Excel.
/// <list type="bullet">
/// <item><b>Exportar:</b> los productos de la lista con sus filtros (la búsqueda, que también busca la marca y el departamento;
/// el departamento; «por completar», «poca existencia» o «margen bajo»; con o sin los inactivos), en su orden y sin las
/// páginas: una fila por variante que se vende, y en otra hoja sus presentaciones. El costo y el margen solo para quien tiene
/// «Ver costos, utilidad y margen». Lo exporta quien ve Productos.</item>
/// <item><b>Importar</b> (<see cref="PlanDeImportacion"/>): el mismo Excel, ya cambiado. Cada renglón se reconoce por su código;
/// un código que no tiene nadie es un producto nuevo. Primero se ve qué cambia, qué es nuevo y qué se ignora (y por qué);
/// al aplicar se vuelve a calcular y, si algo cambió desde la vista, no se aplica. Todo en una sola transacción, con un
/// renglón en la bitácora; el historial de cada precio y la versión del catálogo los escribe la base al guardar, así que las
/// cajas se enteran como con la ficha. Lo hace quien tiene «Productos, precios y etiquetas» en «Puede», como «Cambiar
/// precios»; en el piloto con eleventa no se importa (las altas y los precios se hacen allá).</item>
/// </list>
/// </summary>
public sealed class RegistroProductosEnExcel(PosDbContext db, bool lecturaDeEleventa = false)
{
    /// <summary>Lo que dice la bitácora al empezar: así se reconoce el renglón de una importación.</summary>
    public const string EnLaBitacora = "Importó de Excel ";

    /// <summary>
    /// Lo más que lleva un Excel de productos: mientras se arma, cada celda ocupa memoria del servicio de la caja principal. El
    /// catálogo de la tienda cabe completo; con más, se pide filtrar.
    /// </summary>
    public const int MaximoParaExportar = 30_000;

    /// <summary>Lo que dice el servicio a quien no ve Productos.</summary>
    public const string QuienExporta = "Los productos los exporta quien ve Productos (F3): quien tiene «Productos, precios y etiquetas».";

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Lo que pasa entre leer el catálogo (y armar lo que se aplica) y apartar la base para escribir: nada, salvo en las pruebas, que
    /// ahí cambian algo para ver que entonces no se aplica.
    /// </summary>
    public Func<CancellationToken, Task>? DespuesDeLeer { get; init; }

    // ── Exportar ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Lo que lleva el Excel de la lista de Productos con esos filtros (los mismos de <see cref="RegistroCatalogo.ListaAsync"/>, que
    /// también decide quién ve el margen bajo), en el mismo orden.
    /// </summary>
    public async Task<ProductosParaExcel> ExportarAsync(
        Guid sucursalId, Guid usuarioId, string? texto, FiltroProductos filtro, Guid? departamentoId, bool conInactivos, IReadOnlyDictionary<Guid, int> vendidos,
        CancellationToken ct = default)
    {
        var control = new ServicioControl(db);
        if ((await control.EvaluarAsync(usuarioId, sucursalId, AccionControlada.EditarCatalogo, ct: ct)).Nivel == NivelPermiso.NoPermitido)
            throw new PermisoRequerido(AccionControlada.EditarCatalogo, NivelPermiso.NoPermitido, QuienExporta);
        var conCostos = await control.PuedeAsync(usuarioId, AccionControlada.VerUtilidad, ct);
        // La versión del catálogo, antes de leer los productos: lo que cambie mientras se arma queda después de ella (al importarlo
        // se ve como «cambió después de exportar», nunca al revés).
        var exportacion = new ExportacionDelLibro(await VersionDelCatalogoAsync(db, ct), Ahora, sucursalId);
        var lista = await new RegistroCatalogo(db).ListaAsync(sucursalId, usuarioId, texto, filtro, departamentoId, conInactivos, vendidos, sinPaginas: true, ct: ct);
        var orden = lista.Productos.Select((p, i) => (p.ProductoId, i)).ToDictionary(x => x.ProductoId, x => x.i);
        var ids = orden.Keys.ToList();
        // Con muchos, todo el catálogo y se filtra aquí (LecturaPorId).
        var muchos = ids.Count > LecturaPorId.Maximo;
        var consulta = db.Productos.AsNoTracking().Include(p => p.Marca).Include(p => p.Departamento)
            .Include(p => p.Variantes).ThenInclude(v => v.Presentaciones).ThenInclude(pr => pr.Codigos).AsSplitQuery();
        var productos = (await (muchos ? consulta : consulta.Where(p => ids.Contains(p.Id))).ToListAsync(ct))
            .Where(p => orden.ContainsKey(p.Id)).OrderBy(p => orden[p.Id]).ToList();
        var variantes = productos.SelectMany(p => p.Variantes).Select(v => v.Id).ToList();
        var deEstas = variantes.ToHashSet();
        var consultaPrecios = db.Precios.AsNoTracking().Where(p => p.SucursalId == null);
        var precios = (await (muchos ? consultaPrecios : consultaPrecios.Where(p => variantes.Contains(p.VarianteId))).ToListAsync(ct))
            .Where(p => deEstas.Contains(p.VarianteId)).ToLookup(p => p.VarianteId);
        var consultaExistencias = db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId);
        var existencias = (await (muchos ? consultaExistencias : consultaExistencias.Where(e => variantes.Contains(e.VarianteId))).ToListAsync(ct))
            .Where(e => deEstas.Contains(e.VarianteId)).ToDictionary(e => e.VarianteId);

        var renglones = new List<VarianteParaExcel>();
        var presentaciones = new List<PresentacionParaExcel>();
        var sinExportar = 0;
        var fuera = 0;
        // Los que caben en una lista separada por comas (los otros se cambian en su ficha; la importación no los quita).
        List<string> EnLista(IEnumerable<string> codigos)
        {
            var todos = codigos.ToList();
            var caben = todos.Where(c => !ProductosEnExcel.NoCabeEnUnaLista(c)).ToList();
            fuera += todos.Count - caben.Count;
            return caben;
        }
        foreach (var producto in productos)
        {
            // Los kits y los cobros de terceros (recargas) no se cambian como un producto: se quedan en su ficha.
            if (producto.Tipo is not (TipoProducto.Mercancia or TipoProducto.Servicio))
            {
                sinExportar++;
                continue;
            }
            foreach (var variante in producto.Variantes.Where(v => v.JuntadaEnId is null && (conInactivos || (v.Activa && producto.Activo)))
                         .OrderByDescending(v => v.Activa).ThenBy(v => v.Descripcion, StringComparer.CurrentCultureIgnoreCase))
            {
                var pieza = variante.Presentaciones.FirstOrDefault(p => p.EsBase);
                var codigos = pieza is null ? [] : Ordenados(pieza.Codigos);
                var suyos = precios[variante.Id].ToList();
                var porUnidad = suyos.Where(p => p.PresentacionId is null).OrderBy(p => p.CantidadMinima).ToList();
                var menudeo = porUnidad.FirstOrDefault();
                var existencia = existencias.GetValueOrDefault(variante.Id);
                var mercancia = producto.Tipo == TipoProducto.Mercancia;
                var costo = conCostos && mercancia ? existencia?.CostoPromedio : null;
                var nombre = NombreDe(producto, variante);
                renglones.Add(new VarianteParaExcel(codigos.FirstOrDefault(), EnLista(codigos.Skip(1)), producto.Nombre, variante.Descripcion,
                    producto.Departamento?.Nombre ?? "", producto.Marca?.Nombre, variante.UnidadBase, menudeo?.PrecioCentavos,
                    porUnidad.Skip(1).Select(p => new EscalaParaExcel(p.CantidadMinima.EnUnidades, p.PrecioCentavos)).ToList(),
                    mercancia ? existencia?.Cantidad.EnUnidades ?? 0 : null,
                    mercancia ? variante.InventarioMinimo?.EnUnidades : null, mercancia ? variante.InventarioMaximo?.EnUnidades : null,
                    producto.Activo && variante.Activa, costo,
                    costo is { } c && menudeo is { PrecioCentavos: > 0 } m ? Margen.Calcular(m.PrecioCentavos, c) : null, variante.Id));
                foreach (var presentacion in variante.Presentaciones.Where(p => !p.EsBase).OrderBy(p => p.Factor).ThenBy(p => p.Nombre, StringComparer.CurrentCultureIgnoreCase))
                {
                    var propios = suyos.Where(p => p.PresentacionId == presentacion.Id).OrderBy(p => p.CantidadMinima).ToList();
                    presentaciones.Add(new PresentacionParaExcel(codigos.FirstOrDefault(), nombre, presentacion.Nombre, presentacion.Factor.EnUnidades,
                        variante.UnidadBase, EnLista(Ordenados(presentacion.Codigos)), propios.FirstOrDefault()?.PrecioCentavos,
                        propios.Skip(1).Select(p => new EscalaParaExcel(p.CantidadMinima.EnUnidades, p.PrecioCentavos)).ToList(), variante.Id));
                }
            }
        }
        if (renglones.Count > MaximoParaExportar)
            throw new ArgumentException($"Son {renglones.Count.ToString("#,##0", Mexico)} renglones: se exportan hasta "
                + $"{MaximoParaExportar.ToString("#,##0", Mexico)} a la vez. Elige un departamento o escribe una búsqueda.");

        var departamento = departamentoId is { } d ? await db.Departamentos.AsNoTracking().Where(x => x.Id == d).Select(x => x.Nombre).SingleOrDefaultAsync(ct) : null;
        return new ProductosParaExcel(renglones, presentaciones, conCostos, Filtros(texto, filtro, departamento, conInactivos), departamento, sinExportar,
            exportacion, fuera);
    }

    /// <summary>La versión del catálogo: la de su último cambio (<see cref="Secuencia.Catalogo"/>).</summary>
    private static async Task<long> VersionDelCatalogoAsync(PosDbContext db, CancellationToken ct) =>
        await db.Secuencias.AsNoTracking().Where(x => x.Nombre == Secuencia.Catalogo).Select(x => x.Valor).SingleOrDefaultAsync(ct);

    /// <summary>Los códigos de una presentación: los de fábrica primero (los que se escanean), luego los internos, cada grupo en orden.</summary>
    internal static List<string> Ordenados(IEnumerable<CodigoBarras> codigos) =>
        codigos.OrderBy(c => c.Origen == OrigenCodigo.Fabrica ? 0 : 1).ThenBy(c => c.Codigo, StringComparer.Ordinal).Select(c => c.Codigo).ToList();

    internal static string NombreDe(Producto producto, Variante variante) =>
        string.IsNullOrWhiteSpace(variante.Descripcion) ? producto.Nombre : $"{producto.Nombre} {variante.Descripcion}";

    /// <summary>Los filtros de la lista en palabras: «Todos los productos activos.» o «Solo los que coinciden con «cuaderno» · …».</summary>
    private static string Filtros(string? texto, FiltroProductos filtro, string? departamento, bool conInactivos)
    {
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(texto))
            partes.Add($"los que coinciden con «{texto.Trim()}»");
        if (departamento is not null)
            partes.Add($"del departamento «{departamento}»");
        partes.AddRange(filtro switch
        {
            FiltroProductos.PorCompletar => ["los que están por completar"],
            FiltroProductos.PocaExistencia => ["los que están en su mínimo o debajo"],
            FiltroProductos.MargenBajo => ["los que dejan menos que el margen mínimo"],
            _ => [],
        });
        if (partes.Count == 0)
            return conInactivos ? "Todos los productos, también los inactivos." : "Todos los productos activos.";
        return $"Solo {string.Join(" · ", partes)}{(conInactivos ? " · también los inactivos" : "")}.";
    }

    // ── Importar ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Qué cambia con ese Excel, sin cambiar nada. El archivo se lee (<paramref name="leer"/>) después de revisar el permiso y el
    /// piloto: a quien no puede importar no se le abre.
    /// </summary>
    public async Task<VistaImportacionDto> VistaAsync(SolicitudImportarProductos s, Func<LibroDeProductos> leer, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirParaVerAsync(s.UsuarioId, s.SucursalId, AccionControlada.EditarCatalogo, ct);
        if (lecturaDeEleventa)
            throw new InvalidOperationException(ProductosEnExcel.ImportarEnElPiloto);
        return (await PlanDeImportacion.ArmarAsync(db, s, leer(), paraAplicar: false, ct)).Vista;
    }

    /// <summary>
    /// Aplica lo que se vio (<see cref="SolicitudImportarProductos.Huella"/>), todo junto, en una sola transacción. Si ya se aplicó con
    /// ese <see cref="SolicitudImportarProductos.Id"/> (la respuesta se perdió y se vuelve a pedir), no lo hace otra vez.
    /// <para>
    /// Todo se lee y se arma antes de apartar la base para escribir: con miles de productos eso tarda segundos, y mientras la base
    /// está apartada una venta de otra caja no se puede guardar (la caja espera 2.5 s y la manda a su bandeja, como sin conexión).
    /// Ya apartada, solo se revisa que el catálogo siga como cuando se leyó (si no, no se aplica: la caja vuelve a ver qué cambia) y
    /// se guarda.
    /// </para>
    /// </summary>
    public async Task<ImportacionAplicadaDto> AplicarAsync(SolicitudImportarProductos s, Func<LibroDeProductos> leer, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirDeLaSesionAsync(s.UsuarioId, s.SucursalId, null, AccionControlada.EditarCatalogo, ct);
        if (lecturaDeEleventa)
            throw new InvalidOperationException(ProductosEnExcel.ImportarEnElPiloto);
        var libro = leer();
        if (await YaAplicadaAsync(s.Id, ct) is { } yaAplicada)
            return yaAplicada;
        var leido = await EstadoDelCatalogoAsync(ct);
        var plan = await PlanDeImportacion.ArmarAsync(db, s, libro, paraAplicar: true, ct);
        var vista = plan.Vista;
        const string NadaQueAplicar = "Con ese Excel no cambia nada: todo está igual o se ignora.";
        if (string.IsNullOrEmpty(s.Huella))
            throw new InvalidOperationException(vista.HayQueAplicar ? "Primero ve qué cambia." : NadaQueAplicar);
        // Lo que se vio ya no es lo que quedaría (también si lo que se iba a cambiar cambió después en otro lado: ahora va aparte).
        if (s.Huella != vista.Huella)
            throw new InvalidOperationException(vista.HayQueAplicar || vista.CambiaronDespues is { Count: > 0 } ? ProductosEnExcel.CambioDesdeLaVista : NadaQueAplicar);
        if (!vista.HayQueAplicar)
            throw new InvalidOperationException(NadaQueAplicar);

        plan.Aplicar();
        var detalle = Detalle(s.Archivo, vista);
        var id = s.Id ?? Ids.Nuevo();
        db.Bitacora.Add(new Bitacora
        {
            Id = id,
            SucursalId = s.SucursalId,
            UsuarioId = s.UsuarioId,
            Accion = AccionControlada.EditarCatalogo,
            Detalle = detalle,
            Resultado = ResultadoBitacora.Hecho,
            FechaUtc = Ahora,
        });
        // Lo completo (lo de antes y lo que quedó de cada producto), con su renglón: la bitácora solo dice cuántos.
        db.DetallesBitacora.Add(new DetalleBitacora
        {
            BitacoraId = id, Tipo = DetalleBitacora.ImportacionDeExcel, Contenido = JsonSerializer.Serialize(plan.Detalle, Json),
        });
        db.ChangeTracker.DetectChanges();
        if (DespuesDeLeer is { } despues)
            await despues(ct);

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        if (await YaAplicadaAsync(s.Id, ct) is { } aplicadaMientras)
            return aplicadaMientras;
        if (await EstadoDelCatalogoAsync(ct) != leido)
            throw new InvalidOperationException(ProductosEnExcel.CambioDesdeLaVista);
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return new ImportacionAplicadaDto(vista.TotalCambian, vista.TotalNuevos, vista.DepartamentosNuevos.Count, detalle);
    }

    /// <summary>Si ya se aplicó con ese Id (la respuesta se perdió y se volvió a pedir): lo que dijo la bitácora.</summary>
    private async Task<ImportacionAplicadaDto?> YaAplicadaAsync(Guid? id, CancellationToken ct)
    {
        if (id is not { } clave || await db.Bitacora.AsNoTracking().SingleOrDefaultAsync(b => b.Id == clave, ct) is not { } hecho)
            return null;
        if (hecho.Accion != AccionControlada.EditarCatalogo || !hecho.Detalle.StartsWith(EnLaBitacora, StringComparison.Ordinal))
            throw new ArgumentException("Ese cambio no es una importación de Excel: vuelve a ver qué cambia.");
        return new ImportacionAplicadaDto(0, 0, 0, hecho.Detalle, YaSeHabiaAplicado: true);
    }

    /// <summary>
    /// Cómo está el catálogo: su versión (sube con cualquier cambio de lo que llevan las cajas: precios, códigos, nombres…), el último
    /// cambio de un producto (también su departamento, que no sube la versión) y los departamentos (que tampoco la suben).
    /// </summary>
    private async Task<(long Version, DateTime? Producto, int Departamentos, DateTime? Departamento)> EstadoDelCatalogoAsync(CancellationToken ct)
    {
        var version = await VersionDelCatalogoAsync(db, ct);
        var producto = await db.Productos.AsNoTracking().OrderByDescending(p => p.ModificadoUtc).Select(p => (DateTime?)p.ModificadoUtc).FirstOrDefaultAsync(ct);
        var departamentos = await db.Departamentos.AsNoTracking().Select(d => d.ModificadoUtc).ToListAsync(ct);
        return (version, producto, departamentos.Count, departamentos.Count == 0 ? null : departamentos.Max());
    }

    /// <summary>Cómo se guarda lo completo de una importación con su renglón de la bitácora.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// Lo completo que aplicó una importación (Configuración › Bitácora, «Lo que cambió»): con su renglón de la bitácora. Vacío si ese
    /// renglón no es de una importación (o es de antes de que se guardara, 2026-10-23).
    /// </summary>
    public async Task<(Bitacora Renglon, DetalleDeImportacionDto Detalle)?> DetalleAsync(Guid bitacoraId, Guid sucursalId, CancellationToken ct = default)
    {
        var renglon = await db.Bitacora.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bitacoraId && b.SucursalId == sucursalId, ct);
        var detalle = await db.DetallesBitacora.AsNoTracking().SingleOrDefaultAsync(d => d.BitacoraId == bitacoraId, ct);
        if (renglon is null || detalle is not { Tipo: DetalleBitacora.ImportacionDeExcel })
            return null;
        return (renglon, JsonSerializer.Deserialize<DetalleDeImportacionDto>(detalle.Contenido, Json)!);
    }

    /// <summary>
    /// El renglón de la bitácora: «Importó de Excel «Productos 2026-10-22.xlsx»: cambió 12 productos y dio de alta 3; dio de alta el
    /// departamento «Escolar»; se ignoraron 2 renglones».
    /// </summary>
    private static string Detalle(string archivo, VistaImportacionDto vista)
    {
        string Cuantos(int n, string uno, string varios) => n == 1 ? uno : $"{n.ToString("#,##0", Mexico)} {varios}";
        var partes = new List<string>();
        if (vista.TotalCambian > 0)
            partes.Add($"cambió {Cuantos(vista.TotalCambian, "1 producto", "productos")}");
        if (vista.TotalNuevos > 0)
            partes.Add($"dio de alta {Cuantos(vista.TotalNuevos, "1 producto nuevo", "productos nuevos")}");
        if (vista.DepartamentosNuevos.Count > 0)
            partes.Add(vista.DepartamentosNuevos.Count == 1
                ? $"dio de alta el departamento «{vista.DepartamentosNuevos[0]}»"
                : $"dio de alta los departamentos {string.Join(", ", vista.DepartamentosNuevos.Select(d => $"«{d}»"))}");
        if (vista.TotalIgnorados > 0)
            partes.Add($"se ignoraron {Cuantos(vista.TotalIgnorados, "1 renglón", "renglones")}");
        var nombre = archivo.Trim();
        if (nombre.Length > 120)
            nombre = $"{nombre[..117]}…";
        return $"{EnLaBitacora}«{nombre}»: {string.Join("; ", partes)}";
    }
}
