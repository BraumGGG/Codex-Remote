package webrtcpeer

import (
	"testing"

	"github.com/pion/webrtc/v4"
)

func TestAcceptOfferRejectsEmptyOffer(t *testing.T) {
	answerer, err := NewAnswerer()
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = answerer.Close() })

	_, err = answerer.AcceptOffer(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer})
	if err == nil {
		t.Fatal("expected an empty offer to be rejected")
	}
}

func TestInitialStatusIsClosed(t *testing.T) {
	answerer, err := NewAnswerer()
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = answerer.Close() })

	status := answerer.Status()
	if status.ChannelOpen || status.MessageSeen {
		t.Fatalf("unexpected initial status: %+v", status)
	}
}
