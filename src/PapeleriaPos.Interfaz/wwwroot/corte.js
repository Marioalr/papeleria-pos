// «Efectivo contado» del corte del turno (entrega 6): Enter cierra el turno, y un corte no se deshace. El cursor llega a
// ese campo en cuanto se abre el corte; si ahí escribe el lector, su ráfaga no se toma como dinero (un código de 13 cifras
// serían billones de pesos de sobrante, con su aviso por Telegram): el campo vuelve a lo que tenía y su Enter no cierra
// el turno. El lector se reconoce como en el cobro (caja.js, RAFAGA_MS y RAFAGA_MINIMO): más rápido que una persona y con
// Enter al final. Sin tope ni otra confirmación: lo que escribe una persona se cierra con su Enter, como siempre.
// Aparte de caja.js porque el corte también va en la caja con el turno de otro cajero, donde caja.js no está.
const RAFAGA_MS = 50;
const RAFAGA_MINIMO = 4;

export function proteger(campo) {
    if (!(campo instanceof HTMLInputElement) || campo.dataset.protegido !== undefined) return;
    campo.dataset.protegido = '';
    // La ráfaga en curso: lo que tenía el campo antes de empezar, lo que va tecleando y la hora de la última tecla.
    let rafaga = null;
    campo.addEventListener('keydown', e => {
        // La hora del teclado (timeStamp), no la de atenderla: si la página estuvo ocupada, una persona no se vuelve ráfaga.
        const hora = e.timeStamp;
        if (e.key === 'Enter') {
            const r = rafaga;
            rafaga = null;
            if (!r || r.texto.length < RAFAGA_MINIMO || hora - r.ultima > RAFAGA_MS) return;
            // Antes de que llegue a .NET: el campo vuelve a lo que tenía (y .NET se entera por su @bind) y no se cierra.
            e.preventDefault();
            e.stopImmediatePropagation();
            campo.value = r.antes;
            campo.dispatchEvent(new Event('input', { bubbles: true }));
            return;
        }
        if (e.key.length === 1 && !e.ctrlKey && !e.altKey && !e.metaKey) {
            if (rafaga && hora - rafaga.ultima <= RAFAGA_MS) {
                rafaga.texto += e.key;
                rafaga.ultima = hora;
            } else {
                rafaga = { antes: campo.value, texto: e.key, ultima: hora };
            }
        } else if (e.key !== 'Shift') {
            // Las mayúsculas del lector llegan con Shift; cualquier otra tecla (borrar, flechas) es de una persona.
            rafaga = null;
        }
    });
}
