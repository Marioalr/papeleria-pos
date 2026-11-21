# Corte más claro: «Salir» en la venta y «Venta de hoy» en Reportes

Propuesta de interfaz (2026-11-20), **aprobada por el dueño el 2026-11-20** (con el cajero cerrando su propio turno a
ciegas). Base: `origin/main` en 4c1f197 (después de #183). La maqueta está en `corte-y-salir-maqueta.html`, junto a este
archivo. Se hace en dos PR ([plan](#plan-de-implementación)): el PR 1, «Reportes › Hoy» con el corte del día y el
historial (hecho); el PR 2, «Salir» (hecho).

**PIN para cerrar el turno: decidido por el dueño el 2026-11-20, provisional. Se evaluarán los resultados durante el piloto para
decidir si se queda.** Quien cierra el turno escribe lo contado y lo confirma con su PIN: el de quien entró (en el turno de otro
cajero, el de quien tiene «Hacer el corte del turno de otro»). El servicio lo revisa, con los mismos intentos y esperas que la
entrada: un PIN equivocado no cierra el turno, ni deja ver lo esperado. En el piloto y sin conexión funciona como la entrada (sin
conexión, contra la copia de la caja).

Lo que pidió el dueño:

1. Un botón en la ventana principal para cerrar el turno, con un nombre genérico como «Salir». Al oprimirlo, el cajero
   escribe lo que contó y, si tiene el permiso, también ve las ventas del día.
2. Quitar la pestaña «Corte».
3. Que Reportes abra en lo que se ha vendido hoy: quién vendió, los márgenes, etc.

En resumen:

- **«Salir» (Ctrl+S) sustituye a la pestaña «Corte» y al menú del usuario.** Abre una sola pantalla con lo que se hace casi
  siempre al irse, con el cursor en **«Efectivo contado»**: Enter cierra el turno. En la misma pantalla, como segunda opción,
  **«Cambiar de usuario»** (Ctrl+U, el turno sigue abierto) y, en la caja de Windows, «Minimizar la caja» y «Cerrar la caja».
- **El cajero cierra su propio turno**, a ciegas, sin pedirle a otro que entre. «Hacer el corte de un turno» queda para el
  turno de otro cajero.
- **Quien ve el corte del día** ve además, en «Salir», cuánto debería haber, la diferencia mientras escribe y un resumen
  de la venta de hoy. Al cerrar el último turno, «Cerrar el día» aparece ahí mismo.
- **Reportes siempre abre en «Hoy» (la venta de hoy).** Ahí queda todo lo que hoy está en «Corte › Del día»: los turnos y
  sus diferencias, el efectivo, los movimientos, imprimir y cerrar el día. También se pasa ahí el **historial de cortes**.
  Del corte no se pierde nada (la [tabla](#qué-pasa-con-cada-cosa-de-la-pantalla-corte)).

---

## Lo que hay hoy y por qué confunde

| # | Qué pasa | Dónde |
|---|---|---|
| 1 | «Corte» es una pestaña sin tecla, entre Clientes y Reportes, y no se comporta como las demás: abre una ventana encima en lugar de una página | `PantallaVenta.razor:38`, `:5863` |
| 2 | Una sola ventana mezcla tres cosas que no tienen que ver, y su título cambia según el segmento: cerrar **mi** turno, el corte **del día** de todas las cajas y el **historial** | `CorteCaja.razor:16-46` |
| 3 | El cajero no puede cerrar su turno: ve un candado y «Que entre a esta caja» y tiene que llamar al encargado o al dueño para que cuente por él. Es lo contrario de lo que pide el dueño | `CorteCaja.razor:156-170`; regla de inicio `PermisosDeRoles.cs:35` (cajero «No»); decidido «Solo con el permiso, por revisar» el 2026-10-17 (`propuesta-interfaz.md:865`) |
| 4 | Irse tiene tres caminos con nombres que se cruzan: «Corte» (cerrar el turno), el menú del nombre con «Cambiar de usuario» y «Salir», que hacen **lo mismo** en el navegador, y «Cerrar la caja», que solo está dentro del corte | `PantallaVenta.razor:57-77` (`:67` y `:75` llaman a `SalirDeLaPaginaAsync`), `:5887-5894` |
| 5 | Al cerrar el turno, el botón principal es «Listo: abrir el siguiente turno», que lleva a quien se va a abrir **otro turno suyo**. Cerrar con Esc o la × hace lo mismo | `CorteCaja.razor:85`, `PantallaVenta.razor:6533-6541`, `PantallaCaja.razor:316` |
| 6 | Lo de hoy está dos veces y con distinto detalle: «Reportes › Hoy» (venta, gráfica, departamentos con margen, cajeros, formas de pago) y «Corte › Del día» (lo mismo más los turnos, el efectivo, los movimientos, la utilidad por departamento y «Cerrar el día»). Las ventas por departamento salen en las dos tablas | `Reportes.razor:135-230`, `CorteDelDia.razor:69-370` |
| 7 | Reportes recuerda el último periodo (si antes se dejó en «Mes», al volver abre en «Mes»), así que no siempre abre en lo de hoy | `Paginas.cs:139-142` (`ReportesVistos.Periodo`) |
| 8 | El historial de cortes es una consulta, pero para verlo hay que tener un turno abierto y abrir el corte encima de la venta | `PantallaVenta.razor:1541`, `:6585-6599` |
| 9 | Sin un turno abierto no se puede ver ni cerrar el día: abrir turno y «Productos e inventario, sin abrir turno» no tienen Reportes ni el corte del día. Con el cajero cerrando su propio turno pasaría más seguido (el dueño llega y ya no hay turno) | `PantallaCaja.razor:49-58`, `Administracion.razor:21-33` |
| 10 | El recordatorio dice «ciérralo en «Corte › Del día»», y la guía de la prueba repite «abre «Corte» (arriba)» | `RecordatorioCierreDelDia.razor:15`, `GuiaPrueba.cs:305-365` |
| 11 | En «/» salen «Corte» e «Historial de cortes», con nombres que no dicen qué hacen | `PantallaVenta.razor:1540-1542` |

Lo que sí funciona y se queda tal cual: el conteo a ciegas con solo el total, el lector protegido en «Efectivo contado»
(`corte.js`), Enter para cerrar (F12 no), el corte impreso por la cola, la diferencia a nombre del cajero, el aviso del
umbral, el corte sin conexión, lo esperado para quien ve el corte del día (2026-11-16), los tickets sin cobrar que pasan a
espera y el piloto con eleventa.

## Cómo lo hace eleventa (para llegar primero a lo mismo)

Según `docs/paridad-eleventa.md` (filas 65-67, :235) y las páginas de eleventa que cita (:298-304):

- **«Corte» es un botón de la barra de arriba** que abre el corte de caja: el dinero en caja (fondo, ventas en efectivo,
  abonos, entradas y salidas), las ventas por forma de pago, las ventas y la ganancia por departamento, las entradas y salidas.
  Desde ahí se hace el **corte del cajero** (cierra su turno) y se ve el **corte del día** de todas las cajas. Se imprime y
  hay un historial de cortes.
- **Cada cajero hace el corte de su turno.** Eleventa **le enseña lo esperado** o solo le pide confirmar. Aquí se cuenta a
  ciegas, y eso ya es mejor que eleventa (está en «Lo que el POS ya hace y eleventa no»).
- **La caja se cierra desde el corte** (así se decidió el 2026-11-08, `plan.md:3013`).

Comparación:

| | eleventa | Hoy en el POS | Propuesta |
|---|---|---|---|
| Dónde se cierra el turno | Botón «Corte» | Pestaña «Corte» | Botón **«Salir»** (Ctrl+S) |
| Quién cierra su turno | El cajero | Solo quien tiene el permiso | **El cajero, el suyo**; el de otro, con el permiso |
| Lo esperado | Lo ve | A ciegas (salvo quien ve el corte del día) | Igual que hoy |
| Corte del día | En «Corte» | En «Corte › Del día» | En **Reportes › Hoy** |
| Ganancia y departamentos | En el corte | En el corte del día y en Reportes | En **Reportes › Hoy**, una sola tabla |
| Historial de cortes | En el corte | En «Corte › Historial» | En **Reportes › Historial de cortes** |
| Cerrar el programa | Desde el corte | «Cerrar la caja» en el corte | En **«Salir»** |

Lo que cambia frente a eleventa es el nombre del botón («Salir», como pidió el dueño) y que el corte del día se pasa a
Reportes, que es donde el dueño ya ve las ventas. Lo que hace eleventa sigue todo ahí.

---

## El flujo propuesto

```
Venta ──[Salir · Ctrl+S]──► «Salir» (encima de la venta, ocupa la pantalla como hoy el corte)
                              │
                              ├─ Enter con lo contado ──► Turno cerrado (contado, esperado, diferencia; se imprime)
                              │                             ├─ [Listo · Enter / Esc] ──► ¿Quién atiende?
                              │                             ├─ [Abrir otro turno] ──► Abrir turno (el mismo usuario)
                              │                             ├─ [Cerrar el día] (corte del día y último turno)
                              │                             └─ [Cerrar la caja] (Windows)
                              ├─ [Cambiar de usuario · Ctrl+U] ──► ¿Quién atiende? (el turno sigue abierto)
                              ├─ [Minimizar la caja] (Windows)
                              ├─ [Cerrar la caja] (Windows; avisa que el turno sigue abierto)
                              └─ [Volver a la venta · Esc]
```

### 1. La barra de arriba

- **Se quita la pestaña «Corte».** Quedan Venta F1 · Productos F3 · Inventario F4 · Clientes · Reportes · Configuración.
- **A la derecha, después del nombre, un botón «Salir» con su tecla `Ctrl+S`**, siempre visible. Con la pestaña que se quita
  cabe sin encoger los chips a 1366 px.
- **El nombre deja de ser un menú.** Sigue diciendo quién atiende y «turno desde 09:02»; un clic en él abre «Salir», igual
  que el botón. El menú tenía «Cambiar de usuario», «Salir» (lo mismo, en el navegador) y «Minimizar la caja» (Windows):
  las tres pasan a «Salir».
- **Ctrl+U sigue siendo «Cambiar de usuario» directo,** sin pasar por «Salir»: la costumbre no cambia.

**Por qué Ctrl+S (por revisar):** las doce teclas F ya están ocupadas (F1 venta, F2 impresiones, F3 productos,
F4 inventario, F5 retomar, F6 espera, F7 entrada, F8 salida, F9 verificar, F10 buscar, F11 mayoreo, F12 cobrar), y Esc
ya limpia y cierra. Ctrl+S es la «S» de «Salir», como Ctrl+U es la de «usUario», y en la caja no se usa (`caja.js` solo
toma B, D, E, I, L, M, P, Q, U y V). En WebView2, Ctrl+S abre «Guardar como»: `caja.js` debe cancelarlo, como ya hace
con Ctrl+U («ver el código de la página», `caja.js:539`).

### 2. «Salir», para quien atiende (sin el corte del día)

Es lo que hoy se ve con el permiso del corte, sin los segmentos de arriba:

- **Título «Salir»** y debajo «Turno de Ana · Caja 2 · desde las 09:02» (con la fecha si el turno viene de otro día, como
  ya hace `CorteCaja.Desde`).
- **Si hay tickets sin cobrar,** el aviso de hoy, arriba: «Hay 2 tickets sin cobrar: se quedan en espera en esta caja para
  el siguiente turno».
- **A la izquierda, «Cerrar tu turno»:** «Cuenta el efectivo de la caja y escribe el total. No se muestra cuánto debería
  haber hasta cerrar». El campo grande **«Efectivo contado»** con el cursor, protegido del lector, y **«Cerrar el turno ·
  Enter»**.
- **A la derecha, «Tu turno» (el corte X de hoy):** las ventas (y las canceladas), la tarjeta, las transferencias (y las
  que faltan por verificar), los abonos y lo fiado. Del efectivo no dice nada.
- **Abajo, una fila con lo demás:** «Cambiar de usuario `Ctrl+U`» con la nota «el turno sigue abierto»; en Windows,
  «Minimizar la caja» y «Cerrar la caja», y «Volver a la venta `Esc`».

**Ya cerrado** (lo de hoy, con otros botones): «Turno de Ana cerrado», con Contado, Esperado y Faltante, Sobrante o
Cuadra, «a nombre de Ana»; el detalle del corte; si salió impreso o «Reintentar»; el aviso del umbral y el de sin
conexión. Botones:

- **«Listo» (Enter, y también Esc o la ×):** a «¿Quién atiende?». Quien oprimió «Salir» se va: ya no lo manda a abrir
  otro turno suyo.
- **«Abrir otro turno»:** para quien sigue vendiendo (el dueño que hace un corte a media tarde).
- **«Cerrar la caja»** (Windows).

### 3. «Salir», para quien ve el corte del día (el dueño)

Lo mismo, más:

- **Debajo del campo**, lo de hoy (#176): «Debería haber $1,767.00 (fondo + efectivo − salidas)» y, al escribir,
  «Cuadra», «Faltan $20.00» o «Sobran …».
- **A la derecha, en lugar de «Tu turno», «Venta de hoy · todas las cajas»:** lo vendido y los tickets; efectivo, tarjeta y
  transferencia (con las que faltan por verificar); con «Ver costos, utilidad y margen», la utilidad y el margen (con el
  borde punteado de lo que depende de un permiso); los turnos del día, cada uno con su estado (abierto o su diferencia);
  y el enlace **«Todo en Reportes ›»**.
- **Ya cerrado, si era el último turno abierto y no falta nada por subir,** aparece «Cerrar el día» ahí mismo (el mismo
  componente de hoy, con sus razones si no se puede), y después «Cerrar la caja». Así queda en una sola pantalla lo que
  hace el dueño al terminar el día: contar, cerrar su turno, cerrar el día y cerrar la caja.

### 4. Reportes abre en «Hoy»: la venta de hoy

- **Cada vez que se entra a Reportes** (la pestaña, «/» o el recordatorio), abre en **Hoy**, en el día de hoy. Dentro, el
  periodo que se elige se queda mientras se esté ahí; al salir y volver, otra vez Hoy. «Ver como tabla» se sigue recordando.
- **El encabezado dice «Venta de hoy · viernes 20 de noviembre»**; los segmentos siguen siendo Hoy · Semana · Mes · Año a
  la fecha. ← → en Hoy llevan al día anterior o al siguiente («Venta del jueves 19»), y lo de ese día solo se ve, como
  hoy en el corte del día.
- **Arriba a la derecha:** «Imprimir el corte del día», «Historial de cortes», «Ventas por período», «Impresiones por
  período» y «Exportar a Excel».
- **El cuerpo de Hoy, de arriba abajo:**
  1. «Día cerrado a las 21:05 por Luis», si ya se cerró (aviso de hoy).
  2. **Indicadores:** Venta neta (contra el año pasado), Tickets, Ticket promedio, Utilidad bruta con su margen (con el
     permiso) y, con el corte del día, **Efectivo** («debe haber en las cajas …») y **Diferencias de corte** («1 de 2
     turnos cerrados»).
  3. La gráfica por hora (la de hoy).
  4. **Quién vendió:** cajero, tickets, venta, ticket promedio y diferencias de corte.
  5. **Por departamento:** venta, parte, contra el año pasado y, con el permiso, utilidad y margen (una sola tabla con las
     columnas de las dos de hoy; impresiones y artículo común en su renglón).
  6. **Formas de pago,** con las transferencias por verificar y las rechazadas.
  7. **Turnos y cortes** (la tabla del corte del día): caja, cajero, horario, tickets, vendido, esperado, contado y
     diferencia; «Turno abierto», «En eleventa» y «Corte el 19/11», con sus notas.
  8. **Movimientos de efectivo:** salidas por motivo, entradas, devoluciones, cancelaciones, fiado y abonos, impresiones
     cobradas.
  9. Lo que más se vende.
  10. **«Cerrar el día»** (la tarjeta de hoy, con sus razones, «Hacer ese corte», que ahora abre «Salir», y la vista de
      cómo sale impreso).
- Semana, Mes y Año, «Ventas por período» e «Impresiones por período» quedan como están.
- **«Historial de cortes»** se abre dentro de Reportes, como «Ventas por período» (← Reportes o Esc regresa), con sus
  filtros, ← → y «Reimprimir el corte».

### 5. Sin turno abierto

Con «Ver reportes de ventas» o «Ver y cerrar el corte del día», **«Abrir turno» y «El turno de otro cajero» ofrecen
«Reportes, sin abrir turno»**: entra a la misma pantalla de «Productos e inventario, sin abrir turno» (`Administracion`),
que gana la pestaña Reportes. Así el dueño ve y cierra el día aunque el último cajero ya haya cerrado su turno, sin abrir
uno nuevo (que además le impediría cerrar el día).

---

## Qué pasa con cada cosa de la pantalla «Corte»

| Hoy (dónde) | Propuesta |
|---|---|
| Pestaña «Corte» (`PantallaVenta.razor:38`) | Se quita. Botón **«Salir»** `Ctrl+S` a la derecha de la barra |
| «Corte» en «/» (`:1540`) | «Salir: cerrar el turno o cambiar de usuario» (Ctrl+S) |
| «Historial de cortes» en «/» (`:1541`) | Igual, pero abre **Reportes › Historial de cortes** |
| Menú del nombre: «Cambiar de usuario Ctrl+U» (`:67`) | En «Salir» (y Ctrl+U directo, como hoy) |
| Menú del nombre: «Salir» del navegador (`:75`) | Es «Cambiar de usuario»: un solo nombre |
| Menú del nombre: «Minimizar la caja» (Windows, `:71`) | En «Salir», en la fila de abajo |
| «Cerrar la caja» y su nota (`:5887-5894`) | En «Salir», en la fila de abajo y después de cerrar el turno, con la misma nota |
| Aviso de los tickets sin cobrar (en espera, o se pierden sin base local) (`:5868-5882`) | En «Salir», arriba, igual |
| Título «Corte del turno / del día / Historial» y segmento Turno · Del día · Historial (`CorteCaja.razor:16-31`) | Se quitan: «Salir» es solo el turno; el día y el historial van a Reportes |
| «Listo: abrir el siguiente turno» en el encabezado (`:35`) | Ya no hace falta: el día ya no está dentro del corte |
| Aviso del piloto «el corte de esta caja se hace en eleventa» (`:47-50`) | En «Salir», en lugar del campo (ver [casos](#casos-que-hay-que-cuidar)) |
| Turno cerrado: Contado, Esperado, Diferencia a nombre de (`:55-66`) | En «Salir», igual |
| Aviso del umbral por Telegram (`:67-70`) | Igual |
| Aviso sin conexión (contingencia) (`:71-74`) | Igual |
| Error al cerrar (`:75-78`) | Igual |
| Aviso de impresión del corte (Reintentar / No imprimirlo) (`:79-83`) | Igual |
| «Listo: abrir el siguiente turno» (`:85`) | **«Listo»** → ¿Quién atiende? y **«Abrir otro turno»** aparte |
| «Ver el corte del día» (`:86-89`) | «Cerrar el día» ahí mismo si toca; «Todo en Reportes ›» |
| Detalle del corte: ventas, canceladas, fondo, efectivo de ventas, abonos en efectivo, entradas y salidas (con las canceladas), devoluciones, saldo devuelto, esperado, redondeo, tarjeta, transferencias, abonos con tarjeta y por transferencia, fiado, quién y a qué hora, vista previa (`:92-137`) | En «Salir», ya cerrado, igual |
| «No hay turno abierto» + «Abrir turno»; error + «Volver a intentar»; «Cargando…» (`:140-155`) | Igual, en «Salir» |
| Sin el permiso: candado, quién corta, «Que entre a esta caja» (`:156-170`) | Del turno propio ya no sale. Queda para el turno de otro cajero y si el servicio dice que no (sin conexión con una copia de permisos vieja) |
| Turno de X desde…, nota a ciegas, «Entraste como … no vuelve a pedir tu PIN» (`:175-179`) | Igual en «Salir» (la del PIN solo en el turno de otro) |
| «Debería haber …» y Sobran/Faltan (`#corte-esperado`, `:181-184`) | Igual, con el corte del día |
| «Efectivo contado» (`#corte-contado`), su error, «Cerrar el turno · Enter» (`:186-196`) | Igual, mismos id |
| Corte X informativo (`:411-431`) | «Tu turno», a la derecha; para el dueño lo sustituye «Venta de hoy» (tarjeta y transferencias de su turno siguen en el detalle al cerrar) |
| **Del día:** ← → , la fecha, «Hoy», «Imprimir» y su aviso (`CorteDelDia.razor:17-41`) | Reportes › Hoy: ← → y «Al de hoy» (los de Reportes), «Imprimir el corte del día» |
| Día cerrado a las … por … y el resumen (`:61-67`) | Reportes › Hoy, arriba |
| KPI vendido, efectivo / debe haber, tarjeta y transferencia (por verificar, rechazadas), diferencias, utilidad (`:69-97`) | Indicadores de Reportes › Hoy (venta neta, efectivo, diferencias, utilidad) y Formas de pago (por verificar y rechazadas) |
| Tabla de turnos (caja, cajero, horario, tickets, vendido, esperado, contado, diferencia, «Turno abierto», «En eleventa», «Corte el …») y sus notas (`:99-176`) | Reportes › Hoy › «Turnos y cortes», igual |
| Formas de pago y movimientos: salidas por motivo, entradas, devoluciones, cancelaciones, fiado y abonos, impresiones cobradas (`:180-205`) | Formas de pago y «Movimientos de efectivo» en Reportes › Hoy |
| Cerrar el día: piloto, ya cerrado, razones, «Hacer ese corte», avisos, error, botón; de otro día, «Ver el de hoy» (`:207-262`) | Reportes › Hoy, al final, igual; «Hacer ese corte» abre «Salir». Y en «Salir» al cerrar el último turno |
| Así sale impreso (`:263-268`) | Igual |
| Ventas por departamento con utilidad y margen, impresiones, artículo común (`:271-349`) | Una sola tabla en Reportes › Hoy (con la de Reportes) |
| Tarjeta «Utilidad del día» con sus notas (`:351-368`) | Reportes › Hoy, junto a los departamentos (con el permiso) |
| **Historial** completo (`HistorialCortes.razor`) | Reportes › «Historial de cortes», igual |
| Recordatorio «Falta cerrar el día» → abría el corte (`PantallaVenta.razor:769`) | Abre Reportes › Hoy en «Cerrar el día»; el texto dice «ciérralo en Reportes › Hoy» |
| Esc o la × con el turno ya cerrado → siguiente turno (`:6533-6541`) | Esc o la × = «Listo» (¿Quién atiende?) |
| El turno de otro cajero (`TurnoDeOtro.razor`) con el corte | Igual, con el corte sin segmentos |

## Permisos

Sin permisos nuevos. Cambia una regla y qué abre cada una:

| Qué | Quién | Hoy |
|---|---|---|
| Abrir «Salir», cambiar de usuario, minimizar o cerrar la caja | Todos | Todos (el menú) |
| **Cerrar su propio turno** contando a ciegas | **Todos** (quien atiende ese turno) | Solo «Hacer el corte de un turno» |
| Cerrar el turno de otro cajero (en «El turno de otro cajero») | «Hacer el corte de un turno», en «Puede» (se le pondría el nombre «Hacer el corte del turno de otro») | Igual |
| Ver lo esperado y la diferencia al contar; «Venta de hoy» en «Salir»; «Cerrar el día» | «Ver y cerrar el corte del día» | Igual (en el corte) |
| La utilidad y el margen, donde salgan | «Ver costos, utilidad y margen» | Igual |
| La pestaña Reportes | «Ver reportes de ventas» **o** «Ver y cerrar el corte del día» **o** «Hacer el corte de un turno» (ve lo que le toque) | Solo «Ver reportes de ventas» |
| Reportes › Hoy: venta, gráfica, quién vendió, departamentos, formas de pago, más vendido; Semana, Mes, Año, Excel, Ventas e Impresiones por período | «Ver reportes de ventas» | Igual |
| Reportes › Hoy: efectivo, turnos y cortes, movimientos, imprimir y cerrar el día | «Ver y cerrar el corte del día» | En «Corte › Del día» |
| Reportes › Historial de cortes | «Hacer el corte de un turno» o «Ver y cerrar el corte del día» | En «Corte › Historial» |
| «Ventas del día» en «/» (las de esta caja) | Todos | Igual |

Quién ve qué con los permisos de inicio: el **cajero** (Ana) cierra su turno a ciegas, sin Reportes. El **encargado**
(Carmen: corte de turno, sin reportes) cierra su turno y el de otros, y en Reportes solo ve el historial de cortes. El
**dueño** (Luis) lo ve todo.

El servicio sigue decidiendo: `RegistroCaja.CorteZAsync` y `CorteInformativoAsync` aceptan al cajero del turno
(`usuarioId == turno.CajeroId`) aunque no tenga la regla, y lo mismo `Contingencia` sin conexión. En la bitácora queda
igual, a nombre de quien lo hizo.

## Teclado

| Dónde | Tecla | Qué hace |
|---|---|---|
| Venta y páginas | **Ctrl+S** | Abre «Salir» (el botón dice la tecla) |
| Venta y páginas | Ctrl+U | Cambiar de usuario directo, como hoy |
| «Salir» | (al abrir) | Cursor en «Efectivo contado»; el lector no escribe ahí |
| «Salir» | Enter | Cerrar el turno con lo contado (F12 no cuenta, como hoy) |
| «Salir» | Ctrl+U | Cambiar de usuario sin cerrar el turno |
| «Salir» | Esc | Volver a la venta, sin cerrar nada |
| «Salir» | Tab | Recorre «Cambiar de usuario», «Minimizar la caja», «Cerrar la caja», «Volver» |
| Turno cerrado | Enter, Esc | «Listo»: a ¿Quién atiende? |
| Reportes | ← → | Día (en Hoy) o periodo anterior y siguiente, como hoy |
| Reportes | Esc, F1 | Cierra lo abierto (historial, ventas por período) o regresa a la venta |

## Casos que hay que cuidar

- **El piloto con eleventa:** en «Salir» no hay campo. Va el aviso de hoy, `PilotoConEleventa.CorteAlCerrar` («el corte de
  esta caja se hace en eleventa», más las operaciones que faltan por llegar), y los botones «Cambiar de usuario»,
  «Minimizar la caja» y «Cerrar la caja». Con el corte del día, el resumen de hoy dice «solo lo de la caja 2». En Reportes
  sale el aviso de la caja 2 y «Cerrar el día» dice que se cierra con el corte de eleventa (como hoy). La caja sigue
  pasando sola al siguiente turno cuando eleventa corta (`RevisarElTurnoAsync`).
- **Otra caja con su turno abierto:** «Salir» solo cierra el de esta caja. En «Venta de hoy» y en Reportes, ese turno dice
  «Turno abierto», y «Cerrar el día» no se habilita: «Falta el corte de Caja 1 (Luis)». «Hacer ese corte» solo sale para el
  de esta caja (como hoy).
- **El turno de otro cajero en esta caja:** no cambia (`TurnoDeOtro`): quien tiene «Hacer el corte de un turno» lo cierra
  ahí, sin volver a pedir PIN. Quien no, ve quién lo hace y «Volver». Con el cajero cerrando el suyo al irse, debería
  pasar menos.
- **Un turno que se quedó abierto** (de ayer o de antes): «Salir» dice «desde el 19/11 a las 09:00», y el corte es de todo
  el turno, como hoy. En Reportes › Hoy el turno sale con la nota de «viene de otro día». Un aviso en la venta va a
  Recomendaciones.
- **Sin permiso de nada más:** «Salir» solo pide lo contado y enseña «Tu turno». No hay pestaña Reportes.
- **Sin conexión con la caja principal:** el corte del turno funciona (contingencia, como hoy, con su aviso). «Venta de
  hoy» en «Salir» dice «Se ve al volver la conexión», y Reportes pide la caja principal, como hoy.
- **Un trámite que se imprime o se entregó sin cobrar:** «Salir» y Ctrl+S no lo terminan y dicen por qué, como hoy la
  pestaña «Corte» (`TramiteEnCurso`, `PantallaVenta.razor:1062`).
- **Una ficha con cambios en Productos:** primero su aviso (`FichaDetiene`), como hoy con «Cambiar de usuario».
- **Tickets sin cobrar:** pasan a espera para el siguiente turno; en el navegador, sin base local, el aviso dice que se
  pierden.
- **La respuesta no llega al cerrar:** reintenta con el mismo Id (no cierra dos veces), como hoy.
- **El navegador:** no hay «Minimizar» ni «Cerrar la caja». Lo demás es igual.
- **Un dueño que no abre turno:** «Reportes, sin abrir turno» (sección 5).

## Decidido sin el dueño (por revisar)

| Tema | Decisión | Por qué |
|---|---|---|
| El PIN para cerrar el turno | **Decidido por el dueño el 2026-11-20, provisional.** Lo contado y luego el PIN de quien cierra; el servicio lo revisa como la entrada | Se evalúa en el piloto (ver Recomendaciones) |
| Quién cierra su propio turno | **Aprobado por el dueño el 2026-11-20.** **Todos**, a ciegas; «Hacer el corte de un turno» queda para el de otro | El dueño pidió que el cajero escriba lo que contó. Contradice «Solo con el permiso» (2026-10-17), que era por revisar. La otra forma es darle «Puede» al cajero en «Usuarios y permisos», pero entonces también podría cortar el turno de otros |
| El nombre del botón | «Salir» | El nombre que propuso el dueño. Dice lo que hace quien lo oprime (irse), aunque lo que pasa sea cerrar el turno |
| Qué abre «Salir» | Directo lo contado, con «Cambiar de usuario» como segunda opción en la misma pantalla | Es lo que pidió el dueño y lo que pasa casi siempre. Preguntar primero entre «Cambiar de usuario» y «Cerrar turno» agrega un paso y una decisión. No se cierra el turno por error: hay que escribir una cantidad y oprimir Enter, y el botón dice «Cerrar el turno» |
| La tecla | Ctrl+S | No queda ninguna F libre; S de «Salir» |
| El menú del nombre | Se quita; el nombre abre «Salir» | «Cambiar de usuario» y «Salir» hacían lo mismo en el navegador |
| Al terminar el corte | «Listo» lleva a ¿Quién atiende?; «Abrir otro turno» aparte | Quien oprimió «Salir» se va |
| Reportes al entrar | Siempre en Hoy | Lo pidió el dueño; antes recordaba el periodo |
| El corte del día | Se pasa a Reportes › Hoy, en la misma página que la venta de hoy | El dueño ya ve las ventas ahí, y se juntan las dos tablas de departamentos |
| El historial de cortes | En Reportes, como «Ventas por período» | Es una consulta; ya no hace falta un turno abierto |
| Pestaña Reportes | Con cualquiera de los tres permisos, y cada quien ve su parte | Para que nadie pierda lo que hoy ve en el corte (el encargado, el historial) |
| Sin turno | «Reportes, sin abrir turno» en Abrir turno y en el turno de otro | Si el cajero cierra el último turno, el dueño cierra el día sin abrir otro |
| El nombre del segmento | Se queda «Hoy»; el título dice «Venta de hoy» | Los cuatro periodos siguen leyéndose igual |

## Recomendaciones (para después)

- **Revisar después del piloto si se queda el PIN para cerrar el turno** (decidido por el dueño el 2026-11-20, provisional): con lo
  que pase en el piloto (si estorba al irse, si alguien lo olvida o se queda esperando por los intentos).
- **Un aviso en la venta cuando el turno viene de otro día** («Este turno es de ayer: ciérralo con Salir antes de
  vender»). Hoy no hay nada que lo diga, y el corte junta los dos días.
- **La utilidad por cajero** en «Quién vendió» (con el permiso). Eleventa no la tiene. Necesita sumar el costo de cada
  venta por cajero en el servicio (`ReportesDeVentas`).
- **«Productos e inventario, sin abrir turno»** (`Administracion.razor`) con el mismo botón «Salir» en lugar de su menú,
  para que sea igual en las dos pantallas.
- **«Cerrar la caja» con una tecla** (por ejemplo Ctrl+Q en «Salir», donde Cotizar no aplica), si se ve que lo usan mucho.
- **El corte por correo** (eleventa lo tiene, la tienda no lo usa): sigue sin hacerse.

## Plan de implementación

Dos PR, en este orden, para que en ningún momento falte algo:

**PR 1, «Reportes › Hoy» con el corte del día y el historial** (la pestaña «Corte» se queda mientras):

- `Componentes/CorteDelDia.razor`: separar en partes que se puedan poner en Reportes (indicadores del corte, «Turnos y
  cortes», «Movimientos de efectivo», «Cerrar el día» con la vista de lo impreso), sin cambiar lo que pide al servicio.
- `Componentes/Reportes.razor`: en Hoy, el título «Venta de hoy · …», los indicadores del corte, las partes de arriba,
  «Imprimir el corte del día», «Historial de cortes», y una sola tabla de departamentos con utilidad y margen. ← → en Hoy
  cambian el día de las dos consultas.
- `Paginas.cs`: `ReportesVistos` con `Historial` (abierto) y `Periodo` que regresa a Hoy al entrar; `PermisosDePagina`
  con `VeReportes` (los tres permisos).
- `Componentes/HistorialCortes.razor`: con «← Reportes», como `VentasPorPeriodo`.
- `PantallaVenta.razor`: la pestaña Reportes con `VeReportes` nuevo; «Historial de cortes» en «/» y el recordatorio
  abren Reportes.
- `RecordatorioCierreDelDia.razor:15`: «ciérralo en Reportes › Hoy».
- `caja.css`: las partes del corte dentro de `.vn-reportes`.
- Pruebas: `ReportesTests`, `CorteDelDiaTests` (de `CorteCaja` a `Reportes`: `corte-dia`, `corte-dia-impreso`,
  `vn-corte-dia`, el recordatorio `:583`), `OtrosDiasTests` (historial `:451`, `:596`), `ExcelYVentasPorPeriodoTests`,
  `ImpresionesPorPeriodoEnLaCajaTests`, `PaginasTests` (Reportes con el corte del día o el de turno). Nuevas: abre en Hoy
  aunque antes se dejó en Mes; quien solo tiene el corte del día ve los turnos sin la comparación; el encargado solo ve el
  historial; la utilidad solo con su permiso.

**PR 2, «Salir»** (y se quita la pestaña):

- Servicio: `PapeleriaPos.Datos/RegistroCaja.cs:441-447` y `:475-483` (el cajero del turno puede);
  `PapeleriaPos.Interfaz/Contingencia.cs:1892-1915` (lo mismo sin conexión); `Nucleo/Control/Permisos.cs:348` (el nombre
  «Hacer el corte del turno de otro»).
- `Componentes/CorteCaja.razor`: sin segmentos ni `CorteDelDia`/`HistorialCortes`; «Listo» y «Abrir otro turno»; «Venta
  de hoy» (resumen del `CorteDelDiaDto` de hoy) en lugar del corte X para quien ve el día; «Cerrar el día» al cerrar el
  último turno. Se puede renombrar a `SalirDeLaCaja.razor` y dejar `CorteCaja` para `TurnoDeOtro`.
- `PantallaVenta.razor`: quitar la pestaña (`:38`) y el menú del nombre (`:57-77`); el botón «Salir» y Ctrl+S en
  `TeclaAsync` y `TeclaEnPaginaAsync`; `Ventanas.Corte` → `Ventanas.Salir` (con «Cambiar de usuario», «Minimizar la caja»,
  «Cerrar la caja»); «/» (`:1540`); `Cerrar` (`:6533`) con «Listo» a la entrada; el aviso del trámite con «Salir».
- `PantallaCaja.razor:316`: `CorteTerminado` con «Listo» (a la entrada) o «Abrir otro turno».
- `AbrirTurno.razor`, `TurnoDeOtro.razor`, `Administracion.razor`, `PantallaCaja.razor`: «Reportes, sin abrir turno».
- `wwwroot/caja.js`: Ctrl+S (y cancelar «Guardar como»); `caja.css`: `.vn-salir`, quitar `.vn-menu-usuario`.
- `GuiaPrueba.cs:305-365`: las tareas con «Corte» pasan a «Salir» y a Reportes.
- Docs: `propuesta-interfaz.md` (Corte, el dibujo de la barra `:89`, la pregunta `:865`), `plan.md` (lo decidido y
  Recomendaciones), `paridad-eleventa.md` (filas 65-67), `pruebas-en-tienda.md`.
- Pruebas que hay que cambiar: `PaginasTests:517` (las pestañas sin «Corte»), `ReportesTests:168` (Reportes después de
  Clientes), `RevisionServicios4Tests:57` («Salir» no termina el trámite), `AuditoriaEnLaVentaTests:761`,
  `VentanaComoEleventaTests:37-58` («Minimizar la caja» y «Cerrar la caja» en «Salir»), `PantallaVentaTests:185-307`
  (`Boton("Corte")` → «Salir»; el cajero ya no ve «Que entre a esta caja» en su turno), `ComaEnElDineroTests:170-186`
  (mismo `corte-contado`), `CorteApiTests:132-208` y `UsuariosApiTests:204` (`PuedeCortar` del cajero en su turno),
  `PermisosSinConexionTests`, `CorteEnElPilotoTests`, `CorteZYCancelacionTests`, `ParidadPermisosTests`. Nuevas: el cajero
  cierra el suyo y no el de otro (servicio y sin conexión); Ctrl+S abre «Salir» desde la venta y desde una página; Esc
  regresa sin cerrar; Enter cierra y «Listo» va a la entrada; el dueño ve lo esperado y la venta de hoy, el cajero no;
  «Cerrar el día» aparece al cerrar el último turno; en el piloto no hay campo; Ctrl+U sigue directo.
