//go:build windows

package hostpipe

import (
	"errors"
	"fmt"
	"os"

	"golang.org/x/sys/windows"
)

func Connect(pipeName string) (*os.File, error) {
	if pipeName == "" {
		return nil, fmt.Errorf("pipe name is required")
	}
	path := `\\.\pipe\` + pipeName
	pathPointer, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return nil, fmt.Errorf("invalid pipe name: %w", err)
	}
	handle, err := windows.CreateFile(
		pathPointer,
		windows.GENERIC_READ|windows.GENERIC_WRITE,
		0,
		nil,
		windows.OPEN_EXISTING,
		windows.FILE_FLAG_OVERLAPPED,
		0,
	)
	if err != nil {
		return nil, err
	}
	file := os.NewFile(uintptr(handle), path)
	if file == nil {
		_ = windows.CloseHandle(handle)
		return nil, errors.New("named pipe handle could not be wrapped")
	}
	return file, nil
}
