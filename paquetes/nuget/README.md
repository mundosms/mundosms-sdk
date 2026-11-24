# MundoSMS

Cliente .NET (C#) de la [API REST v3 de MundoSMS](https://www.mundosms.es/api-v3): centralita, llamadas, SMS, VozPush,
NumCheck y WhatsApp. Firma cada petición por ti (`X-Timestamp` en milisegundos y la firma HMAC de tu API Key), pone una
`Idempotency-Key` en cada `POST` para que un reintento no envíe nada dos veces, y reintenta solo lo que es seguro repetir.
Comprueba también la firma de los avisos que te mandamos. Sin dependencias.

Para .NET Framework 4.6.1 o superior, .NET Core 2.0 o superior y .NET 5 o superior.

```sh
dotnet add package MundoSMS
```

Las API Keys (token y clave privada) se crean en el panel de MundoSMS, en **API y Soporte → Claves de API**.

## Uso

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

- `GetAsync`, `PostAsync`, `PatchAsync`, `DeleteAsync` y `RequestAsync` (con un `CancellationToken` opcional), y sus
  versiones síncronas `Get`, `Post`, `Patch`, `Delete` y `Request`. Devuelven la respuesta entera, un `MundoSMSResponse`
  (`r.Data` es su `data`). La ruta va sin `/v3`.
- Los datos y los filtros van en un objeto anónimo (`new { to = "…" }`), en un `Dictionary<string, object>` o en tu propia
  clase: sus propiedades salen con el nombre de `[JsonPropertyName]`, `[JsonProperty]` o `[DataMember(Name)]` y, sin
  atributo, en snake_case (`ClientId` → `client_id`); `[JsonIgnore]` no se manda.
- `All` (y `AllAsync`, con `await foreach`, en .NET 6 o superior) recorre todas las páginas de un listado y te da los
  elementos uno a uno. Al acabar, el último `next_cursor` queda en `LastCursor`. `LastRateLimit` es el cupo
  (`X-RateLimit-*`) de la última respuesta.
- Cualquier error es una `MundoSMSException` con `Http`, `StatusCode`, `Message`, `Detail`, `Data`, `RequestId` y
  `Response` (la respuesta entera). Un WhatsApp sin confirmar (`202`, `data.state` `"unknown"`: puede haber salido) no es un
  error: vuelve la respuesta, y no se reenvía.
- Cada `POST` lleva una `Idempotency-Key`, la misma en todos sus intentos. La tuya (el id de tu pedido, por ejemplo):
  `api.PostAsync(path, data, new MundoSMSRequestOptions { IdempotencyKey = "pedido-881" })`.
- Las redirecciones no se siguen nunca: la API no las usa, y una redirección llevaría tu firma y tus datos a otro servidor.
- `MundoSMSClient.VerifyWebhook(secret, header, body)` comprueba la cabecera `X-MundoSMS-Signature` de un aviso que te llega,
  con el cuerpo tal cual llegó (los bytes): `true` o `false`.
- Opciones, como propiedades: `BaseUrl`, `Retries` (3) y `Timeout` (60 s, un `TimeSpan`).

### Con `IHttpClientFactory`

Pasa tu `HttpClient` como tercer argumento. Su manejador no puede seguir redirecciones (si las sigue, el constructor da
`ArgumentException`):

```csharp
services.AddHttpClient("mundosms")
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
        .RedactLoggedHeaders(new[] { "Authorization", "X-Signature" });   // en .NET 6 y 7, que no salgan en los registros

var api = new MundoSMSClient("TU_TOKEN", "TU_CLAVE_PRIVADA", factory.CreateClient("mundosms"));
```

## Documentación

La referencia completa de cada ruta, sus parámetros y sus respuestas: **[www.mundosms.es/api-v3](https://www.mundosms.es/api-v3)**.
El código, los clientes en PHP, Python, Node.js, Java, Go y Ruby, y los cambios de cada versión:
[github.com/mundosms/mundosms-sdk](https://github.com/mundosms/mundosms-sdk).

## Licencia

[MIT](https://github.com/mundosms/mundosms-sdk/blob/main/LICENSE).
