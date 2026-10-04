using PapeleriaPos.Nucleo.Control;
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
        _ => accion.ToString(),
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

    public static string Resultado(ResultadoBitacora resultado) => resultado switch
    {
        ResultadoBitacora.Hecho => "Hecho",
        ResultadoBitacora.Autorizado => "Autorizado",
        _ => "Negado",
    };
}
