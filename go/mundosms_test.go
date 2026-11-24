package mundosms

// Pruebas sin red: un servidor de prueba (httptest) que comprueba la firma como la API y contesta lo que pide cada caso.

import (
	"bufio"
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

const (
	testToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJqdGkiOiI0MiIsInN1YiI6IjcifQ.cHJ1ZWJh"
	testKey   = "Pr4ebaClaveDe20Letra"
)

// seen es lo que llegó al servidor de prueba en una petición.
type seen struct {
	Method, RawPath, Body string
	Header                http.Header
	SignatureOK           bool
}

type testServer struct {
	*httptest.Server
	mu   sync.Mutex
	reqs []seen
}

func (s *testServer) requests() []seen {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]seen(nil), s.reqs...)
}

// newServer arranca un servidor que apunta cada petición (con su firma comprobada) y contesta con reply(n, petición).
func newServer(t *testing.T, reply func(n int, r *http.Request, w http.ResponseWriter)) *testServer {
	t.Helper()
	s := &testServer{}
	s.Server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		b, _ := io.ReadAll(r.Body)
		raw := r.URL.EscapedPath()
		if r.URL.RawQuery != "" {
			raw += "?" + r.URL.RawQuery
		}
		mac := hmac.New(sha256.New, []byte(testKey))
		io.WriteString(mac, r.Header.Get("Authorization")[len("Bearer "):]+"."+r.Header.Get("X-Timestamp")+"."+r.Method+" "+raw+".")
		mac.Write(b)
		ok := r.Header.Get("X-Signature") == base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
		s.mu.Lock()
		s.reqs = append(s.reqs, seen{r.Method, raw, string(b), r.Header.Clone(), ok})
		n := len(s.reqs)
		s.mu.Unlock()
		reply(n, r, w)
	}))
	t.Cleanup(s.Close)
	return s
}

func jsonReply(w http.ResponseWriter, status int, body string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	io.WriteString(w, body)
}

const okBody = `{"status":"success","status_code":"0","message":"OK","data":{"id":8812345}}`

func client(t *testing.T, s *testServer, opts ...Option) *Client {
	t.Helper()
	c, err := New(testToken, testKey, append([]Option{BaseURL(s.URL + "/v3")}, opts...)...)
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func TestSignatureAndHeaders(t *testing.T) {
	s := newServer(t, func(_ int, _ *http.Request, w http.ResponseWriter) { jsonReply(w, 200, okBody) })
	c := client(t, s)
	ctx := context.Background()
	if _, err := c.Get(ctx, "/calls", Params{"type": "in", "limit": 5}); err != nil {
		t.Fatal(err)
	}
	if _, err := c.Post(ctx, "/sms", Params{"to": "34600123456", "text": "Señor Núñez: 9,50 € <hoy> & «mañana»"}); err != nil {
		t.Fatal(err)
	}
	if _, err := c.Patch(ctx, "/extensions/recepción 1", Params{"record_calls": true}); err != nil {
		t.Fatal(err)
	}
	if _, err := c.Delete(ctx, "/voicefiles/77", nil); err != nil {
		t.Fatal(err)
	}
	r := s.requests()
	if len(r) != 4 {
		t.Fatalf("%d peticiones", len(r))
	}
	for _, x := range r {
		if !x.SignatureOK {
			t.Errorf("%s %s: la firma no casa", x.Method, x.RawPath)
		}
		if ts := x.Header.Get("X-Timestamp"); !regexp.MustCompile(`^[0-9]{13}$`).MatchString(ts) {
			t.Errorf("X-Timestamp en milisegundos: %q", ts)
		}
		if sig := x.Header.Get("X-Signature"); strings.ContainsAny(sig, "+/=") {
			t.Errorf("la firma va en base64url sin relleno: %q", sig)
		}
		if x.Header.Get("Authorization") != "Bearer "+testToken || x.Header.Get("Accept") != "application/json" ||
			x.Header.Get("User-Agent") != "MundoSMS-Go/"+Version {
			t.Errorf("cabeceras: %v", x.Header)
		}
		if (x.Body != "") != (x.Header.Get("Content-Type") == "application/json") {
			t.Errorf("%s: Content-Type solo con cuerpo (%q, %q)", x.Method, x.Body, x.Header.Get("Content-Type"))
		}
	}
	if r[0].RawPath != "/v3/calls?limit=5&type=in" || r[0].Body != "" {
		t.Errorf("GET: %q %q", r[0].RawPath, r[0].Body)
	}
	if r[1].RawPath != "/v3/sms" || r[1].Body != `{"text":"Señor Núñez: 9,50 € <hoy> & «mañana»","to":"34600123456"}` {
		t.Errorf("POST: el cuerpo, tal cual (UTF-8, sin escapar < > &): %q %q", r[1].RawPath, r[1].Body)
	}
	if r[2].Method != "PATCH" || r[2].RawPath != "/v3/extensions/recepci%C3%B3n%201" || r[2].Body != `{"record_calls":true}` {
		t.Errorf("PATCH: %s %q %q", r[2].Method, r[2].RawPath, r[2].Body)
	}
	if r[3].Method != "DELETE" || r[3].Body != "" || r[3].Header.Get("Idempotency-Key") != "" {
		t.Errorf("DELETE: %+v", r[3])
	}
}

func TestWithoutPrivateKeyNoSignature(t *testing.T) {
	s := newServer(t, func(_ int, _ *http.Request, w http.ResponseWriter) { jsonReply(w, 200, okBody) })
	c, err := New(" "+testToken+"\n", "", BaseURL(s.URL+"/v3"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := c.Get(context.Background(), "/account", nil); err != nil {
		t.Fatal(err)
	}
	h := s.requests()[0].Header
	if h.Get("X-Signature") != "" || h.Get("X-Timestamp") == "" || h.Get("Authorization") != "Bearer "+testToken {
		t.Errorf("sin clave privada: sin firma, con X-Timestamp y el token sin espacios (%v)", h)
	}
}

func TestQueryAndPathEncoding(t *testing.T) {
	s := newServer(t, func(_ int, _ *http.Request, w http.ResponseWriter) { jsonReply(w, 200, okBody) })
	c := client(t, s)
	ctx := context.Background()
	madrid := time.FixedZone("CET", 3600)
	cases := []struct {
		path  string
		query Params
		want  string
	}{
		{"/calls", Params{"type": []string{"in", "out"}, "stop_at_ongoing": true, "x": nil, "vacía": []any{}, "nulos": []any{nil},
			"s": "a b+ñ~*'()!", "i": int64(-5), "f": 1.5, "u": uint8(7)},
			"/v3/calls?f=1.5&i=-5&s=a%20b%2B%C3%B1~%2A%27%28%29%21&stop_at_ongoing=true&type=in%2Cout&u=7"},
		{"/calls?type=in&limit=5", Params{"type": "out"}, "/v3/calls?type=out&limit=5"},
		{"calls?a=1+2&b=%2B&c&d=", nil, "/v3/calls?a=1%2B2&b=%2B&c=&d="}, // «+» es «+»: no es un formulario
		{"/calls?from_datetime=2026-11-20T10:15:03+01:00&x=%20", nil, "/v3/calls?from_datetime=2026-11-20T10%3A15%3A03%2B01%3A00&x=%20"},
		{"/v3/numbers/34910123456", nil, "/v3/numbers/34910123456"},
		{"/v3", nil, "/v3/"},
		{"//extensions/a%2Fb c/%zz", nil, "/v3/extensions/a%2Fb%20c/%25zz"},
		{"/calls", Params{"from_datetime": time.Date(2026, 11, 1, 8, 0, 0, 0, madrid)}, "/v3/calls?from_datetime=2026-11-01T08%3A00%3A00.000%2B01%3A00"},
		{"/calls", Params{"limit": json.Number("100"), "p": new(int)}, "/v3/calls?limit=100&p=0"},
	}
	for _, tc := range cases {
		if _, err := c.Get(ctx, tc.path, tc.query); err != nil {
			t.Fatal(err)
		}
	}
	for i, x := range s.requests() {
		if x.RawPath != cases[i].want || !x.SignatureOK {
			t.Errorf("%q %v → %q (firma %v), y tenía que ser %q", cases[i].path, cases[i].query, x.RawPath, x.SignatureOK, cases[i].want)
		}
	}
	// con una base que no acaba en /v3, el /v3 de la ruta no se quita
	c2 := client(t, s, BaseURL(s.URL+"/api"))
	if _, err := c2.Get(ctx, "/v3/calls", nil); err != nil {
		t.Fatal(err)
	}
	if r := s.requests(); r[len(r)-1].RawPath != "/api/v3/calls" || !r[len(r)-1].SignatureOK {
		t.Errorf("base sin /v3: %q", r[len(r)-1].RawPath)
	}
}

func TestInvalidArgumentsSendNothing(t *testing.T) {
	s := newServer(t, func(_ int, _ *http.Request, w http.ResponseWriter) { jsonReply(w, 200, okBody) })
	c := client(t, s)
	ctx := context.Background()
	_, e1 := c.Post(ctx, "/sms", Params{"to": "1"}, IdempotencyKey("pedido 881"))
	_, e2 := c.Post(ctx, "/sms", Params{"to": "1"}, IdempotencyKey(""))
	_, e3 := c.Post(ctx, "/sms", Params{"to": "1"}, IdempotencyKey(strings.Repeat("a", 65)))
	_, e4 := c.Post(ctx, "/sms", Params{"x": make(chan int)})
	_, e5 := c.Get(ctx, "/calls", Params{"x": struct{}{}})
	_, e6 := c.Request(ctx, "GET", "/calls", []int{1})
	_, e7 := c.Request(ctx, "GE T", "/calls", nil)
	for i, err := range []error{e1, e2, e3, e4, e5, e6, e7} {
		if !errors.Is(err, ErrInvalidArgument) {
			t.Errorf("caso %d: tenía que ser ErrInvalidArgument: %v", i+1, err)
		}
	}
	if n := len(s.requests()); n != 0 {
		t.Errorf("con un argumento malo no se manda nada (%d peticiones)", n)
	}
	for i, o := range []Option{Retries(-1), Timeout(0), Timeout(-time.Second), BaseURL("ftp://x/v3"), BaseURL("https:///v3"),
		BaseURL("https://x/v3?a=1"), HTTPClient(nil)} {
		if _, err := New(testToken, testKey, o); !errors.Is(err, ErrInvalidArgument) {
			t.Errorf("opción %d: tenía que dar ErrInvalidArgument (%v)", i, err)
		}
	}
}

func TestBody(t *testing.T) {
	s := newServer(t, func(_ int, _ *http.Request, w http.ResponseWriter) { jsonReply(w, 200, okBody) })
	c := client(t, s)
	ctx := context.Background()
	type sms struct {
		To   string `json:"to"`
		Text string `json:"text"`
		From string `json:"from,omitempty"`
	}
	c.Post(ctx, "/sms", sms{To: "34600123456", Text: "Hola"})
	c.Post(ctx, "/sms/quote", nil)
	c.Patch(ctx, "/extensions/1", Params{})
	c.Post(ctx, "/sms", json.RawMessage(`{"to": "1",  "text":"a"}`))
	r := s.requests()
	want := []string{`{"to":"34600123456","text":"Hola"}`, "", "", `{"to":"1","text":"a"}`}
	for i, x := range r {
		if x.Body != want[i] || !x.SignatureOK {
			t.Errorf("cuerpo %d: %q (firma %v), y tenía que ser %q", i, x.Body, x.SignatureOK, want[i])
		}
	}
}

func TestIdempotencyKey(t *testing.T) {
	s := newServer(t, func(n int, _ *http.Request, w http.ResponseWriter) {
		if n == 1 {
			w.Header().Set("Retry-After", "0")
			jsonReply(w, 409, `{"status":"error","status_code":"3","message":"Request in progress"}`)
			return
		}
		jsonReply(w, 200, okBody)
	})
	c := client(t, s)
	ctx := context.Background()
	if _, err := c.Post(ctx, "/sms", Params{"to": "1"}); err != nil {
		t.Fatal(err)
	}
	if _, err := c.Post(ctx, "/sms", Params{"to": "1"}, IdempotencyKey("pedido-881")); err != nil {
		t.Fatal(err)
	}
	c.Get(ctx, "/calls", nil)
	r := s.requests()
	k := r[0].Header.Get("Idempotency-Key")
	if !regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`).MatchString(k) {
		t.Errorf("la clave automática es un UUID v4: %q", k)
	}
	if r[1].Header.Get("Idempotency-Key") != k || r[1].Header.Get("X-Timestamp") <= r[0].Header.Get("X-Timestamp") || r[1].Header.Get("X-Signature") == r[0].Header.Get("X-Signature") {
		t.Errorf("el reintento del 409 «Request in progress» (inmediato: Retry-After 0) lleva la MISMA clave y otro X-Timestamp, y se firma otra vez")
	}
	if r[2].Header.Get("Idempotency-Key") != "pedido-881" || r[3].Header.Get("Idempotency-Key") != "" {
		t.Errorf("la tuya, tal cual; y un GET, sin clave (%q, %q)", r[2].Header.Get("Idempotency-Key"), r[3].Header.Get("Idempotency-Key"))
	}
	if _, err := c.Post(ctx, "/sms", Params{"to": "1"}); err != nil {
		t.Fatal(err)
	}
	if r := s.requests(); r[4].Header.Get("Idempotency-Key") == k {
		t.Errorf("cada llamada, una clave nueva")
	}
}

// retryCase: lo que contesta el servidor (siempre lo mismo) y cuántas peticiones tienen que llegar con Retries(2).
func TestRetries(t *testing.T) {
	cases := []struct {
		method, status, message string
		want                    int
	}{
		{"GET", "500", "Internal error", 3},
		{"POST", "500", "Internal error", 1},
		{"PATCH", "503", "Unavailable", 1},
		{"DELETE", "500", "Internal error", 1},
		{"POST", "429", "Too many requests", 3},
		{"PATCH", "429", "Too many requests", 3},
		{"GET", "409", "Duplicated request", 3},
		{"PATCH", "409", "Duplicated request (the same signed request arrived twice)", 3},
		{"POST", "409", "Duplicated request", 3},
		{"POST", "409", "Request in progress", 3},
		{"PATCH", "409", "Request in progress", 1},
		{"GET", "404", "Call not found", 1},
		{"POST", "422", "Idempotency-Key already used", 1},
	}
	for _, tc := range cases {
		s := newServer(t, func(_ int, _ *http.Request, w http.ResponseWriter) {
			w.Header().Set("Retry-After", "0")
			st, _ := strconv.Atoi(tc.status)
			jsonReply(w, st, `{"status":"error","status_code":"3","message":"`+tc.message+`"}`)
		})
		c := client(t, s, Retries(2))
		_, err := c.Request(context.Background(), tc.method, "/x", Params{"a": 1})
		var e *Error
		if !errors.As(err, &e) || strconv.Itoa(e.HTTP) != tc.status || e.Message != tc.message {
			t.Errorf("%s %s: %v", tc.method, tc.status, err)
		}
		if n := len(s.requests()); n != tc.want {
			t.Errorf("%s %s «%s»: %d peticiones, y tenían que ser %d", tc.method, tc.status, tc.message, n, tc.want)
		}
	}
}

func TestRetryWait(t *testing.T) {
	future := time.Now().Add(3 * time.Second).UTC().Format(http.TimeFormat)
	cases := []struct {
		header  string
		attempt int
		min     time.Duration
		max     time.Duration
	}{
		{"", 1, time.Second, time.Second},
		{"", 3, 4 * time.Second, 4 * time.Second},
		{"", 10, 60 * time.Second, 60 * time.Second},
		{"2", 1, 2 * time.Second, 2 * time.Second},
		{"0.25", 1, 250 * time.Millisecond, 250 * time.Millisecond},
		{"120", 1, 60 * time.Second, 60 * time.Second},
		{"-5", 2, 0, 0},
		{"NaN", 2, 2 * time.Second, 2 * time.Second},
		{"mañana", 2, 2 * time.Second, 2 * time.Second},
		{future, 1, time.Second, 3 * time.Second},
	}
	for _, tc := range cases {
		if d := retryWait(tc.header, tc.attempt); d < tc.min || d > tc.max {
			t.Errorf("Retry-After %q, intento %d: %v", tc.header, tc.attempt, d)
		}
	}
}

func TestRetryAfterIsHonored(t *testing.T) {
	s := newServer(t, func(n int, _ *http.Request, w http.ResponseWriter) {
		if n == 1 {
			w.Header().Set("Retry-After", "1")
			jsonReply(w, 429, `{"status":"error","status_code":"14","message":"Too many requests"}`)
			return
		}
		jsonReply(w, 200, okBody)
	})
	c := client(t, s)
	t0 := time.Now()
	if _, err := c.Post(context.Background(), "/sms", Params{"to": "1"}); err != nil {
		t.Fatal(err)
	}
	if el := time.Since(t0); el < 900*time.Millisecond || el > 5*time.Second {
		t.Errorf("esperó %v con Retry-After: 1", el)
	}
}

// Un servidor que lee la petición y cierra sin contestar: un fallo de red. Se repite (también un POST, con su misma
// clave), esperando 1 y 2 s.
func TestNetworkErrorRetriesWithSameKey(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	var mu sync.Mutex
	var keys []string
	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			req, err := http.ReadRequest(bufio.NewReader(conn))
			if err == nil {
				io.ReadAll(req.Body)
				mu.Lock()
				keys = append(keys, req.Method+" "+req.Header.Get("Idempotency-Key"))
				mu.Unlock()
			}
			conn.Close()
		}
	}()
	c, _ := New(testToken, testKey, BaseURL("http://"+ln.Addr().String()+"/v3"), Retries(2))
	_, err = c.Post(context.Background(), "/sms", Params{"to": "1"})
	var e *Error
	if !errors.As(err, &e) || e.HTTP != 0 || !strings.HasPrefix(e.Message, "No response from the API: ") {
		t.Fatalf("sin respuesta: %v", err)
	}
	mu.Lock()
	defer mu.Unlock()
	if len(keys) != 3 || keys[0] != keys[1] || keys[1] != keys[2] || !strings.HasPrefix(keys[0], "POST ") || len(keys[0]) < 30 {
		t.Errorf("el POST sin respuesta se repite con la misma Idempotency-Key: %v", keys)
	}
}

func TestTimeoutWholeRequest(t *testing.T) {
	s := newServer(t, func(_ int, _ *http.Request, w http.ResponseWriter) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(200)
		io.WriteString(w, `{"status":"success",`)
		w.(http.Flusher).Flush() // la cabecera llega; el resto, tarde
		time.Sleep(1500 * time.Millisecond)
		io.WriteString(w, `"data":{}}`)
	})
	c := client(t, s, Timeout(200*time.Millisecond), Retries(0))
	t0 := time.Now()
	_, err := c.Get(context.Background(), "/calls", nil)
	var e *Error
	if !errors.As(err, &e) || e.HTTP != 0 || e.Message != "No response from the API: timed out after 0.2 s" {
		t.Errorf("plazo agotado leyendo la respuesta: %v", err)
	}
	if el := time.Since(t0); el > time.Second {
		t.Errorf("el plazo es de la petición entera: tardó %v", el)
	}
}

func TestContextCancelStopsRetries(t *testing.T) {
	s := newServer(t, func(_ int, _ *http.Request, w http.ResponseWriter) {
		w.Header().Set("Retry-After", "30")
		jsonReply(w, 429, `{"status":"error","status_code":"14","message":"Too many requests"}`)
	})
	c := client(t, s)
	ctx, cancel := context.WithTimeout(context.Background(), 300*time.Millisecond)
	defer cancel()
	t0 := time.Now()
	_, err := c.Get(ctx, "/calls", nil)
	var e *Error
	if !errors.Is(err, context.DeadlineExceeded) || !errors.As(err, &e) || e.HTTP != 429 || e.Message != "Too many requests" {
		t.Errorf("tu contexto corta la espera del reintento, y el error es el del 429: %v", err)
	}
	if el := time.Since(t0); el > 2*time.Second {
		t.Errorf("tardó %v", el)
	}
	ctx2, cancel2 := context.WithCancel(context.Background())
	cancel2()
	if _, err := c.Get(ctx2, "/calls", nil); !errors.Is(err, context.Canceled) {
		t.Errorf("con el contexto ya cancelado: %v", err)
	}
}

func TestErrors(t *testing.T) {
	s := newServer(t, func(n int, _ *http.Request, w http.ResponseWriter) {
		w.Header().Set("X-Request-Id", "req-"+strconv.Itoa(n))
		switch n {
		case 1:
			jsonReply(w, 404, `{"status":"error","status_code":"4","message":"Call not found","detail":"no call 999 of type out"}`)
		case 2:
			jsonReply(w, 200, `{"status":"error","status_code":12,"message":"WhatsApp rejected it","data":{"id":"wam-1"}}`)
		case 3:
			w.Header().Set("Content-Type", "text/html")
			w.WriteHeader(502)
			io.WriteString(w, "<html>Bad gateway</html>")
		case 4:
			jsonReply(w, 202, `{"status":"success","status_code":"11","message":"Not confirmed","data":{"id":"wam-2","state":"unknown"}}`)
		case 5:
			jsonReply(w, 400, `{"status":"error","status_code":"3","message":"Parameter xml_plan not valid","detail":["line 1","line 2"]}`)
		}
	})
	c := client(t, s, Retries(0))
	ctx := context.Background()
	_, err := c.Get(ctx, "/calls/out/999", nil)
	var e *Error
	if !errors.As(err, &e) || e.HTTP != 404 || e.StatusCode != "4" || e.Message != "Call not found" || e.Detail != "no call 999 of type out" ||
		e.RequestID != "req-1" || e.Response["status"] != "error" || e.Unwrap() != nil {
		t.Errorf("404: %#v", e)
	}
	if err.Error() != "mundosms: Call not found: no call 999 of type out (HTTP 404, status_code 4)" {
		t.Errorf("Error(): %q", err.Error())
	}
	_, err = c.Post(ctx, "/whatsapp/messages", Params{"to": "1"})
	if !errors.As(err, &e) || e.HTTP != 200 || e.StatusCode != "12" || e.Data["id"] != "wam-1" {
		t.Errorf("un 200 con status error es un error: %v", err)
	}
	_, err = c.Get(ctx, "/calls", nil)
	if !errors.As(err, &e) || e.HTTP != 502 || e.Message != "HTTP 502" || e.Response != nil || e.RequestID != "req-3" {
		t.Errorf("502 sin JSON: %v", err)
	}
	r, err := c.Post(ctx, "/whatsapp/messages", Params{"to": "1"})
	if err != nil || r.Data()["state"] != "unknown" {
		t.Errorf("el 202 de un WhatsApp sin confirmar no es un error: %v %v", r, err)
	}
	_, err = c.Post(ctx, "/campaigns", Params{"to": "1"})
	if !errors.As(err, &e) || e.Detail != `["line 1","line 2"]` {
		t.Errorf("un detail que no es texto, como JSON: %v", err)
	}
}

func TestFilesAndReplayed(t *testing.T) {
	s := newServer(t, func(n int, _ *http.Request, w http.ResponseWriter) {
		switch n {
		case 1:
			w.Header().Set("Content-Type", "audio/mpeg")
			w.Write([]byte{0xff, 0xfb, 0x00, 0x01})
		case 2:
			w.WriteHeader(204)
		case 3:
			w.Header().Set("Idempotent-Replayed", " True ")
			jsonReply(w, 200, okBody)
		case 4:
			jsonReply(w, 200, `{"status":"success","data":{"id":8812345,"cost":0.07,"big":12345678901234567890}}`)
		}
	})
	c := client(t, s)
	ctx := context.Background()
	r, err := c.Get(ctx, "/calls/out/1/recording", nil)
	if err != nil || r["status"] != "success" || r.ContentType() != "audio/mpeg" || string(r.Body()) != "\xff\xfb\x00\x01" {
		t.Errorf("un fichero: %v %v", r, err)
	}
	r, err = c.Delete(ctx, "/voicefiles/1", nil)
	if err != nil || r["status"] != "success" || len(r.Body()) != 0 || r.Body() == nil {
		t.Errorf("un 204: %#v %v", r, err)
	}
	r, err = c.Post(ctx, "/sms", Params{"to": "1"})
	if err != nil || !r.IdempotentReplayed() || r["idempotent_replayed"] != true || r.Data()["id"] == nil {
		t.Errorf("la respuesta repetida: %v %v", r, err)
	}
	r, err = c.Get(ctx, "/account", nil)
	if err != nil || fmt.Sprint(r.Data()["id"]) != "8812345" || fmt.Sprint(r.Data()["big"]) != "12345678901234567890" {
		t.Errorf("los números, sin perder cifras: %v %v", r, err)
	}
	var out struct {
		Data struct {
			ID   int64   `json:"id"`
			Cost float64 `json:"cost"`
		} `json:"data"`
	}
	if err := r.Decode(&out); err != nil || out.Data.ID != 8812345 || out.Data.Cost != 0.07 {
		t.Errorf("Decode: %+v %v", out, err)
	}
}

func TestAll(t *testing.T) {
	pages := []string{
		`{"status":"success","data":{"results":2,"first":[{"id":1},{"id":2}],"calls":[{"id":0}],"has_more":true,"next_cursor":"a.0.2"}}`,
		`{"status":"success","data":{"results":2,"first":[{"id":3},{"id":4}],"has_more":true,"next_cursor":"a.0.4"}}`,
		`{"status":"success","data":{"results":1,"first":[{"id":5}],"has_more":false,"next_cursor":"a.0.5"}}`,
	}
	s := newServer(t, func(n int, _ *http.Request, w http.ResponseWriter) { jsonReply(w, 200, pages[n-1]) })
	c := client(t, s)
	query := Params{"from_datetime": "2026-11-01", "order": "asc"}
	var ids []string
	c.All(context.Background(), "/calls", query)(func(item map[string]any, err error) bool {
		if err != nil {
			t.Fatal(err)
		}
		ids = append(ids, fmt.Sprint(item["id"]))
		return true
	})
	if strings.Join(ids, ",") != "1,2,3,4,5" || c.LastCursor() != "a.0.5" {
		t.Errorf("las tres páginas, en la primera lista del JSON: %v, cursor %q", ids, c.LastCursor())
	}
	r := s.requests()
	want := []string{"/v3/calls?from_datetime=2026-11-01&order=asc", "/v3/calls?cursor=a.0.2&from_datetime=2026-11-01&order=asc",
		"/v3/calls?cursor=a.0.4&from_datetime=2026-11-01&order=asc"}
	for i, x := range r {
		if x.RawPath != want[i] {
			t.Errorf("página %d: %q", i+1, x.RawPath)
		}
	}
	if _, ok := query["cursor"]; ok {
		t.Errorf("All no toca tu query")
	}
	// salir del bucle: no se piden más páginas
	s2 := newServer(t, func(n int, _ *http.Request, w http.ResponseWriter) { jsonReply(w, 200, pages[n-1]) })
	c2 := client(t, s2)
	n := 0
	c2.All(context.Background(), "/calls", nil)(func(map[string]any, error) bool { n++; return false })
	if n != 1 || len(s2.requests()) != 1 {
		t.Errorf("break: %d elementos, %d peticiones", n, len(s2.requests()))
	}
}

func TestAllStopsAndErrors(t *testing.T) {
	cases := []struct {
		pages     []string
		wantIDs   string
		wantErr   bool
		wantReqs  int
		wantCurso string
	}{
		// un cursor que no avanza: para, sin dar vueltas (la primera página vacía con has_more sigue: no es el final)
		{[]string{`{"status":"success","data":{"results":0,"calls":[],"has_more":true,"next_cursor":"x"}}`,
			`{"status":"success","data":{"results":0,"calls":[],"has_more":true,"next_cursor":"x"}}`}, "", false, 2, "x"},
		// revisión E: una página vacía con has_more y el cursor que avanza no es el final
		{[]string{`{"status":"success","data":{"results":0,"calls":[],"has_more":true,"next_cursor":"x"}}`,
			`{"status":"success","data":{"results":1,"calls":[{"id":5}],"has_more":false,"next_cursor":"y"}}`}, "5", false, 2, "y"},
		// revisión E: la página que se para en una llamada en curso (ongoing_call) es el final, aunque diga has_more
		{[]string{`{"status":"success","data":{"results":1,"calls":[{"id":1}],"has_more":true,"next_cursor":"e1","ongoing_call":{"type":"in","id":9}}}`},
			"1", false, 1, "e1"},
		// una lista que no va por páginas (has_more sin next_cursor, como /calls/missed): un error, antes de ningún elemento
		{[]string{`{"status":"success","data":{"results":1,"calls":[{"id":1}],"has_more":true,"next_cursor":null}}`}, "", true, 1, ""},
		{[]string{`{"status":"success","data":{"results":2,"calls":[{"id":1},{"id":2}],"has_more":true}}`}, "", true, 1, ""},
		{[]string{`{"status":"success","data":{"results":1,"calls":[{"id":1}],"has_more":true,"next_cursor":"c1"}}`,
			`{"status":"error","status_code":"3","message":"Parameter cursor not valid"}`}, "1", true, 2, "c1"},
		{[]string{`{"status":"success","data":{"results":1,"calls":["no"],"has_more":false}}`}, "", true, 1, ""},
	}
	for i, tc := range cases {
		s := newServer(t, func(n int, _ *http.Request, w http.ResponseWriter) {
			st := 200
			if strings.Contains(tc.pages[n-1], `"error"`) {
				st = 400
			}
			jsonReply(w, st, tc.pages[n-1])
		})
		c := client(t, s, Retries(0))
		var ids []string
		gotErr := false
		c.All(context.Background(), "/calls", nil)(func(item map[string]any, err error) bool {
			if err != nil {
				gotErr = true
				return false
			}
			ids = append(ids, fmt.Sprint(item["id"]))
			return true
		})
		if strings.Join(ids, ",") != tc.wantIDs || gotErr != tc.wantErr || len(s.requests()) != tc.wantReqs || c.LastCursor() != tc.wantCurso {
			t.Errorf("caso %d: ids %v, error %v, %d peticiones, cursor %q", i, ids, gotErr, len(s.requests()), c.LastCursor())
		}
	}
}

func TestHTTPClientAndRedirects(t *testing.T) {
	s := newServer(t, func(_ int, _ *http.Request, w http.ResponseWriter) {
		w.Header().Set("Location", "https://elsewhere.example/")
		w.WriteHeader(302)
	})
	var mu sync.Mutex
	used := 0
	hc := &http.Client{Transport: roundTripper(func(r *http.Request) (*http.Response, error) {
		mu.Lock()
		used++
		mu.Unlock()
		return http.DefaultTransport.RoundTrip(r)
	})}
	c := client(t, s, HTTPClient(hc), Retries(0))
	_, err := c.Get(context.Background(), "/calls", nil)
	var e *Error
	mu.Lock()
	defer mu.Unlock()
	if !errors.As(err, &e) || e.HTTP != 302 || used != 1 || !strings.Contains(e.Message, "https://elsewhere.example/") {
		t.Errorf("tu http.Client se usa y la redirección no se sigue: %v (%d)", err, used)
	}
	if hc.CheckRedirect != nil {
		t.Errorf("tu http.Client no se toca")
	}
}

// Una redirección no se sigue: es un *Error con su Location, sin la respuesta, y al otro servidor no le llega nada (tampoco el
// POST de un 307), ni se reintenta.
func TestRedirectNotFollowed(t *testing.T) {
	var mu sync.Mutex
	other := 0
	o := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		mu.Lock()
		other++
		mu.Unlock()
		jsonReply(w, 200, okBody)
	}))
	t.Cleanup(o.Close)
	s := newServer(t, func(_ int, r *http.Request, w http.ResponseWriter) {
		code := 302
		if r.Method == http.MethodPost {
			code = 307
		}
		w.Header().Set("Location", o.URL+"/robado")
		jsonReply(w, code, okBody)
	})
	c := client(t, s, Retries(2))
	ctx := context.Background()
	_, err1 := c.Get(ctx, "/calls", nil)
	_, err2 := c.Post(ctx, "/sms", Params{"to": "34600000000", "text": "secreto"})
	for i, err := range []error{err1, err2} {
		var e *Error
		if want := []int{302, 307}[i]; !errors.As(err, &e) || e.HTTP != want || !strings.Contains(e.Message, o.URL+"/robado") || e.Response != nil {
			t.Errorf("redirección %d: %v", want, err)
		}
	}
	mu.Lock()
	defer mu.Unlock()
	if other != 0 || len(s.requests()) != 2 {
		t.Errorf("al otro servidor le llegaron %d peticiones, y a la API %d (tenían que ser 0 y 2)", other, len(s.requests()))
	}
}

// Un 2xx que no es JSON (el HTML de un portal cautivo, o un JSON que no es un objeto) es un error, en POST y en GET; un fichero
// de la API (por su Content-Type) sigue siendo un fichero.
func TestNonJSONSuccessIsAnError(t *testing.T) {
	s := newServer(t, func(n int, _ *http.Request, w http.ResponseWriter) {
		types := []string{"text/html; charset=utf-8", "text/html", "application/json", "", "application/pdf", "image/png", "application/octet-stream"}
		bodies := []string{"<html>Portal</html>", "<html>Portal</html>", "[1,2]", "no", "%PDF-1.7", "\x89PNG", "\x00\x01"}
		if types[n-1] != "" {
			w.Header().Set("Content-Type", types[n-1])
		} else {
			w.Header()["Content-Type"] = nil // sin Content-Type (que net/http no lo adivine)
		}
		io.WriteString(w, bodies[n-1])
	})
	c := client(t, s, Retries(0))
	ctx := context.Background()
	_, e1 := c.Post(ctx, "/sms", Params{"to": "1"})
	_, e2 := c.Get(ctx, "/calls", nil)
	_, e3 := c.Get(ctx, "/calls", nil)
	_, e4 := c.Get(ctx, "/calls", nil)
	for i, err := range []error{e1, e2, e3, e4} {
		var e *Error
		want := []string{"(text/html)", "(text/html)", "(application/json)", "(no Content-Type)"}[i]
		if !errors.As(err, &e) || e.HTTP != 200 || !strings.Contains(e.Message, "the response is not JSON "+want) {
			t.Errorf("caso %d: %v", i+1, err)
		}
	}
	for _, want := range []string{"application/pdf", "image/png", "application/octet-stream"} {
		if r, err := c.Get(ctx, "/fichero", nil); err != nil || r.ContentType() != want || len(r.Body()) == 0 {
			t.Errorf("un fichero %s: %v %v", want, r, err)
		}
	}
}

// Un DELETE que se hizo y perdió la respuesta: el SDK lo repite y la API da 404. Ya estaba hecho: vuelve como éxito. Sin fallo
// de red antes, un 404 es un error; y «Route not found», también tras el fallo.
func TestDeleteRetriedAfterNetworkError(t *testing.T) {
	for _, tc := range []struct {
		cut     bool
		message string
		wantOK  bool
	}{{true, "Speed dial not found", true}, {false, "Speed dial not found", false}, {true, "Route not found", false}} {
		ln, err := net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			t.Fatal(err)
		}
		go func(cut bool, message string) {
			for n := 1; ; n++ {
				conn, err := ln.Accept()
				if err != nil {
					return
				}
				if req, err := http.ReadRequest(bufio.NewReader(conn)); err == nil && !(cut && n == 1) {
					io.ReadAll(req.Body)
					b := `{"status":"error","status_code":"4","message":"` + message + `"}`
					fmt.Fprintf(conn, "HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\nContent-Length: %d\r\nConnection: close\r\n\r\n%s", len(b), b)
				}
				conn.Close()
			}
		}(tc.cut, tc.message)
		c, _ := New(testToken, testKey, BaseURL("http://"+ln.Addr().String()+"/v3"), Retries(1))
		r, err := c.Delete(context.Background(), "/speed-dials/9121", nil)
		ln.Close()
		if tc.wantOK && (err != nil || r["status"] != "success" || r["message"] != "Already deleted" || r.Data()["changed"] != false) {
			t.Errorf("tras el corte, el 404 del reintento: %v %v", r, err)
		}
		var e *Error
		if !tc.wantOK && (!errors.As(err, &e) || e.HTTP != 404 || e.Message != tc.message) {
			t.Errorf("corte %v, %q: tenía que ser un error 404: %v %v", tc.cut, tc.message, r, err)
		}
	}
}

// LastCursor es el del recorrido: al empezar, el que le pasas (una página vacía sin cursor no lo borra), y un recorrido que
// falla al empezar no deja el de otro.
func TestLastCursorPerTraversal(t *testing.T) {
	s := newServer(t, func(_ int, r *http.Request, w http.ResponseWriter) {
		switch r.URL.Path {
		case "/v3/vacia":
			jsonReply(w, 200, `{"status":"success","data":{"results":0,"calls":[],"has_more":false,"next_cursor":null}}`)
		case "/v3/una":
			jsonReply(w, 200, `{"status":"success","data":{"results":1,"calls":[{"id":1}],"has_more":false,"next_cursor":"u1"}}`)
		default:
			jsonReply(w, 400, `{"status":"error","status_code":"3","message":"Parameter cursor not valid"}`)
		}
	})
	c := client(t, s, Retries(0))
	ctx := context.Background()
	drain := func(path string, q Params) {
		c.All(ctx, path, q)(func(map[string]any, error) bool { return true })
	}
	drain("/vacia", Params{"cursor": "c9"})
	if c.LastCursor() != "c9" {
		t.Errorf("reanudar con un cursor y una página vacía: %q", c.LastCursor())
	}
	drain("/una", nil)
	una := c.LastCursor()
	drain("/error", nil)
	if una != "u1" || c.LastCursor() != "" {
		t.Errorf("un recorrido que falla al empezar no deja el de otro: %q, %q", una, c.LastCursor())
	}
}

// LastRateLimit es el cupo de la última respuesta (X-RateLimit-*), y false sin las cabeceras.
func TestLastRateLimit(t *testing.T) {
	s := newServer(t, func(n int, _ *http.Request, w http.ResponseWriter) {
		switch n {
		case 1:
			w.Header().Set("X-RateLimit-Limit", "10")
			w.Header().Set("X-RateLimit-Remaining", "7")
			w.Header().Set("X-RateLimit-Reset", " 1 ")
		case 3:
			w.Header().Set("X-RateLimit-Remaining", "0")
		}
		jsonReply(w, 200, okBody)
	})
	c := client(t, s)
	if _, ok := c.LastRateLimit(); ok {
		t.Errorf("antes de ninguna petición, nada")
	}
	ctx := context.Background()
	c.Get(ctx, "/a", nil)
	if rl, ok := c.LastRateLimit(); !ok || rl != (RateLimit{10, 7, 1}) {
		t.Errorf("10/7/1: %+v %v", rl, ok)
	}
	c.Get(ctx, "/b", nil)
	if _, ok := c.LastRateLimit(); ok {
		t.Errorf("sin las cabeceras, nada")
	}
	c.Get(ctx, "/c", nil)
	if rl, ok := c.LastRateLimit(); !ok || rl != (RateLimit{-1, 0, -1}) {
		t.Errorf("solo Remaining: %+v %v", rl, ok)
	}
}

type roundTripper func(*http.Request) (*http.Response, error)

func (f roundTripper) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

func TestSecretsNotPrinted(t *testing.T) {
	c, _ := New(testToken, testKey)
	j, _ := json.Marshal(c)
	shown := fmt.Sprintf("%v %+v %#v %s %q %x", c, c, c, c, c, c) + fmt.Sprintf("%+v %#v", struct{ C *Client }{c}, []any{c}) + string(j)
	if strings.Contains(shown, testToken) || strings.Contains(shown, testKey) || strings.Contains(shown, hex.EncodeToString([]byte(testKey))) {
		t.Errorf("el cliente impreso enseña el token o la clave: %s", shown)
	}
	if !strings.Contains(fmt.Sprint(c), DefaultBaseURL) {
		t.Errorf("pero sí la dirección: %s", c)
	}
}

func TestConcurrentUse(t *testing.T) {
	s := newServer(t, func(_ int, r *http.Request, w http.ResponseWriter) { jsonReply(w, 200, okBody) })
	c := client(t, s)
	var wg sync.WaitGroup
	errs := make(chan error, 20)
	for i := 0; i < 20; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			if _, err := c.Get(context.Background(), "/calls/out/"+strconv.Itoa(i), nil); err != nil {
				errs <- err
			}
		}(i)
	}
	wg.Wait()
	close(errs)
	for err := range errs {
		t.Error(err)
	}
	ts := map[string]bool{}
	for _, x := range s.requests() {
		if !x.SignatureOK {
			t.Errorf("%s: firma mala", x.RawPath)
		}
		ts[x.Header.Get("X-Timestamp")] = true
	}
	if len(ts) != 20 {
		t.Errorf("20 peticiones a la vez, 20 X-Timestamp distintos (%d)", len(ts))
	}
}

func sign(secret, t, body string) string {
	m := hmac.New(sha256.New, []byte(secret))
	io.WriteString(m, t+"."+body)
	return hex.EncodeToString(m.Sum(nil))
}

func TestVerifyWebhook(t *testing.T) {
	body := `{"id":"evt_1","type":"sms.status","created":"2026-11-23T10:00:00.000+01:00","account_id":7,"data":{"state":"delivered","text":"Ñandú"}}`
	now := strconv.FormatInt(time.Now().Unix(), 10)
	old := strconv.FormatInt(time.Now().Unix()-301, 10)
	good := sign("whsec_prueba", now, body)
	bad := strings.Repeat("0", 64)
	cases := []struct {
		secret, header, body string
		want                 bool
	}{
		{"whsec_prueba", "t=" + now + ",v1=" + good, body, true},
		{" whsec_prueba\n", " t = " + now + " , v1 = " + strings.ToUpper(good) + " ", body, true},
		{"whsec_prueba", "t=" + now + ",v1=" + bad + ",v1=" + good, body, true},
		{"whsec_prueba", "t=" + now + ",v1=" + good, strings.Replace(body, "delivered", "failed", 1), false},
		{"whsec_prueba", "t=" + now + ",v1=" + good, strings.Replace(body, `,"`, `, "`, 1), false},
		{"", "t=" + now + ",v1=" + sign("", now, body), body, false},
		{"   ", "t=" + now + ",v1=" + sign("", now, body), body, false},
		{"whsec_otro", "t=" + now + ",v1=" + good, body, false},
		{"whsec_prueba", "t=" + old + ",v1=" + sign("whsec_prueba", old, body), body, false},
		{"whsec_prueba", "t=" + now + ",t=" + now + ",v1=" + good, body, false},
		{"whsec_prueba", "t=1e9,v1=" + good, body, false},
		{"whsec_prueba", "t=" + now, body, false},
		{"whsec_prueba", "v1=" + good, body, false},
		{"whsec_prueba", "", body, false},
		{"whsec_prueba", "t=" + now + ",v1=" + good[:63], body, false},
		{"whsec_prueba", "t=" + now + ",v1=" + good + "00", body, false},
		{"whsec_prueba", "t=" + now + ";v1=" + good, body, false},
		{"whsec_prueba", "t=-" + now + ",v1=" + good, body, false},
	}
	for i, tc := range cases {
		if got := VerifyWebhook(tc.secret, tc.header, []byte(tc.body)); got != tc.want {
			t.Errorf("caso %d (%q): %v", i, tc.header, got)
		}
	}
	if !VerifyWebhookWithTolerance("whsec_prueba", "t="+old+",v1="+sign("whsec_prueba", old, body), []byte(body), 10*time.Minute) {
		t.Errorf("con más tolerancia, uno de hace 5 minutos y 1 segundo vale")
	}
	if VerifyWebhookWithTolerance("whsec_prueba", "t="+now+",v1="+good, []byte(body), -time.Second) {
		t.Errorf("con tolerancia negativa no vale ninguno")
	}
}
