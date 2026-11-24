# SDK de la API REST de MundoSMS

Clientes de la [API REST v3 de MundoSMS](https://www.mundosms.es/api-v3) (centralita, llamadas, SMS, VozPush, NumCheck y
WhatsApp) en **PHP, Python, Node.js, Java, C#, Go y Ruby**. Cada uno es **un solo fichero, sin dependencias**: lo copias en tu proyecto y ya.
Firman cada petición por ti (`X-Timestamp` en milisegundos y la firma HMAC de tu API Key), ponen una `Idempotency-Key` en
cada `POST` para que un reintento no envíe nada dos veces, y reintentan solo lo que es seguro repetir. También comprueban la
firma de los avisos que te mandamos ([Comprobar los avisos](#comprobar-los-avisos-que-te-llegan)).

| Lenguaje | Fichero | Requisitos |
|---|---|---|
| PHP | [`php/mundosms.php`](php/mundosms.php) | PHP 7.4 o superior, con la extensión curl |
| Python | [`python/mundosms.py`](python/mundosms.py) | Python 3.9 o superior |
| Node.js | [`node/mundosms.js`](node/mundosms.js) (y [`node/mundosms.d.ts`](node/mundosms.d.ts), los tipos de TypeScript) | Node.js 18 o superior |
| Java | [`java/MundoSMS.java`](java/MundoSMS.java) (el paquete `es.mundosms:mundosms-sdk` de Maven Central) | Java 8 o superior |
| C# (.NET) | [`dotnet/MundoSMS.cs`](dotnet/MundoSMS.cs) | .NET Framework 4.6.1 o superior, o .NET 5 o superior (C# 6 o superior) |
| Go | [`go/mundosms.go`](go/mundosms.go) (el módulo `github.com/mundosms/mundosms-sdk/go`) | Go 1.22 o superior |
| Ruby | [`ruby/lib/mundosms.rb`](ruby/lib/mundosms.rb) (la gema `mundosms`) | Ruby 3.1 o superior |

Las API Keys (token y clave privada) se crean en el panel de MundoSMS, en **API y Soporte → Claves de API**.

Todos tienen los mismos nombres, cada uno con el estilo de su lenguaje: `all()` recorre un listado entero,
`lastCursor` guarda dónde se quedó, `request()` hace una petición cualquiera y `verifyWebhook()` comprueba un aviso.

## PHP

```php
require "mundosms.php";

$api = new MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA");   // la clave privada, "" si tu API Key no exige firma
$r = $api->post("/sms", ["to" => "34600123456", "text" => "Tu cita es mañana a las 10:00", "from" => "MiClinica"]);
echo $r["data"]["id"], "\n";

foreach ($api->all("/calls", ["from_datetime" => "2026-11-01"]) as $call) {
    echo $call["type"], " ", $call["id"], " ", $call["state"], "\n";
}

try {
    $api->patch("/extensions/recepcion", ["record_calls" => true]);
} catch (MundoSMSError $e) {
    echo $e->http, " ", $e->statusCode, " ", $e->getMessage(), " ", $e->detail, " ", $e->requestId, "\n";
}
```

## Python

```python
from mundosms import MundoSMS, MundoSMSError

api = MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA")   # la clave privada, "" si tu API Key no exige firma
r = api.post("/sms", {"to": "34600123456", "text": "Tu cita es mañana a las 10:00", "from": "MiClinica"})
print(r["data"]["id"])

for call in api.all("/calls", {"from_datetime": "2026-11-01"}):
    print(call["type"], call["id"], call["state"])

try:
    api.patch("/extensions/recepcion", {"record_calls": True})
except MundoSMSError as e:
    print(e.http, e.status_code, e.message, e.detail, e.request_id)
```

## Node.js

El fichero es **CommonJS**. En un proyecto normal (sin `"type": "module"` en tu `package.json`), con `require` y el código
dentro de una función `async`:

```js
const { MundoSMS, MundoSMSError } = require("./mundosms");

async function main() {
  const api = new MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA"); // la clave privada, "" si tu API Key no exige firma
  const r = await api.post("/sms", { to: "34600123456", text: "Tu cita es mañana a las 10:00", from: "MiClinica" });
  console.log(r.data.id);

  for await (const call of api.all("/calls", { from_datetime: "2026-11-01" })) {
    console.log(call.type, call.id, call.state);
  }

  try {
    await api.patch("/extensions/recepcion", { record_calls: true });
  } catch (e) {
    if (!(e instanceof MundoSMSError)) throw e;
    console.log(e.http, e.statusCode, e.message, e.detail, e.requestId);
  }
}

main().catch((e) => { console.error(e); process.exitCode = 1; });
```

En un proyecto **ESM** (`"type": "module"` en tu `package.json`, o ficheros `.mjs`), guarda el SDK como **`mundosms.cjs`**
(con la extensión `.js`, Node lo leería como ESM y fallaría en su `require`) e impórtalo. Aquí sí vale `await` fuera de una
función:

```js
import { MundoSMS, MundoSMSError } from "./mundosms.cjs";

const api = new MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA");
const r = await api.post("/sms", { to: "34600123456", text: "Tu cita es mañana a las 10:00", from: "MiClinica" });
console.log(r.data.id);
```

El token y la clave privada se guardan en campos privados del objeto: no salen en un `console.log(api)` ni en un
`JSON.stringify`.

### TypeScript

Junto a `mundosms.js` está `mundosms.d.ts`, con los tipos: guárdalo en la misma carpeta (si guardas el SDK como
`mundosms.cjs`, llámalo `mundosms.d.cts`). El tipo que pasas a `get`, `post`, `patch`, `delete` y `request` es el de
`data`; con `FileResponse`, el de una grabación o un PDF. En `all`, el de cada elemento.

```ts
import { MundoSMS, type FileResponse } from "./mundosms";
const api = new MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA");
const account = await api.get<{ balance: number; available: number }>("/account");
const recording = await api.get<FileResponse>("/calls/out/8812345/recording");   // recording.body es un Buffer
```

## Java

Está en Maven Central, `es.mundosms:mundosms-sdk`, sin dependencias. Funciona desde Java 8. Con Maven, en tu `pom.xml`:

```xml
<dependency>
  <groupId>es.mundosms</groupId>
  <artifactId>mundosms-sdk</artifactId>
  <version>1.2.0</version>
</dependency>
```

(con Gradle, `implementation("es.mundosms:mundosms-sdk:1.2.0")` en `dependencies`). También puedes copiar el fichero:
`MundoSMS.java` es del paquete `es.mundosms` y va en la carpeta `es/mundosms/` de tus fuentes (con Maven o Gradle,
`src/main/java/es/mundosms/MundoSMS.java`).

```java
import es.mundosms.MundoSMS;
import es.mundosms.MundoSMS.MundoSMSError;
import java.util.Map;

public class Example {
    public static void main(String[] args) {
        MundoSMS api = new MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA");   // la clave privada, "" si tu API Key no exige firma
        Map<String, Object> r = api.post("/sms", MundoSMS.map("to", "34600123456", "text", "Tu cita es mañana a las 10:00", "from", "MiClinica"));
        Map<?, ?> data = (Map<?, ?>) r.get("data");
        System.out.println(data.get("id"));

        for (Map<String, Object> call : api.all("/calls", MundoSMS.map("from_datetime", "2026-11-01"))) {
            System.out.println(call.get("type") + " " + call.get("id") + " " + call.get("state"));
        }

        try {
            api.patch("/extensions/recepcion", MundoSMS.map("record_calls", true));
        } catch (MundoSMSError e) {
            System.out.println(e.getHttp() + " " + e.getStatusCode() + " " + e.getMessage() + " " + e.getDetail() + " " + e.getRequestId());
        }
    }
}
```

- **Las respuestas** son el JSON entero: los objetos llegan como `Map<String, Object>`, las listas como `List<Object>`,
  los números enteros como `Long` y los decimales como `Double`.
- **Los datos** que mandas pueden ir en cualquier `Map`. `MundoSMS.map("clave", valor, …)` lo abrevia: crea un
  `LinkedHashMap` con esos pares. Dentro valen textos, números, `Boolean`, `null`, listas, arrays y otros `Map`.
- `MundoSMSError` es una excepción no comprobada (`RuntimeException`).
- El cliente HTTP de Java (`HttpURLConnection`) no sabe mandar `PATCH`: el SDK lo manda como `POST` con la cabecera
  `X-HTTP-Method-Override: PATCH` y lo firma como `PATCH`. No tienes que hacer nada.
- Puedes usar el mismo objeto `MundoSMS` desde varios hilos a la vez.

## C# (.NET)

Copia `MundoSMS.cs` en tu proyecto. Vale para .NET Framework 4.6.1 o superior y para .NET 5 o superior (también .NET
Core), con C# 6 o superior. Cada método tiene su versión asíncrona (`GetAsync`, `PostAsync`, `PatchAsync`,
`DeleteAsync`, `RequestAsync`, con un `CancellationToken` opcional) y la síncrona (`Get`, `Post`, `Patch`, `Delete`,
`Request`). Los datos y los filtros van en un objeto anónimo (`new { to = "…" }`) o en un `Dictionary<string, object>`.

```csharp
using System;
using System.Threading.Tasks;
using MundoSMS;

class Program
{
    static async Task Main()
    {
        var api = new MundoSMSClient("TU_TOKEN", "TU_CLAVE_PRIVADA");   // la clave privada, "" si tu API Key no exige firma
        var r = await api.PostAsync("/sms", new { to = "34600123456", text = "Tu cita es mañana a las 10:00", from = "MiClinica" });
        Console.WriteLine(r.Data["id"]);

        foreach (var call in api.All("/calls", new { from_datetime = "2026-11-01" }))
            Console.WriteLine($"{call["type"]} {call["id"]} {call["state"]}");

        try
        {
            await api.PatchAsync("/extensions/recepcion", new { record_calls = true });
        }
        catch (MundoSMSException e)
        {
            Console.WriteLine($"{e.Http} {e.StatusCode} {e.Message} {e.Detail} {e.RequestId}");
        }
    }
}
```

- La respuesta es un `MundoSMSResponse` (un `Dictionary<string, object>` con el JSON entero), y `r.Data` es su `data`.
  Dentro, los objetos son `Dictionary<string, object>`, las listas `List<object>`, los enteros `long` y los decimales
  `double` (`Convert.ToInt32(r.Data["id"])` si lo quieres en un `int`).
- `All` se recorre con `foreach` y pide cada página cuando llegas a ella. En .NET 6 o superior también puedes usar
  `await foreach (var call in api.AllAsync("/calls", …))`.
- Crea un cliente y reutilízalo: se puede usar desde varios hilos. Si usas `IHttpClientFactory`, pasa tu `HttpClient`
  como tercer argumento, con un manejador que **no siga redirecciones** (si las sigue, el constructor da
  `ArgumentException`: una redirección llevaría tu firma y tus datos a otro servidor):

  ```csharp
  services.AddHttpClient("mundosms")
          .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
          .RedactLoggedHeaders(new[] { "Authorization", "X-Signature" });   // en .NET 6 y 7, que no salgan en los registros
  var api = new MundoSMSClient("TU_TOKEN", "TU_CLAVE_PRIVADA", factory.CreateClient("mundosms"));
  ```
- Tus propias clases también valen para los datos: cada propiedad sale con el nombre de `[JsonPropertyName]`
  (System.Text.Json), `[JsonProperty]` (Newtonsoft) o `[DataMember(Name)]` y, sin atributo, en snake_case (`ClientId` →
  `client_id`). `[JsonIgnore]` no se manda y, con `[DataContract]`, solo van los `[DataMember]`. Un objeto anónimo va tal cual.
- Las fechas van como texto (`"2026-11-01 08:00:00"`, hora de Madrid). Un `DateTime` da `ArgumentException` y no se
  manda nada.
- En .NET Framework, si tu aplicación fijó `ServicePointManager.SecurityProtocol` sin TLS 1.2, el SDK lo añade.

## Go

Un módulo, `github.com/mundosms/mundosms-sdk/go`, con un solo fichero y sin dependencias (solo la biblioteca estándar).
Funciona desde Go 1.22; recorrer `All` con `for … range` pide Go 1.23 o superior. En tu módulo:

```sh
go get github.com/mundosms/mundosms-sdk/go
```

```go
package main

import (
	"context"
	"errors"
	"fmt"
	"log"

	mundosms "github.com/mundosms/mundosms-sdk/go"
)

func main() {
	ctx := context.Background()
	api, err := mundosms.New("TU_TOKEN", "TU_CLAVE_PRIVADA") // la clave privada, "" si tu API Key no exige firma
	if err != nil {
		log.Fatal(err)
	}
	r, err := api.Post(ctx, "/sms", mundosms.Params{"to": "34600123456", "text": "Tu cita es mañana a las 10:00", "from": "MiClinica"})
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println(r.Data()["id"])

	for call, err := range api.All(ctx, "/calls", mundosms.Params{"from_datetime": "2026-11-01"}) {
		if err != nil {
			log.Fatal(err)
		}
		fmt.Println(call["type"], call["id"], call["state"])
	}

	_, err = api.Patch(ctx, "/extensions/recepcion", mundosms.Params{"record_calls": true})
	var e *mundosms.Error
	if errors.As(err, &e) {
		fmt.Println(e.HTTP, e.StatusCode, e.Message, e.Detail, e.RequestID)
	}
}
```

- **El contexto:** cada método lleva tu `context.Context` delante. Si lo cancelas (o vence su plazo), se corta la
  petición, no se reintenta y no se espera más: el error es un `*mundosms.Error` con `HTTP` 0, y
  `errors.Is(err, context.Canceled)` (o `context.DeadlineExceeded`) es `true`.
- **Las respuestas** son el JSON entero, un `mundosms.Response` (un `map[string]any`): `r.Data()` es su `data`, los objetos
  llegan como `map[string]any`, las listas como `[]any` y los números como `json.Number`, para no perder cifras
  (`fmt.Println` los escribe tal cual; `n.Int64()` o `n.Float64()` si los quieres en un número). Con `r.Decode(&v)` pasas
  la respuesta a tus propios tipos, como `json.Unmarshal`.
- **Los datos** que mandas van en un `mundosms.Params` (un `map[string]any`) o, en `Post` y `Patch`, en tu struct con sus
  etiquetas `json`. Un `map` no tiene orden: los filtros de la query van por orden alfabético (da igual: se firma justo lo
  que se manda).
- **`All`** es un iterador (`iter.Seq2[map[string]any, error]`): pide cada página cuando llegas a ella, y si sales del
  bucle no pide más. Si una página falla, te da el error y no sigue. En Go 1.22, sin `range`, se llama con tu función:
  `api.All(ctx, "/calls", q)(func(call map[string]any, err error) bool { …; return true })`.
- **Lo que no se puede mandar** (una `Idempotency-Key` mal formada, un cuerpo que no se puede pasar a JSON, un filtro de
  un tipo que no va en la query) da un error que envuelve a `mundosms.ErrInvalidArgument`, y no se manda nada.
- Crea un cliente y reutilízalo: se puede usar desde varias goroutines a la vez.
- `fmt.Print` del cliente (`%v`, `%+v`, `%#v`) enseña solo la dirección de la API: ni el token ni la clave privada.
- La documentación del paquete está en [pkg.go.dev](https://pkg.go.dev/github.com/mundosms/mundosms-sdk/go).

## Ruby

Una gema, `mundosms`, con un solo fichero y sin dependencias (solo la biblioteca estándar). Ruby 3.1 o superior:

```sh
gem install mundosms
```

(o `gem "mundosms"` en tu `Gemfile`).

```ruby
require "mundosms"

api = MundoSMS::Client.new("TU_TOKEN", "TU_CLAVE_PRIVADA")   # la clave privada, "" si tu API Key no exige firma
r = api.post("/sms", { "to" => "34600123456", "text" => "Tu cita es mañana a las 10:00", "from" => "MiClinica" })
puts r["data"]["id"]

api.all("/calls", { "from_datetime" => "2026-11-01" }).each do |call|
  puts "#{call['type']} #{call['id']} #{call['state']}"
end

begin
  api.patch("/extensions/recepcion", { "record_calls" => true })
rescue MundoSMS::Error => e
  puts [e.http, e.status_code, e.message, e.detail, e.request_id].join(" ")
end
```

- **Las respuestas** son el JSON entero, un `Hash` con claves de texto (`r["data"]["id"]`). Los datos que mandas pueden
  llevar claves de texto o símbolos (`{ to: "…" }`).
- **`all`** devuelve un `Enumerator::Lazy`: pide cada página cuando llegas a ella, así que `api.all("/calls").first(10)` pide
  solo las que hacen falta. Con un bloque (`api.all("/calls") { |call| … }`) recorre el listado entero.
- **Lo que no se puede mandar** (una `Idempotency-Key` mal formada, un texto que no es UTF-8, un `Hash` en la query) es un
  `ArgumentError`, y no se manda nada. Una opción que no existe, también: `unknown keyword`.
- Crea un cliente y reutilízalo: se puede usar desde varios hilos a la vez.
- `p`, `inspect`, `to_s` y `pp` del cliente enseñan solo la dirección de la API: ni el token ni la clave privada.
- **En Rails**, para comprobar los avisos que te llegan, usa `request.raw_post` y nunca `params`: [Comprobar los
  avisos](#comprobar-los-avisos-que-te-llegan).

## Qué hace cada método

- `get(path, query)`, `post(path, data)`, `patch(path, data)` y `delete(path, query)` devuelven la respuesta entera
  (`status`, `status_code`, `message`, `data`). `post` admite un tercer argumento con tu `Idempotency-Key`
  ([Reintentos](#reintentos-e-idempotency-key)), igual que `request(method, path, data, options)`.
  - **La ruta** va sin `/v3` (`"/calls"`, `"/numbers/34910123456"`); si la escribes con `/v3` delante, se quita (cuando
    `baseUrl` ya lo lleva, como la de por defecto). Cada trozo se codifica solo (espacios, tildes, `+`…); un `%XX` que ya
    escribas se respeta. Si la ruta trae parámetros (`"/calls?type=in"`), se juntan con los del segundo argumento, y si se
    repite uno, gana el del argumento. En esa query, un `%XX` se decodifica y un `+` es un `+`, no un espacio: una fecha con
    zona escrita en la ruta (`"/calls?from_datetime=2026-11-20T10:15:03+01:00"`) llega tal cual.
  - **En `get` y `delete`**, los filtros van en la query: `true`/`false` como `true` y `false`, una lista separada por
    comas (`["in", "out"]` → `in,out`), y `null` (o `None`) y las listas vacías no se mandan.
  - **En `post` y `patch`**, los datos van en un cuerpo JSON.
- `all(path, query)` recorre todas las páginas de un listado y te da los elementos uno a uno. Cada página lleva los
  mismos filtros (y el mismo `order`) más el `cursor` de la anterior. Al acabar, el último `next_cursor` queda en
  `lastCursor` (PHP y Node.js), `last_cursor` (Python y Ruby), `getLastCursor()` (Java), `LastCursor` (C#) o `LastCursor()` (Go). Guárdalo y pásalo
  como `cursor` la próxima vez para pedir solo lo nuevo.
  - Es el de ese recorrido: al empezarlo, el `cursor` que le pasas (o ninguno), y después el último `next_cursor` que llegó.
    Una página que llega sin cursor no lo borra, y un recorrido que falla en la primera página no deja el de otro.
  - Es uno por cliente: para dos recorridos a la vez (en dos hilos, o con `Promise.all`), un cliente para cada uno.
  - Avanza cuando ya te ha dado todos los elementos de una página: si tu código falla a mitad de una, sigue en la
    anterior. Guárdalo cuando el bucle termine sin error.
  - Termina con `has_more: false`: con `stop_at_ongoing`, también cuando la página se para en una llamada en curso
    (`data.ongoing_call`), y entonces `lastCursor` es justo antes de ella. También para si el cursor no avanza. Una página
    **vacía con `has_more: true`** (con `stop_at_ongoing` y un filtro, cuando ha mirado muchas llamadas que no casan) no es
    el final: sigue.
  - `GET /calls/missed` y `GET /calls/live` no van por páginas: son una foto, y con `has_more: true` no traen `next_cursor`.
    Ahí `all()` no puede darte la lista entera, así que da un error antes de ningún elemento: sube `limit` (hasta 500), o
    filtra, y pídela con `get`.
- `lastRateLimit` (`last_rate_limit` en Python y Ruby, `getLastRateLimit()` en Java, `LastRateLimit` en C# y
  `LastRateLimit()` en Go) es el cupo que dijo la última respuesta, sus cabeceras `X-RateLimit-*`: `limit`, `remaining` y
  `reset`, o nada si no las traía. Con él, un bucle tuyo no se pasa de las 10 peticiones por segundo: cuando `remaining`
  llega a `0`, espera `reset` segundos.
- **Ficheros:** las rutas que devuelven un audio, una imagen o un PDF (la grabación de una llamada, una locución, el
  diagrama, el certificado de un SMS) no contestan JSON. Por su `Content-Type` (`audio/…`, `image/…`, `application/pdf`
  o `application/octet-stream`), vuelven como `{"status": "success", "content_type": "audio/mpeg", "body": …}`, con el
  contenido tal cual en `body`: un `string` en PHP, `bytes` en Python, un `Buffer` en Node.js, un `byte[]` en Java, un
  `byte[]` en C# (también en `r.Body`, con `r.ContentType`) y un `[]byte` en Go (también `r.Body()`, con `r.ContentType()`).
  Una respuesta vacía (un `204`) vuelve igual, con `body` vacío.

## Errores

Cualquier respuesta con `"status": "error"` es una excepción `MundoSMSError` (en C#, `MundoSMSException`; en Ruby,
`MundoSMS::Error`; en Go, el error es un `*mundosms.Error`, que sacas con `errors.As`), venga con el código HTTP que venga. Un WhatsApp **sin confirmar** no es un error: la API contesta `202` con `"status": "success"` y
`data.state` `"unknown"` (WhatsApp no contestó y el mensaje **puede haber salido**), y el SDK te devuelve la respuesta. No
lo reenvíes.

| | PHP | Python | Node.js | Java | C# | Go | Ruby |
|---|---|---|---|---|---|---|---|
| código HTTP (`0` si no hubo respuesta) | `$e->http` | `e.http` | `e.http` | `e.getHttp()` | `e.Http` | `e.HTTP` | `e.http` |
| `status_code` de la API (texto: `"3"`, `"4"`…) | `$e->statusCode` | `e.status_code` | `e.statusCode` | `e.getStatusCode()` | `e.StatusCode` | `e.StatusCode` | `e.status_code` |
| mensaje | `$e->getMessage()` | `e.message` | `e.message` | `e.getMessage()` | `e.Message` | `e.Message` | `e.message` |
| `detail` | `$e->detail` | `e.detail` | `e.detail` | `e.getDetail()` | `e.Detail` | `e.Detail` | `e.detail` |
| `data` de la respuesta | `$e->data` | `e.data` | `e.data` | `e.getData()` | `e.Data` | `e.Data` | `e.data` |
| la cabecera `X-Request-Id` | `$e->requestId` | `e.request_id` | `e.requestId` | `e.getRequestId()` | `e.RequestId` | `e.RequestID` | `e.request_id` |
| la respuesta entera, decodificada | `$e->response` | `e.response` | `e.response` | `e.getResponse()` | `e.Response` | `e.Response` | `e.response` |

- Guarda el `X-Request-Id` cuando algo falle: con él encuentras la petición en Logs API y, si nos escribes, la
  encontramos nosotros.
- Sin respuesta (la red, o el plazo agotado), `http` es `0` y el mensaje empieza por `No response from the API:`, seguido
  de la causa (`timed out after 60 s` si se agotó el plazo). Los mensajes del SDK van en inglés, como los de la API.
- **Una redirección (`3xx`) no se sigue nunca:** la API no redirige, y seguirla llevaría tu token, tu firma, la
  `Idempotency-Key` y, con un `307`, el cuerpo a otro servidor. Es un error con su `http` (`302`…) y adónde mandaba:
  `HTTP 302: redirect to https://… not followed`. Si te pasa, revisa `baseUrl`.
- **Un `2xx` que no es JSON** (el HTML de un proxy, de un portal cautivo o de un cortafuegos) también es un error, nunca un
  éxito: `HTTP 200: the response is not JSON (text/html)`. Los ficheros de la API no, por su `Content-Type`.
- En PHP, si los datos de un `post` o un `patch` no se pueden pasar a JSON (casi siempre, un texto que no es UTF-8), no se
  manda nada: salta una `InvalidArgumentException` que lo dice.
- En Java, si los datos no se pueden pasar a JSON (un `NaN`, o un objeto que no es un `Map`, una lista, un array, un
  texto, un número o un `Boolean`), salta una `IllegalArgumentException` y no se manda nada. En C#, igual con una
  `ArgumentException` (también con un `DateTime`: las fechas van como texto).

## Reintentos e `Idempotency-Key`

Cada `POST` lleva la cabecera **`Idempotency-Key`**: un UUID nuevo en cada llamada, el mismo en todos sus intentos. Si un
`POST` se queda sin respuesta (se cortó la red, o se agotó el plazo) y el SDK lo repite, la API reconoce la clave y no lo
vuelve a hacer: devuelve la respuesta de la primera vez. Así, un SMS o un WhatsApp no sale dos veces aunque la red se corte
justo después de enviarlo.

Qué se reintenta, hasta `retries` veces (3 por defecto):

| Qué pasó | `GET` | `PATCH` y `DELETE` | `POST` |
|---|---|---|---|
| `429`: demasiadas peticiones (no se hizo nada) | sí | sí | sí |
| sin respuesta: la red, o el plazo agotado | sí | sí | sí, con la misma `Idempotency-Key` |
| `409 Duplicated request`: la misma petición firmada llegó dos veces (la repitió algo entre medias) | sí | sí | sí, con la misma clave |
| `409 Request in progress`: la primera con esa clave aún no ha terminado | — | — | sí, con la misma clave |
| `5xx` | sí | no | no: la API no guarda los `5xx`, y repetirlo lo haría otra vez |

Antes de cada intento se espera lo que diga la cabecera `Retry-After` (como mucho 60 segundos); si no la trae, 1, 2, 4…
segundos. Cada intento va firmado con su propio `X-Timestamp`, en milisegundos y siempre mayor que el de la petición
anterior del mismo proceso: un reintento inmediato, o dos peticiones iguales a la vez, nunca firman igual (la API rechazaría
la segunda por repetida).

**Tu propia clave.** La clave automática protege los reintentos de una llamada. Si quieres que tampoco se repita cuando
vuelves a llamar tú (tu programa se reinicia, tu cola reintenta un trabajo), pasa una clave tuya, estable para esa
operación: por ejemplo, el id de tu pedido. De 1 a 64 caracteres: letras sin tilde, cifras y `_ . : -`. La API la recuerda
24 horas. Con otra forma, el SDK lanza un error y **no manda nada**: `InvalidArgumentException` en PHP, `ValueError` en
Python, `TypeError` en Node.js, `IllegalArgumentException` en Java, `ArgumentException` en C#, `ArgumentError` en Ruby y,
en Go, un error que envuelve a `mundosms.ErrInvalidArgument`.

```php
$r = $api->post("/whatsapp/messages", ["to" => "34600000001", "type" => "text", "text" => "Tu pedido ha salido"],
                ["idempotencyKey" => "pedido-881"]);
```

```python
r = api.post("/whatsapp/messages", {"to": "34600000001", "type": "text", "text": "Tu pedido ha salido"},
             idempotency_key="pedido-881")
```

```js
const r = await api.post("/whatsapp/messages", { to: "34600000001", type: "text", text: "Tu pedido ha salido" },
                         { idempotencyKey: "pedido-881" });
```

```java
Map<String, Object> r = api.post("/whatsapp/messages", MundoSMS.map("to", "34600000001", "type", "text", "text", "Tu pedido ha salido"),
                                 new MundoSMS.RequestOptions().idempotencyKey("pedido-881"));
```

```csharp
var r = await api.PostAsync("/whatsapp/messages", new { to = "34600000001", type = "text", text = "Tu pedido ha salido" },
                            new MundoSMSRequestOptions { IdempotencyKey = "pedido-881" });
```

```go
r, err := api.Post(ctx, "/whatsapp/messages", mundosms.Params{"to": "34600000001", "type": "text", "text": "Tu pedido ha salido"},
	mundosms.IdempotencyKey("pedido-881"))
```

```ruby
r = api.post("/whatsapp/messages", { "to" => "34600000001", "type" => "text", "text" => "Tu pedido ha salido" },
             idempotency_key: "pedido-881")
```

En `request` (y en C#, `RequestAsync` y `Request`), las opciones van en el cuarto argumento; en Go, detrás de los datos,
también en `Request`.

**La respuesta repetida.** Cuando la API devuelve la respuesta guardada de la primera vez (con la cabecera
`Idempotent-Replayed: true`), la respuesta trae además **`"idempotent_replayed": true`**; en C#, también
`r.IdempotentReplayed`, y en Go, `r.IdempotentReplayed()`. Quiere decir que no se ha vuelto a hacer nada: el `id` es el de la primera vez. Si no se repitió,
ese campo no va.

- La misma clave con otra petición (otra ruta u otro cuerpo) da `422 Idempotency-Key already used`: es un error, no se
  reintenta.
- En `POST /v3/extensions` la API no guarda la respuesta, porque lleva la contraseña SIP. Si un reintento da
  `409 Extension already exists`, la creó el intento anterior: su contraseña está en el panel, en **Líneas SIP**.
- **Un `DELETE` que se quedó sin respuesta** se repite, y si la API contesta entonces `404`, es que el intento que se quedó
  sin respuesta ya lo borró: vuelve como éxito, `{"status": "success", "message": "Already deleted", "data": {"state":
  "deleted", "changed": false}}`, y no como error. Sin el fallo de red de antes, un `404` sigue siendo un error. Lo mismo si
  la petición la repitió el propio cliente HTTP con la misma firma, con «Rechazar peticiones repetidas» en la clave (así
  nacen): la API contesta `409 Duplicated request` y el SDK la repite firmada de nuevo.

## Opciones

En el tercer argumento del constructor:

| Opción | Qué es | Por defecto |
|---|---|---|
| `baseUrl` (Python y Ruby: `base_url`) | la dirección de la API | `https://api.mundosms.es/v3` |
| `retries` | cuántas veces se reintenta lo que es seguro repetir (`0`, ninguna) | `3` |
| `timeout` | los segundos que puede durar cada petición entera (conectar, mandar y recibir la respuesta). Admite decimales | `60` |

Una opción que no existe (por ejemplo, mal escrita) da error al crear el cliente, en vez de ignorarse:
`InvalidArgumentException` en PHP, `TypeError` en Python y Node.js y `ArgumentError` en Ruby. Un valor que no vale (unos
`retries` negativos, un `timeout` de 0, una dirección que no es `http(s)://`), también: `InvalidArgumentException` en PHP,
`ValueError` en Python, `TypeError` en Node.js y `ArgumentError` en Ruby.

```php
$api = new MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA", ["retries" => 5, "timeout" => 90]);
```

```python
api = MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA", retries=5, timeout=90)
```

```js
const api = new MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA", { retries: 5, timeout: 90 });
```

```ruby
api = MundoSMS::Client.new("TU_TOKEN", "TU_CLAVE_PRIVADA", retries: 5, timeout: 90)   # y base_url:
```

En Java, las opciones van en un `MundoSMS.Options`; en C#, son propiedades (`BaseUrl`, `Retries` y `Timeout`, un
`TimeSpan`); en Go, van detrás de la clave privada (`mundosms.BaseURL`, `mundosms.Retries` y `mundosms.Timeout`, un
`time.Duration`), y un valor que no vale (unos `retries` negativos, una dirección que no es `http(s)://`) hace que `New`
devuelva un error:

```java
MundoSMS api = new MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA", new MundoSMS.Options().retries(5).timeout(90));
```

```csharp
var api = new MundoSMSClient("TU_TOKEN", "TU_CLAVE_PRIVADA") { Retries = 5, Timeout = TimeSpan.FromSeconds(90) };
```

```go
api, err := mundosms.New("TU_TOKEN", "TU_CLAVE_PRIVADA", mundosms.Retries(5), mundosms.Timeout(90*time.Second))
```

En Go hay una opción más, `mundosms.HTTPClient(…)`, para que use tu `*http.Client` (un proxy, tu propio `Transport`); sus
redirecciones no se siguen igualmente. En C#, el tercer argumento del constructor es tu `HttpClient`, sin redirecciones
([C#](#c-net)).

## Comprobar los avisos que te llegan

Los avisos que te mandamos a tu URL (los de `callback_url` en SMS, VozPush y NumCheck, y los webhooks de WhatsApp) llevan
la cabecera `X-MundoSMS-Signature: t=<segundos>,v1=<firma>`. Con ella sabes que el aviso es nuestro y que nadie lo ha
tocado. El secreto está en el panel, en **API y Soporte → Webhooks** (`whsec_…`); en WhatsApp, el que te dimos al guardar
el destino del número.

Cada SDK lo comprueba con una función: `MundoSMS::verifyWebhook` (PHP), `MundoSMS.verify_webhook` (Python),
`MundoSMS.verifyWebhook` (Node.js y Java), `MundoSMSClient.VerifyWebhook` (C#), `mundosms.VerifyWebhook` (Go) y
`MundoSMS.verify_webhook` (Ruby). Los argumentos son el secreto, la cabecera, el cuerpo y, opcional, la tolerancia en
segundos (300; en Ruby, `tolerance:`; en Go, con otra tolerancia, `mundosms.VerifyWebhookWithTolerance`, un
`time.Duration`). Devuelve `true` si:

- `v1` es el HMAC-SHA256, en hexadecimal, de `t` + `.` + el cuerpo, con tu secreto (comparado en tiempo constante);
- y `t` no se aleja de tu hora más de la tolerancia: 5 minutos, salvo que digas otra cosa.

Si no, `false`. Una cabecera que falta o está mal formada también da `false`, sin excepción.

**El cuerpo, tal cual llega:** los bytes de la petición, sin volver a serializar el JSON. Con otro orden o con otros
espacios, la firma no coincide. Si el aviso es bueno, contesta un `2xx` en menos de 10 segundos y procésalo después. Puede
llegarte dos veces: el `id` del aviso (y la cabecera `X-MundoSMS-Event-Id`) es el mismo en cada intento.

Todos los avisos llevan el mismo sobre, con lo que ha pasado en `data`:

```json
{"id":"evt_6b0e3c1f2a9d4e7f8a1b2c3d4e5f6a7b","type":"sms.status","created":"2026-11-21T10:15:03.412+01:00","account_id":12345,"data":{…}}
```

PHP:

```php
<?php
require "mundosms.php";
$body = file_get_contents("php://input");   // tal cual llega
if (!MundoSMS::verifyWebhook("whsec_...", $_SERVER["HTTP_X_MUNDOSMS_SIGNATURE"] ?? "", $body)) {
    http_response_code(401);
    exit;
}
$event = json_decode($body, true);   // $event["type"], $event["data"]…
// ... guarda el aviso y procésalo después
http_response_code(204);
```

Python, con la biblioteca estándar (en Flask, el cuerpo crudo es `request.get_data()`; en Django, `request.body`):

```python
from http.server import BaseHTTPRequestHandler, HTTPServer
from mundosms import MundoSMS


class Webhooks(BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length") or 0))   # tal cual llega
        valid = MundoSMS.verify_webhook("whsec_...", self.headers.get("X-MundoSMS-Signature"), body)
        self.send_response(204 if valid else 401)
        self.end_headers()
        # si valid: guarda el aviso (json.loads(body)) y procésalo después


HTTPServer(("", 8080), Webhooks).serve_forever()
```

Node.js (en Express, con `express.raw({ type: "application/json" })` en esa ruta, el cuerpo crudo es `req.body`):

```js
const http = require("http");
const { MundoSMS } = require("./mundosms");

http.createServer((req, res) => {
  const chunks = [];
  req.on("data", (c) => chunks.push(c));
  req.on("end", () => {
    const body = Buffer.concat(chunks); // tal cual llega
    const valid = MundoSMS.verifyWebhook("whsec_...", req.headers["x-mundosms-signature"], body);
    res.writeHead(valid ? 204 : 401).end();
    // si valid: guarda el aviso (JSON.parse(body)) y procésalo después
  });
}).listen(8080);
```

Java, con el servidor HTTP del propio JDK (en Spring, recibe el cuerpo como `@RequestBody byte[]`):

```java
import com.sun.net.httpserver.HttpServer;
import es.mundosms.MundoSMS;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.InetSocketAddress;

public class Webhooks {
    public static void main(String[] args) throws Exception {
        HttpServer server = HttpServer.create(new InetSocketAddress(8080), 0);
        server.createContext("/", exchange -> {
            ByteArrayOutputStream body = new ByteArrayOutputStream();   // tal cual llega
            try (InputStream in = exchange.getRequestBody()) {
                byte[] b = new byte[8192];
                for (int n; (n = in.read(b)) != -1; ) body.write(b, 0, n);
            }
            boolean valid = MundoSMS.verifyWebhook("whsec_...", exchange.getRequestHeaders().getFirst("X-MundoSMS-Signature"), body.toByteArray());
            exchange.sendResponseHeaders(valid ? 204 : 401, -1);
            exchange.close();
            // si valid: guarda el aviso y procésalo después
        });
        server.start();
    }
}
```

C#, con ASP.NET Core:

```csharp
using MundoSMS;

var app = WebApplication.Create(args);
app.MapPost("/", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);   // tal cual llega
    if (!MundoSMSClient.VerifyWebhook("whsec_...", req.Headers["X-MundoSMS-Signature"], ms.ToArray()))
        return Results.Unauthorized();
    // guarda el aviso y procésalo después
    return Results.NoContent();
});
app.Run("http://0.0.0.0:8080");
```

Go, con `net/http`:

```go
package main

import (
	"io"
	"log"
	"net/http"

	mundosms "github.com/mundosms/mundosms-sdk/go"
)

func main() {
	http.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, 1<<20)) // tal cual llega
		if err != nil || !mundosms.VerifyWebhook("whsec_...", r.Header.Get("X-MundoSMS-Signature"), body) {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		// guarda el aviso (json.Unmarshal(body, …)) y procésalo después
		w.WriteHeader(http.StatusNoContent)
	})
	log.Fatal(http.ListenAndServe(":8080", nil))
}
```

Ruby, en un controlador de Rails (el cuerpo tal cual es `request.raw_post`, nunca `params`, que es el JSON ya leído y
vuelto a montar; con `ActionController::Base`, añade `skip_forgery_protection`):

```ruby
class AvisosController < ActionController::API
  def create
    cuerpo = request.raw_post   # tal cual llega
    return head(:unauthorized) unless MundoSMS.verify_webhook("whsec_...", request.headers["X-MundoSMS-Signature"].to_s, cuerpo)

    AvisoJob.perform_later(cuerpo)   # guarda el aviso y procésalo después
    head :no_content
  end
end
```

La referencia completa de cada ruta, sus parámetros y sus respuestas está en
**[www.mundosms.es/api-v3](https://www.mundosms.es/api-v3)**.

## Versiones

Cada versión lleva su etiqueta (`v1.2.0`…) y sus cambios en [CHANGELOG.md](CHANGELOG.md). Para no llevarte cambios sin
querer, descarga el fichero de una versión concreta. El módulo de Go lleva además su etiqueta, `go/v1.2.0`…, con la misma
versión: `go get github.com/mundosms/mundosms-sdk/go@v1.2.0`.

Con Composer (`composer require mundosms/sdk`) solo se instala lo de PHP (`php/`, `composer.json`, este README, la licencia
y los cambios). El zip del código de cada versión en GitHub («Source code») también lleva solo eso; con todos los clientes,
descarga el `mundosms-sdk-X.Y.Z.zip` adjunto a cada versión, desde la 1.1.0.

## Pruebas

Cada cliente lleva sus pruebas, sin red: un servidor de prueba local que comprueba la firma como la API (los ejemplos de
«Para comprobar tu código» y el del aviso de `whsec_ejemplo`), los reintentos, el plazo, la paginación, las redirecciones, un
`2xx` que no es JSON y el `DELETE` repetido. Se pasan en cada PR, con la versión más vieja que promete cada uno y con la de
hoy (`.github/workflows/ci.yml`):

```sh
php tests/php/test_mundosms.php
python3 tests/python/test_mundosms.py
node tests/node/test_mundosms.js
javac -encoding UTF-8 -d /tmp/c java/MundoSMS.java tests/java/MundoSMSTest.java && java -cp /tmp/c MundoSMSTest
dotnet run -c Release --project tests/dotnet/MundoSMS.Tests.csproj
cd go && go test ./...
cd ruby && ruby -w -Ilib test/test_mundosms.rb
```

## Licencia

[MIT](LICENSE).
