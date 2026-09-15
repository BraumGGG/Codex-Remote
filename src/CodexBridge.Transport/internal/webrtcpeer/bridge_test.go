package webrtcpeer

import (
	"bytes"
	"strings"
	"testing"
	"time"

	"github.com/pion/webrtc/v4"
)

func TestNormalizeCompleteOfferSDPRemovesChromeTrickleRenomination(t *testing.T) {
	offer := "v=0\r\na=ice-options:trickle renomination\r\na=ice-ufrag:test\r\n"
	normalized := normalizeCompleteOfferSDP(offer)
	if strings.Contains(normalized, "ice-options") || !strings.Contains(normalized, "a=ice-ufrag:test") {
		t.Fatalf("unexpected normalized SDP structure")
	}
	if strings.Contains(normalized, "end-of-candidates") {
		t.Fatalf("complete offer end marker should be normalized")
	}
}

func TestRestoreGatheredRelayCandidatesAddsCandidateBeforeEndMarker(t *testing.T) {
	sdp := "v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=end-of-candidates\r\n"
	restored := restoreGatheredRelayCandidates(sdp, []string{
		"candidate:1 1 udp 1 203.0.113.1 50000 typ relay raddr 0.0.0.0 rport 0",
	})
	if !strings.Contains(restored, "a=candidate:1 1 udp 1 203.0.113.1 50000 typ relay") ||
		!strings.Contains(restored, "rport 0\r\na=end-of-candidates") {
		t.Fatal("relay candidate was not restored before the end marker")
	}
}

func TestBridgePeerReceivesAndSendsBinaryData(t *testing.T) {
	receivedByBridge := make(chan []byte, 1)
	selectedPair := make(chan SelectedCandidatePair, 1)
	bridge, err := NewBridgePeer(BridgeCallbacks{
		OnData:                  func(data []byte) { receivedByBridge <- data },
		OnSelectedCandidatePair: func(pair SelectedCandidatePair) { selectedPair <- pair },
	})
	if err != nil {
		t.Fatal(err)
	}
	defer bridge.Close()

	settings := webrtc.SettingEngine{}
	settings.SetIncludeLoopbackCandidate(true)
	offerer, err := webrtc.NewAPI(webrtc.WithSettingEngine(settings)).NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	defer offerer.Close()
	channel, err := offerer.CreateDataChannel(ChannelLabel, nil)
	if err != nil {
		t.Fatal(err)
	}
	opened := make(chan struct{})
	receivedByOfferer := make(chan []byte, 1)
	channel.OnOpen(func() { close(opened) })
	channel.OnMessage(func(message webrtc.DataChannelMessage) { receivedByOfferer <- message.Data })
	offer, err := offerer.CreateOffer(nil)
	if err != nil {
		t.Fatal(err)
	}
	gather := webrtc.GatheringCompletePromise(offerer)
	if err = offerer.SetLocalDescription(offer); err != nil {
		t.Fatal(err)
	}
	<-gather
	answerSDP, err := bridge.AcceptOffer(offerer.LocalDescription().SDP)
	if err != nil {
		t.Fatal(err)
	}
	if err = offerer.SetRemoteDescription(webrtc.SessionDescription{
		Type: webrtc.SDPTypeAnswer,
		SDP:  answerSDP,
	}); err != nil {
		t.Fatal(err)
	}

	select {
	case <-opened:
	case <-time.After(10 * time.Second):
		t.Fatal("DataChannel did not open")
	}
	select {
	case pair := <-selectedPair:
		if pair.LocalType != "host" ||
			(pair.RemoteType != "host" && pair.RemoteType != "prflx") ||
			pair.Protocol != "udp" {
			t.Fatalf("unexpected selected candidate pair: %+v", pair)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("selected candidate pair was not reported")
	}
	if err = channel.Send([]byte("offerer-to-bridge")); err != nil {
		t.Fatal(err)
	}
	select {
	case data := <-receivedByBridge:
		if !bytes.Equal(data, []byte("offerer-to-bridge")) {
			t.Fatalf("unexpected bridge data: %q", data)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("bridge did not receive binary data")
	}
	if err = bridge.Send([]byte("bridge-to-offerer")); err != nil {
		t.Fatal(err)
	}
	select {
	case data := <-receivedByOfferer:
		if !bytes.Equal(data, []byte("bridge-to-offerer")) {
			t.Fatalf("unexpected offerer data: %q", data)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("offerer did not receive bridge data")
	}
}
