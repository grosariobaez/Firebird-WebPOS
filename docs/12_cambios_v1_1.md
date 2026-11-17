# JomliaWebPosSync V1.1 — cambios sobre d0338ec

Cambios quirúrgicos sobre la implementación existente. No se reconstruyó el proyecto. No se ejecutaron build, restore, test, publish, servicios, SQL ni conexiones a bases de datos.

## Código

| Archivo | Motivo | Cambio |
|---|---|---|
| `Services/InvoiceValidator.cs` | P0 anulaciones, P0 ITBIS, P1 pagos | Bloqueo incondicional de `ANU`, validación de replay para payload preparado, `SUM(FPA_MONTO)=FAC_TOTAL`, validación de componentes de pago y validación del importe de ITBIS según la estrategia configurada. |
| `Services/InvoiceMapper.cs` | P0 ITBIS, P1 fuente fiscal | Estrategia `LineItbisInterpretation`, cálculo opcional desde porcentaje confirmado, proveedor fiscal inyectado. `details[].itbis` sigue siendo monetario. |
| `Services/FiscalItbis.cs` (nuevo) | P0 ITBIS | Helper pequeño: tasa configurada por `tax`, redondeo y conversión porcentual. |
| `Services/PaymentConsistency.cs` (nuevo) | P1 pagos | Validador aislado de FPA_TIPO contra componentes y FAC_PAGADO, sin reescribir el tipo. |
| `Services/FiscalDataProvider.cs` (nuevo) | P1 clientes/vencimiento | Interfaz mínima e implementación basada solo en configuración existente. |
| `Services/SyncCoordinator.cs` | P0/P1 anulaciones | `ValidateReplay` antes de comparar huellas, control de eventos antes del ACK y reconciliación de eventos en el polling. |
| `Repositories/FirebirdRepository.cs` | P1 eventos | Lectura condicional y marcado de `PA_FB_EVENTOS` sin tocar `PA_FB_OUTBOX`. |
| `Contracts/Repositories.cs`, `Models/SourceInvoice.cs` | P1 eventos | Contrato y modelo `SourceEvent`. |
| `Worker.cs`, `Program.cs` | P1 eventos/fuente fiscal | Reconciliación tolerante a fallos y registro de `IFiscalDataProvider`. |
| `Configuration/FiscalMappingsOptions.cs`, `ConfigurationValidator.cs`, `appsettings*.json` | P0/P1 configuración | Nuevas opciones, compatibilidad del booleano heredado, validación de reglas y prohibición de `ANU`. |
| `Resilience/RetryPolicy.cs` | P2 resiliencia | Clasificación específica de disco/permisos/archivo bloqueado/ruta; red y timeout permanecen transitorios. |

## SQL preparado (NO ejecutado)

`docs/11_firebird_outbox_safety_v4.sql` crea `PA_FB_EVENTOS`, sustituye `PA_ENCOLAR_FACTURA` sin alterar estados reclamados/sincronizados/en error y añade un trigger `AFTER UPDATE` sobre `FACTURAS` que registra anulaciones. Incluye rollback comentado. No se ejecutó contra ninguna base.

## Compatibilidad

- No se cambió el sobre, la versión del formato, el serializador ni el hash SHA-256.
- FPA_TIPO viaja sin conversión; la equivalencia sigue en `dbo.PA_FB_PaymentTypeMap`.
- No se modifica `dbo.usp_ImportarFacturaFirebird`, `PA_FB_SourceMap` ni el orden Payment → Detail → Header.
- `ULTIMO_ERROR` sigue siendo el token de propietario durante R; los eventos se guardan en otra tabla.
- Los documentos persistidos se reenvían con su JSON original; un cambio de snapshot sigue bloqueando.
