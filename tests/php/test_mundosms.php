<?php
/**
 * Pruebas del cliente PHP (mundosms.php), sin red: un servidor de prueba local (otro proceso de PHP, con stream_socket_server)
 * que comprueba la firma como la API y contesta lo que le pide cada caso. PHP 7.4 o superior, con curl; sin dependencias.
 *   php tests/php/test_mundosms.php          (el código de salida es el número de fallos)
 */
error_reporting(E_ALL);
$avisos = array();
set_error_handler(function ($n, $m, $f = "", $l = 0) use (&$avisos) {
    if (!(error_reporting() & $n)) return false;   // los de una línea con @: los de las pruebas, no los del SDK
    $avisos[] = "$m (" . basename((string) $f) . ":$l)";
    return true;
});
foreach (array(__DIR__ . "/../../php/mundosms.php", __DIR__ . "/../../mundosms.php") as $f) {   // en el repositorio, o con todos los clientes en una carpeta
    if (is_file($f)) { require $f; break; }
}

const TOKEN = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiIsInN1YiI6IjcifQ.cHJ1ZWJh";
const KEY = "Pr4ebaClaveDe20Letra";
const OK = '{"status":"success","status_code":"0","message":"OK","data":{"id":8812345}}';

/** La firma de «La firma» de la documentación: base64url, sin relleno, del HMAC-SHA256. */
function firma($clave, $token, $ts, $metodo, $ruta, $cuerpo = "")
{
    return rtrim(strtr(base64_encode(hash_hmac("sha256", "$token.$ts.$metodo $ruta.$cuerpo", $clave, true)), "+/", "-_"), "=");
}

// el servidor de prueba: lee el plan de cada caso (plan.json: una respuesta por petición, o por el principio de la ruta) y apunta
// cada petición en peticiones.jsonl
$dir = sys_get_temp_dir() . "/mundosms-test-" . getmypid();
@mkdir($dir);
file_put_contents("$dir/servidor.php", <<<'PHP'
<?php
$dir = $argv[1];
$srv = stream_socket_server("tcp://127.0.0.1:0", $errno, $errstr);
file_put_contents("$dir/puerto", explode(":", stream_socket_get_name($srv, false))[1]);
while (true) {
    $c = @stream_socket_accept($srv, 60);
    if (!$c) continue;
    $linea = (string) fgets($c);
    $cab = array();
    while (($h = fgets($c)) !== false && rtrim($h) !== "") { $p = strpos($h, ":"); $cab[strtolower(trim(substr($h, 0, $p)))] = trim(substr($h, $p + 1)); }
    $b = "";
    $n = (int) ($cab["content-length"] ?? 0);
    while (strlen($b) < $n && !feof($c)) $b .= fread($c, $n - strlen($b));
    list($metodo, $ruta) = explode(" ", $linea);
    $clave = "Pr4ebaClaveDe20Letra";
    $esperada = rtrim(strtr(base64_encode(hash_hmac("sha256", substr($cab["authorization"] ?? "", 7) . "." . ($cab["x-timestamp"] ?? "") . ".$metodo $ruta.$b", $clave, true)), "+/", "-_"), "=");
    file_put_contents("$dir/peticiones.jsonl", json_encode(array("metodo" => $metodo, "ruta" => $ruta, "cabeceras" => $cab, "cuerpo" => $b,
                      "firma" => ($cab["x-signature"] ?? null) === $esperada)) . "\n", FILE_APPEND);
    $plan = json_decode(file_get_contents("$dir/plan.json"), true);
    $i = count(file("$dir/peticiones.jsonl"));
    $r = null;
    foreach ($plan["por_ruta"] ?? array() as $pre => $x) if (strncmp($ruta, $pre, strlen($pre)) === 0) { $r = $x; break; }
    if ($r === null) $r = $plan["respuestas"][min($i, count($plan["respuestas"])) - 1];
    if (!empty($r["corta"])) { fclose($c); continue; }
    $cuerpo = (string) ($r["cuerpo"] ?? "");
    $s = "HTTP/1.1 " . $r["estado"] . " X\r\n";
    foreach ($r["cabeceras"] ?? array() as $k => $v) $s .= "$k: $v\r\n";
    fwrite($c, $s . "Content-Length: " . strlen($cuerpo) . "\r\nConnection: close\r\n\r\n");
    if (!empty($r["lento"])) { fflush($c); sleep(1); usleep(500000); }
    fwrite($c, $cuerpo);
    fclose($c);
}
PHP
);
@unlink("$dir/puerto");
$proc = proc_open(array(PHP_BINARY, "$dir/servidor.php", $dir), array(), $tubos);
for ($i = 0; $i < 100 && !is_file("$dir/puerto"); $i++) usleep(50000);
$base = "http://127.0.0.1:" . trim((string) @file_get_contents("$dir/puerto")) . "/v3";

$fallos = 0;
function ok($c, $que)
{
    global $fallos;
    if (!$c) $fallos++;
    echo ($c ? "  ok    " : "  FALLA ") . $que . "\n";
}

/** Prepara un caso: el plan del servidor (respuestas por petición y por ruta) y la lista de peticiones vacía. */
function plan(array $respuestas, array $porRuta = array())
{
    global $dir;
    file_put_contents("$dir/plan.json", json_encode(array("respuestas" => $respuestas, "por_ruta" => (object) $porRuta)));
    file_put_contents("$dir/peticiones.jsonl", "");
}

function peticiones()
{
    global $dir;
    return array_map(function ($l) { return json_decode($l, true); }, array_filter(explode("\n", file_get_contents("$dir/peticiones.jsonl"))));
}

function json($estado, $cuerpo, array $cab = array())
{
    return array("estado" => $estado, "cabeceras" => array("Content-Type" => "application/json") + $cab, "cuerpo" => is_string($cuerpo) ? $cuerpo : json_encode($cuerpo));
}

function cliente(array $opc = array())
{
    global $base;
    return new MundoSMS(TOKEN, KEY, array("baseUrl" => $base) + $opc);
}

/** El MundoSMSError de $f, o null si no salta. */
function error(callable $f)
{
    try { $f(); } catch (MundoSMSError $e) { return $e; }
    return null;
}

echo "== la firma: los ejemplos de la documentación y lo que manda el SDK\n";
ok(firma("A1b2C3d4E5f6G7h8I9j0", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiJ9.ZmlybWE", "1795400000123", "GET", "/v3/calls?type=in&limit=5")
   === "uE09uuJV7e3hSSW2NZIJQ9jMWlT15lMETj-sbrIjoFo", "el ejemplo GET de «Para comprobar tu código»");
ok(firma("A1b2C3d4E5f6G7h8I9j0", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiJ9.ZmlybWE", "1795400000123", "POST", "/v3/sms", '{"to":"34600123456","text":"Hola"}')
   === "_62bUnSBUjpWSsAMT580JFfk1Yhx9bCZhS805swJ2to", "el ejemplo POST");
plan(array(json(200, OK)));
$api = cliente();
$api->get("/calls", array("type" => "in", "limit" => 5));
$api->post("/sms", array("to" => "34600123456", "text" => "Señor Núñez: 9,50 €"));
list($g, $p) = peticiones();
ok($g["ruta"] === "/v3/calls?type=in&limit=5" && $g["cuerpo"] === "" && $p["ruta"] === "/v3/sms", "la ruta y la query, tal cual se firman");
ok($p["cuerpo"] === '{"to":"34600123456","text":"Señor Núñez: 9,50 €"}', "el cuerpo, en UTF-8 sin escapar");
ok($g["firma"] && $p["firma"] && preg_match('/^[0-9]{13}$/', $g["cabeceras"]["x-timestamp"]), "firmadas, con X-Timestamp en milisegundos");
ok(preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/', $p["cabeceras"]["idempotency-key"] ?? "") && !isset($g["cabeceras"]["idempotency-key"]),
   "Idempotency-Key en el POST y no en el GET");

echo "== un aviso: el ejemplo de whsec_ejemplo\n";
$cuerpo = '{"id":"evt_prueba","type":"sms.status","data":{"to":"34600123456","state":"delivered"}}';
$cab = "t=1795161606,v1=d43d40dfd88dd4aba4633580b2786f8651936516aecc2967868edf333ddfdae5";
ok(MundoSMS::verifyWebhook("whsec_ejemplo", $cab, $cuerpo, PHP_INT_MAX) === true, "con su secreto, vale");
ok(MundoSMS::verifyWebhook("whsec_otro", $cab, $cuerpo, PHP_INT_MAX) === false, "con otro secreto, no");
ok(MundoSMS::verifyWebhook("whsec_ejemplo", $cab, str_replace("delivered", "Delivered", $cuerpo), PHP_INT_MAX) === false, "con un byte cambiado, no");
ok(MundoSMS::verifyWebhook("whsec_ejemplo", $cab, $cuerpo) === false, "de hace más de 5 minutos, no");

echo "== reintentos y plazo\n";
plan(array(json(429, '{"status":"error","message":"Too many requests"}', array("Retry-After" => "0")),
           json(429, '{"status":"error","message":"Too many requests"}', array("Retry-After" => "0")), json(200, OK)));
ok((cliente()->get("/calls")["status"] ?? null) === "success" && count(array_unique(array_map(function ($x) { return $x["cabeceras"]["x-timestamp"]; }, peticiones()))) === 3,
   "un 429 se repite, con otro X-Timestamp cada vez");
plan(array(json(500, '{"status":"error","status_code":"2","message":"Internal error"}')));
$e = error(function () { cliente()->post("/sms", array("to" => "1")); });
ok($e && $e->http === 500 && count(peticiones()) === 1, "un POST con 5xx no se repite");
plan(array(json(200, OK) + array("lento" => true)));
$t0 = microtime(true);
$e = error(function () { cliente(array("timeout" => 0.3, "retries" => 0))->get("/calls"); });
ok($e && $e->http === 0 && $e->getMessage() === "No response from the API: timed out after 0.3 s" && microtime(true) - $t0 < 1.2, "el plazo es de la petición entera");

echo "== all recorre las páginas con su cursor\n";
plan(array(json(200, '{"status":"success","data":{"results":2,"calls":[{"id":1},{"id":2}],"has_more":true,"next_cursor":"a.2"}}'),
           json(200, '{"status":"success","data":{"results":1,"calls":[{"id":3}],"has_more":false,"next_cursor":"a.3"}}')));
$api = cliente();
$ids = array();
foreach ($api->all("/calls", array("order" => "asc")) as $x) $ids[] = $x["id"];
ok($ids === array(1, 2, 3) && array_column(peticiones(), "ruta") === array("/v3/calls?order=asc", "/v3/calls?order=asc&cursor=a.2") && $api->lastCursor === "a.3",
   "las dos páginas, y el último cursor");

echo "== una redirección no se sigue\n";
plan(array(), array("/v3/calls" => json(302, OK, array("Location" => "/robado")), "/v3/sms" => json(307, OK, array("Location" => "/robado")), "/robado" => json(200, OK)));
$api = cliente(array("retries" => 2));
$e1 = error(function () use ($api) { $api->get("/calls"); });
$e2 = error(function () use ($api) { $api->post("/sms", array("to" => "1", "text" => "secreto")); });
ok($e1 && $e1->http === 302 && $e1->response === null && strpos($e1->getMessage(), "redirect to /robado not followed") !== false, "302: un error con su Location");
ok($e2 && $e2->http === 307 && strpos($e2->getMessage(), "redirect to /robado not followed") !== false, "307: un error con su Location");
ok(array_column(peticiones(), "ruta") === array("/v3/calls", "/v3/sms"), "al destino no le llega nada");

echo "== un 2xx que no es JSON es un error; un fichero, no\n";
plan(array(array("estado" => 200, "cabeceras" => array("Content-Type" => "text/html"), "cuerpo" => "<html>Portal</html>"),
           array("estado" => 200, "cabeceras" => array("Content-Type" => "text/html"), "cuerpo" => "<html>Portal</html>"),
           array("estado" => 200, "cabeceras" => array("Content-Type" => "audio/mpeg"), "cuerpo" => "MP3"),
           array("estado" => 200, "cabeceras" => array("Content-Type" => "application/pdf"), "cuerpo" => "%PDF"), array("estado" => 204)));
$api = cliente(array("retries" => 0));
$e1 = error(function () use ($api) { $api->post("/sms", array("to" => "1")); });
$e2 = error(function () use ($api) { $api->get("/calls"); });
ok($e1 && $e2 && $e1->http === 200 && $e1->getMessage() === "HTTP 200: the response is not JSON (text/html)" && $e2->http === 200, "el HTML de un portal cautivo, en POST y en GET");
ok($api->get("/rec")["body"] === "MP3" && $api->get("/cert")["content_type"] === "application/pdf" && $api->delete("/x/1")["body"] === "", "un audio, un PDF y un 204, como siempre");

echo "== all de una lista que no va por páginas es un error\n";
plan(array(json(200, '{"status":"success","data":{"results":2,"calls":[{"id":1},{"id":2}],"has_more":true}}')));
$vistos = array();
$e = error(function () use (&$vistos) { foreach (cliente()->all("/calls/missed") as $x) $vistos[] = $x; });
ok($e && strpos($e->getMessage(), "not paginated") !== false && $vistos === array() && count(peticiones()) === 1, "un error, antes de dar nada");

echo "== un «+» en la query de la ruta llega como «+»\n";
plan(array(json(200, OK)));
cliente()->get("/calls?from_datetime=2026-11-20T10:15:03+01:00&a=%20");
$p = peticiones()[0];
ok($p["ruta"] === "/v3/calls?from_datetime=2026-11-20T10%3A15%3A03%2B01%3A00&a=%20" && $p["firma"], "%2B, y la firma casa");

echo "== un DELETE que se hizo y perdió la respuesta\n";
plan(array(array("corta" => true), json(404, '{"status":"error","status_code":"4","message":"Speed dial not found"}')));
$r = null;
error(function () use (&$r) { $r = cliente(array("retries" => 1))->delete("/speed-dials/9121"); });
ok(($r["message"] ?? null) === "Already deleted" && ($r["data"]["changed"] ?? null) === false && count(peticiones()) === 2, "el 404 del reintento: ya estaba borrado");
plan(array(json(404, '{"status":"error","status_code":"4","message":"Speed dial not found"}')));
$e = error(function () { cliente(array("retries" => 1))->delete("/speed-dials/9121"); });
ok($e && $e->http === 404, "sin fallo de red antes, un 404 es un error");

echo "== lastCursor es el del recorrido; lastRateLimit, el cupo de la última respuesta\n";
plan(array(), array("/v3/vacia" => json(200, '{"status":"success","data":{"results":0,"calls":[],"has_more":false,"next_cursor":null}}'),
                    "/v3/una" => json(200, '{"status":"success","data":{"results":1,"calls":[{"id":1}],"has_more":false,"next_cursor":"u1"}}'),
                    "/v3/error" => json(400, '{"status":"error","status_code":"3","message":"Parameter cursor not valid"}')));
$api = cliente(array("retries" => 0));
foreach ($api->all("/vacia", array("cursor" => "c9")) as $x) {}
$c1 = $api->lastCursor;
foreach ($api->all("/una") as $x) {}
$c2 = $api->lastCursor;
error(function () use ($api) { foreach ($api->all("/error") as $x) {} });
ok($c1 === "c9" && $c2 === "u1" && $api->lastCursor === null, "c9 al reanudar con una página vacía, y nada si falla al empezar");
plan(array(json(200, OK, array("X-RateLimit-Limit" => "10", "X-RateLimit-Remaining" => "7", "X-RateLimit-Reset" => "1")), json(200, OK)));
$api = cliente();
$api->get("/a");
$c1 = property_exists($api, "lastRateLimit") ? $api->lastRateLimit : "no";
$api->get("/b");
ok($c1 === array("limit" => 10, "remaining" => 7, "reset" => 1) && property_exists($api, "lastRateLimit") && $api->lastRateLimit === null, "10/7/1, y nada sin las cabeceras");

echo "== las opciones\n";
$malas = 0;
foreach (array(array("retries" => -1), array("timeout" => 0), array("baseUrl" => "ftp://x/v3"), array("base" => "x")) as $o) {
    try { new MundoSMS(TOKEN, KEY, $o); } catch (InvalidArgumentException $e) { $malas++; }
}
ok($malas === 4, "un valor que no vale, o una opción que no existe, da InvalidArgumentException");
ob_start(); var_dump(new MundoSMS(TOKEN, KEY)); $visto = ob_get_clean();
ok(strpos($visto, TOKEN) === false && strpos($visto, KEY) === false, "var_dump no enseña el token ni la clave");
ok($avisos === array(), "ni un aviso de PHP (" . implode("; ", array_slice($avisos, 0, 2)) . ")");

proc_terminate($proc);
array_map("unlink", glob("$dir/*"));
@rmdir($dir);
echo $fallos === 0 ? "== todo bien\n" : "== $fallos fallo(s)\n";
exit(min($fallos, 99));
