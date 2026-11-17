/* Consultas SOLO LECTURA; correr en webpos_rd / SSMS y remitir resultados. */
USE [webpos_rd];
GO
SELECT @@VERSION AS version_sql_server;
SELECT DB_NAME() AS bd, compatibility_level FROM sys.databases WHERE name=DB_NAME();

-- Duplicados comerciales existentes en Header (riesgo: solo PK(id)).
SELECT TOP (50) companyLicCod, branchCod, posCod, docType, docNumber,
       COUNT_BIG(*) AS registros
FROM dbo.Header
GROUP BY companyLicCod,branchCod,posCod,docType,docNumber
HAVING COUNT_BIG(*)>1
ORDER BY registros DESC;

-- Inventario de claves / indices de tablas existentes.
SELECT OBJECT_NAME(i.object_id) AS tabla, i.name AS indice,
       i.is_primary_key, i.is_unique, i.type_desc
FROM sys.indexes i
WHERE i.object_id IN (OBJECT_ID('dbo.Header'), OBJECT_ID('dbo.Detail'), OBJECT_ID('dbo.Payment'))
ORDER BY tabla,indice;

-- Triggers potencialmente activos sobre tablas destino.
SELECT OBJECT_NAME(parent_id) AS tabla, name AS trigger_nombre,
       is_disabled FROM sys.triggers
WHERE parent_id IN (OBJECT_ID('dbo.Header'), OBJECT_ID('dbo.Detail'), OBJECT_ID('dbo.Payment'));

-- Foreign keys que podrían condicionar inserts.
SELECT OBJECT_NAME(parent_object_id) AS tabla, name AS fk_nombre,
       OBJECT_NAME(referenced_object_id) AS referencia
FROM sys.foreign_keys
WHERE parent_object_id IN (OBJECT_ID('dbo.Header'),OBJECT_ID('dbo.Detail'),OBJECT_ID('dbo.Payment'));

-- Tipos de pago existentes, NO asume equivalencias con FPA_TIPO.
SELECT [type], COUNT_BIG(*) AS registros
FROM dbo.Payment
GROUP BY [type]
ORDER BY registros DESC;

-- Ejemplos de tax/itemClass/customerType/taxMode/operMode del sistema vigente.
SELECT TOP (25) customerType,taxMode,operMode,COUNT_BIG(*) AS ejemplos
FROM dbo.Header
GROUP BY customerType,taxMode,operMode ORDER BY ejemplos DESC;
SELECT TOP (25) tax,itemClass,COUNT_BIG(*) AS ejemplos
FROM dbo.Detail GROUP BY tax,itemClass ORDER BY ejemplos DESC;
GO
