# teams-chats

El historial entero de Teams, leído del disco. **Python puro, sin una sola dependencia**: ni snappy, ni leveldb, ni nada de pip. No abre Teams, no toca la nube, no hace falta cerrar nada.

```
python actualizar_extraccion.py                       copia la base a %TEMP% y extrae → %TEMP%\teams-extraido\mensajes.jsonl
python extraer.py <dir_leveldb_copiado> <dir_salida>  la extracción sola, sobre una copia
```

Los chats de Teams nuevo viven en un IndexedDB de Chromium:
`%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\WV2Profile_tfw\IndexedDB\https_teams.microsoft.com_0.indexeddb.leveldb`. `actualizar_extraccion.py` copia los archivos (ignorando el `LOCK`, que Teams tiene tomado) y corre `extraer.py` sobre la copia.

| Módulo | Qué hace |
|---|---|
| `leveldb.py` | lee LevelDB sin la librería nativa: varints, **snappy descomprimido a mano** (literal, copy-1/2/4 con solapamiento byte a byte), SSTables `.ldb` (footer, índice, bloques con prefijo compartido y restarts) y el WAL `.log` (bloques de 32 KiB, `WriteBatch`); funde todo por secuencia, el log pisa a las tablas, los borrados se omiten |
| `idb.py` | decodifica el esquema de IndexedDB de Chromium (`indexed_db_leveldb_coding.cc`): ids de base, store e índice, strings UTF-16BE, `IDBKey`; `scan_metadata` arma bases y stores por nombre; `iter_records` recorre un store |
| `v8serial.py` | deserializa el formato estructurado de V8/Blink (objetos, arrays densos y dispersos, Map, Set, strings latin-1/UTF-16/UTF-8, números, bigint, Date, referencias, RegExp, ArrayBuffer y sus vistas) y lo deja apto para `json.dumps` |
| `extraer.py` | junta `profiles` (quién es cada MRI), `conversations` (cómo se llama cada hilo) y `replychains` (los mensajes), limpia el HTML (citas → `[re: Autor]`, emojis por su `alt`, menciones por `@nombre`, listas), y rescata de los `Event/Call` la lista exacta de participantes con su duración |

## `mensajes.jsonl`

Un objeto por línea, ordenado por conversación y fecha:

| Clave | Contenido |
|---|---|
| `conv_id` | el `conversationId` (lo único estable) |
| `conv` | nombre de la conversación, o el id si no se resolvió |
| `conv_tipo` | el `type` de Teams |
| `fecha` | ISO 8601 en UTC (`+00:00`), o `null` |
| `autor` | el nombre para mostrar |
| `mio` | `true` si lo escribiste vos (`isSentByCurrentUser`, o el id de `TEAMS_YO` si lo definís) |
| `tipo` | `RichText/Html`, `Text`, `Event/Call`, `ThreadActivity/…` |
| `texto` | texto plano; vacío en el ruido (altas, llamadas) |
| `borrado` | hay `deletionInfo` |
| `id` | id del mensaje |
| `llamada` | sólo en `Event/Call` con lista: `{"estado": "ended\|started\|missed\|cancelled\|connecting", "gente": [{"id": MRI, "nombre": string, "dur": segundos}]}` |
| `call_id` | junto a `llamada` |

También escribe `indice.md` con una tabla por conversación (tipo, mensajes, con texto, desde, hasta).

Tarda de decenas de segundos a unos minutos según el tamaño de la base (los IndexedDB de Teams suelen pesar entre decenas y cientos de MB); todo en memoria. TeamsTools lo dispara con «releer Teams» y lee el jsonl con un parser plano propio.
