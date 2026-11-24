//go:build go1.23

package mundosms_test

// Ejemplos (se compilan, no se ejecutan: llamarían a la API de verdad).

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"time"

	mundosms "github.com/mundosms/mundosms-sdk/go/v2"
)

func Example() {
	api, err := mundosms.New(os.Getenv("MUNDOSMS_TOKEN"), os.Getenv("MUNDOSMS_PRIVATE_KEY"))
	if err != nil {
		log.Fatal(err)
	}
	ctx := context.Background()
	r, err := api.Post(ctx, "/sms", mundosms.Params{"to": "34600123456", "text": "Tu cita es mañana a las 10:00", "from": "MiClinica"})
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println(r.Data()["id"])
	_, err = api.Patch(ctx, "/extensions/recepcion", mundosms.Params{"record_calls": true})
	var e *mundosms.Error
	if errors.As(err, &e) {
		fmt.Println(e.HTTP, e.Code, e.Message, e.Detail, e.RequestID)
	}
}

func ExampleClient_All() {
	api, _ := mundosms.New(os.Getenv("MUNDOSMS_TOKEN"), os.Getenv("MUNDOSMS_PRIVATE_KEY"))
	ctx := context.Background()
	for call, err := range api.All(ctx, "/calls", mundosms.Params{"from_datetime": "2026-11-01", "stop_at_ongoing": true}) {
		if err != nil {
			log.Fatal(err)
		}
		fmt.Println(call["type"], call["id"], call["state"])
	}
	fmt.Println("Para seguir desde aquí:", api.LastCursor()) // la próxima vez, como "cursor"
}

func ExampleClient_Post_idempotencyKey() {
	api, _ := mundosms.New(os.Getenv("MUNDOSMS_TOKEN"), os.Getenv("MUNDOSMS_PRIVATE_KEY"), mundosms.Retries(5), mundosms.Timeout(90*time.Second))
	r, err := api.Post(context.Background(), "/whatsapp/messages",
		mundosms.Params{"to": "34600000001", "type": "text", "text": "Tu pedido ha salido"}, mundosms.IdempotencyKey("pedido-881"))
	if err != nil {
		log.Fatal(err)
	}
	if r.IdempotentReplayed() {
		fmt.Println("ya estaba enviado:", r.Data()["id"])
	}
}

func ExampleVerifyWebhook() {
	http.HandleFunc("/avisos", func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, 1<<20)) // tal cual llega
		if err != nil || !mundosms.VerifyWebhook("whsec_...", r.Header.Get("X-MundoSMS-Signature"), body) {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		// guarda el aviso (json.Unmarshal(body, …)) y procésalo después
		w.WriteHeader(http.StatusNoContent)
	})
	log.Fatal(http.ListenAndServe(":8080", nil))
}
