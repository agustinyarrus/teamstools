# diag

Las sondas y pruebas con las que se descubrió cómo leer Teams. Ninguna es necesaria para usar la app; todas son útiles cuando Microsoft cambia algo.

| Herramienta | Qué hace |
|---|---|
| `uia-dump.ps1` | vuelca el árbol de accesibilidad de las ventanas de Teams (`-SoloTitulo 'reuni'`, `-Raw`). Sólo lectura. Con esto se encontraron todos los `AutomationId` |
| `sonda-uia\` | `probe.exe`: modos `chats`, `mensajes`, `enviar`, `tipear`, `directo`, `rico`, `formato`, `limpiar` — cada camino de lectura y escritura por separado, contra tu propio chat |
| `sonda-adjuntos\` | prueba en vivo cómo recibe Teams imágenes, archivos y varios mensajes seguidos sin mostrarse (`estado`, `abrir-yo`, `arbol`, `imagen`, `archivo`, `combo`…). Tu nombre como lo muestra Teams va en la variable de entorno `TEAMS_YO` |
| `prueba-invisible.ps1` | oculta las ventanas de Teams, manda un mensaje con la app y muestrea la pantalla cada 150 ms: máximo 0 ventanas de Teams visibles = 100 % invisible |
| `espia-ventanas\` | `EspiaVentanas.cs`: registra qué le pasa a cada ventana top-level del escritorio (se muestra, se oculta, se minimiza, toma el foreground, se destruye) con proceso, clase y título. Sólo lectura, WinEvent fuera de contexto. Compilar con el `csc` del Framework (es C# 5) |
| `probar-f15.ps1`, `probar-toque.ps1` | confirman que la tecla fantasma F15 (y los otros toques) reinician el reloj de inactividad de Windows sin mover el mouse ni escribir |
| `despertar-teams.ps1`, `ventana.ps1`, `abrir-chat.ps1` | `ShowWindow` a mano sobre un hwnd, montar el contenido de Teams sin robar el foco, abrir un chat por deep link |
| `limpiar-ime.ps1` | vuelve a ocultar las ventanas auxiliares de Teams (`Default IME`, `MSCTFIME UI`…) si alguna sonda las destapó por error |
| `whisper-falso.py` | un transcriptor determinista y sin modelo, con el mismo contrato que `transcribe.py`: prueba el pipeline por trozos de TeamsTools en segundos (`--probar-grabador --archivo x.wav --transcriptor diag\whisper-falso.py --falla-en t03`) |

Las carpetas `out\`, `bin\` y `obj\` de las sondas y los `.log` del espía no se versionan: traen árboles de accesibilidad con nombres reales.
