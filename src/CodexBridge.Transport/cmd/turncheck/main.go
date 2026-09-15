package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"bytes"
	"net/http"
	"os"
	"strings"
	"sync"
	"time"

	"codexbridge/transport/internal/webrtcpeer"
	"github.com/pion/webrtc/v4"
)

type result struct {
	URL           string `json:"url"`
	CandidateType string `json:"candidateType"`
	RelayAddress  string `json:"relayAddress"`
}

func main() {
	turnURL := os.Getenv("TURN_CHECK_URL")
	username := os.Getenv("TURN_CHECK_USERNAME")
	credential := os.Getenv("TURN_CHECK_CREDENTIAL")
	if turnURL == "" || username == "" || credential == "" {
		fail(errors.New("TURN_CHECK_URL, TURN_CHECK_USERNAME and TURN_CHECK_CREDENTIAL are required"))
	}
	if os.Getenv("TURN_CHECK_PAIR") == "1" {
		peerUsername := os.Getenv("TURN_CHECK_USERNAME_2")
		peerCredential := os.Getenv("TURN_CHECK_CREDENTIAL_2")
		if peerUsername == "" {
			peerUsername = username
		}
		if peerCredential == "" {
			peerCredential = credential
		}
		var err error
		if harness := os.Getenv("TURN_CHECK_HARNESS_URL"); harness != "" {
			err = checkHarnessRelayPair(harness, turnURL, username, credential)
		} else if os.Getenv("TURN_CHECK_BRIDGE") == "1" {
			err = checkBridgeRelayPair(turnURL, username, credential, peerUsername, peerCredential)
		} else {
			err = checkRelayPair(turnURL, username, credential, peerUsername, peerCredential)
		}
		if err != nil {
			fail(err)
		}
		_ = json.NewEncoder(os.Stdout).Encode(map[string]string{
			"url": turnURL, "candidateType": "relay", "dataChannel": "open",
		})
		return
	}

	peer, err := webrtc.NewPeerConnection(webrtc.Configuration{
		ICEServers: []webrtc.ICEServer{{
			URLs:       []string{turnURL},
			Username:   username,
			Credential: credential,
		}},
		ICETransportPolicy: webrtc.ICETransportPolicyRelay,
	})
	if err != nil {
		fail(err)
	}
	defer peer.Close()

	if _, err = peer.CreateDataChannel("turn-check", nil); err != nil {
		fail(err)
	}

	candidates := make(chan string, 1)
	done := make(chan struct{})
	peer.OnICECandidate(func(candidate *webrtc.ICECandidate) {
		if candidate == nil {
			close(done)
			return
		}
		value := candidate.ToJSON().Candidate
		if strings.Contains(value, " typ relay ") {
			select {
			case candidates <- value:
			default:
			}
		}
	})

	offer, err := peer.CreateOffer(nil)
	if err != nil {
		fail(err)
	}
	if err = peer.SetLocalDescription(offer); err != nil {
		fail(err)
	}

	var candidate string
	select {
	case candidate = <-candidates:
	case <-done:
		fail(errors.New("ICE gathering completed without a relay candidate"))
	case <-time.After(20 * time.Second):
		fail(errors.New("timed out waiting for a relay candidate"))
	}

	fields := strings.Fields(candidate)
	relayAddress := "unknown"
	if len(fields) >= 6 {
		relayAddress = fmt.Sprintf("%s:%s/%s", fields[4], fields[5], fields[2])
	}
	_ = json.NewEncoder(os.Stdout).Encode(result{
		URL:           turnURL,
		CandidateType: "relay",
		RelayAddress:  relayAddress,
	})
}

func checkHarnessRelayPair(harnessURL, turnURL, username, credential string) error {
	offerer, err := webrtc.NewPeerConnection(webrtc.Configuration{ICEServers: []webrtc.ICEServer{{URLs: []string{turnURL}, Username: username, Credential: credential}}, ICETransportPolicy: webrtc.ICETransportPolicyRelay})
	if err != nil { return err }
	defer offerer.Close()
	received := make(chan struct{})
	var once sync.Once
	answererChannel, err := offerer.CreateDataChannel(webrtcpeer.ChannelLabel, nil)
	if err != nil { return err }
	answererChannel.OnOpen(func() { once.Do(func() { close(received) }) })
	offer, err := offerer.CreateOffer(nil)
	if err != nil { return err }
	gathered := webrtc.GatheringCompletePromise(offerer)
	if err = offerer.SetLocalDescription(offer); err != nil { return err }
	<-gathered
	offerSDP := offerer.LocalDescription().SDP
	if os.Getenv("TURN_CHECK_TRICKLE") == "1" && !strings.Contains(offerSDP, "a=ice-options:trickle") {
		offerSDP = strings.Replace(offerSDP, "\r\na=ice-ufrag:", "\r\na=ice-options:trickle\r\na=ice-ufrag:", 1)
	}
	urls := []string{turnURL}
	if os.Getenv("TURN_CHECK_MULTI") == "1" {
		urls = []string{turnURL, "turns:remote.example.invalid:5349?transport=tcp", "turn:remote.example.invalid:3478?transport=tcp"}
	}
	body, _ := json.Marshal(map[string]any{
		"type": "offer", "sdp": offerSDP,
		"iceServers": []map[string]any{{"urls": urls, "username": username, "credential": credential}},
	})
	resp, err := http.Post(strings.TrimRight(harnessURL, "/")+"/offer", "application/json", bytes.NewReader(body))
	if err != nil { return err }
	defer resp.Body.Close()
	if resp.StatusCode/100 != 2 { return fmt.Errorf("harness returned HTTP %d", resp.StatusCode) }
	var answer struct { Type string `json:"type"`; Sdp string `json:"sdp"` }
	if err = json.NewDecoder(resp.Body).Decode(&answer); err != nil { return err }
	if answer.Type != "answer" || answer.Sdp == "" { return errors.New("harness answer is invalid") }
	if err = offerer.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: answer.Sdp}); err != nil { return err }
	select { case <-received: return nil; case <-time.After(20 * time.Second): return errors.New("harness relay data channel did not open") }
}

func checkBridgeRelayPair(turnURL, username, credential, peerUsername, peerCredential string) error {
	offerer, err := webrtc.NewPeerConnection(webrtc.Configuration{
		ICEServers: []webrtc.ICEServer{{
			URLs: []string{turnURL}, Username: username, Credential: credential,
		}},
		ICETransportPolicy: webrtc.ICETransportPolicyRelay,
	})
	if err != nil {
		return err
	}
	defer offerer.Close()

	received := make(chan struct{})
	var receivedOnce sync.Once
	answerer, err := webrtcpeer.NewBridgePeerWithConfiguration(webrtcpeer.BridgeCallbacks{
		OnData: func(data []byte) {
			if string(data) == "turn-bridge-check" {
				receivedOnce.Do(func() { close(received) })
			}
		},
	}, webrtc.Configuration{
		ICEServers: []webrtc.ICEServer{{
			URLs: []string{turnURL}, Username: peerUsername, Credential: peerCredential,
		}},
		ICETransportPolicy: webrtc.ICETransportPolicyRelay,
	})
	if err != nil {
		return err
	}
	defer answerer.Close()

	channel, err := offerer.CreateDataChannel(webrtcpeer.ChannelLabel, nil)
	if err != nil {
		return err
	}
	channel.OnOpen(func() { _ = channel.Send([]byte("turn-bridge-check")) })
	offer, err := offerer.CreateOffer(nil)
	if err != nil {
		return err
	}
	gathered := webrtc.GatheringCompletePromise(offerer)
	if err = offerer.SetLocalDescription(offer); err != nil {
		return err
	}
	<-gathered
	answerSDP, err := answerer.AcceptOffer(offerer.LocalDescription().SDP)
	if err != nil {
		return err
	}
	if err = offerer.SetRemoteDescription(webrtc.SessionDescription{
		Type: webrtc.SDPTypeAnswer,
		SDP:  answerSDP,
	}); err != nil {
		return err
	}

	select {
	case <-received:
		return nil
	case <-time.After(20 * time.Second):
		return errors.New("BridgePeer relay data channel did not open")
	}
}

func checkRelayPair(turnURL, username, credential, peerUsername, peerCredential string) error {
	offererConfiguration := webrtc.Configuration{
		ICEServers: []webrtc.ICEServer{{
			URLs: []string{turnURL}, Username: username, Credential: credential,
		}},
		ICETransportPolicy: webrtc.ICETransportPolicyRelay,
	}
	answererConfiguration := webrtc.Configuration{
		ICEServers: []webrtc.ICEServer{{
			URLs: []string{turnURL}, Username: peerUsername, Credential: peerCredential,
		}},
		ICETransportPolicy: webrtc.ICETransportPolicyRelay,
	}
	offerer, err := webrtc.NewPeerConnection(offererConfiguration)
	if err != nil {
		return err
	}
	defer offerer.Close()
	answerer, err := webrtc.NewPeerConnection(answererConfiguration)
	if err != nil {
		return err
	}
	defer answerer.Close()

	received := make(chan struct{})
	var receivedOnce sync.Once
	answerer.OnDataChannel(func(channel *webrtc.DataChannel) {
		channel.OnMessage(func(message webrtc.DataChannelMessage) {
			if string(message.Data) == "turn-pair-check" {
				receivedOnce.Do(func() { close(received) })
			}
		})
	})
	channel, err := offerer.CreateDataChannel("turn-pair-check", nil)
	if err != nil {
		return err
	}
	channel.OnOpen(func() { _ = channel.Send([]byte("turn-pair-check")) })

	offer, err := offerer.CreateOffer(nil)
	if err != nil {
		return err
	}
	offerGathered := webrtc.GatheringCompletePromise(offerer)
	if err = offerer.SetLocalDescription(offer); err != nil {
		return err
	}
	<-offerGathered
	if err = answerer.SetRemoteDescription(*offerer.LocalDescription()); err != nil {
		return err
	}
	answer, err := answerer.CreateAnswer(nil)
	if err != nil {
		return err
	}
	answerGathered := webrtc.GatheringCompletePromise(answerer)
	if err = answerer.SetLocalDescription(answer); err != nil {
		return err
	}
	<-answerGathered
	if err = offerer.SetRemoteDescription(*answerer.LocalDescription()); err != nil {
		return err
	}

	select {
	case <-received:
		return nil
	case <-time.After(20 * time.Second):
		return errors.New("relay candidates were allocated but the data channel did not open")
	}
}

func fail(err error) {
	fmt.Fprintln(os.Stderr, err)
	os.Exit(1)
}
