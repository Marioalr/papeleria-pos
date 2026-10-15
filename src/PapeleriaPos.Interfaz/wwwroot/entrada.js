// Las teclas de la entrada de la caja (docs/propuesta-interfaz.md, «Usuarios y permisos»): la letra de una ficha la
// elige, las cifras van al PIN, Enter entra, Borrar quita la última cifra y Esc regresa a las fichas. Se oyen en toda la
// página, no en un campo: no hay dónde escribir el usuario ni el PIN, y no se pierden si el cursor quedó en otro lado
// (un botón de la franja de la prueba). Aparte de caja.js, que es de la pantalla de venta: las dos nunca están a la vez.
//
// Abrir el turno y la caja con el turno de otro solo atrapan Ctrl+U («Cambiar de usuario», como en la venta; en el
// navegador sería ver el código de la página): lo demás es de sus campos y botones, también los del corte.
let actual = null;

// modo: 'fichas' (¿Quién atiende? y el PIN) o 'cambiar' (solo Ctrl+U). ficha: la de la pantalla que las pone (la da .NET),
// para quitar solo las suyas: si la pantalla que se va las quita después de que la nueva puso las suyas, las de la nueva
// se quedan.
export function registrar(pantalla, modo, ficha) {
    if (!actual) document.addEventListener('keydown', alPresionar, true);
    actual = { pantalla, modo, ficha };
    soltarFoco();
}

export function quitar(ficha) {
    if (!actual || actual.ficha !== ficha) return;
    document.removeEventListener('keydown', alPresionar, true);
    actual = null;
}

// Un campo, una lista o algo editable (la nota de la guía de la prueba): sus teclas son suyas.
function escribe(elemento) {
    return elemento instanceof HTMLElement
        && (elemento.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(elemento.tagName));
}

// La guía de la prueba, los avisos al dueño o empezar de cero, abiertos encima: sus teclas son suyas (Esc la cierra), no
// de la entrada que queda atrás, donde se elegiría una ficha o se teclearía el PIN sin verlo.
function hayVentana() {
    return document.querySelector('.fondo-ventana') !== null;
}

// Al llegar a una pantalla de la entrada, el cursor no se queda en un botón de afuera (el de «Simular sin conexión» en la
// franja de la prueba, que no lo suelta al oprimirlo): un Enter de costumbre lo volvería a oprimir. Un campo (la nota de
// la tarea) sí se queda con él.
function soltarFoco() {
    const foco = document.activeElement;
    if (foco instanceof HTMLElement && foco !== document.body && !escribe(foco) && !foco.closest('.entrada, .fondo-ventana')) foco.blur();
}

function avisar(tecla) {
    // Si la pantalla ya se fue (se cambió muy rápido), la tecla ya no es de nadie.
    actual.pantalla.invokeMethodAsync('Tecla', tecla).catch(() => { });
}

function alPresionar(e) {
    if (!actual) return;
    const tecla = e.key;
    if ((e.ctrlKey || e.metaKey) && !e.altKey && (tecla === 'u' || tecla === 'U')) {
        e.preventDefault();
        e.stopPropagation();
        if (!hayVentana()) avisar('Ctrl+U');
        return;
    }
    if (actual.modo !== 'fichas' || e.ctrlKey || e.altKey || e.metaKey || escribe(e.target) || hayVentana()) return;
    const valida = /^[0-9]$/.test(tecla) || /^[a-zA-Z]$/.test(tecla) || tecla === 'Enter' || tecla === 'Backspace' || tecla === 'Escape';
    if (!valida) return;
    // Sin ficha elegida, Enter es del botón con el cursor: una ficha a la que se llegó con Tab, «Reintentar» o uno de la
    // franja. Con el PIN a la vista, entra con lo tecleado: no vuelve a oprimir la ficha, la cifra ni el botón de afuera.
    if (tecla === 'Enter' && !document.querySelector('.entrada .pin')) return;
    // Las letras y las cifras valen aunque el cursor esté en un botón: ahí no escriben nada.
    e.preventDefault();
    e.stopPropagation();
    avisar(tecla);
}
