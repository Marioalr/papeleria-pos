using Microsoft.EntityFrameworkCore;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// El mapeo de las tablas del módulo: los trabajos de impresión, la configuración de la impresora de cada sucursal y sus
/// lecturas, con sus nombres, llaves, precisión e índices. No pone las llaves foráneas hacia las tablas del POS
/// (sucursales, cajas y ventas): esas las agrega el POS encima (PapeleriaPos.Datos, Configuracion/ImpresionesConfiguracion.cs),
/// para que el módulo no arrastre sus entidades. Lo aplica el contexto del POS, y el de las pruebas del módulo.
/// <para>
/// Las tablas viven en la base del POS: un cambio aquí (una tabla, una columna, un índice) pide su migración en
/// PapeleriaPos.Datos, y la prueba de que el modelo no tiene cambios sin migración la reclama si falta. Supone las
/// convenciones del contexto del POS: los enum se guardan como texto y las fechas en UTC.
/// </para>
/// </summary>
public static class ModeloImpresiones
{
    public static ModelBuilder Configurar(ModelBuilder modelo)
    {
        modelo.Entity<TrabajoImpresion>(b =>
        {
            b.ToTable("TrabajosImpresion");
            b.Property(t => t.MargenPorcentaje).HasPrecision(9, 2);
            b.HasIndex(t => new { t.SucursalId, t.Cobro });
            b.HasIndex(t => t.VentaId);
            b.HasIndex(t => new { t.SucursalId, t.ImpresoUtc });
        });
        modelo.Entity<ConfiguracionImpresion>(b =>
        {
            b.ToTable("ConfiguracionesImpresion");
            b.HasKey(c => c.SucursalId);
        });
        modelo.Entity<LecturaImpresora>(b =>
        {
            b.ToTable("LecturasImpresora");
            b.HasIndex(l => new { l.SucursalId, l.TomadaUtc });
        });
        return modelo;
    }
}
