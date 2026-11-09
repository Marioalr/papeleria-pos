// corte.js con un campo hecho a mano (2026-11-09): «Efectivo contado» del corte y el fondo al abrir el turno, donde no está caja.js. La
// ráfaga del lector de teclado (de 4 caracteres o más, a 50 ms o menos y con su Enter) no se queda como dinero y su Enter no llega a
// .NET; lo que teclea una persona, aunque sea rápida, sí. Lo corre CajaJsTests con node. Uso: node prueba-corte.mjs /ruta/a/corte.mjs
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';

class HTMLInputElement {
    constructor(value = '') {
        this.value = value;
        this.dataset = {};
        this.escuchas = [];
        // Los avisos que se le mandaron (input) y si el Enter llegó a .NET (lo que escucha después, como el @onkeydown).
        this.eventos = [];
        this.enters = 0;
    }
    addEventListener(tipo, fn) { if (tipo === 'keydown') this.escuchas.push(fn); }
    dispatchEvent(e) { this.eventos.push(e.type); return true; }
}
Object.assign(globalThis, { HTMLInputElement, Event: class { constructor(t) { this.type = t; } } });

const corte = await import(pathToFileURL(process.argv[2]).href);

// Una tecla como la da la página: primero lo de corte.js, luego .NET (si nadie la detuvo).
function tecla(campo, key, timeStamp) {
    let detenida = false;
    let prevenida = false;
    const e = { key, timeStamp, ctrlKey: false, altKey: false, metaKey: false,
        preventDefault() { prevenida = true; }, stopImmediatePropagation() { detenida = true; } };
    for (const fn of campo.escuchas) {
        fn(e);
        if (detenida) break;
    }
    if (!detenida && key === 'Enter') campo.enters++;
    return prevenida;
}
function teclear(campo, texto, desde, paso) {
    let hora = desde;
    for (const c of texto) {
        tecla(campo, c, hora);
        campo.value += c;
        hora += paso;
    }
    return hora - paso;
}

// 1) El fondo: un código del lector (cada 5 ms) se quita y su Enter no abre el turno; a la pantalla se le dice qué se leyó.
const llamadas = [];
const pantalla = { invokeMethodAsync: (...args) => { llamadas.push(args); return Promise.resolve(); } };
const fondo = new HTMLInputElement();
corte.proteger(fondo, pantalla);
assert.equal(fondo.dataset.protegido, '');
let t = teclear(fondo, '7501234567890', 1000, 5);
assert.equal(tecla(fondo, 'Enter', t + 5), true);
assert.equal(fondo.value, '');
assert.deepEqual(fondo.eventos, ['input']);
assert.equal(fondo.enters, 0);
assert.deepEqual(llamadas, [['LectorEnElDinero', '7501234567890']]);
// «Efectivo contado» del corte, sin pantalla: igual, sin decir nada.
const contado = new HTMLInputElement('1500');
corte.proteger(contado);
t = teclear(contado, '7501234567890', 1500, 5);
assert.equal(tecla(contado, 'Enter', t + 5), true);
assert.equal(contado.value, '1500');
assert.equal(contado.enters, 0);
console.log('ok 1: un código en el fondo o en lo contado no se escribe ni abre o cierra el turno');

// 2) A mano, con su punto, abre con su Enter; una persona muy rápida (51 ms entre teclas), también.
fondo.value = '';
t = teclear(fondo, '500.50', 2000, 120);
assert.equal(tecla(fondo, 'Enter', t + 100), false);
assert.equal(fondo.value, '500.50');
assert.equal(fondo.enters, 1);
fondo.value = '';
t = teclear(fondo, '1500', 3000, 51);
assert.equal(tecla(fondo, 'Enter', t + 51), false);
assert.equal(fondo.value, '1500');
console.log('ok 2: el fondo escrito a mano abre con Enter');

// 3) Lo escrito a mano antes de la ráfaga se queda; con menos de 4 caracteres no se distingue de una persona.
fondo.value = '';
t = teclear(fondo, '200', 4000, 150);
t = teclear(fondo, '7501234567890', t + 900, 5);
assert.equal(tecla(fondo, 'Enter', t + 5), true);
assert.equal(fondo.value, '200');
fondo.value = '';
const enters = fondo.enters;
t = teclear(fondo, '200', 5000, 5);
assert.equal(tecla(fondo, 'Enter', t + 5), false);
assert.equal(fondo.enters, enters + 1);
assert.equal(llamadas.length, 2);
console.log('ok 3: lo de antes se queda; tres cifras rápidas son un número');

// 4) Un campo se protege una vez: otra vez no le pone otra escucha.
corte.proteger(fondo);
assert.equal(fondo.escuchas.length, 1);
console.log('ok 4: una sola vez');

console.log('todo ok');
