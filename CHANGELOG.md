# Cambios

## v1.0.0

Primera versión: los clientes de la [API REST v3 de MundoSMS](https://www.mundosms.es/api-v3) en **PHP, Python, Node.js
(con sus tipos de TypeScript), Java y C#**. Cada uno es un solo fichero, sin dependencias, y todos tienen los mismos nombres,
cada uno con el estilo de su lenguaje.

- **Peticiones:** `get`, `post`, `patch`, `delete` y `request` (una cualquiera). Devuelven la respuesta entera; los
  ficheros (grabaciones, locuciones, certificados) vuelven con `content_type` y `body`.
- **Listados enteros:** `all` recorre las páginas siguiendo `next_cursor`, con los mismos filtros en cada una, y deja el
  último cursor en `lastCursor` (`last_cursor` en Python, `getLastCursor()` en Java, `LastCursor` en C#) para la siguiente
  sincronización.
- **Firma:** cada petición va firmada con `X-Timestamp` en milisegundos y el HMAC de tu API Key, sobre justo lo que se
  manda: la ruta y la query codificadas en RFC 3986.
- **`Idempotency-Key`** en cada `POST`: un UUID nuevo por llamada, el mismo en todos sus intentos, o la tuya (por ejemplo,
  el id de tu pedido). Si la API devuelve la respuesta guardada de la primera vez, trae `"idempotent_replayed": true`.
- **Reintentos** solo de lo que es seguro repetir: un `429` y un fallo de red en cualquier método, un `5xx` en `GET`, y en
  `POST` un `409 Request in progress`. Esperan lo que diga `Retry-After` (como mucho 60 s) o 1, 2, 4… s.
- **Plazo** de la petición entera (`timeout`, 60 s por defecto).
- **Errores:** `MundoSMSError` (en C#, `MundoSMSException`) con el código HTTP, el `status_code`, el mensaje, `detail`,
  `data`, la respuesta entera (`response`) y el `X-Request-Id`. Un WhatsApp sin confirmar (`202`, `data.state`
  `"unknown"`) no es un error: vuelve la respuesta.
- **Avisos:** `verifyWebhook` (`verify_webhook` en Python, `VerifyWebhook` en C#) comprueba la firma
  `X-MundoSMS-Signature` de los avisos que te mandamos (los de `callback_url` y los webhooks de WhatsApp) sobre el cuerpo
  tal cual llega, en tiempo constante y con 5 minutos de tolerancia.
- **Opciones:** `baseUrl`, `retries` y `timeout`. Una opción que no existe da error en vez de ignorarse.
- El token y la clave privada no salen al imprimir el cliente (`var_dump`, `repr`, `console.log`).
