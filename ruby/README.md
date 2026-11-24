# mundosms

Cliente Ruby de la [API REST v3 de MundoSMS](https://www.mundosms.es/api-v3): centralita, llamadas, SMS, VozPush, NumCheck y
WhatsApp. Firma cada petición por ti (`X-Timestamp` en milisegundos y la firma HMAC de tu API Key), pone una
`Idempotency-Key` en cada `POST` para que un reintento no envíe nada dos veces, y reintenta solo lo que es seguro repetir.
Comprueba también la firma de los avisos que te mandamos. Un solo fichero, sin dependencias: solo la biblioteca estándar.

Requiere Ruby 3.1 o superior.

```sh
gem install mundosms
```

(o `gem "mundosms"` en tu `Gemfile`). Las API Keys (token y clave privada) se crean en el panel de MundoSMS, en **API y
Soporte → Claves de API**.

## Uso

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
  puts [e.http, e.code, e.message, e.detail, e.request_id].join(" ")
end
```

- `get(path, query)`, `post(path, data)`, `patch(path, data)` y `delete(path, query)` devuelven la respuesta entera, un
  `Hash` con claves de texto. La ruta va sin `/v3`. `request(method, path, data)` hace una petición cualquiera.
- `all(path, query)` recorre todas las páginas de un listado (un `Enumerator::Lazy`: pide cada página al llegar a ella).
  Al acabar, el último `next_cursor` queda en `last_cursor`; `last_rate_limit` es el cupo (`X-RateLimit-*`) de la última
  respuesta.
- Cualquier error es un `MundoSMS::Error` con `http`, `code`, `message`, `detail`, `data`, `request_id` y `response`.
  Un WhatsApp sin confirmar (`202`, `data["state"]` `"unknown"`: puede haber salido) no es un error: vuelve la respuesta.
- Cada `POST` lleva una `Idempotency-Key`, la misma en todos sus intentos; la tuya, con
  `api.post(path, data, idempotency_key: "pedido-881")`.
- Una redirección no se sigue nunca (llevaría tu firma a otro servidor): es un `MundoSMS::Error`.
- `MundoSMS.verify_webhook(secret, header, body)` comprueba la cabecera `X-MundoSMS-Signature` de un aviso que te llega, con
  el cuerpo tal cual llegó (en Rails, `request.raw_post`, nunca `params`): `true` o `false`.
- Opciones, con nombre: `MundoSMS::Client.new(token, private_key, base_url: …, retries: 3, timeout: 60)`.

## Documentación

La referencia completa de cada ruta, sus parámetros y sus respuestas: **[www.mundosms.es/api-v3](https://www.mundosms.es/api-v3)**.
El código, los clientes en PHP, Python, Node.js, Java, C# y Go, y los cambios de cada versión:
[github.com/mundosms/mundosms-sdk](https://github.com/mundosms/mundosms-sdk).

## Licencia

[MIT](https://github.com/mundosms/mundosms-sdk/blob/main/LICENSE).
