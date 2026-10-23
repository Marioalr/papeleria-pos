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
/// del turno y del día. Después, las páginas de atrás (entrega 8): Productos, Inventario por tareas y Clientes, con lista y
/// detalle, que ya no son ventanas encima de la venta; se hacen con otro turno del cajero, abierto después del corte del
/// día. Al final, la Configuración nueva (entrega 7). Los temas van seguidos: la guía los agrupa.
/// </remarks>
public static class GuiaPrueba
{
    /// <summary>
    /// Las respuestas se guardan con la versión de la guía, y las de otra versión no se cargan: el mismo Id puede ser otra
    /// tarea, y la guía nueva saldría ya contestada. La 1 fue la de la primera prueba (2026-10-10, la pantalla de antes); la
    /// 2, la de la pantalla nueva; la 3 (2026-10-14), la de la entrada con fichas y el corte nuevo, donde «entrar»,
    /// «otro-turno», «alta-rapida» y «corte» cambiaron de sentido. Se sube cuando las tareas cambian de sentido. Con las
    /// páginas de atrás (entrega 8) sigue la 3: sus tareas piden lo mismo en las pantallas nuevas, y «paginas» es nueva. Con
    /// la guía v4 (2026-10-17) también: lo nuevo (las ventas de otros días y el historial de cortes, lo que se señala con
    /// Ctrl+M y sus fotos, cambiar precios de varios, empezar y terminar la temporada, los reportes del inventario y «al
    /// cobrar») va en tareas con Id nuevo, y las de antes piden lo mismo con lo que se les agregó (el corte del día como el de
    /// eleventa; los departamentos y el mínimo en Productos).
    /// </summary>
    public const int Version = 3;

    /// <summary>
    /// Cómo empiezan los temas de las páginas de atrás (entrega 8): Productos, Inventario y Clientes, desde la venta o en
    /// «Productos e inventario, sin cortar». Ya no queda nada que abra sus ventanas de antes.
    /// </summary>
    public static readonly IReadOnlyList<string> Paginas = ["Productos", "Inventario", "Clientes"];

    /// <summary>Si el tema es de las páginas de atrás.</summary>
    public static bool EsDeLasPaginas(string tema) => Paginas.Any(p => tema.StartsWith(p, StringComparison.Ordinal));

    /// <summary>El tema de lo último de la guía: la Configuración nueva (entrega 7).</summary>
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
            + "muestra con Total, Redondeo y Total a pagar. F12 y Enter cobra exacto. La prueba no tiene impresora de tickets: «Reintentar» y "
            + "«No imprimirlo» solo se ven con una impresora que falle."),
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
            + "precio y departamento). Luego oprime Ctrl+P y agrega otro artículo común.",
            "Bajo el cuadro, en rojo: «No existe «7500000000017». Revisa el código o elige:», con «Darlo de alta aquí» y «Venderlo como "
            + "artículo común». El artículo común (también con Ctrl+P) y el alta se llenan en el panel de la derecha y entran a la venta; al "
            + "agregarlos, el aviso rojo se quita. El encargado verá el producto nuevo en Productos (F3), en «Por completar»."),
        new("impresiones", "Más en la venta",
            "Oprime F2 (impresiones por cobrar): agrega una a la venta y cóbrala; marca otra como «no vendida» con su motivo.",
            "Las impresiones salen en el panel de la derecha. La cobrada entra en la línea «Impresiones» del ticket; la no vendida ya no "
            + "aparece por cobrar."),
        new("mostrador", "Lo que se señala",
            "Oprime Ctrl+M (o toca «Ctrl+M Mostrador» en la barra de abajo) y toca una pieza de «Todos Santos». Oprime Ctrl+M otra vez, pasa al "
            + "«Mostrador» con Tab (o tocándolo arriba del panel) y toca una pieza de la repisa de arriba. Luego escribe 3 en el cuadro, oprime "
            + "Ctrl+M y toca otra.",
            "El panel sale a la derecha, sin tapar el ticket: primero la temporada que corre («Todos Santos», con su mesa de la entrada) y luego "
            + "el «Mostrador», con la repisa de arriba y la de abajo; cada producto en su lugar, con su foto o, si no tiene, sus iniciales, su "
            + "nombre y su precio. Al tocar uno entra al ticket, el panel se cierra y queda el total, listo para F12. Con 3 antes entran tres. "
            + "No espera nada: sale de la copia de la caja."),
        new("mostrador-teclado", "Lo que se señala",
            "Con el cuadro vacío oprime Ctrl+M, muévete con las flechas y con Tab, y oprime Enter. Después escribe 2, oprime Ctrl+M, elige otra "
            + "pieza con las flechas y Enter. Por último abre Ctrl+M y escanea (o escribe y Enter) uno de los códigos para vender de "
            + "«Léeme.txt».",
            "El cursor no sale del cuadro: las flechas mueven la pieza marcada y Tab pasa al siguiente exhibidor o temporada (Shift+Tab, al "
            + "anterior). Enter agrega la marcada; con 2 antes, dos. Lo escaneado con el panel abierto entra al ticket como siempre y el panel "
            + "se cierra: un código nunca se toma como la cantidad, aunque sea corto (la cantidad es un número de hasta cuatro cifras; «45*» "
            + "siempre es cantidad)."),
        new("mostrador-verificador", "Lo que se señala",
            "Oprime F9 y luego Ctrl+M (o toca «Mostrador y temporada» en el verificador). Toca una pieza y después «Agregarlo al ticket».",
            "El cuadro de captura se pone en ámbar y el panel dice «Verificador: tocar uno solo muestra su precio.». Al tocar no entra nada al "
            + "ticket: el verificador enseña su foto (o sus iniciales) en grande, su nombre, su precio y su existencia. «Agregarlo al ticket» lo "
            + "agrega y regresa a vender."),
        new("temporada", "Lo que se señala",
            "Mira arriba de la venta, a la derecha del estado de la caja, y toca el chip «Todos Santos». Cierra el panel con Esc, ve a Clientes "
            + "y regresa a la venta con F1.",
            "Mientras corre una temporada (Todos Santos va del 16 de septiembre al 5 de noviembre) está su chip, con su nombre y Ctrl+M; al "
            + "tocarlo se abre el mostrador en ella. En Productos, Inventario y Clientes no sale. Al terminar sus fechas se quita solo."),

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
        new("ventas-del-dia", "Devolver y autorizar",
            "Escribe /ventas y Enter. Con el cursor en el folio, oprime ← y →. Escribe el folio del ticket que cancelaste y Enter; en esa venta "
            + "oprime «Reimprimir», escribe un motivo y Enter. Vuelve a abrir «Ventas del día» y en otra venta oprime «Devolver o cancelar».",
            "Abren las de hoy de esta caja, las más nuevas primero: folio, hora, quién cobró, caja, total y cómo se pagó; la cancelada dice "
            + "«Cancelada» y la devuelta, «Devuelta en parte». Como cajero no cambias de día ni de caja (← y → no hacen nada). El folio se "
            + "encuentra aunque sea de otro día. Reimprimir pide el motivo y sale el ticket; «Devolver o cancelar» abre la devolución con el "
            + "folio ya escrito."),
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
            + "vender»; abajo, las mismas páginas que desde la venta (Inventario con sus tarjetas, Productos y Clientes con su lista y su "
            + "detalle) y su barra de teclas, sin «Volver a la venta». F3 y F4 cambian de página. Al volver como cajero sigues en la venta, "
            + "con tu turno; dice «Volvieron los … tickets que estaban en espera» y lo que tenías en el ticket está en una de sus fichas, con "
            + "sus productos: cambiar de usuario no lo pierde."),
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
            + "«Hacer ese corte», escribe un total y Enter; luego «Ver el corte del día» y «Cerrar el día». Con el cursor en el corte del día "
            + "oprime ← y luego →, y toca «Imprimir».",
            "Con el turno del dueño, la venta tiene todas las pestañas, y en «Corte» sale «Turno | Del día · todas las cajas | Historial» (al "
            + "encargado, «Turno | Historial»; al cajero, nada). Si el cajero dejó tickets sin cobrar, vuelven a la espera («Volvieron los … "
            + "tickets que estaban en espera»): son de la caja, no del turno; tu corte dice que se quedan para el siguiente turno, y vuelven en "
            + "el del cajero. Arriba: lo vendido hoy, el efectivo (con lo esperado en las cajas), tarjeta y transferencia, y las diferencias de "
            + "corte. La tabla tiene el turno del cajero (lo esperado, lo contado y su diferencia) y el tuyo como «Turno abierto»; abajo, las "
            + "formas de pago, las salidas por motivo, las devoluciones, las cancelaciones, lo fiado y los abonos, y las impresiones cobradas. "
            + "Mientras tu turno siga abierto, «Cerrar el día» espera: «Falta cerrar el turno de Dueño de prueba (Caja 1).». Ya cortado, se "
            + "cierra: «Día cerrado a las … por Dueño de prueba.», y el resumen no se manda otra vez: ya salió con el último corte. En «Avisos "
            + "al dueño» llega un «Resumen del día» con cada corte que deja la caja sin turno abierto; al cerrar el día, ninguno más. Abajo "
            + "salen las ventas por departamento (las ve quien ve el corte del día) y, solo para quien ve la utilidad, la utilidad bruta arriba "
            + "y la de cada departamento. ← lleva al corte de ayer, que solo se ve; «Imprimir» lo saca en la impresora de tickets (sin "
            + "impresora, abajo en «Así sale impreso»). A la hora de cierre de Configuración › Tienda, en la venta del dueño sale «Falta cerrar "
            + "el día» hasta que se cierra."),
        new("historial-cortes", "El corte",
            "En «Corte» › «Turno», oprime «Listo: abrir el siguiente turno» y abre otra vez tu turno con $500. Escribe /historial y Enter. Con ↑ "
            + "↓ elige el turno del cajero y oprime «Reimprimir el corte». Oprime ← y →. Luego /ventas, ← y «Todas las cajas». Para seguir con "
            + "lo de abajo, haz el corte de tu turno («Corte», cualquier total en «Efectivo contado» y Enter) y oprime Ctrl+U para abrir otro "
            + "turno del cajero (C, 1111 y $500).",
            "Se abre el corte en «Historial»: los turnos de hoy con lo esperado, lo contado, la diferencia, quién hizo el corte y a qué hora; el "
            + "tuyo, «Turno abierto». El corte sale igual que al cerrar el turno («CORTE DEL TURNO (Z)»; sin impresora, ahí mismo). En «Ventas "
            + "del día» el dueño sí cambia de día y de caja."),

        // ── Las páginas de atrás (entrega 8): Productos, Inventario y Clientes, con otro turno del cajero ──
        new("paginas", "Productos, Inventario y Clientes",
            "Como cajero, con el turno nuevo, deja un ticket en espera (F6) y agrega dos cosas a otro. Oprime F12 y escribe 100, sin Enter "
            + "(sin cobrar). Oprime F4, toca «Clientes» arriba y luego oprime F1.",
            "Inventario y Clientes se abren en lugar de la venta, con su pestaña marcada, ya no encima del ticket; la barra de arriba (los "
            + "chips y tu nombre) se queda y abajo dice «F1 Volver a la venta». F1 (o la pestaña «Venta») regresa a la venta como estaba: el "
            + "ticket con sus productos, el que está en espera y los 100 en «Pagó con». En una página, Esc sin nada abierto también "
            + "regresa a la venta."),
        new("productos", "Productos (F3)",
            "Como encargado (Ctrl+U, E y 2222, y «Productos e inventario, sin cortar»), oprime F3. Sin escribir nada, baja por la lista con ↓ "
            + "(con el cursor en la búsqueda). Escribe parte del nombre de un producto y Enter; toca «Poca existencia» y luego «Todos». Con un "
            + "producto elegido, oprime «Editar»: cámbiale el precio, «Guardar» y Esc. Da de alta uno nuevo con «Nuevo producto», con su "
            + "presentación «Caja 12» y un código interno (botón «Código interno»). A un producto con 5 de existencia ponle «Mínimo de "
            + "inventario» 10 en su ficha y toca «Poca existencia». Con «Departamentos» (abajo de la lista) da de alta uno, cámbiale el nombre y "
            + "desactiva uno que tenga productos, pasándolos a otro. Vuelve como cajero (tu nombre → «Salir», C y 1111) y escribe ese código en "
            + "la venta.",
            "Productos ya no abre vacío: trae la lista, lo más vendido primero, y abajo dice cuántos son («50 de …», con «Ver 50 más»). A la "
            + "derecha, el elegido: su nombre, marca, departamento y código, con «Etiqueta» y «Editar»; su precio (y su mayoreo, si tiene), su "
            + "existencia, su costo promedio y sus últimos movimientos («Inventario inicial», «Venta A-…»). El margen no sale: una nota dice que "
            + "lo ve quien tiene «Ver costos, utilidad y margen». ↓ cambia el detalle; lo escrito busca por nombre, marca, departamento o "
            + "código. «Poca existencia» deja lo que está en su mínimo o debajo (sin mínimo, 3 o menos). La ficha se abre ahí mismo: el precio "
            + "nuevo se guarda y Esc regresa al producto, ya con su precio. Con su mínimo en 10, el que tiene 5 sale en «Poca existencia» (y en "
            + "Inventario, en «Bajos en inventario»). «Departamentos» dice cuántos productos tiene cada uno; el nuevo y su nombre nuevo salen en "
            + "el filtro y en la ficha, y al desactivar uno con productos pide a cuál pasan: ya no se ofrece. El producto nuevo se guarda y se "
            + "vende con su código."),
        new("precios-varios", "Productos (F3)",
            "Como encargado (sin cortar), en Productos (F3) escribe «cuaderno» en la búsqueda y oprime «Cambiar precios» (abajo de la lista). "
            + "Escribe 10 en el porcentaje y Enter; revisa lo que sale y oprime Esc. Toca «Los marcados», toca dos productos de la lista de la "
            + "izquierda, elige «Poner un precio», escribe 25, Enter y luego Ctrl+Enter. Después, como dueño (Ctrl+U, D y 3333, y «Productos e "
            + "inventario, sin cortar»), en «Cambiar precios» toca «Una marca», elige una, escribe 5, Enter y oprime «Aplicar a …». Cobra en la "
            + "venta uno de los que cambiaste.",
            "Se abre a la derecha con lo que buscabas («Los que coinciden» con «cuaderno»), y cada renglón de la lista trae una casilla. Enter "
            + "enseña «Cómo quedan» sin cambiar nada: cuántos cambian, cada producto con su precio de ahora y el nuevo (a los 50 centavos más "
            + "cercanos) y lo que cambia de su mayoreo; Esc la cierra. Un toque marca o desmarca, y «Los marcados» dice cuántos. Ctrl+Enter los "
            + "cambia: «Listo: cambió el precio de 2 productos…», y la lista ya trae los precios nuevos. El encargado no ve el margen; el dueño "
            + "ve el de ahora y el que queda, con «debajo del mínimo» en rojo si queda abajo del mínimo de Configuración › Inventario. La venta "
            + "cobra el precio nuevo, y en la bitácora queda un solo renglón con cuántos, cuáles y la regla. Si el mayoreo de alguno no cabe más "
            + "barato que el menudeo, la vista lo marca «ajustado» o dice por qué ese producto no cambia."),
        new("foto-pc", "Productos (F3)",
            "Como encargado (sin cortar), en Productos (F3) abre la «Calculadora Casio Científica». En «Foto» toca «Elegir una foto…» y elige "
            + "una imagen (o copia una de una página, haz clic en el cuadro de la foto y Ctrl+V); revisa cómo se ve y «Guardar la foto». Con F1: "
            + "Ctrl+M › «Mostrador»; escribe «calculadora»; F9, Ctrl+M y toca la calculadora. Al final, «Quitar la foto».",
            "Se ve antes de guardarla; dice «Guardada: así llega a las cajas (240×…, … KB)». En Ctrl+M la pieza enseña la foto en lugar de sus "
            + "iniciales, en la búsqueda sale chica a la izquierda y en el verificador grande; los demás siguen con iniciales. Al quitarla "
            + "vuelven sus iniciales."),
        new("foto-celular", "Productos (F3)",
            "Con un celular en el mismo wifi: en la ficha del «Compás Maped De precisión», «Con el celular…» y escanea el QR. Entra como "
            + "«encargado» / 2222, toca «Fotos», escribe «compás» y elígelo; «Tomar la foto» con el celular en vertical, revisa y «Guardar». En "
            + "la PC, Ctrl+M › «Mostrador».",
            "Salen «Contar» y «Fotos»; la cámara se abre desde la página; la foto se ve derecha; dice «Guardada: ya llega a las cajas». En unos "
            + "segundos la pieza del compás enseña su foto en la PC. Como cajero (1111) no sale «Fotos»."),
        new("empezar-temporada", "Productos (F3)",
            "Como encargado (sin cortar), en Productos (F3) toca «Temporadas» (abajo de la lista) y elige «Navidad». Revisa la lista. En el "
            + "cuadro «Precio» del primero escribe un precio nuevo y Enter; en el siguiente escribe 1 y oprime Ctrl+Enter. Corrige el que salió "
            + "en rojo (o «Dejar los de hoy» y vuelve a escribir el primero) y Ctrl+Enter.",
            "Abre en la temporada que corre (Todos Santos); arriba se cambia a las demás y dice sus fechas, cuántos productos tiene y cuántos "
            + "sin foto. «1 · Empezar: precios» trae la lista del año pasado en el orden de la mesa: el precio de entonces (en la base de "
            + "prueba, el que más se cobró el año pasado), el de hoy, «sin foto» donde falta, y el cuadro del precio; el encargado no ve costo, "
            + "margen ni sugerido. Enter pasa al cuadro de abajo. Un precio igual o abajo de su mayoreo sale en rojo («más barato que su "
            + "mayoreo») y no deja confirmar. Ctrl+Enter: «Listo: cambió 1 precio; las cajas ya lo tienen.», arriba «Encargado de prueba "
            + "confirmó los precios el …», y la venta ya cobra el precio nuevo. Tocar el nombre de uno abre su ficha (ahí se le pone la foto) y "
            + "Esc regresa a la temporada."),
        new("temporada-costos", "Productos (F3)",
            "Como dueño (Ctrl+U, D y 3333), en la misma temporada.",
            "Salen además el costo de hoy, el margen (en rojo si queda abajo del mínimo) y el sugerido; «Todos al sugerido» los pone en los "
            + "cuadros (nunca baja un precio)."),
        new("hoja-temporada", "Productos (F3)",
            "En «2 · Hoja», «Vista previa». Luego «3 · Tarjetas», «Vista previa».",
            "Abajo se ve la hoja como saldrá: por lugar de la mesa, de tres en tres, cada producto con su foto (o sus iniciales), su nombre, su "
            + "precio y su código de barras, con «Hoja 1 de …». Las tarjetas: 18 por hoja carta con su línea para recortar, el nombre, el precio "
            + "grande y el código. Ya van marcadas las que no tiene. (Sin impresora: «Imprimir» abre el diálogo de Windows; si se imprime, "
            + "quedan anotadas.)"),
        new("tarjetas-reimprimir", "Productos (F3)",
            "En «3 · Tarjetas» oprime «Imprimir … tarjetas» (en el diálogo, cancela). Abre el primero de la lista, «Editar», cámbiale el precio, "
            + "«Guardar» y Esc. Toca «Tarjetas de precio».",
            "El botón dice «Tarjetas de precio · 1 por reimprimir». Abre en «Por reimprimir»: el producto con el precio de su tarjeta, «cambió "
            + "el precio» y el de hoy. Al imprimirla sale de la lista; «Ya no tiene» también la quita."),
        new("mas-preguntado", "Productos (F3)",
            "En la venta (F1), F9 y escanea (o escribe y Enter) el código de un producto tres veces y el de otro una vez. Regresa a Productos › "
            + "«Tarjetas de precio» › «Lo más preguntado».",
            "Salen los dos, primero el de «3 veces», sin marcar; lo que ya tiene tarjeta no sale. Tocarlos y «Imprimir» les da su tarjeta."),
        new("terminar-temporada", "Productos (F3)",
            "En «Temporadas», «Todos Santos», «4 · Terminar». En «Contado» de uno escribe un número y Enter. En el de abajo escribe 1.2345 y sal "
            + "del cuadro; corrígelo a 1 y sal otra vez. Oprime «Terminar la temporada» y «Sí, terminarla».",
            "Cada producto con lo que había, lo que se vendió y lo que quedó, y «se acabó», «no se vendió» o qué parte se vendió. En la base de "
            + "prueba dice «desde que se empezó a usar este POS» (su inventario empieza el 30 de septiembre). Lo contado se guarda al dar Enter "
            + "o salir del cuadro, y abajo dice «guardado; el sistema decía N»; 1.2345 no se guarda y dice «Revisa lo contado: escribe solo "
            + "cantidades, como 12 o 2.5.». Antes de terminarla pregunta (Esc la deja); después: «Encargado de prueba la terminó y contó lo que "
            + "sobró el …»; la existencia queda en lo contado (menos lo que se haya vendido después de contarlo) y el resumen ya no cambia. En "
            + "el piloto con eleventa no sale «Contado» y dice por qué."),
        new("recibir", "Inventario (F4) · Compras",
            "Como cajero, oprime F4 (o «Inventario» arriba) y toca la tarjeta «Recibir mercancía»: elige el proveedor de «Léeme.txt» y cuenta "
            + "lo que dice ahí para la factura (puedes contar uno menos de algo; el nuevo, con su código). Termina el conteo.",
            "Inventario abre con una tarjeta por tarea, ya sin la fila de pestañas, y el cajero solo ve las suyas: Recibir mercancía, Conteo "
            + "del día (con cuántos faltan por contar), Mermas («tú: hasta $200.00»), Ajustes («lo autoriza Encargado de prueba o Dueño de "
            + "prueba»), Abrir un paquete y Proveedores («solo consulta»). «Recibir mercancía» abre ya en elegir el proveedor, con «← "
            + "Inventario» arriba. Mientras cuentas no ves la nota ni los costos. Lo que no está en el catálogo se cuenta aparte. Al "
            + "terminar, dice que falta que el encargado capture la nota."),
        new("factura", "Inventario (F4) · Compras",
            "Como encargado (sin cortar), en Inventario toca «Compras y notas» (dice cuántas notas faltan por capturar), abre esa compra y "
            + "carga «Factura de ejemplo.xml» (en «Ejemplos de la prueba», en el escritorio). Confirma el que pregunta «¿Es…?», corrige lo que "
            + "quieras del producto nuevo y dalo de alta. Cierra la compra.",
            "El encargado ve todas las tarjetas, también Compras y notas, Órdenes de compra, Sugerido de compra, Etiquetas, Hoja de códigos y "
            + "Conteo completo. Todo el pedido queda precargado y se puede corregir renglón por renglón (del nuevo: nombre, precio, piezas). "
            + "Los productos con código llegan solos; el nuevo se da de alta con lo que contaste. La comparación marca lo que faltó y al "
            + "cerrar entra al inventario."),
        new("nota-pdf", "Inventario (F4) · Compras",
            "Haz otra compra con lo que dice «Léeme.txt» para la nota (el cajero la recibe en «Recibir mercancía» y el encargado la captura "
            + "en «Compras y notas») y carga «Nota de ejemplo.pdf».",
            "Lee los renglones del PDF, avisa que repartió el IVA y el producto que ligaste antes ya llega ligado."),
        new("sugerido", "Inventario (F4) · Compras",
            "Como encargado (sin cortar), en Inventario (F4) toca «Sugerido de compra». Cambia la cantidad de un producto, quita otro y da "
            + "«Hacer orden de compra» en un proveedor. Con «← Inventario» ve a «Órdenes de compra», abre esa orden y oprime «Copiar el texto».",
            "Sale lo que hay que pedir por proveedor, lo urgente marcado. La tarjeta «Órdenes de compra» dice cuántas hay abiertas. La orden "
            + "tiene lo que dejaste, y su texto se pega en otro lado (WhatsApp, Bloc de notas) con cantidades y claves."),
        new("paquete", "Inventario (F4)",
            "En Inventario (F4) toca «Abrir un paquete»: abre un producto en otro (por ejemplo, un paquete en sus piezas).",
            "Sale uno y entran sus piezas; aparece en la lista de abajo con tu nombre."),
        new("conteo", "Inventario (F4)",
            "Como cajero, toca «Conteo del día» abajo a la derecha, en la barra de teclas. Cuenta dos o tres de esos productos (en la prueba, "
            + "inventa la cantidad).",
            "El chip abre Inventario › Conteo del día y dice cuántos llevas de cuántos. No ves cuánto dice el sistema. Como encargado (sin "
            + "cortar), en Inventario → «Conteo del día» ves la diferencia; si vale $200 o más, sale en «Avisos al dueño»."),
        new("conteo-completo", "Inventario (F4)",
            "Como encargado (sin cortar), en Inventario (F4) toca «Conteo completo» y abre un departamento chico. Con un celular en el mismo "
            + "wifi, escanea su código QR, entra con el usuario «cajero» y el PIN 1111 (en el celular todavía se escribe el usuario) y "
            + "cuenta tres productos, uno en dos partes (anaquel y bodega). Luego, en la PC, «Revisar y cerrar».",
            "Sin celular, la dirección que muestra se abre en el navegador de esta PC. En el celular no se ve lo que dice el sistema y las dos "
            + "partes se suman. En la revisión salen las diferencias; al cerrar, lo contado queda como existencia, lo que nadie contó queda en "
            + "0 y sale en «Avisos al dueño»."),
        new("etiquetas", "Inventario (F4)",
            "Como encargado (sin cortar), en Productos (F3) elige un producto y oprime «Etiqueta», y luego «Ir a Etiquetas». Agrega la "
            + "compra que cerraste (quita «Solo lo que no trae código de fábrica») e imprime la hoja de prueba desde «Vista previa» (o a "
            + "«Microsoft Print to PDF»). Con «← Inventario», en «Hoja de códigos» agrega tres gomas y da «Vista previa».",
            "«Etiqueta» dice que el producto va en las etiquetas por imprimir, e «Ir a Etiquetas» abre Inventario › Etiquetas con él en la "
            + "lista; la tarjeta «Etiquetas» dice cuántas hay por imprimir. Sale una etiqueta por pieza con el nombre y su código de barras, "
            + "en hojas carta; la hoja de prueba marca dónde cae cada una. La hoja de códigos trae el nombre y el código de cada producto, "
            + "por departamento, y al volver a entrar la lista sigue ahí."),
        new("mermas-ajustes", "Inventario (F4)",
            "Como cajero, en Inventario (F4) toca «Mermas»: registra una merma de 1 producto dañado y luego una de 30 piezas de algo caro. "
            + "Con «← Inventario», en «Ajustes» corrige la existencia de un producto a la que cuentes en el anaquel, con su motivo.",
            "La tarjeta de las mermas dice «tú: hasta $200.00» y la de los ajustes «lo autoriza Encargado de prueba o Dueño de prueba». La "
            + "merma chica entra sola; la grande (más de $200 de costo) y la corrección piden la autorización: se toca la ficha del encargado "
            + "y se escribe su PIN. Todo queda en la lista y en la existencia; si la corrección vale $200 o más, sale en «Avisos al dueño»."),
        new("reportes-inventario", "Inventario (F4)",
            "Como dueño, sin cortar (Ctrl+U, D y 3333, y «Productos e inventario, sin cortar»), en Inventario (F4) abre «Lo que vale el "
            + "inventario» y elige un departamento. Con «← Inventario» abre «Movimientos» (los de hoy), elige «Ventas» en el tipo y mira el "
            + "folio de una venta de la prueba. Luego abre «Bajos en inventario».",
            "«Lo que vale el inventario» dice el total a costo y lo de cada departamento, con sus productos a un lado. «Movimientos» trae los de "
            + "hoy, del más reciente al más viejo, con su folio, quién y lo que quedó; con «Ventas» quedan solo las ventas, cada una con el "
            + "folio de su ticket. En «Bajos en inventario» sale el producto al que le pusiste mínimo 10, con cuánto pedir."),
        new("cliente-alta", "Clientes",
            "Como encargado (sin cortar → Clientes), da de alta un cliente con «Nuevo cliente», con teléfono y 5 % de descuento. Toca «Por "
            + "revisar»: elige a «Maestra Carmen», «Editar» y «Guardar». Elige al «Arq. Ramírez»: en «Del crédito sin conexión, por "
            + "revisar» lee lo que pasó, anota algo y márcalo «Revisado».",
            "Clientes tiene la lista a la izquierda (con lo que debe cada quien) y el elegido a la derecha, y se edita ahí mismo. El cliente "
            + "nuevo queda en la lista y ya se puede elegir con Ctrl+L. En «Avisos al dueño» llega que el encargado le dio privilegios. Al "
            + "guardar a Maestra Carmen se le quita «por revisar». Lo del Arq. Ramírez dice qué pasó, cuándo y por cuánto; al marcarlo sale "
            + "de su detalle."),
        new("credito-saldo-a-favor", "Clientes",
            "Como cajero, abre «Clientes» (arriba) y elige a «Papelería La Esquina» (dice «$150.00 a favor»). Oprime «Devolver saldo a "
            + "favor». Deja vacío «Cuánto» (todo) y oprime «Devolver».",
            "El cajero ve los clientes y sus cuentas, pero no los cambia: no tiene «Editar», «Límite y plazo» ni «Nuevo cliente», y su límite "
            + "dice quién lo da. «Devolver saldo a favor» lleva candado con quien lo autoriza: pide la autorización con la ficha del encargado "
            + "(2222). Sale un comprobante para que el cliente firme que recibió el efectivo y arriba dice cuánto entregar; su cuenta queda "
            + "al corriente y en «Avisos al dueño» llega que se devolvió."),
        new("credito-cuenta", "Clientes · El dueño",
            "Como dueño, sin cortar (Ctrl+U, D y 3333, y «Productos e inventario, sin cortar» → Clientes): toca «Solo los que deben» y elige "
            + "al «Arq. Ramírez». Con «Límite y plazo» ponle plazo de 15 días («Guardar límite y plazo»); imprime su «Estado de cuenta» y "
            + "oprime «Copiar para WhatsApp». Con «Todos», elige a «Papelería La Esquina» y dale $500 de crédito con «Límite y plazo».",
            "La lista dice cuánto debe cada quien (en rojo, si ya venció). En su detalle: lo que debe y cuándo venció lo más antiguo, su "
            + "límite («puedes cambiarlo»), cuánto compra al mes y si paga a tiempo; en «Movimientos de la cuenta», el saldo inicial, lo "
            + "fiado, los abonos y la devolución, cada uno con el saldo que dejó. El estado de cuenta y el texto copiado (pégalo en el Bloc "
            + "de notas) dicen lo mismo, con lo que falta pagar de cada compra y su vencimiento. Solo el dueño cambia el crédito."),

        // ── La Configuración nueva (entrega 7), al final ──
        new("margen", $"{ConfiguracionNueva} · El dueño",
            "Como dueño (sin cortar), abre «Configuración» (arriba) → Inventario y sube el «Margen mínimo» general a 50 (se guarda al salir "
            + "del campo). Luego cierra otra compra (en Inventario, F4, la tarjeta «Compras y notas»).",
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
        new("acomodar", $"{ConfiguracionNueva} · El dueño",
            "Como dueño, vendiendo, oprime Ctrl+M, toca «Acomodar» y elige «Mostrador». En el cuadro para agregar escribe el nombre de un "
            + "producto, elígelo de la lista (↑ ↓) y Enter, o escanea su código. Muévelo con Shift+← y Shift+→ (Shift+↑ ↓ lo cambian de repisa), "
            + "quita otro con Supr y toca «Listo». Luego, en Configuración → Mostrador y temporadas (o escribe «temporada» en su buscador), "
            + "ponle a «Primavera» «del 21 de marzo al 30 de abril» y agrégale al Mostrador un lugar «Pared».",
            "«Acomodar» solo le sale a quien cambia «Productos, precios y etiquetas» (el dueño y el encargado; al cajero, no). Lo agregado entra "
            + "después de la pieza marcada; moverlo y quitarlo se guarda al momento, sin botón, y así queda en todas las cajas. Al acomodar "
            + "también salen las temporadas que no corren, para prepararlas. En la Configuración cada cambio se guarda solo («Guardado a las "
            + "…»), y una fecha que no existe (31/4) lo dice. «Pared» sale en Ctrl+M después de las repisas."),
        new("configuracion", $"{ConfiguracionNueva} · El dueño",
            "Como dueño, en Configuración escribe «fondo» en el buscador y pon el fondo sugerido en $500; en Ticket cambia el pie y mira «Así "
            + "sale». Mira «Usuarios y permisos» y la Bitácora. Abre «Avisos al dueño» en la franja de prueba.",
            "Cada cambio se guarda solo («Guardado a las …»), sin botón ni PIN, y el siguiente «Abrir turno» propone $500. En «Usuarios y "
            + "permisos», Dueño de prueba dice «Todos los permisos · primera cuenta», Encargado de prueba «Corte de turno, productos y "
            + "clientes» y Cajero de prueba «Permisos por defecto»; cada permiso es Puede, Con autorización o No (en Administración, solo "
            + "Puede o No). La bitácora tiene lo de hoy: cancelaciones, precios, autorizaciones, compras, los cortes del turno y el cierre del "
            + "día. En «Avisos al dueño» están los avisos que se habrían mandado: cancelaciones, cortes con diferencia, el resumen del día…"),
        new("al-cobrar", $"{ConfiguracionNueva} · El dueño",
            "Como dueño, en Configuración → Ticket, en «Al cobrar» toca «Ofrecerlo» y cierra la Configuración (Esc). Cobra una venta y oprime "
            + "Ctrl+I. Cobra otra y, sin oprimir nada, escanea lo del siguiente cliente. Vuelve a Configuración → Ticket, toca «No imprimirlo» y "
            + "cobra otra venta. Al terminar, deja «Imprimir el ticket».",
            "La opción queda marcada al tocarla y arriba dice «Guardado a las …», sin botón ni PIN; el ajuste dice «Toda la tienda». Con "
            + "«Ofrecerlo» se cobra igual de rápido, sin ninguna pregunta, y el panel dice «Ticket A-… · Imprimir ticket Ctrl+I»; Ctrl+I lo "
            + "enseña en pantalla (la prueba no tiene impresora). En la otra venta, con el primer escaneo del siguiente cliente ya no se ofrece. "
            + "Con «No imprimirlo» el panel dice «Ticket A-… sin imprimir» y no ofrece nada; si lo piden, «Reimprimir», con su motivo. Lo fiado "
            + "sale siempre."),
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
