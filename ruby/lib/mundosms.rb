# frozen_string_literal: true

# MundoSMS API v3 — cliente Ruby en un solo fichero (Ruby 3.1 o superior). Sin dependencias: solo la biblioteca estándar.
# Documentación: https://www.mundosms.es/api-v3
# Código y versiones: https://github.com/mundosms/mundosms-sdk (licencia MIT)
#
#   require "mundosms"
#   api = MundoSMS::Client.new("TOKEN", "CLAVE_PRIVADA")   # la clave privada, "" si tu API Key no exige firma
#   r = api.post("/sms", { "to" => "34600123456", "text" => "Hola", "from" => "MiEmpresa" })
#   puts r["data"]["id"]
#   api.all("/calls", { "from_datetime" => "2026-11-01", "stop_at_ongoing" => true }).each { |call| ... }
#
# Errores: excepción MundoSMS::Error con #http (código HTTP), #code (la palabra de la API: "not_found", "invalid_parameter"…),
# #message, #detail, #data, #response (la respuesta entera), #request_id (la cabecera X-Request-Id) e #idempotency_key (la
# Idempotency-Key que llevó la petición: la de un POST, para repetirlo tú con la misma). También cuando la API contesta con "status": "error",
# venga con el código HTTP que venga. Un WhatsApp sin confirmar (202) no es un error: vuelve la respuesta, con
# data["state"] "unknown". Lo que no se puede mandar (una Idempotency-Key mal formada, un cuerpo que no es UTF-8) es un
# ArgumentError, y no se manda nada.
# Cada POST lleva una Idempotency-Key (un UUID nuevo en cada llamada, la misma en todos sus intentos; la tuya, con
# api.post(path, data, idempotency_key: "pedido-881")). Por eso un POST que falla por la red, o con 409
# request_in_progress, se repite sin miedo: la API no lo hace dos veces. Si la respuesta es la guardada de la primera vez, trae
# "idempotent_replayed" => true.
# Un 409 request_in_progress (la primera petición con esa clave aún se está haciendo: un WhatsApp con un fichero puede tardar
# 2 o 3 minutos) se repite con la misma clave hasta IN_PROGRESS_WAIT (180 s) desde el primero, sin contar en retries: espera
# lo que diga Retry-After (al menos 1 s) o 1, 2, 4, 8 y luego 15 s. Si Retry-After pide más de lo que queda, o se acaban los
# 180 s, es un MundoSMS::Error con su #idempotency_key: repítelo más tarde con ella. Con retries: 0, no se repite.
# Reintenta: un 429, en cualquier método (no se hizo nada); un fallo de red, en cualquier método; un 5xx, solo en GET (un
# POST con 5xx no se repite: un 500 puede llegar con la operación hecha, y la API lo guarda con la Idempotency-Key, así que
# repetirlo con ella devolvería el mismo 500; un 503 no hizo nada, y puedes repetirlo tú con la misma clave). Espera lo que
# diga Retry-After (como mucho 60 s) o, si no lo trae, 1, 2, 4… s. Un DELETE que se repite tras quedarse sin respuesta y da 404 ya estaba hecho:
# vuelve como éxito, con data["changed"] false.
# Una redirección (3xx) no se sigue nunca: es un MundoSMS::Error (la API no redirige). Un 2xx que no es JSON (el HTML de un
# proxy o de un portal cautivo) también es un error; los ficheros que da la API (audio, imagen, PDF) vuelven por su
# Content-Type.
# Los avisos que te mandamos (callback_url y webhooks de WhatsApp) se comprueban con MundoSMS.verify_webhook.

require "json"
require "net/http"
require "openssl"
require "securerandom"
require "time"
require "timeout"
require "uri"

module MundoSMS
  VERSION = "2.1.0"
  DEFAULT_BASE_URL = "https://api.mundosms.es/v3"
  MAX_RETRY_WAIT = 60      # segundos: lo más que se espera antes de un reintento, aunque Retry-After pida más
  IN_PROGRESS_WAIT = 180   # segundos: lo más que se espera, con la misma Idempotency-Key, a un POST que sigue en marcha (409 request_in_progress)
  IN_PROGRESS_STEP = 15    # segundos: lo más que se espera entre dos intentos de ese POST si la API no manda Retry-After
  WEBHOOK_TOLERANCE = 300  # segundos: lo más que puede alejarse de tu hora la «t» de la firma de un aviso
  IDEMPOTENCY_KEY = /\A[A-Za-z0-9_.:-]{1,64}\z/.freeze   # la forma que exige la API para la Idempotency-Key
  FILE_TYPES = %r{\A(audio/|image/|application/pdf\z|application/octet-stream\z)}.freeze # lo que la API da como fichero

  # El error de cualquier petición: un 4xx o un 5xx, una respuesta 2xx con "status": "error" o, sin respuesta (la red o el
  # plazo agotado), http 0 y un mensaje que empieza por «No response from the API:».
  class Error < StandardError
    attr_reader :http,         # el código HTTP; 0 si no hubo respuesta
                :code,         # el code de la API ("not_found", "invalid_parameter"…); nil si la respuesta no lo trae
                :detail,       # el detail de la respuesta, si lo trae
                :data,         # el data de la respuesta (en WhatsApp, el id del mensaje que puede haber salido)
                :response,     # la respuesta entera, decodificada; nil si no era JSON
                :request_id,   # la cabecera X-Request-Id: con ella encuentras la petición en Logs API
                :idempotency_key # la Idempotency-Key que llevó la petición (la de un POST); nil si no llevó

    def initialize(message, http: 0, code: nil, detail: nil, data: nil, response: nil, request_id: nil, idempotency_key: nil)
      super(message)
      @http = http
      @code = code
      @detail = detail
      @data = data
      @response = response
      @request_id = request_id
      @idempotency_key = idempotency_key
    end
  end

  @timestamp_lock = Mutex.new
  @last_timestamp = 0

  # El X-Timestamp de una petición: milisegundos, y siempre mayor que el de la anterior de este proceso (también desde
  # varios hilos). Dos peticiones iguales en el mismo milisegundo (un reintento tras un 429 con Retry-After: 0, o dos a la
  # vez) firmarían igual, y la API rechazaría la segunda por repetida («409 duplicated_request»).
  def self.timestamp
    @timestamp_lock.synchronize do
      @last_timestamp = [Process.clock_gettime(Process::CLOCK_REALTIME, :millisecond), @last_timestamp + 1].max
      @last_timestamp.to_s
    end
  end

  # true si un aviso que te llega (callback_url o webhook de WhatsApp) es nuestro y nadie lo ha tocado.
  # secret: el de «API y Soporte → Webhooks» (whsec_…); header: X-MundoSMS-Signature; body: el cuerpo TAL CUAL llegó (en
  # Rails, request.raw_post; en Rack, request.body.read), sin volver a serializar el JSON; tolerance: los segundos que puede
  # alejarse «t» de tu hora. Una cabecera que falta o está mal formada da false, sin excepción.
  def self.verify_webhook(secret, header, body, tolerance: WEBHOOK_TOLERANCE)
    raise ArgumentError, "MundoSMS.verify_webhook: pass the raw body as received (a String), not a decoded value" unless body.is_a?(String)

    secret = secret.is_a?(String) ? secret.strip : ""
    return false if secret.empty? || !header.is_a?(String)

    t = nil
    signatures = []
    header.split(",").each do |part|
      k, equals, v = part.partition("=")
      next if equals.empty?

      k = k.strip
      v = v.strip
      if k == "t"
        return false unless t.nil? # dos «t»: no se sabe cuál vale

        t = v
      elsif k == "v1"
        signatures << v.downcase
      end
    end
    return false if t.nil? || !t.match?(/\A[0-9]{1,15}\z/) || signatures.empty?
    return false if (Time.now.to_i - Integer(t, 10)).abs > tolerance

    expected = OpenSSL::HMAC.hexdigest("SHA256", secret, "#{t}.".b + body.b)
    valid = false
    signatures.each do |sig| # todas, sin cortar en la primera: fixed_length_secure_compare es de tiempo constante
      valid = true if sig.match?(/\A[0-9a-f]{64}\z/) && OpenSSL.fixed_length_secure_compare(expected, sig)
    end
    valid
  end

  # El cliente de la API. Créalo una vez y reutilízalo: se puede usar desde varios hilos a la vez.
  class Client
    # El cursor del último listado recorrido con #all: guárdalo para la siguiente sincronización. Al empezar el recorrido, el
    # cursor que le pasas (o nil); después, el último next_cursor que llegó (una página sin cursor no lo borra).
    attr_reader :last_cursor
    # El cupo que dijo la última respuesta (sus cabeceras X-RateLimit-*): { "limit" => 10, "remaining" => 7, "reset" => 1 }, o
    # nil si no lo traía (o no hubo respuesta).
    attr_reader :last_rate_limit
    attr_reader :base_url, :retries, :timeout

    # token: el Token de tu API Key (empieza por eyJ); private_key: su clave privada si exige firma (lo normal), o "".
    # base_url: la dirección de la API; retries: cuántas veces se reintenta lo que es seguro repetir (0, ninguna); timeout:
    # los segundos que puede durar cada petición entera (conectar, mandar y recibir la respuesta), con decimales si quieres.
    # Una opción que no existe es un ArgumentError (unknown keyword).
    def initialize(token, private_key = "", base_url: DEFAULT_BASE_URL, retries: 3, timeout: 60)
      uri = URI.parse(base_url.to_s.sub(%r{/+\z}, ""))
      unless uri.is_a?(URI::HTTP) && uri.host && !uri.host.empty? && uri.query.nil? && uri.fragment.nil?
        raise ArgumentError, "MundoSMS: base_url must be an http(s) address without query, like #{DEFAULT_BASE_URL}"
      end
      raise ArgumentError, "MundoSMS: retries must be an integer, 0 or more" unless retries.is_a?(Integer) && retries >= 0
      raise ArgumentError, "MundoSMS: timeout must be a number of seconds greater than 0" unless timeout.is_a?(Numeric) && timeout.positive?

      @token = token.to_s.strip
      @private_key = private_key.to_s.strip
      @base_url = base_url.to_s.sub(%r{/+\z}, "")
      @uri = uri
      @base_path = uri.path.to_s
      @retries = retries
      @timeout = timeout
      @last_cursor = nil
      @last_rate_limit = nil
    end

    # Lo que enseñan p, inspect, to_s y pp (que usa este inspect): sin el token ni la clave privada, para que no acaben en un log.
    def inspect
      "#<MundoSMS::Client base_url=#{@base_url.inspect}>"
    end
    alias to_s inspect

    # GET: query va en la query.
    def get(path, query = {})
      request("GET", path, query)
    end

    # POST: data va en un cuerpo JSON. idempotency_key: tu clave para que repetir este POST no lo vuelva a hacer (por ejemplo,
    # el id de tu pedido); sin ella, un UUID nuevo en cada llamada.
    def post(path, data = {}, idempotency_key: nil)
      request("POST", path, data, idempotency_key: idempotency_key)
    end

    # PATCH: data va en un cuerpo JSON.
    def patch(path, data)
      request("PATCH", path, data)
    end

    # DELETE: query va en la query.
    def delete(path, query = {})
      request("DELETE", path, query)
    end

    # Recorre un listado entero, página a página, siguiendo su cursor (next_cursor), elemento a elemento. Cada página lleva
    # los mismos filtros (y el mismo order) más el cursor, y se pide al llegar a ella. Con un bloque, lo llama con cada
    # elemento; sin él, devuelve un Enumerator::Lazy (api.all("/calls").first(10) pide solo las páginas que hacen falta). Al
    # acabar, el último next_cursor queda en #last_cursor: guárdalo para la siguiente sincronización. Es uno por cliente: para
    # dos recorridos a la vez, un cliente para cada uno. #last_cursor avanza cuando ya te ha dado todos los elementos de una
    # página: si tu código falla a mitad de una, sigue en la anterior.
    # Termina con has_more false (con stop_at_ongoing, también cuando la página se para en una llamada en curso), o si el
    # cursor no avanza. Una página vacía con has_more true (con stop_at_ongoing y un filtro, cuando ha mirado muchas que no
    # casan) no es el final: sigue.
    # Una lista que no va por páginas (GET /calls/missed y /calls/live: has_more sin next_cursor) no se puede recorrer entera:
    # es un MundoSMS::Error, antes de dar nada. Sube limit, o filtra, y pídela con #get.
    def all(path, query = {}, &block)
      enum = Enumerator.new do |y|
        q = (query || {}).to_h { |k, v| [k.to_s, v] }
        @last_cursor = truthy(q["cursor"]) ? q["cursor"] : nil # el de este recorrido: al empezar, el que le pasas
        list_key = nil
        loop do
          r = get(path, q)
          data = r["data"]
          data = {} unless data.is_a?(Hash)
          cursor = data["next_cursor"]
          if truthy(data["has_more"]) && truthy(data["results"]) && !truthy(cursor)
            message = "MundoSMS: all() cannot go through #{path}: it has more (has_more) but no next_cursor, so the list is not " \
                      "paginated: raise limit or narrow the filters, and read it with get"
            raise Error.new(message, http: 200, data: data, response: r)
          end
          list_key ||= data.keys.find { |k| data[k].is_a?(Array) } # la lista es el primer campo que es una lista
          (list_key && data[list_key].is_a?(Array) ? data[list_key] : []).each { |item| y << item }
          @last_cursor = cursor if truthy(cursor)
          break if !truthy(data["has_more"]) || !truthy(cursor) || cursor == q["cursor"] || truthy(data["ongoing_call"])

          q["cursor"] = cursor
        end
      end.lazy
      return enum unless block

      enum.each(&block)
      nil
    end

    # Una petición cualquiera. path: "/calls", "/numbers/34910123456"… (sin /v3: si lo lleva, se quita); cada trozo se
    # codifica solo y un %XX que ya escribas se respeta. Si la ruta trae "?…", esos parámetros se juntan con data (gana
    # data). En GET y DELETE, data (un Hash) va en la query; en POST y PATCH, en el cuerpo JSON. Devuelve la respuesta
    # decodificada (un Hash con claves de texto). idempotency_key: la Idempotency-Key (en un POST, sin ella, un UUID nuevo).
    def request(method, path, data = nil, idempotency_key: nil)
      method = method.to_s.upcase
      idem_key = idempotency_key_for(idempotency_key, method) # antes de nada: con una clave mala no se manda nada
      encoded_path, path_query = split_path(path, @base_path.end_with?("/v3"))
      body = ""
      if %w[GET DELETE].include?(method)
        raise ArgumentError, "MundoSMS: in GET and DELETE the query must be a Hash (nothing was sent)" unless data.nil? || data.is_a?(Hash)

        (data || {}).each { |k, v| path_query[k.to_s] = v }
      elsif !blank?(data)
        body = json_body(data)
      end
      query = path_query.filter_map { |k, v| (t = query_value(v)).nil? ? nil : "#{rfc3986(k)}=#{rfc3986(t)}" }.join("&")
      rest = encoded_path + (query.empty? ? "" : "?#{query}")
      signed_path = @base_path + rest # lo que se firma es justo lo que se manda
      attempt = 0
      after_network = false # un intento de antes se quedó sin respuesta (o llegó dos veces): puede que se hiciera
      in_progress = [] # cuándo llegó cada 409 request_in_progress de esta llamada (reloj monotónico)
      loop do
        attempt += 1
        status, headers, resp, network_error = send_once(method, rest, signed_path, body, idem_key)
        redirect = network_error.nil? && status.between?(300, 399) # de una redirección no se lee nada
        j = nil
        unless network_error || redirect
          begin
            j = JSON.parse(resp)
          rescue JSON::ParserError, EncodingError
            j = nil
          end
          j = nil unless j.is_a?(Hash)
        end
        if @retries.positive? && in_progress?(method, status, j, idem_key)
          in_progress << Process.clock_gettime(Process::CLOCK_MONOTONIC)
          wait = in_progress_wait(headers["retry-after"], in_progress.size, IN_PROGRESS_WAIT - (in_progress[-1] - in_progress[0]))
          unless wait.nil? # la primera con esta clave sigue en marcha: se espera y se repite, sin gastar retries
            attempt -= 1
            sleep(wait)
            next
          end
        elsif retryable?(method, !network_error.nil?, status, j, idem_key) && attempt <= @retries
          after_network ||= !network_error.nil? || duplicated?(status, j)
          sleep(retry_wait(headers["retry-after"], attempt))
          next
        end
        if network_error
          @last_rate_limit = nil
          raise Error.new("No response from the API: #{network_error}", idempotency_key: idem_key)
        end

        @last_rate_limit = rate_limit(headers)
        rid = headers["x-request-id"]
        if redirect # no se sigue: llevaría el token y la firma a otro servidor
          where = headers["location"].to_s.strip.empty? ? "" : " to #{headers['location'].strip[0, 300]}"
          message = "HTTP #{status}: redirect#{where} not followed (the API does not redirect: check base_url)"
          raise Error.new(message, http: status, request_id: rid, idempotency_key: idem_key)
        end
        replayed = headers["idempotent-replayed"].to_s.strip.downcase == "true"
        j["idempotent_replayed"] = true if replayed && j.is_a?(Hash) # la respuesta guardada de la primera vez
        if status.between?(200, 299)
          unless j.is_a?(Hash) # un fichero, o algo que no es de la API
            type = headers["content-type"].to_s.split(";").first.to_s.strip
            unless resp.empty? || type.downcase.match?(FILE_TYPES)
              message = "HTTP #{status}: the response is not JSON (#{type.empty? ? 'no Content-Type' : type})"
              raise Error.new(message, http: status, request_id: rid, idempotency_key: idem_key)
            end
            file = { "status" => "success", "content_type" => headers["content-type"].to_s, "body" => resp }
            file["idempotent_replayed"] = true if replayed
            return file
          end
          return j unless j["status"] == "error"
        end
        if method == "DELETE" && after_network && status == 404 && j.is_a?(Hash) && api_code(j) != "route_not_found"
          # lo borró el intento que se quedó sin respuesta
          return { "status" => "success", "message" => "Already deleted", "data" => { "state" => "deleted", "changed" => false } }
        end
        if j.is_a?(Hash)
          message = j["message"].is_a?(String) ? j["message"] : "Error"
          raise Error.new(message, http: status, code: api_code(j), detail: text(j["detail"]), data: j["data"],
                                   response: j, request_id: rid, idempotency_key: idem_key)
        end
        raise Error.new("HTTP #{status}", http: status, request_id: rid, idempotency_key: idem_key)
      end
    end

    private

    # Un intento: [código HTTP, cabeceras (en minúsculas), cuerpo, nil] o, sin respuesta entera (la red, el plazo), [0, {}, "",
    # el motivo]. Las redirecciones no se siguen: la API no las usa, y la firma no valdría.
    def send_once(method, rest, signed_path, body, idem_key)
      ts = MundoSMS.timestamp # un reintento inmediato, o dos peticiones iguales a la vez, no firman igual
      req = Net::HTTPGenericRequest.new(method, !body.empty?, true, @uri.path.to_s + rest)
      req["Authorization"] = "Bearer #{@token}"
      req["X-Timestamp"] = ts
      req["Accept"] = "application/json"
      req["User-Agent"] = "MundoSMS-Ruby/#{VERSION}"
      req["Idempotency-Key"] = idem_key if idem_key # la misma en todos los intentos de esta llamada
      unless body.empty?
        req["Content-Type"] = "application/json"
        req.body = body
      end
      unless @private_key.empty?
        signed = "#{@token}.#{ts}.#{method} #{signed_path}.".b + body.b
        req["X-Signature"] = [OpenSSL::HMAC.digest("SHA256", @private_key, signed)].pack("m0").tr("+/", "-_").delete("=")
      end
      res = nil
      Timeout.timeout(@timeout, TimeoutReached) do # el plazo de la petición ENTERA: también una respuesta que llega poco a poco
        http = Net::HTTP.new(@uri.host, @uri.port)
        http.use_ssl = @uri.scheme == "https"
        http.open_timeout = http.read_timeout = http.write_timeout = @timeout
        http.max_retries = 0 # que no repita la petición por su cuenta, con la misma firma: si se corta, la repite el SDK
        http.start { |h| res = h.request(req) }
      end
      headers = {}
      res.each_header { |k, v| headers[k.downcase] = v }
      [res.code.to_i, headers, res.body.to_s, nil]
    rescue TimeoutReached, Net::OpenTimeout, Net::ReadTimeout, Net::WriteTimeout
      [0, {}, "", "timed out after #{format_seconds(@timeout)} s"]
    rescue StandardError => e
      raise if e.is_a?(ArgumentError) || e.is_a?(Error)

      [0, {}, "", e.message.to_s.empty? ? e.class.name : e.message]
    end

    # El plazo agotado (lo lanza Timeout dentro de la petición, sin que lo vea nadie de fuera).
    class TimeoutReached < StandardError; end

    def format_seconds(s)
      s == s.to_i ? s.to_i.to_s : s.to_s
    end

    # La Idempotency-Key de la llamada: la tuya, si tiene la forma que exige la API; en un POST sin ella, un UUID v4 nuevo.
    def idempotency_key_for(key, method)
      return (method == "POST" ? SecureRandom.uuid : nil) if key.nil?
      unless key.is_a?(String) && key.match?(IDEMPOTENCY_KEY)
        raise ArgumentError, "MundoSMS: idempotency_key must be 1 to 64 characters: A-Z, a-z, 0-9 and _ . : - (nothing was sent)"
      end

      key
    end

    # Si se puede repetir (hasta retries veces): un 429 en cualquier método; un GET, ante un fallo de red o un 5xx; un PATCH,
    # un DELETE o un POST con su Idempotency-Key, ante un fallo de red. Un 5xx de un POST no: un 500 puede haberse hecho y la
    # API lo guarda con la clave (repetirlo devolvería el mismo 500), y un 503 no hizo nada, pero repetirlo con la misma
    # clave se deja a tu código. Un 409 duplicated_request (la MISMA petición firmada llegó dos veces: la repitió algo entre
    # medias) es un fallo de red más. El 409 request_in_progress va aparte (in_progress?): se espera por tiempo, no por intentos.
    def retryable?(method, network, status, j, idem_key)
      return true if status == 429

      network ||= duplicated?(status, j)
      return network || status >= 500 if method == "GET"
      return network if %w[PATCH DELETE].include?(method)
      return network if idem_key

      false
    end

    # 409 request_in_progress en un POST con su Idempotency-Key: la primera petición con esa clave aún no ha terminado.
    def in_progress?(method, status, j, idem_key)
      method == "POST" && !idem_key.nil? && status == 409 && api_code(j) == "request_in_progress"
    end

    # Segundos antes de repetir, con la misma clave, un POST que sigue en marcha (tras su n-ésimo 409 request_in_progress), o
    # nil si ya no se espera: lo que pida Retry-After (al menos 1 s y como mucho MAX_RETRY_WAIT) o, sin él, 1, 2, 4, 8 y luego
    # IN_PROGRESS_STEP. remaining: lo que queda de IN_PROGRESS_WAIT. Si Retry-After pide más, o la espera no cabe, nil.
    def in_progress_wait(retry_after, n, remaining)
      s = retry_after_seconds(retry_after)
      if s.nil?
        s = [2**(n - 1), IN_PROGRESS_STEP].min
      elsif s > remaining
        return nil
      end
      s = s.clamp(1, MAX_RETRY_WAIT)
      s <= remaining ? s : nil
    end

    # 409 duplicated_request: la MISMA petición firmada llegó dos veces (la repitió algo entre medias).
    def duplicated?(status, j)
      status == 409 && api_code(j) == "duplicated_request"
    end

    # El code de una respuesta de error ("duplicated_request"…) como texto, o nil si no lo trae.
    def api_code(j)
      j.is_a?(Hash) ? text(j["code"]) : nil
    end

    # El cupo de la respuesta (X-RateLimit-Limit, -Remaining y -Reset, en enteros), o nil si no lo trae.
    def rate_limit(headers)
      out = %w[limit remaining reset].to_h do |k|
        v = headers["x-ratelimit-#{k}"].to_s.strip
        [k, v.match?(/\A-?[0-9]{1,18}\z/) ? Integer(v, 10) : nil]
      end
      out.values.any? ? out : nil
    end

    # Los segundos que pide Retry-After (segundos o fecha HTTP), o nil si no viene o no se entiende.
    def retry_after_seconds(retry_after)
      v = retry_after.to_s.strip
      return nil if v.empty?

      s = begin
        Float(v)
      rescue ArgumentError
        begin
          Time.httpdate(v) - Time.now
        rescue ArgumentError
          nil
        end
      end
      s.nil? || s.nan? ? nil : s
    end

    # Segundos antes del siguiente intento: lo que pida Retry-After (segundos o fecha HTTP), o 1, 2, 4… Como mucho 60.
    def retry_wait(retry_after, attempt)
      s = retry_after_seconds(retry_after)
      s = 2**(attempt - 1) if s.nil?
      s.clamp(0, MAX_RETRY_WAIT)
    end

    # El cuerpo JSON de un POST o un PATCH, tal cual se firma y se manda.
    def json_body(data)
      JSON.generate(data)
    rescue JSON::GeneratorError, EncodingError => e
      raise ArgumentError, "MundoSMS: the body cannot be encoded as JSON, nothing was sent (is every text UTF-8?): #{e.message}"
    end

    def blank?(data)
      data.nil? || data == false || (data.respond_to?(:empty?) && data.empty?)
    end

    # RFC 3986: todo codificado menos A-Z a-z 0-9 - . _ ~, byte a byte en UTF-8.
    def rfc3986(s)
      s.to_s.b.gsub(/[^A-Za-z0-9\-._~]/n) { |c| format("%%%02X", c.ord) }
    end

    # Un valor de la query: true/false, una lista separada por comas; nil o una lista vacía (o solo de nil), nada. Una
    # fecha (Time), en ISO 8601 con milisegundos y su zona.
    def query_value(v)
      case v
      when nil then nil
      when true, false then v.to_s
      when Array
        items = v.map { |x| query_value(x) }.compact
        items.empty? ? nil : items.join(",")
      when Time then v.iso8601(3)
      when Hash then raise ArgumentError, "MundoSMS: a Hash cannot go in the query (nothing was sent)"
      else v.to_s
      end
    end

    # La ruta codificada trozo a trozo (un %XX ya escrito se respeta), sin el /v3 del principio si la base ya lo lleva, y
    # los parámetros que traiga tras «?».
    def split_path(path, strip_v3)
      path, _, query = path.to_s.partition("?")
      path_query = {}
      query.split("&").each do |pair|
        next if pair.empty?

        k, _, v = pair.partition("=")
        path_query[unescape(k)] = unescape(v)
      end
      path = "/#{path.sub(%r{\A/+}, '')}"
      path = "/#{path[3..].sub(%r{\A/+}, '')}" if strip_v3 && (path == "/v3" || path.start_with?("/v3/"))
      encoded = path.split("/", -1).map do |seg|
        seg.split(/(%[0-9A-Fa-f]{2})/).each_with_index.map { |x, i| i.odd? ? x : rfc3986(x) }.join
      end
      [encoded.join("/"), path_query]
    end

    # Un trozo de la query de la ruta: cada %XX, su byte; lo demás, también el «+», tal cual (RFC 3986: no es un formulario),
    # para que una fecha con zona escrita en la ruta (from_datetime=…+01:00) llegue tal cual. Un % que no es %XX se deja.
    def unescape(s)
      s.b.gsub(/%[0-9A-Fa-f]{2}/n) { |m| m[1, 2].to_i(16).chr }.force_encoding(Encoding::UTF_8)
    end

    # Un valor de la respuesta como texto: un String tal cual, un número con sus cifras; una lista o un objeto, en JSON.
    def text(v)
      case v
      when nil, String then v
      when Numeric, true, false then v.to_s
      else JSON.generate(v)
      end
    end

    # Lo que en la respuesta cuenta como «sí» (true, un número que no es 0, un texto, una lista o un objeto con algo).
    def truthy(v)
      return false if v.nil? || v == false
      return v != 0 if v.is_a?(Numeric)
      return !v.empty? if v.respond_to?(:empty?)

      true
    end
  end
end
