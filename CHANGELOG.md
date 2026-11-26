# Cambios

## v2.1.1

- **PHP: funciona desde PHP 7.0.** Hasta la 2.1.0 pedía PHP 7.4 y, por debajo de 7.3, fallaba en cuanto un `POST`
  seguía en marcha (`hrtime()`, el reloj que mide esa espera, no existe antes de 7.3). Ahora usa `hrtime()` si existe y,
  si no, `microtime()`; y los 180 segundos de esa espera cuentan también lo dormido, así que no se alargan aunque la
  hora del servidor retroceda. El resto de lenguajes no cambia.

## v2.1.0

Solo se añaden cosas: tu código de la 2.0 sigue funcionando igual.

- **El error lleva la `Idempotency-Key` que usó la petición:** `$e->idempotencyKey` en PHP, `e.idempotency_key` en Python
  y Ruby, `e.idempotencyKey` en Node.js, `e.getIdempotencyKey()` en Java y `e.IdempotencyKey` en C# y Go. Es la tuya o la
  que puso el SDK en un `POST`; en un `GET`, un `PATCH` o un `DELETE` sin clave, `null` (`None` en Python, `nil` en Ruby y
  `""` en Go). Si un `POST` falla sin saber si se hizo, repítelo con esa clave: la API no lo hace dos veces.
- **Un `POST` que sigue en marcha se espera más.** Un `409` con `code` `request_in_progress` (la primera petición con esa
  clave aún se está haciendo; un WhatsApp con un fichero puede tardar 2 o 3 minutos) se repite con la misma clave hasta
  **180 segundos** desde el primero, sin contar en `retries`, y te llega la respuesta de la primera en cuanto termina.
  Espera lo que diga `Retry-After` (al menos 1 segundo) o, si no lo trae, 1, 2, 4, 8 y luego 15 segundos. Si `Retry-After`
  pide más de lo que queda, o se acaban los 180 segundos, es un error, con la clave. Con `retries` a `0`, no se repite.
  Hasta la 2.0.0 se rendía a los tres intentos (unos 7 segundos), sin decirte la clave.
- `GET /whatsapp/messages` con `all()`: la API ya no aplica `from_datetime` desde la segunda página en orden `asc` (solo
  elige dónde empieza), así que un mensaje que llega tarde con una hora anterior (un historial importado) ya no se queda
  fuera. No cambia nada en el SDK: `all()` sigue mandando los mismos filtros en cada página.

## v2.0.0

**Cambia el error: si tu código lee `status_code`, cámbialo al actualizar.** La API REST deja de mandar `status_code`, en
ninguna respuesta (ni buena ni de error), y cada error trae **`code`**: una palabra en inglés y en snake_case que dice qué
falló (`missing_parameter`, `invalid_parameter`, `token_invalid`, `signature_invalid`, `permission_denied`, `not_found`,
`insufficient_credit`, `whatsapp_window_closed`…; la lista, en el
[Catálogo de errores](https://www.mundosms.es/api-v3#catalogo-de-errores)). Los siete clientes siguen a la API:

- **`code` en vez de `status_code`:** `$e->code` en PHP (también `$e->getCode()`), `e.code` en Python, Node.js y Ruby,
  `e.getCode()` en Java, y `e.Code` en C# y Go. Desaparecen `statusCode`, `status_code`, `getStatusCode()` y `StatusCode`,
  sin alias. Cuando la respuesta no trae `code` (sin respuesta, una redirección o una respuesta que no es JSON), vale `null`
  (`None` en Python, `nil` en Ruby y `""` en Go).
- **Una respuesta buena no lleva `code` ni `status_code`,** tampoco la que da el propio SDK: el `DELETE` que se repite tras
  quedarse sin respuesta y recibe un `404` vuelve como `{"status": "success", "message": "Already deleted", "data":
  {"state": "deleted", "changed": false}}`, sin el `"status_code": "0"` de antes.
- **Los reintentos se deciden por `code` y por el código HTTP,** nunca por el texto del mensaje: un `409` con
  `duplicated_request` se repite en cualquier método, y uno con `request_in_progress`, en un `POST` con su
  `Idempotency-Key`. Un `404` con `route_not_found` sigue siendo un error también tras un `DELETE` sin respuesta. Se repite
  lo mismo que en la 1.2.
- **Go: la ruta del módulo termina en `/v2`,** como pide Go a partir de la versión 2:
  `github.com/mundosms/mundosms-sdk/go/v2`, en el `go get` y en el `import`. La etiqueta del módulo es `go/v2.0.0`.
- El error en una línea dice `code not_found` donde decía `status_code 4`: `toString()` en Java y `Error()` en Go
  (`… (HTTP 404, code not_found)`).
- **Node.js (TypeScript):** `MundoSMSResponse` ya no tiene `status_code`, y `ErrorResponse` trae `code`.
- **Campos de la API con otro nombre.** La respuesta llega entera, así que los verás en `data` y en `response`:
  - WhatsApp: el `error_code` de los mensajes y de los destinatarios es ahora `whatsapp_error_code` (un entero o `null`), y
    los destinatarios traen además `failure_code`;
  - SMS, certificados y NumCheck: `error_code` es ahora `carrier_error_code` (un texto o `null`);
  - llamadas: `state_code` (un número) es ahora `state_detail`, una palabra o `null` (`completed` o `interrupted` con
    `answered`; `closing` o `hanging_up` con `ending`);
  - lo que un error traía en `data.code` (`forwarding`, `has_cost`, `requests_per_second`…) va ahora en `code`, y no en
    `data`.

  `message`, `detail`, `data`, los códigos HTTP y las cabeceras no cambian, salvo una cosa: en WhatsApp, sin tarifa para ese
  tipo de mensaje o con el cobro rechazado, la respuesta es `422` con `code` `no_tariff` o `charge_failed` (antes `500`
  `Internal error`). No salió, y repetirlo no cambia nada: los SDK no lo reintentan.

**Al actualizar:**

1. Cambia `statusCode`, `status_code`, `getStatusCode()` o `StatusCode` por `code` (`getCode()` en Java, `Code` en C# y
   Go).
2. Si comparabas con un número (`"3"`, `"4"`…) o con el texto del mensaje, compara con la palabra de `code`.
3. Si leías `data.code` de un error (por ejemplo, el `requests_per_second` de un `429`), léelo en `code`.
4. Si lees `error_code` o `state_code` en `data`, usa los nombres nuevos de la lista de arriba (`state_code` pasa a
   `state_detail`, que es una palabra).
5. En Go, cambia la ruta del módulo en el `go get` y en el `import`.

## v1.2.1

Solo cambian la documentación y los comentarios del código: los siete clientes hacen lo mismo que en la 1.2.0.

- **Un `500` con `Idempotency-Key`:** decían que la API no guarda los `5xx`. Guarda el `500`, porque puede llegar con la
  operación hecha: el mismo `POST` con la misma clave te devuelve ese `500` (con `"idempotent_replayed": true` en la
  `response` del error) y no lo vuelve a hacer. Compruébalo antes de repetirlo y, si no se hizo, repítelo con otra clave.
- **Un `503` no hizo nada** ni se guarda: el SDK no lo repite solo en un `POST`, pero tú puedes repetirlo con la misma clave.
- **`409 Extension already exists`** al repetir `POST /v3/extensions`: el nombre puede ser de otra cuenta. El README dice
  cómo saberlo con `GET /v3/extensions/{extension}`.

## v1.2.0

**Arreglo de seguridad: actualiza.** El cliente de **Python** (también la 1.0.0 de PyPI) seguía las redirecciones: si la
dirección de la API contestaba con un `3xx`, la petición se repetía en el servidor al que mandaba, con tu token
(`Authorization`), la firma y el `X-Timestamp`, también de `https` a `http`, y su respuesta se tomaba por la de la API. El de
**Node.js** (también la 1.0.0 de npm) las seguía igual, sin el token pero con la firma, la `Idempotency-Key` y, con un `307`,
el cuerpo del `POST` (los teléfonos y el texto), y el de **C#**, lo mismo con un `HttpClient` tuyo. La API no redirige nunca,
así que solo pasaba con una dirección o un intermediario que sí lo hiciera; aun así, actualiza a la 1.2.0.

- **Las redirecciones no se siguen nunca, en los siete:** un `3xx` es un error con su `http` y adónde mandaba
  (`HTTP 302: redirect to … not followed`), sin tomar nada de otro servidor por la respuesta de la API. En C#, un
  `HttpClient` tuyo que sigue redirecciones (el de `new HttpClient()`, o uno de `IHttpClientFactory` sin configurar) no se
  acepta: el constructor da `ArgumentException`. Créalo con `AllowAutoRedirect = false` en su manejador (el README dice
  cómo con `IHttpClientFactory`).
- **Un `2xx` que no es JSON es un error,** nunca un éxito (el HTML de un proxy, de un portal cautivo o de un cortafuegos):
  `HTTP 200: the response is not JSON (text/html)`. Antes volvía como un «fichero» con `"status": "success"`, también en un
  `POST /sms`. Los ficheros de la API (audio, imagen, PDF) siguen igual, por su `Content-Type`, y un `204`, vacío.
- **`all()` de una lista que no va por páginas** (`GET /calls/missed` y `GET /calls/live`, que con `has_more: true` no
  traen `next_cursor`) es un error claro, antes de ningún elemento, en vez de quedarse en la primera página sin avisar.
- **Cuándo termina `all()`, igual en los siete:** con `has_more: false` (con `stop_at_ongoing`, también cuando la página se
  para en una llamada en curso: `data.ongoing_call`) o si el cursor no avanza. Una página **vacía con `has_more: true`** (con
  `stop_at_ongoing` y un filtro, cuando la API ha mirado muchas llamadas que no casan) ya no es el final: sigue. Antes
  terminaba ahí y un informe o una primera descarga salían a medias sin avisar.
- **`lastCursor` avanza cuando ya te ha dado todos los elementos de una página,** en los siete: si tu código falla a mitad de
  una, sigue en la anterior. En Java avanzaba al pedir la página.
- **Un `+` escrito en la query de la ruta llega como `+`:** `get("/calls?from_datetime=2026-11-20T10:15:03+01:00")`
  mandaba un espacio (se leía como un formulario) y la fecha llegaba rota. Un `%XX` se sigue decodificando.
- **Un `DELETE` que se hizo y se quedó sin respuesta** se repite, y el `404` de la repetición ya no es un error: vuelve
  como éxito, con `"message": "Already deleted"` y `data.changed` `false`.
- **`lastCursor`** (`last_cursor`, `getLastCursor()`, `LastCursor`) es el del recorrido: al empezarlo, el `cursor` que le
  pasas, y después el último `next_cursor`. Ya no lo borra una página sin cursor (al reanudar con un cursor guardado y no
  haber nada nuevo, se perdía), ni queda el de otro recorrido si este falla al empezar. Es uno por cliente: para dos
  recorridos a la vez, un cliente para cada uno.
- **`lastRateLimit`** (nuevo; `last_rate_limit` en Python y Ruby, `getLastRateLimit()` en Java, `LastRateLimit` en C# y
  `LastRateLimit()` en Go): el cupo de la última respuesta, sus cabeceras `X-RateLimit-*` (`limit`, `remaining`, `reset`).
- **C#: tus clases, con sus nombres de JSON.** Un objeto propio salía con los nombres de C# (`{"To":…,"ClientId":…}`) y la
  API ignoraba esos campos, sin error. Ahora cuentan `[JsonPropertyName]`, `[JsonProperty]`, `[DataMember(Name)]`,
  `[JsonIgnore]` y `[DataContract]`, y sin atributo el nombre va en snake_case (`ClientId` → `client_id`). Los objetos
  anónimos y los diccionarios, como antes. Además: el error de red lleva la excepción de debajo (`InnerException`), `BaseUrl`
  se comprueba al ponerla y el paquete de NuGet trae `net8.0`.
- **Python: 3.9 o superior.** El paquete se construye con setuptools 80.9.0, que necesita 3.9: desde el código fuente no se
  instalaba en 3.8. Además, un `NaN` en los datos es un `ValueError` (antes salía un JSON que no vale) y `MundoSMSError` se
  puede pasar por `pickle` con todos sus campos.
- **PHP, Python y Node.js comprueban los valores de las opciones,** como Java, C#, Go y Ruby: unos `retries` negativos, un
  `timeout` de 0 o una dirección que no es `http(s)://` dan error al crear el cliente.
- **Ruby: `Net::HTTP` ya no repite la petición por su cuenta** con la misma firma (la repite el SDK, firmada otra vez); y la
  gema lleva su README. **Go:** el `Transport` tampoco repite un `POST` por su cuenta. **Node.js:** el error de red lleva la
  causa en `cause`. **Java:** el error de red lleva la excepción de debajo (`getCause()`), y el jar, la `LICENSE`.
- **Pruebas propias** en PHP, Python, Node.js, Java y C# (en `tests/`, sin red), como ya tenían Go y Ruby, y un workflow que
  pasa las de los siete en cada PR.
- **Java, en Maven Central** (nuevo): `es.mundosms:mundosms-sdk`, para Maven (`<groupId>es.mundosms</groupId>`,
  `<artifactId>mundosms-sdk</artifactId>`) o Gradle (`implementation("es.mundosms:mundosms-sdk:1.2.0")`). Es el mismo
  `MundoSMS.java`, sin dependencias y para Java 8 o superior, con su jar de fuentes y el de Javadoc, firmados. Copiar el
  fichero sigue valiendo.
- **Ruby** (nuevo): la gema `mundosms`, para Ruby 3.1 o superior, en un solo fichero y sin dependencias: `gem install
  mundosms`. Hace lo mismo que los demás, con los nombres al estilo de Ruby: `MundoSMS::Client.new(token, private_key,
  base_url:, retries:, timeout:)`; `get`, `post`, `patch`, `delete` y `request`; `all`, un `Enumerator::Lazy` que pide cada
  página al llegar a ella (o con un bloque); `last_cursor`; `MundoSMS::Error` (`http`, `status_code`, `message`, `detail`,
  `data`, `response` y `request_id`), y `MundoSMS.verify_webhook`, para Rails sobre `request.raw_post`.

## v1.1.0

- **Go** (nuevo): el cliente en Go, para Go 1.22 o superior, en un solo fichero y sin dependencias:
  `go get github.com/mundosms/mundosms-sdk/go`. Hace lo mismo que los demás, con los nombres al estilo de Go:
  `mundosms.New` con las opciones `BaseURL`, `Retries`, `Timeout` y `HTTPClient`; `Get`, `Post`, `Patch`, `Delete` y
  `Request` con tu `context.Context` delante, que corta también los reintentos y sus esperas; `All`, un iterador para
  `for … range` (Go 1.23 o superior); `LastCursor()`; el error `*mundosms.Error` (`HTTP`, `StatusCode`, `Message`, `Detail`,
  `Data`, `Response` y `RequestID`), y `VerifyWebhook`. El módulo lleva su etiqueta, `go/v1.1.0`.
- **Composer** instala solo el cliente de PHP: `composer require mundosms/sdk` dejaba en `vendor/` el repositorio entero,
  con los demás lenguajes. Ahora baja `php/`, `composer.json`, `README.md`, `LICENSE` y `CHANGELOG.md`. Como Composer, el zip
  del código de cada versión en GitHub («Source code») lleva solo eso: los seis clientes van en el `mundosms-sdk-1.1.0.zip`
  adjunto a la versión.
- **`X-Timestamp` siempre creciente** en PHP, Python, Node.js y C#, como ya hacía Java: cada petición lleva uno mayor que
  el de la anterior del mismo proceso. Antes, dos peticiones iguales en el mismo milisegundo (un reintento inmediato tras un
  `429` con `Retry-After: 0`, o dos iguales a la vez desde varios hilos o con `Promise.all`) firmaban igual, y la API
  rechazaba la segunda por repetida (`409 Duplicated request`). El SDK la repetía un segundo después, o daba el error si no
  le quedaban reintentos.
- Por lo demás, PHP, Python, Node.js, Java y C# no cambian, salvo el número de versión.

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
