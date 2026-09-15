package main

import (
	"testing"

	"github.com/pion/webrtc/v4"
)

func TestRemoteTransportForcesRelayOnlyCandidates(t *testing.T) {
	configuration := relayOnlyConfiguration(webrtc.Configuration{
		ICEServers: []webrtc.ICEServer{{URLs: []string{"turn:relay.example:3478"}}},
	})

	if configuration.ICETransportPolicy != webrtc.ICETransportPolicyRelay {
		t.Fatalf("expected relay-only ICE policy, got %s", configuration.ICETransportPolicy.String())
	}
	if len(configuration.ICEServers) != 1 {
		t.Fatal("relay policy must preserve the issued TURN server")
	}
}
