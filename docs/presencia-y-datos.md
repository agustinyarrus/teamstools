# Presencia, patrones y los datos del equipo

## La permanencia online (`Presencia.cs`)

Un bucle cada 5 s con cinco capas, de la más delicada a la más invasiva:

1. **Medir, no asumir.** Cada 20 s: primero el log nativo (por eventos), después el avatar por UIA (una lectura caduca a los 90 s; un «Ausente» necesita dos lecturas seguidas), y como último recurso la sonda profunda (remonta las ventanas con `SW_SHOWMINNOACTIVE`, máximo una cada 5 min). La nube tarda ~3 s en reflejar un cambio: se espera 3000 ms antes de remedir, no 900.
2. **Sostener.** Cuando `GetLastInputInfo` supera `presenciaUmbralSegundos` (mínimo 15), un toque: `f15` (la tecla fantasma; si no reinicia en 120 ms, mouse de 0 px), `mouse0`, `mouse1` (1 px ida y vuelta) o `auto`. Toque exitoso si el idle queda en 2 s o menos. El `INPUT` tiene que ser de 40 bytes (unión con `MOUSEINPUT`): con 32, `SendInput` no hace nada y parece que «no reinicia».
3. **Prevenir.** `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)`: una pantalla dormida manda a Ausente aunque el idle esté en cero.
4. **Corregir.** Si la medición dice Ausente y la sesión no está bloqueada, cuenta una **deriva**: primero un toque; después `FijarPresencia("Disponible")` desde el menú del avatar; si tras 4 intentos Teams vuelve a Ausente igual, **se rinde 15 minutos** y lo dice (`Rendido`). Sólo se corrige el Ausente automático: Ocupado, No molestar, Vuelvo enseguida y Desconectado son decisiones tuyas.
5. **Ser honesto.** `OpenInputDesktop(DESKTOP_SWITCHDESKTOP)` detecta la sesión bloqueada: ahí ningún input inyectado sostiene la presencia, no se gastan toques y la interfaz lo dice.

**Manual vs automático.** «Aparecer como ausente» manda `availability: Away`, idéntico al automático; el avatar dice «estado Ausente» en los dos casos; `IsSelected` de los `RadioButton` es `False` en todos. Lo único que distingue es **nuestra propia F15**: la app sabe cuándo inyecta el pulso, así que un reinicio del reloj de inactividad que no es suyo (idle bajó y el último toque fue hace más de 6 s) es el usuario de verdad. Si Teams pasó a Ausente con input real en los últimos 3 minutos, lo puso él: `RespetandoManual`, 10 minutos antes de re-evaluar.

**Medido una madrugada** (4,5 horas): Teams en Ausente desde las 02:17, ~570 toques y 0 correcciones. La F15 siempre reiniciaba el reloj (idle 13 → 16,7 s), la máquina nunca se suspendió, y Teams —con 0 ms de CPU en 5 s— volvía a decir Away con el idle del sistema en 15 s. ⇒ **Teams no decide Ausente por `GetLastInputInfo`**; la tecla fantasma sirve para que no se vaya mientras la ventana está viva, y lo único que lo trae de vuelta es el menú del avatar.

Fuera del horario laboral (`horarioActivo`) no sostiene ni corrige. Si el guión de presencia pide algo distinto de Disponible, la permanencia se hace a un lado (la jerarquía es explícita; sin ella los dos lazos se peleaban y la presencia parpadeaba).

## El guión (`Guion.cs`)

Tramos (`Estado`, `Minutos` 1..720, `Variacion` 0..80 %) que se repiten en bucle. `Actor` (cada 5 s): si `RespetarHorario` y estás fuera del horario, pausa (y con `DesconectarFuera` te pone Desconectado una vez); si `RespetarManual` y la permanencia detectó que pusiste algo a mano, se hace a un lado; si no, aplica el tramo con duración `max(30 s, Minutos·60·(1 ± Variacion/100))` y pasa al siguiente al vencer. «Ausente» se traduce a «Aparecer como ausente» (el ítem real del menú). Plantillas: *más real* (Disponible 120 ± 15 % → Ausente 5 ± 40 %), *jornada* (seis tramos), *foco* (No molestar 50 → Disponible 10), *siempre online* (Disponible 480).

## El equipo (`Observador.cs`)

Cada 30 s lee la lista de chats sin mostrar nada (bajo la aduana) y compara con lo que tenía: cada cambio de presencia es un `Movimiento` (`presencia.jsonl`: `hora`, `persona`, `desde`, `hasta`). Los acumuladores (Disponible, Ausente, Ocupado, Sin conexión, cambios, primera y última vez Disponible) son **por día**: sin eso, dejar la app toda la noche llenaba «ausente» con 12 horas de gente durmiendo y el % Disponible daba 0 para todos a la mañana. Al arrancar, los cambios de hoy se retoman de `presencia.jsonl`, así la tabla no arranca vacía después de un reinicio.

## Patrones (`Patrones.cs`)

`DiarioPresencia` guarda cada cambio propio en `mi-presencia.jsonl` (`{"t":"…","e":"Ausente","k":"away"}`), porque el log nativo rota cada 2 MB y se lleva la historia; anti-duplicado O(1) por token y segundo.

| Hallazgo | Regla | Peso |
|---|---|---|
| parpadeo | 4 cambios en ≤ 10 min | mirar |
| tramo largo | ≥ 6 h sin moverse | dato |
| madrugada | actividad entre las 0 y las 6, últimos 3 días | dato |
| día movido | hoy más del doble de cambios que la mediana (con mediana ≥ 3) | mirar |
| mucho Ausente | más de 60 min y más del 40 % | mirar |
| hora inestable | la hora con más cambios (si ≥ 8) | curiosidad |
| el primero y el último del equipo · el más inquieto (≥ 6) · no dio señales · vive ocupado (> 60 % con ≥ 30 min) · siempre disponible (> 95 % y > 3 h) | | |
| relojito | ≥ 3 desconexiones con desvío estándar ≤ 20 min | dato |
| la propia app | rendida, respetando un estado manual, derivas sin corregir, forzados, sondas | mirar |

## Corrillos: quién está en call con quién (`Corrillos.cs`)

Tres fuentes, en orden de confianza; lo confirmado le gana a lo deducido cuando se superponen (intersección / el más corto ≥ 0,5):

| fuente | qué es |
|---|---|
| `la llamada` | los `Event/Call` del historial, agrupados por `callId` (varios eventos por llamada; gana el que trae más gente; exige ≥ 2); Teams da una duración para todos, así que acá nadie «llega tarde» |
| `el roster` | cuando estás en la reunión, el panel de Teams da los nombres: `corrillos.jsonl` guarda `hora`, `reunion`, `yo`, `gente` (nombres unidos por ` \| `) una vez por minuto, y se agrupa por reunión y hora |
| deducido | los tramos de «En una llamada / reunión / Presentando» de cada persona; dos tramos son la misma llamada si la **contención** (intersección / el más corto) ≥ 0,70 y arrancan a menos de 15 min; **union-find** con compresión de camino los une por transitividad; un corte de menos de 2 min es una reconexión; la **confianza es la mediana** de las contenciones de a pares |

Contención y no Jaccard: con Jaccard, el que se cuelga 20 minutos a una reunión de 2 horas quedaba afuera. Mediana y no mínimo: un solo tipo que llega 15 minutos tarde hundía la reunión entera a «puede ser». Limitación honesta: mientras todos siguen en llamada sus tramos terminan en «ahora» y se parecen porque nadie cortó; eso se marca como «a la vez», no como «juntos».

De ahí salen los compañeros con los que más hablás, las parejas que más coinciden, los grupos que se repiten (conjuntos idénticos de gente), quién vive en reuniones y a qué hora se junta el equipo.

## Relaciones (`Equipo.cs`)

Sobre el historial entero, una pasada (~300 ms con 60 mil mensajes), agrupando por `conv_id` (el nombre de la conversación se le pisa con el del otro y dos conversaciones distintas terminaban fusionadas).

- **Turno** = corrida de mensajes seguidos del mismo lado, cortada también cuando el hueco supera 4 horas (sin eso, dos mensajes separados por 8 meses caían en el mismo turno y el silencio se volvía invisible).
- **Espera** = desde que el otro **empezó** a escribir hasta tu respuesta. **Reacción** = desde que **terminó**. Se calculan por turnos, sólo cuando el lado cambió, con tope de 24 horas (más que eso no es una respuesta, es otra conversación), y son **medianas**.
- Sólo se miden las conversaciones de a dos: en un grupo, el que habló después no te estaba contestando. Los grupos se cuentan aparte.
- Un hueco de más de 4 horas define quién **arranca**. `Confiable` exige ≥ 6 turnos: los flojos van al final, apagados, con «pocos datos».
- La lente *historia* es 100 % privado y la lente *ritmo* es 100 % todo: mezclar poblaciones daba «274 palabras por mensaje» (las palabras de canal divididas por los mensajes privados).
- **La pelota**: a quién le debés respuesta (el último mensaje es suyo y es una pregunta o un pedido) y desde cuándo. **Enfriados**: chats con ≥ 40 mensajes y más de 3 días juntos cuyo silencio supera max(14, ritmo × 6) días.

Los tres bugs de datos que tuvo esta pestaña se encontraron comparando contra el jsonl crudo con un script aparte, no mirando la pantalla.

## Los micromodelos (`Micromodelos.cs`)

Dieciséis detectores deterministas sobre un mensaje, en microsegundos:

| Detector | Detecta | Puntaje |
|---|---|---|
| urgencia | apuro (léxico, exclamaciones, mayúsculas) | ≥ 0,75 «muy urgente» |
| pregunta | signos e interrogativos | |
| pedido | imperativos y modales («mirá», «podés») | |
| espera | «quedo atento», «avisame» | 0,85 |
| ticket | `\b[A-Z]{2,10}\d?-\d{1,6}\b` | 1 |
| produccion | PROD, producción | 0,9 |
| cuando | horas, fechas, «mañana» | |
| saludo · cierre | apertura y despedida / agradecimiento | |
| animo | positivo / negativo | `pos/(pos+neg)` |
| idioma | es / en por stopwords | |
| tecnico | código, rutas, links | |
| reunion | juntarse, call | |
| largo | palabras / 60 | telegrama / normal / largo |
| datos | números de 4+ dígitos | 0,8 |
| bot | «deployment succeeded», automáticos | 0,9 |

Prioridad 0..100 = `min(100, produccion·34 + urgencia·26 + pedido·16 + espera·12 + pregunta·8 + ticket·4)`. `Analizar` devuelve además el ticket, el vencimiento (sólo con hora explícita más «mañana», «hoy», «a las», «antes de las») y el idioma. Las reglas del autocontestador pueden **exigir o bloquear** cualquiera de estas señales.

## El radar (`Avisos.cs`)

Lectores que necesitan el contexto de la conversación (quién habló último, si contestaste, qué prometiste), sobre los últimos N días (14 por defecto), en cientos de milisegundos y sin modelo:

| Aviso | Qué mira |
|---|---|
| te deben respuesta | alguien te preguntó algo y nunca contestaste |
| te esperan | dijeron explícitamente que esperan algo tuyo |
| vencimientos | fechas que todavía no pasaron |
| producción | todo lo que tocó PROD |
| te nombraron | tu nombre, apellido o apodo (el nombre de pila recortado a 4 letras) |
| tickets | cada ticket y su última novedad |
| tema caliente | palabras que se dispararon contra su propio promedio histórico |
| conversación fría | hablabas seguido y se cortó |

Y la libreta: *me comprometí* («lo miro», «te paso», «me encargo»…), *me prometieron* («te aviso», «lo reviso»…), *se decidió* («quedamos en», «vamos con», «acordamos»…). Lo hecho, lo fijado y lo oculto quedan en `marcas.json`.

## El puente con el modelo local (`AsistenteIA.cs`, `RespuestaIA.cs`)

Cliente HTTP mínimo contra `llama-server` (API compatible con OpenAI) en `127.0.0.1:8080`: `Disponible()` no bloquea nunca (caché de 20 s, sonda de fondo), `Verificar()` es para hilos de fondo. `Redactar` manda un system prompt en castellano rioplatense (una o dos oraciones, sin saludos, sin firma, sin emojis, jamás prometer fechas ni números) con `chat_template_kwargs: { enable_thinking: false }` — sin eso, un modelo que razona pasa el `/health` perfecto y devuelve `content` vacío.

`RespuestaIA.Revisar` es el portero: la IA aporta redacción, no datos. Se descarta lo que devuelva razonamiento, lo vacío, lo de más de 400 caracteres, lo que nombre un ticket que no estaba en el mensaje entrante, lo que prometa una hora, una fecha o un día cuando el entrante no hablaba de tiempo, y lo que invente un enlace. Con tres cortes seguidos, la IA queda en pausa 10 minutos y contesta el texto fijo.

> [!NOTE]
> El proxy de Windows arruina el **primer** pedido a `127.0.0.1` si no se pone `WebRequest.DefaultWebProxy = null` una vez por proceso: `req.Proxy = null` por pedido no alcanza porque el proxy por defecto ya se inicializó.

## Los datos, archivo por archivo

| Archivo | Formato |
|---|---|
| `presencia.jsonl` | `{"hora": "yyyy-MM-dd HH:mm:ss", "persona": "Apellido, Nombre", "desde": "Ausente", "hasta": "Disponible"}` |
| `mi-presencia.jsonl` | `{"t":"yyyy-MM-ddTHH:mm:ss","e":"Disponible","k":"available"}` |
| `corrillos.jsonl` | `{"hora": "…", "reunion": "…", "yo": "Apellido, Nombre", "gente": "A, B \| C, D"}` |
| `bandeja.jsonl` | `{"hora", "chat", "tipo", "autor", "texto", "regla", "respuesta", "respondido", "resultado", "ms"}` |
| `respuestas.json` | ajustes + `reglas[]` (ver README) |
| `recordatorios.json` | `recordatorios[{id, para, correo, cuando, proximo, repetir, hora, textoPlano, textoHtml, textoRtf, partes, activo, paraMi, ultimoEnvio, ultimoResultado, enviados, fallos, historial[{cuando, ok, detalle, ms}]}]` — `repetir` ∈ `"" \| diario \| laborables \| semanal:1,3,5 \| cada:N \| mensual:N \| ultimo:d` |
| `personalizados.json` | `mensajes[{id, nombre, para, etiqueta, textoPlano, textoHtml, textoRtf, partes, usos, ultimoUso}]` |
| `contactos.json` | `contactos[{apodo, nombre, correo, rol}]` |
| `guion.json` | `Activo, RespetarHorario, RespetarManual, DesconectarFuera, Pasos[{Estado, Minutos, Variacion, Activo}]` |
| `mensajes.jsonl` (el extractor) | `conv_id, conv, conv_tipo, fecha (ISO UTC), autor, mio, tipo, texto, borrado, id` y, en los `Event/Call`, `llamada: {estado, gente: [{id, nombre, dur}]}` + `call_id` |

Un envío de varias burbujas se guarda como `"partes": [{tipo: "texto"\|"imagen"\|"archivo", html, plano, rtf \| ruta, nota, esperaMs}]`, y los campos viejos (`textoHtml`…) se mantienen con la primera parte para que un exe anterior siga leyendo el archivo.

## `--foto`: la app se retrata sola

Verificar una pantalla a los manotazos con el mouse es frágil: las terminales roban el foco y el clic cae en la ventana de atrás. La salida fue que la app se saque la foto a sí misma:

1. la segunda instancia escribe `datos\foto-pedido.txt` con `pestaña|salida|ancho|alto|modo|esperaMs` y hace `PostMessage(HWND_BROADCAST, MsgFoto)` (un mensaje de ventana registrado, sin servidores ni puertos);
2. la instancia que corre lo atiende en su `WndProc`: si está en la bandeja se muestra en **(20000, 20000)**, fuera de cualquier escritorio, con `SW_SHOWNOACTIVATE`;
3. cambia de pestaña, le pasa `--modo` a la vista (`Pantalla.Modo(string)`, cada una lo interpreta como quiere), bombea mensajes `--espera` ms para que lleguen las cargas de fondo, y `DrawToBitmap` → PNG;
4. `SW_HIDE` y restaura tamaño, posición y pestaña.

Funciona porque `SetVisibleCore` hace `CreateHandle()` aunque la ventana esté oculta: el handle existe siempre. Ese mecanismo destapó un bug del parser de argumentos: `--foto equipo --modo historia` disparaba el modo `--historia` porque `Es()` no exigía el guion y el **valor** de una opción se tomaba como bandera.
