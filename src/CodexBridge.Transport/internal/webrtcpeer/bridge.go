package webrtcpeer

import (
	"errors"
	"regexp"
	"strings"
	"sync"
	"time"

	"github.com/pion/webrtc/v4"
)

var trickleICEOptionsLine = regexp.MustCompile(`(?m)^a=ice-options:[^\r\n]*\btrickle\b[^\r\n]*(?:\r\n|\n|$)`)
var endOfCandidatesLine = regexp.MustCompile(`(?m)^a=end-of-candidates(?:\r\n|\n|$)`)

type BridgeCallbacks struct {
	OnOpen                  func()
	OnData                  func([]byte)
	OnClose                 func()
	OnFailure               func()
	OnICEState              func(string)
	OnSelectedCandidatePair func(SelectedCandidatePair)
}

type SelectedCandidatePair struct {
	LocalType  string `json:"localType"`
	RemoteType string `json:"remoteType"`
	Protocol   string `json:"protocol"`
}

type BridgePeer struct {
	mu                 sync.RWMutex
	peer               *webrtc.PeerConnection
	channel            *webrtc.DataChannel
	open               bool
	gatheredCandidates []string
	callbacks          BridgeCallbacks
}

func NewBridgePeer(callbacks BridgeCallbacks) (*BridgePeer, error) {
	return NewBridgePeerWithConfiguration(callbacks, webrtc.Configuration{})
}

func NewBridgePeerWithConfiguration(callbacks BridgeCallbacks, configuration webrtc.Configuration) (*BridgePeer, error) {
	// Keep the production answerer on Pion's default API path. The previous
	// custom SettingEngine differed from the proven TURN check path and could
	// leave ICE gathering stuck at checking without producing relay candidates.
	peer, err := webrtc.NewPeerConnection(configuration)
	if err != nil {
		return nil, err
	}
	bridge := &BridgePeer{peer: peer, callbacks: callbacks}
	peer.OnICECandidate(func(candidate *webrtc.ICECandidate) {
		if candidate == nil {
			return
		}
		value := candidate.ToJSON().Candidate
		if !strings.Contains(value, " typ relay ") {
			return
		}
		bridge.mu.Lock()
		bridge.gatheredCandidates = append(bridge.gatheredCandidates, value)
		bridge.mu.Unlock()
	})
	peer.OnDataChannel(bridge.onDataChannel)
	peer.OnICEConnectionStateChange(func(state webrtc.ICEConnectionState) {
		if callbacks.OnICEState != nil {
			callbacks.OnICEState(state.String())
		}
	})
	peer.SCTP().Transport().ICETransport().OnSelectedCandidatePairChange(func(pair *webrtc.ICECandidatePair) {
		if pair == nil || pair.Local == nil || pair.Remote == nil || callbacks.OnSelectedCandidatePair == nil {
			return
		}
		callbacks.OnSelectedCandidatePair(SelectedCandidatePair{
			LocalType:  pair.Local.Typ.String(),
			RemoteType: pair.Remote.Typ.String(),
			Protocol:   pair.Local.Protocol.String(),
		})
	})
	peer.OnConnectionStateChange(func(state webrtc.PeerConnectionState) {
		if state == webrtc.PeerConnectionStateFailed && callbacks.OnFailure != nil {
			callbacks.OnFailure()
		}
	})
	return bridge, nil
}

func (b *BridgePeer) AcceptOffer(offerSDP string) (string, error) {
	if offerSDP == "" {
		return "", errors.New("offer SDP is required")
	}
	// Android WebView emits a fully gathered offer while retaining the
	// trickle-ICE capability attribute. Pion can otherwise keep the answerer
	// gatherer in checking and return an answer without its relay candidate.
	// Candidates are already embedded in this offer, so normalize only this
	// capability marker before handing the SDP to Pion.
	offerSDP = normalizeCompleteOfferSDP(offerSDP)
	b.mu.Lock()
	b.gatheredCandidates = nil
	b.mu.Unlock()
	if err := b.peer.SetRemoteDescription(webrtc.SessionDescription{
		Type: webrtc.SDPTypeOffer,
		SDP:  offerSDP,
	}); err != nil {
		return "", err
	}
	answer, err := b.peer.CreateAnswer(nil)
	if err != nil {
		return "", err
	}
	gatherComplete := webrtc.GatheringCompletePromise(b.peer)
	if err = b.peer.SetLocalDescription(answer); err != nil {
		return "", err
	}
	<-gatherComplete
	local := b.peer.LocalDescription()
	if local == nil {
		return "", errors.New("local WebRTC answer is unavailable")
	}
	// TURN allocation can finish just after Pion reports gathering complete for
	// browser offers that advertise trickle ICE. Keep the peer alive for a short,
	// bounded grace period so the answer is not returned without its relay
	// candidate and immediately torn down by Host's relay-only guard.
	if b.peer.GetConfiguration().ICETransportPolicy == webrtc.ICETransportPolicyRelay &&
		!strings.Contains(local.SDP, " typ relay ") {
		deadline := time.Now().Add(60 * time.Second)
		for time.Now().Before(deadline) {
			time.Sleep(100 * time.Millisecond)
			local = b.peer.LocalDescription()
			if local != nil && strings.Contains(local.SDP, " typ relay ") {
				break
			}
		}
	}
	if local == nil {
		return "", errors.New("local WebRTC answer is unavailable")
	}
	return restoreGatheredRelayCandidates(local.SDP, b.relayCandidates()), nil
}

func normalizeCompleteOfferSDP(offerSDP string) string {
	offerSDP = trickleICEOptionsLine.ReplaceAllString(offerSDP, "")
	return endOfCandidatesLine.ReplaceAllString(offerSDP, "")
}

func (b *BridgePeer) relayCandidates() []string {
	b.mu.RLock()
	defer b.mu.RUnlock()
	return append([]string(nil), b.gatheredCandidates...)
}

func restoreGatheredRelayCandidates(sdp string, candidates []string) string {
	if strings.Contains(sdp, " typ relay ") {
		return sdp
	}
	lines := strings.Split(strings.ReplaceAll(sdp, "\r\n", "\n"), "\n")
	insertAt := -1
	for index, line := range lines {
		if line == "a=end-of-candidates" {
			insertAt = index
			break
		}
	}
	if insertAt < 0 {
		insertAt = len(lines)
	}
	for _, candidate := range candidates {
		if strings.Contains(candidate, " typ relay ") {
			lines = append(lines[:insertAt], append([]string{"a=" + candidate}, lines[insertAt:]...)...)
			insertAt++
		}
	}
	return strings.Join(lines, "\r\n")
}

func (b *BridgePeer) Send(data []byte) error {
	b.mu.RLock()
	channel, open := b.channel, b.open
	b.mu.RUnlock()
	if channel == nil || !open {
		return errors.New("DataChannel is not open")
	}
	return channel.Send(data)
}

func (b *BridgePeer) Close() error {
	return b.peer.Close()
}

func (b *BridgePeer) onDataChannel(channel *webrtc.DataChannel) {
	if channel.Label() != ChannelLabel {
		_ = channel.Close()
		return
	}
	b.mu.Lock()
	if b.channel != nil {
		b.mu.Unlock()
		_ = channel.Close()
		return
	}
	b.channel = channel
	b.mu.Unlock()
	channel.OnOpen(func() {
		b.mu.Lock()
		b.open = true
		b.mu.Unlock()
		if b.callbacks.OnOpen != nil {
			b.callbacks.OnOpen()
		}
	})
	channel.OnMessage(func(message webrtc.DataChannelMessage) {
		if message.IsString {
			if b.callbacks.OnFailure != nil {
				b.callbacks.OnFailure()
			}
			return
		}
		if b.callbacks.OnData == nil {
			return
		}
		b.callbacks.OnData(append([]byte(nil), message.Data...))
	})
	channel.OnClose(func() {
		b.mu.Lock()
		b.open = false
		b.mu.Unlock()
		if b.callbacks.OnClose != nil {
			b.callbacks.OnClose()
		}
	})
}
