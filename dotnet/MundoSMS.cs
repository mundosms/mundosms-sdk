// MundoSMS API v3 — cliente .NET (C#) en un solo fichero. Sin dependencias.
// .NET Framework 4.6.1 o superior, .NET Core 2.0 o superior y .NET 5 o superior; compila con C# 6 o superior.
// Documentación: https://www.mundosms.es/api-v3
// Código y versiones: https://github.com/mundosms/mundosms-sdk (licencia MIT)
//
//   using MundoSMS;
//   var api = new MundoSMSClient("TOKEN", "CLAVE_PRIVADA");   // la clave privada, "" si tu API Key no exige firma
//   var r = await api.PostAsync("/sms", new { to = "34600123456", text = "Hola", from = "MiEmpresa" });
//   Console.WriteLine(r.Data["id"]);
//   foreach (var call in api.All("/calls", new { from_datetime = "2026-11-01", stop_at_ongoing = true })) { ... }
//
// Errores: excepción MundoSMSException con .Http (código HTTP), .Code (la palabra de la API: "not_found", "invalid_parameter"…),
// .Message, .Detail, .Data, .Response (la respuesta entera) y .RequestId (la cabecera X-Request-Id). También cuando la API contesta con "status": "error", venga con el código HTTP que
// venga. Un WhatsApp sin confirmar (202) no es un error: vuelve la respuesta, con data.state "unknown".
// Cada POST lleva una Idempotency-Key (un UUID nuevo en cada llamada, la misma en todos sus intentos; la tuya, con
// api.PostAsync(path, data, new MundoSMSRequestOptions { IdempotencyKey = "pedido-881" })). Por eso un POST que falla por
// la red, o con 409 request_in_progress, se repite sin miedo: la API no lo hace dos veces. Si la respuesta es la guardada de
// la primera vez, r.IdempotentReplayed es true (y r["idempotent_replayed"] también).
// Reintenta: un 429, en cualquier método (no se hizo nada); un fallo de red, en cualquier método; un 5xx, solo en GET (un POST
// con 5xx no se repite: un 500 puede llegar con la operación hecha, y la API lo guarda con la Idempotency-Key, así que
// repetirlo con ella devolvería el mismo 500; un 503 no hizo nada, y puedes repetirlo tú con la misma clave). Espera lo que
// diga Retry-After (como mucho 60 s) o, si no lo trae, 1, 2, 4… s. Un DELETE que se repite tras quedarse sin respuesta y da 404 ya estaba hecho: vuelve
// como éxito, con data.changed false.
// Una redirección (3xx) no se sigue nunca: es una MundoSMSException (la API no redirige, y seguirla llevaría tu firma y tus
// datos a otro servidor); un HttpClient tuyo que las sigue no se acepta. Un 2xx que no es JSON (el HTML de un proxy o de un
// portal cautivo) también es un error; los ficheros que da la API (audio, imagen, PDF) vuelven por su Content-Type.
// Los objetos se mandan con sus nombres de JSON: los de [JsonPropertyName], [JsonProperty] o [DataMember(Name)] y, sin
// atributo, en snake_case (ClientId → client_id); un objeto anónimo (new { client_id = … }), tal cual.
// Los avisos que te mandamos (callback_url y webhooks de WhatsApp) se comprueban con MundoSMSClient.VerifyWebhook().
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
#nullable disable
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
using System.Runtime.CompilerServices;
#endif

namespace MundoSMS
{
    /// <summary>
    /// Un error de la API (una respuesta 4xx o 5xx, o una 2xx con <c>"status": "error"</c>) o la falta de respuesta (la red,
    /// o el plazo agotado: entonces <see cref="Http"/> es 0 y el mensaje empieza por «No response from the API:»).
    /// </summary>
    public class MundoSMSException : Exception
    {
        /// <summary>Crea el error sin respuesta de la API, con la excepción de debajo (la de la red, o la del plazo).</summary>
        public MundoSMSException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>Crea el error. Lo normal es no crearlo tú: lo lanza <see cref="MundoSMSClient"/>.</summary>
        public MundoSMSException(string message, int http = 0, string code = null, string detail = null,
                                 Dictionary<string, object> response = null, Dictionary<string, object> data = null,
                                 string requestId = null)
            : base(message)
        {
            Http = http;
            Code = code;
            Detail = detail;
            Response = response;
            Data = data;
            RequestId = requestId;
        }

        /// <summary>El código HTTP de la respuesta; 0 si no hubo respuesta.</summary>
        public int Http { get; private set; }

        /// <summary>
        /// El <c>code</c> de la API: la palabra que dice qué falló, en inglés y snake_case ("not_found", "invalid_parameter",
        /// "insufficient_credit"…); null si la respuesta no lo trae (sin respuesta, una redirección o una respuesta que no es JSON).
        /// </summary>
        public string Code { get; private set; }

        /// <summary>El <c>detail</c> de la API (qué falló exactamente), o null.</summary>
        public string Detail { get; private set; }

        /// <summary>
        /// El <c>data</c> de la respuesta, o null. En un WhatsApp que WhatsApp rechazó, trae el <c>id</c> del mensaje.
        /// (Oculta a <see cref="Exception.Data"/>, que sigue disponible como <c>((Exception)e).Data</c>.)
        /// </summary>
        public new Dictionary<string, object> Data { get; private set; }

        /// <summary>La cabecera <c>X-Request-Id</c> de la respuesta: con ella encuentras la petición en Logs API.</summary>
        public string RequestId { get; private set; }

        /// <summary>La respuesta entera, decodificada; null si no era JSON.</summary>
        public Dictionary<string, object> Response { get; private set; }
    }

    /// <summary>
    /// La respuesta de la API: el JSON entero como diccionario (<c>status</c>, <c>message</c>, <c>data</c>). Una respuesta que no es JSON (un audio, un PDF, un 204) vuelve con <c>status</c> = "success",
    /// <c>content_type</c> y <c>body</c> (los bytes, también en <see cref="Body"/>).
    /// Los objetos JSON llegan como <c>Dictionary&lt;string, object&gt;</c>, las listas como <c>List&lt;object&gt;</c>, los
    /// enteros como <c>long</c>, los decimales como <c>double</c>, y además <c>string</c>, <c>bool</c> y null.
    /// </summary>
    public sealed class MundoSMSResponse : Dictionary<string, object>
    {
        /// <summary>Una respuesta vacía (las de verdad las crea <see cref="MundoSMSClient"/>).</summary>
        public MundoSMSResponse() : base(StringComparer.Ordinal) { }

        /// <summary>El código HTTP de la respuesta (200, 201, 202, 204…).</summary>
        public int Http { get; internal set; }

        /// <summary>La cabecera <c>Content-Type</c> de la respuesta.</summary>
        public string ContentType { get; internal set; }

        /// <summary>El contenido tal cual cuando la respuesta no es JSON (un fichero); null si lo es.</summary>
        public byte[] Body { get; internal set; }

        /// <summary>La cabecera <c>X-Request-Id</c> de la respuesta.</summary>
        public string RequestId { get; internal set; }

        /// <summary>El <c>data</c> de la respuesta, o null si no trae (o no es un objeto).</summary>
        public Dictionary<string, object> Data
        {
            get { object d; return TryGetValue("data", out d) ? d as Dictionary<string, object> : null; }
        }

        /// <summary>
        /// true si la API devolvió la respuesta guardada de la primera vez (la cabecera <c>Idempotent-Replayed: true</c>, un POST
        /// repetido con la misma <c>Idempotency-Key</c>): no se ha vuelto a hacer. Entonces también va
        /// <c>"idempotent_replayed"</c> = true en el diccionario.
        /// </summary>
        public bool IdempotentReplayed { get; internal set; }
    }

    /// <summary>El cupo que dijo una respuesta: sus cabeceras <c>X-RateLimit-*</c> (<see cref="MundoSMSClient.LastRateLimit"/>).</summary>
    public sealed class MundoSMSRateLimit
    {
        internal MundoSMSRateLimit(long? limit, long? remaining, long? reset)
        {
            Limit = limit;
            Remaining = remaining;
            Reset = reset;
        }

        /// <summary><c>X-RateLimit-Limit</c>: las peticiones por segundo que admite tu clave; null si no lo traía.</summary>
        public long? Limit { get; private set; }

        /// <summary><c>X-RateLimit-Remaining</c>: las que te quedan en este segundo, contada esta; null si no lo traía.</summary>
        public long? Remaining { get; private set; }

        /// <summary><c>X-RateLimit-Reset</c>: los segundos que faltan para que se reponga el cupo; null si no lo traía.</summary>
        public long? Reset { get; private set; }

        /// <summary>El cupo en una línea.</summary>
        public override string ToString()
        {
            return "MundoSMSRateLimit { Limit = " + Limit + ", Remaining = " + Remaining + ", Reset = " + Reset + " }";
        }
    }

    /// <summary>
    /// Opciones de una petición, para <see cref="MundoSMSClient.PostAsync(string, object, MundoSMSRequestOptions, CancellationToken)"/>
    /// y <see cref="MundoSMSClient.RequestAsync(string, string, object, MundoSMSRequestOptions, CancellationToken)"/>:
    /// <c>new MundoSMSRequestOptions { IdempotencyKey = "pedido-881" }</c>.
    /// </summary>
    public sealed class MundoSMSRequestOptions
    {
        private string idempotencyKey;

        /// <summary>
        /// Tu <c>Idempotency-Key</c>, para que repetir este POST no lo vuelva a hacer (por ejemplo, el id de tu pedido): de 1 a 64
        /// caracteres, letras sin tilde, cifras y <c>_ . : -</c>. Sin ella (null), un UUID nuevo en cada llamada. La API la tiene
        /// en cuenta en los POST.
        /// </summary>
        /// <exception cref="ArgumentException">la clave no tiene esa forma</exception>
        public string IdempotencyKey
        {
            get { return idempotencyKey; }
            set { idempotencyKey = value == null ? null : MundoSMSClient.CheckIdempotencyKey(value); }
        }
    }

    /// <summary>
    /// Cliente de la API REST v3 de MundoSMS. Firma cada petición (<c>X-Timestamp</c> en milisegundos y la firma HMAC de tu
    /// API Key) y reintenta solo lo que es seguro repetir. Crea uno y reutilízalo: se puede usar desde varios hilos a la vez
    /// (<see cref="LastCursor"/> y <see cref="LastRateLimit"/> son los del último recorrido y la última respuesta, sean del hilo
    /// que sean).
    /// </summary>
    public class MundoSMSClient
    {
        /// <summary>La versión del SDK.</summary>
        public const string Version = "2.0.0";

        /// <summary>Los segundos que puede alejarse de tu hora la «t» de la firma de un aviso, si no dices otra cosa.</summary>
        public const long WebhookTolerance = 300;

        private const double MaxRetryDelay = 60;   // segundos: lo más que se espera antes de un reintento, aunque Retry-After pida más

        private static readonly Lazy<HttpClient> SharedHttpClient = new Lazy<HttpClient>(CreateHttpClient);
        private static long lastTimestamp;   // el X-Timestamp de la última petición de este proceso (milisegundos)

        private readonly string token;
        private readonly string privateKey;
        private readonly HttpClient httpClient;
        private string baseUrl = "https://api.mundosms.es/v3";
        private int retries = 3;
        private TimeSpan timeout = TimeSpan.FromSeconds(60);

        /// <summary>Crea el cliente.</summary>
        /// <param name="token">el Token de tu API Key (empieza por eyJ)</param>
        /// <param name="privateKey">su clave privada si la API Key exige firma (lo normal); "" si no</param>
        /// <param name="httpClient">
        /// opcional: tu propio <see cref="HttpClient"/> (por ejemplo, de <c>IHttpClientFactory</c>), que NO siga redirecciones:
        /// creado con <c>AllowAutoRedirect = false</c> en su manejador (si las sigue, <see cref="ArgumentException"/>). Sin él,
        /// todos los clientes comparten uno. El plazo de cada petición lo pone <see cref="Timeout"/>; el <c>Timeout</c> del tuyo
        /// también cuenta, si es menor.
        /// </param>
        /// <exception cref="ArgumentException">tu <see cref="HttpClient"/> sigue redirecciones</exception>
        public MundoSMSClient(string token, string privateKey = "", HttpClient httpClient = null)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));
            // una redirección llevaría a otro servidor la firma, la Idempotency-Key y, con un 307, el cuerpo: no se sigue nunca
            if (httpClient != null && FollowsRedirects(httpClient) == true)
                throw new ArgumentException("MundoSMS: your HttpClient follows redirects, and the SDK never follows them (they would take the " +
                                            "signature and the body to another server): create its handler with AllowAutoRedirect = false", nameof(httpClient));
            this.token = token.Trim();
            this.privateKey = (privateKey ?? "").Trim();
            this.httpClient = httpClient ?? SharedHttpClient.Value;
        }

        /// <summary>
        /// Si el <see cref="HttpClient"/> sigue redirecciones: el <c>AllowAutoRedirect</c> del manejador de debajo (pasando por los
        /// <see cref="DelegatingHandler"/> de <c>IHttpClientFactory</c>). null si no se sabe; entonces lo vigila cada respuesta.
        /// </summary>
        private static bool? FollowsRedirects(HttpMessageInvoker client)
        {
            try
            {
                HttpMessageHandler handler = null;
                foreach (string name in new[] { "_handler", "handler" })
                {
                    var field = typeof(HttpMessageInvoker).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                    if (field == null) continue;
                    handler = field.GetValue(client) as HttpMessageHandler;
                    break;
                }
                for (int depth = 0; handler is DelegatingHandler && depth < 64; depth++) handler = ((DelegatingHandler)handler).InnerHandler;
                if (handler == null) return null;
                var property = handler.GetType().GetProperty("AllowAutoRedirect", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(bool)) return null;
                return (bool)property.GetValue(handler, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>La dirección de la API. Por defecto, https://api.mundosms.es/v3.</summary>
        public string BaseUrl
        {
            get { return baseUrl; }
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                string v = value.Trim().TrimEnd('/');
                Uri u;
                if (!Uri.TryCreate(v, UriKind.Absolute, out u) || (u.Scheme != "https" && u.Scheme != "http") || u.Query.Length > 0 || u.Fragment.Length > 0)
                    throw new ArgumentException("MundoSMS: BaseUrl must be an http(s) address without query, like https://api.mundosms.es/v3", nameof(value));
                baseUrl = v;
            }
        }

        /// <summary>Cuántas veces se reintenta lo que es seguro repetir (0, ninguna). Por defecto, 3.</summary>
        public int Retries
        {
            get { return retries; }
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "MundoSMS: Retries cannot be negative");
                retries = value;
            }
        }

        /// <summary>
        /// Lo que puede durar cada petición entera (conectar, mandar y recibir la respuesta). Por defecto, 60 s;
        /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>, sin límite.
        /// </summary>
        public TimeSpan Timeout
        {
            get { return timeout; }
            set
            {
                if (value <= TimeSpan.Zero && value != System.Threading.Timeout.InfiniteTimeSpan)
                    throw new ArgumentOutOfRangeException(nameof(value), "MundoSMS: Timeout must be greater than zero");
                timeout = value;
            }
        }

        /// <summary>
        /// El cursor del último listado recorrido con <see cref="All"/>: guárdalo y pásalo como <c>cursor</c> la próxima vez para
        /// pedir solo lo nuevo. Al empezar el recorrido, el <c>cursor</c> que le pasas (o null); después, el último
        /// <c>next_cursor</c> que llegó (una página sin cursor no lo borra). Uno por cliente: para dos recorridos a la vez, un
        /// cliente para cada uno.
        /// </summary>
        public string LastCursor { get; private set; }

        /// <summary>
        /// El cupo que dijo la última respuesta (sus cabeceras <c>X-RateLimit-*</c>), o null si no lo traía (o no hubo
        /// respuesta). Con él, un bucle propio no se pasa de las 10 peticiones por segundo.
        /// </summary>
        public MundoSMSRateLimit LastRateLimit { get; private set; }

        // ---- asíncronos ----

        /// <summary>GET: los filtros van en la query. Devuelve la respuesta entera.</summary>
        /// <param name="path">"/calls", "/numbers/34910123456"… (sin /v3: si lo lleva, se quita)</param>
        /// <param name="query">un diccionario o un objeto anónimo (<c>new { type = "in", limit = 50 }</c>), o null</param>
        /// <param name="cancellationToken">para cancelar la petición (y la espera de un reintento)</param>
        public Task<MundoSMSResponse> GetAsync(string path, object query = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("GET", path, query, cancellationToken);
        }

        /// <summary>POST: los datos van en un cuerpo JSON. Devuelve la respuesta entera.</summary>
        public Task<MundoSMSResponse> PostAsync(string path, object data = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("POST", path, data, null, cancellationToken);
        }

        /// <summary>POST con tus opciones (tu <c>Idempotency-Key</c>): los datos van en un cuerpo JSON. Devuelve la respuesta entera.</summary>
        /// <param name="path">"/sms", "/whatsapp/messages"… (sin /v3)</param>
        /// <param name="data">un diccionario o un objeto anónimo, o null</param>
        /// <param name="options"><c>new MundoSMSRequestOptions { IdempotencyKey = "pedido-881" }</c>, o null</param>
        /// <param name="cancellationToken">para cancelar la petición (y la espera de un reintento)</param>
        public Task<MundoSMSResponse> PostAsync(string path, object data, MundoSMSRequestOptions options,
                                                CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("POST", path, data, options, cancellationToken);
        }

        /// <summary>PATCH: los datos van en un cuerpo JSON. Devuelve la respuesta entera.</summary>
        public Task<MundoSMSResponse> PatchAsync(string path, object data, CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("PATCH", path, data, cancellationToken);
        }

        /// <summary>DELETE: los filtros van en la query. Devuelve la respuesta entera.</summary>
        public Task<MundoSMSResponse> DeleteAsync(string path, object query = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("DELETE", path, query, cancellationToken);
        }

        // ---- síncronos ----

        /// <summary>Como <see cref="GetAsync"/>, esperando la respuesta.</summary>
        public MundoSMSResponse Get(string path, object query = null) { return Request("GET", path, query); }

        /// <summary>Como <see cref="PostAsync(string, object, CancellationToken)"/>, esperando la respuesta.</summary>
        public MundoSMSResponse Post(string path, object data = null) { return Request("POST", path, data); }

        /// <summary>Como <see cref="PostAsync(string, object, MundoSMSRequestOptions, CancellationToken)"/>, esperando la respuesta.</summary>
        public MundoSMSResponse Post(string path, object data, MundoSMSRequestOptions options) { return Request("POST", path, data, options); }

        /// <summary>Como <see cref="PatchAsync"/>, esperando la respuesta.</summary>
        public MundoSMSResponse Patch(string path, object data) { return Request("PATCH", path, data); }

        /// <summary>Como <see cref="DeleteAsync"/>, esperando la respuesta.</summary>
        public MundoSMSResponse Delete(string path, object query = null) { return Request("DELETE", path, query); }

        /// <summary>Como <see cref="RequestAsync(string, string, object, CancellationToken)"/>, esperando la respuesta.</summary>
        public MundoSMSResponse Request(string method, string path, object data = null)
        {
            return Request(method, path, data, null);
        }

        /// <summary>Como <see cref="RequestAsync(string, string, object, MundoSMSRequestOptions, CancellationToken)"/>, esperando la respuesta.</summary>
        public MundoSMSResponse Request(string method, string path, object data, MundoSMSRequestOptions options)
        {
            // En un hilo del grupo: así no se bloquea nunca con el contexto de sincronización de una aplicación de escritorio
            // o de ASP.NET clásico.
            return Task.Run(() => RequestAsync(method, path, data, options, CancellationToken.None)).GetAwaiter().GetResult();
        }

        // ---- listados ----

        /// <summary>
        /// Recorre un listado entero, página a página, siguiendo su cursor (<c>next_cursor</c>), y da los elementos uno a uno
        /// (se piden a medida que avanzas). Cada página lleva los mismos filtros (y el mismo <c>order</c>) más el cursor. Al
        /// acabar, el último <c>next_cursor</c> queda en <see cref="LastCursor"/>, que avanza cuando ya te ha dado todos los
        /// elementos de una página. Termina con <c>has_more</c> false (con <c>stop_at_ongoing</c>, también cuando la página se
        /// para en una llamada en curso) o si el cursor no avanza: una página vacía con <c>has_more</c> true no es el final.
        /// Una lista que no va por páginas
        /// (<c>has_more</c> sin <c>next_cursor</c>: <c>/calls/missed</c>, <c>/calls/live</c>) es una
        /// <see cref="MundoSMSException"/>, antes de ningún elemento: sube <c>limit</c>, o filtra, y pídela con <see cref="Get"/>.
        /// </summary>
        public IEnumerable<Dictionary<string, object>> All(string path, object query = null)
        {
            return AllPages(path, Pairs(query, "query"));
        }

        private IEnumerable<Dictionary<string, object>> AllPages(string path, Dictionary<string, object> query)
        {
            query = new Dictionary<string, object>(query, StringComparer.Ordinal);
            LastCursor = StartCursor(query);
            string listKey = null;
            while (true)
            {
                var r = Get(path, query);
                var data = r.Data ?? new Dictionary<string, object>();
                CheckPaginated(path, r, data);
                if (listKey == null) listKey = FirstList(data);
                foreach (var item in Items(data, listKey)) yield return item;
                object next;
                if (!NextPage(data, query, out next)) yield break;
                query["cursor"] = next;
            }
        }

#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        /// <summary>Como <see cref="All"/>, con <c>await foreach</c>.</summary>
        public IAsyncEnumerable<Dictionary<string, object>> AllAsync(string path, object query = null,
                                                                     CancellationToken cancellationToken = default(CancellationToken))
        {
            return AllPagesAsync(path, Pairs(query, "query"), cancellationToken);
        }

        private async IAsyncEnumerable<Dictionary<string, object>> AllPagesAsync(string path, Dictionary<string, object> query,
                                                                                 [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            query = new Dictionary<string, object>(query, StringComparer.Ordinal);
            LastCursor = StartCursor(query);
            string listKey = null;
            while (true)
            {
                var r = await GetAsync(path, query, cancellationToken).ConfigureAwait(false);
                var data = r.Data ?? new Dictionary<string, object>();
                CheckPaginated(path, r, data);
                if (listKey == null) listKey = FirstList(data);
                foreach (var item in Items(data, listKey)) yield return item;
                object next;
                if (!NextPage(data, query, out next)) yield break;
                query["cursor"] = next;
            }
        }
#endif

        /// <summary>El cursor de un recorrido al empezar: el que le pasas, o null.</summary>
        private static string StartCursor(Dictionary<string, object> query)
        {
            object start;
            string s = query.TryGetValue("cursor", out start) && start != null ? Convert.ToString(start, CultureInfo.InvariantCulture) : null;
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>Una lista que no va por páginas (has_more sin next_cursor) no se puede recorrer entera.</summary>
        private static void CheckPaginated(string path, MundoSMSResponse r, Dictionary<string, object> data)
        {
            object hasMore, results, cursor;
            data.TryGetValue("has_more", out hasMore);
            data.TryGetValue("results", out results);
            data.TryGetValue("next_cursor", out cursor);
            if (!Truthy(hasMore) || !Truthy(results) || Truthy(cursor)) return;
            throw new MundoSMSException("MundoSMS: all() cannot go through " + path + ": it has more (has_more) but no next_cursor, so the list " +
                                        "is not paginated: raise limit or narrow the filters, and read it with Get", r.Http,
                                        null, null, r, data, r.RequestId);
        }

        /// <summary>La lista de una página: el primer campo de <c>data</c> que es una lista (calls, dids…).</summary>
        private static string FirstList(Dictionary<string, object> data)
        {
            foreach (var kv in data) if (kv.Value is List<object>) return kv.Key;
            return null;
        }

        private static IEnumerable<Dictionary<string, object>> Items(Dictionary<string, object> data, string listKey)
        {
            object v;
            var list = (listKey != null && data.TryGetValue(listKey, out v)) ? v as List<object> : null;
            if (list == null) yield break;
            foreach (var element in list)
            {
                var item = element as Dictionary<string, object>;
                if (item == null)
                    throw new InvalidOperationException("MundoSMS: the list \"" + listKey + "\" does not hold objects; read it with Get");
                yield return item;
            }
        }

        /// <summary>Guarda el cursor (cuando ya se han dado todos los elementos de la página) y dice si hay otra: has_more, un
        /// next_cursor que avanza y sin llamada en curso (ongoing_call). Una página vacía con has_more true no es el final.</summary>
        private bool NextPage(Dictionary<string, object> data, Dictionary<string, object> query, out object cursor)
        {
            object hasMore, ongoing, sent;
            data.TryGetValue("next_cursor", out cursor);
            data.TryGetValue("has_more", out hasMore);
            data.TryGetValue("ongoing_call", out ongoing);
            query.TryGetValue("cursor", out sent);
            string c = Truthy(cursor) ? Convert.ToString(cursor, CultureInfo.InvariantCulture) : null;
            if (c != null) LastCursor = c;
            return Truthy(hasMore) && c != null && !(sent != null && c == Convert.ToString(sent, CultureInfo.InvariantCulture)) && !Truthy(ongoing);
        }

        private static bool Truthy(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            if (v is string) return ((string)v).Length > 0;
            if (v is long) return (long)v != 0;
            if (v is double) return (double)v != 0 && !double.IsNaN((double)v);
            return true;
        }

        // ---- la petición ----

        /// <summary>
        /// Una petición cualquiera. En GET y DELETE, <paramref name="data"/> va en la query: <c>true</c>/<c>false</c>, una
        /// lista separada por comas, y null y las listas vacías no se mandan. En los demás, en un cuerpo JSON. Si la ruta trae
        /// "?…", esos parámetros se juntan con los de <paramref name="data"/> (gana <paramref name="data"/>).
        /// </summary>
        /// <param name="method">GET, POST, PATCH o DELETE</param>
        /// <param name="path">"/calls", "/numbers/34910123456"… (sin /v3: si lo lleva, se quita)</param>
        /// <param name="data">un diccionario o un objeto anónimo, o null</param>
        /// <param name="cancellationToken">para cancelar la petición (y la espera de un reintento)</param>
        /// <exception cref="MundoSMSException">la API contestó con un error, o no contestó</exception>
        public Task<MundoSMSResponse> RequestAsync(string method, string path, object data = null,
                                                   CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync(method, path, data, null, cancellationToken);
        }

        /// <summary>
        /// Una petición cualquiera, con tus opciones (tu <c>Idempotency-Key</c>; sin ella, en un POST, un UUID nuevo). Ver
        /// <see cref="RequestAsync(string, string, object, CancellationToken)"/>.
        /// </summary>
        /// <param name="method">GET, POST, PATCH o DELETE</param>
        /// <param name="path">"/calls", "/numbers/34910123456"… (sin /v3: si lo lleva, se quita)</param>
        /// <param name="data">un diccionario o un objeto anónimo, o null</param>
        /// <param name="options"><c>new MundoSMSRequestOptions { IdempotencyKey = "pedido-881" }</c>, o null</param>
        /// <param name="cancellationToken">para cancelar la petición (y la espera de un reintento)</param>
        /// <exception cref="MundoSMSException">la API contestó con un error, o no contestó</exception>
        public async Task<MundoSMSResponse> RequestAsync(string method, string path, object data, MundoSMSRequestOptions options,
                                                         CancellationToken cancellationToken = default(CancellationToken))
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            method = method.Trim().ToUpperInvariant();
            string ownKey = options == null ? null : options.IdempotencyKey;
            string idempotencyKey = ownKey != null ? CheckIdempotencyKey(ownKey) : method == "POST" ? Guid.NewGuid().ToString("D") : null;
            EnableTls12();
            string baseAddress = BaseUrl;
            var baseUri = new Uri(baseAddress, UriKind.Absolute);
            string encodedPath;
            Dictionary<string, object> parameters;
            SplitPath(path, baseUri.AbsolutePath.EndsWith("/v3", StringComparison.Ordinal), out encodedPath, out parameters);
            byte[] body = null;
            if (method == "GET" || method == "DELETE")
            {
                foreach (var kv in Pairs(data, "query")) parameters[kv.Key] = kv.Value;
            }
            else
            {
                string json = JsonBody(data);
                if (json != null) body = new UTF8Encoding(false).GetBytes(json);
            }
            string query = BuildQuery(parameters);
            var uri = new Uri(baseAddress + encodedPath + (query.Length > 0 ? "?" + query : ""), UriKind.Absolute);
            string signedPath = uri.PathAndQuery;   // lo que se firma es justo lo que se manda
            var httpMethod = new HttpMethod(method);
            TimeSpan limit = timeout;
            bool afterNetwork = false;   // un intento de antes se quedó sin respuesta (o llegó dos veces): puede que se hiciera
            for (int attempt = 1; ; attempt++)
            {
                int httpStatus = 0;
                string contentType = "", retryAfter = null, requestId = null, networkError = null, location = null, redirectedTo = null;
                bool replayed = false;
                byte[] responseBody = null;
                Exception networkException = null;
                MundoSMSRateLimit rateLimit = null;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (var request = new HttpRequestMessage(httpMethod, uri))
                {
                    if (limit != System.Threading.Timeout.InfiniteTimeSpan) cts.CancelAfter(limit);
                    // dos peticiones iguales a la vez, o un reintento inmediato, no firman igual
                    string ts = Timestamp();
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    request.Headers.TryAddWithoutValidation("X-Timestamp", ts);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", "MundoSMS-DotNet/" + Version);
                    request.Headers.ExpectContinue = false;
                    if (idempotencyKey != null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);   // la misma en todos los intentos
                    if (privateKey.Length > 0) request.Headers.TryAddWithoutValidation("X-Signature", Sign(ts, method, signedPath, body));
                    if (body != null)
                    {
                        request.Content = new ByteArrayContent(body);
                        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
                    }
                    HttpResponseMessage response = null;
                    try
                    {
                        response = await WithDeadline(httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token), cts.Token).ConfigureAwait(false);
                        httpStatus = (int)response.StatusCode;
                        contentType = GetHeader(response, "Content-Type") ?? "";
                        retryAfter = GetHeader(response, "Retry-After");
                        requestId = GetHeader(response, "X-Request-Id");
                        replayed = string.Equals((GetHeader(response, "Idempotent-Replayed") ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase);
                        location = GetHeader(response, "Location");
                        rateLimit = ReadRateLimit(response);
                        // un HttpClient que siguió una redirección (uno que no se pudo comprobar al crear el cliente): lo de otro
                        // servidor no se toma por la respuesta de la API
                        if (response.RequestMessage != null && response.RequestMessage.RequestUri != null && !uri.Equals(response.RequestMessage.RequestUri))
                            redirectedTo = response.RequestMessage.RequestUri.AbsoluteUri;
                        responseBody = await WithDeadline(ReadAll(response.Content, cts.Token), cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (IsNetworkError(e) || cts.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();   // la cancelaste tú
                        networkError = cts.IsCancellationRequested ? "timed out after " + Seconds(limit) + " s"
                                     : e is OperationCanceledException ? "timed out (the HttpClient's Timeout)"
                                     : NetworkErrorReason(e);
                        networkException = e;
                    }
                    finally
                    {
                        if (response != null) response.Dispose();
                    }
                }
                if (networkError != null) httpStatus = 0;   // se cortó a medias: es un fallo de red aunque llegara la cabecera
                bool redirect = networkError == null && (redirectedTo != null || (httpStatus >= 300 && httpStatus < 400));
                object parsed;
                var obj = networkError == null && !redirect && Json.TryParse(responseBody, out parsed) ? parsed as Dictionary<string, object> : null;
                if (!redirect && IsRetryable(method, networkError != null, httpStatus, obj, idempotencyKey) && attempt <= retries)
                {
                    afterNetwork = afterNetwork || networkError != null || IsDuplicated(httpStatus, obj);
                    await Task.Delay(RetryDelay(retryAfter, attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (networkError != null)
                {
                    LastRateLimit = null;
                    throw new MundoSMSException("No response from the API: " + networkError, networkException);
                }
                LastRateLimit = rateLimit;
                if (redirect)   // no se sigue: llevaría la firma y los datos a otro servidor
                {
                    string to = (redirectedTo ?? location ?? "").Trim();
                    if (to.Length > 300) to = to.Substring(0, 300);
                    throw new MundoSMSException("HTTP " + httpStatus.ToString(CultureInfo.InvariantCulture) + ": redirect" + (to.Length > 0 ? " to " + to : "") +
                                                " not followed (the API does not redirect: check BaseUrl" +
                                                (redirectedTo != null ? ", and create your HttpClient with AllowAutoRedirect = false" : "") + ")",
                                                httpStatus, null, null, null, null, requestId);
                }
                if (replayed && obj != null) obj["idempotent_replayed"] = true;   // la respuesta guardada de la primera vez
                if (httpStatus >= 200 && httpStatus < 300)
                {
                    var result = new MundoSMSResponse { Http = httpStatus, ContentType = contentType, RequestId = requestId, IdempotentReplayed = replayed };
                    if (obj == null)   // un fichero, o algo que no es de la API
                    {
                        string type = contentType.Split(';')[0].Trim();
                        if (responseBody != null && responseBody.Length > 0 && !IsFileType(type))
                            throw new MundoSMSException("HTTP " + httpStatus.ToString(CultureInfo.InvariantCulture) + ": the response is not JSON (" +
                                                        (type.Length > 0 ? type : "no Content-Type") + ")", httpStatus, null, null, null, null, requestId);
                        result.Body = responseBody;
                        result["status"] = "success";
                        result["content_type"] = contentType;
                        result["body"] = responseBody;
                        if (replayed) result["idempotent_replayed"] = true;
                        return result;
                    }
                    object status;
                    if (!(obj.TryGetValue("status", out status) && "error".Equals(status)))
                    {
                        foreach (var kv in obj) result[kv.Key] = kv.Value;
                        return result;
                    }
                }
                if (method == "DELETE" && afterNetwork && httpStatus == 404 && obj != null && ApiCode(obj) != "route_not_found")
                {
                    // lo borró el intento que se quedó sin respuesta (Http es el 404 de la API)
                    var deleted = new MundoSMSResponse { Http = httpStatus, ContentType = contentType, RequestId = requestId };
                    deleted["status"] = "success";
                    deleted["message"] = "Already deleted";
                    deleted["data"] = new Dictionary<string, object>(StringComparer.Ordinal) { { "state", "deleted" }, { "changed", false } };
                    return deleted;
                }
                if (obj != null)
                {
                    object messageValue, detailValue, dataValue;
                    obj.TryGetValue("message", out messageValue);
                    obj.TryGetValue("detail", out detailValue);
                    obj.TryGetValue("data", out dataValue);
                    throw new MundoSMSException(messageValue == null ? "Error" : ToText(messageValue), httpStatus, ApiCode(obj),
                                                detailValue == null ? null : ToText(detailValue), obj,
                                                dataValue as Dictionary<string, object>, requestId);
                }
                throw new MundoSMSException("HTTP " + httpStatus.ToString(CultureInfo.InvariantCulture), httpStatus, null, null, null, null, requestId);
            }
        }

        /// <summary>
        /// Si se puede repetir: un 429 en cualquier método; un GET, ante un fallo de red o un 5xx; un PATCH o un DELETE, ante un
        /// fallo de red; un POST (con su Idempotency-Key), ante un fallo de red o un 409 request_in_progress. Un 5xx de un POST
        /// no: un 500 puede haberse hecho y la API lo guarda con la clave (repetirlo devolvería el mismo 500), y un 503 no hizo
        /// nada, pero repetirlo con la misma clave se deja a tu código. Un 409 duplicated_request (la MISMA petición firmada
        /// llegó dos veces: la repitió algo entre medias, como hace HttpClient con una conexión reutilizada que se cerró) es un
        /// fallo de red más.
        /// </summary>
        private static bool IsRetryable(string method, bool network, int httpStatus, Dictionary<string, object> json, string idempotencyKey)
        {
            if (httpStatus == 429) return true;
            network = network || IsDuplicated(httpStatus, json);
            if (method == "GET") return network || httpStatus >= 500;
            if (method == "PATCH" || method == "DELETE") return network;
            if (idempotencyKey != null) return network || (httpStatus == 409 && ApiCode(json) == "request_in_progress");
            return false;
        }

        /// <summary>409 duplicated_request: la MISMA petición firmada llegó dos veces (la repitió algo entre medias).</summary>
        private static bool IsDuplicated(int httpStatus, Dictionary<string, object> json)
        {
            return httpStatus == 409 && ApiCode(json) == "duplicated_request";
        }

        /// <summary>El <c>code</c> de una respuesta de error ("duplicated_request"…) como texto, o null si no lo trae.</summary>
        private static string ApiCode(Dictionary<string, object> json)
        {
            object c;
            return json != null && json.TryGetValue("code", out c) && c != null ? ToText(c) : null;
        }

        /// <summary>Lo que la API da como fichero (grabaciones, locuciones, diagramas y el PDF de un certificado), por su Content-Type.</summary>
        private static bool IsFileType(string type)
        {
            string t = type.ToLowerInvariant();
            return t.StartsWith("audio/", StringComparison.Ordinal) || t.StartsWith("image/", StringComparison.Ordinal)
                || t == "application/pdf" || t == "application/octet-stream";
        }

        /// <summary>El cupo de la respuesta (X-RateLimit-Limit, -Remaining y -Reset), o null si no lo trae.</summary>
        private static MundoSMSRateLimit ReadRateLimit(HttpResponseMessage response)
        {
            var values = new long?[3];
            bool any = false;
            string[] names = { "X-RateLimit-Limit", "X-RateLimit-Remaining", "X-RateLimit-Reset" };
            for (int i = 0; i < 3; i++)
            {
                long n;
                string v = GetHeader(response, names[i]);
                if (v != null && long.TryParse(v.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n))
                {
                    values[i] = n;
                    any = true;
                }
            }
            return any ? new MundoSMSRateLimit(values[0], values[1], values[2]) : null;
        }

        /// <summary>La Idempotency-Key, si tiene la forma que exige la API (de 1 a 64: A-Z a-z 0-9 _ . : -).</summary>
        internal static string CheckIdempotencyKey(string key)
        {
            bool valid = key.Length >= 1 && key.Length <= 64;
            for (int i = 0; valid && i < key.Length; i++)
            {
                char c = key[i];
                valid = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == ':' || c == '-';
            }
            if (!valid)
                throw new ArgumentException("MundoSMS: IdempotencyKey must be 1 to 64 characters: A-Z, a-z, 0-9 and _ . : - (nothing was sent)");
            return key;
        }

        // ---- avisos ----

        /// <summary>Como <see cref="VerifyWebhook(string, string, byte[], long)"/>, con el cuerpo como texto (se firma en UTF-8).</summary>
        public static bool VerifyWebhook(string secret, string header, string body, long tolerance = WebhookTolerance)
        {
            return VerifyWebhook(secret, header, body == null ? null : new UTF8Encoding(false).GetBytes(body), tolerance);
        }

        /// <summary>
        /// true si un aviso que te llega (<c>callback_url</c> o webhook de WhatsApp) es nuestro y nadie lo ha tocado: la cabecera
        /// <c>X-MundoSMS-Signature: t=&lt;segundos&gt;,v1=&lt;firma&gt;</c>, con <c>v1</c> = HMAC-SHA256 en hexadecimal, con tu
        /// secreto, de <c>t + "." + cuerpo</c>, y <c>t</c> a no más de <paramref name="tolerance"/> segundos de tu hora. Compara
        /// en tiempo constante. Una cabecera que falta o está mal formada da false, sin excepción.
        /// </summary>
        /// <param name="secret">el de «API y Soporte → Webhooks» (whsec_…)</param>
        /// <param name="header">la cabecera X-MundoSMS-Signature</param>
        /// <param name="body">el cuerpo TAL CUAL llegó, en bytes (sin volver a serializar el JSON)</param>
        /// <param name="tolerance">los segundos que puede alejarse «t» de tu hora (300)</param>
        public static bool VerifyWebhook(string secret, string header, byte[] body, long tolerance = WebhookTolerance)
        {
            string key = (secret ?? "").Trim();
            if (key.Length == 0 || header == null || body == null) return false;
            string t = null;
            var signatures = new List<string>();
            foreach (string part in header.Split(','))
            {
                int p = part.IndexOf('=');
                if (p < 0) continue;
                string k = part.Substring(0, p).Trim(), v = part.Substring(p + 1).Trim();
                if (k == "t")
                {
                    if (t != null) return false;   // dos «t»: no se sabe cuál vale
                    t = v;
                }
                else if (k == "v1")
                {
                    signatures.Add(v.ToLowerInvariant());
                }
            }
            if (t == null || t.Length == 0 || t.Length > 15 || signatures.Count == 0) return false;
            foreach (char c in t) if (c < '0' || c > '9') return false;
            if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(t, CultureInfo.InvariantCulture)) > tolerance) return false;
            var utf8 = new UTF8Encoding(false);
            byte[] prefix = utf8.GetBytes(t + ".");
            byte[] payload = new byte[prefix.Length + body.Length];
            Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
            Buffer.BlockCopy(body, 0, payload, prefix.Length, body.Length);
            byte[] hash;
            using (var hmac = new HMACSHA256(utf8.GetBytes(key))) hash = hmac.ComputeHash(payload);
            var sb = new StringBuilder(64);
            foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            byte[] expected = Encoding.ASCII.GetBytes(sb.ToString());
            bool valid = false;
            foreach (string signature in signatures)   // todas, sin cortar en la primera: la comparación es de tiempo constante
            {
                if (FixedTimeEquals(expected, utf8.GetBytes(signature))) valid = true;
            }
            return valid;
        }

        /// <summary>
        /// El X-Timestamp de una petición: milisegundos, y siempre mayor que el de la anterior de este proceso (también desde
        /// varios hilos). Dos peticiones iguales en el mismo milisegundo (dos a la vez, o un reintento tras un 429 con
        /// Retry-After: 0) firmarían igual, y la API rechazaría la segunda por repetida («409 duplicated_request»).
        /// </summary>
        private static string Timestamp()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            while (true)
            {
                long previous = Interlocked.Read(ref lastTimestamp);
                long next = Math.Max(now, previous + 1);
                if (Interlocked.CompareExchange(ref lastTimestamp, next, previous) == previous)
                    return next.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Compara en tiempo constante: CryptographicOperations.FixedTimeEquals donde está y, en .NET Framework y .NET
        /// Standard 2.0, que no lo tienen, lo mismo a mano (sin optimizar, para que no corte en el primer byte distinto).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining
                                                    | System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            return CryptographicOperations.FixedTimeEquals(a, b);
#else
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
#endif
        }

        private string Sign(string ts, string method, string signedPath, byte[] body)
        {
            var utf8 = new UTF8Encoding(false);
            byte[] prefix = utf8.GetBytes(token + "." + ts + "." + method + " " + signedPath + ".");
            byte[] payload = new byte[prefix.Length + (body == null ? 0 : body.Length)];
            Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
            if (body != null) Buffer.BlockCopy(body, 0, payload, prefix.Length, body.Length);
            using (var hmac = new HMACSHA256(utf8.GetBytes(privateKey)))
            {
                return Convert.ToBase64String(hmac.ComputeHash(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
        }

        private static async Task<byte[]> ReadAll(HttpContent content, CancellationToken cancellationToken)
        {
            if (content == null) return Array.Empty<byte>();
            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Espera a la tarea, pero no más allá del plazo: en .NET Framework, una lectura del socket no siempre atiende a la
        /// cancelación, y una respuesta que llega poco a poco no acabaría nunca.
        /// </summary>
        private static async Task<T> WithDeadline<T>(Task<T> task, CancellationToken cancellationToken)
        {
            if (!task.IsCompleted && cancellationToken.CanBeCanceled)
            {
                var deadline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state).TrySetResult(true), deadline))
                {
                    if (await Task.WhenAny(task, deadline.Task).ConfigureAwait(false) != task)
                    {
                        Observe(task);
                        throw new OperationCanceledException(cancellationToken);
                    }
                }
            }
            return await task.ConfigureAwait(false);
        }

        /// <summary>Que el error de una tarea abandonada no quede sin observar.</summary>
        private static void Observe(Task task)
        {
            task.ContinueWith(t => t.Exception, CancellationToken.None,
                              TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static bool IsNetworkError(Exception e)
        {
            return e is HttpRequestException || e is IOException || e is WebException || e is SocketException
                || e is OperationCanceledException;
        }

        /// <summary>El motivo de un fallo de red, con sus causas (una HttpRequestException suele traerla dentro).</summary>
        private static string NetworkErrorReason(Exception e)
        {
            var sb = new StringBuilder(e.Message);
            for (var inner = e.InnerException; inner != null; inner = inner.InnerException)
            {
                if (!string.IsNullOrEmpty(inner.Message) && sb.ToString().IndexOf(inner.Message, StringComparison.Ordinal) < 0)
                    sb.Append(": ").Append(inner.Message);
            }
            return sb.ToString();
        }

        private static string GetHeader(HttpResponseMessage response, string name)
        {
            IEnumerable<string> values;
            if (response.Headers.TryGetValues(name, out values) || (response.Content != null && response.Content.Headers.TryGetValues(name, out values)))
                return string.Join(", ", values);
            return null;
        }

        /// <summary>Antes del siguiente intento: lo que pida Retry-After (segundos o fecha HTTP), o 1, 2, 4… s. Como mucho 60.</summary>
        private static TimeSpan RetryDelay(string retryAfter, int attempt)
        {
            double seconds = double.NaN;
            if (!string.IsNullOrEmpty(retryAfter))
            {
                string v = retryAfter.Trim();
                DateTimeOffset date;
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                {
                    seconds = DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)
                        ? (date - DateTimeOffset.UtcNow).TotalSeconds : double.NaN;
                }
            }
            if (double.IsNaN(seconds)) seconds = Math.Pow(2, attempt - 1);   // sin cabecera, o no se entiende
            return TimeSpan.FromSeconds(Math.Max(0, Math.Min(MaxRetryDelay, seconds)));
        }

        private static string Seconds(TimeSpan t)
        {
            return t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static HttpClient CreateHttpClient()
        {
            HttpMessageHandler handler = null;
#if NET5_0_OR_GREATER
            if (SocketsHttpHandler.IsSupported)
#endif
#if NETCOREAPP2_1_OR_GREATER
            {
                handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)   // el cliente es de larga vida: que un cambio de DNS se note
                };
            }
#endif
            if (handler == null) handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            return new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };   // el plazo lo pone cada petición
        }

        /// <summary>
        /// La API exige TLS 1.2. En .NET Framework, si la aplicación fijó <c>ServicePointManager.SecurityProtocol</c> sin él
        /// (código antiguo que pone solo <c>Tls</c>, o una aplicación compilada para 4.5), se añade sin quitar nada. Si lo elige
        /// el sistema (<c>SystemDefault</c>, lo normal desde 4.7), no se toca; en .NET Core y .NET 5 o superior, tampoco.
        /// </summary>
        private static void EnableTls12()
        {
#if !NETCOREAPP
            try
            {
                var protocols = ServicePointManager.SecurityProtocol;
                if (protocols != 0 && (protocols & SecurityProtocolType.Tls12) == 0) ServicePointManager.SecurityProtocol = protocols | SecurityProtocolType.Tls12;
            }
            catch (NotSupportedException)
            {
            }
#endif
        }

        // ---- ruta, query y cuerpo ----

        /// <summary>
        /// La ruta codificada trozo a trozo (un %XX ya escrito se respeta), sin el /v3 del principio si la base ya lo lleva,
        /// y los parámetros que traiga tras «?».
        /// </summary>
        private static void SplitPath(string path, bool stripV3, out string encodedPath, out Dictionary<string, object> parameters)
        {
            path = path ?? "";
            parameters = new Dictionary<string, object>(StringComparer.Ordinal);
            int q = path.IndexOf('?');
            if (q >= 0)
            {
                foreach (string pair in path.Substring(q + 1).Split('&'))
                {
                    if (pair.Length == 0) continue;
                    int i = pair.IndexOf('=');
                    parameters[DecodeQueryPart(i >= 0 ? pair.Substring(0, i) : pair)] = DecodeQueryPart(i >= 0 ? pair.Substring(i + 1) : "");
                }
                path = path.Substring(0, q);
            }
            string clean = "/" + path.TrimStart('/');
            if (stripV3 && (clean == "/v3" || clean.StartsWith("/v3/", StringComparison.Ordinal))) clean = "/" + clean.Substring(3).TrimStart('/');
            var segments = clean.Split('/');
            for (int i = 0; i < segments.Length; i++) segments[i] = Encode(segments[i], true);
            encodedPath = string.Join("/", segments);
        }

        /// <summary>
        /// Un trozo de la query de la ruta: cada %XX, su byte; lo demás, también el «+», tal cual (RFC 3986: no es un
        /// formulario), para que una fecha con zona escrita en la ruta (from_datetime=…+01:00) llegue tal cual.
        /// </summary>
        private static string DecodeQueryPart(string s)
        {
            return Uri.UnescapeDataString(s);
        }

        /// <summary>La query en RFC 3986 (todo codificado menos A-Z a-z 0-9 - . _ ~): es lo que se firma y lo que se manda.</summary>
        private static string BuildQuery(Dictionary<string, object> parameters)
        {
            var sb = new StringBuilder();
            foreach (var kv in parameters)
            {
                string text = QueryValue(kv.Value);
                if (text == null) continue;
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Encode(kv.Key, false)).Append('=').Append(Encode(text, false));
            }
            return sb.ToString();
        }

        /// <summary>RFC 3986: todo codificado (en UTF-8) menos A-Z a-z 0-9 - . _ ~; con keepPercent, un %XX ya escrito se deja.</summary>
        private static string Encode(string s, bool keepPercent)
        {
            var sb = new StringBuilder(s.Length);
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (keepPercent && s[i] == '%' && i + 2 < s.Length && IsHex(s[i + 1]) && IsHex(s[i + 2]))
                {
                    // Un %XX ya escrito se deja, en su forma normal (RFC 3986, 6.2.2): el de una letra, una cifra o - . _ ~
                    // pasa a ese carácter, y los demás van en mayúsculas. Es lo que hace Uri en .NET de todos modos; así, lo
                    // que se firma y lo que sale son lo mismo en cualquier versión.
                    AppendUtf8(sb, s.Substring(start, i - start));
                    AppendByte(sb, Convert.ToByte(s.Substring(i + 1, 2), 16));
                    i += 2;
                    start = i + 1;
                }
            }
            AppendUtf8(sb, s.Substring(start));
            return sb.ToString();
        }

        private static void AppendUtf8(StringBuilder sb, string part)
        {
            foreach (byte b in new UTF8Encoding(false).GetBytes(part)) AppendByte(sb, b);
        }

        private static void AppendByte(StringBuilder sb, byte b)
        {
            char c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
        }

        /// <summary>Un valor de la query: true/false, una lista separada por comas; null o una lista vacía (o solo de null), nada.</summary>
        private static string QueryValue(object v)
        {
            if (v == null) return null;
            if (v is string) return (string)v;
            if (v is IDictionary || IsKeyValuePairs(v))
                throw new ArgumentException("MundoSMS: a query value cannot be an object (only strings, numbers, true/false or lists)");
            var list = v as IEnumerable;
            if (list != null)
            {
                var parts = new List<string>();
                foreach (var item in list)
                {
                    string text = QueryValue(item);
                    if (text != null) parts.Add(text);
                }
                return parts.Count > 0 ? string.Join(",", parts) : null;
            }
            if (!Json.IsScalar(v))
                throw new ArgumentException("MundoSMS: a query value cannot be an object (only strings, numbers, true/false or lists)");
            return ToText(v);
        }

        /// <summary>Un escalar como texto: true/false, los números con punto decimal, cualquier otra cosa con su ToString.</summary>
        private static string ToText(object v)
        {
            if (v is string) return (string)v;
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is float) return ((float)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is DateTime || v is DateTimeOffset)
                throw new ArgumentException("MundoSMS: pass dates as text: \"2026-11-01 08:00:00\" (Madrid time) or ISO 8601 with a time zone");
            if (v is Dictionary<string, object> || v is List<object>) return Json.Write(v);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>El cuerpo JSON de un POST o un PATCH; null si no hay datos (o están vacíos).</summary>
        private static string JsonBody(object data)
        {
            if (data == null) return null;
            if (data is string || Json.IsScalar(data))
                throw new ArgumentException("MundoSMS: data must be an object: a dictionary or an anonymous object (new { to = \"…\" })");
            if (!(data is IDictionary) && !IsKeyValuePairs(data) && data is IEnumerable)
                return HasAny((IEnumerable)data) ? Json.Write(data) : null;   // una lista: solo si trae algo
            var pairs = Pairs(data, "data");
            return pairs.Count > 0 ? Json.Write(pairs) : null;
        }

        private static bool HasAny(IEnumerable list)
        {
            var e = list.GetEnumerator();
            try
            {
                return e.MoveNext();
            }
            finally
            {
                var disposable = e as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
        }

        private static bool IsKeyValuePairs(object v)
        {
            return v is IEnumerable<KeyValuePair<string, object>>;
        }

        /// <summary>Los pares nombre-valor de un diccionario o de un objeto (sus propiedades públicas), en su orden.</summary>
        internal static Dictionary<string, object> Pairs(object data, string what)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (data == null) return result;
            var kvs = data as IEnumerable<KeyValuePair<string, object>>;
            if (kvs != null)
            {
                foreach (var kv in kvs) result[kv.Key] = kv.Value;
                return result;
            }
            var dict = data as IDictionary;
            if (dict != null)
            {
                foreach (DictionaryEntry entry in dict) result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = entry.Value;
                return result;
            }
            if (data is string || data is IEnumerable || Json.IsScalar(data))
                throw new ArgumentException("MundoSMS: " + what + " must be a dictionary or an anonymous object (new { type = \"in\" })");
            foreach (var kv in Json.Members(data)) result[kv.Key] = kv.Value;
            return result;
        }
    }

    /// <summary>JSON mínimo: lo justo para la API (objetos, listas, textos, números, true/false y null).</summary>
    internal static class Json
    {
        private const int MaxDepth = 256;

        internal static bool TryParse(byte[] bytes, out object value)
        {
            value = null;
            if (bytes == null || bytes.Length == 0) return false;
            string s = new UTF8Encoding(false).GetString(bytes);
            if (s.Length > 0 && s[0] == '﻿') s = s.Substring(1);
            try
            {
                var reader = new Reader(s);
                reader.SkipWhitespace();
                value = reader.ReadValue(0);
                reader.SkipWhitespace();
                if (reader.Position != s.Length)
                {
                    value = null;
                    return false;
                }
                return true;
            }
            catch (FormatException)
            {
                value = null;
                return false;
            }
        }

        private sealed class Reader
        {
            private readonly string s;
            internal int Position;

            internal Reader(string s) { this.s = s; }

            internal void SkipWhitespace()
            {
                while (Position < s.Length && (s[Position] == ' ' || s[Position] == '\t' || s[Position] == '\n' || s[Position] == '\r')) Position++;
            }

            private char Current { get { if (Position >= s.Length) throw new FormatException(); return s[Position]; } }

            private void Expect(char c)
            {
                if (Current != c) throw new FormatException();
                Position++;
            }

            internal object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw new FormatException();
                char c = Current;
                if (c == '{')
                {
                    Position++;
                    var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                    SkipWhitespace();
                    if (Current == '}') { Position++; return obj; }
                    while (true)
                    {
                        SkipWhitespace();
                        if (Current != '"') throw new FormatException();
                        string key = ReadString();
                        SkipWhitespace();
                        Expect(':');
                        SkipWhitespace();
                        obj[key] = ReadValue(depth + 1);
                        SkipWhitespace();
                        if (Current == ',') { Position++; continue; }
                        Expect('}');
                        return obj;
                    }
                }
                if (c == '[')
                {
                    Position++;
                    var list = new List<object>();
                    SkipWhitespace();
                    if (Current == ']') { Position++; return list; }
                    while (true)
                    {
                        SkipWhitespace();
                        list.Add(ReadValue(depth + 1));
                        SkipWhitespace();
                        if (Current == ',') { Position++; continue; }
                        Expect(']');
                        return list;
                    }
                }
                if (c == '"') return ReadString();
                if (ReadLiteral("true")) return true;
                if (ReadLiteral("false")) return false;
                if (ReadLiteral("null")) return null;
                return ReadNumber();
            }

            private bool ReadLiteral(string text)
            {
                if (string.CompareOrdinal(s, Position, text, 0, text.Length) != 0) return false;
                Position += text.Length;
                return true;
            }

            private string ReadString()
            {
                Position++;   // la comilla
                var sb = new StringBuilder();
                while (true)
                {
                    char c = Current;
                    Position++;
                    if (c == '"') return sb.ToString();
                    if (c < 0x20) throw new FormatException();
                    if (c != '\\') { sb.Append(c); continue; }
                    char escape = Current;
                    Position++;
                    switch (escape)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Position + 4 > s.Length) throw new FormatException();
                            int code;
                            if (!int.TryParse(s.Substring(Position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                throw new FormatException();
                            sb.Append((char)code);
                            Position += 4;
                            break;
                        default: throw new FormatException();
                    }
                }
            }

            private object ReadNumber()
            {
                int start = Position;
                bool isInteger = true;
                if (Position < s.Length && s[Position] == '-') Position++;
                if (Position < s.Length && s[Position] == '0') Position++;
                else if (!ReadDigits()) throw new FormatException();
                if (Position < s.Length && s[Position] == '.')
                {
                    Position++;
                    isInteger = false;
                    if (!ReadDigits()) throw new FormatException();
                }
                if (Position < s.Length && (s[Position] == 'e' || s[Position] == 'E'))
                {
                    Position++;
                    isInteger = false;
                    if (Position < s.Length && (s[Position] == '+' || s[Position] == '-')) Position++;
                    if (!ReadDigits()) throw new FormatException();
                }
                string text = s.Substring(start, Position - start);
                long asLong;
                if (isInteger && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out asLong)) return asLong;
                double asDouble;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out asDouble)) return asDouble;
                return text[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;   // fuera de rango en .NET Framework
            }

            private bool ReadDigits()
            {
                int start = Position;
                while (Position < s.Length && s[Position] >= '0' && s[Position] <= '9') Position++;
                return Position > start;
            }
        }

        internal static string Write(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        internal static bool IsScalar(object v)
        {
            return v is bool || v is char || v is Enum || v is Guid || v is DateTime || v is DateTimeOffset || v is TimeSpan
                || v is byte || v is sbyte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong
                || v is float || v is double || v is decimal;
        }

        internal static IEnumerable<PropertyInfo> Properties(Type type)
        {
            foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                if (p.CanRead && p.GetIndexParameters().Length == 0) yield return p;
        }

        /// <summary>
        /// Las propiedades públicas de un objeto con su nombre en el JSON: el de [JsonPropertyName] (System.Text.Json),
        /// [JsonProperty] (Newtonsoft) o [DataMember(Name)]; sin él, en snake_case (ClientId → client_id), salvo en un objeto
        /// anónimo, que ya lleva los nombres de la API. Sin las de [JsonIgnore] o [IgnoreDataMember] y, en una clase con
        /// [DataContract], solo las de [DataMember]. Los atributos se buscan por su nombre: sin depender de esas bibliotecas.
        /// </summary>
        internal static IEnumerable<KeyValuePair<string, object>> Members(object data)
        {
            var type = data.GetType();
            bool anonymous = type.Name.Contains("AnonymousType") && type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false);
            bool contract = FindAttribute(type, "DataContractAttribute") != null;
            foreach (var p in Properties(type))
            {
                object value = p.GetValue(data, null);
                if (Ignored(p, value) || FindAttribute(p, "IgnoreDataMemberAttribute") != null) continue;
                var dataMember = FindAttribute(p, "DataMemberAttribute");
                if (contract && dataMember == null) continue;
                string name = AttributeText(FindAttribute(p, "JsonPropertyNameAttribute"), "Name")
                              ?? AttributeText(FindAttribute(p, "JsonPropertyAttribute"), "PropertyName")
                              ?? AttributeText(dataMember, "Name");
                yield return new KeyValuePair<string, object>(name ?? (anonymous ? p.Name : SnakeCase(p.Name)), value);
            }
        }

        /// <summary>[JsonIgnore]: el de Newtonsoft siempre; el de System.Text.Json, según su Condition.</summary>
        private static bool Ignored(PropertyInfo p, object value)
        {
            var a = FindAttribute(p, "JsonIgnoreAttribute");
            if (a == null) return false;
            var c = a.GetType().GetProperty("Condition");
            string condition = c == null ? "Always" : Convert.ToString(c.GetValue(a, null), CultureInfo.InvariantCulture);
            if (condition == "Never") return false;
            if (condition == "WhenWritingNull") return value == null;
            if (condition == "WhenWritingDefault") return value == null || (value.GetType().IsValueType && value.Equals(Activator.CreateInstance(value.GetType())));
            return true;
        }

        private static object FindAttribute(MemberInfo member, string name)
        {
            foreach (var a in member.GetCustomAttributes(true)) if (a.GetType().Name == name) return a;
            return null;
        }

        private static string AttributeText(object attribute, string property)
        {
            if (attribute == null) return null;
            var p = attribute.GetType().GetProperty(property);
            string s = p == null ? null : p.GetValue(attribute, null) as string;
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>ClientId → client_id, SMSText → sms_text, Url2 → url2 (lo que ya va en minúsculas, tal cual).</summary>
        internal static string SnakeCase(string name)
        {
            var sb = new StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c))
                {
                    bool afterLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                    bool acronymEnd = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                    if ((afterLower || acronymEnd) && sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v, int depth)
        {
            if (depth > 64) throw new ArgumentException("MundoSMS: the data is nested too deeply (an object that contains itself?)");
            if (v == null) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null) { WriteString(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is char || v is Enum || v is Guid) { WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); return; }
            if (v is DateTime || v is DateTimeOffset || v is TimeSpan)
                throw new ArgumentException("MundoSMS: pass dates as text: \"2026-11-01 08:00:00\" (Madrid time) or ISO 8601 with a time zone");
            if (v is double || v is float)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException("MundoSMS: a number cannot be NaN or infinite");
                sb.Append(v is float ? ((float)v).ToString("R", CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (IsScalar(v)) { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }   // enteros y decimal
            if (v is IDictionary || v is IEnumerable<KeyValuePair<string, object>>)
            {
                WriteObject(sb, MundoSMSClient.Pairs(v, "data"), depth);
                return;
            }
            var list = v as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }
            var obj = new Dictionary<string, object>(StringComparer.Ordinal);   // un objeto: sus propiedades públicas, con su nombre de JSON
            foreach (var kv in Members(v)) obj[kv.Key] = kv.Value;
            WriteObject(sb, obj, depth);
        }

        private static void WriteObject(StringBuilder sb, Dictionary<string, object> obj, int depth)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in obj)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                Write(sb, kv.Value, depth + 1);
            }
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); continue;
                    case '\\': sb.Append("\\\\"); continue;
                    case '\b': sb.Append("\\b"); continue;
                    case '\f': sb.Append("\\f"); continue;
                    case '\n': sb.Append("\\n"); continue;
                    case '\r': sb.Append("\\r"); continue;
                    case '\t': sb.Append("\\t"); continue;
                }
                if (c < 0x20)
                {
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(c).Append(s[i + 1]);   // un emoji: el par entero
                    i++;
                }
                else if (char.IsSurrogate(c))
                {
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));   // medio par suelto: no es UTF-8
                }
                else
                {
                    sb.Append(c);
                }
            }
            sb.Append('"');
        }
    }
}
