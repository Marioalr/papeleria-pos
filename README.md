# Papelería POS

Punto de venta a la medida para una papelería con varias sucursales: venta, inventario, servicios (impresiones,
copias, engargolado, trámites…), pedidos y control de cada sucursal sin que el dueño tenga que estar presente.
Reemplaza a eleventa multicaja e incorpora lo que hoy hace [Control Print](https://github.com/Marioalr/control-print).

> **Estado: fase 3.** La caja vende, cobra e imprime, con control (reglas, autorizaciones y bitácora) y varias cajas
> en red. El inventario ya se maneja en el POS: catálogo (F3), compras con el documento del proveedor, ajustes,
> mermas, conteos, etiquetas y sugerido de compra. También hay mayoreo por escalas, redondeo al peso, clientes
> registrados y crédito. Todo se prueba con datos ficticios. Lo demás sigue en planeación; las decisiones se cierran en
> el [plan](docs/plan.md).

## Desarrollo

Requiere el SDK de .NET 10.

```
dotnet build
dotnet test
```

Base con una papelería inventada (catálogo, un año de ventas y existencias), para probar sin datos reales:

```
dotnet run --project src/PapeleriaPos.Herramientas -- generar-ficticios --salida ficticios.db
```

Diagnóstico en Excel de una base (dinero parado, 80-20, margen, catálogo a revisar y temporada):

```
dotnet run --project src/PapeleriaPos.Herramientas -- diagnostico --base ficticios.db --salida diagnostico.xlsx
```

Importar una copia de la base de eleventa (requiere Firebird 2.5 embebido, ver
[migración](docs/migracion-eleventa.md#leer-la-base)):

```
dotnet run --project src/PapeleriaPos.Herramientas -- importar-eleventa --fdb C:\ruta\PDVDATA.FDB --salida eleventa.db
```

Al dejar eleventa, la importación final trae su historial de ventas a la base de la tienda, sin lo que mandó el puente
(se puede repetir; con `--simular` solo dice qué haría; ver [la importación final](docs/migracion-eleventa.md#la-importación-final)):

```
dotnet run --project src/PapeleriaPos.Herramientas -- importar-eleventa --fdb C:\ruta\PDVDATA.FDB --base pos.db --simular
```

### Probar la caja

1. Una base de prueba (ficticia o importada) con la Caja 1 y usuarios de prueba:

   ```
   dotnet run --project src/PapeleriaPos.Herramientas -- generar-ficticios --salida ficticios.db
   dotnet run --project src/PapeleriaPos.Herramientas -- preparar-caja --base ficticios.db
   ```

2. El servicio de la sucursal, apuntando a esa base (en desarrollo también muestra la caja en el navegador, en
   <http://localhost:5080>, con vista previa del ticket en lugar de imprimir; solo en la misma PC: desde otro aparato
   de la red solo se abre el conteo, en <http://localhost:5080/conteo>):

   ```
   dotnet run --project src/PapeleriaPos.Servicio -- --environment Development --ConnectionStrings:Pos="Data Source=ficticios.db"
   ```

3. La caja de Windows (`src/PapeleriaPos.Caja`) se conecta al servicio e imprime en la impresora de `caja.json`:
   `tcp://192.168.1.100:9100` para una impresora de red, el nombre de una impresora de Windows, o vacío para ver el
   ticket en pantalla. El servicio acepta lo mismo en `Caja:Impresora` para la caja del navegador.

La venta es la del [rediseño](docs/propuesta-interfaz.md), en todas las cajas. La de antes (la misma distribución que
eleventa) ya no existe: el dueño la quitó el 2026-10-14, y una PC que había vuelto a ella entra a la del rediseño y lo
olvida. Se entra como en la propuesta (entrega 6): en «¿Quién atiende?» se toca la ficha (o se oprime su letra) y se
teclea el PIN de 4 cifras, que entra solo con la cuarta; Ctrl+U cambia de usuario. Si la caja tiene el turno de otro
cajero, quien tiene el permiso hace ahí mismo su corte, sin volver a escribir su PIN. El corte del turno solo pide el
total del efectivo, a ciegas, y el dueño tiene además el corte del día. Productos (F3), Inventario (F4) y Clientes ya
son páginas, como en el prototipo (entrega 8): bajo la barra de arriba se ve la venta o una de ellas, y F1 regresa a la
venta como estaba. Productos y Clientes tienen la lista a la izquierda (Productos abre llena, lo más vendido primero) y el
elegido a la derecha; Inventario, una tarjeta por tarea, solo las de quien entró. Sin vender («Productos e inventario, sin
cortar», para quien tiene productos o compras) van las mismas páginas, con la barra de arriba de la venta. La
Configuración ya es la del rediseño (entrega 7): ocupa la pantalla,
con «Buscar un ajuste» y solo los grupos de los permisos de quien entró, y cada cambio se guarda solo, sin «Guardar» ni
volver a escribir el PIN (Telegram, en pausa, sigue como estaba). Quien tiene «Ver reportes de ventas» ve además la página
Reportes (entrega 9): hoy, la semana, el mes y el año a la fecha contra el mismo periodo del año pasado, con la utilidad
para quien ve costos, y «Ventas por período» (los tickets de una fecha a otra, con su resumen para la impresora de
tickets). Los reportes y los del inventario se exportan a Excel: en la ventana de Windows, con «Guardar como»; en el
navegador, se descargan. `preparar-caja` deja en la base de prueba los rápidos
Copia B/N y CURP (10 impresiones); `preparar-prueba`, además, con los precios que decidió el dueño ($2 y $10). La caja
de prueba se actualiza sin tocar su base de trabajo: en una PC donde ya se probó, esos precios salen hasta oprimir
«Empezar de cero» en la franja de prueba.

Usuarios de prueba que crea `preparar-caja` (solo para bases de prueba). En la caja se entra con la ficha y el PIN; el
usuario se escribe solo en el conteo desde el celular:

| Ficha (letra) | Usuario | Permisos | PIN |
|---|---|---|---|
| Cajero de prueba (C) | `cajero` | Permisos por defecto | 1111 |
| Encargado de prueba (E) | `encargado` | Corte de turno, productos y clientes | 2222 |
| Dueño de prueba (D) | `dueno` | Todos los permisos · primera cuenta (también el corte del día y los reportes) | 3333 |

No hay roles: cada usuario tiene sus permisos (Puede, Con autorización o No; en Administración, solo Puede o No), que se ven y se cambian en Configuración →
«Usuarios y permisos» (entrega 1). Al actualizar una base de antes, cada quien queda con lo que podía hacer con su rol.

### Instalar en la tienda (varias cajas)

Instala el administrador de cada PC; después todo se usa desde una cuenta común de Windows, sin administrador
(el cajero, el encargado y el dueño entran con su ficha y su PIN del POS desde esa misma cuenta;
[plan](docs/plan.md#instalación-y-usuarios-de-windows)).

1. **El paquete** (`actualizar.ps1`, `servicio\` y `caja\`, publicados para Windows de 64 bits con .NET adentro): lo deja
   cada corrida del flujo «Instalador de prueba» (el artefacto `PapeleriaPos-Actualizacion-1.0.<número>`), o se arma en una
   PC con Windows y el SDK de .NET:

   ```
   powershell -ExecutionPolicy Bypass -File instalacion\armar-paquete.ps1 -Version 1.0.57
   ```

   Cada .exe lleva esa versión (sin `-Version`, 0.0.0-local), la que compara `actualizar.ps1`.

2. **Caja principal:** como administrador, correr `servicio\instalar-servicio.ps1` desde el paquete. Copia el servicio a
   `C:\Program Files\PapeleriaPos\Servicio` y lo registra desde ahí. Arranca con Windows, deja la base en
   `C:\ProgramData\PapeleriaPos\pos.db`, los respaldos en `C:\ProgramData\PapeleriaPos\respaldos`, la configuración de esa
   PC en `C:\ProgramData\PapeleriaPos\servicio.json` (sin ajustes, si no había) y abre el firewall solo para la red local.
   Lo de cada PC (como el puente con eleventa) va en `servicio.json`, no en el `appsettings.json` del programa: manda sobre
   él, y las variables de entorno mandan sobre ella (la clave de eleventa va solo en la variable `Eleventa__Clave`).
   Mientras no haya datos reales, se copia ahí una base de prueba antes de instalar.
   Por esa misma puerta entran los celulares del conteo completo (`http://<ip>:5080/conteo`; en Inventario (F4) →
   «Conteo completo» salen la dirección y el QR): tienen que estar en el wifi de la misma red que la caja principal.

3. **Cada caja** (también la principal): como administrador, correr `caja\instalar-caja.ps1` desde el paquete (con
   `-ColaImpresion "<cola>"` en la PC de la WF-6590). La copia a Archivos de programa, deja
   `C:\ProgramData\PapeleriaPos\Caja` con escritura para los usuarios y pone el acceso en el escritorio de todos.
   Después, con la cuenta de Windows de todos los días, se abre la caja: la primera vez pide la dirección de la caja principal
   (o la busca en la red) y el PIN del dueño, y queda dada de alta.

4. **Actualizar:** como administrador, `actualizar.ps1` del paquete nuevo
   ([cómo](docs/pruebas-en-tienda.md#actualizar-la-caja-2-del-piloto)). Respalda la base y los programas en
   `C:\ProgramData\PapeleriaPos\actualizaciones`, no toca lo de la PC (la base, `servicio.json`, lo de la caja) y regresa sola a
   la versión de antes si la nueva no arranca. El flujo «Instalación» de GitHub Actions lo prueba en Windows.

Para probar en el navegador lo que hace la ventana de Windows:

- `--Caja:PideAlta=true` muestra la pantalla de alta.
- Para la contingencia hacen falta dos servicios: la caja principal en el 5080 y otro que hace de caja, con
  `--Urls=http://localhost:5090 --Caja:Servicio=http://localhost:5080 --Caja:PideAlta=true --Caja:BaseLocal=caja-local.db`.
  Al apagar la caja principal, la del 5090 sigue vendiendo; al prenderla, sube lo pendiente.

### Prueba con una persona real

Para que alguien de la tienda pruebe lo que ya funciona, solo y sin tocar eleventa ([plan](docs/plan.md#prueba-con-una-persona-real)).

- **El instalador** `PapeleriaPos-Prueba.exe` lo arma GitHub Actions en Windows (flujo «Instalador de prueba»). Se lanza a
  mano desde la pestaña Actions, con una etiqueta `prueba-*` o en un PR que toque `instalacion/`, y queda como artefacto
  de la corrida.
- **Lo instala el administrador, para todas las cuentas de Windows;** quien prueba la usa con una cuenta común. En el escritorio
  deja «Papelería POS (prueba)» y la carpeta «Ejemplos de la prueba»: el Léeme con las fichas y los PIN, y una factura
  XML y una nota PDF de un proveedor de la base.
- **La caja arranca su propio servicio** en el puerto 5180 y lo apaga al cerrarse. No hay servicio de Windows ni
  segunda caja. La base ficticia de trabajo va en `C:\ProgramData\PapeleriaPos-Prueba`. El instalador abre el 5180
  a la red local para probar el conteo completo desde un celular en el mismo wifi.
- **Se actualiza desde la caja:** Configuración › Acerca de › Actualización (solo en la caja de prueba).
  - Lista los instaladores que hay en GitHub, con lo que trae cada uno respecto a la instalada.
  - Lo baja, revisa su huella e instala encima en silencio. Windows pide el permiso de administrador; el instalador
    cierra la caja y la vuelve a abrir. Los datos de la prueba y los tickets en espera se quedan.
  - Si `main` tiene cambios sin instalador, «Armar una con lo último de main» lo pide a GitHub (tarda unos 15 minutos).
  - Hace falta un token «fine-grained» de GitHub con acceso solo a este repositorio: Actions de lectura (de lectura y
    escritura para armar) y Contents de lectura (para ver los cambios). Se guarda cifrado por Windows para ese usuario.
  - La primera versión con esto se instala a mano; desde ahí, las siguientes desde la caja.
- **La franja de prueba,** arriba, tiene la guía de tareas (las respuestas se guardan en un archivo en el escritorio),
  los avisos que le llegarían al dueño por Telegram y «Empezar de cero».
  - La guía es la de la venta del rediseño (versión 3): primero entrar con la ficha y vender; luego cambiar de usuario,
    el corte del turno y el del día; después, con otro turno del cajero, las páginas de Productos, Inventario y
    Clientes; al final, la Configuración.
  - Las respuestas van con la versión de la guía; las de otra versión no se cargan y su archivo se queda en
    `C:\ProgramData\PapeleriaPos-Prueba`.
  - Al terminar una persona, «Guardar respuestas y dejar la guía en blanco» deja su archivo en el escritorio y la guía
    lista para la siguiente.
- Necesita Windows 10 u 11 de 64 bits con WebView2 (viene con Windows 11 y con Windows 10 actualizado).

Lo mismo en desarrollo, en el navegador:

```
dotnet run --project src/PapeleriaPos.Herramientas -- preparar-prueba --salida prueba/datos/prueba.db --ejemplos prueba/Ejemplos
dotnet run --project src/PapeleriaPos.Servicio -- --environment Development --Urls=http://localhost:5180 --Caja:Servicio=http://localhost:5180 --ConnectionStrings:Pos="Data Source=prueba/datos/pos.db" --Prueba:Activa=true --Prueba:BaseInicial=prueba/datos/prueba.db --Telegram:Activo=false
```

### El día pico con varias cajas (simulador)

`simular-cajas` vende un día pico comprimido en varias cajas simuladas, como la caja de verdad pero sin la pantalla (con su
copia del catálogo, su contingencia y su base local), contra un servicio de prueba; mide cada operación contra las metas y al
final cuadra todo: ventas, folios, turnos, cortes y existencias ([avance](docs/plan.md#para-el-piloto-con-dos-cajas)). Lo
más simple es que arranque su propio servicio con una copia de una base de prueba y lo detenga al terminar:

```
dotnet build src/PapeleriaPos.Servicio -c Release
dotnet run -c Release --project src/PapeleriaPos.Herramientas -- simular-cajas --iniciar-servicio src/PapeleriaPos.Servicio/bin/Release/net10.0/PapeleriaPos.Servicio.dll --base prueba/datos/prueba.db --cajas 2 --tickets 120 --minutos 10 --caida 30s@50% --salida dia-pico.md
```

Sin `--base` arma la base como `preparar-prueba`. Sale con 0 si cuadra. En la tienda, `scripts\simular-dia-pico.cmd`
([guía](docs/pruebas-en-tienda.md#el-día-pico-en-la-caja-principal-real)).

Migraciones de la base (EF Core):

```
dotnet tool restore
dotnet ef migrations add <Nombre> -p src/PapeleriaPos.Datos -s src/PapeleriaPos.Datos -o Migraciones
```

Los datos reales del negocio (como la base de eleventa) nunca se guardan en el repositorio.

## Documentos

| Documento | Contenido |
|---|---|
| [Plan](docs/plan.md) | Contexto del negocio, decisiones aprobadas, fases y pendientes |
| [Modelo de datos](docs/modelo-datos.md) | Tablas de la fase 0: catálogo, precios, kardex e histórico importado |
| [Fase 1: caja](docs/fase-1-caja.md) | Entregas de la fase 1, ticket, pantalla de venta, turno y corte, datos nuevos |
| [Fase 1: control](docs/fase-1-control.md) | Entrega 2: reglas por rol, autorizaciones con PIN, bitácora, cancelaciones y devoluciones |
| [Fase 1: varias cajas](docs/fase-1-varias-cajas.md) | Entrega 3: servicio de Windows, alta de cajas con llave, respaldos y contingencia |
| [Fase 1: Telegram](docs/fase-1-telegram.md) | Entrega 4: alertas, autorización a distancia y resumen diario |
| [Fase 1: impresión](docs/fase-1-impresion.md) | Entrega 5: lo de Control Print dentro del POS, y los descuentos |
| [Fase 2: inventario](docs/fase-2-inventario.md) | Catálogo, compras con recepción ciega y el documento del proveedor, ajustes y conteos, etiquetas y sugerido de compra |
| [Arquitectura](docs/arquitectura.md) | Componentes, datos, contingencia, varias sucursales, red, respaldos y hardware |
| [Central y administración ante un suceso](docs/servidor-remoto.md) | Pospuesto: el central para varias sucursales (sincronización, precios por sucursal, mercancía, avisos que escalan, acciones a distancia, opciones y costos) |
| [Cámaras, puente y contador](docs/camaras.md) | El grabador Dahua con el POS (pospuesto) y cómo configurarlo por seguridad (ya) |
| [Prueba de varias sucursales en Linux](docs/prueba-sucursales-linux.md) | Pospuesto: las máquinas, las etapas, las fallas a provocar y las metas |
| [Notas de investigación](docs/notas.md) | La forma de trabajar y lo descubierto el 2026-10-08 y el 2026-10-09: lo que aplica ya con una sucursal (incluida la falla de la red de la tienda) y lo que sirve al retomar la multisucursal |
| [Pruebas en la tienda](docs/pruebas-en-tienda.md) | Guía para los sábados y domingos en la matriz: qué se prepara entre semana y qué se hace cada fin de semana |
| [Caja y control](docs/caja-y-control.md) | Venta, cobro, cortes, autorizaciones configurables, bitácora y alertas |
| [Catálogo e inventario](docs/inventario.md) | Variantes, presentaciones, códigos, kardex, compras, conteos y precios |
| [Servicios y costeo](docs/servicios.md) | Recetas, servicios con parámetros y tres niveles de costo |
| [Pedidos](docs/pedidos.md) | Trabajos que esperan: cola compartida, charolas, recogida, clientes registrados y duplicados |
| [WhatsApp](docs/whatsapp.md) | Cómo llegan los archivos de WhatsApp a los pedidos |
| [Módulo de impresión](docs/impresion.md) | Costo real de cada impresión (reescritura de Control Print) |
| [Módulo de impresión: el código](src/Impresiones/README.md) | Dónde vive, sus límites con el resto del POS (el puerto y la fachada), sus pruebas y cómo cambiar su esquema |
| [Migración desde eleventa](docs/migracion-eleventa.md) | Importación, diagnóstico y arranque |
| [Rediseño de la interfaz](docs/propuesta-interfaz.md) | Aprobado (2026-10-10): la caja rediseñada con permisos por usuario, el cobro, el corte, los reportes, la configuración, el rendimiento en las dos cajas, la app Android en Kotlin y la base; con un [prototipo](docs/propuesta-interfaz/prototipo.html) que se abre con doble clic |
| [Ideas a evaluar](docs/ideas.md) | La multisucursal pospuesta, cámaras con una sola sucursal, dispositivos propios y otras ideas |
