using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos.Configuracion;

// Las tablas del módulo de impresión las mapea el módulo (ModeloImpresiones: nombres, llaves, precisión e índices, y
// PosDbContext lo aplica). Aquí solo van, encima, sus llaves foráneas hacia las tablas del POS: el módulo no conoce las
// sucursales, las cajas ni las ventas, y la base sigue igual que antes de separarlo.

internal sealed class TrabajoImpresionConfiguracion : IEntityTypeConfiguration<TrabajoImpresion>
{
    public void Configure(EntityTypeBuilder<TrabajoImpresion> b)
    {
        b.HasOne<Sucursal>().WithMany().HasForeignKey(t => t.SucursalId);
        b.HasOne<Caja>().WithMany().HasForeignKey(t => t.CajaId);
        b.HasOne<Venta>().WithMany().HasForeignKey(t => t.VentaId);
    }
}

internal sealed class ConfiguracionImpresionConfiguracion : IEntityTypeConfiguration<ConfiguracionImpresion>
{
    public void Configure(EntityTypeBuilder<ConfiguracionImpresion> b) => b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
}

internal sealed class LecturaImpresoraConfiguracion : IEntityTypeConfiguration<LecturaImpresora>
{
    public void Configure(EntityTypeBuilder<LecturaImpresora> b) => b.HasOne<Sucursal>().WithMany().HasForeignKey(l => l.SucursalId);
}

internal sealed class TrabajoControlPrintCobradoConfiguracion : IEntityTypeConfiguration<TrabajoControlPrintCobrado>
{
    public void Configure(EntityTypeBuilder<TrabajoControlPrintCobrado> b)
    {
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
        b.HasOne<Venta>().WithMany().HasForeignKey(c => c.VentaId);
    }
}
