# Fase 1 — Caja

> **Revisado.** Cierra lo que faltaba decidir antes de programar la caja. Lo que la caja debe hacer ya
> está en [caja y control](caja-y-control.md); aquí va cómo se construye, en qué orden y con qué datos.

## Entregas

La fase es grande, así que se parte en entregas. Cada una se prueba con la base importada de eleventa en una PC
aparte, sin tocar la operación de la tienda, hasta el piloto (fase 5).

| # | Entrega | Qué incluye |
|---|---|---|
| 1 | **Vender y cobrar en una caja** | Pantalla de venta, búsqueda y escaneo, ventas en espera, cobro mixto, ticket con logo y QR, cajón, turno con fondo, retiros y gastos, corte ciego (X y Z), entrada con usuario y PIN |
| 2 | **Control** ([plan](fase-1-control.md)) | Roles configurables, autorizaciones con PIN, bitácora, cancelaciones y devoluciones escaneando el ticket, verificar transferencias |
| 3 | **Varias cajas** ([plan](fase-1-varias-cajas.md)) | La caja principal como servidor de la sucursal, las demás cajas por red, contingencia con bandeja de salida |
| 4 | **Telegram** ([plan](fase-1-telegram.md)) | Alertas, autorización a distancia y resumen diario |
| 5 | **Módulo de impresión** ([plan](fase-1-impresion.md)) | Lo que hoy hace Control Print ([detalle](impresion.md)) |

Este documento detalla la entrega 1; las demás se cierran cuando les toque.

**Avance de la entrega 1:** ya funcionan, probados de punta a punta en el navegador con datos ficticios, la entrada
con PIN, abrir turno, escanear y buscar, cantidades (`3*código`, `+`/`-`), varios tickets (F5/F6), artículo común,
entradas y salidas de efectivo, verificador, cobro mixto, ticket, ventas del día, reimprimir, los cortes X y Z y la
configuración del ticket con logo. El ticket ya salió completo en la POS-8360 por la red: logo, acentos, QR y corte
de papel. Falta:

- Probar el cajón (todavía no hay) y la ventana de Windows.
- Instalar el servicio como servicio de Windows (entrega 3).

## Entrega 1: cómo se arma

```
 PC de la caja ─────────────────────────────────────────────────────────────
 │                                                                         │
 │  PapeleriaPos.Caja (ventana de Windows)        PapeleriaPos.Servicio    │
 │  ┌──────────────────────────────┐   HTTP      ┌──────────────────────┐  │
 │  │ PapeleriaPos.Interfaz        │──localhost─▶│ API + reglas         │  │
 │  │ (pantallas Blazor)           │             │ único que escribe    │  │
 │  │ impresión ESC/POS ──▶ POS-8360│             │ SQLite (WAL)         │  │
 │  └──────────────────────────────┘             └──────────────────────┘  │
 ───────────────────────────────────────────────────────────────────────────
```

- **La caja habla con el servicio por la API desde el primer día,** aunque estén en la misma PC. Así la entrega 3
  (varias cajas por red) no obliga a rehacer nada.
- **Proyectos nuevos:**
  - `PapeleriaPos.Interfaz`: componentes Blazor, compartidos después con el panel y la app Android.
  - `PapeleriaPos.Servicio`: ASP.NET Core; en la entrega 1 corre como programa, y como servicio de Windows desde la
    entrega 3.
  - `PapeleriaPos.Caja`: ventana WPF con WebView2, que muestra la interfaz e imprime.
- **La impresora la maneja la caja,** no el servicio: está conectada a esa PC.

## Ticket

**Impresora:** POS-8360, térmica de 80 mm, comandos ESC/POS, conectada a la red (puerto 9100): la caja le manda el ticket directo, sin controlador. Son 576 puntos de ancho: 48 caracteres por línea con la
letra normal. El ticket se manda directo con comandos, no como documento de Windows: sale más rápido y permite el QR.

Mismo orden que el ticket de eleventa, para que nadie extrañe nada:

```
            [ logo de la tienda ]
         NOMBRE DE LA PAPELERÍA
        Este ticket se expide en
         Calle, número y colonia
             Teléfono
 ──────────────────────────────────────────────
 Folio 000123              03/10/2026  12:47
 Caja 1                   Atendió: Ana
 ──────────────────────────────────────────────
 Cant.  Descripción                     Importe
 ══════════════════════════════════════════════
 2      Nylon C10 x Metro                $120.00
 12     Lapiz No. 2 Mirado                $84.00
        Ahorro por mayoreo               -$12.00
 8      Impresión Blanco y Negro           $8.00
 0.5 m  Listón 2.5 cm Rojo                 $4.00
 ──────────────────────────────────────────────
                     No. de artículos:    22.5
                        Usted ahorró: $12.00
                                Total: $204.00
                 Efectivo: $300.00  Cambio: $96.00
 ──────────────────────────────────────────────
              [ QR del folio ]
          Gracias por su compra
     Síguenos en nuestras redes sociales
```

- **Configuración** (botón en la barra, como en eleventa): encabezado y pie de hasta 8 renglones de 48 caracteres,
  "Agregar logotipo…" y "Quitar logotipo…", vista previa e "Imprimir ticket de prueba". Se guarda por sucursal y la
  cambia el encargado o el dueño con su PIN.
- **Logo:** al importarlo se convierte una vez a blanco y negro, a lo más 384 puntos de ancho (unos 48 mm), con los
  grises simulados con puntos; se manda con cada ticket como imagen.
- **QR con el folio:** sirve para devoluciones y, más adelante, para encontrar pedidos.
- **Cajón:** se abre con el comando de la impresora al cobrar en efectivo, y cada apertura queda registrada.
- Las descripciones largas pasan a la siguiente línea; el importe siempre va alineado a la derecha.

### Limpio y configurable (dueño, 2026-11-21)

- **Lo que ya no se imprime:** el IVA («Incluye IVA: …» y «(precios con IVA incluido)»), «Mayoreo desde 12» (sale «Ahorro por
  mayoreo» con lo que se ahorró), los privilegios junto al nombre del cliente («(descuento 10 % · mayoreo siempre)»: lo ahorrado ya
  sale bajo cada producto) y «se confirma al volver» en lo fiado sin conexión (sale «(saldo por confirmar)»). Se queda lo que el
  cliente necesita: la tienda, folio, fecha, caja y quién atendió, cada producto con su cantidad e importe, lo que se ahorró en
  palabras (mayoreo, promoción, descuento, «Usted ahorró»), el total, el redondeo, cómo pagó y su cambio, «SU CUENTA» si fió, la
  nota y el pie. El QR sigue apagado, como en eleventa.
- **Nombres con mayúsculas** (prendido por omisión): cada palabra del producto empieza con mayúscula, también los de eleventa
  todo en mayúsculas («CUADERNO PROF 100H» → «Cuaderno Prof 100h»). Solo lo impreso: el nombre guardado no cambia. Las reglas
  (por revisar con el dueño) están en `NombreEnElTicket`: «de», «y», «con», «para», «x»… en minúsculas salvo al empezar o tras
  «(»; lo que empieza con número en minúsculas («100h», «21 g», «50 x 70 cm»), salvo el grado de un lápiz («2B», «HB») y las
  memorias («32GB»); siglas conocidas (USB, CURP, RFC…) y letra con número («A4») en mayúsculas; lo escrito a propósito con
  mayúsculas y minúsculas («iPhone», «CURP impresa», «Copia B/N») se queda.
- **Cómo sale** (Configuración › Ticket, toda la tienda; con «Configurar el ticket»): nombres con mayúsculas; la letra de la
  impresora, A (la de siempre, 48 caracteres) o B (9×17, 64 caracteres: ESC M 1); su tamaño, normal o alta (doble alto, GS ! 1, mismos
  caracteres); los renglones juntos, normales (ESC 2) o separados (ESC 3 n: la altura de la letra más 4 o 24 puntos); el total grande
  y en negritas; quién atendió; y lo que se ahorró. Por omisión, como salía antes salvo los nombres: a la impresora le llegan los
  mismos comandos. Cada ajuste es un segmento: Tab llega, Enter o Espacio eligen y ← → (Inicio, Fin) pasan a la opción de al lado y
  la eligen. «Así sale» lo enseña al momento (la letra B, más chica; la alta, más separada). Se guarda en `ConfiguracionesTicket.Formato`
  (un JSON; las sucursales de antes guardan `{}`, que es el de siempre) y llega a cada caja con el ticket de cada cobro y con su copia
  (`FormatoDelTicket`), así que sin conexión sale igual.
- **Lo que no cambia:** los comprobantes (devolución, entradas y salidas, abono, estado de cuenta), el corte y los reportes salen con
  la letra A a 48 caracteres, como siempre: su diseño no es el del ticket de venta. No hay papel de 58 mm: la POS-8360 es de 80 mm
  (si algún día hay otra, el ticket ya se arma a cualquier ancho; faltarían los comprobantes).
- **Por revisar en la tienda** (sábado o domingo): la letra B y la alta en la POS-8360 (no todas las de su tipo tienen la B de 9×17),
  los renglones juntos y separados, y los acentos y la ñ con los nombres con mayúsculas.

## Pantalla de venta

Diseñada para 1366×768, todo con teclado y letra grande. **Misma distribución y mismas teclas que eleventa,** para
que los cajeros no tengan que aprender otras.

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ F1 Ventas   F3 Productos   F4 Inventario   Corte              Matriz · Caja 1 · Ana  │
├──────────────────────────────────────────────────────────────────────────────────────┤
│ VENTA - Ticket 1                                                                     │
│ Código del producto: [ 7501015213247______________________ ]   ENTER Agregar         │
│ INS Varios   CTRL+P Art. común   F10 Buscar   F7 Entradas   F8 Salidas   DEL Borrar  │
│ F9 Verificador                                                                       │
├─[ Ticket 1 ]─[ Ticket 2 ]────────────────────────────────────────────────────────────┤
│ Código de barras │ Descripción               │ Precio venta │ Cant. │ Importe │ Exist.│
│ N10              │ Nylon C10 x metro         │       $60.00 │     2 │ $120.00 │  44   │
│ 7501015213247    │ Goma M40 Pelikan          │        $5.00 │     1 │   $5.00 │  74   │
│▶IMP              │ Impresión blanco y negro  │        $1.00 │     8 │   $8.00 │   —   │
├──────────────────────────────────────────────────────────────────────────────────────┤
│ 3  Productos en la venta actual.                       ┌───────────────┐             │
│ F5 Cambiar   F6 Pendiente   Eliminar                   │  F12 Cobrar   │   $133.00   │
│ Total: $133.00   Pagó con: $0.00   Cambio: $0.00       └───────────────┘             │
│ Reimprimir último ticket                     Ventas del día                          │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

| Tecla | Qué hace | Entrega |
|---|---|---|
| ENTER | Agrega el producto del código escrito o escaneado | 1 |
| F10 | Buscar por nombre, código o marca, sin importar acentos | 1 |
| DEL | Borrar el artículo seleccionado | 1 (con bitácora en la 2) |
| F5 / F6 | Cambiar entre tickets abiertos / dejar el ticket pendiente | 1 |
| F7 / F8 | Entrada / salida de efectivo, siempre con motivo; ahí mismo se cancela una equivocada (2026-10-22) | 1; cancelar, paridad con eleventa |
| F9 | Verificador: consultar precio y existencia sin agregar a la venta | 1 |
| INS | «Varios», como en eleventa: la cantidad y luego el código del producto, escaneado o escrito (2026-10-22, decidido, por revisar) | Paridad con eleventa |
| CTRL+P | Artículo común: descripción y precio a mano, sin inventario | 1 (permiso configurable en la 2) |
| F12 | Cobrar | 1 |
| F11 | Mayoreo | Cuando se decida el mayoreo (fase 3) |
| F1, F3, F4 | Ventas, productos, inventario | Conforme llegue cada módulo |

- El lector escribe en el código del producto; si existe, se agrega solo.
- `*` antes del código cambia la cantidad (`3*7501015213247`); también se puede editar la columna de cantidad.
- La columna de existencia se queda, como en eleventa.
- **El artículo común** (Ctrl+P) es la puerta para vender sin que se descuente inventario. Se conserva porque los cajeros
  lo usan, pero en la entrega 2 su permiso será configurable y cada uso quedará en la bitácora.
- **Ins, «Varios»** (decidido, por revisar, 2026-10-22): hasta entonces Ins abría el artículo común, como Ctrl+P. Ahora hace
  lo de eleventa: pide cuántos y luego el código (escaneado o escrito) y agrega ese producto del catálogo, con las mismas
  reglas que `5*código`; el artículo común sigue en Ctrl+P ([paridad](paridad-eleventa.md#venta-y-cobro)).
- **Cobro:** total grande; "pagó con" y cambio; se puede repartir entre efectivo, tarjeta y transferencia.
  La transferencia queda **por verificar**, y la tarjeta guarda la referencia de la terminal.

## Turno y corte ciego

Dos turnos al día. **Cada cajero tiene su propio turno** y responde por su efectivo. **El corte lo hace el
encargado.**

1. **Abrir turno:** el cajero entra con su ficha y su PIN (2026-10-14; antes, con su usuario) y se cuenta el fondo, que
   no siempre es el mismo monto.
2. **Durante el turno:** entradas (F7) y salidas (F8) de efectivo, siempre con motivo. Una equivocada se cancela en el
   mismo panel (2026-10-22, como en eleventa; antes se compensaba con la contraria), con su motivo y con «Cancelar una
   entrada o salida», solo en su turno y antes del corte; el corte ya no la cuenta y dice cuántas se cancelaron. En el
   piloto no: van a eleventa.
3. **Corte X (parcial):** informativo; no cierra el turno.
4. **Corte del turno (Z):** quien tiene el permiso (el encargado o el dueño) ya entró con su PIN; cuenta el efectivo y
   escribe el total **sin ver cuánto debería haber** (2026-10-14: ya no por denominación ni otra vez el PIN; un escaneo en
   ese campo no se toma como dinero). El sistema compara con lo esperado (fondo + efectivo cobrado − cambio + entradas − salidas), guarda la
   diferencia a nombre del cajero del turno y la marca si pasa del umbral. Tarjeta y transferencias se listan aparte.
5. **Cambio de cajero en la misma caja:** se hace el corte del que sale y se abre el turno del que entra.

Imprimir el corte como comprobante en papel queda como [idea](ideas.md#documentos-impresos).

## Datos nuevos

Se suman a los del [modelo de la fase 0](modelo-datos.md), con las mismas reglas (UUIDv7, centavos, milésimas, UTC).

| Tabla | Campos principales |
|---|---|
| **Usuario** | Nombre, usuario, hash del PIN, rol (Dueño, Encargado, Cajero), sucursal, activo, intentos fallidos, bloqueado hasta |
| **Caja** | Sucursal, nombre, letra para lo que cobre sin conexión (B, C…; la principal no lleva, 2026-11-22), es principal |
| **Turno** | Caja, cajero, abierto y cerrado (UTC), fondo |
| **MovimientoCaja** | Turno, tipo (entrada, salida), monto, motivo, quién |
| **Venta** | Sucursal, caja, turno, cajero, serie y folio (000123 sin letra en línea; B-000123 sin conexión; A-000123 lo de antes), estado (en captura, en espera, pagada, cancelada), total, IVA desglosado, fechas |
| **VentaLinea** | Venta, variante (vacía en el artículo común), presentación, descripción tal como se vendió, cantidad, precio, importe, costo al momento de vender |
| **Cobro** | Venta, forma (efectivo, tarjeta, transferencia), monto, recibido y cambio, referencia, verificación (por verificar, verificada, rechazada) |
| **Corte** | Turno, tipo (X o Z), esperado por forma de pago, contado, diferencia, quién |
| **CorteConteo** | Corte, denominación, cantidad (ya no se llena desde el 2026-10-14; quedan los de antes) |
| **AperturaCajon** | Caja, turno, quién, motivo (venta o sin venta), fecha |

- Cada línea vendida genera su salida en el [kardex](modelo-datos.md#kardex), en la misma transacción que la venta.
- El folio sale de un contador en la tabla `Secuencias`: en línea, uno solo por sucursal y sin letra (`folio:{sucursal}:`); sin
  conexión, uno por letra (`folio:{sucursal}:B`). Ver `Folios` y `RegistroCaja.SecuenciaDelFolioAsync` (2026-11-22).
- El IVA se desglosa de precios que ya lo incluyen, según el impuesto de cada producto: con IVA 16 %, base =
  importe ÷ 1.16; con 0 % o exento, todo es base.

## Decisiones de la revisión

| Pregunta | Respuesta | Efecto |
|---|---|---|
| Turnos y corte | Dos turnos al día; el corte lo hace el encargado | Corte Z de quien tiene el permiso, con el total, a ciegas |
| Varios cajeros en una caja | Un turno por cajero | Cambio de cajero = corte del que sale |
| Teclas | Las de eleventa | Misma pantalla y mismas teclas ([arriba](#pantalla-de-venta)) |
| Fondo de caja | No siempre es el mismo | Se cuenta al abrir cada turno |
| Logo | Se importa | En la configuración, "Agregar logotipo…" carga la imagen, como en eleventa (no está en su base de datos) |
