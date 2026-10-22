using System.Globalization;
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
/// <param name="OtrosCodigos">Los demás de su pieza.</param>
/// <param name="Existencia">En la sucursal de la caja; vacía en un servicio.</param>
/// <param name="CostoPromedioCentavos">Solo para quien ve costos, utilidad y margen; vacío sin existencia.</param>
/// <param name="MargenCentesimas">Del menudeo contra el costo promedio, solo para quien lo ve.</param>
public sealed record VarianteParaExcel(
    string? Codigo, IReadOnlyList<string> OtrosCodigos, string Producto, string Variante, string Departamento, string? Marca, UnidadBase Unidad,
    long? PrecioCentavos, IReadOnlyList<EscalaParaExcel> Mayoreo, decimal? Existencia, decimal? Minimo, decimal? Maximo, bool SeVende,
    decimal? CostoPromedioCentavos, int? MargenCentesimas);

/// <summary>Un renglón de la hoja «Presentaciones»: una caja o un paquete de una variante, con sus códigos y su precio propio.</summary>
/// <param name="CodigoDelProducto">El de la columna «Código» de su variante, para reconocerla al importar.</param>
/// <param name="Producto">El nombre de su producto (con su variante), para leerlo.</param>
/// <param name="Contiene">Cuántas unidades trae (piezas, metros…).</param>
/// <param name="PrecioCentavos">Su precio propio, por presentación; vacío: se cobra por sus piezas.</param>
/// <param name="Mayoreo">Sus escalas propias (desde cuántas presentaciones), que casi nunca hay.</param>
public sealed record PresentacionParaExcel(
    string? CodigoDelProducto, string Producto, string Presentacion, decimal Contiene, UnidadBase Unidad, IReadOnlyList<string> Codigos,
    long? PrecioCentavos, IReadOnlyList<EscalaParaExcel> Mayoreo);

/// <summary>Lo que lleva el Excel de los productos.</summary>
/// <param name="Filtros">Los de la lista, en palabras, para el encabezado de cada hoja.</param>
/// <param name="Departamento">Si la lista era de un solo departamento: va en el nombre del archivo.</param>
/// <param name="SinExportar">Los kits y los cobros de terceros de la lista: no van (se cambian en su ficha).</param>
public sealed record ProductosParaExcel(
    IReadOnlyList<VarianteParaExcel> Variantes, IReadOnlyList<PresentacionParaExcel> Presentaciones, bool ConCostos, string Filtros, string? Departamento,
    int SinExportar);

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
                renglones.Add(new VarianteParaExcel(codigos.FirstOrDefault(), codigos.Skip(1).ToList(), producto.Nombre, variante.Descripcion,
                    producto.Departamento?.Nombre ?? "", producto.Marca?.Nombre, variante.UnidadBase, menudeo?.PrecioCentavos,
                    porUnidad.Skip(1).Select(p => new EscalaParaExcel(p.CantidadMinima.EnUnidades, p.PrecioCentavos)).ToList(),
                    mercancia ? existencia?.Cantidad.EnUnidades ?? 0 : null,
                    mercancia ? variante.InventarioMinimo?.EnUnidades : null, mercancia ? variante.InventarioMaximo?.EnUnidades : null,
                    producto.Activo && variante.Activa, costo,
                    costo is { } c && menudeo is { PrecioCentavos: > 0 } m ? Margen.Calcular(m.PrecioCentavos, c) : null));
                foreach (var presentacion in variante.Presentaciones.Where(p => !p.EsBase).OrderBy(p => p.Factor).ThenBy(p => p.Nombre, StringComparer.CurrentCultureIgnoreCase))
                {
                    var propios = suyos.Where(p => p.PresentacionId == presentacion.Id).OrderBy(p => p.CantidadMinima).ToList();
                    presentaciones.Add(new PresentacionParaExcel(codigos.FirstOrDefault(), nombre, presentacion.Nombre, presentacion.Factor.EnUnidades,
                        variante.UnidadBase, Ordenados(presentacion.Codigos), propios.FirstOrDefault()?.PrecioCentavos,
                        propios.Skip(1).Select(p => new EscalaParaExcel(p.CantidadMinima.EnUnidades, p.PrecioCentavos)).ToList()));
                }
            }
        }
        if (renglones.Count > MaximoParaExportar)
            throw new ArgumentException($"Son {renglones.Count.ToString("#,##0", Mexico)} renglones: se exportan hasta "
                + $"{MaximoParaExportar.ToString("#,##0", Mexico)} a la vez. Elige un departamento o escribe una búsqueda.");

        var departamento = departamentoId is { } d ? await db.Departamentos.AsNoTracking().Where(x => x.Id == d).Select(x => x.Nombre).SingleOrDefaultAsync(ct) : null;
        return new ProductosParaExcel(renglones, presentaciones, conCostos, Filtros(texto, filtro, departamento, conInactivos), departamento, sinExportar);
    }

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
    /// </summary>
    public async Task<ImportacionAplicadaDto> AplicarAsync(SolicitudImportarProductos s, Func<LibroDeProductos> leer, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirDeLaSesionAsync(s.UsuarioId, s.SucursalId, null, AccionControlada.EditarCatalogo, ct);
        if (lecturaDeEleventa)
            throw new InvalidOperationException(ProductosEnExcel.ImportarEnElPiloto);
        var libro = leer();
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        if (s.Id is { } id && await db.Bitacora.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct) is { } hecho)
        {
            if (hecho.Accion != AccionControlada.EditarCatalogo || !hecho.Detalle.StartsWith(EnLaBitacora, StringComparison.Ordinal))
                throw new ArgumentException("Ese cambio no es una importación de Excel: vuelve a ver qué cambia.");
            return new ImportacionAplicadaDto(0, 0, 0, hecho.Detalle, YaSeHabiaAplicado: true);
        }
        var plan = await PlanDeImportacion.ArmarAsync(db, s, libro, paraAplicar: true, ct);
        var vista = plan.Vista;
        if (!vista.HayQueAplicar)
            throw new InvalidOperationException("Con ese Excel no cambia nada: todo está igual o se ignora.");
        if (string.IsNullOrEmpty(s.Huella))
            throw new InvalidOperationException("Primero ve qué cambia.");
        if (s.Huella != vista.Huella)
            throw new InvalidOperationException(ProductosEnExcel.CambioDesdeLaVista);

        plan.Aplicar();
        var detalle = Detalle(s.Archivo, vista);
        db.Bitacora.Add(new Bitacora
        {
            Id = s.Id ?? Ids.Nuevo(),
            SucursalId = s.SucursalId,
            UsuarioId = s.UsuarioId,
            Accion = AccionControlada.EditarCatalogo,
            Detalle = detalle,
            Resultado = ResultadoBitacora.Hecho,
            FechaUtc = Ahora,
        });
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return new ImportacionAplicadaDto(vista.TotalCambian, vista.TotalNuevos, vista.DepartamentosNuevos.Count, detalle);
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
