# Módulo de impresión

El núcleo de [Control Print](https://github.com/Marioalr/control-print) dentro del POS: cada impresión de una PC se
mide, se le pone costo y precio y se cobra en el mismo ticket. Vive aparte del resto del POS para trabajarlo sin tocar
la venta ni la interfaz (decisión del dueño, 2026-10-14). Qué hace y qué falta: [módulo de
impresión](../../docs/impresion.md) y [entrega 5](../../docs/fase-1-impresion.md).

**Si vas a trabajar en el módulo, empieza aquí.** La regla: la lógica cambia dentro de `src/Impresiones` y
`tests/Impresiones`; del resto del POS solo se tocan los archivos de [la lista de abajo](#lo-que-se-toca-fuera-del-módulo),
y solo cuando hace falta.

## Qué es y qué no es

Es:

- **El agente** de cada PC que imprime: vigila la cola de Windows, mide la tinta de cada trabajo y lo manda al servicio.
- **El costo y el precio** de cada impresión, de una cotización (un PDF que se mide antes de imprimir) y de las copias
  hechas en el panel de la impresora.
- **Lo que falta cobrar** (F2 en la caja), las no vendidas con su motivo y el desglose del día.
- **La impresora vista por la red** desde la caja principal: niveles, contador, las hojas que de verdad salieron
  (atascos y cancelaciones), el contador contra lo cobrado, la calibración y sus avisos.

Hoy hay una impresora por sucursal: su configuración (la llave es la sucursal), sus lecturas y su vigilancia van por
sucursal, y el agente de cada caja vigila una sola cola (`ColaImpresion` en la configuración de la caja). Las dos
WF-6590 y la L5590 de la matriz piden cambiar eso ([pendientes](../../docs/impresion.md#lo-que-hay-que-lograr)).

No es:

- **La impresión de tickets, etiquetas y hojas de códigos.** Eso es `src/PapeleriaPos.Impresion` (en singular), y
  no tiene que ver con este módulo.
- **La interfaz.** Las pantallas (F2, cotizar, la pestaña Impresora) viven en `src/PapeleriaPos.Interfaz` y las
  trabajan otras sesiones. El módulo les da sus contratos y su API.
- **La venta.** El ticket, el cobro, las cancelaciones y las devoluciones son del POS. El módulo solo deja sus trabajos
  cobrados o por cobrar, por [la fachada](#la-fachada-para-la-venta).
- **Una base aparte.** Sus tablas viven en la base del POS y sus migraciones en `src/PapeleriaPos.Datos` (un solo
  contexto: el cobro marca las impresiones cobradas en la misma transacción que la venta).

## Los proyectos

| Proyecto | Qué tiene | Depende de |
|---|---|---|
| `PapeleriaPos.Impresiones` | El dominio (`TrabajoImpresion`, `ConfiguracionImpresion`, `LecturaImpresora`), el costo, la cotización, la calibración y la impresora por la red (`Medicion/`, traído de Control Print con sus nombres en inglés), los contratos que viajan entre la caja y el servicio (`Contratos.cs`) y el puerto (`IPosParaImpresiones.cs`) | `PapeleriaPos.Nucleo` |
| `PapeleriaPos.Impresiones.Datos` | El mapeo de sus tablas (`ModeloImpresiones`), los trabajos con su precio (`RegistroImpresiones`), la impresora de cada sucursal (`ServicioImpresora`), la fachada para la venta (`ImpresionesEnLaVenta`) y la importación de la configuración de Control Print | `PapeleriaPos.Impresiones` y EF Core (SQLite). **No** depende de `PapeleriaPos.Datos` |
| `PapeleriaPos.Impresiones.Servicio` | La API (`ApiImpresiones`), la vigilancia de la impresora (`VigilanciaImpresora`, con `ILectorImpresora`) y `AgregarImpresiones` | `PapeleriaPos.Impresiones.Datos`, `PapeleriaPos.Datos` y ASP.NET Core |
| `PapeleriaPos.Impresiones.Windows` | El agente (`AgenteImpresion`): el spool, el PrintTicket, la tinta por página y medir un PDF (`MedidorPdfWindows`) | `PapeleriaPos.Impresiones`. Solo Windows |

Quién los usa:

- `PapeleriaPos.Datos` aplica el mapeo del módulo (con sus llaves foráneas hacia sucursales, cajas y ventas encima),
  implementa el puerto (`PosParaImpresiones`) y arma los servicios del módulo sobre la base del POS
  (`ImpresionesDelPos`).
- `PapeleriaPos.Servicio` llama a `AgregarImpresiones` y monta la API en su grupo «/api».
- `PapeleriaPos.Interfaz` usa los contratos y `IMedidorPdf`; `PapeleriaPos.Caja` arranca el agente y pone el medidor
  de PDF de Windows.

## El puerto: lo que el módulo le pide al POS

El módulo no conoce las tablas ni los servicios del POS. Lo que necesita se lo pide por `IPosParaImpresiones`, y nada
más:

- `Reloj`: la hora (las pruebas la fijan).
- `CajaAsync`: la caja de la PC que imprimió, con su sucursal y su nombre (las copias del panel llevan el nombre de la
  caja como PC).
- `Avisar(sucursal, tipo, texto, clave)`: un aviso al dueño. Los tipos son del módulo (`AvisoImpresiones`: `Impresora`
  para la tinta, la caja de mantenimiento y la impresora que no responde; `HojasFaltantes` para las hojas que no
  salieron). Cómo y por dónde le llega lo decide el POS: hoy, por sus alertas de siempre, que están en pausa; no hace
  falta trabajar en ellas. Con la misma clave, un aviso no sale dos veces. No guarda: el aviso se guarda con lo
  siguiente que guarde el módulo, en la misma transacción.
- `FueraDeHorario`: si la tienda está cerrada (de noche no se avisa que la impresora no responde).

Lo implementa `PosParaImpresiones` (`src/PapeleriaPos.Datos/ImpresionesDelPos.cs`) sobre la base del POS, y
`PosDePrueba` en las pruebas del módulo. Si el módulo necesita algo más del POS, se agrega al puerto con su porqué, y se
implementa en los dos.

Otros puntos que se cambian sin tocar el módulo: `ILectorImpresora` (leer la impresora por la red; las pruebas ponen
la suya) e `IMedidorPdf` (medir un PDF; en la caja de Windows es `MedidorPdfWindows`).

## La fachada para la venta

El cobro, las devoluciones y el resumen del día no tocan las tablas del módulo: usan `ImpresionesEnLaVenta` (con
`ImpresionesDelPos.EnLaVenta(db)`), y nada de ella guarda, porque va dentro de la transacción de la venta.

| Método | Quién lo usa | Qué hace |
|---|---|---|
| `ParaCobrarAsync(sucursal, ids)` | `ApiCaja` (`POST /api/ventas`) | Carga las impresiones que van en el ticket; todas tienen que seguir por cobrar |
| `Cobrar(trabajos, venta, ahora)` | `RegistroCaja` | Las deja cobradas con la venta |
| `LiberarAsync(venta)` | `RegistroDevoluciones` | Si se cancela la venta o se devuelve su línea «Impresiones», vuelven a estar por cobrar |
| `ResumenDelDiaAsync(sucursal, fecha)` | El resumen del día del POS | Las no vendidas (cuántas y su costo) y el contador contra lo registrado |

La venta también lee de cada `TrabajoImpresion` su precio y su costo, para la línea «Impresiones». Si algo de esto
tiene que cambiar, se cambia aquí y en esos archivos a la vez.

## La API

`MapearApiImpresiones` monta las rutas sobre el grupo que le da el servicio:
`app.MapGroup("/api").ConErrores().SoloSuCaja().MapearApiImpresiones()` (en `src/PapeleriaPos.Servicio/Program.cs`).
Los errores para el cajero (400 con el mensaje, 403 cuando lo detiene un permiso) y que una caja solo opere la suya los
pone el servicio, como en el resto de la API. Lo que es del POS lo toma la API de su base: los permisos de cada usuario
(entrega 1: ya no hay roles), los nombres de quien atendió y el PIN. El costo de cada impresión (y la ganancia y el
margen al cotizar) solo va a quien tiene «Ver costos, utilidad y margen»; el desglose del día, a quien tiene «Consultar la
bitácora»; y cambiar cómo se lee la impresora o aplicar la calibración lo hace quien tiene «Configurar la impresora», con
su usuario y su PIN. Al migrar de los roles quedaron igual que antes: el costo, solo el dueño; el desglose, el dueño y el
encargado; la impresora, el dueño. La capa de datos del módulo no revisa permisos: eso lo hace la API.

Los contratos (`Contratos.cs`) son lo que entienden la interfaz y las cajas, y el agente manda `TrabajoMedidoDto` por la
bandeja de la caja (si no hay conexión, se queda guardado y se manda después, tal cual). Cambios sin riesgo: agregar un
campo al final con su valor por omisión. Quitar o renombrar uno rompe la interfaz y lo que una caja tenga en su bandeja:
eso se habla antes con quien trabaja la interfaz.

## Las pruebas

```
dotnet test tests/Impresiones/PapeleriaPos.Impresiones.Pruebas
```

Corren en Linux y en Windows y solo dependen del módulo. Ahí están las traídas de Control Print (costo, cotización,
calibración, contador, verificación de hojas y la impresora por la red), la importación de su configuración y
`ModuloSinElPosTests`: el mapeo y los servicios sobre una base con solo las tablas del módulo (`BaseDelModulo`) y un POS
falso (`PosDePrueba`). Lo nuevo del módulo se prueba ahí.

Las del agente (el spool, el PrintTicket y medir un PDF) solo corren en Windows, en el CI:

```
dotnet test tests/Impresiones/PapeleriaPos.Impresiones.Windows.Pruebas
```

Antes de proponer un cambio, también las del POS, que pasan por el servicio de verdad (la API de impresiones, la
impresora y sus avisos, el cobro, las devoluciones, el resumen del día y la base de la prueba):

```
dotnet test tests/PapeleriaPos.Pruebas
```

En Linux, compilar toda la solución pide `dotnet build -p:EnableWindowsTargeting=true` (el agente y la caja son de
Windows); probar solo el módulo o el POS, no.

## Cambiar el esquema

Las tablas del módulo (`TrabajosImpresion`, `ConfiguracionesImpresion` y `LecturasImpresora`) viven en la base del POS,
y sus migraciones en `src/PapeleriaPos.Datos/Migraciones`. Para cambiar una:

1. Cambia la clase (por ejemplo `TrabajoImpresion.cs`) y, si hace falta, su mapeo en `ModeloImpresiones.cs`: el nombre
   de la tabla, la llave, la precisión y los índices.
2. Si la tabla nueva o la columna nueva apunta a una tabla del POS (sucursal, caja, venta), su llave foránea va en
   `src/PapeleriaPos.Datos/Configuracion/ImpresionesConfiguracion.cs`, no en el módulo.
3. Crea la migración en `PapeleriaPos.Datos`:

   ```
   dotnet tool restore
   dotnet ef migrations add <Nombre> -p src/PapeleriaPos.Datos -s src/PapeleriaPos.Datos -o Migraciones
   ```

4. Revisa que la migración solo toque las tablas del módulo. La primera que se haga después de separarlo también
   reescribe la foto del modelo (`PosDbContextModelSnapshot.cs`) con los nombres nuevos de las clases (antes estaban en
   el núcleo del POS): eso es esperado y no cambia la base.

Si se te olvida, `MigracionesTests.El_modelo_no_tiene_cambios_sin_su_migracion` (en las pruebas del POS) falla. Si otra
rama también agregó una migración, al juntarlas hay que volver a crear la tuya encima de la suya: la foto del modelo es
una sola para toda la base.

## Lo que se toca fuera del módulo

| Archivo | Cuándo |
|---|---|
| `src/PapeleriaPos.Datos/Migraciones/` | Con cada cambio al esquema ([arriba](#cambiar-el-esquema)) |
| `src/PapeleriaPos.Datos/Configuracion/ImpresionesConfiguracion.cs` | Si una tabla del módulo apunta a una del POS |
| `src/PapeleriaPos.Datos/ImpresionesDelPos.cs` | Si el puerto cambia (`PosParaImpresiones`), si un aviso nuevo necesita su propio tipo de alerta del POS (mientras, sale como aviso de la impresora), o si hay un servicio nuevo del módulo que arme el POS |
| `src/PapeleriaPos.Datos/RegistroCaja.cs`, `RegistroDevoluciones.cs` y el resumen del día, `src/PapeleriaPos.Servicio/ApiCaja.cs` | Solo si cambia [la fachada](#la-fachada-para-la-venta) |
| `src/PapeleriaPos.Servicio/Program.cs` | Ya llama a `AgregarImpresiones` y monta la API: solo si eso cambia |
| `src/PapeleriaPos.Interfaz` (`ClienteCaja.cs`, `Componentes/ImpresionesPorCobrar.razor`, `ConfiguracionImpresora.razor`, `Cotizar.razor`, `PantallaVenta.razor`, `EstadoCaja.razor`) | Si cambian los contratos o la API que usa la interfaz: con quien la trabaja |
| `src/PapeleriaPos.Caja/MainWindow.xaml.cs` | Si cambia cómo se arranca el agente (por ejemplo, varias colas) o el medidor de PDF |
| `src/PapeleriaPos.Herramientas` (`Program.cs`: `importar-control-print`; `Prueba/PreparacionPrueba.cs`: las 4 impresiones por cobrar de la base de la prueba) | Si cambia lo que usan |
| `tests/PapeleriaPos.Pruebas` (`Servicio/ImpresionesApiTests.cs`, `ImpresoraApiTests.cs`, `PruebaApiTests.cs`) | Si cambia lo que la API responde |
| `PapeleriaPos.slnx` y `.github/workflows/pruebas.yml` | Con un proyecto nuevo o un proyecto de pruebas nuevo |
