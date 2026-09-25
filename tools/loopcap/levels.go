package main

// levels.go — el PULSO en vivo para quien nos lanzó: «el audio está entrando», 20 veces por segundo.
//
// Protocolo por stdout, una línea por evento (stderr sigue siendo la consola humana):
//
//	LV <ms> <idx>:<dB> <idx>:<dB> …                 pico de cada pista en los últimos 50 ms (−120 = nada)
//	DEV <signo> <idx> <salida|mic> <seg> <nombre>    + se sumó · ~ volvió · z en espera · - se desconectó
//
// Como la consola, NUNCA bloquea: una sola goroutine escribe; si el caño se llena, el renglón se descarta y se
// cuenta. Un nivel viejo no le sirve a nadie, y los cambios de pista también quedan en el estado JSON.

import (
	"fmt"
	"os"
	"strings"
	"sync/atomic"
	"time"
)

const levelEvery = 50 * time.Millisecond

type outPipe struct {
	q       chan string
	dropped atomic.Int64
	started atomic.Bool
}

var out = &outPipe{q: make(chan string, 128)}

func (o *outPipe) start() {
	if o.started.CompareAndSwap(false, true) {
		go func() {
			for s := range o.q {
				os.Stdout.WriteString(s)
			}
		}()
	}
}

// send encola sin esperar nunca. O(1).
func (o *outPipe) send(s string) {
	select {
	case o.q <- s:
	default:
		o.dropped.Add(1)
	}
}

// emitLevels publica el pico de cada pista lista cada 50 ms, hasta que cierren stop.
func emitLevels(g *registry, start time.Time, stop <-chan struct{}) {
	t := time.NewTicker(levelEvery)
	defer t.Stop()
	var b strings.Builder
	for {
		select {
		case <-stop:
			return
		case <-t.C:
		}
		b.Reset()
		fmt.Fprintf(&b, "LV %d", time.Since(start).Milliseconds())
		n := 0
		for _, r := range g.snapshot() {
			if !r.ready.Load() {
				continue
			}
			fmt.Fprintf(&b, " %d:%.1f", r.idx, dbNumber(r.takeLevel()))
			n++
		}
		if n > 0 {
			b.WriteByte('\n')
			out.send(b.String())
		}
	}
}
