# Instalación y puesta en marcha — IXU JomliaWebPosSync

**IXU.** Inteligencia · Ejecución · Utilidad.  
Tecnología para operaciones del mundo real.  
INNOVAXD UX SRL — https://innovaxd.ca/es/

## Artefacto

- Instalador: `installer/output/JomliaWebPosSync-Setup.exe`
- Instalación: `C:\Program Files\JomliaWebPosSync\`
- Datos persistentes: `%ProgramData%\PuntoAparte\JomliaWebPosSync\`
- Servicio Windows: `JomliaWebPosSync`
- Configurador: IXU Configurador (`JomliaWebPosSync.Configurator.exe`)

El instalador incluye el servicio, el configurador, la configuración inicial y documentación. No incluye pruebas, scripts QA, bases de datos ni credenciales.

## Requisitos

- Windows 10/11 o Windows Server x64.
- Cuenta con privilegios de administrador para instalar y configurar.
- Acceso de red a Firebird/JOMLIA y SQL Server/WebPOS.
- Objetos SQL Server `dbo.usp_ImportarFacturaFirebird`, `dbo.PA_FB_SourceMap` y `dbo.PA_FB_PaymentTypeMap` creados por el DBA.
- Permisos Firebird de solo lectura sobre `FACTURAS`, `DET_FACTURA` y `FORMAS_PAGO`.

## Instalación

1. Ejecutar `JomliaWebPosSync-Setup.exe` como administrador.
2. Aceptar la ruta `C:\Program Files\JomliaWebPosSync\`.
3. Finalizar y permitir que se abra el configurador IXU.
4. Completar Firebird, SQL Server, datos WebPOS y corte inicial.
5. Presionar **Probar Conexiones**. La prueba es de solo lectura y verifica objetos/permisos.
6. Corregir cualquier error. Cualquier cambio invalida la prueba anterior.
7. Guardar la configuración.
8. Activar la sincronización únicamente cuando la configuración y las conexiones sean válidas.

La sincronización queda inactiva tras la instalación (`Sync:Enabled=false`). El servicio se instala con inicio automático y recuperación SCM, pero no procesa facturas hasta la activación explícita.

## Configuración

El configurador escribe `C:\Program Files\JomliaWebPosSync\appsettings.json` y conserva una copia `appsettings.json.bak` antes de reemplazar. No guarda contraseñas en ese archivo.

Las contraseñas se cifran con DPAPI `LocalMachine` en:

`%ProgramData%\PuntoAparte\JomliaWebPosSync\secrets.json`

El archivo y su carpeta quedan restringidos a `SYSTEM` y `Administradores`. El servicio se ejecuta como `LocalSystem` y lee los secretos al iniciar.

### TLS SQL Server

Valores predeterminados:

- `Encrypt=true`
- `TrustServerCertificate=false`

`TrustServerCertificate=true` puede habilitarse solo mediante selección explícita del administrador y muestra advertencia de seguridad. No se activa silenciosamente.

### Corte inicial

Campos obligatorios:

- `SourceCompanyId`
- `InvoiceStart`
- `InitialInvoiceIdByCompany[SourceCompanyId]` — **IDFACTURA mínimo a sincronizar (inclusive)** (`IDFACTURA >= valor`)

No describir este valor como "última factura procesada". No utilizar valores de QA como predeterminados de producción.

## Operación

- **Activar Sincronización**: guarda `Sync:Enabled=true`, valida y reinicia el servicio.
- **Desactivar Sincronización**: guarda `Sync:Enabled=false` y reinicia el servicio.
- **Iniciar/Detener Servicio**: controla el servicio sin cambiar la configuración.
- **Recargar / Ver Estado**: recarga la configuración y muestra estado del servicio y sincronización.

Logs JSON y Windows EventLog usan el origen `JomliaWebPosSync`. No se escriben payloads, clientes ni credenciales en los logs.

## Actualización y desinstalación

- La actualización conserva `appsettings.json`, secretos DPAPI (`secrets.json`) y payloads en `%ProgramData%\PuntoAparte\JomliaWebPosSync\`.
- La desinstalación no elimina datos persistentes de configuración, secretos ni payloads.

## Rollback

1. Desactivar la sincronización.
2. Detener el servicio.
3. Restaurar `appsettings.json.bak` si corresponde.
4. Restaurar `secrets.json` desde respaldo autorizado si corresponde.
5. Reinstalar la versión anterior.
6. No borrar `%ProgramData%\PuntoAparte\JomliaWebPosSync\` durante un rollback.

## Límites de esta entrega

- No se ejecutó una instalación aislada end-to-end ni una conexión real a producción.
- No se activó DGII ni se enviaron documentos fiscales.
- No se modificaron `ResultFe`, Delphi 5, tablas comerciales Firebird ni el SP de WebPOS.
