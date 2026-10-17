using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Datos.Configuracion;

// Lo que se señala (entrega 13): los exhibidores y las temporadas, sus lugares y lo acomodado en ellos. Van en la copia de
// las cajas: cambiarlos sube la versión del catálogo (LoQueLlevaLaCopia), y lo que se quita queda anotado (Borrado).

internal sealed class ExhibidorConfiguracion : IEntityTypeConfiguration<Exhibidor>
{
    public void Configure(EntityTypeBuilder<Exhibidor> b)
    {
        b.Property(e => e.Nombre).HasMaxLength(Exhibidor.LargoNombre);
        b.HasMany(e => e.Lugares).WithOne(l => l.Exhibidor).HasForeignKey(l => l.ExhibidorId);
        b.Ignore(e => e.Desde);
        b.Ignore(e => e.Hasta);
        b.HasIndex(e => e.Version);
    }
}

internal sealed class LugarExhibidorConfiguracion : IEntityTypeConfiguration<LugarExhibidor>
{
    public void Configure(EntityTypeBuilder<LugarExhibidor> b)
    {
        b.Property(l => l.Nombre).HasMaxLength(LugarExhibidor.LargoNombre);
        b.HasMany(l => l.Productos).WithOne(p => p.Lugar).HasForeignKey(p => p.LugarId);
        b.HasIndex(l => l.Version);
    }
}

internal sealed class ProductoExhibidoConfiguracion : IEntityTypeConfiguration<ProductoExhibido>
{
    public void Configure(EntityTypeBuilder<ProductoExhibido> b)
    {
        b.HasOne(p => p.Variante).WithMany().HasForeignKey(p => p.VarianteId);
        // Un producto va una vez en cada lugar (en otro lugar, o en otro exhibidor, sí puede estar).
        b.HasIndex(p => new { p.LugarId, p.VarianteId }).IsUnique();
        b.HasIndex(p => p.Version);
    }
}
