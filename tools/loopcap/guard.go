package main

// Guardias: lo que impide que loopcap se convierta él mismo en el problema.
//
// 🚨 23-sep-2026: un loopcap de prueba quedó huérfano 15 horas escribiendo cuatro pistas y llenó el
//    disco (22 GB). Eso cortó a las 09:53 la grabación de una reunión real. De ahí salen dos guardias:
//
//    · -parent PID   si el proceso que nos lanzó muere, cortamos limpio. Se abre un HANDLE al padre
//                    al arrancar (no se consulta el PID cada vez): así un PID reciclado no nos engaña.
//    · -minfree MB   con el disco casi lleno, las pistas que vienen MUDAS se pausan (dejan de escribir)
//                    para dejarle el lugar a la que suena. Vuelven solas cuando se libera espacio.

import (
	"path/filepath"
	"syscall"
	"unsafe"
)

var (
	modkernel32             = syscall.NewLazyDLL("kernel32.dll")
	procGetDiskFreeSpaceExW = modkernel32.NewProc("GetDiskFreeSpaceExW")
	procOpenProcess         = modkernel32.NewProc("OpenProcess")
	procWaitForSingleObject = modkernel32.NewProc("WaitForSingleObject")
	procCloseHandle         = modkernel32.NewProc("CloseHandle")
)

const (
	synchronize = 0x00100000
	waitTimeout = 0x00000102
)

// diskFreeMB devuelve los MB libres para el usuario en el volumen de `path`, o -1 si no se pudo medir.
func diskFreeMB(path string) int64 {
	dir := filepath.Dir(path)
	p, err := syscall.UTF16PtrFromString(dir)
	if err != nil {
		return -1
	}
	var libres, total, totalLibres uint64
	r, _, _ := procGetDiskFreeSpaceExW.Call(uintptr(unsafe.Pointer(p)),
		uintptr(unsafe.Pointer(&libres)), uintptr(unsafe.Pointer(&total)), uintptr(unsafe.Pointer(&totalLibres)))
	if r == 0 {
		return -1
	}
	return int64(libres >> 20)
}

// parentWatch vigila al proceso padre por su handle. El valor cero no vigila nada.
type parentWatch struct {
	pid    int
	handle uintptr
}

func watchParent(pid int) parentWatch {
	if pid <= 0 {
		return parentWatch{}
	}
	h, _, _ := procOpenProcess.Call(synchronize, 0, uintptr(pid))
	return parentWatch{pid: pid, handle: h}
}

// alive: true si no hay padre que vigilar, si no se pudo abrir (ante la duda no se corta una grabación)
// o si el padre sigue vivo.
func (p parentWatch) alive() bool {
	if p.handle == 0 {
		return true
	}
	r, _, _ := procWaitForSingleObject.Call(p.handle, 0)
	return r == waitTimeout
}

func (p parentWatch) close() {
	if p.handle != 0 {
		procCloseHandle.Call(p.handle)
	}
}
