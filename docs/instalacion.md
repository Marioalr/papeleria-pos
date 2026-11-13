# Instalar Papelería POS

Un solo programa, `PapeleriaPos-Instalador-1.0.<número>.exe`, para poner Papelería POS en una PC de la tienda, en la matriz o en
la segunda sucursal: hace unas preguntas sencillas y lo instala todo, sin PowerShell ni Bloc de notas. Si la PC ya lo tiene
(instalado con este instalador o a mano con los scripts), lo actualiza sin perder nada. También muda la base de la tienda de una
PC a otra ([La base de la tienda](#la-base-de-la-tienda)). Los pasos a mano de siempre
([instructivo de la tienda](pruebas-en-tienda.md#instalar-la-caja-2-para-el-piloto)) se quedan como respaldo.

- **Lo corre el administrador de la PC** (Windows pide permiso al abrirlo). Después la caja se usa con la cuenta de Windows
  de todos los días ([plan](plan.md#instalación-y-usuarios-de-windows)). Esa cuenta solo escribe en
  `C:\ProgramData\PapeleriaPos\Caja`: la base, los respaldos y los registros son del sistema y de los administradores.
- **Tarda unos minutos.** Si la PC ya tenía el POS, primero respalda la base.
- **Al terminar se abre la caja,** y ahí se termina: la tienda, la cuenta del dueño y los costos de impresión.

## Antes de empezar

- **El instalador:** en GitHub, Actions › «Instalador de prueba» › la corrida › el artefacto
  `PapeleriaPos-Instalador-1.0.<número>` (sale del mismo commit y con el mismo número que el paquete para actualizar). Se lleva
  en la memoria USB.
- **Windows puede decir «Windows protegió su PC»** al abrirlo (no viene firmado, como el de la prueba con una persona real):
  «Más información» › «Ejecutar de todas formas». Después pide permiso de administrador.
- **Si la caja ya está instalada en esa PC, ciérrala** (cobra o deja en espera la venta: los tickets en espera se guardan).
- **Para «Otra caja»:** la caja principal prendida y en la misma red. Si no la encuentra sola, su IP.
- **Si la caja convive con eleventa:** la IP de la PC de eleventa, el número de esta caja en eleventa (y, si ya se sabe, el del
  cajero), el `gbak.exe` de Firebird 2.0 y la clave de Firebird (la de `SYSDBA`, la tiene el dueño).
- **Si la tienda usa Control Print:** la IP de la PC de Control Print y su código de conexión (en esa PC: Control Print →
  Ajustes → Red, «Código de conexión»).
- **Para mudar la base de la tienda a otra PC:** la memoria USB con espacio para la base, y el instalador se abre desde la memoria
  en las dos PC (el paquete de mudanza queda junto a él, en la carpeta `mudanza`). La clave de Firebird y el código de Control Print
  no viajan: a la mano, para escribirlos en la PC nueva.
- **Internet** solo hace falta si a la PC le falta WebView2 (lo usa la pantalla de la caja; Windows 10 al día y Windows 11 ya lo
  traen).

## Página por página

Cada página dice arriba para qué es. Las que no aplican a lo que es la PC no salen. Si la PC ya tiene el POS, cada página
empieza con lo que tiene: basta con «Siguiente» para dejar todo igual. Antes de cambiar nada, «Instalar» revisa todas las
respuestas como se van a usar; si una no se puede usar, lo dice y regresa a su página (en la PC no cambió nada).

**En la matriz, hoy (el piloto):** la caja 2 ya está instalada (con los scripts): el instalador la **actualiza**, y todo
empieza como está; se deja así. **Solo un fin de semana, a una hora tranquila y con el dueño** (la caja 2 deja de vender unos
minutos, y si algo sale mal hay que verlo ahí). En la caja principal de la tienda no se instala nada sin que el dueño lo decida.
**La mudanza a la caja principal** (lo decidió el dueño el 2026-11-09, para el fin de semana del 14 de noviembre): la base de la
caja 2 pasa a la caja principal con este mismo instalador, en tres corridas ([La base de la tienda](#la-base-de-la-tienda); los
pasos, en [la guía](pruebas-en-tienda.md#el-piloto-con-el-servicio-en-la-caja-principal)); desde entonces la principal es la «Caja
principal» y la caja 2, «Otra caja».
**Si la caja 2 todavía no tiene el POS** (una PC nueva): «Qué es esta PC» viene vacío y eleventa en «No»: se elige «Caja principal» y
eleventa en «Sí», con lo demás como dice [el instructivo](pruebas-en-tienda.md#instalar-la-caja-2-para-el-piloto). **En la segunda sucursal:** empieza con una sola PC ([plan](plan.md#para-el-piloto-con-dos-cajas)): es **«La única caja»**.

| Página | Para qué es | En la matriz (la caja 2 del piloto) | En la segunda sucursal (una sola PC) |
|---|---|---|---|
| Bienvenida | Dice qué hace, y si la PC ya tiene el POS (y cuál) | Dice que ya está y que se actualiza sin perder nada | Dice que lo instala |
| **Qué es esta PC** | Lo que hará en la tienda: de eso depende lo que se instala | «Caja principal» (ya viene elegida: la caja 2 tiene su propio servicio en el piloto) | «La única caja» |
| **La base de la tienda** (con servicio, o a media mudanza) | Si la base se queda en esta PC, se muda a otra o se trae de otra | Como viene: «La base de la tienda se queda en esta PC». El día de la mudanza, lo que dice [la guía](pruebas-en-tienda.md#el-piloto-con-el-servicio-en-la-caja-principal) | Como viene: «Empezar sin base (tienda nueva)» |
| **Dónde está la caja principal** (solo «Otra caja») | A qué PC se conecta esta caja: su IP, «Buscar en la red» o vacía (la caja la busca al abrirla) | No sale | No sale |
| **Las impresoras de esta PC** | Dónde sale el ticket (en pantalla, por la red con su IP o una de Windows) y qué impresora mide la caja | Como está: el ticket por la IP de la POS-8360; la que mide, la cola de la WF-6590 | El ticket: «Por la red» con la IP de su impresora de tickets (o una de Windows); la que mide, la de inyección de esa PC, si la hay |
| **El lector de códigos** | Cómo llega el código: como teclado (lo normal) o por puerto COM | Como está: por COM, el puerto y la velocidad de eleventa | «Como teclado», salvo que su lector esté como puerto COM |
| **Control Print** (con servicio) | Si la caja enseña la cola por cobrar de Control Print (solo la lee) | Como está: «Sí», la IP de la caja principal; el código, vacío (se queda el que tiene) | «No», salvo que esa tienda use Control Print |
| **eleventa** (con servicio) | Si la caja convive con eleventa y lee su catálogo y existencias | **Déjalo como viene** (abajo: por qué puede venir en «No») | «No», salvo que esa PC tenga eleventa y el dueño quiera que la caja lo lea |
| **eleventa: respaldos y clave** (con eleventa) | El `gbak.exe` de Firebird 2.0, dónde van los respaldos de eleventa y la clave de Firebird | Como está; la clave, vacía (se queda la que tiene) | Si se dijo «Sí»: su gbak, la carpeta de siempre y la clave |
| Listo para instalar | Lo que va a hacer, en una lista | — | — |
| Instalando | Lo hace: unos minutos | — | — |
| **Cómo quedó** | Lo que hizo y lo que contestó cada parte (el servicio, eleventa, Control Print, la caja principal) | — | — |
| **Terminado** | «Abrir la caja para terminar» | — | — |

### Qué es esta PC

- **Caja principal:** aquí vive la base de la tienda (el servicio); las otras cajas y los celulares se conectan a ella. Es lo
  de siempre.
- **Otra caja:** una caja más; se conecta a la caja principal, que ya está instalada en otra PC. No lleva servicio ni base.
- **La única caja:** la tienda tiene solo esta caja: aquí vive la base. Los celulares (fotos, conteos) sí se conectan;
  otra caja desde otra PC no. Queda en la configuración de la PC (`"Instalacion": { "UnaSolaCaja": true }` en `servicio.json`).

Si la PC ya tiene el servicio, no puede ser «Otra caja» (su base vive ahí), salvo para terminar una mudanza: en una PC a la que
ya se le sacó la base viene «Otra caja» elegida y las demás apagadas, y dice «La base de esta PC se sacó para mudarla (…). En la
página que sigue eliges terminar la mudanza o cancelarla.» ([La base de la tienda](#la-base-de-la-tienda)). Si es «Otra caja» dada
de alta con una caja principal, sigue siendo «Otra caja» ([cambiar lo que es una PC](#cambiar-lo-que-es-una-pc)); una «Otra caja»
que todavía no se da de alta (instalada sin la IP de la caja principal) también viene como «Otra caja».

**Una caja principal (o la única caja) nueva:** antes de seguir, busca en la red si ya contesta una caja principal (la misma
búsqueda de la caja). Si contesta una, lo dice: otra caja principal tendría su propia base, aparte, y lo vendido en una no se ve
en la otra. El botón que viene elegido es **«No, volver»** (para elegir «Otra caja»); para seguir hay que escoger **«Sí, es otra
tienda»**. Al traer la base en una mudanza ese aviso no debe salir: la PC de donde salió la base ya no contesta.

### La base de la tienda

Para **mudar la base de la tienda de una PC a otra** (2026-11-11): la base viaja en una carpeta, el **paquete de mudanza** (por
omisión, la carpeta `mudanza` junto al instalador: la misma memoria USB que lo lleva a las dos PC), y son **tres corridas del mismo
instalador:** sacar la base en la PC que la tiene, traerla en la PC nueva y terminar en la primera, que queda como «Otra caja». En
la tienda, pantalla por pantalla: [la guía](pruebas-en-tienda.md#el-piloto-con-el-servicio-en-la-caja-principal).

La página sale con «Caja principal» o «La única caja», y en una PC a media mudanza; con «Otra caja» sin mudanza, no sale. No
adivina qué toca ni le pregunta nada al servicio: enseña las opciones, y «Instalar» revisa si se puede antes de cambiar nada.

- **En una PC sin mudanza a medias,** hasta tres opciones:
  1. **Sin mudanza.** Dice «La base de la tienda se queda en esta PC» si la PC ya tiene el servicio, o «Empezar sin base (tienda
     nueva)» si no.
  2. **«Mudar la base de esta PC a otra PC (sacar el paquete de mudanza)».** Solo sale en una PC que tiene el servicio.
  3. **«Traer la base de otra PC (paquete de mudanza)».** Sale siempre, también en una PC que ya tiene el servicio: así se puede
     repetir después de un intento que falló.
- **Qué viene elegido:** la 3, si junto al instalador hay un paquete que se puede traer y la PC todavía no tiene el servicio (es
  la PC nueva), o si a esa PC ya se le puso la base de ese mismo paquete (un intento que quedó a medias). En cualquier otro caso,
  la 1. **Ojo:** después de un intento que instaló el programa y no trajo la base, la 3 ya no viene elegida: hay que elegirla. Con
  la 1 elegida y un paquete de otra PC en la carpeta, la nota lo avisa («En … hay un paquete de mudanza, el de …: para poner aquí
  esa base, elige «Traer la base de otra PC».»).
- **En una PC a media mudanza** (ya se le sacó la base), dos: **«Terminar la mudanza: esta PC queda como «Otra caja»»**, que viene
  elegida, y **«Cancelar la mudanza: esta PC vuelve a tener la base»**. Ahí no hay carpeta que elegir.
- **La carpeta,** con «Buscar…»: dónde dejar el paquete (la opción 2) o dónde está (la 3); va con su ruta completa (`E:\mudanza`).
  Con la 3, debajo dice de quién es el paquete (de qué PC, cuándo se sacó, la tienda, sus productos, sus ventas y la última, sus
  cajas y cómo llega el puente con eleventa) o por qué no se puede traer: ahí no hay paquete, le falta la base o no mide lo que
  debe, ya se trajo, o lo sacó una versión más nueva que este instalador. Si se escribe otra carpeta, se revisa al dar «Siguiente».

**El paquete** es esa carpeta con dos archivos: la base (`pos-AAAA-MM-DD-mudanza-HHmm.db`, un respaldo consistente) y su resumen,
`mudanza.txt` («clave=valor»: de qué PC salió y cuándo, la versión, el tamaño y el SHA-256 de la base, la tienda, cuánto trae, sus
cajas, cómo estaba el puente con eleventa y los avisos). Un paquete sin `mudanza.txt` está incompleto. Nunca lleva la clave de
Firebird, el código de Control Print ni la llave de una caja. **Lleva la base completa de la tienda:** la memoria se cuida ese día
como la base misma.

Los tres casos:

- **Sacar** (en la PC que tiene la base, con su caja cerrada).
  - Antes de tocar nada revisa que el servicio de esa PC conteste con su tienda y que en la carpeta se pueda escribir; si no, no
    cambia nada y lo dice.
  - Actualiza como siempre. Al final deshabilita y detiene el servicio, y le pide a su programa el paquete
    (`PapeleriaPos.Servicio.exe mudanza-sacar`): hace el respaldo (queda también en `respaldos`), lo copia a la carpeta y lo vuelve
    a leer de ahí para comparar su SHA-256; deja junto a la base la marca `pos.db.mudada`; y lo último, `mudanza.txt`.
  - **Con la marca, esa PC ya no atiende.** Su servicio no arranca aunque alguien lo habilite (lo dice en su registro y en el Visor
    de eventos), `actualizar.ps1` y `restaurar.ps1` no hacen nada ahí, y desinstalar con «Borrar también los datos» se niega. Si su
    caja se abre, vende con su copia (como cuando el servicio no contesta) y guarda lo cobrado para subirlo después.
  - Si no se pudo sacar, no quedan marca ni paquete y el servicio vuelve a quedar automático y en marcha: la PC sigue como antes
    («No se sacó la base: …. Esta PC sigue trabajando como antes.»). Si no quedó así, «Cómo quedó» lo dice y dice cómo salir: si
    quedó la marca, con «Cancelar la mudanza».
- **Traer** (en la PC nueva).
  - Antes de cambiar nada, «Instalar» revisa (`mudanza-revisar`): el paquete entero (su SHA-256, y que no sea de una versión más
    nueva); que la PC de donde salió no haya vuelto a trabajar con esa base (se canceló allá: ese paquete ya no sirve); qué base
    tiene esta PC; y que la configuración que va a quedar sirva. Si algo no, lo dice y regresa a su página.
  - Después instala el programa, copia la base a `respaldos` (revisando su SHA-256) y la pone con `restaurar.ps1`, que aparta la
    que hubiera en `antes-de-restaurar-<fecha>`. La bitácora dice «Se mudó aquí la base de …». Y hasta el final, con la base ya
    puesta, escribe la configuración del puente: nunca queda el puente prendido sobre una base vacía.
  - **Las páginas de eleventa y Control Print empiezan con lo de la otra PC,** pasado a esta: el servidor en `127.0.0.1` si era esta
    misma PC, y la base y el cajero de allá. El número de esta caja en eleventa empieza vacío; si no se sabe, se deja así (no afecta
    a las otras cajas, y se pone después corriendo el instalador otra vez). El `gbak.exe` y la carpeta de respaldos son los de esta PC.
  - **La clave de Firebird y el código de Control Print no viajan:** se escriben en sus casillas, como siempre.
  - **El puente llega como estaba:** si allá le escribía a eleventa (`Escribir`), aquí sigue escribiendo; si solo leía, solo lee; y
    si estaba apagado, la página viene en «No». Es el único caso en que el instalador escribe `Escribir`, y nunca lo prende por su
    cuenta. La página de eleventa lo dice («El puente llega ESCRIBIENDO en …» o «El puente llega SIN escribir…»). Con el puente de
    allá prendido, «No» y la base no se pueden cambiar (cambiar la base lo detendría); y si escribía, hace falta el `gbak.exe` de
    esta PC: sin el respaldo del día no se manda nada.
  - **`Eleventa:Cajas` lo escribe el instalador:** cada caja de la base mudada que ya tuvo turnos queda con el número de caja de
    eleventa con que escribía allá (una que no tenía número no entra, y el paquete lo avisa). `Eleventa:Caja` es solo lo que se
    conteste aquí; vacío, no hay caja de omisión: una caja sin número espera en la bandeja, nunca escribe como otra. Solo con
    eleventa en «Sí»: con «No», de eleventa no se escribe nada.
  - **De `servicio.json` de la otra PC** solo viaja, dentro de `mudanza.txt`, una lista corta de valores del puente (`Escribir`,
    `Cajero`, `Puerto`, `Usuario`, los productos de impresiones y copias, los plazos y `Cajas`); nunca el archivo, las rutas de esa
    PC (`Gbak`, `CarpetaRespaldos`) ni nada de fuera de `Eleventa`.
  - **Encima de una base:** una sin tienda (o un `pos.db` que quedó de antes, sin servicio) se aparta sola, nunca se borra. Con
    tienda, pregunta «Esta PC ya tiene una tienda. ¿Apartarla (no se borra) y traer la de …?», con «No» elegido.
  - **Un paquete que ya se trajo no se trae otra vez:** al poner la base, el instalador deja en esta PC la nota
    `C:\ProgramData\PapeleriaPos\mudanza-puesta.txt`, y con ella otra corrida con ese mismo paquete solo termina lo que faltó.
  - **Al quedar bien** (el servicio contesta con la tienda del paquete, sin avisar que la base no pasó su revisión), borra la base
    del paquete (queda como respaldo en esta PC) y deja en la carpeta `traida.txt`, con la dirección de esta PC para el último paso.
  - **Si no quedó,** «Cómo quedó» dice cuál de dos: «No se trajo la base: …» (la del paquete no quedó puesta y la configuración
    del puente no se escribió; se repite aquí con la misma memoria, o se cancela allá) o «La base ya está en esta PC, pero no quedó
    terminado: …» (se corre otra vez aquí, con la misma memoria, para terminar). En los dos la PC de donde salió sigue detenida:
    no se pierde nada.
- **Terminar o cancelar** (otra vez en la PC de donde salió la base).
  - **Terminar:** conviene tener conectada la memoria USB de la mudanza: de ahí toma el instalador la dirección de la PC nueva y
    la prueba de que ese paquete sí se trajo allá. Antes de cambiar nada comprueba que la caja principal contesta, que ya tiene la
    misma tienda y que conoce la llave de esta caja (la misma prueba de identidad que hace la caja). Deja `caja.json` con la
    dirección nueva y la llave pública del servicio nuevo, **sin tocar el alta:** la caja sigue siendo la misma, con su serie, sus
    folios, sus tickets en espera y lo que tuviera por subir, y no pide PIN. Quita el servicio de esa PC y **aparta, sin borrar,**
    su base, `servicio.json`, `pos.llave` y `pos.controlprint` en `C:\ProgramData\PapeleriaPos\antes-de-mudar-<fecha>` (la marca,
    lo último: una corrida a medias se puede repetir); los respaldos, los registros y lo de la caja se quedan donde están.
    - **Si la memoria dice que ese paquete no se ha traído** (todavía trae su base y no tiene `traida.txt`), no cambia nada («El
      paquete de mudanza de … todavia trae la base de esta PC y no tiene el aviso de que ya se trajo…»): una base de antes de la
      misma tienda (la de un intento anterior) también conoce la llave de la caja. Primero se trae allá ese paquete. Sin la memoria
      a la mano, esto no se puede revisar.
    - **Si la principal ya no reconoce la llave** (se dio de alta otra PC como esa caja), solo sigue con la memoria de la mudanza
      puesta (su `traida.txt` prueba que ese paquete se trajo a esa PC), y la caja pide su alta otra vez: se elige la misma caja de
      antes. Si la prueba no se pudo hacer (la red), conserva el alta y la caja aprende la llave del servicio nuevo al conectarse.
    - **Si esa PC no contesta, todavía no tiene la base o tiene otra tienda,** no cambia nada.
    - **Desde que `caja.json` apunta a la PC nueva ya no hay regreso.** Si lo que sigue no queda (Windows no termina de quitar el
      servicio, o la base no se deja apartar), «Cómo quedó» empieza con «No se terminó la mudanza: …» y dice que la caja de esa PC
      ya apunta a la caja principal: se corre otra vez «Terminar la mudanza» hasta que quede. Ya no se cancela, ni se desinstala
      la PC nueva con «Borrar también los datos».
  - **Cancelar:** quita la marca y la PC vuelve a trabajar con su base; el paquete, si su carpeta está a la mano, se borra (ya no
    sirve). Nunca dos bases vivas:
    - **Se niega si en la red ya contesta otra PC con esa misma tienda:** primero se desinstala allá con «Borrar también los
      datos». Apagar esa PC no basta: al prenderla volvería a trabajar con esa base. Pregunta en la red y a la dirección que dejó
      anotada la memoria (el `traida.txt` de ese paquete, que busca en la carpeta `mudanza` junto al instalador y en la carpeta a
      donde se sacó).
    - **Si la memoria dice que la base ya se trajo** (ese `traida.txt`), tampoco cancela sin confirmarlo, aunque esa PC no
      conteste (apagada, o sin red): el asistente pregunta «La USB de la mudanza dice que la base de esta PC ya se trajo a la otra
      PC (…). … ¿Allá ya se desinstaló Papelería POS con «Borrar también los datos»?», con «No» elegido. Sin esa confirmación, no
      cambia nada.
    - **Si no pudo preguntar** (esta PC sin red, o la búsqueda no salió), tampoco: «No se pudo revisar si la base ya esta viva en
      otra PC (…): no se cancelo…». Y si la caja de esta PC ya apunta a la PC nueva (un «Terminar» ya empezó), no se cancela: se
      termina.
    - Sin la memoria conectada, y si las dos PC no se ven por la red, no lo puede saber: por eso
      [la guía](pruebas-en-tienda.md#si-algo-falla-en-la-mudanza) pide desinstalar primero allá cuando esa PC ya tiene la base.

**Lo que no se muda:** `pos.llave` (la PC nueva hace la suya). Las cajas de otras PC y los celulares se dan de alta otra vez; y la
caja de la PC nueva y el celular se dan de alta **al final,** ya terminada la mudanza, eligiendo «Caja nueva», nunca la caja mudada.

### Dónde está la caja principal (solo «Otra caja»)

- **«Buscar en la red»** encuentra las cajas principales que contestan, como lo hace la caja (la misma búsqueda); si hay una,
  la pone. **«Probar»** revisa que la de la IP escrita conteste.
- **Vacía** también sirve: al abrir la caja, en su pantalla de alta, «Buscar en la red».
- Si la que se escribió no contesta, pregunta si seguir de todos modos: la caja la busca ahí al abrirla.
- **Avisa** si la que contesta está instalada como «La única caja» (no acepta otras cajas: en esa PC, el instalador otra vez y
  «Caja principal») o si todavía no tiene tienda (primero se termina su instalación, abriendo la caja en esa PC). Se puede seguir:
  esta caja se da de alta cuando eso se arregle.
- **Al terminar una mudanza** ([La base de la tienda](#la-base-de-la-tienda)), la casilla empieza con la IP de la PC que ya tiene
  la base: la trae la memoria de la mudanza (de las que anotó esa PC, la primera que contesta), y debajo dice «Es la dirección que
  trae la USB de la mudanza. Contesta: …». Ahí no se puede dejar vacía; si esa PC no contesta o todavía no tiene la base, al
  instalar no se cambia nada.

### Las impresoras de esta PC

- **El ticket:** «En pantalla» (sin imprimir), «Por la red» con la IP de la impresora de tickets (`tcp://IP:9100`, lo
  recomendado: así la usan las dos cajas sin depender de otra PC) o el nombre de una impresora de Windows.
- **La impresora que mide la caja:** la de inyección, cuando se imprime desde esta PC (`ColaImpresion`). A esa se le activa
  «Conservar documentos impresos» (como `instalar-caja.ps1 -ColaImpresion`): la caja mide cada trabajo y después lo quita. Solo al elegirla o
  cambiarla: al volver a correrlo con la misma, se queda como esté.
  **Avisa** si es la compartida desde otra PC (`\\PRINCIPAL\…`: con ella la tinta no se mide; va la cola con el puerto TCP/IP
  de la impresora) o si tiene apagadas las «características de impresión avanzadas» (RAW: solo se estiman las páginas)
  ([instructivo](pruebas-en-tienda.md#control-print-en-la-caja-2-sus-tarjetas-y-la-cola-de-la-caja-2), paso 3).

### El lector de códigos

- **Como teclado:** lo normal; no hay que configurar nada.
- **Por puerto COM:** como lo tiene eleventa en esa PC. La lista trae los puertos COM de la PC; la velocidad, la de eleventa
  (casi siempre 9600). Lo demás del puerto (paridad, bits) se queda como está en `caja.json`; si eleventa usa otra cosa,
  [el instructivo](pruebas-en-tienda.md#el-lector-de-la-caja-2-por-puerto-com-piloto-alfa) dice dónde se cambia.

### Control Print

- **Sí:** la IP de la PC de Control Print (la caja principal de la tienda) y su código de conexión (16 letras y números, como
  `ABCD-EFGH-JKLM-NPQR`; se escribe sin que se vea). El código va **solo** en la variable del servicio
  (`ControlPrint__Codigo`), nunca en un archivo. **Vacío, se queda el que ya tiene.**
- La caja **solo lee** Control Print: lo que se cobre en la caja sigue «por cobrar» allá.
- **No** en una PC que lo tenía: se quitan su sección de `servicio.json` y su código (como «Para quitarlo» del instructivo).

### eleventa

- **Sí:** la caja **solo lee** eleventa (`Activo`). **Escribirle cada venta (`Escribir`) lo empieza el dueño después, con el
  instructivo** ([empezar a escribir](pruebas-en-tienda.md#empezar-a-escribir-en-la-base-real)): el instalador nunca lo prende,
  y si ya estaba prendido, lo deja así. Mientras no se le escriba, no se vende en la caja nueva (lo vendido no llegaría a
  eleventa; con el número de la caja puesto, la franja lo dice, sin detener el cobro).
  - La IP de la PC de eleventa: si la PC tiene eleventa, viene la de su `pdventa.ini` (`IPServidor`).
  - La base: la de una instalación normal, `C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB`.
  - El número de esta caja en eleventa (en la matriz, la caja 2 es la 3) y el del cajero (el del dueño, que dice
    `importar-eleventa`); si todavía no se saben, vacíos: solo hacen falta para escribirle. Un 0 (como lo trae la plantilla de
    `servicio.json`) es lo mismo que vacío: la casilla empieza vacía y `servicio.json` no cambia.
- **La base de la caja no se arma aquí.** Para empezar con la de eleventa (con sus usuarios y su historial),
  `importar-eleventa`, como dice [el instructivo](pruebas-en-tienda.md#instalar-la-caja-2-para-el-piloto) (pasos 4 y 5). Si no,
  al abrir la caja se crea la tienda y la lectura trae el catálogo. La base de otra PC sí se trae aquí, con la mudanza
  ([La base de la tienda](#la-base-de-la-tienda)).
- **Al traer la base de otra PC,** la página empieza con lo de allá y el puente llega como estaba: es el único caso en que
  `Escribir` puede quedar prendido al instalar (ya lo estaba allá), y entonces la nota de la página dice «El puente llega ESCRIBIENDO
  en …». `Eleventa:Cajas` (de qué caja de eleventa es cada caja del POS) lo escribe el instalador; fuera de una mudanza no lo
  pregunta ni lo toca.
- **No** en una PC donde el puente ya está prendido: no se puede elegir. El puente se apaga con el instructivo
  ([regresar o terminar el piloto](pruebas-en-tienda.md#regresar-o-terminar-el-piloto)), revisando antes lo pendiente.
- **En la caja 2 de la matriz, déjalo como viene.** Después del ensayo o de «Regresar» el puente puede estar apagado a
  propósito: entonces la página viene en «No», y así se deja. El instalador nunca lo vuelve a prender por su cuenta (solo
  «Sí» lo prendería, y eso lo decide el dueño con el instructivo).

### eleventa: respaldos y clave

- **El `gbak.exe` de Firebird 2.0:** con él se respalda eleventa cada día antes de escribirle (sin ese respaldo no le escribe).
  La lista trae los que encuentra (la copia del instructivo, `C:\ProgramData\PapeleriaPos\firebird20\bin\gbak.exe`, y las
  carpetas de Firebird y de eleventa) y dice de qué versión es. **Nunca el de 2.5.** Si la PC no tiene Firebird 2.0, se copia
  su carpeta `bin` de la caja principal
  ([instructivo](pruebas-en-tienda.md#instalar-la-caja-2-para-el-piloto), «El `gbak.exe` de Firebird 2.0»).
- **Los respaldos de eleventa:** en esta PC, nunca en OneDrive. Vacía: junto a la base
  (`C:\ProgramData\PapeleriaPos\respaldos-eleventa`).
- **La clave de Firebird:** sin que se vea. Va **solo** en la variable del servicio (`Eleventa__Clave`), nunca en un archivo,
  en la línea de órdenes ni en un registro. **Vacía, se queda la que ya tiene.**

### Cómo quedó y Terminado

«Cómo quedó» dice lo que hizo (instalado, actualizado de qué versión a cuál, o la misma versión), qué cambió de la
configuración, y si contestan el servicio, eleventa y Control Print (o, en «Otra caja», la caja principal), con lo que dijeron.
**Que eleventa o Control Print no contesten no detiene nada:** se dice y se arregla después (por ejemplo, la clave o el código
correctos, corriendo el instalador otra vez). Si la revisión de que todo conteste no pudo correr, también lo dice. Abajo dice
dónde quedó el detalle.

**Si dice «NO VENDAS EN ESTA CAJA»** (también en la última página): la versión nueva no arrancó y no se pudo regresar sola a
la de antes; el servicio quedó detenido. No se vende en esa caja hasta que quien instala lo revise; lo de antes está completo en
su respaldo ([cómo regresarlo a mano](pruebas-en-tienda.md#actualizar-la-caja-2-del-piloto)).

«Terminado» ofrece **«Abrir la caja para terminar»** (se abre como el usuario que abrió el instalador, no como
administrador). **Con eleventa en «Sí» y una base sin tienda,** esa casilla viene sin marcar y dice que antes se arme la base: en la
[caja 2 nueva del piloto](pruebas-en-tienda.md#instalar-la-caja-2-para-el-piloto) primero los pasos 4 y 5 del instructivo, y la caja se abre
hasta el final; ahí es normal que «Cómo quedó» diga que eleventa todavía no se lee porque no hay tienda. En la caja:

- **Caja principal o la única caja con la base nueva:** la caja pide la tienda, la cuenta del dueño y esta caja; después,
  «Terminar la instalación» repasa la impresora de tickets, **los costos de impresión** (viven en la base, se ponen ahí), el
  lector, y eleventa y Control Print si los hay.
- **Una caja principal que ya tiene su tienda, y «Otra caja»:** el alta de siempre: el usuario y el PIN del dueño.
- **En la caja principal**, la página dice cómo instalar las otras cajas: este mismo instalador en cada PC, eligiendo «Otra
  caja»; encuentran esta en la red, o con su IP (la dice).

**En una mudanza** ([La base de la tienda](#la-base-de-la-tienda)), «Cómo quedó» dice primero cómo quedó la base y qué sigue, y
la última página lleva su propio título:

- **Al sacar:** «LA BASE DE ESTA PC YA ESTÁ EN EL PAQUETE DE MUDANZA (…)», que esa PC ya no atiende y a dónde llevar la memoria; y
  los avisos del paquete (lo que viaja pendiente hacia eleventa, un turno abierto, las cajas que se darán de alta otra vez). Del
  servicio dice que está detenido a propósito, no que no contestó. La última página: «La base ya está en el paquete».
- **Al traer:** «LA BASE DE … YA ESTÁ EN ESTA PC. FALTA UN PASO…», que las altas de esa PC y del celular van después («Caja nueva»,
  no la caja mudada), cómo llegó el puente (escribiendo o sin escribir) y los avisos. La última página: «La base ya está aquí:
  falta un paso». O «La base ya está en esta PC, pero no quedó terminado: …» («La mudanza quedó a medias»): se corre el instalador
  otra vez ahí, con la misma memoria.
- **Al terminar:** «ESTA PC YA ES «OTRA CAJA» DE …», si conserva su alta o la pide otra vez, y dónde quedó apartada la base de antes.
  La última página: «Esta PC ya es «Otra caja»».
- **Al cancelar:** «SE CANCELÓ LA MUDANZA: esta PC vuelve a trabajar con su base.» y, si el paquete no se pudo borrar, que se borre
  su carpeta de la memoria. La última página: «Se canceló la mudanza».
- **Si no se pudo:** al sacar o al traer, con el programa ya puesto, «No se sacó la base: …» o «No se trajo la base: …», con el
  porqué y qué sigue (las últimas páginas: «No se sacó la base» y «No se trajo la base»). Lo que se detiene antes de cambiar nada
  (también al terminar y al cancelar) sale como «NO SE TERMINÓ DE INSTALAR: …», con su porqué. Si la mudanza ya había quedado y
  lo que falló fue lo de después (actualizar la caja), arriba dice cómo quedó la mudanza y abajo «NO SE TERMINÓ DE INSTALAR: La
  mudanza ya se termino…». Y si «Terminar» se cortó con la caja ya apuntando a la caja principal, arriba dice «No se terminó la
  mudanza: …», con lo que falta, y la última página avisa «Ojo: lo de la mudanza sí cambió»: se repite «Terminar la mudanza»; ya
  no se cancela.

**«Abrir la caja para terminar» viene sin marcar** cuando la base se sacó (esa PC ya no atiende) y siempre que se pidió traerla
(haya quedado, no haya quedado o esté a medias): abrir la caja de una caja principal que todavía no tiene su base crearía una
tienda nueva, y las altas de una mudanza van al final. Viene marcada al terminar, al cancelar y cuando no se pudo sacar (esa PC
sigue como antes).

## Volver a correrlo: actualizar o cambiar respuestas

- **Una versión nueva** se pone corriendo su instalador: usa `actualizar.ps1`, así que antes respalda la base, el servicio y la
  caja en `C:\ProgramData\PapeleriaPos\actualizaciones\<fecha>-<versión de antes>`, y si la nueva no arranca, regresa sola a
  la de antes ([cómo funciona](pruebas-en-tienda.md#actualizar-la-caja-2-del-piloto)). Una versión anterior no la pone.
- **La misma versión** no se reinstala: solo cambia lo que se conteste distinto.
- **Lo que se queda siempre:** la base (`pos.db`), `servicio.json` (lo que el instalador no pregunta, tal cual), las variables
  del servicio (la clave y el código, si no se dan otros), y lo de la caja: su alta, su copia y los tickets en espera.
- **Solo se escribe lo que cambió.** Si nada cambió, `servicio.json` y `caja.json` no se tocan (ni un byte).
  - **Los comentarios de `servicio.json` se quedan:** solo se cambia el valor de lo que cambió, lo nuevo se agrega al final de
    su sección y lo que se quita se va con su renglón. Se pierden solo los comentarios de adentro de lo que se quita. Si algún
    día no se pudiera cambiar por partes, se escribe entero, con un comentario arriba que lo dice.
  - **El de antes queda junto a él,** como `servicio.json.anterior` y `caja.json.anterior` (el de la vez anterior que cambió).
  - Un `servicio.json` o un `caja.json` mal escrito detiene el instalador antes de empezar, sin cambiar nada, y dice cuál y en
    qué renglón.

## Cambiar lo que es una PC

- **De «Caja principal» a «La única caja» o al revés:** el instalador otra vez, eligiendo la otra. Solo cambian
  `Instalacion:UnaSolaCaja` en `servicio.json` y el firewall (el 5081 se abre o se cierra).
- **Que la caja principal sea otra PC** (mudar la base): con el instalador, en tres corridas y con la misma memoria USB
  ([La base de la tienda](#la-base-de-la-tienda)): en la PC que tiene la base, «Mudar la base de esta PC a otra PC»; en la PC nueva,
  «Caja principal» y «Traer la base de otra PC»; y otra vez en la primera, «Terminar la mudanza».
- **De «Caja principal» o «La única caja» a «Otra caja»:** solo así, como final de una mudanza: con su base ya sacada, la PC viene
  como «Otra caja» y conserva su alta. Sin mudanza no se ofrece: la base de la tienda vive en esa PC.
- **Regresar una mudanza:** antes de terminarla, «Cancelar la mudanza» en la PC de donde salió la base (si la PC nueva ya tiene la
  base, primero se desinstala allá con «Borrar también los datos»: nunca dos bases vivas). Desde que «Terminar» deja la caja de
  esa PC apuntando a la PC nueva ya no se cancela: se termina. Ya terminada, regresar es otra mudanza, en sentido contrario, con
  el mismo mecanismo; la PC que la recibiría es entonces una «Otra caja» dada de alta: antes, lo del renglón que sigue. Ese
  regreso no se ha probado (el CI prueba la mudanza en un solo sentido).
- **De «Otra caja» (dada de alta) a caja principal:** no se ofrece: la caja guarda el alta de la otra caja principal. Si de
  verdad se quiere, a mano: revisar que no le quede nada por subir (la franja de la caja), desinstalar con «Borrar también los
  datos» y volver a instalar.

## Desinstalar

En Windows, Configuración › Aplicaciones (o Panel de control › Programas) › «Papelería POS» › Desinstalar. Pregunta antes:

- Quita el programa (la caja y, si lo tiene, el servicio), su acceso del escritorio y su firewall. Si la caja está abierta,
  la cierra.
- **Con el servicio se van sus variables:** la clave de Firebird y el código de Control Print. Si se vuelve a instalar, hay que
  escribirlos otra vez (el diálogo lo dice).
- **Se quedan** en `C:\ProgramData\PapeleriaPos` la base, los respaldos, `servicio.json` y lo de la caja, **salvo que se marque
  «Borrar también los datos»** (no viene marcada, y pregunta otra vez diciendo qué se va y qué se queda). Con ella se borra esa
  carpeta, **menos lo de eleventa que deja ahí el instructivo, que nunca se borra:** `respaldos-eleventa` (los respaldos de su
  base de verdad), `firebird20` (la copia de Firebird 2.0 con su gbak), `ensayo`, y la carpeta de respaldos de eleventa que diga
  `servicio.json` si está adentro. Un enlace (una «junction») ahí adentro se quita sin entrar en él: lo del otro lado no se toca.
  El registro de la caja de cada cuenta de Windows (`%LocalAppData%\PapeleriaPos\registros`) se queda.
- **La cola que medía la caja** deja de conservar los documentos impresos, y lo ya impreso se quita de ella: sin la caja, nadie los
  quitaría y se quedarían para siempre.
- **En una PC a media mudanza** (ya se le sacó la base, y no se ha terminado ni cancelado), «Borrar también los datos» se niega y no
  quita nada: su caja puede tener ventas cobradas con su copia, sin subir. Primero se corre el instalador y se termina o se cancela
  la mudanza ([La base de la tienda](#la-base-de-la-tienda)). Ya terminada, la base que quedó apartada (`antes-de-mudar-<fecha>`)
  está en esa misma carpeta: con «Borrar también los datos» se va también.
- **eleventa y Control Print no se tocan.**

## Si algo sale mal

- «Cómo quedó» dice qué pasó; el detalle (lo que dijo cada script) queda en
  `C:\ProgramData\PapeleriaPos\instalador\instalador-<fecha>.log` (se guardan los últimos 10) y el resumen en
  `C:\ProgramData\PapeleriaPos\instalador\resultado.txt`. Al desinstalar, en esa misma carpeta (`desinstalar-<fecha>.log`); con
  «Borrar también los datos», que se lleva esa carpeta, en la temporal de la cuenta (`%TEMP%\papeleria-pos-desinstalar-<fecha>.log`).
- **Si una respuesta no se puede usar,** «Instalar» lo dice y regresa a su página: en la PC no cambió nada.
- **Si no se pudo instalar o actualizar,** lo de antes se queda como estaba (si estaba actualizando, regresó solo a la versión
  de antes con su respaldo). Si dice **«NO VENDAS EN ESTA CAJA»** (no pudo regresar solo): no vender en esa caja y avisar; lo
  de antes está completo en el respaldo ([cómo regresarlo a mano](pruebas-en-tienda.md#actualizar-la-caja-2-del-piloto)).
- **Si dice que el instalador ya se está corriendo:** hay otra copia abierta (quizá en otra cuenta de Windows): se espera a que
  termine. Dos a la vez no se dejan.
- **Si el antivirus detiene al instalador** (su ayudante, `PapeleriaPos.Instalacion.exe`, corre desde la carpeta temporal), en
  una PC que ya tiene el POS no sigue, para no pisar su configuración.
- **En una mudanza,** lo que falla se queda del lado seguro y nada se borra: si no se pudo sacar la base, la PC sigue como antes;
  si no se pudo traer, la PC de antes sigue detenida (su caja vende con su copia) y se repite en la nueva con la misma memoria, o se
  cancela allá; si quedó a medias o no se pudo terminar, se repite (un «Terminar» que ya dejó la caja apuntando a la PC nueva solo
  se repite: ya no se cancela ni se regresa). Cada corrida se puede repetir si se cortó a la mitad (se fue la luz). Si la luz se va
  justo mientras se saca la base, entre deshabilitar el servicio y dejar la marca, el servicio queda deshabilitado: mientras saca la
  base el instalador deja la nota `C:\ProgramData\PapeleriaPos\mudanza-sacando.txt`, y con ella la corrida siguiente lo vuelve a
  dejar automático y en marcha antes de seguir. Solo si esa nota no se pudo dejar, al repetir dice que está deshabilitado, y se pone
  en «Automático» y se inicia en Servicios. Qué hacer en cada caso: [la guía](pruebas-en-tienda.md#si-algo-falla-en-la-mudanza).
- **A mano, como siempre:** los scripts del paquete siguen ahí ([instructivo](pruebas-en-tienda.md#instalar-la-caja-2-para-el-piloto)).

## Sin preguntar (solo el CI)

**Esto es para el CI y para quien revisa el instalador, no para instalar en la tienda:** en la tienda, siempre con el
asistente a la vista (cada página explica y revisa lo suyo).

```
PapeleriaPos-Instalador-1.0.57.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="C:\instalador.log" /ROL=principal ...
```

Lo que no se da, se queda como lo tiene la PC (en una PC nueva, `/ROL` hace falta). También sirven con el asistente a la vista
(las páginas empiezan con eso). Una respuesta que no se puede usar no pregunta nada: el instalador termina antes de cambiar nada
(sale con 7) y `resultado.txt` dice cuál en `motivo`. Si ya contesta una caja principal en la red, solo queda en `/LOG`.

| Respuesta | Valores |
|---|---|
| `/ROL=` | `principal`, `otra` o `unica` |
| `/PRINCIPAL=` | «Otra caja»: la IP de la caja principal (`192.168.1.50`, o con su puerto); vacía, la busca la caja |
| `/IMPRESORA=` | El ticket: vacía o `pantalla`, `tcp://IP:9100`, o el nombre de una impresora de Windows |
| `/COLA=` | La impresora que mide: su nombre, o vacía / `ninguna` |
| `/LECTOR=` | `teclado` o el puerto (`COM3`) |
| `/BAUDIOS=` | La velocidad del lector por COM (`9600`) |
| `/CONTROLPRINT=` | `no`, o la IP de la PC de Control Print |
| `/ELEVENTA=` | `si` o `no` |
| `/ELEVENTASERVIDOR=` | La IP de la PC de eleventa |
| `/ELEVENTABASE=` | La ruta de su base |
| `/ELEVENTACAJA=` | El número de esta caja en eleventa (o vacío) |
| `/ELEVENTACAJERO=` | El del cajero (o vacío) |
| `/ELEVENTAGBAK=` | El `gbak.exe` de Firebird 2.0 |
| `/ELEVENTARESPALDOS=` | La carpeta de los respaldos de eleventa |
| `/MUDANZA=` | La mudanza de la base ([La base de la tienda](#la-base-de-la-tienda)): `sacar` (en la PC que tiene la base), `traer` (en la PC nueva, con un rol con servicio), `terminar` (otra vez en la primera, con `/PRINCIPAL=`; queda como «Otra caja») o `cancelar`. Sin darla, sin mudanza, aunque haya un paquete junto al instalador; en una PC a media mudanza hay que darla (`terminar` o `cancelar`) |
| `/MUDANZAPAQUETE=` | La carpeta del paquete de mudanza, con su ruta completa: dónde dejarlo (`sacar`) o dónde está (`traer`); con `terminar` o `cancelar`, la carpeta que tiene el `traida.txt` de ese paquete (se busca también junto al instalador y en la carpeta a donde se sacó). Sin darla, la carpeta `mudanza` junto al instalador |
| `/MUDANZAENCIMA=` | `si`: con `traer`, si esta PC ya tiene una tienda, la aparta (no se borra) y trae la del paquete. Sin esto, encima de una tienda no se trae nada (termina sin cambiar nada) |
| `/CERRARCAJA=` | `si`: si la caja está abierta, la cierra a la fuerza (la venta que se atiende puede regresar sin lo último que se escaneó). Sin esto, con la caja abierta no instala (sale con 7) |
| `/BORRARDATOS=` | Al desinstalar (`unins000.exe /VERYSILENT /BORRARDATOS=si`): borrar también los datos |

**La mudanza sin preguntar** es la del CI: `/MUDANZA=sacar` en la PC que tiene la base, `/MUDANZA=traer` en la nueva y
`/MUDANZA=terminar` otra vez en la primera; `/MUDANZA=cancelar` la deshace antes de terminar. Al traer, lo que se da por parámetro
manda sobre lo que propone el paquete (por ejemplo, `/ELEVENTACAJA=`), y nada se pregunta: encima de una tienda solo se trae con
`/MUDANZAENCIMA=si`. Lo que no aplica a esa PC no se ignora: se rechaza sin cambiar nada, con su código de salida y su porqué en
`resultado.txt`.

- **Sale con 7** (lo detiene la revisión de antes de instalar; `programa=sin-cambios`): `terminar` o `cancelar` donde no hay una
  mudanza a medias; una PC a media mudanza sin `/MUDANZA=terminar` ni `/MUDANZA=cancelar`; un `/ROL=` que no cuadra con ella (con
  `terminar`, solo `/ROL=otra` o sin `/ROL=`; con `cancelar`, el que la PC tiene o sin `/ROL=`); `sacar` o `traer` con `/ROL=otra`;
  `sacar` en una PC sin el servicio; `traer` con un paquete que no pasa su revisión (también encima de una tienda sin
  `/MUDANZAENCIMA=si`); y `cancelar` cuando el asistente ve el `traida.txt` de ese paquete (la base ya se trajo a otra PC, y sin
  preguntar no hay cómo confirmar que allá ya se desinstaló).
- **Sale con 11** (lo detiene `instalar-pc.ps1`, también antes de cambiar nada): `sacar` si el servicio de esa PC no contesta o su
  base no tiene tienda, o si en la carpeta no se puede escribir; `terminar` si la caja principal no contesta, no tiene la base, tiene
  otra tienda o no reconoce la llave de esta caja, o si el paquete todavía trae su base sin el `traida.txt` (no se ha traído); y
  `cancelar` si la base ya está viva en otra PC.
- **Un 11 de `terminar` no siempre es «antes de cambiar nada»:** si falló al quitar el servicio o al apartar la base, `caja.json`
  ya apunta a la caja principal. Entonces `mudanza` dice `fallo`, `mudanza-mensaje` dice lo que falta y `motivo` empieza con eso:
  se repite `terminar`; ya no se cancela ni se regresa.

**Los secretos nunca van en la línea de órdenes** (quedarían en `/LOG`). El CI pone una clave y un código de mentiras en las
variables de entorno del proceso del instalador, `PAPELERIAPOS_CLAVE_ELEVENTA` y `PAPELERIAPOS_CODIGO_CONTROLPRINT`
(`.github/instalacion/instalador.ps1`); vacías, se quedan las que tiene el servicio. **Una clave de verdad nunca se escribe en
una consola** (quedaría en el historial de PowerShell): va en su casilla del asistente, que no la deja en ningún lado.

**El código de salida:**

| Código | Qué quiere decir |
|---|---|
| 0 | Quedó, y todo lo que se revisó contesta |
| 7 | No se cambió nada: una respuesta no se puede usar, o la caja está abierta (resultado.txt dice cuál en `motivo`) |
| 10 | Quedó, pero algo no contestó o no se pudo poner: eleventa, Control Print, la caja principal, WebView2, la cola de impresión, la clave o el código, o la revisión no corrió; o el programa quedó instalado y la mudanza no (`mudanza` dice `fallo` o `a-medias`, o no se supo cómo quedó) (resultado.txt dice qué) |
| 11 | No se instaló ni se actualizó: lo de antes se queda (resultado.txt dice por qué en `motivo`). Si la mudanza ya se había terminado o cancelado cuando falló lo demás, `mudanza` lo dice (`convertida` o `cancelada`) y `motivo` empieza con eso. Y si «terminar» se cortó después de dejar la caja apuntando a la caja principal, `mudanza` dice `fallo`, con lo que falta en `mudanza-mensaje`, y `motivo` empieza con eso: se repite, ya no se cancela ni se regresa |
| 12 | NO VENDAS EN ESTA CAJA: no se actualizó y no pudo regresar solo a la versión de antes (el servicio quedó detenido); lo de antes está completo en su respaldo |

Los de Inno Setup siguen igual (por ejemplo, 1: no empezó, como cuando ya se está corriendo otro instalador; 2: se canceló
antes de empezar). `C:\ProgramData\PapeleriaPos\instalador\resultado.txt` («clave=valor») lo dice por partes: `resultado`
(`bien`, `con-avisos`, `fallo` o `no-regreso`, con su `aviso`), `codigo`, `rol`, `version`, `version-antes`, `programa`
(`instalado`, `actualizado`, `igual`, `fallo` o `sin-cambios`), `configuracion` y `cambios`, `cola`, `webview2`, `servicio`,
`eleventa`, `controlprint`, `principal` (`contesta`, `no-contesta` o `no-aplica`), `revision` (`bien` o `fallo`, con su
`revision-motivo`), `clave-eleventa` y `codigo-controlprint` (`puesta`/`puesto`, `se-quedo`, `no-hay`, `quitado`…; nunca su
valor), `motivo` y `registro`. En una mudanza, además (solo si se pidió una o el script dijo una; sin mudanza, el archivo queda como
siempre): `mudanza` (`sacada`, `traida`, `ya-estaba`, `a-medias`, `convertida`, `cancelada` o `fallo`), `mudanza-mensaje` (el
porqué, o lo que falta), `mudanza-paquete` (al sacar, la carpeta donde quedó; al traer, `borrado` cuando ya se quitó de ahí la
base) y `mudanza-alta` (al terminar: `se-conserva`, `otra-vez` o `no-habia`); y en la PC a la que se le sacó la base, `servicio`
dice `mudada` (detenido a propósito), no `no-contesta`, y `eleventa` y `controlprint`, `no-aplica`.

## Cómo está hecho

- **El asistente** es [Inno Setup](https://jrsoftware.org/isinfo.php) (`instalacion/papeleria-pos.iss`): solo pregunta y pasa
  las respuestas. Corre como administrador, para todas las cuentas, en Windows de 64 bits.
- **El trabajo lo hacen los scripts de siempre,** con `instalacion/instalar-pc.ps1`: la primera vez `instalar-servicio.ps1`
  (con `-SinBuscarEnLaRed` en «La única caja») e `instalar-caja.ps1`; si ya estaba, `actualizar.ps1`. Después, el firewall según
  lo que es la PC: el 5080 para la red local en «Caja principal» y «La única caja», y el 5081 (buscarla en la red) solo en la
  caja principal. Los revisa `poner-clave-eleventa.ps1` y `poner-codigo-controlprint.ps1` con `-SoloRevisar` (sin pasarles la
  clave).
- **`servicio.json` y `caja.json`** los lee y los escribe su ayudante, `PapeleriaPos.Instalacion.exe`
  (`src/PapeleriaPos.Instalacion`, con sus pruebas en Linux), que también busca la caja principal en la red y dice qué
  impresoras, puertos COM y `gbak.exe` tiene la PC. Del alta de la caja nunca quita nada. Antes de cambiar nada, el asistente
  le pide revisar las respuestas sin escribir (`revisar`): el asistente y el ayudante dejan pasar lo mismo. Como corre como
  administrador y la carpeta de la caja la puede cambiar cualquier cuenta, nunca escribe a través de un enlace (si la carpeta,
  el archivo, su `.nuevo` o su `.anterior` lo son, no escribe y lo dice).
- **La clave y el código** van de su casilla directo al registro de Windows (el valor `Environment` del servicio), sin tocar
  las demás variables, como lo hacen los scripts.
- **La mudanza de la base** ([La base de la tienda](#la-base-de-la-tienda)) reparte el trabajo igual. El paquete lo saca el
  programa del servicio (`PapeleriaPos.Servicio.exe mudanza-sacar`, sin arrancar el servicio, como `pin-usuario`). El ayudante lo
  lee y lo revisa (`mudanza-ver`, `mudanza-revisar`), escribe la configuración (`escribir --mudanza` en la PC nueva y
  `escribir --mudada-a` para la caja de la PC de antes), hace la prueba de identidad con la llave de la caja (`probar --esta-caja`:
  la abre en esa misma PC y nunca la escribe en ningún lado) y busca si esa tienda ya vive en otra PC (`mudanza-viva`). La base se
  pone con `restaurar.ps1 -PorMudanza`. Y el orden lo lleva `instalar-pc.ps1`, para que lo que quede a medias sea siempre seguro: al
  traer, primero la base, con el puente apagado, y hasta el final la configuración que lo prende; al terminar, primero lo que se
  puede deshacer, y la marca se aparta al final, para poder repetir.
- **WebView2:** si falta, el instalador de Microsoft que viene adentro (el chico, que lo baja de internet), descargado en el CI
  de su liga oficial y solo si viene firmado por Microsoft.
- **Lo que revisa el CI** (`.github/workflows/prueba.yml`, el trabajo «instalador-en-windows»), en un Windows limpio y sin
  preguntar: otra copia corriendo (no empieza) y una respuesta que no se puede usar (sale con 7 sin cambiar nada); «Otra caja»
  (sin servicio, `caja.json` a la caja principal que se dio), desinstalarla; «Caja principal» con eleventa
  y Control Print de mentiras (el servicio contesta, `servicio.json` y `caja.json` como se pidió, el firewall, la clave y el
  código de mentiras en las variables del servicio y en ningún archivo ni registro); «La única caja» encima
  (`Instalacion:UnaSolaCaja`, el 5081 cerrado y el 5080 abierto); desinstalar sin borrar (la base y la configuración se quedan);
  una caja instalada con los scripts de hoy que se actualiza (su base, su `servicio.json` y su alta se quedan, byte por byte) y
  desinstalar borrando los datos (lo de eleventa se queda, y una junction no se sigue). Y el flujo «Instalación», los scripts.
- **La mudanza en el CI** (el mismo flujo, el trabajo «mudanza-en-windows»): corre después de armar el instalador, con ese mismo
  instalador y sin preguntar, unos 10 minutos. Un solo equipo hace de las dos PC, una después de la otra, y lo único que pasa de
  una a otra es el paquete, en la carpeta `mudanza` junto al instalador. El flujo se arma también en un PR que toque lo de la
  mudanza en el servicio.
  1. **La caja 2:** instala «La única caja» y, como lo haría la caja, crea su tienda y da de alta una caja de verdad (la «Caja 2»,
     serie B, con su llave cifrada para esa PC en `caja.json`); cobra una venta y deja el puente puesto a mano escribiendo, como la
     caja 3 de eleventa y con su cajero, con una clave de mentiras en el servicio.
  2. **Saca** la base: el servicio queda detenido y deshabilitado, con la marca y el paquete completo (`mudanza.txt` con sus cajas
     de eleventa: `B=3`); `servicio.json` y `caja.json`, iguales byte por byte; habilitado y arrancado a mano, el servicio no revive
     y dice por qué; `restaurar.ps1` se niega; y desinstalar con «Borrar también los datos» se niega sin quitar nada (ni su
     entrada para desinstalar).
  3. **Cancela:** vuelve a contestar como antes, sin la marca y con el paquete borrado. Y la **saca** otra vez: es otro paquete.
  4. **Termina** contra un servicio suelto en otro puerto, que hace de caja principal con la base del paquete y al que se le habla
     por la IP de red del equipo (no por `localhost`). Mientras el paquete trae su base y le falta `traida.txt`, terminar se niega
     sin cambiar nada, aunque esa principal conteste con la misma tienda y conozca la llave de la caja. Con el `traida.txt` que
     deja ahí la prueba (el que dejaría la principal al traer), termina: queda sin servicio (ni su programa ni su firewall), con la
     base, `servicio.json`, `pos.llave` y la marca en `antes-de-mudar-…`, `caja.json` con la dirección nueva, la llave pública de
     ese servicio y el alta intacta, y la llave de la caja entra en él.
  5. **La principal:** desinstala borrando los datos (ya terminada la mudanza, no se niega) y **trae** el paquete: la base con su
     tienda, la llave de la caja entra (con su turno y su venta), `servicio.json` con `Cajas` (`B` → 3), `Escribir` en true y sin
     `Caja`, la bitácora con «Se mudó aquí la base de…» una sola vez, la nota `mudanza-puesta.txt`, `traida.txt` con la IP del
     equipo y el paquete ya sin su base. Traído otra vez el mismo paquete: ya estaba; ni se trae de nuevo ni cambia `servicio.json`.

  En cada paso revisa que ni la clave de mentiras ni la llave de la caja queden en un archivo o en un registro. Si algo falla,
  enseña y sube los registros (`registros-de-la-mudanza`).
- **La mudanza en las pruebas de siempre** (en Linux y en Windows, en todo PR), con bases de prueba y el servicio de verdad en
  memoria: sacar el paquete de una base con su caja, sus ventas y lo pendiente del puente; ponerlo en un segundo servicio, que
  conoce la llave de la caja, deja lo pendiente como «ya se intentó» y lo anota en la bitácora; la caja de verdad, que mientras
  tanto cobró con su copia y, con la conexión como la deja «Terminar» (ahí, armada por la prueba), sube lo suyo al servicio nuevo
  sin pedir alta; que con la marca el servicio no arranca; cada regla de la revisión del paquete; lo que se escribe en
  `servicio.json` y en `caja.json`; y la prueba de identidad con la llave de la caja.
- **Compilarlo en Linux** (para no esperar al CI): Inno Setup 6.7.1 bajo Wine, con su propio prefijo, y `ISCC.exe
  /DVersion=… instalacion\papeleria-pos.iss` con lo de `publicar\` al lado (el ayudante publicado para Windows; el paquete y el
  instalador de WebView2 pueden ser de mentiras para compilar). Un renglón del Pascal no puede empezar con «[»: Inno lo toma por
  una sección.

## Lo que solo se ve en Windows o en la tienda

- **Las páginas como se ven en Windows** (bajo Wine se ven y se recorren, con otra letra) y lo que no se puede en el CI:
  «Buscar en la red» con una caja principal de verdad (y el aviso de «Ya hay una caja principal en la red»), la lista de
  impresoras y de puertos COM de una PC de la tienda, «Buscar…» del gbak.
- **WebView2 en una PC que no lo tiene** (el instalador de Microsoft necesita internet).
- **De la mudanza:** la página «La base de la tienda» como se ve (el CI la recorre sin preguntar; bajo Wine se recorrió con un
  paquete hecho a mano); la memoria USB de verdad (su espacio, expulsarla, que cambie de letra); dos PC de verdad (en el CI las dos
  «PC» son el mismo equipo, donde la llave cifrada para la PC se abre igual, y se termina antes de traer: ahí no se prueba la
  dirección que trae `traida.txt` ni el alta que se pide otra vez); que se vaya la luz a media corrida; y el puente llegando a la
  caja principal con el eleventa real (`127.0.0.1`, su `gbak.exe`, `Eleventa:Cajas`). Se ve el fin de semana de la mudanza, con el
  dueño ([la guía](pruebas-en-tienda.md#el-piloto-con-el-servicio-en-la-caja-principal)).
- **En la tienda,** un fin de semana y con el dueño: actualizar la caja 2 con el instalador (lo que el CI simula con una
  instalación de los scripts), que eleventa y Control Print de verdad contesten con lo que ya tiene, y «Conservar documentos
  impresos» en la cola de la WF-6590. Nada se instala en la caja principal sin el dueño.
- **En la segunda sucursal:** instalar desde cero como «La única caja» y terminar en la caja (la tienda y la cuenta del dueño).
