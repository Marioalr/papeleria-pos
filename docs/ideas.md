# Ideas a evaluar

> Lluvia de ideas. Nada de esto condiciona la arquitectura todavía; solo se cuida no cerrarle la puerta. Lo
> investigado el 2026-10-08 está en [notas](notas.md).

## Varias sucursales (pospuesto)

El 2026-10-08 el dueño **pospuso** la multisucursal para completar primero el POS de la matriz con caja principal y
secundaria, como hoy con eleventa. No se retira: la propuesta queda lista para retomarla, con lo que él pidió
(administrar ante un suceso a cualquier hora, con cámaras y contador; precios por sucursal; cualquier flujo de
mercancía; la mejor calidad-precio).

- **El central:** un servidor virtual siempre prendido que sincroniza las sucursales, reparte catálogo y precios por
  sucursal, recibe los sucesos con foto, escala los avisos (Telegram, alerta que suena en silencio, llamada) y manda
  acciones a distancia (bloquear una caja, pedir un corte X o un conteo). Costo estimado: ≈287–309 MXN al mes
  ([propuesta](servidor-remoto.md)).
- **Mercancía entre sucursales:** traspasos con estados y QR, compras en cualquier sucursal con quién paga separado de
  quién recibe, compras repartidas «de paso», y un almacén como sucursal sin cajas.
- **Las cámaras ligadas al central:** un puente siempre prendido por tienda (Raspberry Pi) para la foto y el clip de
  cada suceso, la vigilancia de noche con la detección de personas del grabador y el reloj de la tienda
  ([cámaras](camaras.md)).
- **La prueba con un servidor Linux:** la laptop Celeron o la PC A6 del dueño como central de ensayo, limitada a lo que
  tendría el servidor virtual ([prueba](prueba-sucursales-linux.md)).
- **Cuándo retomarla:** cuando el dueño decida pasar la sucursal 2 (o la 3.ª) al POS, unos 6 meses antes de esa fecha;
  mientras, la sucursal 2 abre con eleventa ([señales](notas.md#para-retomar-la-multisucursal)).

## Recargas y pago de servicios

Pospuesto (2026-10-08). Integrarlos al sistema: hoy se cobran fuera del POS. Son cobros a cuenta de terceros: solo la
comisión es ingreso ([servicios](servicios.md#cobros-a-cuenta-de-terceros)). Habría que elegir el proveedor (la
plataforma de recargas de eleventa trabaja con saldo prepagado y paga 5.5 % en recargas; el pago de servicios no
aparece en esa página; eleventa.com/recargas, consultada el 2026-10-08) y separarlos en el corte.

## Cámaras con una sola sucursal

Sin central ni puente, de día y con la caja principal, el grabador Dahua de la matriz (si es de la serie XVR5 con
«-I3», como el de la sucursal 2) ya permite cruzar el sistema con las cámaras ([cámaras](camaras.md)):

- **Texto POS en el video:** la caja manda al grabador una línea por suceso (cancelación, retiro, cajón sin venta) y
  queda escrita sobre la grabación de la cámara de esa caja, para buscarla después por folio o monto.
- **Foto del momento en el aviso de Telegram:** la caja principal le pide al grabador la foto de la cámara de esa caja y
  la manda con el aviso. Sin puente, la caja principal guarda la clave del usuario de solo ver del grabador (cifrada,
  fuera del repositorio).
- **De noche,** sin puente, solo quedan los avisos de DMSS de persona detectada, que dependen de la nube de Dahua.
- Falta probar en la tienda la foto, el texto POS y qué acepta el grabador; antes,
  [configurarlo](camaras.md#configurar-el-grabador) por seguridad.

## Dispositivos propios

Pospuesto con la multisucursal. La propuesta del 2026-10-08 usa primero lo que ya hay: el grabador Dahua detecta
personas de noche y cuenta cruces en la puerta, y un puente en la tienda recibe sus eventos ([cámaras](camaras.md)).
Para contar personas, si las líneas del grabador no alcanzan: una cámara Dahua que cuenta (≈4,640 MXN, descontinuada,
con firmware del 26 de marzo de 2026 o posterior) o Frigate (≈6,700); el sensor con ESP32 se descartó para contar
(60–80 % de precisión). Un dispositivo hecho a la medida (por ejemplo con ESP32 y Wi-Fi) queda para la presencia en la
zona del efectivo: con su credencial, actualización remota y memoria para guardar eventos si se cae el internet.

| Idea | Qué aporta al control |
|---|---|
| **Contador de clientes en la entrada** | **Conversión** = tickets ÷ personas que entran, por hora y por turno. Si entra mucha gente y hay pocos tickets, algo pasa: mala atención, faltantes o ventas sin registrar. También ayuda a decidir cuántas cajas abrir por hora |
| **Presencia en la zona donde se guarda el efectivo** | Cada acceso se cruza con los retiros y depósitos registrados en la caja. Acceso sin movimiento registrado → alerta. Acceso fuera de horario → alarma |
| **Vigilancia nocturna de una zona** | Se activa sola con el último corte del día y se desactiva con la apertura. Alerta con foto que escala si el dueño no la ve ([cámaras](camaras.md#de-noche)) |

Lo que implicaría:

- Algo siempre prendido que reciba los eventos de noche: el puente de cada tienda y el central (pospuestos).
- Módem, grabador, cámaras y puente con no-break, para que la vigilancia no se apague con la luz. Opcional: respaldo
  por datos celulares para la alarma.
- La presencia en la zona del efectivo queda para la fase 9 (un sensor en el cajón o una zona del grabador).
- Los eventos de los dispositivos entran por el mismo camino que los de las cajas y se cruzan con ellos (tickets,
  retiros, cortes).
- Si hay cámaras o grabación: aviso de privacidad y letreros.

## Asistente inteligente (piloto automático)

Lo propuso el dueño al revisar la [fase 2](fase-2-inventario.md): que el POS se administre solo cuando él no está y
que pida las tareas de la tienda en el momento oportuno, no a una hora fija.

- **Piloto automático:** si el dueño se ausenta, el POS sigue la operación por su cuenta. Decide lo rutinario con las
  reglas que él dejó (autorizaciones dentro de límites, pedidos del sugerido de compra, avisos al encargado) y solo le
  escala lo que sale de lo normal. Se apoyaría en lo propuesto para [administrar ante un
  suceso](servidor-remoto.md#la-propuesta) (pospuesto): sucesos con foto, avisos que escalan y acciones a distancia. Las
  reglas del modo ausente funcionan sin internet y cada decisión queda en la bitácora como «piloto». Si se usa
  inteligencia artificial, solo resume y explica: nunca aprueba ni bloquea.
- **Detector de ratos tranquilos:** con dispositivos propios (contador de clientes, presencia en el mostrador) y el
  ritmo de las ventas, sabe cuándo hay calma y aprovecha para pedir lo que espera: el conteo cíclico del día, ordenar
  una zona, reacomodar.
- **Tareas que salen de lo vendido:**
  - ordenar una zona después de mucho movimiento ahí;
  - recoger y enrollar los listones después de ciertos metros vendidos;
  - reacomodar los paquetes abiertos.
- Cada tarea se pide en la caja (o al celular del encargado), se marca como hecha y queda registrada: el dueño ve quién
  la hizo y cuánto tardó.

Lo que implicaría: saber en qué zona está cada producto (ubicación en el catálogo), los dispositivos de la sección
anterior y un historial de tareas. El conteo cíclico de la fase 2 se diseña para que después lo pueda disparar el
asistente en lugar de una hora fija.

## Experiencia de usuario (mejora continua)

Se tratan cuando el sistema ya esté en uso y se trabaje en mejorar la experiencia de los cajeros.

- **Más de un código por producto, con distintos usos.** Además del código de barras:
  - el **código del proveedor**, para armar pedidos de compra con la clave que él entiende;
  - una **clave corta** para buscar más rápido lo que se vende seguido.

  El [modelo](modelo-datos.md) ya lo permite (varios códigos por presentación y `CodigoProveedor`). Falta decidir
  cómo se usan en la búsqueda y en los pedidos.

## Documentos impresos

Imprimir en la impresora de tickets, como documentación en papel, lo que hoy solo quedaría en el sistema:

- el **corte** de cada turno, con espacio para la firma del cajero y del encargado;
- las **entradas de efectivo** (F7), con su motivo (las **salidas** ya se decidieron: siempre se imprimen, ver
  [entrega 2](fase-1-control.md#efectivo-en-caja));
- la **apertura del turno** con el fondo contado.

Sirve de respaldo físico para archivar y para aclarar diferencias. Falta decidir cuáles se imprimen siempre y cuáles
solo si se piden.

## Otras ideas que han salido

- Leer la foto de una lista escolar y convertirla en pedido.
- Detector de documentos comunes (CURP, recibo de luz) que sugiere el servicio a cobrar.
- Plantillas de listas por escuela y grado para la temporada de regreso a clases.
