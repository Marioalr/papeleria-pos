namespace PapeleriaPos.Herramientas.Simulador;

/// <summary>Lo que se mide de cada caja, como lo espera quien atiende (las claves de <see cref="Mediciones"/>).</summary>
public static class Operacion
{
    public const string Escanear = "Escanear → la línea (contra la copia)";
    public const string CodigoALaPrincipal = "Un código que no está en la copia → la caja principal";
    public const string Buscar = "Escribir → resultados de la búsqueda (contra la copia)";
    public const string CobrarEnLinea = "Cobrar → listo (en línea)";
    public const string CobrarSinConexion = "Cobrar → listo (sin conexión, con la copia)";
    public const string PrecioCambiadoAlCobrar = "Cobrar y que cambió un precio (lo dice la caja principal)";
    public const string Movimiento = "Entrada o salida de efectivo (F7, F8)";
    public const string BuscarFolio = "Buscar un ticket por su folio";
    public const string Devolver = "Devolver (con el ticket ya encontrado)";
    public const string Cancelar = "Cancelar un ticket (con la autorización)";
    public const string Autorizar = "Autorizar con el PIN del encargado";
    public const string Efectivo = "El efectivo del turno, después de cobrar";
    public const string Quitar = "Quitar un producto del ticket (a la bitácora)";
    public const string RegistrarBusqueda = "Anotar lo que se eligió en la búsqueda";
    public const string AbrirCaja = "Abrir la caja nueva: bajar la copia completa y armar su foto";
    public const string Entrar = "Entrar (las fichas y el PIN)";
    public const string AbrirTurno = "Abrir el turno";
    public const string CorteInformativo = "El corte a ciegas (lo que ve quien cierra)";
    public const string CorteZ = "El corte del turno (corte Z)";
    public const string AltaRapida = "Dar de alta un producto que llegó (alta rápida)";
    public const string AltaDeLaCaja = "Dar de alta la caja (el PIN del dueño)";
    public const string CambioDePrecio = "Cambiar un precio (el dueño)";
    public const string PrecioEnLaCopia = "Cambio de precio → en la copia de cada caja";
    public const string AlDiaTrasLaCaida = "Volver la red → la caja sube lo que hizo y trabaja en línea";
}
