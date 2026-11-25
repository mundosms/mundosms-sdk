"""Pruebas del cliente Python (mundosms.py), sin red: un servidor de prueba local (http.server) que comprueba la firma como la
API y contesta lo que pide cada caso. Python 3.9 o superior, solo la biblioteca estándar.
    python3 tests/python/test_mundosms.py        (o python3 -m unittest, desde esta carpeta)"""
import base64
import hashlib
import hmac
import json
import os
import socket
import sys
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

AQUI = os.path.dirname(os.path.abspath(__file__))
for d in (os.path.join(AQUI, "..", "..", "python"), os.path.join(AQUI, "..", "..")):   # en el repositorio, o con todos los clientes en una carpeta
    if os.path.exists(os.path.join(d, "mundosms.py")):
        sys.path.insert(0, os.path.abspath(d))
        break
from mundosms import MundoSMS, MundoSMSError  # noqa: E402

TOKEN = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiIsInN1YiI6IjcifQ.cHJ1ZWJh"
KEY = "Pr4ebaClaveDe20Letra"
OK = {"status": "success", "message": "OK", "data": {"id": 8812345}}


def firma(clave, token, ts, metodo, ruta, cuerpo=b""):
    """La firma de «La firma» de la documentación: base64url, sin relleno, del HMAC-SHA256."""
    m = hmac.new(clave.encode(), (token + "." + ts + "." + metodo + " " + ruta + ".").encode() + cuerpo, hashlib.sha256)
    return base64.urlsafe_b64encode(m.digest()).rstrip(b"=").decode()


class Servidor:
    """Apunta cada petición (con su firma comprobada) y contesta con responde(n, petición): (estado, cabeceras, cuerpo) o
    "corta" (cierra sin contestar)."""

    def __init__(self, responde):
        self.peticiones = []
        fuera = self

        class H(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *a):
                pass

            def todo(self):
                b = self.rfile.read(int(self.headers.get("Content-Length") or 0))
                p = {"metodo": self.command, "ruta": self.path, "cabeceras": dict(self.headers), "cuerpo": b,
                     "firma": self.headers.get("X-Signature") == firma(KEY, self.headers.get("Authorization", "")[7:],
                                                                       self.headers.get("X-Timestamp", ""), self.command, self.path, b)}
                fuera.peticiones.append(p)
                r = responde(len(fuera.peticiones), p)
                if r == "corta":
                    self.close_connection = True
                    self.connection.shutdown(socket.SHUT_RDWR)
                    return
                estado, cabeceras, cuerpo = r
                if isinstance(cuerpo, (dict, list)):
                    cuerpo = json.dumps(cuerpo).encode()
                    cabeceras = {"Content-Type": "application/json", **cabeceras}
                self.send_response(estado)
                for k, v in cabeceras.items():
                    self.send_header(k, v)
                self.send_header("Content-Length", str(len(cuerpo)))
                self.end_headers()
                if cabeceras.get("X-Lento"):
                    self.wfile.flush()
                    time.sleep(1.5)
                self.wfile.write(cuerpo)

            do_GET = do_POST = do_PATCH = do_DELETE = todo

        class S(ThreadingHTTPServer):
            daemon_threads = True

            def handle_error(self, *a):
                pass

        self.srv = S(("127.0.0.1", 0), H)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.base = "http://127.0.0.1:%d/v3" % self.srv.server_address[1]

    def cierra(self):
        self.srv.shutdown()
        self.srv.server_close()


class Pruebas(unittest.TestCase):
    def servidor(self, responde):
        s = Servidor(responde)
        self.addCleanup(s.cierra)
        return s

    def cliente(self, s, **opc):
        return MundoSMS(TOKEN, KEY, base_url=s.base, **opc)

    def test_firma(self):
        # los dos ejemplos de «Para comprobar tu código», con la firma de referencia de aquí
        t, k, ts = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiJ9.ZmlybWE", "A1b2C3d4E5f6G7h8I9j0", "1795400000123"
        self.assertEqual(firma(k, t, ts, "GET", "/v3/calls?type=in&limit=5"), "uE09uuJV7e3hSSW2NZIJQ9jMWlT15lMETj-sbrIjoFo")
        self.assertEqual(firma(k, t, ts, "POST", "/v3/sms", b'{"to":"34600123456","text":"Hola"}'), "_62bUnSBUjpWSsAMT580JFfk1Yhx9bCZhS805swJ2to")
        s = self.servidor(lambda n, p: (200, {}, OK))
        api = self.cliente(s)
        api.get("/calls", {"type": "in", "limit": 5})
        api.post("/sms", {"to": "34600123456", "text": "Señor Núñez: 9,50 €"})
        g, p = s.peticiones
        self.assertEqual([g["ruta"], g["cuerpo"], p["ruta"]], ["/v3/calls?type=in&limit=5", b"", "/v3/sms"])
        self.assertEqual(p["cuerpo"].decode(), '{"to":"34600123456","text":"Señor Núñez: 9,50 €"}')
        for x in (g, p):
            self.assertTrue(x["firma"])
            self.assertRegex(x["cabeceras"]["X-Timestamp"], r"^[0-9]{13}$")
        self.assertRegex(p["cabeceras"]["Idempotency-Key"], r"^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")
        self.assertNotIn("Idempotency-Key", g["cabeceras"])

    def test_aviso(self):
        cuerpo = b'{"id":"evt_prueba","type":"sms.status","data":{"to":"34600123456","state":"delivered"}}'
        cab = "t=1795161606,v1=d43d40dfd88dd4aba4633580b2786f8651936516aecc2967868edf333ddfdae5"
        self.assertTrue(MundoSMS.verify_webhook("whsec_ejemplo", cab, cuerpo, tolerance=10 ** 10))
        self.assertTrue(MundoSMS.verify_webhook(b"whsec_ejemplo", cab, cuerpo.decode(), tolerance=10 ** 10))
        self.assertFalse(MundoSMS.verify_webhook("whsec_otro", cab, cuerpo, tolerance=10 ** 10))
        self.assertFalse(MundoSMS.verify_webhook("whsec_ejemplo", cab, cuerpo.replace(b"delivered", b"Delivered"), tolerance=10 ** 10))
        self.assertFalse(MundoSMS.verify_webhook("whsec_ejemplo", cab, cuerpo))   # de hace más de 5 minutos

    def test_reintentos(self):
        s = self.servidor(lambda n, p: (429, {"Retry-After": "0"}, {"status": "error", "code": "requests_per_second", "message": "Too many requests"}) if n < 3 else (200, {}, OK))
        self.assertEqual(self.cliente(s).get("/calls")["status"], "success")
        self.assertEqual(len({x["cabeceras"]["X-Timestamp"] for x in s.peticiones}), 3)
        s = self.servidor(lambda n, p: (500, {}, {"status": "error", "code": "internal_error", "message": "Internal error"}))
        with self.assertRaises(MundoSMSError) as e:
            self.cliente(s).post("/sms", {"to": "1"})
        self.assertEqual([e.exception.http, e.exception.code, len(s.peticiones)], [500, "internal_error", 1])   # un POST con 5xx no se repite

    def test_409(self):
        # se decide por el code, no por el message: duplicated_request (cualquier método) y request_in_progress (un POST con su
        # clave) se repiten; otro 409, aunque su message diga lo mismo, no
        # (el POST con request_in_progress, con Retry-After de una hora: más de lo que se espera, así que no se repite; lo que
        # espera, en test_en_marcha)
        casos = [("GET", "duplicated_request", "Duplicated request", 3, "0"), ("PATCH", "duplicated_request", "Duplicated request", 3, "0"),
                 ("POST", "duplicated_request", "Duplicated request", 3, "0"), ("POST", "request_in_progress", "Request in progress", 1, "3600"),
                 ("PATCH", "request_in_progress", "Request in progress", 1, "0"), ("POST", "already_exists", "Request in progress", 1, "0")]
        for metodo, code, mensaje, n, ra in casos:
            s = self.servidor(lambda _n, _p: (409, {"Retry-After": ra}, {"status": "error", "code": code, "message": mensaje}))
            with self.assertRaises(MundoSMSError) as e:
                self.cliente(s, retries=2).request(metodo, "/x", {"a": 1})
            self.assertEqual([e.exception.http, e.exception.code, len(s.peticiones)], [409, code, n], (metodo, code))

    def test_en_marcha(self):
        # 2.1.0: un 409 request_in_progress se repite con la MISMA clave por tiempo (hasta 180 s), no por intentos: aquí, un corte
        # (gasta el único reintento de retries=1) y tres 409 (Retry-After: 0, que se espera como 1 s), y a la quinta, la respuesta
        en = (409, {"Retry-After": "0"}, {"status": "error", "code": "request_in_progress", "message": "Request in progress"})
        s = self.servidor(lambda n, p: "corta" if n == 1 else (en if n < 5 else (200, {}, OK)))
        t0 = time.time()
        self.assertEqual(self.cliente(s, retries=1).post("/whatsapp/messages", {"to": "1"})["status"], "success")
        claves = [x["cabeceras"]["Idempotency-Key"] for x in s.peticiones]
        self.assertEqual([len(claves), len(set(claves)), len({x["cabeceras"]["X-Timestamp"] for x in s.peticiones})], [5, 1, 5])
        self.assertTrue(3 <= time.time() - t0 < 8, time.time() - t0)
        # sin Retry-After: 1 y 2 s
        s = self.servidor(lambda n, p: (409, {}, en[2]) if n < 3 else (200, {}, OK))
        t0 = time.time()
        self.assertEqual(self.cliente(s, retries=1).post("/sms", {"to": "1"})["status"], "success")
        self.assertEqual(len(s.peticiones), 3)
        self.assertTrue(3 <= time.time() - t0 < 6, time.time() - t0)
        # Retry-After pide más de lo que queda de los 180 s: no espera; el error lleva la clave para repetirlo más tarde
        s = self.servidor(lambda n, p: (409, {"Retry-After": "3600"}, en[2]))
        t0 = time.time()
        with self.assertRaises(MundoSMSError) as e:
            self.cliente(s, retries=1).post("/sms", {"to": "1"}, idempotency_key="pedido-881")
        self.assertEqual([e.exception.http, e.exception.code, e.exception.idempotency_key, len(s.peticiones)],
                         [409, "request_in_progress", "pedido-881", 1])
        self.assertLess(time.time() - t0, 1)
        # con retries=0, ninguno; y la clave puesta por el SDK, en el error
        s = self.servidor(lambda n, p: en)
        with self.assertRaises(MundoSMSError) as e:
            self.cliente(s, retries=0).post("/sms", {"to": "1"})
        self.assertEqual([e.exception.idempotency_key, len(s.peticiones)], [s.peticiones[0]["cabeceras"]["Idempotency-Key"], 1])
        # el tope (aquí, 2,5 s en vez de 180): tras dos esperas de 1 s, la tercera no cabe
        import mundosms
        tope = mundosms.IN_PROGRESS_WAIT
        mundosms.IN_PROGRESS_WAIT = 2.5
        try:
            s = self.servidor(lambda n, p: en)
            with self.assertRaises(MundoSMSError) as e:
                self.cliente(s).post("/sms", {"to": "1"})
            self.assertEqual([e.exception.code, len(s.peticiones)], ["request_in_progress", 3])
        finally:
            mundosms.IN_PROGRESS_WAIT = tope

    def test_errores(self):
        r = [(404, {"X-Request-Id": "req-1"}, {"status": "error", "code": "not_found", "message": "Call not found", "detail": "no call 999"}),
             (200, {}, {"status": "error", "code": "whatsapp_rejected", "message": "WhatsApp rejected it", "data": {"id": "wam-1"}}),
             (502, {"Content-Type": "text/html"}, b"<html>Bad gateway</html>"),
             (202, {}, {"status": "success", "message": "Not confirmed", "data": {"id": "wam-2", "state": "unknown"}}),
             (400, {}, {"status": "error", "message": "Sin code"})]
        srv = self.servidor(lambda n, p: r[n - 1])
        api = self.cliente(srv, retries=0)
        with self.assertRaises(MundoSMSError) as e:
            api.get("/calls/out/999")
        x = e.exception
        self.assertEqual([x.http, x.code, x.message, x.detail, x.request_id, x.response["status"], x.idempotency_key],
                         [404, "not_found", "Call not found", "no call 999", "req-1", "error", None])   # un GET no lleva clave
        self.assertFalse(hasattr(x, "status_code"))
        with self.assertRaises(MundoSMSError) as e:
            api.post("/whatsapp/messages", {"to": "1"})
        self.assertEqual([e.exception.http, e.exception.code, e.exception.data], [200, "whatsapp_rejected", {"id": "wam-1"}])
        self.assertRegex(e.exception.idempotency_key or "", r"^[0-9a-f-]{36}$")   # 2.1.0: la Idempotency-Key que llevó el POST
        self.assertEqual(e.exception.idempotency_key, srv.peticiones[1]["cabeceras"]["Idempotency-Key"])
        with self.assertRaises(MundoSMSError) as e:
            api.get("/calls")
        self.assertEqual([e.exception.http, e.exception.code, e.exception.message, e.exception.response], [502, None, "HTTP 502", None])
        self.assertEqual(api.post("/whatsapp/messages", {"to": "1"})["data"]["state"], "unknown")   # el 202 no es un error
        with self.assertRaises(MundoSMSError) as e:
            api.get("/x")
        self.assertIsNone(e.exception.code)

    def test_plazo(self):
        s = self.servidor(lambda n, p: (200, {"X-Lento": "1"}, OK))
        t0 = time.time()
        with self.assertRaises(MundoSMSError) as e:
            self.cliente(s, timeout=0.3, retries=0).get("/calls")
        self.assertEqual([e.exception.http, e.exception.message], [0, "No response from the API: timed out after 0.3 s"])
        self.assertLess(time.time() - t0, 1.2)
        # 2.1.0: sin respuesta, el error de un POST lleva la clave con la que repetirlo (y pasa por pickle con ella)
        with self.assertRaises(MundoSMSError) as e:
            self.cliente(s, timeout=0.3, retries=0).post("/sms", {"to": "1"}, idempotency_key="pedido-882")
        import pickle
        x = pickle.loads(pickle.dumps(e.exception))
        self.assertEqual([e.exception.http, e.exception.idempotency_key, x.idempotency_key, x.message[:16]], [0, "pedido-882", "pedido-882", "No response from"])

    def test_paginas(self):
        pags = [{"results": 2, "calls": [{"id": 1}, {"id": 2}], "has_more": True, "next_cursor": "a.2"},
                {"results": 1, "calls": [{"id": 3}], "has_more": False, "next_cursor": "a.3"}]
        s = self.servidor(lambda n, p: (200, {}, {"status": "success", "data": pags[n - 1]}))
        api = self.cliente(s)
        self.assertEqual([x["id"] for x in api.all("/calls", {"order": "asc"})], [1, 2, 3])
        self.assertEqual([x["ruta"] for x in s.peticiones], ["/v3/calls?order=asc", "/v3/calls?order=asc&cursor=a.2"])
        self.assertEqual(api.last_cursor, "a.3")

    def test_redireccion(self):
        s = self.servidor(lambda n, p: (302 if p["metodo"] == "GET" else 307, {"Location": "/robado"}, OK) if not p["ruta"].startswith("/robado") else (200, {}, OK))
        api = self.cliente(s, retries=2)
        for f, codigo in ((lambda: api.get("/calls"), 302), (lambda: api.post("/sms", {"to": "1", "text": "secreto"}), 307)):
            with self.assertRaises(MundoSMSError) as e:
                f()
            self.assertEqual([e.exception.http, e.exception.response], [codigo, None])
            self.assertIn("redirect to /robado not followed", e.exception.message)
        self.assertEqual([x["ruta"] for x in s.peticiones], ["/v3/calls", "/v3/sms"])   # al destino no le llega nada

    def test_2xx_que_no_es_json(self):
        r = [(200, {"Content-Type": "text/html"}, b"<html>Portal</html>"), (200, {"Content-Type": "text/html"}, b"<html>Portal</html>"),
             (200, {"Content-Type": "application/json"}, b"[1, 2]"), (200, {"Content-Type": "audio/mpeg"}, b"\xff\xfb"),
             (200, {"Content-Type": "application/pdf"}, b"%PDF"), (204, {}, b"")]
        s = self.servidor(lambda n, p: r[n - 1])
        api = self.cliente(s, retries=0)
        for f, tipo in ((lambda: api.post("/sms", {"to": "1"}), "text/html"), (lambda: api.get("/calls"), "text/html"),
                        (lambda: api.get("/calls"), "application/json")):
            with self.assertRaises(MundoSMSError) as e:
                f()
            self.assertEqual([e.exception.http, e.exception.message], [200, "HTTP 200: the response is not JSON (%s)" % tipo])
        self.assertEqual(api.get("/rec")["body"], b"\xff\xfb")
        self.assertEqual(api.get("/cert")["content_type"], "application/pdf")
        self.assertEqual(api.delete("/x/1")["body"], b"")

    def test_lista_sin_cursor(self):
        s = self.servidor(lambda n, p: (200, {}, {"status": "success", "data": {"results": 2, "calls": [{"id": 1}, {"id": 2}], "has_more": True}}))
        vistos = []
        with self.assertRaises(MundoSMSError) as e:
            for x in self.cliente(s).all("/calls/missed"):
                vistos.append(x)
        self.assertIn("not paginated", e.exception.message)
        self.assertEqual([vistos, len(s.peticiones)], [[], 1])

    def test_mas_en_la_ruta(self):
        s = self.servidor(lambda n, p: (200, {}, OK))
        self.cliente(s).get("/calls?from_datetime=2026-11-20T10:15:03+01:00&a=%20")
        self.assertEqual(s.peticiones[0]["ruta"], "/v3/calls?from_datetime=2026-11-20T10%3A15%3A03%2B01%3A00&a=%20")
        self.assertTrue(s.peticiones[0]["firma"])

    def test_delete_repetido(self):
        no = {"status": "error", "code": "not_found", "message": "Speed dial not found"}
        s = self.servidor(lambda n, p: "corta" if n == 1 else (404, {}, no))
        r = self.cliente(s, retries=1).delete("/speed-dials/9121")
        self.assertEqual(r, {"status": "success", "message": "Already deleted", "data": {"state": "deleted", "changed": False}})
        self.assertEqual(len(s.peticiones), 2)
        ruta = {"status": "error", "code": "route_not_found", "message": "Route not found"}   # tras el corte, una ruta que no existe es un error
        s = self.servidor(lambda n, p: "corta" if n == 1 else (404, {}, ruta))
        with self.assertRaises(MundoSMSError) as e:
            self.cliente(s, retries=1).delete("/speed-dials/9121")
        self.assertEqual([e.exception.http, e.exception.code], [404, "route_not_found"])
        s = self.servidor(lambda n, p: (404, {}, no))   # sin fallo de red antes, un 404 es un error
        with self.assertRaises(MundoSMSError) as e:
            self.cliente(s, retries=1).delete("/speed-dials/9121")
        self.assertEqual(e.exception.http, 404)

    def test_last_cursor(self):
        def responde(n, p):
            if p["ruta"].startswith("/v3/vacia"):
                return 200, {}, {"status": "success", "data": {"results": 0, "calls": [], "has_more": False, "next_cursor": None}}
            if p["ruta"].startswith("/v3/una"):
                return 200, {}, {"status": "success", "data": {"results": 1, "calls": [{"id": 1}], "has_more": False, "next_cursor": "u1"}}
            return 400, {}, {"status": "error", "code": "invalid_parameter", "message": "Parameter cursor not valid"}
        api = self.cliente(self.servidor(responde), retries=0)
        list(api.all("/vacia", {"cursor": "c9"}))
        self.assertEqual(api.last_cursor, "c9")
        list(api.all("/una"))
        self.assertEqual(api.last_cursor, "u1")
        with self.assertRaises(MundoSMSError):
            list(api.all("/error"))
        self.assertIsNone(api.last_cursor)

    def test_cupo(self):
        s = self.servidor(lambda n, p: (200, {"X-RateLimit-Limit": "10", "X-RateLimit-Remaining": "7", "X-RateLimit-Reset": "1"} if n == 1 else {}, OK))
        api = self.cliente(s)
        api.get("/a")
        self.assertEqual(api.last_rate_limit, {"limit": 10, "remaining": 7, "reset": 1})
        api.get("/b")
        self.assertIsNone(api.last_rate_limit)

    def test_opciones(self):
        for opc in ({"retries": -1}, {"timeout": 0}, {"base_url": "ftp://x/v3"}):
            with self.assertRaises(ValueError):
                MundoSMS(TOKEN, KEY, **opc)
        with self.assertRaises(TypeError):
            MundoSMS(TOKEN, KEY, base="x")   # una opción que no existe
        self.assertNotIn(TOKEN, repr(MundoSMS(TOKEN, KEY)))


if __name__ == "__main__":
    unittest.main()
