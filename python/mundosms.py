"""MundoSMS API v3 — cliente Python en un solo fichero (Python 3.8 o superior). Sin dependencias.
Documentación: https://www.mundosms.es/api-v3
Código y versiones: https://github.com/mundosms/mundosms-sdk (licencia MIT)

    from mundosms import MundoSMS, MundoSMSError
    api = MundoSMS("TOKEN", "CLAVE_PRIVADA")       # la clave privada, "" si tu API Key no exige firma
    r = api.post("/sms", {"to": "34600123456", "text": "Hola", "from": "MiEmpresa"})
    print(r["data"]["id"])
    for llamada in api.todos("/calls", {"from_datetime": "2026-11-01", "stop_at_ongoing": True}):
        ...

Errores: excepción MundoSMSError con .http (código HTTP), .status_code ("3", "4"…), str(e) y .detail.
Reintenta solo, con espera creciente, lo que es seguro repetir: un 429 (no se hizo nada) y, en GET, los 5xx y los fallos de
red. Un POST o un PATCH que falla por la red NO se repite: no se sabe si llegó (usa client_id y consulta antes).
"""
import base64
import hashlib
import hmac
import json
import time
import urllib.error
import urllib.parse
import urllib.request

__version__ = "3.0.0"


class MundoSMSError(Exception):
    def __init__(self, mensaje, http=0, status_code=None, detail=None, respuesta=None):
        super().__init__(mensaje)
        self.http = http
        self.status_code = status_code
        self.detail = detail
        self.respuesta = respuesta


class MundoSMS:
    def __init__(self, token, clave_privada="", base="https://api.mundosms.es/v3", reintentos=3, plazo=30):
        """token: el Token de tu API Key (empieza por eyJ); clave_privada: su clave privada si exige firma (lo normal), o ""."""
        self.token = token.strip()
        self.clave = clave_privada or ""
        self.base = base.rstrip("/")
        self.reintentos = reintentos
        self.plazo = plazo
        self.ultimo_cursor = None   # el de todos(): guárdalo para la siguiente sincronización

    def get(self, ruta, filtros=None):
        return self.peticion("GET", ruta, filtros)

    def post(self, ruta, datos=None):
        return self.peticion("POST", ruta, datos)

    def patch(self, ruta, datos):
        return self.peticion("PATCH", ruta, datos)

    def delete(self, ruta, filtros=None):
        return self.peticion("DELETE", ruta, filtros)

    def todos(self, ruta, filtros=None):
        """Recorre un listado entero, página a página, siguiendo su cursor (next_cursor o next_from_id), elemento a elemento."""
        filtros = dict(filtros or {})
        lista = None
        while True:
            data = self.get(ruta, filtros).get("data") or {}
            if lista is None:
                lista = next((k for k, v in data.items() if isinstance(v, list)), None)
            for e in (data.get(lista) or []) if lista else []:
                yield e
            self.ultimo_cursor = data.get("next_cursor", data.get("next_from_id"))
            if not data.get("has_more") or not data.get("results"):
                return
            if data.get("next_cursor"):
                filtros.pop("from_datetime", None)
                filtros.pop("order", None)
                filtros["cursor"] = data["next_cursor"]
            elif data.get("next_from_id") is not None:
                filtros["from_id"] = data["next_from_id"]
            else:
                return

    def peticion(self, metodo, ruta, datos=None):
        """Una petición cualquiera. ruta: "/calls", "/numbers/34910123456"… Devuelve la respuesta decodificada."""
        metodo = metodo.upper()
        ruta = "/" + ruta.lstrip("/")
        datos = datos or {}
        query, cuerpo = "", ""
        if metodo in ("GET", "DELETE"):
            q = {}
            for k, v in datos.items():
                if v is None:
                    continue
                q[k] = ("true" if v else "false") if isinstance(v, bool) else (",".join(map(str, v)) if isinstance(v, (list, tuple)) else str(v))
            query = urllib.parse.urlencode(q, quote_via=urllib.parse.quote)
        elif datos:
            cuerpo = json.dumps(datos, ensure_ascii=False, separators=(",", ":"))
        url = self.base + ruta + ("?" + query if query else "")
        camino = urllib.parse.urlsplit(url).path + ("?" + query if query else "")
        intento = 0
        while True:
            intento += 1
            ts = str(int(time.time() * 1000))   # milisegundos: dos peticiones iguales seguidas no firman igual
            cab = {"Authorization": "Bearer " + self.token, "X-Timestamp": ts, "Accept": "application/json",
                   "User-Agent": "MundoSMS-Python/" + __version__}
            if cuerpo:
                cab["Content-Type"] = "application/json"
            if self.clave:
                firmado = (self.token + "." + ts + "." + metodo + " " + camino + "." + cuerpo).encode()
                cab["X-Signature"] = base64.urlsafe_b64encode(hmac.new(self.clave.encode(), firmado, hashlib.sha256).digest()).rstrip(b"=").decode()
            pet = urllib.request.Request(url, data=cuerpo.encode() if cuerpo else None, headers=cab, method=metodo)
            http, tipo, resp, error_red = 0, "", b"", ""
            try:
                with urllib.request.urlopen(pet, timeout=self.plazo) as r:
                    http, tipo, resp = r.status, r.headers.get("Content-Type", ""), r.read()
            except urllib.error.HTTPError as e:
                http, tipo, resp = e.code, e.headers.get("Content-Type", ""), e.read()
            except (urllib.error.URLError, OSError) as e:
                error_red = str(e)
            repetible = http == 429 or (metodo == "GET" and (error_red or http >= 500))
            if repetible and intento <= self.reintentos:
                time.sleep(min(60, 2 ** (intento - 1)))
                continue
            if error_red:
                raise MundoSMSError("Sin respuesta de la API: " + error_red)
            try:
                j = json.loads(resp)
            except ValueError:
                j = None
            if 200 <= http < 300:
                return j if isinstance(j, dict) else {"status": "success", "content_type": tipo, "body": resp}   # un audio
            if isinstance(j, dict):
                raise MundoSMSError(j.get("message", "Error"), http, j.get("status_code"), j.get("detail"), j)
            raise MundoSMSError("HTTP %d" % http, http)
