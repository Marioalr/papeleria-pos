namespace PapeleriaPos.Interfaz;

/// <summary>
/// La ventana de Windows de la caja (PapeleriaPos.Caja). Como en eleventa (el dueño, 2026-11-07): desde arriba (la × de la ventana o
/// el menú del usuario) la caja solo se minimiza, y se cierra desde el corte. En el navegador no hay ventana: no se registra y todo
/// sigue como antes.
/// </summary>
public interface IVentanaDeLaCaja
{
    /// <summary>La manda a la barra de tareas; sigue abierta, con el ticket como estaba.</summary>
    void Minimizar();

    /// <summary>Cierra la caja de verdad (con lo que pregunta al cerrar: lo que falta imprimir, el corte en eleventa).</summary>
    void Cerrar();
}
