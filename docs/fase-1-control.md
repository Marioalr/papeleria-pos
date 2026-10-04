# Fase 1, entrega 2 — Control

> **Revisado.** Cómo se construyen las [autorizaciones configurables y la bitácora](caja-y-control.md) sobre la caja
> de la entrega 1.

**Avance:** la entrega está completa. Parte 1: reglas (con las iniciales de abajo), autorización de un solo uso que
el servicio vuelve a revisar, bitácora (quitar productos, artículo común, entradas y salidas, reimpresiones, corte Z,
configuración y autorizaciones negadas), límite de efectivo con aviso de retiro y comprobante impreso de salidas.
Parte 2: cancelar y devolver escaneando el QR del ticket (o desde ventas del día), con comprobante para firma;
cambio de precio con Ctrl+E (lo autoriza el dueño); verificar transferencias; la ventana de autorización en la caja;
el editor de reglas del dueño y la consulta de la bitácora (en Configuración); devoluciones y cancelaciones en el
corte.

## Qué incluye

- **Reglas por acción, rol y sucursal:** permitido, requiere autorización o no permitido, con umbral.
- **Autorización con PIN** del encargado o del dueño en la misma caja. La autorización a distancia por Telegram
  llega en la entrega 4, con el mismo mecanismo.
- **Bitácora** de todo lo sensible, incluidas las autorizaciones negadas.
- **Cancelar y devolver** escaneando el QR del ticket.
- **Cambios de precio** en la venta, controlados por las reglas. Los **descuentos** quedan por hacer hasta integrar el
  módulo de impresión (entrega 5).
- **Límite de efectivo en caja:** al llegar a un monto configurable, la caja pide hacer un retiro.
- **Comprobante impreso** de cada salida de efectivo.
- **Verificar transferencias.**

## Reglas iniciales

Punto de partida, todas editables. "Encargado" quiere decir que lo autoriza el encargado o el dueño con su PIN.

| Acción | Cajero | Encargado | Umbral propuesto |
|---|---|---|---|
| Quitar un producto ya escaneado | Permitido | Permitido | — (siempre a la bitácora) |
| Artículo varios / común | Permitido | Permitido | — (siempre a la bitácora) |
| Descuento | Por hacer | Por hacer | Se define con el módulo de impresión (entrega 5) |
| Cambiar un precio en la venta | Requiere dueño | Requiere dueño | — |
| Vender debajo del costo | Encargado | Permitido | — |
| Cancelar un ticket cobrado | Encargado | Permitido | — |
| Devolución | Permitido hasta el umbral; más, encargado | Permitido | $200; solo con ticket y hasta 7 días |
| Salida de efectivo (F8) | Permitido | Permitido | — (se guarda y se imprime comprobante) |
| Entrada de efectivo (F7) | Permitido | Permitido | — |
| Abrir el cajón sin venta | Permitido con motivo | Permitido con motivo | — |
| Reimprimir un ticket | Permitido con motivo | Permitido | — |
| Verificar una transferencia | No permitido | Permitido | — |
| Corte de cierre (Z) | No permitido | Permitido | — |
| Configurar el ticket | No permitido | Permitido | — |
| Editar las reglas | No permitido | No permitido | Solo el dueño |

El dueño puede todo. Quedan para su fase: ajuste de inventario (fase 2), mayoreo (fase 3) y pedidos sin anticipo
(fase 4).

## Cómo se autoriza


1. El cajero intenta la acción (por ejemplo, cancelar un ticket ya cobrado).
2. Si la regla dice "requiere autorización", aparece una ventana con el detalle: qué, cuánto, en qué ticket.
3. El encargado escribe su usuario y PIN en la misma caja. La autorización vale **una sola vez y para esa acción**,
   no deja la caja abierta.
4. Hecha o negada, queda en la bitácora: quién la pidió, quién la autorizó, cuándo y desde qué caja.

**El servicio es el que decide, no la pantalla.** La caja pregunta para mostrar la ventana a tiempo, pero cada
operación vuelve a revisar la regla en el servicio. Si requiere autorización, la operación tiene que traer una
autorización vigente (de un solo uso, que vence en 2 minutos; la del cambio de precio, en 30, porque se gasta al
cobrar); si no, se rechaza. Así no se puede saltar el control con otra pantalla o llamando a la API directo.

Lo que pasa antes de cobrar (quitar un producto, agregar un artículo común) se reporta a la bitácora en ese momento.
Al cobrar, el servicio vuelve a revisar los precios de cada línea contra las reglas.

## Efectivo en caja

- **Límite de efectivo:** el dueño configura por sucursal un monto (por ejemplo $3,000). Cuando el efectivo esperado
  en la caja lo alcanza, la caja **pide hacer un retiro**: un aviso que no se quita hasta que se retira, sin detener
  las ventas.
- **Salidas (F8):** el cajero las hace solo, sin autorización. Quedan guardadas con monto, motivo, quién y cuándo, y
  **se imprime un comprobante** en la impresora de tickets.

## Cancelar y devolver

- Se escanea el QR del ticket (o se escribe el folio) y aparece la venta.
- **Cancelar:** el ticket completo, mientras su turno siga abierto. El dinero sale de la caja y la mercancía regresa
  al inventario al mismo costo con que salió.
- **Devolver:** algunos artículos o todos. **Solo con ticket y hasta 7 días** después de la venta, con reembolso en
  efectivo de la caja actual. La mercancía regresa al inventario y un artículo no se puede devolver dos veces.
- Las dos cuentan en el corte: el efectivo esperado baja por lo que se reembolsó.
- Lo que no regresa a la venta (llegó dañado) se marca como merma.

## Verificar transferencias

Lista de transferencias por verificar, con folio, monto y referencia. El encargado confirma que el dinero llegó a la
cuenta o la rechaza. Las rechazadas quedan marcadas en el corte y, en la entrega 4, avisan al dueño por Telegram.

## Datos nuevos

| Tabla | Campos principales |
|---|---|
| **ReglaPermiso** | Acción, rol, sucursal (vacía = todas), nivel (permitido, requiere autorización, no permitido), umbral |
| **Autorizacion** | Acción, detalle, monto, quién la pidió, quién la dio, caja, vence, usada en |
| **Bitacora** | Sucursal, caja, usuario, acción, detalle, monto, venta, autorizó, resultado (hecho, autorizado, negado), fecha. Solo se agregan registros |
| **Devolucion** | Venta, turno donde se reembolsa, quién, motivo, total, autorización |
| **DevolucionLinea** | Devolución, línea de la venta, cantidad, importe, costo, regresa al inventario o merma |

En la venta: estado cancelada con quién y cuándo; en cada línea, el precio original y la autorización si se cambió.
En la configuración de la sucursal: el límite de efectivo en caja.

## Decisiones de la revisión

| Pregunta | Respuesta | Efecto |
|---|---|---|
| Artículo varios / común | Libre | Sin autorización; cada uso va a la bitácora |
| Salidas de efectivo | El cajero retira solo | Se guardan y se imprime comprobante; sin umbral |
| Límite de efectivo | Pedir retiro al llegar a un monto configurable | Aviso en la caja hasta que se retira |
| Devoluciones | Solo con ticket, hasta 7 días, en efectivo | Así se valida al devolver |
| Editar las reglas | Solo el dueño | Con el PIN del dueño |
| Descuentos | Por hacer hasta integrar el módulo de impresión | Fuera de la entrega 2; se definen en la entrega 5 |
