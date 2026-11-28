# Recibir mercancía con la factura (XML del CFDI)

Pedido del dueño (2026-11-27): un modo de recibir mercancía a partir de la factura electrónica del proveedor, reutilizando la
vista de traspasos si se puede y, si no, una propia con las mismas características (el permiso y la funcionalidad). Está en
**Inventario (F4) › Recibir con factura**, junto a «Recibir mercancía».

## Cómo se usa

1. «Cargar el XML de la factura…» (al abrir la tarjeta el cursor ya está ahí: Enter) y se elige el `.xml` que mandó el
   proveedor (el PDF no sirve: no trae el folio fiscal ni las claves). Sale la lista: lo que dice la factura junto al producto
   de aquí. **Todavía no entra nada.**
2. Lo que el POS reconoció ya viene emparejado. Lo demás dice «sin emparejar»: el cursor está en su «Emparejar»; Enter, y se
   escanea el producto (o F10 para buscarlo por nombre). Si la factura cobra cajas, se escanea el código de la caja: así cada
   unidad de la factura es una caja de 12. Al elegirlo, el cursor pasa al siguiente sin emparejar. Si se parece a un producto de
   aquí, sale «¿Es «…»?» para emparejarlo de una vez.
3. Se escribe lo que **llegó** de cada renglón (ya viene lo que dice la factura; si no llegó, 0), con una nota si hace falta.
   Enter pasa al siguiente renglón. Lo que llegó y no venía se escanea o se busca, y entra como renglón «no venía». Lo que se
   escribe se guarda solo.
4. Para terminar:
   - Si llegó lo que dice la factura, «Recibir»: pregunta, y **entra la mercancía** con el costo de la factura.
   - Si algo cambió, quien tiene «Recibir con factura: modificar lo que llegó» la recibe así («Recibir con 3 cambios»). Quien no
     lo tiene la deja «marcada para revisión», y la abre y la recibe quien sí.
   - «Descartar la factura» la quita sin recibirla (se puede cargar otra vez).

La próxima factura de ese proveedor llega con lo emparejado aquí ya emparejado.

## ¿Por qué no es la misma pantalla que los traspasos?

Se reutilizó **la forma** (el mismo flujo, los mismos renglones «Dice / Llegó / Nota», «no venía», marcar para revisión, la pareja
de permisos «Puede/No», los mismos estilos), pero no el componente ni sus datos:

- Los traspasos por archivo son **provisionales**: su pantalla, su registro y su archivo se borran cuando llegue el servidor
  ([qué se quita](traspasos.md#para-quitarlo-o-cambiarlo-cuando-exista-el-servidor)). Recibir con factura se queda.
- Un traspaso no lleva costos ni proveedor; una factura sí: el costo, el último costo con el proveedor, el margen mínimo y lo que
  se reclama. Eso ya lo hacían las **compras** desde octubre (leer el CFDI, emparejar por la clave del proveedor y aprenderla,
  la misma factura no dos veces, cerrar al kardex): por dentro, una factura recibida es una compra (`Compra.ConFactura`) y se
  cierra con el mismo código que «Compras y notas».

## Qué entiende de la factura

Se probó con la factura de ejemplo del dueño (un mayorista de papelería: 27 conceptos, CFDI 4.0 con adenda del proveedor) y, en
las pruebas, con una ficticia de la misma forma (los datos reales no se copian al repositorio).

| De la factura | Qué hace el POS |
|---|---|
| Emisor (RFC y nombre) | El proveedor: por su RFC; si no está, uno sin RFC con el mismo nombre (se le guarda el RFC), y si tampoco, **se da de alta** con la razón social de la factura (se avisa; el teléfono y las notas, en Proveedores) |
| Serie y folio, folio fiscal (UUID del timbre) | El número de la factura y la llave para no cargarla ni recibirla dos veces |
| Cada concepto: `NoIdentificacion` | La clave del proveedor: con ella se empareja (la que ya se emparejó, el código de barras si lo es, o la guardada en el producto de ese proveedor) |
| `Cantidad`, `Unidad` | Lo que dice que llegó, en su unidad; «Pieza» que en realidad es una caja («CJA/12», «C/50», «/6») se empareja con la presentación de la caja |
| `Importe`, `Descuento`, traslados (IVA, IEPS) | El costo del renglón: importe − descuento + impuestos trasladados, como lo que se paga (igual que el costo de eleventa) |
| `ClaveUnidad` E48/ACT o `ClaveProdServ` de servicios (70 a 95) | **Servicio, no mercancía** (en la de ejemplo, un cargo de $145.01 con unidad «Unidad de servicio»): no se toma, y se avisa con lo que cobra |
| `Moneda` y `TipoCambio` | En dólares se pasa a pesos (ya estaba) |
| `TipoDeComprobante` E | Nota de crédito: no se toma (ya estaba) |
| Total contra la suma de los conceptos | Si no cuadra (retenciones, un descuento general), se avisa: «revisa los costos» |
| Lo que no usa | La clave del SAT de cada producto, el valor unitario (sale del importe), forma y método de pago, el receptor, la adenda del proveedor (con letra, almacén, correos). El XML se guarda tal cual con la compra |

Nada de la factura de ejemplo truena: la lee completa (26 conceptos de mercancía y el servicio), con el BOM del principio y la
adenda, y la suma cuadra con el total.

## Lo que se decidió (2026-11-27; lo marcado «por revisar» se decidió sin el dueño)

| Pregunta | Qué quedó | Estado |
|---|---|---|
| ¿La vista de traspasos o una propia? | Una propia con la misma forma (arriba el porqué) | Por revisar |
| Dónde va | Inventario › «Recibir con factura», una tarjeta después de «Recibir mercancía», con la lista de las últimas 60 y su estado; las ya recibidas también salen en «Compras y notas» | Por revisar |
| El permiso | Dos nuevos, en «Usuarios y permisos» › Inventario, los dos «Puede» o «No», como los de traspasos. **«Recibir con la factura (XML)»**: cargar, escribir lo que llegó, emparejar, marcar para revisión, descartar y recibirla como dice. **«Recibir con factura: modificar lo que llegó»**: recibirla distinta. Al llegar, el primero lo tiene quien tiene «Agregar inventario» y el segundo quien tiene «Compras: la nota y los costos» (en «Puede»). «Recibir mercancía a ciegas» no se usó: es a ciegas (la factura aquí es la lista) y tiene «Con autorización» | Por revisar: a quién le llegan |
| Qué cuenta como cambio | Otra cantidad, algo que no venía, otro producto que el que reconoció el POS, y **emparejar a mano** (como en los traspasos: un emparejamiento equivocado mete el producto equivocado, y se recuerda para la siguiente factura) | Por revisar |
| Sin emparejar | Si llegó algo, no se recibe hasta emparejarlo; con 0 en lo que llegó, sí (no entra nada de ese renglón) | Hecho |
| Productos nuevos | No se dan de alta desde aquí (en el piloto, las altas son en eleventa): se da de alta en Productos y se empareja. «Compras y notas» sí los da de alta desde el documento | Por revisar |
| Cuándo entra y a qué costo | **Al recibirla.** Lo que llegó, con el costo de la factura (con descuento e IVA); lo que faltó no entra ni se paga (queda en la bitácora y en el aviso de Telegram para reclamarlo); lo que no venía entra a su último costo | Hecho |
| El flete y otros servicios | No se toman ni se reparten en el costo de los productos; se avisan con lo que cobran | Por revisar |
| El precio de venta | No cambia solo: si con el costo nuevo queda debajo del margen mínimo, el aviso de Telegram sugiere el precio, como en las compras | Hecho |
| Quién ve los costos | Los importes y el total de la factura, solo quien tiene «Compras: la nota y los costos». Quien solo recibe ve qué y cuánto | Por revisar |
| Que no se aplique dos veces | Por el folio fiscal: la misma factura otra vez abre la que ya estaba (y lo dice); al recibirla se revisa otra vez que no haya entrado en otra compra. Una que ya se cargó en una compra a ciegas no se carga aquí | Hecho |
| **En el piloto con eleventa** | Como los traspasos: **es solo el documento**. Se carga, se empareja y se recibe igual, quedan el último costo con el proveedor y lo emparejado, pero **no mueve existencias** (la lectura de eleventa las regresaría) y la pantalla lo dice: lo que llegó se captura en eleventa. Al dejar eleventa, mueve las existencias sin cambiar nada | **Por revisar** (la más importante) |
| Sin la caja principal | No se hace, como «Agregar inventario» | Hecho |

Límites: hasta 500 renglones por factura y hasta 10 MB de XML.

## Lo que no se pudo comprobar

- **La ventana de la caja de Windows.** Se probó con las pruebas automáticas, sin navegador, manejando la pantalla como con el
  teclado (el cursor en cada paso). «Abrir» de Windows con un `.xml` no se ha visto en una PC con Windows.
- **Con el lector de verdad:** el escaneo se probó tecleando el código y Enter.
- **Con el catálogo de la tienda:** cuántos conceptos de una factura real llegan emparejados la primera vez (las claves del
  mayorista son suyas, de 8 cifras: casi todo se empareja a mano la primera vez) y si el «¿Es…?» acierta.
