# frozen_string_literal: true

# Pruebas sin red: un servidor de prueba (TCPServer) que comprueba la firma como la API y contesta lo que pide cada caso.
#   ruby -Ilib test/test_mundosms.rb
require "minitest/autorun"
require "socket"
require "mundosms"

TOKEN = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiIsInN1YiI6IjcifQ.cHJ1ZWJh"
KEY = "Pr4ebaClaveDe20Letra"
OK_BODY = '{"status":"success","message":"OK","data":{"id":8812345}}'

# Lo que llegó al servidor de prueba en una petición.
Seen = Struct.new(:method, :raw_path, :headers, :body, :signature_ok)

# Un servidor HTTP mínimo: una petición por conexión. reply.call(n, seen) devuelve [estado, cabeceras, cuerpo], :close (corta
# sin contestar) o un Proc que escribe él mismo en el socket.
class TestServer
  attr_reader :requests

  def initialize(&reply)
    @reply = reply
    @server = TCPServer.new("127.0.0.1", 0)
    @requests = []
    @lock = Mutex.new
    @thread = Thread.new { loop { Thread.new(@server.accept) { |c| handle(c) } } }
  end

  def url
    "http://127.0.0.1:#{@server.addr[1]}"
  end

  def close
    @thread.kill
    @server.close
  end

  private

  def handle(c)
    line = c.gets or return c.close
    method, raw, = line.split(" ")
    headers = {}
    while (h = c.gets) && h != "\r\n"
      k, v = h.split(":", 2)
      headers[k.strip.downcase] = v.to_s.strip
    end
    body = c.read(headers["content-length"].to_i).to_s
    ok = false
    if headers["authorization"].to_s.start_with?("Bearer ") && headers["x-signature"]
      signed = "#{headers['authorization'][7..]}.#{headers['x-timestamp']}.#{method} #{raw}.".b + body.b
      ok = headers["x-signature"] == [OpenSSL::HMAC.digest("SHA256", KEY, signed)].pack("m0").tr("+/", "-_").delete("=")
    end
    seen = Seen.new(method, raw, headers, body, ok)
    n = @lock.synchronize { @requests << seen; @requests.size }
    r = @reply.call(n, seen)
    if r == :close
      c.close
      return
    end
    return r.call(c) if r.is_a?(Proc)

    status, hs, b = r
    c.write("HTTP/1.1 #{status} X\r\n")
    hs.each { |k, v| c.write("#{k}: #{v}\r\n") }
    c.write("Content-Length: #{b.bytesize}\r\nConnection: close\r\n\r\n")
    c.write(b)
    c.close
  rescue IOError, SystemCallError
    nil
  end
end

class MundoSMSTest < Minitest::Test
  def json(status, body, extra = {})
    [status, { "Content-Type" => "application/json" }.merge(extra), body]
  end

  def server(&reply)
    s = TestServer.new(&reply)
    (@servers ||= []) << s
    s
  end

  def teardown
    (@servers || []).each(&:close)
  end

  def client(s, **opts)
    MundoSMS::Client.new(TOKEN, KEY, base_url: "#{s.url}/v3", **opts)
  end

  def test_signature_and_headers
    s = server { json(200, OK_BODY) }
    api = client(s)
    api.get("/calls", { "type" => "in", "limit" => 5 })
    api.post("/sms", { "to" => "34600123456", "text" => "Señor Núñez: 9,50 € <hoy> & «mañana»" })
    api.patch("/extensions/recepción 1", { record_calls: true })
    api.delete("/voicefiles/77")
    r = s.requests
    assert_equal 4, r.size
    r.each do |x|
      assert x.signature_ok, "#{x.method} #{x.raw_path}: la firma no casa"
      assert_match(/\A[0-9]{13}\z/, x.headers["x-timestamp"])
      refute_match(%r{[+/=]}, x.headers["x-signature"])
      assert_equal "Bearer #{TOKEN}", x.headers["authorization"]
      assert_equal "application/json", x.headers["accept"]
      assert_equal "MundoSMS-Ruby/#{MundoSMS::VERSION}", x.headers["user-agent"]
      assert_equal !x.body.empty?, x.headers["content-type"] == "application/json", "Content-Type solo con cuerpo"
    end
    assert_equal ["/v3/calls?type=in&limit=5", ""], [r[0].raw_path, r[0].body]
    assert_equal ["/v3/sms", '{"to":"34600123456","text":"Señor Núñez: 9,50 € <hoy> & «mañana»"}'.b], [r[1].raw_path, r[1].body.b]
    assert_equal ["PATCH", "/v3/extensions/recepci%C3%B3n%201", '{"record_calls":true}'], [r[2].method, r[2].raw_path, r[2].body]
    assert_equal ["DELETE", "", nil], [r[3].method, r[3].body, r[3].headers["idempotency-key"]]
  end

  def test_without_private_key_no_signature
    s = server { json(200, OK_BODY) }
    MundoSMS::Client.new(" #{TOKEN}\n", "", base_url: "#{s.url}/v3").get("/account")
    h = s.requests[0].headers
    assert_nil h["x-signature"]
    refute_nil h["x-timestamp"]
    assert_equal "Bearer #{TOKEN}", h["authorization"]
  end

  def test_query_and_path_encoding
    s = server { json(200, OK_BODY) }
    api = client(s)
    madrid = "+01:00"
    cases = [
      ["/calls", { "type" => %w[in out], "stop_at_ongoing" => true, "x" => nil, "vacía" => [], "nulos" => [nil], "s" => "a b+ñ~*'()!",
                   "i" => -5, "f" => 1.5 },
       "/v3/calls?type=in%2Cout&stop_at_ongoing=true&s=a%20b%2B%C3%B1~%2A%27%28%29%21&i=-5&f=1.5"],
      ["/calls?type=in&limit=5", { "type" => "out" }, "/v3/calls?type=out&limit=5"],
      ["calls?a=1+2&b=%2B&c&d=", nil, "/v3/calls?a=1%2B2&b=%2B&c=&d="], # «+» es «+»: no es un formulario
      ["/calls?from_datetime=2026-11-20T10:15:03+01:00&x=%20", nil, "/v3/calls?from_datetime=2026-11-20T10%3A15%3A03%2B01%3A00&x=%20"],
      ["/v3/numbers/34910123456", nil, "/v3/numbers/34910123456"],
      ["/v3", nil, "/v3/"],
      ["//extensions/a%2Fb c/%zz", nil, "/v3/extensions/a%2Fb%20c/%25zz"],
      ["/calls", { from_datetime: Time.new(2026, 11, 1, 8, 0, 0, madrid) }, "/v3/calls?from_datetime=2026-11-01T08%3A00%3A00.000%2B01%3A00"],
      ["/calls", { limit: 100, cursor: :abc }, "/v3/calls?limit=100&cursor=abc"]
    ]
    cases.each { |path, query, _| api.get(path, query) }
    s.requests.each_with_index do |x, i|
      assert_equal cases[i][2], x.raw_path, "#{cases[i][0]} #{cases[i][1]}"
      assert x.signature_ok
    end
    client(s, base_url: "#{s.url}/api").get("/v3/calls")
    assert_equal "/api/v3/calls", s.requests.last.raw_path
    assert s.requests.last.signature_ok
  end

  def test_invalid_arguments_send_nothing
    s = server { json(200, OK_BODY) }
    api = client(s)
    ["pedido 881", "", "a" * 65, 881].each do |k|
      assert_raises(ArgumentError) { api.post("/sms", { "to" => "1" }, idempotency_key: k) }
    end
    assert_raises(ArgumentError) { api.get("/calls", { "x" => { "y" => 1 } }) }
    assert_raises(ArgumentError) { api.request("GET", "/calls", [1]) }
    assert_raises(ArgumentError) { api.post("/sms", { "text" => "\xff\xfe".dup.force_encoding("UTF-8") }) }
    assert_raises(ArgumentError) { api.post("/sms", { "n" => Float::NAN }) }
    assert_empty s.requests, "con un argumento malo no se manda nada"
    assert_raises(ArgumentError) { MundoSMS::Client.new(TOKEN, KEY, base: "x") } # una opción que no existe
    assert_raises(ArgumentError) { MundoSMS::Client.new(TOKEN, KEY, retries: -1) }
    assert_raises(ArgumentError) { MundoSMS::Client.new(TOKEN, KEY, timeout: 0) }
    assert_raises(ArgumentError) { MundoSMS::Client.new(TOKEN, KEY, base_url: "ftp://x/v3") }
    assert_raises(ArgumentError) { MundoSMS::Client.new(TOKEN, KEY, base_url: "https://x/v3?a=1") }
  end

  def test_body
    s = server { json(200, OK_BODY) }
    api = client(s)
    api.post("/sms/quote", nil)
    api.post("/sms/quote", {})
    api.patch("/extensions/1", {})
    api.post("/campaigns", { "a" => { "b" => [1, "x", nil, true], "c" => {} }, "n" => 0, "z" => 1.25 })
    assert_equal ["", "", "", '{"a":{"b":[1,"x",null,true],"c":{}},"n":0,"z":1.25}'], s.requests.map(&:body)
    assert(s.requests.all?(&:signature_ok))
  end

  def test_idempotency_key
    s = server do |n, _|
      n == 1 ? json(409, '{"status":"error","code":"request_in_progress","message":"Request in progress"}', "Retry-After" => "0") : json(200, OK_BODY)
    end
    api = client(s)
    api.post("/sms", { "to" => "1" })
    api.post("/sms", { "to" => "1" }, idempotency_key: "pedido-881")
    api.get("/calls")
    api.post("/sms", { "to" => "1" })
    r = s.requests
    k = r[0].headers["idempotency-key"]
    assert_match(/\A[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\z/, k)
    assert_equal k, r[1].headers["idempotency-key"], "el reintento lleva la MISMA clave"
    assert_operator r[1].headers["x-timestamp"].to_i, :>, r[0].headers["x-timestamp"].to_i, "y otro X-Timestamp, mayor"
    refute_equal r[0].headers["x-signature"], r[1].headers["x-signature"]
    assert_equal "pedido-881", r[2].headers["idempotency-key"]
    assert_nil r[3].headers["idempotency-key"]
    refute_equal k, r[4].headers["idempotency-key"]
  end

  # Un 409 se decide por su code, no por el message: otro 409 con el message de request_in_progress no se repite.
  def test_retries
    [["GET", 500, "internal_error", "Internal error", 3], ["POST", 500, "internal_error", "Internal error", 1],
     ["PATCH", 503, "service_unavailable", "Unavailable", 1], ["DELETE", 500, "internal_error", "Internal error", 1],
     ["POST", 429, "requests_per_second", "Too many requests", 3], ["PATCH", 429, "requests_per_second", "Too many requests", 3],
     ["GET", 409, "duplicated_request", "Duplicated request", 3],
     ["PATCH", 409, "duplicated_request", "Duplicated request (the same signed request arrived twice)", 3],
     ["POST", 409, "duplicated_request", "Duplicated request", 3], ["POST", 409, "request_in_progress", "Request in progress", 1],
     ["PATCH", 409, "request_in_progress", "Request in progress", 1], ["POST", 409, "already_exists", "Request in progress", 1],
     ["GET", 404, "not_found", "Call not found", 1],
     ["POST", 422, "idempotency_key_reused", "Idempotency-Key already used", 1]].each do |method, status, code, message, want|
      # el POST con request_in_progress, con Retry-After de una hora: más de lo que se espera (lo que espera, en
      # test_in_progress_waits_by_time)
      ra = method == "POST" && code == "request_in_progress" ? "3600" : "0"
      s = server { json(status, %({"status":"error","code":"#{code}","message":"#{message}"}), "Retry-After" => ra) }
      # si algún 409 se esperase sin deber, que se note en segundos
      e = with_in_progress_wait(3) { assert_raises(MundoSMS::Error) { client(s, retries: 2).request(method, "/x", { "a" => 1 }) } }
      assert_equal [status, code, message], [e.http, e.code, e.message]
      assert_equal want, s.requests.size, "#{method} #{status} #{code}"
    end
  end

  # 2.1.0: un 409 request_in_progress se repite con la MISMA clave por tiempo (hasta 180 s desde el primero), no por intentos.
  def test_in_progress_waits_by_time
    en = '{"status":"error","code":"request_in_progress","message":"Request in progress"}'
    # (a) un corte (gasta el único reintento de retries: 1) y tres 409 con Retry-After: 0 (se espera como 1 s); a la quinta, la
    # respuesta
    s = server do |n, _|
      if n == 1 then :close
      elsif n < 5 then json(409, en, "Retry-After" => "0")
      else json(200, OK_BODY)
      end
    end
    t0 = Time.now
    assert_equal "success", client(s, retries: 1).post("/whatsapp/messages", { "to" => "1" })["status"]
    el = Time.now - t0
    r = s.requests
    assert_equal [5, 1, 5], [r.size, r.map { |x| x.headers["idempotency-key"] }.uniq.size, r.map { |x| x.headers["x-timestamp"] }.uniq.size]
    assert el.between?(3, 8), "(a) #{el}"
    # (b) sin Retry-After: 1 y 2 s
    s = server { |n, _| n < 3 ? json(409, en) : json(200, OK_BODY) }
    t0 = Time.now
    assert_equal "success", client(s, retries: 1).post("/sms", { "to" => "1" })["status"]
    assert_equal 3, s.requests.size
    assert (Time.now - t0).between?(3, 6), "(b) #{Time.now - t0}"
    # (c) Retry-After pide más de lo que queda de los 180 s: no espera; el error lleva la clave para repetirlo más tarde
    s = server { json(409, en, "Retry-After" => "3600") }
    t0 = Time.now
    e = assert_raises(MundoSMS::Error) do # si esperase, que se note en segundos y no en minutos
      Timeout.timeout(3) { client(s, retries: 1).post("/sms", { "to" => "1" }, idempotency_key: "pedido-881") }
    end
    assert_equal [409, "request_in_progress", "pedido-881", 1], [e.http, e.code, e.idempotency_key, s.requests.size]
    assert_operator Time.now - t0, :<, 1
    # (d) con retries: 0, ninguno; y la clave puesta por el SDK, en el error
    s = server { json(409, en, "Retry-After" => "0") }
    e = with_in_progress_wait(3) { assert_raises(MundoSMS::Error) { client(s, retries: 0).post("/sms", { "to" => "1" }) } }
    assert_equal [s.requests[0].headers["idempotency-key"], 1], [e.idempotency_key, s.requests.size]
    refute_nil e.idempotency_key
    # (e) el tope (aquí, 2,5 s en vez de 180): tras dos esperas de 1 s, la tercera no cabe
    s = server { json(409, en, "Retry-After" => "0") }
    e = with_in_progress_wait(2.5) { assert_raises(MundoSMS::Error) { Timeout.timeout(6) { client(s).post("/sms", { "to" => "1" }) } } }
    assert_equal ["request_in_progress", 3], [e.code, s.requests.size]
  end

  # El tope de la espera de request_in_progress (IN_PROGRESS_WAIT), cambiado mientras dura el bloque.
  def with_in_progress_wait(segundos)
    tope = MundoSMS::IN_PROGRESS_WAIT
    MundoSMS.send(:remove_const, :IN_PROGRESS_WAIT)
    MundoSMS.const_set(:IN_PROGRESS_WAIT, segundos)
    yield
  ensure
    MundoSMS.send(:remove_const, :IN_PROGRESS_WAIT)
    MundoSMS.const_set(:IN_PROGRESS_WAIT, tope)
  end

  def test_in_progress_wait
    api = MundoSMS::Client.new(TOKEN, KEY)
    future = (Time.now + 3).httpdate
    [[nil, 1, 180, 1], ["", 4, 180, 8], ["", 9, 180, 15], ["", 9, 10, nil], ["0", 1, 180, 1], ["-5", 1, 180, 1], ["30", 1, 180, 30],
     ["120", 1, 180, 60], ["120", 1, 100, nil], ["3600", 1, 180, nil], ["NaN", 2, 180, 2], ["0", 1, 0.5, nil]].each do |h, n, quedan, want|
      w = api.send(:in_progress_wait, h, n, quedan)
      msg = "Retry-After #{h.inspect}, n #{n}, quedan #{quedan}"
      want.nil? ? assert_nil(w, msg) : assert_equal(want, w, msg)
    end
    w = api.send(:in_progress_wait, future, 1, 180)
    assert w.between?(1, 3), "Retry-After con fecha: #{w}"
  end

  def test_retry_wait
    api = MundoSMS::Client.new(TOKEN, KEY)
    future = (Time.now + 3).httpdate
    [[nil, 1, 1, 1], ["", 3, 4, 4], ["", 10, 60, 60], ["2", 1, 2, 2], ["0.25", 1, 0.25, 0.25], ["120", 1, 60, 60],
     ["-5", 2, 0, 0], ["NaN", 2, 2, 2], ["mañana", 2, 2, 2], [future, 1, 1, 3]].each do |h, attempt, min, max|
      w = api.send(:retry_wait, h, attempt)
      assert w.between?(min, max), "Retry-After #{h.inspect}, intento #{attempt}: #{w}"
    end
  end

  def test_retry_after_is_honored
    s = server { |n, _| n == 1 ? json(429, '{"status":"error","code":"requests_per_second","message":"Too many requests"}', "Retry-After" => "1") : json(200, OK_BODY) }
    t0 = Time.now
    client(s).post("/sms", { "to" => "1" })
    assert (Time.now - t0).between?(0.9, 5)
  end

  def test_network_error_retries_with_same_key
    s = server { :close } # lee la petición y corta sin contestar
    e = assert_raises(MundoSMS::Error) { client(s, retries: 2).post("/sms", { "to" => "1" }) }
    assert_equal 0, e.http
    assert e.message.start_with?("No response from the API: ")
    keys = s.requests.map { |x| x.headers["idempotency-key"] }
    assert_equal 3, keys.size
    assert_equal 1, keys.uniq.size, "el POST sin respuesta se repite con la misma Idempotency-Key"
    assert_equal keys[0], e.idempotency_key, "2.1.0: sin respuesta, el error lleva la clave con la que repetirlo"
  end

  def test_timeout_whole_request
    # la cabecera llega y el cuerpo, un byte cada 0,1 s durante 1,5 s: ninguna lectura agota su plazo, la petición entera sí
    cuerpo = '{"status":"success","data":{}}'
    s = server do
      proc do |c|
        c.write("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: #{cuerpo.bytesize}\r\n\r\n")
        c.flush
        cuerpo.each_char do |ch|
          sleep 0.05
          c.write(ch)
          c.flush
        end
        c.close
      end
    end
    t0 = Time.now
    e = assert_raises(MundoSMS::Error) { client(s, timeout: 0.3, retries: 0).get("/calls") }
    assert_equal [0, "No response from the API: timed out after 0.3 s"], [e.http, e.message]
    assert_operator Time.now - t0, :<, 1
  end

  def test_errors
    s = server do |n, _|
      case n
      when 1 then json(404, '{"status":"error","code":"not_found","message":"Call not found","detail":"no call 999 of type out"}', "X-Request-Id" => "req-1")
      when 2 then json(200, '{"status":"error","code":"whatsapp_rejected","message":"WhatsApp rejected it","data":{"id":"wam-1"}}')
      when 3 then [502, { "Content-Type" => "text/html", "X-Request-Id" => "req-3" }, "<html>Bad gateway</html>"]
      when 4 then json(202, '{"status":"success","message":"Not confirmed","data":{"id":"wam-2","state":"unknown"}}')
      else json(400, '{"status":"error","code":"invalid_parameter","message":"Parameter xml_plan not valid","detail":["line 1","line 2"]}')
      end
    end
    api = client(s, retries: 0)
    e = assert_raises(MundoSMS::Error) { api.get("/calls/out/999") }
    assert_equal [404, "not_found", "Call not found", "no call 999 of type out", "req-1", "error"], [e.http, e.code, e.message, e.detail, e.request_id, e.response["status"]]
    assert_nil e.idempotency_key, "un GET no lleva clave"
    refute_respond_to e, :status_code
    e = assert_raises(MundoSMS::Error) { api.post("/whatsapp/messages", { "to" => "1" }) }
    assert_equal [200, "whatsapp_rejected", "wam-1"], [e.http, e.code, e.data["id"]]
    refute_nil e.idempotency_key
    assert_equal s.requests[1].headers["idempotency-key"], e.idempotency_key, "2.1.0: el error de un POST lleva la Idempotency-Key que usó"
    e = assert_raises(MundoSMS::Error) { api.get("/calls") }
    assert_equal [502, nil, "HTTP 502", nil, "req-3"], [e.http, e.code, e.message, e.response, e.request_id]
    assert_equal "unknown", api.post("/whatsapp/messages", { "to" => "1" })["data"]["state"]
    e = assert_raises(MundoSMS::Error) { api.post("/campaigns", { "to" => "1" }) }
    assert_equal '["line 1","line 2"]', e.detail
    assert_kind_of StandardError, e
  end

  def test_files_and_replayed
    s = server do |n, _|
      case n
      when 1 then [200, { "Content-Type" => "audio/mpeg" }, "\xff\xfb\x00\x01".b]
      when 2 then [204, {}, ""]
      when 3 then json(200, OK_BODY, "Idempotent-Replayed" => " True ")
      else json(200, '{"status":"success","data":{"id":8812345,"cost":0.07,"big":12345678901234567890}}')
      end
    end
    api = client(s)
    r = api.get("/calls/out/1/recording")
    assert_equal ["success", "audio/mpeg", "\xff\xfb\x00\x01".b], [r["status"], r["content_type"], r["body"].b]
    r = api.delete("/voicefiles/1")
    assert_equal ["success", ""], [r["status"], r["body"]]
    r = api.post("/sms", { "to" => "1" })
    assert_equal [true, 8812345], [r["idempotent_replayed"], r["data"]["id"]]
    r = api.get("/account")
    assert_equal [8812345, 0.07, 12_345_678_901_234_567_890], [r["data"]["id"], r["data"]["cost"], r["data"]["big"]]
  end

  PAGES = [
    '{"status":"success","data":{"results":2,"first":[{"id":1},{"id":2}],"calls":[{"id":0}],"has_more":true,"next_cursor":"a.0.2"}}',
    '{"status":"success","data":{"results":2,"first":[{"id":3},{"id":4}],"has_more":true,"next_cursor":"a.0.4"}}',
    '{"status":"success","data":{"results":1,"first":[{"id":5}],"has_more":false,"next_cursor":"a.0.5"}}'
  ].freeze

  def test_all
    s = server { |n, _| json(200, PAGES[n - 1]) }
    api = client(s)
    query = { "from_datetime" => "2026-11-01", order: "asc" }
    ids = api.all("/calls", query).map { |c| c["id"] }.to_a
    assert_equal [[1, 2, 3, 4, 5], "a.0.5"], [ids, api.last_cursor]
    assert_equal ["/v3/calls?from_datetime=2026-11-01&order=asc", "/v3/calls?from_datetime=2026-11-01&order=asc&cursor=a.0.2",
                  "/v3/calls?from_datetime=2026-11-01&order=asc&cursor=a.0.4"], s.requests.map(&:raw_path)
    refute query.key?("cursor"), "all no toca tu query"
    s2 = server { |n, _| json(200, PAGES[n - 1]) }
    assert_equal [{ "id" => 1 }], client(s2).all("/calls").first(1)
    assert_equal 1, s2.requests.size, "perezoso: first(1) pide una página"
    s3 = server { |n, _| json(200, PAGES[n - 1]) }
    seen = []
    assert_nil(client(s3).all("/calls") { |c| seen << c["id"] })
    assert_equal [1, 2, 3, 4, 5], seen
  end

  def test_all_stops_and_errors
    # un cursor que no avanza: para, sin dar vueltas (la primera página vacía con has_more sigue: no es el final)
    s = server { json(200, '{"status":"success","data":{"results":0,"calls":[],"has_more":true,"next_cursor":"x"}}') }
    api = client(s)
    assert_equal [], api.all("/calls").map { |c| c["id"] }.to_a
    assert_equal [2, "x"], [s.requests.size, api.last_cursor]
    # revisión E: una página vacía con has_more y el cursor que avanza no es el final; la que se para en una llamada en curso, sí
    pages = ['{"status":"success","data":{"results":0,"calls":[],"has_more":true,"next_cursor":"x"}}',
             '{"status":"success","data":{"results":1,"calls":[{"id":5}],"has_more":true,"next_cursor":"e1","ongoing_call":{"type":"in","id":9}}}']
    s = server { |n, _| json(200, pages[n - 1]) }
    api = client(s)
    assert_equal [5], api.all("/calls").map { |c| c["id"] }.to_a
    assert_equal [2, "e1"], [s.requests.size, api.last_cursor]
    # una lista que no va por páginas (has_more sin next_cursor, como /calls/missed): un error, antes de ningún elemento
    ['{"status":"success","data":{"results":1,"calls":[{"id":1}],"has_more":true,"next_cursor":null}}',
     '{"status":"success","data":{"results":2,"calls":[{"id":1},{"id":2}],"has_more":true}}'].each do |page|
      s = server { json(200, page) }
      api = client(s)
      got = []
      e = assert_raises(MundoSMS::Error) { api.all("/calls/missed").each { |c| got << c["id"] } }
      assert_includes e.message, "not paginated"
      assert_equal [[], 1, nil], [got, s.requests.size, api.last_cursor]
    end
    pages = ['{"status":"success","data":{"results":1,"calls":[{"id":1}],"has_more":true,"next_cursor":"c1"}}',
             '{"status":"error","code":"invalid_parameter","message":"Parameter cursor not valid"}']
    s = server { |n, _| json(n == 1 ? 200 : 400, pages[n - 1]) }
    api = client(s, retries: 0)
    got = []
    assert_raises(MundoSMS::Error) { api.all("/calls").each { |c| got << c["id"] } }
    assert_equal [[1], "c1"], [got, api.last_cursor]
  end

  # Una redirección no se sigue: es un MundoSMS::Error con su Location, sin la respuesta, y al otro servidor no le llega nada
  # (tampoco el POST de un 307), ni se reintenta.
  def test_redirect_not_followed
    other = server { json(200, OK_BODY) }
    s = server { |_, x| [x.method == "POST" ? 307 : 302, { "Location" => "#{other.url}/robado", "Content-Type" => "application/json" }, OK_BODY] }
    api = client(s, retries: 2)
    [[-> { api.get("/calls") }, 302], [-> { api.post("/sms", { "to" => "34600000000", "text" => "secreto" }) }, 307]].each do |f, code|
      e = assert_raises(MundoSMS::Error) { f.call }
      assert_equal [code, nil], [e.http, e.response]
      assert_includes e.message, "#{other.url}/robado"
    end
    assert_equal [2, 0], [s.requests.size, other.requests.size]
  end

  # Un 2xx que no es JSON (el HTML de un portal cautivo, o un JSON que no es un objeto) es un error, en POST y en GET; un
  # fichero de la API (por su Content-Type) sigue siendo un fichero.
  def test_non_json_success_is_an_error
    replies = [["text/html; charset=utf-8", "<html>Portal</html>"], ["text/html", "<html>Portal</html>"], ["application/json", "[1,2]"],
               [nil, "no"], ["application/pdf", "%PDF-1.7"], ["image/png", "\x89PNG".b], ["application/octet-stream", "\x00".b]]
    s = server { |n, _| [200, replies[n - 1][0] ? { "Content-Type" => replies[n - 1][0] } : {}, replies[n - 1][1]] }
    api = client(s, retries: 0)
    [[-> { api.post("/sms", { "to" => "1" }) }, "(text/html)"], [-> { api.get("/calls") }, "(text/html)"],
     [-> { api.get("/calls") }, "(application/json)"], [-> { api.get("/calls") }, "(no Content-Type)"]].each do |f, want|
      e = assert_raises(MundoSMS::Error) { f.call }
      assert_equal 200, e.http
      assert_includes e.message, "the response is not JSON #{want}"
    end
    %w[application/pdf image/png application/octet-stream].each do |want|
      assert_equal want, api.get("/fichero")["content_type"]
    end
  end

  # Un DELETE que se hizo y perdió la respuesta: el SDK lo repite y la API da 404. Ya estaba hecho: vuelve como éxito. Sin
  # fallo de red antes, un 404 es un error; y route_not_found, también tras el fallo. Net::HTTP no lo repite por su cuenta.
  def test_delete_retried_after_network_error
    [[true, "not_found", true], [false, "not_found", false], [true, "route_not_found", false]].each do |cut, code, ok|
      s = server { |n, _| cut && n == 1 ? :close : json(404, %({"status":"error","code":"#{code}","message":"Not found"})) }
      api = client(s, retries: 1)
      if ok
        r = api.delete("/speed-dials/9121")
        assert_equal({ "status" => "success", "message" => "Already deleted", "data" => { "state" => "deleted", "changed" => false } }, r)
      else
        e = assert_raises(MundoSMS::Error) { api.delete("/speed-dials/9121") }
        assert_equal [404, code], [e.http, e.code]
      end
      assert_equal cut ? 2 : 1, s.requests.size, "una petición por intento del SDK"
    end
  end

  # last_cursor es el del recorrido: al empezar, el que le pasas (una página vacía sin cursor no lo borra), y un recorrido que
  # falla al empezar no deja el de otro.
  def test_last_cursor_per_traversal
    s = server do |_, x|
      case x.raw_path
      when %r{\A/v3/vacia} then json(200, '{"status":"success","data":{"results":0,"calls":[],"has_more":false,"next_cursor":null}}')
      when %r{\A/v3/una} then json(200, '{"status":"success","data":{"results":1,"calls":[{"id":1}],"has_more":false,"next_cursor":"u1"}}')
      else json(400, '{"status":"error","code":"invalid_parameter","message":"Parameter cursor not valid"}')
      end
    end
    api = client(s, retries: 0)
    api.all("/vacia", { "cursor" => "c9" }).to_a
    assert_equal "c9", api.last_cursor
    api.all("/una").to_a
    assert_equal "u1", api.last_cursor
    assert_raises(MundoSMS::Error) { api.all("/error").to_a }
    assert_nil api.last_cursor
  end

  # last_rate_limit es el cupo de la última respuesta (X-RateLimit-*), y nil sin las cabeceras.
  def test_last_rate_limit
    s = server do |n, _|
      case n
      when 1 then json(200, OK_BODY, "X-RateLimit-Limit" => "10", "X-RateLimit-Remaining" => "7", "X-RateLimit-Reset" => " 1 ")
      when 3 then json(200, OK_BODY, "X-RateLimit-Remaining" => "0")
      else json(200, OK_BODY)
      end
    end
    api = client(s)
    assert_nil api.last_rate_limit
    api.get("/a")
    assert_equal({ "limit" => 10, "remaining" => 7, "reset" => 1 }, api.last_rate_limit)
    api.get("/b")
    assert_nil api.last_rate_limit
    api.get("/c")
    assert_equal({ "limit" => nil, "remaining" => 0, "reset" => nil }, api.last_rate_limit)
  end

  def test_secrets_not_printed
    api = MundoSMS::Client.new(TOKEN, KEY)
    require "pp"
    shown = [api.inspect, api.to_s, "#{api}", PP.pp(api, +""), [api].inspect, { a: api }.inspect].join(" ")
    refute_includes shown, TOKEN
    refute_includes shown, KEY
    assert_includes shown, MundoSMS::DEFAULT_BASE_URL
  end

  def test_concurrent_use_distinct_timestamps
    s = server { json(200, OK_BODY) }
    api = client(s)
    20.times.map { |i| Thread.new { api.get("/calls/out/#{i}") } }.each(&:join)
    assert_equal 20, s.requests.size
    assert(s.requests.all?(&:signature_ok))
    assert_equal 20, s.requests.map { |x| x.headers["x-timestamp"] }.uniq.size, "20 peticiones a la vez, 20 X-Timestamp distintos"
  end

  def sign(secret, t, body)
    OpenSSL::HMAC.hexdigest("SHA256", secret, "#{t}.#{body}")
  end

  def test_verify_webhook
    body = '{"id":"evt_1","type":"sms.status","created":"2026-11-23T10:00:00.000+01:00","account_id":7,"data":{"state":"delivered","text":"Ñandú"}}'
    now = Time.now.to_i.to_s
    old = (Time.now.to_i - 301).to_s
    good = sign("whsec_prueba", now, body)
    bad = "0" * 64
    [["whsec_prueba", "t=#{now},v1=#{good}", body, true],
     [" whsec_prueba\n", " t = #{now} , v1 = #{good.upcase} ", body, true],
     ["whsec_prueba", "t=#{now},v1=#{bad},v1=#{good}", body, true],
     ["whsec_prueba", "t=#{now},v1=#{good}", body.sub("delivered", "failed"), false],
     ["whsec_prueba", "t=#{now},v1=#{good}", body.sub(',"', ', "'), false],
     ["", "t=#{now},v1=#{sign('', now, body)}", body, false],
     ["   ", "t=#{now},v1=#{sign('', now, body)}", body, false],
     [nil, "t=#{now},v1=#{good}", body, false],
     ["whsec_otro", "t=#{now},v1=#{good}", body, false],
     ["whsec_prueba", "t=#{old},v1=#{sign('whsec_prueba', old, body)}", body, false],
     ["whsec_prueba", "t=#{now},t=#{now},v1=#{good}", body, false],
     ["whsec_prueba", "t=1e9,v1=#{good}", body, false],
     ["whsec_prueba", "t=#{now}", body, false],
     ["whsec_prueba", "v1=#{good}", body, false],
     ["whsec_prueba", "", body, false],
     ["whsec_prueba", nil, body, false],
     ["whsec_prueba", "t=#{now},v1=#{good[0, 63]}", body, false],
     ["whsec_prueba", "t=#{now},v1=#{good}00", body, false],
     ["whsec_prueba", "t=#{now};v1=#{good}", body, false],
     ["whsec_prueba", "t=-#{now},v1=#{good}", body, false],
     ["whsec_prueba", "t=#{now},v1=#{good.sub(/.\z/, 'é')}", body, false]].each_with_index do |(secret, header, b, want), i|
      assert_equal want, MundoSMS.verify_webhook(secret, header, b), "caso #{i} (#{header.inspect})"
    end
    assert MundoSMS.verify_webhook("whsec_prueba", "t=#{old},v1=#{sign('whsec_prueba', old, body)}", body, tolerance: 600)
    refute MundoSMS.verify_webhook("whsec_prueba", "t=#{now},v1=#{good}", body, tolerance: -1)
    assert MundoSMS.verify_webhook("whsec_prueba", "t=#{now},v1=#{good}", body.b), "el cuerpo en binario, tal cual llega"
    assert_raises(ArgumentError) { MundoSMS.verify_webhook("whsec_prueba", "t=#{now},v1=#{good}", JSON.parse(body)) }
  end
end
