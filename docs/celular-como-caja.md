# El celular como una caja más

**Decisión del dueño (2026-11-08):** el celular (la app Android, hoy el Pixel del dueño) se da de alta en el POS **como
una caja**, con el alta de siempre: encuentra la caja principal en la red, el dueño escribe su usuario y PIN, y se le pone
un nombre como «Celular de Luis». Baja **la copia de la caja** (la misma que baja una caja de Windows) y lo que cambia
después. Esto reemplaza lo que proponía la app de «celulares» como otra clase de equipo con llaves restringidas: no hay
una entidad «celular» ni llaves con menos permisos.

El celular **nunca abre un turno, nunca imprime y nunca vende**: en el piloto las ventas van a eleventa por el puente. Solo
consulta: precio, búsqueda por código y por nombre, y su copia del catálogo para trabajar sin red.

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
| El puente con eleventa (serie, folios, cajero de eleventa) | Sin cambio: solo manda lo que se vende, se devuelve o pasa en un turno, y el celular no hace nada de eso. Su serie de folios nunca se usa | — | `EnvioVentasEleventa.cs` |

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
     «La serie CEL ya es de otra caja.» y se propone otra (`CE2`…). Nunca se usa para folios: el celular no vende.
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
   un turno (los otros celulares no salen): `[{id, nombre, serie, recibe, atiende, esperan}]`. `recibe` es que esa caja
   tiene la venta abierta ahora (pregunta por sus tickets cada 2 s; vale si preguntó hace menos de 8 s); `atiende`, el
   nombre de quien entró a la venta; `esperan`, cuántos tickets tiene en espera.
2. **Mandarlo:** `POST /api/cajas/{id}/mandar` con la llave y
   `{"id": "<uno nuevo por ticket>", "destino": "<id de la caja>", "renglones": [{"varianteId", "presentacionId", "codigo", "cantidadMilesimas"}]}`.
   Cada renglón va por su variante y presentación de la copia o, sin ellas, por el código (con la regla de los códigos).
   Hasta 200 renglones, cada uno de 1 a 9,999. El servicio espera a que la caja lo guarde (hasta 10 s,
   `Celular:SegundosParaRecibir`) y contesta `{"id", "destino", "caja", "renglones", "articulos", "totalCentavos", "recibido"}`
   con el total a los precios y promociones de hoy del servicio. Los errores (`{"mensaje", "codigo"}`), en español para
   enseñarlos tal cual:
   - 409 `caja-no-recibe`: «La Caja 2 no tiene la venta abierta (o está apagada): que entren a vender ahí y vuelve a
     mandarlo.», o, si la caja dejó de preguntar mientras tanto, «La Caja 2 no lo recogió: …». No le llega después.
   - 400 `no-esta` (un producto ya no está en el catálogo), `vacio`, `cantidad`, `demasiados`, `otra-caja-destino`.
   - 409 `ya-cobrado`: ese Id ya es una venta cobrada. 401 `sin-llave`: sin la llave de un celular dado de alta.
   - **Mandarlo otra vez con el mismo Id no lo duplica** (se perdió la respuesta): contesta lo mismo. `recibido: false` solo si
     la caja lo recogió y no alcanzó a decir que lo guardó; mandarlo otra vez lo aclara.
3. **En la caja:** con la venta abierta, la caja pregunta cada 2 s por la red de fondo (`GET /api/cajas/{id}/recibidos?esperan=&atiende=`,
   y `POST` del mismo con `{"ids"}` ya guardados). Llega como un ticket en espera más, al final, con el nombre del celular y el
   ícono del celular en su ficha en lugar de la hora, y el aviso «Llegó un ticket de Celular de Luis: 4 artículos, $94.00.
   Retómalo arriba o con F5.». No toca el ticket que se atiende. Se retoma con F5 o con su ficha, y sobrevive a un reinicio
   como los demás.

**Decidido, por revisar:** lo que está en camino vive en memoria del servicio (unos segundos: el celular espera la respuesta y
la caja lo recoge cada 2 s); si el servicio se reinicia justo entonces, el celular dice que no lo recogió y se manda otra vez.
Manda cualquier celular dado de alta (el alta ya pidió el PIN del dueño); el permiso «Mandar tickets desde el celular» y el
nombre de quien lo armó (con su sesión) quedan para cuando haya más de un celular. Sin red, el QR del ticket (pendiente en la
app).

### Lo que el celular no llama nunca

Turnos (`/api/turnos…`), ventas y cobros (`/api/ventas…`), la bandeja (`/api/bandeja/…`), `folio-usado`, cortes, entradas y
salidas, impresiones. Si un día vende o cuenta, se decide entonces cómo.

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
