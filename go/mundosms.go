// Package mundosms es el cliente de la API REST v3 de MundoSMS (centralita, llamadas, SMS, VozPush, NumCheck y WhatsApp)
// para Go 1.22 o superior. Un solo fichero, sin dependencias: solo la biblioteca estándar.
// Documentación: https://www.mundosms.es/api-v3
// Código y versiones: https://github.com/mundosms/mundosms-sdk (licencia MIT)
//
//	api, err := mundosms.New("TOKEN", "CLAVE_PRIVADA") // la clave privada, "" si tu API Key no exige firma
//	if err != nil {
//		log.Fatal(err)
//	}
//	ctx := context.Background()
//	r, err := api.Post(ctx, "/sms", mundosms.Params{"to": "34600123456", "text": "Hola", "from": "MiEmpresa"})
//	if err != nil {
//		log.Fatal(err)
//	}
//	fmt.Println(r.Data()["id"])
//	for call, err := range api.All(ctx, "/calls", mundosms.Params{"from_datetime": "2026-11-01", "stop_at_ongoing": true}) {
//		if err != nil {
//			log.Fatal(err)
//		}
//		fmt.Println(call["type"], call["id"], call["state"])
//	}
//
// Las respuestas son el JSON entero ([Response], un map[string]any): los objetos llegan como map[string]any, las listas
// como []any, los números como [json.Number] (sin perder cifras: n.Int64(), n.Float64() o n.String()), y además string,
// bool y nil. Con [Response.Decode] lo pasas a tus propios tipos.
//
// Errores: un *[Error] con HTTP (el código HTTP), StatusCode ("3", "4"…), Message, Detail, Data, Response (la respuesta
// entera) y RequestID (la cabecera X-Request-Id); se saca con errors.As. También cuando la API contesta con
// "status": "error", venga con el código HTTP que venga. Un WhatsApp sin confirmar (202) no es un error: vuelve la
// respuesta, con data["state"] "unknown". Lo que no se puede mandar (una Idempotency-Key mal formada, un cuerpo que no
// se puede pasar a JSON) es un error que envuelve a [ErrInvalidArgument], y no se manda nada.
//
// Cada POST lleva una Idempotency-Key (un UUID nuevo en cada llamada, la misma en todos sus intentos; la tuya, con
// api.Post(ctx, path, data, mundosms.IdempotencyKey("pedido-881"))). Por eso un POST que falla por la red, o con 409
// «Request in progress», se repite sin miedo: la API no lo hace dos veces. Si la respuesta es la guardada de la primera
// vez, trae "idempotent_replayed": true ([Response.IdempotentReplayed]).
//
// Reintenta: un 429, en cualquier método (no se hizo nada); un fallo de red, en cualquier método; un 5xx, solo en GET (un
// POST con 5xx no se repite: la API no guarda los 5xx, y repetirlo lo haría otra vez). Espera lo que diga Retry-After
// (como mucho 60 s) o, si no lo trae, 1, 2, 4… s. El contexto corta la petición, los reintentos y las esperas. Un DELETE que
// se repite tras quedarse sin respuesta y da 404 ya estaba hecho: vuelve como éxito, con data["changed"] false.
//
// Una redirección (3xx) no se sigue nunca: es un *[Error] (la API no redirige). Un 2xx que no es JSON (el HTML de un proxy
// o de un portal cautivo) también es un error; los ficheros que da la API (audio, imagen, PDF) vuelven por su Content-Type.
//
// Los avisos que te mandamos (callback_url y webhooks de WhatsApp) se comprueban con [VerifyWebhook].
package mundosms

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"net/http"
	"net/url"
	"reflect"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	// Version es la versión del SDK (va en la cabecera User-Agent).
	Version = "1.2.0"
	// DefaultBaseURL es la dirección de la API.
	DefaultBaseURL = "https://api.mundosms.es/v3"
	// WebhookTolerance es lo más que puede alejarse de tu hora la «t» de la firma de un aviso.
	WebhookTolerance = 300 * time.Second
	// maxRetryWait es lo más que se espera antes de un reintento, aunque Retry-After pida más.
	maxRetryWait = 60 * time.Second
)

// ErrInvalidArgument lo envuelve el error de lo que no se puede mandar: una opción con un valor que no vale, una
// Idempotency-Key mal formada, un cuerpo que no se puede pasar a JSON o un filtro de un tipo que no va en la query. En
// todos esos casos no se manda nada. Se comprueba con errors.Is(err, mundosms.ErrInvalidArgument).
var ErrInvalidArgument = errors.New("mundosms: invalid argument")

var (
	idempotencyKeyRe = regexp.MustCompile(`^[A-Za-z0-9_.:-]{1,64}$`) // la forma que exige la API para la Idempotency-Key
	percentRe        = regexp.MustCompile(`%[0-9A-Fa-f]{2}`)
	timestampRe      = regexp.MustCompile(`^[0-9]{1,15}$`)
	signatureRe      = regexp.MustCompile(`^[0-9a-f]{64}$`)
)

// Params son los filtros de un GET o un DELETE, o los datos de un POST o un PATCH. En la query, true/false van como true y
// false, una lista separada por comas ([]string{"in", "out"} → in,out) y nil (y las listas vacías) no se mandan.
type Params = map[string]any

// Response es la respuesta entera, decodificada: status, status_code, message y data. Las rutas que devuelven un fichero
// (una grabación, una locución, el certificado de un SMS) no contestan JSON: vuelven como status "success", content_type
// y body ([]byte, el contenido tal cual). Una respuesta vacía (un 204) vuelve igual, con body vacío.
type Response map[string]any

// Data es el data de la respuesta, o nil si no trae (o no es un objeto).
func (r Response) Data() map[string]any {
	d, _ := r["data"].(map[string]any)
	return d
}

// Body es el contenido de un fichero, tal cual; nil si la respuesta era JSON.
func (r Response) Body() []byte {
	b, _ := r["body"].([]byte)
	return b
}

// ContentType es el tipo de un fichero (audio/mpeg, application/pdf…); "" si la respuesta era JSON.
func (r Response) ContentType() string {
	s, _ := r["content_type"].(string)
	return s
}

// IdempotentReplayed dice si la API devolvió la respuesta guardada de la primera vez (la cabecera
// Idempotent-Replayed: true, un POST repetido con la misma Idempotency-Key): no se ha vuelto a hacer nada.
func (r Response) IdempotentReplayed() bool {
	b, _ := r["idempotent_replayed"].(bool)
	return b
}

// Decode pasa la respuesta entera a v (un puntero a tu struct, con sus etiquetas json), como json.Unmarshal.
func (r Response) Decode(v any) error {
	b, err := json.Marshal(r)
	if err != nil {
		return err
	}
	return json.Unmarshal(b, v)
}

// Error es el error de cualquier petición: un 4xx o un 5xx, una respuesta 2xx con "status": "error" o, sin respuesta (la
// red, el plazo agotado o el contexto cancelado), HTTP 0 y un Message que empieza por «No response from the API:».
type Error struct {
	HTTP       int            // el código HTTP; 0 si no hubo respuesta
	StatusCode string         // el status_code de la API, como texto ("3", "4", "12"…); "" si la respuesta no era JSON
	Message    string         // el message de la API ("Call not found"…) o el del SDK
	Detail     string         // el detail de la respuesta, si lo trae
	Data       map[string]any // el data de la respuesta (en WhatsApp, el id del mensaje que puede haber salido)
	Response   map[string]any // la respuesta entera, decodificada; nil si no era JSON
	RequestID  string         // la cabecera X-Request-Id: con ella encuentras la petición en Logs API
	cause      error          // sin respuesta: la causa (la red, el plazo o el contexto)
}

func (e *Error) Error() string {
	s := "mundosms: " + e.Message
	if e.Detail != "" {
		s += ": " + e.Detail
	}
	if e.HTTP != 0 {
		s += " (HTTP " + strconv.Itoa(e.HTTP)
		if e.StatusCode != "" {
			s += ", status_code " + e.StatusCode
		}
		s += ")"
	}
	return s
}

// Unwrap da la causa: la de un error sin respuesta (la red, el plazo), y tu contexto si fue él quien cortó (también la
// espera de un reintento: entonces el error es el de la respuesta de antes, un 429 por ejemplo):
// errors.Is(err, context.Canceled) o context.DeadlineExceeded. nil si la API contestó y no hubo más.
func (e *Error) Unwrap() error { return e.cause }

// Client es el cliente de la API. Créalo con [New] y reutilízalo: se puede usar desde varias goroutines a la vez.
type Client struct {
	cred       *credentials // detrás de un puntero: ni %+v del struct enseña el token ni la clave privada
	baseURL    string
	basePath   string // la ruta de baseURL, que también se firma
	stripV3    bool
	retries    int
	timeout    time.Duration
	httpClient *http.Client
	mu         sync.Mutex
	lastCursor string
	lastRate   *RateLimit
}

// RateLimit es el cupo que dijo una respuesta, sus cabeceras X-RateLimit-* ([Client.LastRateLimit]); -1 en la que no traía.
type RateLimit struct {
	Limit     int // X-RateLimit-Limit: las peticiones por segundo que admite tu clave
	Remaining int // X-RateLimit-Remaining: las que te quedan en este segundo, contada esta
	Reset     int // X-RateLimit-Reset: los segundos que faltan para que se reponga el cupo
}

var (
	tsMu   sync.Mutex
	lastTs int64 // el X-Timestamp de la última petición de este proceso (milisegundos)
)

type credentials struct{ token, privateKey string }

// Option es una opción de [New]: [BaseURL], [Retries], [Timeout] o [HTTPClient].
type Option func(*Client) error

// BaseURL es la dirección de la API; por defecto, https://api.mundosms.es/v3.
func BaseURL(baseURL string) Option {
	return func(c *Client) error {
		u, err := url.Parse(strings.TrimRight(baseURL, "/"))
		if err != nil || (u.Scheme != "https" && u.Scheme != "http") || u.Host == "" || u.RawQuery != "" || u.Fragment != "" {
			return fmt.Errorf("%w: baseURL must be an http(s) address without query, like %s", ErrInvalidArgument, DefaultBaseURL)
		}
		c.baseURL = strings.TrimRight(baseURL, "/")
		c.basePath = u.EscapedPath()
		return nil
	}
}

// Retries es cuántas veces se reintenta lo que es seguro repetir (0, ninguna); por defecto, 3.
func Retries(n int) Option {
	return func(c *Client) error {
		if n < 0 {
			return fmt.Errorf("%w: retries cannot be negative", ErrInvalidArgument)
		}
		c.retries = n
		return nil
	}
}

// Timeout es lo que puede durar cada petición entera (conectar, mandar y recibir la respuesta); por defecto, 60 s. Tu
// contexto puede cortarla antes.
func Timeout(d time.Duration) Option {
	return func(c *Client) error {
		if d <= 0 {
			return fmt.Errorf("%w: timeout must be greater than 0", ErrInvalidArgument)
		}
		c.timeout = d
		return nil
	}
}

// HTTPClient es el *http.Client con el que se hacen las peticiones (un proxy, tu propio Transport…); por defecto, uno con
// el Transport de siempre (http.DefaultTransport). Las redirecciones no se siguen: la API no las usa, y la firma no valdría.
func HTTPClient(hc *http.Client) Option {
	return func(c *Client) error {
		if hc == nil {
			return fmt.Errorf("%w: the HTTP client is nil", ErrInvalidArgument)
		}
		copia := *hc
		copia.CheckRedirect = noRedirects
		c.httpClient = &copia
		return nil
	}
}

func noRedirects(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }

// New crea el cliente. token: el Token de tu API Key (empieza por eyJ); privateKey: su clave privada si exige firma (lo
// normal), o "". Da error (envuelve a [ErrInvalidArgument]) si una opción tiene un valor que no vale.
func New(token, privateKey string, opts ...Option) (*Client, error) {
	c := &Client{cred: &credentials{strings.TrimSpace(token), strings.TrimSpace(privateKey)}, retries: 3, timeout: 60 * time.Second,
		httpClient: &http.Client{CheckRedirect: noRedirects}}
	if err := BaseURL(DefaultBaseURL)(c); err != nil {
		return nil, err
	}
	for _, o := range opts {
		if o == nil {
			continue
		}
		if err := o(c); err != nil {
			return nil, err
		}
	}
	c.stripV3 = strings.HasSuffix(c.basePath, "/v3")
	return c, nil
}

// String y Format: lo que enseñan fmt.Print, %v, %+v y %#v, sin el token ni la clave privada, para que no acaben en un log.
func (c *Client) String() string { return "mundosms.Client{BaseURL: " + strconv.Quote(c.baseURL) + "}" }

// Format hace que todos los verbos de fmt enseñen lo mismo que [Client.String].
func (c *Client) Format(f fmt.State, _ rune) { io.WriteString(f, c.String()) }

// LastCursor es el cursor del último [Client.All]: al empezar, el cursor que le pasas ("" si ninguno), y después el último
// next_cursor que llegó (una página sin cursor no lo borra). Guárdalo y pásalo como cursor la próxima vez para pedir solo lo
// nuevo. Es uno por cliente: para dos recorridos a la vez, un cliente para cada uno.
func (c *Client) LastCursor() string {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.lastCursor
}

// LastRateLimit es el cupo que dijo la última respuesta (sus cabeceras X-RateLimit-*), y false si no lo traía o no hubo
// respuesta. Con él, un bucle propio no se pasa de las 10 peticiones por segundo: cuando Remaining llega a 0, espera Reset
// segundos. Es el de la última respuesta del cliente, sea de la goroutine que sea.
func (c *Client) LastRateLimit() (RateLimit, bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.lastRate == nil {
		return RateLimit{}, false
	}
	return *c.lastRate, true
}

// RequestOption es una opción de una petición: [IdempotencyKey].
type RequestOption func(*requestOptions) error

type requestOptions struct {
	idempotencyKey    string
	hasIdempotencyKey bool
}

// IdempotencyKey es tu Idempotency-Key, para que repetir este POST no lo vuelva a hacer (por ejemplo, el id de tu
// pedido): de 1 a 64 caracteres, letras sin tilde, cifras y _ . : -. Sin ella, cada POST lleva un UUID nuevo. La API la
// recuerda 24 horas.
func IdempotencyKey(key string) RequestOption {
	return func(o *requestOptions) error {
		o.idempotencyKey, o.hasIdempotencyKey = key, true
		return nil
	}
}

// Get hace un GET: query va en la query.
func (c *Client) Get(ctx context.Context, path string, query Params) (Response, error) {
	return c.Request(ctx, http.MethodGet, path, query)
}

// Post hace un POST: data (Params, o tu struct con etiquetas json) va en un cuerpo JSON. opts: [IdempotencyKey].
func (c *Client) Post(ctx context.Context, path string, data any, opts ...RequestOption) (Response, error) {
	return c.Request(ctx, http.MethodPost, path, data, opts...)
}

// Patch hace un PATCH: data va en un cuerpo JSON.
func (c *Client) Patch(ctx context.Context, path string, data any) (Response, error) {
	return c.Request(ctx, http.MethodPatch, path, data)
}

// Delete hace un DELETE: query va en la query.
func (c *Client) Delete(ctx context.Context, path string, query Params) (Response, error) {
	return c.Request(ctx, http.MethodDelete, path, query)
}

// Request hace una petición cualquiera. path: "/calls", "/numbers/34910123456"… (sin /v3: si lo lleva, se quita); cada
// trozo se codifica solo y un %XX que ya escribas se respeta. Si la ruta trae "?…", esos parámetros se juntan con data
// (gana data). En GET y DELETE, data (un map) va en la query; en POST y PATCH, en el cuerpo JSON. Devuelve la respuesta
// decodificada. opts: [IdempotencyKey] (en un POST, sin ella, un UUID nuevo); la API la tiene en cuenta en los POST.
func (c *Client) Request(ctx context.Context, method, path string, data any, opts ...RequestOption) (Response, error) {
	r, _, err := c.do(ctx, method, path, data, opts)
	return r, err
}

// All recorre un listado entero, página a página, siguiendo su cursor (next_cursor), elemento a elemento. Cada página lleva
// los mismos filtros (y el mismo order) más el cursor, y se pide al llegar a ella: si sales del bucle, no se piden más.
// Con Go 1.23 o superior se recorre con range:
//
//	for call, err := range api.All(ctx, "/calls", mundosms.Params{"from_datetime": "2026-11-01"}) {
//		if err != nil {
//			return err // el error de una página: después de él no viene nada más
//		}
//		...
//	}
//
// Es un iter.Seq2[map[string]any, error]; en Go 1.22, llámalo con tu función: api.All(…)(func(call map[string]any, err
// error) bool { …; return true }). Al acabar, el último next_cursor queda en [Client.LastCursor]: guárdalo para la
// siguiente sincronización. Avanza cuando ya te ha dado todos los elementos de una página: si tu código falla a mitad de
// una, sigue en la anterior.
//
// Termina con has_more false (con stop_at_ongoing, también cuando la página se para en una llamada en curso), o si el
// cursor no avanza. Una página vacía con has_more true (con stop_at_ongoing y un filtro, cuando ha mirado muchas que no
// casan) no es el final: sigue.
//
// Una lista que no va por páginas (GET /calls/missed y /calls/live: has_more sin next_cursor) no se puede recorrer entera:
// da un *[Error], antes de ningún elemento. Sube limit, o filtra, y pídela con [Client.Get].
func (c *Client) All(ctx context.Context, path string, query Params) func(yield func(map[string]any, error) bool) {
	return func(yield func(map[string]any, error) bool) {
		q := make(Params, len(query)+1)
		for k, v := range query {
			q[k] = v
		}
		c.mu.Lock()
		c.lastCursor = text(q["cursor"]) // el de este recorrido: al empezar, el que le pasas
		c.mu.Unlock()
		listKey := ""
		for {
			r, raw, err := c.do(ctx, http.MethodGet, path, q, nil)
			if err != nil {
				yield(nil, err)
				return
			}
			data := r.Data()
			cursor := text(data["next_cursor"])
			if truthy(data["has_more"]) && truthy(data["results"]) && cursor == "" {
				yield(nil, &Error{HTTP: 200, StatusCode: text(r["status_code"]), Response: r, Data: data,
					Message: "all() cannot go through " + path + ": it has more (has_more) but no next_cursor, so the list is not " +
						"paginated: raise limit or narrow the filters, and read it with Get"})
				return
			}
			if listKey == "" {
				listKey = firstListKey(raw) // la lista es el primer campo de data que es una lista, en el orden del JSON
			}
			if items, ok := data[listKey].([]any); ok && listKey != "" {
				for _, item := range items {
					m, ok := item.(map[string]any)
					if !ok {
						yield(nil, fmt.Errorf("mundosms: %q is not a list of objects", listKey))
						return
					}
					if !yield(m, nil) {
						return
					}
				}
			}
			if cursor != "" {
				c.mu.Lock()
				c.lastCursor = cursor
				c.mu.Unlock()
			}
			if !truthy(data["has_more"]) || cursor == "" || cursor == text(q["cursor"]) || truthy(data["ongoing_call"]) {
				return
			}
			q["cursor"] = cursor
		}
	}
}

func (c *Client) do(ctx context.Context, method, path string, data any, opts []RequestOption) (Response, []byte, error) {
	if ctx == nil {
		ctx = context.Background()
	}
	method = strings.ToUpper(method)
	idemKey, err := idempotencyKey(opts, method) // antes de nada: con una clave mala no se manda nada
	if err != nil {
		return nil, nil, err
	}
	encodedPath, params := splitPath(path, c.stripV3)
	var body []byte
	if method == http.MethodGet || method == http.MethodDelete {
		if err := params.merge(data); err != nil {
			return nil, nil, err
		}
	} else if body, err = jsonBody(data); err != nil {
		return nil, nil, err
	}
	query, err := params.encode()
	if err != nil {
		return nil, nil, err
	}
	rest := encodedPath
	if query != "" {
		rest += "?" + query
	}
	target := c.baseURL + rest
	signedPath := c.basePath + rest // lo que se firma es justo lo que se manda
	// un método o una ruta que no se pueden mandar: un error, no un fallo de red que se reintente
	if _, err := http.NewRequest(method, target, nil); err != nil {
		return nil, nil, fmt.Errorf("%w: %v", ErrInvalidArgument, err)
	}
	afterNetwork := false // un intento de antes se quedó sin respuesta (o llegó dos veces): puede que se hiciera
	for attempt := 1; ; attempt++ {
		status, header, resp, netErr := c.send(ctx, method, target, signedPath, timestamp(), idemKey, body)
		if netErr != nil && ctx.Err() != nil { // tu contexto: ni se reintenta ni se espera
			c.setRateLimit(nil)
			return nil, nil, &Error{Message: "No response from the API: " + ctx.Err().Error(), cause: ctx.Err()}
		}
		var j map[string]any
		isJSON := false
		if netErr == nil && (status < 300 || status >= 400) { // de una redirección no se lee nada
			j, isJSON = decodeObject(resp)
		}
		r, err := result(method, afterNetwork, status, header, resp, netErr, j, isJSON)
		if retryable(method, netErr != nil, status, j, idemKey) && attempt <= c.retries {
			afterNetwork = afterNetwork || netErr != nil || duplicated(status, j)
			if cerr := sleep(ctx, retryWait(header.Get("Retry-After"), attempt)); cerr != nil {
				// tu contexto cortó la espera: el error de este intento (un 429, la red…), y errors.Is ve tu contexto
				if e, ok := err.(*Error); ok {
					e.cause = errors.Join(cerr, e.cause)
				}
				return nil, nil, err
			}
			continue
		}
		if netErr != nil {
			c.setRateLimit(nil)
		} else {
			c.setRateLimit(header)
		}
		if err != nil {
			return nil, nil, err
		}
		return r, resp, nil
	}
}

// setRateLimit guarda el cupo de la última respuesta (nil: no hubo respuesta).
func (c *Client) setRateLimit(h http.Header) {
	var rl *RateLimit
	if h != nil {
		v := RateLimit{-1, -1, -1}
		found := false
		for name, dst := range map[string]*int{"X-RateLimit-Limit": &v.Limit, "X-RateLimit-Remaining": &v.Remaining, "X-RateLimit-Reset": &v.Reset} {
			if n, err := strconv.Atoi(strings.TrimSpace(h.Get(name))); err == nil {
				*dst, found = n, true
			}
		}
		if found {
			rl = &v
		}
	}
	c.mu.Lock()
	c.lastRate = rl
	c.mu.Unlock()
}

// fileTypes es lo que la API da como fichero (grabaciones, locuciones, diagramas y el PDF de un certificado).
var fileTypes = regexp.MustCompile(`^(audio/|image/|application/pdf$|application/octet-stream$)`)

// duplicated: un 409 «Duplicated request», la MISMA petición firmada llegó dos veces (la repitió algo entre medias).
func duplicated(status int, j map[string]any) bool {
	m, _ := j["message"].(string)
	return status == 409 && strings.HasPrefix(m, "Duplicated request")
}

// result es lo que devuelve un intento: la respuesta, o el *Error (también sin respuesta, la red o el plazo). afterNetwork:
// un intento de antes se quedó sin respuesta.
func result(method string, afterNetwork bool, status int, header http.Header, resp []byte, netErr error, j map[string]any, isJSON bool) (Response, error) {
	if netErr != nil {
		return nil, &Error{Message: "No response from the API: " + netErr.Error(), cause: netErr}
	}
	if status >= 300 && status < 400 { // no se sigue: llevaría el token y la firma a otro servidor
		where := ""
		if l := strings.TrimSpace(header.Get("Location")); l != "" {
			if len(l) > 300 {
				l = l[:300]
			}
			where = " to " + l
		}
		return nil, &Error{HTTP: status, RequestID: header.Get("X-Request-Id"),
			Message: "HTTP " + strconv.Itoa(status) + ": redirect" + where + " not followed (the API does not redirect: check BaseURL)"}
	}
	replayed := strings.EqualFold(strings.TrimSpace(header.Get("Idempotent-Replayed")), "true")
	if replayed && isJSON {
		j["idempotent_replayed"] = true // la respuesta guardada de la primera vez: no se ha vuelto a hacer
	}
	if status >= 200 && status < 300 {
		if !isJSON { // un fichero, o algo que no es de la API
			ct := strings.TrimSpace(strings.SplitN(header.Get("Content-Type"), ";", 2)[0])
			if len(resp) > 0 && !fileTypes.MatchString(strings.ToLower(ct)) {
				if ct == "" {
					ct = "no Content-Type"
				}
				return nil, &Error{HTTP: status, RequestID: header.Get("X-Request-Id"),
					Message: "HTTP " + strconv.Itoa(status) + ": the response is not JSON (" + ct + ")"}
			}
			file := Response{"status": "success", "content_type": header.Get("Content-Type"), "body": resp}
			if replayed {
				file["idempotent_replayed"] = true
			}
			return file, nil
		}
		if s, _ := j["status"].(string); s != "error" {
			return Response(j), nil
		}
	}
	if m, _ := j["message"].(string); method == http.MethodDelete && afterNetwork && status == 404 && isJSON && !strings.HasPrefix(m, "Route not found") {
		// lo borró el intento que se quedó sin respuesta
		return Response{"status": "success", "status_code": "0", "message": "Already deleted",
			"data": map[string]any{"state": "deleted", "changed": false}}, nil
	}
	e := &Error{HTTP: status, Message: "HTTP " + strconv.Itoa(status), RequestID: header.Get("X-Request-Id")}
	if isJSON {
		e.Message = "Error"
		if m, ok := j["message"].(string); ok {
			e.Message = m
		}
		e.StatusCode, e.Detail, e.Response = text(j["status_code"]), text(j["detail"]), j
		e.Data, _ = j["data"].(map[string]any)
	}
	return nil, e
}

// send hace un intento: el código HTTP, las cabeceras y el cuerpo, o el fallo de red (sin respuesta entera, aunque llegara
// la cabecera: entonces el código es 0).
func (c *Client) send(ctx context.Context, method, target, signedPath, ts, idemKey string, body []byte) (int, http.Header, []byte, error) {
	actx, cancel := context.WithTimeout(ctx, c.timeout) // el plazo de la petición ENTERA, también leer la respuesta
	defer cancel()
	var reader io.Reader
	if body != nil {
		reader = bytes.NewReader(body)
	}
	req, err := http.NewRequestWithContext(actx, method, target, reader)
	if err != nil {
		return 0, http.Header{}, nil, err
	}
	// sin GetBody, el Transport no repite por su cuenta un POST con Idempotency-Key (con el mismo X-Timestamp: la API lo
	// tomaría por repetido); si la conexión se corta, lo repite el SDK, firmado otra vez
	req.GetBody = nil
	req.Header.Set("Authorization", "Bearer "+c.cred.token)
	req.Header.Set("X-Timestamp", ts)
	req.Header.Set("Accept", "application/json")
	req.Header.Set("User-Agent", "MundoSMS-Go/"+Version)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if idemKey != "" {
		req.Header.Set("Idempotency-Key", idemKey) // la misma en todos los intentos de esta llamada
	}
	if c.cred.privateKey != "" {
		mac := hmac.New(sha256.New, []byte(c.cred.privateKey))
		io.WriteString(mac, c.cred.token+"."+ts+"."+method+" "+signedPath+".")
		mac.Write(body)
		req.Header.Set("X-Signature", base64.RawURLEncoding.EncodeToString(mac.Sum(nil)))
	}
	res, err := c.httpClient.Do(req)
	if err == nil {
		var resp []byte
		resp, err = io.ReadAll(res.Body)
		res.Body.Close()
		if err == nil {
			return res.StatusCode, res.Header, resp, nil
		}
	}
	if ctx.Err() == nil && errors.Is(actx.Err(), context.DeadlineExceeded) {
		err = errors.New("timed out after " + strconv.FormatFloat(c.timeout.Seconds(), 'f', -1, 64) + " s")
	} else {
		var ue *url.Error
		if errors.As(err, &ue) {
			err = ue.Err // sin «Get "https://…":» delante
		}
	}
	return 0, http.Header{}, nil, err
}

// timestamp es el X-Timestamp de una petición, en milisegundos y siempre mayor que el de la anterior de este proceso: dos
// peticiones iguales (un reintento inmediato, o dos goroutines a la vez, también desde dos clientes) nunca firman igual, y
// la API no las toma por una repetida («409 Duplicated request»).
func timestamp() string {
	tsMu.Lock()
	defer tsMu.Unlock()
	ts := time.Now().UnixMilli()
	if ts <= lastTs {
		ts = lastTs + 1
	}
	lastTs = ts
	return strconv.FormatInt(ts, 10)
}

// idempotencyKey es la Idempotency-Key de la llamada: la tuya, si tiene la forma que exige la API; en un POST sin ella,
// un UUID v4 nuevo.
func idempotencyKey(opts []RequestOption, method string) (string, error) {
	var o requestOptions
	for _, f := range opts {
		if f == nil {
			continue
		}
		if err := f(&o); err != nil {
			return "", err
		}
	}
	if o.hasIdempotencyKey {
		if !idempotencyKeyRe.MatchString(o.idempotencyKey) {
			return "", fmt.Errorf("%w: the idempotency key must be 1 to 64 characters: A-Z, a-z, 0-9 and _ . : - (nothing was sent)", ErrInvalidArgument)
		}
		return o.idempotencyKey, nil
	}
	if method != http.MethodPost {
		return "", nil
	}
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", fmt.Errorf("mundosms: cannot create the Idempotency-Key (nothing was sent): %w", err)
	}
	b[6] = b[6]&0x0f | 0x40 // versión 4
	b[8] = b[8]&0x3f | 0x80 // variante RFC 4122
	h := hex.EncodeToString(b[:])
	return h[0:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:], nil
}

// retryable dice si se puede repetir: un 429 en cualquier método; un GET, ante un fallo de red o un 5xx; un PATCH o un
// DELETE, ante un fallo de red; un POST (con su Idempotency-Key), ante un fallo de red o un 409 «Request in progress». Un
// 5xx de un POST no: la API no guarda los 5xx y repetirlo lo haría otra vez. Un 409 «Duplicated request» (la MISMA
// petición firmada llegó dos veces: la repitió algo entre medias, como hace el Transport de Go con una conexión que se
// cerró) es un fallo de red más.
func retryable(method string, network bool, status int, j map[string]any, idemKey string) bool {
	if status == 429 {
		return true
	}
	message, _ := j["message"].(string)
	network = network || duplicated(status, j)
	switch {
	case method == http.MethodGet:
		return network || status >= 500
	case method == http.MethodPatch || method == http.MethodDelete:
		return network
	case idemKey != "":
		return network || (status == 409 && message == "Request in progress")
	}
	return false
}

// retryWait es lo que se espera antes del siguiente intento: lo que pida Retry-After (segundos o fecha HTTP), o 1, 2, 4…
// s. Como mucho 60 s.
func retryWait(retryAfter string, attempt int) time.Duration {
	s, ok := 0.0, false
	if v := strings.TrimSpace(retryAfter); v != "" {
		if f, err := strconv.ParseFloat(v, 64); err == nil && !math.IsNaN(f) {
			s, ok = f, true
		} else if t, err := http.ParseTime(v); err == nil {
			s, ok = time.Until(t).Seconds(), true
		}
	}
	if !ok {
		s = math.Pow(2, float64(attempt-1))
	}
	s = math.Max(0, math.Min(maxRetryWait.Seconds(), s))
	return time.Duration(s * float64(time.Second))
}

func sleep(ctx context.Context, d time.Duration) error {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-t.C:
		return nil
	}
}

// decodeObject decodifica la respuesta si es un objeto JSON (con los números como json.Number); si no, es un fichero.
func decodeObject(b []byte) (map[string]any, bool) {
	dec := json.NewDecoder(bytes.NewReader(b))
	dec.UseNumber()
	var v any
	if dec.Decode(&v) != nil {
		return nil, false
	}
	if _, err := dec.Token(); err != io.EOF { // algo más detrás del JSON: no es JSON
		return nil, false
	}
	m, ok := v.(map[string]any)
	return m, ok
}

// firstListKey es el primer campo de data que es una lista, en el orden en que llega (un map de Go no lo guarda).
func firstListKey(raw []byte) string {
	dec := json.NewDecoder(bytes.NewReader(raw))
	if t, err := dec.Token(); err != nil || t != json.Delim('{') {
		return ""
	}
	for dec.More() {
		t, err := dec.Token()
		if err != nil {
			return ""
		}
		if t != "data" {
			var skip json.RawMessage
			if dec.Decode(&skip) != nil {
				return ""
			}
			continue
		}
		if t, err := dec.Token(); err != nil || t != json.Delim('{') {
			return ""
		}
		for dec.More() {
			k, err := dec.Token()
			if err != nil {
				return ""
			}
			var v json.RawMessage
			if dec.Decode(&v) != nil {
				return ""
			}
			if s, _ := k.(string); len(v) > 0 && v[0] == '[' {
				return s
			}
		}
		return ""
	}
	return ""
}

// text es un valor de la respuesta como texto: un string tal cual, un número con sus cifras; nil, "".
func text(v any) string {
	switch x := v.(type) {
	case nil:
		return ""
	case string:
		return x
	case json.Number:
		return x.String()
	case bool:
		return strconv.FormatBool(x)
	}
	b, _ := json.Marshal(v)
	return string(b)
}

// truthy: lo que en la respuesta cuenta como «sí» (true, un número que no es 0, un texto, una lista o un objeto con algo).
func truthy(v any) bool {
	switch x := v.(type) {
	case nil:
		return false
	case bool:
		return x
	case json.Number:
		f, err := x.Float64()
		return err != nil || f != 0
	case string:
		return x != ""
	case []any:
		return len(x) > 0
	case map[string]any:
		return len(x) > 0
	}
	return true
}

// jsonBody es el cuerpo JSON de un POST o un PATCH, tal cual se firma y se manda; nil si no hay datos.
func jsonBody(data any) ([]byte, error) {
	if data == nil {
		return nil, nil
	}
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetEscapeHTML(false)
	if err := enc.Encode(data); err != nil {
		return nil, fmt.Errorf("%w: the body cannot be encoded as JSON (nothing was sent): %v", ErrInvalidArgument, err)
	}
	b := bytes.TrimRight(buf.Bytes(), "\n")
	switch string(b) {
	case "null", "{}", "[]", `""`, "0", "false": // sin datos: sin cuerpo
		return nil, nil
	}
	return b, nil
}

// params son los parámetros de la query en orden: los de la ruta, como vienen, y después los de data, por orden alfabético
// (un map de Go no guarda el orden). Si se repite uno, gana el de data, en su sitio.
type params struct {
	keys   []string
	values map[string]any
}

func (p *params) set(k string, v any) {
	if _, ok := p.values[k]; !ok {
		p.keys = append(p.keys, k)
	}
	p.values[k] = v
}

func (p *params) merge(data any) error {
	if data == nil {
		return nil
	}
	v := reflect.ValueOf(data)
	if v.Kind() != reflect.Map || v.Type().Key().Kind() != reflect.String {
		return fmt.Errorf("%w: in GET and DELETE the query must be a map, like mundosms.Params (got %T)", ErrInvalidArgument, data)
	}
	keys := make([]string, 0, v.Len())
	byKey := make(map[string]reflect.Value, v.Len())
	for _, k := range v.MapKeys() {
		keys = append(keys, k.String())
		byKey[k.String()] = v.MapIndex(k)
	}
	sort.Strings(keys)
	for _, k := range keys {
		p.set(k, byKey[k].Interface())
	}
	return nil
}

// encode es la query en RFC 3986 (todo codificado menos A-Z a-z 0-9 - . _ ~): es lo que se firma y lo que se manda.
func (p *params) encode() (string, error) {
	parts := make([]string, 0, len(p.keys))
	for _, k := range p.keys {
		t, ok, err := queryValue(p.values[k])
		if err != nil {
			return "", fmt.Errorf("%w: query parameter %q: %v", ErrInvalidArgument, k, err)
		}
		if ok {
			parts = append(parts, rfc3986(k)+"="+rfc3986(t))
		}
	}
	return strings.Join(parts, "&"), nil
}

// queryValue es un valor de la query: true/false, una lista separada por comas; nil o una lista vacía (o solo de nil),
// nada (ok false). Una fecha (time.Time), en ISO 8601 con milisegundos y su zona.
func queryValue(v any) (string, bool, error) {
	switch x := v.(type) {
	case nil:
		return "", false, nil
	case string:
		return x, true, nil
	case bool:
		return strconv.FormatBool(x), true, nil
	case json.Number:
		return x.String(), true, nil
	case time.Time:
		return x.Format("2006-01-02T15:04:05.000Z07:00"), true, nil
	case []byte:
		return string(x), true, nil
	}
	rv := reflect.ValueOf(v)
	switch rv.Kind() {
	case reflect.Pointer, reflect.Interface:
		if rv.IsNil() {
			return "", false, nil
		}
		return queryValue(rv.Elem().Interface())
	case reflect.String:
		return rv.String(), true, nil
	case reflect.Bool:
		return strconv.FormatBool(rv.Bool()), true, nil
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return strconv.FormatInt(rv.Int(), 10), true, nil
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64, reflect.Uintptr:
		return strconv.FormatUint(rv.Uint(), 10), true, nil
	case reflect.Float32, reflect.Float64:
		return strconv.FormatFloat(rv.Float(), 'f', -1, rv.Type().Bits()), true, nil
	case reflect.Slice, reflect.Array:
		items := make([]string, 0, rv.Len())
		for i := 0; i < rv.Len(); i++ {
			t, ok, err := queryValue(rv.Index(i).Interface())
			if err != nil {
				return "", false, err
			}
			if ok {
				items = append(items, t)
			}
		}
		return strings.Join(items, ","), len(items) > 0, nil
	}
	return "", false, fmt.Errorf("a %T cannot go in the query", v)
}

// rfc3986 codifica todo menos A-Z a-z 0-9 - . _ ~, byte a byte en UTF-8.
func rfc3986(s string) string {
	const hexDigits = "0123456789ABCDEF"
	var b strings.Builder
	for i := 0; i < len(s); i++ {
		c := s[i]
		if 'A' <= c && c <= 'Z' || 'a' <= c && c <= 'z' || '0' <= c && c <= '9' || c == '-' || c == '.' || c == '_' || c == '~' {
			b.WriteByte(c)
		} else {
			b.WriteByte('%')
			b.WriteByte(hexDigits[c>>4])
			b.WriteByte(hexDigits[c&15])
		}
	}
	return b.String()
}

// splitPath es la ruta codificada trozo a trozo (un %XX ya escrito se respeta), sin el /v3 del principio si la base ya lo
// lleva, y los parámetros que traiga tras «?».
func splitPath(path string, stripV3 bool) (string, *params) {
	p := &params{values: map[string]any{}}
	path, query, _ := strings.Cut(path, "?")
	for _, pair := range strings.Split(query, "&") {
		if pair == "" {
			continue
		}
		k, v, _ := strings.Cut(pair, "=")
		p.set(unescape(k), unescape(v))
	}
	path = "/" + strings.TrimLeft(path, "/")
	if stripV3 && (path == "/v3" || strings.HasPrefix(path, "/v3/")) {
		path = "/" + strings.TrimLeft(path[3:], "/")
	}
	segments := strings.Split(path, "/")
	for i, seg := range segments {
		var b strings.Builder
		last := 0
		for _, m := range percentRe.FindAllStringIndex(seg, -1) {
			b.WriteString(rfc3986(seg[last:m[0]]))
			b.WriteString(seg[m[0]:m[1]])
			last = m[1]
		}
		b.WriteString(rfc3986(seg[last:]))
		segments[i] = b.String()
	}
	return strings.Join(segments, "/"), p
}

// unescape decodifica un trozo de la query de la ruta: cada %XX, su byte; lo demás, también el «+», tal cual (RFC 3986:
// no es un formulario), para que una fecha con zona escrita en la ruta (from_datetime=…+01:00) llegue tal cual. Un % que no
// es %XX se deja.
func unescape(s string) string {
	return percentRe.ReplaceAllStringFunc(s, func(m string) string {
		n, _ := strconv.ParseUint(m[1:], 16, 8)
		return string([]byte{byte(n)})
	})
}

// VerifyWebhook dice si un aviso que te llega (callback_url o webhook de WhatsApp) es nuestro y nadie lo ha tocado, con 5
// minutos de tolerancia ([VerifyWebhookWithTolerance] para otra). secret: el de «API y Soporte → Webhooks» (whsec_…);
// header: la cabecera X-MundoSMS-Signature (r.Header.Get("X-MundoSMS-Signature")); body: el cuerpo TAL CUAL llegó
// (io.ReadAll(r.Body)), sin volver a serializar el JSON. Una cabecera que falta o está mal formada da false.
func VerifyWebhook(secret, header string, body []byte) bool {
	return VerifyWebhookWithTolerance(secret, header, body, WebhookTolerance)
}

// VerifyWebhookWithTolerance es [VerifyWebhook] con otra tolerancia: lo que puede alejarse «t» de tu hora.
func VerifyWebhookWithTolerance(secret, header string, body []byte, tolerance time.Duration) bool {
	secret = strings.TrimSpace(secret)
	if secret == "" {
		return false
	}
	t, hasT := "", false
	var signatures []string
	for _, part := range strings.Split(header, ",") {
		k, v, ok := strings.Cut(part, "=")
		if !ok {
			continue
		}
		switch k, v = strings.TrimSpace(k), strings.TrimSpace(v); k {
		case "t":
			if hasT {
				return false // dos «t»: no se sabe cuál vale
			}
			t, hasT = v, true
		case "v1":
			signatures = append(signatures, strings.ToLower(v))
		}
	}
	if !hasT || !timestampRe.MatchString(t) || len(signatures) == 0 {
		return false
	}
	sent, _ := strconv.ParseInt(t, 10, 64)
	if diff := math.Abs(float64(time.Now().Unix() - sent)); diff > tolerance.Seconds() {
		return false
	}
	mac := hmac.New(sha256.New, []byte(secret))
	io.WriteString(mac, t+".")
	mac.Write(body)
	expected := []byte(hex.EncodeToString(mac.Sum(nil)))
	valid := false
	for _, sig := range signatures { // todas, sin cortar en la primera: hmac.Equal es de tiempo constante
		if signatureRe.MatchString(sig) && hmac.Equal(expected, []byte(sig)) {
			valid = true
		}
	}
	return valid
}
