# JOMLIA (Firebird) → WebPOS / SQL Server | Integración V3-QA

**Estado: BORRADOR TÉCNICO, NO APTO PARA PRODUCCIÓN.**

Documento de referencia proporcionado: `WPFeDo DBConn - Especificación de campos V6.xlsx`, pestañas `Facturas, NC,ND` y `Unidades de Medida`. **La celda G8 del Excel indica internamente `Versión: V2.0`**, aunque el nombre del archivo dice V6; conviene confirmar con WebPOS cuál es la versión contractual vigente.

## Decisiones confirmadas por la especificación

1. Tablas destino: `dbo.Payment`, `dbo.Detail`, `dbo.Header` (en ese orden exacto). Insertar `Header` AL FINAL con `docToPrint=1` y confirmar los tres grupos dentro de una sola transacción.
2. `docToPrint`: `1` listo para DGII; `2` autorizado; `3` procesando; `4` rechazado. **No actualizar** 2/3/4 desde el integrador: los gestiona WebPOS.
3. `taxMode=0` significa **precio antes de ITBIS** (Tax Exclusive). Nota explícita en hoja: “Vamos a Trabajar con 0, Modelo que utiliza RD”. El valor anterior `1` NO corresponde al flujo propuesto sin verificar los precios de JOMLIA.
4. `Detail.tax`: 0 exento, 1 ITBIS 18%, 2 ITBIS 16%, 3 0% solo E46, 4 no facturable (también precio 0 / 100% de descuento).
5. `Detail.itemClass`: 0 inventario, 1 servicio. No asignar 1 universalmente.
6. `Payment.type`: 01 efectivo, 02 cheque, 03 tarjeta, 04 venta crédito, 05 certificado regalo (solo E32), 06 permuta, 07 nota crédito, 08 otras formas.
7. `CustomerType`: 03 gobierno, 04 RNC, 05 cédula, 06 pasaporte/TAX/VAT ID, 07 consumidor final. La columna SQL Server es NOT NULL incluso cuando la especificación dice “opcional” para consumidor final; el SP usa 07 para E32 sin código.
8. `docNumberDueDate`: fecha del **vencimiento de eNCF** obligatoria salvo E32/E34. `FACTURAS.FAC_FECHAVENCE` **NO se interpreta** como esa fecha sin prueba.
9. `Payment.desc1`: V6 documenta 20 caracteres, mientras DDL SQL Server permite 30; la V3 valida el menor límite (20).
10. `Detail.dperc`: hoja lo describe con texto incluyendo `%`, pero DDL de destino es `decimal(8,4)`. Se mantiene NO habilitado hasta aclarar esta discrepancia; se usa `damt` solo tras confirmar monto por línea.
11. La hoja `Unidades de Medida` permite código o abreviatura; contiene `UND=43`. La abreviatura `GRAD` aparece dos veces (códigos 16 y 17), revisar si llega a utilizarse.
12. El V3 solo habilita facturas `docType=F` con eNCF E31/E32/E44/E45/E46; C/D/P/E se bloquean deliberadamente por requerir reglas adicionales (reversos, referencias, retenciones). No es una afirmación de que esos otros tipos no existan en WebPOS.

## Archivos

- `Matriz_mapeo_WebPOS_V6_JOMLIA.xlsx`: 65 campos V6, origen propuesto, estatus, diccionarios, unidades de medida y 14 gates.
- `02_sqlserver_import_v3_QA.sql`: SP y tablas auxiliares para SQL Server; *SOLO QA*. **No probado contra SQL Server real.**
- `03_consultas_fuente.sql`: snapshot Firebird de una factura.
- `04_diagnostico_y_reconciliacion.sql`: diagnósticos Firebird de V2.
- `06_preflight_sqlserver.sql`, `07_preflight_firebird.sql`: verificaciones de solo lectura originales.
- `08_preflight_mapeo_fiscal.sql`: muestras de tipos de pago, ITBIS, conciliación de importes y metadatos CLIENTES (solo lectura).
- `09_validacion_mapeo_destino.sql`: consultas de QA SQL Server (solo lectura); sustituir parámetros de prueba.
- `10_payload_ejemplo_QA.json`: *sintético*, no ejecutable sin completar el mapeo de tipos FPA reales.

## Contrato del procedimiento dbo.usp_ImportarFacturaFirebird

Parámetros externos: `@SourceSystem`, `@CompanyLicCod`, `@BranchCod`, `@PosCod`, `@Payload`, `@DocNumber OUTPUT`, `@AllowMonetaryLineDiscount=0`.

`@SourceSystem` sugerido: `JOMLIA`, una constante por sistema origen. Los códigos de licencia/branch/pos deben ser confirmados por WebPOS para JOMLIA; NO reciclar los de EasyPOS por asumir equivalencia.

JSON contiene `idCompania`, `idFactura`, `header`, `details[]`, `payments[]`. Cabecera: `docType`, `ncf`, `fecha`, `ncfVencimiento` (requerido salvo E32), `nombre`, `ruc`, `direccion`, `customerType`, `taxMode=0`, `operMode`, `currency`, `currencyRate`, `total`, `itbis`, opcional `paymentType`, `tip`, `subtotalExento`, `subtotalGravado`, `subtotalNet`, `devuelto`. Por detalle: `secuencia`, `idProducto`, `referencia`, `descripcion`, `cantidad`, `precio`, `descuento`, `itbis`, `cantidadDevuelta`, `tax`, `itemClass`, `um`. Pagos: `secuencia`, `tipo` **de Firebird**, `monto`, `estatus='EMI'`, opcional `descripcion`.

**Control de descuento:** `@AllowMonetaryLineDiscount` debe permanecer en `0` hasta confirmar si `DFC_DESCUENTO` es monto total (no porcentaje ni monto unitario). **Control de totales:** con `taxMode=0`, exige `SUM(ROUND(qty*price-damt, 2)) + FAC_ITBIS == FAC_TOTAL`; rechaza si no cuadra. **Pagos:** exige todos EMI, código FPA mapeado 01..08, suma igual al total (cambio/crédito aún pendientes).

`dbo.PA_FB_PaymentTypeMap` queda VACÍA hasta obtener datos fuente: insertar mapeos después de inspeccionar `FPA_TIPO`. Si existe en V2 no se borra.

### Semántica de idempotencia

La tabla auxiliar `dbo.PA_FB_SourceMap` usa una PK del origen (`SourceSystem`,`IDCOMPANIA`,`IDFACTURA`) y UNIQUE por identidad comercial de destino. El SP utiliza `sp_getapplock` para serializar dos envíos concurrentes **dentro de este procedimiento**. Si ya se importó el mismo JSON, devuelve éxito sin reinserción; si cambió, arroja conflicto. Reintentos deben preservar el JSON byte-por-byte (formato canónico). La PK del negocio `Header` sigue siendo `id`, así que **otros procesos ajenos al SP** pueden insertar duplicados de documento comercial. No crear índices únicos en las tablas de negocio sin revisar otros escritores y duplicados históricos.

## Cuidado con la visibilidad de transacción

El INSERT de Header se hace al final y solo después de insertar pagos y detalles; el COMMIT confirma el conjunto. Este diseño requiere confirmar que los consumidores WebPOS no usan lecturas sucias `NOLOCK`/`READ UNCOMMITTED` y que sus triggers son compatibles con el orden de inserción. `docToPrint=1` es una orden de procesamiento, no una prueba de aceptación de DGII.

## Qué falta para producción

- Confirmar versión del motor Firebird y nivel de compatibilidad SQL Server 130+; permisos y DDL real del entorno.
- Confirmar códigos `CompanyLicCod`/`BranchCod`/`POSCod`, vencimiento fiscal del NCF, identificación del cliente.
- Validar `DFC_PRECIO` sin impuestos, `DFC_DESCUENTO` monetario por línea, mapeos `tax`/`itemClass`, unidades de medida y `FPA_TIPO`.
- Pruebas con documentos anonimizados E31, E32, pagos mixtos, crédito, descuento, precios cero, reconexión, carrera de procesadores y reversión.
- Validar el servicio Windows con corte inicial, emisión completa, reintentos desde payloads y SourceMap. El código existe; compilación y pruebas están pendientes.
- Plan de rollback y ejecución gradual sobre clon. **Nunca ejecutar estos scripts directamente en producción en el estado actual.**
