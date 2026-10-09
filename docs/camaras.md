# Cámaras, puente y contador de personas

> **Pospuesto (2026-10-08)** con la multisucursal: cruzar las cámaras con el sistema por medio del central y un puente
> no se programa por ahora. Lo que sí sirve ya: [configurar los grabadores](#configurar-el-grabador) por seguridad, y
> con una sola sucursal, la [idea](ideas.md#cámaras-con-una-sola-sucursal) del texto POS y la foto del momento desde la
> caja principal. Más en [notas](notas.md).

> **Propuesta (2026-10-08).** El dueño quiere, ante un suceso, revisarlo de inmediato contra el sistema y
> las cámaras, también estando fuera ([central](servidor-remoto.md)). Datos de las hojas técnicas de Dahua y de sus
> avisos de seguridad, consultados el 2026-10-08. Lo marcado «(falta probar)» no tiene fuente oficial o depende del
> firmware: se prueba en la tienda antes de programarlo a fondo.

## Lo que hay

- **Sucursal 2:** kit Dahua XVR5104HS-4KL-I3 (grabador de 4 canales, línea WizSense) con 4 cámaras HAC-HFW1500CM-A
  (5 MP, con micrófono, sin inteligencia propia).
- **Matriz:** un kit parecido, modelo por confirmar. Si no es de la serie XVR5 con «-I3» (por ejemplo, un XVR1B04 o uno
  sin «-I»), no tiene texto POS ni líneas de cruce: cambiarlo por un XVR5104HS-4KL-I3 cuesta unos 1,739 MXN con IVA (o
  1,389 un XVR5104HS-I3/T; el XVR1B04-I/T, de 759, no sirve) y se reusa el disco. El modelo también se ve en el menú
  INFO → VERSION del grabador. El mismo modelo con hardware anterior a V3 trae las líneas de cruce en un solo canal.
- Hoy se ven con la app DMSS en el celular.
- **Lo que hace el grabador** (hoja técnica, hardware V3):
  - Detecta personas (SMD Plus) en los 4 canales.
  - Líneas de cruce y zonas (protección perimetral) en 4 canales, con 10 reglas por canal.
  - **Función POS:** el texto de cada operación se sobrepone y se graba en el video, y después se busca por ese texto
    (la búsqueda, falta probar).
  - Da la foto de una cámara y avisa de sus eventos (personas, cruces) a un equipo de la red local (falta probar: en
    algunos grabadores la foto no responde o sale chica, y el aviso de persona solo lo confirma la comunidad).
  - Toma la hora de un servidor de hora (NTP; falta probar que la tome del puente).
- **Lo que no hace:**
  - No cuenta personas: solo cuenta detecciones del día, que no son entradas.
  - Si se activa el reconocimiento de rostros, se apagan la detección de personas y las líneas de cruce.
  - Si se activa la mejora de grabación («Encode Enhancement»), se apagan las líneas de cruce y las zonas.
  - Si se le agregan cámaras IP, se apaga su inteligencia.
  - Su puerto de red es de 10/100: alcanza para fotos, eventos y ver en vivo.

## Cómo se cruza el sistema con las cámaras

1. **El texto en el video.** La caja manda al grabador una línea por suceso, por ejemplo «CANCELACION F-1234 $250.00
   CAJERO ANA». Queda escrita sobre la grabación de la cámara de esa caja y se busca en el grabador por folio o monto.
   Sin acentos hasta probar qué letras acepta. Opcional: una línea por cada venta.
2. **La foto y el clip del momento.** El [puente](#el-puente) pide la foto de la cámara de esa caja en el segundo del
   suceso y otra unos segundos después, y arma un clip de 30 s antes a 90 s después (guarda los últimos minutos de
   video en memoria). La foto va en alta calidad y el clip en baja (≈8 MB); la grabación completa queda en el
   grabador. Todo sube al central y queda 30 días.
3. **El aviso.** Llega al celular con la foto, el ticket, el cajero, el monto, la cámara y la hora del grabador, y
   botones: ver en vivo, ver el clip, ventas de la última hora y bloquear la caja. Si el puente no responde, el aviso
   llega igual, con la nota «sin foto».
4. **La misma hora en todo.** El puente es el reloj de la tienda: el grabador y las cajas toman la hora de ahí, y avisa
   si alguno se desfasa más de 2 s. Sin eso, la foto no es la del momento del suceso.

**Lo que no se ve:** si el cajón se abre con la llave y no desde el POS, el sistema no se entera.

## De noche

- El grabador detecta personas en todas las cámaras y el puente escucha esos eventos.
- La vigilancia se arma sola unos minutos después del último corte Z (los que diga el dueño) y se desarma al abrir
  turno, con un horario fijo de respaldo.
- Por cada persona detectada: foto y clip al central, y el aviso que escala (Telegram, alerta que suena aunque el
  celular esté en silencio, llamada). Máximo un aviso por minuto por cámara; las ráfagas se juntan. Si el central no
  contesta, el puente manda el aviso directo por Pushover.
- Si la tienda deja de reportar (sin luz o sin internet), el central avisa; mientras tanto no se ven las cámaras.
- Un no-break para el módem, el grabador, la fuente de las cámaras y el puente: sin luz no hay vigilancia.
- Si la detección de personas y las líneas de cruce no funcionan a la vez en la cámara de la puerta (falta probar),
  el grabador alterna por horario: líneas de día y detección de noche.

## Ver desde fuera

- **Por la red privada** (NetBird): el puente publica solo la dirección del grabador, y DMSS en el celular lo agrega
  por esa dirección privada (falta probar que DMSS lo permita sin cuenta de Dahua). Nunca se abren puertos del módem.
- **El P2P de DMSS** (la nube de Dahua) es lo que se usa hoy. Entre junio y julio de 2026, un ataque (CameraSwarm)
  entró a equipos Dahua por el puerto expuesto y, por el P2P, solo con el número de serie; el día más fuerte cayó en
  proveedores de internet de México. Propuesta: dejar el P2P solo mientras se prueba la red privada, y luego apagarlo
  (lo decide el dueño). Falta confirmar si los avisos de DMSS siguen llegando sin P2P; el aviso principal es el del
  POS.
- **Ancho de banda:** ver las 4 cámaras en baja calidad usa 1–2 Mbps de subida; una en alta, 1–4 Mbps.

## Contar personas

1. **Gratis, con lo que hay:** dos líneas de cruce con filtro de persona en la cámara de la puerta, una para entrar y
   otra para salir. El puente suma los cruces por hora y el central los compara con los tickets. Se mide dos semanas
   contra un conteo a mano: Dahua no publica su precisión, y un grupo que entra junto puede contar como uno. La cámara
   debe ver la puerta de frente o desde arriba.
2. **Si el error es alto:** una cámara Dahua que cuenta de verdad, sobre la puerta (DH-IPC-HDBW5442R-ASE, unos 4,640
   MXN con IVA). Va conectada a la red, no al grabador (al grabador le apagaría la inteligencia). Está descontinuada:
   pedirla con un firmware del 26 de marzo de 2026 o posterior, por dos fallas de seguridad. Otra opción: conteo con
   inteligencia artificial en el puente (Frigate; necesita una Raspberry Pi con acelerador, unos 6,700 MXN con IVA, o
   una laptop Intel reciente).
- **Descartados:** un sensor de distancia con ESP32 (falla con grupos, igual que las líneas; solo serviría para saber
  que alguien está en la puerta) y una cámara de conteo 3D (unos 18,000 MXN, precio de referencia de EE. UU., sin IVA).

**Reglas** (umbrales fijos al principio; contra lo normal de cada hora después de 4–8 semanas de historia):

- Entra gente y no hay tickets en X minutos.
- Hay personas después del corte Z.
- La caja principal no reporta y entra gente.
- El contador o el puente dejan de reportar.

## El puente

Un equipo pequeño siempre prendido en cada tienda, porque la caja principal se apaga de noche.

- **Raspberry Pi 5 de 2 GB** (kit con fuente, carcasa y microSD: 2,788 MXN con IVA, más envío), en el no-break del
  módem y del grabador. Arranca sola al volver la luz y gasta unos 3 W.
- **Qué hace:** escucha los eventos del grabador, saca la foto y el clip, es el reloj de la tienda, abre la red privada
  hacia el grabador, recibe el contador, reporta al central y a Healthchecks.io cada minuto, manda lo urgente por
  Pushover si el central no contesta y, si el BIOS lo permite, enciende la caja principal a distancia.
- **Nunca corre la venta:** si falla, se sigue vendiendo.
- Guarda la clave del grabador, con un usuario de solo ver y reproducir; con puente, la caja no la guarda.
- Con watchdog, parches automáticos y una microSD de repuesto con la imagen.
- En una emergencia, la laptop o la PC de la [prueba](prueba-sucursales-linux.md#las-máquinas) puede hacer de puente
  unos días, arrancando Linux desde una USB. No reemplaza a la Pi: casi ninguna enciende sola si se le agotó la
  batería o se fue la luz.

## Configurar el grabador

En una visita a cada sucursal, ya y sin costo:

- [ ] Anotar modelo, versión de hardware y firmware (etiqueta, o DMSS → el grabador → Device Details) y respaldar la
  configuración.
- [ ] Actualizar el firmware con el último para ese modelo exacto (si la etiqueta dice «/T», el de esa variante), por
  Maintenance Center o por USB en FAT32, sin cortar la luz mientras se actualiza. Anotar la fecha del firmware: hay
  fallas conocidas en firmwares de 2017 a 2021 y en los modelos «-I2» y «-X».
- [ ] Cuentas: borrar las desconocidas (sobre todo «p2pwn», la que dejó el ataque de 2026), poner una clave de
  administrador nueva, dejar el bloqueo por intentos, crear un usuario de solo ver y reproducir para el POS y otro
  para el DMSS del dueño.
- [ ] Red: dirección fija dentro de la red actual de la tienda, UPnP apagado en el grabador y en el módem, ningún puerto
  abierto y SSH apagado. (Al retomar la multisucursal: una red distinta en cada sucursal, por ejemplo 192.168.10.x y
  192.168.20.x.)
- [ ] Hora: NTP, horario de verano apagado y la zona de cada sucursal.
- [ ] Inteligencia en modo «IVS y SMD», nunca en «rostro»; «Encode Enhancement» apagado; detección de personas en
  todas las cámaras.
- [ ] Opcional (para el contador, pospuesto): las dos líneas de cruce en la cámara de la puerta.
- [ ] Función POS: una entrada por caja, con la cámara de esa caja.
- [ ] Grabación continua; anotar cuántos días guarda el disco.
- [ ] No agregar cámaras IP al grabador.
- [ ] Probar desde una PC de la tienda, con el usuario del POS: la foto y su tamaño, el video, los eventos de personas
  y de cruce, la búsqueda por texto POS, la hora y si la detección y las líneas funcionan juntas en la puerta.

## Privacidad

- Letrero con el aviso de privacidad simplificado y el aviso integral, también para los empleados (Ley de protección
  de datos personales de 2025). Las cámaras también graban audio.
- Fotos y clips de los sucesos: 30 días en el central. A Telegram y Pushover, que están fuera de México, solo va la
  foto reducida; hay que decirlo en el aviso.
- Conviene que lo revise un abogado.

## Preguntas

- [ ] Matriz: ¿qué grabador tiene? Foto de la etiqueta y captura de DMSS → Device Details. Sucursal 2: ¿qué versión de
  hardware dice la etiqueta (V3, «/T» u otra)?
- [ ] ¿Qué cámara ve cada caja, la puerta y el lugar del efectivo? ¿Cómo es la puerta: ancho, una o varias?
- [ ] ¿Quién tiene la clave de administrador del grabador y del módem? ¿Algún técnico abrió puertos?
- [ ] En DMSS, ¿el grabador está agregado por número de serie (P2P) o por dirección? ¿Lo comparte con alguien?
- [ ] ¿Cuántos días graba hoy y de qué tamaño es el disco?
- [ ] ¿El cajón se abre solo desde el POS o también con llave? ¿Qué impresora y qué cajón tienen?
- [ ] ¿Alguien se queda después del corte Z o llega antes de abrir turno (limpieza, velador)? ¿Cuánto tiempo?
- [ ] ¿Acepta apagar el P2P de DMSS cuando la red privada funcione?
- [ ] ¿Para qué quiere el conteo (visitas contra tickets, alarma o ambos) y qué error acepta?
