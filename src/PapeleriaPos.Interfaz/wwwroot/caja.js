// Teclas de la caja, iguales a las de eleventa. Se atrapan antes que el navegador (F5 no recarga, Ctrl+P no imprime,
// Ctrl+E no busca, Ctrl+D no guarda un marcador).
let pantalla = null;
let hayVentana = false;

export function registrar(referencia) {
    pantalla = referencia;
    document.addEventListener('keydown', alPresionar, true);
}

export function quitar() {
    document.removeEventListener('keydown', alPresionar, true);
    pantalla = null;
}

// Con una ventana abierta (cobrar, buscar…) solo se atrapa Escape; lo demás lo maneja la ventana.
export function ventanaAbierta(abierta) {
    hayVentana = abierta;
}

function alPresionar(e) {
    if (!pantalla) return;
    const tecla = e.key;
    const campo = e.target instanceof HTMLInputElement ? e.target : null;
    const enCodigo = campo !== null && campo.dataset.codigo !== undefined;
    const vacio = campo === null || campo.value === '';
    const ctrlP = e.ctrlKey && (tecla === 'p' || tecla === 'P');
    const ctrlE = e.ctrlKey && (tecla === 'e' || tecla === 'E');
    const ctrlD = e.ctrlKey && (tecla === 'd' || tecla === 'D');
    const ctrlL = e.ctrlKey && (tecla === 'l' || tecla === 'L');
    const ctrlB = e.ctrlKey && (tecla === 'b' || tecla === 'B');
    const funcion = /^F([1-9]|1[0-2])$/.test(tecla);

    // Ctrl+Q: cotizar a una tecla. El selector de archivos solo se abre desde la tecla misma, no después.
    if (e.ctrlKey && (tecla === 'q' || tecla === 'Q')) {
        e.preventDefault();
        e.stopPropagation();
        if (!hayVentana) document.getElementById('pdf-cotizar')?.click();
        return;
    }

    let atrapar;
    if (hayVentana) {
        atrapar = tecla === 'Escape' || funcion || ctrlP || ctrlE || ctrlD || ctrlL || ctrlB;
    } else {
        atrapar = funcion || ctrlP || ctrlE || ctrlD || ctrlL || ctrlB || tecla === 'Escape' || tecla === 'Insert'
            || (tecla === 'Delete' && vacio)
            || ((tecla === 'ArrowUp' || tecla === 'ArrowDown') && (campo === null || enCodigo))
            || ((tecla === '+' || tecla === '-') && vacio && (campo === null || enCodigo));
    }
    if (!atrapar) return;

    e.preventDefault();
    e.stopPropagation();
    pantalla.invokeMethodAsync('Tecla', ctrlP ? 'Ctrl+P' : ctrlE ? 'Ctrl+E' : ctrlD ? 'Ctrl+D' : ctrlL ? 'Ctrl+L' : ctrlB ? 'Ctrl+B' : tecla);
}

export function enfocar(selector) {
    const elemento = document.querySelector(selector);
    if (elemento) {
        elemento.focus();
        if (elemento.select) elemento.select();
    }
}

export function mostrar(selector) {
    document.querySelector(selector)?.scrollIntoView({ block: 'nearest' });
}

// El resumen de una cotización, para pegarlo en WhatsApp.
export async function copiar(texto) {
    await navigator.clipboard.writeText(texto);
}

// Lo que recuerda el celular entre recargas (la sesión del conteo). Si el navegador no deja guardar, no pasa nada.
export function guardar(clave, valor) {
    try {
        if (valor === null) localStorage.removeItem(clave);
        else localStorage.setItem(clave, valor);
    } catch {
    }
}

export function leer(clave) {
    try {
        return localStorage.getItem(clave);
    } catch {
        return null;
    }
}

// La hoja de etiquetas va en un marco: así se ve como saldrá y se imprime sola, sin la caja alrededor.
export function mostrarEnMarco(selector, html) {
    const marco = document.querySelector(selector);
    if (!marco) return Promise.resolve();
    return new Promise(listo => {
        marco.addEventListener('load', () => {
            // El marco crece a lo que mide la página (ya acomodada): se recorre con la barra de la vista previa.
            requestAnimationFrame(() => {
                const documento = marco.contentDocument;
                if (documento) marco.style.height = (documento.documentElement.scrollHeight + 4) + 'px';
                listo();
            });
        }, { once: true });
        marco.srcdoc = html;
    });
}

export function imprimirMarco(selector) {
    const marco = document.querySelector(selector);
    if (!marco || !marco.contentWindow) return;
    marco.contentWindow.focus();
    marco.contentWindow.print();
}
