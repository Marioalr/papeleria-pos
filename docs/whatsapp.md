# WhatsApp

## Situación actual

- Un número por sucursal, con **WhatsApp Business** desde hace tiempo, en un celular (también para llamadas).
- El grueso del trabajo se hace desde la PC (WhatsApp vinculado).
- El POS **no reemplaza el chat:** la conversación sigue en WhatsApp. Lo que se busca es que los archivos y el celular
  del cliente lleguen a un [pedido](pedidos.md) sin copiar a mano.

## Etapa 1: captura desde la PC (fase 4)

Sin API, sin costo y sin riesgo:

- Arrastrar el archivo de WhatsApp a la pantalla del pedido, o elegirlo de un panel de **descargas recientes** que
  vigila la carpeta donde WhatsApp guarda los archivos.
- El cajero escribe o pega el celular.
- El pedido queda cotizado al instante (con la función de cotizar de Control Print).

## Etapa 2: API oficial en coexistencia (fase 6)

Con **coexistencia,** el mismo número sigue funcionando en la app de WhatsApp Business y el sistema recibe una copia de
cada mensaje y archivo. Disponible en México desde 2025.

- **Requisito:** el número debe tener al menos 7 días de uso en WhatsApp Business. Ya se cumple.
- **Flujo:**
  1. Llega un mensaje con archivo.
  2. Se crea un pedido en *Recibido*, ya cotizado.
  3. Al terminar el pedido, el aviso de "listo" sale por la API.
- **Costo en México** (tarifas vigentes desde el 1 de octubre de 2026):
  - Los mensajes de los clientes son gratis.
  - Si el cliente escribió en las últimas 24 h, el aviso va como respuesta normal: 1,000 gratis al mes por número.
  - Si no, va como plantilla: 0.0085 USD cada una.
  - En total, unos pocos dólares al mes por sucursal.
- **Dirección pública:** el webhook necesita una. Propuesta: Cloudflare Tunnel hacia la caja principal (gratis; requiere
  un dominio propio, ~10–15 USD al año). No se abren puertos.
- **Si la caja principal está apagada o sin internet:** los mensajes siguen en WhatsApp como siempre; solo se pierde la
  captura automática, y se usa la etapa 1.
- **Celulares:** WhatsApp entrega los de México como `521` + 10 dígitos. Se normalizan a 10 para que el mismo número
  coincida venga por WhatsApp o por mostrador.

## Descartado

Las librerías no oficiales que automatizan WhatsApp Web (Baileys, whatsapp-web.js y similares). Son gratis, pero Meta
banea esos números, y perder el número de una sucursal sería un golpe serio.

## Pendientes

- [ ] Verificar que WhatsApp en la PC (Desktop/Web vinculado) siga funcionando con coexistencia: ahí se hace el grueso
      del trabajo.
- [ ] Alta en coexistencia: ¿directo con Meta o con un proveedor intermediario?
- [ ] Cuánto tiempo reintenta Meta entregar un mensaje si la caja principal no responde.
- [ ] Un webhook por sucursal (cada número a su caja principal) o todo a la matriz y que ella reparta.
- [ ] Dominio para el túnel.
- [ ] Probar en la etapa 1 si se puede arrastrar el archivo directo desde WhatsApp en la PC, o solo desde la carpeta de
      descargas.
