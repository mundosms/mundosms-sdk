"""MundoSMS API v3 — cliente Python en un solo fichero (Python 3.8 o superior). Sin dependencias.
Documentación: https://www.mundosms.es/api-v3
Código y versiones: https://github.com/mundosms/mundosms-sdk (licencia MIT)

    from mundosms import MundoSMS, MundoSMSError
    api = MundoSMS("TOKEN", "CLAVE_PRIVADA")       # la clave privada, "" si tu API Key no exige firma
    r = api.post("/sms", {"to": "34600123456", "text": "Hola", "from": "MiEmpresa"})
    print(r["data"]["id"])
    for call in api.all("/calls", {"from_datetime": "2026-11-01", "stop_at_ongoing": True}):
        ...

Errores: excepción MundoSMSError con .http (código HTTP), .status_code ("3", "4"…), .message (también str(e)), .detail,
.data, .response (la respuesta entera) y .request_id (la cabecera X-Request-Id). También cuando la API contesta con
"status": "error", venga con el código HTTP que venga. Un WhatsApp sin confirmar (202) no es un error: vuelve la respuesta,
con data["state"] "unknown".
Cada POST lleva una Idempotency-Key (un UUID nuevo en cada llamada, la misma en todos sus intentos; la tuya, con
post(path, data, idempotency_key="pedido-881")). Por eso un POST que falla por la red, o con 409 «Request in progress», se
repite sin miedo: la API no lo hace dos veces. Si la respuesta es la guardada de la primera vez, trae
"idempotent_replayed": True.
Reintenta: un 429, en cualquier método (no se hizo nada); un fallo de red, en cualquier método; un 5xx, solo en GET (un POST
con 5xx no se repite: la API no guarda los 5xx, y repetirlo lo haría otra vez). Espera lo que diga Retry-After (como mucho
60 s) o, si no lo trae, 1, 2, 4… s.
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

__version__ = "1.0.0"
MAX_RETRY_WAIT = 60      # segundos: lo más que se espera antes de un reintento, aunque Retry-After pida más
WEBHOOK_TOLERANCE = 300  # segundos: lo más que puede alejarse de tu hora la «t» de la firma de un aviso
_IDEMPOTENCY_KEY = re.compile(r"[A-Za-z0-9_.:-]{1,64}")   # la forma que exige la API para la Idempotency-Key


class MundoSMSError(Exception):
    """El error de cualquier petición: un 4xx o un 5xx, una respuesta 2xx con "status": "error" o, sin respuesta (la red o
    el plazo agotado), http 0 y un mensaje que empieza por «No response from the API:»."""

    def __init__(self, message, http=0, status_code=None, detail=None, response=None, data=None, request_id=None):
        super().__init__(message)
        self.message = message           # el message de la API ("Call not found"…) o el del SDK
        self.http = http                 # el código HTTP; 0 si no hubo respuesta
        self.status_code = status_code   # el status_code de la API, como texto ("3", "4", "12"…)
        self.detail = detail             # el detail de la respuesta, si lo trae
        self.response = response         # la respuesta entera, decodificada; None si no era JSON
        self.data = data                 # el data de la respuesta (en WhatsApp, el id del mensaje que puede haber salido)
        self.request_id = request_id     # la cabecera X-Request-Id: con ella encuentras la petición en Logs API


class MundoSMS:
    def __init__(self, token, private_key="", *, base_url="https://api.mundosms.es/v3", retries=3, timeout=60):
        """token: el Token de tu API Key (empieza por eyJ); private_key: su clave privada si exige firma (lo normal), o "".
        retries: cuántas veces se reintenta lo que es seguro repetir (0, ninguna). timeout: los segundos que puede durar cada
        petición entera (conectar, mandar y recibir la respuesta)."""
        self._token = token.strip()
        self._private_key = (private_key or "").strip()
        self.base_url = base_url.rstrip("/")
        self.retries = retries
        self.timeout = timeout
        self.last_cursor = None   # el de all(): guárdalo para la siguiente sincronización

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
        lleva los mismos filtros (y el mismo order) más el cursor. Para sincronizar, guarda tú last_cursor: la siguiente vez
        empiezas desde ahí, pasándolo como cursor."""
        query = dict(query or {})
        list_key = None
        while True:
            data = self.get(path, query).get("data") or {}
            if list_key is None:
                list_key = next((k for k, v in data.items() if isinstance(v, list)), None)
            for item in (data.get(list_key) or []) if list_key else []:
                yield item
            self.last_cursor = data.get("next_cursor")
            if not data.get("has_more") or not data.get("results") or not data.get("next_cursor"):
                return
            query["cursor"] = data["next_cursor"]

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
            body = json.dumps(data, ensure_ascii=False, separators=(",", ":"))
        query = "&".join(_q(str(k)) + "=" + _q(t) for k, t in ((k, _query_value(v)) for k, v in path_query.items()) if t is not None)
        rest = encoded_path + ("?" + query if query else "")
        url = self.base_url + rest
        signed_path = base_path + rest   # lo que se firma es justo lo que se manda
        attempt = 0
        while True:
            attempt += 1
            ts = str(int(time.time() * 1000))   # milisegundos: dos peticiones iguales seguidas no firman igual
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
                    r = urllib.request.build_opener(_Http(deadline), _Https(deadline)).open(req, timeout=self.timeout)
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
            else:
                try:
                    j = json.loads(resp)
                except ValueError:
                    j = None
            if _retryable(method, bool(network_error), http_, j, idem_key) and attempt <= self.retries:
                time.sleep(_retry_wait(resp_headers.get("Retry-After"), attempt))
                continue
            if network_error:
                raise MundoSMSError("No response from the API: " + network_error)
            replayed = str(resp_headers.get("Idempotent-Replayed", "")).strip().lower() == "true"
            if replayed and isinstance(j, dict):
                j["idempotent_replayed"] = True   # la respuesta guardada de la primera vez: no se ha vuelto a hacer
            if 200 <= http_ < 300:
                if not isinstance(j, dict):   # un fichero
                    file = {"status": "success", "content_type": resp_headers.get("Content-Type", ""), "body": resp}
                    if replayed:
                        file["idempotent_replayed"] = True
                    return file
                if j.get("status") != "error":
                    return j
            rid = resp_headers.get("X-Request-Id")
            if isinstance(j, dict):
                raise MundoSMSError(j.get("message", "Error"), http_, j.get("status_code"), j.get("detail"), j, j.get("data"), rid)
            raise MundoSMSError("HTTP %d" % http_, http_, request_id=rid)

    @staticmethod
    def verify_webhook(secret, header, body, tolerance=WEBHOOK_TOLERANCE):
        """True si un aviso que te llega (callback_url o webhook de WhatsApp) es nuestro y nadie lo ha tocado.
        secret: el de «API y Soporte → Webhooks» (whsec_…); header: X-MundoSMS-Signature; body: el cuerpo TAL CUAL llegó
        (bytes, o str en UTF-8), sin volver a serializar el JSON; tolerance: los segundos que puede alejarse «t» de tu hora.
        Una cabecera que falta o está mal formada da False, sin excepción."""
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


def _idempotency_key(key, method):
    """La Idempotency-Key de la llamada: la tuya, si tiene la forma que exige la API; en un POST sin ella, un UUID v4 nuevo."""
    if key is None:
        return str(uuid.uuid4()) if method == "POST" else None
    if not isinstance(key, str) or not _IDEMPOTENCY_KEY.fullmatch(key):
        raise ValueError("MundoSMS: idempotency_key must be 1 to 64 characters: A-Z, a-z, 0-9 and _ . : - (nothing was sent)")
    return key


def _retryable(method, network, http_, j, idem_key):
    """Si se puede repetir: un 429 en cualquier método; un GET, ante un fallo de red o un 5xx; un PATCH o un DELETE, ante un
    fallo de red; un POST (con su Idempotency-Key), ante un fallo de red o un 409 «Request in progress». Un 5xx de un POST no:
    la API no guarda los 5xx y repetirlo lo haría otra vez. Un 409 «Duplicated request» (la MISMA petición firmada llegó dos
    veces: la repitió algo entre medias, como hace un cliente HTTP con una conexión que se cerró) es un fallo de red más."""
    if http_ == 429:
        return True
    message = j.get("message") if isinstance(j, dict) else None
    network = network or (http_ == 409 and isinstance(message, str) and message.startswith("Duplicated request"))
    if method == "GET":
        return network or http_ >= 500
    if method in ("PATCH", "DELETE"):
        return network
    if idem_key:
        return network or (http_ == 409 and message == "Request in progress")
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
    y los parámetros que traiga tras «?»."""
    path, _, query = str(path).partition("?")
    path_query = dict(urllib.parse.parse_qsl(query, keep_blank_values=True))
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


class _Http(_Watched, urllib.request.HTTPHandler):
    pass


class _Https(_Watched, urllib.request.HTTPSHandler):
    pass
