"""MundoSMS API v3 — cliente Python en un solo fichero (Python 3.9 o superior). Sin dependencias.
Documentación: https://www.mundosms.es/api-v3
Código y versiones: https://github.com/mundosms/mundosms-sdk (licencia MIT)

    from mundosms import MundoSMS, MundoSMSError
    api = MundoSMS("TOKEN", "CLAVE_PRIVADA")       # la clave privada, "" si tu API Key no exige firma
    r = api.post("/sms", {"to": "34600123456", "text": "Hola", "from": "MiEmpresa"})
    print(r["data"]["id"])
    for call in api.all("/calls", {"from_datetime": "2026-11-01", "stop_at_ongoing": True}):
        ...

Errores: excepción MundoSMSError con .http (código HTTP), .code (la palabra de la API: "not_found", "invalid_parameter"…),
.message (también str(e)), .detail, .data, .response (la respuesta entera) y .request_id (la cabecera X-Request-Id). También
cuando la API contesta con "status": "error", venga con el código HTTP que venga. Un WhatsApp sin confirmar (202) no es un error: vuelve la respuesta,
con data["state"] "unknown".
Cada POST lleva una Idempotency-Key (un UUID nuevo en cada llamada, la misma en todos sus intentos; la tuya, con
post(path, data, idempotency_key="pedido-881")). Por eso un POST que falla por la red, o con 409 request_in_progress, se
repite sin miedo: la API no lo hace dos veces. Si la respuesta es la guardada de la primera vez, trae
"idempotent_replayed": True.
Reintenta: un 429, en cualquier método (no se hizo nada); un fallo de red, en cualquier método; un 5xx, solo en GET (un POST
con 5xx no se repite: un 500 puede llegar con la operación hecha, y la API lo guarda con la Idempotency-Key, así que
repetirlo con ella devolvería el mismo 500; un 503 no hizo nada, y puedes repetirlo tú con la misma clave). Espera lo que
diga Retry-After (como mucho 60 s) o, si no lo trae, 1, 2, 4… s. Un DELETE que se repite tras quedarse sin respuesta y da 404 ya estaba hecho: vuelve
como éxito, con data["changed"] False.
Una redirección (3xx) no se sigue nunca: es un MundoSMSError (la API no redirige, y seguirla llevaría tu token y tu firma a
otro servidor). Un 2xx que no es JSON (el HTML de un proxy o de un portal cautivo) también es un error; los ficheros que da
la API (audio, imagen, PDF) vuelven por su Content-Type.
Los avisos que te mandamos (callback_url y webhooks de WhatsApp) se comprueban con MundoSMS.verify_webhook().
"""
import base64
import hashlib
import hmac
import http.client
import json
import re
import socket
import threading
import time
import uuid
import urllib.error
import urllib.parse
import urllib.request
from email.utils import parsedate_to_datetime

__version__ = "2.0.0"
MAX_RETRY_WAIT = 60      # segundos: lo más que se espera antes de un reintento, aunque Retry-After pida más
WEBHOOK_TOLERANCE = 300  # segundos: lo más que puede alejarse de tu hora la «t» de la firma de un aviso
_IDEMPOTENCY_KEY = re.compile(r"[A-Za-z0-9_.:-]{1,64}")   # la forma que exige la API para la Idempotency-Key
_last_timestamp = 0                # el X-Timestamp de la última petición de este proceso (milisegundos)
_timestamp_lock = threading.Lock()


class MundoSMSError(Exception):
    """El error de cualquier petición: un 4xx o un 5xx, una respuesta 2xx con "status": "error" o, sin respuesta (la red o
    el plazo agotado), http 0 y un mensaje que empieza por «No response from the API:»."""

    def __init__(self, message, http=0, code=None, detail=None, response=None, data=None, request_id=None):
        super().__init__(message)
        self.message = message           # el message de la API ("Call not found"…) o el del SDK
        self.http = http                 # el código HTTP; 0 si no hubo respuesta
        self.code = code                 # el code de la API ("not_found", "invalid_parameter"…); None si la respuesta no lo trae
        self.detail = detail             # el detail de la respuesta, si lo trae
        self.response = response         # la respuesta entera, decodificada; None si no era JSON
        self.data = data                 # el data de la respuesta (en WhatsApp, el id del mensaje que puede haber salido)
        self.request_id = request_id     # la cabecera X-Request-Id: con ella encuentras la petición en Logs API

    def __reduce__(self):   # con pickle (Celery, multiprocessing), con todos sus campos y no solo el mensaje
        return (self.__class__, (self.message, self.http, self.code, self.detail, self.response, self.data, self.request_id))


class MundoSMS:
    def __init__(self, token, private_key="", *, base_url="https://api.mundosms.es/v3", retries=3, timeout=60):
        """token: el Token de tu API Key (empieza por eyJ); private_key: su clave privada si exige firma (lo normal), o "".
        retries: cuántas veces se reintenta lo que es seguro repetir (0, ninguna). timeout: los segundos que puede durar cada
        petición entera (conectar, mandar y recibir la respuesta). Un valor que no vale es un ValueError."""
        base_url = base_url.rstrip("/")
        u = urllib.parse.urlsplit(base_url)
        if u.scheme not in ("https", "http") or not u.netloc or u.query or u.fragment:
            raise ValueError("MundoSMS: base_url must be an http(s) address without query, like https://api.mundosms.es/v3")
        if isinstance(retries, bool) or not isinstance(retries, int) or retries < 0:
            raise ValueError("MundoSMS: retries must be an integer, 0 or more")
        if timeout is not None and (isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or not 0 < timeout < float("inf")):
            raise ValueError("MundoSMS: timeout must be a number of seconds greater than 0")
        self._token = token.strip()
        self._private_key = (private_key or "").strip()
        self.base_url = base_url
        self.retries = retries
        self.timeout = timeout
        self.last_cursor = None       # el del último all(): guárdalo para la siguiente sincronización
        self.last_rate_limit = None   # el cupo que dijo la última respuesta: {"limit", "remaining", "reset"}, o None

    def __repr__(self):
        return "MundoSMS(base_url=%r)" % self.base_url   # sin el token ni la clave: que no acaben en un log

    def get(self, path, query=None):
        """GET: query va en la query."""
        return self.request("GET", path, query)

    def post(self, path, data=None, *, idempotency_key=None):
        """POST: data va en un cuerpo JSON. idempotency_key: tu clave para que repetir este POST no lo vuelva a hacer (por
        ejemplo, el id de tu pedido); sin ella, un UUID nuevo en cada llamada."""
        return self.request("POST", path, data, idempotency_key=idempotency_key)

    def patch(self, path, data):
        """PATCH: data va en un cuerpo JSON."""
        return self.request("PATCH", path, data)

    def delete(self, path, query=None):
        """DELETE: query va en la query."""
        return self.request("DELETE", path, query)

    def all(self, path, query=None):
        """Recorre un listado entero, página a página, siguiendo su cursor (next_cursor), elemento a elemento. Cada página
        lleva los mismos filtros (y el mismo order) más el cursor. Para sincronizar, guarda tú last_cursor al acabar: la
        siguiente vez empiezas desde ahí, pasándolo como cursor. last_cursor es el de este recorrido: al empezar, el cursor
        que le pasas (o None), y después el último next_cursor que llegó (una página sin cursor no lo borra). Es uno por
        cliente: para dos recorridos a la vez, un cliente para cada uno. last_cursor avanza cuando ya te ha dado todos los
        elementos de una página: si tu código falla a mitad de una, sigue en la anterior.
        Termina con has_more false (con stop_at_ongoing, también cuando la página se para en una llamada en curso), o si el
        cursor no avanza. Una página vacía con has_more true (con stop_at_ongoing y un filtro, cuando ha mirado muchas que no
        casan) no es el final: sigue.
        Una lista que no va por páginas (GET /calls/missed y /calls/live: has_more sin next_cursor) no se puede recorrer
        entera: es un MundoSMSError, antes de dar nada. Sube limit, o filtra, y pídela con get()."""
        query = dict(query or {})
        start = query.get("cursor")
        self.last_cursor = None if start in (None, "") else start
        list_key = None
        while True:
            r = self.get(path, query)
            data = r.get("data") if isinstance(r.get("data"), dict) else {}
            cursor = data.get("next_cursor")
            if data.get("has_more") and data.get("results") and not cursor:
                raise MundoSMSError(_NOT_PAGINATED % path, 200, None, None, r, data)
            if list_key is None:
                list_key = next((k for k, v in data.items() if isinstance(v, list)), None)
            for item in (data.get(list_key) or []) if list_key else []:
                yield item
            if cursor:
                self.last_cursor = cursor
            if not data.get("has_more") or not cursor or cursor == query.get("cursor") or data.get("ongoing_call"):
                return
            query["cursor"] = cursor

    def request(self, method, path, data=None, *, idempotency_key=None):
        """Una petición cualquiera. path: "/calls", "/numbers/34910123456"… (sin /v3: si lo lleva, se quita). Si la ruta trae
        "?…", esos parámetros se juntan con data (gana data). En GET y DELETE, data va en la query; en POST y PATCH, en el
        cuerpo JSON. Devuelve la respuesta decodificada.
        idempotency_key: la Idempotency-Key (en un POST, sin ella, un UUID nuevo); la API la tiene en cuenta en los POST."""
        method = method.upper()
        idem_key = _idempotency_key(idempotency_key, method)   # antes de nada: con una clave mala no se manda nada
        base_path = urllib.parse.urlsplit(self.base_url).path
        encoded_path, path_query = _split_path(path, base_path.endswith("/v3"))
        data = data or {}
        body = ""
        if method in ("GET", "DELETE"):
            path_query.update(data)
        elif data:
            try:
                body = json.dumps(data, ensure_ascii=False, separators=(",", ":"), allow_nan=False)
            except ValueError as e:   # NaN o infinito: no es JSON
                raise ValueError("MundoSMS: the body cannot be encoded as JSON (nothing was sent): %s" % e) from None
        query = "&".join(_q(str(k)) + "=" + _q(t) for k, t in ((k, _query_value(v)) for k, v in path_query.items()) if t is not None)
        rest = encoded_path + ("?" + query if query else "")
        url = self.base_url + rest
        signed_path = base_path + rest   # lo que se firma es justo lo que se manda
        attempt = 0
        after_network = False   # un intento de antes se quedó sin respuesta (o llegó dos veces): puede que se hiciera
        while True:
            attempt += 1
            ts = _timestamp()   # un reintento inmediato, o dos peticiones iguales a la vez, no firman igual
            headers = {"Authorization": "Bearer " + self._token, "X-Timestamp": ts, "Accept": "application/json",
                       "User-Agent": "MundoSMS-Python/" + __version__}
            if body:
                headers["Content-Type"] = "application/json"
            if idem_key:
                headers["Idempotency-Key"] = idem_key   # la misma en todos los intentos de esta llamada
            if self._private_key:
                signed = (self._token + "." + ts + "." + method + " " + signed_path + "." + body).encode()
                headers["X-Signature"] = base64.urlsafe_b64encode(
                    hmac.new(self._private_key.encode(), signed, hashlib.sha256).digest()).rstrip(b"=").decode()
            req = urllib.request.Request(url, data=body.encode() if body else None, headers=headers, method=method)
            http_, resp_headers, resp, network_error = 0, {}, b"", ""
            deadline = _Deadline(self.timeout)
            try:
                try:
                    r = urllib.request.build_opener(_NoRedirect(), _Http(deadline), _Https(deadline)).open(req, timeout=self.timeout)
                except urllib.error.HTTPError as e:
                    r = e
                with r:
                    http_, resp_headers, resp = r.getcode(), r.headers, r.read()
            except (urllib.error.URLError, OSError, http.client.HTTPException) as e:
                network_error = str(getattr(e, "reason", None) or e)
            finally:
                deadline.stop()
            if deadline.expired:
                network_error = "timed out after %s s" % self.timeout
            j = None
            if network_error:
                http_ = 0   # se cortó a medias: es un fallo de red aunque llegara la cabecera
            elif not 300 <= http_ < 400:   # de una redirección no se lee nada
                try:
                    j = json.loads(resp)
                except ValueError:
                    j = None
                if not isinstance(j, dict):
                    j = None
            if _retryable(method, bool(network_error), http_, j, idem_key) and attempt <= self.retries:
                after_network = after_network or bool(network_error) or _duplicated(http_, j)
                time.sleep(_retry_wait(resp_headers.get("Retry-After"), attempt))
                continue
            if network_error:
                self.last_rate_limit = None
                raise MundoSMSError("No response from the API: " + network_error)
            self.last_rate_limit = _rate_limit(resp_headers)
            rid = resp_headers.get("X-Request-Id")
            if 300 <= http_ < 400:   # no se sigue: llevaría el token y la firma a otro servidor
                raise MundoSMSError(_redirected(http_, resp_headers.get("Location")), http_, request_id=rid)
            replayed = str(resp_headers.get("Idempotent-Replayed", "")).strip().lower() == "true"
            if replayed and j is not None:
                j["idempotent_replayed"] = True   # la respuesta guardada de la primera vez: no se ha vuelto a hacer
            if 200 <= http_ < 300:
                if j is None:   # un fichero, o algo que no es de la API
                    content_type = resp_headers.get("Content-Type", "") or ""
                    if resp and not _is_file(content_type):
                        raise MundoSMSError(_not_json(http_, content_type), http_, request_id=rid)
                    file = {"status": "success", "content_type": content_type, "body": resp}
                    if replayed:
                        file["idempotent_replayed"] = True
                    return file
                if j.get("status") != "error":
                    return j
            if method == "DELETE" and after_network and http_ == 404 and j is not None and j.get("code") != "route_not_found":
                return _already_deleted()   # lo borró el intento que se quedó sin respuesta
            if j is not None:
                raise MundoSMSError(j.get("message", "Error"), http_, _text(j.get("code")), j.get("detail"), j, j.get("data"), rid)
            raise MundoSMSError("HTTP %d" % http_, http_, request_id=rid)

    @staticmethod
    def verify_webhook(secret, header, body, tolerance=WEBHOOK_TOLERANCE):
        """True si un aviso que te llega (callback_url o webhook de WhatsApp) es nuestro y nadie lo ha tocado.
        secret: el de «API y Soporte → Webhooks» (whsec_…); header: X-MundoSMS-Signature; body: el cuerpo TAL CUAL llegó
        (bytes, o str en UTF-8), sin volver a serializar el JSON; tolerance: los segundos que puede alejarse «t» de tu hora.
        Una cabecera que falta o está mal formada da False, sin excepción."""
        if isinstance(secret, (bytes, bytearray)):
            try:
                secret = bytes(secret).decode("utf-8")
            except UnicodeDecodeError:
                return False
        if isinstance(body, str):
            body = body.encode("utf-8")
        elif isinstance(body, (bytearray, memoryview)):
            body = bytes(body)
        elif not isinstance(body, bytes):
            raise TypeError("MundoSMS.verify_webhook: pass the raw body as received (bytes or str), not a decoded value")
        secret = secret.strip() if isinstance(secret, str) else ""
        if not secret or not isinstance(header, str):
            return False
        t, signatures = None, []
        for part in header.split(","):
            k, equals, v = part.partition("=")
            k, v = k.strip(), v.strip()
            if not equals:
                continue
            if k == "t":
                if t is not None:
                    return False   # dos «t»: no se sabe cuál vale
                t = v
            elif k == "v1":
                signatures.append(v.lower())
        if t is None or not re.fullmatch(r"[0-9]{1,15}", t) or not signatures:
            return False
        if abs(int(time.time()) - int(t)) > tolerance:
            return False
        expected = hmac.new(secret.encode("utf-8"), t.encode() + b"." + body, hashlib.sha256).hexdigest().encode()
        valid = False
        for sig in signatures:   # todas, sin cortar en la primera: compare_digest es de tiempo constante
            if re.fullmatch(r"[0-9a-f]{64}", sig) and hmac.compare_digest(expected, sig.encode()):
                valid = True
        return valid


_NOT_PAGINATED = ("MundoSMS: all() cannot go through %s: it has more (has_more) but no next_cursor, so the list is not paginated: "
                  "raise limit or narrow the filters, and read it with get()")
_FILE_TYPES = ("audio/", "image/")   # lo que la API da como fichero (grabaciones, locuciones, diagramas), y el PDF
_FILE_TYPES_EXACT = ("application/pdf", "application/octet-stream")


def _is_file(content_type):
    """Si un 2xx que no es JSON es un fichero de la API (audio, imagen, PDF), por su Content-Type."""
    t = content_type.split(";")[0].strip().lower()
    return t.startswith(_FILE_TYPES) or t in _FILE_TYPES_EXACT


def _not_json(http_, content_type):
    return "HTTP %d: the response is not JSON (%s)" % (http_, content_type.split(";")[0].strip() or "no Content-Type")


def _redirected(http_, location):
    """El error de una redirección, con adónde mandaba (las redirecciones no se siguen: la API no redirige)."""
    where = " to %s" % location.strip()[:300] if location else ""
    return "HTTP %d: redirect%s not followed (the API does not redirect: check base_url)" % (http_, where)


def _code(j):
    """El code de una respuesta de error ("duplicated_request"…), o None si no lo trae."""
    return j.get("code") if isinstance(j, dict) else None


def _text(v):
    """Un valor de la respuesta como texto (el code), o None si no viene."""
    if v is None:
        return None
    return v if isinstance(v, str) else json.dumps(v, ensure_ascii=False)


def _duplicated(http_, j):
    """409 duplicated_request: la MISMA petición firmada llegó dos veces (la repitió algo entre medias)."""
    return http_ == 409 and _code(j) == "duplicated_request"


def _already_deleted():
    """La respuesta de un DELETE que se repitió tras quedarse sin respuesta y dio 404: lo borró el primer intento."""
    return {"status": "success", "message": "Already deleted", "data": {"state": "deleted", "changed": False}}


def _rate_limit(headers):
    """El cupo de la respuesta (X-RateLimit-Limit, -Remaining y -Reset, en enteros), o None si no lo trae."""
    out = {}
    for k in ("limit", "remaining", "reset"):
        v = headers.get("X-RateLimit-" + k.capitalize())
        try:
            out[k] = int(str(v).strip()) if v is not None else None
        except ValueError:
            out[k] = None
    return out if any(v is not None for v in out.values()) else None


def _timestamp():
    """El X-Timestamp de una petición: milisegundos, y siempre mayor que el de la anterior de este proceso (también desde varios
    hilos). Dos peticiones iguales en el mismo milisegundo (un reintento tras un 429 con Retry-After: 0, o dos a la vez)
    firmarían igual, y la API rechazaría la segunda por repetida («409 duplicated_request»)."""
    global _last_timestamp
    with _timestamp_lock:
        _last_timestamp = max(int(time.time() * 1000), _last_timestamp + 1)
        return str(_last_timestamp)


def _idempotency_key(key, method):
    """La Idempotency-Key de la llamada: la tuya, si tiene la forma que exige la API; en un POST sin ella, un UUID v4 nuevo."""
    if key is None:
        return str(uuid.uuid4()) if method == "POST" else None
    if not isinstance(key, str) or not _IDEMPOTENCY_KEY.fullmatch(key):
        raise ValueError("MundoSMS: idempotency_key must be 1 to 64 characters: A-Z, a-z, 0-9 and _ . : - (nothing was sent)")
    return key


def _retryable(method, network, http_, j, idem_key):
    """Si se puede repetir: un 429 en cualquier método; un GET, ante un fallo de red o un 5xx; un PATCH o un DELETE, ante un
    fallo de red; un POST (con su Idempotency-Key), ante un fallo de red o un 409 request_in_progress. Un 5xx de un POST no:
    un 500 puede haberse hecho y la API lo guarda con la clave (repetirlo devolvería el mismo 500), y un 503 no hizo nada, pero
    repetirlo con la misma clave se deja a tu código. Un 409 duplicated_request (la MISMA petición firmada llegó dos veces:
    la repitió algo entre medias, como hace un cliente HTTP con una conexión que se cerró) es un fallo de red más."""
    if http_ == 429:
        return True
    network = network or _duplicated(http_, j)
    if method == "GET":
        return network or http_ >= 500
    if method in ("PATCH", "DELETE"):
        return network
    if idem_key:
        return network or (http_ == 409 and _code(j) == "request_in_progress")
    return False


def _q(s):
    """RFC 3986: todo codificado menos A-Z a-z 0-9 - . _ ~"""
    return urllib.parse.quote(s, safe="")


def _query_value(v):
    """Un valor de la query: true/false, una lista separada por comas; None o una lista vacía (o solo de None), nada."""
    if v is None:
        return None
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, (list, tuple)):
        items = [t for t in map(_query_value, v) if t is not None]
        return ",".join(items) if items else None
    return str(v)


def _split_path(path, strip_v3):
    """La ruta codificada trozo a trozo (un %XX ya escrito se respeta), sin el /v3 del principio si la base ya lo lleva,
    y los parámetros que traiga tras «?»: con su %XX decodificado y el «+» como «+» (RFC 3986; no es un formulario), para
    que una fecha con zona escrita en la ruta (from_datetime=…+01:00) llegue tal cual."""
    path, _, query = str(path).partition("?")
    path_query = {}
    for pair in query.split("&"):
        if pair:
            k, _, v = pair.partition("=")
            path_query[urllib.parse.unquote(k)] = urllib.parse.unquote(v)
    path = "/" + path.lstrip("/")
    if strip_v3 and (path == "/v3" or path.startswith("/v3/")):
        path = "/" + path[3:].lstrip("/")
    segments = ("".join(p if i % 2 else _q(p) for i, p in enumerate(re.split(r"(%[0-9A-Fa-f]{2})", seg))) for seg in path.split("/"))
    return "/".join(segments), path_query


def _retry_wait(retry_after, attempt):
    """Segundos antes del siguiente intento: lo que pida Retry-After (segundos o fecha HTTP), o 1, 2, 4… Como mucho 60."""
    s = None
    if retry_after:
        try:
            s = float(retry_after)
        except ValueError:
            try:
                s = parsedate_to_datetime(retry_after).timestamp() - time.time()
            except (TypeError, ValueError, IndexError, OverflowError):
                s = None
    if s is None or s != s:   # sin cabecera, o NaN
        s = 2 ** (attempt - 1)
    return max(0.0, min(MAX_RETRY_WAIT, s))


class _Deadline:
    """El plazo de la petición ENTERA (conectar, mandar y leer la respuesta): el timeout de urllib es por cada operación del
    socket, y una respuesta que llega poco a poco no lo agota nunca. Al vencer, corta la conexión."""

    def __init__(self, timeout):
        self.expired = False
        self._socks = []
        self._lock = threading.Lock()
        self._timer = threading.Timer(timeout, self._expire) if timeout is not None else None
        if self._timer:
            self._timer.daemon = True
            self._timer.start()

    def watch(self, sock):
        with self._lock:
            self._socks.append(sock)
            if self.expired:
                _shutdown(sock)

    def _expire(self):
        with self._lock:
            self.expired = True
            for s in self._socks:
                _shutdown(s)

    def stop(self):
        if self._timer:
            self._timer.cancel()


def _shutdown(sock):
    try:
        socket.socket.shutdown(sock, socket.SHUT_RDWR)   # el del socket de debajo, también si es TLS: despierta al que lee
    except OSError:
        pass


class _Watched:
    """Manejador de urllib que pone bajo el reloj cada conexión que abre."""

    def __init__(self, deadline):
        super().__init__()
        self.deadline = deadline

    def do_open(self, conn_class, req, **kw):
        deadline = self.deadline

        def open_conn(*a, **k):
            c = conn_class(*a, **k)
            original_connect = c.connect

            def connect():
                original_connect()
                deadline.watch(c.sock)
            c.connect = connect
            return c
        return super().do_open(open_conn, req, **kw)


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """Las redirecciones no se siguen: urllib las sigue por defecto, y al otro servidor le llegarían el token y la firma
    (también de https a http). El 3xx llega como respuesta, y request() lo da como error."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class _Http(_Watched, urllib.request.HTTPHandler):
    pass


class _Https(_Watched, urllib.request.HTTPSHandler):
    pass
