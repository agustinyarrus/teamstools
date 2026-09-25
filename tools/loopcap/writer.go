package main

// Escritor de WAV a prueba de disco lleno: un ACTOR por pista.
//
//   captura ──trozos──▶ canal acotado ──▶ escritor ──▶ disco
//      │                    │ lleno → el trozo se descarta y se cuenta; la captura JAMÁS espera
//      └ nunca hace I/O     └ una sola goroutine es dueña del archivo
//
// 🚨 Por qué (23-sep-2026): con bufio.Writer, el primer error de escritura queda PEGADO para siempre
//    («If an error occurs writing to a Writer, no more data will be accepted»). Un disco lleno de un
//    par de minutos mató las cuatro pistas de una reunión a las 09:53 y ninguna volvió, aunque el
//    espacio se liberó enseguida. Además la captura escribía al disco con el candado tomado: si el
//    disco se trababa, la captura también, y WASAPI perdía audio.
//
// Acá, si el disco falla:
//   · lo que no se pudo escribir espera en una cola acotada (lo más viejo se cae primero),
//   · se reintenta con espera creciente (backoff exponencial 50 ms → 2 s),
//   · al volver, se escribe PRIMERO un silencio del largo de lo perdido y después lo pendiente, así el
//     archivo sigue alineado con el reloj de pared y los tiempos de la transcripción no se corren.
// El encabezado se reescribe cada segundo con lo que REALMENTE está en disco: un corte de luz deja
// siempre un WAV válido y honesto (el viejo declaraba lo que se había INTENTADO escribir).

import (
	"encoding/binary"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"slices"
	"sync"
	"sync/atomic"
	"time"
)

const (
	chunkBytes   = 32 << 10 // trozo captura→escritor: ~0,17 s en mono 48 kHz 16 bit
	inboxChunks  = 512      // colchón del canal: 16 MB si el disco se traba un rato
	maxPending   = 48 << 20 // cola por pista mientras el disco falla (~4 min estéreo, ~8 min mono)
	retryMin     = 50 * time.Millisecond
	retryMax     = 2 * time.Second
	headerEvery  = time.Second
	drainOnClose = 4 * time.Second
	wavHeader    = 44
	maxWavData   = int64(math.MaxUint32) - 36 // el RIFF guarda tamaños de 32 bits
)

// sink es lo mínimo que el escritor necesita del archivo. *os.File lo cumple; las pruebas usan uno que
// falla a pedido.
type sink interface {
	io.Writer
	io.WriterAt
	io.Closer
}

// chunk es lo que cruza de la captura al escritor: silencio primero, datos después.
type chunk struct {
	base []byte // buffer original, para devolverlo al pool
	data []byte // lo que falta escribir de base
	pad  int64  // bytes de silencio a escribir ANTES de data
}

var zeros [64 << 10]byte

// Pool de buffers: la captura produce ~6 trozos por segundo por pista; reciclarlos deja al GC tranquilo.
var bufPool = sync.Pool{New: func() any { b := make([]byte, 0, chunkBytes); return &b }}

func getBuf() []byte { return (*bufPool.Get().(*[]byte))[:0] }

func putBuf(b []byte) {
	if cap(b) == chunkBytes {
		b = b[:0]
		bufPool.Put(&b)
	}
}

type wavWriter struct {
	path        string
	f           sink
	rate        int
	channels    int
	bits        int
	isFloat     bool
	bytesPerSec int64

	in   chan chunk
	done chan struct{}

	// --- lado captura: SOLO los toca la goroutine de captura ---
	acc     []byte
	owed    int64 // silencio a mandar antes del próximo dato (pausas del endpoint, trozos descartados)
	skipped int64 // bytes no grabados mientras la pista estuvo en pausa por disco casi lleno

	// --- reporte: atómicos, los lee el bucle principal cada segundo ---
	onDisk    atomic.Int64 // bytes de audio que están DE VERDAD en el archivo
	pending   atomic.Int64 // bytes esperando para escribirse
	dropped   atomic.Int64 // bytes de audio perdidos (su lugar en el tiempo queda como silencio)
	lastWrite atomic.Int64 // unix nano de la última escritura buena
	errSince  atomic.Int64 // unix nano del primer error de la racha actual; 0 = sano
	errText   atomic.Pointer[string]
	capped    atomic.Bool
}

type writerStats struct {
	OnDisk, Pending, Dropped int64
	LastWrite, ErrSince      time.Time
	ErrText                  string
	Capped                   bool
}

func newWavWriter(path string, rate, channels, bits int, isFloat bool) (*wavWriter, error) {
	f, err := os.Create(path)
	if err != nil {
		return nil, err
	}
	w, err := newWavWriterOn(f, path, rate, channels, bits, isFloat)
	if err != nil {
		f.Close()
		os.Remove(path)
		return nil, err
	}
	return w, nil
}

// newWavWriterOn escribe el encabezado y arranca el actor. Separado de newWavWriter para poder probarlo
// con un archivo falso.
func newWavWriterOn(f sink, path string, rate, channels, bits int, isFloat bool) (*wavWriter, error) {
	blockAlign := channels * bits / 8
	w := &wavWriter{
		path: path, f: f, rate: rate, channels: channels, bits: bits, isFloat: isFloat,
		bytesPerSec: int64(rate * blockAlign),
		in:          make(chan chunk, inboxChunks),
		done:        make(chan struct{}),
	}
	format := uint16(WAVE_FORMAT_PCM)
	if isFloat {
		format = WAVE_FORMAT_IEEE_FLOAT
	}
	h := make([]byte, 0, wavHeader)
	h = append(h, "RIFF"...)
	h = binary.LittleEndian.AppendUint32(h, 36) // se corrige cada segundo
	h = append(h, "WAVEfmt "...)
	h = binary.LittleEndian.AppendUint32(h, 16)
	h = binary.LittleEndian.AppendUint16(h, format)
	h = binary.LittleEndian.AppendUint16(h, uint16(channels))
	h = binary.LittleEndian.AppendUint32(h, uint32(rate))
	h = binary.LittleEndian.AppendUint32(h, uint32(rate*blockAlign))
	h = binary.LittleEndian.AppendUint16(h, uint16(blockAlign))
	h = binary.LittleEndian.AppendUint16(h, uint16(bits))
	h = append(h, "data"...)
	h = binary.LittleEndian.AppendUint32(h, 0)
	if _, err := f.Write(h); err != nil {
		return nil, err
	}
	w.lastWrite.Store(time.Now().UnixNano())
	go w.run()
	return w, nil
}

// ---------------------------------------------------------------------------------------------------
// lado captura — O(frames) por paquete, sin candados, sin I/O
// ---------------------------------------------------------------------------------------------------

// encode convierte un paquete del formato de mezcla al de salida, lo acumula y devuelve el pico.
func (w *wavWriter) encode(raw []byte, frames, srcChannels int, kind sampleKind) float32 {
	need := frames * w.channels * w.bits / 8
	if w.acc == nil {
		w.acc = getBuf()
	}
	start := len(w.acc)
	w.acc = slices.Grow(w.acc, need)[:start+need]
	buf := w.acc[start:]
	var peak float32
	o := 0
	for i := 0; i < frames; i++ {
		base := i * srcChannels
		if w.channels == 1 {
			var sum float32
			for c := 0; c < srcChannels; c++ {
				sum += sampleAt(raw, base+c, kind)
			}
			o += w.putSample(buf[o:], sum/float32(srcChannels), &peak)
		} else {
			for c := 0; c < srcChannels; c++ {
				o += w.putSample(buf[o:], sampleAt(raw, base+c, kind), &peak)
			}
		}
	}
	if len(w.acc) >= chunkBytes {
		w.ship()
	}
	return peak
}

func (w *wavWriter) putSample(dst []byte, v float32, peak *float32) int {
	a := v
	if a < 0 {
		a = -a
	}
	if a > *peak {
		*peak = a
	}
	if w.isFloat {
		binary.LittleEndian.PutUint32(dst, math.Float32bits(v))
		return 4
	}
	if v > 1 {
		v = 1
	} else if v < -1 {
		v = -1
	}
	binary.LittleEndian.PutUint16(dst, uint16(int16(v*32767)))
	return 2
}

func (w *wavWriter) frameBytes(frames int) int64 { return int64(frames * w.channels * w.bits / 8) }

// silence anota silencio real (paquetes SILENT del endpoint o huecos de reloj). No copia ceros: viaja
// como un número y el escritor lo materializa desde un bloque estático.
func (w *wavWriter) silence(frames int) {
	if frames <= 0 {
		return
	}
	if len(w.acc) > 0 {
		w.ship() // lo acumulado va antes que este silencio
	}
	w.owed += w.frameBytes(frames)
	if w.owed >= chunkBytes {
		w.ship()
	}
}

// skip descarta frames sin tocar el disco (pista en pausa por disco casi lleno). Se recuerdan para
// rellenar con silencio si la pista vuelve, así no se corre el reloj.
func (w *wavWriter) skip(frames int) { w.skipped += w.frameBytes(frames) }

// resume devuelve la pista a la vida: lo salteado pasa a ser silencio debido.
func (w *wavWriter) resume() {
	w.owed += w.skipped
	w.skipped = 0
}

// ship entrega lo acumulado (más el silencio debido) SIN esperar nunca.
func (w *wavWriter) ship() {
	if len(w.acc) == 0 && w.owed == 0 {
		return
	}
	c := chunk{base: w.acc, data: w.acc, pad: w.owed}
	select {
	case w.in <- c:
		w.acc, w.owed = nil, 0
	default:
		// el escritor está trabado: el audio se pierde, pero su LUGAR en el tiempo se conserva como silencio
		w.dropped.Add(int64(len(w.acc)))
		w.owed += int64(len(w.acc))
		w.acc = w.acc[:0]
	}
}

// Close hace el último envío (acá sí se puede esperar un poco: la captura ya terminó), cierra el canal
// y espera al escritor con techo. Nunca cuelga.
//
// Lo salteado por una pausa al final NO se rellena: sería escribir ceros en un disco casi lleno para una
// pista que estaba muda (y que finish() probablemente borre).
func (w *wavWriter) Close() {
	if len(w.acc) > 0 || w.owed > 0 {
		c := chunk{base: w.acc, data: w.acc, pad: w.owed}
		select {
		case w.in <- c:
		case <-time.After(2 * time.Second):
			w.dropped.Add(int64(len(w.acc)))
		}
		w.acc, w.owed = nil, 0
	}
	close(w.in)
	select {
	case <-w.done:
	case <-time.After(drainOnClose + 2*time.Second):
		logf("\n[!] %s: el disco no respondió al cerrar; el archivo queda con lo que alcanzó a escribir.\n", filepath.Base(w.path))
	}
}

// ---------------------------------------------------------------------------------------------------
// lado escritor — la ÚNICA goroutine que toca el archivo
// ---------------------------------------------------------------------------------------------------

func (w *wavWriter) run() {
	defer close(w.done)
	var (
		queue   []chunk
		queued  int64 // bytes en queue (silencios + datos)
		debt    int64 // silencio por lo que se tiró del frente de la cola cuando desbordó
		backoff time.Duration
		retry   *time.Timer
		retryC  <-chan time.Time
	)
	hdr := time.NewTicker(headerEvery)
	defer hdr.Stop()

	publish := func() { w.pending.Store(queued + debt) }

	// trim acota la memoria: si la cola pasa el tope, lo más viejo se convierte en deuda de silencio.
	trim := func() {
		for queued > maxPending && len(queue) > 1 {
			c := queue[0]
			n := c.pad + int64(len(c.data))
			debt += n
			queued -= n
			w.dropped.Add(int64(len(c.data)))
			putBuf(c.base)
			queue[0] = chunk{}
			queue = queue[1:]
		}
		publish()
	}

	// flush escribe todo lo pendiente en orden. true = quedó todo en disco.
	flush := func() bool {
		if !w.writeZeros(&debt) {
			publish()
			return false
		}
		for len(queue) > 0 {
			c := &queue[0]
			before := c.pad + int64(len(c.data))
			ok := w.writeZeros(&c.pad) && w.writeData(c)
			queued -= before - (c.pad + int64(len(c.data)))
			if !ok {
				publish()
				return false
			}
			putBuf(c.base)
			queue[0] = chunk{}
			queue = queue[1:]
		}
		publish()
		return true
	}

	attempt := func() {
		if flush() {
			backoff, retryC = 0, nil
			w.healthy()
			return
		}
		if backoff == 0 {
			backoff = retryMin
		} else if backoff = backoff * 2; backoff > retryMax {
			backoff = retryMax
		}
		if retry == nil {
			retry = time.NewTimer(backoff)
		} else {
			retry.Reset(backoff)
		}
		retryC = retry.C
	}

	in := w.in
	for in != nil {
		select {
		case c, ok := <-in:
			if !ok {
				in = nil
				continue
			}
			queue = append(queue, c)
			queued += c.pad + int64(len(c.data))
			trim()
			if retryC == nil { // en espera de reintento no se martilla el disco
				attempt()
			}
		case <-retryC:
			retryC = nil
			attempt()
		case <-hdr.C:
			w.header()
		}
	}

	// cierre: vaciar lo pendiente un rato más, sin colgar nunca
	deadline := time.Now().Add(drainOnClose)
	for len(queue) > 0 || debt > 0 {
		if flush() {
			w.healthy()
			break
		}
		if time.Now().After(deadline) {
			lost := queued + debt
			w.dropped.Add(lost)
			logf("\n[!] %s: cerré con %.1f s sin poder escribir (%v).\n", filepath.Base(w.path), float64(lost)/float64(max(w.bytesPerSec, 1)), w.lastError())
			break
		}
		time.Sleep(4 * retryMin)
	}
	if retry != nil {
		retry.Stop()
	}
	w.header()
	w.f.Close()
}

// put escribe p respetando el tope de 4 GB del WAV. Pasado el tope, descarta y lo dice UNA vez.
func (w *wavWriter) put(p []byte) (int, error) {
	if w.capped.Load() {
		return len(p), nil
	}
	want := len(p)
	if room := maxWavData - w.onDisk.Load(); int64(want) > room {
		p = p[:max(room, 0)]
		w.capped.Store(true)
		logf("\n[!] %s llegó al tope de 4 GB del formato WAV; lo que sigue no entra.\n", filepath.Base(w.path))
	}
	n, err := w.f.Write(p)
	if n > 0 {
		w.onDisk.Add(int64(n))
		w.lastWrite.Store(time.Now().UnixNano())
	}
	if err == nil && w.capped.Load() {
		return want, nil
	}
	return n, err
}

func (w *wavWriter) writeData(c *chunk) bool {
	for len(c.data) > 0 {
		n, err := w.put(c.data)
		c.data = c.data[n:]
		if err == nil && n == 0 {
			err = io.ErrShortWrite
		}
		if err != nil {
			w.fail(err)
			return false
		}
	}
	return true
}

func (w *wavWriter) writeZeros(n *int64) bool {
	for *n > 0 {
		k := min(*n, int64(len(zeros)))
		m, err := w.put(zeros[:k])
		*n -= int64(m)
		if err == nil && m == 0 {
			err = io.ErrShortWrite
		}
		if err != nil {
			w.fail(err)
			return false
		}
	}
	return true
}

// header reescribe los dos tamaños del RIFF con lo que está DE VERDAD en disco. Si falla, se reintenta
// al segundo siguiente: son 8 bytes sobre espacio ya reservado, así que hasta con el disco lleno suele
// entrar.
func (w *wavWriter) header() {
	d := w.onDisk.Load()
	var b [4]byte
	binary.LittleEndian.PutUint32(b[:], uint32(36+d))
	w.f.WriteAt(b[:], 4)
	binary.LittleEndian.PutUint32(b[:], uint32(d))
	w.f.WriteAt(b[:], 40)
}

func (w *wavWriter) fail(err error) {
	s := err.Error()
	w.errText.Store(&s)
	if w.errSince.CompareAndSwap(0, time.Now().UnixNano()) {
		logf("\n[!] no puedo escribir %s: %v · sigo intentando y lo perdido queda como silencio.\n", filepath.Base(w.path), err)
	}
}

func (w *wavWriter) healthy() {
	since := w.errSince.Swap(0)
	if since == 0 {
		return
	}
	w.errText.Store(nil)
	logf("\n[ok] %s volvió a escribir tras %s · %.1f s rellenados con silencio.\n",
		filepath.Base(w.path), time.Since(time.Unix(0, since)).Round(time.Second), float64(w.dropped.Load())/float64(max(w.bytesPerSec, 1)))
}

func (w *wavWriter) lastError() string {
	if p := w.errText.Load(); p != nil {
		return *p
	}
	return "sin error"
}

func (w *wavWriter) stats() writerStats {
	s := writerStats{
		OnDisk: w.onDisk.Load(), Pending: w.pending.Load(), Dropped: w.dropped.Load(),
		Capped: w.capped.Load(),
	}
	if t := w.lastWrite.Load(); t != 0 {
		s.LastWrite = time.Unix(0, t)
	}
	if t := w.errSince.Load(); t != 0 {
		s.ErrSince = time.Unix(0, t)
		s.ErrText = w.lastError()
	}
	return s
}

func (s writerStats) String() string {
	return fmt.Sprintf("disco=%d pendiente=%d perdido=%d", s.OnDisk, s.Pending, s.Dropped)
}
