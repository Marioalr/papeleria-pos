# Propuesta: «Juntar imágenes para Word» dentro del POS

Propuesta del 28 de noviembre de 2026, revisada contra `main` del POS en `f0f8852c` (#229). Todavía no hay código.
**Versión 2:** sigue el flujo que propuso el dueño (juntar en WhatsApp y pegar en Word). La versión 1, que acomodaba e
imprimía dentro del POS, queda como opción para después (§7).

## El problema

Un cliente manda decenas de imágenes por WhatsApp y quiere que se impriman juntas. Hoy se pasan a Word una por una, se
ajusta el tamaño de cada una y se acomodan a mano. Con 30 imágenes son varios minutos de mostrador.

WhatsApp solo deja **copiar una imagen a la vez**, y el portapapeles de Windows también guarda **una a la vez**. Por
eso hace falta algo que las vaya juntando.

---

## 1. La evaluación del flujo propuesto

**El flujo del dueño:** un atajo para empezar, copiar una por una en WhatsApp, otro atajo para terminar, una ventanita
con la configuración, «copiar todas» y pegar en Word.

**Conviene, y es mejor que la versión 1**, por estas razones:

| A favor | Por qué |
|---|---|
| **No se sale de WhatsApp para juntar** | Con un atajo que funciona aunque el POS no esté al frente, el cajero copia, copia, copia sin ir y venir |
| **Word se queda como hoy** | El cajero ya sabe ajustar ahí, poner el nombre del alumno o un título, y imprimir. El POS solo le quita lo tedioso: juntar y poner el tamaño |
| **Menos que programar** | No hacen falta la vista de las hojas, el PDF ni la impresión: Word hace eso. La primera entrega sale más pronto |
| **Se cobra como hoy** | Lo que se imprime desde Word lo mide el agente de impresión y sale en F2 por cobrar, como cualquier impresión |
| **Sirve con cualquier origen** | La app de WhatsApp, el correo, una página o una carpeta: todo lo que se copie mientras está juntando |

Tres ajustes para que salga bien:

1. **Un solo atajo para empezar y terminar.** El mismo, por ejemplo **Ctrl+Alt+J** («juntar»), hace las dos cosas:
   es más fácil de recordar que dos. **Esc** en la ventanita cancela.
2. **Mientras junta, un contador chiquito arriba a la derecha** («Juntando: 7 · Ctrl+Alt+J para terminar»), siempre
   encima y sin robar el foco a WhatsApp. Así se sabe que sí está contando y cuántas van.
3. **Las imágenes se pegan en Word ya con su tamaño**, sin tocar nada en Word (ver §3). Además, en la ventanita,
   «Abrir en Word» crea un documento nuevo con la hoja y los márgenes ya puestos, para que «en N hojas» salga exacto.

Lo que se pierde frente a la versión 1, y es aceptable:
- **El folio y el cobro ligado:** lo impreso desde Word se cobra en F2 como impresión suelta, igual que hoy.
- **Cuidar los datos del cliente:** las imágenes quedan en el documento de Word y en sus recientes. El POS borra lo
  suyo, pero el documento lo maneja el cajero (§5).
- **«En N hojas» es aproximado** si se pega en un Word con otros márgenes; con «Abrir en Word» es exacto.

---

## 2. El flujo, paso a paso

```
 En WhatsApp (la app de Windows)                              En la caja (aunque esté minimizada)
 ───────────────                                              ───────────────────────────────────
 Ctrl+Alt+J  ───────────────────────────────────────────────► empieza a juntar
                                                              ┌──────────────────────────┐
 clic derecho › Copiar          (una)  ─────────────────────► │ Juntando: 1              │
 clic derecho › Copiar          (otra) ─────────────────────► │ Juntando: 2              │
 …                                                            │ …                        │
 clic derecho › Copiar          (la 24) ────────────────────► │ Juntando: 24             │
                                                              │ Ctrl+Alt+J para terminar │
 Ctrl+Alt+J  ───────────────────────────────────────────────► └──────────────────────────┘
                                                              se abre la ventanita ▼
┌ Juntar imágenes · 24 ──────────────────────────────────────────────────────┐
│ Tamaño:  (•) Alto [ 5.0 ] cm   ( ) Ancho [    ] cm   ( ) En [ 2 ] hojas     │
│ Hoja: Carta vertical · márgenes de Word (2.5 / 3 cm) · separación 0.3 cm    │
│ [ ] Líneas de corte   [ ] Blanco y negro                                    │
│                                                                             │
│ Salen 3 hojas. ⚠ 2 imágenes se verán borrosas a 5 cm.                       │
│ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ▓ ⚠ ⚠     (miniaturas, en orden)  │
│                                                                             │
│ Enter Copiar todas para Word    Ctrl+O Abrir en Word    Esc Cancelar        │
└─────────────────────────────────────────────────────────────────────────────┘
 Enter ──► «Copiadas 24 imágenes de 5 cm: pégalas en Word con Ctrl+V»
 En Word: Ctrl+V ──► las 24, del tamaño pedido, acomodadas en renglones
```

Detalles:
- **Solo cuentan las imágenes.** Si mientras junta se copia un texto (un nombre, un número), se ignora.
- **La misma imagen copiada dos veces** se cuenta una vez y el contador lo dice («24 · 1 repetida»).
- **El orden** es en el que se copiaron, que es el del chat si se copian de arriba abajo. En la ventanita se puede
  cambiar con el teclado (§6).
- **Si se olvida que está juntando,** a los 15 minutos sin copiar nada se detiene solo y avisa en el contador.
- **«Traer más»:** en la ventanita, Ctrl+Alt+J otra vez sigue juntando sobre las mismas.
- **Varios archivos de una carpeta** (Ctrl+C en el Explorador) también entran, todos de una vez. Así sirve igual para
  lo que viene en una USB.

## 3. Cómo llegan a Word ya con su tamaño

Al dar «Copiar todas», el POS pone en el portapapeles **un solo bloque con todas las imágenes**, cada una con su
tamaño en centímetros, en el formato RTF. Es el que Word entiende cuando se copia de otro documento: Word las pega en
línea, una tras otra, y las va pasando de renglón y de hoja él solo, como si se hubieran acomodado a mano.

- **Alto:** todas del mismo alto; el ancho sale de cada imagen, sin deformarla.
- **Ancho:** todas del mismo ancho.
- **En N hojas:** el POS calcula el alto más grande con el que caben todas en esas hojas, con los márgenes de la hoja,
  y lo dice («Salen de 6.3 cm de alto»).
- **Una imagen que no cabe a lo ancho** de la hoja se achica a lo ancho, y se marca.
- **Las que se verán borrosas** se marcan con ⚠: el POS sabe cuántos píxeles tiene cada una y a qué tamaño va a salir
  (debajo de unos 150 puntos por pulgada avisa; debajo de 100, en rojo). Así se le dice al cliente antes de imprimir.
- **Separación:** un espacio chico entre imágenes y entre renglones.
- **Líneas de corte:** un borde gris delgado en cada imagen, para recortarlas.
- **Blanco y negro:** las convierte a gris antes de copiarlas, del mismo tamaño en píxeles y sin perder calidad
  (como PNG); sale más barato al imprimir. Viene apagado: también se puede imprimir en gris desde Word.
- **Tal como llegaron:** cada imagen va a Word con **todos sus píxeles y en su mismo formato** (la PNG sigue PNG, la
  JPEG sigue JPEG), sin achicarla ni volver a comprimirla. El tamaño en centímetros es solo cómo se muestra en la hoja:
  si en Word se agranda o se corrige una, conserva todo el detalle que traía. WhatsApp ya las comprime al mandarlas, y
  casi todas son capturas o documentos, no fotos, así que no pesan de más.
- **Enderezadas:** la foto del celular que viene «acostada» se gira según sus datos, como ya hace el POS con las fotos
  de productos.

**«Abrir en Word» (Ctrl+O):** en lugar de copiar, el POS guarda un documento temporal con la hoja (carta u oficio,
vertical u horizontal) y los márgenes ya puestos, y lo abre en Word. Sirve cuando se quiere «todas en una hoja» exacto,
sin depender de los márgenes del Word que esté abierto. También es formato RTF, así que no hace falta una librería
nueva.

## 4. Los márgenes de Word

Para que «En N hojas» salga bien al pegar, el POS necesita saber los márgenes del Word donde se va a pegar. La
ventanita trae los de un documento nuevo de Word en español (carta, 2.5 cm arriba y abajo, 3 cm a los lados) y se
pueden cambiar; lo que se escriba se recuerda en esa caja. Con «Abrir en Word» no importa: el documento ya lleva los
suyos.

## 5. Cuidar los datos del cliente

Las imágenes pueden ser credenciales, actas o fotos de familia.
- **Lo del POS:** las imágenes juntadas viven solo en la memoria y en una carpeta temporal de la caja, y se borran al
  cancelar, a los 15 minutos de haberlas copiado a Word y al abrir la caja. **Nada va a la base de datos ni a la caja
  principal.** En el registro de la caja solo queda «Juntar imágenes · 24 · alto 5 cm».
- **El portapapeles:** el bloque para Word no entra al historial de Windows (Win+V) ni a la nube, como hoy «Copiar
  para WhatsApp». Al terminar el tiempo, si sigue en el portapapeles, se quita.
- **Lo de WhatsApp:** lo que se copió desde WhatsApp sí pudo quedar en el historial de Win+V. Lo mejor es tener
  apagado ese historial en las cajas (Configuración de Windows › Sistema › Portapapeles).
- **Lo de Word:** el documento lo cierra el cajero sin guardar. Si se usa «Abrir en Word», el archivo temporal se
  borra al abrir la caja al día siguiente.

## 6. Solo con el teclado

| Tecla | Dónde | Qué hace |
|---|---|---|
| **Ctrl+Alt+J** | En cualquier programa | Empieza a juntar; otra vez, termina y abre la ventanita |
| Alt+A / Alt+N / Alt+H | Ventanita | Modo Alto, Ancho, En N hojas (el cursor va a su número) |
| Tab | Ventanita | Recorre la hoja: tamaño, orientación, márgenes, separación, cortes, blanco y negro |
| Flechas, Alt+flechas, Supr, R, Ctrl+Z | Miniaturas | Moverse, cambiar de lugar, quitar, girar 90°, deshacer |
| **Enter** | Ventanita | Copiar todas para Word |
| Ctrl+O | Ventanita | Abrir en Word |
| Esc | Ventanita | Cancelar y borrar lo juntado (pregunta si hay más de 3) |

Con una prueba que recorra la ventanita sin mouse, como las demás pantallas.

**El atajo:** Ctrl+Alt+J no lo usan Windows, WhatsApp, Word ni la caja. Si en alguna PC otro programa ya lo tiene,
la caja lo dice al encender y se escoge otro en Configuración.

## 7. Lo que queda para después (de la versión 1)

Si más adelante conviene no depender de Word:
- **Imprimir desde la ventanita:** el POS arma el PDF con SkiaSharp (ya lo trae) y lo manda con folio
  («A-0042 · 24 imágenes»), con su cotización (Ctrl+Q) y su cobro ligado, como los trámites.
- **«Tantas por hoja»** (4, 6, 9 iguales), si los clientes lo piden.
- **Girar las que convenga** para gastar menos hojas.
- **Conectarlo con los pedidos** cuando existan: el cliente manda las imágenes, se juntan, y el aviso de «listo» sale
  por WhatsApp.

## 8. Dónde va el código (para el agente que lo haga)

Dentro del POS, sin scripts aparte. **Solo la caja de Windows:** el atajo global y el portapapeles son de Windows.

| Pieza | Dónde | Por qué |
|---|---|---|
| **El acomodo** (tamaños, «en N hojas», lo borroso, lo que no cabe a lo ancho) | `PapeleriaPos.Nucleo`, sin dependencias | Son cuentas puras: se prueban en Linux con muchos casos |
| **El bloque para Word** (RTF con las imágenes y sus tamaños; el documento de «Abrir en Word» con su hoja y márgenes) | `PapeleriaPos.Impresion`, con SkiaSharp solo para leer los píxeles, enderezar y pasar a gris. **Nunca achica ni recomprime:** los bytes originales van tal cual al RTF | Ya tiene SkiaSharp; se prueba en Linux (que el RTF abra y diga los tamaños correctos) |
| **El atajo global, vigilar el portapapeles, el contador, la ventanita** | `PapeleriaPos.Caja` (la ventana WPF) | Es nuevo en el POS: hoy la caja no registra atajos globales ni vigila el portapapeles. Se parece a lo que hace `PanelDeTramitesDeWindows` con el portapapeles |

### Entregas sugeridas

1. **Lo que resuelve el problema:** el atajo, juntar con el contador, la ventanita con Alto, Ancho y En N hojas, lo
   borroso, «Copiar todas para Word» y «Abrir en Word».
2. **Los extras:** blanco y negro, líneas de corte, reordenar y girar en las miniaturas, archivos copiados de la
   carpeta.
3. **Lo de §7**, si se pide.

## Lo que hay que probar en la caja (Windows) o en la tienda

- **Qué deja en el portapapeles el «Copiar» de una imagen en la app de WhatsApp para Windows** (la que usan las cajas):
  la imagen en sí, el archivo o los dos. El POS acepta cualquiera; si deja el archivo, se toma el original tal cual.
  También, que copiar varias seguidas rápido no se salte ninguna.
- Que la versión de Word de las cajas pegue las imágenes del RTF con el tamaño exacto, y cuánto tarda con 50.
- Que el atajo funcione con la caja minimizada y con WhatsApp al frente.
- Que el contador no le quite el foco a WhatsApp.
- Cuánta memoria usa la caja 2 con 50 imágenes.

## Preguntas para el dueño

Busqué en las notas del POS; esto no está:

1. **¿En qué PC se hace esto** (caja 2, principal, sucursal 2) **y qué Word tienen** (Office 2016, 2019, 365)? ¿Está en
   todas?
2. **¿Qué alto en centímetros piden más?** Para ponerlo por omisión.
3. **¿Usan carta, con los márgenes normales de Word,** o tienen un documento o plantilla propio para esto?
4. **¿Las quieren con líneas de corte** (para recortarlas) o pegadas, sin borde?
5. **¿Ctrl+Alt+J está bien como atajo,** o prefieren otro?
6. ¿Se cobra el armado aparte o solo las hojas impresas? (Con Word, el armado solo se cobraría agregándolo a mano al
   ticket.)
7. ¿Llegan también fotos para **tamaño infantil o credencial**? Sería otra herramienta (tamaños fijos y recorte de la
   cara); la dejo fuera.
