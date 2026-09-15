package hostpipe

import (
	"bufio"
	"encoding/base64"
	"encoding/json"
	"net"
	"testing"
)

func TestCreateProofMatchesDotNetVector(t *testing.T) {
	secret := make([]byte, 32)
	for index := range secret {
		secret[index] = byte(index)
	}
	actual := CreateProof(secret, "client", "AQIDBAUGBwgJCgsMDQ4PEA", 1)
	if actual != "GG2gn3xdfPbtUz0YijMyEa2jI6uJhGkkpDqLg_-daSc" {
		t.Fatalf("unexpected proof: %s", actual)
	}
}

func TestAuthenticatePerformsMutualHandshake(t *testing.T) {
	client, server := net.Pipe()
	defer client.Close()
	defer server.Close()
	secret := make([]byte, 32)
	for index := range secret {
		secret[index] = byte(index)
	}
	done := make(chan error, 1)
	go func() {
		reader := bufio.NewReader(server)
		writer := bufio.NewWriter(server)
		challenge := "AQIDBAUGBwgJCgsMDQ4PEA"
		if err := writeJSONLine(writer, challengeMessage{"challenge", 1, challenge}); err != nil {
			done <- err
			return
		}
		var request authenticationMessage
		if err := readJSONLine(reader, &request); err != nil {
			done <- err
			return
		}
		if request.Proof != CreateProof(secret, "client", challenge, 1) {
			t.Fatalf("client proof was invalid")
		}
		done <- writeJSONLine(writer, authenticationMessage{
			"authenticated", 1, CreateProof(secret, "host", challenge, 1),
		})
	}()

	if _, err := Authenticate(client, secret, 1); err != nil {
		t.Fatal(err)
	}
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}

func TestAuthenticateRejectsForgedHost(t *testing.T) {
	client, server := net.Pipe()
	defer client.Close()
	defer server.Close()
	secret := make([]byte, 32)
	go func() {
		reader := bufio.NewReader(server)
		writer := bufio.NewWriter(server)
		_ = writeJSONLine(writer, challengeMessage{"challenge", 1, "nonce"})
		var request map[string]any
		_ = json.NewDecoder(reader).Decode(&request)
		_ = writeJSONLine(writer, authenticationMessage{
			"authenticated", 1, base64.RawURLEncoding.EncodeToString(make([]byte, 32)),
		})
	}()
	if _, err := Authenticate(client, secret, 1); err == nil {
		t.Fatal("forged host was accepted")
	}
}
