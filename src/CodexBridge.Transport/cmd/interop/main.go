package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"sync"
	"time"

	"codexbridge/transport/internal/webrtcpeer"
)

type sessionDescription struct {
	Type string `json:"type"`
	SDP  string `json:"sdp"`
}

func main() {
	webRoot := flag.String("web-root", "", "directory containing the interop browser page")
	listenAddress := flag.String("listen-address", "127.0.0.1:0", "diagnostic-only HTTP listen address")
	flag.Parse()
	if *webRoot == "" {
		log.Fatal("--web-root is required")
	}

	absoluteRoot, err := filepath.Abs(*webRoot)
	if err != nil {
		log.Fatal(err)
	}
	if _, err = os.Stat(filepath.Join(absoluteRoot, "webrtc-interop.html")); err != nil {
		log.Fatalf("interop page is unavailable: %v", err)
	}

	var peersMu sync.Mutex
	var peers []*webrtcpeer.BridgePeer
	var channelOpen bool
	var messageSeen bool
	defer func() {
		peersMu.Lock()
		defer peersMu.Unlock()
		for _, peer := range peers {
			_ = peer.Close()
		}
	}()

	listener, err := net.Listen("tcp", *listenAddress)
	if err != nil {
		log.Fatal(err)
	}

	mux := http.NewServeMux()
	server := &http.Server{Handler: mux, ReadHeaderTimeout: 5 * time.Second}
	mux.Handle("/", http.FileServer(http.Dir(absoluteRoot)))
	mux.HandleFunc("/offer", func(response http.ResponseWriter, request *http.Request) {
		if request.Method != http.MethodPost {
			http.Error(response, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		defer request.Body.Close()

		var incoming sessionDescription
		decoder := json.NewDecoder(http.MaxBytesReader(response, request.Body, 64*1024))
		decoder.DisallowUnknownFields()
		if err := decoder.Decode(&incoming); err != nil || incoming.Type != "offer" {
			http.Error(response, "invalid offer", http.StatusBadRequest)
			return
		}

		var peer *webrtcpeer.BridgePeer
		peer, err = webrtcpeer.NewBridgePeer(webrtcpeer.BridgeCallbacks{
			OnOpen: func() {
				peersMu.Lock()
				channelOpen = true
				peersMu.Unlock()
			},
			OnData: func(data []byte) {
				peersMu.Lock()
				messageSeen = true
				peersMu.Unlock()
				_ = peer.Send([]byte("pion->browser"))
			},
		})
		if err == nil {
			peersMu.Lock()
			peers = append(peers, peer)
			peersMu.Unlock()
		}
		var answerSDP string
		if err == nil {
			answerSDP, err = peer.AcceptOffer(incoming.SDP)
		}
		if err != nil {
			if peer != nil {
				_ = peer.Close()
			}
			http.Error(response, "offer rejected", http.StatusBadRequest)
			return
		}

		response.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(response).Encode(sessionDescription{Type: "answer", SDP: answerSDP})
	})
	mux.HandleFunc("/result", func(response http.ResponseWriter, _ *http.Request) {
		response.Header().Set("Content-Type", "application/json")
		peersMu.Lock()
		count := len(peers)
		opened := channelOpen
		seen := messageSeen
		peersMu.Unlock()
		_ = json.NewEncoder(response).Encode(map[string]any{
			"peerCount":   count,
			"channelOpen": opened,
			"messageSeen": seen,
		})
	})
	mux.HandleFunc("/shutdown", func(response http.ResponseWriter, request *http.Request) {
		if request.Method != http.MethodPost {
			http.Error(response, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		response.WriteHeader(http.StatusNoContent)
		go func() {
			ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
			defer cancel()
			_ = server.Shutdown(ctx)
		}()
	})

	url := fmt.Sprintf("http://%s/webrtc-interop.html", listener.Addr().String())
	fmt.Printf("INTEROP_URL=%s\n", url)
	if err = server.Serve(listener); err != nil && err != http.ErrServerClosed {
		log.Fatal(err)
	}
}
