using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Interfaz;

/// <summary>Cómo se llaman las cosas en pantalla.</summary>
public static class Textos
{
    public static string Accion(AccionControlada accion) => accion switch
    {
        AccionControlada.QuitarProducto => "Quitar un producto ya escaneado",
        AccionControlada.ArticuloComun => "Artículo varios / común",
        AccionControlada.CambiarPrecio => "Cambiar un precio en la venta",
        AccionControlada.VenderBajoCosto => "Vender debajo del costo",
        AccionControlada.CancelarTicket => "Cancelar un ticket cobrado",
        AccionControlada.Devolucion => "Devolución",
        AccionControlada.SalidaEfectivo => "Salida de efectivo (F8)",
        AccionControlada.EntradaEfectivo => "Entrada de efectivo (F7)",
        AccionControlada.AbrirCajonSinVenta => "Abrir el cajón sin venta",
        AccionControlada.Reimprimir => "Reimprimir un ticket",
        AccionControlada.VerificarTransferencia => "Verificar una transferencia",
        AccionControlada.CorteZ => "Corte de cierre (Z)",
        AccionControlada.ConfigurarTicket => "Configurar el ticket",
        AccionControlada.EditarReglas => "Editar las reglas",
        AccionControlada.ConsultarBitacora => "Consultar la bitácora",
        AccionControlada.AdministrarCajas => "Dar de alta o quitar cajas",
        AccionControlada.ConfigurarTelegram => "Configurar Telegram",
        AccionControlada.ConfigurarImpresora => "Configurar la impresora",
        AccionControlada.Descuento => "Hacer un descuento",
        AccionControlada.AdministrarClientes => "Dar de alta y editar los clientes registrados",
        AccionControlada.EditarCatalogo => "Editar el catálogo (productos y precios)",
        AccionControlada.AltaRapida => "Dar de alta un código desde la caja",
        AccionControlada.RecibirMercancia => "Recibir mercancía (contar lo que llegó)",
        AccionControlada.CostearCompra => "Capturar la nota del proveedor y cerrar la compra",
        AccionControlada.ConfigurarMargen => "Configurar el margen mínimo",
        AccionControlada.PedirCompra => "Hacer órdenes de compra",
        AccionControlada.AbrirPaquete => "Abrir un paquete (sale el paquete, entran sus piezas)",
        AccionControlada.AjustarInventario => "Ajustar una existencia",
        AccionControlada.Merma => "Registrar una merma (dañado, caducado, perdido)",
        AccionControlada.ContarInventario => "Contar (el conteo del día y el conteo completo)",
        AccionControlada.ConteoCompleto => "Abrir y cerrar el conteo completo de un departamento",
        AccionControlada.DarCredito => "Dar crédito a un cliente (límite y plazo)",
        AccionControlada.ExcederLimiteCredito => "Fiar arriba del límite del cliente",
        AccionControlada.AjustarCredito => "Ajustar el saldo de un cliente",
        AccionControlada.CancelarAbono => "Cancelar un abono (en su turno)",
        AccionControlada.DevolverSaldoAFavor => "Devolver en efectivo el saldo a favor de un cliente",
        _ => accion.ToString(),
    };

    /// <summary>Por qué quedó por revisar lo del crédito que se hizo sin conexión.</summary>
    public static string Motivo(MotivoRevisionCredito motivo) => motivo switch
    {
        MotivoRevisionCredito.PasoLimite => "Pasó su límite sin conexión",
        MotivoRevisionCredito.SinCredito => "Se le fió sin conexión sin tener crédito",
        MotivoRevisionCredito.SaldoAFavor => "Un abono sin conexión lo dejó a favor",
        MotivoRevisionCredito.FueraDeSuCorte => "Un abono llegó después de su corte",
        _ => motivo.ToString(),
    };

    public static string Nivel(NivelPermiso nivel) => nivel switch
    {
        NivelPermiso.Permitido => "Permitido",
        NivelPermiso.RequiereAutorizacion => "Requiere autorización",
        _ => "No permitido",
    };

    public static string Rol(Rol rol) => rol switch
    {
        Nucleo.Usuarios.Rol.Cajero => "Cajero",
        Nucleo.Usuarios.Rol.Encargado => "Encargado",
        _ => "Dueño",
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

    /// <summary>Quién autoriza: «Luis», «Carmen o Luis», «Ana, Carmen o Luis»; si son más, los dos primeros y cuántos más.</summary>
    public static string Quien(IReadOnlyList<QuienAutoriza> autorizan) => autorizan.Count switch
    {
        0 => "quien tenga el permiso",
        1 => autorizan[0].Nombre,
        <= 3 => $"{string.Join(", ", autorizan.Take(autorizan.Count - 1).Select(a => a.Nombre))} o {autorizan[^1].Nombre}",
        _ => $"{autorizan[0].Nombre}, {autorizan[1].Nombre} o {autorizan.Count - 2} más",
    };
}
