/**
 * MundoSMS API v3 — cliente Node.js en un solo fichero (Node 18 o superior: usa fetch). Sin dependencias. Es CommonJS: en un
 * proyecto ESM ("type": "module"), guárdalo como mundosms.cjs e impórtalo con import { MundoSMS } from "./mundosms.cjs".
 * Documentación: https://www.mundosms.es/api-v3
 * Código y versiones: https://github.com/mundosms/mundosms-sdk (licencia MIT)
 *
 *   const { MundoSMS, MundoSMSError } = require("./mundosms");
 *   const api = new MundoSMS("TOKEN", "CLAVE_PRIVADA");       // la clave privada, "" si tu API Key no exige firma
 *   const r = await api.post("/sms", { to: "34600123456", text: "Hola", from: "MiEmpresa" });
 *   console.log(r.data.id);
 *   for await (const call of api.all("/calls", { from_datetime: "2026-11-01", stop_at_ongoing: true })) { ... }
 *
 * Errores: excepción MundoSMSError con .http (código HTTP), .code (la palabra de la API: "not_found", "invalid_parameter"…),
 * .message, .detail, .data, .response (la respuesta entera), .requestId (la cabecera X-Request-Id) e .idempotencyKey (la
 * Idempotency-Key que llevó la petición: la de un POST, para repetirlo tú con la misma). También cuando la API
 * contesta con "status": "error", venga con el código HTTP que venga. Un WhatsApp sin confirmar (202) no es un error: vuelve la respuesta, con data.state "unknown".
 * Cada POST lleva una Idempotency-Key (un UUID nuevo en cada llamada, la misma en todos sus intentos; la tuya, con
 * api.post(path, data, { idempotencyKey: "pedido-881" })). Por eso un POST que falla por la red, o con 409
 * request_in_progress, se repite sin miedo: la API no lo hace dos veces. Si la respuesta es la guardada de la primera vez, trae
 * idempotent_replayed: true.
 * Un 409 request_in_progress (la primera petición con esa clave aún se está haciendo: un WhatsApp con un fichero puede tardar
 * 2 o 3 minutos) se repite con la misma clave hasta IN_PROGRESS_WAIT (180 s) desde el primero, sin contar en retries: espera
 * lo que diga Retry-After (al menos 1 s) o 1, 2, 4, 8 y luego 15 s. Si Retry-After pide más de lo que queda, o se acaban los
 * 180 s, es un MundoSMSError con su .idempotencyKey: repítelo más tarde con ella. Con retries 0, no se repite.
 * Reintenta: un 429, en cualquier método (no se hizo nada); un fallo de red, en cualquier método; un 5xx, solo en GET (un POST
 * con 5xx no se repite: un 500 puede llegar con la operación hecha, y la API lo guarda con la Idempotency-Key, así que
 * repetirlo con ella devolvería el mismo 500; un 503 no hizo nada, y puedes repetirlo tú con la misma clave). Espera lo que
 * diga Retry-After (como mucho 60 s) o, si no lo trae, 1, 2, 4… s. Un DELETE que se repite tras quedarse sin respuesta y da 404 ya estaba hecho: vuelve
 * como éxito, con data.changed false.
 * Una redirección (3xx) no se sigue nunca: es un MundoSMSError (la API no redirige, y seguirla llevaría tu firma y tus datos
 * a otro servidor). Un 2xx que no es JSON (el HTML de un proxy o de un portal cautivo) también es un error; los ficheros que
 * da la API (audio, imagen, PDF) vuelven por su Content-Type.
 * Los avisos que te mandamos (callback_url y webhooks de WhatsApp) se comprueban con MundoSMS.verifyWebhook().
 */
"use strict";
const crypto = require("crypto");

const VERSION = "2.1.1";
const MAX_RETRY_WAIT = 60; // segundos: lo más que se espera antes de un reintento, aunque Retry-After pida más
const IN_PROGRESS_WAIT = 180; // segundos: lo más que se espera, con la misma Idempotency-Key, a un POST que sigue en marcha (409 request_in_progress)
const IN_PROGRESS_STEP = 15; // segundos: lo más que se espera entre dos intentos de ese POST si la API no manda Retry-After
const WEBHOOK_TOLERANCE = 300; // segundos: lo más que puede alejarse de tu hora la «t» de la firma de un aviso
const CLIENT_OPTIONS = ["baseUrl", "retries", "timeout"];
const FILE_TYPES = /^(audio\/|image\/|application\/pdf$|application\/octet-stream$)/; // lo que la API da como fichero
const NOT_PAGINATED = "MundoSMS: all() cannot go through %s: it has more (has_more) but no next_cursor, so the list is not paginated: " +
  "raise limit or narrow the filters, and read it with get()";
let lastTimestamp = 0; // el X-Timestamp de la última petición de este proceso (milisegundos)

class MundoSMSError extends Error {
  constructor(message, http = 0, code = null, detail = null, response = null, data = null, requestId = null, idempotencyKey = null) {
    super(message);
    this.name = "MundoSMSError";
    this.http = http;
    this.code = code;
    this.detail = detail;
    this.response = response;
    this.data = data;
    this.requestId = requestId;
    this.idempotencyKey = idempotencyKey; // la Idempotency-Key que llevó la petición (la de un POST); null si no llevó
  }
}

class MundoSMS {
  #token;
  #privateKey;

  /**
   * token: el Token de tu API Key (empieza por eyJ); privateKey: su clave privada si exige firma (lo normal), o "".
   * options: baseUrl (https://api.mundosms.es/v3), retries (3) y timeout: los segundos que puede durar cada petición entera
   * (conectar, mandar y recibir la respuesta), 60. Un valor que no vale es un TypeError. El token y la clave no quedan a la vista
   * en el objeto (ni en un console.log).
   */
  constructor(token, privateKey = "", options = {}) {
    if (options === null || options === undefined) options = {};
    if (typeof options !== "object" || Array.isArray(options)) throw new TypeError("MundoSMS: options must be an object: { baseUrl, retries, timeout }");
    for (const k of Object.keys(options)) {
      if (!CLIENT_OPTIONS.includes(k)) throw new TypeError('MundoSMS: unknown option "' + k + '" (options: baseUrl, retries, timeout)');
    }
    const { baseUrl = "https://api.mundosms.es/v3", retries = 3, timeout = 60 } = options;
    let u = null;
    try { u = new URL(String(baseUrl).replace(/\/+$/, "")); } catch (e) { u = null; }
    if (!u || (u.protocol !== "https:" && u.protocol !== "http:") || u.search || u.hash) {
      throw new TypeError("MundoSMS: baseUrl must be an http(s) address without query, like https://api.mundosms.es/v3");
    }
    if (!Number.isInteger(retries) || retries < 0) throw new TypeError("MundoSMS: retries must be an integer, 0 or more");
    if (typeof timeout !== "number" || !(timeout > 0) || !Number.isFinite(timeout)) {
      throw new TypeError("MundoSMS: timeout must be a number of seconds greater than 0");
    }
    this.#token = String(token).trim();
    this.#privateKey = String(privateKey || "").trim();
    this.baseUrl = String(baseUrl).replace(/\/+$/, "");
    this.retries = retries;
    this.timeout = timeout;
    this.lastCursor = null; // el del último all(): guárdalo para la siguiente sincronización
    this.lastRateLimit = null; // el cupo que dijo la última respuesta: { limit, remaining, reset }, o null
  }

  /** GET: query va en la query. */
  get(path, query = {}) { return this.request("GET", path, query); }
  /**
   * POST: data va en un cuerpo JSON. options: { idempotencyKey: "pedido-881" }, tu clave para que repetir este POST no lo
   * vuelva a hacer; sin ella, un UUID nuevo.
   */
  post(path, data = {}, options = {}) { return this.request("POST", path, data, options); }
  /** PATCH: data va en un cuerpo JSON. */
  patch(path, data) { return this.request("PATCH", path, data); }
  /** DELETE: query va en la query. */
  delete(path, query = {}) { return this.request("DELETE", path, query); }

  /**
   * Recorre un listado entero, página a página, siguiendo su cursor (next_cursor), elemento a elemento. Cada página lleva los
   * mismos filtros (y el mismo order) más el cursor. Para sincronizar, guarda tú lastCursor al acabar: la siguiente vez
   * empiezas desde ahí, pasándolo como cursor. lastCursor es el de este recorrido: al empezar, el cursor que le pasas (o null),
   * y después el último next_cursor que llegó (una página sin cursor no lo borra). Es uno por cliente: para dos recorridos a
   * la vez, un cliente para cada uno. lastCursor avanza cuando ya te ha dado todos los elementos de una página: si tu código
   * falla a mitad de una, sigue en la anterior.
   * Termina con has_more false (con stop_at_ongoing, también cuando la página se para en una llamada en curso), o si el cursor
   * no avanza. Una página vacía con has_more true (con stop_at_ongoing y un filtro, cuando ha mirado muchas que no casan) no es
   * el final: sigue.
   * Una lista que no va por páginas (GET /calls/missed y /calls/live: has_more sin next_cursor) no se puede recorrer entera: es
   * un MundoSMSError, antes de dar nada. Sube limit, o filtra, y pídela con get().
   */
  async *all(path, query = {}) {
    query = { ...query };
    this.lastCursor = query.cursor === undefined || query.cursor === null || query.cursor === "" ? null : query.cursor;
    let listKey = null;
    for (;;) {
      const r = await this.get(path, query);
      const data = r.data && typeof r.data === "object" && !Array.isArray(r.data) ? r.data : {};
      const cursor = data.next_cursor;
      if (data.has_more && data.results && !cursor) {
        throw new MundoSMSError(NOT_PAGINATED.replace("%s", path), 200, null, null, r, data, null);
      }
      if (listKey === null) listKey = Object.keys(data).find((k) => Array.isArray(data[k])) || null;
      for (const item of listKey ? data[listKey] || [] : []) yield item;
      if (cursor) this.lastCursor = cursor;
      if (!data.has_more || !cursor || cursor === query.cursor || data.ongoing_call) return;
      query.cursor = cursor;
    }
  }

  /**
   * Una petición cualquiera. path: "/calls", "/numbers/34910123456"… (sin /v3: si lo lleva, se quita). Si la ruta trae "?…",
   * esos parámetros se juntan con data (gana data). En GET y DELETE, data va en la query; en POST y PATCH, en el cuerpo JSON.
   * Devuelve la respuesta decodificada.
   * options: { idempotencyKey }, la Idempotency-Key (en un POST, sin ella, un UUID nuevo); la API la tiene en cuenta en los POST.
   */
  async request(method, path, data = {}, options = {}) {
    method = method.toUpperCase();
    const idemKey = idempotencyKey(options, method); // antes de nada: con una clave mala no se manda nada
    const [encodedPath, pathQuery] = splitPath(path, new URL(this.baseUrl).pathname.endsWith("/v3"));
    let body = "";
    if (method === "GET" || method === "DELETE") {
      for (const [k, v] of Object.entries(data || {})) pathQuery.set(k, v);
    } else if (data && Object.keys(data).length) {
      body = JSON.stringify(data);
    }
    const query = [...pathQuery].map(([k, v]) => [k, queryValue(v)]).filter(([, t]) => t !== null)
      .map(([k, t]) => rfc3986(k) + "=" + rfc3986(t)).join("&");
    const url = this.baseUrl + encodedPath + (query ? "?" + query : "");
    const signedPath = new URL(url).pathname + (query ? "?" + query : ""); // lo que se firma es justo lo que se manda
    let afterNetwork = false; // un intento de antes se quedó sin respuesta (o llegó dos veces): puede que se hiciera
    const inProgress = []; // cuándo llegó cada 409 request_in_progress de esta llamada (reloj monotónico, en segundos)
    for (let attempt = 1; ; attempt++) {
      const ts = timestamp(); // dos peticiones iguales a la vez, o un reintento inmediato, no firman igual
      const headers = { Authorization: "Bearer " + this.#token, "X-Timestamp": ts, Accept: "application/json", "User-Agent": "MundoSMS-Node/" + VERSION };
      if (body) headers["Content-Type"] = "application/json";
      if (idemKey) headers["Idempotency-Key"] = idemKey; // la misma en todos los intentos de esta llamada
      if (this.#privateKey) {
        headers["X-Signature"] = crypto.createHmac("sha256", this.#privateKey)
          .update(this.#token + "." + ts + "." + method + " " + signedPath + "." + body).digest("base64url");
      }
      let http = 0, contentType = "", resp = null, networkError = "", cause = null, r = null;
      try {
        // redirect "manual": la redirección no se sigue (fetch la sigue por defecto, y llevaría la firma, la Idempotency-Key y,
        // con un 307, el cuerpo a otro servidor); el 3xx llega como respuesta y es un error
        r = await fetch(url, { method, headers, body: body || undefined, redirect: "manual",
                               signal: AbortSignal.timeout(Math.max(1, Math.round(this.timeout * 1000))) });
        http = r.status;
        contentType = r.headers.get("content-type") || "";
        resp = Buffer.from(await r.arrayBuffer());
      } catch (e) {
        http = 0; // se cortó a medias: es un fallo de red aunque llegara la cabecera
        networkError = e && e.name === "TimeoutError" ? "timed out after " + this.timeout + " s" : errorCause(e);
        cause = e;
      }
      const redirect = !networkError && http >= 300 && http < 400; // de una redirección no se lee nada
      let j = null;
      if (!networkError && !redirect) {
        try { j = JSON.parse(resp.toString("utf8")); } catch (e) { j = null; }
      }
      const isJson = j !== null && typeof j === "object" && !Array.isArray(j);
      if (!isJson) j = null;
      if (this.retries > 0 && inProgressCode(method, http, j, idemKey)) {
        inProgress.push(performance.now() / 1000);
        const wait = inProgressWait(r.headers.get("retry-after"), inProgress.length,
                                    IN_PROGRESS_WAIT - (inProgress[inProgress.length - 1] - inProgress[0]));
        if (wait !== null) { // la primera con esta clave sigue en marcha: se espera y se repite, sin gastar retries
          attempt--;
          await new Promise((ok) => setTimeout(ok, wait * 1000));
          continue;
        }
      } else if (retryable(method, !!networkError, http, j, idemKey) && attempt <= this.retries) {
        afterNetwork = afterNetwork || !!networkError || duplicated(http, j);
        await new Promise((ok) => setTimeout(ok, retryWait(r && r.headers.get("retry-after"), attempt) * 1000));
        continue;
      }
      if (networkError) {
        this.lastRateLimit = null;
        const err = new MundoSMSError("No response from the API: " + networkError, 0, null, null, null, null, null, idemKey);
        if (cause) err.cause = cause;
        throw err;
      }
      this.lastRateLimit = rateLimit(r.headers);
      const rid = r.headers.get("x-request-id");
      if (redirect) throw new MundoSMSError(redirected(http, r.headers.get("location")), http, null, null, null, null, rid, idemKey);
      const replayed = String(r.headers.get("idempotent-replayed") || "").trim().toLowerCase() === "true";
      if (replayed && isJson) j.idempotent_replayed = true; // la respuesta guardada de la primera vez: no se ha vuelto a hacer
      if (http >= 200 && http < 300) {
        if (!isJson) { // un fichero, o algo que no es de la API
          if (resp.length && !FILE_TYPES.test(contentType.split(";")[0].trim().toLowerCase())) {
            throw new MundoSMSError("HTTP " + http + ": the response is not JSON (" + (contentType.split(";")[0].trim() || "no Content-Type") + ")",
                                    http, null, null, null, null, rid, idemKey);
          }
          const file = { status: "success", content_type: contentType, body: resp };
          if (replayed) file.idempotent_replayed = true;
          return file;
        }
        if (j.status !== "error") return j;
      }
      if (method === "DELETE" && afterNetwork && http === 404 && isJson && code(j) !== "route_not_found") {
        // lo borró el intento que se quedó sin respuesta
        return { status: "success", message: "Already deleted", data: { state: "deleted", changed: false } };
      }
      if (isJson) throw new MundoSMSError(j.message || "Error", http, code(j), j.detail ?? null, j, j.data ?? null, rid, idemKey);
      throw new MundoSMSError("HTTP " + http, http, null, null, null, null, rid, idemKey);
    }
  }

  /**
   * true si un aviso que te llega (callback_url o webhook de WhatsApp) es nuestro y nadie lo ha tocado.
   * secret: el de «API y Soporte → Webhooks» (whsec_…); header: X-MundoSMS-Signature; body: el cuerpo TAL CUAL llegó (Buffer,
   * Uint8Array o string), sin volver a serializar el JSON (en Express, express.raw({ type: "application/json" }));
   * tolerance: los segundos que puede alejarse «t» de tu hora. Una cabecera que falta o está mal formada da false, sin excepción.
   */
  static verifyWebhook(secret, header, body, tolerance = WEBHOOK_TOLERANCE) {
    let b;
    if (typeof body === "string") b = Buffer.from(body, "utf8");
    else if (body instanceof Uint8Array) b = Buffer.from(body.buffer, body.byteOffset, body.byteLength);
    else if (body instanceof ArrayBuffer) b = Buffer.from(body);
    else throw new TypeError("MundoSMS.verifyWebhook: pass the raw body as received (Buffer or string), not a decoded value");
    const s = typeof secret === "string" ? secret.trim() : "";
    if (Array.isArray(header)) header = header.join(","); // la cabecera repetida: con dos «t», no vale
    if (!s || typeof header !== "string") return false;
    let t = null;
    const signatures = [];
    for (const part of header.split(",")) {
      const p = part.indexOf("=");
      if (p < 0) continue;
      const k = part.slice(0, p).trim(), v = part.slice(p + 1).trim();
      if (k === "t") {
        if (t !== null) return false; // dos «t»: no se sabe cuál vale
        t = v;
      } else if (k === "v1") signatures.push(v.toLowerCase());
    }
    if (t === null || !/^[0-9]{1,15}$/.test(t) || !signatures.length) return false;
    if (Math.abs(Math.floor(Date.now() / 1000) - Number(t)) > tolerance) return false;
    const expected = Buffer.from(crypto.createHmac("sha256", s).update(t + ".").update(b).digest("hex"));
    let valid = false;
    for (const sig of signatures) { // todas, sin cortar en la primera: timingSafeEqual es de tiempo constante
      if (/^[0-9a-f]{64}$/.test(sig) && crypto.timingSafeEqual(Buffer.from(sig), expected)) valid = true;
    }
    return valid;
  }
}

/**
 * El X-Timestamp de una petición: milisegundos, y siempre mayor que el de la anterior de este proceso. Dos peticiones iguales
 * en el mismo milisegundo (dos a la vez con Promise.all, o un reintento inmediato) firmarían igual, y la API rechazaría la
 * segunda por repetida («409 duplicated_request»).
 */
function timestamp() {
  lastTimestamp = Math.max(Date.now(), lastTimestamp + 1);
  return String(lastTimestamp);
}

/** La Idempotency-Key de la llamada: la tuya, si tiene la forma que exige la API; en un POST sin ella, un UUID v4 nuevo. */
function idempotencyKey(options, method) {
  if (options === null || options === undefined) options = {};
  if (typeof options !== "object" || Array.isArray(options)) throw new TypeError("MundoSMS: options must be an object: { idempotencyKey }");
  for (const k of Object.keys(options)) {
    if (k !== "idempotencyKey") throw new TypeError('MundoSMS: unknown option "' + k + '" (the only option is idempotencyKey)');
  }
  const k = options.idempotencyKey;
  if (k === undefined || k === null) return method === "POST" ? crypto.randomUUID() : null;
  if (typeof k !== "string" || !/^[A-Za-z0-9_.:-]{1,64}$/.test(k)) {
    throw new TypeError("MundoSMS: idempotencyKey must be 1 to 64 characters: A-Z, a-z, 0-9 and _ . : - (nothing was sent)");
  }
  return k;
}

/**
 * Si se puede repetir (hasta retries veces): un 429 en cualquier método; un GET, ante un fallo de red o un 5xx; un PATCH, un
 * DELETE o un POST con su Idempotency-Key, ante un fallo de red. Un 5xx de un POST no: un 500 puede haberse hecho y la API lo
 * guarda con la clave (repetirlo devolvería el mismo 500), y un 503 no hizo nada, pero repetirlo con la misma clave se deja a
 * tu código. Un 409 duplicated_request (la MISMA petición firmada llegó dos veces: la repitió algo entre medias, como hace un
 * cliente HTTP con una conexión que se cerró) es un fallo de red más. El 409 request_in_progress va aparte (inProgressCode):
 * se espera por tiempo, no por intentos.
 */
function retryable(method, network, http, j, idemKey) {
  if (http === 429) return true;
  network = network || duplicated(http, j);
  if (method === "GET") return network || http >= 500;
  if (method === "PATCH" || method === "DELETE") return network;
  if (idemKey) return network;
  return false;
}

/** 409 request_in_progress en un POST con su Idempotency-Key: la primera petición con esa clave aún no ha terminado. */
function inProgressCode(method, http, j, idemKey) {
  return method === "POST" && !!idemKey && http === 409 && code(j) === "request_in_progress";
}

/**
 * Segundos antes de repetir, con la misma clave, un POST que sigue en marcha (tras su n-ésimo 409 request_in_progress), o null
 * si ya no se espera: lo que pida Retry-After (al menos 1 s y como mucho MAX_RETRY_WAIT) o, sin él, 1, 2, 4, 8 y luego
 * IN_PROGRESS_STEP. remaining: lo que queda de IN_PROGRESS_WAIT. Si Retry-After pide más, o la espera no cabe, null.
 */
function inProgressWait(retryAfter, n, remaining) {
  let s = retryAfterSeconds(retryAfter);
  if (s === null) s = Math.min(2 ** (n - 1), IN_PROGRESS_STEP);
  else if (s > remaining) return null;
  s = Math.max(1, Math.min(MAX_RETRY_WAIT, s));
  return s <= remaining ? s : null;
}

/** 409 duplicated_request: la MISMA petición firmada llegó dos veces (la repitió algo entre medias). */
function duplicated(http, j) {
  return http === 409 && code(j) === "duplicated_request";
}

/** El code de una respuesta de error ("duplicated_request"…) como texto, o null si no lo trae. */
function code(j) {
  if (j === null || typeof j !== "object" || j.code === undefined || j.code === null) return null;
  return typeof j.code === "string" ? j.code : JSON.stringify(j.code);
}

/** El error de una redirección, con adónde mandaba (las redirecciones no se siguen: la API no redirige). */
function redirected(http, location) {
  const where = location ? " to " + String(location).trim().slice(0, 300) : "";
  return "HTTP " + http + ": redirect" + where + " not followed (the API does not redirect: check baseUrl)";
}

/** El cupo de la respuesta (X-RateLimit-Limit, -Remaining y -Reset, en enteros), o null si no lo trae. */
function rateLimit(h) {
  const out = {};
  let any = false;
  for (const k of ["limit", "remaining", "reset"]) {
    const v = h.get("x-ratelimit-" + k);
    const n = v === null ? NaN : Number(String(v).trim());
    out[k] = v !== null && String(v).trim() !== "" && Number.isInteger(n) ? n : null;
    if (out[k] !== null) any = true;
  }
  return any ? out : null;
}

/** RFC 3986: todo codificado menos A-Z a-z 0-9 - . _ ~ (encodeURIComponent deja sin codificar ! ' ( ) *). */
function rfc3986(s) {
  return encodeURIComponent(s).replace(/[!'()*]/g, (c) => "%" + c.charCodeAt(0).toString(16).toUpperCase());
}

/** Un valor de la query: true/false, una lista separada por comas; null o una lista vacía (o solo de null), nada. */
function queryValue(v) {
  if (v === null || v === undefined) return null;
  if (typeof v === "boolean") return v ? "true" : "false";
  if (Array.isArray(v)) {
    const items = v.map(queryValue).filter((x) => x !== null);
    return items.length ? items.join(",") : null;
  }
  return String(v);
}

/**
 * La ruta codificada trozo a trozo (un %XX ya escrito se respeta), sin el /v3 del principio si la base ya lo lleva, y
 * los parámetros que traiga tras «?»: con su %XX decodificado y el «+» como «+» (RFC 3986; no es un formulario), para que
 * una fecha con zona escrita en la ruta (from_datetime=…+01:00) llegue tal cual.
 */
function splitPath(path, stripV3) {
  path = String(path);
  const p = path.indexOf("?");
  const pathQuery = new Map();
  if (p >= 0) {
    for (const pair of path.slice(p + 1).split("&")) {
      if (!pair) continue;
      const e = pair.indexOf("=");
      pathQuery.set(percentDecode(e >= 0 ? pair.slice(0, e) : pair), percentDecode(e >= 0 ? pair.slice(e + 1) : ""));
    }
  }
  let encoded = "/" + (p >= 0 ? path.slice(0, p) : path).replace(/^\/+/, "");
  if (stripV3 && (encoded === "/v3" || encoded.startsWith("/v3/"))) encoded = "/" + encoded.slice(3).replace(/^\/+/, "");
  encoded = encoded.split("/").map((seg) => seg.split(/(%[0-9A-Fa-f]{2})/).map((x, i) => (i % 2 ? x : rfc3986(x))).join("")).join("/");
  return [encoded, pathQuery];
}

/** Un trozo de la query de la ruta: cada %XX, su byte (en UTF-8); lo demás (también el «+») tal cual. */
function percentDecode(s) {
  const bytes = [];
  for (let i = 0; i < s.length; ) {
    if (s[i] === "%" && /^[0-9A-Fa-f]{2}$/.test(s.slice(i + 1, i + 3))) {
      bytes.push(parseInt(s.slice(i + 1, i + 3), 16));
      i += 3;
    } else {
      const cp = s.codePointAt(i);
      const ch = String.fromCodePoint(cp);
      for (const b of Buffer.from(ch, "utf8")) bytes.push(b);
      i += ch.length;
    }
  }
  return Buffer.from(bytes).toString("utf8");
}

/** Los segundos que pide Retry-After (segundos o fecha HTTP), o null si no viene o no se entiende. */
function retryAfterSeconds(retryAfter) {
  if (!retryAfter) return null;
  const n = Number(retryAfter);
  const s = Number.isFinite(n) ? n : (Date.parse(retryAfter) - Date.now()) / 1000;
  return Number.isFinite(s) ? s : null;
}

/** Segundos antes del siguiente intento: lo que pida Retry-After (segundos o fecha HTTP), o 1, 2, 4… Como mucho 60. */
function retryWait(retryAfter, attempt) {
  let s = retryAfterSeconds(retryAfter);
  if (s === null) s = 2 ** (attempt - 1);
  return Math.max(0, Math.min(MAX_RETRY_WAIT, s));
}

/** El motivo de un fallo de red: fetch solo dice «fetch failed» y la causa (ECONNREFUSED, ENOTFOUND…) va en e.cause. */
function errorCause(e) {
  const t = e && e.message ? e.message : String(e);
  const c = e && e.cause;
  if (!c) return t;
  const d = c.message || (Array.isArray(c.errors) ? c.errors.map((x) => x && x.message).filter(Boolean).join("; ") : "") || c.code || String(c);
  return d && !t.includes(d) ? t + ": " + d : t;
}

module.exports = { MundoSMS, MundoSMSError, VERSION };
