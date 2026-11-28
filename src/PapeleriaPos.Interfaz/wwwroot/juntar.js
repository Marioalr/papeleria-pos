// «Juntar imágenes para Word» (docs/propuestas/juntar-imagenes-interfaz.md): las teclas de la ventanita. Las que son suyas se atrapan
// aquí, antes que el navegador (Espacio no baja la página, Alt+A no abre nada, Enter en un campo no hace otra cosa) y van a
// JuntarImagenes.TeclaAsync con dónde estaba el cursor. Enter en un botón y en una lista hace lo de siempre.
const enLasMiniaturas = ['ArrowRight', 'ArrowLeft', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'Delete', 'Backspace', ' ', 'Enter'];

export function preparar(ventanita) {
    document.addEventListener('keydown', e => {
        const activo = document.activeElement;
        const donde = activo?.id === 'jn-miniaturas' ? 'miniaturas'
            : activo?.tagName === 'BUTTON' ? 'boton'
            : activo?.tagName === 'SELECT' ? 'lista' : '';
        const k = e.key;
        const letra = k.length === 1 ? k.toLowerCase() : k;
        const grande = document.getElementById('jn-grande') !== null;
        const preguntando = document.getElementById('jn-si-borrar') !== null;
        let nuestra;
        if (grande)
            nuestra = k === 'Escape' || k === ' ';
        else if (preguntando)
            nuestra = k === 'Enter' || k === 'Escape';
        else
            nuestra = (e.altKey && !e.ctrlKey && ['a', 'n', 'h'].includes(letra))
                || (e.ctrlKey && !e.altKey && letra === 'o')
                || k === 'Escape'
                || (donde === 'miniaturas' && (enLasMiniaturas.includes(k) || (letra === 'r' && !e.ctrlKey && !e.altKey) || (letra === 'z' && e.ctrlKey)))
                || (k === 'Enter' && donde === '');
        if (!nuestra) {
            // Con una imagen en grande, nada más se mueve detrás.
            if (grande && k !== 'Tab')
                e.preventDefault();
            return;
        }
        e.preventDefault();
        ventanita.invokeMethodAsync('TeclaAsync', k, e.altKey, e.ctrlKey, donde)
            .then(() => document.querySelector('.jn-mi.jn-foco')?.scrollIntoView({ block: 'nearest' }))
            .catch(() => { });
    }, true);
}

export function enfocar(selector) {
    const elemento = document.querySelector(selector);
    if (elemento) {
        elemento.focus();
        if (elemento.select) elemento.select();
    }
}
