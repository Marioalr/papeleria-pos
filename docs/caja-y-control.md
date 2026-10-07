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
- **Redondeo al peso** del cobro en efectivo (fase 3): al peso más cercano y al menos $1; la tarjeta y la transferencia
  van exactas y las impresiones no se redondean. El total y el IVA quedan exactos; el redondeo va aparte en el ticket
  («Total a pagar») y en el corte. Lo que se regresa en efectivo también sale en pesos, y al cancelar se regresa lo
  cobrado.
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

**Cómo se autoriza:**
- con el PIN del encargado o del dueño en la misma caja, o
- a distancia por Telegram: llega la solicitud con el detalle y botones para aprobar o rechazar.

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
- ventas bajo costo
- reimpresiones
- autorizaciones dadas y negadas
- cambios de configuración

## Alertas al dueño (Telegram)

- Diferencia en el corte mayor a lo configurado
- Cancelaciones, descuentos y precios fuera de regla
- Sucursal sin conexión o caja principal apagada (latido cada minuto)
- Caja abierta fuera de horario
- Existencia negativa y diferencias en conteos y recepciones
- Transferencias sin verificar al cierre
- **Resumen diario** al cierre: ventas, utilidad, corte y alertas del día

## Transferencias

Los comprobantes falsos de transferencia son de las estafas más comunes en mostrador. Toda transferencia queda **por
verificar** hasta que alguien con permiso confirma que el dinero llegó a la cuenta.
