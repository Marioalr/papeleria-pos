// caja.js con un DOM mínimo hecho a mano, sin dependencias (revisión del PR #133): lo que manda a la pantalla con cada tecla, y lo
// que hace con cada código del lector por puerto (escaneoExterno, piloto alfa).
// Lo corre CajaJsTests con node. Uso: node prueba-caja.mjs /ruta/a/una/copia/de/caja.mjs
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';

class HTMLElement {
    constructor({ id = null, dataset = {}, padres = [] } = {}) {
        this.id = id;
        this.dataset = dataset;
        // Los selectores de atributo que cumple el elemento o alguno de sus padres (solo lo que usa caja.js).
        this.padres = padres;
        // A la vista (null: oculto, como en una página que no se ve).
        this.offsetParent = {};
        this.labels = [];
        this.placeholder = '';
    }
    getAttribute() { return null; }
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
        // Los avisos que se le mandaron (input, change, keydown…), y si caja.js detuvo su keydown.
        this.eventos = [];
        this.prevenido = false;
    }
    // Como en la página: el keydown pasa primero por lo que escucha caja.js en el documento (alPresionar).
    dispatchEvent(e) {
        this.eventos.push(e.type);
        if (e.type === 'keydown') {
            const campo = this;
            escuchas.keydown?.({ key: e.key, target: campo, timeStamp: performance.now(), ctrlKey: false, altKey: false, metaKey: false,
                shiftKey: false, preventDefault() { campo.prevenido = true; }, stopPropagation() { } });
        }
        return true;
    }
    setSelectionRange() { }
    setRangeText(texto, desde, hasta) {
        this.value = this.value.slice(0, desde) + texto + this.value.slice(hasta);
        this.selectionStart = this.selectionEnd = desde + texto.length;
    }
}
class HTMLButtonElement extends HTMLElement { }
class HTMLSelectElement extends HTMLElement { }
class HTMLAnchorElement extends HTMLElement { }
class HTMLTextAreaElement extends HTMLElement { }

const escuchas = {};
const documento = {
    activo: null,
    elementos: {},
    addEventListener(tipo, fn) { escuchas[tipo] = fn; },
    removeEventListener() { },
    getElementById(id) { return this.elementos[id] ?? null; },
    querySelector() { return null; },
    querySelectorAll() { return []; },
};
// Los relojes de caja.js (el aviso del lector dura unos segundos) no detienen a node al terminar.
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

function tecla(target, key, timeStamp, extra = {}) {
    let prevenido = false;
    escuchas.keydown({ key, target, timeStamp, ctrlKey: false, altKey: false, metaKey: false, shiftKey: false, ...extra,
        preventDefault() { prevenido = true; }, stopPropagation() { } });
    return prevenido;
}
function escribir(campo, texto, desde, paso) {
    let t = desde;
    for (const c of texto) {
        campo.value += c;
        escuchas.input({ target: campo, timeStamp: t });
        t += paso;
    }
    return t;
}
const ultima = () => llamadas.at(-1);

const captura = new HTMLInputElement({ id: 'codigo', dataset: { captura: '' } });
const pagoCon = new HTMLInputElement({ id: 'pago-nuevo', dataset: { pago: '' }, padres: ['[data-cobro]'] });
const montoAbono = new HTMLInputElement({ id: 'abono-monto', dataset: { cobro: '' } });
documento.elementos['pago-nuevo'] = pagoCon;

// 1) En la venta, sin ventana: F4 en «Pagó con» es la nota; en el cuadro de captura, Inventario.
caja.ventanaAbierta(false);
tecla(pagoCon, 'F4', 1000);
assert.deepEqual(ultima(), ['Tecla', 'F4-cobro', null]);
tecla(captura, 'F4', 1100);
assert.deepEqual(ultima(), ['Tecla', 'F4', '']);
console.log('ok 1: F4 en «Pagó con» -> F4-cobro; en el cuadro -> F4');

// 2) Con el panel del abono abierto (Ctrl+B), F4 en su monto (también data-cobro) es Inventario, no la nota.
caja.ventanaAbierta(true, true, false);
tecla(montoAbono, 'F4', 2000);
assert.deepEqual(ultima(), ['Tecla', 'F4', null]);
console.log('ok 2: F4 en el monto del abono (panel abierto) -> F4');

// 3) «Varios» abierto: «5» escrito a mano y F11 por error. caja.js manda F11 con «5» (la pantalla lo regresa al cuadro si el
// panel no se deja).
caja.ventanaAbierta(true, true, true);
captura.value = '';
escribir(captura, '5', 3000, 0);
tecla(captura, 'F11', 3200);
assert.deepEqual(ultima(), ['Tecla', 'F11', '5']);
console.log('ok 3: «Varios» + «5» + F11 -> F11 con «5»');

// 4) «Varios»: una ráfaga del lector (cada 5 ms) es el código; «1500» tecleado a mano (cada 120 ms), no; un lector lento
// (60 ms entre carácter y carácter) con un código corto tampoco: la pantalla decide si es cantidad.
captura.value = '';
let t = escribir(captura, '7501234567890', 4000, 5);
tecla(captura, 'Enter', t);
assert.deepEqual(ultima(), ['EnVarios', '7501234567890', t, true]);
captura.value = '';
t = escribir(captura, '1500', 5000, 120);
tecla(captura, 'Enter', t + 100);
assert.deepEqual(ultima(), ['EnVarios', '1500', t + 100, false]);
captura.value = '';
t = escribir(captura, '4521', 6000, 60);
tecla(captura, 'Enter', t);
assert.deepEqual(ultima(), ['EnVarios', '4521', t, false]);
console.log('ok 4: ráfaga -> código; a mano o lector lento -> sin ráfaga');

// 5) Una página con campos de números (data-rafaga: «Cuántos llegaron» y el costo de «Agregar inventario», revisión del PR
// #138): la ráfaga del lector no se queda en el campo ni su Enter pasa; el código va a la página que se registró (rafagasDe),
// con el campo y lo que tenía. Lo tecleado a mano, y una ráfaga de menos de 4 caracteres, sí son números.
caja.ventanaAbierta(false);
caja.paginaAbierta(true);
const deLaPagina = [];
caja.rafagasDe({ invokeMethodAsync: (...args) => { deLaPagina.push(args); return Promise.resolve(); } });
const cuantos = new HTMLInputElement({ id: 'agregar-cantidad', dataset: { rafaga: '' } });
function teclear(campo, texto, desde, paso) {
    let hora = desde;
    for (const c of texto) {
        tecla(campo, c, hora);
        campo.value += c;
        hora += paso;
    }
    return hora - paso;
}
cuantos.value = '24';
t = teclear(cuantos, '94713', 7000, 5);
assert.equal(tecla(cuantos, 'Enter', t + 5), true);
assert.equal(cuantos.value, '24');
assert.deepEqual(deLaPagina.at(-1), ['RafagaEnCampo', '94713', 'agregar-cantidad', '24']);
cuantos.value = '';
t = teclear(cuantos, '1500', 8000, 120);
assert.equal(tecla(cuantos, 'Enter', t + 100), false);
assert.equal(cuantos.value, '1500');
cuantos.value = '';
t = teclear(cuantos, '123', 9000, 5);
assert.equal(tecla(cuantos, 'Enter', t + 5), false);
assert.equal(deLaPagina.length, 1);
// Sin página registrada (ya se fue), la ráfaga tampoco se queda como número.
caja.rafagasDe(null);
cuantos.value = '7';
t = teclear(cuantos, '7501234567890', 10000, 5);
assert.equal(tecla(cuantos, 'Enter', t + 5), true);
assert.equal(cuantos.value, '7');
assert.equal(deLaPagina.length, 1);
caja.paginaAbierta(false);
console.log('ok 5: ráfaga en un campo de números -> la página, como código; a mano o corta -> número');

// 6) Lo que la venta no tomó («2,5*código», revisión de la coma) regresa al cuadro de captura solo si está vacío, y
// seleccionado (un escaneo lo reemplaza): lo que el lector ya teclea no se pisa ni se le pega. Con el cursor en «Pagó con» (un
// F12 rápido), se pone, pero el cursor se queda ahí.
Object.defineProperty(documento, 'activeElement', { get() { return this.activo; } });
documento.querySelector = selector => selector === '[data-captura]' ? captura : null;
let seleccionado = null;
captura.select = () => { seleccionado = captura.value; };
captura.value = '';
captura.focus();
assert.equal(caja.regresarACaptura('2,5*7501234567890'), true);
assert.equal(captura.value, '2,5*7501234567890');
assert.equal(seleccionado, '2,5*7501234567890');
assert.equal(documento.activo, captura);
captura.value = '75099';
seleccionado = null;
assert.equal(caja.regresarACaptura('2,5*7501234567890'), false);
assert.equal(captura.value, '75099');
assert.equal(seleccionado, null);
captura.value = '';
pagoCon.focus();
assert.equal(caja.regresarACaptura('2,5*7501234567890'), true);
assert.equal(captura.value, '2,5*7501234567890');
assert.equal(documento.activo, pagoCon);
console.log('ok 6: lo rechazado regresa solo al cuadro vacío, seleccionado, sin quitarle el cursor a «Pagó con»');

// 7) El lector por puerto COM (escaneoExterno; piloto alfa, 2026-10-25): la ventana de Windows entrega cada código entero, y caja.js
// lo pone donde está el cursor como una ráfaga del lector reconocida, por el mismo camino y con las mismas protecciones.
documento.body = new HTMLElement({ id: 'body' });
documento.body.appendChild = elemento => { documento.elementos[elemento.id] = elemento; };
documento.createElement = () => ({ hidden: true, textContent: '', setAttribute() { } });
const conDatos = [];
documento.querySelectorAll = selector => {
    const m = /^\[data-([a-z-]+)\]$/.exec(selector);
    return m ? conDatos.filter(e => e.dataset[m[1]] !== undefined) : [];
};
const avisoDelLector = () => documento.elementos['aviso-lector'];
assert.equal(typeof globalThis.cajaLector?.escaneoExterno, 'function');

// 7a) La venta, el cursor en el cuadro de captura vacío: a la venta, con la hora (para medir cuánto tarda en verse).
caja.ventanaAbierta(false);
captura.value = '';
captura.selectionStart = captura.selectionEnd = 0;
captura.focus();
assert.equal(caja.escaneoExterno('7501234567890'), 'captura');
assert.deepEqual(ultima().slice(0, 2), ['Escaneado', '7501234567890']);
assert.equal(typeof ultima()[2], 'number');
assert.equal(captura.value, '');
// «3*» escrito y luego el escaneo: tres de ese código (la cantidad que esperaba su código), como con el teclado.
captura.value = '3*';
captura.selectionStart = captura.selectionEnd = 2;
assert.equal(caja.escaneoExterno('7501234567890'), 'captura');
assert.deepEqual(ultima().slice(0, 2), ['Escaneado', '3*7501234567890']);
// Lo rechazado que regresó al cuadro, seleccionado: el escaneo lo reemplaza.
captura.value = '2,5*7509';
captura.selectionStart = 0;
captura.selectionEnd = 8;
assert.equal(caja.escaneoExterno('7501234567890'), 'captura');
assert.deepEqual(ultima().slice(0, 2), ['Escaneado', '7501234567890']);
// Con el cursor en un botón de la venta: al cuadro, que se queda con el cursor (con el teclado, el código se perdía).
const quitarRenglon = new HTMLButtonElement({ id: 'quitar-renglon' });
quitarRenglon.focus();
assert.equal(caja.escaneoExterno('7501234567891'), 'captura');
assert.deepEqual(ultima().slice(0, 2), ['Escaneado', '7501234567891']);
assert.equal(documento.activo, captura);
console.log('ok 7a: lector por puerto en el cuadro de captura -> la venta, con «3*» y reemplazando lo seleccionado');

// 7b) «Pagó con» con lo que dio el cliente: el código va al ticket (EscaneoEnPago), nunca como dinero, y el campo no cambia. También
// uno corto: lo que llega por el puerto siempre es del lector.
pagoCon.value = '200';
pagoCon.eventos = [];
pagoCon.focus();
assert.equal(caja.escaneoExterno('7501234567890'), 'cobro');
assert.deepEqual(ultima().slice(0, 2), ['EscaneoEnPago', '7501234567890']);
assert.equal(pagoCon.value, '200');
assert.deepEqual(pagoCon.eventos, []);
assert.equal(caja.escaneoExterno('45'), 'cobro');
assert.deepEqual(ultima().slice(0, 2), ['EscaneoEnPago', '45']);
console.log('ok 7b: lector por puerto en «Pagó con» -> al ticket, nunca dinero, aunque sea corto');

// 7c) «Varios» (Ins): es el código, del lector (true), aunque sea corto como una cantidad; con el cursor en un botón del panel, también.
caja.ventanaAbierta(true, true, true);
captura.value = '';
captura.focus();
assert.equal(caja.escaneoExterno('45'), 'varios');
assert.deepEqual([ultima()[0], ultima()[1], ultima()[3]], ['EnVarios', '45', true]);
const cancelarVarios = new HTMLButtonElement({ id: 'varios-cancelar' });
cancelarVarios.focus();
assert.equal(caja.escaneoExterno('7501234567890'), 'varios');
assert.deepEqual([ultima()[0], ultima()[1], ultima()[3]], ['EnVarios', '7501234567890', true]);
assert.equal(documento.activo, captura);
// El mostrador (Ctrl+M): su Enter con el código, del lector: entra al ticket, nunca como la cantidad.
caja.ventanaAbierta(true, true, false);
caja.mostradorAbierto(true);
captura.value = '';
assert.equal(caja.escaneoExterno('7501234567890'), 'mostrador');
assert.deepEqual([ultima()[0], ultima()[1], ultima()[2], ultima()[4]], ['EnMostrador', 'Enter', '7501234567890', true]);
caja.mostradorAbierto(false);
console.log('ok 7c: lector por puerto con «Varios» o el mostrador -> su panel, como código del lector');

// 7d) Un panel con su campo para códigos (el verificador, F9): se escribe donde está el cursor y se le da su Enter, que caja.js deja
// pasar a la página como el de un escaneo del teclado.
const verificador = new HTMLInputElement({ id: 'verificador', dataset: { escaneo: '' } });
verificador.labels = [{ textContent: 'Código del producto\n    ' }];
conDatos.push(verificador);
verificador.focus();
let antes = llamadas.length;
assert.equal(caja.escaneoExterno('7501234567890'), 'campo:Código del producto');
assert.equal(verificador.value, '7501234567890');
assert.deepEqual(verificador.eventos, ['input', 'change', 'keydown', 'keyup']);
assert.equal(verificador.prevenido, false);
assert.equal(llamadas.length, antes);
// Un campo donde no va un código (el motivo de una entrada de efectivo, F7): no se escribe ni se le da Enter, y se dice arriba.
const motivo = new HTMLInputElement({ id: 'motivo', value: 'cambio' });
motivo.labels = [{ textContent: 'Motivo ' }];
motivo.focus();
assert.equal(caja.escaneoExterno('7501234567890'), 'sin-lugar:Motivo');
assert.equal(motivo.value, 'cambio');
assert.deepEqual(motivo.eventos, []);
assert.match(avisoDelLector().textContent, /«7501234567890» con el cursor en «Motivo», donde no va un código/);
assert.equal(avisoDelLector().hidden, false);
assert.equal(llamadas.length, antes);
// Con el cursor fuera de los campos del panel: a su campo para códigos, si tiene uno solo a la vista.
cancelarVarios.focus();
verificador.value = '';
assert.equal(caja.escaneoExterno('7509'), 'campo:Código del producto');
assert.equal(verificador.value, '7509');
conDatos.splice(conDatos.indexOf(verificador), 1);
// Con otro panel y el cursor en el cuadro de captura: se queda escrito ahí, como con el teclado (su Enter no hace nada con un panel).
captura.value = '';
captura.focus();
assert.equal(caja.escaneoExterno('7501234567890'), 'escrito');
assert.equal(captura.value, '7501234567890');
captura.value = '';
// «Efectivo contado» del corte (corte.js): no se toma como dinero.
caja.ventanaAbierta(true);
const contado = new HTMLInputElement({ id: 'efectivo-contado', dataset: { protegido: '' }, value: '1500' });
contado.labels = [{ textContent: 'Efectivo contado' }];
contado.focus();
assert.equal(caja.escaneoExterno('7501234567890'), 'protegido:Efectivo contado');
assert.equal(contado.value, '1500');
assert.deepEqual(contado.eventos, []);
console.log('ok 7d: lector por puerto en un panel -> su campo para códigos con su Enter; en otro campo, nada y el aviso');

// 7e) Una página (Productos): con el cursor fuera de un campo, a su búsqueda a la vista (en lugar de lo que tenía), con su Enter; en
// un campo de números (data-rafaga, «Cuántos llegaron»), el siguiente producto, nunca la cantidad.
caja.ventanaAbierta(false);
caja.paginaAbierta(true);
const deOtraPagina = new HTMLInputElement({ id: 'mov-buscar', dataset: { busca: '' } });
deOtraPagina.offsetParent = null;
const busqueda = new HTMLInputElement({ id: 'busca-prod', dataset: { busca: '' }, value: 'lapiz' });
conDatos.push(deOtraPagina, busqueda);
documento.activo = null;
assert.equal(caja.escaneoExterno('7501234567890'), 'busqueda');
assert.equal(busqueda.value, '7501234567890');
assert.deepEqual(busqueda.eventos, ['input', 'change', 'keydown', 'keyup']);
assert.equal(busqueda.prevenido, false);
assert.equal(deOtraPagina.value, '');
caja.rafagasDe({ invokeMethodAsync: (...args) => { deLaPagina.push(args); return Promise.resolve(); } });
cuantos.value = '24';
cuantos.focus();
assert.equal(caja.escaneoExterno('7501234567890'), 'numeros');
assert.deepEqual(deLaPagina.at(-1), ['RafagaEnCampo', '7501234567890', 'agregar-cantidad', '24']);
assert.equal(cuantos.value, '24');
caja.rafagasDe(null);
caja.paginaAbierta(false);
console.log('ok 7e: lector por puerto en una página -> su búsqueda; en un campo de números, el siguiente producto');

// 7f) Sin la pantalla de venta (la entrada con el PIN, abrir el turno): no se usa.
caja.quitar();
assert.equal(caja.escaneoExterno('7501234567890'), 'sin-pantalla');
console.log('ok 7f: lector por puerto sin la pantalla de venta -> no se usa');

console.log('todo ok');
