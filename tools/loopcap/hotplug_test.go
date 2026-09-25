package main

// Pruebas del PLAN del vigía: función pura de (pistas, endpoints activos, hora). Sin hardware ni COM.

import (
	"strings"
	"testing"
	"time"
)

func ep(id, name string, kind trackKind, handsFree, inUse bool) endpointInfo {
	return endpointInfo{id: id, name: name, kind: kind, handsFree: handsFree, inUse: inUse}
}

func todos(endpointInfo) bool { return true }

func kinds(as []action) string {
	var b strings.Builder
	for _, a := range as {
		b.WriteString(map[actionKind]string{actOpen: "O", actResume: "R", actReplace: "X", actLetGo: "L", actGone: "G"}[a.kind])
	}
	return b.String()
}

func TestPlanAbreSalidasYSoloMicsEnUso(t *testing.T) {
	g := newRegistry(`C:\x\audio`, ".wav")
	t0 := time.Now()
	act := g.plan([]endpointInfo{
		ep("A", "Speakers (Realtek)", trackLoop, false, false),
		ep("B", "Headset (JBL Hands-Free)", trackLoop, true, false), // manos libres sin uso: NO
		ep("M", "Microphone Array", trackMic, false, false),         // mic sin uso: NO
		ep("N", "Headset (JBL)", trackMic, true, true),              // mic en uso por Teams: SÍ
	}, todos, t0)
	if kinds(act) != "OO" || act[0].ep.id != "A" || act[1].ep.id != "N" {
		t.Fatalf("plan = %s %+v", kinds(act), act)
	}
}

func TestPlanDispositivoNuevoAMitadDeGrabacion(t *testing.T) {
	g := newRegistry(`C:\x\audio`, ".wav")
	t0 := time.Now()
	a := g.plan([]endpointInfo{ep("A", "Speakers", trackLoop, false, false)}, todos, t0)
	r := g.add(a[0].ep, 0, nil)
	r.running.Store(true)
	// a los 20 s aparecen los auriculares: pista NUEVA, y la vieja no se toca
	a = g.plan([]endpointInfo{ep("A", "Speakers", trackLoop, false, false), ep("H", "Headphones (JBL)", trackLoop, false, false)}, todos, t0.Add(20*time.Second))
	if kinds(a) != "O" || a[0].ep.id != "H" {
		t.Fatalf("plan = %s", kinds(a))
	}
	h := g.add(a[0].ep, 20, nil)
	if !strings.HasSuffix(h.path, `audio__Headphones_JBL.wav`) || h.idx != 1 || h.joined != 20 {
		t.Fatalf("pista nueva mal armada: %+v", h)
	}
}

func TestPlanVuelveElMismoYSeAvisaUnaVezQueSeFue(t *testing.T) {
	g := newRegistry(`C:\x\audio`, ".wav")
	t0 := time.Now()
	r := g.add(ep("H", "Headphones", trackLoop, false, false), 0, nil)
	// se desconectó: la sesión cayó (no corre) y el endpoint ya no está activo
	if a := g.plan(nil, todos, t0); kinds(a) != "G" {
		t.Fatalf("debía avisar G una vez, dio %s", kinds(a))
	}
	if a := g.plan(nil, todos, t0.Add(time.Second)); kinds(a) != "" {
		t.Fatalf("el aviso se repite: %s", kinds(a))
	}
	r.gone.Store(true)
	// vuelve: se REANUDA la misma pista (no una nueva)
	a := g.plan([]endpointInfo{ep("H", "Headphones", trackLoop, false, false)}, todos, t0.Add(5*time.Second))
	if kinds(a) != "R" || a[0].r != r {
		t.Fatalf("debía reanudar la misma pista: %s", kinds(a))
	}
}

func TestPlanSueltaElMicAlRatoDeQueNadieLoUse(t *testing.T) {
	g := newRegistry(`C:\x\audio`, ".wav")
	t0 := time.Now()
	r := g.add(ep("M", "Mic", trackMic, false, true), 0, nil)
	r.lastInUse = t0
	r.running.Store(true)
	libre := []endpointInfo{ep("M", "Mic", trackMic, false, false)}
	if a := g.plan(libre, todos, t0.Add(2*time.Second)); kinds(a) != "" {
		t.Fatalf("soltó antes de la gracia: %s", kinds(a))
	}
	if a := g.plan(libre, todos, t0.Add(idleGrace+time.Millisecond)); kinds(a) != "L" {
		t.Fatalf("debía soltarlo: %s", kinds(a))
	}
	// ya soltado: si lo vuelven a usar, se reanuda
	r.running.Store(false)
	r.idle.Store(true)
	if a := g.plan([]endpointInfo{ep("M", "Mic", trackMic, false, true)}, todos, t0.Add(10*time.Second)); kinds(a) != "R" {
		t.Fatalf("debía reanudar: %s", kinds(a))
	}
}

func TestPlanRespetaLaEsperaTrasUnError(t *testing.T) {
	g := newRegistry(`C:\x\audio`, ".wav")
	t0 := time.Now()
	r := g.add(ep("A", "Speakers", trackLoop, false, false), 0, nil)
	r.gone.Store(true)
	r.retryAt.Store(t0.Add(2 * time.Second).UnixNano())
	act := []endpointInfo{ep("A", "Speakers", trackLoop, false, false)}
	if a := g.plan(act, todos, t0.Add(time.Second)); kinds(a) != "" {
		t.Fatalf("martilla un dispositivo que falla: %s", kinds(a))
	}
	if a := g.plan(act, todos, t0.Add(3*time.Second)); kinds(a) != "R" {
		t.Fatalf("no reintentó después de la espera: %s", kinds(a))
	}
}

func TestPlanFormatoNuevoVaAPistaNueva(t *testing.T) {
	g := newRegistry(`C:\x\audio`, ".wav")
	t0 := time.Now()
	r := g.add(ep("A", "Speakers", trackLoop, false, false), 0, nil)
	r.retired.Store(true)
	a := g.plan([]endpointInfo{ep("A", "Speakers", trackLoop, false, false)}, todos, t0)
	if kinds(a) != "X" {
		t.Fatalf("plan = %s", kinds(a))
	}
	n := g.add(a[0].ep, 30, a[0].r)
	if n.name != "Speakers (2)" || n.path == r.path || g.byID["A"] != n {
		t.Fatalf("reemplazo mal armado: %q %q", n.name, n.path)
	}
}

func TestNombresRepetidosNoSePisanYLosMicsSeDistinguen(t *testing.T) {
	g := newRegistry(`C:\x\audio.t2`, ".wav")
	a := g.add(ep("1", "Speakers", trackLoop, false, false), 0, nil)
	b := g.add(ep("2", "Speakers", trackLoop, false, false), 0, nil)
	m := g.add(ep("3", "Microphone Array (Intel® Smart Sound)", trackMic, false, true), 0, nil)
	if a.path == b.path || !strings.HasSuffix(b.path, "audio.t2__Speakers_2.wav") {
		t.Fatalf("se pisan: %q %q", a.path, b.path)
	}
	if !strings.HasSuffix(m.path, `audio.t2.mic__Microphone_Array_Intel_Smart_Sound.wav`) {
		t.Fatalf("mic mal nombrado: %q", m.path)
	}
}

func TestSelectorUnaSalidaSigueSuID(t *testing.T) {
	g := newRegistry(`C:\x\audio`, ".wav")
	solo := func(e endpointInfo) bool { return e.id == "A" }
	a := g.plan([]endpointInfo{ep("A", "Speakers", trackLoop, false, false), ep("B", "Otra", trackLoop, false, false)}, solo, time.Now())
	if kinds(a) != "O" || a[0].ep.id != "A" {
		t.Fatalf("plan = %s", kinds(a))
	}
}
