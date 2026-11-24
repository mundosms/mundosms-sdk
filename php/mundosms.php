<?php
/**
 * MundoSMS API v3 — cliente PHP en un solo fichero (PHP 7.4 o superior, con la extensión curl). Sin dependencias.
 * Documentación: https://www.mundosms.es/api-v3
 * Código y versiones: https://github.com/mundosms/mundosms-sdk (licencia MIT)
 *
 *   require "mundosms.php";
 *   $api = new MundoSMS("TOKEN", "CLAVE_PRIVADA");            // la clave privada, "" si tu API Key no exige firma
 *   $r = $api->post("/sms", ["to" => "34600123456", "text" => "Hola", "from" => "MiEmpresa"]);
 *   echo $r["data"]["id"];
 *   foreach ($api->all("/calls", ["from_datetime" => "2026-11-01", "stop_at_ongoing" => true]) as $call) { ... }
 *
 * Errores: excepción MundoSMSError con ->http (código HTTP), ->code (la palabra de la API: "not_found", "invalid_parameter"…;
 * también getCode()), getMessage(), ->detail, ->data, ->response (la respuesta entera) y ->requestId (la cabecera
 * X-Request-Id). También cuando la API contesta con "status": "error", venga con el código HTTP que venga. Un WhatsApp sin confirmar (202) no es un error: vuelve la respuesta,
 * con data["state"] "unknown".
 * Cada POST lleva una Idempotency-Key (un UUID nuevo en cada llamada, la misma en todos sus intentos; la tuya, con
 * $api->post($path, $data, ["idempotencyKey" => "pedido-881"])). Por eso un POST que falla por la red, o con 409
 * request_in_progress, se repite sin miedo: la API no lo hace dos veces. Si la respuesta es la guardada de la primera vez, trae
 * "idempotent_replayed" => true.
 * Reintenta: un 429, en cualquier método (no se hizo nada); un fallo de red, en cualquier método; un 5xx, solo en GET (un POST
 * con 5xx no se repite: un 500 puede llegar con la operación hecha, y la API lo guarda con la Idempotency-Key, así que
 * repetirlo con ella devolvería el mismo 500; un 503 no hizo nada, y puedes repetirlo tú con la misma clave). Espera lo que
 * diga Retry-After (como mucho 60 s) o, si no lo trae, 1, 2, 4… s. Un DELETE que se repite tras quedarse sin respuesta y da 404 ya estaba hecho: vuelve
 * como éxito, con data["changed"] false.
 * Una redirección (3xx) no se sigue nunca: es un MundoSMSError (la API no redirige). Un 2xx que no es JSON (el HTML de un
 * proxy o de un portal cautivo) también es un error; los ficheros que da la API (audio, imagen, PDF) vuelven por su
 * Content-Type.
 * Los avisos que te mandamos (callback_url y webhooks de WhatsApp) se comprueban con MundoSMS::verifyWebhook().
 */

class MundoSMSError extends Exception
{
    /** @var int el código HTTP; 0 si no hubo respuesta */
    public $http;
    /** @var string|null el code de la API ("not_found", "invalid_parameter"…); null si la respuesta no lo trae */
    public $code;
    /** @var string|null el detail de la respuesta, si lo trae */
    public $detail;
    /** @var array|null la respuesta entera, decodificada; null si no era JSON */
    public $response;
    /** @var mixed el data de la respuesta (en WhatsApp, el id del mensaje que puede haber salido) */
    public $data;
    /** @var string|null la cabecera X-Request-Id: con ella encuentras la petición en Logs API */
    public $requestId;

    public function __construct($message, $http = 0, $code = null, $detail = null, $response = null, $data = null, $requestId = null)
    {
        parent::__construct((string) $message);
        $this->http = (int) $http;
        $this->code = $code;
        $this->detail = $detail;
        $this->response = $response;
        $this->data = $data;
        $this->requestId = $requestId;
    }
}

class MundoSMS
{
    const VERSION = "2.0.0";
    const MAX_RETRY_WAIT = 60;      // segundos: lo más que se espera antes de un reintento, aunque Retry-After pida más
    const WEBHOOK_TOLERANCE = 300;  // segundos: lo más que puede alejarse de tu hora la «t» de la firma de un aviso
    const NOT_PAGINATED = "MundoSMS: all() cannot go through %s: it has more (has_more) but no next_cursor, so the list is not paginated: raise limit or narrow the filters, and read it with get()";

    /** @var int el X-Timestamp de la última petición de este proceso (milisegundos): el siguiente siempre es mayor */
    private static $lastTimestamp = 0;

    private $token;
    private $privateKey;
    private $baseUrl;
    private $retries;
    private $timeout;

    /**
     * El cursor del último listado recorrido con all(): guárdalo para la siguiente sincronización. Al empezar el recorrido, el
     * cursor que le pasas (o null); después, el último next_cursor que llegó (una página sin cursor no lo borra).
     * @var string|null
     */
    public $lastCursor = null;

    /**
     * El cupo que dijo la última respuesta (sus cabeceras X-RateLimit-*): ["limit" => 10, "remaining" => 7, "reset" => 1], o
     * null si no lo traía (o no hubo respuesta).
     * @var array{limit: int|null, remaining: int|null, reset: int|null}|null
     */
    public $lastRateLimit = null;

    /**
     * @param string $token       el Token de tu API Key (empieza por eyJ)
     * @param string $privateKey  su clave privada si la API Key exige firma (lo normal); "" si no
     * @param array  $options     baseUrl (https://api.mundosms.es/v3), retries (3) y timeout: los segundos que puede durar
     *                            cada petición entera, con decimales si quieres (60)
     * @throws InvalidArgumentException con una opción que no existe o un valor que no vale
     */
    public function __construct(
        #[\SensitiveParameter]
        $token,
        #[\SensitiveParameter]
        $privateKey = "",
        array $options = array()
    ) {   // cada atributo en su línea: en PHP 7.4, «#[» es un comentario hasta el final de la línea
        foreach ($options as $k => $v) {
            if (!in_array($k, array("baseUrl", "retries", "timeout"), true)) {
                throw new InvalidArgumentException('MundoSMS: unknown option "' . $k . '" (options: baseUrl, retries, timeout)');
            }
        }
        $baseUrl = rtrim((string) ($options["baseUrl"] ?? "https://api.mundosms.es/v3"), "/");
        $u = parse_url($baseUrl);
        if (!is_array($u) || !in_array(strtolower($u["scheme"] ?? ""), array("https", "http"), true) || ($u["host"] ?? "") === ""
            || isset($u["query"]) || isset($u["fragment"])) {
            throw new InvalidArgumentException("MundoSMS: baseUrl must be an http(s) address without query, like https://api.mundosms.es/v3");
        }
        $retries = $options["retries"] ?? 3;   // un número o un texto con el número («5» de un fichero de configuración)
        if (is_bool($retries) || filter_var($retries, FILTER_VALIDATE_INT, array("options" => array("min_range" => 0))) === false) {
            throw new InvalidArgumentException("MundoSMS: retries must be an integer, 0 or more");
        }
        $timeout = $options["timeout"] ?? 60;
        if (!is_numeric($timeout) || !((float) $timeout > 0) || is_infinite((float) $timeout)) {
            throw new InvalidArgumentException("MundoSMS: timeout must be a number of seconds greater than 0");
        }
        $retries = (int) $retries;
        $this->token = trim((string) $token);
        $this->privateKey = trim((string) $privateKey);
        $this->baseUrl = $baseUrl;
        $this->retries = $retries;
        $this->timeout = (float) $timeout;
    }

    /** Lo que enseñan var_dump() y print_r(): sin el token ni la clave privada, para que no acaben en un log. */
    public function __debugInfo()
    {
        return array("baseUrl" => $this->baseUrl, "retries" => $this->retries, "timeout" => $this->timeout, "lastCursor" => $this->lastCursor,
                     "lastRateLimit" => $this->lastRateLimit);
    }

    /**
     * GET: $query va en la query.
     * @return array<string, mixed> la respuesta entera
     * @throws MundoSMSError
     */
    public function get($path, array $query = array()) { return $this->request("GET", $path, $query); }
    /**
     * POST: $data va en un cuerpo JSON. $options: ["idempotencyKey" => "pedido-881"], tu clave para que repetir este POST no
     * lo vuelva a hacer; sin ella, un UUID nuevo. Un objeto JSON vacío, como new stdClass (un array vacío sale como []).
     * @return array<string, mixed> la respuesta entera
     * @throws MundoSMSError
     */
    public function post($path, array $data = array(), array $options = array()) { return $this->request("POST", $path, $data, $options); }
    /**
     * PATCH: $data va en un cuerpo JSON.
     * @return array<string, mixed> la respuesta entera
     * @throws MundoSMSError
     */
    public function patch($path, array $data) { return $this->request("PATCH", $path, $data); }
    /**
     * DELETE: $query va en la query.
     * @return array<string, mixed> la respuesta entera
     * @throws MundoSMSError
     */
    public function delete($path, array $query = array()) { return $this->request("DELETE", $path, $query); }

    /**
     * Recorre un listado entero, página a página, siguiendo su cursor (next_cursor). Devuelve los elementos uno a uno. Cada
     * página lleva los mismos filtros (y el mismo order) más el cursor. Para sincronizar, guarda tú el cursor con lastCursor
     * al acabar: la siguiente vez empiezas desde ahí, pasándolo como cursor. lastCursor es uno por cliente: para dos
     * recorridos a la vez, un cliente para cada uno. lastCursor avanza cuando ya te ha dado todos los elementos de una página:
     * si tu código falla a mitad de una, sigue en la anterior.
     * Termina con has_more false (con stop_at_ongoing, también cuando la página se para en una llamada en curso), o si el
     * cursor no avanza. Una página vacía con has_more true (con stop_at_ongoing y un filtro, cuando ha mirado muchas que no
     * casan) no es el final: sigue.
     * Una lista que no va por páginas (GET /calls/missed y /calls/live: has_more sin next_cursor) no se puede recorrer entera:
     * es un MundoSMSError, antes de dar nada. Sube limit, o filtra, y pídela con get().
     * @return \Generator<int, array<string, mixed>> los elementos
     * @throws MundoSMSError
     */
    public function all($path, array $query = array())
    {
        $this->lastCursor = (isset($query["cursor"]) && $query["cursor"] !== "") ? $query["cursor"] : null;
        $listKey = null;
        while (true) {
            $r = $this->get($path, $query);
            $data = is_array($r["data"] ?? null) ? $r["data"] : array();
            $cursor = $data["next_cursor"] ?? null;
            if (!empty($data["has_more"]) && !empty($data["results"]) && ($cursor === null || $cursor === "")) {
                throw new MundoSMSError(sprintf(self::NOT_PAGINATED, $path), 200, null, null, $r, $data);
            }
            if ($listKey === null) {   // la lista es el primer campo con elementos: un objeto vacío ({}) también llega como array()
                foreach ($data as $k => $v) if (is_array($v) && $v && self::isList($v)) { $listKey = $k; break; }
            }
            foreach (($listKey !== null && is_array($data[$listKey] ?? null)) ? $data[$listKey] : array() as $item) yield $item;
            if ($cursor !== null && $cursor !== "") $this->lastCursor = $cursor;
            if (empty($data["has_more"]) || $cursor === null || $cursor === "" || $cursor === ($query["cursor"] ?? null)
                || !empty($data["ongoing_call"])) return;
            $query["cursor"] = $cursor;
        }
    }

    /**
     * Una petición cualquiera. $path: "/calls", "/numbers/34910123456"… (sin /v3: si lo lleva, se quita). Si la ruta trae
     * "?…", esos parámetros se juntan con $data (gana $data). En GET y DELETE, $data va en la query; en POST y PATCH, en el
     * cuerpo JSON. Devuelve la respuesta decodificada.
     * $options: "idempotencyKey", la Idempotency-Key (en un POST, sin ella, un UUID nuevo); la API la tiene en cuenta en los POST.
     * @return array<string, mixed> la respuesta entera (un fichero: status, content_type y body)
     * @throws MundoSMSError si la API contesta con un error, redirige, no contesta en JSON o no contesta
     * @throws InvalidArgumentException si los datos no se pueden mandar (no se manda nada)
     */
    public function request($method, $path, array $data = array(), array $options = array())
    {
        $method = strtoupper($method);
        $idemKey = self::idempotencyKey($options, $method);   // antes de nada: con una clave mala no se manda nada
        $basePath = (string) parse_url($this->baseUrl, PHP_URL_PATH);
        list($encodedPath, $pathQuery) = self::splitPath($path, substr($basePath, -3) === "/v3");
        $body = "";
        if ($method === "GET" || $method === "DELETE") {
            foreach ($data as $k => $v) $pathQuery[$k] = $v;
            $query = self::query($pathQuery);
        } else {
            $query = self::query($pathQuery);
            if ($data) {
                $body = json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
                if ($body === false) {
                    throw new InvalidArgumentException(json_last_error() === JSON_ERROR_UTF8
                        ? "MundoSMS: the body is not valid UTF-8 (convert your texts to UTF-8 before sending them)"
                        : "MundoSMS: the body cannot be encoded as JSON: " . json_last_error_msg());
                }
            }
        }
        $rest = $encodedPath . ($query !== "" ? "?" . $query : "");
        $url = $this->baseUrl . $rest;
        $signedPath = $basePath . $rest;   // lo que se firma es justo lo que se manda
        $ms = (int) ceil($this->timeout * 1000);
        $attempt = 0;
        $afterNetwork = false;   // un intento de antes se quedó sin respuesta (o llegó dos veces): puede que se hiciera
        while (true) {
            $attempt++;
            $ts = self::timestamp();   // un reintento inmediato no firma igual que el intento de antes
            $headers = array("Authorization: Bearer " . $this->token, "X-Timestamp: " . $ts, "Accept: application/json",
                             "User-Agent: MundoSMS-PHP/" . self::VERSION);
            if ($body !== "") $headers[] = "Content-Type: application/json";
            if ($idemKey !== null) $headers[] = "Idempotency-Key: " . $idemKey;   // la misma en todos los intentos de esta llamada
            if ($this->privateKey !== "") {
                $signature = hash_hmac("sha256", $this->token . "." . $ts . "." . $method . " " . $signedPath . "." . $body, $this->privateKey, true);
                $headers[] = "X-Signature: " . rtrim(strtr(base64_encode($signature), "+/", "-_"), "=");
            }
            $respHeaders = array();
            $ch = curl_init($url);
            curl_setopt_array($ch, array(CURLOPT_CUSTOMREQUEST => $method, CURLOPT_HTTPHEADER => $headers, CURLOPT_RETURNTRANSFER => true,
                                         CURLOPT_TIMEOUT_MS => $ms, CURLOPT_CONNECTTIMEOUT_MS => min(10000, $ms), CURLOPT_NOSIGNAL => true,
                                         CURLOPT_HEADERFUNCTION => function ($ch, $line) use (&$respHeaders) {
                                             if (strncasecmp($line, "HTTP/", 5) === 0) $respHeaders = array();   // otra respuesta (100 Continue): de cero
                                             elseif (($p = strpos($line, ":")) !== false) $respHeaders[strtolower(trim(substr($line, 0, $p)))] = trim(substr($line, $p + 1));
                                             return strlen($line);
                                         }));
            if ($body !== "") curl_setopt($ch, CURLOPT_POSTFIELDS, $body);
            $resp = curl_exec($ch);
            $http = (int) curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
            $contentType = (string) curl_getinfo($ch, CURLINFO_CONTENT_TYPE);
            $networkError = ($resp === false) ? curl_error($ch) : "";
            if ($networkError !== "" && curl_errno($ch) === 28) $networkError = "timed out after " . $this->timeout . " s";   // CURLE_OPERATION_TIMEDOUT: el mismo texto que los otros SDK
            unset($ch);   // sin curl_close(): no hace nada desde PHP 8.0 y en 8.5 deja un «Deprecated» por petición
            if ($networkError !== "") $http = 0;   // se cortó a medias: es un fallo de red aunque llegara la cabecera
            $redirect = $networkError === "" && $http >= 300 && $http < 400;   // de una redirección no se lee nada
            $j = ($resp !== false && $networkError === "" && !$redirect) ? json_decode((string) $resp, true) : null;
            if (!is_array($j) || ($j !== array() && self::isList($j))) $j = null;   // solo un objeto JSON es de la API
            if (self::retryable($method, $networkError !== "", $http, $j, $idemKey) && $attempt <= $this->retries) {
                $afterNetwork = $afterNetwork || $networkError !== "" || self::duplicated($http, $j);
                $s = self::retryWait($respHeaders["retry-after"] ?? null, $attempt);
                if ($s >= 1) sleep((int) $s);
                usleep((int) round(($s - floor($s)) * 1000000));
                continue;
            }
            if ($networkError !== "") {
                $this->lastRateLimit = null;
                throw new MundoSMSError("No response from the API: " . $networkError);
            }
            $this->lastRateLimit = self::rateLimit($respHeaders);
            $rid = $respHeaders["x-request-id"] ?? null;
            if ($redirect) {   // no se sigue: llevaría el token y la firma a otro servidor
                $where = isset($respHeaders["location"]) ? " to " . substr($respHeaders["location"], 0, 300) : "";
                throw new MundoSMSError("HTTP " . $http . ": redirect" . $where . " not followed (the API does not redirect: check baseUrl)",
                                        $http, null, null, null, null, $rid);
            }
            $replayed = strtolower(trim((string) ($respHeaders["idempotent-replayed"] ?? ""))) === "true";
            if ($replayed && is_array($j)) $j["idempotent_replayed"] = true;   // la respuesta guardada de la primera vez: no se ha vuelto a hacer
            if ($http >= 200 && $http < 300) {
                if (!is_array($j)) {   // un fichero, o algo que no es de la API
                    $type = trim(explode(";", $contentType)[0]);
                    if ((string) $resp !== "" && !preg_match('#\A(audio/|image/|application/pdf\z|application/octet-stream\z)#i', $type)) {
                        throw new MundoSMSError("HTTP " . $http . ": the response is not JSON (" . ($type !== "" ? $type : "no Content-Type") . ")",
                                                $http, null, null, null, null, $rid);
                    }
                    $file = array("status" => "success", "content_type" => $contentType, "body" => (string) $resp);
                    if ($replayed) $file["idempotent_replayed"] = true;
                    return $file;
                }
                if (($j["status"] ?? null) !== "error") return $j;
            }
            if ($method === "DELETE" && $afterNetwork && $http === 404 && is_array($j) && self::code($j) !== "route_not_found") {
                // lo borró el intento que se quedó sin respuesta
                return array("status" => "success", "message" => "Already deleted", "data" => array("state" => "deleted", "changed" => false));
            }
            throw new MundoSMSError(is_array($j) ? (string) ($j["message"] ?? "Error") : "HTTP " . $http, $http,
                                    is_array($j) ? self::code($j) : null, is_array($j) ? ($j["detail"] ?? null) : null, $j,
                                    is_array($j) ? ($j["data"] ?? null) : null, $rid);
        }
    }

    /**
     * true si un aviso que te llega (callback_url o webhook de WhatsApp) es nuestro y nadie lo ha tocado.
     * @param string $secret     el de «API y Soporte → Webhooks» (whsec_…)
     * @param string $header     X-MundoSMS-Signature ($_SERVER["HTTP_X_MUNDOSMS_SIGNATURE"] ?? "")
     * @param string $body       el cuerpo TAL CUAL llegó (file_get_contents("php://input")), sin volver a serializar el JSON
     * @param int    $tolerance  los segundos que puede alejarse «t» de tu hora (300)
     * Una cabecera que falta o está mal formada da false, sin excepción.
     */
    public static function verifyWebhook(
        #[\SensitiveParameter]
        $secret,
        $header,
        $body,
        $tolerance = self::WEBHOOK_TOLERANCE
    )
    {
        if (!is_string($body)) {
            throw new InvalidArgumentException('MundoSMS::verifyWebhook: pass the raw body as received (file_get_contents("php://input")), not a decoded value');
        }
        $secret = is_string($secret) ? trim($secret) : "";
        if ($secret === "" || !is_string($header)) return false;
        $t = null;
        $signatures = array();
        foreach (explode(",", $header) as $part) {
            $p = strpos($part, "=");
            if ($p === false) continue;
            $k = trim(substr($part, 0, $p));
            $v = trim(substr($part, $p + 1));
            if ($k === "t") {
                if ($t !== null) return false;   // dos «t»: no se sabe cuál vale
                $t = $v;
            } elseif ($k === "v1") {
                $signatures[] = strtolower($v);
            }
        }
        if ($t === null || !preg_match('/\A[0-9]{1,15}\z/', $t) || !$signatures) return false;
        if (abs(time() - (int) $t) > $tolerance) return false;
        $expected = hash_hmac("sha256", $t . "." . $body, $secret);
        $valid = false;
        foreach ($signatures as $s) {   // todas, sin cortar en la primera: hash_equals es de tiempo constante
            if (hash_equals($expected, $s)) $valid = true;
        }
        return $valid;
    }

    /**
     * El X-Timestamp de una petición: milisegundos, y siempre mayor que el de la anterior de este proceso. Un reintento que sale
     * en el mismo milisegundo (un 429 con Retry-After: 0) firmaría igual que el intento de antes, y la API lo rechazaría por
     * repetido («409 duplicated_request»).
     */
    private static function timestamp()
    {
        self::$lastTimestamp = max((int) round(microtime(true) * 1000), self::$lastTimestamp + 1);
        return (string) self::$lastTimestamp;
    }

    /** La Idempotency-Key de la llamada: la tuya, si tiene la forma que exige la API; en un POST sin ella, un UUID v4 nuevo. */
    private static function idempotencyKey(array $options, $method)
    {
        foreach ($options as $k => $v) {
            if ($k !== "idempotencyKey") throw new InvalidArgumentException('MundoSMS: unknown option "' . $k . '" (the only option is idempotencyKey)');
        }
        $k = $options["idempotencyKey"] ?? null;
        if ($k === null) {
            if ($method !== "POST") return null;
            $b = random_bytes(16);
            $b[6] = chr((ord($b[6]) & 0x0f) | 0x40);   // versión 4
            $b[8] = chr((ord($b[8]) & 0x3f) | 0x80);   // variante RFC 4122
            $h = bin2hex($b);
            return substr($h, 0, 8) . "-" . substr($h, 8, 4) . "-" . substr($h, 12, 4) . "-" . substr($h, 16, 4) . "-" . substr($h, 20);
        }
        if (!is_string($k) || !preg_match('/\A[A-Za-z0-9_.:-]{1,64}\z/', $k)) {
            throw new InvalidArgumentException("MundoSMS: idempotencyKey must be 1 to 64 characters: A-Z, a-z, 0-9 and _ . : - (nothing was sent)");
        }
        return $k;
    }

    /**
     * Si se puede repetir: un 429 en cualquier método; un GET, ante un fallo de red o un 5xx; un PATCH o un DELETE, ante un
     * fallo de red; un POST (con su Idempotency-Key), ante un fallo de red o un 409 request_in_progress. Un 5xx de un POST
     * no: un 500 puede haberse hecho y la API lo guarda con la clave (repetirlo devolvería el mismo 500), y un 503 no hizo
     * nada, pero repetirlo con la misma clave se deja a tu código. Un 409 duplicated_request (la MISMA petición firmada llegó
     * dos veces: la repitió algo entre medias, como hace curl con una conexión que se cerró) es un fallo de red más.
     */
    private static function retryable($method, $network, $http, $j, $idemKey)
    {
        if ($http === 429) return true;
        $network = $network || self::duplicated($http, $j);
        if ($method === "GET") return $network || $http >= 500;
        if ($method === "PATCH" || $method === "DELETE") return $network;
        if ($idemKey !== null) return $network || ($http === 409 && self::code($j) === "request_in_progress");
        return false;
    }

    /** 409 duplicated_request: la MISMA petición firmada llegó dos veces (la repitió algo entre medias). */
    private static function duplicated($http, $j)
    {
        return $http === 409 && self::code($j) === "duplicated_request";
    }

    /** El code de una respuesta de error ("duplicated_request"…) como texto, o null si no lo trae. */
    private static function code($j)
    {
        if (!is_array($j) || !isset($j["code"])) return null;
        return is_string($j["code"]) ? $j["code"] : json_encode($j["code"], JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    }

    /** El cupo de la respuesta (X-RateLimit-Limit, -Remaining y -Reset, en enteros), o null si no lo trae. */
    private static function rateLimit(array $headers)
    {
        $out = array();
        $any = false;
        foreach (array("limit", "remaining", "reset") as $k) {
            $v = isset($headers["x-ratelimit-" . $k]) ? trim($headers["x-ratelimit-" . $k]) : null;
            $out[$k] = ($v !== null && preg_match('/\A-?[0-9]{1,18}\z/', $v)) ? (int) $v : null;
            if ($out[$k] !== null) $any = true;
        }
        return $any ? $out : null;
    }

    /** Si un array es una lista (0, 1, 2…): array_is_list de PHP 8.1, también en 7.4. */
    private static function isList(array $a)
    {
        return $a === array() || array_keys($a) === range(0, count($a) - 1);
    }

    /**
     * La ruta codificada trozo a trozo (un %XX ya escrito se respeta), sin el /v3 del principio si la base ya lo lleva,
     * y los parámetros que traiga tras «?»: con su %XX decodificado y el «+» como «+» (RFC 3986; no es un formulario), para
     * que una fecha con zona escrita en la ruta (from_datetime=…+01:00) llegue tal cual.
     */
    private static function splitPath($path, $stripV3)
    {
        $path = (string) $path;
        $pathQuery = array();
        $p = strpos($path, "?");
        if ($p !== false) {
            foreach (explode("&", substr($path, $p + 1)) as $pair) {
                if ($pair === "") continue;
                $kv = explode("=", $pair, 2);
                $pathQuery[rawurldecode($kv[0])] = rawurldecode($kv[1] ?? "");
            }
            $path = substr($path, 0, $p);
        }
        $path = "/" . ltrim($path, "/");
        if ($stripV3 && ($path === "/v3" || strncmp($path, "/v3/", 4) === 0)) $path = "/" . ltrim(substr($path, 3), "/");
        $segments = explode("/", $path);
        foreach ($segments as $i => $segment) {
            $parts = preg_split('/(%[0-9A-Fa-f]{2})/', $segment, -1, PREG_SPLIT_DELIM_CAPTURE);
            foreach ($parts as $n => $x) if ($n % 2 === 0) $parts[$n] = rawurlencode($x);
            $segments[$i] = implode("", $parts);
        }
        return array(implode("/", $segments), $pathQuery);
    }

    /** La query en RFC 3986 (todo codificado menos A-Z a-z 0-9 - . _ ~): es lo que se firma y lo que se manda. */
    private static function query(array $params)
    {
        $q = array();
        foreach ($params as $k => $v) {
            $v = self::queryValue($v);
            if ($v !== null) $q[] = rawurlencode((string) $k) . "=" . rawurlencode($v);
        }
        return implode("&", $q);
    }

    /** Un valor de la query: true/false, una lista separada por comas; null o una lista vacía (o solo de null), nada. */
    private static function queryValue($v)
    {
        if ($v === null) return null;
        if (is_bool($v)) return $v ? "true" : "false";
        if (is_array($v)) {
            $items = array();
            foreach ($v as $x) if (($x = self::queryValue($x)) !== null) $items[] = $x;
            return $items ? implode(",", $items) : null;
        }
        return (string) $v;
    }

    /** Segundos antes del siguiente intento: lo que pida Retry-After (segundos o fecha HTTP), o 1, 2, 4… Como mucho 60. */
    private static function retryWait($retryAfter, $attempt)
    {
        $s = null;
        if ($retryAfter !== null && $retryAfter !== "") {
            if (is_numeric($retryAfter)) $s = (float) $retryAfter;
            elseif (($t = strtotime($retryAfter)) !== false) $s = $t - time();
        }
        if ($s === null) $s = 2 ** ($attempt - 1);
        return max(0, min(self::MAX_RETRY_WAIT, $s));
    }
}

