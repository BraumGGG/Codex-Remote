package webrtcpeer

import (
	"errors"
	"sync"

	"github.com/pion/webrtc/v4"
)

const (
	ChannelLabel   = "codex-bridge-v1"
	BrowserMessage = "browser->pion"
	PionMessage    = "pion->browser"
)

type Answerer struct {
	mu             sync.RWMutex
	peerConnection *webrtc.PeerConnection
	channelOpen    bool
	messageSeen    bool
}

type Status struct {
	ChannelOpen bool `json:"channelOpen"`
	MessageSeen bool `json:"messageSeen"`
}

func NewAnswerer() (*Answerer, error) {
	settingEngine := webrtc.SettingEngine{}
	settingEngine.SetIncludeLoopbackCandidate(true)
	api := webrtc.NewAPI(webrtc.WithSettingEngine(settingEngine))
	peerConnection, err := api.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		return nil, err
	}

	answerer := &Answerer{peerConnection: peerConnection}
	peerConnection.OnDataChannel(answerer.handleDataChannel)
	return answerer, nil
}

func (a *Answerer) AcceptOffer(offer webrtc.SessionDescription) (webrtc.SessionDescription, error) {
	if offer.Type != webrtc.SDPTypeOffer || offer.SDP == "" {
		return webrtc.SessionDescription{}, errors.New("invalid WebRTC offer")
	}

	if err := a.peerConnection.SetRemoteDescription(offer); err != nil {
		return webrtc.SessionDescription{}, err
	}

	answer, err := a.peerConnection.CreateAnswer(nil)
	if err != nil {
		return webrtc.SessionDescription{}, err
	}

	gatherComplete := webrtc.GatheringCompletePromise(a.peerConnection)
	if err = a.peerConnection.SetLocalDescription(answer); err != nil {
		return webrtc.SessionDescription{}, err
	}
	<-gatherComplete

	local := a.peerConnection.LocalDescription()
	if local == nil {
		return webrtc.SessionDescription{}, errors.New("local WebRTC answer is unavailable")
	}
	return *local, nil
}

func (a *Answerer) Status() Status {
	a.mu.RLock()
	defer a.mu.RUnlock()
	return Status{ChannelOpen: a.channelOpen, MessageSeen: a.messageSeen}
}

func (a *Answerer) Close() error {
	return a.peerConnection.Close()
}

func (a *Answerer) handleDataChannel(channel *webrtc.DataChannel) {
	if channel.Label() != ChannelLabel {
		_ = channel.Close()
		return
	}

	channel.OnOpen(func() {
		a.mu.Lock()
		a.channelOpen = true
		a.mu.Unlock()
	})
	channel.OnMessage(func(message webrtc.DataChannelMessage) {
		if !message.IsString || string(message.Data) != BrowserMessage {
			return
		}

		a.mu.Lock()
		a.messageSeen = true
		a.mu.Unlock()
		_ = channel.SendText(PionMessage)
	})
}
