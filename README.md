# SDK de la API REST de MundoSMS

Clientes de la [API REST v3 de MundoSMS](https://www.mundosms.es/api-v3) (centralita, llamadas, SMS, VozPush, NumCheck y
WhatsApp) en **PHP, Python y Node.js**. Cada uno es **un solo fichero, sin dependencias**: lo copias en tu proyecto y ya.
Firman cada petición por ti (`X-Timestamp` en milisegundos y la firma HMAC de tu API Key) y reintentan solo lo que es
seguro repetir.

| Lenguaje | Fichero | Requisitos |
|---|---|---|
| PHP | [`php/mundosms.php`](php/mundosms.php) | PHP 7.4 o superior, con la extensión curl |
| Python | [`python/mundosms.py`](python/mundosms.py) | Python 3.8 o superior |
| Node.js | [`node/mundosms.js`](node/mundosms.js) | Node.js 18 o superior |

Las API Keys (token y clave privada) se crean en el panel de MundoSMS, en **API y Soporte → Claves de API**.

## PHP

```php
require "mundosms.php";

$api = new MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA");   // la clave privada, "" si tu API Key no exige firma
$r = $api->post("/sms", ["to" => "34600123456", "text" => "Tu cita es mañana a las 10:00", "from" => "MiClinica"]);
echo $r["data"]["id"];

foreach ($api->todos("/calls", ["from_datetime" => "2026-11-01"]) as $llamada) {
    echo $llamada["type"], " ", $llamada["id"], " ", $llamada["state"], "\n";
}
```

## Python

```python
from mundosms import MundoSMS, MundoSMSError

api = MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA")
r = api.post("/sms", {"to": "34600123456", "text": "Tu cita es mañana a las 10:00", "from": "MiClinica"})
print(r["data"]["id"])

for llamada in api.todos("/calls", {"from_datetime": "2026-11-01"}):
    print(llamada["type"], llamada["id"], llamada["state"])
```

## Node.js

```js
const { MundoSMS, MundoSMSError } = require("./mundosms");

const api = new MundoSMS("TU_TOKEN", "TU_CLAVE_PRIVADA");
const r = await api.post("/sms", { to: "34600123456", text: "Tu cita es mañana a las 10:00", from: "MiClinica" });
console.log(r.data.id);

for await (const llamada of api.todos("/calls", { from_datetime: "2026-11-01" })) {
  console.log(llamada.type, llamada.id, llamada.state);
}
```

## Qué hace cada método

- `get(ruta, filtros)`, `post(ruta, datos)`, `patch(ruta, datos)` y `delete(ruta, filtros)`, con la ruta sin `/v3`
  (`"/calls"`, `"/numbers/34910123456"`). Devuelven la respuesta entera (`status`, `message`, `data`).
- `todos(ruta, filtros)` recorre todas las páginas de un listado y te da los elementos uno a uno.
- Un error es una excepción `MundoSMSError`, con el código HTTP, el `status_code` de la API, el mensaje y `detail`.
- **Reintentos:** un `429` (no se hizo nada) y, en `GET`, los `5xx` y los fallos de red se reintentan solos, con espera
  creciente. Un `POST` o un `PATCH` que falla por la red **no** se repite, porque no se sabe si llegó: usa `client_id` y
  consulta antes de volver a mandarlo.

La referencia completa de cada ruta, sus parámetros y sus respuestas está en
**[www.mundosms.es/api-v3](https://www.mundosms.es/api-v3)**.

## Versiones

Cada versión lleva su etiqueta (`v1.0.0`…) y sus cambios en [CHANGELOG.md](CHANGELOG.md). Para no llevarte cambios sin
querer, descarga el fichero de una versión concreta.

## Licencia

[MIT](LICENSE).
