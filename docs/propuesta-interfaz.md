# Propuesta: rediseño de la interfaz de la caja

> **Aprobada por el dueño (2026-10-10), tercera versión.** Las preguntas del final siguen abiertas: se contestan antes de
> la entrega que las necesita. Se puede probar en el [prototipo navegable](propuesta-interfaz/prototipo.html): se abre
> con doble clic, sin instalar nada. Arriba trae los controles para cambiar de usuario, de panel del total, de caja, la
> red, las impresoras y el celular, y a la derecha «Qué probar». Todo es ficticio.
>
> - **Segunda versión:** quitó los roles (cada usuario tiene sus permisos) y sumó el corte con solo el total, el corte
>   del día, los reportes y una configuración nueva. También rehízo los tickets en espera y el panel del total.
> - **Tercera versión:** F12 lleva la atención al cobro con el exacto en gris. Las impresiones por cobrar se ven con la
>   miniatura de su primera hoja, en lotes, como en Control Print. Ctrl+L permite elegir a quien escribió por
>   WhatsApp ([investigación](#whatsapp-quién-escribió-hace-poco)).
> - **Ajustes a la tercera:** el cliente es una flecha chica que se jala para abrir su panel. El cobro se compactó: sin
>   textos de ayuda, F12 bajo el total y las otras formas de pago junto a «Pagó con». Con ese espacio, la bandeja de
>   impresiones pasó bajo el cobro, en dos columnas que se recorren con la rueda del mouse, y el ticket ganó toda la
>   altura. Un ticket armado en el celular llega a la caja elegida como ticket en espera
>   ([desde el celular](#desde-el-celular)). Los tickets en espera ya no tienen límite: los que no caben a la vista van
>   en «Más».
> - **Lo que no se escanea:** la etiqueta del cajón, dos pedazos de palabra en cualquier orden, el camino de siempre
>   (F10 y bajar) que se aprende, y lo exhibido y de temporada con fotos en el mismo acomodo que en la tienda (Ctrl+M).
>   El rendimiento suma lo que encontró la revisión de la comunicación entre cajas.

La app Android será nativa en Kotlin, así que la interfaz Blazor ya no tiene que servir también al celular: se diseña
solo para la caja, con teclado y lector, a 1366×768, buscando la mayor rapidez y que se entienda sin explicarla.

## Qué se queda y qué cambia

- **Se queda:**
  - El **cuadro de captura** arriba, con el cursor siempre ahí. Ahora entiende más que el código de barras.
  - La **lista del ticket** como centro de la pantalla.
  - Las teclas de eleventa que ya usan los cajeros (F12, F10, F5/F6, F7/F8, F9, Ins, Supr, Ctrl+P…).
- **Cambia todo lo demás:**
  - Los casi 30 botones fijos pasan a una barra de teclas abajo, a la fila del artículo y a la lista «/».
  - Las ventanas que tapan el ticket pasan a un panel a la derecha. El total y el cobro son un solo panel.
  - Ya no hay roles: cada usuario ve lo que sus permisos le dejan.

## Lo que estorba hoy

Revisado en la caja actual (navegador, 1366×768, base de la prueba) y con lo que anotó la prueba con una persona real
(PR #54 y #55).

| Hoy | Por qué estorba |
|---|---|
| Casi 30 botones visibles para todos: el cajero ve Configuración, Productos y Clientes aunque no pueda usarlos | Se aprende por error: aprieta, le dice que no puede. Lo que sí importa se pierde entre lo demás |
| Cobrar, buscar, descuento y cliente abren ventanas encima del ticket | Mientras cobra, el cajero no ve lo que está cobrando |
| No se ve quién autoriza hasta intentarlo | La guía de la prueba dijo que el encargado autoriza el cambio de precio; lo autoriza el dueño (PR #55) |
| «INS Varios» y «CTRL+P Art. común» hacen lo mismo | Dos botones para una cosa (PR #55) |
| El corte Z vuelve a pedir usuario y PIN al encargado que ya entró, y pide contar por billete y moneda | Pasos de más (PR #55) |
| F3 Productos abre vacío hasta que se busca | Pantalla en blanco al entrar (PR #55) |
| Una fila de pestañas para los tickets en espera | Ocupa espacio todo el día para algo que casi no se usa |
| Un ticket en espera se pierde si se cierra o se reinicia la caja | Sin no-break, un apagón borra los tickets que esperan (PR #55) |
| Las impresiones se buscan en F2 | La que se acaba de imprimir en esa misma PC se puede olvidar al cobrar |
| Se entra escribiendo el usuario y el PIN | Cambiar de cajero tarda más de lo necesario |
| Cada escaneo es una consulta al servicio por la red | En la caja 2 (Wi-Fi), si la red va lenta, cada escaneo espera; con la conexión medio caída, hasta 15 s antes de usar la copia local |
| Cada tecla del lector repinta toda la pantalla, y el cuadro se vacía después de la consulta | Unos 28 repintados por código. En una ráfaga, un escaneo puede borrar o cortar el siguiente (sale del código; falta verlo pasar) |
| Después de cobrar, la pantalla espera a que salga el ticket | La impresora de tickets la comparten las dos cajas: si la otra imprime, esta espera |

## Lo que necesita la vista principal

La pantalla de venta se usa el 95 % del tiempo. Esto es lo que pasa ahí, de lo más a lo menos frecuente, y cómo lo
resuelve la propuesta. Las frecuencias son estimadas; se confirman con la prueba con una persona real y con la
bitácora.

| # | Necesidad | Qué tan seguido | Cómo queda |
|---|---|---|---|
| 1 | Agregar un producto con el lector | En casi cada venta | Cuadro de captura con el cursor siempre ahí. La línea se ilumina (sin sonidos por ahora) y, si ya estaba, suma uno. Sin red de por medio |
| 2 | Agregar servicios sin código: copias, engargolado, enmicado | Muchas veces al día | **Rápidos** en una fila, con Alt+1 a Alt+6. Un número antes es la cantidad: `20` y Alt+1 son veinte copias |
| 3 | Cobrar en efectivo y dar el cambio | En cada venta | **El panel del total es el cobro.** F12 apaga lo demás y deja el cursor en «Pagó con», con el exacto en gris: Enter cobra exacto; si se escribe lo que dio, Enter cobra y muestra el cambio |
| 4 | Ver el total mientras se agregan cosas | Todo el tiempo | El total grande a la derecha, con el ahorro por mayoreo y el redondeo al peso |
| 5 | Cobrar la impresión que se acaba de hacer en esa PC | Muchas veces al día | **La bandeja:** la miniatura de la primera hoja de cada trabajo de esa PC, en lotes, siempre a la vista bajo el cobro, en dos columnas. Se toca la que se tiene en la mano, o F2 agrega el lote más nuevo |
| 6 | Agregar lo que no se escanea (suelto, chico, sin código o con el código dañado) | Muy seguido | La etiqueta del cajón, el código corto o el principio de dos palabras en cualquier orden; fichas para afinar y los colores en un renglón ([abajo](#lo-que-no-se-escanea-y-casi-no-se-vende)) |
| 7 | Buscar por el camino de siempre: F10, el nombre que se recuerda y bajar hasta el producto | Muy seguido | Igual que hoy, en la misma lista y hasta 30 resultados; lo que se eligió con ese texto sale primero la vez siguiente ([abajo](#el-camino-de-siempre)) |
| 8 | Vender lo que el cliente señala y contestar «¿cuánto cuesta?»: lo exhibido y lo de temporada | Seguido (lo de temporada, unas semanas al año) | Ctrl+M: cada vitrina y cada temporada con fotos, en el mismo acomodo que en la tienda; en el verificador, tocar solo muestra el precio ([abajo](#lo-que-se-señala-exhibidos-y-temporada)) |
| 9 | Cambiar la cantidad o quitar algo | Seguido | `12*código`, + y − en la fila, Supr. Las acciones del artículo van pegadas a su fila |
| 10 | Ver un precio sin vender | A veces | F9: el cuadro cambia a ámbar y no agrega nada. Si se lo lleva, «Agregarlo al ticket» |
| 11 | Ponerle cliente al ticket: quien escribió por WhatsApp, o un registrado (mayoreo, crédito, abono) | A veces | La flecha del cliente, arriba del total, o Ctrl+L: primero «Escribieron por WhatsApp hace poco» y luego los registrados; Ctrl+B para abonar |
| 12 | Tarjeta o transferencia | Pocas veces (hoy casi todo es efectivo) | Botones chicos junto a «Pagó con», sin renglón propio |
| 13 | Dejar un ticket en espera | Rara vez, y casi nunca más de 3 | Un botón y 3 fichas en la fila de los rápidos; los demás en «Más», sin límite; nada de pestañas. Los que se arman en el celular llegan ahí, con un efecto |
| 14 | Descuento, cambio de precio, artículo común, entradas y salidas, reimprimir, devolver | Pocas veces | Las teclas de siempre y la lista «/». Lo que pide autorización dice quién autoriza |
| 15 | Saber si algo falla: red, impresora, límite de efectivo | De un vistazo | Chips arriba; una franja solo si se perdió la caja principal; el aviso de retiro junto al total |

Lo demás (productos, inventario, clientes, corte, reportes y configuración) está en las pestañas de arriba, cada una
según los permisos de quien entró.

## La pantalla de venta

```
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│ [P] Caja 1  Venta F1 · Inventario F4 · Corte        ● Servicio aquí  Tickets  Impr. 6  Ana   │
├──────────────────────────────────────────────────────────────┬───────────────────────────────┤
│ ┌──────────────────────────────────────────────────────────┐ │          ◀ Público en general │
│ │ ▥  Escanea o escribe: código, nombre, 3*código o folio  /│ │ ╱╲╱╲╱╲╱╲╱╲╱╲╱╲╱╲╱╲╱╲╱╲╱╲╱╲╱╲  │
│ └──────────────────────────────────────────────────────────┘ │ Total                 35 art. │
│ ✓ Sacapuntas metálico · 1 × $9.00                            │ $207.00                       │
│ RÁPIDOS [Alt+1 Copia B/N] [Alt+2 CURP]        [⏸ 12:25] [F6] │ [F12] cobrar                  │
│ CANTIDAD   DESCRIPCIÓN                       PRECIO  IMPORTE │ Ahorro por mayoreo     $18.00 │
│ [− 2 +]   Cuaderno profesional cuadro chico  $46.00  $92.00  │ - - - - - - - - - - - - - - - │
│ [− 12 +]  Lápiz HB No. 2  ·Mayoreo desde 12·  $5.50  $66.00  │ Pagó con  [Tarjeta] [Transf.] │
│ [− 20 +]  Copia blanco y negro carta          $2.00  $40.00  │ [ $207.00 (gris)            ] │
│ [− 1 +]   Goma blanca M40                     $5.00   $5.00  │                               │
│▌[− 1 +]   Sacapuntas metálico  ·Quedan 1·     $9.00   $9.00  │ [ Cobrar exacto $207.00     ] │
│▌          [Precio 🔒 Luis] [Descuento hasta 10 %] [Quitar]   │ ┌ Sin cobrar aquí · 4 · $45 ┐ │
│                                                              │ │ 14:50–14:51 · Mónica R.   │ │
│                                                              │ │ [▤ tarea_hist] [▥ portada]│ │
│                                                              │ │ [▤ ine_ambos]  [▥ mapa_mx]│ │
│                                                              │ └─ la rueda del mouse baja ─┘ │
│                                                              │ Último: A-000123 · cambio $63 │
├──────────────────────────────────────────────────────────────┴───────────────────────────────┤
│ F12 Cobrar  F10 Buscar  F9 Verificar  F2 Impresiones  Ins Art. común  F7  F8  F6  / Más      │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

### El cuadro de captura

| Se escribe | Qué pasa |
|---|---|
| Un código (del lector o tecleado) | Agrega el producto. Si ya está en el ticket, suma uno; al llegar a la escala, entra solo el mayoreo |
| `12*código` | Agrega doce de una vez |
| Un número y Alt+1 a Alt+6 | Agrega esa cantidad del rápido: `20` y Alt+1 son veinte copias |
| Texto (`cuaderno estrella`) | Los resultados salen debajo del cuadro, mientras se escribe: nombre, marca, código, precio y existencia. ↑ ↓ y Enter agregan. F10 se queda y solo pone el cursor ahí |
| Un folio (`A-000118`) o su QR | Abre ese ticket en el panel para devolver o cancelar |
| `/` | La lista de todas las acciones que tiene ese usuario, con su tecla; lo que pide autorización lleva candado y el nombre de quien autoriza |
| Un código que no existe | Lo dice bajo el cuadro, en rojo, y ofrece lo que sus permisos dejan: darlo de alta ahí o venderlo como artículo común |
| `+` / `−` con el cuadro vacío | Cambia la cantidad del artículo seleccionado; ↑ ↓ eligen otro; Supr lo quita |

- **El lector se reconoce por su ritmo:** teclea más rápido que una persona y termina en Enter. Mientras llega una
  ráfaga así no se abren resultados, para que la lista no parpadee en cada escaneo. Si el lector escribe en «Pagó
  con», el código se va al cuadro de captura: un escaneo nunca se toma como dinero.
- **El verificador (F9)** cambia el cuadro a ámbar: lo escaneado muestra precio, existencia y mayoreo en el panel y no
  se agrega. F9 o Esc regresan a vender.

### La lista del ticket

- Cantidad editable en la misma fila (− y +), descripción con el código en pequeño, precio e importe.
- **Las acciones del artículo van pegadas a su fila,** debajo de la línea seleccionada: precio, descuento, mayoreo y
  quitar, con su tecla o con el candado y el nombre de quien autoriza. Así el panel de la derecha queda solo para el
  dinero.
- **La existencia ya no ocupa una columna:** aparece como etiqueta solo cuando importa («Quedan 2», «Sin existencia»;
  el umbral se configura). Lo mismo el mayoreo («Mayoreo desde 12 · ahorra $18.00»), el precio cambiado (con quién lo
  autorizó) y el descuento (con su motivo).
- La línea recién agregada se ilumina un momento y la lista baja sola hasta ella.

### Rápidos

Los servicios que no tienen código de barras y se venden todo el día se eligen en Configuración › Venta y cobro, hasta
6. Salen en una fila bajo el cuadro, con su precio y Alt+1 a Alt+6. Escribir un número antes los agrega en esa
cantidad.

**Para empezar (decisión del dueño, 2026-10-10):** dos rápidos.

- **Copia B/N, $2.**
- **CURP, $10:** vende 10 impresiones. La impresión toca el módulo que reemplaza a Control Print; mientras Control
  Print siga aparte, el botón solo equivale a vender 10 de impresiones.

### Lo que no se escanea y casi no se vende

Una buena parte de lo que se vende no se escanea: va suelto, es chico, nunca trajo código o lo trae dañado (el análisis
con los datos de la tienda se comenta aparte). Mucho de eso se vende poco, así que ordenar por lo más vendido no lo
sube. Para eso, de lo más rápido a lo de respaldo:

| # | Cómo | Teclas | Para qué |
|---|---|---|---|
| 1 | **Escanear la etiqueta del cajón o del gancho** | 0 | Lo suelto que vive en un cajón, un gancho o una caja: papel lustre, folders, sobres. El POS imprime la etiqueta con un código interno (los que empiezan con 20 están reservados para uso interno) y queda como un código más del producto. Escanearla es escanear el producto |
| 2 | **El código corto** (`FOAAZ` y Enter) | 4 a 6 | Lo que el cajero ya teclea de memoria. En la lista sale dibujado como tecla, para que se aprenda |
| 3 | **El principio de dos palabras, en cualquier orden** (`lus az` y Enter) | 6 a 8 | Todo lo demás. Cada pedazo es el principio de una palabra del nombre, la marca, el color o el código. Los dos pedazos dejan solo al producto aunque casi no se venda. Sin acentos, con los sinónimos que decida la tienda y con un error de dedo («sobre manial» da los sobres manila) |
| 4 | **Afinar con fichas** (`folder` → «oficio») | 1 clic | Cuando el texto es general, las palabras que separan los resultados salen como fichas. Se tocan o se escribe su principio |
| 5 | **Las familias en un renglón** (`lapicero` → sus colores; `lapicero negro` → sus tamaños) | 1 clic, Enter, o → y ↓ | Lo que solo cambia en el color, el tamaño, la marca, el contenido o el rayado ocupa un solo renglón, con fichas para cada valor (el color con un punto de referencia). Se despliega lo primero que cambia, Tab despliega por otra cosa y Enter agrega siempre el más vendido |
| 6 | **Si no existe: artículo común** (Ins) | El precio | Se vende igual, y lo que se buscó queda anotado |

- **El orden:** el código exacto; luego lo que esta caja eligió antes con ese mismo texto; luego lo que empieza con lo
  escrito; al final, lo más vendido en 90 días (para arrancar, el historial de eleventa).
- **Se aprende de lo que se busca:** cada búsqueda se guarda con el texto, cuántos resultados hubo y qué se eligió, y lo
  elegido sale primero la vez siguiente. Lo que se buscó y no se encontró queda anotado, pero no arma los sinónimos
  (decisión del dueño, 2026-10-11).
- **El nombre de eleventa de cada producto ya es su sinónimo** (decisión del 2026-10-10). Si la depuración del
  catálogo le cambia el nombre, el de siempre lo sigue encontrando, y la lista muestra en letra chica «en eleventa: …»
  junto al nombre nuevo, para irlo aprendiendo sin perder tiempo.
- **Los demás sinónimos los decide la tienda** en Configuración › Sinónimos: quien edita el catálogo los da de alta uno
  por uno («al escribir *boligrafo*, también busca *lapicero*», y al revés si se quiere), y la lista no viene llena. En la
  búsqueda, lo que salió por un sinónimo lo dice («por «boligrafo»»). Un sinónimo mal puesto estorba más de lo que
  ayuda: en esta tienda «pluma» es la pluma decorativa (no el lapicero) y «pegamento» es solo el líquido blanco (el
  silicón, de barra para pistola o líquido, es otro producto).
  Las variantes de escritura (foami y foamy) y las abreviaturas de los nombres que vienen de eleventa (V/, C/, P/) se
  confirman con la tienda antes de usarlas.
- **La etiqueta del cajón** sale del mismo módulo de etiquetas del inventario (fase 2): se elige el producto, se imprime
  y se pega en su lugar.
- **Las familias** (2026-10-11, con el dueño): el renglón lleva el nombre de eleventa sin lo que cambia
  («Lapicero 0.7mm P/Fino» con Negro, Azul, Rojo y el surtido al final). → despliega cada valor con su precio y
  existencia; ← o Esc lo cierran. El precio dice «desde» cuando cambia según el valor: el costo de compra es distinto.
  Los surtidos («V/Colores») siguen como un solo producto; más adelante, el producto podrá venderse por color.

#### El camino de siempre

Después de años con eleventa, muchos productos, con código corto o con EAN, se buscan por un camino aprendido: F10, el
nombre que se recuerda y bajar hasta el producto. Se usa cuando el cliente entró por una sola cosa, pidió verla, la
pagó y se fue con ella en la mano (no hubo nada que escanear), o cuando el código no se deja leer. Hoy la otra salida
es ir por otro igual para escanearlo. La propuesta respeta el camino y lo acorta:

- **F10 y el nombre de siempre funcionan igual:** F10 pone el cursor en el cuadro, y lo escrito da la lista ahí mismo,
  sin otra ventana. El nombre de eleventa sigue encontrando el producto aunque se haya depurado (`jgo geo` da el «Juego
  de geometría metálico»).
- **La lista llega hasta 30 resultados,** así que se puede bajar con ↓ o con la rueda, como hoy.
- **Lo que esta caja elige con un texto sale primero la vez siguiente.** «Nombre, bajar cuatro, Enter» se vuelve
  «nombre, Enter» desde la segunda vez, sin configurar nada.
- **El orden por lo más vendido se recalcula una vez por semana,** no a cada rato, para que lo aprendido no se mueva.

### Lo que se señala: exhibidos y temporada

Dos grupos de productos piden otra cosa que buscar por el nombre:

| | Exhibidos | De temporada |
|---|---|---|
| Qué son | Lo que está en la vitrina, en la pared o colgado | Lo que se vende unas semanas al año, por lo general un mes (Día de Muertos, Navidad…) |
| Por qué cuesta | Es difícil escanearlo, ponerle un precio a la vista y recordar su precio. Se pregunta cuánto cuesta más que lo demás, y no está entre lo más vendido | Es difícil etiquetarlo y escanearlo. Al empezar la temporada nadie recuerda su precio, y con la lista que hoy se guarda con ellos cuesta saber qué producto es cada nombre |

Las fotos ayudan a reconocer el producto, pero en una lista larga todavía hay que buscarlo. La propuesta las pone **en el
mismo acomodo que en la tienda**: el cajero no recuerda el nombre, recuerda dónde está, y ahí lo toca.

1. **El mostrador en la pantalla (Ctrl+M).** Un lugar por exhibidor (la vitrina del mostrador, la pared) y uno por
   temporada. Cada producto, con su foto y su precio, va donde está en la tienda: la repisa de arriba, la de abajo, la
   mesa de la entrada.
   - **Se toca lo que el cliente señala.** Con un número antes, agrega esa cantidad. El panel se cierra y queda el total,
     listo para F12.
   - **En el verificador (F9), tocar solo muestra el precio y la foto.** Así se contesta «¿cuánto cuesta?» sin buscar;
     si se lo lleva, «Agregarlo al ticket».
2. **La temporada, a la vista mientras corre.** Tiene fechas. Mientras está activa, sale primero en Ctrl+M y con un chip
   arriba («Día de Muertos»); al terminar, se oculta sola.
3. **Empezar la temporada,** en Productos, una vez al año y antes de sacar la mercancía.
   - El POS trae la lista del año pasado: el precio de entonces, el costo de hoy y un precio sugerido. Los precios se
     confirman una vez.
   - Se toman las fotos que falten.
   - Se imprime **la hoja de la temporada:** la foto, el nombre, el precio y el código de cada producto, en el orden de
     la mesa. Reemplaza la lista de hoy: con la foto ya no hay que adivinar qué producto es cada nombre.
   - Se imprimen **las tarjetas de precio** para la mesa.
4. **Terminar la temporada.** Lo que sobró se cuenta y se guarda para el año siguiente. Queda el resumen de qué se
   vendió y qué no, para comprar mejor.
5. **Tarjetas de precio en los exhibidores.** El POS imprime una tarjeta por producto, con el precio grande para que el
   cliente lo vea sin preguntar y el código para que el cajero la escanee en lugar del producto. Si cambia un precio, el
   POS dice qué tarjetas volver a imprimir.
6. **Lo que más se pregunta.** El verificador cuenta lo que se consulta. En Productos se ven los productos que más se
   preguntan y no tienen tarjeta de precio: son los primeros a los que conviene ponérsela.
7. **Las fotos se toman con el celular,** desde la página que ya existe para el conteo (`/conteo`), sin esperar a la app
   Android: se escanea o se busca el producto y se toma la foto.
   - Se guardan chicas (unos 15 KB) y bajan a las cajas con el catálogo, solo las que cambian: con unas 300 fotos, unos
     5 MB.
   - La foto también sale en los resultados de la búsqueda y en el verificador, para confirmar que es ese.

En el prototipo, «Qué probar › Lo que se señala: exhibidos y temporada». Las fotos son dibujos.

### Tickets en espera

Hoy casi no se usan y rara vez son más de 3, así que dejan de ocupar una fila de pestañas. Pero **no hay límite**: con
los tickets que lleguen del celular pueden ser más.

- **Un botón** «Poner en espera» (F6) al final de la fila de los rápidos. Guarda el ticket y deja la caja lista para el
  siguiente cliente.
- **3 fichas a la vista** junto al botón, con la hora y el total («⏸ 12:25 · $55»), en orden de llegada. Tocar una la
  retoma; F5 retoma la primera. Si el ticket actual tiene algo, pasa a la espera: es un intercambio, nunca se pierde
  nada. Cuántas fichas se ven se cambia en Configuración.
- **Los demás, en «Más (7) ▾»,** un menú que se despliega con cada ticket en su renglón: la hora (o quién lo mandó),
  cuántos artículos y el total. Se recorre con la rueda del mouse; si son más de 5, tiene **flechas arriba y abajo**
  que lo deslizan al pasar el mouse o al hacer clic, y se apagan al llegar a la orilla. Se cierra con Esc, con un clic
  fuera o al elegir uno.
- **Sin espera, no hay nada que ver,** solo el botón.
- **Se guardan en la caja** (en `caja-local.db`): un apagón o un reinicio no los borra.

#### Desde el celular

La preventa de la [fase 8](plan.md#fases): quien atiende en el piso arma el ticket en la app Android, con la cámara o
buscando, y **elige a qué caja mandarlo**. El celular muestra cada caja con quién atiende y cuántos tickets esperan.

- **En la caja llega como un ticket en espera más,** con quien lo mandó en lugar de la hora («📱 Beto · $187»). Entra
  deslizándose y brilla unos segundos para que se note que acaba de llegar. El aviso bajo el
  cuadro dice quién lo mandó, cuántos artículos trae y el total.
- **No interrumpe:** el ticket que se está cobrando sigue igual. Se retoma como cualquier otro, tocándolo o con F5, y el
  cliente llega a la caja con todo ya escaneado. Si las fichas a la vista ya están ocupadas, entra en «Más» y es «Más»
  el que brilla.
- **Los precios los pone la caja** al recibirlo, con su catálogo. Desde el celular no se cambian precios ni se hacen
  descuentos: eso se hace en la caja, con su permiso. Mandar tickets es un permiso más del usuario.
- **Cómo viaja:** el celular lo manda al servicio de la caja principal, que lo avisa al momento a la caja elegida (el
  mismo aviso que bajaría los cambios del catálogo). Si esa caja está apagada, el celular lo dice y no lo manda.
- **Sin red:** el celular muestra el ticket como un QR (los códigos y cantidades caben de sobra) y el lector de la caja
  lo lee, como lee el QR de un folio.

### Las impresiones por cobrar: la bandeja

Control Print mostraba la **miniatura de la primera hoja** de cada trabajo, y con eso se distinguían al instante. El POS ya
guarda esa miniatura (`TrabajoImpresion.Miniatura`); la propuesta la pone en primer plano:

- **La bandeja**, en el panel de la derecha bajo el cobro, con lo impreso en esa PC que falta cobrar. Así el ticket
  tiene toda la altura de la izquierda. Cada trabajo es una tarjeta con su miniatura (con el número de hojas si son
  varias), el nombre del archivo, blanco y negro o color, la hora y el precio. El cajero toca la que coincide con las
  hojas que trae en la mano.
- **Dos columnas que se recorren con la rueda del mouse.** Caben unas 4 tarjetas a la vista. La rueda mueve solo la
  bandeja, sin mover el cobro. El borde se desvanece cuando hay más arriba o abajo. Lo nuevo entra arriba.
- **Lotes, como en Control Print:** lo que salió de la misma PC con menos de 3 minutos entre trabajo y trabajo va junto,
  porque casi siempre es de un mismo cliente. Un lote va en su recuadro, a lo ancho, con su botón «Los 2 · $17». Los
  trabajos sueltos van de dos en dos. **F2 agrega el lote más nuevo**.
- **No se va sola.** El cajero suele estar en la impresora cuando sale el trabajo; por eso la bandeja se queda hasta
  que se cobra y no avisa con algo que desaparece. Lo nuevo entra iluminado. Se puede plegar a
  una línea («Sin cobrar aquí · 2 · $17 · Agregar F2»).
- **F2 sin nada en la bandeja** abre el panel con todas las PC, con las mismas miniaturas y lotes, para cobrar lo que se
  imprimió en otra computadora.
- **En el ticket,** la línea «Impresiones» lleva las miniaturas pequeñas de lo que incluye.
- **Si el archivo llegó por WhatsApp,** el lote dice de quién («Mónica R.») y, al agregarlo, esa persona queda como
  cliente del ticket ([abajo](#whatsapp-quién-escribió-hace-poco)).
- **El recordatorio al cobrar** queda para cuando la bandeja está plegada o se eligió otra forma de aviso: si hay
  impresiones de esa PC sin cobrar, el panel del cobro lo dice con un botón para agregarlas.

La forma se elige por caja en Configuración: la bandeja (la propuesta), la tarjeta con tiempo de la versión anterior
(ahora con la miniatura) o solo el contador de arriba.

### El panel del total: propuesta B

La primera versión (A) tenía el total con un botón Cobrar que abría el cobro aparte. **La B junta el total y el cobro en
un solo panel, siempre listo:**

- **Arriba, el cliente, en una flecha chica, casi un botón,** pegada a la derecha y apuntando hacia el ticket
  («◀ Público en general»). Al pasar el mouse resalta en azul y se recorre hacia la izquierda, como si se jalara; al
  hacer clic, el panel del cliente entra desde la derecha (lo mismo que Ctrl+L). Con cliente, la flecha lleva su
  nombre, y a su izquierda va lo que debe con «Abonar», o «Quitar».
- Luego el total grande y, **justo debajo, F12**, con el ahorro y los descuentos.
- Debajo, separado como el corte de un ticket, **«Pagó con» siempre a la vista**: se escribe lo que dio el cliente,
  sin botones de billetes (decisión del dueño, 2026-10-10), y el cambio aparece al teclear. Sin textos de ayuda: el
  exacto en gris dentro del cuadro y el botón «Cobrar exacto $207.00» ya dicen qué pasa.
- **Tarjeta, transferencia y crédito, chicos junto a «Pagó con»**, sin renglón propio. Cada uno agrega su renglón con
  lo que falta.
- Bajo el cobro, la [bandeja de impresiones](#las-impresiones-por-cobrar-la-bandeja).
- Al cobrar, el panel muestra el cambio en grande hasta que se escanea al siguiente cliente; si la impresora no
  respondió, ahí mismo están «Reintentar» y «Verlo en pantalla».

**F12:**

1. La atención se va al cobro: el ticket y lo demás se apagan, y el panel del total queda resaltado en verde.
2. El cursor queda en «Pagó con», con **el exacto escrito en gris** («$207.00»).
3. **Enter** cobra exacto.
4. **Si se teclea un número**, reemplaza al gris, el cambio se ve al momento y Enter cobra.
5. **Esc** borra lo escrito y regresa el cursor al cuadro de captura, sin cobrar.

Una venta en efectivo queda en: escanear, **F12, 200, Enter**. Exacta: **F12, Enter**. No se abre nada y el cajero ve el
cambio antes de confirmar. Si el lector escribe en «Pagó con», el código se va al cuadro de captura. La A se puede
dejar por caja (Configuración › Esta caja); en el prototipo se cambian arriba.

### Los demás paneles

Cambian el lado derecho y vuelven con Esc: cliente (Ctrl+L), abono (Ctrl+B), descuento (Ctrl+D), precio (Ctrl+E),
impresiones por cobrar (F2), entrada y salida (F7/F8), artículo común (Ins o Ctrl+P), alta rápida, devolución,
mayoreo (F11), verificador (F9), ventas del día y cotizar un PDF (Ctrl+Q). Cada uno con sus campos y Enter para
terminar; lo que pide autorización la pide ahí mismo.

## WhatsApp: quién escribió hace poco

No se leen los chats. Lo que se busca es poder **elegir en la caja a quien escribió hace poco** y usarlo como cliente
genérico del ticket o del pedido, sin registrarlo. Sirve para el seguimiento («su trabajo está listo»), para cobrar sus
impresiones sin buscarlas y para saber a quién se le vendió.

### En la caja

- **Ctrl+L** muestra primero «Escribieron por WhatsApp hace poco» (las últimas 48 h, la más reciente arriba) y luego
  los clientes registrados. Cada fila: el nombre del perfil, el final del número («···4521»), hace cuánto y el nombre de
  los archivos que mandó. Se busca por nombre o por los últimos dígitos.
- Al elegirla, queda como cliente genérico; el aviso de «listo» va a ese contacto.
- **En la bandeja de impresiones,** el lote dice quién lo mandó si el archivo llegó por WhatsApp. Al agregarlo, esa
  persona queda como cliente del ticket.
- **Cómo se liga la impresión con quien la mandó:** al imprimir un archivo de Descargas, la caja calcula su huella
  (SHA-256) y la compara con la que trae el mensaje. Si no coincide (las fotos se recomprimen), compara el nombre del
  archivo y la hora; con notificaciones, solo la hora y el nombre, y el cajero confirma.

### De dónde sale la lista

Investigado el 2026-10-10.

| Opción | Precisión | Costo al mes | Complejidad | Con la caja principal apagada | Internet |
|---|---|---|---|---|---|
| **API oficial en coexistencia, con un proveedor (Kapso)** | Alta: número o su identificador, y el archivo con su huella | 0 si su plan gratis admite coexistencia; si no, 25 USD por hasta 3 números | Media | Se pide al encender | Sí |
| API en coexistencia con receptor propio (Cloudflare Worker) | Alta, y se guarda solo remitente, hora y archivo | 0 fijo; los avisos, de 0 a unos 80 MXN | Alta | Espera en el receptor | Sí |
| Notificaciones del celular (en la app Kotlin) | Media: el nombre como lo muestra WhatsApp, sin número fiable | 0 | Media-alta | Queda en el celular | Datos del celular |
| Notificaciones de Windows (WhatsApp Web) | Baja: el nombre, y se pierden los chats abiertos | 0 | Media; pide empaquetar la caja con identidad | Se pierde | Sí |
| Sin integración (etapa 1) | Lo que teclee el cajero | 0 | Baja | No aplica | No |

**Propuesta:** la lista sale casi gratis con la etapa 2 de [WhatsApp](whatsapp.md) (la API en coexistencia), así que se
hace con ella:

1. Probar en la matriz con Kapso. Su API de conversaciones ya da el número, el nombre y la última actividad, filtrable
   por fecha.
2. Después decidir si conviene el receptor propio, que guarda solo lo necesario.
3. Si no se quiere la API, las notificaciones del celular con la app Kotlin ya planeada, después de confirmar que el
   celular avisa con WhatsApp Web abierto.
4. Las de Windows solo sirven para probar la pantalla.

**Lo que cambió en 2026** (se suma a [WhatsApp](whatsapp.md)):

- Cada mensaje trae un identificador por negocio (BSUID, desde abril). Desde junio hay nombres de usuario: quien use uno
  puede llegar **sin número**. La lista identifica por ese identificador; el número puede faltar. En el prototipo,
  «Sra. Patricia» llega así.
- Según Meta, los mensajes de clientes que escriben desde **WhatsApp para Windows** no llegan al webhook. En una
  papelería puede ser una fuga; hay que medirla.
- Los dispositivos vinculados se desconectan a los ~30 días sin uso, y el celular a los ~14.
- Kapso dice cobrar la tarifa de Meta sin margen y tiene un plan gratis (1 número, 2,000 mensajes al mes). Eso contradice
  lo que dice [notas](notas.md); hay que confirmarlo.

**Privacidad (LFPDPPP, 2025):**

- El nombre, el número y la hora son datos personales, no sensibles. Basta el aviso de privacidad simplificado a la
  vista: en el mensaje de bienvenida de WhatsApp y en el letrero.
- Las finalidades son identificar el pedido, avisar y cobrar. Un historial por cliente o promociones piden otro
  consentimiento y chocan con «no guardar números de más».
- Lo que no se usó en un ticket o pedido se borra a los 7 días; lo ligado a un pedido sigue su regla.
- Meta, Cloudflare o Kapso se nombran en el aviso como encargados.

**Sin confirmar:**

- Que la huella del mensaje coincida con el archivo que baja WhatsApp Web.
- Que el plan gratis de Kapso admita coexistencia, y cuánto tiempo guarda los mensajes.
- Qué trae el primer mensaje de alguien con nombre de usuario.
- Si el celular avisa con WhatsApp Web activo.

**Fuentes** (consultadas el 2026-10-10):

- Meta: [coexistencia](https://developers.facebook.com/docs/whatsapp/embedded-signup/custom-flows/onboarding-business-app-users),
  [mensaje con documento](https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/document),
  [BSUID](https://developers.facebook.com/documentation/business-messaging/whatsapp/business-scoped-user-ids),
  [archivos](https://developers.facebook.com/documentation/business-messaging/whatsapp/business-phone-numbers/media).
- Kapso: [precios](https://kapso.com/pricing), [conversaciones](https://docs.kapso.ai/api/meta/whatsapp/conversations/list-conversations.md),
  [BSUID y nombres de usuario](https://kapso.com/blog/whatsapp-bsuid-usernames-2026).
- 360dialog: [webhooks en coexistencia](https://docs.360dialog.com/partner/waba-management/whatsapp-coexistence/coexistence-webhooks).
- Microsoft: [oyente de notificaciones](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener),
  [identidad para apps sin paquete](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps).
- Android: [cambios de Android 15](https://developer.android.com/about/versions/15/behavior-changes-all),
  [verificación de desarrolladores](https://developer.android.com/developer-verification).
- [LFPDPPP](https://www.diputados.gob.mx/LeyesBiblio/pdf/LFPDPPP.pdf).

## Usuarios y permisos

**Ya no hay roles de dueño, encargado y cajero.** Cada usuario tiene sus permisos, uno por acción, cada uno en tres
niveles:

| Nivel | En pantalla |
|---|---|
| Puede | Normal. Si tiene límite, lo dice: «Descuento · hasta 10 %», «Devolución · hasta $200» |
| Con autorización | Visible, con candado y el nombre de quien puede autorizarla («🔒 Luis», «Carmen o Luis»). Se autoriza en el mismo panel, con el PIN o por Telegram |
| No | No aparece: ni botón, ni tecla, ni en la lista «/», ni la pestaña |

- **Quién autoriza:** cualquier usuario que tenga esa acción en «Puede» (y por ese monto). La caja lo dice por su nombre
  desde antes de intentarlo.
- **Permisos por defecto:** cada usuario nuevo empieza con ellos (son los del cajero de hoy) y se editan en
  Configuración. En cada usuario: «Copiar los permisos de…», «Todos» y «Los de defecto».
- **La primera cuenta tiene todos los permisos.** Siempre debe quedar alguien con «Usuarios y permisos»: la caja no deja
  quitárselo al último.
- **El servicio sigue decidiendo.** Al entrar, la caja pide los permisos de esa persona en una sola consulta (para
  pintar sin preguntar en cada clic) y cada operación los vuelve a revisar en el servicio, como hoy.
- **Entrar:** fichas con el nombre de cada usuario (o su letra) y un PIN de 4 cifras, sin escribir el usuario. Ctrl+U
  cambia de usuario.
- **Un turno por cajero,** como hoy: si entra otro, la caja lo dice y quien tiene el permiso hace el corte ahí mismo,
  sin volver a escribir su PIN.

Los permisos y cómo empiezan:

| Grupo | Permisos (por defecto) |
|---|---|
| Venta | Quitar un producto (puede) · artículo común (puede) · descuento (hasta 10 %) · cambiar precio (con autorización) · vender debajo del costo (con autorización) · devolución (hasta $200) · cancelar un ticket (con autorización) · reimprimir (puede) · alta rápida (puede) |
| Efectivo y corte | Entradas y salidas (puede) · verificar transferencias (no) · hacer el corte de un turno (no) · ver y cerrar el corte del día (no) |
| Inventario | Recibir a ciegas y contar (puede) · merma (hasta $200) · ajustar una existencia (con autorización) · abrir un paquete (puede) · productos y etiquetas, compras y costos, órdenes y sugerido, conteo completo (no) |
| Clientes y crédito | Dar de alta clientes (no) · fiar arriba del límite (con autorización) · cancelar un abono (con autorización) · dar crédito (no) |
| Reportes | Ver reportes de ventas (no) · ver costos, utilidad y margen (no) |
| Administración | Configuración, usuarios y permisos, dar de alta cajas, bitácora (no) |

En el prototipo hay tres: **Ana** (los de defecto), **Carmen** (con productos, clientes, el corte de turno y
reportes, sin utilidad) y **Luis** (la primera cuenta, con todos). El cambio de precio sigue pidiendo a Luis, como hoy
lo autoriza el dueño.

**En el código:** `Rol` y `ReglaPermiso` (acción, rol, sucursal) pasan a permisos por usuario (usuario, acción, nivel,
umbral) más la tabla de los de defecto; `Permisos.Evaluar` recibe al usuario en lugar del rol. Al migrar, cada usuario
de hoy recibe los permisos de su rol: cajero, los de defecto; encargado, sus reglas actuales; dueño, todos. Lo que hoy
dice «solo el dueño» pasa a un permiso: la utilidad («ver costos, utilidad y margen», 2026-10-09), las reglas
(«usuarios y permisos») y los avisos de Telegram (a quien tenga el chat vinculado).

### Primera instalación

No está en el prototipo:

1. El instalador deja los permisos por defecto.
2. La caja busca la caja principal en la red. Si no la encuentra, el botón **«Conectar a servidor»** pide su dirección.
3. En la caja principal, la primera vez, se crea la primera cuenta (nombre y PIN) con todos los permisos.
4. Esa cuenta da de alta a los demás usuarios. Las otras cajas se dan de alta con el PIN de alguien que tenga «dar de
   alta cajas», en lugar del PIN del dueño.

## Corte

- **El del turno, solo con el total.** Quien tiene el permiso cuenta el efectivo y escribe cuánto hay, sin billete por
  billete y sin ver cuánto debería haber. Al cerrar ve lo esperado, lo contado y la diferencia, que queda a nombre del
  cajero del turno; si pasa del umbral, avisa por Telegram. Tarjeta y transferencias salen como información. Quien no
  tiene el permiso ve el corte X y el nombre de quién puede hacerlo.
- **El del día, para quien tiene «ver y cerrar el corte del día»** (la primera cuenta). Junta todas las cajas y turnos:
  - lo vendido;
  - el efectivo esperado y el contado en cada turno, con sus diferencias;
  - las formas de pago;
  - las salidas por categoría, las devoluciones y las cancelaciones;
  - lo fiado, los abonos y las impresiones cobradas.

  «Cerrar el día» se habilita cuando no queda ningún turno abierto ni ventas por subir de otra caja, y manda el resumen
  por Telegram.
- El conteo por denominación (la tabla `CorteConteo`) deja de usarse.

## Reportes

Una pestaña para quien tiene «ver reportes de ventas»:

- Hoy, semana, mes y año a la fecha, **siempre contra el mismo periodo del año pasado** (el año anterior sale del
  historial de eleventa).
- Venta neta, tickets y ticket promedio, con cuánto subieron o bajaron. La utilidad bruta, solo con «ver costos,
  utilidad y margen».
- Una gráfica: barras de este año y la línea del año pasado, con el detalle al pasar el ratón y «Ver como tabla».
- Por departamento (con el margen, si tiene el permiso), por cajero (con sus diferencias de corte), por forma de pago y
  lo que más se vende. Exportar a Excel.

El plan dejó los reportes para después de la prueba con una persona real. Aquí va su pantalla para aprobarla junto con
lo demás; se programan cuando toque.

## Configuración

Con muchas más opciones, se ordena para encontrarlas:

- **Un buscador** arriba: «redondeo», «fondo» o «impresora» llevan al ajuste aunque no se sepa en qué grupo está.
- **Grupos a la izquierda**, cada uno visible solo con su permiso.
- **Cada ajuste dice su alcance:** «Toda la tienda» o «Solo esta caja».
- **Se guarda solo,** sin botón de guardar; arriba dice a qué hora fue el último cambio.

Opciones propuestas (las que ya existen en el POS y las comunes en una caja):

| Grupo | Ajustes |
|---|---|
| Tienda | Nombre, dirección, teléfono, horario (para avisar si no abre o no se cerró el día), logotipo |
| Venta y cobro | Cobrar el efectivo al peso, formas de pago, comisión de la tarjeta, pedir la referencia de la terminal, si avisa al vender sin existencia (la venta nunca se detiene), sinónimos de la búsqueda, existencia en el ticket cuando queden pocas, tickets en espera, rápidos, días para devolver |
| Efectivo y corte | Límite de efectivo en caja, fondo sugerido, umbral de diferencia que avisa, para qué son las salidas, comprobante de salidas, hora para recordar cerrar el día |
| Ticket | Encabezado y pie, QR del folio, ahorro por mayoreo, qué hacer al cobrar (imprimir, preguntar, no imprimir), ticket de prueba, vista previa |
| Impresoras e impresiones | Impresora de tickets, precio por hoja (blanco y negro y color), cómo se avisa lo impreso en esa PC (bandeja con miniaturas, tarjeta con tiempo o solo el contador; por caja), recordarlo al cobrar, impresoras de inyección |
| Inventario | Margen mínimo (con el permiso de utilidad), productos del conteo del día, aviso de mínimo, días que cubre el sugerido, recibir a ciegas |
| Clientes y crédito | Plazo y límite con que empieza cada cliente, aviso de lo vencido, comprobante de abonos |
| Usuarios y permisos | Usuarios, sus permisos y los de defecto |
| Seguridad | Pedir el PIN tras minutos sin uso, intentos antes de bloquear, largo del PIN, PIN al autorizar por Telegram |
| Avisos por Telegram | El bot, de qué avisa, la hora del resumen del día, autorizar a distancia |
| WhatsApp | La lista «Escribieron por WhatsApp hace poco», de dónde sale, cuántas horas se muestran, a los cuántos días se borra el número de quien no es cliente registrado, el aviso «su trabajo está listo» |
| Cajas | Las cajas dadas de alta, su serie de folios y su conexión; dar de alta una |
| Respaldos | Cada cuánto, cuántos días se guardan, la copia fuera de la tienda, el último respaldo, respaldar ahora, restaurar |
| Esta caja | Panel del total (A o B), tamaño de letra, sonidos (apagados por ahora), pantalla táctil, reconocer el lector |
| Teclas | Las de eleventa, con la opción de cambiarlas por caja |
| Bitácora | Lo sensible: quitar, descuentos, autorizaciones, reimpresiones, cortes |
| Acerca de | Versión, buscar actualización, soporte a distancia |

En el prototipo, los ajustes marcados «cambia el prototipo» se aplican al momento: el redondeo, las formas de pago, los
tickets en espera, los rápidos, el límite de efectivo, el aviso de impresión, el panel del total, la letra y los
sonidos, entre otros.

## Caja principal y secundarias

La misma pantalla en todas; cambia solo lo que dice el estado de arriba:

| | Caja principal | Caja secundaria |
|---|---|---|
| Estado normal | «Servicio aquí · Caja 2 conectada» | «Conectada a Caja 1 · 9 ms» |
| Sin internet | «Sin internet»: Telegram y el respaldo de fuera esperan; la venta sigue igual | Igual |
| Sin la principal | (no aplica: el servicio está en la misma PC) | Franja ámbar: sigue vendiendo con su serie (B-), cuántas ventas esperan para subir y «Volver a buscarla» |
| Impresora sin respuesta | El cobro termina igual; «Reintentar» y «Verlo en pantalla» | Igual |

## Rendimiento

> **Aprobado por el dueño (2026-10-10):** escanear y buscar siempre contra la copia local de cada caja. Los productos
> nuevos se dan de alta pocos días a la semana, así que la copia casi siempre está al día. Y la existencia nunca
> detiene la venta: si el sistema dice que queda una y las dos cajas la escanean a la vez, el producto existe y la base
> es la que está mal.

Como con eleventa, que se congela unos milisegundos en cada escaneo, lo que molesta no es un escaneo suelto: son las
esperas que se suman cuando se pasan varios productos seguidos.

### Metas

Se miden en las PC de la tienda, con el simulador de cajas que ya pide el plan para el día pico. Lo que dice «hoy» sale
del código; lo marcado como medido se midió en la PC de desarrollo con datos ficticios.

| Momento | Meta | Hoy |
|---|---|---|
| Escanear → la línea en pantalla | En la caja 2 por Wi-Fi y con cualquier red (también caída): 16 ms la mitad de las veces y menos de 50 ms el 99 %. Ningún escaneo perdido en una ráfaga | Una consulta al servicio por escaneo, sin plazo propio (hasta 15 s con la conexión medio caída). Unos 28 repintados de toda la pantalla por código. En una ráfaga, un escaneo puede borrar o cortar el siguiente |
| El hilo de la pantalla, durante una ráfaga | Nunca trabado más de 50 ms | Esperas de unos 150 ms cuando la venta y la sincronización escriben a la vez en la base de la caja (medido) |
| Escribir → resultados de la búsqueda | Menos de 100 ms | 150 ms de espera y una consulta por la red |
| F12 → listo para teclear lo que pagó | Menos de 50 ms (solo mueve el cursor) | Abre una ventana |
| Cobrar → listo para el siguiente cliente | Menos de 300 ms; el ticket se imprime aparte | Espera a que salga el ticket |
| Cambiar de usuario | Menos de 3 s (ficha y 4 cifras) | Escribir usuario y PIN |
| Abrir la caja → lista para vender | Menos de 5 s | Por medir |

### Cómo se logra

De lo que más ayuda a lo que menos. Los tres primeros son los que van primero.

1. **El lector, en una sola llamada.** Hoy cada carácter del lector viaja de la página a .NET y repinta toda la
   pantalla (`@bind:event="oninput"`, unos 28 repintados por código). Además, el cuadro se vacía *después* de la
   consulta: si llega otro escaneo mientras tanto, se borra lo que el lector ya escribió de él.
   - **Propuesta:** la captura pasa a JavaScript. Al Enter lee el cuadro, lo vacía en ese momento y manda el código
     con su hora, en una sola llamada. Los escaneos se atienden en orden, uno detrás de otro.
   - El escaneo perdido se puede arreglar ya, sobre la pantalla de hoy, sin esperar al rediseño.
2. **Escanear y buscar contra la copia local, siempre.** Cada caja ya guarda su copia del catálogo para la
   contingencia; hoy solo se usa sin conexión.
   - **Propuesta:** una foto del catálogo en memoria, con un diccionario por código, armada fuera del hilo de la
     pantalla. Buscar un código toma fracciones de microsegundo (medido), y la red no entra en el escaneo.
   - **Si el código no está en la copia** (un producto recién dado de alta en otra caja), la línea entra como
     «buscando…» y se pregunta al servicio en segundo plano, con medio segundo de límite.
   - **El cobro manda la versión de la copia.** El servicio vuelve a poner los precios al cobrar; si cambiaron, la
     pantalla se actualiza antes de cobrar, para que el ticket diga lo mismo que la pantalla.
   - **La existencia nunca detiene la venta,** ni la de la copia ni la del servicio. Lo que quede negativo se revisa en
     el conteo ([inventario](inventario.md)).
3. **La base de la caja, fuera del hilo de la pantalla.** Un solo escritor para `caja-local.db`, en su propio hilo, y
   `busy_timeout` en todas las conexiones. Hoy, si la venta y la sincronización escriben a la vez, una espera unos
   150 ms; con `busy_timeout`, 14 ms (medido). `synchronous=FULL` se queda: ahí se guardan ventas y no hay no-break.
4. **Solo lo que cambió, avisado por la principal.** Hoy, con cualquier cambio, cada caja baja el catálogo completo:
   con 6,000 productos ficticios son 5.7 MB y 125 ms solo para leerlo (medido).
   - **Propuesta:** la principal avisa por SSE (un canal de solo ida, HTTP simple) cuando sube una versión, y la caja
     pide solo los cambios desde la suya. Las existencias llevan su propia versión. El latido queda de respaldo.
   - **Dos detalles por corregir:** hoy la versión sube con cualquier cambio, incluso un PIN mal tecleado, y borrar no
     la sube.
   - Como los productos nuevos se dan de alta pocos días a la semana, casi nunca hay mucho que bajar.
5. **La red con plazos y tres estados.** Un cliente HTTP para lo interactivo y otro para lo de fondo.
   - Cada operación con su plazo (leer, 800 ms; cobrar, 2 s) en lugar de 15 s para todo. Si el cobro no alcanza, va a la
     bandeja de salida con el mismo ID, sin duplicar.
   - Keepalive de TCP, para descubrir pronto una conexión que quedó medio muerta al reconectarse el Wi-Fi.
   - El latido mide la latencia y decide entre buena, lenta y caída. En «lenta», la caja trabaja como sin conexión, sin
     alternar a cada rato.
6. **Nada pesado dentro de una petición del servicio.** El catálogo se recarga en segundo plano y mientras se responde
   con el vigente. La versión y las llaves de las cajas, en memoria. Lo que se lee para cobrar se lee antes de abrir la
   transacción, para que dos cobros no se esperen entre sí.
7. **Pintar menos en Blazor.** Cada fila del ticket es un componente con `@key` y sin crear un manejador nuevo por fila
   en cada pintado: agregar una línea no repinta las demás. Los paneles se pintan dentro de la misma pantalla.
8. **Imprimir aparte.** El ticket entra a una cola de la caja y la pantalla queda libre al instante. La cola reintenta
   si la otra caja está imprimiendo (pendiente del plan: «reintento y botón Reintentar»).
9. **Arranque.** Publicar la caja con ReadyToRun, que ahorra compilar al abrir, sobre todo en un procesador modesto
   ([notas](notas.md#hardware-de-la-tienda)). La pantalla de entrada sale con la copia local, sin esperar a la red. Sin
   fuentes de internet: las letras de Windows (Segoe UI).
10. **Después, si hace falta: el cobro también primero en la caja.** La venta sin crédito ni impresiones usaría la serie
    de folios de la caja, como ya hace sin conexión, y subiría en segundo plano. Es lo más delicado (folios y corte):
    va después de medir lo anterior.

### Cómo se mide

- **Del Enter a la línea pintada,** con el reloj de la página: la mitad, el 95 % y el 99 %, por caja y por hora.
- **Un vigía del hilo de la ventana** que anota cuando se traba más de 30 ms, y qué pasaba: la sincronización, la base
  o la red.
- **Una prueba automática de la ráfaga:** 20 códigos (2 ms por carácter, 30 ms entre códigos) con la red simulada lenta
  y con pérdidas, mientras sube la versión del catálogo y la otra caja cobra. Debe dejar las 20 líneas en orden, sin
  códigos cortados y **sin ninguna petición a la red durante la ráfaga**. Esa última revisión no depende de tiempos, así
  que no falla al azar.
- **De noche, la misma ráfaga en una PC como la caja 2,** con la ventana real y un proxy que agrega retardo y pérdidas.

### La tecnología de la interfaz

| Opción | A favor | En contra | Veredicto |
|---|---|---|---|
| **Blazor en WebView2, rediseñado** (lo de hoy) | El código de .NET corre directo en la PC, sin WebAssembly, y pintar el ticket toma milisegundos. Se aprovechan ~12,400 líneas de componentes y la impresión ya probada. El diseño se hace con HTML y CSS, rápido de cambiar | WebView2 suma memoria (del orden de 150 MB, por medir). Las teclas del navegador se atrapan con JavaScript, como ya se hace | **Recomendada** |
| WPF nativo | Lo más ligero y el arranque más rápido | Rehacer todas las pantallas. Cambiar el diseño cuesta más | No: con 60–120 tickets al día, lo lento hoy es la red y la espera de la impresora, no dibujar la pantalla |
| WinUI 3 o Avalonia | Parecido a WPF | Lo mismo, y menos probado con las impresoras y la ventana actual | No |
| Web (React o Svelte) servida por el servicio | Muchas herramientas | Un segundo lenguaje: el núcleo en C# ya no se comparte con la pantalla | No |

Lo que ya no hace falta por Kotlin: MAUI y el diseño a medias entre celular y caja. La interfaz Blazor queda para la
caja (y para el panel del dueño, pospuesto con el central).

## La app Android en Kotlin

- Nativa (Kotlin con Jetpack Compose). Usa la **misma API del servicio** que las cajas.
- **El contrato, en un solo lugar:** ASP.NET Core 10 genera la descripción de la API (OpenAPI). Con ella se genera el
  cliente de Kotlin, así que si cambia un DTO, la app no compila hasta ajustarse.
- El celular se da de alta como una caja: llave propia y el PIN de alguien con «dar de alta cajas», una vez. Quien
  entra en el celular usa su mismo usuario, PIN y permisos. Sin publicar en la tienda de apps.
- **Mandar un ticket a una caja** (la preventa): se arma en el celular y llega a la caja elegida como ticket en espera
  ([arriba](#desde-el-celular)).
- Mientras llega (fase 8), el conteo desde el celular sigue en la página web que ya existe (`/conteo`).
- Sin base en el celular al principio: trabaja por Wi-Fi contra la caja principal. Si un día necesita trabajar sin
  red, Room, que también es SQLite.

## Base de datos

**Recomendación: seguir con SQLite.** Con el tamaño del negocio, otra base agrega trabajo sin dar nada a cambio.

- **El volumen:**
  - 60 tickets al día, con picos de 120: del orden de mil renglones diarios entre líneas, kardex, cobros y bitácora,
    unos 350 mil al año.
  - La base ficticia, con un año de ventas, pesa 25 MB.
  - SQLite maneja bases de varios GB sin problema.
- **Un solo escritor:** las cajas pasan por el servicio, que escribe solo. Es justo el caso para el que SQLite
  funciona mejor, y guardar una venta toma milisegundos.
- **Cero administración:** un archivo, sin servicio aparte que instalar, actualizar o vigilar en una tienda sin
  técnico. Los respaldos ya funcionan.

| Alternativa | Por qué no |
|---|---|
| PostgreSQL | Otro servicio en la caja principal que instalar, actualizar y respaldar, para un volumen que no lo necesita |
| SQL Server Express | Instalación pesada, límite de 10 GB y un motor más que mantener |
| Firebird (el de eleventa) | Solo sirve para leer la base vieja al importar |
| Bases de documentos embebidas (LiteDB y similares) | Se pierde SQL para el kardex y los reportes |

Ajustes:

- **`synchronous=FULL` explícito** y comprobado al arrancar. Hoy queda el valor con que viene compilado SQLite. Sin
  no-break, cada venta guardada debe sobrevivir a un apagón.
- **Revisión rápida al arrancar** (`PRAGMA quick_check`) cuando la caja principal se apagó de golpe, con aviso por
  Telegram si algo sale mal. Así se usa el respaldo antes de seguir encima de una base dañada.
- **Los tickets en espera, en `caja-local.db`** ([arriba](#tickets-en-espera)).
- **Los permisos por usuario** en lugar de las reglas por rol, con la migración de arriba
  ([usuarios y permisos](#usuarios-y-permisos)).
- **La configuración, en un solo lugar:** cada ajuste con su alcance (la sucursal o la caja) y su versión, para que
  las cajas lo bajen con el latido como el catálogo.
- **Cuándo reconsiderarlo:** solo si un día muchas máquinas escribieran a la vez en el mismo lugar. Ni la matriz con
  tres cajas ni el central pospuesto (que el plan ya pensó con SQLite) llegan a eso.

## Entregas

Cada entrega se prueba en el navegador y en la ventana de Windows, y la guía de la prueba con una persona real se
actualiza con ella.

**El orden (2026-10-10):** primero la versión unicaja (entrega 0): la caja nueva vendiendo sola en una PC, con datos de
prueba, para ver cómo avanza y cómo se comporta; puede tener errores o faltarle detalles. Las entregas que siguen se
ven en esa misma versión. El piloto es en la caja 2: vende con la caja nueva y manda cada venta a eleventa, como hoy;
además de la 0, pide el puente con eleventa y la 10 ([plan](plan.md#la-versión-unicaja-y-el-piloto-en-la-caja-2)). Los
números de siempre se quedan; la tabla va en el orden nuevo.

| # | Entrega | Tamaño | Cuándo |
|---|---|---|---|
| 0 | La versión unicaja, vender en una caja: la pantalla de venta nueva sobre el mismo servicio, cobro, ticket y base. El cuadro de captura con el lector en una sola llamada y las teclas que esperan al último escaneo; `12*código` y el folio; la búsqueda por pedazos de palabra con el nombre de eleventa como sinónimo, lo elegido antes y lo más vendido; la lista con + − y Supr; los rápidos (Copia B/N y CURP); el panel del total con F12, el exacto en gris y el cambio, sin billetes; los tickets en espera con F6 y F5; guardar lo que se busca; sin sonidos. Lo demás abre lo de hoy. Se elige por caja frente a la pantalla de hoy | Mediano a grande | Primero, para desarrollo |
| 10 | Ajustes de la base (`synchronous`, revisión al arrancar) | Chico | Antes del piloto |
| 3 | Venta, lo que falta después de la 0: «/» y el alta; el cobro mixto y la autorización dentro del panel; la flecha del cliente; «Más» en los tickets en espera; las familias, las fichas para afinar, el error de dedo y los sinónimos de la tienda, con lo que se guardó de las búsquedas reales; la lista con etiquetas y acciones en la fila; sonidos, si se quieren | Grande | Sobre la versión unicaja, mientras se arma el puente con eleventa |
| 2 | Base visual: colores, letras, botones, etiquetas, panel y barra de teclas; estado de arriba. Permisos de la sesión en una consulta y pantalla según lo que tiene cada quien | Mediano | Con la 3 |
| 1 | Permisos por usuario: el modelo, la migración desde los roles, la primera cuenta, «Conectar a servidor», los de defecto y la pantalla de usuarios y permisos | Mediano | Después de la 3 |
| 6 | Entrada con fichas y PIN; Ctrl+U; el corte del turno con solo el total y sin volver a pedir el PIN; el corte del día | Mediano | Con la 1 |
| 5 | Rendimiento para la caja 2: escanear contra la copia local, con su versión en el cobro; la base de la caja fuera del hilo de la pantalla; solo lo que cambió, avisado por SSE; plazos y estados de la red; imprimir aparte; menos repintado; la prueba de la ráfaga. El lector en una sola llamada ya va en la 0, y no perder escaneos en ráfaga ya está en `main` (#56) | Grande | Para dejar eleventa: la caja 2 con el servicio nuevo |
| 9 | Reportes, los que el dueño usa hoy en eleventa | Grande | Para dejar eleventa |
| 7 | Configuración nueva: buscador, grupos, alcance y guardado al momento | Mediano | Después |
| 8 | Las pantallas de atrás con el mismo patrón de lista y detalle: Productos (abre con la lista, no vacío), Inventario por tareas, Clientes | Grande | Después |
| 13 | Lo que se señala: el mostrador y la temporada con fotos en su acomodo (Ctrl+M), las fotos desde el celular, la hoja de la temporada y las tarjetas de precio, empezar y terminar la temporada, lo más preguntado | Mediano | Después |
| 4 | Impresiones: la bandeja con miniaturas y lotes por PC, el panel F2 de todas las PC, las miniaturas en el ticket y el recordatorio al cobrar | Mediano | Lo que eleventa no tiene, con el módulo de impresión |
| 11 | WhatsApp: la lista «Escribieron hace poco» y la liga con las impresiones, con la etapa 2 de WhatsApp | Mediano | Lo que eleventa no tiene |
| 12 | Ticket desde el celular, con la app Android (fase 8): armarlo, elegir la caja, el aviso a esa caja, la ficha que llega con su efecto y el QR sin red | Mediano | Lo que eleventa no tiene |

**La prueba con una persona real** ya empezó con la pantalla de hoy; se repite con la entrega 0, y lo que enseñen esa
prueba y el piloto se suma a la entrega 3.

## Preguntas para el dueño

- [ ] ¿El orden de las necesidades de la vista principal se parece a lo que pasa en la tienda? En especial: ¿cuántas
  veces al día se cobra una impresión recién hecha y cuántas copias se venden?
- [x] ¿Cuáles son los rápidos? Copia B/N ($2) y CURP ($10, que vende 10 impresiones) (2026-10-10).
- [ ] ¿Qué cajones, ganchos y cajas llevarían etiqueta? Propuesta: empezar por lo suelto que más se busca por nombre.
- [ ] ¿Qué exhibidores hay y qué temporadas se venden en el año? Propuesta: empezar con la vitrina del mostrador y con la
  siguiente temporada. ¿Quién toma las fotos al empezar cada una?
- [ ] Las impresiones: ¿la bandeja con miniaturas, siempre a la vista bajo el cobro, o la tarjeta que se va a los 20 s? En
  las dos se recuerda al cobrar.
- [ ] ¿Cada cajero puede hacer el corte de su propio turno, o solo quien tenga el permiso? Propuesta: solo con el
  permiso, como hoy el encargado.
- [ ] ¿El corte del día lo cierra solo la primera cuenta? ¿A qué hora se recuerda cerrarlo?
- [x] ¿Qué billetes salen como botones en el cobro? Ninguno: se escribe lo que dio (2026-10-10).
- [ ] WhatsApp: ¿se prueba la lista con Kapso (gratis si su plan admite coexistencia; si no, 25 USD al mes)? ¿Se pone el
  aviso de privacidad simplificado en el mensaje de bienvenida?
- [ ] Ticket desde el celular: ¿quién lo arma (alguien que atiende en el piso, el encargado)? ¿El lector de las cajas
  lee QR, para cuando no hay red?
- [x] ¿Pitido al escanear y al fallar? Sin sonidos por ahora (2026-10-10).
- [ ] ¿Alguna caja tiene o tendrá pantalla táctil?
- [ ] ¿Pedir el PIN otra vez tras unos minutos sin usar la caja? Por ejemplo, 10.
