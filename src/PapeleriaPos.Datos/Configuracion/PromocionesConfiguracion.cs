using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos.Configuracion;

// Las promociones con fechas (fase 3). Van en la copia de las cajas: cambiarlas sube la versión del catálogo
// (LoQueLlevaLaCopia), y la caja recibe la lista completa cuando cambia. No se borran: se terminan.

internal sealed class PromocionConfiguracion : IEntityTypeConfiguration<Promocion>
{
    public void Configure(EntityTypeBuilder<Promocion> b)
    {
        b.ToTable("Promociones");
        b.Property(p => p.Nombre).HasMaxLength(Promocion.LargoNombre);
        // Los días van como número (lunes = 1 … domingo = 64): son banderas, no un solo valor.
        b.Property(p => p.Dias).HasConversion<int>();
        b.HasOne<Sucursal>().WithMany().HasForeignKey(p => p.SucursalId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(p => p.CreadaPorId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(p => p.TerminadaPorId);
        b.HasMany(p => p.Alcance).WithOne().HasForeignKey(a => a.PromocionId);
        b.HasIndex(p => p.Version);
        b.HasIndex(p => new { p.Activa, p.Hasta });
    }
}

internal sealed class AlcancePromocionConfiguracion : IEntityTypeConfiguration<AlcancePromocion>
{
    public void Configure(EntityTypeBuilder<AlcancePromocion> b)
    {
        b.ToTable("AlcancesPromocion");
        b.HasOne<Variante>().WithMany().HasForeignKey(a => a.VarianteId);
        b.HasOne<Departamento>().WithMany().HasForeignKey(a => a.DepartamentoId);
        b.HasOne<Marca>().WithMany().HasForeignKey(a => a.MarcaId);
        b.HasIndex(a => a.VarianteId);
        b.HasIndex(a => a.Version);
    }
}
