package main

// capture.go — una PISTA por endpoint y sus sesiones de captura.
//
// Una pista vive toda la grabación; sus SESIONES de captura van y vienen: el endpoint se desconecta (auricular
// Bluetooth que se apaga), vuelve, o se suelta a propósito porque nadie lo usa (micrófono / manos libres).
// Cada sesión arranca alineando la pista con el reloj de la grabación: lo que faltó (la pista se sumó tarde,
// o estuvo afuera un rato) se escribe como SILENCIO, así todas las pistas empiezan en el mismo cero y se
// pueden mezclar sin correr nada.
//
// Sin «bomba de silencio» (v2 abría un stream de render mudo en cada salida para que el loopback no se
// durmiera): en una salida de manos libres ese stream despertaba el perfil de llamada del auricular. Ahora el
// reloj es el QPC de la grabación (clock.go), el mismo con el que WASAPI marca cada paquete:
//   · mientras nadie suena (no hay paquetes), el archivo se rellena con silencio hasta 100 ms antes de «ahora»;
//   · al RETOMAR (tras un silencio o un salto marcado por el driver), el primer paquete se ubica en su instante
//     exacto: lo que falte entre el relleno y ese instante va como silencio. Error: ~1 ms, no ~250 ms.
//   · con el audio corriendo seguido NO se reubica nada: la deriva natural del reloj del dispositivo (decenas de
//     ppm) metería microcortes audibles; se tolera (unos 0,2 s por hora, irrelevante para mezclar voz).

import (
	"fmt"
	"math"
	"runtime"
	"sync"
	"sync/atomic"
	"time"
	"unsafe"
)

const (
	gapFill       = 250 * time.Millisecond // sin paquetes por más que esto = silencio real (nadie suena)
	fillMargin    = 0.1                    // el relleno llega hasta 100 ms antes de «ahora»: el próximo paquete ubica el resto
	resumeAfter   = 30 * time.Millisecond  // sin paquetes por más que esto, el siguiente se reubica por su marca QPC
	resyncMin     = 0.005                  // atrasos menores a 5 ms no se rellenan (ruido de la marca)
	errBackoffMin = time.Second
	errBackoffMax = 30 * time.Second
)

type trackKind int

const (
	trackLoop trackKind = iota // lo que SALE por una salida (loopback)
	trackMic                   // lo que ENTRA por un micrófono
)

func (k trackKind) String() string {
	if k == trackMic {
		return "mic"
	}
	return "salida"
}

type recorder struct {
	idx       int    // orden de llegada: la identidad de la pista en el protocolo de niveles
	key       string // clave en el registro: ID del endpoint (+ "#n" si es una pista de reemplazo)
	id        string // ID del endpoint
	name      string
	kind      trackKind
	handsFree bool
	path      string
	joined    float64 // segundos desde el arranque en que se sumó

	// los pone la PRIMERA sesión; después solo se leen (publicados con ready, que da el orden)
	w     *wavWriter
	rate  int
	srcCh int
	sk    sampleKind

	frames   atomic.Int64
	peakBits atomic.Uint32 // pico de toda la grabación
	tickBits atomic.Uint32 // pico desde el último reporte de estado (1 s)
	lvlBits  atomic.Uint32 // pico desde el último nivel emitido (50 ms)

	running   atomic.Bool  // hay una sesión de captura en curso
	gone      atomic.Bool  // la última sesión terminó porque el endpoint se fue (el vigía la reabre si vuelve)
	retired   atomic.Bool  // volvió con otro formato: esta pista se cierra y el audio sigue en una nueva
	idle      atomic.Bool  // se soltó a propósito: nadie usa el endpoint (micrófono / manos libres)
	letGo     atomic.Bool  // pedido del vigía a la sesión en curso: «soltalo»
	paused    atomic.Bool  // pausada por disco casi lleno (solo pistas mudas)
	ready     atomic.Bool  // w/rate/srcCh/sk ya están puestos
	sessions  atomic.Int32 // cuántas veces se abrió
	retryAt   atomic.Int64 // no reabrir antes de este instante (UnixNano): espera creciente tras un error
	backoff   time.Duration
	lastInUse time.Time // último instante en que otro proceso lo usaba (solo lo toca el vigía)
	inUse     bool      // en la última pasada, otro proceso lo usaba (solo lo toca el vigía)
	saidGone  bool      // ya se avisó que se desconectó (solo lo toca plan, bajo el candado del registro)

	failMu sync.Mutex
	fail   error
}

func maxStore(b *atomic.Uint32, p float32) {
	for {
		cur := math.Float32frombits(b.Load())
		if p <= cur || b.CompareAndSwap(math.Float32bits(cur), math.Float32bits(p)) {
			return
		}
	}
}

func (r *recorder) bumpPeak(p float32) {
	maxStore(&r.peakBits, p)
	maxStore(&r.tickBits, p)
	maxStore(&r.lvlBits, p)
}

func (r *recorder) peak() float32      { return math.Float32frombits(r.peakBits.Load()) }
func (r *recorder) takeTick() float32  { return math.Float32frombits(r.tickBits.Swap(0)) }
func (r *recorder) takeLevel() float32 { return math.Float32frombits(r.lvlBits.Swap(0)) }

func (r *recorder) setFail(err error) {
	r.failMu.Lock()
	r.fail = err
	r.failMu.Unlock()
}

func (r *recorder) lastFail() error {
	r.failMu.Lock()
	defer r.failMu.Unlock()
	return r.fail
}

// state: una palabra para el estado JSON y la UI.
func (r *recorder) state() string {
	switch {
	case r.running.Load():
		return "grabando"
	case r.retired.Load():
		return "cerrada"
	case r.idle.Load():
		return "en espera"
	case r.gone.Load():
		return "desconectada"
	}
	return "abriendo"
}

// launch abre una sesión nueva sobre `dev`, que pasa a ser de la sesión SOLO si devuelve true (si no, sigue
// siendo de quien llamó, que la libera). La CAS sobre running garantiza UNA sesión por pista: los métodos de
// captura del escritor tienen un solo productor.
func launch(r *recorder, dev *IMMDevice, clk recClock, stop <-chan struct{}, wg *sync.WaitGroup) bool {
	if !r.running.CompareAndSwap(false, true) {
		return false
	}
	r.gone.Store(false)
	r.idle.Store(false)
	r.letGo.Store(false)
	wg.Add(1)
	go func() {
		defer wg.Done()
		defer r.running.Store(false)
		runSession(r, dev, clk, stop)
	}()
	return true
}

// runSession: una sesión de captura. Vuelve cuando cierran stop, cuando el endpoint se cae (gone), cuando el
// vigía la suelta (idle) o cuando el endpoint volvió con otro formato (retired).
func runSession(r *recorder, dev *IMMDevice, clk recClock, stop <-chan struct{}) {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	defer release(unsafe.Pointer(dev))
	if err := coInit(); err != nil {
		r.setFail(err)
		r.gone.Store(true)
		return
	}
	defer procCoUninitialize.Call()
	r.sessions.Add(1)
	invalidated, err := pumpOne(r, dev, clk, stop)
	switch {
	case err != nil:
		r.setFail(err)
		r.gone.Store(true)
		if r.backoff == 0 {
			r.backoff = errBackoffMin
		} else {
			r.backoff = min(2*r.backoff, errBackoffMax)
		}
		r.retryAt.Store(time.Now().Add(r.backoff).UnixNano())
		logf("\n[!] %s: %v · reintento en %s\n", r.name, err, r.backoff)
	case invalidated:
		r.gone.Store(true)
		logf("\n[!] %s se desconectó · lo grabado queda; si vuelve, sigue en la misma pista\n", r.name)
	default:
		r.backoff = 0
	}
}

// pumpOne hace una sesión de captura. Devuelve invalidated=true si Windows invalidó el endpoint.
func pumpOne(r *recorder, dev *IMMDevice, clk recClock, stop <-chan struct{}) (bool, error) {
	client, err := dev.activateAudioClient()
	if err != nil {
		return false, err
	}
	defer release(unsafe.Pointer(client))

	wfx, err := client.mixFormat()
	if err != nil {
		return false, err
	}
	defer procCoTaskMemFree.Call(uintptr(unsafe.Pointer(wfx)))
	sk, err := classify(wfx)
	if err != nil {
		return false, err
	}
	rate, srcCh := int(wfx.SamplesPerSec), int(wfx.Channels)

	if r.w == nil {
		outCh := srcCh
		if opts.mono {
			outCh = 1
		}
		bits := 16
		if opts.float32o {
			bits = 32
		}
		w, err := newWavWriter(r.path, rate, outCh, bits, opts.float32o)
		if err != nil {
			return false, err
		}
		r.w, r.rate, r.srcCh, r.sk = w, rate, srcCh, sk
		r.ready.Store(true) // happens-before: recién ahora el resto puede leer w y rate
		logf("  %-7s %-46s %d Hz %dch %s -> %s\n", r.kind, trunc(r.name, 46), rate, srcCh, sk, baseName(r.path))
	} else if rate != r.rate || srcCh != r.srcCh || sk != r.sk {
		logf("\n[!] %s volvió con otro formato (%d Hz/%dch): sigo en una pista nueva\n", r.name, rate, srcCh)
		r.retired.Store(true)
		return false, nil
	}

	var flags uint32
	if r.kind == trackLoop {
		flags = AUDCLNT_STREAMFLAGS_LOOPBACK
	}
	if err := client.initialize(flags, captureBufferHNS, wfx); err != nil {
		return false, err
	}
	cp, err := client.service(&IID_IAudioCaptureClient)
	if err != nil {
		return false, err
	}
	capture := (*IAudioCaptureClient)(cp)
	defer release(cp)

	raiseThreadPriority()
	if err := client.start(); err != nil {
		return false, err
	}
	defer client.stop()

	// alinear con el reloj de la grabación: lo que falta (se sumó tarde o estuvo afuera) va como silencio,
	// hasta 100 ms antes de ahora; el primer paquete ubica el resto con su marca
	margin := int64(fillMargin * float64(r.rate))
	fillTo := func(target int64) {
		if miss := target - r.frames.Load(); miss > 0 {
			r.w.silence(int(miss))
			r.frames.Add(miss)
		}
	}
	fillTo(clk.frameNow(r.rate) - margin)

	bytesPerFrame := int(wfx.BlockAlign)
	lastData := time.Now()
	resume := true // el primer paquete de la sesión siempre se ubica por su marca
	wasPaused := r.paused.Load()

	// accept pasa frames al escritor según el estado de la pista. Nunca toca el disco.
	accept := func(raw []byte, frames int, silent bool) {
		paused := r.paused.Load()
		if paused != wasPaused {
			if !paused {
				r.w.resume()
			}
			wasPaused = paused
		}
		switch {
		case paused:
			if !silent && raw != nil {
				r.bumpPeak(peakOf(raw, frames*r.srcCh, r.sk)) // sigue midiendo: si suena, vuelve
			}
			r.w.skip(frames)
		case silent:
			r.w.silence(frames)
		default:
			r.bumpPeak(r.w.encode(raw, frames, r.srcCh, r.sk))
		}
		r.frames.Add(int64(frames))
	}

	for {
		select {
		case <-stop:
			// todas las pistas terminan en el MISMO instante: lo que falte hasta el corte va como silencio
			if !r.paused.Load() {
				fillTo(clk.frameNow(r.rate))
			}
			return false, nil
		default:
		}
		if r.letGo.Load() {
			r.idle.Store(true)
			return false, nil
		}

		got := false
		for {
			n, h := capture.nextPacketSize()
			if h.failed() {
				if uint32(h) == 0x88890004 {
					return true, nil
				}
				return false, fmt.Errorf("%s: GetNextPacketSize: %w", r.name, h)
			}
			if n == 0 {
				break
			}
			data, frames, bflags, qpc, h2 := capture.getBuffer()
			if h2.failed() {
				if uint32(h2) == 0x88890004 {
					return true, nil
				}
				return false, fmt.Errorf("%s: GetBuffer: %w", r.name, h2)
			}
			if frames > 0 && qpc != 0 && !r.paused.Load() && (resume || bflags&AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY != 0) {
				// retoma: el paquete va en su instante exacto (lo que falte, como silencio)
				if at := clk.frameAt(qpc, r.rate); at-r.frames.Load() > int64(resyncMin*float64(r.rate)) {
					fillTo(at)
				}
			}
			resume = false
			if frames > 0 {
				if bflags&AUDCLNT_BUFFERFLAGS_SILENT != 0 {
					accept(nil, int(frames), true)
				} else {
					accept(unsafe.Slice(data, int(frames)*bytesPerFrame), int(frames), false)
				}
				got = true
			}
			capture.releaseBuffer(frames)
		}

		if got {
			lastData = time.Now()
			continue
		}
		gap := time.Since(lastData)
		if gap > resumeAfter {
			resume = true // el próximo paquete viene después de un hueco: se ubica por su marca
		}
		// cola VACÍA hace más de 250 ms: nadie suena. Silencio hasta 100 ms antes de ahora (el archivo crece y
		// queda alineado; si el hilo se hubiera trabado, habría paquetes en la cola y no se llegaría acá)
		if gap > gapFill && !r.paused.Load() {
			fillTo(clk.frameNow(r.rate) - margin)
		}
		time.Sleep(pollInterval)
	}
}

// peakOf mide el pico de un paquete sin convertirlo (para pistas en pausa).
func peakOf(raw []byte, samples int, kind sampleKind) float32 {
	var peak float32
	for i := 0; i < samples; i++ {
		v := sampleAt(raw, i, kind)
		if v < 0 {
			v = -v
		}
		if v > peak {
			peak = v
		}
	}
	return peak
}

// sampleAt lee la muestra n-ésima del buffer crudo como float −1..1.
func sampleAt(raw []byte, idx int, kind sampleKind) float32 {
	switch kind {
	case kindFloat32:
		return math.Float32frombits(leUint32(raw[idx*4:]))
	case kindPCM16:
		return float32(int16(leUint16(raw[idx*2:]))) / 32768
	case kindPCM24:
		o := idx * 3
		v := int32(raw[o]) | int32(raw[o+1])<<8 | int32(int8(raw[o+2]))<<16
		return float32(v) / 8388608
	case kindPCM32:
		return float32(int32(leUint32(raw[idx*4:]))) / 2147483648
	}
	return 0
}

func leUint16(b []byte) uint16 { return uint16(b[0]) | uint16(b[1])<<8 }
func leUint32(b []byte) uint32 {
	return uint32(b[0]) | uint32(b[1])<<8 | uint32(b[2])<<16 | uint32(b[3])<<24
}
