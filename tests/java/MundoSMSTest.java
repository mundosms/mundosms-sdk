// Pruebas del cliente Java (MundoSMS.java), sin red: un servidor de prueba local (ServerSocket) que comprueba la firma como la API
// y contesta lo que pide cada caso. Java 8 o superior, sin dependencias. El código de salida es el número de fallos.
//   javac -encoding UTF-8 -d /tmp/c java/MundoSMS.java tests/java/MundoSMSTest.java && java -cp /tmp/c MundoSMSTest
import es.mundosms.MundoSMS;
import es.mundosms.MundoSMS.MundoSMSError;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Base64;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.function.BiFunction;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

public class MundoSMSTest {
    static final String TOKEN = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiIsInN1YiI6IjcifQ.cHJ1ZWJh";
    static final String KEY = "Pr4ebaClaveDe20Letra";
    static final String OK = "{\"status\":\"success\",\"message\":\"OK\",\"data\":{\"id\":8812345}}";
    static int fallos = 0;

    static void ok(boolean c, String que) {
        if (!c) fallos++;
        System.out.println((c ? "  ok    " : "  FALLA ") + que);
    }

    /** La firma de «La firma» de la documentación: base64url, sin relleno, del HMAC-SHA256. */
    static String firma(String clave, String token, String ts, String metodo, String ruta, byte[] cuerpo) throws Exception {
        Mac m = Mac.getInstance("HmacSHA256");
        m.init(new SecretKeySpec(clave.getBytes(StandardCharsets.UTF_8), "HmacSHA256"));
        m.update((token + "." + ts + "." + metodo + " " + ruta + ".").getBytes(StandardCharsets.UTF_8));
        return Base64.getUrlEncoder().withoutPadding().encodeToString(m.doFinal(cuerpo));
    }

    /** Lo que llegó al servidor de prueba. */
    static final class Peticion {
        String metodo;
        String ruta;
        Map<String, String> cabeceras = new LinkedHashMap<String, String>();
        byte[] cuerpo;
        boolean firma;
    }

    /** Una respuesta del servidor de prueba; null es «corta sin contestar». */
    static final class Respuesta {
        final int estado;
        final String tipo;
        final byte[] cuerpo;
        final Map<String, String> cabeceras = new LinkedHashMap<String, String>();
        boolean lento;

        Respuesta(int estado, String tipo, String cuerpo) {
            this.estado = estado;
            this.tipo = tipo;
            this.cuerpo = cuerpo.getBytes(StandardCharsets.UTF_8);
        }

        Respuesta con(String k, String v) {
            cabeceras.put(k, v);
            return this;
        }
    }

    static Respuesta json(int estado, String cuerpo) {
        return new Respuesta(estado, "application/json", cuerpo);
    }

    /**
     * Apunta cada petición (con su firma comprobada) y contesta con responde(n, petición). Una petición firmada que llega
     * dos veces (el mismo X-Timestamp en la misma ruta: la repite el propio HttpURLConnection) da 409 duplicated_request,
     * como la API con «Rechazar peticiones repetidas».
     */
    static final class Servidor implements AutoCloseable {
        final List<Peticion> peticiones = Collections.synchronizedList(new ArrayList<Peticion>());
        final Set<String> vistas = Collections.synchronizedSet(new HashSet<String>());
        final ServerSocket srv;
        final String base;

        Servidor(final BiFunction<Integer, Peticion, Respuesta> responde) throws Exception {
            srv = new ServerSocket(0, 50, InetAddress.getByName("127.0.0.1"));
            base = "http://127.0.0.1:" + srv.getLocalPort() + "/v3";
            Thread t = new Thread(() -> {
                while (true) {
                    final Socket c;
                    try {
                        c = srv.accept();
                    } catch (Exception e) {
                        return;
                    }
                    new Thread(() -> atiende(c, responde)).start();
                }
            });
            t.setDaemon(true);
            t.start();
        }

        void atiende(Socket c, BiFunction<Integer, Peticion, Respuesta> responde) {
            try {
                InputStream in = c.getInputStream();
                Peticion p = new Peticion();
                String[] linea = leeLinea(in).split(" ");
                p.metodo = linea[0];
                p.ruta = linea[1];
                for (String h; !(h = leeLinea(in)).isEmpty(); ) {
                    int i = h.indexOf(':');
                    p.cabeceras.put(h.substring(0, i).trim().toLowerCase(Locale.ROOT), h.substring(i + 1).trim());
                }
                int n = Integer.parseInt(p.cabeceras.getOrDefault("content-length", "0"));
                p.cuerpo = new byte[n];
                for (int leido = 0; leido < n; ) leido += in.read(p.cuerpo, leido, n - leido);
                String metodo = p.cabeceras.getOrDefault("x-http-method-override", p.metodo);
                String auth = p.cabeceras.getOrDefault("authorization", "");
                p.firma = firma(KEY, auth.length() > 7 ? auth.substring(7) : "", p.cabeceras.get("x-timestamp"), metodo, p.ruta, p.cuerpo)
                          .equals(p.cabeceras.get("x-signature"));
                peticiones.add(p);
                Respuesta r = vistas.add(p.ruta + " " + p.cabeceras.get("x-timestamp")) ? responde.apply(peticiones.size(), p)
                            : json(409, "{\"status\":\"error\",\"code\":\"duplicated_request\",\"message\":\"Duplicated request\"}");
                if (r == null) {
                    c.close();
                    return;
                }
                OutputStream out = c.getOutputStream();
                StringBuilder sb = new StringBuilder("HTTP/1.1 " + r.estado + " X\r\n");
                if (r.tipo != null) sb.append("Content-Type: ").append(r.tipo).append("\r\n");
                for (Map.Entry<String, String> e : r.cabeceras.entrySet()) sb.append(e.getKey()).append(": ").append(e.getValue()).append("\r\n");
                sb.append("Content-Length: ").append(r.cuerpo.length).append("\r\nConnection: close\r\n\r\n");
                out.write(sb.toString().getBytes(StandardCharsets.UTF_8));
                out.flush();
                if (r.lento) Thread.sleep(1500);
                out.write(r.cuerpo);
                out.flush();
                c.close();
            } catch (Exception e) {
                try {
                    c.close();
                } catch (Exception ignored) {
                    // ya cerrada
                }
            }
        }

        static String leeLinea(InputStream in) throws Exception {
            ByteArrayOutputStream b = new ByteArrayOutputStream();
            for (int x; (x = in.read()) != -1 && x != '\n'; ) if (x != '\r') b.write(x);
            return new String(b.toByteArray(), StandardCharsets.UTF_8);
        }

        List<String> rutas() {
            List<String> l = new ArrayList<String>();
            synchronized (peticiones) {
                for (Peticion p : peticiones) l.add(p.ruta);
            }
            return l;
        }

        @Override
        public void close() throws java.io.IOException {
            srv.close();
        }
    }

    static MundoSMS cliente(Servidor s, int reintentos, double plazo) {
        return new MundoSMS(TOKEN, KEY, new MundoSMS.Options().baseUrl(s.base).retries(reintentos).timeout(plazo));
    }

    interface Llamada {
        void hace() throws Exception;
    }

    /** El MundoSMSError de f, o null si no salta. */
    static MundoSMSError error(Llamada f) throws Exception {
        try {
            f.hace();
        } catch (MundoSMSError e) {
            return e;
        }
        return null;
    }

    interface Pide {
        Map<String, Object> hace() throws Exception;
    }

    /** La respuesta de f o, si salta un MundoSMSError, {"status": "error …"}: el caso cuenta como fallo y las pruebas siguen. */
    static Map<String, Object> enMarcha(Pide f) throws Exception {
        try {
            return f.hace();
        } catch (MundoSMSError e) {
            Map<String, Object> r = new LinkedHashMap<String, Object>();
            r.put("status", "error " + e.getHttp() + " " + e.getCode());
            return r;
        }
    }

    static List<Object> ids(Iterable<Map<String, Object>> l) {
        List<Object> r = new ArrayList<Object>();
        for (Map<String, Object> x : l) r.add(x.get("id"));
        return r;
    }

    @SuppressWarnings("unchecked")
    static Object en(Object o, String... camino) {
        for (String c : camino) o = o instanceof Map ? ((Map<String, Object>) o).get(c) : null;
        return o;
    }

    public static void main(String[] a) throws Exception {
        System.out.println("== la firma: los ejemplos de la documentación y lo que manda el SDK");
        String tk = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiJ9.ZmlybWE";
        ok(firma("A1b2C3d4E5f6G7h8I9j0", tk, "1795400000123", "GET", "/v3/calls?type=in&limit=5", new byte[0])
           .equals("uE09uuJV7e3hSSW2NZIJQ9jMWlT15lMETj-sbrIjoFo"), "el ejemplo GET de «Para comprobar tu código»");
        ok(firma("A1b2C3d4E5f6G7h8I9j0", tk, "1795400000123", "POST", "/v3/sms", "{\"to\":\"34600123456\",\"text\":\"Hola\"}".getBytes(StandardCharsets.UTF_8))
           .equals("_62bUnSBUjpWSsAMT580JFfk1Yhx9bCZhS805swJ2to"), "el ejemplo POST");
        try (Servidor s = new Servidor((n, p) -> json(200, OK))) {
            MundoSMS api = cliente(s, 3, 60);
            api.get("/calls", MundoSMS.map("type", "in", "limit", 5));
            api.post("/sms", MundoSMS.map("to", "34600123456", "text", "Señor Núñez: 9,50 €"));
            api.patch("/extensions/recepción", MundoSMS.map("record_calls", true));
            Peticion g = s.peticiones.get(0), p = s.peticiones.get(1), x = s.peticiones.get(2);
            ok(g.ruta.equals("/v3/calls?type=in&limit=5") && g.cuerpo.length == 0 && p.ruta.equals("/v3/sms"), "la ruta y la query, tal cual se firman");
            ok(new String(p.cuerpo, StandardCharsets.UTF_8).equals("{\"to\":\"34600123456\",\"text\":\"Señor Núñez: 9,50 €\"}"), "el cuerpo, en UTF-8 sin escapar");
            ok(g.firma && p.firma && x.firma && g.cabeceras.get("x-timestamp").matches("[0-9]{13}"), "firmadas, con X-Timestamp en milisegundos");
            ok(x.metodo.equals("POST") && "PATCH".equals(x.cabeceras.get("x-http-method-override")), "PATCH como POST con X-HTTP-Method-Override, firmado como PATCH");
            ok(p.cabeceras.getOrDefault("idempotency-key", "").matches("[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}")
               && !g.cabeceras.containsKey("idempotency-key"), "Idempotency-Key en el POST y no en el GET");
        }

        System.out.println("== un aviso: el ejemplo de whsec_ejemplo");
        String cuerpo = "{\"id\":\"evt_prueba\",\"type\":\"sms.status\",\"data\":{\"to\":\"34600123456\",\"state\":\"delivered\"}}";
        String cab = "t=1795161606,v1=d43d40dfd88dd4aba4633580b2786f8651936516aecc2967868edf333ddfdae5";
        ok(MundoSMS.verifyWebhook("whsec_ejemplo", cab, cuerpo.getBytes(StandardCharsets.UTF_8), Long.MAX_VALUE / 2), "con su secreto, vale");
        ok(!MundoSMS.verifyWebhook("whsec_otro", cab, cuerpo, Long.MAX_VALUE / 2), "con otro secreto, no");
        ok(!MundoSMS.verifyWebhook("whsec_ejemplo", cab, cuerpo.replace("delivered", "Delivered"), Long.MAX_VALUE / 2), "con un byte cambiado, no");
        ok(!MundoSMS.verifyWebhook("whsec_ejemplo", cab, cuerpo), "de hace más de 5 minutos, no");

        System.out.println("== reintentos y plazo");
        try (Servidor s = new Servidor((n, p) -> n < 3 ? json(429, "{\"status\":\"error\",\"code\":\"requests_per_second\",\"message\":\"Too many requests\"}").con("Retry-After", "0") : json(200, OK))) {
            ok("success".equals(cliente(s, 3, 60).get("/calls").get("status")) && s.peticiones.size() == 3, "un 429 se repite");
        }
        try (Servidor s = new Servidor((n, p) -> json(500, "{\"status\":\"error\",\"code\":\"internal_error\",\"message\":\"Internal error\"}"))) {
            MundoSMSError e = error(() -> cliente(s, 3, 60).post("/sms", MundoSMS.map("to", "1")));
            ok(e != null && e.getHttp() == 500 && "internal_error".equals(e.getCode()) && s.peticiones.size() == 1, "un POST con 5xx no se repite");
        }
        // un 409 se decide por el code, no por el message: duplicated_request (cualquier método) y request_in_progress (un POST con
        // su clave) se repiten; otro 409, aunque su message diga lo mismo, no (el POST con request_in_progress, con Retry-After de
        // una hora: más de lo que se espera, así que no se repite; lo que espera, en «409 request_in_progress»)
        String[][] casos409 = {{"GET", "duplicated_request", "Duplicated request", "3", "0"}, {"PATCH", "duplicated_request", "Duplicated request", "3", "0"},
                               {"POST", "duplicated_request", "Duplicated request", "3", "0"}, {"POST", "request_in_progress", "Request in progress", "1", "3600"},
                               {"PATCH", "request_in_progress", "Request in progress", "1", "0"}, {"POST", "already_exists", "Request in progress", "1", "0"}};
        for (String[] c : casos409) {
            String b = "{\"status\":\"error\",\"code\":\"" + c[1] + "\",\"message\":\"" + c[2] + "\"}";
            try (Servidor s = new Servidor((n, p) -> json(409, b).con("Retry-After", c[4]))) {
                MundoSMSError e = error(() -> cliente(s, 2, 60).request(c[0], "/x", MundoSMS.map("a", 1)));
                ok(e != null && e.getHttp() == 409 && c[1].equals(e.getCode()) && s.peticiones.size() == Integer.parseInt(c[3]),
                   "409 " + c[1] + " en " + c[0] + ": " + c[3] + " petición(es)");
            }
        }

        System.out.println("== 409 request_in_progress: se espera por tiempo, con la misma clave (2.1.0)");
        // un corte (gasta el único reintento de retries 1) y tres 409 con Retry-After: 0 (se espera 1 s), y a la quinta, la respuesta
        String enMarcha = "{\"status\":\"error\",\"code\":\"request_in_progress\",\"message\":\"Request in progress\"}";
        try (Servidor s = new Servidor((n, p) -> n == 1 ? null : n < 5 ? json(409, enMarcha).con("Retry-After", "0") : json(200, OK))) {
            long t0 = System.currentTimeMillis();
            Map<String, Object> r = enMarcha(() -> cliente(s, 1, 60).post("/whatsapp/messages", MundoSMS.map("to", "1")));
            long t = System.currentTimeMillis() - t0;
            Set<String> claves = new HashSet<String>();
            Set<String> tss = new HashSet<String>();
            synchronized (s.peticiones) {
                for (Peticion p : s.peticiones) {
                    claves.add(p.cabeceras.get("idempotency-key"));
                    tss.add(p.cabeceras.get("x-timestamp"));
                }
            }
            ok("success".equals(r.get("status")) && s.peticiones.size() == 5 && claves.size() == 1 && !claves.contains(null) && tss.size() == 5
               && t >= 3000 && t < 8000, "un corte y tres 409 con retries 1: 5 peticiones con la misma clave y la respuesta (" + t + " ms)");
        }
        try (Servidor s = new Servidor((n, p) -> n < 3 ? json(409, enMarcha) : json(200, OK))) {
            long t0 = System.currentTimeMillis();
            Map<String, Object> r = enMarcha(() -> cliente(s, 1, 60).post("/sms", MundoSMS.map("to", "1")));
            long t = System.currentTimeMillis() - t0;
            ok("success".equals(r.get("status")) && s.peticiones.size() == 3 && t >= 3000 && t < 6000, "sin Retry-After: 1 y 2 s (" + t + " ms)");
        }
        try (Servidor s = new Servidor((n, p) -> json(409, enMarcha).con("Retry-After", "3600"))) {
            long t0 = System.currentTimeMillis();
            MundoSMSError e = error(() -> cliente(s, 1, 60).post("/sms", MundoSMS.map("to", "1"), new MundoSMS.RequestOptions().idempotencyKey("pedido-881")));
            long t = System.currentTimeMillis() - t0;
            ok(e != null && e.getHttp() == 409 && "request_in_progress".equals(e.getCode()) && "pedido-881".equals(e.getIdempotencyKey())
               && s.peticiones.size() == 1 && t < 1000, "Retry-After pide más de lo que queda: no espera, y el error lleva la clave (" + t + " ms)");
        }
        try (Servidor s = new Servidor((n, p) -> json(409, enMarcha).con("Retry-After", "0"))) {
            MundoSMSError e = error(() -> cliente(s, 0, 60).post("/sms", MundoSMS.map("to", "1")));
            ok(e != null && s.peticiones.size() == 1 && e.getIdempotencyKey() != null && e.getIdempotencyKey().equals(s.peticiones.get(0).cabeceras.get("idempotency-key")),
               "con retries 0, ninguno; y la clave puesta por el SDK, en el error");
        }

        System.out.println("== los campos del error");
        Respuesta[] errs = {json(404, "{\"status\":\"error\",\"code\":\"not_found\",\"message\":\"Call not found\",\"detail\":\"no call 999\"}").con("X-Request-Id", "req-1"),
                            json(200, "{\"status\":\"error\",\"code\":\"whatsapp_rejected\",\"message\":\"WhatsApp rejected it\",\"data\":{\"id\":\"wam-1\"}}"),
                            new Respuesta(502, "text/html", "<html>Bad gateway</html>"),
                            json(202, "{\"status\":\"success\",\"message\":\"Not confirmed\",\"data\":{\"id\":\"wam-2\",\"state\":\"unknown\"}}")};
        try (Servidor s = new Servidor((n, p) -> errs[n - 1])) {
            MundoSMS api = cliente(s, 0, 60);
            MundoSMSError e = error(() -> api.get("/calls/out/999"));
            ok(e != null && e.getHttp() == 404 && "not_found".equals(e.getCode()) && "Call not found".equals(e.getMessage()) && "no call 999".equals(e.getDetail())
               && "req-1".equals(e.getRequestId()) && "error".equals(e.getResponse().get("status")), "un 404: http, code, message, detail, X-Request-Id y la respuesta");
            ok(e != null && e.getIdempotencyKey() == null, "un GET no lleva Idempotency-Key: null en el error");
            ok(e != null && e.toString().endsWith("Call not found (HTTP 404, code not_found, detail: no call 999, X-Request-Id req-1)"), "toString con el code (" + e + ")");
            MundoSMSError e2 = error(() -> api.post("/whatsapp/messages", MundoSMS.map("to", "1")));
            ok(e2 != null && e2.getHttp() == 200 && "whatsapp_rejected".equals(e2.getCode()) && "wam-1".equals(en(e2.getData(), "id")), "un 200 con status error es un error, con su code");
            ok(e2 != null && e2.getIdempotencyKey() != null && e2.getIdempotencyKey().matches("[0-9a-f-]{36}")
               && e2.getIdempotencyKey().equals(s.peticiones.get(1).cabeceras.get("idempotency-key")), "el error de un POST lleva la Idempotency-Key que usó");
            MundoSMSError e3 = error(() -> api.get("/calls"));
            ok(e3 != null && e3.getHttp() == 502 && e3.getCode() == null && "HTTP 502".equals(e3.getMessage()) && e3.getResponse() == null, "un 502 sin JSON: sin code");
            ok("unknown".equals(en(api.post("/whatsapp/messages", MundoSMS.map("to", "1")), "data", "state")), "el 202 de un WhatsApp sin confirmar no es un error");
        }
        try (Servidor s = new Servidor((n, p) -> {
            Respuesta r = json(200, OK);
            r.lento = true;
            return r;
        })) {
            long t0 = System.currentTimeMillis();
            MundoSMSError e = error(() -> cliente(s, 0, 0.3).get("/calls"));
            ok(e != null && e.getHttp() == 0 && "No response from the API: timed out after 0.3 s".equals(e.getMessage())
               && System.currentTimeMillis() - t0 < 1200, "el plazo es de la petición entera");
            MundoSMSError ep = error(() -> cliente(s, 0, 0.3).post("/sms", MundoSMS.map("to", "1"), new MundoSMS.RequestOptions().idempotencyKey("pedido-882")));
            ok(ep != null && ep.getHttp() == 0 && "pedido-882".equals(ep.getIdempotencyKey()), "sin respuesta, el error de un POST lleva la clave con la que repetirlo");
        }

        System.out.println("== all recorre las páginas con su cursor");
        String[] pags = {"{\"status\":\"success\",\"data\":{\"results\":2,\"calls\":[{\"id\":1},{\"id\":2}],\"has_more\":true,\"next_cursor\":\"a.2\"}}",
                         "{\"status\":\"success\",\"data\":{\"results\":1,\"calls\":[{\"id\":3}],\"has_more\":false,\"next_cursor\":\"a.3\"}}"};
        try (Servidor s = new Servidor((n, p) -> json(200, pags[n - 1]))) {
            MundoSMS api = cliente(s, 3, 60);
            ok(ids(api.all("/calls", MundoSMS.map("order", "asc"))).equals(Arrays.asList(1L, 2L, 3L))
               && s.rutas().equals(Arrays.asList("/v3/calls?order=asc", "/v3/calls?order=asc&cursor=a.2")) && "a.3".equals(api.getLastCursor()),
               "las dos páginas, y el último cursor");
        }

        System.out.println("== una redirección no se sigue");
        try (Servidor s = new Servidor((n, p) -> p.ruta.startsWith("/robado") ? json(200, OK)
                                                  : json(p.metodo.equals("GET") ? 302 : 307, OK).con("Location", "/robado"))) {
            MundoSMS api = cliente(s, 2, 60);
            MundoSMSError e1 = error(() -> api.get("/calls"));
            MundoSMSError e2 = error(() -> api.post("/sms", MundoSMS.map("to", "1", "text", "secreto")));
            ok(e1 != null && e1.getHttp() == 302 && e1.getResponse() == null && e1.getMessage().contains("redirect to /robado not followed"), "302: un error con su Location");
            ok(e2 != null && e2.getHttp() == 307 && e2.getMessage().contains("redirect to /robado not followed"), "307: un error con su Location");
            ok(s.rutas().equals(Arrays.asList("/v3/calls", "/v3/sms")), "al destino no le llega nada");
        }

        System.out.println("== un 2xx que no es JSON es un error; un fichero, no");
        Respuesta[] rs = {new Respuesta(200, "text/html", "<html>Portal</html>"), new Respuesta(200, "text/html", "<html>Portal</html>"),
                          new Respuesta(200, "audio/mpeg", "MP3"), new Respuesta(200, "application/pdf", "%PDF"), new Respuesta(204, null, "")};
        try (Servidor s = new Servidor((n, p) -> rs[n - 1])) {
            MundoSMS api = cliente(s, 0, 60);
            MundoSMSError e1 = error(() -> api.post("/sms", MundoSMS.map("to", "1")));
            MundoSMSError e2 = error(() -> api.get("/calls"));
            ok(e1 != null && e2 != null && e1.getHttp() == 200 && "HTTP 200: the response is not JSON (text/html)".equals(e1.getMessage()) && e2.getHttp() == 200,
               "el HTML de un portal cautivo, en POST y en GET");
            ok(((byte[]) api.get("/rec").get("body")).length == 3 && "application/pdf".equals(api.get("/cert").get("content_type"))
               && ((byte[]) api.delete("/x/1").get("body")).length == 0, "un audio, un PDF y un 204, como siempre");
        }

        System.out.println("== all de una lista que no va por páginas es un error");
        try (Servidor s = new Servidor((n, p) -> json(200, "{\"status\":\"success\",\"data\":{\"results\":2,\"calls\":[{\"id\":1},{\"id\":2}],\"has_more\":true}}"))) {
            List<Object> vistos = new ArrayList<Object>();
            MundoSMSError e = error(() -> {
                for (Map<String, Object> x : cliente(s, 3, 60).all("/calls/missed")) vistos.add(x);
            });
            ok(e != null && e.getMessage().contains("not paginated") && vistos.isEmpty() && s.peticiones.size() == 1, "un error, antes de dar nada");
        }

        System.out.println("== un «+» en la query de la ruta llega como «+»");
        try (Servidor s = new Servidor((n, p) -> json(200, OK))) {
            cliente(s, 3, 60).get("/calls?from_datetime=2026-11-20T10:15:03+01:00&a=%20");
            Peticion p = s.peticiones.get(0);
            ok(p.ruta.equals("/v3/calls?from_datetime=2026-11-20T10%3A15%3A03%2B01%3A00&a=%20") && p.firma, "%2B, y la firma casa");
        }

        System.out.println("== un DELETE que se hizo y perdió la respuesta");
        String no = "{\"status\":\"error\",\"code\":\"not_found\",\"message\":\"Speed dial not found\"}";
        try (Servidor s = new Servidor((n, p) -> n == 1 ? null : json(404, no))) {
            final List<Map<String, Object>> r = new ArrayList<Map<String, Object>>();
            error(() -> r.add(cliente(s, 1, 60).delete("/speed-dials/9121")));
            ok(r.size() == 1 && "Already deleted".equals(r.get(0).get("message")) && Boolean.FALSE.equals(en(r.get(0), "data", "changed"))
               && r.get(0).keySet().equals(new HashSet<String>(Arrays.asList("status", "message", "data"))), "el 404 del reintento: ya estaba borrado (sin code ni status_code)");
        }
        String ruta = "{\"status\":\"error\",\"code\":\"route_not_found\",\"message\":\"Route not found\"}";
        try (Servidor s = new Servidor((n, p) -> n == 1 ? null : json(404, ruta))) {
            MundoSMSError e = error(() -> cliente(s, 1, 60).delete("/speed-dials/9121"));
            ok(e != null && e.getHttp() == 404 && "route_not_found".equals(e.getCode()), "tras el corte, route_not_found sigue siendo un error");
        }
        try (Servidor s = new Servidor((n, p) -> json(404, no))) {
            MundoSMSError e = error(() -> cliente(s, 1, 60).delete("/speed-dials/9121"));
            ok(e != null && e.getHttp() == 404, "sin fallo de red antes, un 404 es un error");
        }

        System.out.println("== getLastCursor es el del recorrido; getLastRateLimit, el cupo de la última respuesta");
        try (Servidor s = new Servidor((n, p) -> p.ruta.startsWith("/v3/vacia") ? json(200, "{\"status\":\"success\",\"data\":{\"results\":0,\"calls\":[],\"has_more\":false,\"next_cursor\":null}}")
                                                  : p.ruta.startsWith("/v3/una") ? json(200, "{\"status\":\"success\",\"data\":{\"results\":1,\"calls\":[{\"id\":1}],\"has_more\":false,\"next_cursor\":\"u1\"}}")
                                                  : json(400, "{\"status\":\"error\",\"code\":\"invalid_parameter\",\"message\":\"Parameter cursor not valid\"}"))) {
            MundoSMS api = cliente(s, 0, 60);
            ids(api.all("/vacia", MundoSMS.map("cursor", "c9")));
            String c1 = api.getLastCursor();
            ids(api.all("/una"));
            String c2 = api.getLastCursor();
            error(() -> ids(api.all("/error")));
            ok("c9".equals(c1) && "u1".equals(c2) && api.getLastCursor() == null, "c9 al reanudar con una página vacía, y nada si falla al empezar");
        }
        try (Servidor s = new Servidor((n, p) -> n == 1 ? json(200, OK).con("X-RateLimit-Limit", "10").con("X-RateLimit-Remaining", "7").con("X-RateLimit-Reset", "1") : json(200, OK))) {
            MundoSMS api = cliente(s, 3, 60);
            api.get("/a");
            MundoSMS.RateLimit c1 = api.getLastRateLimit();
            api.get("/b");
            ok(c1 != null && c1.getLimit() == 10 && c1.getRemaining() == 7 && c1.getReset() == 1 && api.getLastRateLimit() == null, "10/7/1, y nada sin las cabeceras");
        }

        System.out.println("== las opciones");
        int malas = 0;
        for (Llamada f : Arrays.<Llamada>asList(() -> new MundoSMS.Options().retries(-1), () -> new MundoSMS.Options().timeout(0),
                                                 () -> new MundoSMS(TOKEN, KEY, new MundoSMS.Options().baseUrl("ftp://x/v3")))) {
            try {
                f.hace();
            } catch (IllegalArgumentException e) {
                malas++;
            }
        }
        ok(malas == 3, "un valor que no vale da IllegalArgumentException");
        ok(!new MundoSMS(TOKEN, KEY).toString().contains(TOKEN), "toString no enseña el token");
        System.out.println(fallos == 0 ? "== todo bien" : "== " + fallos + " fallo(s)");
        System.exit(Math.min(fallos, 99));
    }
}
