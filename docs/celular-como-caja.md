# El celular como una caja más

**Decisión del dueño (2026-11-08):** el celular (la app Android, hoy el Pixel del dueño) se da de alta en el POS **como
una caja**, con el alta de siempre: encuentra la caja principal en la red, el dueño escribe su usuario y PIN, y se le pone
un nombre como «Celular de Luis». Baja **la copia de la caja** (la misma que baja una caja de Windows) y lo que cambia
después. Esto reemplaza lo que proponía la app de «celulares» como otra clase de equipo con llaves restringidas: no hay
una entidad «celular» ni llaves con menos permisos.

El celular **nunca abre un turno ni imprime**. Consulta (precio, búsqueda por código y por nombre, y su copia del catálogo para
trabajar sin red), manda tickets a una caja (paso 9) y, **desde el 2026-11-22, cobra** (paso 10, decisión del dueño: la app es de
uso personal, de él o de alguien igual de confianza): lo que cobra entra en el turno abierto de una caja de su sucursal (la caja
principal), con el folio, el corte y el puente con eleventa de esa caja. También manda lo contado a una caja («Mandar un conteo a una caja», al final): ahí quien atiende lo acepta; el celular nunca cambia existencias él mismo.

Este documento es lo que necesita la app para hablar con el POS: la secuencia de llamadas, qué guarda y qué no.

## Qué cambió en el POS

| Qué | Cómo queda | Estado | Dónde |
|---|---|---|---|
| El celular, ¿una caja o algo aparte? | Una caja más, sin campo nuevo: lo que la distingue es que nunca abre un turno | Del dueño | — |
| La regla de los códigos de fábrica (UPC/EAN) | La misma de la app (2026-10-11) en el servicio y en la copia de las cajas: tal cual; UPC-A de 12 con 0 sin ese 0; EAN-13 con 0 como UPC-A de 12 y luego de 11; UPC-E expandido. El tal cual siempre gana | Del dueño (2026-10-11) | `Nucleo/Catalogo/VariantesDeCodigo.cs`, `CodigosDeEscaneo.Escanear` |
| Si las variantes de un código dan con dos productos | No se adivina: no se encuentra ninguno (404 con «coincide con más de un producto… búscalo por nombre»). También si dan con dos presentaciones del mismo producto (la pieza y la caja de 12: cambiaría la cantidad) | Decidido, por revisar | `CodigosDeEscaneo.Escanear`, `ApiCaja.cs` |
| El lector de la caja no dice la simbología | Se deduce del largo: 12 dígitos, UPC-A; 13, EAN-13; 8, UPC-E si se expande a un UPC-A válido. Un EAN-8 que también es un UPC-E válido se prueba además expandido (la app no lo hace; solo se nota si el catálogo no tiene el EAN-8 y sí ese UPC-A) | Decidido, por revisar | `VariantesDeCodigo.cs` |
| El corte del día («No se sabe de la caja…») | No se avisa de una caja que nunca ha abierto un turno: no pudo vender sin conexión. Las que sí abrieron turno avisan igual | Decidido, por revisar | `RegistroCorteDelDia.cs` |
| La bitácora de red de la caja principal («dejó de latir») | Igual: solo las cajas que alguna vez abrieron un turno. El celular entra y sale de la red todo el día y no dice nada de la red de la tienda | Decidido, por revisar | `ApiRed.cs` (`CajasQueLaten`) |
| La alerta de Telegram de caja sin conexión | Sin cambio: ya era solo de cajas con turno abierto | — | `MotorTelegram.cs` |
| El recordatorio de cerrar el día | Sin cambio: es de la sucursal, no de cada caja, y el celular no lo pide | — | `RegistroCorteDelDia.cs` |
| Configuración › Cajas | Sin cambio: el celular sale con su serie y su equipo. Es donde se le quita la llave si se pierde («Quitar PC») | Decidido, por revisar | `AdministrarCajas.razor` |
| Los filtros de caja de los reportes (ventas de otros días, historial de cortes, ventas por periodo) | Sin cambio: sale el celular, como una caja recién dada de alta antes de su primer turno; elegirlo dice que no hubo turnos | Decidido, por revisar | `VentasDeOtrosDias.cs` |
| El corte del día de todas las cajas | Sin cambio: sus renglones son turnos, y el celular no tiene | — | `RegistroCorteDelDia.cs` |
| El puente con eleventa (serie, folios, cajero de eleventa) | Lo que cobra el celular es una venta de la caja en cuyo turno entra: va a eleventa como las de esa caja (su caja de eleventa, el cajero de eleventa de quien cobra), con el folio de esa caja. La serie del celular nunca se usa | Decidido, por revisar (2026-11-22) | `ApiCobroDelCelular.cs`, `EnvioVentasEleventa.cs` |
| Cobrar en el celular | En una llamada, en el turno abierto de una caja de su sucursal, por el mismo camino que el cobro de esa caja (paso 10) | Del dueño (2026-11-22); los detalles, por revisar | `ApiCobroDelCelular.cs` |

## La secuencia de la app

Todo es HTTP con JSON (nombres en camelCase; los enums van como número; las fechas en ISO 8601) al servicio de la caja
principal, puerto **5080**, por el Wi-Fi de la tienda. Los errores llegan como `{"mensaje": "…", "codigo": "…"}` (el
código puede venir vacío); el `mensaje` está escrito para enseñarse tal cual.

### 1. Encontrar la caja principal

- **Por la red:** un UDP `PAPELERIAPOS?` (texto) a la difusión de la red al puerto **5081**. Contesta
  `PAPELERIAPOS 5080` desde su IP: la dirección es `http://{esa IP}:5080/`. Lo que contesta por UDP no prueba nada; la
  identidad se prueba en el paso 4.
- **A mano:** la IP que escriba el dueño.
- `GET /api/estado` (sin llave) confirma que es el POS (`servicio: "Papelería POS"`). Si dice `unaSolaCaja: true`, la caja
  principal se instaló como «La única caja» y **no da de alta otros equipos** (el alta contesta 403 con código
  `una-sola-caja`): se le dice al dueño tal cual. Si dice `tiendaNueva: true`, todavía no hay tienda.

### 2. Dar de alta el celular (una vez)

1. `POST /api/alta/opciones` con `{"usuario": "luis", "pin": "1234"}` (de quien tiene «Dar de alta y quitar cajas»).
   Contesta las sucursales con sus cajas: `[{"id", "nombre", "cajas": [{"id", "nombre", "serie", "equipo"}]}]`. Un usuario o
   PIN malo, o sin ese permiso: 400 con el mensaje para enseñarlo; tras varios intentos malos, ese aparato espera.
2. `POST /api/alta` con:

   ```json
   {"usuario": "luis", "pin": "1234", "sucursalId": "…", "cajaId": null,
    "nombre": "Celular de Luis", "serie": "CEL", "equipo": "Pixel 8 Pro"}
   ```

   - **`cajaId` siempre `null`**: así se crea una caja nueva. Con el id de una caja que ya existe, el celular **le quita su
     llave a esa caja** (es como se reemplaza la PC de una caja): la caja de Windows dejaría de funcionar.
   - `serie`: de 1 a 3 letras, sin repetir la de otra caja (B ya es de la caja 2; la principal no lleva). `CEL` sirve; si ya existe, 400
     «La serie CEL ya es de otra caja.» y se propone otra (`CE2`…). Nunca se usa para folios: lo que cobra el celular toma el
     folio de la caja en cuyo turno entra (paso 10), que en línea sale sin letra.
   - `equipo`: el modelo, como se verá en Configuración › Cajas.

   Contesta `{"caja": {"id", "nombre", "serie", "sucursalId", "sucursal", "equipo"}, "llave": "…", "llaveDelServicio": "…"}`.
3. La app guarda **la llave** (cifrada con el Android Keystore), el id de la caja, la sucursal, la dirección y
   **`llaveDelServicio`** (la llave pública del servicio, para el paso 4). La llave va en **cada** llamada después, en el
   encabezado **`X-Caja-Llave`**. Con esa llave el celular es una caja: puede pedir lo mismo que una caja.

### 3. Entrar

`POST /api/sesion` con `{"usuario", "pin"}` (con la llave): contesta la sesión con el id del usuario, su nombre y sus
permisos. El celular no la necesita para el precio ni la búsqueda (piden solo la llave), solo para lo que dependa de
quién es.

### 4. Si la caja principal cambia de IP

Cuando la dirección guardada deja de contestar, se busca otra vez por UDP (paso 1) y, **antes de mandarle la llave**, se le
pide que pruebe quién es: `GET /api/identidad?caja={id}&reto={reto}` (sin llave; solo contesta en la red de la tienda). El
reto: 32 bytes al azar en base64 para URL, sin `=` (de 16 a 128 letras, números, `-` o `_`). Contesta
`{"prueba", "llaveDelServicio", "firma"}`. Se acepta la dirección nueva solo si:

- `prueba` es `HEX(HMAC-SHA256(llave = UTF8(huella), mensaje = UTF8("papeleria-pos/identidad/1\n{cajaId}\n{reto}\n{direccion}")))`,
  donde `huella = HEX_MAYÚSCULAS(SHA-256(UTF8(llave de la caja)))` y `direccion` es `IP:puerto` a la que se conectó
  (`192.168.0.10:5080`; IPv6 entre corchetes). Se compara sin importar mayúsculas.
- `firma` (base64, ECDSA P-256 con SHA-256, formato IEEE P1363 de 64 bytes) es válida con la `llaveDelServicio` guardada
  en el alta (base64 de SubjectPublicKeyInfo) sobre `UTF8("papeleria-pos/identidad-del-servicio/1\n{cajaId}\n{reto}\n{direccion}\n{prueba}")`.

Es lo mismo que hace la caja de Windows (`Descubrimiento.cs`, `ReencuentroDeLaPrincipal.cs`).

### 5. El latido

`GET /api/estado?caja={id}` con la llave, **cada 30 segundos mientras la app está al frente** (la caja de Windows lo hace
cada 5; el celular no lo necesita tan seguido) y nunca en segundo plano. **No manda `porSubir` ni `conError`**: son de la
bandeja de lo vendido sin conexión, que el celular no tiene. Cualquier llamada con la llave ya cuenta como «se le vio».

Lo que contesta trae las versiones con que se decide si pedir cambios (paso 7): `versionDatos` (catálogo),
`versionExistencias`, `base` y `formatoCopia`.

### 6. La copia completa

`GET /api/cajas/{id}/paquete` con la llave. Es grande (varios MB con el catálogo de la tienda, sin comprimir): espera hasta
60 s y se baja la primera vez, cuando los cambios dicen que se baje completa, o cuando la de la app no se puede leer.

Lo que trae y qué hace la app con cada cosa:

| Campo | Para qué | ¿Se guarda? |
|---|---|---|
| `version`, `versionExistencias`, `base`, `formato`, `versionVendidos`, `versionElegidos`, `huellaPromociones` | Para pedir los cambios | Sí |
| `caja` | Su nombre y su sucursal (`sucursalId`) | Sí |
| `productos` | Cada variante con su descripción, código principal, impuesto, unidad, `existencia`, sus presentaciones y precios | Sí |
| `codigos` | **Todos** los códigos: `{codigo, varianteId, presentacionId}` (también los de la caja de 12) | Sí |
| `marcas`, `sinonimos`, `vendidos`, `elegidos` | La búsqueda por nombre como la de la caja | Si la app busca sin red |
| `promociones` | Las que no han terminado, con sus fechas | Si enseña el precio con promoción |
| `fotos` | De qué producto es cada foto y su huella; la foto se baja aparte: `GET /api/fotos/{varianteId}` (JPEG, ETag con la huella) | Si enseña fotos |
| `configuracion` | El nombre de la tienda y lo demás de Configuración › Tienda | Opcional |
| **`usuarios`, `permisos`, `reglas`** | Los usuarios con el **hash de su PIN** | **No: se descartan al leer** (abajo, «Privacidad») |
| **`clientes`, `registrados`** | Los clientes con su crédito | **No: se descartan al leer** |
| `encabezado`, `pie`, `logo`, `alCobrar`, `ultimoFolio`, `limiteEfectivoCentavos`, `exhibidores` | Del ticket y la venta | No hacen falta |

**El precio de menudeo** de un producto (el que enseña el verificador): de sus `precios`, los que tienen
`presentacionId` vacío; de esos, los de la sucursal de la caja si hay alguno, si no los generales (`sucursalId` vacío); y
de ellos, el de la `cantidadMinimaMilesimas` más chica (`PrecioDeMenudeo.De`). Los demás renglones son el mayoreo (desde
cierta cantidad) y los de cada presentación. Los precios van en centavos con IVA; las cantidades en milésimas.

**Al escanear contra la copia:** la regla de los códigos de `Catalogo.kt` sobre `codigos` (es la misma del POS). El
producto sale con la presentación de **ese código** (`presentacionId`), no siempre la pieza.

### 7. Lo que cambió

Al abrir la app, y cuando el latido traiga una versión distinta de la de la copia (`versionDatos ≠ version`,
`versionExistencias ≠ versionExistencias`, otra `base` u otro `formatoCopia`):

```
GET /api/cajas/{id}/cambios?desde={version}&existencias={versionExistencias}&base={base}&formato={formato}
    &vendidos={versionVendidos}&elegidos={versionElegidos}&promociones={huellaPromociones}
```

Con la llave; espera hasta 10 s. Si `paqueteCompleto` trae texto (otra base, otro formato, demasiados cambios), se baja la
copia completa (paso 6). Si no, se aplica como la caja de Windows (`CopiaAlDia.Aplicar`):

- `productos`: reemplazan a los de la copia con el mismo `varianteId`; los nuevos se agregan.
- `quitados`: se quitan, con sus códigos.
- `codigos`: **todos** los de los productos que llegaron; reemplazan los que la copia tenía de esos productos, y un código
  que llegó con otro producto deja de ser del de antes.
- `existencias`: `{varianteId, cantidad}` que cambiaron.
- Lo demás (`marcas`, `sinonimos`, `promociones`, `fotos`, `fotosQuitadas`, `vendidos`, `elegidos`, `caja`…): si viene, reemplaza
  lo de la copia; vacío es que no cambió. `usuarios`, `permisos`, `reglas`, `clientes` y `registrados` se descartan igual.
- Después: `version`, `versionExistencias`, `versionVendidos`, `versionElegidos` y `huellaPromociones` toman lo que dicen
  los cambios.

**Con qué frecuencia:** con el latido de cada 30 s al frente basta; los productos nuevos se dan de alta pocos días a la
semana. Si se quiere al momento, `GET /api/cajas/{id}/avisos` (con la llave) es un canal `text/event-stream` que manda
`event: versiones` con `{"base", "catalogo", "existencias", "credito", "formato"}` al conectarse y cada vez que cambia
algo, y `: vivo` cada 15 s; solo al frente, y se cierra al ir a segundo plano.

### 8. Con red: precio y búsqueda

Todas con la llave y `sucursalId` de la caja:

- `GET /api/productos/codigo/{codigo}?sucursalId=…`: el producto de ese código, con la regla de los códigos y la
  existencia de ahora. 404 si no está o si sus variantes dan con dos productos (el `mensaje` lo dice).
- `GET /api/productos/buscar?q={texto}&sucursalId=…&cajaId={id}&maximo=30`: por nombre, código o marca, como la caja
  (`[{varianteId, descripcion, codigo, precioCentavos, existencia, vendidos, …}]`).
- `GET /api/productos/{varianteId}?sucursalId=…`: el producto completo, con su presentación base.

Si no contesta, lo mismo contra la copia.

### 9. Mandar el ticket a una caja (2026-11-21)

La preventa (fase 8): el ticket armado en el celular llega a la espera de una caja, que lo cobra. **El celular dice qué y
cuánto; los precios los pone el servicio con su catálogo y la caja al armarlo, nunca el celular.**

1. **A qué cajas:** `GET /api/cajas/{id}/para-mandar` con la llave. Las cajas activas de su sucursal que alguna vez abrieron
   un turno (los otros celulares no salen): `[{id, nombre, serie, recibe, atiende, esperan, turnoAbierto}]`. `recibe` es que esa caja
   tiene la venta abierta ahora (pregunta por sus tickets cada 2 s; vale si preguntó hace menos de 8 s); `atiende`, el
   nombre de quien entró a la venta; `esperan`, cuántos tickets tiene en espera; `turnoAbierto` (2026-11-22), que tiene un turno
   abierto: el celular puede cobrar en él (paso 10), aunque no tenga la venta abierta.
2. **Mandarlo:** `POST /api/cajas/{id}/mandar` con la llave y
   `{"id": "<uno nuevo por ticket>", "destino": "<id de la caja>", "renglones": [{"varianteId", "presentacionId", "codigo", "cantidadMilesimas"}]}`.
   Cada renglón va por su variante y presentación de la copia o, sin ellas, por el código (con la regla de los códigos). **Un
   artículo común** (2026-11-22) va sin variante, con `descripcion` y `precioCentavos`:
   `{"descripcion": "Engargolado", "precioCentavos": 3550, "cantidadMilesimas": 1000}` (paso 10, «Los renglones»). La caja lo pone
   en su espera con ese precio, como su Ctrl+P.
   Hasta 200 renglones, cada uno de 1 a 9,999. El servicio espera a que la caja lo guarde (hasta 10 s,
   `Celular:SegundosParaRecibir`) y contesta `{"id", "destino", "caja", "renglones", "articulos", "totalCentavos", "recibido"}`
   con el total a los precios y promociones de hoy del servicio. Los errores (`{"mensaje", "codigo"}`), en español para
   enseñarlos tal cual:
   - 409 `caja-no-recibe`: «La Caja 2 no tiene la venta abierta (o está apagada): que entren a vender ahí y vuelve a
     mandarlo.», o, si la caja dejó de preguntar mientras tanto, «La Caja 2 no lo recogió: …». No le llega después.
   - 400 `no-esta` (un producto ya no está en el catálogo), `vacio`, `cantidad`, `demasiados`, `otra-caja-destino`, `comun` (un
     artículo común mal armado, o un producto del catálogo con precio).
   - 409 `ya-cobrado`: ese Id ya es una venta cobrada. 401 `sin-llave`: sin la llave de un celular dado de alta.
   - **Mandarlo otra vez con el mismo Id no lo duplica** (se perdió la respuesta): contesta lo mismo. `recibido: false` solo si
     la caja lo recogió y no alcanzó a decir que lo guardó; mandarlo otra vez lo aclara.
3. **En la caja:** con la venta abierta, la caja pregunta cada 2 s por la red de fondo (`GET /api/cajas/{id}/recibidos?esperan=&atiende=`,
   y `POST` del mismo con `{"ids"}` ya guardados). Llega como un ticket en espera más, al final, con el nombre del celular y el
   ícono del celular en su ficha en lugar de la hora, y el aviso «Llegó un ticket de Celular de Luis: 4 artículos, $94.00.
   Retómalo arriba o con F5.». No toca el ticket que se atiende. Se retoma con F5 o con su ficha, y sobrevive a un reinicio
   como los demás. **Para que se note (2026-11-22):** su ficha (o «Más», si va ahí) brilla unos segundos y abajo a la izquierda
   sale un aviso que no detiene nada, en la venta y en cualquier página: «Llegó un ticket de Celular de Luis · 4 art. · $94.00 ·
   F5 para elegirlo». Si llegó con una página abierta, la ficha brilla al volver a la venta; F5 desde la página lleva a elegirlo.

**Decidido, por revisar:** lo que está en camino vive en memoria del servicio (unos segundos: el celular espera la respuesta y
la caja lo recoge cada 2 s); si el servicio se reinicia justo entonces, el celular dice que no lo recogió y se manda otra vez.
Manda cualquier celular dado de alta (el alta ya pidió el PIN del dueño); el permiso «Mandar tickets desde el celular» y el
nombre de quien lo armó (con su sesión) quedan para cuando haya más de un celular. Sin red, el QR del ticket (pendiente en la
app).

### 10. Cobrar en el celular (2026-11-22)

**Decisión del dueño:** la app es de uso personal (él, o alguien igual de confianza) y el celular cobra. En la primera versión
solo cobra con la caja principal contestando; sin red, el ticket se queda en el celular. Como al mandarlo, **el celular dice qué
y cuánto; los precios y las promociones los pone el servicio, nunca el celular.**

Todo con la llave (`X-Caja-Llave`) y el id del celular en la ruta. Los montos en centavos con IVA; las cantidades en milésimas;
`forma` es un número: **0 efectivo, 1 tarjeta, 2 transferencia** (3, crédito, no se acepta desde el celular).

#### Los renglones (los mismos al mandar, cotizar y cobrar)

```json
[{"varianteId": "…", "presentacionId": "…", "cantidadMilesimas": 3000},
 {"codigo": "7501234567890", "cantidadMilesimas": 1000},
 {"descripcion": "Engargolado", "precioCentavos": 3550, "cantidadMilesimas": 1000}]
```

- **Producto:** por su variante y presentación de la copia (sin presentación, la base) o, sin variante, por el código (la regla
  de los códigos). **Nunca lleva precio**: con `precioCentavos` y `varianteId`, 400 `comun`.
- **Artículo común:** sin variante, con `precioCentavos` (por pieza, de 1 a 9,999,999) y `descripcion` (de 1 a 80 letras, se le
  quitan los espacios de las orillas). Con `precioCentavos` el renglón es artículo común aunque traiga `codigo`. Sin inventario.
- Hasta 200 renglones; cada cantidad de 1 a 9,999 (1 a 9,999,000 milésimas). Dos renglones del mismo producto y presentación se
  juntan en una línea (como en la caja).

#### Cotizar: `POST /api/cajas/{id}/cotizar`

Lo que costaría, **con la misma función que el cobro** (no se separan). No cobra nada ni pide turno. `id` puede ir vacío y
`cobros` es opcional:

```json
{"renglones": [ … ], "cobros": [{"forma": 0, "montoCentavos": 10000}]}
```

Contesta (200):

```json
{"lineas": [{"varianteId": "…", "presentacionId": "…", "descripcion": "Lápiz del 2 Pelikan Triangular", "cantidadMilesimas": 3000,
             "precioCentavos": 800, "importeCentavos": 2400, "ahorroMayoreoCentavos": 0, "ahorroPromocionCentavos": 0,
             "promocion": null, "articuloComun": false},
            {"varianteId": null, "presentacionId": null, "descripcion": "Engargolado", "cantidadMilesimas": 1000,
             "precioCentavos": 3550, "importeCentavos": 3550, "ahorroMayoreoCentavos": 0, "ahorroPromocionCentavos": 0,
             "promocion": null, "articuloComun": true}],
 "renglones": 2, "articulos": 4, "totalCentavos": 5950, "efectivoCentavos": 6000, "redondeoCentavos": 50,
 "pago": {"sinEfectivoCentavos": 0, "efectivoCentavos": 6000, "redondeoCentavos": 50, "recibidoCentavos": 10000,
          "faltanCentavos": 0, "cambioCentavos": 4000}}
```

`totalCentavos` es el exacto (el de tarjeta y transferencia); `efectivoCentavos`, lo que se cobra si todo es en efectivo;
`pago` solo viene con `cobros`, y dice cuánto falta o el cambio (sin error si falta). Con los cobros, los mismos errores de forma
que el cobro (`forma`, `solo-efectivo`, `cobro`, `pasa-del-total`).

#### El redondeo al peso (para enseñarlo sin red)

Es `Venta.Efectivo` (`src/PapeleriaPos.Nucleo/Ventas/Venta.cs`) y `Redondeo.AlPeso`; la app lo puede portar tal cual:

1. `total` = la suma de los importes de las líneas (ya con promociones y mayoreo).
2. `sinEfectivo` = la suma de tarjeta + transferencia. Si pasa de `total`: error `pasa-del-total` (no dan cambio).
3. `exacto` = `total − sinEfectivo`. Si es 0, no hay efectivo que cobrar.
4. Si no: `efectivo` = `exacto` redondeado **al peso más cercano, los 50 centavos hacia arriba**:
   `((exacto + 50) / 100) * 100` con división entera ($70.49 → $70; $70.50 → $71). Si da 0 (algo de menos de $0.50), se cobra
   **$1** (100).
5. `redondeo` = `efectivo − exacto` (puede ser negativo, hasta −49). Lo que paga el cliente es `total + redondeo`.
6. `cambio` = `recibido − efectivo`; si `recibido < efectivo`, faltan `efectivo − recibido` (error `falta` al cobrar).

Tarjeta y transferencia van exactas, sin redondeo. (Las impresiones no se redondean, pero el celular no las cobra.)

#### Cobrar: `POST /api/cajas/{id}/cobrar`

```json
{"id": "<uno nuevo por ticket>",
 "renglones": [ … ],
 "cobros": [{"forma": 1, "montoCentavos": 2000, "referencia": "4321"},
            {"forma": 2, "montoCentavos": 3000},
            {"forma": 0, "montoCentavos": 5000}],
 "caja": null, "cajeroId": "<el usuarioId de POST /api/sesion>", "totalVisto": 7050}
```

- `id`: **uno nuevo por ticket**, es el Id de la venta. **Mandarlo otra vez con el mismo no cobra dos veces**: contesta la
  misma venta con `yaCobrada: true` (también si ese Id ya se cobró en una caja, por ejemplo uno que se mandó). Si ese ticket
  estaba en camino a una caja y no lo había recogido, ya no se le da.
- `cobros`: efectivo (el monto es lo que entregó el cliente, uno solo), tarjeta (exacta; `referencia` opcional, hasta 60) y
  transferencia (exacta; **queda «por verificar»**, como en la caja). Se pueden mezclar.
- `caja`: en el turno de qué caja entra (de `para-mandar`, las de `turnoAbierto`). Vacía: la única de la sucursal con turno
  abierto (en la sucursal 2, la caja principal); si hay más de una, 400 `elige-caja`.
- `cajeroId`: quién cobra (el que entró en el celular con `POST /api/sesion`). Vacío: el cajero del turno donde entra.
- `totalVisto` (opcional, recomendado): el total exacto que el celular enseñó. Si el del servicio es otro, no cobra (409
  `total-distinto`): se cotiza otra vez y se revisa con el cliente.

Contesta (200):

```json
{"id": "…", "folio": "A-000001", "folioEleventa": null, "cajaId": "…", "caja": "Caja 1", "cajero": "Encargado de prueba",
 "renglones": 2, "articulos": 4, "totalCentavos": 5950, "redondeoCentavos": 50, "cobradoCentavos": 6000,
 "recibidoCentavos": 10000, "cambioCentavos": 4000,
 "cobros": [{"forma": 0, "montoCentavos": 6000, "recibidoCentavos": 10000, "cambioCentavos": 4000, "referencia": null}],
 "yaCobrada": false, "ticket": { "…el TicketDto, como lo imprime la caja…" }}
```

`folio` es el del POS (el de la serie de esa caja; ver abajo). En el piloto, `folioEleventa` es el de eleventa si contestó a
tiempo (el servicio espera como en la caja, hasta `Eleventa:SegundosAlCobrar`); si no, va vacío y la venta sube a eleventa en la
vuelta de la bandeja. El celular espera la respuesta hasta unos 15 s.

#### Los errores del cobro y de la cotización

Todos `{"mensaje", "codigo"}`, con el mensaje para enseñarse tal cual:

| Estado | `codigo` | `mensaje` |
|---|---|---|
| 401 | `sin-llave` | Solo un celular dado de alta cobra desde el celular. |
| 401 | `caja-sin-alta` | Este celular ya no está dado de alta. (o el de la llave: se vuelve al alta) |
| 403 | `otra-caja` | Esta PC está dada de alta como otra caja. (el id de la ruta no es el de la llave) |
| 403 | `no-es-celular` | Esta caja abre turnos: cobra en su pantalla de venta. |
| 400 | `sin-id` | El ticket no trae su número: actualiza la app. (solo al cobrar) |
| 400 | `vacio` | El ticket está vacío. |
| 400 | `demasiados` | El ticket trae N renglones; van hasta 200: pártelo en dos. |
| 400 | `cantidad` | Una cantidad del ticket no es válida: va de 1 a 9,999. |
| 400 | `no-esta` | «7501234567890» ya no está en el catálogo de la caja principal: quítalo del ticket. (o «N productos ya no están…») |
| 400 | `comun` | Un renglón sin producto es un artículo común: lleva su descripción (hasta 80 letras) y su precio, de $0.01 a $99,999.99. |
| 400 | `comun` | Un producto del catálogo no lleva precio: lo pone la caja principal. Mándalo sin precio o como artículo común. |
| 400 | `sin-cobro` | Falta cómo paga: efectivo, tarjeta o transferencia. |
| 400 | `forma` | Desde el celular se cobra en efectivo, tarjeta o transferencia: lo fiado, en una caja. |
| 400 | `solo-efectivo` | Mientras dure el piloto con eleventa, esta caja solo cobra en efectivo: sin tarjeta, transferencia, crédito ni saldo a favor. Cóbralo todo en efectivo. |
| 400 | `cobro` | Cada cobro debe ser mayor a cero. / El efectivo va en un solo cobro. / La referencia va hasta 60 letras. |
| 400 | `pasa-del-total` | Tarjeta y transferencia no pueden pasar del total: no dan cambio. |
| 400 | `falta` | Faltan $59.00 por cobrar. |
| 409 | `total-distinto` | El total cambió: ahora es $90.00 (el celular decía $135.00). Revísalo con el cliente y vuelve a cobrar. |
| 409 | `sin-turno` | Ninguna caja tiene un turno abierto: abre el turno en la caja principal y vuelve a cobrar. |
| 409 | `sin-turno` | La Caja 1 no tiene un turno abierto: que lo abran ahí, o elige otra caja. (con `caja`) |
| 400 | `elige-caja` | Hay 2 cajas con turno abierto (Caja 1, Caja 2): elige en cuál entra el cobro. |
| 400 | `otra-caja-destino` | Esa caja ya no está en la tienda: elige otra. |
| 400 | `cajero` | Ese usuario ya no cobra en esta sucursal: entra otra vez en el celular. |
| 403 | `no-permitido` | Quien cobra no tiene permiso para el artículo común: quítalo del ticket o mándalo a una caja. |

Un 400 sin código es una regla del cobro de la caja que se colaba (por ejemplo, el turno se cerró justo en ese momento): se
enseña el mensaje. Un 5xx o sin respuesta: se manda otra vez con el **mismo** `id`.

#### Qué se decidió (decidido, por revisar; el dueño no estaba)

- **El turno y el corte:** el celular nunca abre un turno. Lo que cobra entra en **el turno abierto de una caja de su
  sucursal**: la que elija la app (`caja`) o, sin elegir, la única con turno (en la sucursal 2, la caja principal). Es una venta
  más de esa caja: sale en su «Ventas del día», cuenta en su corte X/Z y en el corte del día, y **el efectivo se espera en el
  cajón de esa caja** (quien cobra en el celular lo deja ahí). Si se quiere un turno propio del celular con su propio corte,
  hace falta otra cosa (abrir y cerrar turno en el celular): queda en «Recomendaciones».
- **Los folios:** el de la serie de la caja donde entra, asignado por el mismo camino que las ventas en línea de esa caja
  (`RegistroCaja.CobrarAsync`), así que el cambio de folios en curso (las ventas en línea sin letra) le aplica igual. La serie
  del celular no se usa.
- **Eleventa en el piloto:** la venta entra a la bandeja hacia eleventa como las de esa caja (la misma caja de eleventa; el
  cajero de eleventa de quien cobra). Solo efectivo mientras dure el piloto, como en la caja.
- **Quién cobra:** el usuario que entró en el celular (`cajeroId`, de `POST /api/sesion` con su PIN); sin él, el cajero del
  turno. No se pide autorización (el dueño: casi no se pide).
- **El permiso:** ninguno nuevo; basta el alta (que ya pidió el PIN del dueño), como al mandar. Una caja de Windows (que abre
  turnos) no cobra por aquí. El artículo común pide lo mismo que en la caja: «Artículo común (sin inventario)» en «Puede» (lo
  tienen todos al llegar).
- **Inventario y bitácora:** baja lo vendido como en la caja (el artículo común no); el artículo común queda en la bitácora
  como en la caja, y la venta, con un renglón «Cobró en Celular de Luis, en el turno de la Caja 1, sin imprimir el ticket»
  (acción «Cobrar sin imprimir el ticket», con el celular como caja, sin pedir ese permiso). En el registro del servicio,
  «Cobro del celular · …».
- **El ticket:** el celular no imprime. La venta queda «sin imprimir»: desde «Ventas del día» de esa caja, su primera impresión
  no pide motivo. **Imprimirlo desde el celular en la impresora de una caja queda pendiente** (pide que la caja lo recoja como
  los tickets mandados): «Recomendaciones».
- **Precios:** siempre los del servicio con las promociones de hoy (su día, no el del celular). Con `totalVisto`, si no
  coinciden, no se cobra.

#### Probarlo contra la base de prueba

En la PC de desarrollo (Linux o Windows), con la papelería ficticia (usuarios de prueba: cajero 1111, encargado 2222, dueno
3333; nunca datos de la tienda):

```
dotnet run --project src/PapeleriaPos.Herramientas -p:EnableWindowsTargeting=true -- preparar-prueba --salida /tmp/cel/prueba.db
cd src/PapeleriaPos.Servicio/bin/Debug/net10.0
dotnet PapeleriaPos.Servicio.dll --environment Development --Urls=http://0.0.0.0:5080 \
  "--ConnectionStrings:Pos=Data Source=/tmp/cel/prueba.db" --Caja:BaseLocal=/tmp/cel/caja-local.db \
  --Telegram:Activo=false --Descubrimiento:Activo=false --Impresora:Vigilar=false --Respaldos:Activo=false --Eleventa:Activo=false
```

(`preparar-prueba` deja también una carpeta `Ejemplos` donde se corre: se borra.) Se abre el turno en la Caja 1 como siempre (la
pantalla de venta) o, solo en la base de prueba, con curl: se le da una llave nueva a la Caja 1 con el alta (`"cajaId": "<id de
la Caja 1>"`) y `POST /api/turnos`. Luego, con la llave del celular (el alta del paso 2, con `"cajaId": null`):

```
U=http://<IP de la PC>:5080; L=<llave del celular>; C=<id del celular>
curl -s -H "X-Caja-Llave: $L" $U/api/cajas/$C/para-mandar
curl -s -H "X-Caja-Llave: $L" -H 'Content-Type: application/json' -X POST $U/api/cajas/$C/cotizar \
  -d '{"renglones":[{"varianteId":"<variante>","cantidadMilesimas":3000},{"descripcion":"Engargolado","precioCentavos":3550,"cantidadMilesimas":1000}],
       "cobros":[{"forma":0,"montoCentavos":10000}]}'
curl -s -H "X-Caja-Llave: $L" -H 'Content-Type: application/json' -X POST $U/api/cajas/$C/cobrar \
  -d '{"id":"<uuid nuevo>","renglones":[…los mismos…],"cobros":[{"forma":0,"montoCentavos":10000}],"totalVisto":5950}'
```

El mismo `cobrar` otra vez contesta `yaCobrada: true` con el mismo folio. Las pruebas del servicio:
`tests/PapeleriaPos.Pruebas/Servicio/CobroDelCelularTests.cs` (con `ServicioDePrueba`).

### 11. Imprimir el ticket de lo que cobra el celular (2026-11-26)

**Decisión del dueño:** el ticket de lo que cobra el celular se imprime. Lo ideal es que la impresora de tickets esté en la red; si
no, está conectada a la caja principal y esa PC lo imprime. En la caja se puede prender «Imprimir lo que cobra el celular», y en el
celular, al cobrar, hay una casilla «Imprimir ticket» (marcada por omisión, con su preferencia en los ajustes de la app).

**El celular nunca le habla a la impresora: se lo pide al servicio.** Esto reemplaza lo que el paso 10 dice de «El ticket» (ahí
quedó pendiente). Todo con la llave (`X-Caja-Llave`) y el id del celular en la ruta; los errores, `{"mensaje", "codigo"}`, con el
mensaje para enseñarse tal cual.

#### Por dónde sale

El ticket sale en la impresora de tickets de **la caja en cuyo turno entró la venta** (en la sucursal 2, la caja principal), el
mismo que imprime la caja (el `ticket` que contesta el cobro) y **sin abrir el cajón**:

- **Impresora de red** (`tcp://ip:9100` en caja.json): **el servicio se la manda directo**, sin depender de la ventana de la caja
  (puede estar cerrada). Espera a que la impresora confirme: contesta `impreso`, o por qué no (`impresora-no-contesta`, `sin-papel`,
  `sin-confirmar`).
- **Impresora de Windows** (su nombre en caja.json): **la recoge la ventana de esa caja**, como los tickets mandados (pregunta cada
  2 s desde que abre, con la entrada, la venta o cualquier página a la vista), la pone en la cola de su impresora y dice si salió.
  Con la ventana cerrada no se puede (`ventana-cerrada`). Si no sale, el papel se queda en la cola de esa caja («Reintentar» o «No
  imprimirlo», como cualquier ticket); si después sale, la venta deja de estar «sin imprimir».
- **Qué impresora tiene cada caja** lo dice su ventana al preguntar (lo de `Impresora` de caja.json) y el servicio lo guarda: no
  hay nada nuevo que configurar. Hasta que una caja abre su ventana con esta versión, el servicio no lo sabe (`sin-impresora`).
- **El interruptor:** en la caja, Configuración › Ticket › **«Imprimir lo que cobra el celular»** (Apagado / Prendido; llega
  **apagado**; lo cambia quien tiene «Configurar el ticket»; con el teclado: Tab llega, ← → o Enter eligen). Es de toda la tienda,
  como «Al cobrar». Apagado, el celular no imprime (`apagado`).

#### 11.1 Antes de cobrar: `GET /api/cajas/{id}/para-mandar`

Cada caja de la lista (paso 9) trae ahora `impresion`: si el ticket de lo que se cobre en ella puede salir, y por dónde.

```json
[{"id": "…", "nombre": "Caja 1", "serie": "", "recibe": true, "atiende": "Ana López", "esperan": 0, "turnoAbierto": true,
  "impresion": {"puede": true, "por": "caja", "codigo": null, "mensaje": "Sale en la impresora de la Caja 1.", "automatico": true}}]
```

- `puede`: la casilla «Imprimir ticket» se puede marcar. Si no, sale apagada con `mensaje`.
- `por`: `"red"` (el servicio se la manda directo) o `"caja"` (la imprime la ventana de esa caja); vacío si no hay por dónde.
- `automatico`: «Imprimir lo que cobra el celular» está prendido en la caja.
- La caja que cuenta es la del cobro: la que se mande en `caja` o, sin elegir, la única con `turnoAbierto`.
- Con impresora de red no se sabe si está prendida o con papel hasta mandarle algo: `puede` es `true` y, si falla, lo dice el cobro.

| `puede` | `por` | `codigo` | `mensaje` |
|---|---|---|---|
| `true` | `red` o `caja` | — | Sale en la impresora de la Caja 1. |
| `false` | — | `apagado` | Está apagado «Imprimir lo que cobra el celular»: se prende en la caja, en Configuración › Ticket. |
| `false` | — | `sin-impresora` | La Caja 1 no tiene impresora de tickets configurada. |
| `false` | — | `sin-impresora` | La Caja 1 todavía no dice qué impresora de tickets tiene: abre su ventana. |
| `false` | `caja` | `ventana-cerrada` | La Caja 1 no tiene su ventana abierta (o está apagada): ábrela para que imprima. |

#### 11.2 Al cobrar: `"imprimir"` en `POST /api/cajas/{id}/cobrar`

El cobro del paso 10 con un campo más:

```json
{"id": "…", "renglones": [ … ], "cobros": [{"forma": 0, "montoCentavos": 10000}], "totalVisto": 5950, "imprimir": true}
```

- `imprimir: true`: que salga. `false`: que no. **Sin el campo** (una app de antes): lo que diga el interruptor de la caja.
- **El cobro nunca falla por el ticket:** primero se cobra (con su folio y, en el piloto, el de eleventa) y después se imprime. La
  respuesta es la del paso 10 más `impresion`:

```json
{"id": "…", "folio": "000123", "…": "…", "yaCobrada": false, "ticket": { "…": "…" },
 "impresion": {"estado": "impreso", "codigo": null, "mensaje": "Salió en la impresora de la Caja 1.",
               "cajaId": "…", "caja": "Caja 1", "por": "red", "salio": true}}
```

`salio` es `true` solo con `estado: "impreso"`.

| `estado` | `codigo` | `mensaje` | Qué hace la app |
|---|---|---|---|
| `impreso` | — | Salió en la impresora de la Caja 1. | Nada |
| `impreso` | — | El ticket 000123 ya había salido. (el mismo cobro otra vez) | Nada |
| `en-camino` | — | La Caja 1 ya lo tiene y lo está imprimiendo. | Nada: va a salir. Si no salió, «Imprimir» (11.3) |
| `no-pedida` | — | No se pidió imprimir el ticket. | Nada |
| `no-impreso` | `apagado`, `sin-impresora`, `ventana-cerrada` | Los de la tabla de 11.1 | Enseñar el mensaje; «Imprimir» después |
| `no-impreso` | `ventana-cerrada` | La Caja 1 no lo recogió: revisa que tenga su ventana abierta y pídelo otra vez. | «Imprimir» |
| `no-impreso` | `impresora-no-contesta` | La impresora de la Caja 1 no contesta: revisa que esté prendida y conectada, y pídelo otra vez. | «Imprimir» |
| `no-impreso` | `sin-papel` | Se acabó el papel de la impresora de la Caja 1: pon papel y pídelo otra vez. | «Imprimir» |
| `no-impreso` | `sin-confirmar` | La impresora de la Caja 1 no terminó de confirmar: mira si salió antes de pedirlo otra vez. | Preguntar antes de «Imprimir»: pudo salir |
| `no-impreso` | `no-salio` | No salió de la impresora de la Caja 1: {lo que dijo la impresora}. En esa caja se reintenta o se deja sin imprimir. | Enseñarlo: se resuelve en la caja |
| `no-impreso` | `no-salio` | La venta quedó cobrada, pero no se pudo pedir su ticket: pídelo otra vez. | «Imprimir» |

- `sin-papel` y `sin-confirmar` solo los sabe la impresora de red (contesta su estado). Una de Windows dice lo que diga Windows, en
  `no-salio`.
- **Cuánto esperar:** con `imprimir`, el cobro puede tardar más: hasta 8 s a la ventana de la caja (`Celular:SegundosParaImprimir`) o,
  con impresora de red apagada, unos 8 s (tres intentos de 2 s, `Celular:SegundosParaConectarImpresora`), además de lo del paso 10. La
  app espera **hasta 25 s**. Si prefiere contestar el cobro al momento: cobra con `imprimir: false` y pide el ticket con 11.3.
- **El mismo cobro otra vez** (se perdió la respuesta, mismo `id`): no cobra ni imprime dos veces. Si el ticket ya salió, `impreso`
  («ya había salido»); si va en camino, cómo va; si no había salido, lo intenta.

#### 11.3 Después: `POST /api/cajas/{id}/ventas/{ventaId}/imprimir`

Para una venta ya cobrada (`ventaId` es el `id` del cobro): no salió la primera vez, o la piden otra vez. El cuerpo es opcional:

```json
{"id": "<uno nuevo cada vez que se oprime Imprimir>", "cajeroId": "<el usuarioId de POST /api/sesion>", "motivo": null}
```

- `id`: el de **esta petición**. Mandarla otra vez con el mismo (se perdió la respuesta) **no saca otro papel**: contesta cómo quedó
  esa. Sin `id`, cada llamada es una petición nueva. Mientras una de esa venta va en camino, otra tampoco saca otro papel.
- `cajeroId`: quién lo pide; vacío, quien cobró la venta.
- **La primera impresión** (la venta sigue «sin imprimir») **no pide motivo ni permiso** y no duplica nada: la venta es la misma.
- **Si ya había salido, es una reimpresión**, como en la caja: con el permiso «Reimprimir un ticket» de quien la pide y `motivo` si a
  ese usuario se le pide (al cajero, de inicio, sí; hasta 200 letras). Queda en la bitácora.
- Sale en la impresora de la caja donde entró la venta, aunque su turno ya esté cerrado.

Contesta **200 con el mismo `impresion`** de 11.2 (`impreso`, `en-camino` o `no-impreso` con su `codigo`): que no haya salido no es
un error de la llamada. Errores:

| Estado | `codigo` | `mensaje` |
|---|---|---|
| 401 | `sin-llave` | Solo un celular dado de alta pide tickets a la impresora de una caja. |
| 401 | `caja-sin-alta` | Este celular ya no está dado de alta. |
| 403 | `otra-caja` | Esta PC está dada de alta como otra caja. (el id de la ruta no es el de la llave) |
| 403 | `no-es-celular` | Esta caja abre turnos: cobra en su pantalla de venta. |
| 404 | `no-esta` | Esa venta no está en la caja principal: revisa que se haya cobrado. |
| 400 | `cajero` | Ese usuario ya no cobra en esta sucursal: entra otra vez en el celular. |
| 400 | `con-motivo` | El ticket 000123 ya salió: para reimprimirlo, escribe el motivo. (o «El motivo va hasta 200 letras.») |
| 403 | `no-permitido` | El ticket 000123 ya salió, y quien lo pide no tiene permiso para reimprimir tickets. |

#### La venta deja de estar «sin imprimir»

- Al cobrar, la venta queda «sin imprimir» (como en el paso 10). **Cuando el papel sale** (lo confirma la impresora de red o la
  ventana de la caja), deja de estarlo y cuenta una impresión: en «Ventas del día» ya no dice «Ticket sin imprimir», y pedirla otra
  vez es reimprimir.
- Si no salió (o no se sabe: `en-camino`, `sin-confirmar`), sigue «sin imprimir»: su primera impresión, desde el celular (11.3) o
  desde «Ventas del día» de la caja, no pide motivo.
- **La bitácora:** si salió al cobrar, nada (como el ticket que la caja imprime al cobrar). Si se pidió y no salió: «Cobró en Celular
  de Luis, en el turno de la Caja 1, sin imprimir el ticket: se pidió y no salió (sin-papel)» («Cobrar sin imprimir el ticket», con
  el celular como caja, sin pedir ese permiso). Si no se pidió, el renglón del paso 10. Si sale después: «Primera impresión del
  ticket 000123: no salió al cobrar (no es una reimpresión); la pidió Celular de Luis y salió en la Caja 1». La reimpresión:
  «Reimpresión del ticket 000123, pedida desde Celular de Luis en la impresora de la Caja 1. Motivo: …».

#### Las llamadas de la ventana de la caja (no son del celular)

Con la llave de la caja, cada 2 s mientras su ventana está abierta (`ImpresionesDelCelular`, que arranca `PantallaCaja`):

- `GET /api/cajas/{caja}/por-imprimir?impresora={lo de caja.json}`: dice qué impresora de tickets tiene (el servicio la guarda si
  cambió) y recibe lo que le toca imprimir: `[{"id", "ventaId", "de": "Celular de Luis", "ticket": { …TicketDto… }}]`. Con impresora
  de red no recibe nada. Lo que no ha confirmado se le da otra vez (por su `id` no lo imprime dos veces), hasta 30 s: después ya no.
- `POST /api/cajas/{caja}/por-imprimir` con `{"id", "salio": true}` o `{"id", "salio": false, "motivo": "…"}`. Si no salió y después
  sale con «Reintentar», lo dice otra vez con `salio: true`.

#### Qué se decidió (decidido, por revisar; el dueño no estaba)

| Qué | Cómo queda | Por qué |
|---|---|---|
| En qué impresora sale | En la de **la caja en cuyo turno entró la venta**; si esa no puede, no sale en otra | Ahí se deja el efectivo y ahí está el cliente. En la sucursal 2 es la caja principal (la única PC), como pidió el dueño |
| Dónde se configura la impresora | En ningún lado nuevo: es la de caja.json de cada caja, que su ventana le dice al servicio | Una sola configuración; el instalador ya la pide |
| El interruptor | Configuración › Ticket, **por sucursal**, y llega **apagado** | «Se puede habilitar»: nada sale solo de la impresora sin que alguien lo prenda. Va con lo demás del ticket («Al cobrar») |
| El interruptor y la casilla del celular | Apagado: el celular no imprime aunque lo pida. Prendido: manda la casilla (`imprimir`); si el celular no dice nada, se imprime | El interruptor es el permiso de la tienda; la casilla, lo de cada cobro |
| Permiso para pedirlo | Ninguno nuevo: basta el alta del celular. La reimpresión, el permiso de reimprimir de quien la pide (Puede o No; quien necesitaría autorización, no puede desde el celular) | El dueño: casi no se pide autorización; los permisos son Puede/No |
| El cajón | **No se abre.** El ticket del celular va siempre sin el pulso del cajón | El pulso sale con el papel, y quien cobra está con el celular, no frente al cajón; se abriría solo. En la tienda no hay cajón. El efectivo se deja en esa caja como en el paso 10 |
| Con la ventana cerrada (impresora de Windows) | No se imprime ni se guarda para después: lo que la ventana no recoge en 30 s ya no se le da | Un ticket que sale solo una hora después, sin cliente, confunde. Se pide otra vez o se imprime desde «Ventas del día» |
| Lo que está en camino | En memoria del servicio, como los tickets mandados: si se reinicia justo entonces, la venta queda «sin imprimir» y se pide otra vez | La venta ya está cobrada y guardada; el papel se puede pedir otra vez sin motivo |
| El cobro y el papel | Primero se cobra; lo que pase con el papel nunca deshace ni falla el cobro | El dinero ya se recibió |
| Reintentos a la impresora de red | Dos más si no contesta al conectarse (no se le mandó nada); ninguno si falló después de mandarle algo | Pudo salir: mandarlo otra vez sacaría dos papeles |
| Si la impresora de red tiene papel antes de cobrar | No se pregunta: `para-mandar` dice que puede y el cobro dice si falló | Preguntarle es abrirle una conexión cada vez que el celular consulta; la POS-8360 atiende una a la vez |
| Cuál impresora dice una caja | La que diga su ventana con su llave (también `tcp://…`) | Las cajas son equipos de confianza (su alta pidió el PIN del dueño) |

Lo pospuesto está en «Recomendaciones» del [plan](plan.md#recomendaciones).

#### Probarlo con la base ficticia, sin impresora de verdad

Las pruebas (una impresora de red de mentiras en un puerto TCP de la PC, y la ventana de la caja con una impresora que guarda los bytes):

```
dotnet test tests/PapeleriaPos.Pruebas -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~ImpresionDelCelular|FullyQualifiedName~ImprimirLoDelCelular"
```

`tests/PapeleriaPos.Pruebas/Servicio/ImpresionDelCelularTests.cs` (las rutas y cada error) y
`tests/PapeleriaPos.Pruebas/Interfaz/ImprimirLoDelCelularEnLaCajaTests.cs` (el interruptor con el teclado y la ventana de la caja).

A mano, en la PC de desarrollo, con el servicio como en el paso 10 y **una impresora de red de mentiras**: un puerto que recibe el
ticket y contesta «con papel» (un byte 0) cuando le preguntan. En otra terminal:

```
python3 -c '
import socket
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind(("127.0.0.1", 9100)); s.listen()
while True:
    c, _ = s.accept(); datos = b""
    while not datos.endswith(b"\x1dr\x01"):
        parte = c.recv(4096)
        if not parte: break
        datos += parte
    print(len(datos), "bytes de ticket"); c.sendall(b"\x00"); c.close()'
```

(Con `b"\x0c"` en lugar de `b"\x00"` contesta «sin papel»; sin el `sendall`, «no confirmó»; sin correrlo, «no contesta».) Luego:

1. Se arranca el servicio del paso 10 con `--Caja:Impresora=tcp://127.0.0.1:9100`, se abre la caja del navegador
   (`http://localhost:5080`), se entra como dueno y se abre el turno: esa caja ya le dijo su impresora al servicio.
2. En Configuración › Ticket se prende «Imprimir lo que cobra el celular» (Tab hasta el interruptor, →).
3. Sin navegador, lo mismo con curl y la llave de la Caja 1 (la del paso 10, `L1`; `K` es el id de la Caja 1 y `D` el `usuarioId` de
   `POST /api/sesion`): la «ventana» dice su impresora una vez, y se prende el interruptor.

```
curl -s -H "X-Caja-Llave: $L1" "$U/api/cajas/$K/por-imprimir?impresora=tcp%3A%2F%2F127.0.0.1%3A9100"
curl -s -H "X-Caja-Llave: $L1" -H 'Content-Type: application/json' -X PUT $U/api/cajas/$K/configuracion/ticket/imprimir-lo-del-celular \
  -d '{"usuarioId":"'$D'","imprimir":true}'
```

4. Con la llave del celular (el alta del paso 2):

```
curl -s -H "X-Caja-Llave: $L" $U/api/cajas/$C/para-mandar
curl -s -H "X-Caja-Llave: $L" -H 'Content-Type: application/json' -X POST $U/api/cajas/$C/cobrar \
  -d '{"id":"<uuid nuevo>","renglones":[{"descripcion":"Engargolado","precioCentavos":3550,"cantidadMilesimas":1000}],
       "cobros":[{"forma":0,"montoCentavos":5000}],"imprimir":true}'
curl -s -H "X-Caja-Llave: $L" -H 'Content-Type: application/json' -X POST $U/api/cajas/$C/ventas/<el mismo uuid>/imprimir \
  -d '{"id":"<otro uuid>","motivo":"Lo pidieron otra vez"}'
```

La terminal de la impresora dice cuántos bytes le llegaron; `impresion` dice `impreso`, y en «Ventas del día» la venta ya no dice
«Ticket sin imprimir». Sin impresora (`--Caja:Impresora` vacío), `para-mandar` dice `sin-impresora`. El camino de la impresora de
Windows (la ventana de la caja la recoge) solo se prueba a mano en Windows, con la caja instalada; en Linux lo cubren las pruebas.

**En la tienda (sábado o domingo):** prender el interruptor en la caja principal, cobrar en el celular con la casilla marcada y ver
que sale el papel en la POS-8360 sin abrir nada; con la ventana de la caja cerrada (si la impresora es de Windows) ver que el celular
lo dice; y quitarle el papel o apagarla para ver el aviso.

### Lo que el celular no llama nunca

Turnos (`/api/turnos…`), el cobro de la caja (`POST /api/ventas`) y lo demás de `/api/ventas…`, la bandeja (`/api/bandeja/…`),
`folio-usado`, cortes, entradas y salidas, impresiones. Cobra solo con `POST /api/cajas/{id}/cobrar` (paso 10) y manda lo contado solo con `POST /api/cajas/{id}/mandar-conteo` («Mandar un conteo a una caja»): nunca cambia existencias él mismo.

### Cuando la llave deja de servir

Un 401 con código `caja-sin-alta` quiere decir que el dueño le quitó la llave (Configuración › Cajas › «Quitar PC») o que
la caja principal es otra base: la app borra la llave y su copia y vuelve al alta. Un 403 con `otra-caja` es una
llamada con el id de otra caja (un error de la app).

## Privacidad (decidido, por revisar)

La copia de la caja trae los usuarios con el **hash de su PIN** (y sus permisos) y los clientes con su crédito: la caja de
Windows los usa para entrar y vender sin conexión. Un PIN de 4 dígitos son 10,000 posibilidades: con el hash, si el
celular se pierde, se sacan todos en segundos.

- **En el POS no cambia la copia:** el dueño eligió que el celular baje la de la caja (2026-11-08).
- **Lo recomendado para la app:** descartar `usuarios`, `permisos`, `reglas`, `clientes` y `registrados` al leer el
  paquete y los cambios, y **nunca guardarlos** (ni en la base, ni en caché, ni en registros). Entrar es siempre con red
  (`POST /api/sesion`); sin red, la app solo consulta precios, que no piden sesión.
- **Si se pierde el celular:** Configuración › Cajas › «Quitar PC» en su renglón, y su llave ya no entra. La copia que
  quedó en el celular solo tiene el catálogo, si la app siguió la recomendación.
- **Lo que queda por revisar con el dueño:** si un día se quiere entrar sin red en el celular, hace falta otra cosa (por
  ejemplo, la huella del celular con una sesión guardada), no el hash del PIN.

## Pendiente del dueño

- **«La única caja» y el celular:** en una caja principal instalada como «La única caja» (la sucursal 2 empezará con una
  sola PC), el alta no acepta otros equipos, y el celular ahora es uno. En la matriz no pasa (está como «Caja principal»).
  Si se quiere el celular ahí, hay que decidir si «La única caja» acepta celulares (no hay forma de distinguirlos de una PC
  sin un campo nuevo) o si esa PC se instala como «Caja principal».

## Mandar un conteo a una caja (2026-11-22)

«Contar inventario» de la app (0.5): se escanea, cada lectura suma una pieza y el celular guarda la lista. **El celular nunca
cambia existencias:** manda la lista a una caja y ahí quien puede la acepta (tal cual o con cambios) o la rechaza. No depende
del «piloto automático» (el POS que pide qué contar) ni lo prende.

### Decisiones (por revisar)

| Qué | Cómo queda | Por qué |
|---|---|---|
| El permiso para aceptar | **«Conteo completo de un departamento»** (`ConteoCompleto`), solo «Puede» (Puede/No: «Con autorización» cuenta como No). No hay permiso nuevo | Es el que ya deja que lo contado reemplace la existencia viendo lo que dice el sistema (cerrar el conteo completo, el conteo de la temporada). «Contar inventario» es para contar a ciegas, y «Ajustar una existencia» lleva límite y autorización |
| A qué cajas | Las activas de su sucursal que alguna vez abrieron turno (como «para-mandar»), que tienen **un turno abierto de alguien con ese permiso**. Si nadie así, no recibe y se dice por qué | Del dueño |
| Qué es la cantidad | **Lo que hay en total** de ese producto (anaquel, mostrador y bodega): «hay 12». Lo que no viene en la lista **no se toca** | Como lo propone la app; un producto que no se contó no es un cero |
| Presentaciones | Cada renglón va con su presentación: 2 «Caja 12» son 24 piezas. Varios renglones del mismo producto (2 cajas y 5 sueltas) **se suman**: 29 | Como el ticket del celular |
| Lo vendido entre contar y aceptar | Queda **lo contado + lo que se movió desde que se contó** (lo vendido resta, lo que entró suma): contó 15, se vendieron 3, quedan 12. La caja lo enseña («Desde que se contó», «Quedará») | Es la regla del conteo completo (`EsperadoDe`): la diferencia es contra lo que decía el sistema al contar, y lo vendido después sigue contando |
| La hora de contar | `contadoUtc` del celular; sin ella, cuando llega; si viene «en el futuro» (reloj adelantado), cuando llega. Más de 7 días: no se recibe (`contado-viejo`) | Lo vendido desde entonces se cuenta desde esa hora |
| Dos listas con el mismo producto | **Gana la que se contó al final**, se acepten en el orden que sea. Si al aceptar una ya hay algo más nuevo de ese producto (otra lista contada después y aceptada, el conteo del día, el conteo completo o un ajuste hechos después), ese producto **no se aplica** y la caja dice por qué («Ya se contó después…») | Lo más nuevo es lo que se vio. Lo aplicado por listas más viejas no cuenta como «movido desde que se contó» |
| Dos personas, partes del mismo producto | No se suman entre listas: cada lista es un total | Que lo junten en una sola lista |
| Quién contó | Opcional: el `usuarioId` de la sesión del celular (`POST /api/sesion`); si no viene o no existe, solo se sabe el celular | Los datos son de confianza (sin conteo a ciegas ni doble) |
| El piloto con eleventa | **No se manda ni se aplica:** el inventario se lleva en eleventa (`TrastiendaEnElPiloto`). `para-contar` dice que ninguna caja recibe (`piloto-eleventa`) y `mandar-conteo` contesta 409 con ese código. Una lista que llegó antes del piloto no se aplica (409 `piloto-eleventa`); se rechaza | La lectura de eleventa regresaría la existencia en unos minutos sin decir nada. **En la sucursal 2, mientras dure el piloto, no se usa** |
| Sin red | La lista se queda en el celular; se manda solo con la caja principal contestando | De la app |

### Las llamadas del celular

Todas con la llave (`X-Caja-Llave`) y el id del celular en la ruta. Errores: `{"mensaje", "codigo"}`, el mensaje para enseñarse tal cual.

**1. A qué cajas:** `GET /api/cajas/{id}/para-contar`

```json
[{"id": "…", "nombre": "Caja 2", "serie": "B", "recibe": true, "atiende": "Ana López", "codigo": null, "porQue": null, "pendientes": 0},
 {"id": "…", "nombre": "Caja principal", "serie": "A", "recibe": false, "atiende": "Pedro",
  "codigo": "sin-permiso", "porQue": "Pedro tiene el turno de la Caja principal y no puede aceptar conteos (le falta «Conteo completo de un departamento»): que abra turno ahí quien puede, o mándala a otra caja.", "pendientes": 1}]
```

`pendientes`: las listas que ya esperan ahí. `atiende`: quien tiene el turno (el que la puede aceptar, si recibe). Los códigos
de `codigo` (y de `mandar-conteo`):

| Código | `porQue` |
|---|---|
| `sin-turno` | «La Caja 2 no tiene un turno abierto: que abran turno ahí y vuelve a mandarla.» |
| `sin-permiso` | «{quien} tiene el turno de la Caja 2 y no puede aceptar conteos (le falta «Conteo completo de un departamento»): que abra turno ahí quien puede, o mándala a otra caja.» |
| `piloto-eleventa` | «Mientras dure el piloto con eleventa, el inventario se lleva en eleventa: las existencias llegan aquí con su lectura. El conteo se captura allá.» |

**2. Mandarla, en una llamada:** `POST /api/cajas/{id}/mandar-conteo`

```json
{"id": "<uno nuevo por lista>", "destino": "<id de la caja>", "contadoUtc": "2026-11-22T17:42:00Z", "usuarioId": null,
 "renglones": [{"varianteId": "…", "presentacionId": "…", "codigo": null, "cantidadMilesimas": 2000},
               {"varianteId": null, "presentacionId": null, "codigo": "7501234567890", "cantidadMilesimas": 5000}]}
```

Cada renglón, como en «mandar»: por su variante y presentación de la copia o, sin ellas, por el código (con la regla de los
códigos; el código de la caja de 12 cuenta cajas). `cantidadMilesimas` en la presentación del renglón (2 cajas = 2000), de 0 a
99,999 (0 es «no hay ninguno»). Hasta 500 renglones. Contesta 200 con cómo va (abajo), `estado: "recibida"`. **Con el mismo `id`
no se duplica:** contesta lo que ya se guardó, aunque la caja ya no reciba. La lista queda guardada en la caja principal (en su
base, no en memoria): no hace falta que la caja la recoja al momento.

| Estado | Código | Mensaje |
|---|---|---|
| 400 | `sin-id` | «La lista no trae su número: actualiza la app.» |
| 400 | `vacio` | «La lista está vacía.» |
| 400 | `demasiados` | «La lista trae 501 renglones; van hasta 500: pártela en dos.» |
| 400 | `cantidad` | «Una cantidad de la lista no es válida: va de 0 a 99,999.» o ««{producto}» se cuenta en piezas enteras.» |
| 400 | `no-esta` | ««{código}» ya no está en el catálogo de la caja principal: quítalo de la lista.» (o «N productos ya no están… (…): quítalos de la lista.») |
| 400 | `ambiguo` | ««{código}» coincide con más de un producto: búscalo por nombre en el celular.» |
| 400 | `sin-inventario` | ««{producto}» no lleva inventario (es un servicio): quítalo de la lista.» |
| 400 | `otra-caja-destino` | «Esa caja ya no está en la tienda: elige otra.» |
| 400 | `contado-viejo` | «Esta lista se contó hace más de 7 días: desde entonces se vendió mucho; cuenta otra vez.» |
| 409 | `sin-turno`, `sin-permiso`, `piloto-eleventa` | Los de la tabla de arriba |
| 409 | `otro-celular` | «Esa lista es de otro celular.» (el mismo `id` desde otro celular) |
| 401 | `sin-llave` | «Solo un celular dado de alta manda conteos a las cajas.» |
| 401 | `caja-sin-alta` / 403 `otra-caja` | Como en todas (la llave ya no sirve / el id de la ruta no es el suyo) |

**3. Cómo va:** `GET /api/cajas/{id}/conteos/{conteoId}` (una) y `GET /api/cajas/{id}/conteos` (las últimas 20 del celular, de la
más reciente). 404 `no-esta` («Esa lista no llegó a ninguna caja: mándala otra vez.») si no es suya o no llegó.

```json
{"id": "…", "destino": "…", "caja": "Caja 2", "estado": "aceptada-con-cambios",
 "contadoUtc": "2026-11-22T17:42:00Z", "recibidoUtc": "2026-11-22T17:43:10Z", "resueltoUtc": "2026-11-22T18:05:00Z",
 "resolvio": "Ana López", "motivo": null, "productos": 3, "aplicados": 1,
 "renglones": [{"varianteId": "…", "descripcion": "Lápiz HB", "contado": 29, "aceptado": 27, "aplicado": true, "nota": "En la caja lo cambiaron a 27."},
               {"varianteId": "…", "descripcion": "Goma blanca", "contado": 3, "aceptado": null, "aplicado": false, "nota": "Lo quitaron en la caja: no cambió."},
               {"varianteId": "…", "descripcion": "Regla 30 cm", "contado": 10, "aceptado": 10, "aplicado": false, "nota": "No se aplicó: ya se había contado después."}]}
```

`estado`: `recibida`, `aceptada` (tal cual), `aceptada-con-cambios` (se cambió una cantidad o se quitó un producto) o `rechazada`
(con `motivo` si lo escribieron; nada cambió). `contado` y `aceptado` en unidad base (piezas). Lo recomendado para la app:
después de mandarla, preguntar por ella al abrir la lista de conteos (no hace falta un sondeo seguido; la caja puede tardar
horas en revisarla).

### En la caja

- **El aviso:** con la venta abierta, y solo para quien tiene el permiso, la caja pregunta cada 10 s por la red de fondo
  (`GET /api/cajas/{caja}/conteos-recibidos`). Cuando llega una: «Llegó un conteo de Celular de Luis: 14 productos. Revísalo en
  Inventario (F4) › Conteos del celular.», y en la barra de abajo el chip «Conteos del celular 1», que lleva ahí. También con
  «/» › «Conteos del celular». No toca el ticket que se atiende.
- **Dónde:** Inventario (F4) › «Conteos del celular» (la tarjeta dice «1 por revisar»). Cada lista es un renglón «Celular de Luis ·
  contó Ana · contado a las 10:42 · 14 productos»; Enter la abre. Cada producto: lo contado (y cómo lo mandó: «2 Caja 12 + 5
  Pieza»), lo movido desde que se contó, lo que quedará, lo que hay hoy y la diferencia.
- **Con el teclado:** se escribe otra cantidad sobre la contada y Enter pasa al siguiente; Ctrl+Supr quita el producto (o lo
  regresa; también «Quitar»); Ctrl+Enter (o «Aplicar al inventario») la aplica. «Rechazar la lista» pide el porqué (opcional) y
  Enter la rechaza; «No» regresa. Esc vuelve a las tareas sin tocar nada: la lista sigue esperando.
- **Las llamadas de la caja** (con su llave): `GET /api/cajas/{caja}/conteos-recibidos` (las que esperan),
  `GET …/conteos-recibidos/{conteoId}` (una, con `desde` y `hoy` de cada producto), `POST …/{conteoId}/aceptar`
  `{"usuarioId", "renglones": [{"varianteId", "cantidadMilesimas"}]}` (los que no vienen se quitaron) y `POST …/{conteoId}/rechazar`
  `{"usuarioId", "motivo"}`. Errores: 403 `no-permitido` (sin el permiso; queda el intento en la bitácora), 409 `ya-resuelta`
  («Esa lista ya se aplicó.» / «… ya se rechazó.»), 409 `piloto-eleventa`, 404 `no-esta` («Esa lista no es de esta caja.»), 400
  `vacio` («No queda ningún producto: si no se aplica nada, recházala.»), `otro-producto`, `cantidad`.

### El kardex y la bitácora

- **Kardex:** cada producto aplicado con diferencia deja un movimiento de **conteo** (`TipoMovimiento.Conteo`, documento
  `ConteoDelCelular` con el id de la lista), a costo como el conteo del día y el completo (`AgregarDiferenciaDeConteoAsync`). En
  Inventario › Movimientos sale «del celular · Celular de Luis» y quien la aceptó. Cuenta como «contado» para el conteo del día.
- **Bitácora**, al llegar (`ContarInventario`, del celular y de quien contó, si se sabe): «Mandó un conteo de Celular de Luis a la
  Caja 2: 14 productos (contó Ana), contado a las 10:42». Al aceptarla (`ConteoCompleto`, de quien la aceptó, en esa caja, con lo
  que vale la diferencia): «Aplicó el conteo de Celular de Luis (contó Ana), contado a las 10:42: 13 productos, 3 con diferencia;
  cambió 1 producto; quitó 1 producto». Al rechazarla: «Rechazó el conteo de …: 14 productos, nada cambió. Motivo: …».

### Probarlo con la base ficticia

Pruebas: `tests/PapeleriaPos.Pruebas/Servicio/ConteosDelCelularTests.cs` (el servicio, con `ServicioDePrueba`) y
`tests/PapeleriaPos.Pruebas/Interfaz/ConteosDelCelularEnLaCajaTests.cs` (la caja, sin navegador y con el teclado):

```
dotnet test tests/PapeleriaPos.Pruebas -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~ConteosDelCelular"
```

A mano, con el servicio sobre la base ficticia (README, «Desarrollo»): se da de alta el celular (paso 2 de arriba), se abre un
turno en una caja con un usuario que tenga «Conteo completo de un departamento», y:

```
curl -s -H "X-Caja-Llave: <llave del celular>" http://localhost:5080/api/cajas/<id del celular>/para-contar
curl -s -H "X-Caja-Llave: <llave del celular>" -H "Content-Type: application/json" \
     -d '{"id":"<un guid nuevo>","destino":"<id de la caja>","contadoUtc":null,"usuarioId":null,"renglones":[{"varianteId":null,"presentacionId":null,"codigo":"<un código>","cantidadMilesimas":12000}]}' \
     http://localhost:5080/api/cajas/<id del celular>/mandar-conteo
curl -s -H "X-Caja-Llave: <llave del celular>" http://localhost:5080/api/cajas/<id del celular>/conteos/<el guid>
```

En la caja: Inventario (F4) › Conteos del celular, Enter, Ctrl+Enter.
