# Propuesta: quitar el fondo de fotos para documentos

Fecha: 2026-11-28. Es el segundo módulo de imágenes del POS. El primero es «Juntar imágenes para Word», del PR #233.

## El problema

Llegan por WhatsApp fotos de personas, casi siempre tomadas por ellas mismas con el celular: selfies o fotos de frente. Hay que dejarlas con fondo blanco (u otro color) para un documento y pegarlas en Word.

Hoy no hay herramienta en las cajas:
- Word trae «Quitar fondo», pero en fotos de personas deja orillas malas y hay que marcar a mano.
- Paint de Windows 11 sí lo hace bien. Pero la caja 2 tiene Windows 10, y ahí no está.

## El flujo (el mismo de juntar)

1. En WhatsApp: clic derecho en la foto y **Copiar**.
2. **Ctrl+Alt+H** abre el panel de herramientas, y **F** escoge «Quitar fondo» (ver [la interfaz](quitar-fondo-interfaz.md); el dueño pidió un solo atajo para todas las herramientas).
3. Se abre la ventanita, la misma de juntar, con la foto **antes y después**. El fondo se quita solo en uno o dos segundos (por medir en la caja 2).
4. Si hace falta, se ajusta con teclas (ver abajo).
5. **Enter** copia y la ventanita se cierra sola. En Word: **Ctrl+V**.

Sin sonido. Al copiar, el aviso verde sale en el contador de juntar: «Listo: pega en Word con Ctrl+V».

**Dentro de juntar:** en la ventanita de juntar, la tecla **F** sobre una miniatura le quita el fondo a esa imagen. Así salen, por ejemplo, seis fotos de seis personas en una hoja.

## La ventanita

Arriba están las dos fotos lado a lado, «Antes» y «Después». Abajo, las opciones; todas se manejan con teclado.

| Opción | Teclas | Qué hace |
|---|---|---|
| Fondo | Alt+B blanco · Alt+Z azul · Alt+T transparente · Alt+O otro color | El color de atrás. Por omisión, blanco. Se recuerda en cada caja. |
| Borde | + / − | Recorta más o menos persona, para cuando quedó un pedazo de pared o se comió el pelo. |
| Suavizar | Alt+S | Orilla más suave o más dura. |
| Tamaño | Alt+M | Ninguno (la foto entera) o una medida para recortar (ver «Medidas»). |
| Copias | Alt+C | Cuántas copias en la hoja. Usa el acomodo de juntar, con líneas de corte. |
| Abrir en Word | Ctrl+O | Igual que en juntar. |
| Cancelar | Esc | Se cierra sin copiar. |

**Medidas.** Al elegir una medida, la ventanita recorta centrando la cara: deja aire arriba de la cabeza y hombros abajo. El recorte se mueve con las flechas y se agranda o achica con Alt+flechas. Lleva una lista corta de medidas que se puede editar: infantil, credencial, pasaporte y «otra». Las medidas exactas las pone el dueño. No las escribo aquí porque cada dependencia pide la suya y cambian.

## Calidad: lo que se cuida

- **Resolución original.** La persona sale con los píxeles de la foto que llegó, sin achicar, como pidió el dueño para juntar. El modelo trabaja en chico para encontrar a la persona; luego la máscara se agranda y se afina contra la foto original. Así el pelo sale de la foto, no del modelo.
- **Formato.** Sale en PNG, porque los píxeles cambiaron y PNG no pierde calidad. Si el original era JPEG, no se vuelve a comprimir.
- **Orillas sin halo.** Al pasar a fondo blanco, se le quita a la orilla el color de la pared vieja. Si no, queda una línea gris o verde alrededor del pelo.
- **Selfies.** A veces sale el brazo que sostiene el celular, o la cara está muy cerca. El modelo de retratos lo separa bien. Lo que sobre se quita con «Borde», y si es un pedazo grande, con el recorte de la medida.
- **Foto girada.** Se endereza con el dato del celular (EXIF), igual que en juntar.

## Con qué se quita el fondo

Todo corre **dentro de la caja, sin internet**. Ninguna foto sale a un servicio, ni para probar. Se usa ONNX Runtime para .NET (el paquete de Microsoft, en CPU) con un modelo de código abierto que va dentro del instalador.

Revisé los candidatos el 2026-11-28, en sus repositorios oficiales:

| Modelo | Licencia | Tamaño | Para qué |
|---|---|---|---|
| **MODNet** (retratos) | Apache 2.0 (README y LICENSE oficiales) | 26 MB; 6.6 MB reducido | **El de fábrica.** Hecho para personas, ligero, rápido en CPU. Funciona en la caja 2. |
| **BiRefNet lite** | MIT | 224 MB; 115 MB en fp16 | **Opcional, «Mejor orilla».** Pelo más fino. Solo si en la caja principal se nota la diferencia y el tiempo es aceptable. |
| BiRefNet portrait | MIT | 973 MB; 490 MB en fp16 | No: demasiado pesado para las cajas. |
| RMBG 1.4 / 2.0 (BRIA) | No comercial; para cobrar hay que comprar licencia | — | **No se usa.** La tienda cobra por el servicio. |

La licencia del modelo de fábrica permite usarlo en un negocio. Se anota en el aviso de licencias del POS, como las demás bibliotecas.

## Privacidad

Son fotos de caras de clientes para documentos de identidad. Por eso:
- No se guarda ninguna copia: ni miniatura, ni bitácora, ni archivo. Solo existe en memoria mientras la ventanita está abierta.
- Lo que se copia lleva los mismos avisos que juntar para que Windows no lo guarde en el historial del portapapeles.
- «Abrir en Word» escribe un archivo temporal y lo borra al cerrar la caja. Es igual que en juntar, y queda revisarlo para las dos herramientas.
- El registro diario anota solo «se quitó el fondo a 1 foto, 1.4 s», sin imagen.

## Cómo se arma en el código

- Se reusa todo lo de juntar: el atajo (ahora abre el panel), la lectura del portapapeles, la ventanita Blazor, `DocumentoParaWord` y el acomodo para las copias.
- Nuevo en `Impresion`: `QuitarFondo` recibe los bytes de la imagen y devuelve la máscara. Con ella, `ComponerSobre(color)` y `RecortarAMedida(...)`.
- Nuevo en `Nucleo`: `AjustesDeQuitarFondo` (color, borde, medidas) y las cuentas del recorte centrado en la cara. La cara se ubica con la propia máscara: es lo más alto y angosto de la persona. No hace falta otro modelo.
- Pruebas: con imágenes sintéticas para la composición, el halo y el recorte, y una prueba del modelo con una foto libre de derechos que viene en el repo. Nada de fotos de la tienda.

## Por entregas

1. **Lo básico:** el panel de herramientas (Ctrl+Alt+H, que reemplaza al Ctrl+Alt+J de juntar), el modelo MODNet, fondo blanco, azul o transparente, Borde y Suavizar, y Copiar. Se mide el tiempo en la caja 2.
2. **Para documentos:** las medidas, el recorte centrado en la cara, las copias en hoja, y la tecla F dentro de juntar.
3. **Si hace falta:** BiRefNet lite como «Mejor orilla» y un pincel con mouse para corregir a mano. El pincel es extra: todo lo anterior se hace sin mouse.

## Lo que solo se puede probar en la tienda (fin de semana)

- El tiempo en la caja 2: Windows 10 con pocos recursos.
- La calidad con selfies reales que llegan por WhatsApp. Se prueba con una foto del dueño o del personal, no de clientes.
- Que el resultado se pegue en Word a la medida exacta.

## Por decidir con el dueño

- La lista de medidas y sus valores exactos.
- Si el atajo Ctrl+Alt+H del panel está bien o choca con algo de las cajas.
- Si el fondo azul que piden algunos documentos es uno solo o hay varios tonos.
