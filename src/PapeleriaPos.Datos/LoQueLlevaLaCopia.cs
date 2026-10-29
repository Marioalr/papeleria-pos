using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos;

/// <summary>
/// Lo que lleva la copia de cada caja (el paquete de <c>api/cajas/{id}/paquete</c> y sus cambios, entrega 5, parte 2): la
/// versión del catálogo sube solo cuando cambia algo de eso. Antes subía con cualquier cambio de un registro versionado —un
/// PIN mal tecleado (los intentos del usuario), el último costo de un proveedor al recibir una compra, un departamento— y
/// cada caja volvía a bajar la copia entera (5.7 MB con 6,000 productos ficticios). Abrir y cerrar un turno no toca nada
/// versionado y tampoco la sube.
/// <para>
/// Por omisión todo cuenta: un registro versionado nuevo, o una propiedad nueva de uno que ya va, sube la versión aunque
/// nadie se acuerde de anotarla aquí (a lo más, la caja pide unos cambios de más). Aquí va lo que la copia no lleva.
/// </para>
/// </summary>
public static class LoQueLlevaLaCopia
{
    /// <summary>Versionados que la copia no lleva: agregarlos, cambiarlos o quitarlos no sube la versión.</summary>
    private static readonly HashSet<Type> NoVan = [typeof(Departamento), typeof(Proveedor), typeof(ProveedorVariante)];

    /// <summary>De lo que sí va, lo que no lleva (lo demás, sí).</summary>
    private static readonly Dictionary<Type, HashSet<string>> NoLleva = new()
    {
        // Lleva su usuario, nombre, el hash del PIN, si está activo, su sucursal (quién entra) y el rol de antes. Su número de
        // eleventa no: solo lo usa el puente, en el servicio.
        [typeof(Usuario)] =
            [nameof(Usuario.IntentosFallidos), nameof(Usuario.BloqueadoHastaUtc), nameof(Usuario.PrimeraCuenta), nameof(Usuario.EleventaId)],
        // Solo la caja que pide, con su nombre, serie, sucursal y equipo; la llave no viaja.
        [typeof(Caja)] = [nameof(Caja.LlaveHuella), nameof(Caja.AltaUtc), nameof(Caja.EsPrincipal), nameof(Caja.Activa)],
        [typeof(Sucursal)] = [nameof(Sucursal.Clave), nameof(Sucursal.Activa)],
        // El producto en la venta: su nombre (con la marca), impuesto, si lleva inventario y si está activo. Y su departamento
        // (fase 3): una promoción de un departamento llega a la copia con sus productos.
        [typeof(Producto)] = [nameof(Producto.ClaveSat), nameof(Producto.ClaveUnidadSat), nameof(Producto.PorCompletar)],
        [typeof(Variante)] = [nameof(Variante.ManejaCaducidad), nameof(Variante.JuntadaEnId)],
        [typeof(CodigoBarras)] = [nameof(CodigoBarras.Origen)],
        // La configuración de la tienda que se usa sin conexión (el nombre, el horario, el efectivo, el corte y la espera);
        // los rápidos, las alertas, las compras, las etiquetas y el sugerido se piden en línea.
        [typeof(ConfiguracionSucursal)] =
        [
            nameof(ConfiguracionSucursal.Rapidos), nameof(ConfiguracionSucursal.AlertasApagadas), nameof(ConfiguracionSucursal.UmbralDevolucionCentavos),
            nameof(ConfiguracionSucursal.UmbralSalidaCentavos), nameof(ConfiguracionSucursal.UmbralAjusteCentavos), nameof(ConfiguracionSucursal.MinutosSinConexion),
            nameof(ConfiguracionSucursal.MargenMinimoCentesimas), nameof(ConfiguracionSucursal.EtiquetasFormato), nameof(ConfiguracionSucursal.EtiquetasPropio),
            nameof(ConfiguracionSucursal.EtiquetasAjusteXDecimas), nameof(ConfiguracionSucursal.EtiquetasAjusteYDecimas),
            nameof(ConfiguracionSucursal.SugeridoCoberturaDias), nameof(ConfiguracionSucursal.SugeridoColchonDias),
        ],
    };

    /// <summary>Los versionados que la copia no lleva (para las pruebas).</summary>
    public static IReadOnlyCollection<Type> QueNoVan => NoVan;

    /// <summary>De cada versionado que va, lo que no lleva (para las pruebas: cada nombre tiene que existir en el modelo).</summary>
    public static IReadOnlyDictionary<Type, HashSet<string>> QueNoLleva => NoLleva;

    /// <summary>Si este cambio baja con la copia: agregar o quitar algo que va, o cambiarle algo que lleva.</summary>
    public static bool Cuenta(EntityEntry entrada) =>
        !NoVan.Contains(entrada.Metadata.ClrType) && entrada.State switch
        {
            EntityState.Added or EntityState.Deleted => true,
            EntityState.Modified => entrada.Properties.Any(p => p.IsModified && Lleva(entrada.Metadata.ClrType, p.Metadata.Name)),
            _ => false,
        };

    private static bool Lleva(Type tipo, string propiedad) =>
        propiedad is not (nameof(IVersionado.Version) or nameof(IVersionado.ModificadoUtc))
        && !(NoLleva.TryGetValue(tipo, out var no) && no.Contains(propiedad));

    /// <summary>
    /// Si el cambio deja algo en otro producto: lo quitado y lo que pasó de una variante a otra (un código, una presentación
    /// o un precio), con la variante de la que era. Con lo que solo cambia, la variante de ahora ya lo dice su versión.
    /// </summary>
    internal static bool DejaOtroProducto(EntityEntry entrada) =>
        entrada.State == EntityState.Deleted
        || entrada.State == EntityState.Modified && entrada.Entity switch
        {
            CodigoBarras => entrada.Property(nameof(CodigoBarras.PresentacionId)).IsModified,
            Presentacion => entrada.Property(nameof(Presentacion.VarianteId)).IsModified,
            Precio => entrada.Property(nameof(Precio.VarianteId)).IsModified,
            _ => false,
        };
}
