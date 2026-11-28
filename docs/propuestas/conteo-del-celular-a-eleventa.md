# El conteo con el celular, reportado a eleventa

Plan (2026-11-27), **por aprobar**. Base: `origin/main` en 88a3a4e (después de #215). Lo pidió el dueño el 2026-11-27 como
el siguiente hito después del ensayo en la matriz: contar la mercancía con el celular, aceptarlo en el POS y que el POS **le
reporte el resultado a eleventa**, que sigue siendo el sistema oficial. Aquí no hay código: es lo que se averiguó, la
propuesta, las entregas en orden y lo que falta decidir.

Los ejemplos son inventados. Aquí no va ningún nombre, folio, monto ni cantidad de la base de la tienda.

## La respuesta corta

**Hoy no se puede.** Faltan tres cosas, y ninguna es un detalle:

| # | Qué falta | Dónde se ve |
|---|---|---|
| 1 | **El POS rechaza el conteo en el piloto.** El celular pregunta a qué caja mandar y todas contestan que no reciben (`piloto-eleventa`); mandarlo o aceptarlo contesta 409. En Inventario (F4) la tarea «Conteos del celular» ni se ofrece | `ApiConteosDelCelular.cs:183-185` (para-contar y mandar-conteo), `:327-328` (aceptar); `Paginas.cs:320-324` (las tareas que se esconden); la regla, en `TrastiendaEnElPiloto.cs:27-29` y `:76-80` |
| 2 | **El puente no sabe escribir un ajuste de inventario en eleventa.** Solo toca el inventario de allá al mandar una venta («Venta #folio») o una devolución («Devolución de venta #folio»). La bandeja no tiene un tipo de envío para ajustes, y en todo el repositorio no aparece la tabla donde eleventa guarda sus ajustes (`INVENTARIO_AJUSTES`) | `EnvioEleventa.cs:601-617`, `EnvioEleventa.Devoluciones.cs:349-369`; los tipos de la bandeja, `EnvioAEleventa.cs:38-53` |
| 3 | **La app todavía no llama las rutas del conteo** (`para-contar`, `mandar-conteo`, `conteos`). Cuenta y guarda la lista en el celular, pero no la manda | Según lo que se sabe de la app 0.5.4; su sesión lo confirma con el [encargo](#el-encargo-para-la-app-android) |

Aunque se quitara el rechazo del punto 1, no serviría: la lectura de cada 5 minutos deja la existencia de cada producto en
lo que diga eleventa (`SincronizacionEleventa.cs:664-687`), así que el conteo se borraría solo en unos minutos. Y hay un
cuarto detalle que hoy no se nota porque todo está cerrado: al aceptar, el POS toma cualquier ajuste de la lectura hecho
después de contar como «ya se contó después» y no aplica ese producto (`ApiConteosDelCelular.cs:433-436`; la lectura
ajusta con un movimiento de tipo conteo, `RegistroInventario.cs:68`). En el piloto eso pasaría con cada producto que
eleventa vendiera entre contar y aceptar.

Lo bueno: del lado del POS casi todo lo demás ya existe (recibir la lista, la pantalla de la caja para aceptarla con el
teclado, el permiso, la bitácora, «lo vendido desde que se contó»), y eleventa guarda sus ajustes de una forma sencilla que
ya se pudo leer completa en una copia.

## Lo que se averiguó de eleventa

Fuentes: el esquema completo (tablas, triggers, procedimientos y contadores) y los renglones históricos de una **copia** de
la base de la tienda del 2026-10-24, leída en esta máquina con el Firebird embebido de 32 bits (solo lectura, sobre una
copia de la copia); y lo que ya estaba escrito en [migracion-eleventa.md](../migracion-eleventa.md) y en el código del puente.
No se tocó ningún equipo de la tienda.

### Comprobado en la copia

**Eleventa no tiene un módulo de conteo o inventario físico.** En el esquema no hay ninguna tabla de conteos. Lo que tiene
es el ajuste de un producto a la vez (Inventario › Ajustes) y agregar inventario (Inventario › Agregar). La tienda ya cuenta
así: cerca de la mitad de todos los ajustes de la copia se capturaron en unos pocos días, uno tras otro, con segundos o un
minuto entre cada uno. Eso es un conteo capturado a mano, producto por producto.

**Un ajuste a mano deja tres cosas, y nada más que se alcance a ver en los datos:**

| Tabla | Qué queda | Ejemplo inventado |
|---|---|---|
| `INVENTARIO_AJUSTES` | Un renglón por ajuste. `ID` del contador `GEN_INVENTARIO_AJUSTES_ID`; `FOLIO` **igual al `ID`** (en todos); `CUANDO_FUE` al segundo; `PRODUCTO_ID`; `CANTIDAD` = **la diferencia con su signo** (lo nuevo menos lo que había); `COSTO_UNITARIO` = el costo del producto en ese momento (`PRODUCTOS.PCOSTO`); `DESCRIPCION` = **el nombre del producto**; `MOTIVO` = lo que escribió quien ajustó o, si no escribió nada, «Ajuste de inventario #folio»; `CAJA_ID`, `USUARIO_ID`, `ALMACEN_ID` = 1 | Había 12 y se contaron 9: `CANTIDAD` −3, `DESCRIPCION` «Lápiz HB», `MOTIVO` «Ajuste de inventario #412» |
| `INVENTARIO_HISTORIAL` (el kardex) | Un renglón: `AJUSTE_ID` = el del ajuste (los de venta, recibo y transferencia, vacíos); `CANTIDAD_ANTERIOR` = lo que había; `CANTIDAD` = la misma diferencia con signo; `DESCRIPCION` = **el `MOTIVO`** del ajuste (en todos); `COSTO_UNITARIO` igual al del ajuste; `CAJA_ID` y `USUARIO_ID` iguales a los del ajuste; `VENTA_POR_KIT` 'f'; `ALMACEN_ID` 1. La hora es la del ajuste (en unos cuantos, un segundo después) | `CANTIDAD_ANTERIOR` 12, `CANTIDAD` −3, «Ajuste de inventario #412» |
| `INVENTARIO_BALANCES` | `CANTIDAD_ACTUAL` queda en lo anterior **más** la diferencia | 9 |

- **El signo va al revés que en las ventas.** En el kardex de eleventa una venta lleva la cantidad en positivo y la
  existencia baja; una devolución, en negativo y sube (así las escribe ya el puente). En un ajuste, una recepción y un alta
  es al contrario: lo que sigue es lo anterior **más** la cantidad. Se comprobó con la cadena del kardex: en todos los
  ajustes que tienen un movimiento después, ese movimiento empieza en `CANTIDAD_ANTERIOR + CANTIDAD` del ajuste; en las
  ventas y devoluciones, en `CANTIDAD_ANTERIOR − CANTIDAD`. Y donde el ajuste es lo último del producto, el balance es
  exactamente eso. Este es el dato que más importa: con el signo equivocado, el conteo dejaría la existencia al doble de mal.
- **Los triggers no hacen nada más que poner el `ID`.** Todos los de la base son «si el ID viene vacío, toma el siguiente
  del contador». No hay ninguno que mueva existencias, ni procedimientos de inventario. Lo que no se escriba, no pasa solo.
- **`FOLIO` no sale de su contador.** Existe `INVENTARIO_AJUSTE_FOLIO`, pero está en su valor inicial, muy por encima del
  último folio: eleventa no lo usa. Lo mismo pasa con las recepciones (`INVENTARIOS_RECEPCION_FOLIO`).
- **No lleva turno.** Ninguna de las tres tablas tiene columna de turno, y un ajuste no toca `TURNOS` ni el corte. (Todos
  los de la copia se hicieron con un turno abierto en esa caja, pero porque eleventa no deja entrar sin abrirlo.)
- **`PRODUCTOS` no cambia:** `DINVENTARIO` sigue desactualizado después de un ajuste, como ya decía
  [migracion-eleventa.md](../migracion-eleventa.md#cómo-guarda-eleventa-los-datos-esquema-216).
- **Todos los productos activos tienen su renglón en `INVENTARIO_BALANCES`** y todos llevan inventario. Hay existencias
  con fracciones (lo de a granel) y eleventa admite negativas.
- **Cómo se ve lo demás del kardex,** para no confundirlo: «Recepción de inventario #folio» y «Alta inicial de producto»
  llevan `RECIBO_INVENTARIO_ID` (tabla `INVENTARIO_RECIBOS` y su detalle); las ventas y devoluciones, `VENTA_ID`. La tabla
  vieja `HISTORIAL_INVENTARIO` dejó de usarse en octubre de 2024.
- **A nombre de quién:** casi todos los ajustes de la copia son de un mismo usuario, desde dos cajas.

### Por comprobar en la tienda

Esto no se puede saber sin ver a eleventa hacerlo. Es un paso del plan ([entrega 0](#entrega-0-mirar-a-eleventa-ajustar-15-minutos-en-la-tienda)): un ajuste a mano en eleventa contra la **copia de ensayo**, con `observar-eleventa` tomando la foto antes y después.

| Qué | Por qué importa | Qué mirar |
|---|---|---|
| Si un ajuste toca alguna otra tabla | En los datos solo se ven las tres de arriba, pero una tabla que se reescribe (configuración, caché, sincronización con la nube) no deja huella en el historial | La lista de tablas cambiadas entre las dos fotos: deben ser solo `INVENTARIO_AJUSTES`, `INVENTARIO_HISTORIAL` e `INVENTARIO_BALANCES`, y los contadores `GEN_INVENTARIO_AJUSTES_ID` y `GEN_INVENTARIO_HISTORIAL_ID` |
| De dónde saca el `FOLIO` | En la copia siempre es igual al `ID`; hay que ver que `INVENTARIO_AJUSTE_FOLIO` no se mueve | El valor de ese contador antes y después |
| `COSTO_DESPUES` del kardex | En más de 9 de cada 10 ajustes es igual a `COSTO_UNITARIO`. En el resto es otro número y no se pudo deducir de dónde sale. En las ventas siempre son iguales | Ajustar un producto hacia arriba y otro hacia abajo y ver los dos costos contra `PRODUCTOS.PCOSTO` |
| Qué pregunta la ventana «Ajustar»: la cantidad nueva o la diferencia | Importa para la [alternativa](#la-alternativa-solo-la-lista-de-diferencias) (qué número se le dicta a quien captura) | La ventana misma |
| Si eleventa lee «lo que había» al abrir la ventana o al guardar | Si es al abrir, un ajuste a mano mientras se vende ya pierde ventas hoy; no cambia el diseño, pero conviene saberlo | Abrir «Ajustar», vender una pieza de eso en la otra caja, y guardar |
| Cómo enseña eleventa un ajuste que no escribió él | Es lo que verá el dueño: el reporte de movimientos de inventario y el kardex del producto, con un motivo propio («Conteo del celular…») y con la caja y el usuario que ponga el POS | Después del ensayo de la [entrega 4](#entrega-4-ensayo-en-la-tienda-contra-la-copia-sábado-o-domingo), abrir esos reportes en el eleventa de ensayo |
| Si el eleventa abierto en otra caja ve la existencia nueva sin reiniciar | Para saber si hay que avisar «cierra y abre la pantalla de inventario» | Tener abierta la ficha del producto en eleventa mientras llega el conteo |

### Visto en la matriz (2026-11-27, entrega 0)

El dueño ajustó en el eleventa de la caja principal, con `observar-eleventa` leyendo la base real. Cuatro ajustes (bajar 1
con motivo, subir 1 sin motivo, bajar 0.5 a uno de a granel y dejar uno en cero) y el intento de ajustar mientras la otra
caja vendía.

| Qué se quería saber | Qué se vio |
|---|---|
| Si toca alguna otra tabla | No. En los cuatro, solo `INVENTARIO_AJUSTES`, `INVENTARIO_HISTORIAL` e `INVENTARIO_BALANCES`, y los contadores `GEN_INVENTARIO_AJUSTES_ID` y `GEN_INVENTARIO_HISTORIAL_ID` |
| De dónde sale el `FOLIO` | Es el `ID`. `INVENTARIO_AJUSTE_FOLIO` no se movió |
| El signo | Confirmado: `CANTIDAD` es la diferencia con su signo, igual en las dos tablas, y el balance queda en lo anterior más eso. Con fracción y hasta cero, igual |
| El motivo | Sin escribir nada: «Ajuste de inventario #folio». Escribiendo algo: **«Ajuste: » más lo escrito** (esto corrige la tabla de arriba; en la copia, 17 de 236 llevan ese prefijo y el resto son sin motivo) |
| `COSTO_DESPUES` | Igual a `COSTO_UNITARIO` en los cuatro. El caso raro de la copia no salió: se escribe igual al costo |
| Hora | La misma, al segundo y sin milésimas, en el ajuste y en el kardex |
| Si lee «lo que había» al abrir o al guardar | **Al guardar, y se niega:** con la ventana abierta se vendió una pieza en la otra caja, y al guardar eleventa avisó que el inventario cambió mientras se hacía el ajuste y no guardó nada. Un ajuste a mano no pisa ventas; el POS debe hacer lo mismo (leer y escribir en la misma transacción) |
| `PRODUCTOS` | En tres ajustes no cambió. En el que dejó un producto en cero cambió su `PORCENTAJE_GANANCIA` (de 20 a lo que sale de su precio y su costo). El dueño confirmó que solo usó «Ajustar»: la ventana recalcula ese porcentaje cuando el guardado no coincide con el precio y el costo. Es un dato derivado; el POS no lo escribe |

Sigue sin verse: cómo enseña eleventa un ajuste que no escribió él, y si otra caja ve la existencia nueva sin reiniciar
(las dos se ven en la entrega 4). De paso se vio el cierre y la apertura de turno de la otra caja: eleventa cambió un
renglón de `CONFIGURACION` (`ID` 129, 19 bytes) que el puente no escribe; falta saber qué es. El turno no se cerró a
propósito: la caja 2 estaba suspendida y al regresar eleventa se desconecta y abre turno nuevo (ya pasaba antes).

## La propuesta recomendada

**Que el POS le escriba a eleventa cada diferencia del conteo como un ajuste de inventario igual al que eleventa hace a mano,
por etapas, y con un interruptor que empieza apagado.** La primera etapa es la alternativa barata (la lista de diferencias):
sirve desde el primer día y queda como respaldo si alguna vez hay que apagar la escritura.

### Cómo queda para quien cuenta y quien acepta

1. Con el celular se cuenta como hoy: escanear, la cantidad es lo que hay en total de ese producto, y lo que no se contó no
   se toca.
2. El celular manda la lista a una caja con turno abierto de alguien que tiene «Conteo completo de un departamento».
3. En esa caja, Inventario (F4) › «Conteos del celular», con el teclado como ya funciona: se ve lo contado, lo que dice
   eleventa, lo que se movió desde que se contó y la diferencia. Se corrige, se quita lo dudoso y Ctrl+Enter.
4. El POS cambia su existencia y pone el conteo en la bandeja hacia eleventa. La pantalla dice de cada producto «Va a
   eleventa», «Ya quedó en eleventa» o «Eleventa no lo tomó: …». El celular ve lo mismo al abrir su lista.
5. En eleventa cada diferencia queda como un ajuste más del kardex, igual que uno hecho a mano («Ajuste de inventario
   #folio»), sin el nombre de quien contó ni de quien aceptó ([decisiones del dueño](#decisiones-del-dueño-2026-11-27)).

### Qué escribe el POS en eleventa

Por cada producto **con diferencia** (lo que cuadra no se escribe: eleventa no tiene ajustes en cero), en una transacción
corta de las mismas que ya usa el puente (escritura, foto fija, sin esperar candados):

1. Revisa que eleventa no se haya restaurado a un respaldo anterior (la «huella», como en cada venta).
2. Busca su marca: si ese producto ya tiene un ajuste con el motivo de este conteo, ya entró y no se repite.
3. Lee el producto de eleventa (nombre y costo de hoy). Si eleventa ya lo borró, queda por revisar.
4. Revisa que nadie lo haya ajustado a mano en eleventa después de contarlo (un renglón del kardex con `AJUSTE_ID` y hora
   posterior). Si pasó, no se aplica: manda lo más nuevo, como ya es la regla del conteo.
5. Lee `CANTIDAD_ACTUAL`, le **suma la diferencia** (no escribe «quedan 9»: escribe «−3») y guarda el ajuste y su renglón del
   kardex con las columnas de la tabla de arriba: `FOLIO` = `ID`, el costo y el nombre del producto de eleventa, el motivo
   en las dos tablas, la caja de eleventa de la caja que aceptó, el usuario de eleventa de quien aceptó y `ALMACEN_ID` 1.

**Una transacción por producto, no una por conteo.** Una transacción con 100 productos tendría apartados 100 renglones de
existencias durante varios segundos, y una venta de eleventa de cualquiera de ellos chocaría. De uno en uno, cada candado
dura lo que un ajuste a mano. Cada renglón de la lista guarda si ya entró.

### Por qué no pisa lo que eleventa vendió mientras tanto

- **Entre contar y aceptar:** al aceptar se pide primero una lectura fresca de eleventa. Lo que eleventa vendió, recibió o
  devolvió desde que se contó ya está en el kardex del POS (como ajuste de la lectura) y cuenta como «movido desde que se
  contó»: si se contaron 15 y eleventa vendió 3, quedan 12. Para eso hay que corregir el detalle de la respuesta corta: un
  ajuste de la lectura es movimiento, no «otro conteo».
- **Entre aceptar y que llegue a eleventa:** se manda la diferencia, y eleventa la suma a lo que tenga en ese momento. Si
  vendió 2 más mientras tanto, siguen vendidas.
- **Lo que el POS vendió y todavía no llega a eleventa:** ya se descuenta hoy de la existencia del POS (`SinLlegar`,
  `SincronizacionEleventa.cs:673`). La diferencia se calcula contra esa existencia, así que esas ventas no se cuentan dos
  veces cuando lleguen.
- **La hora del celular:** todo depende de «cuándo se contó». Si el reloj del celular va mal, «lo vendido desde entonces»
  sale mal. El encargo de la app pide mandar también la hora del celular al enviar, para que el POS corrija el desfase.

### Reintentos, respaldo y la lectura

- **El mismo conteo no se aplica dos veces.** La marca es el motivo del ajuste («Conteo del celular #A1B2C3D4», con la
  clave corta de la lista) más el producto. Si se corta la red después de que eleventa guardó, el siguiente intento lo
  encuentra y lo da por hecho, como hoy una venta por su `CREADO_EN` (`EnvioEleventa.cs:244-253`). Tampoco se repite si se
  restaura un respaldo del POS.
- **Va por la misma bandeja y con el mismo candado** que las ventas (`EnvioVentasEleventa`), con un tipo de envío nuevo. Así
  hereda el **respaldo del día**: antes de lo primero que se escribe en eleventa cada día, el puente hace su respaldo con
  gbak (`EnvioVentasEleventa.cs:687-689`, `RespaldoDeEleventa`). Un conteo nunca detiene las ventas que vienen detrás: si
  eleventa lo rechaza, ese producto queda por revisar de inmediato y lo demás sigue.
- **La lectura no lo regresa ni lo duplica.** Mientras un producto del conteo no ha llegado, su diferencia cuenta como «lo
  que esta caja movió y allá todavía no está», igual que una venta pendiente (`BandejaAsync`, `SincronizacionEleventa.cs:716-766`,
  que hoy solo mira ventas y devoluciones). Cuando llega, eleventa ya trae la existencia nueva y la lectura no encuentra
  diferencia. Mientras va en camino y no se sabe si entró, esa existencia no se ajusta («en camino»), como hoy.
- **El turno:** en eleventa el ajuste no lleva turno, así que no depende de que eleventa tenga uno abierto. En el POS sí
  hace falta el turno abierto de quien acepta, como ya es.
- **A nombre de quién:** de quien lo aceptó en la caja (su usuario de eleventa, `Usuario.EleventaId`); si no viene de
  eleventa, el cajero de la configuración, como las ventas. Quién contó queda en el POS (bitácora y la lista).

### Qué se abre en el POS

| Qué | Cómo queda | Por qué |
|---|---|---|
| El conteo del celular aceptado en la caja | **Se abre en el piloto.** Con el interruptor apagado (`Eleventa:MandarConteos`), en modo lista; prendido, escribe en eleventa | Es lo que pidió el dueño |
| El conteo del día y el conteo completo desde la PC, ajustes, mermas, compras, abrir un paquete | **Siguen cerrados**, con el aviso de hoy | Primero un solo camino, bien probado. Lo demás va a «Recomendaciones» |
| El permiso | El mismo: «Conteo completo de un departamento», solo Puede o No. Ninguno nuevo, sin autorización ni tope por el tamaño de la diferencia | Regla del dueño |
| El aviso | En la pantalla del conteo: «El inventario se lleva en eleventa: lo que aceptes aquí se le manda como ajuste.» En modo lista: «…se captura a mano en eleventa con esta lista.» Si el puente está detenido o sin red: «Se manda en cuanto eleventa conteste» | Que nadie crea que ya quedó allá si no ha llegado |
| Un producto que solo existe en el POS (dado de alta aquí) | No se aplica: «No está en eleventa: dalo de alta allá.» | En el piloto esos productos no llevan existencia (van a eleventa como artículo común) |
| Un producto que solo existe en eleventa | El celular no lo encuentra hasta que llegue con la lectura (5 minutos) y baje su copia | Ya es así |
| Si eleventa no contesta al aceptar | No se acepta: «Eleventa no contesta: sin su existencia de ahora no se puede calcular la diferencia. Inténtalo en un momento.» La lista sigue esperando | Aceptar con una existencia vieja es justo el riesgo que hay que evitar |
| Todo con el teclado | La pantalla ya se maneja sin mouse; lo nuevo (el estado de cada renglón, imprimir la lista) también, con su prueba | Regla del dueño |

## La alternativa: solo la lista de diferencias

El POS no le escribe el inventario a eleventa. El conteo llega a la caja igual; la pantalla enseña lo contado, lo que dice
eleventa y la diferencia, y entrega la lista (en pantalla, impresa o en Excel) para capturarla a mano en eleventa ›
Inventario › Ajustes. La lectura trae después la existencia nueva.

| | Escribirle a eleventa | Solo la lista |
|---|---|---|
| Trabajo | Tres PR (puente, POS, herramientas) y dos visitas a la tienda | Un PR chico y ninguna visita |
| Riesgo para el eleventa real | Existe, y es el peor de este plan | Ninguno: el POS no escribe |
| Captura | Una vez, con el celular | Dos veces: cada diferencia se teclea en eleventa, una por una |
| Errores | Los del programa, que se prueban contra copias | Los de dedo, en cada conteo |
| Lo vendido mientras se captura | Se respeta solo | Hay que capturar pronto, o la lista ya no vale; la pantalla tendría que recalcular «captura este número» al momento |
| Quién contó y cuándo | Queda ligado al ajuste | Solo en el POS |

**Recomiendo escribirle a eleventa, empezando por la lista.** La lista sola no quita el trabajo que el dueño quiere quitar:
la tienda ya captura sus conteos a mano, y con la lista lo seguiría haciendo. Pero la lista es casi gratis (la pantalla ya
existe), sale primero, deja usar el conteo del celular mientras se prueba la escritura con calma, y es a lo que se regresa
si un día hay que apagar la escritura.

## Las entregas, en orden

Cada una es chica y se prueba contra una **copia** de la base, nunca contra la real. La 0 y la 4 necesitan la tienda (solo
sábado o domingo, con el dueño); las demás se hacen entre semana.

### Entrega 0. Mirar a eleventa ajustar (15 minutos en la tienda)

Sin instalar nada nuevo: con `observar-eleventa`, que ya está en el paquete, como en el [paso 3 de la guía](../guia-sucursal-2.md#3-el-ensayo-contra-la-copia). Se puede hacer
el mismo día que el ensayo de la matriz. En eleventa:

1. Ajustar un producto hacia abajo, sin escribir motivo.
2. Ajustar otro hacia arriba, con un motivo escrito.
3. Dejar uno en cero.
4. Ajustar uno de a granel con fracción.
5. Abrir «Ajustar» de un producto, venderlo en otra caja y entonces guardar.

**Ojo, por resolver antes de ir:** el eleventa de la tienda abre siempre la base real, no la copia (por eso la guía dice que
el corte no se puede probar contra la copia). Hay dos formas de hacer esto, y la decide el dueño:

- **Mirar un ajuste de verdad.** La próxima vez que la tienda vaya a ajustar un producto en eleventa (lo hace seguido),
  `observar-eleventa` corre contra la base real, que solo lee, y toma la foto antes y después. No se inventa ningún ajuste:
  los cinco casos de arriba se van viendo conforme salgan, y el 5 puede no darse.
- **Abrir un eleventa contra la copia.** Habría que averiguar cómo se le dice a eleventa que abra `PDVDATA-ENSAYO.FDB`
  (su `pdventa.ini`) y regresarlo después. No se ha hecho nunca y un error dejaría a la tienda vendiendo en la copia: no se
  recomienda sin ensayarlo antes en la PC de desarrollo.

Entre cada paso, Enter en `observar-eleventa`. Qué mirar: la tabla de [«Por comprobar en la tienda»](#por-comprobar-en-la-tienda).
Lo que salga se anota en [migracion-eleventa.md](../migracion-eleventa.md) y corrige este plan antes de programar la
escritura. Si no se alcanza a hacer, las entregas 1 y 2 no se detienen (no dependen de esto); la 3 sí lo espera.

### Entrega 1. La lista (el conteo del celular, abierto en el piloto sin escribir)

- `para-contar` y `mandar-conteo` reciben en el piloto; la tarea «Conteos del celular» se ofrece.
- La pantalla compara contra la existencia de eleventa (con una lectura fresca), ya sin tomar los ajustes de la lectura como
  «otro conteo».
- Aceptar no cambia existencias: deja la lista «por capturar en eleventa», se puede imprimir y sacar a Excel, y se cierra
  con «Ya lo capturé en eleventa».
- **Cómo se prueba:** la suite (pruebas nuevas del servicio y de la pantalla con el teclado). En esta máquina, el arnés del
  ensayo de una PC contra una copia servida en 127.0.0.1, con `curl` haciendo de celular: mandar una lista, vender en la
  copia entre contar y aceptar, y ver que la diferencia lo toma en cuenta.

### Entrega 2. El puente aprende a escribir un ajuste

- `EnvioEleventa`: el ajuste de un producto, como se describe arriba, con su marca.
- Las pruebas con el Firebird de prueba (el esquema de prueba gana `INVENTARIO_AJUSTES` y su contador).
- `probar-envio-eleventa` acepta un ajuste (`--ajuste <producto>:<diferencia>`) y enseña qué cambió, como ya hace con una venta.
- `observar-eleventa` aprende a revisar un ajuste hecho a mano contra lo que escribiría el POS (✓ coincide, ✗ no), y a decir
  si eleventa tocó alguna tabla que el POS no escribe.
- **Cómo se prueba:** contra una copia local, comparar campo por campo un ajuste del POS con los ajustes a mano que ya trae
  la copia; revisar la cadena del kardex del producto (cada renglón empieza donde terminó el anterior) y que el balance
  cuadre; repetir el mismo ajuste y ver que no se duplica; cortar la conexión a media escritura y ver que no queda a medias.

### Entrega 3. El POS manda el conteo aceptado

- El tipo de envío nuevo en la bandeja, el estado por renglón, el interruptor `Eleventa:MandarConteos` (apagado de fábrica).
- La lectura cuenta lo del conteo que no ha llegado; la restauración de eleventa lo incluye en su lista de «lo que se perdió».
- La pantalla y el celular dicen cómo va cada producto. La pantalla de la bandeja hacia eleventa enseña también los conteos pendientes y por revisar.
- **Cómo se prueba:** la suite, con las mismas carreras que ya se prueban para las ventas (lectura a media escritura, red
  cortada, eleventa restaurado). En esta máquina, el ensayo completo contra una copia: contar 20 productos, aceptar, y
  comparar eleventa antes y después; vender en la copia mientras el conteo va en camino; medir cuánto dura apartado cada
  producto.

### Entrega 4. Ensayo en la tienda contra la copia (sábado o domingo)

Con el dueño, contra `PDVDATA-ENSAYO.FDB`, con `observar-eleventa` abierto:

1. Contar cinco productos con el celular (uno que sobre, uno que falte, uno que cuadre, uno a granel, uno en cero).
2. Antes de aceptar, vender en el eleventa de ensayo una pieza de uno de ellos.
3. Aceptar en la caja. Mirar en `observar-eleventa`: solo cambian las tres tablas; todas las comprobaciones en ✓.
4. Abrir el eleventa de ensayo: la existencia de cada uno, su kardex y el reporte de movimientos de inventario.
5. Esperar una lectura: el POS no cambia nada.

Si algo no coincide, se corrige entre semana y se repite. No se pasa a la real con una ✗.

### Entrega 5. El primer conteo en el eleventa real (sábado o domingo)

Solo después de la 4 limpia, **y solo en una tienda donde el POS ya le escribe ventas al eleventa real** (hoy ninguna: en
la sucursal 2 falta el [paso 5](../guia-sucursal-2.md#5-la-primera-escritura-en-la-base-real)). Con el dueño:

1. Ver que el respaldo de eleventa del día existe (la franja de la caja lo dice) y sacar además el respaldo propio de eleventa.
2. Prender `Eleventa:MandarConteos`.
3. Contar **tres** productos, aceptar, y revisarlos uno por uno en eleventa.
4. Si están bien, contar un anaquel completo.
5. Al cierre, comparar en eleventa el reporte de movimientos del día contra la lista del POS.

Para deshacer: cada ajuste queda en el kardex de eleventa con lo que había antes, así que se corrige con el ajuste
contrario; el POS guarda la lista con «había» y «quedó» de cada producto. Si fuera todo el día, el respaldo.

### En paralelo: la app

El [encargo](#el-encargo-para-la-app-android) no depende de nada de lo anterior: las rutas ya existen y se prueban hoy contra
la base ficticia. Debe estar lista para la entrega 1.

## Riesgos, del más grave al menos

| # | Riesgo | Qué lo contiene |
|---|---|---|
| 1 | **Dañar existencias en el eleventa real:** un signo al revés, una diferencia aplicada dos veces o calculada contra una existencia vieja | Se escribe la diferencia, nunca el total; la marca por producto; la lectura fresca obligatoria al aceptar; el respaldo del día antes de escribir; el interruptor apagado de fábrica; el primer día, tres productos; cada ajuste se puede deshacer con su contrario; la entrega 4 contra la copia, con todo en ✓, antes de la real |
| 2 | **Estorbarle a eleventa mientras cobra:** que una venta de eleventa choque con el candado de un producto que se está ajustando | Una transacción corta por producto, sin esperar candados y con reintento; se mide en la entrega 3; se puede contar con la tienda cerrada |
| 3 | **Contar mientras se vende:** la hora del celular mal, o un conteo largo en que lo primero se contó una hora antes que lo último | La hora por producto y la corrección del reloj (encargo de la app); la pantalla enseña «desde que se contó» para que quien acepta lo vea; una lista de más de 7 días no se recibe |
| 4 | **Alguien ajusta el mismo producto a mano en eleventa** después de contarlo y antes de que llegue el conteo: la corrección se aplicaría dos veces | El envío revisa el kardex de eleventa de ese producto desde la hora del conteo; si hay un ajuste, no aplica y lo dice |
| 5 | **La lectura deshace o duplica el conteo** | El mismo mecanismo de las ventas pendientes, con sus pruebas |
| 6 | **Eleventa se restaura a un respaldo anterior** y pierde ajustes que el POS ya dio por enviados | La huella detiene el puente, como hoy; la lista de lo perdido incluye los conteos |
| 7 | **Algo que no se vio en la copia:** otra tabla que eleventa toca, o `COSTO_DESPUES` | La entrega 0; si `COSTO_DESPUES` resulta ser otra cosa, afectaría el valor del inventario en los reportes de eleventa, no las existencias |
| 8 | **Una actualización de eleventa cambia sus tablas** | El mismo riesgo que ya tienen las ventas; no crece |
| 9 | **Fracciones:** eleventa guarda las cantidades con poca precisión (`FLOAT`) | Solo afecta a lo de a granel; se prueba en las entregas 2 y 4 |

## Decisiones del dueño (2026-11-27)

1. **Se le escribe a eleventa.** El conteo se va a usar de verdad para ajustar el inventario en cuanto funcione con
   certeza, para empezar la depuración en la matriz. Y después del piloto la base de eleventa se tiene que seguir
   actualizando unos meses: la escritura no es provisional.
2. **En eleventa se ve igual que un ajuste a mano:** el motivo es «Ajuste de inventario #folio», sin texto propio.
3. **Sin el nombre de quien lo hace.** No se anota en eleventa ni quién contó ni quién aceptó. La columna `USUARIO_ID` no
   puede ir vacía: lleva el usuario de eleventa con el que el puente ya escribe las ventas de esa caja (interpretación mía,
   por revisar). Quién contó y quién aceptó quedan en el POS.
4. **El primer conteo real es en la matriz.**
5. **La entrega 0 se hace el 2026-11-27 en la matriz,** mirando a eleventa ajustar en la base real con
   `observar-eleventa` (solo lee), y de paso lo demás que falte por ver.
6. **Uso real en cuanto el conteo funcione con certeza.**

**Lo que cambian en el diseño de arriba** (donde el texto de arriba diga otra cosa, manda esto):

- **La marca para no repetir ya no puede ser el motivo.** Con «Ajuste de inventario #folio» el ajuste no dice de qué conteo
  viene. La marca pasa a ser la hora: cada renglón del conteo recibe, al aceptarse, una hora exacta al segundo que se guarda
  en el POS antes de escribir; el ajuste se escribe con esa `CUANDO_FUE`, y antes de escribir se busca en eleventa un ajuste
  de ese producto con esa misma hora y esa misma diferencia. Si está, ya entró. Se afina en la entrega 2.
- **El motivo lleva el folio,** que es el `ID` del ajuste: hay que tomar el `ID` del contador antes de armar el texto.
- **En el kardex de eleventa no se distingue lo del celular de lo hecho a mano.** Para saber qué vino de un conteo se mira
  el POS, que guarda el `ID` del ajuste de eleventa de cada renglón.

## El encargo para la app Android

Listo para pegar en su sesión. No repite lo que ya dice [celular-como-caja.md](../celular-como-caja.md#mandar-un-conteo-a-una-caja-2026-11-22)
(«Las llamadas del celular»): ahí están las rutas, los cuerpos, los estados y todos los códigos de error.

> **Mandar el conteo a una caja.** El POS ya tiene las rutas (PR #200) y la app todavía no las llama. Hay que:
>
> 1. **Llamarlas como están documentadas** en `docs/celular-como-caja.md` del repositorio del POS, sección «Mandar un conteo a
>    una caja» › «Las llamadas del celular»: `GET /api/cajas/{id}/para-contar` para elegir la caja,
>    `POST /api/cajas/{id}/mandar-conteo` para mandar la lista y `GET /api/cajas/{id}/conteos` (o `/conteos/{conteoId}`) para
>    ver cómo va. Los errores traen `{"mensaje", "codigo"}` y el mensaje se enseña tal cual.
> 2. **El `id` de la lista se genera una vez y se guarda con ella.** Si la respuesta se pierde, se reintenta con el mismo
>    `id`: el POS no la duplica. Sin red, la lista se queda en el celular y se manda cuando la caja principal conteste.
> 3. **Lo que cambia contra el documento** (el POS lo acepta desde su entrega 1; mientras tanto ignora lo que no conoce):
>    - En el piloto con eleventa las cajas **sí reciben**. `piloto-eleventa` deja de salir; si aparece, se enseña su mensaje
>      como cualquier otro.
>    - Mandar además `enviadoUtc`: la hora del celular en el momento de enviar. El POS la compara con la suya y corrige el
>      desfase del reloj del celular.
>    - Mandar en cada renglón `contadoUtc`: la hora del **último escaneo** de ese producto (opcional; sin ella vale la de la
>      lista). En un conteo largo, lo que se vendió después de contar cada producto se calcula desde su propia hora.
>    - En la respuesta de `conteos`, cada renglón puede traer `eleventa`: `"por-capturar"` (se capturará a mano),
>      `"pendiente"` (va en camino), `"enviado"` (ya quedó en eleventa), `"por-revisar"` (eleventa no lo tomó) o `null`. La
>      `nota` del renglón ya trae el texto para la persona: enseñar la nota, y usar `eleventa` solo para el icono o el color.
>      Un valor desconocido se trata como `null`.
> 4. **Qué enseñar:** antes de mandar, a qué caja va y quién la atiende (`atiende`), o por qué ninguna recibe (`porQue`).
>    Después, el estado de la lista (recibida, aceptada, aceptada con cambios, rechazada con su motivo) y, por producto, lo
>    contado, lo aceptado y la nota. No hace falta sondear: basta preguntar al abrir la lista de conteos.
> 5. **Lo que no cambia:** el celular nunca cambia existencias; la cantidad es lo que hay en total de ese producto; lo que
>    no se contó no se manda; cero es «no hay ninguno» y sí se manda.
> 6. **Probarlo** contra el servicio con la base ficticia, como dice «Probarlo con la base ficticia» de esa sección. No hace
>    falta eleventa.

## Para después (van a «Recomendaciones» del plan)

- Abrir en el piloto, por el mismo camino, el conteo del día y el conteo completo desde la PC.
- «Agregar inventario» y recibir mercancía hacia eleventa (allá es otra tabla, `INVENTARIO_RECIBOS`, y además cambia el costo).
- Mandar varios productos por transacción si la medición dice que uno por uno tarda demasiado.
