# Fase 1, entrega 3 — Varias cajas

> **Propuesta para revisar.** Cómo pasa la caja de la entrega 1 a una sucursal con varias cajas en red, y qué hace
> una caja cuando no encuentra al servidor. Parte de la [arquitectura](arquitectura.md).

## Qué incluye

- **El servicio como servicio de Windows** en la caja principal: arranca con la PC, aunque nadie haya iniciado
  sesión, y Windows lo reinicia si falla.
- **Las demás cajas por la red**, hablando con la caja principal. La caja principal también vende: es una caja más
  que además guarda la base.
- **Alta de cajas:** una caja nueva se da de alta una sola vez con el PIN del dueño. Sin alta, la API no le responde.
- **Contingencia:** si la caja principal no responde, cada caja sigue vendiendo con su copia del catálogo y guarda
  las ventas en una bandeja de salida. Al volver la conexión las sube solas.
- **Respaldo diario** de la base en la caja principal, con varios días de historia.

## Cómo queda la sucursal

```
 ┌──────────────────────────────────────────┐
 │ Caja principal (PC que siempre prende)   │
 │  Servicio de Windows: API + base SQLite  │◄──── LAN (IP fija en el router) ────┐
 │  Caja 1 (ventana de Windows)             │                                     │
 └──────────────────────────────────────────┘                          ┌──────────┴──────────┐
                                                                       │ Caja 2              │
                                                                       │ ventana de Windows  │
                                                                       │ copia del catálogo  │
                                                                       │ bandeja de salida   │
                                                                       └─────────────────────┘
```

- La caja encuentra al servicio por la **IP fija** reservada en el router; si cambia, lo busca sola en la red (un
  aviso por difusión que el servicio contesta).
- Cada caja imprime en su propia impresora de tickets (la de la red o la conectada a esa PC).
- Todas las cajas de una sucursal llevan la misma versión del programa (las actualizaciones llegan después, con el
  central).

## Alta de una caja

1. Se instala el programa en la PC nueva y se abre.
2. Encuentra al servicio y pide el alta: el dueño escribe su usuario y PIN y elige el nombre y la serie de folios
   (Caja 2, serie B).
3. El servicio le entrega una **llave** propia. La caja la guarda cifrada con la protección de Windows y la manda en
   cada llamada.
4. El dueño puede dar de baja una caja (la llave deja de servir) desde Configuración.

Así un celular o una laptop conectados al Wi-Fi de la tienda no pueden vender, cancelar ni leer nada aunque conozcan
la dirección. La API hoy está abierta; en esta entrega se cierra.

## Contingencia: caja sin servidor

La caja revisa al servicio cada pocos segundos (latido). Si deja de contestar, la pantalla lo avisa con una franja
de color y la caja pasa a contingencia.

**Qué sigue funcionando:**

- Escanear, buscar, varios tickets, cobro mixto, ticket impreso y cajón.
- **Folios:** la serie es de la caja (A, B…), así que la caja continúa su propio consecutivo sin chocar con las
  demás. Al volver, el servicio acepta esos folios.
- **Precios y catálogo:** la última copia que recibió. La caja la actualiza en cuanto cambia algo (cada producto y
  precio lleva un número de versión, ya existe desde la fase 0).
- Entradas y salidas de efectivo, con su comprobante.
- El turno abierto sigue igual.

**Qué espera a que vuelva la conexión** (propuesta, ver preguntas):

- Cancelar y devolver: necesitan la venta original y el inventario al día.
- Verificar transferencias, el corte Z y la configuración.
- Las autorizaciones con PIN: la propuesta es permitirlas con una copia local de los PIN (cifrados, nunca en texto)
  para no frenar la venta, y marcarlas en la bitácora como "en contingencia".

**Al volver:** la caja sube la bandeja en orden. Cada venta lleva su ID, así que reenviarla no la duplica. El
inventario no choca porque son movimientos. Si algo no se puede aplicar (por ejemplo, un producto dado de baja
mientras tanto), queda marcado para que el encargado lo revise; no se pierde.

**Límites:** en contingencia una caja no ve las ventas de las demás, y las existencias pueden estar desfasadas. Cada
venta en contingencia queda marcada para que se vea en el corte y en la bitácora.

## Respaldos

- Copia diaria de la base en la caja principal (y otra al hacer el corte Z), con los últimos 30 días.
- Se hace con la copia en línea de SQLite: no detiene la venta.
- La copia fuera de la tienda (en la nube) llega con el central (fase 6). Mientras tanto, opcional: copiar también a
  una memoria USB o a otra carpeta.

## Cómo se arma

| Pieza | Qué cambia |
|---|---|
| `PapeleriaPos.Servicio` | Corre como servicio de Windows, escucha en la red, responde al descubrimiento, revisa la llave de cada caja, recibe la bandeja y hace los respaldos |
| `PapeleriaPos.Caja` | Configuración inicial (encontrar el servicio y pedir el alta), latido, copia local del catálogo en su propia SQLite, bandeja de salida |
| `PapeleriaPos.Interfaz` | Franja de contingencia; las pantallas usan un cliente que decide si va a la API o a la copia local |
| `PapeleriaPos.Herramientas` | Instalar y quitar el servicio de Windows y la regla del firewall |
| Datos nuevos | Caja: llave (solo su huella), alta y baja. Venta y movimiento: "en contingencia". Respaldo: fecha y archivo |

**Pruebas:** dos cajas en la misma PC (dos ventanas con su propia copia local) contra un servicio; se apaga el
servicio a media venta, se sigue vendiendo en las dos, se prende y se revisa que todo llegue una sola vez, con
folios, inventario y corte correctos.

## Preguntas

1. **Cajas:** ¿cuántas cajas hay hoy en la matriz y cuál PC sería la principal? Conviene la que siempre prende
   primero y tiene no-break.
2. **Contingencia:** ¿se permiten las autorizaciones con PIN sin servidor (propuesta: sí, marcadas), o todo lo que
   pide autorización espera a que vuelva la conexión?
3. **Corte sin servidor:** si la caja principal no prende al cierre, ¿se permite un corte Z en contingencia (y se
   cuadra después) o el turno se queda abierto hasta que vuelva?
4. **Respaldos:** ¿30 días de historia está bien? ¿Hay una memoria USB o un disco donde copiar también?
5. **Arranque de la PC:** ¿las cajas inician sesión de Windows solas y abren el POS (modo kiosco), o el cajero inicia
   sesión y lo abre?
