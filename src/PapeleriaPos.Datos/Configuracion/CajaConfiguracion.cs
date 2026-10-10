using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Impresiones;
using PapeleriaPos.Nucleo.Telegram;
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
        b.HasIndex(c => c.LlaveHuella).IsUnique();
        b.Property(c => c.LlaveHuella).HasMaxLength(64);
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
        b.Ignore(v => v.DescuentoCentavos);
        b.HasOne<ClienteEspecial>().WithMany().HasForeignKey(v => v.ClienteEspecialId);
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
        b.Ignore(l => l.PrecioCambiado);
        b.Ignore(l => l.CantidadPorDevolver);
        b.Ignore(l => l.TieneDescuento);
        b.Ignore(l => l.ImporteSinDescuentoCentavos);
        b.Ignore(l => l.PorcentajeDescuento);
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
        // Las sucursales que ya tenían configuración arrancan con los umbrales propuestos (docs/fase-1-telegram.md).
        b.Property(c => c.UmbralCorteCentavos).HasDefaultValue(5000L).ValueGeneratedNever();
        b.Property(c => c.UmbralDevolucionCentavos).HasDefaultValue(20000L).ValueGeneratedNever();
        b.Property(c => c.UmbralSalidaCentavos).HasDefaultValue(50000L).ValueGeneratedNever();
        b.Property(c => c.UmbralAjusteCentavos).HasDefaultValue(20000L).ValueGeneratedNever();
        b.Property(c => c.MinutosSinConexion).HasDefaultValue(10).ValueGeneratedNever();
        b.Property(c => c.HoraApertura).HasDefaultValue(new TimeOnly(8, 0)).ValueGeneratedNever();
        b.Property(c => c.HoraCierre).HasDefaultValue(new TimeOnly(21, 0)).ValueGeneratedNever();
        b.Property(c => c.MargenMinimoCentesimas).HasDefaultValue(Margen.MinimoInicial).ValueGeneratedNever();
        b.Property(c => c.EtiquetasFormato).HasDefaultValue(FormatoEtiquetas.PorOmision.Id).ValueGeneratedNever();
        // Las medidas de una hoja propia van como JSON: se leen y se guardan juntas.
        b.Property(c => c.EtiquetasPropio).HasConversion(
            f => f == null ? null : JsonSerializer.Serialize(f, JsonOpciones),
            j => j == null ? null : JsonSerializer.Deserialize<FormatoEtiquetas>(j, JsonOpciones));
    }

    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);
}

internal sealed class ChatTelegramConfiguracion : IEntityTypeConfiguration<ChatTelegram>
{
    public void Configure(EntityTypeBuilder<ChatTelegram> b)
    {
        b.ToTable("ChatsTelegram");
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.UsuarioId);
        b.HasIndex(c => c.ChatId);
    }
}

internal sealed class CodigoVinculacionConfiguracion : IEntityTypeConfiguration<CodigoVinculacion>
{
    public void Configure(EntityTypeBuilder<CodigoVinculacion> b)
    {
        b.ToTable("CodigosVinculacion");
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.UsuarioId);
        b.HasIndex(c => c.Codigo);
    }
}

internal sealed class SolicitudRemotaConfiguracion : IEntityTypeConfiguration<SolicitudRemota>
{
    public void Configure(EntityTypeBuilder<SolicitudRemota> b)
    {
        b.ToTable("SolicitudesRemotas");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(s => s.SucursalId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(s => s.SolicitanteId);
        b.HasIndex(s => s.Estado);
    }
}

internal sealed class MensajeTelegramConfiguracion : IEntityTypeConfiguration<MensajeTelegram>
{
    public void Configure(EntityTypeBuilder<MensajeTelegram> b)
    {
        b.ToTable("MensajesTelegram");
        b.HasIndex(m => m.EnviadoUtc);
        b.HasIndex(m => m.Clave);
        b.HasIndex(m => m.SolicitudId);
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

internal sealed class DevolucionConfiguracion : IEntityTypeConfiguration<Devolucion>
{
    public void Configure(EntityTypeBuilder<Devolucion> b)
    {
        b.ToTable("Devoluciones");
        b.HasOne<Venta>().WithMany().HasForeignKey(d => d.VentaId);
        b.HasOne<Turno>().WithMany().HasForeignKey(d => d.TurnoId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(d => d.UsuarioId);
        b.HasMany(d => d.Lineas).WithOne().HasForeignKey(l => l.DevolucionId);
        b.HasIndex(d => d.VentaId);
        b.HasIndex(d => d.TurnoId);
    }
}

internal sealed class DevolucionLineaConfiguracion : IEntityTypeConfiguration<DevolucionLinea>
{
    public void Configure(EntityTypeBuilder<DevolucionLinea> b)
    {
        b.ToTable("DevolucionesLineas");
        b.HasOne<VentaLinea>().WithMany().HasForeignKey(l => l.VentaLineaId);
    }
}

internal sealed class TrabajoImpresionConfiguracion : IEntityTypeConfiguration<TrabajoImpresion>
{
    public void Configure(EntityTypeBuilder<TrabajoImpresion> b)
    {
        b.ToTable("TrabajosImpresion");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(t => t.SucursalId);
        b.HasOne<Caja>().WithMany().HasForeignKey(t => t.CajaId);
        b.HasOne<Venta>().WithMany().HasForeignKey(t => t.VentaId);
        b.Property(t => t.MargenPorcentaje).HasPrecision(9, 2);
        b.HasIndex(t => new { t.SucursalId, t.Cobro });
        b.HasIndex(t => t.VentaId);
        b.HasIndex(t => new { t.SucursalId, t.ImpresoUtc });
    }
}

internal sealed class ConfiguracionImpresionConfiguracion : IEntityTypeConfiguration<ConfiguracionImpresion>
{
    public void Configure(EntityTypeBuilder<ConfiguracionImpresion> b)
    {
        b.ToTable("ConfiguracionesImpresion");
        b.HasKey(c => c.SucursalId);
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
    }
}

internal sealed class LecturaImpresoraConfiguracion : IEntityTypeConfiguration<LecturaImpresora>
{
    public void Configure(EntityTypeBuilder<LecturaImpresora> b)
    {
        b.ToTable("LecturasImpresora");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(l => l.SucursalId);
        b.HasIndex(l => new { l.SucursalId, l.TomadaUtc });
    }
}

internal sealed class ClienteEspecialConfiguracion : IEntityTypeConfiguration<ClienteEspecial>
{
    public void Configure(EntityTypeBuilder<ClienteEspecial> b)
    {
        b.ToTable("ClientesEspeciales");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
        b.Property(c => c.Nombre).HasMaxLength(80);
    }
}

internal sealed class DecisionDepuracionConfiguracion : IEntityTypeConfiguration<DecisionDepuracion>
{
    public void Configure(EntityTypeBuilder<DecisionDepuracion> b)
    {
        b.ToTable("DecisionesDepuracion");
        b.HasIndex(d => d.VarianteId);
    }
}
