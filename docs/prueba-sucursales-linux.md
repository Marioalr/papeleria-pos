# Prueba de varias sucursales con un servidor Linux

> Propuesta, en lluvia de ideas: puede cambiar. Con solo dos sucursales, la propuesta es
> [empezar sin central](servidor-remoto.md#el-primer-año-sin-central): el primer año ninguna máquina Linux queda en
> la tienda. La prueba se ajusta a eso: los arreglos para Linux van ya, la medición de la máquina con Linux se puede
> hacer ya si el dueño quiere, se ensaya cómo trabajan las dos sucursales sin central y el servidor Linux con dos
> sucursales se prueba cuando se decida montar el central.

## Para qué

- **Ensayar las dos sucursales** antes de que la sucursal 2 pase al POS: el catálogo que reparte la matriz, el respaldo
  fuera de la tienda, los avisos de apertura y la red privada.
- **El central correrá en Linux** cuando haga falta. Hoy el servicio solo se ha usado en Linux para programar: nunca
  como servidor de red, ni como servicio del sistema, ni prendido días seguidos.
- **Ver cómo responde con poco hardware.** Si el procesador no es muy viejo ([ver abajo](#las-máquinas)), por núcleo
  estas máquinas rinden algo menos que el servidor virtual: si el servicio responde bien ahí, allá irá igual o mejor.
- **Encontrar a tiempo lo que solo funciona en Windows.**

## Las máquinas

| | Laptop | PC |
|---|---|---|
| Procesador | Intel Celeron (modelo por confirmar) | AMD A6 (modelo por confirmar) |
| Memoria | 8 GB | 8 GB |
| Si se va la luz | Su batería hace de no-break; si se acaba, casi ninguna enciende sola al regresar la luz | Se apaga de golpe: necesita no-break y, en el BIOS, encender sola al regresar la luz |
| Luz que gasta (aprox.) | 6–12 W | 25–45 W, de 3 a 5 veces más |
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
  para dejarla después como repuesto o banco de pruebas: las versiones nuevas de .NET ya no arrancan ahí.
- **Cuánto rinde** (por núcleo, según PassMark). Un Celeron N4000, N4020, N4120, N4500 o N5100 rinde unos dos tercios
  de un núcleo del servidor virtual; un A6-5400K, 6400K, 7400K o 9500, parecido o algo menos. Un Celeron N28xx (Bay
  Trail) rinde menos de la mitad: sus resultados saldrían demasiado pesimistas.
- **El disco.** En la tercera línea, `ROTA` 1 es un disco duro mecánico, `mmcblk` es la memoria soldada y `ROTA` 0
  con `sata` o `nvme` es un SSD. La línea con `usb` es la propia USB de Ubuntu: no cuenta.

**Cuál queda con Windows:** la mejor de las dos (que llegue a x86-64-v2, que rinda como un N4000 o mejor y que tenga
SSD o se le pueda poner), de preferencia la laptop: casi no gasta luz, no hace ruido y trae su propio no-break. Es la
sucursal 2 de la etapa 2 y después la caja principal de repuesto. **La otra va a Linux**, por cable: la medición de la
etapa 1 y las etapas 3 y 4. Si es la PC A6, al terminar se recicla (gasta de 3 a 5 veces más luz).

## Etapas

| # | Etapa | Cuándo | Qué necesita |
|---|---|---|---|
| 0 | **Conocer las máquinas** | Ya: 10 minutos, sin borrar nada | Una USB de Ubuntu, o Windows |
| 1 | **Arreglos para Linux** | Ya, en paralelo a la fase 3 | Cambios chicos en el código ([abajo](#lo-que-hay-que-arreglar-o-construir)) |
| 2 | **Dos sucursales sin central** | Antes de que la sucursal 2 pase al POS (fase 6a) | El código del enlace de las dos sucursales, las cuentas de los servicios y dos bots de prueba |
| 3 | **El central de verdad** | Cuando se decida el central (la 3.ª sucursal o una [señal](servidor-remoto.md#cuándo-montar-el-central)) | El modo central y la sincronización |
| 4 | **Ensayo largo** | Después de la etapa 3, antes de contratar el servidor virtual | La etapa 3 aprobada |

Las cajas simuladas corren en la PC de desarrollo, no en la máquina de prueba, para no quitarle procesador.

### 1. Arreglos para Linux

Ayudan a programar hoy (en la PC de desarrollo, que es Linux) y los va a necesitar el central: dónde guarda sus datos,
el logo del ticket, las pruebas en Linux y el simulador de cajas, que también sirve para medir el día pico y la
contingencia contra el servicio de Windows.

**Si se quiere ver ya cómo responde la máquina** con Linux, la medición completa se puede hacer cuando se quiera; no
bloquea nada y deja datos para el central. La máquina con Ubuntu Server 26.04 LTS y el servicio como servicio del
sistema, con la base ficticia de un año de ventas:

- **Día pico comprimido:** los ~120 tickets de un día pico en una hora, en 3 cajas simuladas, con búsquedas, cobro
  mixto, cortes X y Z (cada corte Z con su respaldo).
- **El techo:** subir la carga hasta que escanear tarde más de lo que marca la [meta](#qué-se-mide-y-cuándo-sale-bien).
- **Cambiar precios a media venta:** cada cambio hace que todas las cajas vuelvan a bajar el catálogo (~1 MB). No
  debe notarse al escanear.
- **Matar el servicio y desconectar la luz** a media venta: las cajas siguen en contingencia, la base queda íntegra y
  todo vuelve solo.
- **72 horas prendida,** para ver si la memoria crece.
- **Con lo que tendría en el servidor virtual de 1 GB:** cada medición se repite con el servicio limitado a 1 núcleo
  y unos 800 MB (Ubuntu usa el resto).

### 2. Dos sucursales sin central

La matriz corre en la PC de desarrollo (o en la máquina con Linux, para medirla con carga real); la sucursal 2, en la
máquina que conserve Windows, con el instalador real.
Así se prueba de verdad lo que va a correr en la tienda (Windows, la tarea programada de respaldo, la red privada y el
control remoto).

- **El catálogo que baja:** un cambio de precio en la matriz llega a la sucursal 2, también uno que quita un código.
  La sucursal 2 no puede editar el catálogo.
- **Prendidas a distintas horas:** con la matriz apagada, la sucursal 2 vende con el último catálogo; con la sucursal 2
  apagada tres días, al encender recibe el catálogo vigente.
- **Respaldo fuera:** la copia diaria y la del corte Z llegan a Backblaze B2, cifradas; se restaura una en una PC
  limpia.
- **Avisos:** Healthchecks avisa en menos de 15 minutos cuando una sucursal no abrió a su hora, cada una con su
  horario; un bot de prueba por sucursal.
- **Internet de por medio:** la red privada con retraso y pérdida de paquetes simulados.
- **Dos sucursales en una sola base** queda como prueba automática chica, porque será el modelo del central.

### 3. El central de verdad

Cuando se decida el central. La máquina se reinstala desde cero y se corre el **script del central**, el mismo que
quedará para el servidor virtual: Ubuntu, parches automáticos, cortafuegos, WireGuard, Caddy (solo si hay algo
público), el servicio en modo central y Litestream. Dos sucursales, cada una en una PC con Windows: la sucursal 1 con
una copia del respaldo de la matriz si el dueño lo permite (se le pregunta entonces; si no, otra base ficticia; nunca
la matriz en uso) y la sucursal 2 con la base ficticia, otra serie de folios y otro horario.

- **Catálogo que baja:** un cambio de precio en el panel llega a las dos; el precio de una sola sucursal, solo a esa.
- **Ventas que suben una sola vez:** por sucursal y por día, los tickets, los totales y los cortes cuadran entre la
  sucursal y el central.
- **Lo del dueño:** el panel por la red privada; Telegram en el central (la autorización pedida en la sucursal 2
  regresa solo a ella); el aviso de una sucursal que no abrió; lo que llega con una sucursal apagada.
- **Respaldo:** Litestream, y restaurar en una máquina limpia con el script.

### 4. Ensayo largo

Dos a cuatro semanas con las dos sucursales vendiendo en sus horarios y apagándose de noche. Una o dos
[fallas provocadas](#fallas-a-provocar) por semana, los parches de Ubuntu con su reinicio, una actualización del POS
con regreso a la versión anterior y, al final, una restauración con semanas de datos. Una bitácora de la prueba:
cada caída, cuánto duró, si se recuperó sola y qué hubo que hacer. Al terminar, el script corre unas horas en un
servidor virtual de verdad, que se cobra por hora, para confirmar el tamaño del plan.

## Fallas a provocar

| Falla | Etapas | Debe pasar |
|---|---|---|
| El servicio se cae o se desconecta el cable | 1, 2 | Si se cae, el sistema lo levanta en segundos. En los dos casos, las cajas venden en contingencia y suben su bandeja sin duplicar |
| Se va la luz de golpe, unas 10 veces: a media venta, a media sincronización, durante un respaldo | 1, 3, 4 | La base queda íntegra y todo arranca solo al regresar la luz |
| Disco lleno | 1, 4 | La base no se daña; el respaldo falla con aviso; si una venta no se puede guardar, la caja lo dice claro y no la da por hecha; al liberar espacio todo sigue |
| Backblaze llega a su tope | 2 | El respaldo deja de subir y Healthchecks avisa |
| Una sucursal apagada tres días | 2, 3, 4 | Cada día llega el aviso de que no abrió; al encender recibe el catálogo vigente |
| Sin internet en una sucursal, una hora y un día | 2, 3, 4 | Vende y hace sus cortes; al volver sube su respaldo y recibe los precios pendientes |
| Central apagado una hora y una noche | 3, 4 | Las sucursales venden igual; llega el aviso; al volver se sincroniza solo |
| Reinicio a media sincronización, del central o de la sucursal | 3, 4 | Reintenta solo, sin duplicar |
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
| Catálogo | Un cambio en la matriz llega a la sucursal 2 prendida en menos de 5 minutos |
| Sincronización con el central | Tras 8 horas sin conexión se pone al día en menos de 2 minutos; tras 3 días, en menos de 5 |
| Avisos | Sucursal sin abrir: menos de 15 minutos después de su hora. Central caído: menos de 10 minutos |
| Restaurar | Una sucursal desde Backblaze, o el central desde Ubuntu recién instalado, en menos de 30 minutos |

## Lo que hay que arreglar o construir

**Etapa 1**, en cambios chicos, uno por tema:

- **Dónde guarda sus datos.** Sin configurar, la base, los respaldos y el token de Telegram van en la carpeta común de
  datos, que en Linux es `/usr/share`, donde no se puede escribir: el servicio no arranca. En Linux van en
  `/var/lib/papeleriapos`.
- **El logo del ticket.** La biblioteca que procesa el logo necesita su versión para Linux. Sin ella falla
  Configuración → Ticket; con ella también pasan las 8 pruebas automáticas que hoy fallan en Linux.
- **Pruebas en Linux** en GitHub Actions, para que lo que se rompa en Linux se vea en cada cambio.
- **Simulador de cajas:** da de alta N cajas con el PIN del dueño y vende por la API como la caja, con su contingencia
  (si el servicio no contesta, guarda las ventas y las sube al volver). Anota cuánto tarda cada llamada. Es una
  herramienta de prueba, no parte del POS.
- **Instalarlo en Linux** (solo para la medición opcional): el servicio publicado para Linux y un script que crea el
  usuario sin privilegios, el servicio del sistema y el cortafuegos. Es el principio del script del central.

**Etapa 2:** el [enlace de las dos sucursales](servidor-remoto.md#el-primer-año-sin-central) y dos bots de Telegram
solo para la prueba, uno por sucursal (Telegram admite un solo receptor por bot, y el de la tienda no se toca).

**Etapa 3:** el modo central y, antes de poner Caddy, que nada pase sin llave.

## Fuera de la prueba

- **WhatsApp:** su receptor se prueba aparte ([WhatsApp](whatsapp.md)); no necesita la máquina.
- **Abrir puertos** de la casa o la tienda a internet: todo pasa por la red privada.
- **Lo de Windows:** la caja, el agente de impresión y el instalador siguen en Windows.
- **El servidor de cada sucursal** sigue en su caja principal: la prueba solo junta datos, no cambia esa decisión.
- **Datos reales:** nunca en el repositorio, y solo si el dueño lo permite. La copia de la matriz vive en las máquinas
  de la prueba y en su respaldo, y se borra al terminar.

## Qué decide

- **Si el enlace sin central funciona:** la etapa 2 muestra que el catálogo, el respaldo, los avisos y la red privada
  trabajan bien sin central. Si hace falta un servidor el primer año lo deciden la
  [respuesta del dueño](servidor-remoto.md#preguntas) y las [señales](servidor-remoto.md#cuándo-montar-el-central).
- **Tamaño del plan,** cuando llegue el central: 1 GB o 2 GB ([precios](servidor-remoto.md#dónde-cuando-haga-falta)).
- **Qué hacer con las máquinas:** la que conserve Windows, caja principal de repuesto (si muere la PC de una
  sucursal, se instala el servicio y se restaura la copia de Backblaze) y banco de pruebas de cada actualización. La de
  Linux, para las etapas 3 y 4; si es la PC A6, al terminar se recicla.

## Preguntas

- [ ] ¿Qué procesador, qué disco y qué Windows tiene cada máquina? (en Windows o con la USB de Ubuntu,
  [arriba](#las-máquinas)) ¿La batería de la laptop todavía sirve?
- [ ] ¿Se puede dejar una con Windows como caja principal de repuesto, y la otra para Linux?
- [ ] ¿Quiere medir ya cómo responde la máquina con Linux (no bloquea nada) o se deja para cuando se decida el
  central?
- [ ] ¿Puede crear dos bots de Telegram solo para pruebas, uno por sucursal (con @BotFather)? Hacen falta desde la
  etapa 2.
