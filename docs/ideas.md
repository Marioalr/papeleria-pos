# Ideas a evaluar

> Lluvia de ideas. Nada de esto condiciona la arquitectura todavía; solo se cuida no cerrarle la puerta.

## Dispositivos propios

Supuesto de trabajo: dispositivos hechos a la medida (por ejemplo con ESP32 y Wi-Fi), conectados al central con
MQTT sobre TLS. Cada uno con su credencial, actualización remota y memoria para guardar eventos si se cae el internet.

| Idea | Qué aporta al control |
|---|---|
| **Contador de clientes en la entrada** | **Conversión** = tickets ÷ personas que entran, por hora y por turno. Si entra mucha gente y hay pocos tickets, algo pasa: mala atención, faltantes o ventas sin registrar. También ayuda a decidir cuántas cajas abrir por hora |
| **Presencia en la zona donde se guarda el efectivo** | Cada acceso se cruza con los retiros y depósitos registrados en la caja. Acceso sin movimiento registrado → alerta. Acceso fuera de horario → alarma |
| **Vigilancia nocturna de una zona** | Se activa sola con el último corte del día y se desactiva con la apertura. Alerta por Telegram |

Lo que implicaría:

- Algo siempre prendido que reciba los eventos de noche: el central en el servidor virtual (una de las razones de
  [esa propuesta](servidor-remoto.md)).
- Router y dispositivo con no-break, para que la vigilancia no se apague con la luz. Opcional: respaldo por datos
  celulares para la alarma.
- Los eventos de los dispositivos entran por el mismo camino que los de las cajas y se cruzan con ellos (tickets,
  retiros, cortes).
- Si hay cámaras o grabación: aviso de privacidad y letreros.

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
- las **entradas y salidas de efectivo** (F7 y F8), con su motivo;
- la **apertura del turno** con el fondo contado.

Sirve de respaldo físico para archivar y para aclarar diferencias. Falta decidir cuáles se imprimen siempre y cuáles
solo si se piden.

## Otras ideas que han salido

- Leer la foto de una lista escolar y convertirla en pedido.
- Detector de documentos comunes (CURP, recibo de luz) que sugiere el servicio a cobrar.
- Plantillas de listas por escuela y grado para la temporada de regreso a clases.
