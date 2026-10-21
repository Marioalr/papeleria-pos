using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Interfaz;

/// <summary>Cómo se llaman las cosas en pantalla.</summary>
public static class Textos
{
    /// <summary>Cómo se llama cada permiso: los mismos nombres que la bitácora y los mensajes del servicio.</summary>
    public static string Accion(AccionControlada accion) => Permisos.Nombre(accion);

    /// <summary>Por qué quedó por revisar lo del crédito que se hizo sin conexión.</summary>
    public static string Motivo(MotivoRevisionCredito motivo) => motivo switch
    {
        MotivoRevisionCredito.PasoLimite => "Pasó su límite sin conexión",
        MotivoRevisionCredito.SinCredito => "Se le fió sin conexión sin tener crédito",
        MotivoRevisionCredito.SaldoAFavor => "Un abono sin conexión lo dejó a favor",
        MotivoRevisionCredito.FueraDeSuCorte => "Un abono llegó después de su corte",
        _ => motivo.ToString(),
    };

    /// <summary>Los niveles de un permiso, como en «Usuarios y permisos».</summary>
    public static string Nivel(NivelPermiso nivel) => nivel switch
    {
        NivelPermiso.Permitido => "Puede",
        NivelPermiso.RequiereAutorizacion => "Con autorización",
        _ => "No",
    };

    public static string Estado(EstadoCompra estado) => estado switch
    {
        EstadoCompra.Recibiendo => "Contando",
        EstadoCompra.PorCostear => "Falta la nota",
        EstadoCompra.Cerrada => "Cerrada",
        _ => "Cancelada",
    };

    public static string Estado(EstadoOrden estado) => estado switch
    {
        EstadoOrden.Abierta => "Abierta",
        EstadoOrden.Recibida => "Recibida",
        _ => "Cancelada",
    };

    public static string Diferencia(DiferenciaCompra diferencia) => diferencia switch
    {
        DiferenciaCompra.Ninguna => "Completo",
        DiferenciaCompra.Falto => "Faltó",
        DiferenciaCompra.Sobro => "Sobró",
        DiferenciaCompra.NoLlego => "No llegó",
        _ => "No está en la nota",
    };

    public static string Resultado(ResultadoBitacora resultado) => resultado switch
    {
        ResultadoBitacora.Hecho => "Hecho",
        ResultadoBitacora.Autorizado => "Autorizado",
        _ => "Negado",
    };

    /// <summary>
    /// Lo que se dice al recibir un abono, al cancelarlo o al devolver un saldo a favor: lo que hay que entregar o el cambio,
    /// y cómo queda su cuenta. En la venta va bajo el cuadro de captura; en la página de Clientes, en el detalle del cliente.
    /// </summary>
    public static string Comprobante(ComprobanteAbonoDto comprobante) =>
        comprobante.EsSaldoDevuelto
            ? $"Entrega {Formato.Dinero(comprobante.CobradoCentavos)} en efectivo a «{comprobante.Cliente}» (su saldo a favor) y que firme el comprobante."
            : comprobante.Cancelado
                ? $"Se canceló el abono {comprobante.Folio}"
                  + (comprobante.Forma == FormaPago.Efectivo ? $": entrega {Formato.Dinero(comprobante.CobradoCentavos)} en efectivo" : "")
                  + $". «{comprobante.Cliente}» vuelve a deber {Formato.Dinero(comprobante.MontoCentavos)}."
                : $"Abono de {Formato.Dinero(comprobante.MontoCentavos)} de «{comprobante.Cliente}». Saldo: {Formato.Dinero(comprobante.SaldoCentavos)}"
                  + (comprobante.Forma == FormaPago.Efectivo ? $". Cambio: {Formato.Dinero(comprobante.CambioCentavos)}." : ".");

    /// <summary>Un movimiento del kardex en Inventario › Movimientos: «Venta», «Lectura de eleventa»…</summary>
    public static string Clase(ClaseMovimiento clase) => clase switch
    {
        ClaseMovimiento.Venta => "Venta",
        ClaseMovimiento.Devolucion => "Devolución",
        ClaseMovimiento.Compra => "Compra",
        ClaseMovimiento.Ajuste => "Ajuste",
        ClaseMovimiento.Merma => "Merma",
        ClaseMovimiento.Conteo => "Conteo",
        ClaseMovimiento.Paquete => "Paquete abierto",
        ClaseMovimiento.InventarioInicial => "Inventario inicial",
        ClaseMovimiento.LecturaEleventa => "Lectura de eleventa",
        ClaseMovimiento.Traspaso => "Traspaso",
        _ => "Consumo de un servicio",
    };

    /// <summary>
    /// Los tipos que se eligen en el filtro de Movimientos, como en eleventa (entradas, salidas, devoluciones y ajustes) y los
    /// del POS. Los traspasos y los consumos de servicios todavía no existen: no se ofrecen.
    /// </summary>
    public static IReadOnlyList<(ClaseMovimiento Clase, string Nombre)> ClasesParaFiltrar { get; } =
    [
        (ClaseMovimiento.Venta, "Ventas"),
        (ClaseMovimiento.Devolucion, "Devoluciones y cancelaciones"),
        (ClaseMovimiento.Compra, "Compras"),
        (ClaseMovimiento.Ajuste, "Ajustes"),
        (ClaseMovimiento.Merma, "Mermas"),
        (ClaseMovimiento.Conteo, "Conteos"),
        (ClaseMovimiento.Paquete, "Paquetes abiertos"),
        (ClaseMovimiento.InventarioInicial, "Inventario inicial"),
        (ClaseMovimiento.LecturaEleventa, "Lectura de eleventa"),
    ];

    /// <summary>Una cantidad en su unidad: «24», «1.5 m», «0.25 kg».</summary>
    public static string Cantidad(decimal cantidad, UnidadBase unidad) => Formato.Numero(cantidad) + unidad switch
    {
        UnidadBase.Metro => " m",
        UnidadBase.Kilogramo => " kg",
        UnidadBase.Litro => " l",
        _ => "",
    };

    /// <summary>«A», «A y B», «A, B y C».</summary>
    public static string Lista(IReadOnlyList<string> cosas) => cosas.Count switch
    {
        0 => "",
        1 => cosas[0],
        _ => $"{string.Join(", ", cosas.Take(cosas.Count - 1))} y {cosas[^1]}",
    };

    /// <summary>Quién autoriza: «Luis», «Carmen o Luis», «Ana, Carmen o Luis»; si son más, los dos primeros y cuántos más.</summary>
    public static string Quien(IReadOnlyList<QuienAutoriza> autorizan) => autorizan.Count switch
    {
        0 => "quien tenga el permiso",
        1 => autorizan[0].Nombre,
        <= 3 => $"{string.Join(", ", autorizan.Take(autorizan.Count - 1).Select(a => a.Nombre))} o {autorizan[^1].Nombre}",
        _ => $"{autorizan[0].Nombre}, {autorizan[1].Nombre} o {autorizan.Count - 2} más",
    };
}
