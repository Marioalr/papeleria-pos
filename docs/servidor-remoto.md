# Central y administración ante un suceso

> **Propuesta revisada el 2026-10-08** con las respuestas del dueño, en revisión. Él rechazó empezar sin central:
> necesita administrar ante un suceso a cualquier hora, con las cámaras y el contador, y precios por sucursal. Pide
> la mejor calidad-precio (cumplir todo al menor precio), aunque cueste más programarlo. Precios consultados ese día;
> tipo de cambio: 17.98 MXN por dólar (FIX de Banxico del 7 de octubre de 2026). El equipo lleva IVA; los servicios
> que se pagan en dólares, no, salvo donde se dice.

## Lo que pide el dueño

1. **Administrar ante un suceso estando fuera:** enterarse, revisarlo de inmediato contra el sistema y las cámaras (y
   el contador de personas), y actuar. Es la base del piloto automático ([ideas](ideas.md)).
2. **Precios por sucursal:** iguales el primer día; con seguridad se irán ajustando.
3. **Resistir cualquier flujo de mercancía:** dónde llega y cómo se reparte está por definir. No hay clientes con
   crédito compartidos.
4. **Calidad-precio:** cumplir los requisitos al menor precio posible; la dificultad de programarlo no es impedimento.

Y lo de siempre: cada sucursal vende sin internet, las cajas principales se apagan de noche, los horarios son
distintos y solo serán 2 sucursales durante al menos un año (una 3.ª, quizá en unos dos).

## La propuesta

```
Cada sucursal                                    Central: servidor virtual siempre prendido
├─ Caja principal: vende sin internet ──HTTPS──► sincronización, catálogo y precios por sucursal,
│  sube cada cambio; baja catálogo,              sucesos y avisos, acciones a distancia, panel,
│  precios, reglas y comandos                    un solo bot, WhatsApp, respaldo continuo
├─ Cajas: contingencia, como hoy
├─ Puente (Raspberry Pi) ──────────────HTTPS──►
│  cámaras, reloj de la tienda, contador
└─ Grabador Dahua y cámaras

Fuera: Healthchecks.io (vigila al central y a las tiendas), Backblaze B2 (respaldos), NetBird (red privada)
```

- **Cada sucursal vende sola:** su servidor sigue en la caja principal y ninguna venta pasa por el central.
- **El central** va en un servidor virtual en Ciudad de México (Vultr), con respaldo diario: **215.74 MXN al mes** el
  de 2 GB (12 USD, sin IVA ni CFDI), o 107.87 el de 1 GB si la [prueba](prueba-sucursales-linux.md) muestra que
  alcanza (se puede subir de plan, no bajar).
- **Sincronización hecha a la medida:** la caja principal sube cada cambio en segundos cuando hay internet, y baja
  catálogo, precios, reglas y comandos. La conexión siempre la abre la tienda: nada entra a la tienda desde fuera.
- **Un puente en cada tienda** para las cámaras, el reloj y el contador ([cámaras](camaras.md)).
- **Avisos que escalan:** Telegram con foto y botones; si no se ve, una alerta que suena aunque el celular esté en
  silencio (Pushover); a los 5 minutos, una llamada en español con opciones por teclado (Twilio), que entra aunque el
  celular no tenga datos; a los 10, al suplente. Healthchecks.io vigila desde fuera, aunque falle el central, y si el
  central no contesta, el puente manda lo urgente directo por Pushover.
- **Actuar a distancia:** bloquear lo sensible o toda la caja, corte X sorpresa, suspender a un usuario, modo estricto
  por unas horas (todo retiro, cancelación, devolución o descuento pide autorización), pedir un conteo, mandar un
  mensaje a la pantalla de la caja y encender la caja principal. Lo sensible pide PIN o huella. Cada acción vence sola
  si no llega a tiempo, y la caja confirma que la recibió. El bloqueo también aplica en contingencia.
- **Panel del dueño en internet** con llave de acceso (passkey, resiste el robo de contraseñas) en un dominio propio.
  No depende de la red privada.
- **Red privada** (NetBird, gratis) solo para ver las cámaras en vivo y para soporte. Si se cae, siguen los avisos, el
  panel y las acciones.
- **En el celular del dueño:** Telegram, Pushover (con las alertas críticas encendidas), NetBird conectada para ver en
  vivo, DMSS y el navegador para el panel. La llamada no necesita app.

### Cómo se cumple cada respuesta

| Respuesta | Cómo |
|---|---|
| 1. Administrar ante un suceso | **Detectar:** reglas en la sucursal, que funcionan sin internet, y reglas en el central (horario, noche, presencia, contador). **Ver:** cada suceso llega con el ticket, lo que pasó en esa caja 10 minutos antes y después, el conteo de personas, la foto de esa caja en ese segundo y su clip ([cámaras](camaras.md)). **Actuar:** las acciones a distancia. **Escalar:** Telegram, alerta que suena aunque el celular esté en silencio, llamada y suplente. Si la caja principal deja de reportar en horario, el central avisa en minutos y dice si fue la PC o la luz y el internet |
| 2. Precios por sucursal | El cobro ya usa el precio de la sucursal y, si no hay, el base, con conexión y sin ella. El día 1 todo queda en el base. En F3, un interruptor por producto: «igual en todas» o «distinto», y un permiso aparte para «precio de mi sucursal». Un cambio hecho sin internet se aplica al momento y sube como propuesta: si nadie tocó ese precio entra; si alguien lo tocó, gana el de mayor rango, nunca por la hora del reloj, y al otro se le avisa |
| 3. Cualquier flujo de mercancía | El kardex dice dónde está cada pieza. Cualquier sucursal compra y recibe; la compra guarda quién paga y quién recibe, y el retiro con que se pagó se liga a ella aunque la reciba la otra sucursal. La orden lleva el destino de cada línea, así que el proveedor puede entregar directo en cada sucursal. En una compra repartida, quien recibe registra todo al costo de la nota y la parte de la otra sucursal queda «de paso» (no se vende) hasta que sale como traspaso, al mismo costo. Traspaso con estados (borrador → enviado → recibido a ciegas → cerrado) y un QR. Un almacén futuro es una sucursal sin cajas. El crédito es de cada sucursal y sube al central solo para verlo |
| 4. Calidad-precio | Cada pieza cubre un requisito y se eligió la más barata que lo cumple ([opciones](#opciones-comparadas)) |

## Opciones comparadas

| Pieza | Elegida | Costo | Lo que se descartó y por qué |
|---|---|---|---|
| Central | Vultr Ciudad de México con respaldo diario | 2 GB: 215.74 MXN al mes; 1 GB: 107.87 (en dólares, sin IVA ni CFDI) | **Por medir:** 1 GB. La estimación deja 110–500 MB libres durante un suceso; la prueba decide. **Alternativa:** Lightsail de 2 GB facturado por AWS México, ≈315–325 con IVA y CFDI, si el contador dice que la factura lo compensa. **Descartados:** un equipo en la casa del dueño (depende de su luz e internet); Oracle gratis (recupera las máquinas que casi no usan procesador); todas las cajas trabajando contra el central (sin internet no cancelan, no devuelven ni cobran impresiones); sin central (no cumple la respuesta 1) |
| Red privada | NetBird gratis (5 personas, 100 equipos; sus términos admiten un negocio) | 0 | **Plan B:** Tailscale para negocio con 1 usuario, 143.82 MXN al mes (en dólares, sin IVA), si NetBird falla la prueba o retira su plan gratis. **Descartados:** Tailscale y ZeroTier gratis (solo uso no comercial); Headscale en el central (si cae el central se pierde también el acceso a las cámaras); dejar el P2P de DMSS para siempre ([ataque de 2026](camaras.md#ver-desde-fuera)) |
| Puente | Raspberry Pi 5 de 2 GB en cada tienda (kit con fuente, carcasa y microSD) | 2,788 MXN con IVA cada uno, más envío; 9–11 al mes de luz | La laptop o la PC del dueño, solo de emergencia: casi ninguna enciende sola si se le agotó la batería. **Descartados:** mini PC N150 (5,100–7,600 con IVA, sin hacer nada más) y router GL.iNet (no guarda video) |
| Contador | Líneas de cruce del grabador | 0 | Si el error es alto: una cámara Dahua que cuenta de verdad sobre la puerta (≈4,640 con IVA; descontinuada) o Frigate (≈6,700 con IVA). **Descartados:** sensor ESP32 (falla con grupos, igual que las líneas), cámara 3D y contador comercial (12,000–19,000 por entrada, sin IVA) ([cámaras](camaras.md#contar-personas)) |
| Avisos | Telegram, Pushover, Twilio y Healthchecks.io gratis | Pushover 99–105 MXN una vez, con IVA; Twilio 37–55 al mes, con IVA | **Descartado:** Healthchecks de pago para llamar (359.56 al mes, sin IVA; llama en inglés y no actúa sobre el POS) |
| Respaldo | Backblaze B2 con llaves que no pueden borrar y bloqueo de 14–30 días | 0 hasta 10 GB | Cloudflare R2 (pide tarjeta y cobra escrituras) |
| Dominio | `.com` en Cloudflare | 188 MXN si se registra antes del 31 de octubre de 2026; 201 al año después (en dólares) | Hace falta: las llaves de acceso no funcionan con una dirección IP. **Alternativa en pesos:** `.com.mx` en Akky, 249 con IVA el primer año, sin prisa |

## Costo

| Concepto | Una vez | Al mes |
|---|---|---|
| Central (Vultr 2 GB con respaldo) | 0 | 215.74 (en dólares) |
| Dominio `.com` | — | ≈16 (188–201 al año) |
| 2 puentes Raspberry Pi 5 de 2 GB y una microSD de repuesto | 5,892 con IVA, más envío | 18–22 de luz |
| Pushover en el celular del dueño | 99–105 | 0 |
| Twilio: llamada que entra aunque el celular no tenga datos, también al suplente | saldo prepagado, que se gasta en las llamadas | 37–55 con IVA (10 a 25 llamadas de 1 minuto; ≈86 si duran 2; ≈12–31 si sale con el teléfono fijo de la tienda) |
| NetBird, Healthchecks.io, Backblaze B2, Telegram y el software | 0 | 0 |
| Contador | 0 si sirven las líneas del grabador | 0 |
| No-break de 800–1000 VA, solo si no hay (2 × 1,009–1,799, con IVA; dato previo sin reverificar) | 2,020–3,600 | 0 |
| Grabador de la matriz, solo si es básico (XVR5104HS-4KL-I3, se reusa el disco) | ≈1,739 con IVA | 0 |
| Opcional: internet de respaldo por 4G, por sucursal | 720–940 | ≈200 |

- **Al mes, ya instalado:** ≈287–309 MXN.
- **Primer año:** ≈9,400–9,700 MXN, o ≈11,400–13,300 si hay que comprar los dos no-break.
- **A 3 años:** ≈16,300–17,100 MXN, más los no-break si hacen falta.
- **Si alcanza el central de 1 GB:** ≈1,300 menos al año.
- **La 3.ª sucursal** suma su puente y su no-break (≈3,800–4,600 MXN) y ≈10 al mes de luz; su grabador debe ser de la
  serie XVR5 con «-I3» (si no, ≈1,739 más).

## Entregas

| Cuándo | Qué |
|---|---|
| **Ya** (fase 3; solo el dominio cuesta) | [Configurar los grabadores](camaras.md#configurar-el-grabador). Abrir a nombre del negocio, con verificación en dos pasos, Backblaze, Healthchecks.io, NetBird, Cloudflare y Vultr. Registrar el dominio (`.com` antes del 31 de octubre si acepta pagar en dólares; si no, `.com.mx` sin prisa). Etapa 0 de la [prueba](prueba-sucursales-linux.md) |
| **Arreglos previos** (fase 3, chico) | Cerrar un hueco de seguridad: hoy el servicio deja pasar sin llave lo que llega de la misma PC. Identidad fija de cada sucursal. Quitar lo que da por hecho una sola sucursal. Registrar lo que se quita del catálogo (hoy las presentaciones, los códigos y las ligas con proveedores se borran sin rastro; solo los precios quedan en su historial). Autor, sucursal de origen y versión en el historial de precios. Prefijo de sucursal en los códigos internos. Días de descanso. Los arreglos para Linux |
| **Fase 5** (piloto en la matriz) | Respaldo cifrado en B2, aviso de apertura en Healthchecks.io, red privada para soporte. El puente de la matriz con lo básico: ver la matriz en vivo sin abrir puertos ni usar el P2P |
| **6.1** Central, solo de subida (grande) | **Lo que gana el dueño:** ve en el panel, casi en vivo, las ventas, los cortes y las existencias de las dos sucursales, y un cuadre diario avisa si algo no llegó. **Por dentro:** llave por sucursal, bandeja de subida, réplica sin duplicados, cuarentena de lo que no cuadra, versión del protocolo y respaldo continuo. Al cerrarla: unas horas en un servidor por hora, se contrata el central y entra a trabajar solo con la matriz |
| **6.2** Bajada y precios (grande) | Catálogo maestro, precios por sucursal en F3, propuestas sin internet, el arreglo del ajuste de margen en compras (hoy mueve el precio base) y el alta de la sucursal 2 desde el central con los mismos Id |
| **6.3** Sucesos, avisos y acciones (mediano) | Un solo bot en el central, presencia de cada sucursal, estados del suceso, Pushover (también directo desde el puente si el central no contesta), llamada con suplente, acciones a distancia con vencimiento y confirmación, bloqueo que aplica en contingencia, PIN o huella, diagnóstico dentro del aviso |
| **6.4** Mercancía (grande) | Sucursal o almacén, traspaso con estados y QR, orden con destino por línea, compra en cualquier sucursal con quién paga separado de quién recibe, compra repartida «de paso» con salida al costo de la nota, y devolución de una venta de otra sucursal |
| **6.5** Cámaras y contador (mediano) | La caja manda el texto de cada suceso al grabador. El servicio del puente: eventos del grabador, foto y clip, reloj de la tienda, vigilancia nocturna y conteo. Simulacro de noche con datos móviles: cámara y panel en menos de 15 s |
| **6.6** Ensayo largo (2–4 semanas) | Fallas provocadas, restauraciones y medir la memoria y el disco del central |
| **Fase 7** (sucursal 2) | Llega con su puente, su no-break, sus avisos y su horario; precios iguales el día 1. **Mínimo para pasarla al POS:** de 6.1 a 6.5 y la etapa 2 de la [prueba](prueba-sucursales-linux.md) (dos sucursales con central). Si abre antes, abre con eleventa: su puente solo sirve para ver en vivo sin P2P; no hay avisos de sucesos ni cruce con el sistema |
| **Fase 9** (piloto automático) | Modo ausente con sus reglas, que funcionan sin internet; límites de aprobación; comparación contra lo normal de cada hora; cada decisión queda en la bitácora como «piloto». La inteligencia artificial, si se usa, solo resume: nunca aprueba ni bloquea |

La **API de WhatsApp** ya no espera a toda la fase 6: va cuando el dueño la quiera, después de 6.2 (cuando ya hay
bajada), con el webhook en el central ([WhatsApp](whatsapp.md)).

## Si algo falla

| Falla | Qué sigue funcionando |
|---|---|
| El central | La venta y las cámaras en vivo por la red privada; Healthchecks.io avisa, y el puente manda directo por Pushover lo urgente (persona de noche, retiro grande). Mientras vuelve se pierden el panel, la sincronización, los avisos por Telegram y las acciones a distancia; en la caja queda el PIN |
| La red privada | Los avisos con foto, el panel y las acciones; no se ve en vivo |
| El puente | La venta; el central avisa. De día, los sucesos llegan sin foto; de noche, no hay vigilancia ni contador hasta que vuelva |
| El internet de una tienda | La venta; el central avisa. Mientras dure, no se ven las cámaras ni llegan los sucesos (por ejemplo, si de noche cortan el cable); suben al volver. Por eso el internet de respaldo por 4G es opcional |
| Telegram | La alerta que suena aunque el celular esté en silencio, y la llamada |

## Respaldos

1. **Local:** la copia diaria y la del corte Z en cada caja principal, como hoy.
2. **El central:** la réplica casi en vivo de las dos sucursales.
3. **Fuera:** la copia cifrada de cada sucursal en Backblaze B2, con una llave que no puede borrar y bloqueo de 14–30
   días.
4. **El central mismo:** copia continua (Litestream) a B2 y el respaldo diario de Vultr.

**Ensayos:** una restauración automática cada semana, una completa cada mes en la máquina con Linux de la
[prueba](prueba-sucursales-linux.md#las-máquinas) y reconstruir una caja principal cada 3 meses.

## Cómo se arma el central

```
Servidor virtual · Ubuntu 26.04 LTS · 1 núcleo · 1 o 2 GB
│
├─ Cortafuegos y parches automáticos; acceso de soporte solo por la red privada
├─ Caddy: HTTPS; a internet solo la sincronización, los puentes, el panel, la app de Telegram, la llamada
│  y, cuando llegue, el webhook de WhatsApp; todo con llave, llave de acceso o firma
├─ Servicio POS en modo central (.NET 10): réplica, catálogo, sucesos, acciones, bot, panel
│  ├─ central.db (SQLite)
│  └─ fotos y clips de los sucesos, 30 días, como archivos aparte
├─ Litestream: copia continua de central.db a Backblaze B2 (sin las fotos ni los clips)
Fuera: Healthchecks.io avisa si el central deja de reportarse
```

- **Instalación con un script** que queda en el repositorio. El servidor es desechable: si muere, se crea otro, se corre
  el script y se restaura la base en 15–30 minutos.
- **Memoria:** la prueba mide si basta 1 GB; con 2 GB, debe quedar bajo 1.4 GB sostenidos (si no, el plan de 4 GB,
  431.47 MXN al mes con respaldo, sin IVA).
- **Disco:** los clips van en calidad baja (≈8 MB cada uno) y la foto en alta. Con hasta unos 100 sucesos al día, 30
  días ocupan ≈24 GB: caben en los 55 GB del plan de 2 GB; con 1 GB (25 GB de disco), menos días.
- **Antes de Caddy,** el servicio deja de confiar en lo que llega de la misma PC.

## Riesgos

- **Mucho código:** sincronización, acciones, mercancía y puente son lo más pesado del plan. Defensas: entregas en
  orden, pruebas de dos sucursales, un simulador con fallas y el cuadre diario. La sucursal 2 puede abrir con eleventa
  si el código no llega a tiempo.
- **Precios mal cobrados al separar un producto:** hoy el precio propio de una presentación le gana a la escala de la
  sucursal, y el ajuste de margen en compras mueve el precio base. Deben quedar cubiertos por pruebas antes de 6.2.
- **El grabador:** falta probar en sitio la foto, los eventos de personas, los permisos del usuario limitado y la hora
  ([cámaras](camaras.md)).
- **Servicios gratis:** NetBird puede retirar su plan con 14 días de aviso y parar hasta 2 horas por semana por
  mantenimiento, que en México puede caer entre las 18:00 y las 19:00, con la tienda abierta: en ese rato quizá no se
  vea en vivo, pero los avisos con foto sí llegan; el simulacro decide y Tailscale es el plan B. Healthchecks.io
  gratis borra la cuenta tras un año sin entrar: soporte entra cada pocos meses.
- **La hora:** sin el reloj de la tienda, la foto no coincide con el suceso.
- **Demasiados avisos:** si suena todo, el dueño deja de hacer caso. Defensas: tres niveles, agrupar los repetidos,
  umbrales fijos el primer mes y, de noche, solo con persona detectada.
- **El celular del dueño:** la alerta que suena en silencio se activa a mano (en iPhone) o con una excepción de «No
  molestar» (en Android); el número de la llamada se guarda como contacto para que no parezca spam.
- **Pagos en dólares:** Vultr y el dominio, sin factura mexicana; Twilio sí da CFDI desde marzo de 2026 si se capturan
  bien el RFC y los datos fiscales (cobra IVA e IEPS). La deducción con factura extranjera la confirma el contador.
- **Telegram** guarda los mensajes sin leer solo 24 horas: si el central pasa más de un día caído, se pierden las
  respuestas de ese tiempo.
- **Privacidad:** fotos y clips son datos personales ([cámaras](camaras.md#privacidad)).

## Preguntas

- [ ] ¿Cuándo abre la sucursal 2? ¿Abre con eleventa o directo con el POS? ¿Cuántas cajas tendrá?
- [ ] ¿Qué horario y qué días de descanso tiene cada sucursal? ¿En qué estado y municipio está cada una (zona
  horaria)?
- [ ] Internet de cada sucursal: ¿qué compañía, fibra o cobre, qué velocidad de subida? ¿El módem queda prendido de
  noche? ¿Hay no-break? ¿Quiere internet de respaldo por 4G para que la vigilancia de noche no dependa del cable?
- [ ] ¿Hay teléfono fijo en la tienda? Si lo hay, la llamada de aviso sale con ese número: más barata y no parece
  spam.
- [ ] ¿Su celular es iPhone o Android? ¿Acepta instalar Pushover y NetBird y dejar NetBird conectada?
- [ ] Si usted no contesta en 10 minutos, ¿a quién llamamos? ¿Esa persona solo se entera, o también puede bloquear o
  aprobar?
- [ ] ¿Desde qué nivel de suceso lo despertamos de noche? ¿Desde qué monto es un retiro grande y una diferencia en
  corte?
- [ ] Bloquear una caja a distancia: ¿detiene toda venta o solo lo sensible (retiros, cancelaciones, devoluciones y
  descuentos)?
- [ ] Precios: ¿quién cambia los de cada sucursal, solo usted o también el encargado? ¿Se pueden cambiar sin
  internet? ¿Las sucursales pueden dar de alta productos?
- [ ] Mercancía: ¿dónde llegan hoy las compras? ¿Habrá entrega directa a la sucursal 2? En una compra repartida, ¿el
  reparto se decide al pedir o al recibir? ¿Quién paga? En un traspaso con faltante, ¿quién responde?
- [ ] ¿Se puede devolver en una sucursal lo comprado en la otra? ¿El descuento de un cliente registrado vale en las
  dos?
- [ ] Pagos: ¿en dólares sin factura (Vultr) o en pesos con CFDI (Lightsail, ≈315–325 con IVA)? Pregúntele a su
  contador por la deducción de servicios del extranjero.
- [ ] ¿Acepta comprar 2 Raspberry Pi y, si hace falta, 2 no-break? ¿Y cambiar el grabador de la matriz si es básico
  (≈1,739)?
- [ ] ¿Acepta guardar 30 días de fotos y clips en el central? ¿Ya tiene aviso de privacidad y letreros?
- [ ] ¿Quién más necesita el panel o las cámaras (encargado, contador, soporte)? En NetBird gratis caben 5 personas.
- [ ] ¿Quiere la API de WhatsApp el primer año? ¿La sucursal 2 tendrá un número nuevo?
- [ ] ¿Qué tan segura es la 3.ª sucursal y para cuándo?
