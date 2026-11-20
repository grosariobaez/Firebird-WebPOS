# Configuración y operación — IXU JomliaWebPosSync

Implementación de código fuente en **.NET 10 para Windows**, basada en los contratos V3-QA de `docs/`. La entrega está deshabilitada por defecto (`Sync:Enabled=false`); en ese estado no se accede a las bases ni se crea el almacén local.

Abrir `JomliaWebPosSync.slnx`. Proyecto: `JomliaWebPosSync/JomliaWebPosSync.csproj`.

## Identidad comercial

IXU. Inteligencia · Ejecución · Utilidad. Tecnología para operaciones del mundo real.
INNOVAXD UX SRL — https://innovaxd.ca/es/

## Componentes y flujo

| Componente | Función |
|---|---|
| `Program.cs`, `Worker.cs` | Host, DI, Windows Service/SCM, configuración validada, un procesador secuencial, polling con pausa y cancelación. |
| `Repositories/FirebirdRepository.cs` | Polling de solo lectura: candidatas por fecha/ID y emisión completa; snapshot de las tres tablas en una transacción SNAPSHOT. |
| `Services/InvoiceReader.cs` | Obtención del snapshot completo. |
| `Services/InvoiceMapper.cs`, `InvoiceValidator.cs`, `UnitCatalog.cs` | Equivalencias explícitas, comprobación de emisión, tipos/documentos, precisión decimal, ITBIS por línea y conciliación de totales/pagos. |
| `Serialization/InvoiceJsonSerializer.cs` | JSON determinista, fechas ISO, propiedades ordenadas, colecciones por secuencia, decimales invariantes y huellas SHA-256. |
| `Services/PayloadStore.cs`, `Security/` | Sobre inmutable, checksum, flush a disco, renombrado atómico sin sobrescritura, ACL y bloqueo de instancia. |
| `Repositories/SqlServerRepository.cs` | Verifica mapas existentes y ejecuta exclusivamente `dbo.usp_ImportarFacturaFirebird`, parámetros tipados, timeout y `DocNumber OUTPUT`. |
| `Services/SyncCoordinator.cs` | SourceMap → snapshot → validación → JSON durable → SP. El sobre se reutiliza en reintentos. |
| `Resilience/` | Backoff exponencial con jitter para el polling, clasificación de errores y reintentos sin límite para fallos transitorios. |
| `Diagnostics/SyncDiagnostics.cs` | Logs estructurados y métricas internas mediante `System.Diagnostics.Metrics`. |

No se escribe en Firebird. No se insertan registros directamente en Header, Detail ni Payment. La confirmación es la fila de `dbo.PA_FB_SourceMap`; **no significa autorización DGII**.

## Configuración requerida

`JomliaWebPosSync/appsettings.json`/`appsettings.example.json` contienen opciones sin secretos. Los valores desconocidos están vacíos/deshabilitados. Las contraseñas se guardan cifradas con DPAPI `LocalMachine` en `%ProgramData%\PuntoAparte\JomliaWebPosSync\secrets.json`.

- `Firebird:*`: host, puerto, base, charset, usuario/rol y credenciales protegidas.
- `SqlServer:*`: servidor/base. `Encrypt=true` y `TrustServerCertificate=false` por defecto; `TrustServerCertificate=true` solo mediante selección explícita del administrador con advertencia.
- `WebPos:SourceCompanyId`, `WebPos:CompanyLicCod`, `WebPos:BranchCod`, `WebPos:PosCod`: valores exclusivos y confirmados para JOMLIA.
- `Sync:InvoiceStart=YYYY-MM-DD`: fecha del corte (`FAC_FECHA >= InvoiceStart`).
- `WebPos:InitialInvoiceIdByCompany[SourceCompanyId]`: **IDFACTURA mínimo a sincronizar (inclusive)** (`IDFACTURA >= valor`). No describir como "última factura procesada".
- `Sync:PayloadDirectory`: carpeta dedicada local NTFS, preservada entre reinicios/actualizaciones.
- `Sync:Enabled`: permanece `false` al instalar; habilitar solo con activación explícita.

No utilizar valores de QA como predeterminados de producción. Las opciones se fijan durante el arranque; reiniciar para aplicar cambios.

## Equivalencias fiscales pendientes

Las claves son literales de Firebird. No se convierten códigos ni se adivinan equivalencias.

| Opción de `FiscalMappings` | Contenido que debe confirmarse |
|---|---|
| `DocumentTypes` | Diccionario `FAC_TIPO -> "F"`; cualquier otro tipo final se rechaza. |
| `AllowedHeaderStatuses`, `AllowedDetailStatuses` | Estados comerciales admitidos. `ANU` está prohibido. |
| `PricesExcludeItbisConfirmed` | DFC_PRECIO antes de ITBIS para taxMode=0. |
| `LineItbisInterpretation`, `ItbisRatesByTaxCode`, `ItbisRoundedPerLineConfirmed` | Estrategia explícita para DFC_ITBIS y tasas canónicas 0/3/4=0%, 1=18%, 2=16%. |
| `TaxCodes` | Valor literal de DET_FACTURA.ITBIS -> tax 0/1/2/3/4. |
| `ItemClasses` | Clave `IDCOMPANIA/IDPRODUCTO` -> 0 inventario o 1 servicio. |
| `Units` | MEDIDA -> código o abreviatura del catálogo V6. |
| `Customers` | Clave `IDCOMPANIA/IDCLIENTE` -> `CustomerType` y `Ruc` reales. |
| `NcfExpiryDates` | Clave `IDCOMPANIA/eNCF` -> fecha ISO `yyyy-MM-dd`, salvo E32. |
| `PaymentTypes` | FPA_TIPO -> `WebPosType` (01..08) y descripción opcional. |
| `PaymentConsistencyRules` | Regla obligatoria por tipo de pago presente. |
| `Currency`, `CurrencyRate`, `OperMode` | Moneda, tasa y operación reales del origen. |

## Corte inicial y detección de emitidas

Cada poll consulta solo `FACTURAS`, `DET_FACTURA` y `FORMAS_PAGO`. Una candidata cumple:

- `IDCOMPANIA = WebPos:SourceCompanyId`.
- `IDFACTURA >= InitialInvoiceIdByCompany[SourceCompanyId]`.
- `FAC_FECHA >= Sync:InvoiceStart`.
- `FAC_STATUS` distinto de `ANU`; hay al menos una línea en `DET_FACTURA`.
- Existe al menos `FORMAS_PAGO.FPA_STATUS='EMI'` y no existe ninguna fila con otro estado.

No se guarda un cursor de "último ID visto". Las ya importadas se omiten por `dbo.PA_FB_SourceMap`.

## Recuperación e idempotencia

- Antes del primer envío se guarda un payload inmutable con checksum y huella del snapshot.
- `dbo.PA_FB_SourceMap` es la autoridad para no reenviar.
- Los reintentos reutilizan el mismo JSON; un resultado incierto se resuelve consultando SourceMap.
- Si el snapshot cambia después de preparar el sobre, la factura se bloquea para revisión.
- No hay purge automática de payloads.

## Permisos y operación

Firebird: solo SELECT sobre `FACTURAS`, `DET_FACTURA` y `FORMAS_PAGO`. SQL Server: EXECUTE del SP y SELECT sobre `PA_FB_SourceMap`/`PA_FB_PaymentTypeMap`.

El instalador registra el servicio con inicio automático, recuperación SCM y origen EventLog. El configurador IXU permite probar conexiones, guardar configuración protegida y activar/desactivar la sincronización. El servicio no requiere sesión interactiva.

Logs JSON y Windows EventLog con origen `JomliaWebPosSync`. No se escriben payloads, clientes ni credenciales en los logs.
