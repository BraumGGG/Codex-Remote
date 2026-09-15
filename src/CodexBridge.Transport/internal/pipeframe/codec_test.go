package pipeframe

import (
	"bytes"
	"encoding/hex"
	"testing"
)

func TestCodecMatchesDotNetVector(t *testing.T) {
	var output bytes.Buffer
	if err := Write(&output, Message{Kind: HostData, Payload: []byte("abc")}); err != nil {
		t.Fatal(err)
	}
	if actual := hex.EncodeToString(output.Bytes()); actual != "0200000003616263" {
		t.Fatalf("unexpected vector: %s", actual)
	}
	message, err := Read(&output)
	if err != nil {
		t.Fatal(err)
	}
	if message.Kind != HostData || string(message.Payload) != "abc" {
		t.Fatalf("unexpected round trip: %#v", message)
	}
}

func TestReadRejectsOversizedLength(t *testing.T) {
	encoded, _ := hex.DecodeString("0200020001")
	if _, err := Read(bytes.NewReader(encoded)); err == nil {
		t.Fatal("oversized length was accepted")
	}
}
