package main

import (
	"bufio"
	"encoding/base64"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"os"
	"strings"
	"sync"

	"codexbridge/transport/internal/hostpipe"
	"codexbridge/transport/internal/pipeframe"
	"codexbridge/transport/internal/webrtcpeer"
	"github.com/pion/webrtc/v4"
)

func main() {
	pipeName := flag.String("pipe", "", "Host named pipe")
	protocolVersion := flag.Int("protocol-version", 0, "Host pipe protocol version")
	flag.Parse()
	if *pipeName == "" || *protocolVersion != hostpipe.ProtocolVersion {
		log.Fatal("invalid transport bootstrap arguments")
	}

	secretLine, err := bufio.NewReader(io.LimitReader(os.Stdin, 128)).ReadString('\n')
	if err != nil {
		log.Fatal("bootstrap secret is unavailable")
	}
	secretLine = trimLine(secretLine)
	secret, err := base64.RawURLEncoding.DecodeString(secretLine)
	secretLine = ""
	if err != nil || len(secret) != 32 {
		log.Fatal("bootstrap secret is invalid")
	}
	defer clear(secret)

	connection, err := hostpipe.Connect(*pipeName)
	if err != nil {
		log.Fatal("Host pipe is unavailable")
	}
	defer connection.Close()
	reader, err := hostpipe.Authenticate(connection, secret, *protocolVersion)
	if err != nil {
		log.Fatal("Host pipe authentication failed")
	}
	fmt.Println("TRANSPORT_READY=1")
	var writerMu sync.Mutex
	write := func(kind pipeframe.Kind, payload []byte) {
		writerMu.Lock()
		defer writerMu.Unlock()
		if err := pipeframe.Write(connection, pipeframe.Message{Kind: kind, Payload: payload}); err != nil {
			_ = connection.Close()
		}
	}
	var peer *webrtcpeer.BridgePeer
	defer func() {
		if peer != nil {
			_ = peer.Close()
		}
	}()
	for {
		message, readErr := pipeframe.Read(reader)
		if readErr != nil {
			return
		}
		switch message.Kind {
		case pipeframe.Offer:
			if peer != nil {
				write(pipeframe.Error, nil)
				continue
			}
			offerSDP := string(message.Payload)
			configuration := relayOnlyConfiguration(webrtc.Configuration{})
			log.Println("transport_ice_policy=relay")
			if strings.HasPrefix(strings.TrimSpace(offerSDP), "{") {
				var configured struct {
					SDP        string             `json:"sdp"`
					ICEServers []webrtc.ICEServer `json:"iceServers"`
				}
				if json.Unmarshal(message.Payload, &configured) != nil || configured.SDP == "" {
					write(pipeframe.Error, nil)
					continue
				}
				offerSDP = configured.SDP
				configuration.ICEServers = configured.ICEServers
			}
			urlCount := 0
			for _, server := range configuration.ICEServers {
				urlCount += len(server.URLs)
			}
			if payload, marshalErr := json.Marshal(map[string]string{
				"event": "ice-config",
				"state": fmt.Sprintf("servers_%d_urls_%d", len(configuration.ICEServers), urlCount),
			}); marshalErr == nil {
				write(pipeframe.Diagnostic, payload)
			}
			callbacks := webrtcpeer.BridgeCallbacks{
				OnOpen:  func() { write(pipeframe.ChannelOpen, nil) },
				OnData:  func(data []byte) { write(pipeframe.RemoteData, data) },
				OnClose: func() { write(pipeframe.ChannelClosed, nil) },
				OnICEState: func(state string) {
					payload, marshalErr := json.Marshal(map[string]string{"event": "ice-state", "state": state})
					if marshalErr == nil {
						write(pipeframe.Diagnostic, payload)
					}
				},
				OnSelectedCandidatePair: func(pair webrtcpeer.SelectedCandidatePair) {
					payload, marshalErr := json.Marshal(pair)
					if marshalErr == nil {
						write(pipeframe.Diagnostic, payload)
					}
				},
				OnFailure: func() {
					write(pipeframe.Error, nil)
				},
			}
			var answer string
			active := true
			channelReady := false
			attemptCallbacks := callbacks
			attemptCallbacks.OnOpen = func() { if active { channelReady = true; callbacks.OnOpen() } }
			attemptCallbacks.OnData = func(data []byte) { if active { callbacks.OnData(data) } }
			attemptCallbacks.OnClose = func() { if active && channelReady { callbacks.OnClose() } }
			attemptCallbacks.OnFailure = func() { if active && channelReady { callbacks.OnFailure() } }
			attemptCallbacks.OnICEState = func(state string) { if active { callbacks.OnICEState(state) } }
			attemptCallbacks.OnSelectedCandidatePair = func(pair webrtcpeer.SelectedCandidatePair) { if active { callbacks.OnSelectedCandidatePair(pair) } }
			peer, err = webrtcpeer.NewBridgePeerWithConfiguration(attemptCallbacks, configuration)
			if err == nil {
				answer, err = peer.AcceptOffer(offerSDP)
			}
			if err != nil || peer == nil {
				errorCode := "offer_final_failed"
				if err != nil { errorCode = classifyOfferError(err) }
				writeOfferError(write, errorCode)
				write(pipeframe.Error, nil)
				continue
			}
			write(pipeframe.Answer, []byte(answer))
		case pipeframe.HostData:
			if peer == nil || peer.Send(message.Payload) != nil {
				write(pipeframe.Error, nil)
			}
		case pipeframe.Close:
			return
		default:
			write(pipeframe.Error, nil)
		}
	}
}

func writeOfferError(write func(pipeframe.Kind, []byte), code string) {
	payload, err := json.Marshal(map[string]string{"event": "offer-error", "state": code})
	if err == nil { write(pipeframe.Diagnostic, payload) }
}

func classifyOfferError(err error) string {
	message := strings.ToLower(err.Error())
	switch {
	case strings.Contains(message, "set remote"):
		return "set_remote_failed"
	case strings.Contains(message, "create answer"):
		return "create_answer_failed"
	case strings.Contains(message, "set local"):
		return "set_local_failed"
	default:
		return "offer_accept_failed"
	}
}

func relayOnlyConfiguration(configuration webrtc.Configuration) webrtc.Configuration {
	configuration.ICETransportPolicy = webrtc.ICETransportPolicyRelay
	return configuration
}

func trimLine(value string) string {
	for len(value) > 0 && (value[len(value)-1] == '\r' || value[len(value)-1] == '\n') {
		value = value[:len(value)-1]
	}
	return value
}
