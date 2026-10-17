// Teclas de la caja, iguales a las de eleventa. Se atrapan antes que el navegador (F5 no recarga, Ctrl+P no imprime,
// Ctrl+E no busca, Ctrl+D no guarda un marcador).
let pantalla = null;
let hayVentana = false;
// La ventana abierta es un panel a la derecha: el ticket y la barra de teclas siguen a la vista.
let hayPanel = false;
// Se ve una página (Productos, Inventario o Clientes) en lugar de la venta (entrega 8): la venta está oculta.
let hayPagina = false;
// El mostrador (Ctrl+M, entrega 13) está abierto con el cursor en el cuadro de captura (no al acomodar).
let hayMostrador = false;
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
    clearTimeout(mandarMedidos);
    mandarMedidos = null;
    medidos = [];
    rodando = null;
    rafaga = null;
    hayPagina = false;
    hayMostrador = false;
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

// El cuadro de captura no avisa a .NET con cada carácter del lector: al Enter, el texto se lee y el cuadro se vacía aquí
// mismo, y el código va en una sola llamada. Así un escaneo nunca se pega al siguiente aunque lleguen sin pausa, y el
// lector no repinta la pantalla con cada carácter. Lo que se escribe a mano con letras se avisa en cuanto se teclea: la
// búsqueda va contra la copia de la caja y sale al momento (entrega 5, parte 3; antes se esperaban 150 ms y la red). Lo que
// llega en ráfaga (el lector, que termina con su Enter antes de parar) y lo que no trae letras (la cantidad de un rápido o
// de F11) se avisa al dejar de teclear.
function esCaptura(elemento) {
    return elemento instanceof HTMLInputElement && elemento.dataset.captura !== undefined;
}

// La hora (del teclado) de lo último que se escribió en el cuadro de captura.
let ultimaEscrita = -Infinity;

function alEscribir(e) {
    if (!pantalla || !esCaptura(e.target)) return;
    clearTimeout(escribiendo);
    const campo = e.target;
    const enRafaga = e.timeStamp - ultimaEscrita < RAFAGA_MS;
    ultimaEscrita = e.timeStamp;
    const avisar = () => pantalla?.invokeMethodAsync('Escribiendo', campo.value);
    if (!enRafaga && /\p{L}/u.test(campo.value)) avisar();
    else escribiendo = setTimeout(avisar, RAFAGA_MS);
}

export function limpiarCaptura() {
    const campo = document.querySelector('[data-captura]');
    if (campo) campo.value = '';
}

// Con una ventana abierta solo se atrapan las teclas que no escriben en sus campos: Esc, las F y las Ctrl de la caja (la
// pantalla de venta decide qué hacen; con un panel, las de la barra siguen funcionando). Supr, las flechas, + y −, Enter
// y Alt+1 a Alt+6 son de la ventana. Con un panel a la derecha, también Ins (como Ctrl+P) y Ctrl+Q.
export function ventanaAbierta(abierta, enPanel = false) {
    hayVentana = abierta;
    hayPanel = abierta && enPanel;
}

// Una página en lugar de la venta (entrega 8). Con algo encima de ella (el corte, la Configuración, la guía de la prueba),
// manda la ventana, como en la venta.
export function paginaAbierta(abierta) {
    hayPagina = abierta;
}

// El mostrador (Ctrl+M, entrega 13): el cursor se queda en el cuadro de captura, como en el prototipo, y lo escrito ahí es la
// cantidad. Las flechas eligen la pieza, Tab el exhibidor o la temporada, y Enter la agrega (en el verificador, la enseña).
// El Enter dice si llegó en ráfaga (el lector, más rápido que una persona): lo escaneado va al ticket como siempre, nunca
// como cantidad. Lo demás (escribir, Esc, las F) es como con cualquier panel.
export function mostradorAbierto(abierto) {
    hayMostrador = abierto;
}

function teclaDelMostrador(e, campo) {
    const tecla = e.key;
    const flecha = tecla === 'ArrowLeft' || tecla === 'ArrowRight' || tecla === 'ArrowUp' || tecla === 'ArrowDown';
    if (e.ctrlKey || e.altKey || e.metaKey || !(flecha || tecla === 'Enter' || tecla === 'Tab')) return false;
    e.preventDefault();
    e.stopPropagation();
    clearTimeout(escribiendo);
    const texto = campo.value;
    if (tecla === 'Enter') {
        const delLector = texto.length >= RAFAGA_MINIMO && e.timeStamp - ultimaEscrita < RAFAGA_MS;
        campo.value = '';
        pantalla.invokeMethodAsync('EnMostrador', 'Enter', texto, e.timeStamp, delLector);
    } else {
        pantalla.invokeMethodAsync('EnMostrador', tecla === 'Tab' && e.shiftKey ? 'Shift+Tab' : tecla, texto, null, false);
    }
    return true;
}

// En una página: Esc, las F y las Ctrl de la caja van a la pantalla, que decide (F1 a la venta, F3 y F4 a su página, Esc
// cierra lo abierto); así F5 no recarga ni Ctrl+P imprime. Las flechas, Enter y lo que se escribe son de la página: ↑ ↓ en
// su búsqueda recorren la lista, y en la lista o en las tarjetas pasan a la de al lado. Lo que se escribe fuera de un campo
// va a la búsqueda de la página (data-busca), en lugar de lo que había: así el lector, o quien empieza a escribir con el
// cursor en la lista, busca sin poner antes el cursor ahí. Nada de esto toca a la venta, que está oculta.
function teclaDePagina(e) {
    const tecla = e.key;
    const funcion = /^F([1-9]|1[0-2])$/.test(tecla);
    const ctrlCaja = e.ctrlKey && !e.altKey && /^[pedlbuqmPEDLBUQM]$/.test(tecla);
    if (tecla === 'Escape' || funcion || ctrlCaja) {
        e.preventDefault();
        e.stopPropagation();
        pantalla.invokeMethodAsync('Tecla', ctrlCaja ? 'Ctrl+' + tecla.toUpperCase() : tecla, null);
        return;
    }
    const donde = e.target instanceof HTMLElement ? e.target : null;
    if ((tecla === 'ArrowUp' || tecla === 'ArrowDown') && donde?.matches('[data-busca]')) {
        // La página las atiende (su @onkeydown); que el cursor no brinque al principio o al final de lo escrito.
        e.preventDefault();
        return;
    }
    if (tecla.startsWith('Arrow') && !e.ctrlKey && !e.altKey && !e.shiftKey && moverEnLista(donde, tecla)) {
        e.preventDefault();
        return;
    }
    const control = donde instanceof HTMLButtonElement || donde instanceof HTMLInputElement || donde instanceof HTMLSelectElement
        || donde instanceof HTMLAnchorElement;
    if (tecla.length !== 1 || e.ctrlKey || e.altKey || e.metaKey || esEditable(donde) || (tecla === ' ' && control)) return;
    const busca = [...document.querySelectorAll('[data-busca]')].find(c => c.offsetParent !== null);
    if (!busca) return;
    e.preventDefault();
    busca.focus();
    busca.select();
    busca.setRangeText(tecla, busca.selectionStart ?? 0, busca.selectionEnd ?? 0, 'end');
    busca.dispatchEvent(new Event('input', { bubbles: true }));
}

function esEditable(elemento) {
    if (elemento instanceof HTMLTextAreaElement || elemento instanceof HTMLSelectElement) return true;
    if (elemento instanceof HTMLInputElement)
        return !['button', 'checkbox', 'radio', 'submit', 'reset', 'file', 'image', 'range', 'color'].includes(elemento.type);
    return elemento?.isContentEditable === true;
}

// ↑ ↓ en una lista (data-lista) eligen el de arriba o el de abajo, como un clic; las flechas en las tarjetas del
// inventario (data-rejilla) pasan el cursor a la de al lado, arriba o abajo, y Enter la abre.
function moverEnLista(donde, tecla) {
    const renglon = donde?.closest('[data-lista] .vn-item');
    if (renglon && (tecla === 'ArrowUp' || tecla === 'ArrowDown')) {
        const renglones = [...renglon.closest('[data-lista]').querySelectorAll('.vn-item')];
        const otro = renglones[renglones.indexOf(renglon) + (tecla === 'ArrowDown' ? 1 : -1)];
        if (otro) {
            otro.focus();
            otro.click();
        }
        return true;
    }
    const tarjeta = donde?.closest('[data-rejilla] .vn-tarea');
    if (tarjeta) {
        const rejilla = tarjeta.closest('[data-rejilla]');
        const tarjetas = [...rejilla.querySelectorAll('.vn-tarea')];
        const columnas = getComputedStyle(rejilla).gridTemplateColumns.split(' ').filter(Boolean).length || 1;
        const paso = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -columnas, ArrowDown: columnas }[tecla] ?? 0;
        tarjetas[tarjetas.indexOf(tarjeta) + paso]?.focus();
        return true;
    }
    return false;
}

// El cobro (data-cobro: todo el panel del total, con «Pagó con», los montos y referencias de tarjeta, transferencia y
// crédito y el botón de cobrar; también el abono): el lector se reconoce por su ritmo, más rápido que una persona y con
// Enter al final (docs/propuesta-interfaz.md, «El cuadro de captura»). Si escribe ahí, lo que tecleó se quita del
// campo, su Enter no cobra (ni oprime el botón) y el código va a la captura: un escaneo nunca se toma como dinero ni se
// cobra sin él. Una persona no baja de unos 80 ms entre tecla y tecla ni con el teclado numérico; un lector USB teclea
// cada carácter en pocos milisegundos. El ritmo del lector de la tienda falta medirlo allá (docs/plan.md, paso 7): si
// no se reconoce, se ajustan RAFAGA_MS y RAFAGA_MINIMO (el cambio no lleva tope: lo decidió el dueño el 2026-10-14).
// RAFAGA_MS también dice, en el cuadro de captura, qué se escribe en ráfaga y no busca hasta que se deja de teclear.
const RAFAGA_MS = 50;
// Menos caracteres no se distinguen de alguien que teclea rápido «200» y Enter.
const RAFAGA_MINIMO = 4;
// La ráfaga en curso: dónde se teclea, lo que tenía antes de empezar, lo que va tecleando y la hora de la última tecla.
let rafaga = null;

// Un campo o un botón marcado, o lo que esté dentro de algo marcado (el panel del total).
function esDelCobro(elemento) {
    return elemento instanceof HTMLElement && elemento.closest('[data-cobro]') !== null;
}

// Cada tecla en el cobro. Devuelve true si era el Enter de una ráfaga del lector: ya se mandó a la captura.
// La hora es la del teclado (timeStamp), no la de atenderla: si la página estuvo ocupada, lo que tecleó una persona no
// se junta en una ráfaga.
function rafagaDelLector(e) {
    const donde = e.target;
    if (!esDelCobro(donde)) {
        rafaga = null;
        return false;
    }
    const hora = e.timeStamp;
    if (e.key === 'Enter') {
        const r = rafaga;
        rafaga = null;
        if (!r || r.donde !== donde || r.texto.length < RAFAGA_MINIMO || hora - r.ultima > RAFAGA_MS) return false;
        e.preventDefault();
        e.stopPropagation();
        // Un campo vuelve a lo que tenía (lo que se tecleó antes a mano se queda) y .NET se entera por su @bind; un botón
        // no tiene qué devolver.
        if (donde instanceof HTMLInputElement) {
            donde.value = r.antes;
            donde.dispatchEvent(new Event('input', { bubbles: true }));
        }
        pantalla.invokeMethodAsync('EscaneoEnPago', r.texto, hora);
        return true;
    }
    if (e.key.length === 1 && !e.ctrlKey && !e.altKey && !e.metaKey) {
        if (rafaga && rafaga.donde === donde && hora - rafaga.ultima <= RAFAGA_MS) {
            rafaga.texto += e.key;
            rafaga.ultima = hora;
        } else {
            rafaga = { donde, antes: donde instanceof HTMLInputElement ? donde.value : '', texto: e.key, ultima: hora };
        }
    } else if (e.key !== 'Shift') {
        // Las mayúsculas del lector llegan con Shift; cualquier otra tecla (borrar, flechas) es de una persona.
        rafaga = null;
    }
    return false;
}

function alPresionar(e) {
    if (!pantalla) return;
    const tecla = e.key;
    const campo = e.target instanceof HTMLInputElement ? e.target : null;
    if (rafagaDelLector(e)) return;
    if (hayPagina && !hayVentana) {
        teclaDePagina(e);
        return;
    }
    if (hayMostrador && hayPanel && esCaptura(campo) && teclaDelMostrador(e, campo)) return;

    if (tecla === 'Enter' && !hayVentana && esCaptura(campo)) {
        e.preventDefault();
        e.stopPropagation();
        clearTimeout(escribiendo);
        const texto = campo.value;
        campo.value = '';
        // Con la hora del Enter (el reloj de la página): se mide cuánto tarda en verse su línea (pintado, abajo).
        if (texto.trim() !== '') pantalla.invokeMethodAsync('Escaneado', texto, e.timeStamp);
        return;
    }
    const enCodigo = esCaptura(campo);
    const enPago = campo !== null && campo.dataset.pago !== undefined;
    const vacio = campo === null || campo.value === '';
    // Los rápidos: Alt+1 a Alt+6.
    const rapido = e.altKey && !e.ctrlKey && /^[1-6]$/.test(tecla);
    const ctrlP = e.ctrlKey && (tecla === 'p' || tecla === 'P');
    const ctrlE = e.ctrlKey && (tecla === 'e' || tecla === 'E');
    const ctrlD = e.ctrlKey && (tecla === 'd' || tecla === 'D');
    const ctrlL = e.ctrlKey && (tecla === 'l' || tecla === 'L');
    const ctrlB = e.ctrlKey && (tecla === 'b' || tecla === 'B');
    // Ctrl+M: el mostrador y la temporada (entrega 13).
    const ctrlM = e.ctrlKey && (tecla === 'm' || tecla === 'M');
    // Ctrl+U: cambiar de usuario (en el navegador sería ver el código de la página).
    const ctrlU = e.ctrlKey && (tecla === 'u' || tecla === 'U');
    const funcion = /^F([1-9]|1[0-2])$/.test(tecla);

    // Ctrl+Q: cotizar a una tecla. Sin ventana, el selector de archivos se abre aquí mismo, desde la tecla. Con un panel
    // abierto lo decide la pantalla de venta: si el panel se deja, abre el selector enseguida (al elegir el PDF, el panel
    // cambia al de cotizar); si espera su Enter (artículo común, descuento…) o una autorización, lo dice.
    if (e.ctrlKey && (tecla === 'q' || tecla === 'Q')) {
        e.preventDefault();
        e.stopPropagation();
        if (!hayVentana) document.getElementById('pdf-cotizar')?.click();
        else if (hayPanel) pantalla.invokeMethodAsync('Tecla', 'Ctrl+Q', null);
        return;
    }

    // La lista con familias: → despliega la elegida (solo con el cursor al final del texto, para no estorbar al
    // corregir), ← y Esc la cierran y Tab despliega por otra cosa. Sin la lista, hacen lo de siempre.
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
        atrapar = tecla === 'Escape' || funcion || ctrlP || ctrlE || ctrlD || ctrlL || ctrlB || ctrlM || ctrlU || (hayPanel && tecla === 'Insert');
    } else {
        atrapar = funcion || ctrlP || ctrlE || ctrlD || ctrlL || ctrlB || ctrlM || ctrlU || rapido || tecla === 'Escape' || tecla === 'Insert'
            || (tecla === 'Delete' && vacio && !enPago)
            || ((tecla === 'ArrowUp' || tecla === 'ArrowDown') && (campo === null || enCodigo))
            || ((tecla === '+' || tecla === '-') && vacio && (campo === null || enCodigo));
    }
    if (!atrapar) return;

    e.preventDefault();
    e.stopPropagation();
    // Con lo que hay en el cuadro de captura (no está enlazado): «20» y Alt+1, o las piezas y F11.
    // Lo que usa ese texto (un rápido, F11, Esc) vacía el cuadro aquí mismo, para que la tecla siguiente no lo vuelva a usar.
    let texto = null;
    if (esCaptura(campo)) {
        clearTimeout(escribiendo);
        texto = campo.value;
        if (rapido || tecla === 'Escape' || (tecla === 'F11' && texto !== '')) campo.value = '';
    }
    let nombre = ctrlP ? 'Ctrl+P' : ctrlE ? 'Ctrl+E' : ctrlD ? 'Ctrl+D' : ctrlL ? 'Ctrl+L' : ctrlB ? 'Ctrl+B' : ctrlM ? 'Ctrl+M' : ctrlU ? 'Ctrl+U'
        : rapido ? 'Alt+' + tecla : tecla;
    // F12: el cursor pasa a «Pagó con» aquí mismo, sin esperar la vuelta a .NET, para que lo que se teclee enseguida
    // (lo que dio el cliente) caiga ahí y no en el cuadro de captura. .NET sabe que ya se movió.
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

// Del Enter a la línea pintada (entrega 5, docs/propuesta-interfaz.md, «Cómo se mide»): .NET avisa cuando ya dibujó lo de
// esos escaneos, con la hora de cada Enter; aquí se mide con el reloj de la página en el cuadro siguiente, el que la pinta,
// y se le manda a la pantalla de a varios (una vez por segundo), para no sumarle trabajo en plena ráfaga.
let medidos = [];
let mandarMedidos = null;

export function pintado(horas) {
    requestAnimationFrame(() => {
        const ahora = performance.now();
        for (const hora of horas) medidos.push(ahora - hora);
        if (mandarMedidos) return;
        mandarMedidos = setTimeout(() => {
            mandarMedidos = null;
            const lista = medidos.splice(0);
            if (lista.length > 0 && pantalla) pantalla.invokeMethodAsync('Pintados', lista).catch(() => { });
        }, 1000);
    });
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

// La foto de un producto (Productos, F3; entrega 13, parte 2): con el cursor en su cuadro, Ctrl+V pega la imagen que se
// copió (de otra página, de una captura de pantalla). Se achica aquí, a lo más 960 por lado y en JPEG, y se le pasa a la ficha
// (ImagenPegada) para verla antes de guardarla; el servicio la deja de 240. Lo que no es una imagen se pega como siempre.
export function pegarImagen(selector, ficha) {
    const zona = document.querySelector(selector);
    if (!zona) return;
    zona.onpaste = async e => {
        const imagen = [...(e.clipboardData?.items ?? [])].find(i => i.kind === 'file' && i.type.startsWith('image/'));
        if (!imagen) return;
        e.preventDefault();
        try {
            await ficha.invokeMethodAsync('ImagenPegada', await achicarImagen(imagen.getAsFile(), 960));
        } catch {
            ficha.invokeMethodAsync('ImagenPegada', null).catch(() => { });
        }
    };
}

async function achicarImagen(archivo, lado) {
    const imagen = await createImageBitmap(archivo);
    const escala = Math.min(1, lado / Math.max(imagen.width, imagen.height));
    const lienzo = document.createElement('canvas');
    lienzo.width = Math.max(1, Math.round(imagen.width * escala));
    lienzo.height = Math.max(1, Math.round(imagen.height * escala));
    const dibujo = lienzo.getContext('2d');
    // Lo transparente (una imagen de una página) queda blanco, como en la caja principal.
    dibujo.fillStyle = '#fff';
    dibujo.fillRect(0, 0, lienzo.width, lienzo.height);
    dibujo.drawImage(imagen, 0, 0, lienzo.width, lienzo.height);
    imagen.close();
    const jpeg = await new Promise(listo => lienzo.toBlob(listo, 'image/jpeg', 0.9));
    return new Uint8Array(await jpeg.arrayBuffer());
}
