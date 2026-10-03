using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Datos.Configuracion;

internal sealed class SucursalConfiguracion : IEntityTypeConfiguration<Sucursal>
{
    public void Configure(EntityTypeBuilder<Sucursal> b)
    {
        b.HasIndex(s => s.Clave).IsUnique();
        b.HasIndex(s => s.Version);
    }
}

internal sealed class DepartamentoConfiguracion : IEntityTypeConfiguration<Departamento>
{
    public void Configure(EntityTypeBuilder<Departamento> b) => b.HasIndex(d => d.Version);
}

internal sealed class MarcaConfiguracion : IEntityTypeConfiguration<Marca>
{
    public void Configure(EntityTypeBuilder<Marca> b)
    {
        b.HasIndex(m => m.NombreNormalizado).IsUnique();
        b.HasIndex(m => m.Version);
    }
}

internal sealed class ProductoConfiguracion : IEntityTypeConfiguration<Producto>
{
    public void Configure(EntityTypeBuilder<Producto> b)
    {
        b.HasOne(p => p.Marca).WithMany().HasForeignKey(p => p.MarcaId);
        b.HasOne(p => p.Departamento).WithMany().HasForeignKey(p => p.DepartamentoId);
        b.HasMany(p => p.Variantes).WithOne(v => v.Producto).HasForeignKey(v => v.ProductoId);
        b.HasIndex(p => p.Version);
    }
}

internal sealed class VarianteConfiguracion : IEntityTypeConfiguration<Variante>
{
    public void Configure(EntityTypeBuilder<Variante> b)
    {
        b.HasMany(v => v.Presentaciones).WithOne(p => p.Variante).HasForeignKey(p => p.VarianteId);
        b.HasIndex(v => v.Version);
    }
}

internal sealed class PresentacionConfiguracion : IEntityTypeConfiguration<Presentacion>
{
    public void Configure(EntityTypeBuilder<Presentacion> b)
    {
        b.HasMany(p => p.Codigos).WithOne(c => c.Presentacion).HasForeignKey(c => c.PresentacionId);
        b.HasIndex(p => p.Version);
    }
}

internal sealed class CodigoBarrasConfiguracion : IEntityTypeConfiguration<CodigoBarras>
{
    public void Configure(EntityTypeBuilder<CodigoBarras> b)
    {
        b.HasIndex(c => c.Codigo).IsUnique();
        b.HasIndex(c => c.Version);
    }
}

internal sealed class PrecioConfiguracion : IEntityTypeConfiguration<Precio>
{
    public void Configure(EntityTypeBuilder<Precio> b)
    {
        b.HasOne(p => p.Variante).WithMany().HasForeignKey(p => p.VarianteId);
        b.HasOne(p => p.Presentacion).WithMany().HasForeignKey(p => p.PresentacionId);
        b.HasOne(p => p.Sucursal).WithMany().HasForeignKey(p => p.SucursalId);
        // Que no se repita una escala lo cuida un índice único en la migración (con IFNULL, porque SQLite considera
        // distintos dos NULL).
        b.HasIndex(p => p.VarianteId);
        b.HasIndex(p => p.Version);
    }
}

internal sealed class PrecioHistorialConfiguracion : IEntityTypeConfiguration<PrecioHistorial>
{
    public void Configure(EntityTypeBuilder<PrecioHistorial> b)
    {
        b.ToTable("PreciosHistorial");
        b.HasIndex(h => new { h.VarianteId, h.FechaUtc });
    }
}

internal sealed class ProveedorConfiguracion : IEntityTypeConfiguration<Proveedor>
{
    public void Configure(EntityTypeBuilder<Proveedor> b) => b.HasIndex(p => p.Version);
}

internal sealed class ProveedorVarianteConfiguracion : IEntityTypeConfiguration<ProveedorVariante>
{
    public void Configure(EntityTypeBuilder<ProveedorVariante> b)
    {
        b.ToTable("ProveedoresVariantes");
        b.HasOne(pv => pv.Proveedor).WithMany().HasForeignKey(pv => pv.ProveedorId);
        b.HasOne(pv => pv.Variante).WithMany().HasForeignKey(pv => pv.VarianteId);
        b.HasIndex(pv => new { pv.ProveedorId, pv.VarianteId }).IsUnique();
        b.HasIndex(pv => pv.Version);
    }
}
