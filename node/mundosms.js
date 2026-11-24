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
 * Errores: excepción MundoSMSError con .http (código HTTP), .statusCode ("3", "4"…), .message, .detail, .data, .response (la
 * respuesta entera) y .requestId (la cabecera X-Request-Id). También cuando la API contesta con "status": "error", venga con
 * el código HTTP que venga. Un WhatsApp sin confirmar (202) no es un error: vuelve la respuesta, con data.state "unknown".
 * Cada POST lleva una Idempotency-Key (un UUID nuevo en cada llamada, la misma en todos sus intentos; la tuya, con
 * api.post(path, data, { idempotencyKey: "pedido-881" })). Por eso un POST que falla por la red, o con 409 «Request in
 * progress», se repite sin miedo: la API no lo hace dos veces. Si la respuesta es la guardada de la primera vez, trae
 * idempotent_replayed: true.
 * Reintenta: un 429, en cualquier método (no se hizo nada); un fallo de red, en cualquier método; un 5xx, solo en GET (un POST
 * con 5xx no se repite: la API no guarda los 5xx, y repetirlo lo haría otra vez). Espera lo que diga Retry-After (como mucho
 * 60 s) o, si no lo trae, 1, 2, 4… s.
 * Los avisos que te mandamos (callback_url y webhooks de WhatsApp) se comprueban con MundoSMS.verifyWebhook().
 */
"use strict";
const crypto = require("crypto");

const VERSION = "1.0.0";
const MAX_RETRY_WAIT = 60; // segundos: lo más que se espera antes de un reintento, aunque Retry-After pida más
const WEBHOOK_TOLERANCE = 300; // segundos: lo más que puede alejarse de tu hora la «t» de la firma de un aviso
const CLIENT_OPTIONS = ["baseUrl", "retries", "timeout"];

class MundoSMSError extends Error {
  constructor(message, http = 0, statusCode = null, detail = null, response = null, data = null, requestId = null) {
    super(message);
    this.name = "MundoSMSError";
    this.http = http;
    this.statusCode = statusCode;
    this.detail = detail;
    this.response = response;
    this.data = data;
    this.requestId = requestId;
  }
}

class MundoSMS {
  #token;
  #privateKey;

  /**
   * token: el Token de tu API Key (empieza por eyJ); privateKey: su clave privada si exige firma (lo normal), o "".
   * options: baseUrl (https://api.mundosms.es/v3), retries (3) y timeout: los segundos que puede durar cada petición entera
   * (conectar, mandar y recibir la respuesta), 60. El token y la clave no quedan a la vista en el objeto (ni en un console.log).
   */
  constructor(token, privateKey = "", options = {}) {
    if (options === null || options === undefined) options = {};
    if (typeof options !== "object" || Array.isArray(options)) throw new TypeError("MundoSMS: options must be an object: { baseUrl, retries, timeout }");
    for (const k of Object.keys(options)) {
      if (!CLIENT_OPTIONS.includes(k)) throw new TypeError('MundoSMS: unknown option "' + k + '" (options: baseUrl, retries, timeout)');
    }
    const { baseUrl = "https://api.mundosms.es/v3", retries = 3, timeout = 60 } = options;
    this.#token = String(token).trim();
    this.#privateKey = String(privateKey || "").trim();
    this.baseUrl = String(baseUrl).replace(/\/+$/, "");
    this.retries = retries;
    this.timeout = timeout;
    this.lastCursor = null; // el de all(): guárdalo para la siguiente sincronización
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
   * mismos filtros (y el mismo order) más el cursor. Para sincronizar, guarda tú lastCursor: la siguiente vez empiezas desde
   * ahí, pasándolo como cursor.
   */
  async *all(path, query = {}) {
    query = { ...query };
    let listKey = null;
    for (;;) {
      const data = (await this.get(path, query)).data || {};
      if (listKey === null) listKey = Object.keys(data).find((k) => Array.isArray(data[k])) || null;
      for (const item of listKey ? data[listKey] || [] : []) yield item;
      this.lastCursor = data.next_cursor ?? null;
      if (!data.has_more || !data.results || !data.next_cursor) return;
      query.cursor = data.next_cursor;
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
    for (let attempt = 1; ; attempt++) {
      const ts = String(Date.now()); // milisegundos: dos peticiones iguales seguidas no firman igual
      const headers = { Authorization: "Bearer " + this.#token, "X-Timestamp": ts, Accept: "application/json", "User-Agent": "MundoSMS-Node/" + VERSION };
      if (body) headers["Content-Type"] = "application/json";
      if (idemKey) headers["Idempotency-Key"] = idemKey; // la misma en todos los intentos de esta llamada
      if (this.#privateKey) {
        headers["X-Signature"] = crypto.createHmac("sha256", this.#privateKey)
          .update(this.#token + "." + ts + "." + method + " " + signedPath + "." + body).digest("base64url");
      }
      let http = 0, contentType = "", resp = null, networkError = "", r = null;
      try {
        r = await fetch(url, { method, headers, body: body || undefined,
                               signal: AbortSignal.timeout(Math.max(1, Math.round(this.timeout * 1000))) });
        http = r.status;
        contentType = r.headers.get("content-type") || "";
        resp = Buffer.from(await r.arrayBuffer());
      } catch (e) {
        http = 0; // se cortó a medias: es un fallo de red aunque llegara la cabecera
        networkError = e && e.name === "TimeoutError" ? "timed out after " + this.timeout + " s" : errorCause(e);
      }
      let j = null;
      if (!networkError) {
        try { j = JSON.parse(resp.toString("utf8")); } catch (e) { j = null; }
      }
      const isJson = j !== null && typeof j === "object";
      if (retryable(method, !!networkError, http, isJson ? j : null, idemKey) && attempt <= this.retries) {
        await new Promise((ok) => setTimeout(ok, retryWait(r && r.headers.get("retry-after"), attempt) * 1000));
        continue;
      }
      if (networkError) throw new MundoSMSError("No response from the API: " + networkError);
      const replayed = String(r.headers.get("idempotent-replayed") || "").trim().toLowerCase() === "true";
      if (replayed && isJson) j.idempotent_replayed = true; // la respuesta guardada de la primera vez: no se ha vuelto a hacer
      if (http >= 200 && http < 300) {
        if (!isJson) { // un fichero
          const file = { status: "success", content_type: contentType, body: resp };
          if (replayed) file.idempotent_replayed = true;
          return file;
        }
        if (j.status !== "error") return j;
      }
      const rid = r.headers.get("x-request-id");
      if (isJson) throw new MundoSMSError(j.message || "Error", http, j.status_code ?? null, j.detail ?? null, j, j.data ?? null, rid);
      throw new MundoSMSError("HTTP " + http, http, null, null, null, null, rid);
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
 * Si se puede repetir: un 429 en cualquier método; un GET, ante un fallo de red o un 5xx; un PATCH o un DELETE, ante un fallo
 * de red; un POST (con su Idempotency-Key), ante un fallo de red o un 409 «Request in progress». Un 5xx de un POST no: la API
 * no guarda los 5xx y repetirlo lo haría otra vez. Un 409 «Duplicated request» (la MISMA petición firmada llegó dos veces: la
 * repitió algo entre medias, como hace un cliente HTTP con una conexión que se cerró) es un fallo de red más.
 */
function retryable(method, network, http, j, idemKey) {
  if (http === 429) return true;
  const message = j !== null && typeof j.message === "string" ? j.message : null;
  network = network || (http === 409 && message !== null && message.startsWith("Duplicated request"));
  if (method === "GET") return network || http >= 500;
  if (method === "PATCH" || method === "DELETE") return network;
  if (idemKey) return network || (http === 409 && message === "Request in progress");
  return false;
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
 * los parámetros que traiga tras «?».
 */
function splitPath(path, stripV3) {
  path = String(path);
  const p = path.indexOf("?");
  const pathQuery = new Map(p >= 0 ? new URLSearchParams(path.slice(p + 1)) : []);
  let encoded = "/" + (p >= 0 ? path.slice(0, p) : path).replace(/^\/+/, "");
  if (stripV3 && (encoded === "/v3" || encoded.startsWith("/v3/"))) encoded = "/" + encoded.slice(3).replace(/^\/+/, "");
  encoded = encoded.split("/").map((seg) => seg.split(/(%[0-9A-Fa-f]{2})/).map((x, i) => (i % 2 ? x : rfc3986(x))).join("")).join("/");
  return [encoded, pathQuery];
}

/** Segundos antes del siguiente intento: lo que pida Retry-After (segundos o fecha HTTP), o 1, 2, 4… Como mucho 60. */
function retryWait(retryAfter, attempt) {
  let s = null;
  if (retryAfter) {
    const n = Number(retryAfter);
    s = Number.isFinite(n) ? n : (Date.parse(retryAfter) - Date.now()) / 1000;
    if (!Number.isFinite(s)) s = null;
  }
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
