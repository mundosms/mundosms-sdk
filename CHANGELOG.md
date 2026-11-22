# Cambios

## v1.0.0 — 22/11/2026

Primera versión pública: los clientes de la API REST v3 en PHP, Python y Node.js, de un solo fichero y sin dependencias.

- `get`, `post`, `patch`, `delete` y `todos` (recorre las páginas con el cursor).
- Firma de cada petición con `X-Timestamp` en milisegundos.
- Reintentos solo de lo que es seguro repetir (`429`; en `GET`, `5xx` y fallos de red).
- Errores como `MundoSMSError`, con el código HTTP, el `status_code`, el mensaje y `detail`.
