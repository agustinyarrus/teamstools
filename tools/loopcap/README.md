# loopcap

Graba el audio que **sale** por los parlantes (WASAPI loopback) y, si se pide, lo que **entra** por el micrófono que esté usando otro programa. A WAV, sin drivers virtuales ni dependencias: Go puro con COM directo. Es el motor de captura de TeamsTools, y sirve solo.

```
loopcap -o salida.wav              graba la salida predeterminada hasta Ctrl+C
loopcap -o salida.wav -all         graba TODAS las salidas, incluidas las que aparezcan después
loopcap -o salida.wav -all -mic    …y además el micrófono que esté usando la llamada
loopcap -o salida.wav -levels      publica por stdout el nivel de cada pista 20 veces por segundo
loopcap -o salida.wav -t 3600      corta a la hora
loopcap -o salida.wav -stop x.flag corta limpio cuando aparece ese archivo
loopcap -o salida.wav -parent 1234 corta solo si muere el proceso 1234 (quien nos lanzó)
loopcap -probe 10                  medidor en vivo: muestra por dónde está sonando
loopcap -list                      lista salidas y micrófonos (con quién los usa)
```

Otros flags: `-status estado.json` (estado JSON en vivo, una vez por segundo), `-device <subcadena>` (una salida en particular), `-mono`, `-float` (float32 nativo), `-quiet`, `-keepsilent` (no borrar las pistas mudas al terminar), `-minfree <MB>` (400: con menos disco pausa las pistas mudas), `-version`.

## Compilar

Go 1.24 o más nuevo. Sin módulos externos.

```powershell
go build -trimpath -ldflags "-s -w" -o loopcap.exe .
go test ./...
```

`-trimpath` para que el exe no lleve las rutas de la máquina donde se compiló.

## Cómo está hecho

- **`writer.go`**: un escritor-actor por pista. La captura nunca hace I/O: manda trozos de 32 KiB por un canal acotado; lo que no entra al disco espera en una cola de hasta 48 MiB; ante un error de escritura reintenta con backoff de 50 ms a 2 s; al volver el espacio escribe primero **silencio del largo perdido**, así el archivo sigue alineado con el reloj; el encabezado RIFF se reescribe cada segundo con lo que hay de verdad en disco.
- **`console.go`**: la consola jamás bloquea (cola fija de 256 mensajes vaciada por una goroutine, medidor «el más nuevo gana»). Un lanzador que redirige stdout y no lo lee no puede colgar la captura.
- **`hotplug.go`** + **`notify.go`**: un vigía sigue los dispositivos en caliente por eventos de Windows (`IMMNotificationClient` y las sesiones de audio) con debounce de 40 ms y una ronda de seguridad cada 5 s. Un dispositivo nuevo se suma a su propia pista alineada al reloj de la grabación; uno que se va y vuelve sigue en su pista; otro formato va a una pista «(2)». Los micrófonos y las salidas «manos libres» de unos auriculares Bluetooth se abren **sólo mientras otro proceso los usa** (abrirlos porque sí los pasa al perfil de llamada y le arruina el audio a quien los usa) y se sueltan a los 4 s.
- **`clock.go`**: cada paquete de WASAPI trae el instante en que se capturó (QPC); la posición exacta de cada muestra es `(qpc − cero) · rate`.
- **`levels.go`**: el pulso por stdout: `LV <ms> <idx>:<dB> …` cada 50 ms y `DEV <+|~|z|-> <idx> <salida|mic> <seg> <nombre>` cuando un dispositivo se suma, vuelve, queda en espera o se va.
- **`guard.go`**: handle al proceso padre abierto al arrancar (un PID reciclado no engaña) y la guardia de disco.

Estado JSON (`-status`): `state`, `reason`, `started`, `elapsed`, `disk_free_mb`, `parent_alive`, `console_dropped`, `levels_dropped`, `device_events` y, por pista, `kind`, `state`, `hands_free`, `joined_s`, `file`, `seconds`, `audio_seconds`, `peak_db`, `peak_total_db`, `mb`, `pending_mb`, `lost_seconds`, `write_error`, `paused`, `gone`, `kept`.

## Pruebas

`go test ./...`: 16 casos con disco falso y dispositivos falsos (disco lleno un rato, cola desbordada, escritor trabado, escrituras parciales, silencio en su lugar, consola con el caño lleno, conversión de muestras; dispositivo nuevo a mitad, el que vuelve, el mic que se suelta, la espera tras un error, formato nuevo a pista nueva, nombres repetidos, selector de salida por ID). En `pruebas\` hay dos scripts de Python para medir el consumo de CPU y una prueba de integración con un caño sin leer.
