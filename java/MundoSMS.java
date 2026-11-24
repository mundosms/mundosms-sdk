/*
 * MundoSMS API v3 — cliente Java en un solo fichero (Java 8 o superior). Sin dependencias.
 * Documentación: https://www.mundosms.es/api-v3
 * Código y versiones: https://github.com/mundosms/mundosms-sdk (licencia MIT)
 */
package es.mundosms;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.lang.reflect.Array;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.net.HttpURLConnection;
import java.net.SocketTimeoutException;
import java.net.URI;
import java.net.URISyntaxException;
import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.time.ZoneOffset;
import java.time.ZonedDateTime;
import java.time.format.DateTimeFormatter;
import java.time.format.DateTimeParseException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Base64;
import java.util.Collection;
import java.util.Collections;
import java.util.HashSet;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.NoSuchElementException;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.Callable;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

/**
 * Cliente de la API REST v3 de MundoSMS: firma cada petición (X-Timestamp en milisegundos y la firma HMAC de tu API Key) y
 * reintenta solo lo que es seguro repetir.
 *
 * <pre>{@code
 * MundoSMS api = new MundoSMS("TOKEN", "CLAVE_PRIVADA");   // la clave privada, "" si tu API Key no exige firma
 * Map<String, Object> r = api.post("/sms", MundoSMS.map("to", "34600123456", "text", "Hola", "from", "MiEmpresa"));
 * System.out.println(((Map<?, ?>) r.get("data")).get("id"));
 * for (Map<String, Object> call : api.all("/calls", MundoSMS.map("from_datetime", "2026-11-01"))) { ... }
 * }</pre>
 *
 * <p>Las respuestas llegan como el JSON entero, decodificado: los objetos como {@code Map<String, Object>} (en el orden en
 * que llegan), las listas como {@code List<Object>}, los textos como {@code String}, los números enteros como {@code Long}
 * (o {@code BigInteger} si no caben), los decimales como {@code Double}, y {@code Boolean} y {@code null}.
 *
 * <p>Errores: excepción {@link MundoSMSError} (no comprobada) con {@code getHttp()} (código HTTP), {@code getStatusCode()}
 * ("3", "4"…), {@code getMessage()}, {@code getDetail()}, {@code getData()}, {@code getResponse()} (la respuesta entera) y
 * {@code getRequestId()} (la cabecera X-Request-Id). También cuando la API contesta con "status": "error", venga con el código HTTP que venga. Un WhatsApp sin
 * confirmar (202) no es un error: vuelve la respuesta, con data.state "unknown".
 *
 * <p>Cada POST lleva una Idempotency-Key (un UUID nuevo en cada llamada, la misma en todos sus intentos; la tuya, con
 * {@code api.post(path, data, new MundoSMS.RequestOptions().idempotencyKey("pedido-881"))}). Por eso un POST que falla por
 * la red, o con 409 «Request in progress», se repite sin miedo: la API no lo hace dos veces. Si la respuesta es la guardada
 * de la primera vez, trae {@code "idempotent_replayed"} = {@code Boolean.TRUE}.
 *
 * <p>Reintenta: un 429, en cualquier método (no se hizo nada); un fallo de red, en cualquier método; un 5xx, solo en GET (un
 * POST con 5xx no se repite: la API no guarda los 5xx, y repetirlo lo haría otra vez). Espera lo que diga Retry-After (como
 * mucho 60 s) o, si no lo trae, 1, 2, 4… s. Un DELETE que se repite tras quedarse sin respuesta y da 404 ya estaba hecho:
 * vuelve como éxito, con data.changed {@code false}.
 *
 * <p>Una redirección (3xx) no se sigue nunca: es un {@link MundoSMSError} (la API no redirige). Un 2xx que no es JSON (el HTML
 * de un proxy o de un portal cautivo) también es un error; los ficheros que da la API (audio, imagen, PDF) vuelven por su
 * Content-Type.
 *
 * <p>Los avisos que te mandamos (callback_url y webhooks de WhatsApp) se comprueban con
 * {@link #verifyWebhook(String, String, byte[])}.
 *
 * <p>PATCH sale como POST con la cabecera {@code X-HTTP-Method-Override: PATCH} (HttpURLConnection no sabe mandar PATCH),
 * firmado como PATCH. Se puede usar el mismo objeto desde varios hilos a la vez.
 */
public class MundoSMS {

    /** La versión de este cliente: va también en el User-Agent. */
    public static final String VERSION = "1.2.0";

    /** Los segundos que puede alejarse de tu hora la «t» de la firma de un aviso, si no dices otra cosa. */
    public static final long WEBHOOK_TOLERANCE = 300;

    private static final String DEFAULT_BASE_URL = "https://api.mundosms.es/v3";
    private static final int MAX_RETRY_DELAY = 60;   // segundos: lo más que se espera antes de un reintento, aunque Retry-After pida más
    private static final int MAX_DEPTH = 1000;   // anidamiento de un JSON (contra respuestas o datos que se contienen a sí mismos)
    private static final Set<String> QUERY_METHODS = new HashSet<String>(Arrays.asList("GET", "DELETE", "HEAD", "OPTIONS", "TRACE"));
    private static final Set<String> NATIVE_METHODS = new HashSet<String>(Arrays.asList("GET", "POST", "PUT", "DELETE", "HEAD", "OPTIONS", "TRACE"));
    private static final AtomicLong LAST_TIMESTAMP = new AtomicLong();
    private static final DateTimeFormatter[] HTTP_DATES = {
        DateTimeFormatter.RFC_1123_DATE_TIME,                                                     // Sun, 06 Nov 1994 08:49:37 GMT
        DateTimeFormatter.ofPattern("EEEE, dd-MMM-yy HH:mm:ss zzz", Locale.US),                   // Sunday, 06-Nov-94 08:49:37 GMT
        DateTimeFormatter.ofPattern("EEE MMM ppd HH:mm:ss yyyy", Locale.US).withZone(ZoneOffset.UTC), // Sun Nov  6 08:49:37 1994
    };

    /** Cada petición corre en un hilo de estos y quien la pidió espera, como mucho, el plazo: así el plazo es de la petición ENTERA. */
    private static final ExecutorService THREADS = Executors.newCachedThreadPool(task -> {
        Thread t = new Thread(task, "MundoSMS-http");
        t.setDaemon(true);
        return t;
    });

    private final String token;
    private final String privateKey;
    private final String baseUrl;
    private final String basePath;
    private final int retries;
    private final double timeout;
    private volatile String lastCursor;
    private volatile RateLimit lastRateLimit;

    /**
     * El cupo que dijo una respuesta: sus cabeceras X-RateLimit-* ({@link #getLastRateLimit()}). Lo que no traía, -1.
     */
    public static final class RateLimit {
        private final long limit;
        private final long remaining;
        private final long reset;

        RateLimit(long limit, long remaining, long reset) {
            this.limit = limit;
            this.remaining = remaining;
            this.reset = reset;
        }

        /**
         * X-RateLimit-Limit: las peticiones por segundo que admite tu clave.
         *
         * @return el límite, o -1 si no lo traía
         */
        public long getLimit() {
            return limit;
        }

        /**
         * X-RateLimit-Remaining: las que te quedan en este segundo, contada esta.
         *
         * @return las que quedan, o -1 si no lo traía
         */
        public long getRemaining() {
            return remaining;
        }

        /**
         * X-RateLimit-Reset: los segundos que faltan para que se reponga el cupo.
         *
         * @return los segundos, o -1 si no lo traía
         */
        public long getReset() {
            return reset;
        }

        @Override
        public String toString() {
            return "RateLimit{limit=" + limit + ", remaining=" + remaining + ", reset=" + reset + "}";
        }
    }

    /**
     * Opciones del cliente, para el tercer argumento del constructor:
     * {@code new MundoSMS.Options().retries(5).timeout(90)}.
     */
    public static final class Options {
        private String baseUrl = DEFAULT_BASE_URL;
        private int retries = 3;
        private double timeout = 60;

        /** Las opciones por defecto: baseUrl https://api.mundosms.es/v3, 3 reintentos y 60 s de plazo. */
        public Options() {
        }

        /**
         * La dirección de la API.
         *
         * @param baseUrl por defecto, https://api.mundosms.es/v3
         * @return estas mismas opciones
         */
        public Options baseUrl(String baseUrl) {
            if (baseUrl == null) throw new IllegalArgumentException("MundoSMS: baseUrl is missing");
            this.baseUrl = baseUrl;
            return this;
        }

        /**
         * Cuántas veces se reintenta lo que es seguro repetir.
         *
         * @param retries 0, ninguna; por defecto, 3
         * @return estas mismas opciones
         */
        public Options retries(int retries) {
            if (retries < 0) throw new IllegalArgumentException("MundoSMS: retries cannot be negative");
            this.retries = retries;
            return this;
        }

        /**
         * Los segundos que puede durar cada petición entera (conectar, mandar y recibir la respuesta).
         *
         * @param seconds mayor que 0, con decimales si quieres; por defecto, 60
         * @return estas mismas opciones
         */
        public Options timeout(double seconds) {
            if (!(seconds > 0) || Double.isInfinite(seconds)) throw new IllegalArgumentException("MundoSMS: timeout must be a number of seconds greater than 0");
            this.timeout = seconds;
            return this;
        }
    }

    /**
     * Opciones de una petición, para el último argumento de {@link #post(String, Map, RequestOptions)} y de
     * {@link #request(String, String, Map, RequestOptions)}: {@code new MundoSMS.RequestOptions().idempotencyKey("pedido-881")}.
     */
    public static final class RequestOptions {
        private String idempotencyKey;

        /** Sin opciones: en un POST, la Idempotency-Key es un UUID nuevo. */
        public RequestOptions() {
        }

        /**
         * Tu Idempotency-Key, para que repetir este POST no lo vuelva a hacer (por ejemplo, el id de tu pedido). La API la tiene
         * en cuenta en los POST.
         *
         * @param key de 1 a 64 caracteres: letras sin tilde, cifras y _ . : -; {@code null}, un UUID nuevo
         * @return estas mismas opciones
         * @throws IllegalArgumentException si la clave no tiene esa forma
         */
        public RequestOptions idempotencyKey(String key) {
            this.idempotencyKey = key == null ? null : validKey(key);
            return this;
        }

        /**
         * La Idempotency-Key que pusiste.
         *
         * @return la clave, o {@code null}
         */
        public String getIdempotencyKey() {
            return idempotencyKey;
        }
    }

    /**
     * Un cliente para una API Key que no exige firma.
     *
     * @param token el Token de tu API Key (empieza por eyJ)
     */
    public MundoSMS(String token) {
        this(token, "", null);
    }

    /**
     * Un cliente con las opciones por defecto.
     *
     * @param token      el Token de tu API Key (empieza por eyJ)
     * @param privateKey su clave privada si exige firma (lo normal), o ""
     */
    public MundoSMS(String token, String privateKey) {
        this(token, privateKey, null);
    }

    /**
     * Un cliente con sus opciones.
     *
     * @param token      el Token de tu API Key (empieza por eyJ)
     * @param privateKey su clave privada si exige firma (lo normal), o ""
     * @param options    baseUrl, retries y timeout; {@code null}, las de por defecto
     */
    public MundoSMS(String token, String privateKey, Options options) {
        if (token == null) throw new IllegalArgumentException("MundoSMS: the token is missing");
        Options o = options == null ? new Options() : options;
        this.token = trimSpace(token);
        this.privateKey = privateKey == null ? "" : trimSpace(privateKey);
        String b = o.baseUrl.trim();
        while (b.endsWith("/")) b = b.substring(0, b.length() - 1);
        URI u;
        try {
            u = new URI(b);
        } catch (URISyntaxException e) {
            throw new IllegalArgumentException("MundoSMS: baseUrl is not a valid URL: " + o.baseUrl, e);
        }
        String scheme = u.getScheme() == null ? "" : u.getScheme().toLowerCase(Locale.ROOT);
        if (!(scheme.equals("https") || scheme.equals("http")) || u.getHost() == null || u.getRawQuery() != null || u.getRawFragment() != null) {
            throw new IllegalArgumentException("MundoSMS: baseUrl must be an http(s):// URL with no \"?\" or \"#\": " + o.baseUrl);
        }
        this.baseUrl = b;
        this.basePath = u.getRawPath() == null ? "" : u.getRawPath();
        this.retries = o.retries;
        this.timeout = o.timeout;
    }

    /**
     * Una consulta sin filtros.
     *
     * @param path "/calls", "/numbers/34910123456"… (sin /v3)
     * @return la respuesta entera ({@code status}, {@code status_code}, {@code message}, {@code data})
     * @throws MundoSMSError si la API contesta con un error o no contesta
     */
    public Map<String, Object> get(String path) {
        return request("GET", path, null);
    }

    /**
     * Una consulta. Los filtros van en la query: {@code true}/{@code false} como true y false, una lista o un array separado
     * por comas, y {@code null} y las listas vacías no se mandan.
     *
     * @param path  "/calls", "/numbers/34910123456"… (sin /v3)
     * @param query los parámetros, o {@code null}
     * @return la respuesta entera
     * @throws MundoSMSError si la API contesta con un error o no contesta
     */
    public Map<String, Object> get(String path, Map<String, ?> query) {
        return request("GET", path, query);
    }

    /**
     * Un POST sin cuerpo.
     *
     * @param path la ruta (sin /v3)
     * @return la respuesta entera
     * @throws MundoSMSError si la API contesta con un error o no contesta
     */
    public Map<String, Object> post(String path) {
        return request("POST", path, null);
    }

    /**
     * Un POST, con los datos en un cuerpo JSON.
     *
     * @param path la ruta (sin /v3): "/sms", "/whatsapp/messages"…
     * @param data el cuerpo: un {@code Map} con textos, números, {@code Boolean}, {@code null}, listas, arrays y otros
     *             {@code Map}; {@code null} o vacío, sin cuerpo
     * @return la respuesta entera
     * @throws MundoSMSError            si la API contesta con un error o no contesta
     * @throws IllegalArgumentException si los datos no se pueden pasar a JSON (no se manda nada)
     */
    public Map<String, Object> post(String path, Map<String, ?> data) {
        return request("POST", path, data, null);
    }

    /**
     * Un POST, con los datos en un cuerpo JSON y tus opciones (tu Idempotency-Key).
     *
     * @param path    la ruta (sin /v3): "/sms", "/whatsapp/messages"…
     * @param data    el cuerpo, como en {@link #post(String, Map)}
     * @param options {@code new MundoSMS.RequestOptions().idempotencyKey("pedido-881")}, o {@code null}
     * @return la respuesta entera
     * @throws MundoSMSError            si la API contesta con un error o no contesta
     * @throws IllegalArgumentException si los datos no se pueden pasar a JSON (no se manda nada)
     */
    public Map<String, Object> post(String path, Map<String, ?> data, RequestOptions options) {
        return request("POST", path, data, options);
    }

    /**
     * Un PATCH, con los datos en un cuerpo JSON (sale como POST con {@code X-HTTP-Method-Override: PATCH}).
     *
     * @param path la ruta (sin /v3): "/extensions/recepcion"…
     * @param data lo que cambia
     * @return la respuesta entera
     * @throws MundoSMSError            si la API contesta con un error o no contesta
     * @throws IllegalArgumentException si los datos no se pueden pasar a JSON (no se manda nada)
     */
    public Map<String, Object> patch(String path, Map<String, ?> data) {
        return request("PATCH", path, data);
    }

    /**
     * Un DELETE sin parámetros.
     *
     * @param path la ruta (sin /v3)
     * @return la respuesta entera
     * @throws MundoSMSError si la API contesta con un error o no contesta
     */
    public Map<String, Object> delete(String path) {
        return request("DELETE", path, null);
    }

    /**
     * Un DELETE, con los parámetros en la query (como en {@link #get(String, Map)}).
     *
     * @param path  la ruta (sin /v3)
     * @param query los parámetros, o {@code null}
     * @return la respuesta entera
     * @throws MundoSMSError si la API contesta con un error o no contesta
     */
    public Map<String, Object> delete(String path, Map<String, ?> query) {
        return request("DELETE", path, query);
    }

    /**
     * Recorre un listado entero sin filtros. Ver {@link #all(String, Map)}.
     *
     * @param path la ruta del listado (sin /v3)
     * @return los elementos, uno a uno
     */
    public Iterable<Map<String, Object>> all(String path) {
        return all(path, null);
    }

    /**
     * Recorre un listado entero, página a página, siguiendo su cursor (next_cursor), elemento a elemento. Cada página lleva
     * los mismos filtros (y el mismo order) más el cursor. Las páginas se piden según se recorren, y cada recorrido empieza
     * desde el principio. Al acabar, el último next_cursor queda en {@link #getLastCursor()}, que avanza cuando ya te ha dado
     * todos los elementos de una página: si tu código falla a mitad de una, sigue en la anterior.
     *
     * <p>Termina con has_more false (con stop_at_ongoing, también cuando la página se para en una llamada en curso), o si el
     * cursor no avanza. Una página vacía con has_more true (con stop_at_ongoing y un filtro, cuando ha mirado muchas que no
     * casan) no es el final: sigue.
     *
     * <p>Una lista que no va por páginas (GET /calls/missed y /calls/live: has_more sin next_cursor) no se puede recorrer
     * entera: es un {@link MundoSMSError}, antes de ningún elemento. Sube limit, o filtra, y pídela con {@link #get(String, Map)}.
     *
     * @param path  la ruta del listado (sin /v3): "/calls", "/numbers"…
     * @param query los parámetros, o {@code null}
     * @return los elementos, uno a uno; un error de la API salta, como {@link MundoSMSError}, al pedir el siguiente
     */
    public Iterable<Map<String, Object>> all(final String path, Map<String, ?> query) {
        if (path == null) throw new IllegalArgumentException("MundoSMS: the path is missing");
        final Map<String, Object> copy = new LinkedHashMap<String, Object>();
        if (query != null) copy.putAll(query);
        return () -> new Pages(path, new LinkedHashMap<String, Object>(copy));
    }

    /**
     * El cursor del último listado recorrido con {@link #all(String, Map)}: guárdalo y pásalo como cursor la próxima vez
     * para pedir solo lo nuevo. Al empezar el recorrido, el cursor que le pasas (o {@code null}); después, el último
     * next_cursor que llegó (una página sin cursor no lo borra). Es uno por cliente: para dos recorridos a la vez, un cliente
     * para cada uno.
     *
     * @return el último next_cursor, o {@code null}
     */
    public String getLastCursor() {
        return lastCursor;
    }

    /**
     * El cupo que dijo la última respuesta (sus cabeceras X-RateLimit-*): con él, un bucle propio no se pasa de las 10
     * peticiones por segundo (cuando {@code getRemaining()} llega a 0, espera {@code getReset()} segundos).
     *
     * @return el cupo, o {@code null} si la última respuesta no lo traía (o no hubo respuesta)
     */
    public RateLimit getLastRateLimit() {
        return lastRateLimit;
    }

    /**
     * Una petición cualquiera. Si la ruta trae "?…", esos parámetros se juntan con los datos (y si se repite uno, gana el de
     * los datos). En GET y DELETE los datos van en la query; en POST y PATCH, en un cuerpo JSON.
     *
     * @param method "GET", "POST", "PATCH" o "DELETE"
     * @param path   "/calls", "/numbers/34910123456"… (sin /v3: si lo lleva, se quita). Cada trozo se codifica solo; un %XX
     *               que ya escribas se respeta
     * @param data   los parámetros o el cuerpo, o {@code null}
     * @return la respuesta entera, o, si no es JSON (un audio, un PDF, un 204), un {@code Map} con {@code status}
     *         ("success"), {@code content_type} y {@code body} (un {@code byte[]} con el contenido tal cual)
     * @throws MundoSMSError            si la API contesta con un error o no contesta
     * @throws IllegalArgumentException si los datos no se pueden pasar a JSON (no se manda nada)
     */
    public Map<String, Object> request(String method, String path, Map<String, ?> data) {
        return request(method, path, data, null);
    }

    /**
     * Una petición cualquiera, con tus opciones (tu Idempotency-Key). Ver {@link #request(String, String, Map)}.
     *
     * @param method  "GET", "POST", "PATCH" o "DELETE"
     * @param path    la ruta (sin /v3)
     * @param data    los parámetros o el cuerpo, o {@code null}
     * @param options {@code new MundoSMS.RequestOptions().idempotencyKey("pedido-881")}, o {@code null}: en un POST, la
     *                Idempotency-Key es entonces un UUID nuevo
     * @return la respuesta entera
     * @throws MundoSMSError            si la API contesta con un error o no contesta
     * @throws IllegalArgumentException si los datos no se pueden pasar a JSON (no se manda nada)
     */
    public Map<String, Object> request(String method, String path, Map<String, ?> data, RequestOptions options) {
        if (method == null || path == null) throw new IllegalArgumentException("MundoSMS: the method or the path is missing");
        String verb = method.trim().toUpperCase(Locale.ROOT);
        String ownKey = options == null ? null : options.idempotencyKey;
        String idemKey = ownKey != null ? validKey(ownKey) : verb.equals("POST") ? UUID.randomUUID().toString() : null;
        Map<String, Object> params = new LinkedHashMap<String, Object>();
        String pathname = splitPath(path, basePath.endsWith("/v3"), params);
        String body = "";
        boolean inQuery = QUERY_METHODS.contains(verb);
        if (inQuery) {
            if (data != null) {
                for (Map.Entry<String, ?> e : data.entrySet()) params.put(String.valueOf(e.getKey()), e.getValue());
            }
        } else if (data != null && !data.isEmpty()) {
            body = toJson(data);
        }
        String query = encodeQuery(params);
        String pathAndQuery = pathname + (query.isEmpty() ? "" : "?" + query);
        String url = baseUrl + pathAndQuery;
        String signedPath = basePath + pathAndQuery;   // lo que se firma es justo lo que se manda
        byte[] bytes = inQuery ? null : body.getBytes(StandardCharsets.UTF_8);
        String httpVerb = NATIVE_METHODS.contains(verb) ? verb : "POST";
        // POST y PATCH van con la longitud fija (streaming): así HttpURLConnection no los repite por su cuenta si se corta la
        // conexión. Pero en ese modo un 401 llega sin cuerpo: como un 401 no hizo nada, se pide otra vez sin streaming para
        // leer el error.
        boolean noStreaming = false;
        boolean afterNetwork = false;   // un intento de antes se quedó sin respuesta (o llegó dos veces): puede que se hiciera
        int attempt = 0;
        while (true) {
            attempt++;
            String ts = String.valueOf(timestamp());   // milisegundos: dos peticiones iguales seguidas no firman igual
            Map<String, String> headers = new LinkedHashMap<String, String>();
            headers.put("Authorization", "Bearer " + token);
            headers.put("X-Timestamp", ts);
            headers.put("Accept", "application/json");
            headers.put("User-Agent", "MundoSMS-Java/" + VERSION);
            if (bytes != null) headers.put("Content-Type", "application/json; charset=utf-8");
            if (!httpVerb.equals(verb)) headers.put("X-HTTP-Method-Override", verb);
            if (idemKey != null) headers.put("Idempotency-Key", idemKey);   // la misma en todos los intentos de esta llamada
            if (!privateKey.isEmpty()) headers.put("X-Signature", sign(token + "." + ts + "." + verb + " " + signedPath + "." + body));
            Response r = null;
            String networkError = null;
            Throwable networkCause = null;
            try {
                r = exchange(url, httpVerb, headers, bytes, !noStreaming);
            } catch (NetworkFailure e) {
                if (e.interrupted) {
                    lastRateLimit = null;
                    throw withCause(new MundoSMSError("No response from the API: " + e.getMessage()), e.getCause());
                }
                networkError = e.getMessage();
                networkCause = e.getCause();
            }
            int http = r == null ? 0 : r.http;   // si se cortó a medias, es un fallo de red aunque llegara la cabecera
            if (http == 401 && bytes != null && !noStreaming && r.body.length == 0) {
                noStreaming = true;
                attempt--;
                continue;
            }
            boolean redirect = r != null && http >= 300 && http < 400;   // de una redirección no se lee nada
            Map<String, Object> j = r == null || redirect ? null : parseJsonObject(r.body);
            if (retryable(verb, networkError != null, http, j, idemKey) && attempt <= retries
                && pause(retryDelay(r == null ? null : r.retryAfter, attempt))) {
                afterNetwork = afterNetwork || networkError != null || duplicated(http, j);
                continue;
            }
            if (networkError != null) {
                lastRateLimit = null;
                throw withCause(new MundoSMSError("No response from the API: " + networkError), networkCause);
            }
            lastRateLimit = r.rateLimit;
            if (redirect) {   // no se sigue: llevaría el token y la firma a otro servidor
                String where = r.location == null || r.location.trim().isEmpty() ? ""
                             : " to " + (r.location.trim().length() > 300 ? r.location.trim().substring(0, 300) : r.location.trim());
                throw new MundoSMSError("HTTP " + http + ": redirect" + where + " not followed (the API does not redirect: check baseUrl)",
                                        http, null, null, null, null, r.requestId);
            }
            if (r.replayed && j != null) j.put("idempotent_replayed", Boolean.TRUE);   // la respuesta guardada de la primera vez
            if (http >= 200 && http < 300) {
                if (j == null) {   // un fichero, o algo que no es de la API
                    String type = r.contentType == null ? "" : r.contentType.split(";", 2)[0].trim();
                    if (r.body.length > 0 && !isFileType(type)) {
                        throw new MundoSMSError("HTTP " + http + ": the response is not JSON (" + (type.isEmpty() ? "no Content-Type" : type) + ")",
                                                http, null, null, null, null, r.requestId);
                    }
                    Map<String, Object> f = new LinkedHashMap<String, Object>();
                    f.put("status", "success");
                    f.put("content_type", r.contentType == null ? "" : r.contentType);
                    f.put("body", r.body);
                    if (r.replayed) f.put("idempotent_replayed", Boolean.TRUE);
                    return f;
                }
                if (!"error".equals(j.get("status"))) return j;
            }
            if (verb.equals("DELETE") && afterNetwork && http == 404 && j != null
                && !String.valueOf(j.get("message")).startsWith("Route not found")) {
                // lo borró el intento que se quedó sin respuesta
                Map<String, Object> d = new LinkedHashMap<String, Object>();
                d.put("state", "deleted");
                d.put("changed", Boolean.FALSE);
                Map<String, Object> ok = new LinkedHashMap<String, Object>();
                ok.put("status", "success");
                ok.put("status_code", "0");
                ok.put("message", "Already deleted");
                ok.put("data", d);
                return ok;
            }
            if (j != null) {
                String message = asText(j.get("message"));
                if (message == null || message.isEmpty()) message = "Error";
                throw new MundoSMSError(message, http, asText(j.get("status_code")), asText(j.get("detail")), j, j.get("data"), r.requestId);
            }
            throw new MundoSMSError("HTTP " + http, http, null, null, null, null, r.requestId);
        }
    }

    /**
     * Si se puede repetir: un 429 en cualquier método; un GET, ante un fallo de red o un 5xx; un PATCH o un DELETE, ante un
     * fallo de red; un POST (con su Idempotency-Key), ante un fallo de red o un 409 «Request in progress». Un 5xx de un POST
     * no: la API no guarda los 5xx y repetirlo lo haría otra vez.
     */
    private static boolean retryable(String verb, boolean network, int http, Map<String, Object> j, String idemKey) {
        if (http == 429) return true;
        // Un GET o un DELETE que se corta tras mandarlo lo repite el propio HttpURLConnection con la MISMA firma; con «Rechazar
        // peticiones repetidas», esa copia da 409 Duplicated request (la MISMA petición firmada llegó dos veces): es un fallo
        // de red más, en cualquier método.
        Object message = j == null ? null : j.get("message");
        network = network || duplicated(http, j);
        if (verb.equals("GET")) return network || http >= 500;
        if (verb.equals("PATCH") || verb.equals("DELETE")) return network;
        if (idemKey != null) return network || (http == 409 && "Request in progress".equals(message));
        return false;
    }

    /** 409 «Duplicated request»: la MISMA petición firmada llegó dos veces (la repitió algo entre medias). */
    private static boolean duplicated(int http, Map<String, Object> j) {
        Object m = j == null ? null : j.get("message");
        return http == 409 && m instanceof String && ((String) m).startsWith("Duplicated request");
    }

    /** Lo que la API da como fichero (grabaciones, locuciones, diagramas y el PDF de un certificado), por su Content-Type. */
    private static boolean isFileType(String type) {
        String t = type.toLowerCase(Locale.ROOT);
        return t.startsWith("audio/") || t.startsWith("image/") || t.equals("application/pdf") || t.equals("application/octet-stream");
    }

    private static MundoSMSError withCause(MundoSMSError e, Throwable cause) {
        if (cause != null) e.initCause(cause);
        return e;
    }

    /** La Idempotency-Key, si tiene la forma que exige la API (de 1 a 64: A-Z a-z 0-9 _ . : -). */
    private static String validKey(String key) {
        boolean ok = !key.isEmpty() && key.length() <= 64;
        for (int i = 0; ok && i < key.length(); i++) {
            char c = key.charAt(i);
            ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == ':' || c == '-';
        }
        if (!ok) throw new IllegalArgumentException("MundoSMS: idempotencyKey must be 1 to 64 characters: A-Z, a-z, 0-9 and _ . : - (nothing was sent)");
        return key;
    }

    // -------------------------------------------------------------------------------------------------------------- avisos

    /**
     * Si un aviso que te llega (callback_url o webhook de WhatsApp) es nuestro y nadie lo ha tocado, con la tolerancia de
     * 300 s. Ver {@link #verifyWebhook(String, String, byte[], long)}.
     *
     * @param secret el de «API y Soporte → Webhooks» (whsec_…)
     * @param header la cabecera X-MundoSMS-Signature
     * @param body   el cuerpo TAL CUAL llegó, en bytes
     * @return {@code true} si la firma es buena y reciente
     */
    public static boolean verifyWebhook(String secret, String header, byte[] body) {
        return verifyWebhook(secret, header, body, WEBHOOK_TOLERANCE);
    }

    /**
     * Como {@link #verifyWebhook(String, String, byte[])}, con el cuerpo como texto (se firma en UTF-8).
     *
     * @param secret el de «API y Soporte → Webhooks» (whsec_…)
     * @param header la cabecera X-MundoSMS-Signature
     * @param body   el cuerpo TAL CUAL llegó, sin volver a serializar el JSON
     * @return {@code true} si la firma es buena y reciente
     */
    public static boolean verifyWebhook(String secret, String header, String body) {
        return verifyWebhook(secret, header, body == null ? null : body.getBytes(StandardCharsets.UTF_8), WEBHOOK_TOLERANCE);
    }

    /**
     * Como {@link #verifyWebhook(String, String, byte[], long)}, con el cuerpo como texto (se firma en UTF-8).
     *
     * @param secret    el de «API y Soporte → Webhooks» (whsec_…)
     * @param header    la cabecera X-MundoSMS-Signature
     * @param body      el cuerpo TAL CUAL llegó, sin volver a serializar el JSON
     * @param tolerance los segundos que puede alejarse «t» de tu hora
     * @return {@code true} si la firma es buena y reciente
     */
    public static boolean verifyWebhook(String secret, String header, String body, long tolerance) {
        return verifyWebhook(secret, header, body == null ? null : body.getBytes(StandardCharsets.UTF_8), tolerance);
    }

    /**
     * Si un aviso que te llega (callback_url o webhook de WhatsApp) es nuestro y nadie lo ha tocado: la cabecera
     * {@code X-MundoSMS-Signature: t=<segundos>,v1=<firma>}, con {@code v1} = HMAC-SHA256 en hexadecimal, con tu secreto, de
     * {@code t + "." + body}, y {@code t} a no más de {@code tolerance} segundos de tu hora. Compara en tiempo constante.
     * Una cabecera que falta o está mal formada da {@code false}, sin excepción.
     *
     * @param secret    el de «API y Soporte → Webhooks» (whsec_…)
     * @param header    la cabecera X-MundoSMS-Signature
     * @param body      el cuerpo TAL CUAL llegó, en bytes (sin volver a serializar el JSON)
     * @param tolerance los segundos que puede alejarse «t» de tu hora
     * @return {@code true} si la firma es buena y reciente
     */
    public static boolean verifyWebhook(String secret, String header, byte[] body, long tolerance) {
        String s = secret == null ? "" : trimSpace(secret);
        if (s.isEmpty() || header == null || body == null) return false;
        String t = null;
        List<String> signatures = new ArrayList<String>();
        for (String part : header.split(",", -1)) {
            int p = part.indexOf('=');
            if (p < 0) continue;
            String k = part.substring(0, p).trim();
            String v = part.substring(p + 1).trim();
            if (k.equals("t")) {
                if (t != null) return false;   // dos «t»: no se sabe cuál vale
                t = v;
            } else if (k.equals("v1")) {
                signatures.add(v.toLowerCase(Locale.ROOT));
            }
        }
        if (t == null || t.isEmpty() || t.length() > 15 || signatures.isEmpty()) return false;
        for (int i = 0; i < t.length(); i++) if (t.charAt(i) < '0' || t.charAt(i) > '9') return false;
        if (Math.abs(System.currentTimeMillis() / 1000 - Long.parseLong(t)) > tolerance) return false;
        byte[] expected;
        try {
            Mac mac = Mac.getInstance("HmacSHA256");
            mac.init(new SecretKeySpec(s.getBytes(StandardCharsets.UTF_8), "HmacSHA256"));
            mac.update((t + ".").getBytes(StandardCharsets.UTF_8));
            byte[] h = mac.doFinal(body);
            StringBuilder sb = new StringBuilder(64);
            for (byte x : h) sb.append(Character.forDigit((x >> 4) & 15, 16)).append(Character.forDigit(x & 15, 16));
            expected = sb.toString().getBytes(StandardCharsets.US_ASCII);
        } catch (GeneralSecurityException e) {
            throw new IllegalStateException("MundoSMS: this Java cannot compute HmacSHA256", e);
        }
        boolean ok = false;
        for (String f : signatures) {   // todas, sin cortar en la primera: MessageDigest.isEqual es de tiempo constante
            if (MessageDigest.isEqual(expected, f.getBytes(StandardCharsets.UTF_8))) ok = true;
        }
        return ok;
    }

    /**
     * Un {@code Map} (un {@code LinkedHashMap}, que conserva el orden) a partir de pares clave, valor:
     * {@code MundoSMS.map("to", "34600123456", "text", "Hola")}. Vale cualquier {@code Map}; esto solo lo abrevia.
     *
     * @param keyValues clave, valor, clave, valor…: las claves son textos
     * @return el {@code Map}, que se puede seguir cambiando
     */
    public static Map<String, Object> map(Object... keyValues) {
        if (keyValues.length % 2 != 0) throw new IllegalArgumentException("MundoSMS.map: the arguments go in key, value pairs (got " + keyValues.length + ")");
        Map<String, Object> m = new LinkedHashMap<String, Object>();
        for (int i = 0; i < keyValues.length; i += 2) {
            if (!(keyValues[i] instanceof String)) throw new IllegalArgumentException("MundoSMS.map: key " + (i / 2 + 1) + " is not a String: " + keyValues[i]);
            m.put((String) keyValues[i], keyValues[i + 1]);
        }
        return m;
    }

    // ---------------------------------------------------------------------------------------------------------------- errores

    /**
     * El error de una petición: la API contestó con un error (también un 2xx con "status": "error") o no contestó
     * ({@code getHttp()} 0 y el mensaje empieza por «No response from the API:», seguido de la causa).
     */
    public static class MundoSMSError extends RuntimeException {
        private static final long serialVersionUID = 1L;
        /** El código HTTP, 0 si no hubo respuesta. */
        private final int http;
        /** El status_code de la API. */
        private final String statusCode;
        /** El detail de la API. */
        private final String detail;
        private final transient Map<String, Object> response;
        private final transient Object data;
        /** La cabecera X-Request-Id. */
        private final String requestId;

        /**
         * Un error sin respuesta de la API.
         *
         * @param message qué pasó
         */
        public MundoSMSError(String message) {
            this(message, 0, null, null, null, null, null);
        }

        /**
         * Un error con todo lo que trajo la respuesta.
         *
         * @param message    el message de la API, o «HTTP 502» si no contestó en JSON
         * @param http       el código HTTP, 0 si no hubo respuesta
         * @param statusCode el status_code de la API
         * @param detail     el detail de la API
         * @param response   la respuesta entera, decodificada
         * @param data       el data de la respuesta
         * @param requestId  la cabecera X-Request-Id
         */
        public MundoSMSError(String message, int http, String statusCode, String detail, Map<String, Object> response, Object data, String requestId) {
            super(message);
            this.http = http;
            this.statusCode = statusCode;
            this.detail = detail;
            this.response = response;
            this.data = data;
            this.requestId = requestId;
        }

        /**
         * El código HTTP.
         *
         * @return 0 si no hubo respuesta (la red, o el plazo agotado)
         */
        public int getHttp() {
            return http;
        }

        /**
         * El status_code de la API, en texto: "3", "4"…
         *
         * @return el status_code, o {@code null} si la respuesta no lo trae
         */
        public String getStatusCode() {
            return statusCode;
        }

        /**
         * El detail de la API: qué falta o qué está mal.
         *
         * @return el detail, o {@code null}
         */
        public String getDetail() {
            return detail;
        }

        /**
         * La respuesta entera, decodificada.
         *
         * @return la respuesta, o {@code null} si no fue JSON
         */
        public Map<String, Object> getResponse() {
            return response;
        }

        /**
         * El data de la respuesta (en un WhatsApp que WhatsApp rechazó, un {@code Map} con el id del mensaje).
         *
         * @return el data, o {@code null}
         */
        public Object getData() {
            return data;
        }

        /**
         * La cabecera X-Request-Id de la respuesta: con ella encuentras la petición en Logs API.
         *
         * @return el X-Request-Id, o {@code null}
         */
        public String getRequestId() {
            return requestId;
        }

        /**
         * El mensaje con el código HTTP, el status_code, el detail y el X-Request-Id: lo que hace falta en un registro.
         *
         * @return el error en una línea
         */
        @Override
        public String toString() {
            StringBuilder sb = new StringBuilder(getClass().getName()).append(": ").append(getMessage());
            List<String> extra = new ArrayList<String>();
            if (http != 0) extra.add("HTTP " + http);
            if (statusCode != null) extra.add("status_code " + statusCode);
            if (detail != null) extra.add("detail: " + detail);
            if (requestId != null) extra.add("X-Request-Id " + requestId);
            if (!extra.isEmpty()) sb.append(" (").append(String.join(", ", extra)).append(')');
            return sb.toString();
        }
    }

    // ------------------------------------------------------------------------------------------------------------ por dentro

    /** Lo que volvió de una petición. */
    private static final class Response {
        final int http;
        final String contentType;
        final String retryAfter;
        final String requestId;
        final byte[] body;
        final boolean replayed;   // Idempotent-Replayed: true
        final String location;    // la de una redirección
        final RateLimit rateLimit;

        Response(int http, String contentType, String retryAfter, String requestId, byte[] body, boolean replayed, String location,
                 RateLimit rateLimit) {
            this.http = http;
            this.contentType = contentType;
            this.retryAfter = retryAfter;
            this.requestId = requestId;
            this.body = body;
            this.replayed = replayed;
            this.location = location;
            this.rateLimit = rateLimit;
        }
    }

    /** No hubo respuesta: la red, el plazo o el hilo interrumpido. */
    private static final class NetworkFailure extends Exception {
        private static final long serialVersionUID = 1L;
        final boolean interrupted;

        NetworkFailure(String message, boolean interrupted) {
            this(message, interrupted, null);
        }

        NetworkFailure(String message, boolean interrupted, Throwable cause) {
            super(message, cause);
            this.interrupted = interrupted;
        }
    }

    /** Las páginas de un listado, según se recorren. */
    private final class Pages implements Iterator<Map<String, Object>> {
        private final String path;
        private final Map<String, Object> query;
        private String listKey;
        private Iterator<?> page = Collections.emptyIterator();
        private boolean done;
        private String pendingCursor;   // el next_cursor de la página en curso: pasa a lastCursor cuando se han dado todos sus elementos

        Pages(String path, Map<String, Object> query) {
            this.path = path;
            this.query = query;
            Object start = query.get("cursor");   // el cursor de este recorrido: al empezar, el que le pasas
            lastCursor = start == null || String.valueOf(start).isEmpty() ? null : String.valueOf(start);
        }

        @Override
        public boolean hasNext() {
            while (!page.hasNext()) {
                if (pendingCursor != null) {
                    lastCursor = pendingCursor;
                    pendingCursor = null;
                }
                if (done) return false;
                fetchNext();
            }
            return true;
        }

        @Override
        @SuppressWarnings("unchecked")
        public Map<String, Object> next() {
            if (!hasNext()) throw new NoSuchElementException();
            Object e = page.next();
            if (e != null && !(e instanceof Map)) throw new IllegalStateException("MundoSMS: \"" + listKey + "\" is not a list of objects");
            return (Map<String, Object>) e;
        }

        private void fetchNext() {
            Map<String, Object> r = get(path, query);
            Object d = r.get("data");
            Map<?, ?> data = d instanceof Map ? (Map<?, ?>) d : Collections.emptyMap();
            Object cursor = data.get("next_cursor");
            if (truthy(data.get("has_more")) && truthy(data.get("results")) && !truthy(cursor)) {
                done = true;
                throw new MundoSMSError("MundoSMS: all() cannot go through " + path + ": it has more (has_more) but no next_cursor, so the list "
                                        + "is not paginated: raise limit or narrow the filters, and read it with get()",
                                        200, asText(r.get("status_code")), null, r, data, null);
            }
            if (listKey == null) {   // la lista es el primer campo que es una lista: un objeto ({}) no lo es
                for (Map.Entry<?, ?> e : data.entrySet()) {
                    if (e.getValue() instanceof List) {
                        listKey = String.valueOf(e.getKey());
                        break;
                    }
                }
            }
            Object l = listKey == null ? null : data.get(listKey);
            page = l instanceof List ? ((List<?>) l).iterator() : Collections.emptyIterator();
            if (truthy(cursor)) pendingCursor = String.valueOf(cursor);
            Object sent = query.get("cursor");
            if (!truthy(data.get("has_more")) || !truthy(cursor) || (sent != null && String.valueOf(sent).equals(String.valueOf(cursor)))
                || truthy(data.get("ongoing_call"))) done = true;
            else query.put("cursor", cursor);
        }
    }

    /**
     * La petición, en un hilo del grupo. Quien la pidió espera como mucho el plazo y, si se agota, la abandona: la conexión se
     * cierra en segundo plano (cerrarla desde aquí podría quedarse esperando a la lectura que está en marcha).
     */
    private Response exchange(final String url, final String verb, final Map<String, String> headers, final byte[] body,
                              final boolean streaming) throws NetworkFailure {
        final int ms = (int) Math.min(Integer.MAX_VALUE, Math.max(1L, (long) Math.ceil(timeout * 1000)));
        final AtomicReference<HttpURLConnection> connection = new AtomicReference<HttpURLConnection>();
        final AtomicBoolean abandoned = new AtomicBoolean();
        Callable<Response> task = () -> send(url, verb, headers, body, streaming, ms, connection, abandoned);
        Future<Response> f;
        try {
            f = THREADS.submit(task);
        } catch (RejectedExecutionException e) {
            throw new NetworkFailure("could not start the request: " + e.getMessage(), false);
        }
        try {
            return f.get(Math.max(1L, (long) (timeout * 1e9)), TimeUnit.NANOSECONDS);
        } catch (TimeoutException e) {
            abandon(f, connection, abandoned);
            throw new NetworkFailure("timed out after " + formatSeconds(timeout) + " s", false);
        } catch (InterruptedException e) {
            abandon(f, connection, abandoned);
            Thread.currentThread().interrupt();
            throw new NetworkFailure("the thread waiting for the response was interrupted", true);
        } catch (ExecutionException e) {
            Throwable c = e.getCause();
            if (c instanceof SocketTimeoutException) throw new NetworkFailure("timed out after " + formatSeconds(timeout) + " s", false, c);
            if (c instanceof RuntimeException) throw (RuntimeException) c;
            if (c instanceof Error) throw (Error) c;
            throw new NetworkFailure(reason(c), false, c);
        }
    }

    private static Response send(String url, String verb, Map<String, String> headers, byte[] body, boolean streaming, int ms,
                                 AtomicReference<HttpURLConnection> connection, AtomicBoolean abandoned) throws IOException {
        HttpURLConnection c = (HttpURLConnection) URI.create(url).toURL().openConnection();
        connection.set(c);
        boolean complete = false;
        try {
            c.setConnectTimeout(ms);
            c.setReadTimeout(ms);
            c.setInstanceFollowRedirects(false);   // una redirección cambiaría la ruta firmada
            c.setUseCaches(false);
            c.setRequestMethod(verb);
            for (Map.Entry<String, String> e : headers.entrySet()) c.setRequestProperty(e.getKey(), e.getValue());
            if (abandoned.get()) throw new IOException("abandoned");
            if (body != null) {
                c.setDoOutput(true);
                if (streaming) c.setFixedLengthStreamingMode(body.length);
                OutputStream o = c.getOutputStream();
                try {
                    o.write(body);
                } finally {
                    o.close();
                }
            }
            int http = c.getResponseCode();
            if (http < 0) throw new IOException("the response is not HTTP");
            InputStream in = http >= 400 ? c.getErrorStream() : c.getInputStream();
            ByteArrayOutputStream b = new ByteArrayOutputStream();
            if (in != null) {
                try {
                    byte[] buf = new byte[8192];
                    for (int n; (n = in.read(buf)) != -1; ) {
                        if (abandoned.get()) throw new IOException("abandoned");
                        b.write(buf, 0, n);
                    }
                } finally {
                    in.close();
                }
            }
            complete = true;
            String replayed = c.getHeaderField("Idempotent-Replayed");
            long[] rl = {-1, -1, -1};
            boolean any = false;
            String[] names = {"X-RateLimit-Limit", "X-RateLimit-Remaining", "X-RateLimit-Reset"};
            for (int i = 0; i < 3; i++) {
                String v = c.getHeaderField(names[i]);
                try {
                    if (v != null) {
                        rl[i] = Long.parseLong(v.trim());
                        any = true;
                    }
                } catch (NumberFormatException ignored) {
                    // no es un número: como si no viniera
                }
            }
            return new Response(http, c.getContentType(), c.getHeaderField("Retry-After"), c.getHeaderField("X-Request-Id"), b.toByteArray(),
                                replayed != null && replayed.trim().equalsIgnoreCase("true"), c.getHeaderField("Location"),
                                any ? new RateLimit(rl[0], rl[1], rl[2]) : null);
        } finally {
            if (!complete) c.disconnect();
        }
    }

    private static void abandon(Future<?> f, AtomicReference<HttpURLConnection> connection, AtomicBoolean abandoned) {
        abandoned.set(true);
        f.cancel(true);
        HttpURLConnection c = connection.get();
        if (c != null) {
            try {
                THREADS.execute(c::disconnect);
            } catch (RejectedExecutionException e) {
                // sin hilos: la conexión se cerrará sola al agotarse su plazo de lectura
            }
        }
    }

    /** Milisegundos, siempre crecientes en este proceso: dos peticiones a la vez desde dos hilos no firman con la misma marca. */
    private static long timestamp() {
        return LAST_TIMESTAMP.accumulateAndGet(System.currentTimeMillis(), (previous, now) -> Math.max(now, previous + 1));
    }

    private String sign(String text) {
        try {
            Mac mac = Mac.getInstance("HmacSHA256");
            mac.init(new SecretKeySpec(privateKey.getBytes(StandardCharsets.UTF_8), "HmacSHA256"));
            return Base64.getUrlEncoder().withoutPadding().encodeToString(mac.doFinal(text.getBytes(StandardCharsets.UTF_8)));
        } catch (GeneralSecurityException e) {
            throw new IllegalStateException("MundoSMS: this Java cannot compute HmacSHA256", e);
        }
    }

    /** Segundos antes del siguiente intento: lo que pida Retry-After (segundos o fecha HTTP), o 1, 2, 4… Como mucho 60. */
    static double retryDelay(String retryAfter, int attempt) {
        Double s = null;
        if (retryAfter != null && !retryAfter.trim().isEmpty()) {
            String t = retryAfter.trim();
            try {
                s = Double.valueOf(t);
            } catch (NumberFormatException e) {
                for (DateTimeFormatter formatter : HTTP_DATES) {
                    try {
                        s = (ZonedDateTime.parse(t, formatter).toInstant().toEpochMilli() - System.currentTimeMillis()) / 1000.0;
                        break;
                    } catch (DateTimeParseException ignored) {
                        // el siguiente formato
                    }
                }
            }
            if (s != null && s.isNaN()) s = null;
        }
        if (s == null) s = Math.pow(2, attempt - 1);
        return Math.max(0, Math.min(MAX_RETRY_DELAY, s));
    }

    /** Espera; si interrumpen el hilo, no espera más (y deja la marca de interrumpido). */
    private static boolean pause(double seconds) {
        try {
            Thread.sleep((long) Math.ceil(seconds * 1000));
            return true;
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return false;
        }
    }

    /** El token y la clave sin espacios ni saltos de línea al principio y al final (también los de fuera de ASCII). */
    private static String trimSpace(String s) {
        int a = 0;
        int z = s.length();
        while (a < z && isSpace(s.charAt(a))) a++;
        while (z > a && isSpace(s.charAt(z - 1))) z--;
        return s.substring(a, z);
    }

    private static boolean isSpace(char c) {
        return c <= ' ' || Character.isWhitespace(c) || Character.isSpaceChar(c) || c == '﻿';
    }

    private static String formatSeconds(double s) {
        return s == Math.rint(s) && s < 1e15 ? String.valueOf((long) s) : String.valueOf(s);
    }

    private static String reason(Throwable e) {
        String m = e.getMessage();
        String className = e.getClass().getSimpleName();
        return m == null || m.isEmpty() ? className : className + ": " + m;
    }

    // ---------------------------------------------------------------------------------------------------------- ruta y query

    /**
     * La ruta codificada trozo a trozo (un %XX ya escrito se respeta), sin el /v3 del principio si la base ya lo lleva; los
     * parámetros que traiga tras «?» quedan en params.
     */
    private static String splitPath(String path, boolean stripV3, Map<String, Object> params) {
        int p = path.indexOf('?');
        String pathname = p >= 0 ? path.substring(0, p) : path;
        if (p >= 0) {
            for (String pair : path.substring(p + 1).split("&")) {
                if (pair.isEmpty()) continue;
                int eq = pair.indexOf('=');
                params.put(formDecode(eq >= 0 ? pair.substring(0, eq) : pair), formDecode(eq >= 0 ? pair.substring(eq + 1) : ""));
            }
        }
        pathname = "/" + stripLeadingSlashes(pathname);
        if (stripV3 && (pathname.equals("/v3") || pathname.startsWith("/v3/"))) pathname = "/" + stripLeadingSlashes(pathname.substring(3));
        StringBuilder sb = new StringBuilder();
        String[] segments = pathname.split("/", -1);
        for (int i = 0; i < segments.length; i++) {
            if (i > 0) sb.append('/');
            String t = segments[i];
            int from = 0;
            int k = 0;
            while (k < t.length()) {
                if (t.charAt(k) == '%' && k + 2 < t.length() && hex(t.charAt(k + 1)) && hex(t.charAt(k + 2))) {
                    rfc3986(t.substring(from, k), sb);
                    sb.append(t, k, k + 3);
                    k += 3;
                    from = k;
                } else {
                    k++;
                }
            }
            rfc3986(t.substring(from), sb);
        }
        return sb.toString();
    }

    private static String stripLeadingSlashes(String s) {
        int i = 0;
        while (i < s.length() && s.charAt(i) == '/') i++;
        return s.substring(i);
    }

    private static boolean hex(char c) {
        return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
    }

    /**
     * Un parámetro de la query de la ruta: cada %XX, su byte; lo demás, también el «+», tal cual (RFC 3986: no es un
     * formulario), para que una fecha con zona escrita en la ruta (from_datetime=…+01:00) llegue tal cual.
     */
    private static String formDecode(String s) {
        ByteArrayOutputStream b = new ByteArrayOutputStream();
        int i = 0;
        while (i < s.length()) {
            char c = s.charAt(i);
            if (c == '%' && i + 2 < s.length() && hex(s.charAt(i + 1)) && hex(s.charAt(i + 2))) {
                b.write(Integer.parseInt(s.substring(i + 1, i + 3), 16));
                i += 3;
            } else {
                int n = Character.charCount(s.codePointAt(i));
                byte[] u = s.substring(i, i + n).getBytes(StandardCharsets.UTF_8);
                b.write(u, 0, u.length);
                i += n;
            }
        }
        return new String(b.toByteArray(), StandardCharsets.UTF_8);
    }

    /** La query en RFC 3986 (todo codificado menos A-Z a-z 0-9 - . _ ~): es lo que se firma y lo que se manda. */
    private static String encodeQuery(Map<String, Object> params) {
        StringBuilder sb = new StringBuilder();
        for (Map.Entry<String, Object> e : params.entrySet()) {
            String t = queryValue(e.getValue());
            if (t == null) continue;
            if (sb.length() > 0) sb.append('&');
            rfc3986(e.getKey(), sb);
            sb.append('=');
            rfc3986(t, sb);
        }
        return sb.toString();
    }

    private static final char[] HEX = "0123456789ABCDEF".toCharArray();

    private static void rfc3986(String s, StringBuilder sb) {
        for (byte x : s.getBytes(StandardCharsets.UTF_8)) {
            int c = x & 0xff;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~') {
                sb.append((char) c);
            } else {
                sb.append('%').append(HEX[c >> 4]).append(HEX[c & 15]);
            }
        }
    }

    /** Un valor de la query: true/false, una lista o un array separado por comas; null o una lista vacía (o solo de null), nada. */
    private static String queryValue(Object v) {
        if (v == null) return null;
        if (v instanceof Boolean) return ((Boolean) v) ? "true" : "false";
        List<Object> l = toList(v);
        if (l != null) {
            StringBuilder sb = new StringBuilder();
            boolean any = false;
            for (Object x : l) {
                String t = queryValue(x);
                if (t == null) continue;
                if (any) sb.append(',');
                sb.append(t);
                any = true;
            }
            return any ? sb.toString() : null;
        }
        return String.valueOf(v);
    }

    /** Una lista o un array (también de tipos primitivos) como lista; cualquier otra cosa, null. */
    private static List<Object> toList(Object v) {
        if (v instanceof Iterable) {
            List<Object> l = new ArrayList<Object>();
            for (Object x : (Iterable<?>) v) l.add(x);
            return l;
        }
        if (v.getClass().isArray()) {
            int n = Array.getLength(v);
            List<Object> l = new ArrayList<Object>(n);
            for (int i = 0; i < n; i++) l.add(Array.get(v, i));
            return l;
        }
        return null;
    }

    private static boolean truthy(Object v) {
        if (v == null) return false;
        if (v instanceof Boolean) return (Boolean) v;
        if (v instanceof Number) return ((Number) v).doubleValue() != 0;
        if (v instanceof String) return !((String) v).isEmpty();
        if (v instanceof Collection) return !((Collection<?>) v).isEmpty();
        if (v instanceof Map) return !((Map<?, ?>) v).isEmpty();
        return true;
    }

    /** Un campo de la respuesta como texto: un texto tal cual, null como null y cualquier otra cosa como su JSON. */
    private static String asText(Object v) {
        if (v == null || v instanceof String) return (String) v;
        return v instanceof Number || v instanceof Boolean ? String.valueOf(v) : toJson(v);
    }

    // ------------------------------------------------------------------------------------------------------------------ JSON

    /** El JSON de unos datos, en UTF-8 y sin escapar lo que no hace falta (tildes, eñes, emojis o «/»). */
    static String toJson(Object v) {
        StringBuilder sb = new StringBuilder();
        writeJson(sb, v, 0);
        return sb.toString();
    }

    private static void writeJson(StringBuilder sb, Object v, int depth) {
        if (depth > MAX_DEPTH) throw new IllegalArgumentException("MundoSMS: the body cannot be encoded as JSON: it is nested too deeply (does it contain itself?)");
        if (v == null) {
            sb.append("null");
        } else if (v instanceof CharSequence || v instanceof Character || v instanceof Enum<?>) {
            writeString(sb, v.toString());
        } else if (v instanceof Boolean) {
            sb.append(((Boolean) v) ? "true" : "false");
        } else if (v instanceof Number) {
            sb.append(formatNumber((Number) v));
        } else if (v instanceof Map) {
            sb.append('{');
            boolean first = true;
            for (Map.Entry<?, ?> e : ((Map<?, ?>) v).entrySet()) {
                if (!first) sb.append(',');
                first = false;
                writeString(sb, String.valueOf(e.getKey()));
                sb.append(':');
                writeJson(sb, e.getValue(), depth + 1);
            }
            sb.append('}');
        } else {
            List<Object> l = toList(v);
            if (l == null) {
                throw new IllegalArgumentException("MundoSMS: the body cannot be encoded as JSON: a " + v.getClass().getName()
                                                   + " is not supported (use Map, List, arrays, String, numbers, Boolean or null)");
            }
            sb.append('[');
            for (int i = 0; i < l.size(); i++) {
                if (i > 0) sb.append(',');
                writeJson(sb, l.get(i), depth + 1);
            }
            sb.append(']');
        }
    }

    private static String formatNumber(Number n) {
        if (n instanceof Integer || n instanceof Long || n instanceof Short || n instanceof Byte || n instanceof BigInteger
            || n instanceof AtomicInteger || n instanceof AtomicLong) {
            return n.toString();
        }
        if (n instanceof BigDecimal) return n.toString();
        if (!(n instanceof Double) && !(n instanceof Float)) {
            try {
                return new BigDecimal(n.toString()).toString();
            } catch (NumberFormatException e) {
                // como un double
            }
        }
        double d = n.doubleValue();
        if (Double.isNaN(d) || Double.isInfinite(d)) throw new IllegalArgumentException("MundoSMS: the body cannot be encoded as JSON: " + n + " is not a valid JSON number");
        return n instanceof Float ? n.toString() : String.valueOf(d);
    }

    private static void writeString(StringBuilder sb, String s) {
        sb.append('"');
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"': sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\n': sb.append("\\n"); break;
                case '\r': sb.append("\\r"); break;
                case '\t': sb.append("\\t"); break;
                case '\b': sb.append("\\b"); break;
                case '\f': sb.append("\\f"); break;
                default:
                    boolean lone = Character.isHighSurrogate(c) ? !(i + 1 < s.length() && Character.isLowSurrogate(s.charAt(i + 1)))
                                   : Character.isLowSurrogate(c) && !(i > 0 && Character.isHighSurrogate(s.charAt(i - 1)));
                    if (c < 0x20 || lone) {   // un surrogate suelto no es UTF-8 válido: va escapado
                        sb.append("\\u").append(HEX[(c >> 12) & 15]).append(HEX[(c >> 8) & 15]).append(HEX[(c >> 4) & 15]).append(HEX[c & 15]);
                    } else {
                        sb.append(c);
                    }
            }
        }
        sb.append('"');
    }

    /** El cuerpo como objeto JSON, o null si no lo es (un fichero, un JSON que no es un objeto, o UTF-8 roto). */
    @SuppressWarnings("unchecked")
    private static Map<String, Object> parseJsonObject(byte[] b) {
        int i = b.length >= 3 && (b[0] & 0xff) == 0xEF && (b[1] & 0xff) == 0xBB && (b[2] & 0xff) == 0xBF ? 3 : 0;
        while (i < b.length && (b[i] == ' ' || b[i] == '\t' || b[i] == '\n' || b[i] == '\r')) i++;
        if (i >= b.length || b[i] != '{') return null;
        String s;
        try {
            s = StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
                                      .decode(ByteBuffer.wrap(b, i, b.length - i)).toString();
        } catch (CharacterCodingException e) {
            return null;
        }
        try {
            Object v = new JsonReader(s).parse();
            return v instanceof Map ? (Map<String, Object>) v : null;
        } catch (IllegalArgumentException e) {
            return null;
        }
    }

    /** Un lector de JSON estricto (RFC 8259). */
    private static final class JsonReader {
        private final String s;
        private int i;
        private int depth;

        JsonReader(String s) {
            this.s = s;
        }

        Object parse() {
            Object v = value();
            skipWhitespace();
            if (i != s.length()) throw invalid();
            return v;
        }

        private IllegalArgumentException invalid() {
            return new IllegalArgumentException("invalid JSON at position " + i);
        }

        private void skipWhitespace() {
            while (i < s.length() && (s.charAt(i) == ' ' || s.charAt(i) == '\t' || s.charAt(i) == '\n' || s.charAt(i) == '\r')) i++;
        }

        /** Tras un elemento: true si sigue otro (una coma), false si se cierra con close; cualquier otra cosa no es JSON. */
        private boolean more(char close) {
            skipWhitespace();
            if (i >= s.length()) throw invalid();
            char c = s.charAt(i);
            if (c != ',' && c != close) throw invalid();
            i++;
            return c == ',';
        }

        private Object value() {
            skipWhitespace();
            if (i >= s.length()) throw invalid();
            char c = s.charAt(i);
            if (c == '{') return object();
            if (c == '[') return array();
            if (c == '"') return string();
            if (c == '-' || (c >= '0' && c <= '9')) return number();
            if (s.startsWith("true", i)) { i += 4; return Boolean.TRUE; }
            if (s.startsWith("false", i)) { i += 5; return Boolean.FALSE; }
            if (s.startsWith("null", i)) { i += 4; return null; }
            throw invalid();
        }

        private Map<String, Object> object() {
            if (++depth > MAX_DEPTH) throw invalid();
            i++;
            Map<String, Object> m = new LinkedHashMap<String, Object>();
            skipWhitespace();
            if (i < s.length() && s.charAt(i) == '}') {
                i++;
            } else {
                do {
                    skipWhitespace();
                    if (i >= s.length() || s.charAt(i) != '"') throw invalid();
                    String k = string();
                    skipWhitespace();
                    if (i >= s.length() || s.charAt(i) != ':') throw invalid();
                    i++;
                    m.put(k, value());
                } while (more('}'));
            }
            depth--;
            return m;
        }

        private List<Object> array() {
            if (++depth > MAX_DEPTH) throw invalid();
            i++;
            List<Object> l = new ArrayList<Object>();
            skipWhitespace();
            if (i < s.length() && s.charAt(i) == ']') {
                i++;
            } else {
                do {
                    l.add(value());
                } while (more(']'));
            }
            depth--;
            return l;
        }

        private String string() {
            i++;
            StringBuilder sb = new StringBuilder();
            while (true) {
                if (i >= s.length()) throw invalid();
                char c = s.charAt(i++);
                if (c == '"') return sb.toString();
                if (c < 0x20) throw invalid();
                if (c != '\\') {
                    sb.append(c);
                    continue;
                }
                if (i >= s.length()) throw invalid();
                char e = s.charAt(i++);
                switch (e) {
                    case '"': sb.append('"'); break;
                    case '\\': sb.append('\\'); break;
                    case '/': sb.append('/'); break;
                    case 'b': sb.append('\b'); break;
                    case 'f': sb.append('\f'); break;
                    case 'n': sb.append('\n'); break;
                    case 'r': sb.append('\r'); break;
                    case 't': sb.append('\t'); break;
                    case 'u':
                        if (i + 4 > s.length()) throw invalid();
                        int cp = 0;
                        for (int k = 0; k < 4; k++) {
                            if (!hex(s.charAt(i + k))) throw invalid();
                            cp = cp * 16 + Character.digit(s.charAt(i + k), 16);
                        }
                        sb.append((char) cp);
                        i += 4;
                        break;
                    default:
                        throw invalid();
                }
            }
        }

        private Object number() {
            int start = i;
            if (s.charAt(i) == '-') i++;
            if (i >= s.length()) throw invalid();
            if (s.charAt(i) == '0') {
                i++;
            } else if (s.charAt(i) >= '1' && s.charAt(i) <= '9') {
                while (i < s.length() && isDigit(s.charAt(i))) i++;
            } else {
                throw invalid();
            }
            boolean integer = true;
            if (i < s.length() && s.charAt(i) == '.') {
                integer = false;
                i++;
                digits();
            }
            if (i < s.length() && (s.charAt(i) == 'e' || s.charAt(i) == 'E')) {
                integer = false;
                i++;
                if (i < s.length() && (s.charAt(i) == '+' || s.charAt(i) == '-')) i++;
                digits();
            }
            String t = s.substring(start, i);
            if (!integer) return Double.valueOf(t);
            try {
                return Long.valueOf(t);
            } catch (NumberFormatException e) {
                return new BigInteger(t);
            }
        }

        private void digits() {
            int d = i;
            while (i < s.length() && isDigit(s.charAt(i))) i++;
            if (i == d) throw invalid();
        }

        private static boolean isDigit(char c) {
            return c >= '0' && c <= '9';
        }
    }
}
