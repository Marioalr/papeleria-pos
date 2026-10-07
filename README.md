# Papelería POS

Punto de venta a la medida para una papelería con varias sucursales: venta, inventario, servicios (impresiones,
copias, engargolado, trámites…), pedidos y control de cada sucursal sin que el dueño tenga que estar presente.
Reemplaza a eleventa multicaja e incorpora lo que hoy hace [Control Print](https://github.com/Marioalr/control-print).

> **Estado: fase 2.** La caja vende, cobra e imprime, con control (reglas, autorizaciones y bitácora) y varias cajas
> en red; el catálogo (F3) y las compras (F4) ya se manejan en el POS. Todo se prueba con datos ficticios. Lo demás
> sigue en planeación; las decisiones se cierran en el [plan](docs/plan.md).

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

### Probar la caja

1. Una base de prueba (ficticia o importada) con la Caja 1 y usuarios de prueba:

   ```
   dotnet run --project src/PapeleriaPos.Herramientas -- generar-ficticios --salida ficticios.db
   dotnet run --project src/PapeleriaPos.Herramientas -- preparar-caja --base ficticios.db
   ```

2. El servicio de la sucursal, apuntando a esa base (en desarrollo también muestra la caja en el navegador, en
   <http://localhost:5080>, con vista previa del ticket en lugar de imprimir):

   ```
   dotnet run --project src/PapeleriaPos.Servicio -- --environment Development --ConnectionStrings:Pos="Data Source=ficticios.db"
   ```

3. La caja de Windows (`src/PapeleriaPos.Caja`) se conecta al servicio e imprime en la impresora de `caja.json`:
   `tcp://192.168.1.100:9100` para una impresora de red, el nombre de una impresora de Windows, o vacío para ver el
   ticket en pantalla. El servicio acepta lo mismo en `Caja:Impresora` para la caja del navegador.

Usuarios de prueba que crea `preparar-caja` (solo para bases de prueba):

| Usuario | Rol | PIN |
|---|---|---|
| `cajero` | Cajero | 1111 |
| `encargado` | Encargado (hace el corte Z) | 2222 |
| `dueno` | Dueño | 3333 |

### Instalar en la tienda (varias cajas)

Instala el administrador de cada PC; después todo se usa desde un usuario común de Windows, sin administrador
([plan](docs/plan.md#instalación-y-usuarios-de-windows)).

1. Publicar, en la PC de desarrollo:

   ```
   dotnet publish src/PapeleriaPos.Servicio -c Release -r win-x64 --self-contained -o publicar/servicio
   dotnet publish src/PapeleriaPos.Caja -c Release -r win-x64 --self-contained -o publicar/caja
   ```

2. **Caja principal:** copiar `publicar/servicio` (por ejemplo a `C:\PapeleriaPos\Servicio`) y, como
   administrador, correr ahí `instalar-servicio.ps1`. El servicio arranca con Windows, deja la base en
   `C:\ProgramData\PapeleriaPos\pos.db`, los respaldos en `C:\ProgramData\PapeleriaPos\respaldos` y abre el firewall
   solo para la red local. Mientras no haya datos reales, se copia ahí una base de prueba antes de instalar.

3. **Cada caja** (también la principal): como administrador, correr `instalar-caja.ps1` desde `publicar/caja` (con
   `-ColaImpresion "<cola>"` en la PC de la WF-6590). La copia a Archivos de programa, deja
   `C:\ProgramData\PapeleriaPos\Caja` con escritura para los usuarios y pone el acceso en el escritorio de todos.
   Después, con el usuario de todos los días, se abre la caja: la primera vez pide la dirección de la caja principal
   (o la busca en la red) y el PIN del dueño, y queda dada de alta.

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
- **Lo instala el administrador, para todos los usuarios;** quien prueba la usa con su usuario común. En el escritorio
  deja «Papelería POS (prueba)» y la carpeta «Ejemplos de la prueba»: el Léeme con los usuarios y PIN, y una factura
  XML y una nota PDF de un proveedor de la base.
- **La caja arranca su propio servicio** en el puerto 5180 y lo apaga al cerrarse. No hay servicio de Windows ni
  segunda caja. La base ficticia de trabajo va en `C:\ProgramData\PapeleriaPos-Prueba`.
- **La franja de prueba,** arriba, tiene la guía de tareas (las respuestas se guardan en un archivo en el escritorio),
  los avisos que le llegarían al dueño por Telegram y «Empezar de cero».
- Necesita Windows 10 u 11 de 64 bits con WebView2 (viene con Windows 11 y con Windows 10 actualizado).

Lo mismo en desarrollo, en el navegador:

```
dotnet run --project src/PapeleriaPos.Herramientas -- preparar-prueba --salida prueba/datos/prueba.db --ejemplos prueba/Ejemplos
dotnet run --project src/PapeleriaPos.Servicio -- --environment Development --Urls=http://localhost:5180 --ConnectionStrings:Pos="Data Source=prueba/datos/pos.db" --Prueba:Activa=true --Prueba:BaseInicial=prueba/datos/prueba.db --Telegram:Activo=false
```

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
| [Administrar desde fuera](docs/servidor-remoto.md) | Central en servidor virtual, gestión por excepción y costos |
| [Caja y control](docs/caja-y-control.md) | Venta, cobro, cortes, autorizaciones configurables, bitácora y alertas |
| [Catálogo e inventario](docs/inventario.md) | Variantes, presentaciones, códigos, kardex, compras, conteos y precios |
| [Servicios y costeo](docs/servicios.md) | Recetas, servicios con parámetros, utilidad directa y neta |
| [Pedidos](docs/pedidos.md) | Trabajos que esperan: cola compartida, charolas, recogida, clientes registrados y duplicados |
| [WhatsApp](docs/whatsapp.md) | Cómo llegan los archivos de WhatsApp a los pedidos |
| [Módulo de impresión](docs/impresion.md) | Costo real de cada impresión (reescritura de Control Print) |
| [Migración desde eleventa](docs/migracion-eleventa.md) | Importación, diagnóstico y arranque |
| [Ideas a evaluar](docs/ideas.md) | Dispositivos propios (contador de clientes, presencia, vigilancia) y otras ideas |
