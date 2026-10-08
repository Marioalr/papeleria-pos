# Ideas a evaluar

> Lluvia de ideas. Nada de esto condiciona la arquitectura todavía; solo se cuida no cerrarle la puerta.

## Dispositivos propios

Supuesto de trabajo: dispositivos hechos a la medida (por ejemplo con ESP32 y Wi-Fi). Para avisar, directo a Telegram y
a Healthchecks.io; para cruzar sus eventos con la caja, conectados al central con MQTT sobre TLS. Cada uno con su
credencial, actualización remota y memoria para guardar eventos si se cae el internet.

| Idea | Qué aporta al control |
|---|---|
| **Contador de clientes en la entrada** | **Conversión** = tickets ÷ personas que entran, por hora y por turno. Si entra mucha gente y hay pocos tickets, algo pasa: mala atención, faltantes o ventas sin registrar. También ayuda a decidir cuántas cajas abrir por hora |
| **Presencia en la zona donde se guarda el efectivo** | Cada acceso se cruza con los retiros y depósitos registrados en la caja. Acceso sin movimiento registrado → alerta. Acceso fuera de horario → alarma |
| **Vigilancia nocturna de una zona** | Se activa sola con el último corte del día y se desactiva con la apertura. Alerta por Telegram |

Lo que implicaría:

- Para solo avisar no hace falta el central: el dispositivo manda su aviso a Telegram con su propio bot y avisa a
  Healthchecks.io que sigue vivo. Para cruzar sus eventos con la caja hace falta algo siempre prendido que los reciba
  de noche: es una de las señales para montar el [central](servidor-remoto.md#cuándo-montar-el-central).
- Router y dispositivo con no-break, para que la vigilancia no se apague con la luz. Opcional: respaldo por datos
  celulares para la alarma.
- Los eventos de los dispositivos entran por el mismo camino que los de las cajas y se cruzan con ellos (tickets,
  retiros, cortes).
- Si hay cámaras o grabación: aviso de privacidad y letreros.

## Asistente inteligente (piloto automático)

Lo propuso el dueño al revisar la [fase 2](fase-2-inventario.md): que el POS se administre solo cuando él no está y
que pida las tareas de la tienda en el momento oportuno, no a una hora fija.

- **Piloto automático:** si el dueño se ausenta, el POS sigue la operación por su cuenta. Decide lo rutinario con las
  reglas que él dejó (autorizaciones dentro de límites, pedidos del sugerido de compra, avisos al encargado) y solo le
  escala lo que sale de lo normal.
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
