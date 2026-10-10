# Módulo de impresión

[Control Print](https://github.com/Marioalr/control-print) fue la prueba de concepto: demostró que se puede medir el
costo real de cada impresión en la WF-6590 y llevar la cola de lo que falta cobrar. Para el POS se **reescribe como
módulo propio.** Se conserva lo aprendido (formato del spool EMF, PrintTicket, lecturas SNMP de niveles y contadores,
método de calibración, atascos y cancelaciones) y se reusa código donde convenga.

## Por qué reescribirlo en lugar de conectarlo por API

- Un solo inicio de sesión, con los mismos permisos, base, instalador y actualizaciones.
- Las impresiones son líneas de venta y de pedido desde el principio, no algo que se sincroniza entre dos sistemas.
- El POS ya tiene un servidor por sucursal: el módulo solo necesita un agente en cada PC que imprime.

## Cómo encaja

- **Agente en cada PC que imprime:** vigila la cola, mide cada trabajo y manda el resultado al servicio de la sucursal.
- **Caja principal:** lee niveles y contadores de la impresora, calibra y verifica las hojas que salieron.
- **Cobro:** las impresiones y copias por cobrar aparecen en la caja y se agregan al ticket.
- **Margen real:** el costo de cada impresión entra a la utilidad del día y a la receta de los
  [servicios](servicios.md).
- **Papel:** las hojas impresas se descuentan del inventario de papel (consumo interno).
- **Copias en el panel de la impresora:** las detectadas por contador tienen que cobrarse o justificarse. Con el dueño
  ausente, esto cierra la fuga más común de una papelería con servicio de impresión.
- **Pedidos:** lo que se imprime desde un [pedido](pedidos.md) lleva el folio en el nombre del trabajo y se liga solo.
- **Repetidos:** la huella visual de cada página sale del mismo dibujo a 300 dpi que se hace para medir la tinta.
- **Cotizar:** los archivos que llegan a un pedido se cotizan antes de imprimir.

## Lo que hay que lograr

Pendientes que vienen de Control Print:

- Que los costos solo los vea quien tiene permiso, aplicado en el servidor y no solo en la pantalla.
- Cotizar PDF y detector de documentos comunes (CURP, recibo de luz).
- Impresiones desde el celular y copias hechas en el panel de la impresora.
- Varias impresoras: la matriz tiene **dos WF-6590 y una EcoTank L5590**, todas por red. Hoy el POS mide y cobra una
  sola; las tres hacen falta antes del piloto ([plan](plan.md#para-el-piloto-con-dos-cajas)).

## Transición

Control Print sigue en uso hasta que el POS lo reemplace en la matriz, durante el piloto.
