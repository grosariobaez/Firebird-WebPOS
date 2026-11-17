/* Diagnostico Firebird: ejecutar SELECTs en copia o produccion de solo lectura.
   Las consultas estan separadas para poder explicar cada discrepancia. */

-- 1) Version real del motor (no se deduce de .GDB)
SELECT RDB$GET_CONTEXT('SYSTEM', 'ENGINE_VERSION') AS MOTOR
FROM RDB$DATABASE;

-- 2) Tipos/status de pago utilizados (para armar mapeo SIN ADIVINAR)
SELECT FPA_TIPO, FPA_STATUS, COUNT(*) AS CANTIDAD
FROM FORMAS_PAGO
GROUP BY FPA_TIPO, FPA_STATUS
ORDER BY FPA_TIPO, FPA_STATUS;

-- 3) Ultimas 20 facturas con al menos una forma de pago EMI
SELECT FIRST 20
       F.IDCOMPANIA, F.IDFACTURA, F.FAC_FECHA,
       F.FAC_NCF, F.FAC_STATUS, F.FAC_TOTAL, F.FAC_ITBIS,
       F.FAC_DESCUENTO, F.FAC_PAGADO
FROM FACTURAS F
WHERE EXISTS (
    SELECT 1 FROM FORMAS_PAGO P
    WHERE P.IDCOMPANIA = F.IDCOMPANIA
      AND P.IDFACTURA = F.IDFACTURA
      AND P.FPA_STATUS = 'EMI'
)
ORDER BY F.FAC_FECHA DESC;

-- 4) Descuentos por linea, sin asumir si son monto o porcentaje
SELECT FIRST 30
       D.IDCOMPANIA, D.IDFACTURA, D.SECUENCIA,
       D.DFC_CANTIDAD, D.DFC_PRECIO, D.DFC_DESCUENTO,
       D.DFC_ITBIS, D.DFC_CANTDEVUELTA, D.DFC_STATUS
FROM DET_FACTURA D
WHERE COALESCE(D.DFC_DESCUENTO, 0) <> 0
ORDER BY D.IDFACTURA DESC, D.SECUENCIA;

-- 5) Formas de pago mixtas: eventos EMI multiples por factura
SELECT FIRST 30 IDCOMPANIA, IDFACTURA, COUNT(*) AS FILAS_EMI
FROM FORMAS_PAGO
WHERE FPA_STATUS = 'EMI'
GROUP BY IDCOMPANIA, IDFACTURA
HAVING COUNT(*) > 1
ORDER BY COUNT(*) DESC;

-- 6) Reconciliacion: facturas con EMI que faltan en la cola.
--    SOLO despues de instalar la cola. Su resultado debe ser cero.
SELECT FIRST 100
       P.IDCOMPANIA, P.IDFACTURA
FROM FORMAS_PAGO P
WHERE P.FPA_STATUS = 'EMI'
  AND NOT EXISTS (
    SELECT 1 FROM PA_FB_OUTBOX Q
    WHERE Q.IDCOMPANIA = P.IDCOMPANIA
      AND Q.IDFACTURA = P.IDFACTURA
  )
GROUP BY P.IDCOMPANIA, P.IDFACTURA
ORDER BY P.IDCOMPANIA, P.IDFACTURA;

/* Recuperacion historica opcional (NO ejecutar sin acotar fecha/rango):
   Registrar facturas ya emitidas antes de la instalacion del trigger.
   Revisar primero su impacto y volumen con consulta 6.

INSERT INTO PA_FB_OUTBOX (IDCOMPANIA, IDFACTURA, ESTADO, INTENTOS, FECHA_EVENTO)
SELECT DISTINCT P.IDCOMPANIA, P.IDFACTURA, 'P', 0, CURRENT_TIMESTAMP
FROM FORMAS_PAGO P
WHERE P.FPA_STATUS = 'EMI'
  AND NOT EXISTS (
    SELECT 1 FROM PA_FB_OUTBOX Q
    WHERE Q.IDCOMPANIA = P.IDCOMPANIA
      AND Q.IDFACTURA = P.IDFACTURA
  );
*/
