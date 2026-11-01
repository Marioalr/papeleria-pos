// Revisión independiente de «Servicios» (servicios/curp, 7c8e143; 2026-10-31), traída al repositorio con su arreglo. Lo corre
// RevisionServiciosCajaJsTests. Cambios a la de la revisión: el aviso de arriba (document.body y createElement) se arma desde el principio,
// porque ahora el Enter del lector en un botón de un panel hace lo del lector por puerto y lo dice; y de cada botón se revisa además ese
// aviso («no hay dónde escribirlo»: el código no se pierde sin decirlo), con el enlace de otra pestaña y la lista de impresoras. De la
// segunda revisión (4b): en la lista de impresoras ninguna tecla del código la cambia, solo las flechas.
// caja.js con el mismo DOM mínimo de prueba-caja.mjs: el lector de teclado (su ráfaga y su Enter) con el cursor en un botón de los
// paneles nuevos. El panel del trámite pone el cursor en «Imprimir» al llegar el documento (TramiteEnLaCaja.razor:155, «Enter imprime»),
// en «Cobrar CURP» en Edge o en la caja del navegador (:159-160) y en «Reintentar» (:157-158); «Servicios» lo pone en la CURP
// (ListaDeServicios.razor:43). Si caja.js no detiene ese Enter, el navegador oprime el botón: un escaneo imprime y cobra la CURP, la cobra
// sin documento o abre el trámite, y el código leído no llega al ticket. El cobro ya se cuida así (data-cobro: el Enter del lector no
// oprime «Cobrar»): eso se revisa aquí también, para comparar.
// Uso: node revision-servicios.mjs /ruta/a/una/copia/de/caja.mjs
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';

class HTMLElement {
    constructor({ id = null, dataset = {}, padres = [], clases = [], padre = null, fijo = false } = {}) {
        this.id = id;
        this.dataset = dataset;
        this.padres = padres;
        this.clases = clases;
        this.padre = padre;
        this.offsetParent = fijo ? null : {};
        this.fijo = fijo;
        this.labels = [];
        this.placeholder = '';
    }
    getAttribute() { return null; }
    getClientRects() { return this.offsetParent !== null || this.fijo ? [{}] : []; }
    contains(otro) {
        for (let e = otro; e; e = e.padre) if (e === this) return true;
        return false;
    }
    querySelectorAll(selector) { return documento.querySelectorAll(selector).filter(e => e !== this && this.contains(e)); }
    closest(selector) {
        const m = /^\[data-([a-z-]+)\]$/.exec(selector);
        if (m && (this.dataset[m[1]] !== undefined || this.padres.includes(selector))) return this;
        return null;
    }
    matches() { return false; }
    focus() { documento.activo = this; }
    select() { }
}
class HTMLInputElement extends HTMLElement {
    constructor(opciones = {}) {
        super(opciones);
        this.value = opciones.value ?? '';
        this.type = 'text';
        this.selectionStart = this.value.length;
        this.selectionEnd = this.value.length;
    }
    dispatchEvent() { return true; }
    setSelectionRange() { }
}
class HTMLButtonElement extends HTMLElement { }
class HTMLSelectElement extends HTMLElement { }
class HTMLAnchorElement extends HTMLElement { }
class HTMLTextAreaElement extends HTMLElement { }

const escuchas = {};
const documento = {
    activo: null,
    elementos: {},
    get activeElement() { return this.activo; },
    body: null,
    addEventListener(tipo, fn) { escuchas[tipo] = fn; },
    removeEventListener() { },
    getElementById(id) { return this.elementos[id] ?? null; },
    querySelector() { return null; },
    querySelectorAll() { return []; },
};
const setTimeoutDeNode = globalThis.setTimeout;
Object.assign(globalThis, {
    HTMLElement, HTMLInputElement, HTMLButtonElement, HTMLSelectElement, HTMLAnchorElement, HTMLTextAreaElement,
    document: documento, Event: class { constructor(t) { this.type = t; } },
    KeyboardEvent: class { constructor(t, init = {}) { this.type = t; Object.assign(this, init); } },
    requestAnimationFrame: f => f(), getComputedStyle: () => ({ gridTemplateColumns: '' }),
    setTimeout: (f, ms) => { const reloj = setTimeoutDeNode(f, ms); reloj.unref?.(); return reloj; },
});

const llamadas = [];
const pantalla = { invokeMethodAsync: (...args) => { llamadas.push(args); return Promise.resolve(); } };

const caja = await import(pathToFileURL(process.argv[2]).href);
caja.registrar(pantalla);
// El aviso del lector, arriba (avisarDelLector): desde el principio, porque el Enter del lector en un botón de un panel ahora lo usa.
documento.body = new HTMLElement({ id: 'body' });
documento.body.appendChild = elemento => { documento.elementos[elemento.id] = elemento; };
documento.createElement = () => ({ hidden: true, textContent: '', setAttribute() { } });

function tecla(target, key, timeStamp) {
    let prevenido = false;
    escuchas.keydown({ key, target, timeStamp, ctrlKey: false, altKey: false, metaKey: false, shiftKey: false,
        preventDefault() { prevenido = true; }, stopPropagation() { } });
    return prevenido;
}

// El lector de teclado: un carácter cada 5 ms y su Enter. Devuelve si caja.js detuvo el Enter (si no, el navegador oprime lo enfocado).
function escanear(donde, codigo, desde) {
    let t = desde;
    for (const c of codigo) {
        tecla(donde, c, t);
        t += 5;
    }
    return tecla(donde, 'Enter', t);
}

const fallas = [];
function revisar(condicion, texto) {
    if (!condicion) fallas.push(texto);
}

// El panel de la derecha (.vn-panel) abierto en la venta: el trámite o «Servicios».
caja.paginaAbierta(false);
caja.mostradorAbierto(false);
caja.ventanaAbierta(true, true, false);
const panel = new HTMLElement({ clases: ['vn-panel'] });

// 0) Para comparar: el botón «Cobrar» del cobro (data-cobro). El Enter del lector no lo oprime: el código va al ticket.
const cobrar = new HTMLButtonElement({ id: 'cobrar', padres: ['[data-cobro]'] });
caja.ventanaAbierta(false);
cobrar.focus();
llamadas.length = 0;
assert.equal(escanear(cobrar, '7501234567890', 1000), true, 'control: el Enter del lector en «Cobrar» se detiene');
assert.deepEqual(llamadas.at(-1)?.slice(0, 2), ['EscaneoEnPago', '7501234567890'], 'control: el código va al ticket');
console.log('ok 0 (control): en el cobro, el Enter del lector no oprime «Cobrar» y el código va al ticket');
caja.ventanaAbierta(true, true, false);

// 1-4) Los botones que enfocan los paneles nuevos.
const casos = [
    ['tramite-imprimir', '«Imprimir» con el documento listo: imprime «T-0042 · CURP» y la CURP entra al ticket', HTMLButtonElement],
    ['tramite-cobrar', '«Cobrar CURP» (en la caja del navegador): la CURP entra al ticket sin documento', HTMLButtonElement],
    ['tramite-reintentar', '«Reintentar»: vuelve a cargar la página oficial', HTMLButtonElement],
    ['servicio-curp', '«CURP» en Servicios: abre el trámite (folio nuevo y gob.mx encima del ticket)', HTMLButtonElement],
    ['tramite-elegir-pdf', '«Elegir el PDF descargado» (Edge): abre el diálogo de Windows', HTMLButtonElement],
    ['tramite-cobrar-de-nuevo', '«Agregar la CURP al ticket»: la cobra', HTMLButtonElement],
    ['tramite-otra-pestana', 'el enlace «Abrir gob.mx/curp en otra pestaña» (caja del navegador): abre gob.mx', HTMLAnchorElement],
    ['tramite-impresora', 'la lista de impresoras: el Enter no imprime, pero el código no se dice', HTMLSelectElement],
];
let t = 2000;
for (const [id, efecto, Clase] of casos) {
    const boton = new Clase({ id, padre: panel });
    boton.focus();
    llamadas.length = 0;
    documento.elementos['aviso-lector'] && (documento.elementos['aviso-lector'].textContent = '');
    const detenido = escanear(boton, '7501234567890', t);
    t += 1000;
    const alTicket = llamadas.some(l => ['Escaneado', 'EscaneoEnPago'].includes(l[0]) && l[1] === '7501234567890');
    revisar(detenido, `#${id}: el Enter del lector no se detiene, así que el navegador oprime el botón (${efecto}); el código no va al ticket `
        + `(${alTicket ? 'sí fue' : 'no fue'}) ni se avisa (llamadas: ${JSON.stringify(llamadas)})`);
    // Como el lector por puerto: el código no se pierde sin decirlo.
    const aviso = documento.elementos['aviso-lector'];
    revisar(aviso?.hidden === false && /«7501234567890»/.test(aviso.textContent ?? ''),
        `#${id}: el código leído no se dice arriba (aviso: ${JSON.stringify(aviso?.textContent)})`);
}

// 4b) Segunda revisión: en la lista de impresoras ninguna tecla del código elige otra impresora (en el navegador, escribir en una lista
// elige la opción que empieza así), ni la primera, que todavía no se distingue de una persona. Con las flechas sí se elige. Tercera:
// solo en los paneles de «Servicios» y del trámite (data-solo-flechas).
{
    const lista = new HTMLSelectElement({ id: 'tramite-impresora', padre: panel, padres: ['[data-solo-flechas]'] });
    lista.focus();
    let hora = 90000;
    const sinDetener = [...'7501234567890'].filter(c => {
        const detenida = tecla(lista, c, hora);
        hora += 5;
        return !detenida;
    });
    revisar(sinDetener.length === 0, `#tramite-impresora: estas teclas del código llegan a la lista y le cambian la impresora: ${JSON.stringify(sinDetener)}`);
    revisar(tecla(lista, 'Enter', hora), '#tramite-impresora: el Enter del código no se detiene');
    revisar(!tecla(lista, 'ArrowDown', hora + 1000), '#tramite-impresora: las flechas ya no eligen la impresora');
}

// 4c) Tercera revisión: en las demás listas de una ventana o un panel (Descuento, Abonar, F2, la Configuración), escribir elige, como
// siempre: una persona que teclea «t» para «Tarjeta» no se queda sin eso. Su Enter del lector sigue sin oprimir nada.
{
    const otraLista = new HTMLSelectElement({ id: 'abono-forma', padre: panel });
    otraLista.focus();
    revisar(!tecla(otraLista, 't', 95000), '#abono-forma: una lista fuera de los paneles del trámite ya no elige al teclear');
    let hora = 96000;
    for (const c of '7501234567890') {
        tecla(otraLista, c, hora);
        hora += 5;
    }
    revisar(tecla(otraLista, 'Enter', hora), '#abono-forma: el Enter del código no se detiene');
}

// 5) Para comparar: el lector por puerto (la caja 2) con el cursor en «Imprimir» no oprime nada: dice que no hay dónde escribirlo.
const imprimir = new HTMLButtonElement({ id: 'tramite-imprimir', padre: panel });
imprimir.focus();
llamadas.length = 0;
assert.equal(caja.escaneoExterno('7501234567890'), 'sin-lugar', 'control: el puerto no oprime el botón');
assert.equal(documento.elementos['aviso-lector']?.hidden, false, 'control: el puerto lo dice arriba');
console.log('ok 5 (control): el lector por puerto con el cursor en «Imprimir» no oprime nada y lo dice');

if (fallas.length > 0) {
    console.log(`FALLA: ${fallas.length} de ${casos.length}:\n- ${fallas.join('\n- ')}`);
    process.exit(1);
}
console.log('todo ok');
