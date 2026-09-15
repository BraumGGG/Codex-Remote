package pipeframe

import (
	"encoding/binary"
	"errors"
	"fmt"
	"io"
)

type Kind byte

const (
	Offer         Kind = 1
	HostData      Kind = 2
	Close         Kind = 3
	Answer        Kind = 11
	RemoteData    Kind = 12
	ChannelOpen   Kind = 13
	ChannelClosed Kind = 14
	Error         Kind = 15
	Diagnostic    Kind = 16
	MaxPayload         = 128 * 1024
)

type Message struct {
	Kind    Kind
	Payload []byte
}

func Write(writer io.Writer, message Message) error {
	if err := validate(message.Kind, len(message.Payload)); err != nil {
		return err
	}
	header := [5]byte{byte(message.Kind)}
	binary.BigEndian.PutUint32(header[1:], uint32(len(message.Payload)))
	if _, err := writer.Write(header[:]); err != nil {
		return err
	}
	if len(message.Payload) > 0 {
		_, err := writer.Write(message.Payload)
		return err
	}
	return nil
}

func Read(reader io.Reader) (Message, error) {
	header := [5]byte{}
	if _, err := io.ReadFull(reader, header[:]); err != nil {
		return Message{}, err
	}
	kind := Kind(header[0])
	length := int(binary.BigEndian.Uint32(header[1:]))
	if err := validate(kind, length); err != nil {
		return Message{}, err
	}
	payload := make([]byte, length)
	if _, err := io.ReadFull(reader, payload); err != nil {
		return Message{}, err
	}
	return Message{Kind: kind, Payload: payload}, nil
}

func validate(kind Kind, length int) error {
	switch kind {
	case Offer, HostData, Close, Answer, RemoteData, ChannelOpen, ChannelClosed, Error, Diagnostic:
	default:
		return fmt.Errorf("unknown pipe frame kind: %d", kind)
	}
	if length < 0 || length > MaxPayload {
		return errors.New("pipe frame payload exceeds its limit")
	}
	return nil
}
