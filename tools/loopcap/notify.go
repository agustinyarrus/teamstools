package main

// notify.go — el vigía por EVENTOS: Windows avisa cuando aparece, se va o cambia de estado un dispositivo
// (IMMNotificationClient) y cuando un programa abre, arranca o suelta un stream en un micrófono / manos libres
// (IAudioSessionNotification + IAudioSessionEvents).
//
// 🚨 Medido el 23-sep con la máquina cargada: cada consulta al servicio de audio tarda 12–16 ms y una pasada
//    completa del vigía ~110 ms. Preguntar «¿cambió algo?» cuatro veces por segundo costaba ~3 % de un núcleo.
//    Ahora se pregunta cuando Windows dice que cambió algo (latencia de decenas de ms, no de 250) y una vez cada
//    5 s por las dudas.
//
// Los tres objetos COM son de Go: un puntero a su tabla de métodos, armada con syscall.NewCallback (UNA vez por
// método: hay un tope de callbacks por proceso). Los métodos no hacen nada salvo «patear» al vigía, que hace el
// trabajo en SU hilo: dentro de un aviso de COM no se debe llamar al servicio de audio.

import (
	"sync/atomic"
	"syscall"
	"unsafe"
)

var (
	IID_IUnknown                  = GUID{0x00000000, 0x0000, 0x0000, [8]byte{0xC0, 0, 0, 0, 0, 0, 0, 0x46}}
	IID_IMMNotificationClient     = GUID{0x7991EEC9, 0x7E89, 0x4D85, [8]byte{0x83, 0x90, 0x6C, 0x70, 0x3C, 0xEC, 0x60, 0xC0}}
	IID_IAudioSessionNotification = GUID{0x641DD20B, 0x4D41, 0x49CC, [8]byte{0xAB, 0xA3, 0x17, 0x4B, 0x94, 0x77, 0xBB, 0x08}}
	IID_IAudioSessionEvents       = GUID{0x24918ACC, 0x64B3, 0x37C1, [8]byte{0x8C, 0xA9, 0x74, 0xA6, 0x6E, 0x99, 0x57, 0xA8}}
)

const eNoInterface = 0x80004002

type unknownVtbl struct{ QueryInterface, AddRef, Release uintptr }

type mmNotifVtbl struct {
	unknownVtbl
	OnDeviceStateChanged, OnDeviceAdded, OnDeviceRemoved, OnDefaultDeviceChanged, OnPropertyValueChanged uintptr
}

type sessNotifVtbl struct {
	unknownVtbl
	OnSessionCreated uintptr
}

type sessEventsVtbl struct {
	unknownVtbl
	OnDisplayNameChanged, OnIconPathChanged, OnSimpleVolumeChanged, OnChannelVolumeChanged,
	OnGroupingParamChanged, OnStateChanged, OnSessionDisconnected uintptr
}

// comObject es lo que ve COM: una dirección cuyo primer campo apunta a la tabla de métodos. Son globales: COM
// guarda sus direcciones mientras dure el registro, y el GC de Go no mueve memoria.
type comObject struct{ vtbl unsafe.Pointer }

var (
	kick         = make(chan struct{}, 1) // coalescente: diez avisos seguidos = una pasada
	notifyEvents atomic.Int64             // cuántos avisos llegaron (diagnóstico, va al estado)

	vtMM   mmNotifVtbl
	vtSN   sessNotifVtbl
	vtSE   sessEventsVtbl
	objMM  comObject
	objSN  comObject
	objSE  comObject
	objsOK bool
)

func poke() uintptr {
	notifyEvents.Add(1)
	select {
	case kick <- struct{}{}:
	default:
	}
	return 0
}

// queryInterfaceFor: el QueryInterface de nuestros objetos (IUnknown + la interfaz propia). Los parámetros llegan
// como punteros tipados: memoria de COM, que el GC de Go no toca.
func queryInterfaceFor(iid GUID) uintptr {
	return syscall.NewCallback(func(this uintptr, riid *GUID, ppv *uintptr) uintptr {
		if riid != nil && ppv != nil && (*riid == IID_IUnknown || *riid == iid) {
			*ppv = this
			return 0
		}
		if ppv != nil {
			*ppv = 0
		}
		return eNoInterface
	})
}

// buildNotifyObjects arma las tablas una sola vez (syscall.NewCallback tiene tope por proceso).
func buildNotifyObjects() {
	if objsOK {
		return
	}
	addRef := syscall.NewCallback(func(this uintptr) uintptr { return 2 }) // viven lo que el proceso: sin cuenta real
	rel := syscall.NewCallback(func(this uintptr) uintptr { return 1 })
	nada3 := syscall.NewCallback(func(this, a, b uintptr) uintptr { return 0 })
	nada4 := syscall.NewCallback(func(this, a, b, c uintptr) uintptr { return 0 })
	nada5 := syscall.NewCallback(func(this, a, b, c, d uintptr) uintptr { return 0 })
	patea2 := syscall.NewCallback(func(this, a uintptr) uintptr { return poke() })
	patea3 := syscall.NewCallback(func(this, a, b uintptr) uintptr { return poke() })

	vtMM = mmNotifVtbl{unknownVtbl{queryInterfaceFor(IID_IMMNotificationClient), addRef, rel},
		patea3, // OnDeviceStateChanged(id, estado)
		patea2, // OnDeviceAdded(id)
		patea2, // OnDeviceRemoved(id)
		nada4,  // OnDefaultDeviceChanged(flujo, rol, id): se graban todas, el predeterminado no cambia nada
		nada3}  // OnPropertyValueChanged(id, clave)
	vtSN = sessNotifVtbl{unknownVtbl{queryInterfaceFor(IID_IAudioSessionNotification), addRef, rel},
		patea2} // OnSessionCreated(sesión): alguien abrió un stream
	vtSE = sessEventsVtbl{unknownVtbl{queryInterfaceFor(IID_IAudioSessionEvents), addRef, rel},
		nada3, nada3, // nombre, ícono
		nada4,  // OnSimpleVolumeChanged(float, BOOL, ctx): el float viaja en XMM; no se lee nada
		nada5,  // OnChannelVolumeChanged
		nada3,  // OnGroupingParamChanged
		patea2, // OnStateChanged(estado): el stream arrancó o paró
		patea2} // OnSessionDisconnected(motivo)
	objMM = comObject{unsafe.Pointer(&vtMM)}
	objSN = comObject{unsafe.Pointer(&vtSN)}
	objSE = comObject{unsafe.Pointer(&vtSE)}
	objsOK = true
}

func (e *IMMDeviceEnumerator) registerNotify() bool {
	buildNotifyObjects()
	r, _, _ := syscall.SyscallN(e.v.RegisterEndpointNotificationCallback, uintptr(unsafe.Pointer(e)),
		uintptr(unsafe.Pointer(&objMM)))
	return !hr(r).failed()
}

func (e *IMMDeviceEnumerator) unregisterNotify() {
	syscall.SyscallN(e.v.UnregisterEndpointNotificationCallback, uintptr(unsafe.Pointer(e)),
		uintptr(unsafe.Pointer(&objMM)))
}

func (m *IAudioSessionManager2) registerSessionNotify() bool {
	buildNotifyObjects()
	r, _, _ := syscall.SyscallN(m.v.RegisterSessionNotification, uintptr(unsafe.Pointer(m)),
		uintptr(unsafe.Pointer(&objSN)))
	return !hr(r).failed()
}

func (m *IAudioSessionManager2) unregisterSessionNotify() {
	syscall.SyscallN(m.v.UnregisterSessionNotification, uintptr(unsafe.Pointer(m)), uintptr(unsafe.Pointer(&objSN)))
}

func (s *IAudioSessionControl2) registerEvents() bool {
	buildNotifyObjects()
	r, _, _ := syscall.SyscallN(s.v.RegisterAudioSessionNotification, uintptr(unsafe.Pointer(s)),
		uintptr(unsafe.Pointer(&objSE)))
	return !hr(r).failed()
}

func (s *IAudioSessionControl2) unregisterEvents() {
	syscall.SyscallN(s.v.UnregisterAudioSessionNotification, uintptr(unsafe.Pointer(s)), uintptr(unsafe.Pointer(&objSE)))
}

// instanceID: identificador único de la sesión (dura lo que dura el stream de ese programa).
func (s *IAudioSessionControl2) instanceID() string {
	var p *uint16
	r, _, _ := syscall.SyscallN(s.v.GetSessionInstanceIdentifier, uintptr(unsafe.Pointer(s)), uintptr(unsafe.Pointer(&p)))
	if hr(r).failed() || p == nil {
		return ""
	}
	id := utf16PtrToString(p)
	procCoTaskMemFree.Call(uintptr(unsafe.Pointer(p)))
	return id
}
