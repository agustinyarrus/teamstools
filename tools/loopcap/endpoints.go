package main

// endpoints.go — los endpoints de audio por FLUJO (salidas y micrófonos), con identidad estable y «quién los usa».
//
// El nombre de un dispositivo no sirve de identidad: dos «Speakers» pueden convivir y un nombre cambia si el
// usuario lo renombra. El ID del endpoint (IMMDevice::GetId) es estable entre reconexiones: con él el vigía
// reconoce a un dispositivo que VUELVE y lo sigue en su misma pista.
//
// «Quién lo usa» sale de las sesiones de audio del endpoint (IAudioSessionManager2): una sesión ACTIVA de otro
// proceso = alguien está sonando por esa salida o escuchando ese micrófono. Sirve para no despertar lo que nadie
// usa: abrir la salida «manos libres» o el micrófono de unos auriculares Bluetooth pasa el auricular al perfil de
// llamada (HFP, 16 kHz mono) y le arruina el audio al usuario. Esos se graban SOLO mientras otro los usa.

import (
	"fmt"
	"os"
	"strings"
	"syscall"
	"unsafe"
)

var (
	IID_IAudioSessionManager2  = GUID{0x77AA99A0, 0x1BD6, 0x484F, [8]byte{0x8B, 0xC7, 0x2C, 0x65, 0x4C, 0x9A, 0x9B, 0x6F}}
	IID_IAudioSessionControl2  = GUID{0xBFB7FF88, 0x7239, 0x4FC9, [8]byte{0x8F, 0xA2, 0x07, 0xC9, 0x50, 0xBE, 0x9C, 0x6D}}
	PKEY_Device_EnumeratorName = PROPERTYKEY{
		GUID{0xA45C254E, 0xDF1C, 0x4EFD, [8]byte{0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0}}, 24,
	}
)

const audioSessionStateActive = 1

type iAudioSessionManager2Vtbl struct {
	iUnknownVtbl
	GetAudioSessionControl        uintptr
	GetSimpleAudioVolume          uintptr
	GetSessionEnumerator          uintptr
	RegisterSessionNotification   uintptr
	UnregisterSessionNotification uintptr
	RegisterDuckNotification      uintptr
	UnregisterDuckNotification    uintptr
}

type iAudioSessionEnumeratorVtbl struct {
	iUnknownVtbl
	GetCount   uintptr
	GetSession uintptr
}

type iAudioSessionControl2Vtbl struct {
	iUnknownVtbl
	GetState                           uintptr
	GetDisplayName                     uintptr
	SetDisplayName                     uintptr
	GetIconPath                        uintptr
	SetIconPath                        uintptr
	GetGroupingParam                   uintptr
	SetGroupingParam                   uintptr
	RegisterAudioSessionNotification   uintptr
	UnregisterAudioSessionNotification uintptr
	GetSessionIdentifier               uintptr
	GetSessionInstanceIdentifier       uintptr
	GetProcessId                       uintptr
	IsSystemSoundsSession              uintptr
	SetDuckingPreference               uintptr
}

type IAudioSessionManager2 struct{ v *iAudioSessionManager2Vtbl }
type IAudioSessionEnumerator struct{ v *iAudioSessionEnumeratorVtbl }
type IAudioSessionControl2 struct{ v *iAudioSessionControl2Vtbl }

// endpointInfo es un endpoint ACTIVO visto en una pasada del vigía. `dev` es una referencia propia: quien la
// recibe la usa o la libera.
type endpointInfo struct {
	id        string
	name      string
	kind      trackKind
	handsFree bool // salida/micrófono de perfil de llamada Bluetooth (HFP): solo se abre si otro ya lo usa
	inUse     bool // hay una sesión ACTIVA de otro proceso (sin contar los sonidos del sistema)
	dev       *IMMDevice
}

// onDemand: los que se graban solo mientras otro proceso los usa (micrófonos y manos libres).
func (e endpointInfo) onDemand() bool { return e.kind == trackMic || e.handsFree }

func (e *IMMDeviceEnumerator) devices(f int) ([]*IMMDevice, error) {
	var col *IMMDeviceCollection
	r, _, _ := syscall.SyscallN(e.v.EnumAudioEndpoints,
		uintptr(unsafe.Pointer(e)), uintptr(f), DEVICE_STATE_ACTIVE, uintptr(unsafe.Pointer(&col)))
	if h := hr(r); h.failed() {
		return nil, fmt.Errorf("EnumAudioEndpoints: %w", h)
	}
	defer release(unsafe.Pointer(col))
	var n uint32
	syscall.SyscallN(col.v.GetCount, uintptr(unsafe.Pointer(col)), uintptr(unsafe.Pointer(&n)))
	out := make([]*IMMDevice, 0, n)
	for i := uint32(0); i < n; i++ {
		var d *IMMDevice
		r, _, _ := syscall.SyscallN(col.v.Item, uintptr(unsafe.Pointer(col)), uintptr(i), uintptr(unsafe.Pointer(&d)))
		if hr(r).failed() {
			continue
		}
		out = append(out, d)
	}
	return out, nil
}

func (e *IMMDeviceEnumerator) defaultDevice(f int) (*IMMDevice, error) {
	var dev *IMMDevice
	r, _, _ := syscall.SyscallN(e.v.GetDefaultAudioEndpoint,
		uintptr(unsafe.Pointer(e)), uintptr(f), eConsole, uintptr(unsafe.Pointer(&dev)))
	if h := hr(r); h.failed() {
		return nil, fmt.Errorf("GetDefaultAudioEndpoint: %w", h)
	}
	return dev, nil
}

// id: el identificador estable del endpoint ("{0.0.0.00000000}.{guid}"). "" si no se pudo leer.
func (d *IMMDevice) id() string {
	var p *uint16
	r, _, _ := syscall.SyscallN(d.v.GetId, uintptr(unsafe.Pointer(d)), uintptr(unsafe.Pointer(&p)))
	if hr(r).failed() || p == nil {
		return ""
	}
	s := utf16PtrToString(p)
	procCoTaskMemFree.Call(uintptr(unsafe.Pointer(p)))
	return s
}

// stringProp lee una propiedad de texto del endpoint ("" si no está).
func (d *IMMDevice) stringProp(key *PROPERTYKEY) string {
	var store *IPropertyStore
	r, _, _ := syscall.SyscallN(d.v.OpenPropertyStore, uintptr(unsafe.Pointer(d)), 0, uintptr(unsafe.Pointer(&store)))
	if hr(r).failed() {
		return ""
	}
	defer release(unsafe.Pointer(store))
	var pv PROPVARIANT
	r, _, _ = syscall.SyscallN(store.v.GetValue, uintptr(unsafe.Pointer(store)), uintptr(unsafe.Pointer(key)),
		uintptr(unsafe.Pointer(&pv)))
	if hr(r).failed() || pv.Vt != VT_LPWSTR || pv.Val == 0 {
		return ""
	}
	s := utf16PtrToString(*(**uint16)(unsafe.Pointer(&pv.Val)))
	procPropVariantClear.Call(uintptr(unsafe.Pointer(&pv)))
	return s
}

// isHandsFree: el endpoint cuelga del enumerador de manos libres de Bluetooth (BTHHFENUM). Si la propiedad no
// está, se mira el nombre (los drivers de BT lo dicen: «Hands-Free», «Manos libres»).
func isHandsFree(d *IMMDevice, name string) bool {
	if en := strings.ToUpper(d.stringProp(&PKEY_Device_EnumeratorName)); en != "" {
		return en == "BTHHFENUM"
	}
	n := strings.ToLower(name)
	return strings.Contains(n, "hands-free") || strings.Contains(n, "manos libres")
}

// sessionManager: el gestor de sesiones del endpoint (se guarda en la caché: activarlo es una llamada al servicio
// de audio que no hace falta repetir cuatro veces por segundo).
func sessionManager(d *IMMDevice) *IAudioSessionManager2 {
	var p unsafe.Pointer
	r, _, _ := syscall.SyscallN(d.v.Activate, uintptr(unsafe.Pointer(d)),
		uintptr(unsafe.Pointer(&IID_IAudioSessionManager2)), CLSCTX_ALL, 0, uintptr(unsafe.Pointer(&p)))
	if hr(r).failed() || p == nil {
		return nil
	}
	return (*IAudioSessionManager2)(p)
}

// sessionsInUse recorre las sesiones del endpoint y dice si OTRO proceso tiene una ACTIVA (los sonidos del sistema
// no cuentan: un «ding» no es una llamada). Con `track`, además se suscribe a los cambios de estado de cada sesión
// ajena nueva (IAudioSessionEvents) y suelta las que ya no existen: así el vigía se entera al instante de que
// Teams arrancó o paró el micrófono, sin preguntar. O(sesiones del endpoint): un puñado.
func sessionsInUse(mgr *IAudioSessionManager2, track map[string]*IAudioSessionControl2) bool {
	if mgr == nil {
		return false
	}
	var en *IAudioSessionEnumerator
	r, _, _ := syscall.SyscallN(mgr.v.GetSessionEnumerator, uintptr(unsafe.Pointer(mgr)), uintptr(unsafe.Pointer(&en)))
	if hr(r).failed() || en == nil {
		return false
	}
	defer release(unsafe.Pointer(en))
	var n int32
	syscall.SyscallN(en.v.GetCount, uintptr(unsafe.Pointer(en)), uintptr(unsafe.Pointer(&n)))
	me := uint32(os.Getpid())
	inUse := false
	var present map[string]bool
	if track != nil {
		present = make(map[string]bool, n)
	}
	for i := int32(0); i < n; i++ {
		var ctl unsafe.Pointer
		r, _, _ := syscall.SyscallN(en.v.GetSession, uintptr(unsafe.Pointer(en)), uintptr(i), uintptr(unsafe.Pointer(&ctl)))
		if hr(r).failed() || ctl == nil {
			continue
		}
		var c2p unsafe.Pointer
		vt := *(**iUnknownVtbl)(ctl)
		r, _, _ = syscall.SyscallN(vt.QueryInterface, uintptr(ctl), uintptr(unsafe.Pointer(&IID_IAudioSessionControl2)),
			uintptr(unsafe.Pointer(&c2p)))
		release(ctl)
		if hr(r).failed() || c2p == nil {
			continue
		}
		c2 := (*IAudioSessionControl2)(c2p)
		var state int32
		var pid uint32
		syscall.SyscallN(c2.v.GetState, uintptr(c2p), uintptr(unsafe.Pointer(&state)))
		syscall.SyscallN(c2.v.GetProcessId, uintptr(c2p), uintptr(unsafe.Pointer(&pid)))
		sys, _, _ := syscall.SyscallN(c2.v.IsSystemSoundsSession, uintptr(c2p))
		if state == audioSessionStateActive && pid != me && pid != 0 && hr(sys) != 0 { // S_OK (0) = sonidos del sistema
			inUse = true
		}
		kept := false
		if track != nil && pid != me {
			if id := c2.instanceID(); id != "" {
				present[id] = true
				if _, ya := track[id]; !ya && c2.registerEvents() {
					track[id] = c2 // la referencia queda en la suscripción
					kept = true
				}
			}
		}
		if !kept {
			release(c2p)
		}
	}
	for id, c := range track {
		if !present[id] {
			c.unregisterEvents()
			release(unsafe.Pointer(c))
			delete(track, id)
		}
	}
	return inUse
}

// usedByOthers: la misma pregunta sin suscribirse a nada (para -list).
func usedByOthers(mgr *IAudioSessionManager2) bool { return sessionsInUse(mgr, nil) }

// devMeta: lo que NO cambia de un endpoint mientras está activo (nombre, si es manos libres) y su gestor de
// sesiones. Se lee UNA vez por ID: el vigía pregunta 4 veces por segundo y cada lectura cruza al servicio de audio.
type devMeta struct {
	name      string
	handsFree bool
	mgr       *IAudioSessionManager2            // solo para los que se graban a pedido
	notifying bool                              // suscripto a sesiones nuevas del endpoint
	sessions  map[string]*IAudioSessionControl2 // sesiones ajenas con sus cambios de estado suscriptos
	seen      bool                              // visto en la pasada actual (lo que no se ve se libera)
}

// release suelta suscripciones y referencias del endpoint.
func (m *devMeta) release() {
	for id, c := range m.sessions {
		c.unregisterEvents()
		release(unsafe.Pointer(c))
		delete(m.sessions, id)
	}
	if m.mgr != nil {
		if m.notifying {
			m.mgr.unregisterSessionNotify()
		}
		release(unsafe.Pointer(m.mgr))
		m.mgr = nil
	}
}

type endpointCache struct{ m map[string]*devMeta }

func newEndpointCache() *endpointCache { return &endpointCache{m: map[string]*devMeta{}} }

// close suelta todas las suscripciones y referencias.
func (c *endpointCache) close() {
	for id, m := range c.m {
		m.release()
		delete(c.m, id)
	}
}

// activeEndpoints: los endpoints ACTIVOS de salida (y de micrófono si mic) con su identidad y si alguien los
// usa. Con caché: por pasada, solo el ID de cada uno y las sesiones de los que se graban a pedido.
// O(dispositivos · sesiones), del orden de una décima de milisegundo.
func (c *endpointCache) activeEndpoints(en *IMMDeviceEnumerator, mic bool) ([]endpointInfo, error) {
	for _, m := range c.m {
		m.seen = false
	}
	eps, err := activeEndpoints(en, mic, c)
	for id, m := range c.m {
		if !m.seen {
			m.release()
			delete(c.m, id)
		}
	}
	return eps, err
}

// activeEndpoints sin caché (c == nil) sirve para -list: lee todo de nuevo.
func activeEndpoints(en *IMMDeviceEnumerator, mic bool, c *endpointCache) ([]endpointInfo, error) {
	flows := []struct {
		f    int
		kind trackKind
	}{{eRender, trackLoop}}
	if mic {
		flows = append(flows, struct {
			f    int
			kind trackKind
		}{eCapture, trackMic})
	}
	var out []endpointInfo
	for _, fl := range flows {
		devs, err := en.devices(fl.f)
		if err != nil {
			for _, o := range out {
				release(unsafe.Pointer(o.dev))
			}
			return nil, err
		}
		for _, d := range devs {
			id := d.id()
			if id == "" {
				release(unsafe.Pointer(d))
				continue
			}
			var m *devMeta
			if c != nil {
				m = c.m[id]
			}
			if m == nil {
				name := d.friendlyName()
				m = &devMeta{name: name, handsFree: isHandsFree(d, name)}
				if fl.kind == trackMic || m.handsFree {
					m.mgr = sessionManager(d)
					if c != nil && m.mgr != nil {
						m.notifying = m.mgr.registerSessionNotify() // avisos de streams nuevos en este endpoint
						m.sessions = map[string]*IAudioSessionControl2{}
					}
				}
				if c != nil {
					c.m[id] = m
				}
			}
			m.seen = true
			info := endpointInfo{id: id, name: m.name, kind: fl.kind, handsFree: m.handsFree, dev: d}
			if info.onDemand() {
				info.inUse = sessionsInUse(m.mgr, m.sessions) // con caché: también se suscribe a las sesiones nuevas
			}
			if c == nil && m.mgr != nil {
				release(unsafe.Pointer(m.mgr))
			}
			out = append(out, info)
		}
	}
	return out, nil
}
