using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos.Configuracion;

internal sealed class UsuarioConfiguracion : IEntityTypeConfiguration<Usuario>
{
    public void Configure(EntityTypeBuilder<Usuario> b)
    {
        b.HasIndex(u => u.NombreUsuario).IsUnique();
        b.HasOne<Sucursal>().WithMany().HasForeignKey(u => u.SucursalId);
        b.HasIndex(u => u.Version);
    }
}

internal sealed class CajaConfiguracion : IEntityTypeConfiguration<Caja>
{
    public void Configure(EntityTypeBuilder<Caja> b)
    {
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
        b.HasIndex(c => new { c.SucursalId, c.Serie }).IsUnique();
        b.HasIndex(c => c.Version);
    }
}

internal sealed class TurnoConfiguracion : IEntityTypeConfiguration<Turno>
{
    public void Configure(EntityTypeBuilder<Turno> b)
    {
        b.HasOne<Sucursal>().WithMany().HasForeignKey(t => t.SucursalId);
        b.HasOne<Caja>().WithMany().HasForeignKey(t => t.CajaId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(t => t.CajeroId);
        b.HasIndex(t => new { t.CajaId, t.CerradoUtc });
    }
}

internal sealed class MovimientoCajaConfiguracion : IEntityTypeConfiguration<MovimientoCaja>
{
    public void Configure(EntityTypeBuilder<MovimientoCaja> b)
    {
        b.ToTable("MovimientosCaja");
        b.HasOne<Turno>().WithMany().HasForeignKey(m => m.TurnoId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(m => m.UsuarioId);
    }
}

internal sealed class AperturaCajonConfiguracion : IEntityTypeConfiguration<AperturaCajon>
{
    public void Configure(EntityTypeBuilder<AperturaCajon> b)
    {
        b.ToTable("AperturasCajon");
        b.HasOne<Caja>().WithMany().HasForeignKey(a => a.CajaId);
        b.HasOne<Turno>().WithMany().HasForeignKey(a => a.TurnoId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(a => a.UsuarioId);
        b.HasIndex(a => new { a.CajaId, a.FechaUtc });
    }
}

internal sealed class VentaConfiguracion : IEntityTypeConfiguration<Venta>
{
    public void Configure(EntityTypeBuilder<Venta> b)
    {
        b.HasOne<Sucursal>().WithMany().HasForeignKey(v => v.SucursalId);
        b.HasOne<Caja>().WithMany().HasForeignKey(v => v.CajaId);
        b.HasOne<Turno>().WithMany().HasForeignKey(v => v.TurnoId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(v => v.CajeroId);
        b.HasMany(v => v.Lineas).WithOne().HasForeignKey(l => l.VentaId);
        b.HasMany(v => v.Cobros).WithOne().HasForeignKey(c => c.VentaId);
        b.HasIndex(v => new { v.SucursalId, v.Serie, v.Folio }).IsUnique();
        b.HasIndex(v => v.TurnoId);
        b.HasIndex(v => v.PagadaUtc);
        b.Ignore(v => v.FolioTexto);
        b.Ignore(v => v.NumeroDeArticulos);
        b.Ignore(v => v.EsEditable);
        b.Ignore(v => v.CambioCentavos);
        b.Ignore(v => v.LlevaEfectivo);
    }
}

internal sealed class VentaLineaConfiguracion : IEntityTypeConfiguration<VentaLinea>
{
    public void Configure(EntityTypeBuilder<VentaLinea> b)
    {
        b.ToTable("VentasLineas");
        b.HasOne<Variante>().WithMany().HasForeignKey(l => l.VarianteId);
        b.HasOne<Presentacion>().WithMany().HasForeignKey(l => l.PresentacionId);
        b.HasIndex(l => l.VarianteId);
    }
}

internal sealed class CobroConfiguracion : IEntityTypeConfiguration<Cobro>
{
    public void Configure(EntityTypeBuilder<Cobro> b)
    {
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.VerificadoPorId);
        b.HasIndex(c => c.Verificacion);
    }
}

internal sealed class CorteConfiguracion : IEntityTypeConfiguration<Corte>
{
    public void Configure(EntityTypeBuilder<Corte> b)
    {
        b.HasOne<Turno>().WithMany().HasForeignKey(c => c.TurnoId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.RealizadoPorId);
        b.HasMany(c => c.Conteo).WithOne().HasForeignKey(c => c.CorteId);
    }
}

internal sealed class CorteConteoConfiguracion : IEntityTypeConfiguration<CorteConteo>
{
    public void Configure(EntityTypeBuilder<CorteConteo> b) => b.ToTable("CortesConteo");
}

internal sealed class ConfiguracionTicketConfiguracion : IEntityTypeConfiguration<ConfiguracionTicket>
{
    public void Configure(EntityTypeBuilder<ConfiguracionTicket> b)
    {
        b.ToTable("ConfiguracionesTicket");
        b.HasKey(c => c.SucursalId);
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
        b.Ignore(c => c.RenglonesEncabezado);
        b.Ignore(c => c.RenglonesPie);
        b.Ignore(c => c.TieneLogo);
    }
}

internal sealed class ConfiguracionSucursalConfiguracion : IEntityTypeConfiguration<ConfiguracionSucursal>
{
    public void Configure(EntityTypeBuilder<ConfiguracionSucursal> b)
    {
        b.ToTable("ConfiguracionesSucursal");
        b.HasKey(c => c.SucursalId);
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
    }
}

internal sealed class ReglaPermisoConfiguracion : IEntityTypeConfiguration<ReglaPermiso>
{
    public void Configure(EntityTypeBuilder<ReglaPermiso> b)
    {
        b.ToTable("ReglasPermiso");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(r => r.SucursalId);
        b.HasIndex(r => new { r.Accion, r.Rol, r.SucursalId });
        b.HasIndex(r => r.Version);
    }
}

internal sealed class AutorizacionConfiguracion : IEntityTypeConfiguration<Autorizacion>
{
    public void Configure(EntityTypeBuilder<Autorizacion> b)
    {
        b.ToTable("Autorizaciones");
        b.HasOne<Usuario>().WithMany().HasForeignKey(a => a.SolicitanteId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(a => a.AutorizadorId);
    }
}

internal sealed class BitacoraConfiguracion : IEntityTypeConfiguration<Bitacora>
{
    public void Configure(EntityTypeBuilder<Bitacora> b)
    {
        b.ToTable("Bitacora");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(x => x.SucursalId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(x => x.UsuarioId);
        b.HasIndex(x => new { x.SucursalId, x.FechaUtc });
        b.HasIndex(x => x.VentaId);
    }
}
