# Integración con Control Print

[Control Print](https://github.com/Marioalr/control-print) ya calcula el costo real de cada impresión en la WF-6590,
lleva la cola de trabajos por cobrar y funciona en red. El POS lo aprovecha en lugar de repetirlo.

## Etapa 1: por API

Control Print sigue funcionando igual y el POS habla con su servidor en la red de la sucursal. Su núcleo depende de
Windows (`System.Printing`, spooler), así que el núcleo del POS no lo referencia directamente.

- **Cobro:** las impresiones y copias por cobrar aparecen en la caja y se agregan al ticket. Al cobrar, se marcan como
  cobradas en Control Print con el folio del ticket.
- **Margen real:** el costo de cada impresión entra a la utilidad del día.
- **Papel:** las hojas impresas se descuentan del inventario de papel (consumo interno).
- **Copias en el panel de la impresora:** las detectadas por contador tienen que cobrarse o justificarse. Con el dueño
  ausente, esto cierra la fuga más común de una papelería con servicio de impresión.
- **Pedidos:** lo que se imprime desde un [pedido](pedidos.md) lleva el folio en el nombre del trabajo y Control Print
  lo liga solo.
- **Repetidos:** la huella visual de cada página sale del mismo dibujo a 300 dpi que Control Print ya hace para medir la
  tinta.
- **Cotizar:** los archivos que llegan a un pedido se cotizan con la función de cotizar PDF (planeada en Control Print).

## Etapa 2: un solo sistema

- Un solo inicio de sesión: los usuarios y permisos del POS.
- Control Print pasa a ser un módulo del POS (mismo repositorio o paquete compartido).

## Pendientes de Control Print que afectan al POS

- Que la API no mande costos a empleados e invitado (hoy se ocultan en la pantalla).
- Cotizar PDF (punto 8 de su plan) y detector de documentos comunes (punto 7).
- Impresiones desde el celular y copias en el panel (punto 6).
- Varias impresoras (otra WF-6590 o la L5590), sobre todo pensando en la sucursal 2.
