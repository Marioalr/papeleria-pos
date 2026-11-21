# Productos › «Nuevo producto»: propuesta para que no confunda

Pantalla: `src/PapeleriaPos.Interfaz/Componentes/FichaProducto.razor` (877 líneas), que se abre en la columna del detalle de
`Productos.razor` (línea 176) con `ProductoId` vacío, y que es la misma para editar. Estilos en
`src/PapeleriaPos.Interfaz/wwwroot/caja.css` (líneas 173–184 y 1346–1367, 1428, 1463–1465, 1541–1543).
Maqueta: `maqueta.html`, junto a este archivo.

Lo que pidió el dueño: «un poco confuso». Lo que se le agrega: **el precio de venta (con IVA, lo que paga el cliente) se
escribe directo y es lo principal**; la ganancia ayuda, y sale del costo y del precio en los dos sentidos.

---

## 1. Lo que confunde hoy

En orden de pantalla. «L» = línea de `FichaProducto.razor`.

| # | Qué pasa | Dónde |
|---|----------|-------|
| 1 | **El precio está al fondo.** Para el caso de todos los días (un producto, un precio, un código) hay que pasar por Nombre, Departamento, Marca, Tipo, IVA, Activo, Variante, Se vende por, Activa, Mínimo, Máximo, Costo, la nota de la foto y toda la tabla de presentaciones antes de llegar a la casilla del precio: son **unos 20 Tab** desde el nombre (el código, unos 15). En eleventa el precio está en la primera pantalla, junto al costo. | L205–243 |
| 2 | **El código también está escondido**: es la cuarta columna de la tabla «Presentaciones y códigos», en un renglón que se llama «Pieza» con una casilla «Contiene» desactivada y un botón «Base». En eleventa el código es lo primero (se escanea lo que se tiene en la mano). | L164–200 |
| 3 | **El costo y el precio están en dos bloques distintos**: el costo arriba, en el encabezado de la variante; el precio y el margen abajo, en otra tabla. Para ver la ganancia hay que mirar dos lugares. | L133–139 vs L225–229 |
| 4 | **El margen solo se escribe en %**, sin ver cuántos pesos se ganan. La explicación está en un `title` que nadie ve (pasar el ratón). Escribirlo **sobrescribe el precio** y lo sube a los 50 centavos, **sin decirlo**. | L209, L605–612 |
| 5 | **El % no es el de eleventa** (por confirmar, ver §11): aquí es sobre el precio (`Margen.cs`: (precio − costo) ÷ precio); en eleventa la «ganancia» se escribe sobre el costo. Con $28.50 y $45.00 aquí sale 36.7 %; en eleventa, 57.9 %. Quien escriba «50» como en eleventa obtiene otro precio. | `Nucleo/Catalogo/Margen.cs` |
| 6 | El margen sin costo sale **desactivado con «sin costo»** y no dice qué hacer. Un margen de 100 % o más **se ignora sin aviso**. | L229, L608–609 |
| 7 | **Columnas de mayoreo en el caso sencillo**: «De [Cada pieza]» y «Desde (cantidad) 1» se ven siempre, aunque el 90 % solo tiene un precio. «Desde 1 es el menudeo; más cantidad, mayoreo» es lenguaje de sistema. | L207, L216–224, L250 |
| 8 | **Pregunta por variantes cuando no hay**: un recuadro «Producto» con la casilla «Variante (color, tamaño…)» y la indicación «Vacío si es la única». | L109–111 |
| 9 | **Dos casillas de «activo»**: «Se vende (activo)» del producto y «Activa» de la variante, que con una sola variante son lo mismo. | L102, L121 |
| 10 | **Tipo, IVA, Mínimo y Máximo a la vista** aunque casi nunca se cambian (16 %, mercancía, vacío). «el del sugerido» como indicación del máximo no lo entiende un empleado; su explicación está en `title`. | L89–101, L122–132, L434–438 |
| 11 | **Palabras de sistema**: «Presentaciones», «Contiene», «Base», «precio propio», «Código interno», «Variante». | L166, L173, L192, L220 |
| 12 | **Tres «Agregar…» con el mismo peso** (presentación, precio, variante) y cada renglón con su «Quitar»: el que llega no sabe cuál necesita. | L202, L245, L290 |
| 13 | «La foto se le pone después de guardarlo.» sale en un producto nuevo, donde no se puede hacer nada con ella. | L161 |
| 14 | **Errores lejos y genéricos**: salen abajo, en el pie, uno a la vez, sin llevar el cursor a la casilla. Un producto nuevo sin precio dice «Revisa los precios: cada uno lleva desde qué cantidad y cuánto.» (habla de cantidades que el usuario no tocó). | L295–298, L783–787 |
| 15 | La casilla del precio no tiene etiqueta propia (solo el encabezado de la tabla) ni `data-numero`, a diferencia del costo (L137). | L225 |
| 16 | Al abrir, el cursor va al primer `input` habilitado (`PrimerCampo`, L407), que hoy es el nombre; el código, que es lo que se escanea, queda lejos. | L407, L500 |

Lo que **sí funciona y se queda**: Ctrl+Enter guarda (L507–513), Esc cierra (Productos), el pie con «Guardar» siempre a la
vista (L294, `.vn-ficha-pie`), «Nuevo departamento» ahí mismo, las marcas sugeridas, «Agregar variante» que copia la primera
(L565–582), los permisos (`VeCostos` L441, `VeMargen` L444, `PuedeEditar` L467), lo de eleventa solo de lectura (L39, L102, L121,
L138, L173, L178–184, L216–228) y el aviso de cambios sin guardar de Productos.

---

## 2. El acomodo propuesto

Una idea: **arriba, lo que pide eleventa en su «Nuevo producto»; abajo, cerrado, lo demás**, con un renglón que dice qué
tiene cada sección para que nada quede escondido sin saberlo.

```
┌ Nuevo producto ──────────────────────────────────────────────────────────────┐
│ Código de barras  [▌ escanéalo aquí               ] (Sin código: generar uno) │
│ Nombre            [Cuaderno profesional 100 hojas                           ] │
│ Departamento [Cuadernos ▾] (Nuevo)     Marca [Scribe        ]                  │
│                                                                               │
│ ┌ Precio ───────────────────────────────────────────────────────────────────┐ │
│ │ Lo que te cuesta    Precio de venta (lo que paga el cliente)   Ganancia   │ │
│ │ [$ 28.50]       →   [$   45.00  ]  por [pieza ▾]               [36.7] %   │ │
│ │ Ganas $16.50 por pieza (36.7 % del precio). Precios con IVA.              │ │
│ └───────────────────────────────────────────────────────────────────────────┘ │
│                                                                               │
│ ▸ Inventario          Avisa con 3 o menos · sin máximo                        │
│ ▸ Mayoreo y cajas     Sin mayoreo ni cajas                                    │
│ ▸ Colores, tamaños…   Un solo tipo                                            │
│ ▸ Otros datos         IVA 16 % · se vende · lleva inventario                  │
├───────────────────────────────────────────────────────────────────────────────┤
│                                            [Cancelar]  [Guardar  Ctrl+Enter]  │
└───────────────────────────────────────────────────────────────────────────────┘
```

Por qué así:

- **Igual que eleventa en lo de arriba** (código, descripción, departamento, costo, ganancia, precio de venta, se vende por):
  quien viene de eleventa encuentra lo mismo en el mismo orden (primero lo que eleventa ya hace).
- **El precio es la casilla grande**, con letra de cifras (`--vn-f-cifras`), y **la ganancia se ve en pesos** además del %:
  los pesos no se prestan a la confusión del % sobre costo o sobre precio.
- **Una sola fila de precio** con el costo a la izquierda (lo que entra) y el precio a la derecha (lo que sale), la ganancia al
  final como ayuda: se lee de izquierda a derecha como una cuenta.
- **Secciones cerradas con resumen** (`<details>` nativo): se abren con Enter o espacio, el resumen dice lo que hay
  («Caja de 20 a $810.00 · mayoreo desde 12»), y **se abren solas si tienen algo** (al editar).
- El caso de 90 % cabe en una pantalla de 1366×768 sin desplazarse; el pie con «Guardar» no se mueve.

### Con más (variantes, caja, mayoreo)

- **Mayoreo y cajas** (una sección, porque en la tienda las dos son «vender más barato por cantidad»):
  - *Mayoreo*: renglones «Desde [12] piezas → [$40.00] cada una · ganas $11.50 (28.8 %)». «Agregar mayoreo».
  - *Cajas y paquetes*: renglones «[Caja] con [20] piezas · código [▌] · precio de la caja [$810.00] · ganas $240.00 (29.6 %)».
    Vacío el precio: «Sin precio propio: 20 × $45.00 = $900.00». «Agregar caja o paquete».
  - Debajo, en chico, «Mayoreo de la caja» solo si ya hay uno (precios de una presentación desde 2 o más: raros, pero existen).
- **Colores, tamaños…**: «Este producto viene en varios colores, tamaños o rayados». Al marcarlo, la fila de precio de arriba
  se vuelve una **tabla, una fila por cada uno**: Nombre (Raya, Cuadro chico…) · Código · Costo · Precio · Ganancia · Mínimo ·
  [Más…]. «Agregar otro» copia la fila anterior sin nombre ni código (lo de hoy, L576–581). «Más…» abre, debajo de esa fila,
  su mayoreo y sus cajas (copiados de la primera, como hoy) y «Se vende» de esa variante.
- **Otros datos**: IVA (16 % / 0 % / Exento), «Se vende» (activo), «Es un servicio (no lleva inventario)», y al editar: la foto,
  la existencia y el costo promedio, los proveedores, el kardex y depurar.

---

## 3. Orden de los campos y del Tab

El orden del marcado es el del Tab; nada de `tabindex` positivos.

**Caso sencillo, dueño (ve costos y ganancia):**

| Tab | Campo | id / clase | Enter |
|----:|-------|------------|-------|
| 1 | Código de barras (al abrir, el cursor está aquí) | `#ficha-codigo`, `input.codigos[data-escaneo=varios]` | pasa a Nombre (el Enter del lector también) |
| 2 | «Generar uno» (código interno) | botón | lo pone y pasa a Nombre |
| 3 | Nombre | `#ficha-nombre` | siguiente |
| 4 | Departamento | `#ficha-departamento` | — |
| 5 | «Nuevo» (departamento) | `#ficha-nuevo-departamento` | — |
| 6 | Marca (con sugerencias) | `#ficha-marca` | siguiente |
| 7 | Lo que te cuesta | `input.umbral.costo[data-numero]` | siguiente |
| 8 | **Precio de venta** | `#ficha-precio`, `input.umbral[data-numero]` | si todo lo obligatorio está, va a «Guardar»; si no, al que falta |
| 9 | Ganancia % | `input.umbral.margen[data-numero]` | pone el precio (§5) |
| 10 | Se vende por (pieza…) | `select` | — |
| 11–14 | Resúmenes: Inventario, Mayoreo y cajas, Colores…, Otros datos | `summary` | abre/cierra |
| 15 | Cancelar | | |
| 16 | Guardar | `#ficha-guardar` | guarda |

- Del código al precio son **7 Tab** (hoy, unos 20 desde el nombre, y el código está a 15).
- **Sin «Ver costos»**: no hay costo ni ganancia (Tab 7 y 9 no existen): código → nombre → departamento → marca → precio.
- **Con «Costear compras» pero sin «Ver utilidad»**: costo y precio, sin la ganancia (como hoy, L441/L444).
- Ctrl+Enter guarda desde cualquier casilla (L507). Esc cierra (Productos; con cambios, el aviso de siempre).
- El lector de código: con el cursor en cualquier parte de la ficha que no sea una casilla, el escaneo va al único campo de
  códigos que se ve (`caja.js`, `propios.length === 1`, ~L688). En el caso sencillo hay uno solo, así que **escanear con la ficha
  abierta siempre cae en «Código de barras»**. Con cajas abiertas hay varios: hay que estar en la casilla.
- Enter avanza como Tab en lo de arriba (como `AltaRapida.razor` L63–72: «lo que falta es el siguiente campo, no un error»).

---

## 4. Lo que va cerrado («más opciones»)

| Sección | Qué lleva | Resumen cerrado | Se abre sola si… |
|---------|-----------|-----------------|------------------|
| Inventario | Mínimo de inventario, Máximo (explicados en una línea visible, no en `title`), y al editar la existencia | «Avisa con 3 o menos · sin máximo» / «Mínimo 6 · máximo 24» | tiene mínimo o máximo |
| Mayoreo y cajas | Mayoreo de la pieza; cajas y paquetes con su código y su precio; mayoreo de una caja si ya existe | «Sin mayoreo ni cajas» / «Mayoreo desde 12 a $40.00 · Caja de 20 a $810.00» | tiene algún precio desde 2+, o una presentación que no es la base |
| Colores, tamaños… | La casilla «viene en varios…» y la tabla de variantes | «Un solo tipo» / «3: Raya, Cuadro chico, Cuadro grande» | tiene 2 o más variantes (y entonces queda abierta, porque los precios están ahí) |
| Otros datos | IVA, Se vende, Es un servicio; al editar: foto, costo promedio, proveedores, kardex, depurar | «IVA 16 % · se vende · lleva inventario» | IVA no es 16 %, no se vende, o es servicio |

Regla: el resumen **siempre** dice lo que hay adentro, así lo cerrado nunca engaña. Se usa `<details>`: lo cerrado **se
sigue dibujando** (las pruebas lo encuentran, ver §10) pero el Tab no entra hasta abrirlo.

Lo que **se quita** del caso sencillo: el recuadro «Producto/Variante 1», la casilla «Variante (color, tamaño…)», la casilla
«Activa» de la variante (con una sola, «Se vende» del producto pone las dos), el renglón «Pieza» con «Contiene» y «Base», las
columnas «De» y «Desde», y la nota de la foto en un producto nuevo.

---

## 5. Precio, costo y ganancia

Definiciones (las del sistema, `Margen.cs`): todo **con IVA**; ganancia en pesos = precio − costo; ganancia % = (precio − costo)
÷ precio, con un decimal (`"0.#"`, como hoy L602).

**El precio es el que manda.** Nada cambia un precio que se escribió, salvo escribir la ganancia %.

| Se escribe… | Qué pasa | Ejemplo (costo $28.50) |
|-------------|----------|------------------------|
| **Precio** | Se queda tal cual, al centavo, con la regla de la coma del POS (`Formato.LeerPesos`). Se recalculan ganancia en pesos y %. | $45.00 → «Ganas $16.50 por pieza (36.7 %)» |
| **Costo** con precio ya escrito | El precio no se mueve; la ganancia se recalcula (lo de hoy, L614–619). | costo $30.00 → «Ganas $15.00 (33.3 %)» |
| **Costo** sin precio, con una ganancia % escrita antes | Sale el precio de esa ganancia (fila siguiente). | 40 % escrito, costo $28.50 → $47.50 |
| **Ganancia %** (con costo) | Precio = costo ÷ (1 − %), **subido a los siguientes 50 centavos** (`Margen.PrecioSugerido`, como el sugerido de compras). La ganancia se vuelve a mostrar con el precio redondeado, y una línea dice: «Redondeado a $45.50: la ganancia queda en 37.4 %». | 37 % → $45.24 → **$45.50** (37.4 %) · 40 % → $47.50 (40 %) |
| **Ganancia %** sin costo | La casilla no se desactiva: se puede escribir y queda guardada para cuando llegue el costo; abajo dice «Escribe lo que te cuesta para calcular el precio». | — |
| Ganancia ≥ 100 % o negativa | No pone precio y lo dice: «La ganancia va de 0 a 99 % del precio». | — |
| Precio debajo del costo | **No bloquea** (casi no se pide autorización); se ve en rojo: «Con este precio pierdes $3.50 por pieza.» | $25.00 |
| Precio igual al costo | «No ganas nada con este precio.» | $28.50 |

- La ganancia en pesos y % es **texto que se actualiza al escribir** (`@bind:event="oninput"` en costo y precio), no hay que salir
  de la casilla para verla. El precio se sigue tomando del texto escrito al guardar (`@bind` normal), sin reformatearlo mientras
  se escribe.
- **Mayoreo**: cada renglón con su ganancia, con el costo de la pieza. Escribir su % hace lo mismo que arriba.
- **Caja**: su costo es costo × lo que trae (lo de hoy, `CostoDe`, L587–597). Sin precio propio se muestra «20 × $45.00 =
  $900.00» (no se guarda un precio: se vende como hoy, por piezas).
- **Variantes**: cada fila con su costo, su precio y su ganancia; «Agregar otro» copia costo, precio y ganancia de la fila de
  arriba (hoy copia los de la primera).
- En el piloto, un precio de eleventa se ve con su ganancia, pero ni el precio ni el % se escriben (el costo tampoco, L138).

---

## 6. Validaciones y mensajes

Al guardar: se revisa todo, el cursor va a **la primera casilla con problema**, el mensaje sale **junto a esa casilla** (y,
como hoy, en el pie). Lo que no impide guardar es aviso (ámbar), no error (rojo).

| Caso | Mensaje | Bloquea |
|------|---------|---------|
| Sin nombre | «Escribe el nombre del producto.» (el de hoy, L755) | Sí |
| Sin departamento | «Elige el departamento.» (L760) | Sí |
| Sin precio o en cero | «Escribe el precio de venta (lo que paga el cliente).» | Sí |
| Precio con coma de centavos | «Revisa los precios: los centavos van con punto: 12.50 (la coma solo separa los miles: 1,250.00).» (el de hoy, `Formato.ProblemaDePesos`) | Sí |
| Costo con coma o no es número | «Revisa el costo de «…»: …» (L806) | Sí |
| Ganancia ≥ 100 o < 0 | «La ganancia va de 0 a 99 % del precio.» (al salir de la casilla) | No (no pone precio) |
| Precio debajo del costo | «Con este precio pierdes $X por pieza.» | No |
| Sin código | «Sin código se vende buscándolo por nombre. ¿Generar uno?» (aviso suave, solo al guardar un nuevo) | No |
| Código que ya tiene otro producto | El del servicio, junto al código; después, al escanear (§11) | Sí (servicio) |
| Mayoreo desde 1 o menos | «El mayoreo es desde 2 piezas o más.» | Sí |
| Mayoreo repetido (misma cantidad) | «Ya hay un mayoreo desde 12.» | Sí |
| Mayoreo más caro que la pieza | «El mayoreo sale más caro que la pieza ($45.00).» | No |
| Caja sin nombre o que trae 1 o menos | «Escribe cuántas piezas trae la caja (2 o más).» (hoy: «Revisa cuánto contiene «…»», L771) | Sí |
| Varias variantes, una sin nombre o repetida | «Ponle nombre a cada uno: Raya, Cuadro chico…» / «Hay dos «Raya».» | Sí |
| Mínimo / máximo | «Revisa el mínimo y el máximo de «…»: una cantidad, o vacío.» (L796) y lo del servicio | Sí |

---

## 7. Al editar un producto que ya existe

- **El mismo acomodo** (una sola pantalla para nuevo y editar, como hoy). Título «Editar «nombre»» (Productos, L314).
- El cursor va al **precio** (lo que más se edita), no al código: `PrimerCampo` pasa a ser un parámetro (nuevo: el código;
  editar: el precio; si el precio es de eleventa, el primer campo habilitado, como hoy).
- Las secciones con datos **se abren solas** (§4) y su resumen lo dice.
- **Inventario** muestra la existencia y el costo promedio (hoy L141–150) arriba del mínimo y el máximo.
- **Otros datos** lleva la foto (`FotoDeLaVariante`, L154–158), los proveedores (L253–256), «Ver kardex» y depurar
  (L257–286), igual que hoy.
- «Lo dio de alta un cajero desde la caja» (L34–37) dice qué le falta en concreto: «Revisa el departamento (el cajero dejó
  «X»), la marca y el costo». El alta rápida pone el primer departamento (`AltaRapida.razor` L46), así que hay que revisarlo.
- Con 2+ variantes la tabla de variantes queda abierta y la fila de precio de arriba no se ve (los precios están en la tabla).

## 8. Productos de eleventa (piloto)

- La nota de arriba se queda (`_enEleventa`, L30–33) y **cada casilla bloqueada lleva un candado** con «Se cambia en eleventa»
  escrito al lado (no solo en `title`): nombre, «Se vende», «Se vende» de la variante, los códigos de la pieza (como chips,
  `.codigos-fijos`, con «Otro código» habilitado para agregar), el precio de menudeo y las promociones de eleventa, el costo.
- Se cambian aquí (como hoy): departamento, marca, mínimo y máximo, la unidad (solo entre pieza, pieza a granel, metro y litro:
  la lista **solo ofrece esas cuatro** a una variante de eleventa, en lugar de dejar que el servicio rechace «Kilo»), otros
  códigos, cajas, mayoreo de aquí.
- La ganancia de un precio de eleventa se ve (texto), pero el % no se escribe.
- No se depura (L262) — sin cambio.
- El cursor no va a una casilla bloqueada (regla de hoy).

## 9. Permisos (sin cambio en el servicio)

| Quién | Ve |
|-------|----|
| Sin «Productos, precios y etiquetas» | La ficha de solo lectura y «El catálogo lo editan el dueño y el encargado» (L311) |
| Con editar, sin costos | Código, nombre, departamento, marca, **precio**; sin costo ni ganancia en ninguna parte (tampoco en mayoreo, cajas ni variantes) |
| Con «Costear compras» | + costo |
| Con «Ver costos, utilidad y margen» | + ganancia en pesos y % |

---

## 10. Plan de implementación

Sin cambios en el servicio ni en los contratos para lo principal. `FichaProductoDto`, `VarianteFichaDto`,
`PresentacionFichaDto`, `PrecioFichaDto` siguen iguales: solo cambia cómo se dibujan.

**Pasos (un PR, en este orden):**

1. **Encabezado** (L38–103): poner arriba «Código de barras» atado a `Variantes[0].Presentaciones.Single(EsBase).Codigos`
   (con `Fijos` como chips para eleventa) y «Generar uno» (`CodigoInternoAsync`). Tipo, IVA y Activo bajan a «Otros datos».
   «Tipo» se vuelve casilla «Es un servicio (no lleva inventario)».
2. **Fila de precio**: nuevo bloque que, con una sola variante, ata costo a `Variantes[0].UltimoCosto` y precio/ganancia al
   `PrecioEditable` de menudeo (`Presentacion == ""` y `Desde == "1"`; se crea si no existe). Texto de ganancia en pesos con
   un método nuevo `GananciaDe(variante, precio)` junto a `MargenDe` (L600). `PrecioDelMargen` (L606) devuelve si redondeó
   para mostrar «Redondeado a…». Cambiar el `return` silencioso de L608–609 por un aviso. La ganancia sin costo se guarda en
   `PrecioEditable.Margen` y se aplica al llegar el costo (en `RecalcularMargenes`, L615, cuando el precio está vacío).
3. **Secciones `<details>`** (Inventario, Mayoreo y cajas, Colores…, Otros datos), cada una con un método `ResumenDe…()` y
   `open` calculado al cargar (`Mostrar`, L515). Lo de L141–162 y L253–286 se mueve a «Otros datos»/«Inventario» sin cambiar
   su lógica.
4. **Mayoreo y cajas**: dos listas filtradas sobre las mismas colecciones: mayoreo = `Precios` con `Presentacion == ""` y
   `Desde > 1`; cajas = `Presentaciones` con `!EsBase`, cada una con su precio = `Precios` con su nombre y `Desde == 1`.
   El radio «Base» (L173, `HacerBase`) solo aparece si el producto ya tenía una base que no es la primera (casos viejos).
5. **Variantes**: con 2+ (`p.Variantes.Count > 1`, o al marcar la casilla) se dibuja la tabla; con una, nada de
   `fieldset`/«Variante». «Se vende» del producto pone también `Variantes[0].Activa` cuando hay una sola.
6. **Teclado**: Enter avanza en el encabezado y la fila de precio; `PrimerCampo` según nuevo/editar; `data-numero` en
   precio y ganancia; errores con el id de la casilla para `Js.EnfocarAsync`.
7. **Validaciones** de §6 en `GuardarAsync` (L746–830): mensajes por casilla (`Dictionary<string,string> _errores`) además de
   `_error`.
8. **CSS** (`caja.css`, junto a L173–184): `.ficha-precio` (la fila), `.ficha-seccion` (details/summary con resumen),
   `.ficha-ganancia` (texto, rojo si pierde), `.ficha-candado`; con media query de ≤ 900 px a una columna. Las clases de hoy
   (`umbral`, `umbral costo`, `umbral margen`, `umbral minimo`, `umbral maximo`, `codigos`, `codigos-fijos`, `de-eleventa`) **se
   quedan con el mismo nombre**: las pruebas las buscan.

**Riesgos para las pruebas** (`tests/PapeleriaPos.Pruebas/Interfaz`):

| Prueba | De qué depende | Qué hacer |
|--------|----------------|-----------|
| `PaginasTests.cs:161, 168, 172`, `DepartamentosTests.cs:238` | esperan el texto «Presentaciones y códigos» para saber que abrió la ficha | Cambiarlas a `EsperarElementoAsync("ficha-nombre")` (o dejar ese texto como título de la sección de cajas; mejor lo primero) |
| `ComaEnElDineroTests.cs:248` | espera «Precios (con IVA)» | Igual: esperar `ficha-precio`/`ficha-nombre`. L250 busca `Clase == "umbral"` con «20.00»: sigue si el precio conserva la clase exacta `umbral` |
| `ComaEnElDineroTests.cs:255` | el texto exacto «Revisa los precios: los centavos van con punto…» | Conservar el prefijo «Revisa los precios:» en ese caso |
| `PantallasConTecladoTests.cs:760, 774` | el precio es el **último** `input` de clase exacta `umbral` vacío | En un nuevo no hay renglones de mayoreo ni cajas: se cumple. Las casillas de precio de mayoreo/caja deben llevar otra clase (`umbral mayoreo`, `umbral caja`) para no ser «el último» si alguien las agrega |
| `PantallasConTecladoTests.cs:748` | `FocoEnAsync(c, FichaProducto.PrimerCampo)` | Si `PrimerCampo` deja de ser constante, exponer la del nuevo (`#ficha-codigo`) y actualizar |
| `AltaDeProductoTests.cs:88–95` | `umbral costo`, `umbral margen`, «Agregar variante», dos casillas de costo tras agregar | El botón pasa a «Agregar otro» dentro de «Colores, tamaños…»: o se conserva el texto «Agregar variante» o se actualiza la prueba. Las clases se quedan |
| `AltaDeProductoTests.cs:96–100` | placeholder «Azul, Carta…» en el nombre de cada variante | Conservar ese placeholder en la columna Nombre de la tabla de variantes |
| `TrastiendaEnElPilotoTests.cs:116` | **exactamente 2** checkbox desactivadas (Activo y Activa) en un eleventa de una variante | Si con una variante solo se dibuja «Se vende», la cuenta baja a 1: dibujar la de la variante dentro de «Otros datos» (cerrada, pero dibujada) o actualizar la prueba. Recomiendo actualizarla: dos casillas para lo mismo es parte de lo que confunde |
| `TrastiendaEnElPilotoTests.cs:117–129` | `codigos-fijos`, placeholder «Otro código», «de eleventa», `umbral minimo/maximo`, casillas con «21.00» y «Caja 12» habilitadas, sin «Dar de baja» | Se cumplen si se conservan clases y textos; lo de `<details>` cerrado se sigue dibujando (las pruebas ya oprimen botones dentro del `<details>` «Más» de Productos sin abrirlo, p. ej. `PreciosDeVariosTests.cs:118`) |
| `ReportesInventarioTests.cs:241` | el texto «Mínimo de inventario» | Conservar la etiqueta |
| Ids `ficha-nombre`, `ficha-departamento`, `ficha-departamento-nombre`, `ficha-nuevo-departamento`, `ficha-marca`, `ficha-guardar`, `ficha-campos` | varias pruebas | Conservarlos todos |

Después: correr la suite completa con Firebird como en el CI (dos suites con `taskset -c 0-3`) y ver la ficha en Windows el
fin de semana (no entre semana).

---

## 11. Recomendaciones (para después)

- **«Ya hay en la tienda»** en un producto nuevo (eleventa lo tiene en su «Nuevo producto»: «Hay»): guardaría y después
  haría un «Agregar inventario» con esa cantidad. Pide tocar el guardado (dos llamadas). Paridad con eleventa: primero.
- **Margen mínimo del departamento** junto a la ganancia («Debajo del mínimo de Cuadernos: 30 %»): `OpcionesCatalogoDto`
  (`Contratos.cs:1160`) no lo trae; habría que agregarlo.
- **Código repetido al escanear**, no al guardar: «Ese código ya es de «…» (Abrirlo)».
- **Confirmar en la tienda (sábado o domingo)** cómo calcula eleventa su «% de ganancia»: con un producto de $10 de costo y 50 %,
  ¿da $15 (sobre costo) o $20 (sobre precio)? Si es sobre costo, la etiqueta debe decir «del precio» siempre y el resumen en
  pesos es lo que se lee primero. Sin preguntarle al dueño: se ve en eleventa.
- En el piloto, ¿«Nuevo producto» aquí o en eleventa? El Excel ya no deja dar de alta productos nuevos en el piloto
  (`PlanDeImportacion.cs:755`); la ficha sí. Si se decide igual que el Excel, una línea arriba lo diría.

## 12. Decidido sin el dueño (por revisar)

- El cursor empieza en el código (como eleventa), no en el nombre.
- La ganancia en % es la del sistema (sobre el precio), con los pesos al lado.
- El precio derivado del % sube a los 50 centavos (como compras); el precio escrito no se redondea.
- Precio debajo del costo: aviso, no bloqueo.
- Mayoreo y cajas en una sola sección.
- Con una variante, una sola casilla «Se vende».
