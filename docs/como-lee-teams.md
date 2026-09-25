# Cómo lee (y escribe en) Teams

TeamsTools no usa ninguna API de Microsoft, ni tokens, ni la nube: trabaja sobre lo que Teams ya muestra en pantalla y lo que ya escribe en disco. Este documento es el mapa de todo eso, tal como se verificó en vivo con Teams nuevo (`ms-teams.exe`, versión 26213, interfaz en castellano).

## Las ventanas

Teams nuevo es una aplicación web dentro de WebView2. Sus ventanas de verdad son las de clase **`TeamsWebView`**; el proceso tiene además ocho ventanas auxiliares ocultas (`Default IME`, `MSCTFIME UI`, `GDI+ Hook Window Class`, `RtcPalVideoPnPMonitorHWND`, `OleDdeWndClass`…).

> [!CAUTION]
> Toda operación sobre una ventana de Teams filtra por `Clase == "TeamsWebView"` **antes** de tocarla. Destapar una auxiliar con `ShowWindow` la deja como una barrita gris estilo Windows 3.1 pegada a la barra de tareas. Pasó, y `diag\limpiar-ime.ps1` existe por eso.

La ventana principal se reconoce por el título (`… | Microsoft Teams`); una reunión, por su propio título. **Con la ventana minimizada el árbol de accesibilidad está entero** (el rectángulo queda en −32000 pero los nodos están). Con la ventana **oculta** en la bandeja durante horas, el DOM no está montado: la ventana expone 6 elementos en vez de ~450. `ShowWindow(SW_SHOWMINNOACTIVE)` la destapa como minimizada (sale en la barra de tareas, nunca en pantalla) y en ~1 s el árbol se monta.

## La reunión (`TeamsScanner.cs`)

Todo se acota al documento `AutomationId = RootWebArea`: fuera de él hay un `MenuItem "System"` (el menú de sistema) que inflaba los conteos.

| Qué | Dónde | Nota |
|---|---|---|
| botón colgar | `Button hangup-button` `[Invoke]` | independiente del idioma; `Invoke()` cuelga con la ventana minimizada en ~2 s |
| reloj | `Text call-duration-custom` | el `Name` queda congelado («Tiempo transcurrido 30:03»); el valor vivo está en el **hijo** `Text` que matchea `^\d{1,2}:\d{2}(:\d{2})?$` |
| gente | `Button roster-button` | no expone contador; sirve para abrir el panel |
| participantes | un `MenuItem` por persona: «Apellido, Nombre, El vídeo está activado, …, Menú contextual está disponible» | la galería pagina de a 9: `Button "Navegar a la página de vídeo siguiente. Actualmente 1/2"` |
| tu ficha | `Image "Vídeo de mí mismo, Apellido, Nombre, …, Silenciado, …"` | de ahí se aprende tu nombre y si Teams te tiene en silencio |
| contenido compartido | `MenuItem "Contenido compartido por Apellido, Nombre"` | cuenta como «hay gente» si no es tuyo |
| panel Gente | `Group roster-title-section-2 "En esta reunión, 12 en total"` (+ `roster-title-section-3 "Otros invitados, 2 en total"`) | formato con coma y «en total»; `Invoke` en `roster-button` abre y el mismo `Invoke` cierra |
| vista compacta | ventana aparte `Vista compacta de la reunión \| X \| Microsoft Teams` | tiene `hangup-button` pero **no** `roster-button` y muestra un solo altavoz: no sirve para contar |

Otros ids útiles: `chat-button`, `raisehands-button`, `video-button`, `microphone-button`, `share-button`, `callingButtons-showMoreBtn`, `e2ee-status`, `roster-participants-search-input`.

Cada lectura arma una `Lectura` inmutable (llamada, ventana, reunión, otros, fichas, compartido, página, tu ficha, nombres, milisegundos) que consume el vigía. Una lectura tarda ~340 ms con 12 personas.

### El vigía (`Watcher.cs`)

Máquina de estados explícita: `SinTeams → SinLlamada → EnLlamada → EsperandoGente → SalaVacia → Pospuesto → Saliendo → Salido`, más `Pausado`. Reglas:

- `Otros > 0` o hay contenido compartido ⇒ **EnLlamada** (si venía de vacía, se cancela la cuenta).
- nunca hubo nadie y `requiereHaberVistoGente` ⇒ **EsperandoGente** hasta `salirSiNadieLlegaMinutos` (0 = para siempre).
- se vació ⇒ `VacioDesde`, evento `SalaVaciada`, gracia de `graciaSegundos`; durante la cuenta la lectura baja a 1 s.
- antes de salir, si `verificarConRoster` y no es vista compacta, abre el panel Gente: si dice más de 1, **no sale**.
- hasta 3 intentos de salida con 20 s entre ellos; `simulacion` marca la salida sin tocar nada.

Todo va a `estado.json` en cada lectura (`estado`, `reunion`, `otros`, `nombres`, `segundosRestantes`, `presencia…`, `ui` con los percentiles de la interfaz, `grabador`, `disco`).

## La lista de chats y los mensajes (`Mensajeria.cs`)

| Elemento | Cómo se identifica | Patrón |
|---|---|---|
| lista de chats | `Tree "Teams"` → `TreeItem` | nombre «Mensaje sin leer Chat Apellido, Nombre Ausente»: se parsea prefijo de no leído, tipo (Chat, Chat de grupo, Chat de reunión, Equipos y canales), nombre, presencia; el sufijo «(Usted)» marca tu propio chat |
| abrir un chat | el `TreeItem` | `SelectionItemPattern.Select()` (sin foco, sin ventana); se verifica el título `Chat \| nombre \| Microsoft Teams` hasta 5 s |
| un mensaje | `Group message-body-<ts>` | `ts` en milisegundos Unix; el texto en `content-<ts>`; el autor es el `Text` hermano anterior |
| el editor | `Edit new-message-*` | `ValuePattern` para leer lo que hay |
| enviar | `Button "Enviar (Ctrl+Enter)"` | `Invoke` |
| barra de formato | `Button "Negrita"`, «Cursiva», «Subrayado», «Tachado», «Lista con viñetas», «Lista numerada», «Cita», «Bloque de código» | `Toggle` / `Invoke` |
| a dónde se tipea | ventana hija de clase `Chrome_RenderWidgetHostHWND` | `PostMessage(WM_CHAR)` |

> [!IMPORTANT]
> La lista de presencias que se recorta del nombre incluye «En una reunión» / «In a meeting». Sin ella, el estado quedaba pegado al apellido (`Apellido, Nombre En una reunión`) y esa persona contaba como dos distintas en el equipo y en los corrillos.

### Cómo se manda sin mostrar nada

1. Si Teams está cerrado a la bandeja, `SW_SHOWMINNOACTIVE` (la ventana conserva su árbol; fuera de pantalla no sirve porque Chromium no renderiza off-screen y no monta el editor). Al final, `SW_HIDE`.
2. Texto plano: `WM_CHAR` posteado al render con la ventana minimizada. Se espera a que `ValuePattern` estabilice el valor (no un `Sleep` fijo: daba falsos negativos y el reintento **re-posteaba sin limpiar**, «dame un cachitodame un cachito»); antes de reintentar se confirma que el editor quedó **vacío**.
3. Con formato: `SetFocus` en el editor (Teams pasa a foreground pero sigue minimizada) y atajos por `SendInput`; el texto va como Unicode. `Shift+Enter` entre líneas, nunca Enter suelto.
4. Imágenes: portapapeles con el stream `PNG` + `CF_BITMAP` y `Ctrl+V`; se espera a que el adjunto quede listo (hasta 45 s, 12 lecturas quietas).
5. `Invoke` en Enviar y verificación de que el editor quedó vacío.
6. Devolver el foco: una ventana minimizada puede quedarse con el foreground; se la minimiza de nuevo, se devuelve a quien lo tenía y, como red final, se fuerza que **no** quede en Teams.

> [!CAUTION]
> **La tecla fantasma F15 le sabotea el foreground al envío**: Windows le niega el foreground a una app que no acaba de recibir input del usuario, y el pulso de F15 de la permanencia online cuenta como input reciente. `SetFocus` solo no alcanza: si a los 600 ms Windows no dio el foreground, se fuerza con `AttachThreadInput` + `BringWindowToTop` + `SetForegroundWindow` (techo 2400 ms).

Las `CF_HDROP` (pegar un archivo) Teams las **ignora**: no falla, no pasa nada (verificado comparando la firma de los 874 nodos del documento antes y después de `Ctrl+V`). El botón «Adjuntar archivos» es `LeafNode` sin `InvokePattern`; su atajo `Alt+Shift+O` abre el flyout, pero el diálogo nativo depende de un gesto de usuario confiable que Chromium acepta 1 de 5 veces. Un autocontestador desatendido no puede depender de eso, así que los archivos no-imagen no se mandan.

### La aduana (`TeamsGate.cs`)

Tres hilos (observador cada 30 s, autocontestador cada 6 s, presencia cada 20 s + rescate) tocaban las mismas ventanas y el mismo árbol a destiempo. Uno le hacía `SW_HIDE` a la ventana mientras otro caminaba el árbol → `COMException`, y el rescate robaba el foreground (parecía que «se cerraba Chrome»). La cura es **un único `Monitor` reentrante** que se toma alrededor de la **operación completa** (abrir → leer → escribir → enviar), no método por método. Cinco puntos de entrada: el autocontestador, el observador, la medición de presencia (la F15 va **por afuera**: sostener la presencia no puede depender de que Teams esté libre), el cron y el guión. Un solo lock ⇒ imposible deadlock. Verificado 65 s bajo carga real: 0 `COMException`, 0 focos robados (antes: 6).

## El avatar y la presencia

El botón `idna-me-control-avatar-trigger` se llama «Tu perfil, estado Disponible » (con espacio final) ⇒ leer la presencia real es leer ese nombre, barriendo **todas** las ventanas `TeamsWebView` y descartando la compacta. No expone `InvokePattern` pero sí **`ExpandCollapsePattern`**: con eso se abre el menú de perfil desde UIA puro, sin teclas, sin foreground, sin ventanas.

```
avatar  idna-me-control-avatar-trigger        [ExpandCollapse]   → Expand()
  └ MenuItem «Ausente, cambiar estado»        [ExpandCollapse]   → Expand()
      └ RadioButton «Disponible»              [Invoke]           → Invoke()
        RadioButton «Ocupado» / «No molestar» / «Vuelvo enseguida» / «Aparecer como ausente» / «Desconectado»
        MenuItem   «Duración»                 nova-me-control-set-availability-duration
        MenuItem   «Restablecer estado»       nova-me-control-set-availability-reset-status
```

- Cada nivel del menú vive en su **propio popover portalizado** al `RootWebArea`, no como hijo del ítem: hay que barrer la ventana entera.
- Las opciones son `RadioButton` (ARIA `menuitemradio`), no `MenuItem`.
- **Destapar antes de buscar**: con Teams dormido el avatar no existe; buscar primero falla en 506 ms, destapar y esperar el montaje da OK en ~8 s. Y ese es justo el escenario del rescate.
- `Expand()`/`Invoke()` sobre Chromium le dan el foreground a Teams aunque esté minimizada: `DevolverFoco` en un `finally`, siempre.
- Cerrar el menú **siempre** en un `finally`: dejarlo abierto deja el resto del DOM `aria-hidden` y rompe la lectura del avatar y el envío. Si el usuario lo dejó abierto y cerró Teams a la bandeja, el estado sigue a la vista en `MenuItem "Disponible, cambiar estado"` (`Popover.cs` lo lee y lo cierra con un Escape sin mostrar nada).

Verificado con `--fijar-presencia`: Disponible → Ocupado confirmado por la nube a los 3 s → Disponible a los 6 s, foreground intacto, 0 ventanas en pantalla, Teams minimizada.

## El log nativo (`PresenciaLog.cs`)

`%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\Logs\MSTeams_*.log`, el más nuevo por fecha de modificación (rota cada ~2 MB; al arrancar se tragan también los dos anteriores).

| Línea | Qué dice | Token |
|---|---|---|
| `UserDataCrossCloudModule: Received Action: UserPresenceAction: {…, availability: Away}` | lo que la nube dice de vos (lo que ven los demás) | `Available` / `Away` / `Busy` / `InACall` / … |
| `TaskbarBadgeServiceLegacy:Work: SetBadge Setting badge: GlyphBadge{"away"}, …, estado Ausente` | lo que la app pinta en la barra de tareas, con el nombre traducido | `"available"` / `"away"` … |

Preferir el glifo (idioma-independiente); si la línea trae `NumericBadge{0}` (contador de mensajes), caer al `estado …` localizado. Las `SetBadge PreSetBadge verification:` son duplicados.

Trampas medidas:

- **El huso miente**: los dígitos son UTC y el `-03:00` es decorativo. Se parsea el instante y se trata como UTC. El nombre del archivo sí es hora local.
- Abrir con `FileShare.ReadWrite | FileShare.Delete`: Teams lo tiene abierto.
- El offset se avanza hasta el **último `\n` completo** del bloque leído, nunca hasta `fs.Length`: una línea a medias se relee entera en la próxima pasada. La versión que devolvía `fs.Length` después de cerrar un `StreamReader` (que cierra el `FileStream`) tiraba `ObjectDisposedException`, el `catch` devolvía el offset viejo y el archivo se releía entero cada 20 s, duplicando eventos.
- Sólo se guardan transiciones; un evento anterior al último guardado es una relectura y se descarta.

`--probar-presencia-log` (18 comprobaciones) fabrica logs falsos con esas mismas plantillas, incluida la línea a medias y la rotación.

## Los volcados

`TeamsTools.exe --dump` (o `diag\uia-dump.ps1`) escribe el árbol de cada ventana de Teams en `datos\volcados\`. Es lo que se usó para descubrir todo lo de arriba y lo que hay que mirar si una versión nueva de Teams cambia un `AutomationId`.
