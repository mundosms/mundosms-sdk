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
 * Errores: excepción MundoSMSError con ->http (código HTTP), ->statusCode ("3", "4"…), getMessage(), ->detail, ->data,
 * ->response (la respuesta entera) y ->requestId (la cabecera X-Request-Id). También cuando la API contesta con
 * "status": "error", venga con el código HTTP que venga. Un WhatsApp sin confirmar (202) no es un error: vuelve la respuesta,
 * con data["state"] "unknown".
 * Cada POST lleva una Idempotency-Key (un UUID nuevo en cada llamada, la misma en todos sus intentos; la tuya, con
 * $api->post($path, $data, ["idempotencyKey" => "pedido-881"])). Por eso un POST que falla por la red, o con 409 «Request
 * in progress», se repite sin miedo: la API no lo hace dos veces. Si la respuesta es la guardada de la primera vez, trae
 * "idempotent_replayed" => true.
 * Reintenta: un 429, en cualquier método (no se hizo nada); un fallo de red, en cualquier método; un 5xx, solo en GET (un POST
 * con 5xx no se repite: la API no guarda los 5xx, y repetirlo lo haría otra vez). Espera lo que diga Retry-After (como mucho
 * 60 s) o, si no lo trae, 1, 2, 4… s.
 * Los avisos que te mandamos (callback_url y webhooks de WhatsApp) se comprueban con MundoSMS::verifyWebhook().
 */

class MundoSMSError extends Exception
{
    /** @var int el código HTTP; 0 si no hubo respuesta */
    public $http;
    /** @var string|null el status_code de la API, como texto ("3", "4", "12"…) */
    public $statusCode;
    /** @var string|null el detail de la respuesta, si lo trae */
    public $detail;
    /** @var array|null la respuesta entera, decodificada; null si no era JSON */
    public $response;
    /** @var mixed el data de la respuesta (en WhatsApp, el id del mensaje que puede haber salido) */
    public $data;
    /** @var string|null la cabecera X-Request-Id: con ella encuentras la petición en Logs API */
    public $requestId;

    public function __construct($message, $http = 0, $statusCode = null, $detail = null, $response = null, $data = null, $requestId = null)
    {
        parent::__construct((string) $message);
        $this->http = (int) $http;
        $this->statusCode = $statusCode;
        $this->detail = $detail;
        $this->response = $response;
        $this->data = $data;
        $this->requestId = $requestId;
    }
}

class MundoSMS
{
    const VERSION = "1.0.0";
    const MAX_RETRY_WAIT = 60;      // segundos: lo más que se espera antes de un reintento, aunque Retry-After pida más
    const WEBHOOK_TOLERANCE = 300;  // segundos: lo más que puede alejarse de tu hora la «t» de la firma de un aviso

    private $token;
    private $privateKey;
    private $baseUrl;
    private $retries;
    private $timeout;

    /** El cursor del último listado recorrido con all(): guárdalo para la siguiente sincronización. */
    public $lastCursor = null;

    /**
     * @param string $token       el Token de tu API Key (empieza por eyJ)
     * @param string $privateKey  su clave privada si la API Key exige firma (lo normal); "" si no
     * @param array  $options     baseUrl (https://api.mundosms.es/v3), retries (3) y timeout: los segundos que puede durar
     *                            cada petición entera, con decimales si quieres (60)
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
        $this->token = trim((string) $token);
        $this->privateKey = trim((string) $privateKey);
        $this->baseUrl = rtrim((string) ($options["baseUrl"] ?? "https://api.mundosms.es/v3"), "/");
        $this->retries = (int) ($options["retries"] ?? 3);
        $this->timeout = (float) ($options["timeout"] ?? 60);
    }

    /** Lo que enseñan var_dump() y print_r(): sin el token ni la clave privada, para que no acaben en un log. */
    public function __debugInfo()
    {
        return array("baseUrl" => $this->baseUrl, "retries" => $this->retries, "timeout" => $this->timeout, "lastCursor" => $this->lastCursor);
    }

    /** GET: $query va en la query. */
    public function get($path, array $query = array()) { return $this->request("GET", $path, $query); }
    /**
     * POST: $data va en un cuerpo JSON. $options: ["idempotencyKey" => "pedido-881"], tu clave para que repetir este POST no
     * lo vuelva a hacer; sin ella, un UUID nuevo.
     */
    public function post($path, array $data = array(), array $options = array()) { return $this->request("POST", $path, $data, $options); }
    /** PATCH: $data va en un cuerpo JSON. */
    public function patch($path, array $data) { return $this->request("PATCH", $path, $data); }
    /** DELETE: $query va en la query. */
    public function delete($path, array $query = array()) { return $this->request("DELETE", $path, $query); }

    /**
     * Recorre un listado entero, página a página, siguiendo su cursor (next_cursor). Devuelve los elementos uno a uno. Cada
     * página lleva los mismos filtros (y el mismo order) más el cursor. Para sincronizar, guarda tú el cursor con lastCursor:
     * la siguiente vez empiezas desde ahí, pasándolo como cursor.
     */
    public function all($path, array $query = array())
    {
        $listKey = null;
        while (true) {
            $r = $this->get($path, $query);
            $data = is_array($r["data"] ?? null) ? $r["data"] : array();
            if ($listKey === null) {   // la lista es el primer campo con elementos: un objeto vacío ({}) también llega como array()
                foreach ($data as $k => $v) if (is_array($v) && $v && array_is_list($v)) { $listKey = $k; break; }
            }
            foreach (($listKey !== null && is_array($data[$listKey] ?? null)) ? $data[$listKey] : array() as $item) yield $item;
            $this->lastCursor = $data["next_cursor"] ?? null;
            if (empty($data["has_more"]) || (int) ($data["results"] ?? 0) === 0 || !isset($data["next_cursor"])) return;
            $query["cursor"] = $data["next_cursor"];
        }
    }

    /**
     * Una petición cualquiera. $path: "/calls", "/numbers/34910123456"… (sin /v3: si lo lleva, se quita). Si la ruta trae
     * "?…", esos parámetros se juntan con $data (gana $data). En GET y DELETE, $data va en la query; en POST y PATCH, en el
     * cuerpo JSON. Devuelve la respuesta decodificada.
     * $options: "idempotencyKey", la Idempotency-Key (en un POST, sin ella, un UUID nuevo); la API la tiene en cuenta en los POST.
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
        while (true) {
            $attempt++;
            $ts = (string) (int) round(microtime(true) * 1000);   // milisegundos: dos peticiones iguales seguidas no firman igual
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
            $j = ($resp !== false && $networkError === "") ? json_decode((string) $resp, true) : null;
            if (self::retryable($method, $networkError !== "", $http, $j, $idemKey) && $attempt <= $this->retries) {
                $s = self::retryWait($respHeaders["retry-after"] ?? null, $attempt);
                if ($s >= 1) sleep((int) $s);
                usleep((int) round(($s - floor($s)) * 1000000));
                continue;
            }
            if ($networkError !== "") throw new MundoSMSError("No response from the API: " . $networkError);
            $replayed = strtolower(trim((string) ($respHeaders["idempotent-replayed"] ?? ""))) === "true";
            if ($replayed && is_array($j)) $j["idempotent_replayed"] = true;   // la respuesta guardada de la primera vez: no se ha vuelto a hacer
            if ($http >= 200 && $http < 300) {
                if (!is_array($j)) {   // un fichero
                    $file = array("status" => "success", "content_type" => $contentType, "body" => (string) $resp);
                    if ($replayed) $file["idempotent_replayed"] = true;
                    return $file;
                }
                if (($j["status"] ?? null) !== "error") return $j;
            }
            throw new MundoSMSError(is_array($j) ? (string) ($j["message"] ?? "Error") : "HTTP " . $http, $http,
                                    is_array($j) ? ($j["status_code"] ?? null) : null, is_array($j) ? ($j["detail"] ?? null) : null, $j,
                                    is_array($j) ? ($j["data"] ?? null) : null, $respHeaders["x-request-id"] ?? null);
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
     * fallo de red; un POST (con su Idempotency-Key), ante un fallo de red o un 409 «Request in progress». Un 5xx de un POST
     * no: la API no guarda los 5xx y repetirlo lo haría otra vez. Un 409 «Duplicated request» (la MISMA petición firmada llegó
     * dos veces: la repitió algo entre medias, como hace curl con una conexión que se cerró) es un fallo de red más.
     */
    private static function retryable($method, $network, $http, $j, $idemKey)
    {
        if ($http === 429) return true;
        $message = (is_array($j) && is_string($j["message"] ?? null)) ? $j["message"] : null;
        $network = $network || ($http === 409 && $message !== null && strncmp($message, "Duplicated request", 18) === 0);
        if ($method === "GET") return $network || $http >= 500;
        if ($method === "PATCH" || $method === "DELETE") return $network;
        if ($idemKey !== null) return $network || ($http === 409 && $message === "Request in progress");
        return false;
    }

    /**
     * La ruta codificada trozo a trozo (un %XX ya escrito se respeta), sin el /v3 del principio si la base ya lo lleva,
     * y los parámetros que traiga tras «?».
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
                $pathQuery[urldecode($kv[0])] = urldecode($kv[1] ?? "");
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

if (!function_exists("array_is_list")) {
    function array_is_list(array $a) { return $a === array() || array_keys($a) === range(0, count($a) - 1); }
}
