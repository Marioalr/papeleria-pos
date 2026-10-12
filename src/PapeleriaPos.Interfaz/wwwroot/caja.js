// Teclas de la caja, iguales a las de eleventa. Se atrapan antes que el navegador (F5 no recarga, Ctrl+P no imprime,
// Ctrl+E no busca, Ctrl+D no guarda un marcador).
let pantalla = null;
let hayVentana = false;
let escribiendo = null;

export function registrar(referencia) {
    pantalla = referencia;
    document.addEventListener('keydown', alPresionar, true);
    document.addEventListener('input', alEscribir, true);
    document.addEventListener('mouseover', alPasarFlecha, true);
    document.addEventListener('click', alTocarFlecha, true);
    document.addEventListener('scroll', alRodarLista, true);
}

export function quitar() {
    document.removeEventListener('keydown', alPresionar, true);
    document.removeEventListener('input', alEscribir, true);
    document.removeEventListener('mouseover', alPasarFlecha, true);
    document.removeEventListener('click', alTocarFlecha, true);
    document.removeEventListener('scroll', alRodarLista, true);
    clearTimeout(escribiendo);
    clearInterval(rodando);
    rodando = null;
    pantalla = null;
}

// «Más» de los tickets en espera: con muchos, sus flechas deslizan la lista al pasar el mouse (o un renglón con un
// clic) y se apagan al llegar a la orilla. La rueda del mouse también la recorre.
let rodando = null;

function alPasarFlecha(e) {
    const flecha = e.target.closest?.('.vn-menu-flecha');
    if (!flecha || rodando) return;
    const paso = Number(flecha.dataset.rodar) * 3;
    rodando = setInterval(() => {
        const lista = document.getElementById('lista-espera');
        if (!lista || !flecha.isConnected || !flecha.matches(':hover')) {
            clearInterval(rodando);
            rodando = null;
            return;
        }
        lista.scrollTop += paso;
    }, 16);
}

function alTocarFlecha(e) {
    const flecha = e.target.closest?.('.vn-menu-flecha');
    if (!flecha) return;
    document.getElementById('lista-espera')?.scrollBy({ top: Number(flecha.dataset.rodar) * 42, behavior: 'smooth' });
}

function alRodarLista(e) {
    const lista = e.target;
    if (!(lista instanceof HTMLElement) || lista.id !== 'lista-espera') return;
    const [arriba, abajo] = lista.parentElement.querySelectorAll('.vn-menu-flecha');
    arriba?.classList.toggle('apagada', lista.scrollTop <= 1);
    abajo?.classList.toggle('apagada', lista.scrollTop + lista.clientHeight >= lista.scrollHeight - 1);
}

// El cuadro de captura de la pantalla nueva no avisa a .NET en cada tecla: al Enter, el texto se lee y el cuadro se
// vacía aquí mismo, y el código va en una sola llamada. Así un escaneo nunca se pega al siguiente aunque lleguen sin
// pausa, y el lector no repinta la pantalla con cada carácter. Lo que se escribe a mano se avisa al dejar de teclear
// (para un rápido con cantidad, F11, y la búsqueda), no con cada tecla.
function esCaptura(elemento) {
    return elemento instanceof HTMLInputElement && elemento.dataset.captura !== undefined;
}

function alEscribir(e) {
    if (!pantalla || !esCaptura(e.target)) return;
    clearTimeout(escribiendo);
    const campo = e.target;
    escribiendo = setTimeout(() => pantalla?.invokeMethodAsync('Escribiendo', campo.value), 150);
}

export function limpiarCaptura() {
    const campo = document.querySelector('[data-captura]');
    if (campo) campo.value = '';
}

// Con una ventana abierta (cobrar, buscar…) solo se atrapa Escape; lo demás lo maneja la ventana.
export function ventanaAbierta(abierta) {
    hayVentana = abierta;
}

function alPresionar(e) {
    if (!pantalla) return;
    const tecla = e.key;
    const campo = e.target instanceof HTMLInputElement ? e.target : null;

    if (tecla === 'Enter' && !hayVentana && esCaptura(campo)) {
        e.preventDefault();
        e.stopPropagation();
        clearTimeout(escribiendo);
        const texto = campo.value;
        campo.value = '';
        if (texto.trim() !== '') pantalla.invokeMethodAsync('Escaneado', texto);
        return;
    }
    const enCodigo = campo !== null && campo.dataset.codigo !== undefined;
    const enPago = campo !== null && campo.dataset.pago !== undefined;
    const vacio = campo === null || campo.value === '';
    // Los rápidos de la pantalla nueva: Alt+1 a Alt+6.
    const rapido = e.altKey && !e.ctrlKey && /^[1-6]$/.test(tecla);
    const ctrlP = e.ctrlKey && (tecla === 'p' || tecla === 'P');
    const ctrlE = e.ctrlKey && (tecla === 'e' || tecla === 'E');
    const ctrlD = e.ctrlKey && (tecla === 'd' || tecla === 'D');
    const ctrlL = e.ctrlKey && (tecla === 'l' || tecla === 'L');
    const ctrlB = e.ctrlKey && (tecla === 'b' || tecla === 'B');
    // Ctrl+U: cambiar de usuario (en el navegador sería ver el código de la página).
    const ctrlU = e.ctrlKey && (tecla === 'u' || tecla === 'U');
    const funcion = /^F([1-9]|1[0-2])$/.test(tecla);

    // Ctrl+Q: cotizar a una tecla. El selector de archivos solo se abre desde la tecla misma, no después.
    if (e.ctrlKey && (tecla === 'q' || tecla === 'Q')) {
        e.preventDefault();
        e.stopPropagation();
        if (!hayVentana) document.getElementById('pdf-cotizar')?.click();
        return;
    }

    // La lista con familias (pantalla nueva): → despliega la elegida (solo con el cursor al final del texto, para no
    // estorbar al corregir), ← y Esc la cierran y Tab despliega por otra cosa. Sin la lista, hacen lo de siempre.
    const lista = !hayVentana && esCaptura(campo) ? document.querySelector('[data-familias]') : null;
    if (lista && !e.ctrlKey && !e.altKey) {
        const abierta = lista.querySelector('.vn-res-valor') !== null;
        const alFinal = campo.selectionStart === campo.value.length && campo.selectionEnd === campo.value.length;
        const familia = tecla === 'ArrowRight' && alFinal && !e.shiftKey ? 'ArrowRight'
            : tecla === 'ArrowLeft' && abierta && !e.shiftKey ? 'ArrowLeft'
                : tecla === 'Tab' ? (e.shiftKey ? 'Shift+Tab' : 'Tab')
                    : tecla === 'Escape' && abierta ? 'Escape-familia'
                        : null;
        if (familia) {
            e.preventDefault();
            e.stopPropagation();
            pantalla.invokeMethodAsync('Tecla', familia, campo.value);
            return;
        }
    }

    let atrapar;
    if (hayVentana) {
        atrapar = tecla === 'Escape' || funcion || ctrlP || ctrlE || ctrlD || ctrlL || ctrlB || ctrlU;
    } else {
        atrapar = funcion || ctrlP || ctrlE || ctrlD || ctrlL || ctrlB || ctrlU || rapido || tecla === 'Escape' || tecla === 'Insert'
            || (tecla === 'Delete' && vacio && !enPago)
            || ((tecla === 'ArrowUp' || tecla === 'ArrowDown') && (campo === null || enCodigo))
            || ((tecla === '+' || tecla === '-') && vacio && (campo === null || enCodigo));
    }
    if (!atrapar) return;

    e.preventDefault();
    e.stopPropagation();
    // Con lo que hay en el cuadro de captura de la pantalla nueva (no está enlazado): «20» y Alt+1, o las piezas y F11.
    // Lo que usa ese texto (un rápido, F11, Esc) vacía el cuadro aquí mismo, para que la tecla siguiente no lo vuelva a usar.
    let texto = null;
    if (esCaptura(campo)) {
        clearTimeout(escribiendo);
        texto = campo.value;
        if (rapido || tecla === 'Escape' || (tecla === 'F11' && texto !== '')) campo.value = '';
    }
    let nombre = ctrlP ? 'Ctrl+P' : ctrlE ? 'Ctrl+E' : ctrlD ? 'Ctrl+D' : ctrlL ? 'Ctrl+L' : ctrlB ? 'Ctrl+B' : ctrlU ? 'Ctrl+U'
        : rapido ? 'Alt+' + tecla : tecla;
    // F12 en la pantalla nueva: el cursor pasa a «Pagó con» aquí mismo, sin esperar la vuelta a .NET, para que lo que se
    // teclee enseguida (lo que dio el cliente) caiga ahí y no en el cuadro de captura. .NET sabe que ya se movió.
    if (tecla === 'F12' && !hayVentana && !enPago) {
        const pago = document.getElementById('pago-nuevo');
        if (pago) {
            pago.focus();
            pago.select();
            nombre = 'F12-pago';
        }
    }
    pantalla.invokeMethodAsync('Tecla', nombre, texto);
}

// «/ Más acciones»: deja el texto en el cuadro de captura, con el cursor al final.
export function escribirCaptura(texto) {
    const campo = document.querySelector('[data-captura]');
    if (!campo) return;
    campo.value = texto;
    campo.focus();
    campo.setSelectionRange(texto.length, texto.length);
}

// «Cotizar un PDF» desde «/»: el selector de archivos, como con Ctrl+Q.
export function clic(selector) {
    document.querySelector(selector)?.click();
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
