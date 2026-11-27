using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos.Configuracion;

/// <summary>Los traspasos por archivo, provisionales mientras no exista el servidor (2026-11-26; docs/traspasos.md).</summary>
internal sealed class TraspasoConfiguracion : IEntityTypeConfiguration<Traspaso>
{
    public void Configure(EntityTypeBuilder<Traspaso> b)
    {
        b.ToTable("Traspasos");
        // El Id viaja en el archivo: es el mismo en la sucursal que manda y en la que recibe.
        b.Property(t => t.Id).ValueGeneratedNever();
        b.HasOne<Sucursal>().WithMany().HasForeignKey(t => t.SucursalId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(t => t.UsuarioId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(t => t.CerroId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(t => t.MarcoId);
        b.HasMany(t => t.Renglones).WithOne().HasForeignKey(r => r.TraspasoId);
        b.Property(t => t.OtraSucursal).HasMaxLength(Traspaso.SucursalMaxima);
        b.Property(t => t.Nota).HasMaxLength(Traspaso.NotaMaxima);
        b.Property(t => t.Huella).HasMaxLength(64);
        b.Ignore(t => t.Clave);
        b.Ignore(t => t.Abierto);
        // Los de la sucursal, del más reciente al más viejo.
        b.HasIndex(t => new { t.SucursalId, t.CreadoUtc });
    }
}

internal sealed class TraspasoRenglonConfiguracion : IEntityTypeConfiguration<TraspasoRenglon>
{
    public void Configure(EntityTypeBuilder<TraspasoRenglon> b)
    {
        b.ToTable("TraspasosRenglones");
        b.Property(r => r.Id).ValueGeneratedNever();
        b.HasOne<Variante>().WithMany().HasForeignKey(r => r.VarianteId);
        b.HasOne<Variante>().WithMany().HasForeignKey(r => r.VarianteDelArchivoId);
        b.Property(r => r.Codigo).HasMaxLength(100);
        b.Property(r => r.Nombre).HasMaxLength(300);
        b.Property(r => r.Nota).HasMaxLength(Traspaso.NotaMaxima);
        b.Ignore(r => r.Cambio);
    }
}
