# Fase 1, entrega 2 — Control

> **Propuesta para revisar.** Cómo se construyen las [autorizaciones configurables y la bitácora](caja-y-control.md)
> sobre la caja de la entrega 1. Las preguntas están al final.

## Qué incluye

- **Reglas por acción, rol y sucursal:** permitido, requiere autorización o no permitido, con umbral.
- **Autorización con PIN** del encargado o del dueño en la misma caja. La autorización a distancia por Telegram
  llega en la entrega 4, con el mismo mecanismo.
- **Bitácora** de todo lo sensible, incluidas las autorizaciones negadas.
- **Cancelar y devolver** escaneando el QR del ticket.
- **Descuentos y cambios de precio** en la venta, controlados por las reglas.
- **Verificar transferencias.**

## Reglas iniciales

Punto de partida, todas editables. "Encargado" quiere decir que lo autoriza el encargado o el dueño con su PIN.

| Acción | Cajero | Encargado | Umbral propuesto |
|---|---|---|---|
| Quitar un producto ya escaneado | Permitido | Permitido | — (siempre a la bitácora) |
| Artículo varios / común | Permitido hasta el umbral; más, encargado | Permitido | $50 por artículo ([pregunta 1](#preguntas)) |
| Descuento | Permitido hasta el umbral; más, encargado | Permitido | 10 % |
| Cambiar un precio en la venta | Requiere dueño | Requiere dueño | — |
| Vender debajo del costo | Encargado | Permitido | — |
| Cancelar un ticket cobrado | Encargado | Permitido | — |
| Devolución | Permitido hasta el umbral; más, encargado | Permitido | $200 |
| Salida de efectivo (F8) | Permitido hasta el umbral; más, encargado | Permitido | $500 ([pregunta 2](#preguntas)) |
| Entrada de efectivo (F7) | Permitido | Permitido | — |
| Abrir el cajón sin venta | Permitido con motivo | Permitido con motivo | — |
| Reimprimir un ticket | Permitido con motivo | Permitido | — |
| Verificar una transferencia | No permitido | Permitido | — |
| Corte de cierre (Z) | No permitido | Permitido | — |
| Configurar el ticket | No permitido | Permitido | — |
| Editar las reglas | No permitido | No permitido | Solo el dueño ([pregunta 4](#preguntas)) |

El dueño puede todo. Quedan para su fase: ajuste de inventario (fase 2), mayoreo (fase 3) y pedidos sin anticipo
(fase 4).

## Cómo se autoriza

1. El cajero intenta la acción (por ejemplo, un descuento del 15 %).
2. Si la regla dice "requiere autorización", aparece una ventana con el detalle: qué, cuánto, en qué ticket.
3. El encargado escribe su usuario y PIN en la misma caja. La autorización vale **una sola vez y para esa acción**,
   no deja la caja abierta.
4. Hecha o negada, queda en la bitácora: quién la pidió, quién la autorizó, cuándo y desde qué caja.

**El servicio es el que decide, no la pantalla.** La caja pregunta para mostrar la ventana a tiempo, pero cada
operación vuelve a revisar la regla en el servicio. Si requiere autorización, la operación tiene que traer una
autorización vigente (de un solo uso, que vence en 2 minutos); si no, se rechaza. Así no se puede saltar el
control con otra pantalla o llamando a la API directo.

Lo que pasa antes de cobrar (quitar un producto, un descuento) se reporta a la bitácora en ese momento. Al cobrar, el
servicio vuelve a revisar los descuentos y precios de cada línea contra las reglas.

## Cancelar y devolver

- Se escanea el QR del ticket (o se escribe el folio) y aparece la venta.
- **Cancelar:** el ticket completo, mientras su turno siga abierto. El dinero sale de la caja y la mercancía regresa
  al inventario al mismo costo con que salió.
- **Devolver:** algunos artículos o todos, de cualquier turno. Se reembolsa en efectivo de la caja actual y la
  mercancía regresa al inventario. Un artículo no se puede devolver dos veces.
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

En la venta: estado cancelada con quién y cuándo; en cada línea, el precio original, el descuento y la autorización
que lo permitió.

## Preguntas

1. **Artículo varios / común:** ¿lo dejamos libre hasta $50 por artículo y más con encargado, o que siempre pida
   encargado? Es la forma más fácil de vender sin que se descuente inventario.
2. **Salidas de efectivo:** ¿a partir de cuánto pide encargado? La propuesta es $500.
3. **Devoluciones:** ¿hay una política (plazo, solo con ticket)? La propuesta es solo con ticket, hasta 7 días, y
   reembolso en efectivo.
4. **Editar las reglas:** ¿solo el dueño, o también el encargado?
5. **Descuentos:** ¿se usan hoy? ¿Por porcentaje, por monto o los dos?
