package main

// Pruebas del escritor-actor y de la consola, con un «disco» falso que falla, se traba o escribe a
// medias a pedido. Cada una reproduce una forma real de perder audio y verifica byte a byte que:
//   · el archivo final tiene EXACTAMENTE el largo de lo capturado (lo perdido queda como ceros),
//   · cada trozo está en su posición (el reloj no se corre),
//   · el encabezado declara lo que de verdad hay en disco,
//   · la captura nunca espera.

import (
	"bytes"
	"encoding/binary"
	"errors"
	"io"
	"math"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

var errDiskFull = errors.New("There is not enough space on the disk.")

type fakeSink struct {
	mu      sync.Mutex
	buf     []byte
	full    atomic.Bool
	block   chan struct{} // si no es nil, cada Write espera a que se cierre
	partial bool          // escribe la mitad y devuelve error (como un disco que se llena a mitad)
	closed  atomic.Bool
}

func (f *fakeSink) Write(p []byte) (int, error) {
	if f.block != nil {
		<-f.block
	}
	if f.full.Load() {
		return 0, errDiskFull
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	n := len(p)
	if f.partial && n > 1 {
		n /= 2
		f.buf = append(f.buf, p[:n]...)
		return n, io.ErrShortWrite
	}
	f.buf = append(f.buf, p...)
	return n, nil
}

func (f *fakeSink) WriteAt(p []byte, off int64) (int, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if int(off)+len(p) > len(f.buf) {
		return 0, io.ErrShortWrite
	}
	copy(f.buf[off:], p)
	return len(p), nil
}

func (f *fakeSink) Close() error { f.closed.Store(true); return nil }

func (f *fakeSink) snapshot() []byte {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]byte(nil), f.buf...)
}

// feed simula la captura: trozos de chunkBytes con el byte i+1 repetido (nunca 0, para distinguirlos
// del silencio de relleno).
func feed(w *wavWriter, i int) {
	w.acc = append(getBuf(), bytes.Repeat([]byte{byte(i%250 + 1)}, chunkBytes)...)
	w.ship()
}

func checkLayout(t *testing.T, f *fakeSink, chunks int, mustBeData func(i int) bool) {
	t.Helper()
	b := f.snapshot()
	if got, want := len(b)-wavHeader, chunks*chunkBytes; got != want {
		t.Fatalf("largo de datos = %d, esperaba %d (el reloj se corrió %d bytes)", got, want, got-want)
	}
	if !f.closed.Load() {
		t.Fatalf("el archivo no se cerró")
	}
	riff := binary.LittleEndian.Uint32(b[4:])
	data := binary.LittleEndian.Uint32(b[40:])
	if int(data) != len(b)-wavHeader || int(riff) != len(b)-8 {
		t.Fatalf("encabezado miente: riff=%d data=%d, archivo=%d", riff, data, len(b))
	}
	for i := 0; i < chunks; i++ {
		seg := b[wavHeader+i*chunkBytes : wavHeader+(i+1)*chunkBytes]
		want := byte(i%250 + 1)
		if mustBeData(i) {
			if seg[0] != want || seg[chunkBytes-1] != want {
				t.Fatalf("trozo %d fuera de lugar: empieza con %d, esperaba %d", i, seg[0], want)
			}
		} else if seg[0] != 0 && seg[0] != want {
			t.Fatalf("trozo %d: ni el dato ni silencio (%d)", i, seg[0])
		}
	}
}

func TestSano(t *testing.T) {
	f := &fakeSink{}
	w, err := newWavWriterOn(f, "sano.wav", 48000, 1, 16, false)
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 64; i++ {
		feed(w, i)
	}
	w.Close()
	checkLayout(t, f, 64, func(int) bool { return true })
	if s := w.stats(); s.Dropped != 0 || s.Pending != 0 || s.ErrText != "" {
		t.Fatalf("stats de un disco sano: %+v", s)
	}
}

// El caso del 23-sep: el disco se llena un rato y vuelve. Nada se pierde (la cola alcanza) y ninguna
// pista muere.
func TestDiscoLlenoUnRato(t *testing.T) {
	f := &fakeSink{}
	w, _ := newWavWriterOn(f, "lleno.wav", 48000, 1, 16, false)
	for i := 0; i < 300; i++ {
		switch i {
		case 60:
			f.full.Store(true)
		case 200:
			f.full.Store(false)
		}
		feed(w, i)
		time.Sleep(200 * time.Microsecond)
	}
	w.Close()
	checkLayout(t, f, 300, func(int) bool { return true })
	if s := w.stats(); s.Dropped != 0 || s.ErrText != "" {
		t.Fatalf("no debió perderse nada ni quedar error: %+v", s)
	}
}

// Disco lleno más tiempo del que aguanta la cola: se cae lo más VIEJO de la cola, su lugar queda en
// ceros y todo lo demás cae en su posición exacta.
func TestColaDesbordada(t *testing.T) {
	f := &fakeSink{}
	w, _ := newWavWriterOn(f, "desborde.wav", 48000, 1, 16, false)
	const total, desde, hasta = 2200, 100, 2000 // 1900 trozos ≈ 59 MB con el disco lleno > tope de 48 MB
	f.full.Store(false)
	for i := 0; i < total; i++ {
		if i == desde {
			f.full.Store(true)
		}
		if i == hasta {
			time.Sleep(20 * time.Millisecond) // que el escritor absorba el canal antes de liberar
			f.full.Store(false)
		}
		feed(w, i)
		if i%64 == 0 {
			time.Sleep(time.Millisecond)
		}
	}
	w.Close()
	s := w.stats()
	if s.Dropped == 0 {
		t.Fatalf("con %d MB encolados debió descartar algo", (hasta-desde)*chunkBytes>>20)
	}
	perdidos := int(s.Dropped / chunkBytes)
	// Los descartados son los MÁS VIEJOS de lo pendiente, y lo pendiente incluye los trozos que estaban en
	// vuelo cuando el disco se llenó: el hueco puede empezar ANTES de `desde`. La invariante real es que haya
	// UN solo hueco contiguo de `perdidos` trozos y que todo lo demás esté en su posición exacta.
	b := f.snapshot()
	z0 := -1
	for i := 0; i < total; i++ {
		if b[wavHeader+i*chunkBytes] == 0 {
			z0 = i
			break
		}
	}
	if z0 < 0 || z0 > desde {
		t.Fatalf("el hueco de silencio empieza en %d; tenía que empezar antes o en %d", z0, desde)
	}
	checkLayout(t, f, total, func(i int) bool { return i < z0 || i >= z0+perdidos })
	for i := z0; i < z0+perdidos; i++ {
		if b[wavHeader+i*chunkBytes] != 0 || b[wavHeader+(i+1)*chunkBytes-1] != 0 {
			t.Fatalf("el trozo descartado %d debería ser silencio", i)
		}
	}
	t.Logf("disco lleno %d trozos: perdidos %d (%.1f MB) como silencio, el resto en su lugar", hasta-desde, perdidos, float64(s.Dropped)/(1<<20))
}

// El escritor se traba (antivirus, disco dormido): la captura JAMÁS espera; lo que no entra en el
// canal se pierde pero conserva su lugar en el tiempo.
func TestEscritorTrabado(t *testing.T) {
	f := &fakeSink{block: make(chan struct{})}
	// el encabezado lo escribe el constructor: soltamos solo esa primera escritura
	go func() { f.block <- struct{}{} }()
	w, _ := newWavWriterOn(f, "trabado.wav", 48000, 1, 16, false)
	const total = inboxChunks + 300
	peor := time.Duration(0)
	for i := 0; i < total; i++ {
		t0 := time.Now()
		feed(w, i)
		if d := time.Since(t0); d > peor {
			peor = d
		}
	}
	if peor > 20*time.Millisecond {
		t.Fatalf("la captura esperó %s: no debe esperar nunca", peor)
	}
	if w.dropped.Load() == 0 {
		t.Fatalf("con el escritor trabado y %d trozos, algo tuvo que descartarse", total)
	}
	close(f.block) // el disco se destraba
	w.Close()
	checkLayout(t, f, total, func(i int) bool { return i < inboxChunks-1 })
	t.Logf("peor espera de la captura: %s · perdidos %d trozos como silencio", peor, w.dropped.Load()/chunkBytes)
}

func TestEscriturasParciales(t *testing.T) {
	f := &fakeSink{}
	w, _ := newWavWriterOn(f, "parcial.wav", 48000, 1, 16, false)
	f.partial = true
	for i := 0; i < 40; i++ {
		feed(w, i)
	}
	time.Sleep(50 * time.Millisecond)
	f.mu.Lock()
	f.partial = false
	f.mu.Unlock()
	w.Close()
	checkLayout(t, f, 40, func(int) bool { return true })
}

// El silencio viaja como número y se materializa en su lugar.
func TestSilencioEnSuLugar(t *testing.T) {
	f := &fakeSink{}
	w, _ := newWavWriterOn(f, "silencio.wav", 48000, 1, 16, false)
	feed(w, 0)
	w.silence(chunkBytes / 2) // frames: mono 16 bit = 2 bytes por frame → chunkBytes bytes
	feed(w, 2)
	w.Close()
	b := f.snapshot()
	if len(b)-wavHeader != 3*chunkBytes {
		t.Fatalf("largo %d, esperaba %d", len(b)-wavHeader, 3*chunkBytes)
	}
	if b[wavHeader+chunkBytes] != 0 || b[wavHeader+2*chunkBytes-1] != 0 || b[wavHeader+2*chunkBytes] != 3 {
		t.Fatalf("el silencio no quedó entre los dos trozos")
	}
}

// La consola con un stderr que NADIE lee (el bug del 23-sep): 20 000 mensajes y ninguno espera.
func TestConsolaConCanioLleno(t *testing.T) {
	r, wp, err := os.Pipe()
	if err != nil {
		t.Fatal(err)
	}
	viejo := os.Stderr
	os.Stderr = wp
	defer func() {
		// destrabar la goroutine de la consola sin ensuciar la salida de las pruebas: lo que quede en
		// la cola va a parar a NUL y recién después vuelve el stderr de verdad
		if dn, err := os.OpenFile(os.DevNull, os.O_WRONLY, 0); err == nil {
			os.Stderr = dn
		}
		r.Close()
		drain(2 * time.Second)
		os.Stderr = viejo
	}()
	t0 := time.Now()
	linea := strings.Repeat("x", 120)
	for i := 0; i < 20000; i++ {
		logf("%d %s\n", i, linea)
		meterf("\r medidor " + linea)
	}
	if d := time.Since(t0); d > 2*time.Second {
		t.Fatalf("logf tardó %s con el caño lleno: está bloqueando", d)
	}
	t1 := time.Now()
	drain(150 * time.Millisecond)
	if d := time.Since(t1); d > time.Second {
		t.Fatalf("drain no respetó su techo: %s", d)
	}
	if con.dropped.Load() == 0 {
		t.Fatalf("con el caño lleno tuvo que descartar mensajes")
	}
	t.Logf("20 000 mensajes en %s · descartados %d · drain devolvió en %s", time.Since(t0).Round(time.Millisecond), con.dropped.Load(), time.Since(t1).Round(time.Millisecond))
}

// encode: float32 estéreo → PCM16 mono (promedio de canales) y estéreo (intercalado), con recorte a ±1.
func TestConversionDeMuestras(t *testing.T) {
	muestras := []float32{0.5, -0.5, 1.5, 1.5, -0.25, -0.75} // 3 frames estéreo; el segundo satura
	raw := make([]byte, len(muestras)*4)
	for i, v := range muestras {
		binary.LittleEndian.PutUint32(raw[i*4:], math.Float32bits(v))
	}
	pcm := func(b []byte, i int) int16 { return int16(binary.LittleEndian.Uint16(b[i*2:])) }

	fm := &fakeSink{}
	mono, _ := newWavWriterOn(fm, "mono.wav", 48000, 1, 16, false)
	if p := mono.encode(raw, 3, 2, kindFloat32); p < 1.49 || p > 1.51 {
		t.Fatalf("pico mono = %v, esperaba 1,5 (se mide antes de recortar)", p)
	}
	mono.Close()
	b := fm.snapshot()[wavHeader:]
	if len(b) != 6 || pcm(b, 0) != 0 || pcm(b, 1) != 32767 || pcm(b, 2) != -16383 {
		t.Fatalf("mono mal convertido: %v %v %v (%d bytes)", pcm(b, 0), pcm(b, 1), pcm(b, 2), len(b))
	}

	fs := &fakeSink{}
	est, _ := newWavWriterOn(fs, "estereo.wav", 48000, 2, 16, false)
	est.encode(raw, 3, 2, kindFloat32)
	est.Close()
	b = fs.snapshot()[wavHeader:]
	want := []int16{16383, -16383, 32767, 32767, -8191, -24575} // int16(v*32767) trunca hacia cero
	for i, w := range want {
		if pcm(b, i) != w {
			t.Fatalf("estéreo muestra %d = %d, esperaba %d", i, pcm(b, i), w)
		}
	}
}
