package main

// hotplug.go — el VIGÍA de dispositivos: el audio de una reunión no se pierde porque cambiaste de parlante.
//
// 🚨 Por qué existe (23-sep-2026): en una daily el usuario pasó de los parlantes a unos auriculares a los 20 s.
//    loopcap v2 solo abría las salidas que existían al arrancar: los auriculares no estaban, y los 8 minutos
//    siguientes quedaron afuera de la grabación (tres pistas mudas, nadie se enteró).
//
//    Ahora el vigía mira los endpoints ACTIVOS cada 250 ms (una pasada cuesta ~1 ms):
//      · uno NUEVO (por ID)                     → pista nueva, alineada con el reloj (lo anterior va como silencio)
//      · uno que se fue y VUELVE                → sigue en SU pista, con el hueco como silencio
//      · uno que vuelve con OTRO formato        → pista nueva «(2)»: un WAV no cambia de formato a mitad
//      · micrófono o manos libres               → se graba SOLO mientras otro proceso lo usa, y se suelta a los
//                                                 4 s de que nadie lo use (abrirlo por las nuestras le cambia el
//                                                 perfil al auricular Bluetooth y le arruina el audio al usuario)
//    El plan es una función PURA de (pistas conocidas, endpoints activos, hora): se prueba sin hardware.
//    Cuándo se mira: cuando Windows AVISA (notify.go), cuando vence un plazo (soltar un mic, reintentar tras un
//    error) y una ronda de seguridad cada 5 s. Medido: una pasada cuesta ~110 ms de llamadas al servicio de audio
//    con la máquina cargada; por sondeo cada 250 ms eran ~3 % de un núcleo tirados.

import (
	"fmt"
	"path/filepath"
	"strings"
	"sync"
	"time"
	"unsafe"
)

const (
	safetyEvery = 5 * time.Second       // ronda de seguridad, por si un aviso se pierde
	debounce    = 40 * time.Millisecond // los avisos llegan en ráfaga: se junta la ráfaga en una sola pasada
	idleGrace   = 4 * time.Second       // un micrófono / manos libres sin uso ajeno por esto se suelta
)

type registry struct {
	mu    sync.Mutex
	list  []*recorder          // en orden de llegada
	byID  map[string]*recorder // ID del endpoint → su pista VIGENTE (la última, si hubo reemplazos)
	names map[string]int       // nombres de archivo usados: dos «Speakers» no se pisan
	stem  string
	ext   string
}

func newRegistry(stem, ext string) *registry {
	return &registry{byID: map[string]*recorder{}, names: map[string]int{}, stem: stem, ext: ext}
}

func (g *registry) snapshot() []*recorder {
	g.mu.Lock()
	defer g.mu.Unlock()
	return append([]*recorder(nil), g.list...)
}

// path: salida → «stem__Nombre.wav», micrófono → «stem.mic__Nombre.wav»; un nombre repetido suma «_2».
func (g *registry) path(name string, kind trackKind) string {
	base := sanitize(name)
	if base == "" {
		base = "dispositivo"
	}
	pre := g.stem
	if kind == trackMic {
		pre += ".mic"
	}
	key := strings.ToLower(pre + "__" + base)
	g.names[key]++
	if n := g.names[key]; n > 1 {
		base = fmt.Sprintf("%s_%d", base, n)
	}
	return fmt.Sprintf("%s__%s%s", pre, base, g.ext)
}

// add registra una pista nueva para el endpoint (o de reemplazo, si `prev` se cerró por cambio de formato).
func (g *registry) add(ep endpointInfo, joined float64, prev *recorder) *recorder {
	g.mu.Lock()
	defer g.mu.Unlock()
	name := ep.name
	if prev != nil {
		n := 1
		for _, x := range g.list {
			if x.id == ep.id {
				n++
			}
		}
		name = fmt.Sprintf("%s (%d)", ep.name, n)
	}
	r := &recorder{idx: len(g.list), id: ep.id, key: ep.id, name: name, kind: ep.kind, handsFree: ep.handsFree,
		path: g.path(name, ep.kind), joined: joined}
	if prev != nil {
		r.key = fmt.Sprintf("%s#%d", ep.id, r.idx)
	}
	g.list = append(g.list, r)
	g.byID[ep.id] = r
	return r
}

type actionKind int

const (
	actOpen    actionKind = iota // pista nueva
	actResume                    // la pista vuelve a capturar (volvió el endpoint, o alguien lo usa de nuevo)
	actReplace                   // la pista se cerró por formato: el audio sigue en una nueva
	actLetGo                     // nadie lo usa hace idleGrace: se suelta
	actGone                      // no está activo y no corre: marcar desconectada (una vez)
)

type action struct {
	kind actionKind
	ep   endpointInfo // vale para Open/Resume/Replace
	r    *recorder
}

// plan decide qué hacer sin tocar COM. O(pistas + endpoints) con el índice por ID. `accept` filtra qué
// endpoints corresponden (todas las salidas, una sola, micrófonos…). Actualiza lastInUse (solo lo toca el vigía).
func (g *registry) plan(active []endpointInfo, accept func(endpointInfo) bool, now time.Time) []action {
	g.mu.Lock()
	defer g.mu.Unlock()
	var out []action
	seen := make(map[string]bool, len(active))
	for _, ep := range active {
		if !accept(ep) {
			continue
		}
		seen[ep.id] = true
		r := g.byID[ep.id]
		wanted := !ep.onDemand() || ep.inUse
		if r != nil {
			// la gracia para soltarlo corre desde que se SUPO que lo dejaron de usar, no desde la última pasada que
			// lo vio en uso (con avisos, esa pasada pudo haber sido segundos antes)
			if ep.inUse || r.inUse {
				r.lastInUse = now
			}
			r.inUse = ep.inUse
		}
		switch {
		case r == nil:
			if wanted {
				out = append(out, action{kind: actOpen, ep: ep})
			}
		case r.running.Load():
			if ep.onDemand() && !ep.inUse && now.Sub(r.lastInUse) >= idleGrace && !r.letGo.Load() {
				out = append(out, action{kind: actLetGo, r: r})
			}
		case r.retired.Load():
			if wanted {
				out = append(out, action{kind: actReplace, ep: ep, r: r})
			}
		case now.UnixNano() < r.retryAt.Load():
			// espera creciente tras un error: no se martilla un dispositivo que falla
		case wanted:
			r.saidGone = false
			out = append(out, action{kind: actResume, ep: ep, r: r})
		}
	}
	// lo que ya no está activo y no captura: se avisa UNA vez (la sesión pudo haber caído sola por invalidación)
	for id, r := range g.byID {
		if !seen[id] && !r.running.Load() && !r.saidGone && !r.retired.Load() {
			r.saidGone = true
			out = append(out, action{kind: actGone, r: r})
		}
	}
	return out
}

// nextWake: el próximo plazo que vence sin que Windows avise nada — soltar un mic que nadie usa, o reintentar
// un dispositivo que falló. Cero si no hay ninguno. O(pistas).
func (g *registry) nextWake() time.Time {
	g.mu.Lock()
	defer g.mu.Unlock()
	var w time.Time
	pick := func(t time.Time) {
		if !t.IsZero() && (w.IsZero() || t.Before(w)) {
			w = t
		}
	}
	for _, r := range g.list {
		if r.running.Load() && !r.inUse && !r.lastInUse.IsZero() && (r.kind == trackMic || r.handsFree) && !r.letGo.Load() {
			pick(r.lastInUse.Add(idleGrace))
		}
		if ra := r.retryAt.Load(); ra > 0 && !r.running.Load() {
			if t := time.Unix(0, ra); t.After(time.Now()) {
				pick(t)
			}
		}
	}
	return w
}

// watch es el vigía. Corre en su propio hilo COM y es el ÚNICO que abre sesiones (y el único que llama
// wg.Add después del arranque): al cerrar stop, se espera a que salga ANTES del wg.Wait del final.
func watch(g *registry, mic bool, accept func(endpointInfo) bool, start time.Time, clk recClock,
	stop <-chan struct{}, wg *sync.WaitGroup, firstScan chan<- int) {
	if err := coInit(); err != nil {
		logf("\n[!] vigía de dispositivos sin COM: %v\n", err)
		close(firstScan)
		return
	}
	defer procCoUninitialize.Call()
	en, err := newEnumerator()
	if err != nil {
		logf("\n[!] vigía de dispositivos: %v\n", err)
		close(firstScan)
		return
	}
	defer release(unsafe.Pointer(en))
	cache := newEndpointCache()
	defer cache.close()
	if en.registerNotify() {
		defer en.unregisterNotify()
	} else {
		logf("\n[!] Windows no acepta avisos de dispositivos: miro cada %s\n", safetyEvery)
	}

	fails := 0
	scan := func() int {
		eps, err := cache.activeEndpoints(en, mic)
		if err != nil {
			if fails++; fails == 1 || fails%240 == 0 {
				logf("\n[!] no pude listar los dispositivos (%v); sigo intentando\n", err)
			}
			return 0
		}
		fails = 0
		now := time.Now()
		used := map[*IMMDevice]bool{}
		opened := 0
		for _, a := range g.plan(eps, accept, now) {
			select {
			case <-stop:
				continue // ya se está cerrando: no se abre nada más
			default:
			}
			switch a.kind {
			case actOpen, actReplace:
				r := g.add(a.ep, now.Sub(start).Seconds(), a.r)
				r.lastInUse = now
				if launch(r, a.ep.dev, clk, stop, wg) {
					used[a.ep.dev] = true
					opened++
					announce("+", r)
				}
			case actResume:
				if launch(a.r, a.ep.dev, clk, stop, wg) {
					used[a.ep.dev] = true
					opened++
					announce("~", a.r)
				}
			case actLetGo:
				a.r.letGo.Store(true)
				announce("z", a.r)
			case actGone:
				a.r.gone.Store(true)
				announce("-", a.r)
			}
		}
		for _, ep := range eps {
			if !used[ep.dev] {
				release(unsafe.Pointer(ep.dev))
			}
		}
		return opened
	}

	firstScan <- scan()
	close(firstScan)
	safety := time.NewTicker(safetyEvery)
	defer safety.Stop()
	wake := time.NewTimer(time.Hour)
	defer wake.Stop()
	for {
		// el próximo plazo propio (soltar / reintentar), si hay
		if !wake.Stop() {
			select {
			case <-wake.C:
			default:
			}
		}
		if w := g.nextWake(); !w.IsZero() {
			wake.Reset(max(time.Until(w)+10*time.Millisecond, 10*time.Millisecond))
		} else {
			wake.Reset(time.Hour)
		}
		select {
		case <-stop:
			return
		case <-kick:
			time.Sleep(debounce) // la ráfaga de avisos de un mismo cambio se atiende en UNA pasada
			select {
			case <-kick:
			default:
			}
			scan()
		case <-wake.C:
			scan()
		case <-safety.C:
			scan()
		}
	}
}

// announce avisa un cambio de pista: a la consola (para la bitácora) y, con -levels, al protocolo de stdout.
//
//   - se sumó una pista · ~ volvió · z en espera (nadie la usa) · - se desconectó
func announce(sign string, r *recorder) {
	que := map[string]string{"+": "se sumó", "~": "volvió", "z": "en espera (nadie la usa)", "-": "se desconectó"}[sign]
	// con -levels el que nos lanzó ya se entera por el protocolo: la consola lo dice sin marca (a su log de
	// depuración), así la bitácora no lo anota dos veces
	pre := "[ok]"
	if sign == "-" {
		pre = "[!]"
	}
	if opts.levels {
		pre = "·"
	}
	logf("\n%s %s «%s» %s · %s\n", pre, r.kind, r.name, que, filepath.Base(r.path))
	if opts.levels {
		out.send(fmt.Sprintf("DEV %s %d %s %.1f %s\n", sign, r.idx, r.kind, r.joined, r.name))
	}
}
