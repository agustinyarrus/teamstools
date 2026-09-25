# transcribe

Transcripción y separación de voces, **100 % local**. TeamsTools los llama con el mismo contrato de stdout, así que sirven también sueltos.

| Script | Qué hace |
|---|---|
| `transcribe_sherpa.py` | transcribe con los motores de `motores.py` (Cohere Transcribe 2B por defecto, Parakeet TDT 0.6B, Qwen3-ASR, FastConformer) sobre sherpa-onnx |
| `transcribe.py` | transcribe con faster-whisper (large-v3), el camino viejo |
| `hablantes.py` | «quién habló cuándo» de una reunión entera (pyannote + ERes2Net sobre ONNX) y «Yo» desde tu micrófono |
| `motores.py`, `diarizacion.py`, `texto.py`, `consola.py` | los motores, la diarización, la normalización de texto y la consola de las pruebas |
| `banco_verdad.py`, `banco_asr.py`, `banco_hablantes.py`, `reunion_sintetica.py`, `prueba_hablantes.py` | el banco de pruebas: una daily rioplatense sintetizada con voces neuronales y verdad conocida palabra por palabra, para medir WER, DER y cpWER de cada motor y configuración |

## Uso

```
python transcribe_sherpa.py audio16.wav [--motor cohere|parakeet+b1.5|qwen3+kw|conformer] [--lang es] [--threads N] [--cortes turnos.json]
python transcribe.py audio.wav [--lang es] [--compute float32] [--beam 10]
python hablantes.py audio16.wav [--salida salida16.wav --mic mic16.wav] [--max-hablantes N] [--cache DIR]
```

Salidas junto a la entrada: `<base>.txt` (texto corrido), `<base>.srt`, `<base>.json` (`{"language", "duration", "motor", "segments": [{id, start, end, text, speaker?, words?}]}`) y, con `--cortes`, `<base>.hablantes.txt` («[hh:mm:ss] Persona 2: …»). `hablantes.py` escribe `<audio>.hablantes.json` = `{"config", "duracion", "hablantes": ["Yo", "Persona 1", …], "turnos": [[ini, fin, quien], …]}`.

Renglones que lee TeamsTools: `[modelo] … cargado en Ns` · `[info] idioma=es … duracion=Ns` · `[progreso] P% …` · `[ok] …`. El `.json` se escribe **último** y de forma atómica: es la marca de trozo terminado.

## Modelos y dependencias

| Variable | Default | Qué va ahí |
|---|---|---|
| `ASR_MODELOS` | `C:\Apps\asr-modelos` | los modelos de sherpa-onnx: `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8`, Cohere Transcribe 2B, Qwen3-ASR 0.6B, FastConformer |
| `DIARIZACION_MODELOS` | `C:\Apps\asr-modelos\diarizacion` | `sherpa-onnx-pyannote-segmentation-3-0` y las huellas (`3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx`, `wespeaker_en_voxceleb_resnet34_LM.onnx`) |
| `WHISPER_MODEL_DIR` | `C:\Apps\whisper\models\large-v3` | faster-whisper large-v3 |
| `ASR_MOTOR` | `cohere` | el motor por defecto de `transcribe_sherpa.py` |

```
pip install sherpa-onnx onnxruntime numpy scipy      # Cohere / Parakeet / diarización
pip install faster-whisper                           # whisper
pip install edge-tts num2words rapidfuzz             # sólo el banco de pruebas
```

Los modelos se bajan de sus repos de HuggingFace / GitHub (los de sherpa-onnx están en las releases de `k2-fsa/sherpa-onnx`). En una red con inspección TLS, `huggingface_hub` se cuelga: bajarlos con el navegador o con `Invoke-WebRequest`.

## Por qué así

- **Sin VAD**: un detector de voz descarta lo que cree que no es habla, y ahí es donde un transcriptor se come palabras (una voz baja, alguien lejos del micrófono). El audio se corta en tramos de hasta 20 s en el marco **más callado** cerca de cada límite y se transcribe el 100 %, en lotes de 6.
- **Compuerta de voz**: al transcribir turno por turno aparecen trozos de puro silencio y los motores inventan «Thank you.» / «Yeah.». Un trozo sin 100 ms seguidos 10 dB sobre el piso de ruido no se le da al motor.
- **Cohere pide `use_punct` y `use_itn` explícitos**; sin ellos no puntúa ni escribe números en cifras.
- **La diarización va sobre la reunión entera** una vez (por trozo, «Persona 1» cambiaría de trozo a trozo) y lo caro queda en caché `.npz` por SHA-1 del audio: re-etiquetar cuesta milisegundos. Elegido con el banco: pyannote + ERes2Net, paso 2 s, enlace promedio, umbral 0,22.
- **«Yo» sale del micrófono, no se adivina**: tus turnos son donde el mic está 15 dB sobre su piso y a la par de la salida (el eco llega mucho más bajo).

## Números (banco de verdad conocida: 557 palabras, 4 voces, 3 condiciones)

| motor | limpio | como Teams | duro | voz baja comida (duro) | velocidad |
|---|---|---|---|---|---|
| Cohere Transcribe 2B | 3,8 % | 3,0 % | 6,8 % | 6 % | ~0,5× tiempo real |
| whisper large-v3 | 2,9 % | 2,3 % | 11,1 % | 26 % | 1,5–20× |
| Parakeet TDT 0.6B | 3,8 % | 4,1 % | 11,1 % | 7 % | ~0,2× |
| Qwen3-ASR 0.6B | 5,6 % | 6,3 % | 16,0 % | 30 % | ~0,8× |
| FastConformer es | 9,3 % | 10,6 % | 28,2 % | 83 % | ~0,07× |

WER = palabras mal + comidas + sobrantes, sobre las palabras dichas. `banco_verdad.py` sintetiza la reunión con voces neuronales de Edge (AR y UY) y guarda el tiempo exacto de cada palabra; `banco_asr.py` y `banco_hablantes.py` corren cada motor y cada configuración de diarización contra esa verdad.
