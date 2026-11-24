/**
 * MundoSMS API v3 — tipos de TypeScript del cliente Node.js (mundosms.js). Ponlo junto a mundosms.js (si lo guardas como
 * mundosms.cjs, llámalo mundosms.d.cts).
 * Documentación: https://www.mundosms.es/api-v3
 * Código y versiones: https://github.com/mundosms/mundosms-sdk (licencia MIT)
 *
 *   import { MundoSMS, MundoSMSError, type FileResponse } from "./mundosms";
 *   const api = new MundoSMS("TOKEN", "CLAVE_PRIVADA");
 *   const r = await api.post<{ id: number }>("/sms", { to: "34600123456", text: "Hola" });
 *   console.log(r.data?.id);
 *   const audio = await api.get<FileResponse>("/calls/out/8812345/recording");   // audio.body: Buffer
 *   for await (const call of api.all("/calls", { from_datetime: "2026-11-01" })) { ... }
 *   const ok = MundoSMS.verifyWebhook("whsec_…", req.headers["x-mundosms-signature"], rawBody);   // un aviso que te llega
 */
/// <reference types="node" />
// (los tipos usan los de Node, Buffer y AbortSignal: en un proyecto de TypeScript, instala @types/node)

/** Opciones del constructor (su tercer argumento). Una opción que no sea de estas, o un valor que no vale, da un `TypeError`. */
export interface MundoSMSOptions {
  /** La dirección de la API. Por defecto, `https://api.mundosms.es/v3`. */
  baseUrl?: string;
  /**
   * Cuántas veces se reintenta lo que es seguro repetir (0, ninguna): un 429 y un fallo de red en cualquier método, un 5xx en
   * GET y, en POST, un 409 request_in_progress. Por defecto, 3.
   */
  retries?: number;
  /** Los segundos que puede durar cada petición entera (conectar, mandar y recibir la respuesta). Admite decimales. Por defecto, 60. */
  timeout?: number;
}

/**
 * Un valor de la query en `get` y `delete`: `true`/`false` van como `true` y `false`, una lista separada por comas, y `null`,
 * `undefined` o una lista vacía no se mandan.
 */
export type QueryValue = string | number | boolean | null | undefined | ReadonlyArray<QueryValue>;

/** Los filtros de `get`, `delete` y `all`: van en la query. */
export type QueryParams = { readonly [name: string]: QueryValue };

/** Los datos de `post` y `patch`: van en un cuerpo JSON (un objeto). */
export type RequestBody = object;

/** Opciones de una petición (el último argumento de `post` y de `request`). */
export interface RequestOptions {
  /**
   * Tu `Idempotency-Key`, para que repetir este POST no lo vuelva a hacer (por ejemplo, el id de tu pedido): de 1 a 64
   * caracteres, letras sin tilde, cifras y `_ . : -`. Sin ella, un UUID nuevo en cada llamada (el mismo en todos sus
   * intentos). Con otra forma, salta un `TypeError` y no se manda nada. La API la tiene en cuenta en los POST.
   */
  idempotencyKey?: string | null;
}

/**
 * La respuesta de la API, decodificada. Un error no llega aquí: salta como `MundoSMSError`, también una respuesta 2xx con
 * `"status": "error"`. Un WhatsApp sin confirmar (202) sí llega aquí, con `data.state` `"unknown"`.
 * `T` es el tipo de `data`.
 */
export interface MundoSMSResponse<T = any> {
  status: "success";
  /** "OK", "N results" en los listados… */
  message: string;
  detail?: string | null;
  /** Los datos, cuando la ruta los devuelve. */
  data?: T;
  /**
   * Solo si la API devolvió la respuesta guardada de la primera vez (cabecera `Idempotent-Replayed: true`): un POST repetido
   * con la misma `Idempotency-Key`, que no se ha vuelto a hacer.
   */
  idempotent_replayed?: true;
}

/**
 * Lo que devuelven las rutas que contestan con un fichero en vez de JSON (la grabación de una llamada, una locución, el PDF
 * de un certificado). Pídelo como `get<FileResponse>(…)`. Una respuesta vacía (un 204) llega igual, con `body` vacío.
 */
export interface FileResponse {
  status: "success";
  /** La cabecera Content-Type de la respuesta ("audio/mpeg", "application/pdf"…), o "" si no la trae. */
  content_type: string;
  /** El contenido, tal cual. */
  body: Buffer;
  /** Solo si es la respuesta guardada de la primera vez (cabecera `Idempotent-Replayed: true`). */
  idempotent_replayed?: true;
}

/** `FileResponse` si se pide `get<FileResponse>(…)`; si no, `MundoSMSResponse<T>`. */
export type Result<T> = 0 extends 1 & T ? MundoSMSResponse<any> : [T] extends [FileResponse] ? FileResponse : MundoSMSResponse<T>;

/** La respuesta de error, tal como la mandó la API (en `MundoSMSError.response`). */
export interface ErrorResponse {
  status?: string;
  /** La palabra que dice qué falló, en inglés y snake_case: "not_found", "invalid_parameter", "duplicated_request"… */
  code?: string;
  message?: string;
  detail?: string | null;
  data?: any;
  /** Solo si es la respuesta guardada de la primera vez (un POST repetido con la misma Idempotency-Key). */
  idempotent_replayed?: true;
  [field: string]: unknown;
}

/** El cupo que dijo la última respuesta (sus cabeceras `X-RateLimit-*`), en `MundoSMS.lastRateLimit`. */
export interface RateLimit {
  /** `X-RateLimit-Limit`: las peticiones por segundo que admite tu clave. */
  limit: number | null;
  /** `X-RateLimit-Remaining`: las que te quedan en este segundo, contada esta. */
  remaining: number | null;
  /** `X-RateLimit-Reset`: los segundos que faltan para que se reponga el cupo. */
  reset: number | null;
}

/**
 * El error de cualquier petición: un 4xx o un 5xx, una redirección (no se siguen), una respuesta 2xx con `"status": "error"`
 * o que no es JSON (el HTML de un proxy) o, sin respuesta (la red o el plazo agotado), `http` 0 y un mensaje que empieza por
 * «No response from the API:» (y la causa en `cause`).
 */
export declare class MundoSMSError extends Error {
  constructor(
    message: string,
    http?: number,
    code?: string | null,
    detail?: string | null,
    response?: ErrorResponse | null,
    data?: any,
    requestId?: string | null,
  );
  name: "MundoSMSError";
  /** El código HTTP; 0 si no hubo respuesta. */
  http: number;
  /**
   * El `code` de la API: la palabra que dice qué falló, en inglés y snake_case ("not_found", "invalid_parameter",
   * "insufficient_credit"…). `null` si la respuesta no lo trae (sin respuesta, una redirección o una respuesta que no es JSON).
   */
  code: string | null;
  /** El `detail` de la respuesta, si lo trae. */
  detail: string | null;
  /** La respuesta entera, decodificada; `null` si no era JSON. */
  response: ErrorResponse | null;
  /** El `data` de la respuesta (en WhatsApp, el `id` del mensaje que puede haber salido); `null` si no lo trae. */
  data: any;
  /** La cabecera X-Request-Id: con ella encuentras la petición en Logs API. */
  requestId: string | null;
  /** Sin respuesta: el error de red de debajo (el de `fetch`). */
  cause?: unknown;
}

export declare class MundoSMS {
  /**
   * @param token      el Token de tu API Key (empieza por eyJ)
   * @param privateKey su clave privada si la API Key exige firma (lo normal); "" si no
   * @param options    baseUrl, retries y timeout
   */
  constructor(token: string, privateKey?: string | null, options?: MundoSMSOptions | null);

  /** La dirección de la API, sin la barra final. El token y la clave privada no se exponen. */
  baseUrl: string;
  retries: number;
  timeout: number;
  /**
   * El cursor del último `all()`: al empezar, el `cursor` que le pasas (o `null`), y después el último `next_cursor` que
   * llegó (una página sin cursor no lo borra). Guárdalo y pásalo como `cursor` la próxima vez. Uno por cliente.
   */
  lastCursor: string | null;
  /** El cupo que dijo la última respuesta (`X-RateLimit-*`), o `null` si no lo traía (o no hubo respuesta). */
  lastRateLimit: RateLimit | null;

  /** GET: `query` va en la query. */
  get<T = any>(path: string, query?: QueryParams | null): Promise<Result<T>>;
  /**
   * POST: `data` va en un cuerpo JSON. Lleva una `Idempotency-Key` (la de `options` o un UUID nuevo), la misma en todos sus
   * intentos: por eso se repite sin miedo ante un fallo de red o un 409 request_in_progress.
   */
  post<T = any>(path: string, data?: RequestBody | null, options?: RequestOptions | null): Promise<Result<T>>;
  /** PATCH: `data` va en un cuerpo JSON. */
  patch<T = any>(path: string, data: RequestBody): Promise<Result<T>>;
  /** DELETE: `query` va en la query. */
  delete<T = any>(path: string, query?: QueryParams | null): Promise<Result<T>>;

  /**
   * Recorre un listado entero, página a página, siguiendo su `next_cursor`, y da los elementos uno a uno. Cada página lleva
   * los mismos filtros (y el mismo `order`) más el cursor. `T` es el tipo de cada elemento. Termina con `has_more` false (con
   * `stop_at_ongoing`, también cuando la página se para en una llamada en curso) o si el cursor no avanza: una página vacía con
   * `has_more` true no es el final. `lastCursor` avanza cuando ya te ha dado todos los elementos de una página. Una lista que no va por páginas
   * (`has_more` sin `next_cursor`: `/calls/missed`, `/calls/live`) es un `MundoSMSError`, antes de dar nada.
   */
  all<T = any>(path: string, query?: QueryParams | null): AsyncGenerator<T, void, undefined>;

  /**
   * Una petición cualquiera. `path`: "/calls", "/numbers/34910123456"… (sin /v3: si lo lleva, se quita). Si la ruta trae
   * "?…", esos parámetros se juntan con `data` (gana `data`). En GET y DELETE, `data` va en la query; en POST y PATCH, en el
   * cuerpo JSON. `options.idempotencyKey`: la `Idempotency-Key` (en un POST, sin ella, un UUID nuevo).
   */
  request<T = any>(method: string, path: string, data?: RequestBody | QueryParams | null, options?: RequestOptions | null): Promise<Result<T>>;

  /**
   * `true` si un aviso que te llega (`callback_url` o webhook de WhatsApp) es nuestro y nadie lo ha tocado: comprueba la
   * cabecera `X-MundoSMS-Signature` (`t=<segundos>,v1=<firma>`) con tu secreto, en tiempo constante, y que `t` no se aleje de
   * tu hora más de `tolerance` segundos (300). Una cabecera que falta o está mal formada da `false`, sin excepción; un cuerpo
   * que no es crudo (un objeto ya decodificado), un `TypeError`.
   * @param secret    el de «API y Soporte → Webhooks» (whsec_…)
   * @param header    la cabecera X-MundoSMS-Signature
   * @param body      el cuerpo TAL CUAL llegó, sin volver a serializar el JSON (en Express, `express.raw({ type: "application/json" })`)
   * @param tolerance los segundos que puede alejarse `t` de tu hora (300)
   */
  static verifyWebhook(
    secret: string,
    header: string | string[] | null | undefined,
    body: Buffer | Uint8Array | ArrayBuffer | string,
    tolerance?: number,
  ): boolean;
}

export declare const VERSION: string;
