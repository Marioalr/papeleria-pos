# Prueba de varias sucursales con un servidor Linux

> Propuesta, en lluvia de ideas: puede cambiar. Antes de contratar el [servidor virtual](servidor-remoto.md), el
> central se prueba en una máquina que ya hay, con Linux, para ver cómo responde con poco hardware y llegar a la fase 6
> sin sorpresas.

## Para qué

- **El central correrá en Linux** (Ubuntu, en el servidor virtual). Hoy el servicio solo se ha usado en Linux para
  programar: nunca como servidor de red, ni como servicio del sistema, ni prendido días seguidos.
- **Ver cómo responde con poco hardware.** Si el procesador no es muy viejo ([ver abajo](#las-máquinas)), por núcleo
  estas máquinas rinden algo menos que el servidor virtual: si el servicio responde bien ahí, allá irá igual o mejor.
- **Encontrar a tiempo lo que solo funciona en Windows**, para corregirlo antes de la fase 6.
- **Juntar datos para las [preguntas del central](servidor-remoto.md#preguntas):** servidor virtual o mini PC, y de
  qué tamaño.

## Las máquinas

| | Laptop | PC |
|---|---|---|
| Procesador | Intel Celeron (modelo por confirmar) | AMD A6 (modelo por confirmar) |
| Memoria | 8 GB | 8 GB |
| Si se va la luz | Su batería hace de no-break; si se acaba, casi ninguna enciende sola al regresar la luz | Se apaga de golpe: necesita no-break y, en el BIOS, encender sola al regresar la luz |
| Luz que gasta (aprox.) | 6–12 W | 25–45 W |
| Disco que suele traer | Memoria soldada (eMMC) de 32–64 GB, o un disco que se puede cambiar por un SSD | Disco duro mecánico: conviene cambiarlo por un SSD (barato) |
| Red | A veces solo Wi-Fi: un adaptador USB a cable | Cable |
| Ruido | Casi ninguno | Ventiladores de procesador y fuente |

**Primero, el modelo exacto.** En Windows: Configuración → Sistema → Acerca de (o «Información») dice el procesador, y
Administrador de tareas → Rendimiento → Disco dice si es SSD o HDD. Para confirmarlo, o si ya no tiene Windows, se
arranca con una USB de Ubuntu, sin instalar nada:

```
LC_ALL=C lscpu | grep 'Model name'
/lib64/ld-linux-x86-64.so.2 --help | grep supported
lsblk -d -e 7 -o NAME,ROTA,TRAN,SIZE,MODEL
```

- **Que corra .NET.** El proyecto usa .NET 10, con soporte hasta noviembre de 2028, y corre en cualquier procesador de
  64 bits. Desde .NET 11 se pide el nivel **x86-64-v2**: si la segunda línea no lo marca como `supported` (un A6-3xxx
  «Llano» o un Celeron viejo de las series T3xxx, 900, E3xxx, P4xxx o U3xxx), la máquina sirve para la prueba, pero no
  para dejarla después como central de repuesto o banco de pruebas: las versiones nuevas de .NET ya no arrancan ahí.
- **Cuánto rinde** (por núcleo, según PassMark). Un Celeron N4000, N4020, N4120, N4500 o N5100 rinde unos dos tercios
  de un núcleo del servidor virtual; un A6-5400K, 6400K, 7400K o 9500, parecido o algo menos. Un Celeron N28xx (Bay
  Trail) rinde menos de la mitad: sus resultados saldrían demasiado pesimistas.
- **El disco.** En la tercera línea, `ROTA` 1 es un disco duro mecánico, `mmcblk` es la memoria soldada y `ROTA` 0
  con `sata` o `nvme` es un SSD. La línea con `usb` es la propia USB de Ubuntu: no cuenta. SQLite escribe poco y
  seguido: en un disco mecánico se nota.

**Cuál usar**, en este orden: que llegue a x86-64-v2, que rinda como un N4000 o mejor, que tenga SSD (o se le pueda
poner), que vaya por cable y que aguante un apagón. Si el Celeron es un N4000 o posterior, la laptop: casi no gasta
luz, no hace ruido y trae su propio no-break (hay que configurar Ubuntu para que no se suspenda al cerrar la tapa). Si
no, o si su batería ya no sirve, la PC con un SSD y un no-break.

**Parecerse al servidor virtual.** El servidor virtual tiene 1 núcleo y 1 GB, que comparte con Ubuntu; estas
máquinas, 2 o más núcleos y 8 GB. Por eso cada medición se hace dos veces: sin límites y con el servicio limitado a lo
que tendría en el servidor virtual: 1 núcleo y unos 800 MB (Ubuntu usa el resto). No se imitan el disco ni la
distancia hasta Ciudad de México: por eso, antes de contratar, el mismo script corre unas horas en un servidor virtual
de verdad, que se cobra por hora (0.007 USD).

## Etapas

La sincronización y el modo central llegan hasta la fase 6; antes se prueba lo que ya existe.

| # | Etapa | Cuándo | Qué necesita |
|---|---|---|---|
| 1 | **La máquina y el servicio de hoy** | En paralelo a la fase 3, en cuanto esté elegida la máquina | Los [arreglos para Linux](#lo-que-hay-que-arreglar-o-construir) y el simulador de cajas |
| 2 | **Dos sucursales en una sola base** | Después de la etapa 1, sin esperar a la fase 6 | Un comando para dar de alta la sucursal 2 y un bot de Telegram de prueba |
| 3 | **El central de verdad** | En la fase 6, en cuanto existan el modo central y la sincronización | El script del central |
| 4 | **Ensayo largo** | Al cerrar la fase 6, antes de contratar el central y de que la sucursal 2 entre al POS | La etapa 3 aprobada |

Las cajas simuladas corren en la PC de desarrollo, no en la máquina de prueba, para no quitarle procesador.

### 1. La máquina y el servicio de hoy

Ubuntu Server 26.04 LTS, la misma versión que llevaría el servidor virtual (trae .NET 10 en sus repositorios). El
servicio de la sucursal queda como servicio del sistema: arranca con la máquina sin que nadie inicie sesión, vuelve
solo si se cae y corre con un usuario sin privilegios. La base es la ficticia, con un año de ventas.

- **Día pico comprimido:** los ~120 tickets de un día pico en una hora, en 3 cajas simuladas, con búsquedas, cobro
  mixto, cortes X y Z (cada corte Z con su respaldo).
- **El techo:** subir la carga hasta que escanear tarde más de lo que marca la [meta](#qué-se-mide-y-cuándo-sale-bien).
- **Cambiar precios a media venta:** cada cambio hace que todas las cajas vuelvan a bajar el catálogo (~1 MB). No
  debe notarse al escanear.
- **Matar el servicio a media venta:** las cajas siguen en contingencia y suben su bandeja al volver, sin duplicar.
- **Desconectarla de la luz** a media venta y durante un respaldo: la base queda íntegra y el servicio vuelve solo.
- **72 horas prendida,** con un día de ventas cada día, para ver si la memoria crece.
- **Una caja con Windows de verdad,** si hay una PC a la mano con la caja normal (no la de la
  [prueba con una persona real](plan.md#prueba-con-una-persona-real), que trae su propio servicio): encuentra la
  máquina con «Buscar en la red», se da de alta con el PIN del dueño y se mide cuánto tarda en aparecer lo escaneado.

### 2. Dos sucursales en una sola base

Todavía no hay sincronización, pero el modelo ya lleva la sucursal en casi todo (ventas, turnos, existencias, kardex,
folios, precios y reglas) y un servicio ya atiende cajas de varias sucursales en una sola base. Probarlo adelanta
errores de la fase 7:

- La sucursal 2 con sus cajas, su serie de folios y su configuración.
- Cajas de las dos sucursales vendiendo a la vez: folios, existencias, cortes y bitácora sin mezclarse, y el resumen
  de Telegram por sucursal.
- Lo que hoy da por hecho que solo hay una sucursal (por ejemplo, al preparar la caja y en la depuración) se anota
  para corregirlo.
- La sucursal 2 desde la PC de desarrollo con internet de por medio (por Tailscale, o con retraso simulado en la red):
  cómo se siente la caja y cuándo entra en contingencia.

No es la arquitectura final (un servicio por sucursal y el central que sincroniza): prueba el modelo y la máquina con
dos sucursales, todavía sin central.

### 3. El central de verdad

En la fase 6. La máquina se reinstala desde cero y se corre el **script del central**, el mismo que quedará para el
servidor virtual: Ubuntu, parches automáticos, cortafuegos, memoria de intercambio, Tailscale, Caddy, el servicio en
modo central y Litestream. Dos sucursales, cada una en una PC con Windows: la sucursal 1 con una copia del respaldo de
la matriz si el dueño lo permite (si no, otra base ficticia; nunca la matriz en uso), y la sucursal 2 (puede ser la
máquina que no se eligió) con la base ficticia, otra serie de folios y otro horario.

- **Catálogo que baja:** un cambio de precio en el panel llega a las dos; el precio de una sola sucursal, solo a esa.
- **Ventas que suben una sola vez:** por sucursal y por día, los tickets, los totales y los cortes cuadran entre la
  sucursal y el central.
- **Lo del dueño:** el panel por Tailscale; Telegram en el central (la autorización pedida en la sucursal 2 regresa
  solo a ella); el aviso de una sucursal que no abrió a su hora; la cola de entrega con una sucursal apagada.
- **Respaldo:** Litestream, y restaurar en una máquina limpia con el script.
- **Vigilancia:** Healthchecks.io avisa si el central deja de reportarse.
- **De preferencia, la máquina en casa del dueño** y las sucursales en la tienda: la sincronización pasa por internet
  de verdad y, de paso, se miden los riesgos del mini PC en casa.

### 4. Ensayo largo

Dos a cuatro semanas con las dos sucursales vendiendo en sus horarios y apagándose de noche. Una o dos
[fallas provocadas](#fallas-a-provocar) por semana, los parches de Ubuntu con su reinicio, una actualización del POS
con regreso a la versión anterior y, al final, una restauración con semanas de datos. Una bitácora de la prueba:
cada caída, cuánto duró, si se recuperó sola y qué hubo que hacer. Al terminar, el script corre unas horas en un
servidor virtual de verdad para confirmar el tamaño del plan.

## Fallas a provocar

| Falla | Etapas | Debe pasar |
|---|---|---|
| El servicio se cae o se desconecta el cable | 1, 2 | Si se cae, el sistema lo levanta en segundos. En los dos casos, las cajas venden en contingencia y suben su bandeja sin duplicar |
| Se va la luz de golpe, unas 10 veces: a media venta, a media sincronización, durante un respaldo | 1, 3, 4 | La base queda íntegra y todo arranca solo al regresar la luz |
| Disco lleno | 1, 4 | La base no se daña; el respaldo falla con aviso; si una venta no se puede guardar, la caja lo dice claro y no la da por hecha; al liberar espacio todo sigue |
| Central apagado una hora y una noche | 3, 4 | Las sucursales venden igual; llega el aviso; al volver se sincroniza solo |
| Sin internet en una sucursal, una hora y un día | 3, 4 | Vende y hace sus cortes; al volver sube todo y recibe los precios pendientes |
| Sucursal apagada tres días | 3, 4 | Cada día llega el aviso de que no abrió; al encender recibe los cambios y la cola, en orden |
| Reinicio a media sincronización, del central o de la sucursal | 3, 4 | Reintenta solo, sin duplicar |
| Internet lento o que pierde paquetes (simulado) | 3 | Termina más lento, sin duplicar |
| Relojes desfasados 10 minutos, una hora y un día | 2, 3, 4 | Las ventas quedan en su día; los avisos de horario no se disparan de más |
| Memoria al límite | 3 | El sistema vuelve a levantar el servicio y las sucursales reintentan solas |

## Qué se mide y cuándo sale bien

Los tiempos son los de 19 de cada 20 veces: la vez más lenta de cada 20 no cuenta.

| Qué | Meta |
|---|---|
| Escanear un producto | En la caja de Windows, que aparezca en menos de 100 ms (la [meta de la caja](caja-y-control.md)); en el simulador, que el servicio conteste en menos de 50 ms, para dejar margen a la pantalla. En el día pico y mientras corre un respaldo |
| Buscar por nombre | Menos de 300 ms |
| Cobrar | Menos de 500 ms |
| Techo | Al menos 300 tickets por hora: unas 10 veces la hora más cargada de un día pico (se calcula en unos 30 de sus ~120 tickets; el dato real sale del historial de eleventa) |
| Memoria del servicio | Menos de 300 MB y sin crecer de un día a otro (en la PC de desarrollo usa ~190–230 MB) |
| Memoria del central, con lo que tendría en el plan de 1 GB | Menos de 600 MB y casi sin memoria de intercambio; si no, el plan de 2 GB |
| Arranque | Contesta menos de 2 minutos después de prender la máquina; si se cae, vuelve en menos de 10 s |
| Cuadre | Ninguna venta perdida ni duplicada: tickets, cortes y existencias cuadran (y entre sucursal y central) |
| Sincronización | Un cambio de precio llega en menos de 2 minutos; tras 8 horas sin conexión se pone al día en menos de 2, tras 3 días en menos de 5 |
| Avisos | Sucursal sin abrir: menos de 15 minutos después de su hora. Central caído: menos de 10 minutos |
| Restaurar | De Ubuntu recién instalado al central con sus datos en menos de 30 minutos, [lo previsto](servidor-remoto.md#qué-hay-que-instalar) |
| También se anota | Procesador, disco (escrituras pequeñas), tamaño y crecimiento de la base, tráfico por sucursal, temperatura y luz que gasta |

## Lo que hay que arreglar o construir

**Antes de la etapa 1**, en cambios chicos, uno por tema:

- **Dónde guarda sus datos.** Sin configurar, la base, los respaldos y el token de Telegram van en la carpeta común de
  datos, que en Linux es `/usr/share`, donde no se puede escribir: el servicio no arranca. En Linux van en
  `/var/lib/papeleriapos`.
- **El logo del ticket.** La biblioteca que procesa el logo necesita su versión para Linux. Sin ella falla
  Configuración → Ticket; con ella también pasan las 8 pruebas automáticas que hoy fallan en Linux.
- **Instalarlo en Linux:** el servicio publicado para Linux y un script que crea el usuario sin privilegios, el
  servicio del sistema y el cortafuegos (solo la red local). Es el principio del script del central.
- **Pruebas en Linux** en GitHub Actions, para que lo que se rompa en Linux se vea en cada cambio.
- **Simulador de cajas:** da de alta N cajas con el PIN del dueño y vende por la API como la caja, con su contingencia
  (si el servicio no contesta, guarda las ventas y las sube al volver). Anota cuánto tarda cada llamada. Es una
  herramienta de prueba, no parte del POS.

**Para la etapa 2:**

- Un comando para dar de alta una sucursal con su configuración y sus cajas (hoy no hay cómo).
- Un bot de Telegram solo para la prueba: Telegram admite un solo receptor por bot, y el de la tienda no se toca.

**Para la etapa 3,** dentro de la fase 6: **nada sin llave detrás de Caddy.** Hoy el servicio deja pasar sin llave lo
que llega de la misma PC. Con Caddy (o cualquier intermediario) en la misma máquina, todo parecería venir de ahí: el
servicio tiene que dejar de confiar en eso antes de poner Caddy.

## Fuera de la prueba

- **WhatsApp:** su webhook necesita una dirección pública con HTTPS; se prueba en un servidor virtual.
- **Abrir puertos** de la casa o la tienda a internet: todo pasa por Tailscale (su costo para un negocio está en las
  [preguntas del central](servidor-remoto.md#preguntas)).
- **Lo de Windows:** la caja, el agente de impresión y el instalador siguen en Windows.
- **El servidor de cada sucursal** sigue en su caja principal: la prueba solo junta datos, no cambia esa decisión.
- **Datos reales:** nunca en el repositorio, y solo si el dueño lo permite. La copia de la matriz vive en las máquinas
  de la prueba y en su respaldo, y se borra al terminar.

## Qué decide

- **Servidor virtual o mini PC:** con la máquina en casa, la etapa 4 mide justo los riesgos del mini PC (la luz y el
  internet de la casa, alguien que la desconecta).
- **Tamaño del plan:** 1 GB (5 USD al mes) o 2 GB (10 USD al mes).
- **Si se eligiera el mini PC,** si conviene Linux con el mismo script en lugar de Windows 11.
- **Qué hacer con la máquina después:** central de repuesto (se restaura con el script si el servidor virtual falla o
  suspenden la cuenta, el riesgo principal), banco de pruebas de cada actualización antes de mandarla a las sucursales,
  o reciclarla.
- **Si el SSD es indispensable** también en las cajas principales.

## Preguntas

- [ ] ¿Qué procesador y qué disco tiene cada máquina? (en Windows o con la USB de Ubuntu, [arriba](#las-máquinas))
- [ ] ¿Se puede borrar el disco de alguna para dejarle solo Ubuntu Server, o hay que conservar Windows?
- [ ] ¿La batería de la laptop todavía sirve? ¿Hay no-break para la PC?
- [ ] Para las etapas 3 y 4, ¿la máquina se puede quedar en casa del dueño (más realista) o va en la tienda? ¿Llega un
  cable de red al router?
- [ ] ¿Se puede usar en la etapa 3 una copia del respaldo de la matriz, o solo datos ficticios?
- [ ] ¿Puede crear un bot de Telegram solo para pruebas (con @BotFather)? Hace falta desde la etapa 2.
