// Teclas de la caja, iguales a las de eleventa. Se atrapan antes que el navegador (F5 no recarga, Ctrl+P no imprime,
// Ctrl+E no busca).
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
    const funcion = /^F([1-9]|1[0-2])$/.test(tecla);

    let atrapar;
    if (hayVentana) {
        atrapar = tecla === 'Escape' || funcion || ctrlP || ctrlE;
    } else {
        atrapar = funcion || ctrlP || ctrlE || tecla === 'Escape' || tecla === 'Insert'
            || (tecla === 'Delete' && vacio)
            || ((tecla === 'ArrowUp' || tecla === 'ArrowDown') && (campo === null || enCodigo))
            || ((tecla === '+' || tecla === '-') && vacio && (campo === null || enCodigo));
    }
    if (!atrapar) return;

    e.preventDefault();
    e.stopPropagation();
    pantalla.invokeMethodAsync('Tecla', ctrlP ? 'Ctrl+P' : ctrlE ? 'Ctrl+E' : tecla);
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
