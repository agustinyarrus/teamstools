// loopcap -- graba el audio que SALE por los parlantes (WASAPI loopback) y, si se pide, lo que ENTRA por el
// micrófono que esté usando otro programa (la llamada). A WAV, sin drivers virtuales ni dependencias: COM directo.
//
//	loopcap -o salida.wav              graba la salida predeterminada hasta Ctrl+C
//	loopcap -o salida.wav -all         graba TODAS las salidas, incluidas las que aparezcan después
//	loopcap -o salida.wav -all -mic    ...y además el micrófono que esté usando la llamada
//	loopcap -o salida.wav -levels      publica por stdout el nivel de cada pista 20 veces por segundo
//	loopcap -o salida.wav -t 3600      corta a la hora
//	loopcap -o salida.wav -stop x.flag corta cuando aparece ese archivo
//	loopcap -o salida.wav -parent 1234 corta solo si muere el proceso 1234 (quien nos lanzó)
//	loopcap -probe 10                  medidor en vivo: muestra por dónde está sonando
//	loopcap -list                      lista salidas y micrófonos (con quién los usa)
//
// v2 (23-sep-2026, mañana) -- a prueba de lanzadores descuidados y de discos llenos:
//   - la consola jamás bloquea (console.go); un escritor-actor por pista (writer.go); guardias (guard.go).
//
// v3 (23-sep-2026, tarde) -- a prueba de «cambié de auriculares en la llamada»:
//   - un VIGÍA (hotplug.go) sigue los dispositivos en caliente: los nuevos se suman alineados con el reloj,
//     los que vuelven siguen en su pista, y micrófonos / manos libres se graban solo mientras otro los usa;
//   - -mic graba el micrófono de la llamada en su propia pista («audio.mic__Nombre.wav»);
//   - -levels: el pulso en vivo por stdout (levels.go) para que la interfaz muestre que el audio ENTRA;
//   - sin bomba de silencio: el reloj lo sostiene la pared (capture.go).
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"math"
	"os"
	"os/signal"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"
)

const (
	version          = "3.0"
	captureBufferHNS = 20_000_000            // 2 s de buffer: aguanta hipos del sistema sin perder audio
	pollInterval     = 10 * time.Millisecond // el período nativo de WASAPI en modo compartido; el buffer es de 2 s
	guardEvery       = 5 * time.Second       // cada cuánto se mira el disco
	silentPeak       = 1e-5                  // por debajo de esto una pista se considera muda
	closeWait        = 12 * time.Second
)

type options struct {
	out        string
	seconds    float64
	stopFile   string
	status     string
	device     string
	all        bool
	mic        bool
	levels     bool
	probe      float64
	mono       bool
	float32o   bool
	list       bool
	quiet      bool
	keepSilent bool
	parent     int
	minFreeMB  int64
	version    bool
}

var opts options

func main() {
	flag.StringVar(&opts.out, "o", "", "archivo WAV de salida (obligatorio salvo -list/-probe)")
	flag.Float64Var(&opts.seconds, "t", 0, "duración máxima en segundos (0 = sin límite)")
	flag.StringVar(&opts.stopFile, "stop", "", "cortar limpio cuando exista este archivo")
	flag.StringVar(&opts.status, "status", "", "escribir estado JSON en vivo a este archivo")
	flag.StringVar(&opts.device, "device", "", "subcadena de la salida a grabar (default: la predeterminada)")
	flag.BoolVar(&opts.all, "all", false, "grabar TODAS las salidas, también las que aparezcan después")
	flag.BoolVar(&opts.mic, "mic", false, "grabar también el micrófono que esté usando otro programa (la llamada)")
	flag.BoolVar(&opts.levels, "levels", false, "publicar por stdout el nivel de cada pista (20 por segundo)")
	flag.Float64Var(&opts.probe, "probe", 0, "medir N segundos el nivel de cada salida y salir (no graba)")
	flag.BoolVar(&opts.mono, "mono", false, "mezclar a mono (mitad de tamaño, ideal para voz)")
	flag.BoolVar(&opts.float32o, "float", false, "guardar float32 nativo en vez de PCM 16 bit")
	flag.BoolVar(&opts.list, "list", false, "listar salidas y micrófonos y salir")
	flag.BoolVar(&opts.quiet, "quiet", false, "sin medidor en pantalla")
	flag.BoolVar(&opts.keepSilent, "keepsilent", false, "con -all, conservar también las pistas mudas")
	flag.IntVar(&opts.parent, "parent", 0, "PID del proceso que nos lanzó: si muere, se corta limpio")
	flag.Int64Var(&opts.minFreeMB, "minfree", 400, "MB libres mínimos: por debajo se pausan las pistas mudas")
	flag.BoolVar(&opts.version, "version", false, "mostrar la versión y salir")
	bench := flag.Int("benchscan", 0, "(diagnóstico) medir N pasadas de cada llamada del vigía y salir")
	flag.Parse()

	if opts.version {
		fmt.Println("loopcap " + version)
		return
	}

	runtime.LockOSThread()
	if err := coInit(); err != nil {
		fatal(err)
	}
	defer procCoUninitialize.Call()

	var err error
	switch {
	case *bench > 0:
		err = benchScan(*bench)
	case opts.list:
		err = listDevices()
	case opts.probe > 0:
		err = probeDevices(opts.probe)
	case opts.out == "":
		flag.Usage()
		os.Exit(2)
	default:
		err = record()
	}
	if err != nil {
		fatal(err)
	}
	drain(time.Second)
}

func fatal(err error) {
	logf("\nERROR: %v\n", err)
	drain(time.Second)
	os.Exit(1)
}

// ---------------------------------------------------------------------------
// Descubrimiento (para -list y -probe; la grabación usa el vigía)
// ---------------------------------------------------------------------------

type endpoint struct {
	dev  *IMMDevice
	name string
	def  bool
}

func discover(want string, all bool) ([]endpoint, error) {
	en, err := newEnumerator()
	if err != nil {
		return nil, err
	}
	defer release(unsafe.Pointer(en))

	defName := ""
	if d, err := en.defaultDevice(eRender); err == nil {
		defName = d.friendlyName()
		release(unsafe.Pointer(d))
	}
	devs, err := en.devices(eRender)
	if err != nil {
		return nil, err
	}
	var out []endpoint
	lw := strings.ToLower(want)
	for _, d := range devs {
		n := d.friendlyName()
		keep := false
		switch {
		case all:
			keep = true
		case want != "":
			keep = strings.Contains(strings.ToLower(n), lw) && len(out) == 0
		default:
			keep = n == defName && len(out) == 0
		}
		if keep {
			out = append(out, endpoint{dev: d, name: n, def: n == defName})
		} else {
			release(unsafe.Pointer(d))
		}
	}
	if len(out) == 0 {
		if want != "" {
			return nil, fmt.Errorf("ningún dispositivo de salida contiene %q (probá -list)", want)
		}
		return nil, fmt.Errorf("no se encontró ningún dispositivo de reproducción activo")
	}
	return out, nil
}

func listDevices() error {
	en, err := newEnumerator()
	if err != nil {
		return err
	}
	defer release(unsafe.Pointer(en))
	eps, err := activeEndpoints(en, true, nil)
	if err != nil {
		return err
	}
	defID := map[trackKind]string{}
	for k, f := range map[trackKind]int{trackLoop: eRender, trackMic: eCapture} {
		if d, err := en.defaultDevice(f); err == nil {
			defID[k] = d.id()
			release(unsafe.Pointer(d))
		}
	}
	for _, k := range []trackKind{trackLoop, trackMic} {
		fmt.Println(map[trackKind]string{trackLoop: "Salidas activas:", trackMic: "\nMicrófonos activos:"}[k])
		for _, e := range eps {
			if e.kind != k {
				continue
			}
			var tags []string
			if e.id == defID[k] {
				tags = append(tags, "predeterminado")
			}
			if e.handsFree {
				tags = append(tags, "manos libres")
			}
			if e.onDemand() {
				if e.inUse {
					tags = append(tags, "EN USO por otro programa")
				} else {
					tags = append(tags, "sin uso: se graba solo cuando otro lo use")
				}
			}
			fmt.Printf("   %-52s %s\n", e.name, strings.Join(tags, " · "))
			fmt.Printf("   %-52s %s\n", "", e.id)
		}
	}
	for _, e := range eps {
		release(unsafe.Pointer(e.dev))
	}
	return nil
}

// probeDevices muestra el pico en vivo de cada salida: sirve para confirmar por dónde está sonando algo.
func probeDevices(secs float64) error {
	eps, err := discover("", true)
	if err != nil {
		return err
	}
	meters := make([]*IAudioMeterInformation, 0, len(eps))
	kept := eps[:0]
	for _, e := range eps {
		m, err := e.dev.meter()
		if err != nil {
			release(unsafe.Pointer(e.dev))
			continue
		}
		meters = append(meters, m)
		kept = append(kept, e)
	}
	eps = kept
	defer func() {
		for i := range eps {
			release(unsafe.Pointer(meters[i]))
			release(unsafe.Pointer(eps[i].dev))
		}
	}()

	logf("Escuchando %.0f s. Poné el audio a sonar ahora.\n\n", secs)
	peaks := make([]float32, len(eps))
	deadline := time.Now().Add(time.Duration(secs * float64(time.Second)))
	for time.Now().Before(deadline) {
		for i := range eps {
			if p := meters[i].peak(); p > peaks[i] {
				peaks[i] = p
			}
		}
		var b strings.Builder
		for i, e := range eps {
			fmt.Fprintf(&b, "  %-46s %s %s\n", trunc(e.name, 46), dbfs(peaks[i]), meter(peaks[i]))
		}
		fmt.Fprintf(&b, "\033[%dA", len(eps))
		logf("%s", b.String())
		time.Sleep(120 * time.Millisecond)
	}
	logf("\033[%dB\n", len(eps))

	best, bestPeak := -1, float32(0)
	for i := range eps {
		if peaks[i] > bestPeak {
			best, bestPeak = i, peaks[i]
		}
	}
	if best < 0 || bestPeak < 1e-4 {
		logf("No se detectó sonido en ninguna salida.\n")
		return nil
	}
	logf("Está sonando por: %s (%s)\n", eps[best].name, dbfs(bestPeak))
	logf("Para grabar solo esa:  loopcap -o audio.wav -device \"%s\"\n", firstWord(eps[best].name))
	return nil
}

// ---------------------------------------------------------------------------
// Grabación
// ---------------------------------------------------------------------------

// selector decide qué endpoints se graban. Con -all, todas las salidas (y con -mic, los micrófonos en uso);
// si no, UNA salida —la de -device o la predeterminada al arrancar— seguida por su ID aunque se reconecte.
func selector() (func(endpointInfo) bool, string, error) {
	if opts.all {
		que := "todas las salidas (también las que aparezcan)"
		if opts.mic {
			que += " + el micrófono de la llamada"
		}
		return func(ep endpointInfo) bool { return ep.kind == trackLoop || opts.mic }, que, nil
	}
	en, err := newEnumerator()
	if err != nil {
		return nil, "", err
	}
	defer release(unsafe.Pointer(en))
	var id, name string
	if opts.device != "" {
		devs, err := en.devices(eRender)
		if err != nil {
			return nil, "", err
		}
		lw := strings.ToLower(opts.device)
		for _, d := range devs {
			if n := d.friendlyName(); id == "" && strings.Contains(strings.ToLower(n), lw) {
				id, name = d.id(), n
			}
			release(unsafe.Pointer(d))
		}
		if id == "" {
			return nil, "", fmt.Errorf("ningún dispositivo de salida contiene %q (probá -list)", opts.device)
		}
	} else {
		d, err := en.defaultDevice(eRender)
		if err != nil {
			return nil, "", err
		}
		id, name = d.id(), d.friendlyName()
		release(unsafe.Pointer(d))
	}
	que := "«" + name + "»"
	if opts.mic {
		que += " + el micrófono de la llamada"
	}
	return func(ep endpointInfo) bool { return ep.id == id || (opts.mic && ep.kind == trackMic) }, que, nil
}

func record() error {
	base, _ := filepath.Abs(opts.out)
	ext := filepath.Ext(base)
	stem := strings.TrimSuffix(base, ext)
	if ext == "" {
		ext = ".wav"
	}
	accept, que, err := selector()
	if err != nil {
		return err
	}

	parent := watchParent(opts.parent)
	defer parent.close()
	if opts.parent > 0 && parent.handle == 0 {
		logf("[!] no pude abrir el proceso padre %d: sigo sin esa guardia (el tope -t sigue valiendo)\n", opts.parent)
	}
	if opts.stopFile != "" {
		os.Remove(opts.stopFile)
	}
	if opts.levels {
		out.start()
	}

	logf("loopcap %s · grabando %s:\n", version, que)
	start, clk := time.Now(), newRecClock()
	reg := newRegistry(stem, ext)
	stop := make(chan struct{})
	var wg sync.WaitGroup
	first := make(chan int, 1)
	watcherDone := make(chan struct{})
	go func() {
		defer close(watcherDone)
		runtime.LockOSThread() // COM: el vigía vive en SU hilo
		watch(reg, opts.mic, accept, start, clk, stop, &wg, first)
	}()
	if n := <-first; n == 0 {
		logf("[!] todavía no hay nada para grabar: espero a que aparezca un dispositivo\n")
	}
	if opts.levels {
		go emitLevels(reg, start, stop)
	}
	time.Sleep(300 * time.Millisecond) // que abran y reporten formato

	sig := make(chan os.Signal, 2)
	signal.Notify(sig, os.Interrupt, syscall.SIGTERM)
	st := &liveState{started: start, parent: parent, freeMB: diskFreeMB(base)}
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	lastGuard := time.Time{}
	reason := "señal"
loop:
	for {
		select {
		case <-sig:
			logf("\n[Ctrl+C] cerrando los archivos...\n")
			reason = "Ctrl+C"
			break loop
		case <-ticker.C:
			recs := reg.snapshot()
			if time.Since(lastGuard) >= guardEvery {
				lastGuard = time.Now()
				st.freeMB = diskFreeMB(base)
				guardDisk(recs, st.freeMB)
			}
			report(recs, st)
			if opts.seconds > 0 && time.Since(start).Seconds() >= opts.seconds {
				reason = "tiempo cumplido"
				break loop
			}
			if opts.stopFile != "" {
				if _, err := os.Stat(opts.stopFile); err == nil {
					reason = "archivo STOP"
					break loop
				}
			}
			if !parent.alive() {
				reason = "el proceso padre terminó"
				break loop
			}
		}
	}
	st.state = "stopping"
	report(reg.snapshot(), st)
	close(stop)
	t0 := time.Now()
	<-watcherDone // el vigía ya no abre nada: recién ahora se puede esperar a las sesiones
	tVigia := time.Since(t0)

	finished := make(chan struct{})
	go func() { wg.Wait(); close(finished) }()
	select {
	case <-finished:
	case <-time.After(closeWait):
		logf("\n[!] alguna pista no cerró en %s; sigo igual.\n", closeWait)
	}
	tSesiones := time.Since(t0) - tVigia
	recs := reg.snapshot()
	closeWriters(recs)
	tEscr := time.Since(t0) - tVigia - tSesiones
	logf("\n[·] cierre en %d ms: vigía %d · sesiones %d · escritores %d\n", time.Since(t0).Milliseconds(),
		tVigia.Milliseconds(), tSesiones.Milliseconds(), tEscr.Milliseconds())
	return finish(recs, reason, st)
}

// closeWriters cierra todos los escritores EN PARALELO (cada uno espera a su disco con techo).
func closeWriters(recs []*recorder) {
	var wg sync.WaitGroup
	for _, r := range recs {
		if !r.ready.Load() {
			continue
		}
		wg.Add(1)
		go func(w *wavWriter) { defer wg.Done(); w.Close() }(r.w)
	}
	wg.Wait()
}

// guardDisk: con el disco casi lleno, pausa las pistas que vienen mudas; con espacio de sobra, las devuelve.
// Una pista pausada sigue midiendo su pico: si empieza a sonar, vuelve aunque el disco siga justo.
func guardDisk(recs []*recorder, freeMB int64) {
	if freeMB < 0 || opts.minFreeMB <= 0 {
		return
	}
	for _, r := range recs {
		silent := r.peak() < silentPeak
		switch {
		case freeMB < opts.minFreeMB && silent && !r.paused.Load() && len(recs) > 1:
			r.paused.Store(true)
			logf("\n[!] disco casi lleno (%d MB libres): pauso %s, que viene muda.\n", freeMB, r.name)
		case r.paused.Load() && (!silent || freeMB > 2*opts.minFreeMB):
			r.paused.Store(false)
			logf("\n[ok] reanudo %s (%d MB libres).\n", r.name, freeMB)
		}
	}
}

// ---------------------------------------------------------------------------
// Reporte: medidor (si no es -quiet) y estado JSON en vivo
// ---------------------------------------------------------------------------

type liveState struct {
	started time.Time
	parent  parentWatch
	freeMB  int64
	state   string // "" = grabando
}

type deviceStatus struct {
	Index        int     `json:"index"`
	Device       string  `json:"device"`
	Kind         string  `json:"kind"`  // salida | mic
	State        string  `json:"state"` // grabando | en espera | desconectada | cerrada | abriendo
	ID           string  `json:"id"`
	HandsFree    bool    `json:"hands_free,omitempty"`
	JoinedS      float64 `json:"joined_s"` // segundo de la grabación en que se sumó (antes: silencio)
	Sessions     int32   `json:"sessions"`
	File         string  `json:"file"`
	Seconds      float64 `json:"seconds"`       // reloj capturado (incluye silencios)
	AudioSeconds float64 `json:"audio_seconds"` // lo que hay DE VERDAD en el archivo
	PeakDB       float64 `json:"peak_db"`       // pico del último segundo
	PeakTotalDB  float64 `json:"peak_total_db"`
	MB           float64 `json:"mb"` // en disco
	Bytes        int64   `json:"bytes"`
	PendingMB    float64 `json:"pending_mb"`
	LostSeconds  float64 `json:"lost_seconds"`
	WriteError   string  `json:"write_error,omitempty"`
	ErrorSince   string  `json:"error_since,omitempty"`
	LastWrite    string  `json:"last_write,omitempty"`
	SessionError string  `json:"session_error,omitempty"`
	Paused       bool    `json:"paused,omitempty"`
	Gone         bool    `json:"gone,omitempty"`
	Capped       bool    `json:"capped,omitempty"`
	Kept         *bool   `json:"kept,omitempty"` // solo en el estado final
}

type statusDoc struct {
	Version        string         `json:"version"`
	PID            int            `json:"pid"`
	State          string         `json:"state"`
	Reason         string         `json:"reason,omitempty"`
	Started        string         `json:"started"`
	Elapsed        float64        `json:"elapsed"`
	HHMMSS         string         `json:"hhmmss"`
	Updated        string         `json:"updated"`
	DiskFreeMB     int64          `json:"disk_free_mb"`
	Parent         int            `json:"parent,omitempty"`
	ParentAlive    bool           `json:"parent_alive"`
	ConsoleDropped int64          `json:"console_dropped"`
	LevelsDropped  int64          `json:"levels_dropped"`
	DeviceEvents   int64          `json:"device_events"` // avisos de Windows que despertaron al vigía
	Devices        []deviceStatus `json:"devices"`
	Kept           []string       `json:"kept,omitempty"`
}

func deviceRow(r *recorder, tick float32) deviceStatus {
	d := deviceStatus{Index: r.idx, Device: r.name, Kind: r.kind.String(), State: r.state(), ID: r.id,
		HandsFree: r.handsFree, JoinedS: round1(r.joined), Sessions: r.sessions.Load(), File: r.path,
		Paused: r.paused.Load(), Gone: r.gone.Load(), PeakDB: dbNumber(tick), PeakTotalDB: dbNumber(r.peak())}
	if err := r.lastFail(); err != nil {
		d.SessionError = err.Error()
	}
	if r.rate > 0 {
		d.Seconds = round1(float64(r.frames.Load()) / float64(r.rate))
	}
	if r.w != nil {
		s := r.w.stats()
		bps := float64(max(r.w.bytesPerSec, 1))
		d.Bytes = s.OnDisk
		d.MB = round1(float64(s.OnDisk) / (1 << 20))
		d.AudioSeconds = round1(float64(s.OnDisk) / bps)
		d.PendingMB = round1(float64(s.Pending) / (1 << 20))
		d.LostSeconds = round1(float64(s.Dropped) / bps)
		d.WriteError = s.ErrText
		d.Capped = s.Capped
		if !s.ErrSince.IsZero() {
			d.ErrorSince = s.ErrSince.Format(time.RFC3339)
		}
		if !s.LastWrite.IsZero() {
			d.LastWrite = s.LastWrite.Format(time.RFC3339)
		}
	}
	return d
}

func report(recs []*recorder, st *liveState) {
	elapsed := time.Since(st.started).Seconds()
	rows := make([]deviceStatus, 0, len(recs))
	var b strings.Builder
	if !opts.quiet {
		fmt.Fprintf(&b, "\r  %s ", hhmmss(elapsed))
	}
	for _, r := range recs {
		if !r.ready.Load() {
			continue
		}
		tick := r.takeTick()
		rows = append(rows, deviceRow(r, tick))
		if !opts.quiet {
			fmt.Fprintf(&b, "| %s %s %s ", trunc(firstWord(r.name), 12), dbfs(tick), meter(tick))
		}
	}
	if !opts.quiet && len(rows) > 0 {
		meterf(b.String() + "  ")
	}
	state := st.state
	if state == "" {
		state = "recording"
	}
	writeStatus(statusDoc{
		State: state, Elapsed: round1(elapsed), HHMMSS: hhmmss(elapsed), DiskFreeMB: st.freeMB,
		Started: st.started.Format(time.RFC3339Nano), Parent: st.parent.pid, ParentAlive: st.parent.alive(),
		Devices: rows,
	})
}

// writeStatus escribe el JSON de forma atómica (tmp + rename). Un fallo no afecta la grabación.
func writeStatus(doc statusDoc) {
	if opts.status == "" {
		return
	}
	doc.Version, doc.PID = version, os.Getpid()
	doc.Updated = time.Now().Format(time.RFC3339)
	doc.ConsoleDropped = con.dropped.Load()
	doc.LevelsDropped = out.dropped.Load()
	doc.DeviceEvents = notifyEvents.Load()
	b, err := json.Marshal(doc)
	if err != nil {
		return
	}
	tmp := opts.status + ".tmp"
	if os.WriteFile(tmp, b, 0644) == nil {
		os.Rename(tmp, opts.status)
	}
}

func finish(recs []*recorder, reason string, st *liveState) error {
	logf("\n\nGrabación terminada (%s).\n\n", reason)

	type result struct {
		r    *recorder
		row  deviceStatus
		peak float32
	}
	var res []result
	for _, r := range recs {
		if !r.ready.Load() {
			continue
		}
		res = append(res, result{r, deviceRow(r, 0), r.peak()})
	}
	sort.Slice(res, func(i, j int) bool { return res[i].peak > res[j].peak })

	var kept []string
	rows := make([]deviceStatus, 0, len(res))
	for i := range res {
		x := &res[i]
		tag := ""
		if x.peak < silentPeak {
			tag = "  (mudo)"
		}
		logf("  %-6s %-46s %s  %s  %s%s\n", x.r.kind, trunc(x.r.name, 46), hhmmss(x.row.AudioSeconds), dbfs(x.peak), baseName(x.r.path), tag)
		if x.row.LostSeconds > 0 {
			logf("      [!] %.1f s no se pudieron escribir (quedaron como silencio)\n", x.row.LostSeconds)
		}
		if err := x.r.lastFail(); err != nil {
			logf("      [!] %v\n", err)
		}
		keep := !(x.peak < silentPeak && len(res) > 1 && !opts.keepSilent)
		if !keep {
			os.Remove(x.r.path)
		} else {
			kept = append(kept, x.r.path)
		}
		k := keep
		x.row.Kept = &k
		rows = append(rows, x.row)
	}

	free := int64(-1)
	if len(recs) > 0 {
		free = diskFreeMB(recs[0].path)
	}
	writeStatus(statusDoc{
		State: "done", Reason: reason, Started: st.started.Format(time.RFC3339Nano),
		Elapsed: round1(time.Since(st.started).Seconds()), HHMMSS: hhmmss(time.Since(st.started).Seconds()),
		DiskFreeMB: free, Parent: st.parent.pid, ParentAlive: st.parent.alive(),
		Devices: rows, Kept: kept,
	})

	if len(res) == 0 {
		logf("\n[!] No se abrió ningún dispositivo en toda la grabación.\n")
		return nil
	}
	if res[0].peak < silentPeak {
		logf("\n[!] NO se captó audio en ninguna pista.\n    Revisá que algo estuviera sonando (probá: loopcap -probe 10).\n")
		return nil
	}
	if len(res) > 1 && !opts.keepSilent {
		logf("\n  (se borraron las pistas mudas)\n")
	}
	logf("\nAudio útil: %s\n", strings.Join(kept, "\n            "))
	return nil
}

// ---------------------------------------------------------------------------
// Presentación
// ---------------------------------------------------------------------------

func round1(v float64) float64 { return math.Round(v*10) / 10 }

func baseName(p string) string { return filepath.Base(p) }

func hhmmss(sec float64) string {
	if sec < 0 {
		sec = 0
	}
	t := int(sec)
	return fmt.Sprintf("%02d:%02d:%02d", t/3600, (t%3600)/60, t%60)
}

func dbNumber(peak float32) float64 {
	if peak <= 0 {
		return -120
	}
	return math.Round(20*math.Log10(float64(peak))*10) / 10
}

func dbfs(peak float32) string {
	if peak <= 1e-6 {
		return "  -inf dB"
	}
	return fmt.Sprintf("%+7.1f dB", dbNumber(peak))
}

func meter(peak float32) string {
	const width = 16
	n := int((dbNumber(peak) + 60) / 60 * width)
	if n < 0 {
		n = 0
	}
	if n > width {
		n = width
	}
	return "[" + strings.Repeat("#", n) + strings.Repeat(".", width-n) + "]"
}

func trunc(s string, n int) string {
	r := []rune(s)
	if len(r) <= n {
		return s
	}
	return string(r[:n-1]) + "…"
}

func firstWord(s string) string {
	if i := strings.IndexAny(s, " ("); i > 0 {
		return s[:i]
	}
	return s
}

func sanitize(s string) string {
	var b strings.Builder
	prev := false
	for _, r := range s {
		ok := (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9')
		if ok {
			b.WriteRune(r)
			prev = false
		} else if !prev {
			b.WriteByte('_')
			prev = true
		}
	}
	return strings.Trim(b.String(), "_")
}
