package main

import (
	"fmt"
	"syscall"
	"unsafe"
)

// ---------------------------------------------------------------------------
// COM / WASAPI plumbing -- raw vtable calls, no cgo, no third-party packages.
// ---------------------------------------------------------------------------

type GUID struct {
	Data1 uint32
	Data2 uint16
	Data3 uint16
	Data4 [8]byte
}

type PROPERTYKEY struct {
	FmtID GUID
	PID   uint32
}

// PROPVARIANT: only the header + first pointer-sized union member are needed.
type PROPVARIANT struct {
	Vt         uint16
	Reserved1  uint16
	Reserved2  uint16
	Reserved3  uint16
	Val        uintptr
	ValPadding uintptr
}

var (
	CLSID_MMDeviceEnumerator   = GUID{0xBCDE0395, 0xE52F, 0x467C, [8]byte{0x8E, 0x3D, 0xC4, 0x57, 0x92, 0x91, 0x69, 0x2E}}
	IID_IMMDeviceEnumerator    = GUID{0xA95664D2, 0x9614, 0x4F35, [8]byte{0xA7, 0x46, 0xDE, 0x8D, 0xB6, 0x36, 0x17, 0xE6}}
	IID_IAudioClient           = GUID{0x1CB9AD4C, 0xDBFA, 0x4C32, [8]byte{0xB1, 0x78, 0xC2, 0xF5, 0x68, 0xA7, 0x03, 0xB2}}
	IID_IAudioCaptureClient    = GUID{0xC8ADBD64, 0xE71E, 0x48A0, [8]byte{0xA4, 0xDE, 0x18, 0x5C, 0x39, 0x5C, 0xD3, 0x17}}
	IID_IAudioRenderClient     = GUID{0xF294ACFC, 0x3146, 0x4483, [8]byte{0xA7, 0xBF, 0xAD, 0xDC, 0xA7, 0xC2, 0x60, 0xE2}}
	IID_IAudioMeterInformation = GUID{0xC02216F6, 0x8C67, 0x4B5B, [8]byte{0x9D, 0x00, 0xD0, 0x08, 0xE7, 0x3E, 0x00, 0x64}}

	KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = GUID{0x00000003, 0x0000, 0x0010, [8]byte{0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71}}
	KSDATAFORMAT_SUBTYPE_PCM        = GUID{0x00000001, 0x0000, 0x0010, [8]byte{0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71}}

	PKEY_Device_FriendlyName = PROPERTYKEY{
		GUID{0xA45C254E, 0xDF1C, 0x4EFD, [8]byte{0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0}}, 14,
	}
)

const (
	CLSCTX_ALL           = 0x17
	COINIT_MULTITHREADED = 0x0

	eRender     = 0
	eConsole    = 0
	eCapture    = 1
	eMultimedia = 1

	DEVICE_STATE_ACTIVE = 0x1

	AUDCLNT_SHAREMODE_SHARED     = 0
	AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000

	AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1
	AUDCLNT_BUFFERFLAGS_SILENT             = 0x2

	AUDCLNT_S_BUFFER_EMPTY       = 0x08890001
	AUDCLNT_E_DEVICE_INVALIDATED = -0x776F_FFFC // 0x88890004 as signed int32

	WAVE_FORMAT_PCM        = 1
	WAVE_FORMAT_IEEE_FLOAT = 3
	WAVE_FORMAT_EXTENSIBLE = 0xFFFE

	VT_LPWSTR = 31
)

var (
	modole32                          = syscall.NewLazyDLL("ole32.dll")
	procCoInitializeEx                = modole32.NewProc("CoInitializeEx")
	procCoUninitialize                = modole32.NewProc("CoUninitialize")
	procCoCreateInstance              = modole32.NewProc("CoCreateInstance")
	procCoTaskMemFree                 = modole32.NewProc("CoTaskMemFree")
	procPropVariantClear              = modole32.NewProc("PropVariantClear")
	modavrt                           = syscall.NewLazyDLL("avrt.dll")
	procAvSetMmThreadCharacteristicsW = modavrt.NewProc("AvSetMmThreadCharacteristicsW")
)

type hresult int32

func (h hresult) failed() bool { return h < 0 }

func (h hresult) Error() string {
	switch uint32(h) {
	case 0x88890001:
		return "AUDCLNT_E_NOT_INITIALIZED"
	case 0x88890003:
		return "AUDCLNT_E_WRONG_ENDPOINT_TYPE"
	case 0x88890004:
		return "AUDCLNT_E_DEVICE_INVALIDATED"
	case 0x88890008:
		return "AUDCLNT_E_UNSUPPORTED_FORMAT"
	case 0x8889000A:
		return "AUDCLNT_E_DEVICE_IN_USE"
	case 0x88890010:
		return "AUDCLNT_E_SERVICE_NOT_RUNNING (Windows Audio service stopped)"
	case 0x80070005:
		return "E_ACCESSDENIED"
	case 0x80004005:
		return "E_FAIL"
	}
	return fmt.Sprintf("HRESULT 0x%08X", uint32(h))
}

func hr(r1 uintptr) hresult { return hresult(int32(uint32(r1))) }

// --- vtables ---------------------------------------------------------------

type iUnknownVtbl struct {
	QueryInterface uintptr
	AddRef         uintptr
	Release        uintptr
}

type iMMDeviceEnumeratorVtbl struct {
	iUnknownVtbl
	EnumAudioEndpoints                     uintptr
	GetDefaultAudioEndpoint                uintptr
	GetDevice                              uintptr
	RegisterEndpointNotificationCallback   uintptr
	UnregisterEndpointNotificationCallback uintptr
}

type iMMDeviceCollectionVtbl struct {
	iUnknownVtbl
	GetCount uintptr
	Item     uintptr
}

type iMMDeviceVtbl struct {
	iUnknownVtbl
	Activate          uintptr
	OpenPropertyStore uintptr
	GetId             uintptr
	GetState          uintptr
}

type iPropertyStoreVtbl struct {
	iUnknownVtbl
	GetCount uintptr
	GetAt    uintptr
	GetValue uintptr
	SetValue uintptr
	Commit   uintptr
}

type iAudioClientVtbl struct {
	iUnknownVtbl
	Initialize        uintptr
	GetBufferSize     uintptr
	GetStreamLatency  uintptr
	GetCurrentPadding uintptr
	IsFormatSupported uintptr
	GetMixFormat      uintptr
	GetDevicePeriod   uintptr
	Start             uintptr
	Stop              uintptr
	Reset             uintptr
	SetEventHandle    uintptr
	GetService        uintptr
}

type iAudioCaptureClientVtbl struct {
	iUnknownVtbl
	GetBuffer         uintptr
	ReleaseBuffer     uintptr
	GetNextPacketSize uintptr
}

type iAudioRenderClientVtbl struct {
	iUnknownVtbl
	GetBuffer     uintptr
	ReleaseBuffer uintptr
}

type iAudioMeterInformationVtbl struct {
	iUnknownVtbl
	GetPeakValue            uintptr
	GetMeteringChannelCount uintptr
	GetChannelsPeakValues   uintptr
	QueryHardwareSupport    uintptr
}

type IMMDeviceEnumerator struct{ v *iMMDeviceEnumeratorVtbl }
type IAudioMeterInformation struct{ v *iAudioMeterInformationVtbl }
type IMMDeviceCollection struct{ v *iMMDeviceCollectionVtbl }
type IMMDevice struct{ v *iMMDeviceVtbl }
type IPropertyStore struct{ v *iPropertyStoreVtbl }
type IAudioClient struct{ v *iAudioClientVtbl }
type IAudioCaptureClient struct{ v *iAudioCaptureClientVtbl }
type IAudioRenderClient struct{ v *iAudioRenderClientVtbl }

func release(p unsafe.Pointer) {
	if p == nil {
		return
	}
	vt := *(**iUnknownVtbl)(p)
	syscall.SyscallN(vt.Release, uintptr(p))
}

// --- WAVEFORMATEX ----------------------------------------------------------

type WAVEFORMATEX struct {
	FormatTag      uint16
	Channels       uint16
	SamplesPerSec  uint32
	AvgBytesPerSec uint32
	BlockAlign     uint16
	BitsPerSample  uint16
	CbSize         uint16
}

// WAVEFORMATEXTENSIBLE no se puede declarar como struct de Go: en Windows
// WAVEFORMATEX esta empaquetada a 1 byte (sizeof == 18), mientras que Go la
// alinea a 4 y la infla a 20 -- lo que corre el SubFormat dos bytes y hace que
// todo formato de mezcla parezca "no soportado". Se lee por offset crudo.
const (
	offValidBitsPerSample = 18
	offChannelMask        = 20
	offSubFormat          = 24
)

// subFormatOf lee el GUID de subformato de una WAVEFORMATEXTENSIBLE nativa.
func subFormatOf(wfx *WAVEFORMATEX) GUID {
	return *(*GUID)(unsafe.Add(unsafe.Pointer(wfx), offSubFormat))
}

// sampleKind describes how to interpret the raw endpoint bytes.
type sampleKind int

const (
	kindFloat32 sampleKind = iota
	kindPCM16
	kindPCM24
	kindPCM32
)

func (k sampleKind) String() string {
	switch k {
	case kindFloat32:
		return "float32"
	case kindPCM16:
		return "pcm16"
	case kindPCM24:
		return "pcm24"
	case kindPCM32:
		return "pcm32"
	}
	return "?"
}

func classify(wfx *WAVEFORMATEX) (sampleKind, error) {
	tag := wfx.FormatTag
	if tag == WAVE_FORMAT_EXTENSIBLE && wfx.CbSize >= 22 {
		switch subFormatOf(wfx) {
		case KSDATAFORMAT_SUBTYPE_IEEE_FLOAT:
			tag = WAVE_FORMAT_IEEE_FLOAT
		case KSDATAFORMAT_SUBTYPE_PCM:
			tag = WAVE_FORMAT_PCM
		}
	}
	switch tag {
	case WAVE_FORMAT_IEEE_FLOAT:
		if wfx.BitsPerSample == 32 {
			return kindFloat32, nil
		}
	case WAVE_FORMAT_PCM:
		switch wfx.BitsPerSample {
		case 16:
			return kindPCM16, nil
		case 24:
			return kindPCM24, nil
		case 32:
			return kindPCM32, nil
		}
	}
	return 0, fmt.Errorf("formato de mezcla no soportado: tag=%d bits=%d", tag, wfx.BitsPerSample)
}

// --- helpers ---------------------------------------------------------------

func coInit() error {
	r, _, _ := procCoInitializeEx.Call(0, COINIT_MULTITHREADED)
	if h := hr(r); h.failed() {
		return fmt.Errorf("CoInitializeEx: %w", h)
	}
	return nil
}

func newEnumerator() (*IMMDeviceEnumerator, error) {
	var p unsafe.Pointer
	r, _, _ := procCoCreateInstance.Call(
		uintptr(unsafe.Pointer(&CLSID_MMDeviceEnumerator)),
		0, CLSCTX_ALL,
		uintptr(unsafe.Pointer(&IID_IMMDeviceEnumerator)),
		uintptr(unsafe.Pointer(&p)),
	)
	if h := hr(r); h.failed() {
		return nil, fmt.Errorf("CoCreateInstance(MMDeviceEnumerator): %w", h)
	}
	return (*IMMDeviceEnumerator)(p), nil
}

func (d *IMMDevice) friendlyName() string {
	var store *IPropertyStore
	const STGM_READ = 0
	r, _, _ := syscall.SyscallN(d.v.OpenPropertyStore,
		uintptr(unsafe.Pointer(d)), STGM_READ, uintptr(unsafe.Pointer(&store)))
	if hr(r).failed() {
		return "(dispositivo)"
	}
	defer release(unsafe.Pointer(store))

	var pv PROPVARIANT
	r, _, _ = syscall.SyscallN(store.v.GetValue,
		uintptr(unsafe.Pointer(store)),
		uintptr(unsafe.Pointer(&PKEY_Device_FriendlyName)),
		uintptr(unsafe.Pointer(&pv)))
	if hr(r).failed() || pv.Vt != VT_LPWSTR || pv.Val == 0 {
		return "(dispositivo)"
	}
	// pv.Val es un LPWSTR nativo: se lee via la direccion del campo para no
	// convertir un uintptr suelto en puntero (go vet, con razon, lo marca).
	name := utf16PtrToString(*(**uint16)(unsafe.Pointer(&pv.Val)))
	procPropVariantClear.Call(uintptr(unsafe.Pointer(&pv)))
	return name
}

func (d *IMMDevice) activateAudioClient() (*IAudioClient, error) {
	var p unsafe.Pointer
	r, _, _ := syscall.SyscallN(d.v.Activate,
		uintptr(unsafe.Pointer(d)),
		uintptr(unsafe.Pointer(&IID_IAudioClient)),
		CLSCTX_ALL, 0, uintptr(unsafe.Pointer(&p)))
	if h := hr(r); h.failed() {
		return nil, fmt.Errorf("IMMDevice::Activate(IAudioClient): %w", h)
	}
	return (*IAudioClient)(p), nil
}

// meter da el medidor de pico del endpoint sin abrir un stream de captura:
// sirve para ver al instante por que salida esta sonando algo.
func (d *IMMDevice) meter() (*IAudioMeterInformation, error) {
	var p unsafe.Pointer
	r, _, _ := syscall.SyscallN(d.v.Activate,
		uintptr(unsafe.Pointer(d)),
		uintptr(unsafe.Pointer(&IID_IAudioMeterInformation)),
		CLSCTX_ALL, 0, uintptr(unsafe.Pointer(&p)))
	if h := hr(r); h.failed() {
		return nil, fmt.Errorf("Activate(IAudioMeterInformation): %w", h)
	}
	return (*IAudioMeterInformation)(p), nil
}

func (m *IAudioMeterInformation) peak() float32 {
	var v float32
	syscall.SyscallN(m.v.GetPeakValue, uintptr(unsafe.Pointer(m)), uintptr(unsafe.Pointer(&v)))
	return v
}

func (c *IAudioClient) mixFormat() (*WAVEFORMATEX, error) {
	var wfx *WAVEFORMATEX
	r, _, _ := syscall.SyscallN(c.v.GetMixFormat, uintptr(unsafe.Pointer(c)), uintptr(unsafe.Pointer(&wfx)))
	if h := hr(r); h.failed() {
		return nil, fmt.Errorf("GetMixFormat: %w", h)
	}
	return wfx, nil
}

func (c *IAudioClient) initialize(flags uint32, hnsBuffer int64, wfx *WAVEFORMATEX) error {
	r, _, _ := syscall.SyscallN(c.v.Initialize,
		uintptr(unsafe.Pointer(c)),
		AUDCLNT_SHAREMODE_SHARED,
		uintptr(flags),
		uintptr(hnsBuffer),
		0,
		uintptr(unsafe.Pointer(wfx)),
		0)
	if h := hr(r); h.failed() {
		return fmt.Errorf("IAudioClient::Initialize: %w", h)
	}
	return nil
}

func (c *IAudioClient) service(iid *GUID) (unsafe.Pointer, error) {
	var p unsafe.Pointer
	r, _, _ := syscall.SyscallN(c.v.GetService,
		uintptr(unsafe.Pointer(c)), uintptr(unsafe.Pointer(iid)), uintptr(unsafe.Pointer(&p)))
	if h := hr(r); h.failed() {
		return nil, fmt.Errorf("GetService: %w", h)
	}
	return p, nil
}

func (c *IAudioClient) start() error {
	r, _, _ := syscall.SyscallN(c.v.Start, uintptr(unsafe.Pointer(c)))
	if h := hr(r); h.failed() {
		return fmt.Errorf("IAudioClient::Start: %w", h)
	}
	return nil
}

func (c *IAudioClient) stop() {
	syscall.SyscallN(c.v.Stop, uintptr(unsafe.Pointer(c)))
}

func (c *IAudioClient) bufferSize() uint32 {
	var n uint32
	syscall.SyscallN(c.v.GetBufferSize, uintptr(unsafe.Pointer(c)), uintptr(unsafe.Pointer(&n)))
	return n
}

func (c *IAudioClient) currentPadding() uint32 {
	var n uint32
	syscall.SyscallN(c.v.GetCurrentPadding, uintptr(unsafe.Pointer(c)), uintptr(unsafe.Pointer(&n)))
	return n
}

func (cc *IAudioCaptureClient) nextPacketSize() (uint32, hresult) {
	var n uint32
	r, _, _ := syscall.SyscallN(cc.v.GetNextPacketSize, uintptr(unsafe.Pointer(cc)), uintptr(unsafe.Pointer(&n)))
	return n, hr(r)
}

// getBuffer entrega el próximo paquete y el instante QPC (100 ns) en que el dispositivo capturó su primer frame.
func (cc *IAudioCaptureClient) getBuffer() (data *byte, frames uint32, flags uint32, qpcPos uint64, h hresult) {
	var devPos uint64
	r, _, _ := syscall.SyscallN(cc.v.GetBuffer,
		uintptr(unsafe.Pointer(cc)),
		uintptr(unsafe.Pointer(&data)),
		uintptr(unsafe.Pointer(&frames)),
		uintptr(unsafe.Pointer(&flags)),
		uintptr(unsafe.Pointer(&devPos)),
		uintptr(unsafe.Pointer(&qpcPos)))
	return data, frames, flags, qpcPos, hr(r)
}

func (cc *IAudioCaptureClient) releaseBuffer(frames uint32) {
	syscall.SyscallN(cc.v.ReleaseBuffer, uintptr(unsafe.Pointer(cc)), uintptr(frames))
}

func (rc *IAudioRenderClient) getBuffer(frames uint32) (*byte, hresult) {
	var p *byte
	r, _, _ := syscall.SyscallN(rc.v.GetBuffer, uintptr(unsafe.Pointer(rc)), uintptr(frames), uintptr(unsafe.Pointer(&p)))
	return p, hr(r)
}

func (rc *IAudioRenderClient) releaseBuffer(frames, flags uint32) {
	syscall.SyscallN(rc.v.ReleaseBuffer, uintptr(unsafe.Pointer(rc)), uintptr(frames), uintptr(flags))
}

func utf16PtrToString(p *uint16) string {
	if p == nil {
		return ""
	}
	n := 0
	for ptr := unsafe.Pointer(p); *(*uint16)(ptr) != 0; ptr = unsafe.Add(ptr, 2) {
		n++
		if n > 1<<16 {
			break
		}
	}
	return syscall.UTF16ToString(unsafe.Slice(p, n+1))
}

// raiseThreadPriority asks MMCSS for "Pro Audio" scheduling so the capture loop
// is never starved by an under-load desktop -- a dropped packet is lost forever.
func raiseThreadPriority() uintptr {
	task, _ := syscall.UTF16PtrFromString("Pro Audio")
	var idx uint32
	h, _, _ := procAvSetMmThreadCharacteristicsW.Call(uintptr(unsafe.Pointer(task)), uintptr(unsafe.Pointer(&idx)))
	return h
}
