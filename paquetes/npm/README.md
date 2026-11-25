# @mundosms/sdk

Cliente Node.js de la [API REST v3 de MundoSMS](https://www.mundosms.es/api-v3): centralita, llamadas, SMS, VozPush,
NumCheck y WhatsApp. Firma cada petición por ti (`X-Timestamp` en milisegundos y la firma HMAC de tu API Key), pone una
`Idempotency-Key` en cada `POST` para que un reintento no envíe nada dos veces, y reintenta solo lo que es seguro repetir.
Comprueba también la firma de los avisos que te mandamos. Sin dependencias. Con tipos de TypeScript.

Requiere Node.js 18 o superior.

```sh
npm install @mundosms/sdk
```

Las API Keys (token y clave privada) se crean en el panel de MundoSMS, en **API y Soporte → Claves de API**.

## Uso

```js
const { MundoSMS, MundoSMSError } = require("@mundosms/sdk");
// o, en un proyecto ESM: import { MundoSMS, MundoSMSError } from "@mundosms/sdk";

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
    console.log(e.http, e.code, e.message, e.detail, e.requestId);
  }
}

main().catch((e) => { console.error(e); process.exitCode = 1; });
```

- `get(path, query)`, `post(path, data)`, `patch(path, data)` y `delete(path, query)` devuelven la respuesta entera
  (`status`, `message`, `data`). La ruta va sin `/v3`. En `get` y `delete` los filtros van en la query; en
  `post` y `patch`, los datos van en un cuerpo JSON. `request(method, path, data, options)` hace una petición cualquiera.
- `all(path, query)` recorre todas las páginas de un listado y te da los elementos uno a uno (`for await`). Al acabar,
  el último `next_cursor` queda en `lastCursor`.
- Las rutas que devuelven un fichero (una grabación, una locución, el PDF de un certificado) vuelven como
  `{ status: "success", content_type, body }`, con el contenido en un `Buffer`.
- Cualquier error es una excepción `MundoSMSError` con `http`, `code`, `message`, `detail`, `data`, `requestId`,
  `idempotencyKey` (la `Idempotency-Key` que llevó la petición, en un `POST`) y `response` (la respuesta entera), siempre
  que la API contesta con `"status": "error"`. Un WhatsApp sin confirmar (`202`, `data.state` `"unknown"`: puede haber
  salido) no es un error: vuelve la respuesta, y no se reenvía.
- Cada `POST` lleva una `Idempotency-Key`, la misma en todos sus intentos: por eso se reintenta sin miedo si se queda sin
  respuesta. La tuya (el id de tu pedido, por ejemplo), en el tercer argumento: `api.post(path, data, { idempotencyKey:
  "pedido-881" })`. Si la API devuelve la respuesta guardada de la primera vez, trae `idempotent_replayed: true`.
- Un `409` con `code` `request_in_progress` (la primera petición con esa clave aún se está haciendo) se repite con la misma
  clave durante hasta 180 s, sin contar en `retries`. Si sigue en marcha, el error trae la clave en `idempotencyKey`:
  repítelo más tarde con ella.
- `MundoSMS.verifyWebhook(secret, header, body)` comprueba la cabecera `X-MundoSMS-Signature` de un aviso que te llega (de
  `callback_url` o un webhook de WhatsApp), con el cuerpo tal cual llegó (un `Buffer`): `true` o `false`.
- Opciones, en el tercer argumento del constructor: `{ baseUrl, retries, timeout }` (por defecto
  `https://api.mundosms.es/v3`, `3` y `60` s). Una opción que no existe, o un valor que no vale, da un `TypeError`.
- Una redirección no se sigue nunca (llevaría tu firma y tus datos a otro servidor): es un `MundoSMSError`. Un 2xx que no es
  JSON (el HTML de un proxy), también.
- `api.lastRateLimit` es el cupo de la última respuesta (`X-RateLimit-*`): `{ limit: 10, remaining: 7, reset: 1 }`.
- El token y la clave privada van en campos privados: no salen en un `console.log(api)`.

### TypeScript

```ts
import { MundoSMS, type FileResponse } from "@mundosms/sdk";

const api = new MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA");
const account = await api.get<{ balance: number; available: number }>("/account");
console.log(account.data?.available);
const recording = await api.get<FileResponse>("/calls/out/8812345/recording"); // recording.body es un Buffer
```

El tipo que pasas a `get`, `post`, `patch`, `delete` y `request` es el de `data`; con `FileResponse`, el de un fichero. En
`all`, el de cada elemento. Los tipos usan los de Node (`Buffer`): en tu proyecto, instala `@types/node`.

## Documentación

La referencia completa de cada ruta, sus parámetros y sus respuestas: **[www.mundosms.es/api-v3](https://www.mundosms.es/api-v3)**.
El código, los clientes en PHP, Python, Java, C#, Go y Ruby y los cambios de cada versión:
[github.com/mundosms/mundosms-sdk](https://github.com/mundosms/mundosms-sdk).

## Licencia

MIT.
