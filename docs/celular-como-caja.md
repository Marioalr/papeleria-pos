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
   - `serie`: de 1 a 3 letras, sin repetir la de otra caja (A y B ya son de las cajas). `CEL` sirve; si ya existe, 400
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
