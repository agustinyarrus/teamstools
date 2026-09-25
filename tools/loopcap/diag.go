package main

// diag.go — «medir, no suponer»: cuánto cuesta cada llamada que hace el vigía (opción oculta -benchscan N).

import (
	"fmt"
	"time"
	"unsafe"
)

func benchScan(n int) error {
	en, err := newEnumerator()
	if err != nil {
		return err
	}
	defer release(unsafe.Pointer(en))
	medir := func(nombre string, f func()) {
		t0 := time.Now()
		for i := 0; i < n; i++ {
			f()
		}
		fmt.Printf("  %-44s %8.1f µs por llamada\n", nombre, float64(time.Since(t0).Microseconds())/float64(n))
	}
	medir("EnumAudioEndpoints(salidas) + GetId", func() {
		devs, _ := en.devices(eRender)
		for _, d := range devs {
			_ = d.id()
			release(unsafe.Pointer(d))
		}
	})
	medir("EnumAudioEndpoints(micrófonos) + GetId", func() {
		devs, _ := en.devices(eCapture)
		for _, d := range devs {
			_ = d.id()
			release(unsafe.Pointer(d))
		}
	})
	devs, _ := en.devices(eCapture)
	if len(devs) > 0 {
		mgr := sessionManager(devs[0])
		medir("sesiones de un micrófono (gestor en caché)", func() { _ = usedByOthers(mgr) })
		medir("nombre + tipo (lo que la caché ahorra)", func() { _ = isHandsFree(devs[0], devs[0].friendlyName()) })
		if mgr != nil {
			release(unsafe.Pointer(mgr))
		}
	}
	for _, d := range devs {
		release(unsafe.Pointer(d))
	}
	c := newEndpointCache()
	defer c.close()
	medir("pasada completa del vigía (con caché, -mic)", func() {
		eps, _ := c.activeEndpoints(en, true)
		for _, e := range eps {
			release(unsafe.Pointer(e.dev))
		}
	})
	medir("QueryPerformanceCounter (reloj)", func() { _ = qpc100ns() })
	return nil
}
