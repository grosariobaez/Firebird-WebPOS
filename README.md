# JOMLIA → WebPOS Windows Service

Código fuente completo del Worker Service para **.NET 10 / Windows**, basado en el paquete V3-QA de `docs/`. La entrega permanece deshabilitada (`Sync:Enabled=false`) hasta completar la configuración y la fase posterior de validación.

**No se ejecutaron build, restore, test, publish, el servicio, scripts SQL, instalaciones, conexiones a servidores ni despliegues.**

## Archivos principales

- [Solución](JomliaWebPosSync.slnx) y [proyecto](JomliaWebPosSync/JomliaWebPosSync.csproj).
- `Program.cs` y `Worker.cs`: Host, DI, SCM, validación de opciones, un procesador, polling y cancelación.
- `Repositories/`: cola Firebird, snapshot y ejecución tipada del SP SQL Server.
- `Services/`: lectura, mapeo, validación, almacén durable y coordinación.
- `Serialization/`: JSON determinista y SHA-256.
- `Resilience/`: reservas renovables y reintentos exponenciales con jitter.
- `Security/` y `Diagnostics/`: conexiones sin secretos en código, ACL, escritura durable, logs estructurados y contadores.
- [Configuración sin secretos](JomliaWebPosSync/appsettings.example.json) y [guía detallada](JomliaWebPosSync/CONFIGURATION.md).

## Flujo implementado

`PA_FB_OUTBOX → claim condicional → snapshot consistente → validación fiscal/financiera → JSON → archivo durable → usp_ImportarFacturaFirebird → ACK condicional S`.

No se modifican las tablas comerciales Firebird ni se insertan registros directamente en WebPOS. El procedimiento controla su transacción y el orden Payment → Detail → Header. S confirma la importación, no la autorización DGII.

## Recuperación

Las reservas usan un token único en ULTIMO_ERROR durante R y vencimiento en PROXIMO_INTENTO, calculado por Firebird. Solo el propietario vigente puede renovarlas o confirmar S. Los triggers que restablecen P invalidan al propietario anterior.

El archivo inmutable se escribe antes de SQL, con checksum, flush a disco, renombrado atómico y ACL. Se conserva después de S para detectar cambios posteriores. Los reintentos reutilizan exactamente el mismo JSON; conflictos y corrupción requieren revisión, sin regenerar contenido. Las reservas vencidas se recuperan y los fallos transitorios no agotan un límite de intentos.

Se implementa entrega **al menos una vez con efectos idempotentes**. Una instancia por almacén local NTFS; no se ofrece HA activo-activo con almacenes independientes.

## Configuración pendiente

Completar servidor SQL, credenciales protegidas, charset Firebird, compañía origen, licencia/sucursal/POS y carpeta durable. Variables de entorno: prefijo `JOMLIA_SYNC_`, separador `__`; por ejemplo `JOMLIA_SYNC_Firebird__Password`. También se admite `appsettings.Local.json`, excluido del versionado y de la copia automática al publicar.

Las equivalencias fiscales permanecen vacías o deshabilitadas: tipo/estados documentales, precios sin ITBIS, descuentos, impuestos, clasificación de artículos, unidades, identificación real del cliente, vencimiento eNCF y FPA_TIPO. La [guía de configuración](JomliaWebPosSync/CONFIGURATION.md) define cada clave, reglas de precisión, permisos y tratamiento de errores. No se inventa el esquema de CLIENTES ni datos fiscales.

## Atención antes de producción

- El JSON de ejemplo contiene un eNCF de 12 caracteres; el servicio aplica los 13 exigidos por el SP.
- El paquete presenta discrepancias de versión y longitudes entre Excel y SP; se aplican límites conservadores y se documentan en la guía.
- El SP V3 sigue siendo QA. El servicio comprueba SourceMap y la ruta persistida, pero otros escritores y cambios concurrentes de mapas necesitan control operativo.
- Confirmar motor/proveedor, esquema físico, charset, tipos monetarios y consumidores WebPOS sin lecturas sucias. FLOAT/DOUBLE monetarios se bloquean hasta confirmar una conversión decimal.
- Preservar el almacén y sus respaldos. Cambios comerciales que no reencolen la factura no se detectan mediante esta cola.
- La instalación futura deberá configurar inicio automático y recuperación SCM, identidad, EventLog y permisos. Compilación y validación con clones quedan expresamente pendientes.
