using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos.Configuracion;

internal sealed class MovimientoInventarioConfiguracion : IEntityTypeConfiguration<MovimientoInventario>
{
    public void Configure(EntityTypeBuilder<MovimientoInventario> b)
    {
        b.ToTable("MovimientosInventario");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(m => m.SucursalId);
        b.HasOne<Variante>().WithMany().HasForeignKey(m => m.VarianteId);
        b.HasIndex(m => new { m.SucursalId, m.VarianteId, m.FechaUtc });
        b.HasIndex(m => new { m.DocumentoTipo, m.DocumentoId });
    }
}

internal sealed class ExistenciaConfiguracion : IEntityTypeConfiguration<Existencia>
{
    public void Configure(EntityTypeBuilder<Existencia> b)
    {
        b.HasKey(e => new { e.SucursalId, e.VarianteId });
        b.HasOne<Sucursal>().WithMany().HasForeignKey(e => e.SucursalId);
        b.HasOne<Variante>().WithMany().HasForeignKey(e => e.VarianteId);
    }
}

internal sealed class VentaHistoricaConfiguracion : IEntityTypeConfiguration<VentaHistorica>
{
    public void Configure(EntityTypeBuilder<VentaHistorica> b)
    {
        b.ToTable("VentasHistoricas");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(v => v.SucursalId);
        b.HasMany(v => v.Lineas).WithOne().HasForeignKey(l => l.VentaHistoricaId);
        b.HasIndex(v => v.FechaUtc);
    }
}

internal sealed class VentaHistoricaLineaConfiguracion : IEntityTypeConfiguration<VentaHistoricaLinea>
{
    public void Configure(EntityTypeBuilder<VentaHistoricaLinea> b)
    {
        b.ToTable("VentasHistoricasLineas");
        b.HasOne<Variante>().WithMany().HasForeignKey(l => l.VarianteId);
        b.HasIndex(l => l.VarianteId);
    }
}

internal sealed class MapeoImportacionConfiguracion : IEntityTypeConfiguration<MapeoImportacion>
{
    public void Configure(EntityTypeBuilder<MapeoImportacion> b)
    {
        b.ToTable("MapeosImportacion");
        b.HasIndex(m => new { m.Origen, m.Entidad, m.IdOrigen }).IsUnique();
    }
}

internal sealed class CompraConfiguracion : IEntityTypeConfiguration<Compra>
{
    public void Configure(EntityTypeBuilder<Compra> b)
    {
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
        b.HasOne<Proveedor>().WithMany().HasForeignKey(c => c.ProveedorId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.RecibioId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.CosteoId);
        b.HasMany(c => c.Conteo).WithOne().HasForeignKey(c => c.CompraId);
        b.HasMany(c => c.Renglones).WithOne().HasForeignKey(r => r.CompraId);
        b.HasIndex(c => new { c.SucursalId, c.Folio }).IsUnique();
        b.HasIndex(c => new { c.SucursalId, c.Estado });
        b.Ignore(c => c.FolioTexto);
        b.Ignore(c => c.TotalNotaCentavos);
    }
}

internal sealed class CompraConteoConfiguracion : IEntityTypeConfiguration<CompraConteo>
{
    public void Configure(EntityTypeBuilder<CompraConteo> b)
    {
        b.ToTable("ComprasConteo");
        b.HasOne<Variante>().WithMany().HasForeignKey(c => c.VarianteId);
        b.HasOne<Presentacion>().WithMany().HasForeignKey(c => c.PresentacionId);
    }
}

internal sealed class CompraRenglonConfiguracion : IEntityTypeConfiguration<CompraRenglon>
{
    public void Configure(EntityTypeBuilder<CompraRenglon> b)
    {
        b.ToTable("ComprasRenglones");
        b.HasOne<Variante>().WithMany().HasForeignKey(r => r.VarianteId);
        b.HasOne<Presentacion>().WithMany().HasForeignKey(r => r.PresentacionId);
    }
}

internal sealed class SecuenciaConfiguracion : IEntityTypeConfiguration<Secuencia>
{
    public void Configure(EntityTypeBuilder<Secuencia> b) => b.HasKey(s => s.Nombre);
}
