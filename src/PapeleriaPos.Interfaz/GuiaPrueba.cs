using System.Text;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <param name="Id">Fijo: con él se guardan las respuestas aunque cambie el texto.</param>
/// <param name="Hacer">Qué hacer, paso a paso.</param>
/// <param name="Esperado">Qué debe pasar.</param>
public sealed record TareaPrueba(string Id, string Tema, string Hacer, string Esperado);

/// <summary>
/// La guía de la prueba con una persona real (docs/plan.md): tareas cortas de lo que ya funciona, con lo que debe pasar.
/// Quien prueba marca si salió bien y escribe lo que vio; al final se guardan sus respuestas en un archivo.
/// </summary>
public static class GuiaPrueba
{
    public static readonly IReadOnlyList<TareaPrueba> Tareas =
    [
        new("entrar", "Empezar",
            "Entra con el usuario «cajero» y el PIN 1111. Abre el turno con un fondo de $500.",
            "Aparece la pantalla de venta y arriba dice «Le atiende: Cajero de prueba»."),
        new("vender", "Vender",
            "Agrega productos escribiendo su código (en «Léeme.txt» hay algunos) o buscándolos con F10 (por ejemplo «pluma»). "
            + "Cobra con F12 en efectivo y paga con un billete más grande.",
            "El ticket sale en pantalla con los productos, el total y el cambio correcto."),
        new("cantidades", "Vender",
            "En una venta, cambia la cantidad de un producto con + y −, y quita uno con la tecla Supr (DEL).",
            "El total cambia cada vez; quitar un producto no deja rastro en el ticket."),
        new("dos-tickets", "Vender",
            "Empieza una venta, déjala pendiente con F6, atiende otra y cóbrala. Vuelve a la primera con F5 y cóbrala.",
            "Las dos ventas se cobran por separado y ninguna pierde sus productos."),
        new("cobro-mixto", "Vender",
            "Cobra una venta una parte en efectivo y otra con tarjeta.",
            "El ticket muestra las dos formas de pago y el cambio solo del efectivo."),
        new("comun", "Vender",
            "Agrega un artículo común con la tecla Insert: escribe la descripción y el precio a mano.",
            "Entra a la venta con lo que escribiste."),
        new("alta-rapida", "Vender",
            "Escribe un código que no existe (por ejemplo 7500000000017) y Enter. Dalo de alta con nombre, precio y departamento.",
            "Queda en la venta enseguida. En F3 Productos aparece como «Por completar»."),
        new("cambiar-precio", "Control",
            "Cambia el precio de un producto de la venta con Ctrl+E. Cuando lo pida, autoriza con «encargado» y 2222.",
            "Sin la autorización no deja; con ella, el precio cambia solo en esta venta."),
        new("descuento", "Control",
            "Haz un descuento de 5 % a un producto con Ctrl+D. Luego intenta uno de 20 %.",
            "El de 5 % entra solo; el de 20 % pide la autorización del encargado."),
        new("efectivo", "Control",
            "Haz una salida de efectivo con F8 (con motivo) y una entrada con F7.",
            "Las dos piden motivo y salen en el corte."),
        new("cancelar", "Control",
            "En «Devoluciones», cancela un ticket que ya cobraste. Autoriza con el encargado.",
            "El ticket queda cancelado y la cancelación aparece en «Avisos al dueño» (arriba, en la franja de prueba)."),
        new("devolver", "Control",
            "En «Devoluciones», devuelve solo un producto de otra venta.",
            "Se devuelve el importe de ese producto y vuelve al inventario."),
        new("verificador", "Vender",
            "Consulta un precio con F9 (verificador).",
            "Muestra el producto y su precio sin agregarlo a la venta."),
        new("impresiones", "Impresiones",
            "Abre F2 (impresiones por cobrar): agrega una a la venta y cóbrala; marca otra como «no vendida» con su motivo.",
            "La cobrada entra en la línea «Impresiones» del ticket; la no vendida ya no aparece por cobrar."),
        new("corte", "Corte",
            "Haz un corte X (parcial). Después haz el corte Z: entra el encargado, cuenta el efectivo sin ver cuánto debería haber.",
            "El corte X no cierra el turno. El Z lo cierra y muestra si sobró o faltó; con diferencia, sale en «Avisos al dueño»."),
        new("productos", "Productos (F3)",
            "Entra como «encargado» (2222) y elige «Productos e inventario». En F3 busca un producto, cámbiale el precio y mira su kardex.",
            "El precio nuevo queda guardado y el kardex muestra sus movimientos (ventas, compras…)."),
        new("producto-nuevo", "Productos (F3)",
            "Da de alta un producto nuevo con su presentación «Caja 12» y un código interno (botón «Código interno»).",
            "Se guarda y se puede vender escaneando su código."),
        new("recibir", "Compras (F4)",
            "Como «cajero», en F4 Inventario → Compras → «Recibir mercancía»: elige el proveedor de «Léeme.txt» y cuenta lo que dice "
            + "ahí para la factura (puedes contar uno menos de algo). El producto nuevo no está en el catálogo: cuéntalo con su código. Termina el conteo.",
            "Mientras cuentas no ves la nota ni los costos. Lo que no está en el catálogo se cuenta aparte. Al terminar, dice que falta que el encargado capture la nota."),
        new("factura", "Compras (F4)",
            "Como «encargado» (Productos e inventario), abre esa compra y carga «Factura de ejemplo.xml» (carpeta «Ejemplos de la prueba» "
            + "del escritorio). Revisa lo que se precargó: confirma el que pregunta «¿Es…?», corrige lo que quieras del producto nuevo "
            + "(nombre, precio, piezas) y dalo de alta. Cierra la compra.",
            "Todo el pedido queda cargado y se puede corregir renglón por renglón. Los productos con código llegan solos; el nuevo se da de "
            + "alta con lo que contaste. La comparación marca lo que faltó y al cerrar entra al inventario."),
        new("nota-pdf", "Compras (F4)",
            "Haz otra compra con lo que dice «Léeme.txt» para la nota y carga «Nota de ejemplo.pdf».",
            "Lee los renglones del PDF, avisa que repartió el IVA y el producto que ligaste antes ya llega ligado."),
        new("orden", "Compras (F4)",
            "Arma una orden de compra en F4 → Órdenes de compra y copia su texto.",
            "El texto del pedido se pega en otro lado (WhatsApp, Bloc de notas) con cantidades y claves."),
        new("paquete", "Compras (F4)",
            "En F4 → Abrir paquete, abre un producto en otro (por ejemplo, un paquete en sus piezas).",
            "Sale uno y entran sus piezas; aparece en la lista de abajo con tu nombre."),
        new("conteo", "Inventario (F4)",
            "Al abrir el turno aparece «Conteo del día». Cuenta dos o tres de esos productos (en la prueba, inventa la cantidad) con «Contar ahora».",
            "No ves cuánto dice el sistema. Como encargado, en F4 → Conteo del día ves la diferencia; si vale $200 o más, sale en «Avisos al dueño»."),
        new("conteo-completo", "Inventario (F4)",
            "Como «encargado», en F4 → Conteo completo abre un departamento chico. Con un celular en el mismo wifi, escanea el código QR "
            + "de esa pantalla (o abre en el navegador de esta PC la dirección que muestra) y entra como «cajero» (1111). Cuenta tres "
            + "productos, uno de ellos en dos partes (anaquel y bodega). Luego, como encargado, «Revisar y cerrar».",
            "En el celular no se ve lo que dice el sistema y las dos partes se suman. En la revisión salen las diferencias; al cerrar, "
            + "lo contado queda como existencia, lo que nadie contó queda en 0 y sale en «Avisos al dueño»."),
        new("merma", "Inventario (F4)",
            "Como «cajero», en F4 → Ajustes y mermas, registra una merma de 1 producto dañado. Luego intenta una de 30 piezas de algo caro.",
            "La chica entra sola; la grande (más de $200 de costo) pide al encargado. Las dos salen del inventario y quedan en la lista."),
        new("ajuste", "Inventario (F4)",
            "Como «encargado», corrige la existencia de un producto a la que cuentes en el anaquel, con su motivo.",
            "La existencia queda en lo que escribiste; si la diferencia vale $200 o más, sale en «Avisos al dueño»."),
        new("margen", "Dueño",
            "Entra como «dueno» (3333). En F4 → Margen mínimo, sube el margen general a 50 % y cierra otra compra.",
            "Los productos debajo del 50 % sugieren precio y se avisa al dueño. Regrésalo a 30 % al terminar."),
        new("configuracion", "Dueño",
            "Como dueño, en Configuración mira las reglas, la bitácora y el ticket.",
            "La bitácora tiene lo que hiciste en la prueba (cancelaciones, precios, compras…)."),
        new("avisos", "Dueño",
            "Abre «Avisos al dueño» en la franja de prueba.",
            "Están los avisos que le habrían llegado por Telegram: cancelaciones, cortes con diferencia, compras…"),
    ];

    /// <summary>Las respuestas como documento: lo que se guarda en el escritorio para mandarlo.</summary>
    public static string Documento(RespuestasPruebaDto respuestas, DateTime ahora)
    {
        var t = new StringBuilder();
        t.AppendLine("# Respuestas de la prueba de Papelería POS");
        t.AppendLine();
        t.AppendLine($"- Quién probó: {(string.IsNullOrWhiteSpace(respuestas.Nombre) ? "(sin nombre)" : respuestas.Nombre.Trim())}");
        t.AppendLine($"- Fecha: {ahora:dd/MM/yyyy HH:mm}");
        var contestadas = Tareas.Count(x => respuestas.Tareas.GetValueOrDefault(x.Id)?.SalioBien is not null);
        var fallas = Tareas.Count(x => respuestas.Tareas.GetValueOrDefault(x.Id)?.SalioBien == false);
        t.AppendLine($"- Contestadas: {contestadas} de {Tareas.Count} · con problemas: {fallas}");
        foreach (var grupo in Tareas.GroupBy(x => x.Tema))
        {
            t.AppendLine();
            t.AppendLine($"## {grupo.Key}");
            foreach (var tarea in grupo)
            {
                var r = respuestas.Tareas.GetValueOrDefault(tarea.Id);
                var estado = r?.SalioBien switch { true => "Salió bien", false => "Algo falló", null => "Sin contestar" };
                t.AppendLine();
                t.AppendLine($"### {tarea.Hacer}");
                t.AppendLine($"- Debía pasar: {tarea.Esperado}");
                t.AppendLine($"- Resultado: **{estado}**");
                if (!string.IsNullOrWhiteSpace(r?.Nota))
                    t.AppendLine($"- Lo que vio: {r.Nota.Trim()}");
            }
        }
        if (!string.IsNullOrWhiteSpace(respuestas.Comentarios))
        {
            t.AppendLine();
            t.AppendLine("## Comentarios generales");
            t.AppendLine();
            t.AppendLine(respuestas.Comentarios.Trim());
        }
        return t.ToString();
    }
}
