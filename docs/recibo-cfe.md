# El recibo de CFE desde el POS

> **Investigación del 2026-10-31.** En [trámites](tramites.md#recibo-de-cfe) el recibo de CFE quedó en «no conviene»
> porque la consulta sin cuenta ya manda a iniciar sesión con la contraseña del cliente. El dueño no está de acuerdo:
> hoy, en la app, se entra fácil con una cuenta de Google, se escribe el número de servicio y sale el recibo, y eso
> podría indicar una API. Aquí se revisa eso.
>
> **Cómo se investigó:** el 2026-10-31 se leyeron las fichas de la app en Google Play y en la App Store, sus reseñas
> públicas, las páginas públicas de MiCFE, los términos y el aviso de privacidad de CFE, notas de prensa y repositorios
> públicos. **No se inició sesión, no se creó ninguna cuenta, no se agregó ningún servicio ni se envió ningún
> formulario.** Tampoco se instaló la app ni se llamó a ninguna interfaz interna de CFE. Lo que solo se sabe con una
> sesión real quedó en [su lista](#lo-que-solo-se-sabe-con-una-sesión-real), para el dueño. Es una propuesta: no hay
> código.

## Para el dueño

- **Lo que vio el dueño es cierto.** En la app oficial, CFE Contigo, se entra con Google (en iPhone también con Apple,
  o con correo y contraseña), se agrega un servicio con su número de 12 dígitos o escaneando el código de barras del
  recibo, y se baja el último recibo en PDF. La app no pide ser el titular: los medios lo explican para pagar la luz de
  los papás, de un local o de una casa rentada.
- **Pero no es una API.** CFE no ofrece una conexión para programas, ni para negocios. Lo que se vende como «API del
  recibo» son servicios de terceros sin convenio conocido con CFE (uno dice que entra al portal con cuentas suyas), y
  todos piden además el nombre del titular y el total de un recibo reciente.
- **Con una cuenta de la tienda, no.** Varias reseñas de este mes dicen que la app bloquea la cuenta al pasar de unos
  10 servicios, o al borrar uno y agregar otro, y que ya no se recupera. Agregar, bajar y quitar el servicio de cada
  cliente es justo eso. Además, los términos de CFE son de uso personal, y los servicios de clientes en una cuenta de
  la tienda serían datos personales de terceros a cargo de la tienda.
- **Lo que sí cambia: el cliente ya no necesita contraseña.** Entra con su Google en su celular, en un par de toques. El
  cajero lo guía: abre CFE Contigo, entra con su Google, agrega su servicio y manda el PDF al WhatsApp de la tienda. Se
  imprime y se cobra RECIBO ($15). No hay nada que programar.
- **Lo que decide el dueño:** [abajo](#decisiones-para-el-dueño). Lo que hay que ver con una sesión real lo puede
  revisar él un fin de semana, con la cuenta y el servicio de la tienda.

## Lo que se encontró

Todo se revisó el 2026-10-31, salvo lo que diga otra fecha. Lo marcado como «no oficial» viene de un tercero, no de CFE.

### La app CFE Contigo

- **Es la oficial:** CFE Contigo, de CFE SSB (CFE Suministrador de Servicios Básicos).
  - [Google Play](https://play.google.com/store/apps/details?id=mx.com.cfe.cfecontigo&hl=es_MX): más de 10 millones de
    descargas, actualizada el 29 de septiembre de 2026.
  - [App Store](https://apps.apple.com/mx/app/cfe-contigo/id1354232135): versión 7.0.0, del 3 de octubre de 2026; pide
    iOS 15 o posterior y pesa 181.5 MB.
  - Su descripción: pagar con tarjeta, cargo recurrente, **consultar el saldo y descargar el último recibo**, reportar
    fallas, el recibo digital y recordatorios de pago.
- **Lo que declara de privacidad:** en Google Play, CFE dice que la app no recopila ni comparte datos; en la App Store,
  que junta diagnósticos, la ubicación aproximada y un identificador de usuario, sin ligarlos a la persona. Su [aviso de
  privacidad](#los-términos-de-cfe) sí lista nombre, domicilio, teléfonos, correo y RFC.
- **Entrar:**
  - con **Google**: lo dicen reseñas de la App Store del 12 de septiembre y del 21 de octubre de 2026 (la segunda, que
    le daba error) y una guía no oficial actualizada en julio de 2026;
  - con **Apple**, en iPhone: la app lo agregó en su versión 3.8, de diciembre de 2020 (sus notas de versión); una
    reseña del 26 de octubre de 2026 dice que no le funciona;
  - con **correo o celular y contraseña**.
  - Según sus notas de versión, tiene captcha desde la 4.0.0 (diciembre de 2022), y la cuenta se puede eliminar desde
    el menú desde la 4.1.0 (marzo de 2023).
- **¿Es la misma cuenta que la web?** No del todo.
  - Reseñas del 5, 14, 26 y 27 de octubre de 2026 dicen que su usuario entra a la web y no a la app; otra, del 1 de
    octubre, dice que las cuentas de la app y de la web no están ligadas.
  - **Con Google son cuentas distintas:** una reseña del 12 de septiembre de 2026 cuenta que la web pide el usuario, la
    app el correo o el celular, y que entrar con Google le hizo una tercera cuenta, cada una con sus servicios y alguno
    repetido en varias ([espejo de la reseña](https://mwm.ai/es/apps/cfe-contigo/1354232135)). Un mismo servicio, pues,
    puede estar en varias cuentas. La web no tiene entrada con Google ([abajo](#la-web-micfe)).
- **Agregar un servicio:** menú, «Números de servicio», «Añadir servicio»; se **escanea el código de barras del recibo
  o se teclea el número de servicio**, y se le pone un alias
  ([Excélsior](https://www.excelsior.com.mx/nacional/como-agregar-y-pagar-mas-de-un-recibo-cfe-desde-la-aplicacion-oficial/1732787),
  2025-08-13; [Luz Noticias](https://www.luznoticias.mx/2026-06-02/mexico/guia-para-la-app-de-la-cfe-contigo-como-pagar-varios-recibos-de-luz-en-un-solo-lugar-/291578),
  2026-06-02). Ninguna fuente menciona el nombre del titular, el total ni el número de medidor.
- **Servicios de otros:** sí. Luz Noticias lo explica para quien paga la luz de sus papás, de un negocio o de una casa
  rentada.
- **El límite:** CFE no lo publica.
  - En la [App Store](https://apps.apple.com/mx/app/cfe-contigo/id1354232135?see-all=reviews), reseñas del 6 al 26 de
    octubre de 2026 dicen que la app **bloquea la cuenta** al pasar de 10 servicios (otras hablan de 6 u 8), o **al
    borrar uno y dar de alta otro**, y que la cuenta ya no se recupera. Quien quiso seguir tuvo que abrir otra cuenta.
  - Un servicio no oficial que agrega servicios ajenos por la web dice que ahí ese límite no existe
    ([registro de cambios](https://github.com/zomars/cfe-api-docs/blob/main/CHANGELOG.md), 2026-10-10).
  - Sea cual sea la regla, lo que las reseñas describen antes del bloqueo es agregar y quitar servicios.
- **Quitar un servicio:** se puede (lo cuentan las reseñas), pero dos de ellas dicen que el bloqueo llegó después de
  borrar uno.
- **El recibo:**
  - **El último, en PDF,** que se guarda o se comparte desde la app
    ([Excélsior](https://www.excelsior.com.mx/trending/cfe-como-descargar-el-recibo-de-luz-desde-tu-celular-en-2025/1727952),
    2025-07-18). Una reseña en Google Play del 26 de agosto de 2026 cuenta que baja sus recibos recientes en PDF.
  - **Los anteriores, ya no:** reseñas del 4, 5, 15 y 27 de octubre de 2026 dicen que la app ya no muestra los recibos
    anteriores; una dice que antes sí se podían bajar.
  - **Fallas este mes:** del 14 al 19 de octubre de 2026, varias reseñas dicen que la app respondía que el número de
    servicio no existe, con servicios que tenían años en la cuenta.
- **¿Sirve como comprobante de domicilio?** Según medios, el recibo digital de CFE vale como comprobante
  ([ADN40](https://www.adn40.mx/mexico/2025-04-27/comprobante-luz-la-cfe-como-obtenerlo-en-5-pasos), 2025-04-27). Lo
  decide quien lo pide, y casi siempre lo quiere de menos de tres meses. Si el PDF de la app es idéntico al de papel se
  ve en la revisión con la cuenta de la tienda.

### La web: MiCFE

- **Entrar** ([MiCFE](https://app.cfe.mx/Aplicaciones/CCFE/MiEspacio/Login.aspx), consultada el 2026-10-31): usuario,
  contraseña y un captcha de imagen. **No hay entrada con Google.**
- **Registrarse:** usuario, contraseña de 12 caracteres o más (con mayúscula, minúscula, número y carácter especial),
  correo, un captcha y aceptar los términos.
- **Un código por correo en cada inicio de sesión, desde el 24 de septiembre de 2026,** según el registro de cambios del
  servicio no oficial ([CHANGELOG](https://github.com/zomars/cfe-api-docs/blob/main/CHANGELOG.md), entradas del
  2026-09-26 y del 2026-10-10). Por verificar.
- **Agregar un servicio pide más que en la app:** el número, el **nombre del titular como lo tiene registrado CFE** y
  el **total a pagar del último o del penúltimo recibo**, sin centavos. Lo dicen tres fuentes no oficiales: ese mismo
  registro (2026-09-12 y 2026-10-24), un paquete que automatiza MiCFE ([cfe-api](https://www.npmjs.com/package/cfe-api),
  versión 0.3.0, 2026-04-12) y un servicio de verificación de identidad
  ([Verificamex](https://verificamex.com/soluciones/validacion-comprobante-cfe), consultado el 2026-10-31). Por
  verificar.
- **Los periodos:** ya agregado el servicio, la web lista su historial de varios años (no oficial, 2026-09-14).
- **La consulta sin cuenta:** la dirección vieja muestra un aviso y manda a MiCFE en 10 segundos (consultada el
  2026-10-31). Según el registro no oficial, CFE la retiró el **12 de septiembre de 2026**. Pedía el nombre del
  titular, el número de servicio, un teléfono y un correo.

### Otros canales de CFE

- **Los oficiales,** según CFE ([boletín](https://app.cfe.mx/Aplicaciones/OTROS/Boletines/boletin?i=3841),
  2023-05-03): el 071, Mi Espacio en cfe.mx, la app CFE Contigo y el bot **CFEContigoBot** de Telegram. El mismo boletín
  advierte que los sitios no oficiales para «consultar tu recibo», que piden nombre, dirección y datos del servicio,
  sirven para robar datos personales.
- **El bot de Telegram** se presenta como el chatbot para consultar el saldo y el recibo
  ([su página pública](https://t.me/CFEContigoBot), consultada el 2026-10-31). Según medios, pide el número de servicio
  y el de medidor y manda el PDF al chat
  ([Excélsior](https://www.excelsior.com.mx/trending/cfe-como-descargar-el-recibo-de-luz-desde-tu-celular-en-2025/1727952),
  2025-07-18). No sé si sigue igual. Queda como vía del cliente: no se propone para la tienda porque Telegram está en
  pausa.
- **WhatsApp:** CFE dice que no tiene, y circulan números falsos
  ([Expansión](https://politica.expansion.mx/mexico/2024/06/11/como-sacar-recibo-cfe-por-telegram), 2024-06-11).

## ¿Hay una API?

- **Oficial, no.** No encontré en cfe.mx un portal para programadores ni un programa para terceros (2026-10-31). Los
  paquetes que automatizan MiCFE dicen lo mismo: CFE no publica una API.
- **Los que cobran** (OXXO, bancos, comisionistas) **solo cobran.** El aviso de privacidad de CFE dice que les pasa
  información para abrir canales de pago ([abajo](#los-términos-de-cfe)). Cobran con el número de servicio o el código
  de barras y dan un ticket de pago; no encontré que alguno entregue el recibo.
- **Verificación de identidad para empresas:**
  [Verificamex](https://verificamex.com/soluciones/validacion-comprobante-cfe) (consultado el 2026-10-31) revisa un
  comprobante de CFE con el número de servicio, el nombre del titular y el total exacto del último o penúltimo recibo,
  y regresa el PDF. Es para financieras, inmobiliarias y empresas que dan de alta clientes; se paga por consulta y no
  dice que tenga convenio con CFE. **No sirve a la tienda:** pide los mismos datos que el cliente ya tendría, y sus
  datos pasarían a otra empresa.
- **No oficiales:**
  - un servicio de pago ([cfe-api-docs](https://github.com/zomars/cfe-api-docs), abierto en abril de 2026) agrega cada
    servicio consultado a cuentas suyas de MiCFE y regresa el recibo. Su registro de cambios muestra que tiene que
    ajustarse casi cada semana a lo que cambia CFE;
  - paquetes para programadores ([cfe-api](https://www.npmjs.com/package/cfe-api),
    [PyCfeMx](https://github.com/oamilkar/PyCfeMx)) que entran a MiCFE con un usuario y su contraseña. Uno recomienda
    una «cuenta maestra» que agregue los servicios de todos sus usuarios;
  - sitios de «consulta sin registro» que piden el nombre, el número y el total.

  **Ninguno se usa:** usan cuentas de CFE en lo que sus términos dicen que es personal, los datos del cliente pasarían
  por un tercero desconocido, CFE advierte contra esos sitios, y el dueño ya dijo que no busca saltarse el
  procedimiento estándar. Solo se leyó lo que publican; no se ejecutó nada ni se llamó a sus servicios.

## Los términos y la ley

### Los términos de CFE

[Términos y condiciones](https://www.cfe.mx/Pages/TerminosyCondiciones.aspx) (consultados el 2026-10-31):

- aplican al sitio «y a cualquiera de las aplicaciones del mismo»;
- los materiales son para **uso personal, no comercial**, y no se pasan a terceros;
- la cuenta es personal: su dueño debe mantener en secreto el usuario y la contraseña, y **responde por todo lo que se
  haga con ellos**, lo haya autorizado o no;
- quien usa los servicios en línea autoriza a CFE a dar a un tercero lo que ingresa al sistema;
- prohíben descompilar el software;
- **no dicen nada** de manejar servicios de otras personas desde una cuenta, ni del acceso automático.

El aviso de privacidad de sus oficinas y medios remotos y digitales (en
[avisos de privacidad](https://www.cfe.gob.mx/transparencia_etica/transparencia/Pages/avisos_privacidad.aspx),
«Atención a Clientes CAC y Medios de Atención Remota y Digital», elaborado en junio de 2026 y consultado el 2026-10-31):

- lista lo que CFE junta: nombre, CURP, identificación, domicilio, teléfonos, RFC y correo;
- lo pasa a **comisionistas**, para abrir canales de pago, y a gestores de cobranza;
- no habla de terceros que consulten recibos.

### La ley de datos personales

Si la tienda agregara servicios de clientes a una cuenta suya, según la
[LFPDPPP](https://www.diputados.gob.mx/LeyesBiblio/pdf/LFPDPPP.pdf) (texto vigente, consultado el 2026-10-31):

- **Sería una base de datos personales de la tienda.** La ley llama así a cualquier conjunto ordenado de datos de
  personas, sin importar dónde esté guardado (artículo 2, fracción II). Cada servicio de la cuenta abre el nombre, el
  domicilio, el consumo y el adeudo de su titular.
- **El adeudo y los pagos pueden contar como datos patrimoniales,** que piden el consentimiento expreso del titular
  (artículo 7). La excepción de «lo que pidió el cliente» (artículo 9, fracción IV) cubre al que está en el mostrador;
  si el titular es otro (el casero, un familiar), no lo cubre.
- **Solo lo necesario y nada después** (artículos 10 y 12): el servicio se quita de la cuenta al imprimir, el PDF se
  borra del celular, del chat y de la caja, y el número de servicio no se anota en ningún lado.
- **El aviso de privacidad** (artículo 15) tendría que decirlo.
- **La multa** por tratar datos sin el consentimiento expreso que la ley exige va de 200 a 320,000 UMA (artículo 58,
  fracción XIII, y 59, fracción III).

Con el celular del cliente (lo recomendado), la tienda solo recibe el PDF que el cliente le manda, lo imprime y lo
borra, como cualquier archivo: lo que ya dice la [privacidad de los trámites](tramites.md#privacidad).

## Cómo funcionaría en la tienda

| | (a) Panel en el POS con MiCFE | (b) Celular de la tienda con la app | (c) Celular del cliente |
|---|---|---|---|
| **Los pasos** | Entrar con la cuenta de la tienda (usuario, contraseña, captcha y quizá el código del correo), agregar el servicio con el nombre y el total, bajar el PDF y quitar el servicio | Agregar el servicio (escanear o teclear), bajar el PDF, mandarlo a la caja y quitar el servicio | El cliente entra con su Google, agrega su servicio y manda el PDF al WhatsApp de la tienda |
| **El tiempo, a ojo** | 3 a 4 minutos | 2 a 3 minutos | 1 minuto si ya tiene la app; 3 a 6 si la instala |
| **Lo que queda** | Los servicios de clientes pasan por la cuenta de la tienda | Lo mismo, y el PDF en el celular | Solo el PDF en el WhatsApp de la tienda, hasta borrarlo |
| **Lo que se rompe** | El cliente que no sabe el total; la sesión, el captcha y el código; los cambios de MiCFE | El bloqueo de la cuenta; las fallas de la app | Las fallas de la app; el cliente sin celular o sin datos |
| **Los términos y la ley** | Uso personal; datos de terceros en la cuenta | Igual | Lo de siempre: cada quien usa su cuenta |
| **El esfuerzo** | La base de los trámites (mediano) y el inicio de sesión (chico) | Nada con WhatsApp; mediano con una página en la red local | Nada; con el panel, elegir el PDF |
| **Lo que gana la tienda** | RECIBO ($15) | RECIBO ($15) | RECIBO ($15), sin riesgo |

RECIBO salió 13 veces en tres meses en eleventa (copia del 2026-10-24): unos $65 al mes.

### (a) Un panel en el POS con MiCFE

- **Google no deja entrar con Google dentro de otra aplicación.** Su ayuda lo dice de los navegadores integrados en
  otra app ([Ayuda de Google](https://support.google.com/accounts/answer/7675428?hl=es-419), consultada el 2026-10-31;
  la regla empezó en 2021, según el
  [blog de Google](https://developers.googleblog.com/upcoming-security-changes-to-googles-oauth-20-authorization-endpoint-in-embedded-webviews/)).
  Un reporte del 29 de octubre de 2026 lo confirma con WebView2 en Windows 11: Google rechaza la entrada con «Couldn't
  sign you in» ([issue](https://github.com/theogmiguel/houston/issues/388)). Una cuenta de la tienda hecha con Google no
  serviría en el panel.
- **Aquí no estorba, porque MiCFE no ofrece Google:** solo usuario, contraseña y captcha. El panel usaría una cuenta de
  usuario y contraseña de la tienda.
- **Choca con el diseño del panel:** el panel de trámites borra su perfil al terminar cada trámite
  ([trámites](tramites.md#el-panel-trámites)). Con CFE habría que entrar cada vez (contraseña, captcha y, si es cierto,
  el código del correo) o dejar la sesión de la tienda abierta en la caja.
- **Agregar el servicio pide el nombre como lo tiene CFE y el total del último o del penúltimo recibo.** Si el cliente
  trae su recibo anterior, sirve; si no, no.
- **Veredicto: no.**

### (b) El celular de la tienda con la app

- **Los pasos:** el cajero agrega el servicio del cliente (escanea su recibo o teclea el número), abre el recibo, lo
  comparte, lo manda a la caja, lo imprime, lo cobra y **quita el servicio**.
- **Cómo llegaría el PDF a la caja:**
  - **WhatsApp, a mano:** el celular de la tienda es el del WhatsApp Business
    ([WhatsApp](whatsapp.md#situación-actual)); se manda el PDF a sí mismo y la caja lo abre en WhatsApp Web. Sin
    código. Quedan copias en el chat y en el celular hasta borrarlas.
  - **Una página en la red local:** la página del celular (`/conteo`) ya pide usuario y PIN, que duran un turno
    ([SesionesCelular](../src/PapeleriaPos.Servicio/SesionesCelular.cs)), y ya sube fotos desde la cámara
    ([FotosCelular](../src/PapeleriaPos.Interfaz/Componentes/FotosCelular.razor)). Una pestaña «Mandar un PDF a una
    caja» y el aviso en la caja, como los tickets del celular de la
    [propuesta](propuesta-interfaz.md#desde-el-celular), serían de esfuerzo mediano. Va por la red de las cajas, que no
    comparte el público ([seguridad](notas.md#seguridad)): sirve para el celular de la tienda, no para el del cliente.
  - **Un QR en la caja** con una dirección de un solo uso: lo mismo sin el PIN, con un poco más de trabajo.
- **El riesgo es el bloqueo.** Agregar y quitar servicios ajenos una y otra vez es lo que las reseñas describen antes
  de que la app bloquee la cuenta. Si fuera la cuenta que tiene el servicio de luz de la propia tienda, la tienda
  perdería también el acceso a su propio recibo.
- **Veredicto: no como rutina.** La página en la red local tampoco vale la pena solo para esto.

### (c) El celular del cliente

1. El cliente abre CFE Contigo. Si no la tiene, la instala desde la tienda oficial de su celular.
2. **Entra con su Google** (o con Apple, en iPhone). No teclea una contraseña: el cajero nunca la ve.
3. **«Añadir servicio»:** escanea el código de barras de un recibo suyo o teclea los 12 dígitos.
4. Abre el recibo y lo **comparte por WhatsApp** al número de la tienda.
5. El cajero lo imprime y cobra RECIBO ($15). Hoy, desde WhatsApp Web; con el panel de trámites, eligiendo el PDF, como
   en el [plan B](tramites.md#en-la-caja-del-navegador): el POS lo imprime y agrega RECIBO al ticket.
6. Al terminar, «Eliminar para mí» en el chat y el PDF fuera de Descargas.

- **Lo que se rompe:** las fallas de la app (como las de octubre), el cliente sin celular o sin datos, o un iPhone con
  un iOS anterior al 15.
- **El cliente sin celular:** que lo baje un familiar en el suyo, o en un Centro de Atención de CFE. El bot de Telegram
  de CFE también es una vía suya ([arriba](#otros-canales-de-cfe)).
- **Lo que gana la tienda:** lo mismo que hoy, sin cuentas ni datos que cuidar.
- **Veredicto: sí. Es lo recomendado.**

## Veredicto

- **«Entrar con Google y escribir el número de servicio da el recibo»: es cierto, en la app.** Lo confirman la ficha
  de la app, notas de 2025 y 2026 y reseñas de septiembre y octubre de 2026. No se probó con una sesión: eso queda para
  el dueño.
- **No es una API ni un permiso:** la web pide más (el nombre y el total), la app bloquea cuentas con muchos servicios,
  y los términos dicen uso personal.
- **El veredicto de ayer cambia de razón, no de fondo.** La tienda no lo hace con una cuenta suya, pero ya no es porque
  el cliente tenga que dar su contraseña: ahora lo hace en su celular, con su Google, en unos minutos, y la tienda lo
  guía, lo imprime y lo cobra.
- **El esfuerzo:** nada que programar. Si se hace el panel de trámites, el recibo entra como «elegir el PDF y cobrar
  RECIBO».

## Lo que solo se sabe con una sesión real

El dueño, un sábado o domingo, con **la cuenta de CFE de la tienda y su propio servicio**. Nunca con números de
clientes, sin probar el límite y **sin quitar el servicio de la tienda** (dos reseñas dicen que el bloqueo llegó al
borrar uno).

1. **Entrar a la app:** qué opciones muestra hoy (Google, Apple, correo o celular), si pide captcha y si muestra
   términos propios (y si dicen algo de servicios de otras personas).
2. **Agregar un servicio:** si pide solo el número o el código de barras, o también el nombre o el total, y si avisa
   de un límite. Si el servicio de la tienda ya está en su cuenta, basta con ver la pantalla de «Añadir servicio».
3. **El recibo:** que salga en PDF, qué periodos ofrece, si es igual al de papel y por dónde se comparte.
4. **MiCFE en Edge:** si llega un código por correo al entrar, qué pide para agregar un servicio y cuántos periodos
   lista.
5. **Si una cuenta de Google de la app entra a MiCFE** (se espera que no).
6. **Con un cliente que quiera:** cuánto tarda (c) de principio a fin y si el PDF llega bien por WhatsApp.

Esto entra a la [guía del fin de semana](pruebas-en-tienda.md) cuando se programe, junto con lo de los
[trámites](tramites.md#lo-que-solo-se-puede-verificar-en-la-tienda).

## Decisiones para el dueño

Ninguna se marcó como «decidido, por revisar»: no se va a programar nada por ahora.

| # | Decisión | Recomendación |
|---|---|---|
| 1 | Bajar recibos de clientes con una cuenta de CFE de la tienda (app o web) | **No.** Hay bloqueos reportados, los términos dicen uso personal, y serían datos de terceros a cargo de la tienda |
| 2 | Cómo se ofrece el recibo | **En el celular del cliente:** entra a CFE Contigo con su Google, agrega su servicio y manda el PDF al WhatsApp de la tienda; se imprime y se cobra |
| 3 | El precio | RECIBO $15 cuando la tienda guía e imprime, con la primera copia. Si el cliente ya trae el PDF, el precio de la impresión, como cualquier archivo |
| 4 | El cliente sin celular | Que lo baje un familiar o que vaya a un Centro de Atención de CFE. No con una cuenta de la tienda |
| 5 | La cuenta de CFE con el servicio de luz de la tienda | Nunca agregarle servicios de clientes: si la bloquean, la tienda pierde su propio recibo |
| 6 | Un letrero en el mostrador | Sí: los pasos y un QR a la ficha oficial de la app (Google Play y App Store), nunca a sitios de «consulta sin registro» |
| 7 | Mandar PDF del celular de la tienda a la caja por la red local | No por ahora: el recibo no lo justifica y WhatsApp Web ya lo hace. Revisarlo si aparece otro uso |
| 8 | Revisar la app y MiCFE con una sesión real | Sí, el dueño, con la [lista de arriba](#lo-que-solo-se-sabe-con-una-sesión-real) |
