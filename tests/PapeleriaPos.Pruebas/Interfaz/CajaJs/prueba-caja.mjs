// caja.js con un DOM mínimo hecho a mano, sin dependencias (revisión del PR #133): lo que manda a la pantalla con cada tecla, y lo
// que hace con cada código del lector por puerto (escaneoExterno, piloto alfa).
// Lo corre CajaJsTests con node. Uso: node prueba-caja.mjs /ruta/a/una/copia/de/caja.mjs
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';

class HTMLElement {
    constructor({ id = null, dataset = {}, padres = [], clases = [], padre = null, fijo = false } = {}) {
        this.id = id;
        this.dataset = dataset;
        // Los selectores de atributo que cumple el elemento o alguno de sus padres (solo lo que usa caja.js).
        this.padres = padres;
        // Sus clases (.fondo-ventana, .vn-panel) y dónde está (padre): la ventana o el panel que lo tiene.
        this.clases = clases;
        this.padre = padre;
        // A la vista (null: oculto, como en una página que no se ve). Lo fijo en la pantalla (una ventana) no tiene offsetParent.
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
// «Pagó con»: del cobro (data-cobro, en el panel del total), su Enter cobra (data-cobrar).
const pagoCon = new HTMLInputElement({ id: 'pago-nuevo', dataset: { pago: '', cobrar: '' }, padres: ['[data-cobro]'] });
const montoAbono = new HTMLInputElement({ id: 'abono-monto', dataset: { cobro: '' } });
documento.elementos['pago-nuevo'] = pagoCon;

// 1) En la venta, sin ventana: F4 en «Pagó con» es la nota; en el cuadro de captura, Inventario. (Cada tecla lleva al final la hora
// de la PC en que se oprimió: 9a.)
caja.ventanaAbierta(false);
tecla(pagoCon, 'F4', 1000);
assert.deepEqual(ultima().slice(0, 3), ['Tecla', 'F4-cobro', null]);
tecla(captura, 'F4', 1100);
assert.deepEqual(ultima().slice(0, 3), ['Tecla', 'F4', '']);
console.log('ok 1: F4 en «Pagó con» -> F4-cobro; en el cuadro -> F4');

// 2) Con el panel del abono abierto (Ctrl+B), F4 en su monto (también data-cobro) es Inventario, no la nota.
caja.ventanaAbierta(true, true, false);
tecla(montoAbono, 'F4', 2000);
assert.deepEqual(ultima().slice(0, 3), ['Tecla', 'F4', null]);
console.log('ok 2: F4 en el monto del abono (panel abierto) -> F4');

// 3) «Varios» abierto: «5» escrito a mano y F11 por error. caja.js manda F11 con «5» (la pantalla lo regresa al cuadro si el
// panel no se deja).
caja.ventanaAbierta(true, true, true);
captura.value = '';
escribir(captura, '5', 3000, 0);
tecla(captura, 'F11', 3200);
assert.deepEqual(ultima().slice(0, 3), ['Tecla', 'F11', '5']);
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
function coincide(elemento, selector) {
    return selector.split(',').map(x => x.trim()).some(uno => {
        const dato = /^\[data-([a-z-]+)\]$/.exec(uno);
        if (dato) return elemento.dataset?.[dato[1]] !== undefined;
        const clase = /^\.([a-z-]+)$/.exec(uno);
        return clase !== null && (elemento.clases ?? []).includes(clase[1]);
    });
}
documento.querySelectorAll = selector => conDatos.filter(e => coincide(e, selector));
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
// El panel de la derecha (.vn-panel), donde están sus botones y sus campos.
const panel = new HTMLElement({ id: 'panel', clases: ['vn-panel'] });
const cancelarVarios = new HTMLButtonElement({ id: 'varios-cancelar', padre: panel });
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
const verificador = new HTMLInputElement({ id: 'verificador', dataset: { escaneo: '' }, padre: panel });
verificador.labels = [{ textContent: 'Código del producto\n    ' }];
conDatos.push(panel, verificador);
verificador.focus();
let antes = llamadas.length;
assert.equal(caja.escaneoExterno('7501234567890'), 'campo:Código del producto');
assert.equal(verificador.value, '7501234567890');
assert.deepEqual(verificador.eventos, ['input', 'change', 'keydown', 'keyup']);
assert.equal(verificador.prevenido, false);
assert.equal(llamadas.length, antes);
// Un campo donde no va un código (el motivo de una entrada de efectivo, F7): no se escribe ni se le da Enter, y se dice arriba.
const motivo = new HTMLInputElement({ id: 'motivo', value: 'cambio', padre: panel });
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
conDatos.splice(conDatos.indexOf(panel), 1);
// Con otro panel y el cursor en el cuadro de captura: a la venta, que decide (nunca se queda escrito: el siguiente se le pegaba, y F11 lo
// tomaba como cantidad; auditoría, 2026-11-04).
captura.value = '';
captura.focus();
assert.equal(caja.escaneoExterno('7501234567890'), 'captura');
assert.equal(captura.value, '');
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '7501234567890', true]);
// El teclado, igual: el Enter del cuadro con un panel abierto va a la venta, en ráfaga o a mano.
captura.value = '75012';
tecla(captura, 'Enter', 40000);
assert.equal(captura.value, '');
assert.deepEqual(ultima().slice(0, 2), ['Escaneado', '75012']);
// Con una ventana encima (no un panel), el Enter no hace nada y se queda escrito.
caja.ventanaAbierta(true);
captura.value = '';
captura.focus();
assert.equal(caja.escaneoExterno('7501234567890'), 'escrito');
captura.value = '';
caja.ventanaAbierta(true, true, false);
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

// 8) Revisión del PR #142.
caja.registrar(pantalla);
conDatos.length = 0;

// 8a) Con su número, después de darle el código a la venta, caja.js le dice que ya lo recibió (LectorRecibido), en orden: el cobro,
// F6 y «5*» esperan lo que el puerto ya leyó hasta eso. Sin número (una ventana de antes), sin acuse.
caja.ventanaAbierta(false);
caja.paginaAbierta(false);
captura.value = '';
captura.focus();
assert.equal(caja.escaneoExterno('7501234567890', 7), 'captura');
assert.deepEqual(llamadas.at(-2).slice(0, 2), ['Escaneado', '7501234567890']);
assert.deepEqual(llamadas.at(-1), ['LectorRecibido', 7]);
pagoCon.focus();
assert.equal(caja.escaneoExterno('7501234567890', 8), 'cobro');
assert.deepEqual(llamadas.at(-2).slice(0, 2), ['EscaneoEnPago', '7501234567890']);
assert.deepEqual(llamadas.at(-1), ['LectorRecibido', 8]);
captura.focus();
let cuantas = llamadas.length;
caja.escaneoExterno('7509');
assert.equal(llamadas.length, cuantas + 1);
console.log('ok 8a: lector por puerto -> la venta y luego LectorRecibido con su número');

// 8b) Una ventana encima de una página (la Configuración sobre Inventario › contar un departamento), con el cursor en un botón de
// la Configuración: el código no va al campo de la página de atrás, ni con el cursor en él; a uno de la ventana, sí.
const pagina = new HTMLElement({ id: 'pagina-inventario' });
const delConteo = new HTMLInputElement({ id: 'buscar-conteo', dataset: { escaneo: '' }, padre: pagina });
const configuracion = new HTMLElement({ id: 'configuracion', clases: ['fondo-ventana'], fijo: true });
const acercaDe = new HTMLButtonElement({ id: 'cfg-acerca', padre: configuracion });
conDatos.push(delConteo, configuracion);
caja.paginaAbierta(true);
caja.ventanaAbierta(true);
acercaDe.focus();
assert.equal(caja.escaneoExterno('7501234567890'), 'sin-lugar');
assert.equal(delConteo.value, '');
assert.deepEqual(delConteo.eventos, []);
assert.equal(documento.activo, acercaDe);
assert.match(avisoDelLector().textContent, /aquí no hay dónde escribirlo/);
delConteo.focus();
assert.match(caja.escaneoExterno('7501234567890'), /^sin-lugar/);
assert.equal(delConteo.value, '');
const deLaVentana = new HTMLInputElement({ id: 'buscar', dataset: { escaneo: '' }, padre: configuracion });
conDatos.push(deLaVentana);
acercaDe.focus();
assert.equal(caja.escaneoExterno('7501234567890'), 'campo');
assert.equal(deLaVentana.value, '7501234567890');
assert.equal(delConteo.value, '');
caja.ventanaAbierta(false);
conDatos.length = 0;
console.log('ok 8b: con una ventana encima, solo a su campo para códigos, nunca al de la página de atrás');

// 8c) Sin pegarse a lo que tenía: en la ficha (varios códigos separados por espacio) se agrega con su espacio; en el «Código de
// barras» de la nota (uno, que se guarda al cambiar) reemplaza; con una cantidad delante («12*», al recibir mercancía) se queda.
const codigos = new HTMLInputElement({ id: 'codigos', dataset: { escaneo: 'varios' }, value: '7501234567890' });
codigos.focus();
caja.escaneoExterno('7509876543210');
assert.equal(codigos.value, '7501234567890 7509876543210');
const vacio = new HTMLInputElement({ id: 'codigos-2', dataset: { escaneo: 'varios' }, value: '  ' });
vacio.focus();
caja.escaneoExterno('7509876543210');
assert.equal(vacio.value, '7509876543210');
const codigoDeLaNota = new HTMLInputElement({ id: 'nota-codigo', dataset: { escaneo: '' }, value: '7501234567890' });
codigoDeLaNota.focus();
caja.escaneoExterno('7509876543210');
assert.equal(codigoDeLaNota.value, '7509876543210');
assert.deepEqual(codigoDeLaNota.eventos, ['input', 'change', 'keydown', 'keyup']);
const recibir = new HTMLInputElement({ id: 'codigo-compra', dataset: { busca: '' }, value: '12*' });
recibir.focus();
caja.escaneoExterno('7509876543210');
assert.equal(recibir.value, '12*7509876543210');
caja.paginaAbierta(false);
console.log('ok 8c: el código no se pega: se agrega con espacio, reemplaza o va después de «12*»');

// 8d) El lector de teclado en un número que se guarda con Enter (data-numero: el monto de F7, lo contado): lo tecleado se quita y su
// Enter no guarda; se dice arriba. A mano es un número.
caja.ventanaAbierta(true, true);
const monto = new HTMLInputElement({ id: 'movimiento-monto', dataset: { numero: '' } });
monto.labels = [{ textContent: 'Monto ' }];
monto.focus();
t = teclear(monto, '7501234567890', 30000, 5);
assert.equal(tecla(monto, 'Enter', t + 5), true);
assert.equal(monto.value, '');
assert.deepEqual(monto.eventos, ['input']);
assert.match(avisoDelLector().textContent, /«7501234567890» con el cursor en «Monto»: un código no es una cantidad/);
monto.value = '';
t = teclear(monto, '1500', 31000, 120);
assert.equal(tecla(monto, 'Enter', t + 100), false);
assert.equal(monto.value, '1500');
// Por el puerto, en un número no se escribe nada.
monto.value = '';
monto.eventos = [];
assert.equal(caja.escaneoExterno('7501234567890'), 'sin-lugar:Monto');
assert.equal(monto.value, '');
assert.deepEqual(monto.eventos, []);
caja.ventanaAbierta(false);
console.log('ok 8d: un código en un número que se guarda con Enter no se escribe ni se guarda');

// 8e) Los demás números que se guardan con Enter (2026-11-09): el precio de Ctrl+E, «Cuánto» de Ctrl+D y el precio especial de una
// promoción, igual que el monto de F7. A mano siguen con su punto o su «%», y lo escrito antes de la ráfaga se queda.
caja.ventanaAbierta(true);
const precioNuevo = new HTMLInputElement({ id: 'precio-nuevo', dataset: { numero: '' } });
precioNuevo.labels = [{ textContent: 'Precio nuevo (con IVA) ' }];
precioNuevo.focus();
t = teclear(precioNuevo, '7501234567890', 32000, 4);
assert.equal(tecla(precioNuevo, 'Enter', t + 4), true);
assert.equal(precioNuevo.value, '');
assert.match(avisoDelLector().textContent, /«7501234567890» con el cursor en «Precio nuevo \(con IVA\)»/);
precioNuevo.value = '';
t = teclear(precioNuevo, '12.50', 33000, 110);
assert.equal(tecla(precioNuevo, 'Enter', t + 90), false);
assert.equal(precioNuevo.value, '12.50');
const cuanto = new HTMLInputElement({ id: 'descuento-valor', dataset: { numero: '' } });
cuanto.focus();
t = teclear(cuanto, '15%', 34000, 100);
assert.equal(tecla(cuanto, 'Enter', t + 100), false);
assert.equal(cuanto.value, '15%');
caja.ventanaAbierta(false);
caja.paginaAbierta(true);
const precioEspecial = new HTMLInputElement({ id: 'promo-precio-0', dataset: { numero: '' }, value: '6.50' });
precioEspecial.getAttribute = nombre => nombre === 'aria-label' ? 'Precio especial de Lápiz' : null;
precioEspecial.focus();
t = teclear(precioEspecial, 'ABC-1234', 35000, 5);
assert.equal(tecla(precioEspecial, 'Enter', t + 5), true);
assert.equal(precioEspecial.value, '6.50');
assert.deepEqual(precioEspecial.eventos, ['input']);
assert.match(avisoDelLector().textContent, /con el cursor en «Precio especial de Lápiz»/);
caja.paginaAbierta(false);
console.log('ok 8e: el precio, el descuento y la promoción: el código no se guarda; a mano, sí');

// 9) Segunda revisión del PR #142.

// 9a) Las teclas que van a la pantalla llevan la hora de la PC en que se oprimieron (la del reloj del sistema, como la del lector por
// puerto), aunque la página las atienda después: el cobro, F6 y «5*» esperan solo lo que el puerto empezó a leer antes de ellas.
caja.ventanaAbierta(false);
caja.paginaAbierta(false);
captura.value = '';
captura.focus();
let oprimida = Date.now() - 40;
// La página la atiende 40 ms después de oprimirla (estaba ocupada): cuenta cuando se oprimió.
tecla(captura, 'F6', performance.now() - 40);
assert.deepEqual(ultima().slice(0, 3), ['Tecla', 'F6', '']);
assert.ok(Math.abs(ultima()[3] - oprimida) < 25, `F6 con la hora ${ultima()[3]}, no ${oprimida}`);
captura.value = '5*';
oprimida = Date.now();
tecla(captura, 'Enter', performance.now());
assert.deepEqual(ultima().slice(0, 2), ['Escaneado', '5*']);
assert.ok(Math.abs(ultima()[3] - oprimida) < 25, `«5*» con la hora ${ultima()[3]}, no ${oprimida}`);
// Lo que llega por el puerto no es una tecla: sin esa hora.
captura.value = '';
caja.escaneoExterno('7501234567890');
assert.deepEqual(ultima().slice(0, 2), ['Escaneado', '7501234567890']);
assert.equal(ultima()[3], null);
console.log('ok 9a: las teclas llevan la hora de la PC en que se oprimieron');

// 9b) Enter en «Pagó con» (data-cobrar) cobra con su hora, como F12 ahí: va a la pantalla y no al campo. El de la nota del ticket
// (también del cobro, sin data-cobrar) la deja, como siempre; con un panel abierto, tampoco lo manda caja.js.
pagoCon.value = '200';
pagoCon.focus();
oprimida = Date.now();
assert.equal(tecla(pagoCon, 'Enter', performance.now()), true);
assert.deepEqual(ultima().slice(0, 3), ['Tecla', 'Enter-cobro', null]);
assert.ok(Math.abs(ultima()[3] - oprimida) < 25);
const nota = new HTMLInputElement({ id: 'nota-ticket', dataset: { pago: '' }, padres: ['[data-cobro]'] });
nota.focus();
cuantas = llamadas.length;
assert.equal(tecla(nota, 'Enter', performance.now()), false);
assert.equal(llamadas.length, cuantas);
caja.ventanaAbierta(true, true);
pagoCon.focus();
assert.equal(tecla(pagoCon, 'Enter', performance.now()), false);
assert.equal(llamadas.length, cuantas);
caja.ventanaAbierta(false);
console.log('ok 9b: Enter en «Pagó con» cobra con su hora; el de la nota no');

// 9c) Con una ventana encima, el cobro y los números de la página de atrás tampoco: con la Configuración encima de Inventario ›
// Agregar, el código no va a «Cuántos llegaron» como el siguiente producto; con el corte encima de la venta, no va al ticket desde
// «Pagó con» (EscaneoEnPago cerraba el corte).
caja.rafagasDe({ invokeMethodAsync: (...args) => { deLaPagina.push(args); return Promise.resolve(); } });
const deInventario = new HTMLElement({ id: 'pagina-inventario' });
const cuantosDeAtras = new HTMLInputElement({ id: 'agregar-cantidad', dataset: { rafaga: '' }, padre: deInventario, value: '24' });
const configuracionEncima = new HTMLElement({ id: 'configuracion', clases: ['fondo-ventana'], fijo: true });
conDatos.push(cuantosDeAtras, configuracionEncima);
caja.paginaAbierta(true);
caja.ventanaAbierta(true);
cuantosDeAtras.focus();
let aLaPagina = deLaPagina.length;
assert.match(caja.escaneoExterno('7501234567890', 41), /^sin-lugar/);
assert.equal(deLaPagina.length, aLaPagina);
assert.equal(cuantosDeAtras.value, '24');
caja.paginaAbierta(false);
conDatos.length = 0;
const corte = new HTMLElement({ id: 'corte', clases: ['fondo-ventana'], fijo: true });
conDatos.push(corte);
pagoCon.focus();
cuantas = llamadas.length;
assert.match(caja.escaneoExterno('7501234567890', 42), /^sin-lugar/);
assert.deepEqual(llamadas.slice(cuantas), [['LectorRecibido', 42]]);
// Sin ventana encima, «Pagó con» sí: al ticket.
caja.ventanaAbierta(false);
assert.equal(caja.escaneoExterno('7501234567890'), 'cobro');
conDatos.length = 0;
caja.rafagasDe(null);
console.log('ok 9c: con una ventana encima, el código no va al cobro ni a los números de atrás');

// 9d) Los números que se guardan con Enter (data-numero) se guardan a mano: una persona muy rápida (51 ms entre teclas) sí; en
// ráfaga (50 ms o menos), no. En lo contado de una página (sin ventana) la ráfaga se quita y lo escrito antes se queda.
const montoRapido = new HTMLInputElement({ id: 'movimiento-monto', dataset: { numero: '' } });
montoRapido.focus();
t = teclear(montoRapido, '1500', 40000, 51);
assert.equal(tecla(montoRapido, 'Enter', t + 51), false);
assert.equal(montoRapido.value, '1500');
montoRapido.value = '';
t = teclear(montoRapido, '1500', 41000, 50);
assert.equal(tecla(montoRapido, 'Enter', t + 50), true);
assert.equal(montoRapido.value, '');
caja.paginaAbierta(true);
const contadoDeLaPagina = new HTMLInputElement({ id: 'contar-0', dataset: { numero: '' }, value: '3' });
contadoDeLaPagina.focus();
t = teclear(contadoDeLaPagina, '7501234567890', 42000, 5);
assert.equal(tecla(contadoDeLaPagina, 'Enter', t + 5), true);
assert.equal(contadoDeLaPagina.value, '3');
caja.paginaAbierta(false);
console.log('ok 9d: un número a mano se guarda; la ráfaga del lector, no');

// 10) Los comandos en el cuadro de captura («/servicios», «/curp»; 2026-10-31): el Enter le dice a la venta si lo mandó el lector, y un
// escaneo (la ráfaga del lector, de RAFAGA_MINIMO caracteres o más, o el puerto) nunca empieza un comando.
caja.ventanaAbierta(false);
caja.paginaAbierta(false);
caja.mostradorAbierto(false);
documento.querySelector = selector => selector === '[data-captura]' ? captura : null;
captura.focus();

// 10a) «/curp» tecleado a mano y Enter: un comando (no es del lector).
captura.value = '';
t = teclear(captura, '/curp', 50000, 120);
tecla(captura, 'Enter', t + 150);
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '/curp', false]);
// Una persona rápida que suelta el Enter enseguida de la última letra, tampoco: no tecleó cuatro seguidas en ráfaga.
captura.value = '';
t = teclear(captura, '/curp', 51000, 90);
tecla(captura, 'Enter', t + 20);
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '/curp', false]);

// 10b) Un código del lector de teclado (cada 5 ms y su Enter): del lector.
captura.value = '';
t = teclear(captura, '7501234567890', 52000, 5);
tecla(captura, 'Enter', t + 5);
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '7501234567890', true]);

// 10c) Un código que empieza con «/» (un Code 39): va como código del lector, y la venta no lo toma como comando.
captura.value = '';
t = teclear(captura, '/CURP123', 53000, 5);
tecla(captura, 'Enter', t + 5);
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '/CURP123', true]);

// 10d) «/» a mano (la lista de las acciones abierta) y luego un escaneo: va solo lo leído, como código; la lista se cierra.
captura.value = '';
t = teclear(captura, '/', 54000, 0);
t = teclear(captura, '7501234567890', t + 600, 5);
tecla(captura, 'Enter', t + 5);
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '7501234567890', true]);
// «/cu» a mano y luego el escaneo: lo mismo.
captura.value = '';
t = teclear(captura, '/cu', 55000, 120);
t = teclear(captura, '7501234567890', t + 400, 5);
tecla(captura, 'Enter', t + 5);
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '7501234567890', true]);

// 10e) «3*» a mano y luego el escaneo: tres de ese código, como siempre (ahora dice que es del lector).
captura.value = '';
t = teclear(captura, '3*', 56000, 150);
t = teclear(captura, '7501234567890', t + 500, 5);
tecla(captura, 'Enter', t + 5);
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '3*7501234567890', true]);
// Borrar a mano corta la ráfaga: lo que se teclea después ya no es del lector.
captura.value = '';
t = teclear(captura, '7501', 57000, 5);
tecla(captura, 'Backspace', t + 5);
captura.value = '750';
tecla(captura, 'Enter', t + 10);
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '750', false]);

// 10f) El lector por puerto: siempre del lector. Con «/» escrito a mano antes, va solo el código; con «3*», tres.
captura.value = '/';
captura.selectionStart = captura.selectionEnd = 1;
assert.equal(caja.escaneoExterno('7501234567890'), 'captura');
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '7501234567890', true]);
captura.value = '/curp';
captura.selectionStart = captura.selectionEnd = 5;
caja.escaneoExterno('7501234567890');
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '7501234567890', true]);
captura.value = '3*';
captura.selectionStart = captura.selectionEnd = 2;
caja.escaneoExterno('7501234567890');
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '3*7501234567890', true]);
captura.value = '';
captura.selectionStart = captura.selectionEnd = 0;
caja.escaneoExterno('/CURP123');
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '/CURP123', true]);
console.log('ok 10: «/curp» a mano es un comando; lo del lector (teclado o puerto) nunca, y con «/» escrito antes va solo el código');

// 10b) Esc con el recuadro del chip de eleventa abierto lo cierra a él y no toca el cuadro; Av Pág y Re Pág solo van a la pantalla con la
// lista de la búsqueda abierta.
let clicsDelPuente = 0;
documento.querySelector = selector => selector === '.vn-puente .vn-fondo-menu' ? { click() { clicsDelPuente++; } } : null;
captura.value = 'lo escrito';
cuantas = llamadas.length;
assert.equal(tecla(captura, 'Escape', 950000), true);
assert.deepEqual([clicsDelPuente, llamadas.length, captura.value], [1, cuantas, 'lo escrito']);
documento.querySelector = () => null;
assert.equal(tecla(captura, 'PageDown', 951000), false);
assert.equal(llamadas.length, cuantas);
documento.querySelector = selector => selector === '.vn-resultados' ? {} : null;
assert.equal(tecla(captura, 'PageDown', 952000), true);
assert.deepEqual([ultima()[0], ultima()[1], ultima()[2]], ['Tecla', 'PageDown', 'lo escrito']);
documento.querySelector = () => null;
captura.value = '';
console.log('ok 10b: Esc cierra el recuadro del puente; Av Pág solo con la lista abierta');

// 11) El lugar de la página oficial de un trámite (la ventana de Windows): caja.js le dice a la venta dónde está el hueco del ticket, y
// al dejar de observarlo ya no.
const hueco = { isConnected: true, getBoundingClientRect: () => ({ left: 16, top: 118, width: 930, height: 560 }) };
documento.querySelector = selector => selector === '#tramite-lugar' ? hueco : selector === '[data-captura]' ? captura : null;
caja.observarLugar('#tramite-lugar');
assert.deepEqual(ultima(), ['LugarDelTramite', 16, 118, 930, 560]);
caja.dejarDeObservarLugar();
// Sin el hueco (se fue a otra página o se cerró el panel), mide cero: la página oficial se oculta.
documento.querySelector = () => null;
caja.observarLugar('#tramite-lugar');
assert.deepEqual(ultima(), ['LugarDelTramite', 0, 0, 0, 0]);
caja.quitar();
cuantas = llamadas.length;
await new Promise(listo => setTimeoutDeNode(listo, 600));
assert.equal(llamadas.length, cuantas, 'Al quitar la pantalla, caja.js deja de observar el lugar.');
console.log('ok 11: el lugar del ticket para la página oficial de un trámite, y se deja de observar');

// 12) El cursor fuera de un campo, en la venta sin ventana (auditoría, 2026-11-04): lo que se escribe va al cuadro de captura, y el Enter
// del lector ya no oprime el botón donde quedó el cursor. + y − se quedan para el renglón; con una ventana, nada de esto.
caja.registrar(pantalla);
caja.ventanaAbierta(false);
caja.paginaAbierta(false);
const elCuadro = new HTMLInputElement({ id: 'codigo', dataset: { captura: '' } });
documento.querySelector = selector => selector === '[data-captura]' ? elCuadro : null;
const unBoton = new HTMLButtonElement({ id: 'rapido-1' });
documento.activo = unBoton;
let t12 = 90000;
// La primera tecla cae en el botón: se pasa al cuadro. Las demás del lector ya caen en él.
assert.equal(tecla(unBoton, '7', t12), true);
assert.equal(documento.activo, elCuadro);
for (const c of '5012345') {
    t12 += 5;
    assert.equal(tecla(elCuadro, c, t12), false);
    elCuadro.value += c;
}
t12 += 5;
assert.equal(elCuadro.value, '75012345');
tecla(elCuadro, 'Enter', t12);
assert.deepEqual(ultima().slice(0, 2), ['Escaneado', '75012345']);
assert.equal(ultima()[4], true, 'es del lector');
documento.activo = unBoton;
elCuadro.value = '';
tecla(unBoton, '+', t12 + 500);
assert.equal(elCuadro.value, '');
assert.deepEqual(ultima().slice(0, 2), ['Tecla', '+']);
caja.ventanaAbierta(true);
tecla(unBoton, '7', t12 + 900);
assert.equal(elCuadro.value, '');
caja.ventanaAbierta(false);
console.log('ok 12: con el cursor fuera de un campo, lo que se escribe va al cuadro');

// 13) Lo que se buscaba a mano no se le pega al código del lector; «3*» sí se queda, y un número delante también (puede ser su primera cifra).
documento.activo = elCuadro;
let t13 = 95000;
elCuadro.value = 'cuad';
for (const c of '75012345') {
    tecla(elCuadro, c, t13);
    elCuadro.value += c;
    t13 += 5;
}
tecla(elCuadro, 'Enter', t13);
assert.deepEqual([ultima()[0], ultima()[1], ultima()[4]], ['Escaneado', '75012345', true]);
elCuadro.value = '3*';
t13 += 500;
for (const c of '75012345') {
    tecla(elCuadro, c, t13);
    elCuadro.value += c;
    t13 += 5;
}
tecla(elCuadro, 'Enter', t13);
assert.deepEqual(ultima().slice(0, 2), ['Escaneado', '3*75012345']);
elCuadro.value = '7';
t13 += 500;
for (const c of '5012345') {
    tecla(elCuadro, c, t13);
    elCuadro.value += c;
    t13 += 5;
}
tecla(elCuadro, 'Enter', t13);
assert.deepEqual(ultima().slice(0, 2), ['Escaneado', '75012345']);
console.log('ok 13: el escaneo no se pega a lo que se buscaba');

// 14) Lo escrito en el cuadro con el cursor en otro lado (un clic en un hueco, o en «Pagó con»): F12 y el Enter del cobro lo llevan, para
// que la venta no cobre sin ello (revisión de la rama junta, 2026-11-06). Sin nada escrito, como siempre; con una ventana, tampoco.
const enPagoCon = new HTMLInputElement({ id: 'pago-nuevo', dataset: { pago: '', cobrar: '' }, padres: ['[data-cobro]'] });
documento.elementos['pago-nuevo'] = enPagoCon;
elCuadro.value = '7501234567890';
documento.activo = unBoton;
assert.equal(tecla(unBoton, 'F12', 99000), true);
assert.deepEqual(ultima().slice(0, 3), ['Tecla', 'F12-pago', '7501234567890']);
assert.equal(documento.activo, enPagoCon);
assert.equal(tecla(enPagoCon, 'F12', 99100), true);
assert.deepEqual(ultima().slice(0, 3), ['Tecla', 'F12', '7501234567890']);
assert.equal(tecla(enPagoCon, 'Enter', 99200), true);
assert.deepEqual(ultima().slice(0, 3), ['Tecla', 'Enter-cobro', '7501234567890']);
elCuadro.value = '   ';
assert.equal(tecla(enPagoCon, 'Enter', 99300), true);
assert.deepEqual(ultima().slice(0, 3), ['Tecla', 'Enter-cobro', null]);
assert.equal(tecla(enPagoCon, 'F12', 99400), true);
assert.deepEqual(ultima().slice(0, 3), ['Tecla', 'F12', null]);
elCuadro.value = '7501234567890';
assert.equal(caja.leerCaptura(), '7501234567890');
console.log('ok 14: F12 y Enter del cobro llevan lo escrito en el cuadro');

console.log('todo ok');
