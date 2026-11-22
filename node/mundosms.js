/**
 * MundoSMS API v3 — cliente Node.js en un solo fichero (Node 18 o superior: usa fetch). Sin dependencias.
 * Documentación: https://www.mundosms.es/api-v3
 * Código y versiones: https://github.com/mundosms/mundosms-sdk (licencia MIT)
 *
 *   const { MundoSMS, MundoSMSError } = require("./mundosms");
 *   const api = new MundoSMS("TOKEN", "CLAVE_PRIVADA");       // la clave privada, "" si tu API Key no exige firma
 *   const r = await api.post("/sms", { to: "34600123456", text: "Hola", from: "MiEmpresa" });
 *   console.log(r.data.id);
 *   for await (const llamada of api.todos("/calls", { from_datetime: "2026-11-01", stop_at_ongoing: true })) { ... }
 *
 * Errores: excepción MundoSMSError con .http (código HTTP), .statusCode ("3", "4"…), .message y .detail.
 * Reintenta solo, con espera creciente, lo que es seguro repetir: un 429 (no se hizo nada) y, en GET, los 5xx y los fallos
 * de red. Un POST o un PATCH que falla por la red NO se repite: no se sabe si llegó (usa client_id y consulta antes).
 */
"use strict";
const crypto = require("crypto");

const VERSION = "3.0.0";

class MundoSMSError extends Error {
  constructor(mensaje, http = 0, statusCode = null, detail = null, respuesta = null) {
    super(mensaje);
    this.name = "MundoSMSError";
    this.http = http;
    this.statusCode = statusCode;
    this.detail = detail;
    this.respuesta = respuesta;
  }
}

class MundoSMS {
  /** token: el Token de tu API Key (empieza por eyJ); clavePrivada: su clave privada si exige firma (lo normal), o "". */
  constructor(token, clavePrivada = "", { base = "https://api.mundosms.es/v3", reintentos = 3, plazo = 30 } = {}) {
    this.token = String(token).trim();
    this.clave = clavePrivada || "";
    this.base = base.replace(/\/+$/, "");
    this.reintentos = reintentos;
    this.plazo = plazo;
    this.ultimoCursor = null; // el de todos(): guárdalo para la siguiente sincronización
  }

  get(ruta, filtros = {}) { return this.peticion("GET", ruta, filtros); }
  post(ruta, datos = {}) { return this.peticion("POST", ruta, datos); }
  patch(ruta, datos) { return this.peticion("PATCH", ruta, datos); }
  delete(ruta, filtros = {}) { return this.peticion("DELETE", ruta, filtros); }

  /** Recorre un listado entero, página a página, siguiendo su cursor (next_cursor o next_from_id), elemento a elemento. */
  async *todos(ruta, filtros = {}) {
    filtros = { ...filtros };
    let lista = null;
    for (;;) {
      const data = (await this.get(ruta, filtros)).data || {};
      if (lista === null) lista = Object.keys(data).find((k) => Array.isArray(data[k])) || null;
      for (const e of lista ? data[lista] || [] : []) yield e;
      this.ultimoCursor = data.next_cursor ?? data.next_from_id ?? null;
      if (!data.has_more || !data.results) return;
      if (data.next_cursor) { delete filtros.from_datetime; delete filtros.order; filtros.cursor = data.next_cursor; }
      else if (data.next_from_id != null) filtros.from_id = data.next_from_id;
      else return;
    }
  }

  /** Una petición cualquiera. ruta: "/calls", "/numbers/34910123456"… Devuelve la respuesta decodificada. */
  async peticion(metodo, ruta, datos = {}) {
    metodo = metodo.toUpperCase();
    ruta = "/" + ruta.replace(/^\/+/, "");
    let query = "", cuerpo = "";
    if (metodo === "GET" || metodo === "DELETE") {
      query = Object.entries(datos || {})
        .filter(([, v]) => v !== null && v !== undefined)
        .map(([k, v]) => encodeURIComponent(k) + "=" + encodeURIComponent(typeof v === "boolean" ? String(v) : Array.isArray(v) ? v.join(",") : String(v)))
        .join("&");
    } else if (datos && Object.keys(datos).length) {
      cuerpo = JSON.stringify(datos);
    }
    const url = this.base + ruta + (query ? "?" + query : "");
    const camino = new URL(url).pathname + (query ? "?" + query : "");
    for (let intento = 1; ; intento++) {
      const ts = String(Date.now()); // milisegundos: dos peticiones iguales seguidas no firman igual
      const cab = { Authorization: "Bearer " + this.token, "X-Timestamp": ts, Accept: "application/json", "User-Agent": "MundoSMS-Node/" + VERSION };
      if (cuerpo) cab["Content-Type"] = "application/json";
      if (this.clave) {
        cab["X-Signature"] = crypto.createHmac("sha256", this.clave)
          .update(this.token + "." + ts + "." + metodo + " " + camino + "." + cuerpo).digest("base64url");
      }
      let http = 0, tipo = "", resp = null, errorRed = "";
      try {
        const r = await fetch(url, { method: metodo, headers: cab, body: cuerpo || undefined, signal: AbortSignal.timeout(this.plazo * 1000) });
        http = r.status;
        tipo = r.headers.get("content-type") || "";
        resp = Buffer.from(await r.arrayBuffer());
      } catch (e) {
        errorRed = String(e && e.message ? e.message : e);
      }
      const repetible = http === 429 || (metodo === "GET" && (errorRed || http >= 500));
      if (repetible && intento <= this.reintentos) {
        await new Promise((ok) => setTimeout(ok, Math.min(60, 2 ** (intento - 1)) * 1000));
        continue;
      }
      if (errorRed) throw new MundoSMSError("Sin respuesta de la API: " + errorRed);
      let j = null;
      try { j = JSON.parse(resp.toString("utf8")); } catch (e) { j = null; }
      if (http >= 200 && http < 300) return j && typeof j === "object" ? j : { status: "success", content_type: tipo, body: resp }; // un audio
      if (j && typeof j === "object") throw new MundoSMSError(j.message || "Error", http, j.status_code ?? null, j.detail ?? null, j);
      throw new MundoSMSError("HTTP " + http, http);
    }
  }
}

module.exports = { MundoSMS, MundoSMSError, VERSION };
