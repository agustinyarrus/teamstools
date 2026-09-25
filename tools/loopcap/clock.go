package main

// clock.go — el RELOJ de la grabación, en la misma unidad que usa WASAPI para marcar cada paquete.
//
// IAudioCaptureClient::GetBuffer devuelve, por paquete, el instante del contador de rendimiento (QPC) en que el
// dispositivo capturó su primer frame, en unidades de 100 ns. Con el QPC del arranque como cero, la posición
// EXACTA de ese paquete en el archivo es (qpc − cero) · rate: así una pista que retoma después de un silencio,
// o que se sumó tarde, cae en su lugar al milisegundo, sin heurísticas de «cuánto tardó en llegar».

import (
	"syscall"
	"unsafe"
)

var (
	procQueryPerformanceCounter   = modkernel32.NewProc("QueryPerformanceCounter")
	procQueryPerformanceFrequency = modkernel32.NewProc("QueryPerformanceFrequency")
)

// qpc100ns: el QPC de ahora en unidades de 100 ns (las de WASAPI). Sin desborde: parte entera y resto aparte.
func qpc100ns() uint64 {
	var c, f int64
	syscall.SyscallN(procQueryPerformanceCounter.Addr(), uintptr(unsafe.Pointer(&c)))
	syscall.SyscallN(procQueryPerformanceFrequency.Addr(), uintptr(unsafe.Pointer(&f)))
	if f <= 0 {
		return 0
	}
	return uint64(c/f)*10_000_000 + uint64(c%f)*10_000_000/uint64(f)
}

// recClock: el cero de la grabación en QPC.
type recClock struct{ zero uint64 }

func newRecClock() recClock { return recClock{zero: qpc100ns()} }

// frameAt: frame de una pista a `rate` que corresponde al instante qpc (100 ns). 0 si es anterior al cero.
func (c recClock) frameAt(qpc uint64, rate int) int64 {
	if qpc <= c.zero {
		return 0
	}
	d := qpc - c.zero
	return int64(d/10_000_000)*int64(rate) + int64(d%10_000_000)*int64(rate)/10_000_000
}

// frameNow: el frame que corresponde a este instante.
func (c recClock) frameNow(rate int) int64 { return c.frameAt(qpc100ns(), rate) }
