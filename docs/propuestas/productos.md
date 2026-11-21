# Productos (F3): dónde va cada cosa

Revisión de la página Productos (lo que se ve en `main` 4c1f197 más el trabajo sin fusionar de la ficha). Solo se toca dónde
va cada botón, filtro y vista, y cómo se abre la ficha. **Lo de adentro de la ficha y de «Nuevo producto»
(`FichaProducto.razor`) queda fuera:** lo está rehaciendo otro agente.

Los renglones citados son de `src/PapeleriaPos.Interfaz/Componentes/Productos.razor` mientras no se diga otro archivo.
La maqueta (`productos-maqueta.html`, junto a este archivo) enseña lo de hoy y lo propuesto con el botón «Hoy / Propuesta».

## En corto

- **Hoy:** casi todo lo que no es la lista está abajo, debajo de la lista. «Nuevo producto» y «Productos en Excel» se ven;
  los otros siete están en «Más». Ahí van juntas cosas de todos los días, de temporada y de la mudanza de eleventa. Esas
  siete abren una vista a la derecha, y solo Esc avisa cómo salir.
- **Propuesta:** una **barra de Productos arriba, a todo lo ancho**, como la fila de botones de Productos (F3) en
  eleventa. Lleva los lugares de la página como pestañas (**Productos · Cambiar precios · Tarjetas de precio ·
  Promociones · Temporadas · Departamentos · Excel**), con «Nuevo producto» a la derecha. Lo de la mudanza va en un solo
  menú, «De eleventa». Abajo de la lista solo queda cuántos hay y «Ver 50 más». Cada vista tiene su «← Producto».
- No se quita nada: todo lo de hoy tiene su lugar nuevo ([tabla](#de-dónde-a-dónde)). Los permisos no cambian. Las
  pruebas que buscan por id siguen funcionando porque los ids se quedan. Cambian cuatro textos de pruebas y la guía de
  la prueba.

## Lo que hay hoy

| # | Elemento | Dónde está | Permiso | Qué tan seguido (estimado) |
|---|---|---|---|---|
| 1 | Búsqueda «Nombre, marca o código» | Arriba de la lista (L27) | Página: `Ve(EditarCatalogo)` (PantallaVenta.razor:27) | Cada vez |
| 2 | Filtros Todos · Por completar *n* · Poca existencia · Margen bajo | Debajo de la búsqueda (L29-41) | Margen bajo: `Puede(VerUtilidad)` (L36) | Diario: «Por completar» (lo que dio de alta un cajero) |
| 3 | Departamento (lista) y «Con inactivos» | Debajo de los filtros (L42-51) | — | Seguido / raro |
| 4 | Lista con lo más vendido primero, «Ver 50 más» | Centro y pie de la lista (L53-121) | — | Cada vez |
| 5 | «Nuevo producto» | **Abajo de la lista**, a la izquierda (L124-127) | `Puede(EditarCatalogo)` | Varias veces por semana |
| 6 | «Productos en Excel» | Abajo de la lista, a la vista (L128-129) | Para todos los de la página; importar pide `Puede` (ExcelEnProductos.razor:35) | Raro (cargar mínimos o departamentos) |
| 7 | «Más · *n* por reimprimir» (un `<details>`) | Abajo de la lista (L130-131) | — | — |
| 8 | Mayoreo por revisar | Más, el primero (L135) | `Puede` | Una vez (mudanza) |
| 9 | Cambiar precios | Más (L136) | `Puede` | Cuando sube un proveedor (semanal o mensual) |
| 10 | Temporadas | Más (L137) | `Puede` | Unas semanas al año |
| 11 | Tarjetas de precio · *n* por reimprimir | Más (L139-140) | `Puede` | Después de cada cambio de precio |
| 12 | Promociones | Más (L141) | `Puede` | Regreso a clases y alguna más |
| 13 | Departamentos | Más (L144) | Ver: todos; cambiar: `Puede` (Departamentos.razor:213) | Raro |
| 14 | Limpieza del catálogo | Más, el último (L145) | Todos | Una vez (mudanza) |
| 15 | Detalle: nombre, marca, departamento, código, avisos | Derecha (L957-1019) | — | Cada vez |
| 16 | «Etiqueta» (a la lista de Inventario › Etiquetas) y «Ir a Etiquetas» | Cabecera del detalle (L991-996, L1001-1010) | `Ve(EditarCatalogo)` | Seguido |
| 17 | «Editar» / «Ver la ficha» | Cabecera del detalle (L997-999) | `Puede` cambia el texto | Diario |
| 18 | Variantes, precio, existencia, costo, margen | Detalle (L1020-1070) | Costo: `CostearCompra` o `VerUtilidad`; margen: `VerUtilidad` | Cada vez |
| 19 | Últimos movimientos y «el kardex completo está en la ficha» | Detalle (L1077-1103) | — | Seguido |
| 20 | La ficha (editar o nueva) | Derecha, en lugar del detalle (L155-191) | — | Diario |
| 21 | Teclas: F3, ↑ ↓, Enter, Esc, Ctrl+Enter en la ficha | L533-568, L414-457; barra de abajo en PantallaVenta.razor:838-846 | — | Cada vez |

## Lo que confunde, con su renglón

1. **Lo principal está abajo.** «Nuevo producto» está al pie de la lista (L124-127), debajo de 50 renglones. En eleventa
   está arriba, en la fila de botones de F3. Quien busca dónde dar de alta mira arriba.
2. **«Más» mezcla tres cosas que no tienen que ver** (L130-147):
   - lo de todos los días o casi: tarjetas por reimprimir y cambiar precios;
   - lo de temporada: temporadas y promociones;
   - lo de una sola vez, la mudanza: mayoreo por revisar y limpieza del catálogo. Por eso «Mayoreo por revisar», que es
     de la mudanza, sale primero.

   Además, «Departamentos» va ahí aunque en eleventa es un botón propio.
3. **Lo raro se ve y lo frecuente se esconde.** «Productos en Excel» se ve siempre (L128), y se usa para cargas de vez en
   cuando. En cambio, «Cambiar precios» (el «Actualizar varios» de eleventa) y «Tarjetas de precio» están escondidos en
   «Más».
4. **«Más» se abre solo y le quita renglones a la lista.** Su `open` sigue a la vista que esté abierta (L130). En caja.css
   (`.vn-lista-mas[open] { flex-basis: 100% }`, ~L1522), abierto ocupa todo un renglón de botones más, justo lo que el
   comentario de L122 quería evitar. Se ve como un botón más; nada dice que despliega algo.
5. **El número de tarjetas por reimprimir sale dos veces** («Más · 3 por reimprimir» y «Tarjetas de precio · 3 por
   reimprimir», L131 y L140). Con «Más» cerrado no se sabe de qué son.
6. **No se ve en qué vista se está ni cómo salir.** Son 10 vistas (enum, L271) en el mismo lado derecho. La única señal es
   el botón con `on` dentro de «Más», que puede estar cerrado. Cada cabecera repite «Esc regresa al producto» (L193,
   L223; PreciosDeVarios.razor:18, TemporadaEnProductos.razor:23, ExcelEnProductos.razor:17, Departamentos.razor:13,
   TarjetasDePrecio.razor:14), pero **no hay botón para regresar con el ratón**. Inventario sí lo tiene: «← Inventario»
   (Inventario.razor:24).
7. **La lista cambia de comportamiento sin avisarlo bien.** Con «Cambiar precios» o al armar una promoción, un toque marca
   en lugar de abrir (`Marcando`, L356). La única señal es la casilla de cada renglón. Desde abajo no se ve que se está en
   «Cambiar precios».
8. **Nombres que un cajero o el encargado no entienden:**
   - «Mayoreo por revisar» (L135): ¿qué mayoreo? Es el F11 de eleventa sin paquete.
   - «Limpieza del catálogo» (L145) abre «Lo que se decidió…» y habla de `aplicar-depuracion` (L235), un comando de
     programador. La columna dice «No (se repite a mano)» (L241).
   - «Poca existencia» (L35) es lo mismo que «Bajos en inventario» de Inventario (Paginas.cs:257), que así se llama
     también en eleventa. Su propio `title` lo dice (L34).
   - «Con inactivos» (L50) no dice si los agrega o los deja solos.
   - «Etiqueta» (L994) y «Tarjetas de precio» son dos cosas impresas distintas: la etiqueta de código va en Inventario y
     la tarjeta en Productos. El nombre no ayuda a separarlas.
9. **Entradas repetidas o partidas:**
   - Las etiquetas se arman en Productos («Etiqueta», L993) y se imprimen en Inventario («Ir a Etiquetas», L1007); las
     tarjetas, en Productos.
   - «Modificar las temporadas» está en Productos › Temporadas (TemporadaEnProductos.razor:27) y en Configuración ›
     Mostrador y temporadas.
   - El kardex está en tres lugares: los últimos 8 en el detalle, «Ver kardex» en la ficha (la nota de L1102 lo manda
     allá) e Inventario › Movimientos.
   - Precios de varios se cambian en «Cambiar precios» y en «Productos en Excel».

   Repetir no está mal mientras los nombres digan lo mismo.
10. **Los permisos no siguen una regla** (L124-145):
    - Promociones, Temporadas y Tarjetas se esconden sin `Puede(EditarCatalogo)` (L133), aunque esas vistas ya saben
      enseñarse solo para consulta (PromocionesEnProductos.razor:157 y 366; TemporadaEnProductos.razor:432 y 742).
    - Departamentos, Excel y Limpieza se enseñan a todos los de la página.
    - «Etiqueta» va con `Ve`, «Nuevo producto» con `Puede`.

    Quien tiene «Productos, precios y etiquetas» *con autorización* abre «Más» y solo encuentra Departamentos y Limpieza.
11. **La barra de teclas de abajo dice «Enter abrir»** (PantallaVenta.razor:842), pero Enter solo abre si se llegó con ↑ ↓.
    Si no, vuelve a buscar (L554-566). «Nuevo producto» no tiene tecla.
12. **Dos estilos en las vistas de la mudanza:** RevisarMayoreo usa `boton chico` y la tabla `resultados` (RevisarMayoreo.razor:43-44),
    y las Decisiones usan `resultados` (L236). Las demás usan `vn-boton` y `vn-tabla`. Se nota que son de otra época.

## Cómo lo acomoda eleventa

Consultado hoy (2026-11-20) en sus páginas de ayuda, enlazadas en `docs/paridad-eleventa.md`:

- **Productos (F3)** es un módulo con **una fila de botones arriba**. Las páginas nombran «Modificar», «Eliminar»,
  «Departamentos», «Promociones», «Importar» y «Catálogo». En «Catálogo» se marcan productos y está «Actualizar varios»
  (precios de varios). F10 busca el producto que se va a modificar.
- **Inventario** va aparte, con los suyos: agregar, ajustes, productos bajos, reporte de inventario y movimientos. Así lo
  tiene ya el POS (Inventario.razor).
- Los nombres exactos de la fila de F3 («Nuevo», el orden) **se confirman en la caja 1 el sábado** (por revisar). Las
  páginas no los traen todos.

Es lo que el dueño y el encargado conocen: arriba, los botones de lo que se hace con los productos; abajo, la lista. La
propuesta sigue eso y no copia la ventana de eleventa: la lista y el detalle del POS se quedan.

## La propuesta

### 1. La barra de Productos, arriba y a todo lo ancho

Una franja de 44 px entre la barra de pestañas y la lista. Tiene tres partes:

```
[ Productos | Cambiar precios | Tarjetas de precio (3) | Promociones | Temporadas | Departamentos | Excel ]   [De eleventa ▾ 12]  [+ Nuevo producto]
```

- **Los lugares de la página, como pestañas** (`role="tablist"`, el estilo de `.vn-segmento`). Siempre se ve dónde se está;
  la que está abierta va marcada. «Productos» es la de siempre: el detalle del elegido y su ficha. Van por qué tan seguido
  se usan:
  1. **Productos**: cada vez.
  2. **Cambiar precios**: cuando sube un proveedor. Es el «Actualizar varios» de eleventa.
  3. **Tarjetas de precio (3)**: después de cada cambio de precio. El número sale una sola vez, aquí, en ámbar.
  4. **Promociones** y 5. **Temporadas**: unas semanas al año.
  6. **Departamentos**: de vez en cuando. En eleventa tiene botón propio.
  7. **Excel** (exportar e importar): para cargas. Antes «Productos en Excel»; dentro de Productos, «Productos» sobra.
- **«De eleventa ▾»** es un menú chico con lo de la mudanza:
  - «Mayoreo de eleventa por revisar (12)»;
  - «Lo que se decidió al limpiar el catálogo».

  El número de la etiqueta es lo que queda por revisar. Así lo de una sola vez deja de estorbar entre lo de diario. Con 0
  por revisar, el número no sale.
- **«+ Nuevo producto»**, botón principal (azul), a la derecha, siempre a la vista, también con la ficha abierta. Tecla
  **Alt+N** (por revisar).

Por qué arriba y no a la izquierda o en el pie:
- Arriba está donde lo tiene eleventa.
- La lista recupera los 2 o 3 renglones que hoy se comen los botones.
- La barra le dice a la lista para qué está: en «Cambiar precios» y al armar una promoción, la pestaña dice que un toque
  marca. Además, la lista lleva arriba una franja «Toca para marcar · 5 marcados · Quitar las marcas».

### 2. La lista: la búsqueda, los filtros y nada más

- Búsqueda igual. Filtros: **Todos · Por completar *n* · Bajos en inventario · Margen bajo** (este último, con
  `VerUtilidad`). «Poca existencia» pasa a «Bajos en inventario»: es el nombre de Inventario y de eleventa (por revisar).
- Debajo: Departamento y **«Incluir los inactivos»** (antes «Con inactivos»).
- Pie: solo «Lo más vendido en 90 días primero · 50 de 2,345.» y «Ver 50 más». Los botones se van a la barra.

### 3. El detalle y la ficha

- Cabecera del detalle: el nombre y, a la derecha, **«Etiqueta de código»** (antes «Etiqueta»; así no se confunde con la
  tarjeta de precio) y **«Editar»** (o «Ver la ficha»). Lo demás, igual.
- «Últimos movimientos» lleva un enlace **«Ver el kardex completo»** que abre la ficha en su kardex (hoy solo hay una nota
  que lo dice, L1102). Si la ficha nueva no tiene a dónde saltar, se queda la nota (Recomendaciones).
- **La ficha se abre igual:** en el lado derecho, en lugar del detalle, con la lista a la vista. Se abre con «Editar»,
  con Enter tras ↑ ↓ y desde Temporadas o el mayoreo. La pestaña «Productos» sigue marcada y la cabecera dice «Editar
  «…»» o «Nuevo producto».
- **Volver:** cada vista que no es «Productos» lleva en su cabecera **«← Producto Esc»**, como «← Inventario». La
  pestaña «Productos» hace lo mismo. Esc no cambia. Las cabeceras dejan de repetir «Esc regresa al producto»: lo dice el
  botón.

### 4. Nombres

| Hoy | Propuesto | Por qué |
|---|---|---|
| Mayoreo por revisar | Mayoreo de eleventa por revisar | Dice de dónde viene (el F11 de eleventa) |
| Limpieza del catálogo | Lo que se decidió al limpiar el catálogo | Es una lista de decisiones, no una herramienta para limpiar |
| «…con `aplicar-depuracion`» (L235) | «Lo que vino de eleventa se vuelve a aplicar solo en la importación final.» | Sin comandos |
| «No (se repite a mano)» (L241) | «Hay que repetirla a mano» | Se lee de una vez |
| Poca existencia | Bajos en inventario | Un solo nombre en Productos, Inventario y eleventa (por revisar) |
| Con inactivos | Incluir los inactivos | Dice qué hace |
| Etiqueta | Etiqueta de código | Distinta de la «Tarjeta de precio» |
| Productos en Excel | Excel | Ya se está en Productos; la vista sigue titulada «Productos en Excel» |
| Más · 3 por reimprimir | (desaparece) Tarjetas de precio (3) | El número va con lo suyo |
| Barra de abajo: «Enter abrir» | «Enter buscar · abrir el elegido» | Es lo que hace |

## De dónde a dónde

| Hoy (dónde) | Propuesta (dónde) | Id |
|---|---|---|
| Búsqueda (lista, arriba) | Igual | `busca-prod` |
| Filtros Todos, Por completar, Margen bajo | Igual | — |
| Filtro Poca existencia | Igual, como «Bajos en inventario» | — |
| Departamento y «Con inactivos» | Igual, «Incluir los inactivos» | — |
| Orden y «Ver 50 más» (pie de la lista) | Igual | — |
| Nuevo producto (pie de la lista) | Barra de Productos, derecha, botón principal; Alt+N | `nuevo-producto` (nuevo) |
| Productos en Excel (pie de la lista) | Barra › pestaña «Excel» | `ver-excel` (se queda) |
| Más › Mayoreo por revisar | Barra › «De eleventa ▾» › Mayoreo de eleventa por revisar | `ver-mayoreo` (nuevo), menú `ver-eleventa` |
| Más › Cambiar precios | Barra › pestaña «Cambiar precios» | `ver-precios` (nuevo) |
| Más › Temporadas | Barra › pestaña «Temporadas» | `ver-temporadas` (se queda) |
| Más › Tarjetas de precio · n | Barra › pestaña «Tarjetas de precio (n)» | `ver-tarjetas` (se queda) |
| Más › Promociones | Barra › pestaña «Promociones» | `ver-promociones` (se queda) |
| Más › Departamentos | Barra › pestaña «Departamentos» | `ver-departamentos` (nuevo) |
| Más › Limpieza del catálogo | Barra › «De eleventa ▾» › Lo que se decidió al limpiar el catálogo | `ver-decisiones` (nuevo) |
| «Más» (el `<details>`) | Desaparece | — |
| Detalle › Etiqueta / Ir a Etiquetas | Igual, «Etiqueta de código» | — |
| Detalle › Editar / Ver la ficha | Igual | — |
| Detalle › nota del kardex | Enlace «Ver el kardex completo» (si la ficha nueva lo permite) | — |
| La ficha, en el lado derecho | Igual; la pestaña «Productos» queda marcada | `ficha-*` |
| Esc en cada vista | Igual, y además «← Producto» en cada cabecera | `volver-al-producto` (nuevo) |

## Permisos

No cambia quién ve qué. Solo se ordena dónde sale:

| Qué | Hoy | Propuesta |
|---|---|---|
| La página (pestaña F3) | `Ve(EditarCatalogo)` | Igual |
| Nuevo producto (y Alt+N) | `Puede(EditarCatalogo)` | Igual; sin permiso, ni botón ni tecla |
| Cambiar precios, Tarjetas, Promociones, Temporadas | `Puede(EditarCatalogo)` | Igual: sin el permiso no sale la pestaña |
| Departamentos | Todos los de la página (cambiar: `Puede`) | Igual |
| Excel | Todos (importar: `Puede`) | Igual |
| De eleventa › Mayoreo | `Puede` | Igual |
| De eleventa › Decisiones | Todos los de la página | Igual. Sin el mayoreo, el menú tiene una sola entrada; se queda menú para que no cambie de lugar |
| Margen bajo, margen del detalle | `Puede(VerUtilidad)` | Igual |
| Costo del detalle | `CostearCompra` o `VerUtilidad` | Igual |
| Etiqueta de código | `Ve(EditarCatalogo)` | Igual |

Lo que convendría cambiar de permisos va en [Recomendaciones](#recomendaciones): no entra con este acomodo.

## Teclado

| Tecla | Hoy | Propuesta |
|---|---|---|
| F3 | Abre Productos con el cursor en la búsqueda | Igual |
| ↑ ↓ | Eligen en la lista; el detalle sigue | Igual |
| Enter | Busca ya o, tras ↑ ↓, abre la ficha; marcando, marca | Igual; la barra de abajo lo dice bien |
| Esc | Cierra lo de adentro; luego al producto; luego a la venta | Igual |
| Ctrl+Enter | Guarda la ficha, aplica precios, Excel y promociones | Igual |
| **Alt+N** | — | **Nuevo producto** (por revisar). No choca con Alt+1 a Alt+6 (rápidos) ni con las Ctrl de caja.js (L531-549). Ins queda para «Varios» y Ctrl+N es «ventana nueva» del navegador |
| Tab | Recorre la página | La barra queda entre las pestañas de arriba y la búsqueda, y las flechas ← → se mueven dentro de la barra (patrón de pestañas). El cursor sigue empezando en la búsqueda |

La barra de abajo en Productos queda así: `F1 Volver a la venta · ↑ ↓ elegir · Enter buscar o abrir el elegido · Alt+N
nuevo producto · Esc cierra lo abierto`.

## Decidido sin el dueño (por revisar)

1. La barra arriba y a todo lo ancho, con los lugares como pestañas (no en un menú). **Por revisar.**
2. El orden de las pestañas por qué tan seguido se usan (Productos, Cambiar precios, Tarjetas, Promociones, Temporadas,
   Departamentos, Excel), no el de eleventa. **Por revisar.**
3. Lo de la mudanza en un menú «De eleventa». **Por revisar.**
4. «Poca existencia» → «Bajos en inventario». **Por revisar.**
5. Alt+N para «Nuevo producto». **Por revisar.**
6. «Etiqueta» → «Etiqueta de código»; «Productos en Excel» → «Excel» en la pestaña. **Por revisar.**
7. Los permisos se quedan como están, aunque haya incongruencias (Recomendaciones). **Por revisar.**
8. Lo que eleventa tiene en F3 y aquí está en otra página se queda donde está: «Ventas por período» en Reportes y
   «Eliminar» como «inactivo» en la ficha. No se agrega a la barra. **Por revisar.**

## Recomendaciones (pospuestas)

- **Permisos para consultar:** dejar ver Promociones, Temporadas y Tarjetas a quien tiene «Productos, precios y etiquetas»
  con autorización. Esas vistas ya se enseñan solo para consulta. Hoy no las ve.
- **Temporadas y Tarjetas a todo lo ancho:** no usan la lista de la izquierda. Podrían usar todo el ancho, como las tareas
  de Inventario. Se deja para después de ver el acomodo en la tienda.
- **Quitar el menú «De eleventa»** después de la importación final, cuando no quede mayoreo por revisar. Las decisiones
  pasarían a la bitácora.
- **Las etiquetas en un solo lugar:** hoy se arman en Productos y se imprimen en Inventario. Conviene una pestaña «Para
  imprimir» que junte etiquetas de código y tarjetas de precio, o al menos que «Etiqueta de código» diga cuántas van.
- **«Tarjeta de precio» desde el detalle**, junto a «Etiqueta de código», para agregar la de ese producto.
- **«Modificar las temporadas»:** un solo lugar, el de Productos o el de Configuración, con un enlace desde el otro.
- **«Ver el kardex completo»** directo a la ficha nueva en su kardex, en cuanto la ficha lo permita.
- **Las vistas de la mudanza con `vn-boton` y `vn-tabla`**, como las demás (RevisarMayoreo.razor:43-44, L236).
- **Probar con el encargado** en la tienda (sábado o domingo): «da de alta un producto», «cambia los precios de una marca»,
  «¿qué tarjetas hay que reimprimir?». Medir si encuentra cada cosa a la primera, con lo de hoy y con la propuesta.

## Cómo hacerlo

Un PR chico, después de que se fusione el de la ficha (los dos tocan `Productos.razor` y `caja.css`).

1. **`Productos.razor`**
   - Envolver la página en `<div class="vn-prod-pag">` (rejilla de dos renglones: la barra y `.vn-modulo-pag`).
   - Agregar `<nav class="vn-prod-barra">` con:
     - las pestañas (`role="tab"`, `aria-selected`), con los ids de la tabla;
     - el menú `ver-eleventa` (un `<details>` chico que se cierra al elegir; o un `vn-boton` con su lista);
     - `nuevo-producto`.
   - Una propiedad `Pestana` que traduzca `_vista` (Detalle y Ficha → Productos; Decisiones y Mayoreo → ninguna, y se
     marca el menú).
   - La pestaña «Productos» llama a `AlProductoAsync()` pasando por `DetenidoPorLaFicha` (el aviso de cambios sin guardar
     sigue mandando).
   - Quitar L122-148 (botonera y `<details>`); se quedan el orden y «Ver más».
   - Cambiar textos: L35 (Bajos en inventario), L50, L193, L223, L235, L241, L994.
   - Agregar `volver-al-producto` en la cabecera de Mayoreo y Decisiones; las demás, en su componente.
2. **Componentes de las vistas:** un botón «← Producto <kbd>Esc</kbd>» en la cabecera. Conviene un parámetro
   `AlVolver`, o mejor, que la cabecera la ponga `Productos.razor` arriba de cada vista para no tocar seis componentes.
   Esto quita «Esc regresa al producto.» de PreciosDeVarios.razor:18, TemporadaEnProductos.razor:23,
   ExcelEnProductos.razor:17, Departamentos.razor:13 y TarjetasDePrecio.razor:14.
3. **Alt+N:**
   - En `caja.js` (~L531), una constante `altN` y meterla en `atrapar` solo si `document.getElementById('nuevo-producto')`
     existe.
   - En PantallaVenta.razor (switch de teclas, ~L3925) y Administracion.razor:286-296, `case "Alt+N"`: si la página es
     Productos, `_productos.NuevoProducto()` (hacer público `Nuevo`).
4. **La barra de abajo:** PantallaVenta.razor:838-846, con el texto de [Teclado](#teclado). Administracion.razor tiene la
   suya: revisar que diga lo mismo.
5. **`caja.css`:**
   - `.vn-prod-pag { display: grid; grid-template-rows: auto minmax(0, 1fr); }`
   - `.vn-prod-barra { display: flex; align-items: center; gap: 10px; padding: 6px 14px; background: var(--vn-hoja); border-bottom: 1px solid var(--vn-linea); min-width: 0; }`
   - Las pestañas con `.vn-segmento` (el que ya existe, ~L1156), que se recorre de lado sin ensanchar la página
     (`overflow-x: auto`).
   - Quitar `.vn-lista-mas` (~L1522-1525).
6. **`GuiaPrueba.cs`:** cambiar «(en «Más», abajo de la lista)» por «(arriba, en la barra de Productos)». Está en L392,
   408, 427, 430, 434, 443-444, 451, 454, 502, 524-525, 529 y 532. Revisar también L386-388 («Poca existencia»).
7. **Docs:** en `docs/propuesta-interfaz.md`, una sección «Productos (F3): la barra» con el decidido por revisar. En
   `docs/paridad-eleventa.md` no cambia nada: los nombres «Productos › Cambiar precios», «Promociones» y «Departamentos»
   siguen valiendo.

### Las pruebas (`tests/PapeleriaPos.Pruebas/Interfaz`)

| Prueba | De qué depende | Qué hacer |
|---|---|---|
| PaginasTests.cs:121-126 | Los textos de los filtros, `filtros[2] == "Poca existencia"` | Cambiar a «Bajos en inventario» |
| PaginasTests.cs:137 | Un `vn-boton` con el texto exacto «Nuevo producto» | Que siga exacto: Alt+N va en `title` y en la barra de abajo, no en un `<kbd>` dentro del botón |
| PaginasTests.cs:214 | `BotonAsync("Etiqueta")` (empieza con) | Sigue: «Etiqueta de código» empieza igual y ninguna pestaña empieza con «Etiqueta» |
| PreciosDeVariosTests.cs:118, 409 | Un botón «Cambiar precios»; sin permiso, que no esté | Sigue con la pestaña (misma regla `Puede`) |
| DepartamentosTests.cs:60 | `BotonAsync("Departamentos")`, el primero que empieza así | Sigue (la pestaña). Mejor: `PorIdAsync("ver-departamentos")` |
| TemporadaEnProductosTests.cs:66, 103, 264 | `ver-temporadas` y `ver-tarjetas`; L263 espera el texto «por reimprimir» | Los ids se quedan. L263: la pestaña dice «(3)»; esperar `ver-tarjetas` con «(1)», o dejar «· 1 por reimprimir» en el `title` y el texto visible |
| PromocionesEnProductosTests.cs:84, ExcelEnProductosTests.cs:115 | `ver-promociones`, `ver-excel` | Nada; solo los comentarios «abajo de la lista» (ExcelEnProductosTests.cs:112, DepartamentosTests.cs:57) |
| PantallasConTecladoTests.cs:747-903, NuevoProductoTests.cs:59, AltaDeProductoTests.cs:69, PaginasTests.cs:580, 619 | `BotonAsync("Nuevo producto")` | Siguen (el de la barra es el primero) |
| Nuevas | — | La barra según el permiso (encargado sin `VerUtilidad`, dueño, uno con autorización: solo Productos, Departamentos, Excel y De eleventa); la pestaña marcada al abrir cada vista y la ficha; «← Producto» igual que Esc y con el aviso de cambios sin guardar; Alt+N abre la ficha nueva y sin permiso no hace nada; «De eleventa» con su número |

Al terminar: la suite completa como siempre (con Firebird, dos a la vez con `taskset -c 0-3`) y una mirada a 1366×768 en
la ventana de Windows.
