# WhatsApp

## Situación actual

- Un número por sucursal, con **WhatsApp Business** desde hace tiempo, en un celular (también para llamadas).
- El grueso del trabajo se hace desde la PC (WhatsApp vinculado).
- El POS **no reemplaza el chat:** la conversación sigue en WhatsApp. Lo que se busca es que los archivos y el celular
  del cliente lleguen a un [pedido](pedidos.md) sin copiar a mano.

## Etapa 1: captura desde la PC (fase 4)

Sin API, sin costo y sin riesgo:

- Arrastrar el archivo de WhatsApp a la pantalla del pedido, o elegirlo de un panel de **descargas recientes** que
  vigila la carpeta donde WhatsApp guarda los archivos (con WhatsApp Web, que hace falta desde la etapa 2, la carpeta
  de descargas del navegador).
- El cajero escribe o pega el celular.
- El pedido queda cotizado al instante (con la función de cotizar del [módulo de impresión](impresion.md)).

## Etapa 2: API oficial en coexistencia (cuando el dueño la quiera)

Con **coexistencia,** el mismo número sigue funcionando en la app de WhatsApp Business y el sistema recibe una copia de
cada mensaje y archivo. Disponible en México desde 2025.

- **Requisitos:** la app de WhatsApp Business 2.24.17 o más nueva, y el alta la hace un proveedor reconocido por
  Meta (o el negocio, si Meta lo aprueba como «Tech Provider»). Los proveedores piden unos 7 días de uso del número;
  el de la matriz ya cumple.
- **En las PC, WhatsApp Web:** en coexistencia Meta no admite la app de WhatsApp para Windows. Se usa
  web.whatsapp.com en Chrome o Edge (hasta 4 dispositivos vinculados). Al conectar el número, Meta desvincula todos
  los dispositivos y hay que volver a vincularlos. El celular debe abrir la app al menos una vez cada 13 días (Meta lo
  desconecta a los ~14 días sin uso).
- **Flujo:**
  1. Llega un mensaje con archivo.
  2. Se crea un pedido en *Recibido*, ya cotizado.
  3. Al terminar el pedido, el aviso de "listo" sale por la API.
- **Costo en México** (tarifas vigentes desde el 1 de octubre de 2026):
  - Los mensajes de los clientes son gratis.
  - Si el cliente escribió en las últimas 24 h, el aviso va como respuesta normal: 1,000 gratis al mes por número.
  - Si no, va como plantilla: 0.1565 MXN cada una si la cuenta se cobra en pesos (la moneda se elige al crearla; según
    proveedores, después no se cambia), o 0.0085 USD. Desde octubre de 2026 las plantillas de utilidad se cobran aunque
    vayan dentro de las 24 h.
  - Hay que registrar un medio de pago: según proveedores, sin él Meta deja de entregar después del mensaje 1,000.
  - En total, de 0 a unos 80 MXN al mes por sucursal.
  - Con un proveedor se suma su cuota: Kapso, 25 USD al mes por hasta 3 números; 360dialog, 49 EUR por número.
- **Dirección pública:** Meta manda cada mensaje a una dirección HTTPS con certificado válido, y hay que validar la
  firma de cada aviso. Mientras no haya central ([pospuesto](servidor-remoto.md)), un receptor aparte guarda los
  mensajes hasta que la caja principal los pide (con una conexión de salida, como Telegram):
  - **Meta directo** con un Cloudflare Worker y su base (gratis: 100,000 peticiones al día). Hay que pasar la revisión
    de Meta como «Tech Provider» y confirmar que acepte la dirección `workers.dev` (si no, un dominio propio).
  - **Un proveedor que guarde los mensajes,** como Kapso (25 USD al mes por hasta 3 números): la caja los pide por su
    API al encender, sin receptor propio.
  - **Nunca un túnel en la caja principal:** el servicio deja pasar sin llave lo que llega de la misma PC
    ([notas](notas.md#seguridad)), y de noche está apagada.
  Con el central, el webhook se muda ahí. Se puede usar una dirección por número.
- **Si la caja principal está apagada (de noche):** los mensajes esperan en el receptor y aparecen como pedidos al
  encenderla. El archivo hay que bajarlo de Meta antes de 7 días.
- **Si el receptor no responde:** Meta reintenta hasta 7 días (puede repetir mensajes: se descartan por su ID). Mientras
  tanto, los mensajes siguen en WhatsApp como siempre y se usa la etapa 1.
- **Celulares:** el número que manda WhatsApp para México puede no coincidir con el que se marca. Se guarda tal como
  llega, para responder, y se normaliza a 10 dígitos para que coincida con el de mostrador.

## La lista de quién escribió (propuesta, 2026-10-10)

En la caja, Ctrl+L mostraría «Escribieron por WhatsApp hace poco» para usar a esa persona como cliente genérico del
ticket o del pedido, sin leer los chats ([propuesta de la interfaz](propuesta-interfaz.md#whatsapp-quién-escribió-hace-poco)).
Sale de la etapa 2: de cada mensaje se guarda el remitente, la hora y los datos del archivo, sin el texto.

Lo que cambió desde la investigación anterior (consultado el 2026-10-10):

- **Identificador por negocio (BSUID)** en cada mensaje desde abril de 2026, y **nombres de usuario** desde junio:
  quien use uno puede llegar sin número. Se identifica al contacto por ese identificador.
- Los mensajes de clientes que escriben desde **WhatsApp para Windows** no llegan al webhook, según Meta: hay que medir
  cuántos son.
- Los **dispositivos vinculados** se desconectan a los ~30 días sin uso (el celular, a los ~14).
- **Kapso** dice cobrar la tarifa de Meta sin margen y tiene un plan gratis (1 número, 2,000 mensajes al mes); su API de
  conversaciones da el número, el nombre y la última actividad. Falta confirmar que el plan gratis admita coexistencia.
- **Sin API:** las notificaciones del celular (en la futura app Kotlin) dan solo el nombre; las de Windows piden
  empaquetar la caja con identidad y pierden los chats abiertos.

## Descartado

Las librerías no oficiales que automatizan WhatsApp Web (Baileys, whatsapp-web.js y similares). Son gratis, pero Meta
banea esos números, y perder el número de una sucursal sería un golpe serio.

## Pendientes

- [x] WhatsApp en la PC con coexistencia: la app para Windows no se admite; WhatsApp Web en Chrome o Edge sí.
- [ ] Alta en coexistencia: ¿directo con Meta (sin cuota; hay que pasar la revisión de Meta como «Tech Provider», sin
      plazo publicado, y un receptor aparte) o con un proveedor (Kapso: 25 USD ≈ 449 MXN al mes por hasta 3 números,
      sin esa revisión ni receptor propio)?
- [x] Cuánto reintenta Meta si el receptor no responde: hasta 7 días.
- [ ] Probar en la etapa 1 si se puede arrastrar el archivo directo desde WhatsApp en la PC, o solo desde la carpeta de
      descargas.
- [ ] La lista de quién escribió: ¿el plan gratis de Kapso admite coexistencia? ¿La huella del archivo que manda Meta
      coincide con el que baja WhatsApp Web? ¿Cuántos clientes escriben desde WhatsApp para Windows?
