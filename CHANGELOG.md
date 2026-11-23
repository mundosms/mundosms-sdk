# Cambios

## v1.1.0 — 23/11/2026

- `todos()` sigue solo `next_cursor`: la API ya no devuelve `next_from_id` ni acepta `from_id`. Para empezar desde un
  punto, pasa `cursor` (el `next_cursor` que guardaste).

## v1.0.1 — 23/11/2026

- `todos()` con `from_id` fallaba en la segunda página (`400 Parameter cursor not valid`): seguía mandando `from_id` junto
  al cursor. Ahora, al pasar al cursor, lo quita.
- En `get` y `delete`, una lista vacía (`[]`) ya no se manda como `campo=`: es como no mandar el campo, igual que `null`.
- La versión que va en el `User-Agent` es la de la etiqueta (antes decía 3.0.0).

## v1.0.0 — 22/11/2026

Primera versión pública: los clientes de la API REST v3 en PHP, Python y Node.js, de un solo fichero y sin dependencias.

- `get`, `post`, `patch`, `delete` y `todos` (recorre las páginas con el cursor).
- Firma de cada petición con `X-Timestamp` en milisegundos.
- Reintentos solo de lo que es seguro repetir (`429`; en `GET`, `5xx` y fallos de red).
- Errores como `MundoSMSError`, con el código HTTP, el `status_code`, el mensaje y `detail`.
