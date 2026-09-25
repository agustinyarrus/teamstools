# El grabador, por dentro

Graba cada llamada de Teams (lo que sale por los parlantes y lo que entra por tu micrófono), la archiva en `.opus`, la transcribe con motores locales, separa quién habla y la resume. Este documento cuenta cómo, con las constantes exactas, y por qué cada pieza existe.

## La máquina de estados

Los estados son strings (así los índices viejos siguen valiendo): `grabando · cerrando · grabada · comprimiendo · transcribiendo · transcripta · resumiendo · lista · falló · descartada`.

| Desde | Hacia | Quién |
|---|---|---|
| — | grabando | `Arrancar`, al detectar la llamada |
| grabando | cerrando | `Cortar`: gracia vencida, interruptor apagado, «cortar ya», loopcap cayó más de 8 veces, cierre de la app |
| cerrando | grabada · papelera | `Finalizar`: repara encabezados, mide segundos; sin pistas o más corta que `MinimoSegundos` va a `_papelera` |
| grabada | comprimiendo → transcribiendo | `Comprimir` |
| transcribiendo | transcripta (si hay resumen) · lista | `Transcribir` |
| transcripta | resumiendo → lista | `Resumir` |
| cualquier paso | falló | `Fallar` — **nunca toca el audio** |
| falló · lista | grabada · transcribiendo · transcripta | `Reintentar`, según haya WAV sin `.opus`, `.opus`, o sólo texto |
| al reabrir la app | el punto verificado más avanzado | `CargarIndice`: resumiendo→transcripta; transcribiendo con `.opus`→transcribiendo; hay WAV→grabada; hay `.opus`→transcribiendo; nada→falló |

## Dos hilos, un actor

- `grabador-control` es un actor con buzón (`BlockingCollection<Action>`): la interfaz sólo encola (`Mirar`, `CortarAhora`, `Reintentar`, `Borrar`, `GuardarConfig`) y vuelve al instante; el bucle atiende el buzón cada 250 ms y `Vigilar` corre una vez por segundo. Prioridad Normal a propósito: bajarla invita a una inversión de prioridad con el candado tomado.
- `grabador-cola` procesa una grabación por vez, la más reciente primero, y duerme 15 s si no hay nada.
- La interfaz lee una `FotoGrabador` inmutable (`Volatile.Read`), publicada como mucho cada 250 ms salvo cambios de estado.

Antes de esto, `Cortar` corría en el hilo de la interfaz con un `WaitForExit(15000)`: la ventana quedaba congelada 15 s. Fue el «se quedó grabando» que se vio una vez.

## Cómo se detecta la llamada

Cada lectura del vigía llega con la `Lectura`, si hubo gente, si tu presencia nativa dice «En una llamada» y si tu presencia dice Disponible/Ausente.

- **Dos disparadores**: la ventana de la reunión, o **tu presencia** (si Teams dice «En una llamada / reunión / Presentando» pero la ventana no se ve —minimizada, compacta, árbol sin montar— igual graba: loopcap captura el audio del sistema, no necesita ver nada). Se llama «reunión (detectada por tu presencia)» hasta que aparece la ventana.
- Con ventana se respeta `SoloConGente`. `MaxOtros` guarda el máximo de participantes que mostró Teams: es el tope para separar voces.
- **Al primer «no veo la llamada» no se corta**: la lectura por UIA parpadea (una reunión de 58 minutos quedó partida en 12 grabaciones por eso). Corre una gracia de `SegundosParaCortar` (90 s) que baja a `GraciaCortaSegundos` (8 s) si tu presencia confirma que saliste, incluso a mitad de la espera. Si la reunión vuelve, se anota y sigue la misma grabación.

## loopcap

```
-o "<dir>\audio[.tN].wav" -all -mic -levels -mono -keepsilent -quiet
-stop "<dir>\STOP[.tN]" -status "<dir>\estado[.tN].json" -parent <pid> -t 21600 -minfree 400
```

stdout se drena línea por línea hacia `PulsoAudio` (los `LV` y `DEV`); stderr, a la bitácora. Ambos siempre: un caño sin leer se llena a los ~21 s y el bucle principal queda colgado en un `Fprintf` (la causa raíz de una reunión que quedó «declarando 21 s» de 26 minutos). loopcap **no** entra al Job Object: corta solo y limpio con `-parent`.

**Tramos**: `Vigilar` lee `estado[.tN].json`; si el proceso murió o el `updated` tiene más de 25 s, escribe STOP, espera 4 s, mata y arranca un tramo nuevo con backoff 1, 2, 4, 8 s (tope 10). Más de `ReiniciosMaximos` (8) ⇒ corta y conserva lo grabado. Al final los tramos se concatenan alineados.

Al arrancar el grabador, `CortarLoopcapsHuerfanos` busca loopcaps de otra corrida por su línea de comando (WMI) y les escribe el STOP.

### El protocolo y el estado

- `LV <ms> <idx>:<dB> …` cada 50 ms (−120 = nada) y `DEV <+|~|z|-> <idx> <salida|mic> <seg> <nombre>` (se sumó · volvió · en espera · se desconectó). Cola de 128 renglones; si nadie lee, se descarta y se cuenta (`levels_dropped`).
- `estado.json` (escritura atómica, una vez por segundo y al final): `state`, `reason`, `started` (el cero de las pistas, RFC 3339), `elapsed`, `disk_free_mb`, `parent_alive`, `console_dropped`, `levels_dropped`, `device_events`, y por pista `kind`, `state`, `hands_free`, `joined_s`, `file`, `seconds`, `audio_seconds`, `peak_db`, `peak_total_db`, `mb`, `pending_mb`, `lost_seconds`, `write_error`, `paused`, `gone`, `kept`.

### El diseño

| Pieza | Qué garantiza |
|---|---|
| `writer.go` — un actor por pista | la captura nunca hace I/O ni espera: canal acotado de 512 trozos de 32 KiB; cola pendiente hasta 48 MiB; reintento con backoff 50 ms → 2 s; al volver el disco, primero un **silencio del largo perdido** para no correr el reloj; el encabezado se reescribe cada segundo con lo que hay de verdad en disco |
| `console.go` — la consola que nunca bloquea | cola fija de 256 mensajes vaciada por una goroutine, medidor «el más nuevo gana» |
| `hotplug.go` — el vigía de dispositivos | por eventos de Windows (`IMMNotificationClient` y las sesiones de audio) con debounce de 40 ms y una ronda cada 5 s; endpoint nuevo → pista nueva alineada al reloj; el que vuelve sigue en su pista; otro formato → pista «(2)»; micrófonos y manos libres se graban **sólo mientras otro proceso los usa** (abrir el mic de unos auriculares Bluetooth los pasa al perfil de llamada y le arruina el audio a quien los usa) y se sueltan a los 4 s |
| `clock.go` — QPC | cada paquete trae el instante en que se capturó; la posición exacta es `(qpc − cero) · rate`: una pista que retoma después de un silencio cae en su lugar al milisegundo |
| `guard.go` | handle al padre abierto al arrancar (un PID reciclado no engaña); con menos de `minfree` MB se pausan las pistas mudas y vuelven al sonar o al superar 2 × minfree |

Medido: loopcap 3 gasta ~1,4 % de un núcleo (v2: ~2,1 %). Una pasada del vigía por sondeo costaba ~110 ms de llamadas al servicio de audio con la máquina cargada; por eventos, casi nada.

## El pipeline

1. **Finalizar**: `Wav.Reparar` en cada pista (RIFF y `data` honestos), duración = suma por tramo del máximo de sus pistas, `SegundosPerdidos` = máximo de `lost_seconds`.
2. **Comprimir**: energía por muestreo (100 ms cada 2 s); una pista «suena» si no es muda **o** si el pico exacto que anotó loopcap supera −60 dB (un mic con dos frases en media hora salía mudo con el muestreo). UNA pasada de ffmpeg: `-ignore_length 1` por entrada; el micrófono con `volume=0:enable='between(t,a,b)+…'` en los intervalos en que Teams te tenía silenciado (de `mic-teams.jsonl`, corridos −0,2 s por la demora del vigía); `amix normalize=0` + `alimiter limit=0.95`; `concat` de tramos; `asplit` → `libopus -b:a 32k -application voip` a `audio.tmp.opus` y `-ar 16000 pcm_s16le` a `audio16.tmp.wav`. Progreso por `out_time_us`. **ffprobe verifica** que ambos duren lo esperado ±max(2 s, 1 %) y recién ahí se renombran (`File.Replace`). Con pista de mic y de salida, `SepararMicYSalida` deja `mic16.wav` y `salida16.wav` en la misma línea de tiempo.
3. **Transcribir**: la vara es la duración **verificada** (`SegundosAudio`), nunca el propio `audio16.wav` (era circular). `hablantes.py` corre una vez sobre la reunión entera (con `--mic/--salida` si existen; caché en `diar\`). `PlanDeTrozos`: cortes cada `TrozoSegundos` en un cambio de turno dentro de ±min(45 s, 20 %) o, sin turnos, en el punto más callado (`Wav.PuntoMasCallado`). Cada trozo: `ffmpeg -ss -t -c copy` → `trozos\tNN.wav`, turnos recortados a `tNN.cortes.json`, y **doble verificación**: el script imprime `duracion=Ns` al arrancar y si ve menos que lo esperado −max(3 s, 2 %) se aborta antes de gastar CPU; al terminar, `TrozoListo` exige `duration` ≥ esperado −max(2 s, 2 %) y, con hablantes, `"hablantes": true`. Un trozo listo se saltea al reintentar. `UnirTrozos` corre los tiempos al reloj de la reunión, escribe `audio16.json/.srt/.txt` y `audio16.hablantes.txt`, y verifica que lo leído ≥ total −max(3 s, 2 %). Recién entonces `LimpiarWavs` (si `BorrarAudio` y el `.opus` existe).
4. **Resumir**: `AsistenteIA.Verificar`; texto truncado a 9 000 caracteres; prompt fijo (línea de resumen, «Temas:», «Decisiones:», «Pendientes:», sin inventar); sin modelo queda «lista (sin resumen)».

`CorrerPaso` (transcriptor y hablantes) mapea `[progreso] P%` al total, **pausa con `NtSuspendProcess`** mientras estás en una llamada, fuerza `BelowNormal` y aplica un tope de CPU activa (el cronómetro se detiene en pausa): whisper max(45 min, esperado × 5, × 14 en «maximo»); sherpa max(20 min, esperado × `FactorTope`); hablantes max(20 min, total × 2). ffmpeg, ffprobe y python van en un **Job Object** con `KILL_ON_JOB_CLOSE`.

Disco: no arranca con menos de 300 MB libres, avisa en la bitácora bajo 2 GB, y loopcap pausa las pistas mudas bajo 400 MB.

## Los archivos de una grabación

`datos\grabaciones\<yyyyMMdd-HHmmss>\`:

| Archivo | Qué es |
|---|---|
| `audio[.tN][.mic]__<dispositivo>.wav` | las pistas de loopcap (tramo N, `.mic` = tu micrófono, `__dev` = el nombre saneado); regex `^audio(?:\.t(?<n>\d+))?(?<mic>\.mic)?(?:__(?<dev>.+))?\.wav$` |
| `*.tmp.opus`, `*.tmp.wav` | temporales: nunca pasan por buenos |
| `audio.opus` | el archivo definitivo (~14 MB por hora a 32 kbps) |
| `audio16.wav`, `mic16.wav`, `salida16.wav` | 16 kHz mono para transcribir y separar «Yo» |
| `audio16.json` `.srt` `.txt` | la transcripción unida: `{"language","duration","segments":[{id,start,end,text,speaker?}]}` |
| `audio16.hablantes.txt` / `.json` | «[hh:mm:ss] Persona N: …» y `{"config","duracion","hablantes","turnos":[[ini,fin,quien]]}` |
| `trozos\tNN.*` | los trozos reanudables; la carpeta se borra al unir |
| `estado[.tN].json`, `STOP[.tN]` | el estado vivo de loopcap y su señal de corte |
| `mic-teams.jsonl` | `{"t":"<ISO>","silenciado":true\|false}` por cada cambio del botón de mic |
| `nombres.json` | `{"_ayuda":…, "Persona 3":"Nombre"}` |
| `diar\diar-<sha1>-…npz` | caché de segmentación y huellas de voz |
| `_papelera\<id>\` (en la raíz de `grabaciones`) | descartes y borrados, purga a los `DiasPapelera` |
| `indice.json` | el índice; si no se puede leer se copia a `indice.ilegible-<ts>.json` y no se importa nada como huérfano |

> [!CAUTION]
> `JavaScriptSerializer` escribe las fechas como `\/Date(ms)\/` **en UTC**. Reescribir el índice con otro serializador que no las escape rompe la carga entera; el lector las normaliza, y muestra las horas en local (una reunión de las 10:01 aparecía como 13:01).

## Quién habla

`hablantes.py` reconstruye el pipeline de pyannote 3 sobre ONNX + sherpa-onnx: segmentación (`pyannote-segmentation-3-0`, ventanas de 10 s, marcos de ~17 ms, decodificación powerset), huellas por ventana donde habla una sola persona (ERes2Net), aglomerativo con enlace promedio y umbral **0,22** (elegido con el banco: DER 16 % limpio / 9,5 % Teams, 4 de 4 personas), asignación por ventana con `linear_sum_assignment` (dos locales de la misma ventana nunca son la misma persona), partición completa en turnos con los silencios partidos al medio y los turnos de menos de 0,25 s fundidos.

**«Yo» sale del micrófono**: marcos de 100 ms; tu voz es donde el mic está 15 dB sobre su piso (percentil 10) y a −3 dB o más respecto de la salida (el eco llega mucho más bajo); mediana de 3 marcos; huecos de hasta 0,35 s se rellenan (hasta 1 s si nadie habla por la salida); mínimo 0,25 s. La diarización corre sobre `salida16`, donde sólo están los demás, y `fundir` impone tus intervalos sobre la partición. Con el mic aparte, el WER con atribución de hablante (cpWER) bajó de 35 % a 14 % y «Yo» cubre el 99 % de tu voz.

Con Cohere se transcribe turno por turno (`--cortes`; los turnos de más de 20 s se parten en su marco más callado) y el resultado es `[00:01:12] Persona 2: …`.

## El vigía del micrófono

`VigiaMic` busca el botón `microphone-button` de la reunión (o un botón cuyo nombre hable de micrófono) y después sólo lee su nombre cada 300 ms: «reactivar / activar el micrófono / unmute» ⇒ silenciado; «silenciar / mute» ⇒ abierto. Respaldo: tu ficha con «Silenciado». Cada cambio va con hora a `mic-teams.jsonl`, y al archivar esos tramos entran con volumen 0.

## Pruebas

| Modo | Qué hace |
|---|---|
| `--probar-grabador [--segundos N] [--sin-whisper] [--matar-loopcap]` | grabación real de punta a punta en `datos\prueba-grabador` con llamada simulada: estado en vivo, pulso ≥ 12/s, gracia corta, encabezados honestos, `.opus` verificado, mute registrado, retoma en tramo 2 si se mata loopcap |
| `--probar-grabador --archivo x.wav [--claves a,b,c] [--trozo 90] [--idioma en]` | el pipeline entero sobre un audio de verdad conocida: ≥ 85 % de las claves reconocidas y en orden |
| `--probar-grabador --archivo x.wav --transcriptor diag\whisper-falso.py --falla-en t03` | trozos, falla simulada, reintento que retoma («2 ya estaban hechos»), cada segmento en su lugar exacto del reloj, cobertura completa: 10/10 en 25 s |
| `--probar-grabador --mezcla-mic` | grabación sintética (440 Hz salida, 880 Hz mic desde 2 s, silencio de Teams 5–9 s) y análisis Goertzel por 100 ms: 12 chequeos |
| `--foto-banda DIR` | la banda en vivo y el REC con un pulso sintético, en PNG |
| `--reintentar <id>` | la instancia abierta reprocesa una grabación con el motor y los ajustes de hoy |
