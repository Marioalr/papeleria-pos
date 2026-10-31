# Trámites dentro del POS

> **Investigación del 2026-10-30.** El dueño pidió ver si conviene hacer los trámites desde el POS, cuáles y cómo, sobre
> todo con el poco volumen que hay. La CURP es lo más pedido, con picos de hasta 100 al día una o dos veces al año. Su
> idea: capturar los datos, procesar, que el cajero dé clic o resuelva el captcha, atrapar el documento y mostrarlo con
> Imprimir y Compartir. «No busco saltarme el procedimiento estándar.»
>
> **Cómo se investigó:** el 2026-10-30 se leyeron las páginas públicas de cada sitio oficial (su HTML, sus scripts, sus
> avisos de privacidad y sus términos de uso). **No se envió ningún formulario ni se escribió ningún dato** en ellos, y
> no se tocó ningún captcha. Lo que solo se sabe con un envío real quedó en [lo que se verifica en la
> tienda](#lo-que-solo-se-puede-verificar-en-la-tienda). Es una propuesta: no hay código.
>
> **El recibo de CFE se revisó otra vez el 2026-10-31,** a pedido del dueño: [recibo de CFE](recibo-cfe.md).

## En corto

- **Sí conviene dentro del POS:** la **CURP** y la **cita para la verificación**. Las dos dan el documento al momento,
  sin cuenta y sin correo.
- **Con límites:** los del **IMSS** (vigencia de derechos, no derechohabiencia, NSS, semanas cotizadas). Piden un correo
  que **queda asociado a la CURP del cliente**, y el documento llega a ese correo. Tiene que ser el correo del cliente,
  nunca el de la tienda.
- **El recibo de CFE, en el celular del cliente** ([revisado el 2026-10-31](recibo-cfe.md)): en la app CFE Contigo el
  cliente entra con su cuenta de Google, sin contraseña nueva, agrega su servicio con el número y manda el PDF. Con una
  cuenta de la tienda, no: la app bloquea cuentas con muchos servicios y CFE pide uso personal.
- **No conviene:** el **acta de nacimiento** (cuenta Llave MX y pago en línea), la **constancia del SAT** (contraseña o
  e.firma) e **INFONAVIT** (cuenta). La tienda no debe teclear ni guardar contraseñas de clientes. Si el cliente trae
  el PDF, se imprime como cualquier archivo.
- **ISSSTEP:** no encontré un trámite en línea de no derechohabiencia; es en sus oficinas. El **ISSSTE** federal sí
  tiene uno, con solo la CURP: va en [otros trámites](#otros-trámites-para-revisar).
- **La idea del dueño funciona casi tal cual,** con dos ajustes: el cajero escribe (o pega) en la página oficial en
  lugar de que el POS la llene solo, y en la CURP **no hay captcha que resolver**: es invisible y, si Google duda, la
  búsqueda falla y se vuelve a intentar.
- **No hay una API para la tienda** (una conexión directa con RENAPO): el servicio web de la CURP es solo para
  dependencias de gobierno con convenio, y esos convenios lo suspenden si se usa con fines de lucro. Con CFE tampoco
  hay una.
- **Lo que gana la tienda** frente al navegador de siempre: el trámite se cobra solo, la impresión no se cobra dos
  veces, no quedan CURP ni PDF de clientes en Descargas, en el historial ni en el autollenado del navegador, y en el
  pico hay una cola.
- **Con poco volumen, lo que paga la base es la CURP** (diaria y con picos) y no dejar datos de clientes regados en la
  PC. Una vez hecha la base, cada trámite más es chico; el recibo de CFE (13 en tres meses) no la pagaría solo.
- **No es urgente:** eleventa no tiene trámites, así que va después de alcanzarla. Mientras tanto hay [prácticas que no
  piden código](#mientras-tanto-sin-programar-nada).

## La tabla

| Trámite | Sitio oficial | Qué pide | Captcha o cuenta | Cómo sale el documento | Veredicto | Esfuerzo |
|---|---|---|---|---|---|---|
| **CURP** | [gob.mx/curp](https://www.gob.mx/curp/) (RENAPO, Gobernación) | La CURP, o nombre, apellidos, fecha y estado de nacimiento y sexo | reCAPTCHA invisible de Google (no hay casilla) y el filtro contra robots de Akamai. Sin cuenta | **PDF al momento** («Descargar pdf») | **Sí, dentro del POS** | La base (mediano) y la CURP encima (chico) |
| **Recibo de CFE** | App [CFE Contigo](https://play.google.com/store/apps/details?id=mx.com.cfe.cfecontigo&hl=es_MX) y [MiCFE](https://app.cfe.mx/Aplicaciones/CCFE/MiEspacio/Login.aspx) (CFE) | App: una cuenta (con Google, Apple o correo) y el número de servicio o el código de barras del recibo. Web: además el nombre del titular y el total de un recibo reciente | **Cuenta**; la web, también captcha de imagen | PDF del último recibo, dentro de la cuenta | **En el celular del cliente**, con su Google. Con una cuenta de la tienda, no | Nada (con el panel, elegir el PDF) |
| **Vigencia de derechos (IMSS)** | [Servicios digitales del IMSS](https://serviciosdigitales.imss.gob.mx/gestionAsegurados-web-externo/vigencia) | CURP, NSS y correo, que **queda asociado a la CURP** | Captcha de imagen. Sin cuenta | **Liga al correo** del cliente y de ahí el PDF. Vale solo ese día | **Con límites:** solo con el correo del cliente | Chico |
| **No derechohabiencia (IMSS)** | [IMSS Digital](https://digital.imss.gob.mx/derechohabiencia/tramite/solicitudConstancia) | CURP y correo (asociado a la CURP) | Captcha de imagen. Sin cuenta | Al correo, según el IMSS (un medio dice que también en pantalla: por verificar). Vale solo ese día | **Con límites** (como la vigencia) | Chico |
| **No derechohabiencia (ISSSTEP)** | [issstep.puebla.gob.mx](https://issstep.puebla.gob.mx/) | — | — | No encontré trámite en línea | **No:** es en sus oficinas | — |
| **Cita para la verificación** | [verificacionvehicular.puebla.gob.mx](https://verificacionvehicular.puebla.gob.mx/) (Gobierno de Puebla) | Verificentro, día y hora; placa, serie, tarjeta de circulación, marca, submarca, modelo, año, color, estado y combustible; nombre y correo | Ninguno. Sin cuenta | **PDF al momento** («Descargar cita»); se recupera con el folio o la placa | **Sí, dentro del POS** | Chico |

**El esfuerzo, a grandes rasgos** (chico: días; mediano: una o dos semanas, con las pruebas en Windows):

- **La base** se hace una vez: el panel, atrapar el documento, imprimir, cobrar y borrar. Es mediana.
- **Cada trámite** encima es chico: su dirección, su producto y cómo sale su documento.
- **Ligar la impresión con el trámite** toca el [módulo de impresión](impresion.md) (mediano). Su agente se lanza solo
  cuando el dueño lo pide.

## El diseño recomendado

Esto es para la **caja de Windows**: la ventana WPF que muestra la caja con WebView2, el navegador de Edge que la caja
ya usa por dentro. La caja en el navegador va [aparte](#en-la-caja-del-navegador).

### El panel «Trámites»

- **Se abre desde la venta** (un rápido «Trámites», o el de la CURP; lo decide el dueño) y muestra la lista: CURP, cita
  de verificación, IMSS…
- **La página oficial, tal cual:** al elegir un trámite, el POS abre el sitio oficial dentro de la caja, en un WebView2
  aparte. No se le cambia nada: el cajero ve lo mismo que vería en Edge.
- **Una barra del POS arriba** con:
  - el trámite y su folio (T-0042);
  - **la dirección oficial en grande**, para no caer en sitios falsos que cobran por la CURP;
  - el estado («Esperando el documento»);
  - tres botones: **Terminar**, **Ya salió** (ver [atrapar el documento](#atrapar-el-documento)) y **Abrir en el
    navegador**, por si algo falla.
- **Su propio perfil, aislado:** el panel no comparte cookies, historial ni autollenado con la caja ni con Edge.
  - Va en modo InPrivate (como la ventana privada de Edge: lo que guarda vive en memoria).
  - Tiene **apagados el autollenado y el guardar contraseñas**. Si no, el siguiente cliente vería sugerencias con la
    CURP del anterior.
  - Al terminar cada trámite se borra lo del perfil: cookies, caché, historial y lo escrito (Microsoft documenta cómo
    borrar cada tipo de dato de un perfil:
    [carpeta de datos de WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder),
    consultada el 2026-10-30).
- **Solo sitios oficiales:** el panel navega solo a una lista fija de dominios (las direcciones de los sitios): gob.mx,
  curp.gob.mx, imss.gob.mx, issste.gob.mx y puebla.gob.mx. Un enlace a otro sitio no se abre ahí. Lo que cada página
  carga de otros lados (el reCAPTCHA de Google, por ejemplo) sí se deja, y las ventanas nuevas que abra el sitio se
  quedan en el panel.
- **Ligero para la caja 2:** lo recomendado es la misma carpeta de datos de la caja, con otro perfil. Microsoft
  recomienda perfiles en lugar de carpetas aparte porque cada carpeta arranca su propio navegador, que pesa en memoria.
  Cuánto pesa en la caja 2, que tiene pocos recursos, se mide en la tienda.

### (a) El cajero escribe o (b) el POS llena

| | (a) El cajero escribe en la página oficial | (b) Un formulario del POS llena la página |
|---|---|---|
| **Lo que gana** | Nada que mantener: si el sitio cambia, sigue funcionando | Se escribe una sola vez, y el POS revisa el formato antes de ir al sitio (la CURP trae un dígito verificador) |
| **Lo que cuesta** | Escribir en la página (la CURP son 18 letras y números) | Se rompe sin avisar cuando el sitio cambia sus campos, y se nota con un cliente enfrente. Varias páginas (las hechas con Angular o React) no toman un valor puesto por fuera sin trucos |
| **Los términos** | Es lo que el sitio espera | Los de gob.mx no lo prohíben ni lo permiten; piden que la información sea propia y auténtica. La CURP usa un reCAPTCHA que califica toda la sesión: llenar por script podría bajar la calificación (no se sabe) |
| **El botón y el captcha** | Del cajero | Del cajero |

**Recomendación: (a), con una ayuda para el pico.** La [cola del pico](#el-pico-hasta-100-al-día) guarda la CURP ya
revisada y, cuando le toca, **la copia al portapapeles**: el cajero la pega con Ctrl+V en la página oficial y da Buscar.
Se escribe una vez, el POS revisa el dígito verificador y nadie le mete mano a la página oficial. La copia se marca para
que no quede en el historial del portapapeles de Windows, y se borra al terminar.

### Atrapar el documento

- **Cuando la página oficial descarga un PDF, WebView2 le avisa al POS antes de guardarlo** (el evento
  `DownloadStarting`). Microsoft documenta que el programa puede cancelar la descarga, cambiar dónde se guarda y ocultar
  la ventana de descargas ([DownloadStarting](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.downloadstarting),
  consultada el 2026-10-30). El POS:
  1. lo guarda en una carpeta temporal suya **con un nombre neutro**, «T-0042.pdf». El sitio de la CURP lo llama
     `CURP_` más la CURP de la persona;
  2. revisa que sea un PDF que abre: en agosto de 2026 la página de la CURP entregó PDF que no abrían;
  3. lo muestra en su visor, con **Imprimir** y **Compartir**.
- **Si el sitio no descarga** y solo muestra una página para imprimir, **Ya salió** guarda esa página como PDF (WebView2
  lo permite) y sigue igual.
- **El archivo se borra** al imprimir o compartir, a los 15 minutos si nadie lo usó, y al abrir la caja (por si se cerró
  a la mitad de un trámite).
- **Nada personal se anota.** En el registro de la caja queda «Trámite T-0042 · CURP · documento atrapado (1 página)».
  Nunca la CURP, el nombre, el nombre original del archivo ni la dirección de una liga (las ligas del IMSS llevan una
  clave).
  - [LoQueEsSecreto](../src/PapeleriaPos.Servicio/LoQueEsSecreto.cs) tacha un valor cuando su nombre es el de un
    secreto (una clave, un PIN, un código). La CURP o el nombre de un cliente no se llaman así: no se tacharían.
  - Por eso la regla es otra: **lo del trámite no se escribe nunca** en el registro de la caja, en el del servicio ni en
    lo que junta el piloto.

### Imprimir sin cobrar dos veces

- **El nombre del trabajo lleva el folio:** el POS manda el PDF a la cola de la WF-6590 de la caja 2 como
  **«T-0042 · CURP»**, nunca con el nombre ni la CURP del cliente (el nombre del trabajo se ve en la cola de Windows y
  lo guarda el módulo). Si se imprime desde el visor de WebView2, lo más probable es que el trabajo tome el nombre del
  archivo, que ya es «T-0042.pdf»: también sirve. Qué nombre llega de verdad al agente se verifica en Windows.
- **El agente lo mide como siempre.** Al ver «T-…», el servicio **lo liga al trámite**:
  - guarda su costo (tinta y papel), para la utilidad del trámite;
  - **no le pone precio ni sale en F2 «por cobrar»**: dice «Incluida en el trámite T-0042».
- **Es el mismo mecanismo que ya está pensado para los pedidos** («P-0042 · archivo.pdf», en
  [pedidos](pedidos.md#quién-lo-prepara)) y que todavía no está hecho. Se haría una vez para los dos, en el
  [módulo de impresión](../src/Impresiones/README.md).
- **Sin miniatura.** Hoy el agente guarda una miniatura de la primera página de cada trabajo. La de un trámite sería la
  CURP de un cliente guardada en la base: no se guarda.
- **Una reimpresión** desde el visor también va ligada. Si una copia extra se cobra, lo [decide el
  dueño](#decisiones-para-el-dueño).
- **Lo que se imprime fuera del panel** (desde Edge, como hoy) sale en F2 como cualquier impresión y se cobra como hoy.
- **En la caja principal**, mientras sigan eleventa y Control Print, no hay panel: ahí se sigue como hoy.

### Cobrar

- **Cuando el documento sale, el POS agrega el trámite al ticket en curso:** «CURP · T-0042 · $10». Se cobra con F12,
  como todo.
- **Si el documento no sale** (el sitio falló, el PDF no abre, no apareció la persona), no se agrega nada.
- **En el piloto, la línea llega a eleventa con su producto,** como hoy las impresiones llegan como IMP. CURP ($10) y
  RECIBO ($15) ya existen allá; los nuevos (cita de verificación, IMSS) necesitan su producto en eleventa o un
  «TRÁMITE» general (decisión del dueño).

### Compartir

| Cómo | Privacidad | Esfuerzo | Lo que piden los clientes |
|---|---|---|---|
| **WhatsApp de la tienda** (WhatsApp Web en la caja, como hoy) | El PDF queda en el chat y en el celular de la tienda hasta que se borra. El POS no puede mandarlo solo: sin la API oficial, las librerías que lo automatizan se [descartaron](whatsapp.md#descartado) porque Meta banea esos números | Chico: el POS deja el archivo listo para arrastrarlo al chat y después lo borra | Lo más pedido, según el dueño (se confirma en la tienda) |
| **Correo** | Queda en los «Enviados» de la tienda; hay que darle al POS una cuenta y su clave | Mediano | Poco (se confirma en la tienda) |
| **QR para bajarlo de la caja por el Wi-Fi** | El celular del cliente tendría que entrar a la red de las cajas, y la regla es que el público no comparta red con ellas ([seguridad](notas.md#seguridad)) | Mediano | — |
| **Memoria USB** | Bien, si el POS solo escribe en ella y no abre nada de lo que trae | Chico: «Guardar en USB» con el diálogo de Windows que la caja ya usa | Algunos |

**Recomendación: WhatsApp (a mano, como hoy) y USB.** Después de mandarlo, el cajero lo borra del chat de la tienda
(«Eliminar para mí»: al cliente le queda) y el POS borra su copia. Conviene que el WhatsApp de la tienda no guarde en la
galería del celular lo que manda y recibe. El correo, solo si se pide seguido. El QR por Wi-Fi, no.

### Privacidad

- **El aviso de privacidad,** a la vista en el mostrador (el simplificado) y el integral a la mano. La ley lo pide
  ([abajo](#la-ley-de-datos-personales-2025)) y ya estaba pendiente para los pedidos ([pedidos](pedidos.md#archivos)).
- **Solo lo necesario, y nada después.**
  - La cola guarda solo lo que pide la página oficial, y se borra al entregar o, a más tardar, al cierre del día.
  - No queda ni la huella de la CURP: con ella sola se identifica a la persona, así que la huella también sería un dato
    personal. Los pedidos guardan la huella de sus archivos para detectar repetidos; un trámite no la necesita.
  - Del trámite queda solo el tipo, la hora, quién lo hizo, el precio y si salió.
- **Contraseñas de clientes: nunca.** Si un trámite pide la cuenta del cliente (CFE, SAT, Llave MX, INFONAVIT), el
  cliente lo hace en su celular y la tienda imprime lo que le manda. Al revés, tampoco: los servicios de los clientes no
  se agregan a una cuenta de la tienda ([CFE](recibo-cfe.md#la-ley-de-datos-personales)).
- **El correo de la tienda, nunca en el IMSS:** el IMSS lo asociaría a la CURP del cliente.
- **Lo que se queda en la PC:**
  - el perfil del panel se borra en cada trámite;
  - el PDF se borra al usarlo;
  - Windows guarda el trabajo en su cola mientras el agente lo mide (para eso pide «Conservar documentos impresos»), y
    el agente lo quita después, como hoy.
- **El personal:** la ley pide que quien trata datos guarde la confidencialidad aun después de dejar el trabajo
  (artículo 20). Basta una línea en las reglas de la tienda.

### Sin internet o con el sitio caído

- **Sin internet,** el panel lo dice y no deja empezar: «Sin internet: los trámites no funcionan. Toma los datos en la
  cola si el cliente quiere volver, o pídele que regrese». La venta sigue: la caja ya trabaja sin internet.
- **Con internet pero el sitio caído,** el panel ofrece **Reintentar** y no cobra nada. El mensaje de la CURP en esos
  casos es «El servicio no está disponible, por favor intenta más tarde.».
- **En el pico,** la cola guarda los pendientes para cuando el sitio vuelva.

### En la caja del navegador

- **Los sitios oficiales no se dejan meter en otra página** (un iframe). Lo revisado el 2026-10-30:
  - gob.mx/curp manda `X-Frame-Options: SAMEORIGIN` y `frame-ancestors 'self'`: solo se deja mostrar dentro de sí misma;
  - CFE, la página del IMSS (imss.gob.mx) y la Oficina Virtual del ISSSTE, lo mismo;
  - la plataforma del Registro Civil (las actas), `DENY`: en ningún lado;
  - las páginas de los trámites del IMSS (serviciosdigitales y digital.imss.gob.mx) y la de verificación no mandan ese
    aviso. Aun así, la página del POS no puede ver ni atrapar lo que se descarga dentro de otro sitio, y las cookies de
    un sitio metido en otro suelen fallar.
- **Plan B:** en el navegador, el panel abre el sitio oficial en otra pestaña.
  1. El cajero hace el trámite ahí y el PDF cae en Descargas.
  2. Lo **elige o lo arrastra al POS**, que lo imprime y agrega el trámite al ticket.
  3. El POS le recuerda borrarlo de Descargas: el navegador no deja que una página borre archivos de la PC.

  Es la misma idea del panel de descargas recientes de [WhatsApp, etapa 1](whatsapp.md#etapa-1-captura-desde-la-pc-fase-4).

## La CURP, paso a paso

### Un día normal

1. El cajero abre el trámite CURP (hoy el rápido CURP, Alt+2, vende 10 impresiones; el dueño decide si pasa a abrir el
   trámite). Se abre gob.mx/curp en el panel.
2. Escribe la CURP en la página oficial. Si el cliente no la sabe, usa la pestaña «Datos personales»: nombre, apellidos,
   día, mes y año de nacimiento, sexo y estado de nacimiento.
3. Da **Buscar**. El reCAPTCHA es invisible: no hay casilla ni imágenes. Si Google duda de la sesión, la página dice que
   no se pudo y se vuelve a intentar; si sigue fallando, **Abrir en el navegador**.
4. **Revisa con el cliente** el nombre y la fecha que muestra la página: con «Datos personales» puede salir otra persona
   con datos parecidos.
5. Da **Descargar pdf**. El POS lo atrapa y lo muestra.
6. **Imprimir** (una copia) y, si el cliente lo pide, **Compartir**.
7. El ticket ya trae «CURP · T-0042 · $10». F12.
8. **Terminar** (o se termina solo al imprimir y compartir): se borran el PDF y lo del perfil del panel.

A ojo, uno o dos minutos por CURP si el sitio responde bien; se mide en la tienda.

### El pico (hasta 100 al día)

**Cuándo llega:** con lo que pide la CURP del alumno y de sus papás.

- Las **preinscripciones de la SEP en Puebla** son en febrero. En 2026 fueron entre el 3 y el 13 o el 27 de febrero,
  según el nivel (los medios no coinciden), y para 2027 se proponen del 2 al 12 de febrero
  ([Expansión](https://politica.expansion.mx/mexico/2026/02/03/ya-empezaron-preinscripcioes-2026-preescolar-primaria-secundaria),
  2026-02-03).
- El **registro a la beca Rita Cetina** está abierto **del 26 de octubre al 6 de noviembre de 2026**, según el aviso de
  la portada de [gob.mx](https://www.gob.mx/) (consultada el 2026-10-30): puede haber un pico estos días. Ese registro
  pide la Llave MX del tutor ([Crónica](https://www.cronica.com.mx/nacional/2026/02/10/beca-rita-cetina-2026-fecha-de-inicio-de-registro-en-linea-para-estudiantes-de-primaria/),
  2026-02-10): la tienda no lo hace, pero imprime las CURP que pide.

**El flujo, con una cola:**

1. **Tomar los datos rápido.** Una pantalla «Cola de CURP»: el cajero escribe la CURP y el POS revisa al momento el
   formato y el dígito verificador (un error de dedo no llega a gob.mx). Si no la saben, se toman los datos. Cada uno
   lleva un **turno** (A-23) que se le dice al cliente. Nada más: ni teléfono ni nombre si no hacen falta.
2. **Procesar** en un rato libre, o en la otra caja. «Siguiente»: el POS copia la CURP, el cajero la pega y da Buscar y
   Descargar, y el POS imprime con el turno en el nombre del trabajo («T-0042 · A-23») para ordenar las hojas.
3. **Entregar y cobrar.** Cuando el cliente vuelve con su turno, «A-23» agrega el trámite al ticket, con lo impreso ya
   ligado, y se cobra. Si no vuelve, al cierre se borra su dato y la hoja queda como no vendida.
4. **Si el sitio falla,** la cola espera y se reintenta. Cada falla se cuenta, sin datos, para saber cuánto estuvo
   caído.

**¿Vale la pena llenar la página por script en el pico?** No. Copiar y pegar da casi toda la ganancia (se escribe una
vez y sin errores) y no se rompe cuando gob.mx cambia; llenar por script ahorraría unos segundos por CURP. La cola es la
misma idea de los [pedidos](pedidos.md) (un trámite ya está en su lista): si los pedidos se hacen antes, la cola es un
pedido de tipo trámite.

## Lo que se encontró en cada trámite

Todo se revisó el 2026-10-30, salvo lo que diga otra fecha. Lo marcado como «medio» viene de la prensa, no de una
fuente oficial.

### CURP

- **El sitio:** [gob.mx/curp](https://www.gob.mx/curp/), de RENAPO (Secretaría de Gobernación). La página consulta a
  RENAPO por gob.mx (`www.gob.mx/v1/renapoCURP/consulta`) y baja el PDF de `consultas.curp.gob.mx`.
- **Qué pide:** la CURP, o en «Datos personales»: nombre, primer y segundo apellido, día, mes y año de nacimiento, sexo
  (mujer, hombre o no binario) y estado de nacimiento.
- **Captcha:** reCAPTCHA Enterprise de Google, del que califica sin preguntar (invisible): corre solo al cargar la
  página y al buscar. No hay casilla que marcar ni imágenes que resolver. La página también trae el filtro contra robots
  de Akamai, la red que reparte gob.mx. Sin cuenta, sin correo y sin código por SMS.
- **El documento:** al dar «Descargar pdf», la página pide el PDF, lo arma en el navegador y lo descarga como un archivo
  normal, llamado `CURP_` más la CURP. WebView2 ve esa descarga.
- **Es gratis:** consultarla e imprimirla no cuesta. La ficha oficial que se citaba antes ya no abre (manda a la portada
  de gob.mx); los medios lo repiten y advierten de sitios falsos que cobran
  ([Excélsior](https://www.excelsior.com.mx/nacional/como-consultar-mi-curp-en-linea/1377637), 2025-05-07). La tienda
  cobra su servicio y la impresión, no la CURP.
- **Fallas conocidas:**
  - febrero de 2024, en plenas preinscripciones: «El servicio no está disponible» al buscar y al descargar
    ([N+](https://www.nmas.com.mx/nacional/curp-que-pasa-descarga-falla-plataforma-en-plenas-preinscripciones-2024/),
    2024-02-06);
  - julio de 2026: CURP que salían como inexistentes después de una actualización de la base
    ([Eje Central](https://www.ejecentral.com.mx/documentos-sin-control-actualizacion-provoca-desaparicion-de-algunas-curp),
    medio);
  - 14 de agosto de 2026: la búsqueda funcionaba, pero el PDF descargado no abría
    ([Por Esto](https://www.poresto.com/mexico/2026/8/14/reportan-falla-al-descargar-la-curp-en-pdf-desde-pagina-oficial-del-gobierno-de-mexico.html));
  - la página tiene un aviso de mantenimiento que se prende desde un archivo de gob.mx; el último que tiene guardado es
    de media hora en una madrugada de septiembre de 2024, y hoy está apagado.
- **La CURP biométrica es otra cosa:** se tramita en persona, con cita en RENAPO, y su constancia llega al correo que se
  da ahí ([Expansión](https://politica.expansion.mx/mexico/2026/06/03/curp-biometrica-requisito-indispensable-poder-descargar),
  2026-06-03). La CURP de siempre se sigue descargando en gob.mx/curp: la página funcionaba el 2026-10-30. La tienda no
  puede hacer la biométrica.
- **Veredicto: sí, dentro del POS.** Lo que gana la tienda: el cobro automático, la impresión ligada, nada en Descargas
  y la cola en el pico.

### Recibo de CFE

Se revisó otra vez el 2026-10-31, a pedido del dueño. El detalle, con sus fuentes, está en [recibo de
CFE](recibo-cfe.md).

- **La consulta sin cuenta se acabó.** La dirección que usaba gob.mx
  (`app.cfe.mx/Aplicaciones/CCFE/ReciboDeLuzGMX/Consulta`) muestra un aviso y manda a **MiCFE**: usuario, contraseña y
  captcha de imagen, sin entrada con Google. Según un servicio no oficial que la usaba, CFE la retiró el 12 de
  septiembre de 2026. Las guías que la describen, como una de mayo de 2026
  ([Infobae](https://www.infobae.com/mexico/2026/05/03/cfe-como-descargar-el-recibo-de-luz-desde-el-celular-gratis-y-sin-necesidad-de-registrarse/)),
  ya no sirven.
- **En la app es fácil, como dijo el dueño.** En CFE Contigo se entra con Google (o Apple, o correo), se agrega un
  servicio con su número o escaneando el código de barras del recibo, y sale el último recibo en PDF. No pide ser el
  titular.
- **Pero no es para hacerlo con una cuenta de la tienda:**
  - reseñas de octubre de 2026 dicen que la app bloquea la cuenta al pasar de unos 10 servicios, o al borrar uno y
    agregar otro, y que ya no se recupera;
  - los términos de CFE piden uso personal, no comercial, y quien tiene la cuenta responde por todo lo que se haga con
    ella;
  - los servicios de clientes en una cuenta de la tienda serían datos de terceros a cargo de la tienda;
  - la web, además, pide el nombre del titular y el total de un recibo reciente para agregar un servicio.
- **No hay API:** ni oficial ni para negocios. Lo que se vende como «API del recibo» es de terceros, sin convenio
  conocido con CFE, y no se usa.
- **El volumen:** en eleventa, RECIBO salió 13 veces en tres meses (copia del 2026-10-24).
- **Veredicto: en el celular del cliente.** El cliente entra a la app con su Google (no teclea contraseña), agrega su
  servicio y manda el PDF al WhatsApp de la tienda; se imprime y se cobra RECIBO ($15). No hay nada que programar; con
  el panel, el cajero elige el PDF y se cobra solo.

### Vigencia de derechos (IMSS)

- **El sitio:** [Servicios digitales del IMSS](https://serviciosdigitales.imss.gob.mx/gestionAsegurados-web-externo/vigencia).
  Es el trámite IMSS-02-020-B y es gratis ([ficha](https://www.imss.gob.mx/tramites/imss02020)).
- **Qué pide:**
  - CURP, NSS, correo y su confirmación (el campo de la confirmación no deja pegar);
  - las letras de un captcha de imagen («usa mayúsculas y minúsculas»).
- **El correo queda ligado:** la página avisa que el correo **queda asociado a la CURP**, y si ya hay otro correo
  ligado ofrece cambiarlo. Además rechaza los correos de Gmail con «+» o «-», así que no se puede usar una variante del
  correo de la tienda.
- **El documento:** después de Continuar, el IMSS manda una liga de confirmación al correo; al abrirla se descarga la
  constancia ([IDC](https://idconline.mx/seguridad-social/2026/07/31/vigencia-de-derechos-imss-descarga-tu-constancia-en-linea),
  2026-07-31). La constancia **vale solo el día en que se emite** (ficha del IMSS).
- **Protección:** Imperva, un filtro contra robots delante del sitio. La página no manda el aviso que impide meterla en
  otra página.
- **Las vías del cliente:** la app IMSS Digital o su Unidad de Medicina Familiar (ficha del IMSS).
- **Veredicto: con límites.**
  - En el POS, el cajero escribe los datos con el correo **del cliente** y resuelve el captcha.
  - El cliente abre la liga en su celular y, o baja el PDF y lo manda, o le pasa la liga a la tienda por WhatsApp y el
    panel la abre y atrapa el PDF. Que la liga funcione en otro navegador se verifica en la tienda.
  - Con el correo de la tienda, no: quedaría como el correo del cliente en el IMSS.
  - Lo que gana la tienda es menos que con la CURP: el cobro automático y que el PDF no quede en Descargas. La espera
    del correo es la misma.

### No derechohabiencia (IMSS e ISSSTEP)

- **IMSS:** la [página informativa](https://www.imss.gob.mx/constancia-no-derechohabiencia) lleva a
  [IMSS Digital](https://digital.imss.gob.mx/derechohabiencia/tramite/solicitudConstancia).
  - Pide la CURP (los demás datos salen de ella), el correo con su confirmación y un captcha de imagen de 5 letras.
  - Dice que el correo queda asociado a la CURP.
  - Según sus preguntas frecuentes, el documento llega a ese correo y el trámite solo se hace en línea. Una nota de
    [Crónica](https://www.cronica.com.mx/nacional/2026/09/28/constancia-de-no-derechohabiencia-del-imss-como-obtenerla-y-para-que-sirve/)
    (2026-09-28) dice que aparece para descargarlo e imprimirlo. **Por verificar** cuál de las dos.
  - Vale solo el día en que se emite, y no se emite si la persona sí tiene derecho al servicio médico.
- **ISSSTEP** (los trabajadores del gobierno de Puebla): su [sitio](https://issstep.puebla.gob.mx/) no tiene un trámite
  en línea de no derechohabiencia, y sus boletines hablan de la vigencia de derechos como un servicio de sus oficinas.
  **Es presencial.**
- **ISSSTE** (el federal), por si es lo que piden los clientes: su Oficina Virtual da la acreditación de no afiliación
  con solo la CURP. Va en [otros trámites](#otros-trámites-para-revisar).
- **Veredicto:** el del IMSS, con límites (como la vigencia); el del ISSSTEP, no.

### Cita para la verificación (Puebla)

- **El sitio:** [verificacionvehicular.puebla.gob.mx](https://verificacionvehicular.puebla.gob.mx/), del Gobierno de
  Puebla. Se actualizó en septiembre de 2026.
  - Antes las citas iban en citasenlinea.puebla.gob.mx, que se cayó en junio de 2023 a una semana del plazo
    ([Milenio](https://amp.milenio.com/politica/gobierno/colapsa-portal-citas-verificacion-vehicular-puebla),
    2023-06-22).
- **Qué pide,** en cuatro pasos:
  1. el verificentro;
  2. el día y la hora;
  3. el vehículo: placa, número de serie, número de tarjeta de circulación, marca, submarca, modelo, año, color, estado
     y combustible (y una casilla de verificación voluntaria para los de otro estado);
  4. el contacto: nombre completo (o razón social, si es empresa) y correo.
- **Captcha:** ninguno. Sin cuenta.
- **El documento:** la confirmación muestra el folio y un QR, y «Descargar cita» baja un PDF. La cita se vuelve a buscar
  con el folio o la placa, para descargarla o cancelarla.
- **Los costos,** según el mismo sitio: la verificación cuesta $628 (hologramas 00, 0, 1 y 2) y la multa por verificar
  tarde, $2,346. Agendar la cita no pide pago.
- **Lo que se lleva al verificentro,** según la confirmación: el folio impreso, una identificación, la tarjeta de
  circulación y el certificado anterior, entre otros.
- **No es una API:** la página habla con una interfaz interna suya (`/api/cita/…`). No está ofrecida a terceros, y
  usarla directo sería automatizar el sitio.
- **Veredicto: sí, dentro del POS.** Es donde más se escribe (unos 15 campos), pero todo viene de la tarjeta de
  circulación que el cliente trae en la mano, y se escribe en la página oficial, como la CURP.
  - El correo: el del cliente, porque ahí le llega lo que mande el sitio. Si no tiene, lo [decide el
    dueño](#decisiones-para-el-dueño).
  - Lo que gana la tienda: el PDF de la cita sale y se imprime en el mismo paso, y el trámite se cobra solo.

## Otros trámites, para revisar

Revisados el 2026-10-30.

| Trámite | Sitio | Lo que encontré | Veredicto |
|---|---|---|---|
| **Acreditación de no afiliación (ISSSTE)** | [Oficina Virtual del ISSSTE](https://oficinavirtual.issste.gob.mx/Servicios/Acreditaci%C3%B3n-de-No-Afiliaci%C3%B3n) | Solo la CURP y un reCAPTCHA de Google (parece el de la casilla; por confirmar). Si el documento sale al momento, por verificar | **Proponer:** parece tan simple como la CURP |
| **NSS, asignación o localización (IMSS)** | [Servicios digitales del IMSS](https://serviciosdigitales.imss.gob.mx/gestionAsegurados-web-externo/asignacionNSS) | CURP y correo (asociado a la CURP), captcha de imagen, y un paso para el domicilio. Cómo llega, por verificar: la [ficha](https://imss.gob.mx/tramites/imss02008) no lo dice. Gratis | Con límites, como la vigencia |
| **Semanas cotizadas (IMSS)** | [SISEC del IMSS](https://serviciosdigitales.imss.gob.mx/semanascotizadas-web/usuarios/IngresoAsegurado) | CURP, NSS, correo y celular (cada uno con su confirmación) y captcha de imagen; el correo queda ligado. Cómo llega, por verificar | Con límites, como la vigencia |
| **Acta de nacimiento** | gob.mx/ActaNacimiento manda a [miregistrocivil.gob.mx](https://www.miregistrocivil.gob.mx/), la Plataforma Nacional del Registro Civil | Se elige el estado; usa reCAPTCHA. La página pide la Llave MX (la cuenta de identidad del gobierno, con contraseña) para ver el estado de un trámite, y según medios también para pedir el acta, desde 2025 o 2026: no coinciden ([Expansión](https://politica.expansion.mx/mexico/2026/03/17/cambia-tramite-acta-de-nacimiento-certificada-llave-mx), 2026-03-17). Se paga en línea. En Puebla, $170 según un medio que cita la Ley de Ingresos ([Cultura Colectiva](https://news.culturacolectiva.com/noticias/mexico/acta-de-nacimiento-puebla-en-linea-costo-requisitos/), 2026-08-31) | **No:** cuenta y pago del cliente |
| **Constancia de situación fiscal (SAT)** | sat.gob.mx | RFC y contraseña o e.firma; sin ellas, SAT ID la manda al correo en días ([Bloomberg Línea](https://www.bloomberglinea.com/latinoamerica/mexico/constancia-de-situacion-fiscal-como-sacarla-en-linea-y-en-cuanto-tiempo-la-entrega-el-sat-en-2026/), medio) | **No:** la contraseña o la e.firma del cliente (la e.firma vale como su firma) |
| **Precalificación INFONAVIT** | Mi Cuenta Infonavit | Pide iniciar sesión; el sitio rechazó la lectura automática (403), así que no se revisó a fondo | **No:** cuenta del cliente |
| **Antecedentes no penales (Puebla)** | [Ventanilla digital de la Fiscalía](https://fiscalia.puebla.gob.mx/Ventanilla_Digital/Constancia_Digital) | En línea solo si la anterior es de hace un año o menos; si no, cita en persona. Se genera una referencia de pago | **No:** pago, y casi siempre en persona |
| **Licencia y control vehicular (Puebla)** | Ventanilla y citas del estado | Se pagan con línea de captura o tarjeta; la licencia es en persona, con cita en citasenlinea.puebla.gob.mx (con reCAPTCHA) | **No** por ahora (pagos); la cita, quizá después |
| **Preinscripción SEP Puebla** | preinscripcion.seppue.gob.mx | Solo abre en febrero; hoy su certificado no corresponde a su dirección (fuera de temporada) | **Revisar en enero:** llega junto con el pico de la CURP |
| **Registro a la beca Rita Cetina** | gob.mx | Abierto del 26 de octubre al 6 de noviembre de 2026; pide la Llave MX del tutor (medio) | **No** dentro del POS, pero explica picos de CURP como el de estos días |

Otros que también se piden, como la cita del pasaporte o la del INE, no se revisaron.

## La ley y los términos

### La ley de datos personales (2025)

- **Sí, la de 2025 reemplazó a la de 2010.** La nueva Ley Federal de Protección de Datos Personales en Posesión de los
  Particulares se publicó en el DOF el 20 de marzo de 2025, entró en vigor al día siguiente y abrogó la de 2010. Su
  texto vigente tiene una reforma del 14 de noviembre de 2025 ([Cámara de
  Diputados](https://www.diputados.gob.mx/LeyesBiblio/pdf/LFPDPPP.pdf), consultado el 2026-10-30).
  - La autoridad ya no es el INAI: es la **Secretaría Anticorrupción y Buen Gobierno** (artículo 2).
  - No encontré un reglamento nuevo publicado.
- **Aplica a la tienda:** aplica a cualquier persona o negocio privado que trate datos personales. Solo se exceptúa el
  uso personal, sin fines comerciales (artículo 1).
- **Lo que pide:**
  - **Un aviso de privacidad** (artículos 14 a 16) que diga quién es el responsable y su domicilio, qué datos se toman,
    para qué, cómo limitar su uso, cómo ejercer los derechos ARCO (acceso, rectificación, cancelación y oposición) y
    cómo se avisan los cambios. Cuando los datos se toman por medios electrónicos basta el **simplificado** (las
    primeras cuatro cosas y dónde ver el integral).
  - **El consentimiento** (artículos 7 a 9). Por regla vale el tácito: el aviso estaba a la vista y el cliente no se
    opuso. Los datos financieros o patrimoniales piden consentimiento expreso, y los **sensibles** (la salud, por
    ejemplo), **expreso y por escrito**. No hace falta cuando los datos se necesitan para cumplir lo que el cliente
    pidió (artículo 9, fracción IV): hacer su trámite. El aviso sí.
  - **Guardarlos lo mínimo** (artículo 10): cuando ya no se necesitan para lo que se tomaron, se borran.
  - **Medidas de seguridad** (artículo 18), y avisar de inmediato al titular de una fuga que lo afecte (artículo 19).
  - **La confidencialidad del personal,** aun después de dejar el trabajo (artículo 20).
  - **Las multas** van de 100 a 320,000 UMA (artículo 59), y hay cárcel para quien lucre con una fuga o con engaños
    (artículos 62 a 64).
- **Para los trámites:**
  - el aviso a la vista en el mostrador;
  - los datos, solo para hacer el trámite, y borrados al entregar;
  - nada de CURP en los registros, los PDF borrados y el perfil limpio;
  - ningún dato de salud guardado: las constancias del IMSS dicen si alguien tiene servicio médico, no cómo está de
    salud, pero tampoco se guardan.

### Lo que dicen los sitios

- **gob.mx** ([términos](https://www.gob.mx/terminos)):
  - autoriza ver y descargar sus materiales para uso personal y no comercial, y no pasarlos a terceros;
  - pide que quien hace un trámite dé información propia y auténtica;
  - prohíbe dañar o estorbar sus sistemas;
  - no dice nada de ayudar a otra persona con su trámite ni de abrir la página dentro de un programa.

  En la práctica, la tienda ayuda al titular, que está presente, con su propio documento, y cobra su servicio y la
  impresión, no el documento; al cliente se le dice que el trámite es gratis en gob.mx. Es una zona gris donde las
  papelerías trabajan desde siempre, pero no hay una autorización expresa.
- **CFE** ([términos](https://www.cfe.mx/Pages/TerminosyCondiciones.aspx), revisados otra vez el 2026-10-31): uso
  personal y no comercial, también en sus aplicaciones; la cuenta es personal y quien la tiene responde por todo lo que
  se haga con ella. Por eso la tienda no usa una cuenta suya para recibos de clientes: el cliente lo hace en su celular.
- **IMSS:** no encontré términos propios en esas páginas. Lo que sí dicen: el correo queda asociado a la CURP, y sus
  avisos de privacidad dicen para qué usan los datos.
- **Abrirlos dentro de la caja:** el aviso que impide meter un sitio en otra página (X-Frame-Options) aplica a los
  iframes, no a un navegador como WebView2, que abre la página completa, como Edge.
- **Automatizar:** ningún sitio lo permite expresamente, y la propuesta no automatiza nada: el cajero escribe, el cajero
  da clic y el cajero resuelve los captchas.

### ¿Hay una API oficial?

- **CURP (RENAPO): confirmado que no es para la tienda.** RENAPO da sus «Servicios Web de CURP» por **convenio de
  coordinación** a dependencias de gobierno. Por ejemplo, a la Secretaría de Educación de Guanajuato
  ([DOF](https://dof.gob.mx/nota_detalle.php?codigo=5793937), 2026-07-20) y a la Secretaría de Bienestar del Estado de
  México ([DOF](https://dof.gob.mx/nota_detalle.php?codigo=5799137), 2026-09-21). Esos convenios suspenden el servicio,
  entre otras causas, por **usarlo con fines de lucro**.
- **La Plataforma Única de Identidad** (lineamientos en el DOF del 27 de noviembre de 2025) tampoco: la usan las
  autoridades ([IDC](https://idconline.mx/corporativo/2025/11/27/publican-reglas-de-la-plataforma-unica-de-identidad)).
- **CFE: confirmado que no hay una para la tienda** (2026-10-31). No tiene portal para programadores, los comisionistas
  solo cobran, y lo que se vende como «API del recibo» son servicios no oficiales o de verificación de identidad, sin
  convenio conocido con CFE, que piden además el nombre del titular y el total de un recibo reciente
  ([detalle](recibo-cfe.md#hay-una-api)).
- **IMSS y verificación:** no encontré una API pública para estos trámites. Sus páginas hablan con interfaces internas
  suyas; usarlas directo sería automatizar el sitio, justo lo que no se quiere.

## Riesgos

- **Los sitios cambian, y rápido.** Entre 2025 y 2026, la consulta del recibo de CFE pasó a pedir cuenta (el 12 de
  septiembre de 2026, según un tercero), el acta se fue a una plataforma nueva con Llave MX y la página de verificación
  se rehízo. Por eso:
  - nada de llenar por script;
  - cada trámite es solo su dirección, su producto y cómo sale su documento;
  - si algo cambia, **Abrir en el navegador** y elegir el PDF siguen funcionando.
- **Los captchas.** El de la CURP es invisible: si Google califica mal la sesión (por ejemplo, muchas consultas seguidas
  desde la misma conexión en el pico), la búsqueda falla y no hay nada que resolver: se reintenta o se usa Edge. Los del
  IMSS son de imagen: los resuelve el cajero.
- **Los filtros contra robots** (Akamai en gob.mx; Imperva en el IMSS y en CFE): 100 consultas en un día desde la misma
  conexión podrían frenarse. Se ve en el pico.
- **Un PDF que no abre** (como en agosto de 2026): el POS lo revisa antes de imprimir y, si no abre, no cobra.
- **La persona equivocada:** con «Datos personales» puede salir otra persona; se confirma con el cliente antes de
  imprimir.
- **Las copias que quedan:**
  - las miniaturas: hoy el módulo guarda la de cada trabajo impreso, y Control Print también. Una CURP impresa hoy deja
    su miniatura en la base de quien la midió (Control Print en la caja principal, el POS en la caja 2): es una nota
    para el agente del módulo de impresión;
  - WhatsApp guarda lo que se manda;
  - el portapapeles, si guarda historial;
  - la cola de Windows, mientras el agente mide.
- **La ley:** los términos piden uso personal (zona gris); el aviso de privacidad está pendiente; las multas son altas,
  aunque el riesgo práctico de una papelería sea bajo.
- **Las contraseñas de clientes:** la tentación de «hágamelo usted» con la cuenta del cliente. Va una regla firme.
- **Una cuenta de la tienda con servicios de clientes (CFE):** la app de CFE bloquea cuentas con muchos servicios, y
  no se recuperan (reseñas de octubre de 2026). Si fuera la cuenta con el servicio de luz de la propia tienda, se
  perdería también su recibo. Ninguna cuenta de la tienda lleva servicios de clientes.
- **Los sitios de «consulta sin registro» del recibo de luz:** no son de CFE, piden el nombre, el número y el total, y
  CFE advierte que esos sitios roban datos. Ni usarlos ni recomendarlos.
- **El correo de la tienda en el IMSS:** quedaría asociado a la CURP del cliente, y los avisos del IMSS para el cliente
  llegarían a la tienda.
- **La memoria de la caja 2:** un WebView2 más.

## Lo que solo se puede verificar en la tienda

Sábado o domingo, en la caja 2, con un cliente real que esté de acuerdo (o con los datos del dueño, si él quiere):

1. Que el panel atrape el PDF de la CURP (una descarga que el sitio arma en el navegador) y el de la cita de
   verificación, y que el visor los muestre.
2. Que el reCAPTCHA invisible de la CURP no falle más en el panel que en Edge, y cuántas veces sale «El servicio no está
   disponible».
3. Cuánto tarda cada trámite, de principio a fin, en un día normal y en uno de pico.
4. **IMSS:** que la liga del correo abra en el panel (otro navegador que el que la pidió) y baje el PDF; si la no
   derechohabiencia sale en pantalla o solo por correo; cómo llegan el NSS y las semanas cotizadas.
5. **ISSSTE:** qué captcha muestra y si el PDF sale al momento.
6. **Verificación:** que salga el PDF, y si el sitio también manda correo.
7. **Imprimir:** qué nombre de trabajo llega al agente («T-0042 · CURP» o «T-0042.pdf»), que no se guarde la miniatura y
   que el trabajo salga de la cola de Windows.
8. Cuánta memoria usa la caja 2 con el panel abierto.
9. **Qué piden los clientes:** ISSSTEP o ISSSTE, cuántos piden compartir y por dónde.
10. **CFE:** lo que piden hoy la app y MiCFE, que revisa el dueño con la cuenta y el servicio de la tienda: la lista en
    [recibo de CFE](recibo-cfe.md#lo-que-solo-se-sabe-con-una-sesión-real).

Esto entra a la [guía del fin de semana](pruebas-en-tienda.md) cuando se programe.

## Mientras tanto, sin programar nada

Para el pico de estos días (la beca Rita Cetina) y hasta que exista el panel:

- Los trámites, en una **ventana InPrivate de Edge**: no guarda historial ni autollenado. Se cierra entre cliente y
  cliente.
- **Nunca guardar contraseñas de clientes** en el navegador, ni teclearlas: el cliente escribe la suya en su celular.
- **Borrar de Descargas** los PDF de clientes al imprimirlos o, a más tardar, al cierre.
- En el IMSS, **solo el correo del cliente**.
- **El recibo de CFE:** guiar al cliente en su celular (CFE Contigo, entrar con su Google, agregar su servicio y mandar
  el PDF al WhatsApp de la tienda). Nunca en una cuenta de la tienda ni en sitios de «consulta sin registro».
- Cobrar con los productos de siempre (CURP $10, RECIBO $15).

## Decisiones para el dueño

Ninguna se marcó como «decidido, por revisar»: no se va a programar nada por ahora.

| # | Decisión | Recomendación |
|---|---|---|
| 1 | Cuáles entran y en qué orden | CURP y cita de verificación primero. Después el IMSS (con el correo del cliente) y la no afiliación del ISSSTE. El recibo de CFE, en el celular del cliente (decisión 13). Acta, SAT, INFONAVIT y antecedentes, no: se imprime lo que el cliente traiga |
| 2 | Cuándo | Después de alcanzar a eleventa, porque eleventa no tiene trámites. Si el piloto lo permite, la CURP lista antes del pico de febrero de 2027 |
| 3 | (a) o (b) | (a): el cajero escribe en la página oficial; para el pico, la cola con copiar y pegar. Nada de llenar por script |
| 4 | Qué incluye el precio | La CURP a $10 con la primera copia; las copias extra, al precio de la impresión. «Solo compartir», también $10: el servicio es el mismo |
| 5 | Los productos en eleventa | CURP y RECIBO ya existen; para lo nuevo, un producto por trámite (se ve mejor en los reportes que un «TRÁMITE» general) |
| 6 | El rápido CURP (Alt+2) | Que abra el trámite, con un «Solo cobrar» para cuando el cliente trae su propio archivo (vende las 10 impresiones, como hoy) |
| 7 | Compartir | WhatsApp a mano y USB. El correo, después y solo si se pide. El QR por Wi-Fi, no |
| 8 | Cuándo se cobra en el pico | Al entregar, como hoy: sin documento no hay cobro |
| 9 | El aviso de privacidad | Hacerlo ya, para los trámites y los pedidos: el responsable (el dueño o su empresa) y su domicilio, para qué se usan los datos, y un letrero en el mostrador |
| 10 | El cliente sin correo | IMSS: no se hace con el de la tienda; el cliente va a su clínica, usa la app IMSS Digital o abre su correo. Verificación: el de la tienda solo si se borra el mensaje al imprimir |
| 11 | Contraseñas de clientes | Una regla: la tienda nunca las teclea ni las guarda, ni agrega servicios de clientes a una cuenta suya |
| 12 | Ligar la impresión por su nombre (T- y P-) y no guardar su miniatura | Pedirlo cuando se lance el agente del módulo de impresión, junto con revisar las miniaturas de documentos personales que hoy guarda Control Print |
| 13 | El recibo de CFE | En el celular del cliente, con su Google; nunca con una cuenta de la tienda. Sus decisiones (precio, cliente sin celular, letrero), en [recibo de CFE](recibo-cfe.md#decisiones-para-el-dueño) |
