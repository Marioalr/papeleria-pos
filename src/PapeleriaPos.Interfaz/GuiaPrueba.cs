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
/// <remarks>
/// Es la de la pantalla de venta del rediseño (docs/propuesta-interfaz.md), la única desde el 2026-10-14 (el dueño quitó la
/// de antes: «no volveremos a esa versión»), para la segunda prueba (17 y 18 de octubre de 2026): la primera (2026-10-10)
/// fue con la pantalla de antes, y al dueño le pareció horrible. Primero va entrar con la ficha y vender, para probar lo
/// importante antes de que quien prueba se canse; luego la tarjeta, la transferencia y el crédito, que siguen (el solo
/// efectivo es del piloto con eleventa, y la prueba va sin eleventa), y lo de la entrega 6: cambiar de usuario y el corte,
/// del turno y del día. Al final, lo que todavía abre sus ventanas de antes (productos, inventario, clientes,
/// configuración), con el tema marcado «Ventana de antes»: se hace con otro turno del cajero, abierto después del corte del
/// día. Los temas van seguidos: la guía los agrupa.
/// </remarks>
public static class GuiaPrueba
{
    /// <summary>
    /// Las respuestas se guardan con la versión de la guía, y las de otra versión no se cargan: el mismo Id puede ser otra
    /// tarea, y la guía nueva saldría ya contestada. La 1 fue la de la primera prueba (2026-10-10, la pantalla de antes); la
    /// 2, la de la pantalla nueva; la 3 (2026-10-14), la de la entrada con fichas y el corte nuevo, donde «entrar»,
    /// «otro-turno», «alta-rapida» y «corte» cambiaron de sentido. Se sube cuando las tareas cambian de sentido.
    /// </summary>
    public const int Version = 3;

    /// <summary>
    /// Lo que va antes del tema de lo que todavía abre sus ventanas de antes, encima de la venta o en «Productos e
    /// inventario, sin cortar»: productos, inventario, clientes y configuración, que se rediseñan después (entregas 7 y 8).
    /// </summary>
    public const string VentanaDeAntes = "Ventana de antes";

    /// <summary>El tema de lo último de la guía: la Configuración nueva (entrega 7), que ya no es una ventana de antes.</summary>
    public const string ConfiguracionNueva = "Configuración";

    public static readonly IReadOnlyList<TareaPrueba> Tareas =
    [
        new("entrar", "Empezar",
            "En «¿Quién atiende?» toca la ficha «Cajero de prueba» (o oprime la C). Teclea primero un PIN equivocado, 0000; espera a que diga "
            + "que no es y teclea el bueno, 1111, con el teclado de la pantalla o con el de la PC. Abre el turno con un fondo de $500 y Enter.",
            "A la izquierda están la caja, la hora y cómo está todo: «Servicio en esta PC», la impresora de tickets (o «Ticket en pantalla») y "
            + "«Sin turno abierto». A la derecha, una ficha por usuario con su letra y lo que puede hacer: ya no se escribe el usuario. Con "
            + "0000 dice «PIN incorrecto. Te quedan 4 intentos.» bajo los puntos y los borra; un Enter después no quita el mensaje. Con la "
            + "cuarta cifra buena entra solo, sin Enter: «Abrir turno de Cajero de prueba», sin ningún error en rojo aunque hayas oprimido "
            + "Enter. Con el turno abierto se abre la pantalla de venta: arriba a la derecha estás tú («Cajero de prueba») con la hora del "
            + "turno, y el cursor está en el cuadro de captura."),

        // ── Vender ──
        new("escanear", "Vender",
            "Escribe en el cuadro de captura uno de los códigos para vender de «Léeme.txt» y Enter, como si lo escanearas. Escribe otra vez el "
            + "mismo. Luego escribe 3* antes de otro de esos códigos (3*7501…) y Enter.",
            "El producto entra al ticket y queda marcado; el mismo código otra vez suma uno en su renglón. Con 3* entran tres de una vez. El "
            + "total grande de la derecha cambia cada vez y el cursor se queda en el cuadro."),
        new("buscar", "Vender",
            "Escribe en el cuadro de captura, sin oprimir F10, dos pedazos de palabra: «cua pro», y luego al revés: «pro cua». Baja con ↓ hasta "
            + "uno y oprime Enter. Después oprime F10 y escribe «pl gel».",
            "La lista sale debajo del cuadro mientras escribes, sin abrir otra ventana, con lo escrito resaltado, el precio y la existencia; el "
            + "orden de los pedazos no importa. ↑ ↓ eligen, Enter agrega y Esc cierra la lista. F10 solo pone el cursor en el cuadro."),
        new("familias", "Vender",
            "Escribe «pluma gel». En el renglón de la familia oprime → (flecha derecha), baja a uno y oprime Enter. Escribe otra vez «pluma gel» "
            + "y oprime Tab.",
            "Lo que solo cambia en el color, el tamaño o la marca va en un solo renglón, con una ficha por cada uno (el color con su punto). → "
            + "despliega cada uno con su precio y existencia; ← o Esc lo cierran. Enter en el renglón agrega el más vendido. Tab despliega por "
            + "otra cosa (por ejemplo, la marca) y la próxima vez con ese texto se despliega igual."),
        new("afinar", "Vender",
            "Escribe «folder» y toca una de las fichas de «Afinar» (por ejemplo «oficio»). Después escribe con un error de dedo: «sobre manial» "
            + "y luego «cuadreno».",
            "Con un texto general salen fichas con las palabras que separan los resultados, cada una con cuántos tiene; al tocar una se agrega "
            + "a lo escrito y la lista se achica. Con el error de dedo, la lista dice «Parecidos a …: nada coincide exacto, ¿será un error de "
            + "dedo?» y trae los sobres manila y los cuadernos."),
        new("rapidos", "Vender",
            "Escribe 20 en el cuadro y oprime Alt+1 (o toca «Copia B/N» en la fila de los rápidos). Luego oprime Alt+2 (CURP).",
            "Entran 20 copias a $2.00 ($40.00) sin buscar nada. La CURP agrega $10.00 (vende 10 impresiones)."),
        new("cantidades", "Vender",
            "Escribe «3*cua pro» y Enter. Con el cuadro vacío, elige un renglón del ticket con ↑ ↓, escribe 5* y Enter. Cambia su cantidad con "
            + "los botones − y + del renglón (o con las teclas − y +, con el cuadro vacío) y quita otro renglón con Supr.",
            "Lo de antes del asterisco es la cantidad: entran tres del primero de la lista, y 5* solo deja ese renglón en cinco. − y + cambian "
            + "de uno en uno y el total cambia cada vez; Supr quita el renglón sin dejar rastro en el ticket."),
        new("mayoreo", "Vender",
            "Agrega un folder (tienen mayoreo desde 100), escribe 100 y oprime F11. Luego oprime F11 sin escribir nada.",
            "Al llegar a la cantidad el precio baja solo: el renglón dice «Mayoreo desde … · ahorra $…» y bajo el total sale «Ahorro por "
            + "mayoreo». F11 sin nada muestra sus escalas en el panel de la derecha, sin tapar el ticket. El ticket lo dice en «Usted ahorró»."),

        // ── El cobro en el panel del total ──
        new("cobrar", "Cobrar",
            "Agrega un producto y unas hojas blancas sueltas, para que el total lleve centavos. Oprime F12 y luego Esc. Oprime F12 otra vez, "
            + "escribe lo que te dio el cliente (por ejemplo 200) y Enter. En la siguiente venta oprime F12 y Enter, sin escribir nada.",
            "F12 apaga lo demás y deja el cursor en «Pagó con» con el total en gris, ya al peso («En efectivo, al peso»); Esc regresa al cuadro "
            + "sin cobrar. Al escribir, el cambio sale al momento; Enter cobra y el panel deja el cambio en grande hasta que agregas lo del "
            + "siguiente cliente. Sin impresora, el panel dice «Ticket A-… · verlo en pantalla»: el ticket no se abre solo, y ese enlace lo "
            + "muestra con Total, Redondeo y Total a pagar. F12 y Enter cobra exacto."),
        new("cambio-grande", "Cobrar",
            "Cobra algo de menos de $100 como si te pagaran con un billete de $1,000: oprime F12, escribe 1000 y Enter.",
            "Antes del Enter, el panel ya dice el cambio en grande, y el botón, «Cobrar · cambio $…». Un solo Enter cobra, sin preguntar otra "
            + "vez (F12 ya es la confirmación), y el cambio se queda en grande para darlo."),

        // ── Lo demás de la venta ──
        new("acciones", "Más en la venta",
            "Escribe / en el cuadro y recorre la lista. Escribe «/sal» y Enter: haz una salida de efectivo de $100 con su motivo. Después oprime "
            + "F7 y haz una entrada.",
            "«/» muestra todo lo que puedes hacer, con su tecla; lo que pide autorización lleva candado y dice quién la da (por ejemplo «Lo "
            + "autoriza Dueño de prueba»), y lo que no puedes no sale. «/sal» deja solo las que dicen «sal» y Enter la abre. La salida y la "
            + "entrada se capturan en el panel de la derecha, piden motivo y salen en el corte."),
        new("verificador", "Más en la venta",
            "Oprime F9 y escribe (o escanea) un código. Oprime F9 otra vez. Vuelve a abrirlo y ciérralo con Esc.",
            "El verificador sale en el panel de la derecha: muestra el producto, su precio y su existencia sin agregarlo a la venta. F9 o Esc "
            + "regresan a vender, con el cursor en el cuadro."),
        new("alta-rapida", "Más en la venta",
            "Escribe un código que no existe (por ejemplo 7500000000017) y Enter, toca «Venderlo como artículo común» y agrégalo con su "
            + "descripción y su precio. Escribe otra vez el mismo código y Enter, y ahora dalo de alta con «Darlo de alta aquí» (nombre, "
            + "precio y departamento). Luego oprime Ins y agrega otro artículo común.",
            "Bajo el cuadro, en rojo: «No existe «7500000000017». Revisa el código o elige:», con «Darlo de alta aquí» y «Venderlo como "
            + "artículo común». El artículo común (también con Ins) y el alta se llenan en el panel de la derecha y entran a la venta; al "
            + "agregarlos, el aviso rojo se quita. El encargado verá el producto nuevo en Productos (F3) como «Por completar»."),
        new("impresiones", "Más en la venta",
            "Oprime F2 (impresiones por cobrar): agrega una a la venta y cóbrala; marca otra como «no vendida» con su motivo.",
            "Las impresiones salen en el panel de la derecha. La cobrada entra en la línea «Impresiones» del ticket; la no vendida ya no "
            + "aparece por cobrar."),

        // ── Los tickets en espera ──
        new("en-espera", "Tickets en espera",
            "Empieza una venta y oprime F6. Atiende otra y cóbrala. Deja en espera cuatro más, cada una con algo. Retoma una tocando su ficha, "
            + "otra con F5 y otra desde «Más».",
            "Cada ticket en espera queda como una ficha con su hora y su total, junto a «Poner en espera»; desde el cuarto van en «Más (…)». "
            + "F5 retoma el que lleva más tiempo esperando. Si el ticket que atiendes tiene algo, pasa a la espera: nunca se pierde nada y "
            + "ninguno pierde sus productos."),
        new("espera-al-cerrar", "Tickets en espera",
            "Deja un ticket en espera y cierra la caja con la X de la ventana. Ábrela otra vez desde el escritorio y entra con tu ficha (C) y tu "
            + "PIN, 1111.",
            "Vuelves a la venta con tu mismo turno; dice «Volvió el ticket que estaba en espera» (si eran varios, «Volvieron los … tickets "
            + "que estaban en espera») y sus fichas están ahí, con sus productos."),

        // ── Devolver y autorizar ──
        new("devolver", "Devolver y autorizar",
            "Escribe en el cuadro el folio de un ticket que ya cobraste (sale en el panel al cobrar y abajo, en «Último: A-…») y Enter. "
            + "Devuelve solo un producto: escribe 1 en «Devolver», el motivo y oprime «Devolver lo marcado».",
            "Ya no hay botón «Devoluciones»: el folio abre ese ticket en el panel de la derecha. Se devuelve el importe de ese producto y vuelve "
            + "al inventario."),
        new("descuento", "Devolver y autorizar",
            "Elige un renglón (con ↑ ↓ o tocándolo): debajo salen sus acciones. Haz un descuento de 5 % con «Descuento» (o Ctrl+D) y su "
            + "motivo. Luego, a otro renglón, uno de 20 %: cuando pida la autorización, toca la ficha de «Dueño de prueba» y escribe su PIN "
            + "(3333).",
            "La fila dice «Descuento hasta 10 %». El de 5 % entra solo. El de 20 % pide la autorización en el mismo panel: salen las fichas de "
            + "quienes pueden darla (Encargado de prueba y Dueño de prueba) y solo se escribe el PIN del que se toca. Vale una vez."),
        new("cambiar-precio", "Devolver y autorizar",
            "Elige un renglón y oprime «Precio» debajo de él (o Ctrl+E). Escribe un precio nuevo y autoriza.",
            "En la fila, «Precio» lleva un candado con «Dueño de prueba»: el cambio de precio solo lo autoriza el dueño, y el panel pide solo "
            + "su PIN (3333). Con él, el precio cambia solo en esta venta y el renglón dice «Precio cambiado», con el de antes tachado."),
        new("cancelar", "Devolver y autorizar",
            "Escribe / y elige «Devolver o cancelar un ticket». Escribe el folio de otro ticket cobrado, escribe el motivo y oprime «Cancelar "
            + "todo el ticket». Autoriza tocando la ficha de «Encargado de prueba» (2222).",
            "La cancelación pide la autorización en el panel, con las fichas de quienes pueden darla. El ticket queda cancelado y la "
            + "cancelación aparece en «Avisos al dueño» (arriba, en la franja de prueba)."),
        new("cliente-venta", "Cliente del ticket",
            "Toca la flecha «Público en general» arriba del total (o Ctrl+L) y busca a la «Escuela Primaria Benito Juárez» por su teléfono "
            + "(escribe 1234). Agrega productos y cóbrala. En otra venta elige a «Papelería La Esquina» y agrega un solo folder.",
            "El panel del cliente entra por la derecha y la flecha lleva su nombre. La escuela lleva su 10 % en todo el ticket sin pedir "
            + "autorización. La papelería paga el folder a precio de mayoreo aunque sea uno: el renglón dice «Mayoreo de cliente». Si un "
            + "descuento deja algo debajo del costo, la autorización se pide al cobrar, en el panel, y luego sigue el cobro."),

        // ── Tarjeta, transferencia y crédito: siguen (el solo efectivo es del piloto con eleventa) ──
        new("tarjeta-transferencia", "Tarjeta, transferencia y crédito",
            "Cobra con tarjeta: F12 y toca «Tarjeta» junto a «Pagó con»; su monto ya trae el total. La referencia va en «Referencia» "
            + "(opcional); Enter cobra. En otra, deja la tarjeta en la mitad y paga el resto en «Pagó con». En otra, usa «Transf.».",
            "Cada forma agrega su renglón con lo que falta, en el mismo panel. La tarjeta va exacta, sin redondeo al peso. En la mixta el "
            + "efectivo cubre el resto, el cambio es solo del efectivo y el ticket muestra las dos formas. La transferencia dice «queda por "
            + "verificar»."),
        new("credito-fiar", "Tarjeta, transferencia y crédito",
            "Con Ctrl+L elige al «Arq. Ramírez». Agrega productos por unos $200, oprime F12, toca «Crédito» junto a «Pagó con» y Enter.",
            "Al elegirlo, arriba del total dice que debe $350 y que está vencido, con «Abonar» (no impide fiarle). «Crédito» agrega su renglón "
            + "con todo el total y dice lo que debe y lo que le queda. Se cobra sin efectivo y el ticket trae «SU CUENTA»: lo fiado, cuánto debe "
            + "ahora y hasta cuándo paga."),
        new("credito-limite", "Tarjeta, transferencia y crédito",
            "Al mismo cliente fíale otra venta de unos $600 (su límite es $1,000). En otra, baja el monto del crédito y paga el resto en «Pagó "
            + "con».",
            "El panel avisa, con candado, que pasa su límite y quién lo autoriza; al cobrar pide la autorización ahí mismo, con la ficha del "
            + "encargado o del dueño. La autorización aparece en «Avisos al dueño». Con una parte en efectivo, el cambio es solo de lo que dio."),
        new("credito-abono", "Tarjeta, transferencia y crédito",
            "Oprime Ctrl+B, busca al «Arq. Ramírez» y abónale $300 en efectivo pagando con $500. Vuelve a oprimir Ctrl+B y en «Abonos de hoy» "
            + "escribe un motivo y reimprime el comprobante.",
            "El abono se hace en el panel de la derecha. Sale el comprobante con lo que debía, lo que abonó, el cambio ($200) y lo que queda; "
            + "la reimpresión sale igual."),
        new("credito-devolucion", "Tarjeta, transferencia y crédito",
            "Escribe en el cuadro el folio de una de las ventas fiadas y devuelve un producto.",
            "Dice que ese ticket se fió. No sale efectivo: el comprobante dice cuánto se descuenta de su cuenta y cuánto debe ahora."),
        new("credito-sin-conexion", "Tarjeta, transferencia y crédito",
            "En la caja de Windows, oprime «Simular sin conexión» en la franja de prueba. Con Ctrl+L elige a la «Escuela Primaria Benito "
            + "Juárez» y fíale unos $300 con «Crédito»; luego, otra venta de unos $2,500. Con Ctrl+B abónale $100. Oprime «Volver a conectar».",
            "Arriba, el estado dice «Sin la principal» y una franja avisa que se vende con la copia de esta caja. Se fía y se abona con el saldo "
            + "que conoce la caja: el ticket y el comprobante dicen «(saldo según esta caja)». La segunda venta (aun con su 10 %) pasa su límite "
            + "de $2,000 y pide la autorización con la ficha del encargado. Unos segundos después de volver, el aviso se quita y en «Clientes» "
            + "su cuenta tiene las dos ventas y el abono una sola vez, marcados «(sin conexión)»."),

        // ── Cambiar de usuario y el corte (entrega 6) ──
        new("otro-turno", "Cambiar de usuario",
            "Deja algo en el ticket que atiendes, sin cobrarlo ni ponerlo en espera. Oprime Ctrl+U (o tu nombre arriba a la derecha → "
            + "«Cambiar de usuario»), oprime la E y teclea 2222 (Encargado de prueba). Elige «Productos e inventario, sin cortar» y "
            + "mira Productos (F3), Inventario (F4) y Clientes. Sal con tu nombre arriba a la derecha → «Salir» (o Ctrl+U) y entra otra "
            + "vez como cajero (C y 1111).",
            "Ctrl+U regresa a «¿Quién atiende?»: la ficha del cajero dice «· turno abierto» y a la izquierda, «Turno abierto: Cajero de "
            + "prueba desde las …». Como el turno es del cajero, sale «Caja 1 tiene abierto el turno de Cajero de prueba» y «Cada cajero "
            + "responde por su efectivo: antes de que venda Encargado de prueba, se hace el corte del turno de Cajero de prueba.», con "
            + "«Hacer el corte de Cajero de prueba», «Productos e inventario, sin cortar» y «Volver» (Ctrl+U también regresa a las fichas). "
            + "Sin cortar, arriba va la misma barra que en la venta: la caja, Productos (F3), Inventario (F4), Clientes y tu nombre con «sin "
            + "vender»; F3 y F4 cambian de pestaña. Al volver como cajero sigues en la venta, con tu turno; dice «Volvieron los … tickets que "
            + "estaban en espera» y lo que tenías en el ticket está en una de sus fichas, con sus productos: cambiar de usuario no lo pierde."),
        new("entrar-sin-conexion", "Cambiar de usuario",
            "En la caja de Windows, oprime «Simular sin conexión» en la franja de prueba. Oprime Ctrl+U y, sin tocar la pantalla, oprime la "
            + "C, teclea 0000, espera a que diga cuántos intentos quedan, teclea 1111 y oprime Enter de más. Al terminar, «Volver a conectar».",
            "A la izquierda dice «Sin la caja principal: vende con su copia», con el punto ámbar. Las fichas y sus letras son las mismas, y la "
            + "letra y el PIN funcionan aunque acabes de oprimir el botón de la franja. Con 0000 dice cuántos intentos quedan; con 1111 entra "
            + "con la copia de esta caja y vuelves a tu venta, con tu turno. El Enter de más no oprime «Volver a conectar»."),
        new("corte", "El corte",
            "Como cajero, abre «Corte» (arriba) y mira lo que dice. Oprime «Que entre a esta caja» y entra con la ficha del encargado (E) y "
            + "su PIN, 2222: «Hacer el corte de Cajero de prueba». En «Efectivo contado», antes de escribir, escanea cualquier producto con "
            + "el lector (si hay). Luego escribe lo que cuentas (en la prueba no hay billetes: escribe, por ejemplo, 400) y oprime Enter. "
            + "Después, «Listo: abrir el siguiente turno» (ya tiene el cursor: basta Enter) y «Volver», sin abrir turno.",
            "El cajero ve el corte X: sus ventas, la tarjeta y las transferencias, sin el efectivo, y que el corte lo hace quien tiene ese "
            + "permiso (el encargado o el dueño); si hay tickets sin cobrar, dice que se quedan en espera en esta caja para el siguiente "
            + "turno. «Que entre a esta caja» regresa a «¿Quién atiende?». El encargado no vuelve a escribir su PIN ni cuenta billete por "
            + "billete, y no ve cuánto debería haber hasta oprimir Enter. El escaneo no se toma como dinero: el campo se queda como estaba y "
            + "el turno no se cierra. Al cerrar ve lo contado, lo esperado y el sobrante o faltante a nombre de Cajero de prueba, con el "
            + "detalle (fondo, efectivo de ventas, entradas, salidas…); si la diferencia pasa de $50 dice «se avisa por Telegram» y en "
            + "«Avisos al dueño» llega «Diferencia en el corte». Sale el corte impreso («CORTE DEL TURNO (Z)»; sin impresora, en el "
            + "detalle). «Listo» lleva a «Abrir turno de Encargado de prueba»."),
        new("corte-del-dia", "El corte",
            "Entra con la ficha del dueño (D) y su PIN, 3333, y abre tu turno con $500. En «Corte» elige «Del día · todas las cajas». Oprime "
            + "«Hacer ese corte», escribe un total y Enter; luego «Ver el corte del día» y «Cerrar el día». Para seguir con lo de abajo, "
            + "oprime Ctrl+U y abre otro turno del cajero (C, 1111 y $500).",
            "Con el turno del dueño, la venta tiene todas las pestañas, y en «Corte» sale «Turno | Del día · todas las cajas» (al "
            + "cajero y al encargado no). Si el cajero dejó tickets sin cobrar, vuelven a la espera («Volvieron los … tickets que estaban "
            + "en espera»): son de la caja, no del turno; tu corte dice que se quedan para el siguiente turno, y vuelven en el del "
            + "cajero. Arriba: lo vendido hoy, el efectivo (con lo esperado en las cajas), tarjeta y transferencia, y las diferencias "
            + "de corte. La tabla tiene el turno del cajero (lo esperado, lo contado y su diferencia) y el tuyo como «Turno abierto»; "
            + "abajo, las formas de pago, las salidas por motivo, las devoluciones, las cancelaciones, lo fiado y los abonos, y las "
            + "impresiones cobradas. Mientras tu turno siga abierto, «Cerrar el día» espera: «Falta cerrar el turno de Dueño de prueba "
            + "(Caja 1).». Ya cortado, se cierra: «Día cerrado a las … por Dueño de prueba.», y el resumen no se manda otra vez: ya "
            + "salió con el último corte. En «Avisos al dueño» llega un «Resumen del día» con cada corte que deja la caja sin turno "
            + "abierto; al cerrar el día, ninguno más."),

        // ── Lo que todavía abre sus ventanas de antes, con otro turno del cajero ──
        new("productos", $"{VentanaDeAntes} · Productos (F3)",
            "Como encargado (Ctrl+U, E y 2222, y «Productos e inventario, sin cortar»), en Productos (F3) busca un producto, cámbiale el "
            + "precio y mira su kardex. Da de alta uno nuevo con su presentación «Caja 12» y un código interno (botón «Código interno»). "
            + "Vuelve como cajero (tu nombre → «Salir», C y 1111) y escribe ese código en la venta.",
            "El precio nuevo queda guardado y el kardex muestra sus movimientos (ventas, compras…). El producto nuevo se guarda y se vende con "
            + "su código."),
        new("recibir", $"{VentanaDeAntes} · Compras (F4)",
            "Como cajero, en Inventario (arriba, o F4) → Compras → «Recibir mercancía»: elige el proveedor de «Léeme.txt» y cuenta lo que dice "
            + "ahí para la factura (puedes contar uno menos de algo; el nuevo, con su código). Termina el conteo.",
            "Mientras cuentas no ves la nota ni los costos. Lo que no está en el catálogo se cuenta aparte. Al terminar, dice que falta que el "
            + "encargado capture la nota."),
        new("factura", $"{VentanaDeAntes} · Compras (F4)",
            "Como encargado (sin cortar), abre esa compra y carga «Factura de ejemplo.xml» (en «Ejemplos de la prueba», en el escritorio). "
            + "Confirma el que pregunta «¿Es…?», corrige lo que quieras del producto nuevo y dalo de alta. Cierra la compra.",
            "Todo el pedido queda precargado y se puede corregir renglón por renglón (del nuevo: nombre, precio, piezas). Los productos con "
            + "código llegan solos; el nuevo se da de alta con lo que contaste. La comparación marca lo que faltó y al cerrar entra al "
            + "inventario."),
        new("nota-pdf", $"{VentanaDeAntes} · Compras (F4)",
            "Haz otra compra con lo que dice «Léeme.txt» para la nota (el cajero cuenta, el encargado la captura) y carga «Nota de ejemplo.pdf».",
            "Lee los renglones del PDF, avisa que repartió el IVA y el producto que ligaste antes ya llega ligado."),
        new("sugerido", $"{VentanaDeAntes} · Compras (F4)",
            "Como encargado (sin cortar), abre F4 → Sugerido de compra. Cambia la cantidad de un producto, quita otro y da «Hacer orden de "
            + "compra» en un proveedor. En «Órdenes de compra» abre esa orden y oprime «Copiar el texto».",
            "Sale lo que hay que pedir por proveedor, lo urgente marcado. La orden tiene lo que dejaste, y su texto se pega en otro lado "
            + "(WhatsApp, Bloc de notas) con cantidades y claves."),
        new("paquete", $"{VentanaDeAntes} · Inventario (F4)",
            "En F4 → Abrir paquete, abre un producto en otro (por ejemplo, un paquete en sus piezas).",
            "Sale uno y entran sus piezas; aparece en la lista de abajo con tu nombre."),
        new("conteo", $"{VentanaDeAntes} · Inventario (F4)",
            "Como cajero, toca «Conteo del día» abajo a la derecha, en la barra de teclas. Cuenta dos o tres de esos productos (en la prueba, "
            + "inventa la cantidad).",
            "El chip dice cuántos llevas de cuántos. No ves cuánto dice el sistema. Como encargado (sin cortar), en F4 → Conteo del día ves la "
            + "diferencia; si vale $200 o más, sale en «Avisos al dueño»."),
        new("conteo-completo", $"{VentanaDeAntes} · Inventario (F4)",
            "Como encargado (sin cortar), en F4 → Conteo completo abre un departamento chico. Con un celular en el mismo wifi, escanea su "
            + "código QR, entra con el usuario «cajero» y el PIN 1111 (en el celular todavía se escribe el usuario) y cuenta tres productos, "
            + "uno en dos partes (anaquel y bodega). Luego, en la PC, «Revisar y cerrar».",
            "Sin celular, la dirección que muestra se abre en el navegador de esta PC. En el celular no se ve lo que dice el sistema y las dos "
            + "partes se suman. En la revisión salen las diferencias; al cerrar, lo contado queda como existencia, lo que nadie contó queda en "
            + "0 y sale en «Avisos al dueño»."),
        new("etiquetas", $"{VentanaDeAntes} · Inventario (F4)",
            "Como encargado (sin cortar), en F4 → Etiquetas agrega la compra que cerraste (quita «Solo lo que no trae código de fábrica») e "
            + "imprime la hoja de prueba desde «Vista previa» (o a «Microsoft Print to PDF»). En F4 → Hoja de códigos, agrega tres gomas y da "
            + "«Vista previa».",
            "Sale una etiqueta por pieza con el nombre y su código de barras, en hojas carta; la hoja de prueba marca dónde cae cada una. La "
            + "hoja de códigos trae el nombre y el código de cada producto, por departamento, y al volver a entrar la lista sigue ahí."),
        new("mermas-ajustes", $"{VentanaDeAntes} · Inventario (F4)",
            "Como cajero, en Inventario (F4) → Ajustes y mermas, registra una merma de 1 producto dañado y luego una de 30 piezas de algo "
            + "caro. Después corrige la existencia de un producto a la que cuentes en el anaquel, con su motivo.",
            "La merma chica entra sola; la grande (más de $200 de costo) y la corrección piden la autorización: se toca la ficha del encargado "
            + "y se escribe su PIN. Todo queda en la lista y en la existencia; si la corrección vale $200 o más, sale en «Avisos al dueño»."),
        new("cliente-alta", $"{VentanaDeAntes} · Clientes",
            "Como encargado (sin cortar → Clientes), da de alta un cliente con teléfono y 5 % de descuento; abre a «Maestra Carmen» («Por "
            + "revisar») y guárdala. En la pestaña «Por revisar», lee lo del «Arq. Ramírez», anota algo y márcalo «Revisado».",
            "El cliente queda en la lista y ya se puede elegir con Ctrl+L. En «Avisos al dueño» llega que el encargado le dio privilegios. Al "
            + "guardar a Maestra Carmen se quita «Por revisar». Lo del Arq. Ramírez dice qué pasó, cuándo y por cuánto; al marcarlo sale de la "
            + "lista."),
        new("credito-saldo-a-favor", $"{VentanaDeAntes} · Clientes",
            "Como cajero, abre «Clientes» (arriba) → «Papelería La Esquina» (tiene $150 a favor) y oprime «Devolver saldo a favor». Deja "
            + "vacío «Cuánto» (todo) y oprime «Devolver».",
            "El cajero ve los clientes y sus cuentas, pero no los cambia. «Devolver saldo a favor» lleva candado: pide la autorización con la "
            + "ficha del encargado (2222). Sale un comprobante para que el cliente firme que recibió el efectivo; su cuenta queda al corriente y "
            + "en «Avisos al dueño» llega que se devolvió."),
        new("credito-cuenta", $"{VentanaDeAntes} · El dueño",
            "Como dueño, sin cortar (Ctrl+U, D y 3333, y «Productos e inventario, sin cortar» → Clientes): en «Solo los que deben», abre al "
            + "«Arq. Ramírez», ponle plazo de 15 días, imprime su «Estado de cuenta» y oprime «Copiar para WhatsApp». Sin el filtro, dale $500 "
            + "de crédito a «Papelería La Esquina».",
            "La lista dice cuánto debe cada quien (en rojo, si tiene algo vencido). En su cuenta están el saldo inicial, lo fiado, los abonos "
            + "y la devolución, cada uno con el saldo que dejó; el estado de cuenta y el texto copiado (pégalo en el Bloc de notas) dicen lo "
            + "mismo, con lo que falta pagar de cada compra y su vencimiento. Solo el dueño cambia el crédito."),
        new("margen", $"{ConfiguracionNueva} · El dueño",
            "Como dueño (sin cortar), abre «Configuración» (arriba) → Inventario y sube el «Margen mínimo» general a 50 (se guarda al salir "
            + "del campo). Luego cierra otra compra (Inventario F4 → Compras).",
            "Arriba dice «Guardado a las …», sin botón de guardar ni PIN. Los productos debajo del 50 % sugieren precio y se avisa al dueño. "
            + "Regrésalo a 30 al terminar."),
        new("sinonimos", $"{ConfiguracionNueva} · El dueño",
            "Como dueño, entra a vender: sal de «sin cortar» (tu nombre → «Salir»), entra otra vez (D y 3333) y oprime «Hacer el corte de "
            + "Cajero de prueba»; escribe cualquier total y Enter, y con «Listo» abre tu turno con $500. Escribe «lapicero» en el cuadro de "
            + "captura. Luego, en Configuración → Venta y cobro → «Sinónimos de la búsqueda» (o escribe «sinónimos» en su buscador), agrega: "
            + "al escribir «lapicero» también busca «pluma». Vuelve a escribir «lapicero» en el cuadro.",
            "La primera vez no sale ninguna pluma: solo «Parecidos a «lapicero»…» (lapiceras y estuches). Con el sinónimo salen las plumas, y "
            + "cada renglón dice «por «lapicero»». Los sinónimos solo los dan de alta el dueño y el encargado; uno mal puesto mezcla "
            + "productos, y se quita ahí mismo."),
        new("configuracion", $"{ConfiguracionNueva} · El dueño",
            "Como dueño, en Configuración escribe «fondo» en el buscador y pon el fondo sugerido en $500; en Ticket cambia el pie y mira «Así "
            + "sale». Mira «Usuarios y permisos» y la Bitácora. Abre «Avisos al dueño» en la franja de prueba.",
            "Cada cambio se guarda solo («Guardado a las …»), sin botón ni PIN, y el siguiente «Abrir turno» propone $500. En «Usuarios y "
            + "permisos», Dueño de prueba dice «Todos los permisos · primera cuenta», Encargado de prueba «Corte de turno, productos y "
            + "clientes» y Cajero de prueba «Permisos por defecto»; cada permiso es Puede, Con autorización o No (en Administración, solo "
            + "Puede o No). La bitácora tiene lo de hoy: cancelaciones, precios, autorizaciones, compras, los cortes del turno y el cierre del "
            + "día. En «Avisos al dueño» están los avisos que se habrían mandado: cancelaciones, cortes con diferencia, el resumen del día…"),
    ];

    /// <summary>
    /// La primera tarea sin contestar después de <paramref name="desde"/> (-1: desde el principio), dando la vuelta; -1 si
    /// ya están todas. Es la que la franja de prueba enseña mientras se hace.
    /// </summary>
    public static int SinContestarDespuesDe(IReadOnlyDictionary<string, RespuestaTareaDto> respuestas, int desde)
    {
        var total = Tareas.Count;
        for (var k = 1; k <= total; k++)
        {
            var i = ((desde + k) % total + total) % total;
            if (respuestas.GetValueOrDefault(Tareas[i].Id)?.SalioBien is null)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// «Siguiente» en la franja: la que sigue sin contestar (la que se saltó vuelve al dar la vuelta); si las demás ya
    /// están, la que sigue en orden; -1 si ya están todas.
    /// </summary>
    public static int Siguiente(IReadOnlyDictionary<string, RespuestaTareaDto> respuestas, int actual)
    {
        var siguiente = SinContestarDespuesDe(respuestas, actual);
        return siguiente == actual ? (actual + 1) % Tareas.Count : siguiente;
    }

    /// <summary>Las respuestas como documento: lo que se guarda en el escritorio para mandarlo.</summary>
    public static string Documento(RespuestasPruebaDto respuestas, DateTime ahora)
    {
        var t = new StringBuilder();
        t.AppendLine("# Respuestas de la prueba de Papelería POS");
        t.AppendLine();
        t.AppendLine($"- Quién probó: {(string.IsNullOrWhiteSpace(respuestas.Nombre) ? "(sin nombre)" : respuestas.Nombre.Trim())}");
        t.AppendLine($"- Fecha: {ahora:dd/MM/yyyy HH:mm}");
        t.AppendLine($"- Guía: versión {respuestas.Version}{(respuestas.Version == Version ? "" : $" (la de esta caja es la {Version})")}");
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
