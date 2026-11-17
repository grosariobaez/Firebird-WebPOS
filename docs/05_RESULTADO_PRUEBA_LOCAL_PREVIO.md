# Evidencia de prueba local (NO entorno productivo)

**Motor:** Firebird 4.0 instalado en el entorno de validación.
**Base utilizada:** una base temporal independiente con una tabla mínima
`FORMAS_PAGO(IDFACTURA, IDCOMPANIA, FPA_SECUENCIA, FPA_STATUS)`.

Se ejecutó `01_firebird_outbox.sql` mediante `isql-fb` sin errores.
Se realizaron estas operaciones y se confirmó la transacción:

```sql
INSERT INTO FORMAS_PAGO (IDCOMPANIA,IDFACTURA,FPA_SECUENCIA,FPA_STATUS)
VALUES (1,50,1,'PEN');
UPDATE FORMAS_PAGO SET FPA_STATUS='EMI' WHERE IDFACTURA=50;
INSERT INTO FORMAS_PAGO (IDCOMPANIA,IDFACTURA,FPA_SECUENCIA,FPA_STATUS)
VALUES (1,50,2,'EMI');
INSERT INTO FORMAS_PAGO (IDCOMPANIA,IDFACTURA,FPA_SECUENCIA,FPA_STATUS)
VALUES (1,51,1,'EMI');
COMMIT;
```

Consulta de la cola:

```text
IDCOMPANIA  IDFACTURA  ESTADO  INTENTOS
1           50         P       0
1           51         P       0
```

**Resultado:** PASS — los eventos INSERT y UPDATE generaron una sola entrada
por factura aun cuando la factura 50 tenía dos filas de pago EMI.

**No probado:** compatibilidad con la versión real de JOMLIA; tiempos de
respuesta bajo carga, llaves y otras reglas del esquema productivo;
integración con un SQL Server real; servicio integrador Windows.
