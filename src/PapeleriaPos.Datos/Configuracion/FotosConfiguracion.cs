using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Datos.Configuracion;

// Las fotos de los productos (entrega 13, parte 2): una por variante, chica, en la misma base (así el respaldo diario y el de
// cada corte Z las llevan, y restaurar uno las regresa como estaban). Van en la copia de las cajas solo por su huella:
// cambiarlas sube la versión del catálogo (LoQueLlevaLaCopia) y quitarlas queda anotado (Borrado).

internal sealed class FotoProductoConfiguracion : IEntityTypeConfiguration<FotoProducto>
{
    public void Configure(EntityTypeBuilder<FotoProducto> b)
    {
        b.HasKey(f => f.VarianteId);
        b.HasOne<Variante>().WithOne().HasForeignKey<FotoProducto>(f => f.VarianteId);
        b.Property(f => f.Huella).HasMaxLength(FotoProducto.LargoHuella);
        b.HasIndex(f => f.Version);
    }
}
