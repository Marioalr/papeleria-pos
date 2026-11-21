using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// El corte del día que se ve (entrega 6, 2026-10-14; como el de eleventa, 2026-10-17): el día que se pidió, lo que contestó la
/// caja principal, «Cerrar el día» e «Imprimir». Lo usan Reportes › Hoy
/// (docs/propuestas/corte-y-salir.md, 2026-11-20), que pone sus partes (TurnosDelDia, MovimientosDelDia,
/// CerrarElDia, DepartamentosDelDia) donde les toca, sin cambiar lo que se le pide al servicio.
/// </summary>
public sealed class CorteDelDiaVisto(ClienteCaja cliente, IImpresora impresora, Guid cajaId, SesionDto sesion)
{
    public Guid CajaId => cajaId;

    public SesionDto Sesion => sesion;

    /// <summary>Lo que contestó la caja principal del día que se ve; vacío mientras llega (o si falló).</summary>
    public CorteDelDiaDto? Dia { get; private set; }

    /// <summary>Cada cuánto se revisa si ya pasó la medianoche con el de hoy a la vista.</summary>
    public static readonly TimeSpan CadaCuanto = TimeSpan.FromMinutes(1);

    /// <summary>«margen de 32 %», y si quedó fuera el artículo común (no tiene costo).</summary>
    public static string MargenDelDia(UtilidadDelDiaDto u, VentasPorDepartamentoDto? porDepartamento) =>
        (u.MargenCentesimas is { } margen ? $"margen de {Nucleo.Catalogo.Margen.Texto(margen)}" : "sin ventas con costo")
        + (porDepartamento?.ArticuloComunCentavos is not (null or 0) ? " · sin artículo común" : "");

    /// <summary>Qué pasó con el resumen al cerrar el día (solo justo después de cerrarlo).</summary>
    public EnvioResumen? Resumen { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Cerrando el día: el botón no se oprime dos veces.</summary>
    public bool Ocupado { get; private set; }

    /// <summary>Hoy, según la caja principal (lo dice el primer corte que llega); mientras, no se sabe.</summary>
    public DateOnly? Hoy { get; private set; }

    /// <summary>El día que se pidió; vacío: hoy.</summary>
    public DateOnly? Pedido { get; private set; }

    /// <summary>Sin impresora (el navegador, las pruebas): como saldría en el papel.</summary>
    public string? VistaPrevia { get; private set; }

    /// <summary>Sin impresora: dónde se ve lo impreso.</summary>
    public string? Impreso { get; private set; }

    /// <summary>Lo impreso en la cola de la impresora de tickets, para decir cómo va.</summary>
    public TrabajoDeImpresion? Trabajo { get; private set; }

    /// <summary>El día que se ve (o el que se está pidiendo).</summary>
    public DateOnly? Fecha => Dia?.Fecha ?? Pedido ?? Hoy;

    public bool EsHoy => Dia is { DeOtroDia: false } || (Dia is null && Pedido is null);

    /// <summary>Cuántas veces se ha pedido: si se movió otra vez antes de que llegara, lo que llega tarde no se enseña.</summary>
    private int _cargas;

    public async Task CargarAsync()
    {
        var carga = ++_cargas;
        Error = null;
        try
        {
            var dia = Pedido is { } fecha ? await cliente.CorteDelDiaAsync(cajaId, sesion.UsuarioId, fecha) : await cliente.CorteDelDiaAsync(cajaId, sesion.UsuarioId);
            if (carga != _cargas)
                return;
            Dia = dia;
            // El de hoy dice qué día es hoy (también si pasó la medianoche, o si una caja principal de antes no supo de otra fecha).
            if (!dia.DeOtroDia)
                (Hoy, Pedido) = (dia.Fecha, null);
        }
        catch (ErrorCaja ex)
        {
            if (carga == _cargas)
                Error = ex.Message;
        }
    }

    /// <summary>
    /// Al día de <paramref name="fecha"/>: hoy (vacío, o uno que no ha llegado) o uno que ya pasó. Lo de antes se quita: lo
    /// impreso y el resumen eran de aquel día.
    /// </summary>
    public Task IrAAsync(DateOnly? fecha)
    {
        Pedido = fecha is { } dia && (Hoy is not { } hoy || dia < hoy) ? dia : null;
        (Dia, Resumen, VistaPrevia, Impreso, Trabajo) = (null, null, null, null, null);
        return CargarAsync();
    }

    /// <summary>
    /// Si pasó la medianoche con el de hoy a la vista, ese día ya pasó: se vuelve a pedir, solo para verlo, sin «Cerrar el
    /// día» (que cerraría el nuevo). Con la hora de esta caja; si la de la caja principal todavía no pasa la medianoche, sigue
    /// siendo hoy y se vuelve a revisar al siguiente minuto. Devuelve si se volvió a pedir.
    /// </summary>
    public async Task<bool> RevisarElDiaAsync()
    {
        if (Ocupado || Dia is not { DeOtroDia: false } dia || DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow)) <= dia.Fecha)
            return false;
        Pedido = dia.Fecha;
        (Resumen, VistaPrevia, Impreso, Trabajo) = (null, null, null, null);
        await CargarAsync();
        return true;
    }

    /// <summary>«Cerrar el día», el que se ve. Devuelve si se cerró.</summary>
    public async Task<bool> CerrarAsync()
    {
        if (Ocupado || Dia is not { } dia)
            return false;
        Ocupado = true;
        Error = null;
        try
        {
            // El día que se ve: si ya no es hoy (pasó la medianoche con el corte abierto), el servicio no lo cierra.
            Resumen = (await cliente.CerrarElDiaAsync(cajaId, sesion.UsuarioId, dia.Fecha)).Resumen;
            await CargarAsync();
            return true;
        }
        catch (ErrorCaja ex)
        {
            // Se vuelve a pedir el que se ve, con lo que dice el servicio: si ya pasó (la medianoche), queda solo para verlo,
            // sin «Cerrar el día»; si sigue siendo hoy, con lo que falta para cerrarlo. Y por qué no se cerró.
            Pedido = dia.Fecha;
            await CargarAsync();
            Error = ex.Message;
            return false;
        }
        finally
        {
            Ocupado = false;
        }
    }

    /// <summary>
    /// «Imprimir», como el corte del turno: lo que se ve va a la cola de la impresora de tickets (entrega 5, parte 3), sin
    /// esperarla, y se ve si salió, con «Reintentar» si no; sin impresora, en «Así sale impreso».
    /// </summary>
    public void Imprimir()
    {
        if (Dia is not { } dia)
            return;
        var documento = DisenoComprobante.CorteDelDia(dia, sesion.Nombre, HoraMexico.ALocal(DateTime.UtcNow));
        var cola = ColaDeImpresion.De(impresora);
        if (cola.MuestraVistaPrevia)
        {
            VistaPrevia = TicketTexto.Generar(documento);
            Impreso = "Sin impresora: abajo, en «Así sale impreso».";
            return;
        }
        Trabajo = cola.Encolar($"Corte del día {dia.Fecha.ToString("dd/MM", Formato.Mexico)}", documento, abrirCajon: false);
    }

    // ── Las cuentas que se enseñan en más de un lugar ──

    /// <summary>Los turnos con su corte de este POS (lo contado).</summary>
    public static int Cerrados(CorteDelDiaDto d) => d.Turnos.Count(t => t.ContadoCentavos is not null);

    /// <summary>La suma de las diferencias de corte del día.</summary>
    public static long Diferencias(CorteDelDiaDto d) => d.Turnos.Sum(t => t.DiferenciaCentavos ?? 0);

    /// <summary>
    /// Lo que debe haber en los cajones: el último turno de cada caja. Su fondo ya trae lo que dejó el de antes; sumarlos contaba
    /// el mismo dinero varias veces (auditoría, 2026-11-04).
    /// </summary>
    public static long Esperado(CorteDelDiaDto d) => d.Turnos.GroupBy(t => t.CajaId).Sum(caja => caja.MaxBy(t => t.AbiertoLocal)!.EsperadoCentavos ?? 0);

    /// <summary>«debe haber en las cajas: $…», o lo del piloto.</summary>
    public static string NotaDelEfectivo(CorteDelDiaDto d) =>
        d.Piloto ? "lo esperado del piloto, en eleventa" : $"debe haber en las cajas: {Formato.Pesos(Esperado(d))}";

    /// <summary>«1 de 2 turnos cerrados».</summary>
    public static string NotaDeLasDiferencias(CorteDelDiaDto d) =>
        $"{Cerrados(d)} de {d.Turnos.Count} {(d.Turnos.Count == 1 ? "turno cerrado" : "turnos cerrados")}";

    /// <summary>Qué pasó con el resumen al cerrar el día (solo justo después de cerrarlo).</summary>
    public static string TextoResumen(EnvioResumen? resumen) => resumen switch
    {
        EnvioResumen.Enviado => "El resumen va por Telegram.",
        EnvioResumen.YaHabiaSalido => "El resumen ya había salido por Telegram con el último corte: no se manda otra vez.",
        EnvioResumen.SinTelegram => "Telegram no está vinculado: el resumen no salió.",
        EnvioResumen.Apagado => "El resumen del día está apagado en los avisos de Telegram.",
        _ => "",
    };
}
