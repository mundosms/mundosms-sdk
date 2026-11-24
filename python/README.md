# mundosms-sdk

Cliente Python de la [API REST v3 de MundoSMS](https://www.mundosms.es/api-v3): centralita, llamadas, SMS, VozPush,
NumCheck y WhatsApp. Firma cada petición por ti (`X-Timestamp` en milisegundos y la firma HMAC de tu API Key), pone una
`Idempotency-Key` en cada `POST` para que un reintento no envíe nada dos veces, y reintenta solo lo que es seguro repetir.
Comprueba también la firma de los avisos que te mandamos. Sin dependencias: solo la biblioteca estándar.

Requiere Python 3.9 o superior.

```sh
pip install mundosms-sdk
```

Se importa como `mundosms`.

Las API Keys (token y clave privada) se crean en el panel de MundoSMS, en **API y Soporte → Claves de API**.

## Uso

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
    print(e.http, e.code, e.message, e.detail, e.request_id)
```

- `get(path, query)`, `post(path, data)`, `patch(path, data)` y `delete(path, query)` devuelven la respuesta entera
  (`status`, `message`, `data`). La ruta va sin `/v3`. En `get` y `delete` los filtros van en la query; en
  `post` y `patch`, los datos van en un cuerpo JSON. `request(method, path, data)` hace una petición cualquiera.
- `all(path, query)` recorre todas las páginas de un listado y te da los elementos uno a uno. Al acabar, el último
  `next_cursor` queda en `last_cursor`.
- Las rutas que devuelven un fichero (una grabación, una locución, el PDF de un certificado) vuelven como
  `{"status": "success", "content_type": …, "body": …}`, con el contenido en `bytes`.
- Cualquier error es una excepción `MundoSMSError` con `http`, `code`, `message`, `detail`, `data`, `request_id` y
  `response` (la respuesta entera), siempre que la API contesta con `"status": "error"`. Un WhatsApp sin confirmar (`202`,
  `data["state"]` `"unknown"`: puede haber salido) no es un error: vuelve la respuesta, y no se reenvía.
- Cada `POST` lleva una `Idempotency-Key`, la misma en todos sus intentos: por eso se reintenta sin miedo si se queda sin
  respuesta. La tuya (el id de tu pedido, por ejemplo): `api.post(path, data, idempotency_key="pedido-881")`. Si la API
  devuelve la respuesta guardada de la primera vez, trae `"idempotent_replayed": True`.
- `MundoSMS.verify_webhook(secret, header, body)` comprueba la cabecera `X-MundoSMS-Signature` de un aviso que te llega
  (de `callback_url` o un webhook de WhatsApp), con el cuerpo tal cual llegó (`bytes`): `True` o `False`.
- Opciones, con nombre: `MundoSMS(token, private_key, base_url=…, retries=3, timeout=60)`. Un valor que no vale es un
  `ValueError`.
- Una redirección no se sigue nunca (llevaría tu token y tu firma a otro servidor): es un `MundoSMSError`. Un 2xx que no es
  JSON (el HTML de un proxy), también.
- `api.last_rate_limit` es el cupo de la última respuesta (`X-RateLimit-*`): `{"limit": 10, "remaining": 7, "reset": 1}`.

## Documentación

La referencia completa de cada ruta, sus parámetros y sus respuestas: **[www.mundosms.es/api-v3](https://www.mundosms.es/api-v3)**.
El código, los clientes en PHP, Node.js, Java, C#, Go y Ruby y los cambios de cada versión:
[github.com/mundosms/mundosms-sdk](https://github.com/mundosms/mundosms-sdk).

## Licencia

MIT.
