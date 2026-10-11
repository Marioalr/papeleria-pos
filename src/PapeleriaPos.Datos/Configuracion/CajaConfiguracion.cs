using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Impresiones;
using PapeleriaPos.Nucleo.Inventario;
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
        b.HasOne<ClienteRegistrado>().WithMany().HasForeignKey(v => v.ClienteId);
        b.HasIndex(v => new { v.ClienteId, v.PagadaUtc });
        b.Ignore(v => v.MayoreoDeCliente);
        b.Ignore(v => v.CreditoCentavos);
        b.Ignore(v => v.EsEditable);
        b.Ignore(v => v.CambioCentavos);
        b.Ignore(v => v.LlevaEfectivo);
        b.Ignore(v => v.TotalCobradoCentavos);
    }
}

internal sealed class EnvioAEleventaConfiguracion : IEntityTypeConfiguration<EnvioAEleventa>
{
    public void Configure(EntityTypeBuilder<EnvioAEleventa> b)
    {
        b.ToTable("EnviosAEleventa");
        b.HasOne<Venta>().WithMany().HasForeignKey(e => e.VentaId);
        b.HasOne<Devolucion>().WithMany().HasForeignKey(e => e.DevolucionId);
        b.HasOne<Turno>().WithMany().HasForeignKey(e => e.TurnoId);
        b.HasOne<MovimientoCaja>().WithMany().HasForeignKey(e => e.MovimientoId);
        // Una venta, una devolución, una entrada o salida o un cierre van una sola vez.
        b.HasIndex(e => e.VentaId).IsUnique().HasFilter("\"Tipo\" = 'Venta'");
        b.HasIndex(e => e.DevolucionId).IsUnique();
        b.HasIndex(e => e.TurnoId).IsUnique();
        b.HasIndex(e => e.MovimientoId).IsUnique();
        // La bandeja se recorre por estado y en orden; una devolución busca el ticket por el folio de eleventa.
        b.HasIndex(e => new { e.Estado, e.CreadoUtc });
        b.HasIndex(e => e.FolioEleventa);
        b.Property(e => e.Error).HasMaxLength(500);
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
        b.Ignore(l => l.TieneMayoreo);
        b.Ignore(l => l.MayoreoDeCliente);
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
        b.Property(c => c.SugeridoCoberturaDias).HasDefaultValue(SugeridoCompra.Parametros.PorOmision.CoberturaDias).ValueGeneratedNever();
        b.Property(c => c.SugeridoColchonDias).HasDefaultValue(SugeridoCompra.Parametros.PorOmision.ColchonDias).ValueGeneratedNever();
        // Las medidas de una hoja propia van como JSON: se leen y se guardan juntas.
        b.Property(c => c.EtiquetasPropio).HasConversion(
            f => f == null ? null : JsonSerializer.Serialize(f, JsonOpciones),
            j => j == null ? null : JsonSerializer.Deserialize<FormatoEtiquetas>(j, JsonOpciones));
        // Los rápidos, en orden, también como JSON: se configuran y se leen juntos.
        b.Property(c => c.Rapidos).HasConversion(
            r => r == null ? null : JsonSerializer.Serialize(r, JsonOpciones),
            j => j == null ? null : JsonSerializer.Deserialize<List<Rapido>>(j, JsonOpciones),
            new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<Rapido>?>(
                (x, y) => x == null ? y == null : y != null && x.SequenceEqual(y),
                l => l == null ? 0 : l.Aggregate(0, (h, r) => HashCode.Combine(h, r)),
                l => l == null ? null : l.ToList()));
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

internal sealed class ClienteRegistradoConfiguracion : IEntityTypeConfiguration<ClienteRegistrado>
{
    public void Configure(EntityTypeBuilder<ClienteRegistrado> b)
    {
        b.ToTable("ClientesRegistrados");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
        b.Property(c => c.Nombre).HasMaxLength(80);
        b.Property(c => c.Telefono).HasMaxLength(20);
        b.Property(c => c.Nota).HasMaxLength(200);
        b.Property(c => c.PlazoCreditoDias).HasDefaultValue(ClienteRegistrado.PlazoPorOmision).ValueGeneratedNever();
        b.Ignore(c => c.TieneCredito);
    }
}

/// <summary>La cuenta de los clientes (fase 3, entrega 2): el libro de solo agregar, los abonos y lo que quedó por revisar.</summary>
internal sealed class MovimientoCreditoConfiguracion : IEntityTypeConfiguration<MovimientoCredito>
{
    public void Configure(EntityTypeBuilder<MovimientoCredito> b)
    {
        b.ToTable("MovimientosCredito");
        // El Id lo pone lo que lo originó (la venta, el abono, la devolución): repetido no entra.
        b.Property(m => m.Id).ValueGeneratedNever();
        b.HasOne<ClienteRegistrado>().WithMany().HasForeignKey(m => m.ClienteId);
        b.HasOne<Sucursal>().WithMany().HasForeignKey(m => m.SucursalId);
        b.HasOne<Caja>().WithMany().HasForeignKey(m => m.CajaId);
        b.HasOne<Turno>().WithMany().HasForeignKey(m => m.TurnoId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(m => m.UsuarioId);
        b.HasOne<Venta>().WithMany().HasForeignKey(m => m.VentaId);
        b.HasOne<Devolucion>().WithMany().HasForeignKey(m => m.DevolucionId);
        b.HasOne<Abono>().WithMany().HasForeignKey(m => m.AbonoId);
        b.Property(m => m.Motivo).HasMaxLength(200);
        b.HasIndex(m => new { m.ClienteId, m.FechaUtc });
        b.HasIndex(m => m.VentaId);
        b.HasIndex(m => m.DevolucionId);
        // Un abono se rechaza o se cancela una sola vez.
        b.HasIndex(m => new { m.AbonoId, m.Tipo }).IsUnique().HasFilter("\"AbonoId\" IS NOT NULL");
    }
}

internal sealed class AbonoConfiguracion : IEntityTypeConfiguration<Abono>
{
    public void Configure(EntityTypeBuilder<Abono> b)
    {
        b.ToTable("Abonos");
        b.Property(a => a.Id).ValueGeneratedNever();
        b.HasOne<Sucursal>().WithMany().HasForeignKey(a => a.SucursalId);
        b.HasOne<Caja>().WithMany().HasForeignKey(a => a.CajaId);
        b.HasOne<Turno>().WithMany().HasForeignKey(a => a.TurnoId);
        b.HasOne<ClienteRegistrado>().WithMany().HasForeignKey(a => a.ClienteId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(a => a.UsuarioId);
        b.Property(a => a.Referencia).HasMaxLength(60);
        b.Property(a => a.MotivoCancelacion).HasMaxLength(200);
        b.HasIndex(a => a.TurnoId);
        b.HasIndex(a => new { a.ClienteId, a.FechaUtc });
        b.HasIndex(a => new { a.Forma, a.Verificacion });
        b.Ignore(a => a.CobradoCentavos);
        b.Ignore(a => a.Folio);
    }
}

internal sealed class RevisionCreditoConfiguracion : IEntityTypeConfiguration<RevisionCredito>
{
    public void Configure(EntityTypeBuilder<RevisionCredito> b)
    {
        b.ToTable("RevisionesCredito");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(r => r.SucursalId);
        b.HasOne<ClienteRegistrado>().WithMany().HasForeignKey(r => r.ClienteId);
        b.Property(r => r.Detalle).HasMaxLength(300);
        b.Property(r => r.Nota).HasMaxLength(200);
        // Una revisión por motivo de cada movimiento: lo que sube dos veces no se revisa dos veces.
        b.HasIndex(r => new { r.MovimientoId, r.Motivo }).IsUnique();
        b.HasIndex(r => new { r.SucursalId, r.RevisadaUtc });
        b.HasIndex(r => r.ClienteId);
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

internal sealed class MayoreoPorRevisarConfiguracion : IEntityTypeConfiguration<MayoreoPorRevisar>
{
    public void Configure(EntityTypeBuilder<MayoreoPorRevisar> b)
    {
        b.ToTable("MayoreoPorRevisar");
        b.HasOne<Variante>().WithMany().HasForeignKey(m => m.VarianteId);
        b.HasIndex(m => m.VarianteId);
    }
}
