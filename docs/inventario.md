# Catálogo e inventario

## Catálogo

- **Producto y variantes:** un producto (modelo) agrupa variantes por color, tamaño o marca. Cada variante tiene su
  código, existencia y precio.
- **Departamentos** para agrupar y reportar.
- **Presentaciones:** pieza, paquete, caja, con su factor de conversión. El inventario se lleva en la unidad base.
  "Abrir paquete" es un movimiento que convierte un paquete en piezas sin perder el rastro.
- **Por metro y a granel:** se venden con fracciones (1.5 m, 0.250 kg). Lo que ya viene pesado se maneja como pieza.
- **Marca** como catálogo propio: distingue productos parecidos y sirve para buscar.
- **Caducidad:** opcional por producto (son pocos), con aviso de próximos a caducar. Sin control de lotes por ahora.
- **Campos fiscales** (clave SAT, unidad, impuestos) desde el inicio, aunque no se facture todavía.
- Tamaño estimado: 3,000 a 8,000 SKUs contando variantes (el dato real sale de eleventa). Diseño para más de 50,000.

## Códigos

- Los códigos de fábrica (EAN/UPC) se usan tal cual. Un producto puede tener varios.
- **Códigos internos** para lo que no trae: Code 128 corto, impreso en hojas adhesivas carta en la WF-6590.
- **Hoja de códigos** enmicada en el mostrador para lo diminuto (gomas, clips sueltos).
- QR donde aporta: tickets, pedidos y etiquetas de charola.

## Kardex

| Movimiento | Origen |
|---|---|
| Venta / devolución | Caja |
| Compra | Recepción de mercancía |
| Ajuste | Con motivo obligatorio y, según configuración, autorización |
| Merma | Producto dañado, impresión no vendida, pedido no recogido |
| Traspaso (salida / entrada) | Entre sucursales |
| Consumo de servicio | Receta de un servicio (micas, espirales, hojas…) |
| Apertura de paquete | Paquete → piezas |
| Conteo | Diferencia encontrada al contar |

- La existencia es la suma de movimientos. El **costo promedio** se actualiza con cada compra.
- **Nunca se bloquea una venta por falta de existencia:** se vende y se genera una alerta para revisar. Si el
  sistema dice que queda una y las dos cajas la escanean a la vez, el producto existe y la base es la que está mal; las
  dos ventas pasan y la diferencia sale en el conteo. Tampoco hay opción para bloquearla.
- **Apartados y pedidos:** la mercancía separada se descuenta del disponible, no de la existencia.
- **Se consulta** en Inventario (2026-10-17, como en eleventa): los movimientos de toda la tienda por días, tipo,
  producto, departamento y quién (el movimiento no guarda quién: lo dice su documento, la venta, la compra, el ajuste o
  el conteo), y lo que vale el inventario, la suma de los costos del kardex de lo que tiene existencia, por
  departamento.

## Compras

- Orden de compra (opcional) → **recepción ciega**: quien recibe cuenta lo que llegó sin ver lo pedido, y las
  diferencias se marcan.
- Proveedores con su último costo por producto.
- Hoy las compras llegan a la matriz. Con varias sucursales
  ([pospuesto](servidor-remoto.md#cómo-se-cumple-cada-respuesta)), dónde llega la mercancía y cómo se reparte está por
  definir, y el POS debe resistir cualquier forma: cualquier sucursal compra y recibe; la orden lleva el destino de cada
  línea, así que un proveedor puede entregar directo en cada sucursal; una compra repartida queda «de paso» en quien la
  recibe y sale como traspaso al mismo costo; el traspaso tiene estados (borrador → enviado → recibido a ciegas →
  cerrado) y un QR; un almacén futuro es una sucursal sin cajas.
- **Sugerido de compra:** mínimos y máximos calculados con el historial de ventas (incluido el de eleventa) y la
  temporada (regreso a clases).
- **Mínimo y máximo propios** (2026-10-17, como en eleventa): cada variante puede llevar los suyos en su ficha. Con su
  mínimo o menos sale en «Poca existencia» (Productos) y en «Bajos en inventario» (Inventario), con cuánto pedir para
  llegar a su máximo, contando lo que ya viene en órdenes; sin mínimo propio cuenta 3 o menos, y sin máximo, lo que pide
  el sugerido, pero al menos lo que falta para llegar a su mínimo. Los de eleventa (la tienda los usa, iguales al
  máximo) llegan con la importación y, en el piloto, con la lectura, que los pone cada vez: ahí no se cambian en el POS
  (2026-10-24, [migración](migracion-eleventa.md#qué-columna-de-eleventa-va-a-dónde)).

## Conteos

- **Cíclicos ciegos diarios:** el sistema elige de 10 a 20 productos al día, priorizando los de más valor y rotación.
  El empleado cuenta sin ver la existencia y las diferencias generan alerta.
- **Conteo completo** (al arrancar y anual): por departamento, con varias personas contando desde el celular.

## Precios

- Precio base y **precio por sucursal**: el modelo ya lo permite; la pantalla queda pospuesta con la multisucursal (la
  propuesta: un interruptor por producto, «igual en todas» o «distinto»,
  [central](servidor-remoto.md#cómo-se-cumple-cada-respuesta)).
- **Mayoreo:** por escalas de cantidad y por cliente registrado.
- **Promociones:** 3×2, porcentaje por producto o departamento, con vigencia por fechas y sucursal.
- **Kits:** precio propio; descuentan del inventario cada producto que los forma.
- Redondeo configurable.

## Listas escolares

- **Básico (fase 4):** una lista es un [pedido](pedidos.md) con productos.
- **Avanzado (fase 9):**
  - plantillas por escuela y grado
  - sustitutos (otra marca o variante)
  - existencia reservada
  - armado por cualquier empleado
  - foto de la lista convertida en pedido
