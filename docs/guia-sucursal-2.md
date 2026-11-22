# Guía de la sucursal 2: el POS junto a eleventa en una sola PC

Los pasos en la tienda para la sucursal 2, donde hay **una sola PC**: ahí están eleventa, su base Firebird y el POS completo
(el servicio y la caja), y el POS lee y escribe en la base de eleventa **de esa misma PC**. Eleventa sigue siendo el oficial y
sigue abierto: si algo falla, se vende ahí en el momento.

Es la misma guía del piloto ([pruebas en la tienda](pruebas-en-tienda.md#el-piloto-alfa-en-la-caja-2)) vuelta a escribir para una
PC. Donde aquella dice «la caja principal» o «la caja 2», aquí es **esta PC**, y la IP de eleventa es `127.0.0.1`.

- **Cuándo:** solo sábado y domingo, con el dueño. Entre semana no se toca esa PC ni se le llama desde fuera.
- **Decidido por el dueño (2026-11-21):** en la sucursal se cobra solo efectivo, y la PC se instala como **«Caja principal»**
  para poder dar de alta el celular («La única caja» rechaza el alta de otros equipos).
- **Ensayado antes en la PC de desarrollo (2026-11-21)** contra una copia de la base de la sucursal: leer, vender en la misma caja
  y el mismo turno que eleventa, cancelar, devolver, el respaldo, el corte y un número de caja equivocado. No se ha visto con
  el eleventa de verdad, con su Firebird 2.0.1 ni con el instalador en esa PC: eso es lo que prueban estas visitas.

## Dónde va (2026-11-22)

**Hechos los pasos 1 a 4. Sigue el [paso 5](#5-la-primera-escritura-en-la-base-real): la primera escritura en la base real,**
que el dueño decidió para ese mismo día, más tarde. Quien retome empieza por leer `…/api/eleventa` y `…/api/eleventa/envios`.

**Cómo quedó la PC:**

- El instalador 1.0.63 (commit `757dc19`) y las herramientas 1.0.4, del mismo commit. La PC tenía un POS de una prueba del día
  anterior: se desinstaló con «Borrar también los datos» y se instaló de cero, como «Caja principal».
- Lo del paso 1 se cumple: Firebird 2.0 como servicio y contestando en el 3050, el `gbak.exe` y el `isql.exe` de eleventa en
  `FirebirdSQL20\bin`, Windows de 64 bits con disco de sobra, el lector como teclado, el ticket por una cola de Windows, sin
  cajón, y el respaldo de eleventa al salir prendido en su carpeta de siempre.
- La base del POS salió de `importar-eleventa` contra la copia. `Eleventa:Caja` y `Eleventa:Cajero` ya están en `servicio.json`.
- **El puente quedó leyendo la base real, con `Escribir` en `false`:** `…/api/eleventa` dice `"bien": true` con `origen`
  `…PDVDATA.FDB`; `…/api/eleventa/envios`, `"activo": false`, `"problema": null` y `"enviadas": 0`. No se vende en el POS.
- Junto a la base real quedaron las dos copias, `PDVDATA-ENSAYO.FDB` y `PDVDATA-ENSAYO-2.FDB` (no se borran). Otro ensayo
  usaría `PDVDATA-ENSAYO-3.FDB`. Los dos .zip de `juntar-datos-piloto.ps1` están en
  `C:\ProgramData\PapeleriaPos\datos-del-piloto`.

**Lo que se ensayó contra las copias** (dos ensayos, cada uno regresado con su respaldo del punto de partida):

- Ventas en efectivo, una con precio por cantidad, una con descuento (Ctrl+D), una con artículo común, una entrada y una
  salida: folios seguidos después del último de eleventa, y el turno, el corte, los departamentos y las existencias sumaron lo
  vendido.
- Devolver parte de una venta y cancelar otra: todas las comprobaciones de `observar-eleventa` salieron iguales.
- Cerrar eleventa con el servicio prendido: su respaldo de salida salió completo, y al abrirlo otra vez la lectura siguió bien.
- Después de cada ensayo, a la base real no había llegado nada (visto en eleventa y, la segunda vez, en la copia nueva).

**Lo que falta ver,** todo en el paso 5: el corte con el puente prendido, cerrar eleventa mientras se escribe, la reimpresión
(no se confirmó) y qué pasa si eleventa no contesta.

**Lo que se detectó ese día, por corregir:**

- **`servicio.json` trae arriba un ejemplo comentado de `"Eleventa"`,** y los cambios de `Base` y de `Cajero` se hicieron ahí
  la primera vez: el servicio siguió leyendo la base real. Los pasos 2.5 y 3.3 deben decir que es la sección sin `//`, o el
  ejemplo debe salir de un `servicio.json` que ya tiene la sección de verdad.
- **«Respaldar ahora» del paso 3.1 se hizo en eleventa** en vez de en la caja del POS: el paso debe decir en qué programa va
  cada cosa. El respaldo a mano de eleventa cierra eleventa; hay que abrirlo otra vez y revisar su turno.
- **La guía supone una PC sin POS.** Si ya tiene uno de una prueba, antes se desinstala con «Borrar también los datos».
- **La impresora que mide quedó puesta** (la guía pide ninguna): su cola conserva lo impreso mientras la caja no esté abierta.
  Se quita corriendo el instalador otra vez.
- **Los usuarios se ven con el nombre completo de eleventa, no con su usuario:** un cajero salió con un nombre que la tienda
  no reconocía y pareció que faltaba uno y sobraba otro.
- **Un precio por cantidad llegó a eleventa como `PORCENTAJE_DESCUENTO`, sin `USA_MAYOREO`.** Falta que la tienda diga si en
  eleventa ese producto baja por promoción o por mayoreo.
- **`observar-eleventa` pide «Haz algo en eleventa»** y compara con «la caja 2», cuando en el ensayo las cosas se hacen en la
  caja del POS de esta PC.
- **Al restaurar un respaldo desde la caja, la caja siguió abierta.** Falta ver si debe cerrarse o volver a pedir el PIN.
- **El contador de tickets de la base real avanzó sin tickets nuevos** entre la copia de la mañana y la del mediodía, con
  eleventa cerrado y abierto en medio. No cambió los folios; falta saber por qué.
- **El segundo `gbak -c` sobre una copia que ya existe se niega** («already exists»): es lo esperado, y nunca se usa `-REP`.
- Windows dice «Firebird 2.0.1» y su `gbak.exe` dice 2.0.3.
- Sin administrador no se leen `servicio.json`, los registros ni los respaldos; lo que contesta el servicio sí.

## Lo que nunca se hace

- Escribir en `PDVDATA.FDB` antes del [paso 5](#5-la-primera-escritura-en-la-base-real), restaurar encima de ella, o borrar o
  renombrar algo en su carpeta.
- Usar el `gbak` de Firebird 2.5: su restauración deja la base en un formato que el Firebird 2.0 de eleventa ya no abre.
- Anotar la clave de Firebird. La escribe el dueño cada vez, y la ventana donde se escribió se cierra al terminar.
- Dar por buena una base sin leer su nombre en pantalla. **La copia del ensayo y la base real viven en el mismo Firebird:**
  antes de cada paso se lee `origen` o `destino` y se comprueba cuál es.

## Lo que se lleva

Se prepara entre semana ([de dónde sale](instalacion.md#antes-de-empezar)):

- La memoria USB con el instalador de una corrida de «Instalador de prueba» **entera en verde**, y solo ese `.exe`.
- `PapeleriaPos.Herramientas.exe`, del flujo «Herramientas para la tienda», **del mismo commit** que el instalador.
- Espacio libre en la memoria para los datos que se juntan al final.

## 1. Conocer la PC, sin instalar nada

Solo mirar y anotar. Antes de todo, **un respaldo de eleventa con su propia función**, como lo hace el dueño.

| Qué | Cómo se ve | Sirve si |
|---|---|---|
| La carpeta de eleventa | `C:\Program Files (x86)\AbarrotesMultiCaja`, con `db\PDVDATA.FDB` y `pdventa.ini` | Existe. Si la carpeta es otra, se anota la ruta completa de `PDVDATA.FDB` |
| Firebird como servicio | En PowerShell: `Get-Service *firebird*` | Sale un servicio «Running» |
| Firebird contesta por la red | `Test-NetConnection 127.0.0.1 -Port 3050` | `TcpTestSucceeded : True` |
| El `gbak` de Firebird 2.0 | `Get-ChildItem "C:\Program Files*" -Recurse -Filter gbak.exe -ErrorAction SilentlyContinue` y, con su ruta, `& "<ruta>\gbak.exe" -z` | Dice una versión 2.0 (`WI-V2.0…`) |
| Windows y disco | Configuración › Sistema › Acerca de; el Explorador | 64 bits y 10 GB libres |
| El lector | En eleventa, su configuración del lector | Se anota: como teclado, o el puerto COM y su velocidad |
| La impresora de tickets y el cajón | Configuración › Impresoras | Se anota el nombre de la cola o su IP |
| El respaldo de eleventa al salir | En eleventa, Configuración › Mantenimiento | Se anota si está prendido y a qué carpeta |

**Si Firebird no sale como servicio o no contesta en el 3050, aquí se detiene todo.** El puente solo se conecta por la red,
aunque sea dentro de la misma PC; con un Firebird embebido no hay puente, y se avisa antes de instalar nada.

**El número de la caja de eleventa en esta PC.** La base de la sucursal salió de la de la matriz y trae sus cajas (1 «Caja
Principal», 2 «Secundaria», 3 «Caja 2», 4 «Cajero3»); esta PC es otra, así que hay que leer con cuál abre. Con eleventa abierto
y su turno del día, en PowerShell, con el `isql.exe` que está junto al `gbak.exe` (solo lee):

```
$env:ISC_USER = "SYSDBA"
$c = Read-Host "Clave de Firebird" -AsSecureString
```

La escribe el dueño (no se ve), Enter, y luego:

```
$env:ISC_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($c))
& "<ruta>\isql.exe" "127.0.0.1:C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB"
```

Adentro:

```
SELECT ID, NOMBRE, ULTIMO_INGRESO FROM CAJAS;
SELECT ID, ID_CAJA, ID_CAJERO, INICIO_EN FROM TURNOS WHERE TERMINO_EN IS NULL;
QUIT;
```

La caja de esta PC es la del turno abierto de hoy (`ID_CAJA`), que coincide con el `ULTIMO_INGRESO` de hoy en `CAJAS`. **Se
anotan ese número y el `ID_CAJERO`.** Se cierra la ventana: tiene la clave en sus variables.

## 2. Instalar y dejarlo leyendo

`Escribir` queda apagado todo este paso. **No se vende en el POS.**

1. **Las herramientas:** en PowerShell como administrador, `mkdir C:\PapeleriaPos-paquete` y copiar ahí
   `PapeleriaPos.Herramientas.exe`.
2. **El instalador** ([sus páginas](instalacion.md)):

   | Página | Qué se contesta |
   |---|---|
   | Qué es esta PC | **«Caja principal»** |
   | La base de la tienda | Como viene: «Empezar sin base (tienda nueva)» |
   | Las impresoras | La de tickets: su cola de Windows o su IP. La que mide: ninguna |
   | El lector | Como se anotó. Con puerto COM, solo uno de los dos programas lo tiene a la vez |
   | Control Print | «No» |
   | eleventa | «Sí». Servidor `127.0.0.1`; la base, la ruta que se anotó; **el número de esta caja, el que se anotó**; el cajero, vacío |
   | eleventa: respaldos y clave | El `gbak.exe` 2.0 de esta PC, la carpeta de siempre y la clave (la escribe el dueño) |

   Al final, «Abrir la caja para terminar» se deja **sin marcar**. Es normal que «Cómo quedó» diga que eleventa todavía no se lee
   porque no hay tienda.
3. **La copia de la base,** con eleventa abierto y su turno del día (la copia lleva ese turno, que el ensayo necesita). En una
   ventana de PowerShell como administrador, en tres bloques; cada uno se pega cuando el anterior terminó:

   ```
   $gbak = "<ruta>\gbak.exe"
   $base = "C:\Program Files (x86)\AbarrotesMultiCaja\db"
   $env:ISC_USER = "SYSDBA"
   $c = Read-Host "Clave de Firebird" -AsSecureString
   ```

   ```
   $env:ISC_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($c))
   mkdir C:\ProgramData\PapeleriaPos\ensayo
   & $gbak -b -g -v "127.0.0.1:$base\PDVDATA.FDB" C:\ProgramData\PapeleriaPos\ensayo\pdvdata-ensayo.fbk
   $LASTEXITCODE
   ```

   ```
   & $gbak -c -v C:\ProgramData\PapeleriaPos\ensayo\pdvdata-ensayo.fbk "127.0.0.1:$base\PDVDATA-ENSAYO.FDB"
   $LASTEXITCODE
   ```

   - El segundo respalda la base real solo leyendo. El tercero crea un archivo **nuevo** junto a ella; si ya existe, se niega.
     Cada uno termina bien si `$LASTEXITCODE` dice `0`. El tercero se pega solo si el segundo dijo `0`.
   - **Revisar dos veces que la ruta del tercero termine en `PDVDATA-ENSAYO.FDB`.**
   - Si el tercero se corta, no se borra nada: se repite con `PDVDATA-ENSAYO-2.FDB` y se usa ese nombre en adelante.
4. **La base del POS, de esa copia, con el dueño,** en la misma ventana:

   ```
   cd C:\PapeleriaPos-paquete
   mkdir base
   .\PapeleriaPos.Herramientas.exe importar-eleventa --servidor 127.0.0.1 --ruta "$base\PDVDATA-ENSAYO.FDB" --usuario SYSDBA --salida "base\pos-$(Get-Date -Format yyyy-MM-dd)-importada.db" --duenio <su usuario o su número en eleventa>
   powershell -ExecutionPolicy Bypass -File "C:\Program Files\PapeleriaPos\Servicio\restaurar.ps1" -Carpeta C:\PapeleriaPos-paquete\base -Respaldo 1 -Si
   ```

   - Pide la clave y el PIN del dueño (4 cifras, dos veces). Trae el catálogo, las existencias, las ventas y los usuarios de
     eleventa: el dueño con todos los permisos y su PIN, los demás sin PIN.
   - Al final dice **el número del dueño en eleventa: anotarlo.** Una ruta que termina en `PDVDATA.FDB` no la acepta.
   - Si `restaurar.ps1` no dice «Se restauro el respaldo…», no se sigue
     ([qué significa cada código](pruebas-en-tienda.md#instalar-la-caja-2-para-el-piloto), paso 5).
   - Si salió bien: `Remove-Item -Recurse C:\PapeleriaPos-paquete\base`, y **se cierra esta ventana** (tiene la clave).
5. **El cajero:** en `C:\ProgramData\PapeleriaPos\servicio.json`, con el Bloc de notas como administrador, debajo del renglón de
   `"Caja"` se agrega `"Cajero": <el número del dueño>,`. Guardar y, en PowerShell como administrador,
   `Restart-Service PapeleriaPos`.
6. **La caja y los PIN:** abrir «Caja Papelería POS». Pide el usuario y el PIN del dueño y pregunta qué caja es esta PC:
   «Caja nueva», nombre `Caja 1`, «Dar de alta» (en esta PC es la caja principal: no pide letra, sus tickets salen sin letra,
   `000001`; 2026-11-22). Después, en Configuración › Usuarios y permisos, el dueño le pone su
   PIN a cada quien. La tienda sale con el nombre «Matriz»: así la deja la importación (pendiente de arreglar).

**Sale bien si,** en Edge:

- `http://localhost:5080/api/eleventa` dice `"bien": true` y en `origen` `127.0.0.1` con `…PDVDATA.FDB`;
- `http://localhost:5080/api/eleventa/envios` dice `"activo": false` y `"problema": null`;
- el precio y la existencia de cinco productos son los mismos en eleventa y en F3.

**Lo que se mira ese día, con eleventa vendiendo normal:**

- En el Administrador de tareas, la memoria y el procesador de `PapeleriaPos.Servicio`, de Firebird y de eleventa.
- Si eleventa se siente más lento cada 5 minutos, cuando el servicio lee.
- **Cerrar eleventa una vez con el servicio prendido:** qué hace su respaldo de salida, cuánto tarda y si al abrirlo otra vez
  `…/api/eleventa` sigue diciendo `"bien": true`. Se anota la hora y lo que diga.

**El celular,** si se va a probar: se da de alta desde la app (Ajustes › La conexión), con el celular en el Wi-Fi de la sucursal.

## 3. El ensayo contra la copia

Las ventas del ensayo son de prueba y van a `PDVDATA-ENSAYO.FDB`, nunca a la real. Se hace **con un solo usuario, el del dueño,**
y con eleventa abierto con su turno del día.

1. **El punto de partida, en papel:** en la caja, Configuración › Respaldos › «Respaldar ahora» y **el nombre exacto** del
   respaldo; en eleventa, **el folio del último ticket.**
2. **Si la copia es de otro día,** se hace otra como en el paso 2.3, con otro nombre, y se usa esa.
3. **Apuntar a la copia, en dos cambios** (`servicio.json`, como administrador, cada uno con su `Restart-Service PapeleriaPos`):
   1. Solo `Base`, con `…\\PDVDATA-ENSAYO.FDB`. `…/api/eleventa` dice en `origen` `…PDVDATA-ENSAYO.FDB` y `"bien": true`;
      `…/api/eleventa/envios`, `"activo": false`. Si `origen` dice otra cosa, no se sigue.
   2. `Escribir` en `true`. `…/api/eleventa/envios` dice `"activo": true` y en `destino` **`…PDVDATA-ENSAYO.FDB`** con el número
      de caja. **Si dice `PDVDATA.FDB`, `Escribir` otra vez en `false` y reiniciar antes de cualquier venta.**
   3. El número de caja: uno o dos minutos después (la primera lectura), `…/api/eleventa` no trae en `avisos` «Eleventa no
      tiene la caja…». Si lo trae, el número está mal: `Escribir` en `false`, se corrige `Caja` y se empieza este paso otra vez.
      (El aviso viene desde el PR #194; con una versión de antes, una caja que no existe solo se nota porque todo espera con
      «Abre la caja en eleventa».)
4. **Esperar el respaldo del día:** `Get-ChildItem C:\ProgramData\PapeleriaPos\respaldos-eleventa\*ENSAYO*` enseña un `.fbk` de
   hoy (un `.tmp` es que sigue).
5. **Qué probar,** cada cosa una vez:
   - una venta en efectivo: el ticket sale con el folio de eleventa, sin esperar;
   - una con un artículo común, una con mayoreo y una con descuento (Ctrl+D);
   - cancelar una venta y devolver parte de otra; una entrada (F7) y una salida (F8);
   - el lector, la impresora (dos tickets seguidos y una reimpresión) y el cajón. Con lector por puerto COM hay que cerrar
     eleventa para que la caja lo tenga, y al terminar abrirlo otra vez
     ([cómo](pruebas-en-tienda.md#el-lector-de-la-caja-2-por-puerto-com-piloto-alfa));
   - varios tickets de 1 a 10 artículos, como en un día normal, para los tiempos.

   No se puede probar aquí: el corte (eleventa usa la base real, no la copia) ni «eleventa que no contesta» (la regla de
   firewall de la otra guía no detiene una conexión dentro de la misma PC). Los dos se ensayaron en la PC de desarrollo.
6. **Compararlo,** en otra ventana, a la par del 5 (después de cada cosa se escribe qué fue y Enter; «fin» termina):

   ```
   cd C:\PapeleriaPos-paquete
   .\PapeleriaPos.Herramientas.exe observar-eleventa --servidor 127.0.0.1 --ruta "C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA-ENSAYO.FDB" --usuario SYSDBA
   ```

7. **Juntar los datos** ([cómo](pruebas-en-tienda.md#juntar-los-datos-del-piloto)).
8. **Regresar,** en este orden, cada cambio con su reinicio y su revisión:
   1. Cerrar la caja.
   2. `Activo` y `Escribir` en `false`, **sin tocar `Base`.** `…/api/eleventa/envios` dice `"activo": false`, `"destino": null` y
      `"problema": null`.
   3. Solo entonces, `Base` otra vez con `…\\PDVDATA.FDB`, con los dos todavía en `false`. `…/api/eleventa/envios` sigue en
      `"activo": false` y `"problema": null`.
   4. En la caja, Configuración › Respaldos › «Restaurar un respaldo»: **el que se anotó en el 1,** no el más reciente. Así el POS
      olvida las ventas del ensayo; si no, al ver la base real creería que eleventa se restauró y detendría el puente.
   5. `…/api/eleventa/envios` dice `"pendientes": 0`, `"porRevisar": 0` y `"enviadas": 0`. Si quedó un turno abierto del ensayo,
      se corta como dice el [7.4 de la otra guía](pruebas-en-tienda.md#el-ensayo-contra-una-copia).
   6. **Que a la base real no llegó nada:** en eleventa, después del folio anotado solo hay ventas de la tienda.
   7. `Remove-Item -Recurse -Force C:\ProgramData\PapeleriaPos\ensayo`. La copia `PDVDATA-ENSAYO.FDB` no se borra: está junto a
      la base real y ahí un error costaría la base.
   8. Para dejarlo leyendo hasta el paso 5: `Activo` en `true` (`Escribir` sigue en `false`) y reiniciar. `…/api/eleventa` dice
      en `origen` `…PDVDATA.FDB` y `"bien": true`. **No se vende en el POS** mientras `Escribir` esté apagado.

## 4. Antes de escribir en la real

Se decide con el dueño, con lo que salió del ensayo:

- lo de la copia cuadró con lo que deja eleventa (`observar-eleventa`);
- a la base real no llegó nada;
- se sabe qué hace el respaldo de salida de eleventa con el servicio prendido;
- el lector, la impresora y el cajón sirven en los dos programas.

## 5. La primera escritura en la base real

Con el dueño presente, en un rato tranquilo.

1. **Su respaldo:** uno de eleventa con su propia función.
2. **Eleventa abierto, con su turno del día y el mismo usuario que va a cobrar en el POS.** El POS no abre turnos en eleventa:
   sin uno, lo que se cobre espera.
3. **La caja del POS sin turno propio abierto:** entrar con el dueño; si no pide abrir turno, el corte del
   [paso 2 de la otra guía](pruebas-en-tienda.md#empezar-a-escribir-en-la-base-real), y «Volver» sin abrirlo.
4. **`Activo` y `Escribir` en `true`** y `Restart-Service PapeleriaPos`. `…/api/eleventa` dice en `origen` `…PDVDATA.FDB`. `…/api/eleventa/envios` dice `"activo": true`, en `destino`
   **`…PDVDATA.FDB`** con el número de esta caja, y `"problema": null`.
5. **Esperar el respaldo del día:** `eleventa-AAAA-MM-DD-PDVDATA.fbk` en `C:\ProgramData\PapeleriaPos\respaldos-eleventa`.
6. **Un cobro en el POS.** En eleventa se revisa: el folio, el cajero, la existencia y que la venta sumó a su turno.
7. **Una venta en eleventa y otra en el POS, alternadas, tres veces:** los folios van seguidos sin repetirse, y a los 5 minutos
   la existencia es la misma en los dos.
8. **El corte, en eleventa,** con `…/api/eleventa/envios` en `"pendientes": 0`. El POS lo toma solo y cierra su turno.
9. **Cerrar eleventa con el puente prendido** y abrirlo otra vez: `…/api/eleventa` sigue en `"bien": true` y el chip de la caja no
   dice «Detenido». Si lo dice, se anota la hora y lo que diga, y se vende en eleventa.

## 6. Cada día del piloto

1. Abrir eleventa y su turno, con el usuario de quien vende.
2. Abrir la caja del POS y vender ahí. Solo efectivo.
3. Si el chip de arriba dice algo del puente, tocarlo y leerlo.
4. Antes del corte: el chip sin pendientes. El corte, en eleventa.
5. En cada visita: [juntar los datos](pruebas-en-tienda.md#juntar-los-datos-del-piloto) y traer el .zip.

Lo que diga «por revisar» no pasa solo a eleventa: se anota a mano allá y se marca «Ya lo anoté en eleventa».

## 7. Regresar

- **En el momento:** se vende en eleventa, que está abierto al lado. No se cambia ninguna configuración. Lo pendiente del POS se
  queda en su bandeja y se manda cuando eleventa conteste.
- **Para apagar el puente:** `…/api/eleventa/envios` con `"pendientes": 0` y `"porRevisar": 0`; juntar los datos; `Activo` y
  `Escribir` en `false` y `Restart-Service PapeleriaPos`. Desde ahí no se vende en el POS
  ([el detalle](pruebas-en-tienda.md#regresar-o-terminar-el-piloto)).
- **Si eleventa se dañara:** [restaurar un respaldo de eleventa](pruebas-en-tienda.md#restaurar-un-respaldo-de-eleventa-fbk),
  con el dueño.
- **Quitar el POS:** Configuración › Aplicaciones › «Papelería POS» › Desinstalar. No toca nada de eleventa.
