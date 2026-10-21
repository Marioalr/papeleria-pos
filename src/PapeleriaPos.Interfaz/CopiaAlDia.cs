using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// La copia de la caja con lo que cambió encima (entrega 5, parte 2): queda igual que si se hubiera bajado completa en ese
/// momento. Cada producto que cambió llega completo y reemplaza al de la copia, con todos sus códigos; los que ya no van se
/// quitan con los suyos; las existencias que cambiaron se ponen en su producto; y de lo demás (usuarios, permisos, la caja, el
/// ticket, la configuración, los clientes, las marcas, los sinónimos) lo que llegó reemplaza lo de la copia.
/// </summary>
public static class CopiaAlDia
{
    /// <summary>
    /// El formato de la copia que entiende esta caja (<c>VersionesDeLaCopia.Formato</c> en el servicio): si la copia guardada
    /// o la caja principal tienen otro, se baja completa.
    /// </summary>
    public const int Formato = 1;

    public static PaqueteContingenciaDto Aplicar(PaqueteContingenciaDto copia, CambiosCopiaDto cambios)
    {
        if (cambios.PaqueteCompleto is not null)
            throw new InvalidOperationException("Esos no son cambios: la copia se baja completa.");
        var llegaron = cambios.Productos.ToDictionary(p => p.VarianteId);
        var fuera = cambios.Quitados.ToHashSet();
        var existencias = cambios.Existencias.GroupBy(e => e.VarianteId).ToDictionary(g => g.Key, g => g.Last().Cantidad);

        ProductoVentaDto ConSuExistencia(ProductoVentaDto p) =>
            p.LlevaInventario && existencias.TryGetValue(p.VarianteId, out var cantidad) && p.Existencia != cantidad ? p with { Existencia = cantidad } : p;

        // Cada uno en su lugar; los nuevos, al final.
        var productos = new List<ProductoVentaDto>(copia.Productos.Count + llegaron.Count);
        var vistos = new HashSet<Guid>();
        foreach (var p in copia.Productos)
        {
            if (fuera.Contains(p.VarianteId) || !vistos.Add(p.VarianteId))
                continue;
            productos.Add(ConSuExistencia(llegaron.GetValueOrDefault(p.VarianteId) ?? p));
        }
        productos.AddRange(cambios.Productos.Where(p => vistos.Add(p.VarianteId)).Select(ConSuExistencia));

        // Los códigos de los que cambiaron o se fueron, los nuevos; un código es de un solo producto: si llegó con otro, ya no es del de antes.
        var nuevos = cambios.Codigos.Select(c => c.Codigo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var codigos = copia.Codigos
            .Where(c => !llegaron.ContainsKey(c.VarianteId) && !fuera.Contains(c.VarianteId) && !nuevos.Contains(c.Codigo))
            .Concat(cambios.Codigos)
            .ToList();

        var configuracion = cambios.Configuracion ?? copia.Configuracion;
        return copia with
        {
            Version = cambios.Version,
            VersionExistencias = cambios.VersionExistencias,
            Productos = productos,
            Codigos = codigos,
            UltimoFolio = Math.Max(copia.UltimoFolio, cambios.UltimoFolio),
            Caja = cambios.Caja ?? copia.Caja,
            Usuarios = cambios.Usuarios ?? copia.Usuarios,
            Permisos = cambios.Permisos ?? copia.Permisos,
            Reglas = cambios.Reglas ?? copia.Reglas,
            Encabezado = cambios.Ticket?.Encabezado ?? copia.Encabezado,
            Pie = cambios.Ticket?.Pie ?? copia.Pie,
            Logo = cambios.Ticket is { } ticket ? ticket.Logo : copia.Logo,
            Configuracion = configuracion,
            LimiteEfectivoCentavos = cambios.Configuracion is { } nueva ? nueva.LimiteEfectivoCentavos : copia.LimiteEfectivoCentavos,
            Clientes = cambios.Clientes ?? copia.Clientes,
            Registrados = cambios.Registrados ?? copia.Registrados,
            Marcas = cambios.Marcas ?? copia.Marcas,
            Sinonimos = cambios.Sinonimos ?? copia.Sinonimos,
        };
    }
}
