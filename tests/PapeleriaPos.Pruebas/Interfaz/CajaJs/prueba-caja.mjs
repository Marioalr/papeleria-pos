// caja.js con un DOM mínimo hecho a mano, sin dependencias (revisión del PR #133): lo que manda a la pantalla con cada tecla.
// Lo corre CajaJsTests con node. Uso: node prueba-caja.mjs /ruta/a/una/copia/de/caja.mjs
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';

class HTMLElement {
    constructor({ id = null, dataset = {}, padres = [] } = {}) {
        this.id = id;
        this.dataset = dataset;
        // Los selectores de atributo que cumple el elemento o alguno de sus padres (solo lo que usa caja.js).
        this.padres = padres;
    }
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
    dispatchEvent() { }
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
    addEventListener(tipo, fn) { escuchas[tipo] = fn; },
    removeEventListener() { },
    getElementById(id) { return this.elementos[id] ?? null; },
    querySelector() { return null; },
    querySelectorAll() { return []; },
};
Object.assign(globalThis, {
    HTMLElement, HTMLInputElement, HTMLButtonElement, HTMLSelectElement, HTMLAnchorElement, HTMLTextAreaElement,
    document: documento, Event: class { constructor(t) { this.type = t; } },
    requestAnimationFrame: f => f(), getComputedStyle: () => ({ gridTemplateColumns: '' }),
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

console.log('todo ok');
