# Configuración y operación de JOMLIA → WebPOS

Implementación de código fuente en **.NET 10 para Windows**, basada en los contratos V3-QA de `docs/`. No se han ejecutado build, restore, test, publish, el servicio, scripts SQL ni conexiones a bases de datos. La compilación y la validación contra clones pertenecen a una fase posterior.

Abrir `JomliaWebPosSync.slnx`. Proyecto: `JomliaWebPosSync/JomliaWebPosSync.csproj`. La entrega está deshabilitada por defecto (`Sync:Enabled=false`); en ese estado no se accede a las bases ni se crea el almacén local.

## Componentes y flujo

| Componente | Función |
|---|---|
| `Program.cs`, `Worker.cs` | Host, DI, Windows Service/SCM, configuración validada, un procesador secuencial, polling con pausa y cancelación. |
| `Repositories/FirebirdRepository.cs` | Cola parametrizada, reclamo condicional, renovación, recuperación, ACK y lectura de las tres tablas en SNAPSHOT de solo lectura. |
| `Services/InvoiceReader.cs` | Obtención del snapshot completo. |
| `Services/InvoiceMapper.cs`, `InvoiceValidator.cs`, `UnitCatalog.cs` | Equivalencias explícitas, comprobación de emisión, tipos/documentos, precisión decimal, ITBIS por línea y conciliación de totales/pagos. |
| `Serialization/InvoiceJsonSerializer.cs` | JSON determinista, fechas ISO, propiedades ordenadas, colecciones por secuencia, decimales invariantes y huellas SHA-256. |
| `Services/PayloadStore.cs`, `Security/` | Sobre inmutable, checksum, flush a disco, renombrado atómico sin sobrescritura, ACL y bloqueo de instancia. |
| `Repositories/SqlServerRepository.cs` | Verifica mapas existentes y ejecuta exclusivamente `dbo.usp_ImportarFacturaFirebird`, parámetros tipados, timeout y `DocNumber OUTPUT`. |
| `Services/SyncCoordinator.cs` | Reclamo → snapshot → validación → JSON durable → SP → ACK Firebird. |
| `Resilience/` | Lease renovable, backoff exponencial con jitter, clasificación de errores y recuperación sin límite de intentos transitorios. |
| `Diagnostics/SyncDiagnostics.cs` | Logs estructurados y métricas internas mediante `System.Diagnostics.Metrics`. |

No se escribe en FACTURAS, DET_FACTURA ni FORMAS_PAGO. No se insertan registros directamente en Header, Detail o Payment. El SP conserva su transacción y el orden Payment → Detail → Header. S significa importación confirmada por SQL Server; **no significa autorización DGII**.

## Configuración requerida

`JomliaWebPosSync/appsettings.example.json` contiene todas las opciones y ningún secreto. Los valores desconocidos están vacíos/deshabilitados. Se admite `appsettings.Local.json` fuera del control de versiones, o variables de entorno con prefijo `JOMLIA_SYNC_` y `__` como separador:

- `JOMLIA_SYNC_Firebird__User`, `JOMLIA_SYNC_Firebird__Password`.
- `JOMLIA_SYNC_Firebird__Charset`: confirmar charset real; no asumir UTF8 en un sistema Delphi 5.
- `JOMLIA_SYNC_SqlServer__Server`. Autenticación integrada por defecto; alternativamente `SqlServer__IntegratedSecurity=false`, `SqlServer__User`, `SqlServer__Password` bajo el mismo prefijo.
- `JOMLIA_SYNC_WebPos__SourceCompanyId`, `WebPos__CompanyLicCod`, `WebPos__BranchCod`, `WebPos__PosCod`: valores exclusivos y confirmados para JOMLIA.
- `JOMLIA_SYNC_Sync__PayloadDirectory`: carpeta dedicada local NTFS, preservada entre reinicios/actualizaciones.
- `JOMLIA_SYNC_Sync__Enabled`: habilitar solo en la fase posterior autorizada.

Las opciones se fijan durante el arranque; reiniciar en la fase operativa para aplicar cambios. No se acepta cambiar la ruta de destino de un payload existente: queda bloqueado. No cambiar `SourceSystem`, IDs ni base origen para reutilizar un almacén de otra instalación. Un proceso atiende **una compañía origen y un destino**; para otras compañías, configurar servicios y directorios separados.

Las variables de entorno deben aprovisionarse para la identidad del servicio mediante el mecanismo protegido de operación; no guardar secretos en scripts/versionado ni confiar en el entorno de una sesión interactiva. Los payloads y la configuración de clientes contienen datos personales: ACL de servicio y administradores, respaldo protegido y política de conservación. La ACL no cifra el disco; valorar cifrado de volumen en la fase de infraestructura.

## Equivalencias fiscales pendientes

Las claves son literales de Firebird (se eliminan solo espacios de relleno a la derecha). No se convierten códigos a mayúsculas ni se adivinan equivalencias. Las claves compuestas usan `/`, ya que `:` tiene significado especial en configuración .NET.

| Opción de `FiscalMappings` | Contenido que debe confirmarse |
|---|---|
| `DocumentTypes` | Diccionario `FAC_TIPO → "F"`; cualquier otro tipo final se rechaza. |
| `AllowedHeaderStatuses`, `AllowedDetailStatuses` | Estados comerciales admitidos de FAC_STATUS y DFC_STATUS. No se filtran líneas silenciosamente. `ANU` está prohibido incluso si se configura por error: el bloqueo es incondicional (`SOURCE_INVOICE_CANCELLED`). |
| `PricesExcludeItbisConfirmed` | DFC_PRECIO antes de ITBIS para taxMode=0. No hay conversión automática de precios inclusivos. Sigue siendo obligatorio en ambos modos de DFC_ITBIS. |
| `LineItbisInterpretation`, `ItbisRatesByTaxCode`, `LineItbisIsMonetaryAmountConfirmed`, `ItbisRoundedPerLineConfirmed` | Estrategia explícita para DFC_ITBIS: `Unconfirmed` (predeterminado, bloquea), `MonetaryAmount` (conserva el comportamiento original) o `PercentageRate` (porcentaje confirmado). En `PercentageRate`, `ItbisRatesByTaxCode` debe contener, para cada `tax` usado, la tasa porcentual configurada (por ejemplo `"1": 18`); el servicio exige igualdad con DFC_ITBIS y calcula el impuesto monetario sobre `ROUND(cantidad*precio-descuento, 2)`. El booleano antiguo solo conserva compatibilidad cuando no se configura la estrategia; contradecir `PercentageRate` bloquea el arranque. Redondeo por línea a dos decimales, mitades alejándose de cero. |
| `TaxCodes` | Valor literal de DET_FACTURA.ITBIS → tax 0/1/2/3/4. No se infiere desde el importe. |
| `ItemClasses` | Clave `IDCOMPANIA/IDPRODUCTO` → 0 inventario o 1 servicio. |
| `Units` | MEDIDA → código o abreviatura del catálogo V6. GRAD es ambiguo: usar código numérico 16 o 17 confirmado. |
| `Customers` | Clave `IDCOMPANIA/IDCLIENTE` → objeto con `CustomerType` y `Ruc` reales. Incluso consumidor final debe estar identificado explícitamente como 07; no existe fallback global a consumidor final. |
| `NcfExpiryDates` | Clave `IDCOMPANIA/eNCF` → fecha ISO `yyyy-MM-dd`; alternativamente confirmar explícitamente `UseFacFechaVenceAsNcfExpiryConfirmed`. E32 admite ausencia. |
| `PaymentTypes` | FPA_TIPO → objeto `WebPosType` (01..08) y `Description` opcional (máximo 20). El JSON envía el **FPA_TIPO fuente**, no el código destino. Debe coincidir con la tabla SQL `PA_FB_PaymentTypeMap`. |
| `PaymentConsistencyRules` | FPA_TIPO → `ExpectedComponent` (`Efectivo`, `TarjetaMonto`, `Transferencia`, `ChequeMonto` o `Credito`), `ZeroComponents` (los demás componentes que deben ser NULL/0) y `PaidIncludesCredit`. Regla obligatoria por tipo presente. Valida FPA_MONTO contra el componente confirmado, detecta contradicciones (`PAYMENT_COMPONENT_CONFLICT`) y concilia FAC_PAGADO con la parte no crediticia. F6/EFE/TRA solo se habilitan cuando se configura su regla; no se convierten a 04/01/08 en el JSON. |
| `PaymentAmountsAreAppliedAmountsConfirmed` | FPA_MONTO es importe aplicado; pagos mixtos/crédito/cambio conciliados. No se recalculan usando los desgloses de pago. `SUM(FPA_MONTO)=FAC_TOTAL` se exige en origen antes de mapear. |
| `MonetaryLineDiscountConfirmed` | DFC_DESCUENTO es monto total por línea; descuentos no cero también requieren `WebPos:AllowMonetaryLineDiscount=true`. No se habilita automáticamente. |

Ejemplo estructural (valores ilustrativos, **no** equivalencias confirmadas):

```json
"PaymentConsistencyRules": {
  "TIPO": {
    "ExpectedComponent": "Credito",
    "ZeroComponents": [ "Efectivo", "TarjetaMonto", "Transferencia", "ChequeMonto" ],
    "PaidIncludesCredit": false
  }
}
```

F6, EFE y TRA permanecen sujetos a confirmación funcional; el dato real de la factura 294 (EFE con componente de crédito) se bloquea como `PAYMENT_COMPONENT_CONFLICT`, y la factura 218 (diferencia de pagos) se bloquea como `PAYMENT_TOTAL_MISMATCH`.
| `HeaderDiscountEqualsLineSumConfirmed` | Necesario si FAC_DESCUENTO es no cero; exige igualdad con la suma de descuentos de línea. |
| `NoTipsConfirmed` | Confirmación de que este flujo no contiene propinas; el snapshot conocido no expone un campo específico para detectarlas. |
| `Currency`, `CurrencyRate`, `OperMode` | Moneda, tasa y operación reales del origen. DOP requiere tasa 1. No aplicar una configuración uniforme a documentos de monedas diferentes. |
| `PaymentType` | Opcional, null permite la inferencia documentada de WebPOS; 1/2/3 únicamente cuando el negocio lo confirme. |

No se consultó ni inventó el esquema de CLIENTES. `IFiscalDataProvider` abstrae la identificación fiscal y el vencimiento; la implementación incluida (`ConfiguredFiscalDataProvider`) usa únicamente `Customers`, `NcfExpiryDates` y el indicador confirmado de `FAC_FECHAVENCE`. Una futura fuente Firebird autorizada puede sustituir ese registro sin cambiar el mapper, el JSON ni los contratos. No se crea un valor de consumidor final por defecto; si falta el dato, la factura queda bloqueada.

## Anulaciones y eventos de origen (V1.1)

`FAC_STATUS='ANU'` se comprueba antes de cualquier regla de pagos, descuentos o campos fiscales; no depende de `AllowedHeaderStatuses`. Las tres situaciones se tratan por separado:

| Situación | Código | Efecto |
|---|---|---|
| Anulada sin payload | `SOURCE_INVOICE_CANCELLED` | Sin JSON, sin SQL Server; exclusión terminal en `ULTIMO_ERROR`. No se tocan datos comerciales. |
| Anulada con payload preparado | `CANCELLED_AFTER_PREPARATION` | No se regenera ni reenvía el documento; se conserva el sobre y exige conciliación. Se comprueba antes de la huella, en la rama de reintento. |
| Sincronizada y luego anulada | `POST_SYNC_CANCELLATION` | Evento durable leído por el servicio; no se eliminan Header/Detail/Payment ni se genera nota de crédito o reverso. |

La migración `docs/11_firebird_outbox_safety_v4.sql` es un prerrequisito para habilitar el servicio: sin `PA_FB_EVENTOS`, la comprobación previa al ACK y la reconciliación no pueden operar. Tras aplicarla, el trigger local sobre `FACTURAS` registra `ANU_BEFORE_SYNC`, `ANU_DURING_PROCESSING` o `POST_SYNC_CANCELLATION` según el estado observado en la cola. `PA_ENCOLAR_FACTURA` ya no transforma R/S/E en P ni limpia errores o tokens: para filas existentes registra `SOURCE_REQUEUE_P/R/S/E`. El servicio consulta además eventos pendientes de la factura reclamada antes del ACK; si existe evidencia de cambio, bloquea con `SOURCE_CHANGED_BEFORE_ACK` y conserva el payload. Los eventos se marcan de forma condicional (`PROCESADO='S'`), sin alterar la cola. La migración no se ejecutó en esta fase.

Los importes usan `decimal`; no se redondea para hacer pasar un documento. La base por línea es `ROUND(cantidad * precio - descuento, 2)` y el ITBIS del JSON sigue siendo un importe monetario en `details[].itbis`. En modo `MonetaryAmount` se conserva el valor fuente y se valida contra la tasa; en `PercentageRate` se valida la tasa configurada y se calcula `ROUND(base * porcentaje / 100, 2)`. Ambos modos exigen reglas de redondeo confirmadas y precios sin ITBIS (TaxMode=0). Se rechaza precisión que el SP/contrato truncaría, devoluciones, propinas, impuestos no conciliados y líneas no facturables con base distinta de cero. Si los campos monetarios físicos de Firebird resultan ser FLOAT/DOUBLE, se bloquean con `FLOAT_SOURCE_REQUIRES_CONFIRMED_DECIMAL_CONVERSION`. FAC_PAGADO y los desgloses FPA ya se interpretan solo mediante `PaymentConsistencyRules`; sin regla confirmada el documento queda bloqueado con `PAYMENT_RULE_UNCONFIRMED`.

Los fallos de almacenamiento local no se tratan como transitorios por defecto: disco lleno, permisos, archivo bloqueado, ruta inválida o `IOException` no clasificada producen estados terminales (`STORAGE_FULL`, `ACCESS_DENIED`, `FILE_LOCKED`, `STORAGE_PATH`, `STORAGE_UNCLASSIFIED`) para evitar ciclos agresivos. No se elimina ni reescribe un payload para resolverlos. Los errores de red/timeout y la pérdida de conexión siguen siendo reintentables; los códigos SQL permanentes de esquema, permisos y capacidad siguen bloqueando.

## Reservas, recuperación e idempotencia

- El reclamo exige estado P, evento/contador observados y ventana de estabilización. Confirma una fila afectada y realiza COMMIT antes de leer o llamar a SQL Server.
- En R, `ULTIMO_ERROR` contiene `CLAIM:<GUID>` como token de propietario; `PROXIMO_INTENTO` contiene el vencimiento calculado con el reloj **Firebird**. La renovación ocurre cada tercio del lease. En P/E, ULTIMO_ERROR vuelve a contener un código de diagnóstico seguro.
- ACK, renovación, reintento y bloqueo comprueban R, token y lease vigente. Un trigger que restablezca P invalida al propietario anterior aunque FECHA_EVENTO tenga la misma precisión. No se agregan columnas ni se mantienen locks durante el envío SQL.
- Las reservas vencidas pasan a P en lotes acotados. Caídas de red no agotan un límite de intentos; INTENTOS se satura en `Int32.MaxValue`. Los triggers existentes pueden reiniciar ese contador.
- Antes del primer envío se guarda `JOMLIA_<compañía>_<factura>.payload`. Contiene el texto JSON original, ruta sin credenciales, checksum UTF-16LE compatible con el hash de SQL, huella del snapshot y equivalencias de pagos utilizadas. Otro checksum protege el sobre completo. El temporal se vacía a disco y se renombra con `MoveFileExW(WRITE_THROUGH)` en el mismo volumen, sin sobrescribir.
- Después del COMMIT SQL y antes del ACK Firebird, cualquier interrupción conserva el archivo. El siguiente intento reenvía su mismo texto, sin remapear ni serializar nuevamente. Un cambio del snapshot produce E y revisión manual, incluso si el resultado SQL anterior es incierto; nunca se envía un contenido nuevo para resolverlo.
- Se **conserva el archivo después de S**. Esto permite detectar alteraciones si un trigger vuelve a poner la factura en P. No hay purga automática; respaldar y conservar junto con la cola y SourceMap. La huella también incluye campos leídos que no llegan al JSON, por lo que cambios comerciales posteriores pueden exigir revisión aun sin alterar importes.
- Al arrancar se inspeccionan los archivos en lotes intercalados con el procesamiento y se recuperan las reservas; la cola es el único planificador. Archivos huérfanos, corruptos o estados E se reportan, no se borran ni se regeneran. Archivos `.tmp` de una caída previa al renombrado nunca fueron enviados; se ignoran.
- El directorio tiene un handle exclusivo de instancia y ACL para la identidad del proceso, SYSTEM y administradores. Se rechazan UNC, volúmenes sin NTFS y reparse points. Dos procesos que compitan por una fila obtienen como máximo un propietario válido; el SP protege adicionalmente los efectos. Una operación SQL ya iniciada puede terminar tras perder el lease: su ACK se rechaza y el payload queda recuperable.

La entrega es **al menos una vez con efectos idempotentes**, no exactly-once. Esta fase admite un procesador y un almacén local autoritativo. No ofrece HA activo-activo en varios equipos con almacenes independientes: trasladar un servicio requiere trasladar su almacén completo. No borrar payloads para desbloquear conflictos. La durabilidad frente a pérdida física de disco depende del respaldo y de que el almacenamiento respete flush/write-through.

Para revisar E: consultar el código/campo de ULTIMO_ERROR y el log correlacionado, verificar estado real SQL y el sobre original, corregir la equivalencia/dato autorizado, y reencolar mediante un procedimiento operativo supervisado. El servicio no cambia datos comerciales, elimina mapas ni reescribe archivos para resolver conflictos. No se incluyen comandos de producción para esa operación.

## Permisos y operación futura

Firebird: SELECT sobre las tres tablas comerciales y PA_FB_OUTBOX; UPDATE solo de PA_FB_OUTBOX, más acceso por el rol configurado. SQL Server: EXECUTE del SP y SELECT sobre PA_FB_SourceMap/PA_FB_PaymentTypeMap para las comprobaciones; permisos internos del SP resueltos por el DBA. No se requieren permisos DDL ni INSERT directo para el servicio.

La carpeta dedicada y los padres deben permitir su preparación por la cuenta del servicio. El host integra SCM sin interfaz. La instalación posterior deberá definir inicio automático y recuperación SCM, además de aprovisionar el origen EventLog si la cuenta no puede crearlo. El código no instala, registra ni cambia políticas SCM. No requiere sesión interactiva.

Logs JSON y Windows EventLog con origen `JomliaWebPosSync`. Los eventos por factura contienen origen, IDs, eNCF válido cuando se conoce, intento, estado, duración y tipo/campo de error. No se escriben payloads, clientes ni mensajes crudos de excepciones de proveedores. `PendingCount`, `ProcessingCount`, `SynchronizedCount`, `ErrorCount` reflejan la cola de la compañía (refresco cada minuto); `RetryCount` cuenta reintentos programados durante el proceso. No se crea dashboard.

## Contratos revisados y riesgos previos a producción

Se inspeccionaron README V3-QA, scripts 01/02/03/08, JSON 10 y ambos Excel solicitados; no falta ninguno de esos documentos. Los originales permanecen intactos.

1. El ejemplo `10_payload_ejemplo_QA.json` usa un eNCF de **12 caracteres**. El SP y el contrato indican 13. El servicio exige `E` + 12 dígitos, con prefijo E31/E32/E44/E45/E46; no rellena el ejemplo ni lo usa como fixture válido.
2. El Excel llamado V6 indica internamente V2.0. También limita POS a 3 caracteres y RUC a 11, mientras el SP admite 4 y 20. El servicio aplica los menores límites; confirmar versión contractual y casos de identificación extranjera antes de ampliar. Moneda/tasa y precisión de cantidades se restringen según V6.
3. El SP V3 comprueba hash/NCF en replay, pero no todos los campos de destino de SourceMap. El servicio fija la ruta en disco y contrasta el mapa existente antes de ejecutar el SP. Esa lectura no corrige otros escritores ni cambios concurrentes de configuración. Los mapas fiscales SQL y la ruta deben mantenerse estables durante operación.
4. El SP es QA sin validación real; requiere SQL Server 2016 SP1+/compatibilidad 130+. Confirmar esquema físico, permisos, versión Firebird (scripts 2.1+), compatibilidad del proveedor, charset y tipos numéricos. No se degrada SNAPSHOT a lecturas inconsistentes.
5. Confirmar consumidores WebPOS sin NOLOCK/READ UNCOMMITTED y triggers compatibles. Otros escritores fuera del SP pueden crear duplicados comerciales: el servicio no cambia sus índices ni políticas.
6. La cola original notificaba cambios de pagos a EMI, no cambios posteriores en FACTURAS/DET_FACTURA. La migración 11 añade eventos transaccionales para anulaciones y reencolados, pero todavía no es CDC general de cualquier cambio comercial. Un payload persistido detecta cambios en su huella al volver a procesarse. La ventana de estabilización reduce carreras; no sustituye la inmutabilidad comercial después de emitir.
7. Antes de producción quedan compilación y validación de escenarios de desconexión, respuesta perdida, corte de energía, competencia por reservas, disco lleno, ACL, datos fiscales reales anonimizados y actualizaciones de esquema/serializer. **Nada de esto se ejecutó en esta fase.**

Versiones estables fijadas, sin restaurarlas: [Hosting 10.0.15](https://www.nuget.org/packages/Microsoft.Extensions.Hosting/10.0.15), [WindowsServices 10.0.15](https://www.nuget.org/packages/Microsoft.Extensions.Hosting.WindowsServices/10.0.15), [FirebirdClient 10.3.4](https://www.nuget.org/packages/FirebirdSql.Data.FirebirdClient/10.3.4), [SqlClient 6.1.6](https://www.nuget.org/packages/Microsoft.Data.SqlClient/6.1.6). Comprobar compatibilidad efectiva y política de actualizaciones de seguridad en la fase de preparación.
