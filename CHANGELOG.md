# Cambios

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
