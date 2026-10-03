# Papelería POS

Punto de venta a la medida para una papelería con varias sucursales: venta, inventario, servicios (impresiones,
copias, engargolado, trámites…), pedidos y control de cada sucursal sin que el dueño tenga que estar presente.
Reemplaza a eleventa multicaja e incorpora lo que hoy hace [Control Print](https://github.com/Marioalr/control-print).

> **Estado: fase 0.** Se construye el modelo de datos con datos ficticios. El resto sigue en planeación; las
> decisiones se cierran en el [plan](docs/plan.md).

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
