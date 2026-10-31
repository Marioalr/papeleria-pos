# Instalar Papelería POS

Un solo programa, `PapeleriaPos-Instalador-1.0.<número>.exe`, para poner Papelería POS en una PC de la tienda, en la matriz o en
la segunda sucursal: hace unas preguntas sencillas y lo instala todo, sin PowerShell ni Bloc de notas. Si la PC ya lo tiene
(instalado con este instalador o a mano con los scripts), lo actualiza sin perder nada. Los pasos a mano de siempre
([instructivo de la tienda](pruebas-en-tienda.md#instalar-la-caja-2-para-el-piloto)) se quedan como respaldo.

- **Lo corre el administrador de la PC** (Windows pide permiso al abrirlo). Después la caja se usa con la cuenta de Windows
  de todos los días ([plan](plan.md#instalación-y-usuarios-de-windows)).
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
- **Internet** solo hace falta si a la PC le falta WebView2 (lo usa la pantalla de la caja; Windows 10 al día y Windows 11 ya lo
  traen).

## Página por página

Cada página dice arriba para qué es. Las que no aplican a lo que es la PC no salen. Si la PC ya tiene el POS, cada página
empieza con lo que tiene: basta con «Siguiente» para dejar todo igual.

**En la matriz, hoy (el piloto):** la caja 2 ya está instalada (con los scripts): el instalador la **actualiza**, y todo
empieza como está; se deja así. En la caja principal de la tienda no se instala nada sin que el dueño lo decida.
**En la segunda sucursal:** empieza con una sola PC ([plan](plan.md#para-el-piloto-con-dos-cajas)): es **«La única caja»**.

| Página | Para qué es | En la matriz (la caja 2 del piloto) | En la segunda sucursal (una sola PC) |
|---|---|---|---|
| Bienvenida | Dice qué hace, y si la PC ya tiene el POS (y cuál) | Dice que ya está y que se actualiza sin perder nada | Dice que lo instala |
| **Qué es esta PC** | Lo que hará en la tienda: de eso depende lo que se instala | «Caja principal» (ya viene elegida: la caja 2 tiene su propio servicio en el piloto) | «La única caja» |
| **Dónde está la caja principal** (solo «Otra caja») | A qué PC se conecta esta caja: su IP, «Buscar en la red» o vacía (la caja la busca al abrirla) | No sale | No sale |
| **Las impresoras de esta PC** | Dónde sale el ticket (en pantalla, por la red con su IP o una de Windows) y qué impresora mide la caja | Como está: el ticket por la IP de la POS-8360; la que mide, la cola de la WF-6590 | El ticket: «Por la red» con la IP de su impresora de tickets (o una de Windows); la que mide, la de inyección de esa PC, si la hay |
| **El lector de códigos** | Cómo llega el código: como teclado (lo normal) o por puerto COM | Como está: por COM, el puerto y la velocidad de eleventa | «Como teclado», salvo que su lector esté como puerto COM |
| **Control Print** (con servicio) | Si la caja enseña la cola por cobrar de Control Print (solo la lee) | Como está: «Sí», la IP de la caja principal; el código, vacío (se queda el que tiene) | «No», salvo que esa tienda use Control Print |
| **eleventa** (con servicio) | Si la caja convive con eleventa y lee su catálogo y existencias | Como está: «Sí», con su IP, su base, la caja 3 y el cajero | «No», salvo que esa PC tenga eleventa y el dueño quiera que la caja lo lea |
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

Si la PC ya tiene el servicio, no puede ser «Otra caja» (su base vive ahí). Si es «Otra caja» dada de alta con una caja
principal, sigue siendo «Otra caja» ([cambiar lo que es una PC](#cambiar-lo-que-es-una-pc)).

### Dónde está la caja principal (solo «Otra caja»)

- **«Buscar en la red»** encuentra las cajas principales que contestan, como lo hace la caja (la misma búsqueda); si hay una,
  la pone. **«Probar»** revisa que la de la IP escrita conteste.
- **Vacía** también sirve: al abrir la caja, en su pantalla de alta, «Buscar en la red».
- Si la que se escribió no contesta, pregunta si seguir de todos modos: la caja la busca ahí al abrirla.

### Las impresoras de esta PC

- **El ticket:** «En pantalla» (sin imprimir), «Por la red» con la IP de la impresora de tickets (`tcp://IP:9100`, lo
  recomendado: así la usan las dos cajas sin depender de otra PC) o el nombre de una impresora de Windows.
- **La impresora que mide la caja:** la de inyección, cuando se imprime desde esta PC (`ColaImpresion`). A esa se le activa
  «Conservar documentos impresos» (como `instalar-caja.ps1 -ColaImpresion`): la caja mide cada trabajo y después lo quita.
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
  eleventa).
  - La IP de la PC de eleventa: si la PC tiene eleventa, viene la de su `pdventa.ini` (`IPServidor`).
  - La base: la de una instalación normal, `C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB`.
  - El número de esta caja en eleventa (en la matriz, la caja 2 es la 3) y el del cajero (el del dueño, que dice
    `importar-eleventa`); si todavía no se saben, vacíos: solo hacen falta para escribirle.
- **La base de la caja no se arma aquí.** Para empezar con la de eleventa (con sus usuarios y su historial),
  `importar-eleventa`, como dice [el instructivo](pruebas-en-tienda.md#instalar-la-caja-2-para-el-piloto) (pasos 4 y 5). Si no,
  al abrir la caja se crea la tienda y la lectura trae el catálogo.
- **No** en una PC donde el puente ya está prendido: no se puede elegir. El puente se apaga con el instructivo
  ([regresar o terminar el piloto](pruebas-en-tienda.md#regresar-o-terminar-el-piloto)), revisando antes lo pendiente.

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
correctos, corriendo el instalador otra vez). Abajo dice dónde quedó el detalle.

«Terminado» ofrece **«Abrir la caja para terminar»** (se abre como el usuario que abrió el instalador, no como
administrador). En la caja:

- **Caja principal o la única caja con la base nueva:** la caja pide la tienda, la cuenta del dueño y esta caja; después,
  «Terminar la instalación» repasa la impresora de tickets, **los costos de impresión** (viven en la base, se ponen ahí), el
  lector, y eleventa y Control Print si los hay.
- **Una caja principal que ya tiene su tienda, y «Otra caja»:** el alta de siempre: el usuario y el PIN del dueño.
- **En la caja principal**, la página dice cómo instalar las otras cajas: este mismo instalador en cada PC, eligiendo «Otra
  caja»; encuentran esta en la red, o con su IP (la dice).

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
- **De «Caja principal» o «La única caja» a «Otra caja»:** no se ofrece: la base de la tienda vive en esa PC.
- **De «Otra caja» (dada de alta) a caja principal:** no se ofrece: la caja guarda el alta de la otra caja principal. Si de
  verdad se quiere, a mano: revisar que no le quede nada por subir (la franja de la caja), desinstalar con «Borrar también los
  datos» y volver a instalar.

## Desinstalar

En Windows, Configuración › Aplicaciones (o Panel de control › Programas) › «Papelería POS» › Desinstalar. Pregunta antes:

- Quita el programa (la caja y, si lo tiene, el servicio con sus variables), su acceso del escritorio y su firewall. Si la
  caja está abierta, la cierra.
- **Se quedan** en `C:\ProgramData\PapeleriaPos` la base, los respaldos, `servicio.json` y lo de la caja, **salvo que se marque
  «Borrar también los datos»** (no viene marcada, y pregunta otra vez). Con ella se borra toda esa carpeta (también los respaldos
  de eleventa y la copia de Firebird 2.0 del instructivo). El registro de la caja de cada cuenta de Windows
  (`%LocalAppData%\PapeleriaPos\registros`) se queda, y la cola de impresión sigue con «Conservar documentos impresos».
- **eleventa y Control Print no se tocan.**

## Si algo sale mal

- «Cómo quedó» dice qué pasó; el detalle (lo que dijo cada script) queda en
  `C:\ProgramData\PapeleriaPos\instalador\instalador-<fecha>.log` (se guardan los últimos 10) y el resumen en
  `C:\ProgramData\PapeleriaPos\instalador\resultado.txt`. Al desinstalar, en esa misma carpeta (`desinstalar-<fecha>.log`); con
  «Borrar también los datos», que se lleva esa carpeta, en la temporal de la cuenta (`%TEMP%\papeleria-pos-desinstalar-<fecha>.log`).
- **Si no se pudo instalar o actualizar,** lo de antes se queda como estaba (si estaba actualizando, regresó solo a la versión
  de antes con su respaldo). Si dice que no pudo regresar solo: no vender en esa caja y avisar; lo de antes está completo en el
  respaldo ([cómo regresarlo a mano](pruebas-en-tienda.md#actualizar-la-caja-2-del-piloto)).
- **Si el antivirus detiene al instalador** (su ayudante, `PapeleriaPos.Instalacion.exe`, corre desde la carpeta temporal), en
  una PC que ya tiene el POS no sigue, para no pisar su configuración.
- **A mano, como siempre:** los scripts del paquete siguen ahí ([instructivo](pruebas-en-tienda.md#instalar-la-caja-2-para-el-piloto)).

## Sin preguntar (el CI, o soporte)

```
PapeleriaPos-Instalador-1.0.57.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="C:\instalador.log" /ROL=principal ...
```

Lo que no se da, se queda como lo tiene la PC (en una PC nueva, `/ROL` hace falta). También sirven con el asistente a la vista
(las páginas empiezan con eso).

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
| `/CERRARCAJA=` | `si`: si la caja está abierta, la cierra a la fuerza (la venta que se atiende puede regresar sin lo último que se escaneó). Sin esto, con la caja abierta no instala (sale con 7) |
| `/BORRARDATOS=` | Al desinstalar (`unins000.exe /VERYSILENT /BORRARDATOS=si`): borrar también los datos |

**Los secretos nunca van en la línea de órdenes** (quedarían en `/LOG`): sin preguntar, se toman de las variables de entorno
del proceso del instalador, `PAPELERIAPOS_CLAVE_ELEVENTA` (la clave de Firebird) y `PAPELERIAPOS_CODIGO_CONTROLPRINT` (el
código de Control Print). Vacías, se quedan las que tiene el servicio. Con el asistente a la vista no se leen: se escriben en su
casilla.

**El código de salida:**

| Código | Qué quiere decir |
|---|---|
| 0 | Quedó, y todo lo que se revisó contesta |
| 10 | Quedó, pero algo no contestó o no se pudo poner: eleventa, Control Print, la caja principal, WebView2, la cola de impresión, la clave o el código (resultado.txt dice qué) |
| 11 | No se instaló ni se actualizó: lo de antes se queda (resultado.txt dice por qué en `motivo`) |
| 12 | No se actualizó y no pudo regresar solo a la versión de antes: lo de antes está completo en su respaldo |

Los de Inno Setup siguen igual (por ejemplo, 2: se canceló antes de empezar; 7: no pudo prepararse, como con la caja abierta).
`C:\ProgramData\PapeleriaPos\instalador\resultado.txt` («clave=valor») lo dice por partes: `resultado` (`bien`, `con-avisos` o
`fallo`), `codigo`, `rol`, `version`, `version-antes`, `programa` (`instalado`, `actualizado`, `igual` o `fallo`),
`configuracion` y `cambios`, `cola`, `webview2`, `servicio`, `eleventa`, `controlprint`, `principal` (`contesta`, `no-contesta`
o `no-aplica`), `clave-eleventa` y `codigo-controlprint` (`puesta`/`puesto`, `se-quedo`, `no-hay`, `quitado`…; nunca su valor),
`motivo` y `registro`.

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
  impresoras, puertos COM y `gbak.exe` tiene la PC. Del alta de la caja nunca quita nada.
- **La clave y el código** van de su casilla directo al registro de Windows (el valor `Environment` del servicio), sin tocar
  las demás variables, como lo hacen los scripts.
- **WebView2:** si falta, el instalador de Microsoft que viene adentro (el chico, que lo baja de internet), descargado en el CI
  de su liga oficial y solo si viene firmado por Microsoft.
- **Lo que revisa el CI** (`.github/workflows/prueba.yml`, el trabajo «instalador-en-windows»), en un Windows limpio y sin
  preguntar: «Otra caja» (sin servicio, `caja.json` a la caja principal que se dio), desinstalarla; «Caja principal» con eleventa
  y Control Print de mentiras (el servicio contesta, `servicio.json` y `caja.json` como se pidió, el firewall, la clave y el
  código de mentiras en las variables del servicio y en ningún archivo ni registro); «La única caja» encima
  (`Instalacion:UnaSolaCaja`, el 5081 cerrado y el 5080 abierto); desinstalar sin borrar (la base y la configuración se quedan);
  una caja instalada con los scripts de hoy que se actualiza (su base, su `servicio.json` y su alta se quedan, byte por byte) y
  desinstalar borrando los datos. Y el flujo «Instalación», los scripts.

## Lo que solo se ve en Windows o en la tienda

- **Las páginas como se ven** (el tamaño de los textos, los avisos en rojo) y lo que no se puede en el CI: «Buscar en la red»
  con una caja principal de verdad, la lista de impresoras y de puertos COM de una PC de la tienda, «Buscar…» del gbak.
- **WebView2 en una PC que no lo tiene** (el instalador de Microsoft necesita internet).
- **En la tienda,** un fin de semana y con el dueño: actualizar la caja 2 con el instalador (lo que el CI simula con una
  instalación de los scripts), que eleventa y Control Print de verdad contesten con lo que ya tiene, y «Conservar documentos
  impresos» en la cola de la WF-6590. Nada se instala en la caja principal sin el dueño.
- **En la segunda sucursal:** instalar desde cero como «La única caja» y terminar en la caja (la tienda y la cuenta del dueño).
