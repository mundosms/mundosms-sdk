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
 *   foreach ($api->todos("/calls", ["from_datetime" => "2026-11-01", "stop_at_ongoing" => true]) as $llamada) { ... }
 *
 * Errores: excepción MundoSMSError con ->http (código HTTP), ->statusCode ("3", "4"…), getMessage() y ->detail.
 * Reintenta solo, con espera creciente, lo que es seguro repetir: un 429 (no se hizo nada) y, en GET, los 5xx y los fallos
 * de red. Un POST o un PATCH que falla por la red NO se repite: no se sabe si llegó (usa client_id y consulta antes).
 */

class MundoSMSError extends Exception
{
    public $http;
    public $statusCode;
    public $detail;
    public $respuesta;

    public function __construct($mensaje, $http = 0, $statusCode = null, $detail = null, $respuesta = null)
    {
        parent::__construct((string) $mensaje);
        $this->http = (int) $http;
        $this->statusCode = $statusCode;
        $this->detail = $detail;
        $this->respuesta = $respuesta;
    }
}

class MundoSMS
{
    const VERSION = "1.1.0";

    private $token;
    private $clave;
    private $base;
    private $reintentos;
    private $plazo;

    /**
     * @param string $token          el Token de tu API Key (empieza por eyJ)
     * @param string $clavePrivada   su clave privada si la API Key exige firma (lo normal); "" si no
     * @param array  $opciones       base (https://api.mundosms.es/v3), reintentos (3), plazo en segundos (30)
     */
    public function __construct($token, $clavePrivada = "", array $opciones = array())
    {
        $this->token = trim((string) $token);
        $this->clave = (string) $clavePrivada;
        $this->base = rtrim((string) ($opciones["base"] ?? "https://api.mundosms.es/v3"), "/");
        $this->reintentos = (int) ($opciones["reintentos"] ?? 3);
        $this->plazo = (int) ($opciones["plazo"] ?? 30);
    }

    public function get($ruta, array $filtros = array()) { return $this->peticion("GET", $ruta, $filtros); }
    public function post($ruta, array $datos = array()) { return $this->peticion("POST", $ruta, $datos); }
    public function patch($ruta, array $datos) { return $this->peticion("PATCH", $ruta, $datos); }
    public function delete($ruta, array $filtros = array()) { return $this->peticion("DELETE", $ruta, $filtros); }

    /**
     * Recorre un listado entero, página a página, siguiendo su cursor (next_cursor). Devuelve los elementos
     * uno a uno. Para sincronizar, guarda tú el cursor con cursor(): la siguiente vez empiezas desde ahí.
     */
    public function todos($ruta, array $filtros = array())
    {
        $lista = null;
        while (true) {
            $r = $this->get($ruta, $filtros);
            $data = $r["data"] ?? array();
            if ($lista === null) {
                foreach ($data as $k => $v) if (is_array($v) && array_is_list($v)) { $lista = $k; break; }
            }
            foreach (($lista !== null ? ($data[$lista] ?? array()) : array()) as $e) yield $e;
            $this->ultimoCursor = $data["next_cursor"] ?? null;
            if (empty($data["has_more"]) || (int) ($data["results"] ?? 0) === 0 || !isset($data["next_cursor"])) return;
            unset($filtros["from_datetime"], $filtros["order"]);
            $filtros["cursor"] = $data["next_cursor"];
        }
    }

    /** El cursor del último listado recorrido con todos(): guárdalo para la siguiente sincronización. */
    public $ultimoCursor = null;

    /** Una petición cualquiera. $ruta: "/calls", "/numbers/34910123456"… Devuelve la respuesta decodificada. */
    public function peticion($metodo, $ruta, array $datos = array())
    {
        $metodo = strtoupper($metodo);
        $ruta = "/" . ltrim($ruta, "/");
        $query = "";
        $cuerpo = "";
        if ($metodo === "GET" || $metodo === "DELETE") {
            $q = array();
            foreach ($datos as $k => $v) {
                if ($v === null || (is_array($v) && !$v)) continue;   // una lista vacía, como no mandarla
                $q[$k] = is_bool($v) ? ($v ? "true" : "false") : (is_array($v) ? implode(",", $v) : (string) $v);
            }
            $query = http_build_query($q, "", "&", PHP_QUERY_RFC3986);
        } elseif ($datos) {
            $cuerpo = json_encode($datos, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        }
        $url = $this->base . $ruta . ($query !== "" ? "?" . $query : "");
        $camino = (string) parse_url($url, PHP_URL_PATH) . ($query !== "" ? "?" . $query : "");
        $intento = 0;
        while (true) {
            $intento++;
            $ts = (string) (int) round(microtime(true) * 1000);   // milisegundos: dos peticiones iguales seguidas no firman igual
            $cab = array("Authorization: Bearer " . $this->token, "X-Timestamp: " . $ts, "Accept: application/json",
                         "User-Agent: MundoSMS-PHP/" . self::VERSION);
            if ($cuerpo !== "") $cab[] = "Content-Type: application/json";
            if ($this->clave !== "") {
                $firma = hash_hmac("sha256", $this->token . "." . $ts . "." . $metodo . " " . $camino . "." . $cuerpo, $this->clave, true);
                $cab[] = "X-Signature: " . rtrim(strtr(base64_encode($firma), "+/", "-_"), "=");
            }
            $ch = curl_init($url);
            curl_setopt_array($ch, array(CURLOPT_CUSTOMREQUEST => $metodo, CURLOPT_HTTPHEADER => $cab, CURLOPT_RETURNTRANSFER => true,
                                         CURLOPT_TIMEOUT => $this->plazo, CURLOPT_CONNECTTIMEOUT => 10));
            if ($cuerpo !== "") curl_setopt($ch, CURLOPT_POSTFIELDS, $cuerpo);
            $resp = curl_exec($ch);
            $http = (int) curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
            $tipo = (string) curl_getinfo($ch, CURLINFO_CONTENT_TYPE);
            $error_red = ($resp === false) ? curl_error($ch) : "";
            unset($ch);   // sin curl_close(): no hace nada desde PHP 8.0 y en 8.5 deja un «Deprecated» por petición
            $j = ($resp !== false) ? json_decode((string) $resp, true) : null;
            $repetible = ($http === 429) || ($metodo === "GET" && ($error_red !== "" || $http >= 500));
            if ($repetible && $intento <= $this->reintentos) {
                usleep((int) (min(60, 2 ** ($intento - 1)) * 1000000));
                continue;
            }
            if ($error_red !== "") throw new MundoSMSError("Sin respuesta de la API: " . $error_red);
            if ($http >= 200 && $http < 300) {
                if (!is_array($j)) return array("status" => "success", "content_type" => $tipo, "body" => $resp);   // un audio
                return $j;
            }
            throw new MundoSMSError(is_array($j) ? (string) ($j["message"] ?? "Error") : "HTTP " . $http, $http,
                                    is_array($j) ? ($j["status_code"] ?? null) : null, is_array($j) ? ($j["detail"] ?? null) : null, $j);
        }
    }
}

if (!function_exists("array_is_list")) {
    function array_is_list(array $a) { return $a === array() || array_keys($a) === range(0, count($a) - 1); }
}
