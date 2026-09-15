package hostpipe

import (
	"bufio"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
)

const ProtocolVersion = 1

type challengeMessage struct {
	Type            string `json:"type"`
	ProtocolVersion int    `json:"protocolVersion"`
	Challenge       string `json:"challenge"`
}

type authenticationMessage struct {
	Type            string `json:"type"`
	ProtocolVersion int    `json:"protocolVersion"`
	Proof           string `json:"proof"`
}

func CreateProof(secret []byte, role string, challenge string, protocolVersion int) string {
	mac := hmac.New(sha256.New, secret)
	_, _ = fmt.Fprintf(mac, "codex-bridge-pipe-v1|%s|%d|%s", role, protocolVersion, challenge)
	return base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
}

func Authenticate(connection io.ReadWriter, secret []byte, protocolVersion int) (*bufio.Reader, error) {
	if len(secret) != 32 {
		return nil, errors.New("bootstrap secret must be 256 bits")
	}
	if protocolVersion != ProtocolVersion {
		return nil, errors.New("unsupported pipe protocol version")
	}

	reader := bufio.NewReader(connection)
	writer := bufio.NewWriter(connection)
	var challenge challengeMessage
	if err := readJSONLine(reader, &challenge); err != nil {
		return nil, fmt.Errorf("read host challenge: %w", err)
	}
	if challenge.Type != "challenge" || challenge.ProtocolVersion != protocolVersion || challenge.Challenge == "" {
		return nil, errors.New("invalid host challenge")
	}

	request := authenticationMessage{
		Type:            "authenticate",
		ProtocolVersion: protocolVersion,
		Proof:           CreateProof(secret, "client", challenge.Challenge, protocolVersion),
	}
	if err := writeJSONLine(writer, request); err != nil {
		return nil, fmt.Errorf("write client proof: %w", err)
	}

	var response authenticationMessage
	if err := readJSONLine(reader, &response); err != nil {
		return nil, fmt.Errorf("read host proof: %w", err)
	}
	if response.Type != "authenticated" ||
		response.ProtocolVersion != protocolVersion ||
		!hmac.Equal(
			mustDecodeProof(response.Proof),
			mustDecodeProof(CreateProof(secret, "host", challenge.Challenge, protocolVersion))) {
		return nil, errors.New("host authentication failed")
	}
	return reader, nil
}

func readJSONLine(reader *bufio.Reader, destination any) error {
	line, err := reader.ReadBytes('\n')
	if err != nil {
		return err
	}
	if len(line) > 4096 {
		return errors.New("authentication frame is too large")
	}
	return json.Unmarshal(line, destination)
}

func writeJSONLine(writer *bufio.Writer, value any) error {
	encoded, err := json.Marshal(value)
	if err != nil {
		return err
	}
	if _, err = writer.Write(encoded); err != nil {
		return err
	}
	if err = writer.WriteByte('\n'); err != nil {
		return err
	}
	return writer.Flush()
}

func mustDecodeProof(value string) []byte {
	decoded, err := base64.RawURLEncoding.DecodeString(value)
	if err != nil {
		return nil
	}
	return decoded
}
