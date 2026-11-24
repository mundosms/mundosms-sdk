// Pruebas del cliente Node.js (mundosms.js), sin red: un servidor de prueba local (http) que comprueba la firma como la API y
// contesta lo que pide cada caso. Node.js 18 o superior, sin dependencias (node:test).
//   node tests/node/test_mundosms.js
"use strict";
const test = require("node:test");
const assert = require("node:assert");
const crypto = require("node:crypto");
const fs = require("node:fs");
const http = require("node:http");
const path = require("node:path");

const sdk = [path.join(__dirname, "..", "..", "node", "mundosms.js"), path.join(__dirname, "..", "..", "mundosms.js")].find((f) => fs.existsSync(f));
const { MundoSMS, MundoSMSError } = require(sdk); // en el repositorio, o con todos los clientes en una carpeta

const TOKEN = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiIsInN1YiI6IjcifQ.cHJ1ZWJh";
const KEY = "Pr4ebaClaveDe20Letra";
const OK = { status: "success", message: "OK", data: { id: 8812345 } };

/** La firma de «La firma» de la documentación: base64url, sin relleno, del HMAC-SHA256. */
function firma(clave, token, ts, metodo, ruta, cuerpo = Buffer.alloc(0)) {
  return crypto.createHmac("sha256", clave).update(token + "." + ts + "." + metodo + " " + ruta + ".").update(cuerpo).digest("base64url");
}

/** Apunta cada petición (con su firma comprobada) y contesta con responde(n, petición): [estado, cabeceras, cuerpo] o "corta". */
async function servidor(t, responde) {
  const peticiones = [];
  const srv = http.createServer((req, res) => {
    const trozos = [];
    req.on("data", (c) => trozos.push(c));
    req.on("end", () => {
      const b = Buffer.concat(trozos);
      const p = { metodo: req.method, ruta: req.url, cabeceras: req.headers, cuerpo: b,
                  firma: req.headers["x-signature"] === firma(KEY, String(req.headers.authorization || "").slice(7), req.headers["x-timestamp"], req.method, req.url, b) };
      peticiones.push(p);
      const r = responde(peticiones.length, p);
      if (r === "corta") { req.socket.destroy(); return; }
      let [estado, cabeceras, cuerpo] = r;
      if (!Buffer.isBuffer(cuerpo) && typeof cuerpo !== "string") { cuerpo = JSON.stringify(cuerpo); cabeceras = { "Content-Type": "application/json", ...cabeceras }; }
      res.writeHead(estado, cabeceras);
      if (cabeceras["X-Lento"]) { res.flushHeaders(); setTimeout(() => res.end(cuerpo), 1500); } else res.end(cuerpo);
    });
  });
  await new Promise((ok) => srv.listen(0, "127.0.0.1", ok));
  t.after(() => { srv.closeAllConnections && srv.closeAllConnections(); srv.close(); });
  return { peticiones, base: "http://127.0.0.1:" + srv.address().port + "/v3" };
}

const cliente = (s, opc = {}) => new MundoSMS(TOKEN, KEY, { baseUrl: s.base, ...opc });

async function falla(f) {
  try { await f(); } catch (e) { if (e instanceof MundoSMSError) return e; throw e; }
  assert.fail("tenía que dar un MundoSMSError");
}

test("la firma: los ejemplos de la documentación y lo que manda el SDK", async (t) => {
  const tk = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiJ9.ZmlybWE", k = "A1b2C3d4E5f6G7h8I9j0", ts = "1795400000123";
  assert.strictEqual(firma(k, tk, ts, "GET", "/v3/calls?type=in&limit=5"), "uE09uuJV7e3hSSW2NZIJQ9jMWlT15lMETj-sbrIjoFo");
  assert.strictEqual(firma(k, tk, ts, "POST", "/v3/sms", Buffer.from('{"to":"34600123456","text":"Hola"}')), "_62bUnSBUjpWSsAMT580JFfk1Yhx9bCZhS805swJ2to");
  const s = await servidor(t, () => [200, {}, OK]);
  const api = cliente(s);
  await api.get("/calls", { type: "in", limit: 5 });
  await api.post("/sms", { to: "34600123456", text: "Señor Núñez: 9,50 €" });
  const [g, p] = s.peticiones;
  assert.deepStrictEqual([g.ruta, g.cuerpo.length, p.ruta, p.cuerpo.toString()], ["/v3/calls?type=in&limit=5", 0, "/v3/sms", '{"to":"34600123456","text":"Señor Núñez: 9,50 €"}']);
  for (const x of [g, p]) { assert.ok(x.firma); assert.match(x.cabeceras["x-timestamp"], /^[0-9]{13}$/); }
  assert.match(p.cabeceras["idempotency-key"], /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  assert.strictEqual(g.cabeceras["idempotency-key"], undefined);
});

test("un aviso: el ejemplo de whsec_ejemplo", () => {
  const cuerpo = '{"id":"evt_prueba","type":"sms.status","data":{"to":"34600123456","state":"delivered"}}';
  const cab = "t=1795161606,v1=d43d40dfd88dd4aba4633580b2786f8651936516aecc2967868edf333ddfdae5";
  assert.ok(MundoSMS.verifyWebhook("whsec_ejemplo", cab, Buffer.from(cuerpo), 1e10));
  assert.ok(!MundoSMS.verifyWebhook("whsec_otro", cab, cuerpo, 1e10));
  assert.ok(!MundoSMS.verifyWebhook("whsec_ejemplo", cab, cuerpo.replace("delivered", "Delivered"), 1e10));
  assert.ok(!MundoSMS.verifyWebhook("whsec_ejemplo", cab, cuerpo)); // de hace más de 5 minutos
});

test("reintentos: un 429 sí; un POST con 5xx, no", async (t) => {
  const s = await servidor(t, (n) => (n < 3 ? [429, { "Retry-After": "0" }, { status: "error", code: "requests_per_second", message: "Too many requests" }] : [200, {}, OK]));
  assert.strictEqual((await cliente(s).get("/calls")).status, "success");
  assert.strictEqual(new Set(s.peticiones.map((x) => x.cabeceras["x-timestamp"])).size, 3);
  const s2 = await servidor(t, () => [500, {}, { status: "error", code: "internal_error", message: "Internal error" }]);
  const e = await falla(() => cliente(s2).post("/sms", { to: "1" }));
  assert.deepStrictEqual([e.http, e.code, s2.peticiones.length], [500, "internal_error", 1]);
});

// un 409 se decide por el code, no por el message: duplicated_request (cualquier método) y request_in_progress (un POST con su
// clave) se repiten; otro 409, aunque su message diga lo mismo, no
test("un 409: por su code", async (t) => {
  for (const [metodo, code, mensaje, n] of [["GET", "duplicated_request", "Duplicated request", 3], ["PATCH", "duplicated_request", "Duplicated request", 3],
    ["POST", "duplicated_request", "Duplicated request", 3], ["POST", "request_in_progress", "Request in progress", 3],
    ["PATCH", "request_in_progress", "Request in progress", 1], ["POST", "already_exists", "Request in progress", 1]]) {
    const s = await servidor(t, () => [409, { "Retry-After": "0" }, { status: "error", code, message: mensaje }]);
    const e = await falla(() => cliente(s, { retries: 2 }).request(metodo, "/x", { a: 1 }));
    assert.deepStrictEqual([e.http, e.code, s.peticiones.length], [409, code, n], metodo + " " + code);
  }
});

test("los campos del error", async (t) => {
  const r = [[404, { "X-Request-Id": "req-1" }, { status: "error", code: "not_found", message: "Call not found", detail: "no call 999" }],
             [200, {}, { status: "error", code: "whatsapp_rejected", message: "WhatsApp rejected it", data: { id: "wam-1" } }],
             [502, { "Content-Type": "text/html" }, "<html>Bad gateway</html>"],
             [202, {}, { status: "success", message: "Not confirmed", data: { id: "wam-2", state: "unknown" } }]];
  const s = await servidor(t, (n) => r[n - 1]);
  const api = cliente(s, { retries: 0 });
  let e = await falla(() => api.get("/calls/out/999"));
  assert.deepStrictEqual([e.http, e.code, e.message, e.detail, e.requestId, e.response.status], [404, "not_found", "Call not found", "no call 999", "req-1", "error"]);
  assert.ok(!("statusCode" in e));
  e = await falla(() => api.post("/whatsapp/messages", { to: "1" }));
  assert.deepStrictEqual([e.http, e.code, e.data], [200, "whatsapp_rejected", { id: "wam-1" }]);
  e = await falla(() => api.get("/calls"));
  assert.deepStrictEqual([e.http, e.code, e.message, e.response], [502, null, "HTTP 502", null]);
  assert.strictEqual((await api.post("/whatsapp/messages", { to: "1" })).data.state, "unknown"); // el 202 no es un error
});

test("el plazo es de la petición entera", async (t) => {
  const s = await servidor(t, () => [200, { "X-Lento": "1" }, OK]);
  const t0 = Date.now();
  const e = await falla(() => cliente(s, { timeout: 0.3, retries: 0 }).get("/calls"));
  assert.deepStrictEqual([e.http, e.message], [0, "No response from the API: timed out after 0.3 s"]);
  assert.ok(Date.now() - t0 < 1200);
});

test("all recorre las páginas con su cursor", async (t) => {
  const pags = [{ results: 2, calls: [{ id: 1 }, { id: 2 }], has_more: true, next_cursor: "a.2" }, { results: 1, calls: [{ id: 3 }], has_more: false, next_cursor: "a.3" }];
  const s = await servidor(t, (n) => [200, {}, { status: "success", data: pags[n - 1] }]);
  const api = cliente(s), ids = [];
  for await (const x of api.all("/calls", { order: "asc" })) ids.push(x.id);
  assert.deepStrictEqual([ids, s.peticiones.map((x) => x.ruta), api.lastCursor], [[1, 2, 3], ["/v3/calls?order=asc", "/v3/calls?order=asc&cursor=a.2"], "a.3"]);
});

test("una redirección no se sigue", async (t) => {
  const s = await servidor(t, (n, p) => (p.ruta.startsWith("/robado") ? [200, {}, OK] : [p.metodo === "GET" ? 302 : 307, { Location: "/robado" }, OK]));
  const api = cliente(s, { retries: 2 });
  for (const [f, codigo] of [[() => api.get("/calls"), 302], [() => api.post("/sms", { to: "1", text: "secreto" }), 307]]) {
    const e = await falla(f);
    assert.deepStrictEqual([e.http, e.response], [codigo, null]);
    assert.match(e.message, /redirect to \/robado not followed/);
  }
  assert.deepStrictEqual(s.peticiones.map((x) => x.ruta), ["/v3/calls", "/v3/sms"]); // al destino no le llega nada
});

test("un 2xx que no es JSON es un error; un fichero, no", async (t) => {
  const r = [[200, { "Content-Type": "text/html" }, "<html>Portal</html>"], [200, { "Content-Type": "text/html" }, "<html>Portal</html>"],
             [200, { "Content-Type": "application/json" }, "[1, 2]"], [200, { "Content-Type": "audio/mpeg" }, Buffer.from([0xff, 0xfb])],
             [200, { "Content-Type": "application/pdf" }, "%PDF"], [204, {}, ""]];
  const s = await servidor(t, (n) => r[n - 1]);
  const api = cliente(s, { retries: 0 });
  for (const [f, tipo] of [[() => api.post("/sms", { to: "1" }), "text/html"], [() => api.get("/calls"), "text/html"], [() => api.get("/calls"), "application/json"]]) {
    const e = await falla(f);
    assert.deepStrictEqual([e.http, e.message], [200, "HTTP 200: the response is not JSON (" + tipo + ")"]);
  }
  assert.deepStrictEqual([...(await api.get("/rec")).body], [0xff, 0xfb]);
  assert.strictEqual((await api.get("/cert")).content_type, "application/pdf");
  assert.strictEqual((await api.delete("/x/1")).body.length, 0);
});

test("all de una lista que no va por páginas es un error", async (t) => {
  const s = await servidor(t, () => [200, {}, { status: "success", data: { results: 2, calls: [{ id: 1 }, { id: 2 }], has_more: true } }]);
  const vistos = [];
  const e = await falla(async () => { for await (const x of cliente(s).all("/calls/missed")) vistos.push(x); });
  assert.match(e.message, /not paginated/);
  assert.deepStrictEqual([vistos, s.peticiones.length], [[], 1]);
});

test("un «+» en la query de la ruta llega como «+»", async (t) => {
  const s = await servidor(t, () => [200, {}, OK]);
  await cliente(s).get("/calls?from_datetime=2026-11-20T10:15:03+01:00&a=%20");
  assert.strictEqual(s.peticiones[0].ruta, "/v3/calls?from_datetime=2026-11-20T10%3A15%3A03%2B01%3A00&a=%20");
  assert.ok(s.peticiones[0].firma);
});

test("un DELETE que se hizo y perdió la respuesta", async (t) => {
  const no = { status: "error", code: "not_found", message: "Speed dial not found" };
  const s = await servidor(t, (n) => (n === 1 ? "corta" : [404, {}, no]));
  const r = await cliente(s, { retries: 1 }).delete("/speed-dials/9121");
  assert.deepStrictEqual([r, s.peticiones.length], [{ status: "success", message: "Already deleted", data: { state: "deleted", changed: false } }, 2]);
  const ruta = { status: "error", code: "route_not_found", message: "Route not found" }; // tras el corte, una ruta que no existe es un error
  const s3 = await servidor(t, (n) => (n === 1 ? "corta" : [404, {}, ruta]));
  const e = await falla(() => cliente(s3, { retries: 1 }).delete("/speed-dials/9121"));
  assert.deepStrictEqual([e.http, e.code], [404, "route_not_found"]);
  const s2 = await servidor(t, () => [404, {}, no]); // sin fallo de red antes, un 404 es un error
  assert.strictEqual((await falla(() => cliente(s2, { retries: 1 }).delete("/speed-dials/9121"))).http, 404);
});

test("lastCursor es el del recorrido", async (t) => {
  const s = await servidor(t, (n, p) => (p.ruta.startsWith("/v3/vacia") ? [200, {}, { status: "success", data: { results: 0, calls: [], has_more: false, next_cursor: null } }]
    : p.ruta.startsWith("/v3/una") ? [200, {}, { status: "success", data: { results: 1, calls: [{ id: 1 }], has_more: false, next_cursor: "u1" } }]
      : [400, {}, { status: "error", code: "invalid_parameter", message: "Parameter cursor not valid" }]));
  const api = cliente(s, { retries: 0 });
  for await (const x of api.all("/vacia", { cursor: "c9" })) void x;
  assert.strictEqual(api.lastCursor, "c9");
  for await (const x of api.all("/una")) void x;
  assert.strictEqual(api.lastCursor, "u1");
  await falla(async () => { for await (const x of api.all("/error")) void x; });
  assert.strictEqual(api.lastCursor, null);
});

test("lastRateLimit: el cupo de la última respuesta", async (t) => {
  const s = await servidor(t, (n) => [200, n === 1 ? { "X-RateLimit-Limit": "10", "X-RateLimit-Remaining": "7", "X-RateLimit-Reset": "1" } : {}, OK]);
  const api = cliente(s);
  await api.get("/a");
  assert.deepStrictEqual(api.lastRateLimit, { limit: 10, remaining: 7, reset: 1 });
  await api.get("/b");
  assert.strictEqual(api.lastRateLimit, null);
});

test("las opciones", () => {
  for (const opc of [{ retries: -1 }, { timeout: 0 }, { baseUrl: "ftp://x/v3" }, { base: "x" }]) assert.throws(() => new MundoSMS(TOKEN, KEY, opc), TypeError);
  const visto = require("node:util").inspect(new MundoSMS(TOKEN, KEY), { showHidden: true }) + JSON.stringify(new MundoSMS(TOKEN, KEY));
  assert.ok(!visto.includes(TOKEN) && !visto.includes(KEY));
});
