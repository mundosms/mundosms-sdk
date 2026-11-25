// Pruebas del cliente C# (MundoSMS.cs), sin red: un servidor de prueba local (TcpListener) que comprueba la firma como la API y
// contesta lo que pide cada caso. Sin paquetes. dotnet run -c Release; el código de salida es el número de fallos.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MundoSMS;

public class Peticion
{
    public string Metodo, Ruta;
    public Dictionary<string, string> Cabeceras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public byte[] Cuerpo;
    public bool Firma;
    public string Texto { get { return Encoding.UTF8.GetString(Cuerpo); } }
}

/// <summary>Una respuesta del servidor de prueba; null es «corta sin contestar».</summary>
public class Respuesta
{
    public int Estado;
    public string Tipo = "application/json";
    public byte[] Cuerpo = new byte[0];
    public Dictionary<string, string> Cabeceras = new Dictionary<string, string>();
    public bool Lento;

    public static Respuesta Json(int estado, string cuerpo) { return new Respuesta { Estado = estado, Cuerpo = Encoding.UTF8.GetBytes(cuerpo) }; }
    public static Respuesta De(int estado, string tipo, string cuerpo) { return new Respuesta { Estado = estado, Tipo = tipo, Cuerpo = Encoding.UTF8.GetBytes(cuerpo) }; }
    public Respuesta Con(string k, string v) { Cabeceras[k] = v; return this; }
}

/// <summary>
/// Apunta cada petición (con su firma comprobada) y contesta con responde(n, petición). La misma petición firmada que llega dos
/// veces (el mismo X-Timestamp: la repite el propio HttpClient con una conexión que se cerró) da 409 duplicated_request, como
/// la API con «Rechazar peticiones repetidas».
/// </summary>
public sealed class Servidor : IDisposable
{
    public readonly List<Peticion> Peticiones = new List<Peticion>();
    private readonly HashSet<string> vistas = new HashSet<string>();
    private readonly TcpListener srv;
    private readonly Func<int, Peticion, Respuesta> responde;
    public readonly string Base;

    public Servidor(Func<int, Peticion, Respuesta> responde)
    {
        this.responde = responde;
        srv = new TcpListener(IPAddress.Loopback, 0);
        srv.Start();
        Base = "http://127.0.0.1:" + ((IPEndPoint)srv.LocalEndpoint).Port + "/v3";
        Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await srv.AcceptTcpClientAsync(); } catch (Exception) { return; }
                _ = Task.Run(() => Atiende(c));
            }
        });
    }

    private static string Linea(Stream s)
    {
        var b = new MemoryStream();
        for (int x; (x = s.ReadByte()) != -1 && x != '\n';) if (x != '\r') b.WriteByte((byte)x);
        return Encoding.UTF8.GetString(b.ToArray());
    }

    private void Atiende(TcpClient c)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var p = new Peticion();
                var l = Linea(s).Split(' ');
                p.Metodo = l[0];
                p.Ruta = l[1];
                for (string h; (h = Linea(s)).Length > 0;) { int i = h.IndexOf(':'); p.Cabeceras[h.Substring(0, i).Trim()] = h.Substring(i + 1).Trim(); }
                string largo;
                int n = p.Cabeceras.TryGetValue("Content-Length", out largo) ? int.Parse(largo) : 0;
                p.Cuerpo = new byte[n];
                for (int leido = 0; leido < n;) leido += s.Read(p.Cuerpo, leido, n - leido);
                string auth, ts, firma;
                p.Cabeceras.TryGetValue("Authorization", out auth);
                p.Cabeceras.TryGetValue("X-Timestamp", out ts);
                p.Cabeceras.TryGetValue("X-Signature", out firma);
                p.Firma = firma == Pruebas.Firma(Pruebas.Clave, (auth ?? "       ").Substring(7), ts, p.Metodo, p.Ruta, p.Cuerpo);
                Respuesta r;
                lock (Peticiones)
                {
                    Peticiones.Add(p);
                    r = vistas.Add(p.Ruta + " " + ts) ? responde(Peticiones.Count, p)
                        : Respuesta.Json(409, "{\"status\":\"error\",\"code\":\"duplicated_request\",\"message\":\"Duplicated request\"}");
                }
                if (r == null) return;   // corta sin contestar
                var sb = new StringBuilder("HTTP/1.1 " + r.Estado + " X\r\n");
                if (r.Tipo != null) sb.Append("Content-Type: ").Append(r.Tipo).Append("\r\n");
                foreach (var kv in r.Cabeceras) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
                sb.Append("Content-Length: ").Append(r.Cuerpo.Length).Append("\r\nConnection: close\r\n\r\n");
                var cab = Encoding.UTF8.GetBytes(sb.ToString());
                s.Write(cab, 0, cab.Length);
                s.Flush();
                if (r.Lento) Thread.Sleep(1500);
                s.Write(r.Cuerpo, 0, r.Cuerpo.Length);
            }
            catch (Exception)
            {
            }
        }
    }

    public List<string> Rutas() { lock (Peticiones) return Peticiones.Select(x => x.Ruta).ToList(); }

    public void Dispose() { srv.Stop(); }
}

/// <summary>Un objeto propio para el cuerpo, con los atributos de siempre en .NET.</summary>
public class SmsDto
{
    [JsonPropertyName("to")] public string Destino { get; set; }
    public string ClientId { get; set; }
    public string CallbackURL { get; set; }
    [DataMember(Name = "text")] public string Texto { get; set; }
    [JsonIgnore] public string Interno { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Opcional { get; set; }
}

[DataContract]
public class ContratoDto
{
    [DataMember(Name = "to")] public string Destino { get; set; }
    [DataMember] public string TextoLargo { get; set; }
    public string NoVa { get; set; }
}

public class Paso : DelegatingHandler
{
}

public static class Pruebas
{
    public const string Token = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiIsInN1YiI6IjcifQ.cHJ1ZWJh";
    public const string Clave = "Pr4ebaClaveDe20Letra";
    const string Ok = "{\"status\":\"success\",\"message\":\"OK\",\"data\":{\"id\":8812345}}";
    static int fallos;

    static void Bien(bool c, string que)
    {
        if (!c) fallos++;
        Console.WriteLine((c ? "  ok    " : "  FALLA ") + que);
    }

    /// <summary>La firma de «La firma» de la documentación: base64url, sin relleno, del HMAC-SHA256.</summary>
    public static string Firma(string clave, string token, string ts, string metodo, string ruta, byte[] cuerpo)
    {
        var prefijo = Encoding.UTF8.GetBytes(token + "." + ts + "." + metodo + " " + ruta + ".");
        using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(clave)))
            return Convert.ToBase64String(h.ComputeHash(prefijo.Concat(cuerpo ?? new byte[0]).ToArray())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static MundoSMSClient Cliente(Servidor s, int reintentos = 3, double plazo = 60)
    {
        return new MundoSMSClient(Token, Clave) { BaseUrl = s.Base, Retries = reintentos, Timeout = TimeSpan.FromSeconds(plazo) };
    }

    /// <summary>La respuesta de f o, si salta una MundoSMSException, una con status «error …»: el caso cuenta como fallo y las pruebas siguen.</summary>
    static MundoSMSResponse EnMarcha(Func<MundoSMSResponse> f)
    {
        try { return f(); }
        catch (MundoSMSException e) { var r = new MundoSMSResponse(); r["status"] = "error " + e.Http + " " + e.Code; return r; }
    }

    static MundoSMSException Error(Action f)
    {
        try { f(); } catch (MundoSMSException e) { return e; }
        return null;
    }

    public static int Main()
    {
        Console.WriteLine("== la firma: los ejemplos de la documentación y lo que manda el SDK");
        string tk = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiJ9.ZmlybWE";
        Bien(Firma("A1b2C3d4E5f6G7h8I9j0", tk, "1795400000123", "GET", "/v3/calls?type=in&limit=5", null) == "uE09uuJV7e3hSSW2NZIJQ9jMWlT15lMETj-sbrIjoFo",
             "el ejemplo GET de «Para comprobar tu código»");
        Bien(Firma("A1b2C3d4E5f6G7h8I9j0", tk, "1795400000123", "POST", "/v3/sms", Encoding.UTF8.GetBytes("{\"to\":\"34600123456\",\"text\":\"Hola\"}"))
             == "_62bUnSBUjpWSsAMT580JFfk1Yhx9bCZhS805swJ2to", "el ejemplo POST");
        using (var s = new Servidor((n, p) => Respuesta.Json(200, Ok)))
        {
            var api = Cliente(s);
            api.Get("/calls", new { type = "in", limit = 5 });
            api.Post("/sms", new { to = "34600123456", text = "Señor Núñez: 9,50 €" });
            Peticion g = s.Peticiones[0], p = s.Peticiones[1];
            Bien(g.Ruta == "/v3/calls?type=in&limit=5" && g.Cuerpo.Length == 0 && p.Ruta == "/v3/sms", "la ruta y la query, tal cual se firman");
            Bien(p.Texto == "{\"to\":\"34600123456\",\"text\":\"Señor Núñez: 9,50 €\"}", "el cuerpo, en UTF-8 sin escapar");
            Bien(g.Firma && p.Firma && Regex.IsMatch(g.Cabeceras["X-Timestamp"], "^[0-9]{13}$"), "firmadas, con X-Timestamp en milisegundos");
            Bien(p.Cabeceras.ContainsKey("Idempotency-Key") && !g.Cabeceras.ContainsKey("Idempotency-Key"), "Idempotency-Key en el POST y no en el GET");
        }

        Console.WriteLine("== un aviso: el ejemplo de whsec_ejemplo");
        string cuerpo = "{\"id\":\"evt_prueba\",\"type\":\"sms.status\",\"data\":{\"to\":\"34600123456\",\"state\":\"delivered\"}}";
        string cab = "t=1795161606,v1=d43d40dfd88dd4aba4633580b2786f8651936516aecc2967868edf333ddfdae5";
        Bien(MundoSMSClient.VerifyWebhook("whsec_ejemplo", cab, Encoding.UTF8.GetBytes(cuerpo), long.MaxValue / 2), "con su secreto, vale");
        Bien(!MundoSMSClient.VerifyWebhook("whsec_otro", cab, cuerpo, long.MaxValue / 2), "con otro secreto, no");
        Bien(!MundoSMSClient.VerifyWebhook("whsec_ejemplo", cab, cuerpo.Replace("delivered", "Delivered"), long.MaxValue / 2), "con un byte cambiado, no");
        Bien(!MundoSMSClient.VerifyWebhook("whsec_ejemplo", cab, cuerpo), "de hace más de 5 minutos, no");

        Console.WriteLine("== reintentos y plazo");
        using (var s = new Servidor((n, p) => n < 3 ? Respuesta.Json(429, "{\"status\":\"error\",\"code\":\"requests_per_second\",\"message\":\"Too many requests\"}").Con("Retry-After", "0") : Respuesta.Json(200, Ok)))
            Bien((string)Cliente(s).Get("/calls")["status"] == "success" && s.Peticiones.Count == 3, "un 429 se repite");
        using (var s = new Servidor((n, p) => Respuesta.Json(500, "{\"status\":\"error\",\"code\":\"internal_error\",\"message\":\"Internal error\"}")))
        {
            var e = Error(() => Cliente(s).Post("/sms", new { to = "1" }));
            Bien(e != null && e.Http == 500 && e.Code == "internal_error" && s.Peticiones.Count == 1, "un POST con 5xx no se repite");
        }
        // un 409 se decide por el code, no por el message: duplicated_request (cualquier método) y request_in_progress (un POST con su
        // clave) se repiten; otro 409, aunque su message diga lo mismo, no (el POST con request_in_progress, con Retry-After de una
        // hora: más de lo que se espera, así que no se repite; lo que espera, en «409 request_in_progress»)
        string[][] casos409 = { new[] { "GET", "duplicated_request", "Duplicated request", "3", "0" }, new[] { "PATCH", "duplicated_request", "Duplicated request", "3", "0" },
                                new[] { "POST", "duplicated_request", "Duplicated request", "3", "0" }, new[] { "POST", "request_in_progress", "Request in progress", "1", "3600" },
                                new[] { "PATCH", "request_in_progress", "Request in progress", "1", "0" }, new[] { "POST", "already_exists", "Request in progress", "1", "0" } };
        foreach (var c in casos409)
        {
            string b = "{\"status\":\"error\",\"code\":\"" + c[1] + "\",\"message\":\"" + c[2] + "\"}";
            using (var s = new Servidor((n, p) => Respuesta.Json(409, b).Con("Retry-After", c[4])))
            {
                var e = Error(() => Cliente(s, 2).Request(c[0], "/x", new { a = 1 }));
                Bien(e != null && e.Http == 409 && e.Code == c[1] && s.Peticiones.Count == int.Parse(c[3]), "409 " + c[1] + " en " + c[0] + ": " + c[3] + " petición(es)");
            }
        }

        Console.WriteLine("== 409 request_in_progress: se espera por tiempo, con la misma clave (2.1.0)");
        // un corte (gasta el único reintento de Retries = 1) y tres 409 con Retry-After: 0 (se espera 1 s), y a la quinta, la respuesta
        string enMarcha = "{\"status\":\"error\",\"code\":\"request_in_progress\",\"message\":\"Request in progress\"}";
        using (var s = new Servidor((n, p) => n == 1 ? null : n < 5 ? Respuesta.Json(409, enMarcha).Con("Retry-After", "0") : Respuesta.Json(200, Ok)))
        {
            var t0 = DateTime.UtcNow;
            var r = EnMarcha(() => Cliente(s, 1).Post("/whatsapp/messages", new { to = "1" }));
            double t = (DateTime.UtcNow - t0).TotalSeconds;
            List<Peticion> ps;
            lock (s.Peticiones) ps = s.Peticiones.ToList();
            Bien((string)r["status"] == "success" && ps.Count == 5 && ps.Select(x => x.Cabeceras.ContainsKey("Idempotency-Key") ? x.Cabeceras["Idempotency-Key"] : null).Distinct().Count() == 1
                 && ps[0].Cabeceras.ContainsKey("Idempotency-Key") && ps.Select(x => x.Cabeceras["X-Timestamp"]).Distinct().Count() == 5 && t >= 3 && t < 8,
                 "un corte y tres 409 con Retries 1: 5 peticiones con la misma clave y la respuesta (" + t.ToString("0.0", CultureInfo.InvariantCulture) + " s)");
        }
        using (var s = new Servidor((n, p) => n < 3 ? Respuesta.Json(409, enMarcha) : Respuesta.Json(200, Ok)))
        {
            var t0 = DateTime.UtcNow;
            var r = EnMarcha(() => Cliente(s, 1).Post("/sms", new { to = "1" }));
            double t = (DateTime.UtcNow - t0).TotalSeconds;
            Bien((string)r["status"] == "success" && s.Peticiones.Count == 3 && t >= 3 && t < 6,
                 "sin Retry-After: 1 y 2 s (" + t.ToString("0.0", CultureInfo.InvariantCulture) + " s)");
        }
        using (var s = new Servidor((n, p) => Respuesta.Json(409, enMarcha).Con("Retry-After", "3600")))
        {
            var t0 = DateTime.UtcNow;
            var e = Error(() => Cliente(s, 1).Post("/sms", new { to = "1" }, new MundoSMSRequestOptions { IdempotencyKey = "pedido-881" }));
            double t = (DateTime.UtcNow - t0).TotalSeconds;
            Bien(e != null && e.Http == 409 && e.Code == "request_in_progress" && e.IdempotencyKey == "pedido-881" && s.Peticiones.Count == 1 && t < 1,
                 "Retry-After pide más de lo que queda: no espera, y el error lleva la clave (" + t.ToString("0.0", CultureInfo.InvariantCulture) + " s)");
        }
        using (var s = new Servidor((n, p) => Respuesta.Json(409, enMarcha).Con("Retry-After", "0")))
        {
            var e = Error(() => Cliente(s, 0).Post("/sms", new { to = "1" }));
            Bien(e != null && s.Peticiones.Count == 1 && e.IdempotencyKey != null && e.IdempotencyKey == s.Peticiones[0].Cabeceras["Idempotency-Key"],
                 "con Retries 0, ninguno; y la clave puesta por el SDK, en el error");
        }

        Console.WriteLine("== los campos del error");
        Respuesta[] errs = { Respuesta.Json(404, "{\"status\":\"error\",\"code\":\"not_found\",\"message\":\"Call not found\",\"detail\":\"no call 999\"}").Con("X-Request-Id", "req-1"),
                             Respuesta.Json(200, "{\"status\":\"error\",\"code\":\"whatsapp_rejected\",\"message\":\"WhatsApp rejected it\",\"data\":{\"id\":\"wam-1\"}}"),
                             Respuesta.De(502, "text/html", "<html>Bad gateway</html>"),
                             Respuesta.Json(202, "{\"status\":\"success\",\"message\":\"Not confirmed\",\"data\":{\"id\":\"wam-2\",\"state\":\"unknown\"}}") };
        using (var s = new Servidor((n, p) => errs[n - 1]))
        {
            var api = Cliente(s, 0);
            var e = Error(() => api.Get("/calls/out/999"));
            Bien(e != null && e.Http == 404 && e.Code == "not_found" && e.Message == "Call not found" && e.Detail == "no call 999" && e.RequestId == "req-1"
                 && (string)e.Response["status"] == "error" && typeof(MundoSMSException).GetProperty("StatusCode") == null,
                 "un 404: Http, Code, Message, Detail, RequestId y Response");
            Bien(e != null && e.IdempotencyKey == null, "un GET no lleva Idempotency-Key: null en el error");
            var e2 = Error(() => api.Post("/whatsapp/messages", new { to = "1" }));
            Bien(e2 != null && e2.Http == 200 && e2.Code == "whatsapp_rejected" && (string)e2.Data["id"] == "wam-1", "un 200 con status error es un error, con su Code");
            Bien(e2 != null && e2.IdempotencyKey != null && Regex.IsMatch(e2.IdempotencyKey, "^[0-9a-f-]{36}$") && e2.IdempotencyKey == s.Peticiones[1].Cabeceras["Idempotency-Key"],
                 "el error de un POST lleva la Idempotency-Key que usó");
            var e3 = Error(() => api.Get("/calls"));
            Bien(e3 != null && e3.Http == 502 && e3.Code == null && e3.Message == "HTTP 502" && e3.Response == null, "un 502 sin JSON: sin Code");
            Bien((string)api.Post("/whatsapp/messages", new { to = "1" }).Data["state"] == "unknown", "el 202 de un WhatsApp sin confirmar no es un error");
        }
        using (var s = new Servidor((n, p) => { var r = Respuesta.Json(200, Ok); r.Lento = true; return r; }))
        {
            var t0 = DateTime.UtcNow;
            var e = Error(() => Cliente(s, 0, 0.3).Get("/calls"));
            Bien(e != null && e.Http == 0 && e.Message == "No response from the API: timed out after 0.3 s" && e.InnerException != null
                 && (DateTime.UtcNow - t0).TotalSeconds < 1.2, "el plazo es de la petición entera (con la excepción de debajo)");
            var ep = Error(() => Cliente(s, 0, 0.3).Post("/sms", new { to = "1" }, new MundoSMSRequestOptions { IdempotencyKey = "pedido-882" }));
            Bien(ep != null && ep.Http == 0 && ep.IdempotencyKey == "pedido-882", "sin respuesta, el error de un POST lleva la clave con la que repetirlo");
        }

        Console.WriteLine("== All recorre las páginas con su cursor");
        string[] pags = { "{\"status\":\"success\",\"data\":{\"results\":2,\"calls\":[{\"id\":1},{\"id\":2}],\"has_more\":true,\"next_cursor\":\"a.2\"}}",
                          "{\"status\":\"success\",\"data\":{\"results\":1,\"calls\":[{\"id\":3}],\"has_more\":false,\"next_cursor\":\"a.3\"}}" };
        using (var s = new Servidor((n, p) => Respuesta.Json(200, pags[n - 1])))
        {
            var api = Cliente(s);
            var ids = api.All("/calls", new { order = "asc" }).Select(x => Convert.ToInt64(x["id"])).ToList();
            Bien(ids.SequenceEqual(new long[] { 1, 2, 3 }) && s.Rutas().SequenceEqual(new[] { "/v3/calls?order=asc", "/v3/calls?order=asc&cursor=a.2" })
                 && api.LastCursor == "a.3", "las dos páginas, y el último cursor");
        }

        Console.WriteLine("== una redirección no se sigue, tampoco con tu HttpClient");
        using (var s = new Servidor((n, p) => p.Ruta.StartsWith("/robado") ? Respuesta.Json(200, Ok)
                                              : Respuesta.Json(p.Metodo == "GET" ? 302 : 307, Ok).Con("Location", "/robado")))
        {
            var api = Cliente(s, 2);
            var e1 = Error(() => api.Get("/calls"));
            var e2 = Error(() => api.Post("/sms", new { to = "1", text = "secreto" }));
            Bien(e1 != null && e1.Http == 302 && e1.Response == null && e1.Message.Contains("redirect to /robado not followed"), "302: un error con su Location");
            Bien(e2 != null && e2.Http == 307 && e2.Message.Contains("redirect to /robado not followed"), "307: un error con su Location");
            bool rechaza = false, cadena = false;
            try { new MundoSMSClient(Token, Clave, new HttpClient()); } catch (ArgumentException) { rechaza = true; }
            try { new MundoSMSClient(Token, Clave, new HttpClient(new Paso { InnerHandler = new HttpClientHandler() })); } catch (ArgumentException) { cadena = true; }
            Bien(rechaza && cadena, "tu HttpClient que sigue redirecciones (también detrás de un DelegatingHandler) no se acepta");
            var propio = new MundoSMSClient(Token, Clave, new HttpClient(new Paso { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false } })) { BaseUrl = s.Base, Retries = 0 };
            var e3 = Error(() => propio.Get("/calls"));
            Bien(e3 != null && e3.Http == 302, "tu HttpClient con AllowAutoRedirect = false, sí: la redirección es un error");
            Bien(s.Rutas().SequenceEqual(new[] { "/v3/calls", "/v3/sms", "/v3/calls" }), "al destino no le llega nada");
        }
        using (var s = new Servidor((n, p) => Respuesta.Json(200, Ok)))
        {
            // un manejador que «redirige» por su cuenta (cambia la URI de la petición, como lo haría uno que no se puede comprobar)
            var raro = new MundoSMSClient(Token, Clave, new HttpClient(new Desvio(s.Base.Replace("/v3", "/otro")))) { BaseUrl = s.Base, Retries = 0 };
            var e = Error(() => raro.Get("/calls"));
            Bien(e != null && e.Message.Contains("not followed") && e.Message.Contains("AllowAutoRedirect = false"), "una respuesta de otra URI no se toma por la de la API");
        }

        Console.WriteLine("== un 2xx que no es JSON es un error; un fichero, no");
        var rs = new[] { Respuesta.De(200, "text/html", "<html>Portal</html>"), Respuesta.De(200, "text/html", "<html>Portal</html>"),
                         Respuesta.De(200, "audio/mpeg", "MP3"), Respuesta.De(200, "application/pdf", "%PDF"), new Respuesta { Estado = 204, Tipo = null } };
        using (var s = new Servidor((n, p) => rs[n - 1]))
        {
            var api = Cliente(s, 0);
            var e1 = Error(() => api.Post("/sms", new { to = "1" }));
            var e2 = Error(() => api.Get("/calls"));
            Bien(e1 != null && e2 != null && e1.Http == 200 && e1.Message == "HTTP 200: the response is not JSON (text/html)" && e2.Http == 200, "el HTML de un portal cautivo, en POST y en GET");
            Bien(api.Get("/rec").Body.Length == 3 && (string)api.Get("/cert")["content_type"] == "application/pdf" && api.Delete("/x/1").Body.Length == 0,
                 "un audio, un PDF y un 204, como siempre");
        }

        Console.WriteLine("== All de una lista que no va por páginas es un error");
        using (var s = new Servidor((n, p) => Respuesta.Json(200, "{\"status\":\"success\",\"data\":{\"results\":2,\"calls\":[{\"id\":1},{\"id\":2}],\"has_more\":true}}")))
        {
            var vistos = new List<object>();
            var e = Error(() => { foreach (var x in Cliente(s).All("/calls/missed")) vistos.Add(x); });
            Bien(e != null && e.Message.Contains("not paginated") && vistos.Count == 0 && s.Peticiones.Count == 1, "un error, antes de dar nada");
        }

        Console.WriteLine("== un «+» en la query de la ruta llega como «+»");
        using (var s = new Servidor((n, p) => Respuesta.Json(200, Ok)))
        {
            Cliente(s).Get("/calls?from_datetime=2026-11-20T10:15:03+01:00&a=%20");
            Bien(s.Peticiones[0].Ruta == "/v3/calls?from_datetime=2026-11-20T10%3A15%3A03%2B01%3A00&a=%20" && s.Peticiones[0].Firma, "%2B, y la firma casa");
        }

        Console.WriteLine("== un DELETE que se hizo y perdió la respuesta");
        string no = "{\"status\":\"error\",\"code\":\"not_found\",\"message\":\"Speed dial not found\"}";
        using (var s = new Servidor((n, p) => n == 1 ? null : Respuesta.Json(404, no)))
        {
            MundoSMSResponse r = null;
            Error(() => r = Cliente(s, 1).Delete("/speed-dials/9121"));
            Bien(r != null && (string)r["message"] == "Already deleted" && false.Equals(r.Data["changed"])
                 && string.Join(",", r.Keys.OrderBy(k => k, StringComparer.Ordinal)) == "data,message,status", "el 404 del reintento: ya estaba borrado (sin code ni status_code)");
        }
        string ruta = "{\"status\":\"error\",\"code\":\"route_not_found\",\"message\":\"Route not found\"}";
        using (var s = new Servidor((n, p) => n == 1 ? null : Respuesta.Json(404, ruta)))
        {
            var e = Error(() => Cliente(s, 1).Delete("/speed-dials/9121"));
            Bien(e != null && e.Http == 404 && e.Code == "route_not_found", "tras el corte, route_not_found sigue siendo un error");
        }
        using (var s = new Servidor((n, p) => Respuesta.Json(404, no)))
        {
            var e = Error(() => Cliente(s, 1).Delete("/speed-dials/9121"));
            Bien(e != null && e.Http == 404, "sin fallo de red antes, un 404 es un error");
        }

        Console.WriteLine("== LastCursor es el del recorrido; LastRateLimit, el cupo de la última respuesta");
        using (var s = new Servidor((n, p) => p.Ruta.StartsWith("/v3/vacia") ? Respuesta.Json(200, "{\"status\":\"success\",\"data\":{\"results\":0,\"calls\":[],\"has_more\":false,\"next_cursor\":null}}")
                                              : p.Ruta.StartsWith("/v3/una") ? Respuesta.Json(200, "{\"status\":\"success\",\"data\":{\"results\":1,\"calls\":[{\"id\":1}],\"has_more\":false,\"next_cursor\":\"u1\"}}")
                                              : Respuesta.Json(400, "{\"status\":\"error\",\"code\":\"invalid_parameter\",\"message\":\"Parameter cursor not valid\"}")))
        {
            var api = Cliente(s, 0);
            api.All("/vacia", new { cursor = "c9" }).ToList();
            string c1 = api.LastCursor;
            api.All("/una").ToList();
            string c2 = api.LastCursor;
            Error(() => api.All("/error").ToList());
            Bien(c1 == "c9" && c2 == "u1" && api.LastCursor == null, "c9 al reanudar con una página vacía, y nada si falla al empezar");
        }
        using (var s = new Servidor((n, p) => n == 1 ? Respuesta.Json(200, Ok).Con("X-RateLimit-Limit", "10").Con("X-RateLimit-Remaining", "7").Con("X-RateLimit-Reset", "1") : Respuesta.Json(200, Ok)))
        {
            var api = Cliente(s);
            api.Get("/a");
            var c1 = api.LastRateLimit;
            api.Get("/b");
            Bien(c1 != null && c1.Limit == 10 && c1.Remaining == 7 && c1.Reset == 1 && api.LastRateLimit == null, "10/7/1, y nada sin las cabeceras");
        }

        Console.WriteLine("== tus objetos van con sus nombres de JSON");
        using (var s = new Servidor((n, p) => Respuesta.Json(200, Ok)))
        {
            var api = Cliente(s);
            api.Post("/sms", new SmsDto { Destino = "34600000000", ClientId = "pedido-1", CallbackURL = "https://x/y", Texto = "Hola", Interno = "no" });
            api.Post("/sms", new ContratoDto { Destino = "34600000000", TextoLargo = "Hola", NoVa = "no" });
            api.Post("/campaigns", new { to = new[] { "34600000000" }, opciones = new SmsDto { Destino = "1", Opcional = "sí" } });
            api.Get("/calls", new SmsDto { ClientId = "pedido-1" });
            Bien(s.Peticiones[0].Texto == "{\"to\":\"34600000000\",\"client_id\":\"pedido-1\",\"callback_url\":\"https://x/y\",\"text\":\"Hola\"}",
                 "[JsonPropertyName], [DataMember(Name)], [JsonIgnore] y snake_case sin atributo (" + s.Peticiones[0].Texto + ")");
            Bien(s.Peticiones[1].Texto == "{\"to\":\"34600000000\",\"texto_largo\":\"Hola\"}", "[DataContract]: solo los [DataMember] (" + s.Peticiones[1].Texto + ")");
            Bien(s.Peticiones[2].Texto == "{\"to\":[\"34600000000\"],\"opciones\":{\"to\":\"1\",\"client_id\":null,\"callback_url\":null,\"text\":null,\"opcional\":\"sí\"}}",
                 "dentro de un objeto anónimo, igual (" + s.Peticiones[2].Texto + ")");
            Bien(s.Peticiones[3].Ruta == "/v3/calls?client_id=pedido-1", "y en la query (" + s.Peticiones[3].Ruta + ")");
        }

        Console.WriteLine("== las opciones");
        int malas = 0;
        foreach (var f in new Action[] { () => new MundoSMSClient(Token, Clave) { Retries = -1 }, () => new MundoSMSClient(Token, Clave) { Timeout = TimeSpan.Zero },
                                         () => new MundoSMSClient(Token, Clave) { BaseUrl = "ftp://x/v3" }, () => new MundoSMSClient(Token, Clave) { BaseUrl = "no es una url" } })
        {
            try { f(); } catch (ArgumentException) { malas++; }
        }
        Bien(malas == 4, "un valor que no vale da ArgumentException al ponerlo");
        Console.WriteLine(fallos == 0 ? "== todo bien" : "== " + fallos + " fallo(s)");
        return Math.Min(fallos, 99);
    }
}

/// <summary>Un manejador que manda la petición a otra dirección (como uno que sigue redirecciones y no se puede comprobar).</summary>
public class Desvio : DelegatingHandler
{
    private readonly string a;
    public Desvio(string a) : base(new HttpClientHandler { AllowAutoRedirect = false }) { this.a = a; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.RequestUri = new Uri(a + request.RequestUri.PathAndQuery);
        return base.SendAsync(request, cancellationToken);
    }
}
