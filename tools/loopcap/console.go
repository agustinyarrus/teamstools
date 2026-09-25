package main

// Consola que NUNCA bloquea.
//
// 🚨 Por qué existe (23-sep-2026): el grabador de TeamsTools lanzaba loopcap con stderr redirigido a un
//    caño que nadie leía. A los ~4 KB (unos 21 s de medidor) el caño se llenó y el siguiente
//    fmt.Fprintf(os.Stderr) quedó colgado PARA SIEMPRE dentro del bucle principal: no más estado en
//    vivo, no más encabezado del WAV, no más chequeo del archivo STOP. El WAV quedó declarando 21 s,
//    whisper transcribió 21 s y el pipeline borró 26 minutos de reunión.
//
//    Acá toda la salida pasa por una cola de capacidad fija que vacía UNA goroutine. Si la cola se
//    llena (nadie lee), el mensaje se descarta y se cuenta. Perder texto de diagnóstico es aceptable;
//    congelar la grabación, jamás. El medidor va aparte con semántica «el más nuevo gana»: nunca se
//    acumulan renglones viejos.

import (
	"fmt"
	"os"
	"sync/atomic"
	"time"
)

const consoleQueue = 256 // mensajes en vuelo antes de empezar a descartar

type console struct {
	queue   chan string
	meter   atomic.Pointer[string] // último renglón del medidor; nil = nada nuevo
	kick    chan struct{}          // «hay medidor nuevo», coalescente (capacidad 1)
	busy    atomic.Bool            // la goroutine está escribiendo (quizás trabada en un caño lleno)
	dropped atomic.Int64
}

var con = startConsole()

func startConsole() *console {
	c := &console{queue: make(chan string, consoleQueue), kick: make(chan struct{}, 1)}
	go c.pump()
	return c
}

// pump es la única goroutine que toca os.Stderr. Si el caño se llena, se traba ELLA sola.
func (c *console) pump() {
	for {
		select {
		case s := <-c.queue:
			c.write(s)
		case <-c.kick:
			if p := c.meter.Swap(nil); p != nil {
				c.write(*p)
			}
		}
	}
}

func (c *console) write(s string) {
	c.busy.Store(true)
	os.Stderr.WriteString(s)
	c.busy.Store(false)
}

// logf encola un mensaje. O(1), sin esperar nunca.
func logf(format string, a ...any) {
	s := fmt.Sprintf(format, a...)
	select {
	case con.queue <- s:
	default:
		con.dropped.Add(1)
	}
}

// meterf publica el medidor en vivo. Si la consola va atrasada, el renglón anterior se pisa.
func meterf(s string) {
	con.meter.Store(&s)
	select {
	case con.kick <- struct{}{}:
	default:
	}
}

// drain espera a que la cola se vacíe, con techo: la salida del programa no se cuelga jamás.
// Pide DOS lecturas ociosas seguidas: entre sacar un mensaje de la cola y marcar `busy` hay un instante
// en que todo parece vacío y el último renglón todavía no salió.
func drain(max time.Duration) {
	deadline := time.Now().Add(max)
	idle := 0
	for time.Now().Before(deadline) {
		if len(con.queue) == 0 && len(con.kick) == 0 && !con.busy.Load() {
			if idle++; idle >= 2 {
				return
			}
		} else {
			idle = 0
		}
		time.Sleep(5 * time.Millisecond)
	}
}
