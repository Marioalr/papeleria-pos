# Caja y control

El centro del proyecto: que la sucursal funcione bien y con control aunque el dueño no esté.

## Venta

- Captura con el lector (código de barras o QR) o por búsqueda: nombre, código o marca, sin importar acentos.
- Todo con teclado (teclas F para cobrar, buscar, cantidad, quitar línea…).
- Varias ventas en espera (el cliente que regresa por algo más).
- Meta: un producto escaneado aparece en pantalla en menos de 100 ms.
- **Cobro mixto:**
  - efectivo
  - tarjeta (terminal externa; se registra la referencia)
  - transferencia (queda **por verificar**, ver abajo)
  - crédito (solo clientes registrados)
  - anticipos de pedidos
- **Mayoreo por escalas** (fase 3): el precio baja solo al llegar a la cantidad (o al escanear el paquete); el
  renglón y el ticket dicen desde cuánto y cuánto se ahorró. F11 ya no cambia el precio: muestra las escalas del
  producto, o con las piezas escritas antes (como en eleventa) pone esa cantidad.
- **Redondeo al peso** del cobro en efectivo (fase 3): al peso más cercano y al menos $1; la tarjeta y la transferencia
  van exactas y las impresiones no se redondean. El total y el IVA quedan exactos; el redondeo va aparte en el ticket
  («Total a pagar») y en el corte. Lo que se regresa en efectivo también sale en pesos, y al cancelar se regresa lo
  cobrado.
- **Clientes registrados** (fase 3): los de confianza, dados de alta a mano en el módulo «Clientes» por el dueño o el
  encargado (nombre, teléfono y notas, sin RFC). Con CTRL+L se elige el del ticket por nombre o teléfono, y queda
  aunque no lleve descuento; el ticket dice su nombre y sus privilegios. Privilegios por cliente:
  - **descuento fijo:** todo el ticket lo lleva (también lo que se agregue después) y no pide autorización; si deja algo
    debajo del costo, eso sí se pide (al elegirlo o al oprimir F12). Un descuento a mano lo reemplaza en ese renglón, y al
    quitar los descuentos a mano vuelve el del cliente;
  - **mayoreo siempre:** paga desde la primera pieza el precio de la primera escala de mayoreo; las escalas más altas
    siguen al llegar a ellas. El renglón dice «Mayoreo de cliente». Si también tiene descuento, el descuento va sobre el
    precio de mayoreo;
  - **pedido sin anticipo** y **apartar sin pagar**, que se usan con los pedidos (fase 4).

  El ticket dice los privilegios que llevó la venta (también al reimprimirlo). Su ficha dice cuánto compra al mes (el
  promedio de los últimos tres meses completos, sin lo cancelado ni lo devuelto). Lo que el encargado cambia de los
  privilegios le llega al dueño por Telegram, y también cuando un cliente con privilegios cambia de nombre o de teléfono o
  vuelve a estar activo. Si se edita al cliente de un ticket abierto, el ticket se pone al día; si se cambia desde otra
  caja, al cobrar se pide volver a elegirlo. Los que eran «clientes especiales» pasaron con su descuento, marcados «Por
  revisar». Sin conexión se eligen de la copia de la caja. Una caja que todavía no se actualiza solo ve a los que tienen
  descuento y cobra sin el mayoreo del cliente.
- Ticket con QR del folio: sirve para devoluciones y para encontrar pedidos.
- Devoluciones escaneando el ticket.

## Turnos y cortes

- Apertura con fondo de caja.
- Retiros de efectivo y gastos de caja, siempre con motivo.
- **Corte ciego:** el cajero cuenta por denominación sin ver cuánto debería haber. El sistema calcula la diferencia y
  avisa al dueño.
- Corte parcial (X) y de cierre (Z).

## Autorizaciones configurables

Tabla de **acción × rol × sucursal**. Cada combinación tiene una de tres opciones:

- **Permitido**
- **Requiere autorización**
- **No permitido**

Se pueden poner umbrales. Ejemplos de reglas (todas editables):

| Acción | Ejemplo de regla |
|---|---|
| Quitar un producto ya escaneado | Permitido, queda en bitácora |
| Cancelar ticket | Requiere encargado |
| Descuento | Hasta 10 % permitido; más, requiere encargado |
| Cambiar un precio en la venta | Requiere dueño |
| Vender bajo costo | Requiere encargado |
| Devolución | Arriba de $200 requiere encargado |
| Ajuste de inventario | Requiere encargado |
| Abrir el cajón sin venta | Permitido con motivo |
| Preparar un pedido grande sin anticipo (cliente no registrado) | Requiere encargado |
| Reimprimir un documento ya impreso | Permitido con motivo |
| Verificar una transferencia | Solo encargado o dueño |
| Dar de alta y editar clientes registrados | Encargado o dueño (los privilegios que da el encargado se le avisan al dueño) |

**Cómo se autoriza:**
- con el PIN del encargado o del dueño en la misma caja, o
- a distancia por Telegram: llega la solicitud con el detalle y botones para aprobar o rechazar. Pedir además un PIN
  está por decidir; no necesita el central ([pendientes](plan.md#pendientes)).

Cada autorización guarda quién, cuándo y desde dónde. Es la idea de los permisos del invitado de Control Print,
extendida a todo el POS.

**Roles base:**
- **Dueño:** todo, en todas las sucursales.
- **Encargado:** su sucursal.
- **Cajero.**

En la caja se entra con usuario y PIN corto para cambiar de cajero rápido, con bloqueo por intentos fallidos.

## Bitácora

Queda registrado, con quién y cuándo:

- quitar productos ya escaneados
- cancelaciones y devoluciones
- cambios de precio y descuentos
- aperturas del cajón sin venta
- ajustes de inventario
- acciones a distancia (bloqueos, cortes X sorpresa, conteos pedidos) y lo que decida el piloto automático, cuando
  existan
- ventas bajo costo
- reimpresiones
- autorizaciones dadas y negadas
- cambios de configuración

## Alertas al dueño (Telegram)

Con el [central](servidor-remoto.md) (pospuesto): tres niveles; lo urgente llegaría con la foto de la cámara de esa
caja y botones, y si el dueño no lo ve, sonaría aunque el celular esté en silencio y después habría llamada. Desde el
aviso se podría actuar: bloquear lo sensible o toda la caja, pedir un corte X o un conteo ([cámaras](camaras.md)).

- Diferencia en el corte mayor a lo configurado
- Cancelaciones, descuentos y precios fuera de regla
- Caja principal apagada o sin internet en horario: el bot del POS vive en ella y no puede avisar de su propia caja;
  lo avisa la vigilancia de Healthchecks.io, a la que la caja principal se reporta cada pocos minutos
  ([plan](plan.md#para-el-piloto-con-dos-cajas))
- Con las cámaras y el contador (pospuesto): personas en la tienda después del corte Z, y mucha gente con pocos
  tickets
- Caja abierta fuera de horario
- Existencia negativa y diferencias en conteos y recepciones
- Transferencias sin verificar al cierre
- **Resumen diario** al cierre: ventas, utilidad, corte y alertas del día

## Transferencias

Los comprobantes falsos de transferencia son de las estafas más comunes en mostrador. Toda transferencia queda **por
verificar** hasta que alguien con permiso confirma que el dinero llegó a la cuenta.
