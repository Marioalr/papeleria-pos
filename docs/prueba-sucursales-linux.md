# Prueba de varias sucursales con un servidor Linux

> **Pospuesto (2026-10-08).** El dueño pausó la multisucursal para completar primero el POS de la matriz con caja
> principal y secundaria, como hoy con eleventa. Esta propuesta no se retira: queda como nota para retomarla y no se
> programa por ahora. Lo que sí aplica con una sola sucursal está en [notas](notas.md#aplica-ya-con-una-sola-sucursal).

> **Actualizado el 2026-11-26:** el [central](servidor-remoto.md) elegido correrá en Linux (ARM64) dentro de **Oracle Cloud
> Always Free en modo PAYG** (2 OCPUs, 4–8 GB RAM), sin videovigilancia, bot de Telegram, WhatsApp ni IoT en su 1.ª fase.
> Antes de subir a la nube se ensaya en una máquina propia con Linux, con dos sucursales, para validar rendimiento,
> sincronización, traspasos y restauraciones. (La etapa 3 previa del puente de cámaras queda fuera del alcance del
> servidor.)

## Para qué

- **Ver cómo responde con poco hardware.** Si el procesador no es muy viejo ([ver abajo](#las-máquinas)), por núcleo
  estas máquinas rinden parecido o algo menos que el servidor virtual (de dos tercios a casi igual, según el modelo).
  Como el núcleo del servidor virtual es compartido, el resultado se confirma con unas horas en uno de verdad.
- **Ensayar las dos sucursales con el central** antes de que la sucursal 2 pase al POS: precios por sucursal,
  mercancía, sucesos, avisos y acciones a distancia.
- **Probar el puente contra el grabador real:** la foto del momento, los eventos de personas, el reloj y la red
  privada.
- **Encontrar a tiempo lo que solo funciona en Windows.** Hoy el servicio solo se ha usado en Linux para programar:
  nunca como servidor de red, ni como servicio del sistema, ni prendido días seguidos.

## Las máquinas

| | Laptop | PC |
|---|---|---|
| Procesador | Intel Celeron (modelo por confirmar) | AMD A6 (modelo por confirmar) |
| Memoria | 8 GB | 8 GB |
| Si se va la luz | Su batería hace de no-break; si se acaba, casi ninguna enciende sola al regresar la luz | Se apaga de golpe |
| Luz que gasta (aprox.) | 6–12 W | 25–45 W, de 3 a 5 veces más |
| Disco que suele traer | Memoria soldada (eMMC) de 32–64 GB, o un disco que se puede cambiar por un SSD | Disco duro mecánico: conviene cambiarlo por un SSD (barato) |

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
  para dejarla después como banco de pruebas: las versiones nuevas de .NET ya no arrancan ahí.
- **Cuánto rinde** (por núcleo, según PassMark). Un Celeron N4000, N4020, N4120, N4500 o N5100 rinde de dos tercios a
  cuatro quintos de un núcleo del servidor virtual; un A6-5400K, 6400K, 7400K o 9500, parecido o algo menos. Un Celeron
  N28xx (Bay Trail) rinde menos de la mitad: sus resultados saldrían demasiado pesimistas.
- **El disco.** En la tercera línea, `ROTA` 1 es un disco duro mecánico, `mmcblk` es la memoria soldada y `ROTA` 0
  con `sata` o `nvme` es un SSD. La línea con `usb` es la propia USB de Ubuntu: no cuenta.

**Papeles:** la mejor de las dos, de preferencia la laptop, **se queda con Windows**: en la prueba hace de sucursal 2
con el instalador real y después queda como caja principal de repuesto, guardada y apagada. **La otra va a Linux**,
por cable: es el central de ensayo y el banco de cada restauración y de cada actualización; se prende solo para eso. Si
la etapa 0 muestra que la PC es mejor, se invierten. Las dos sirven de [puente](camaras.md#el-puente) de emergencia,
arrancando Linux desde una USB.

- **Parecerse a un servidor modesto:** la máquina con Linux arranca limitada (`maxcpus=1 mem=2G`) para no ocultar
  problemas de memoria ni de rendimiento. Al cerrar 6.1, el mismo script de despliegue corre en la instancia ARM de
  **Oracle Cloud Always Free (PAYG)** (o en un ensayo temporal si se valida compatibilidad ARM64).

## Etapas

| # | Etapa | Cuándo | Qué necesita |
|---|---|---|---|
| 0 | **Conocer las máquinas** | Al retomar (o antes, para saber si una sirve de caja de repuesto) | Una USB de Ubuntu, o Windows |
| 1 | **Arreglos para Linux** | Al retomar. Lo que pasó al piloto: el logo en Linux, las pruebas en cada PR y el simulador de cajas ([plan](plan.md#para-el-piloto-con-dos-cajas)) | Cambios chicos en el código ([abajo](#lo-que-hay-que-arreglar-o-construir)) |
| 2 | **Dos sucursales con central** | Entregas 6.1 a 6.4, después del piloto. Al cerrar 6.1: levantar la instancia en Oracle Cloud Always Free (PAYG) con el script | El modo central, sincronización, precios, traspasos y panel |
| 3 | **Ensayo largo** | Entrega 6.5 | La etapa 2 aprobada |

Las cajas simuladas corren en la PC de desarrollo, no en el central de ensayo, para no quitarle procesador.

### 1. Arreglos para Linux

Ayudan a programar hoy (la PC de desarrollo es Linux) y los va a necesitar el central: dónde guarda sus datos, el logo
del ticket, las pruebas en Linux y el simulador de cajas, que también sirve para medir el día pico y la contingencia
contra el servicio de Windows.

**Si se quiere ver ya cómo responde la máquina** con Linux, la medición se puede hacer cuando se quiera; no bloquea
nada. Ojo: es el servidor de **una sola sucursal**; la prueba de varias sucursales con el central (etapa 2) solo se
puede hacer cuando exista ese código, en la entrega 6.1, después del piloto en la matriz. El servicio de hoy como
servicio del sistema, con la base ficticia de un año de ventas y el límite de 1 núcleo y 2 GB:

- **Día pico comprimido:** los ~120 tickets de un día pico en una hora, en 3 cajas simuladas, con búsquedas, cobro
  mixto, cortes X y Z (cada corte Z con su respaldo).
- **El techo:** subir la carga hasta que escanear tarde más de lo que marca la [meta](#qué-se-mide-y-cuándo-sale-bien).
- **Matar el servicio y desconectar la luz** a media venta: las cajas siguen en contingencia, la base queda íntegra y
  todo vuelve solo.
- **72 horas prendida,** para ver si la memoria crece.

### 2. Dos sucursales con central

El central en la máquina con Linux, instalado con el script que luego irá al servidor virtual. La matriz en la PC de
desarrollo (o en el simulador) y la sucursal 2 en la máquina con Windows, con el instalador real.

- **Subida y bajada:** ventas, cortes y movimientos llegan una sola vez y cuadran; el catálogo baja, también lo que se
  quita.
- **Precios:** el de una sola sucursal llega solo a esa; un cambio hecho sin internet con conflicto se resuelve por
  rango, no por la hora.
- **Mercancía:** un traspaso en línea con diferencia y una compra repartida «de paso».
- **Permisos locales:** las autorizaciones en ausencia corren con los permisos del encargado y quedan en bitácora y en
  el panel.
- **Antes de abrir producción** (al cerrar 6.1): correr el mismo script en la instancia ARM64 de **Oracle Cloud Always
  Free (PAYG)** y probar la restauración completa desde Backblaze B2.
- **Dos sucursales en una sola base** queda como prueba automática.

### 3. Ensayo largo

Dos a cuatro semanas con las dos sucursales vendiendo en sus horarios y apagándose de noche. Una o dos
[fallas provocadas](#fallas-a-provocar) por semana, los parches de Ubuntu con su reinicio, una actualización del POS
con regreso a la versión anterior y, al final, una restauración completa desde Backblaze B2 en una VM limpia. Una
bitácora de la prueba: cada caída, cuánto duró, si se recuperó sola y qué hubo que hacer.

## Fallas a provocar

| Falla | Etapas | Debe pasar |
|---|---|---|
| El servicio se cae o se desconecta el cable | 1, 2 | Si se cae, el sistema lo levanta en segundos. En los dos casos, las cajas venden en contingencia y suben su bandeja sin duplicar |
| Se va la luz de golpe, unas 10 veces: a media venta, a media sincronización, durante un respaldo | 1, 2, 3 | La base queda íntegra y todo arranca solo al regresar la luz |
| Disco lleno | 1, 3 | La base no se daña; el respaldo falla con aviso; si una venta no se puede guardar, la caja lo dice claro y no la da por hecha |
| Central apagado una hora y una noche | 2, 3 | Las sucursales venden igual; Healthchecks.io avisa; al volver el central se sincroniza solo |
| Sin internet en una sucursal, una hora y un día | 2, 3 | Vende y hace sus cortes; al volver sube todo y recibe los precios pendientes |
| Una sucursal apagada tres días | 2, 3 | Al encender recibe lo pendiente, en orden; Healthchecks.io reporta la falta de latido |
| Reinicio a media sincronización, del central o de la sucursal | 2, 3 | Reintenta solo, sin duplicar |
| Relojes desfasados 10 minutos, una hora y un día | 2, 3 | Las ventas quedan en su día (UTC) |
| Red privada (NetBird) caída | 3 | La venta y la sincronización siguen (si hay HTTPS) o reintentan solas al volver NetBird |
| Memoria al límite | 2 | El sistema vuelve a levantar el servicio y las sucursales reintentan solas |

## Qué se mide y cuándo sale bien

Los tiempos son los de 19 de cada 20 veces: la vez más lenta de cada 20 no cuenta.

| Qué | Meta |
|---|---|
| Escanear un producto | En la caja de Windows, que aparezca en menos de 100 ms (la [meta de la caja](caja-y-control.md)); en el simulador, que el servicio conteste en menos de 50 ms, para dejar margen a la pantalla. En el día pico y mientras corre un respaldo |
| Buscar por nombre / cobrar | Menos de 300 ms / menos de 500 ms |
| Memoria del servicio de sucursal | Menos de 300 MB y sin crecer de un día a otro (en la PC de desarrollo usa ~190–230 MB) |
| Memoria del central | En Oracle Cloud ARM (`A1.Flex`, 4–8 GB RAM) queda sobrado; en la máquina de ensayo (2 GB), menos de 1.4 GB sostenidos y casi sin memoria de intercambio |
| Disco del central | Sin videovigilancia (~24 GB), `central.db` y registros caben en pocos GB (el mínimo de disco en OCI es 50 GB) |
| Cuadre | Ninguna venta perdida ni duplicada: tickets, cortes y existencias cuadran entre sucursal y central |
| Caja principal apagada en horario | Healthchecks.io avisa en menos de 10 minutos |
| Central caído | Healthchecks.io avisa en menos de 10 minutos |
| Sincronización | Un cambio de precio llega en menos de 2 minutos; tras 8 horas sin conexión se pone al día en menos de 2, tras 3 días en menos de 5 |
| Restaurar | El central desde Ubuntu recién instalado, o una sucursal desde Backblaze, en menos de 30 minutos |

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
  herramienta de prueba, no parte del POS. **Ya está (2026-10-17):** `simular-cajas`, con la caja de verdad sin la
  pantalla, una caída de la red a media jornada, las metas de abajo, la memoria del servicio y el cuadre al final
  ([avance](plan.md#para-el-piloto-con-dos-cajas)). Para el central le falta la carga sostenida (el techo, las 72 horas) y
  apagar el servicio a media venta.
- **Instalarlo en Linux:** el servicio publicado para Linux y un script que crea el usuario sin privilegios, el
  servicio del sistema y el cortafuegos. Es el principio del script del central.

**Etapas 2 y 3:** las [entregas de la 1.ª fase del central](servidor-remoto.md#entregas). (El bot de
Telegram está en evaluación y fuera de la 1.ª fase, [evaluación](evaluacion-telegram.md).)

## Fuera de la prueba

- **Lo de Windows:** la caja, el agente de impresión y el instalador siguen en Windows.
- **El servidor de cada sucursal** sigue en su caja principal.
- **Datos reales:** nunca en el repositorio, y solo si el dueño lo permite. La copia de la matriz vive en las máquinas
  de la prueba y en su respaldo, y se borra al terminar.

## Preguntas

- [ ] ¿Qué procesador, qué disco y qué Windows tiene cada máquina? (en Windows o con la USB de Ubuntu,
  [arriba](#las-máquinas)) ¿La batería de la laptop todavía sirve?
- [ ] ¿Se puede dejar una con Windows como caja principal de repuesto, y la otra para Linux?
- [ ] ¿Quiere medir ya cómo responde la máquina con Linux (no bloquea nada) o se deja para el ensayo del central?
- [ ] ¿Se puede usar en la etapa 2 una copia del respaldo de la matriz, o solo datos ficticios?
