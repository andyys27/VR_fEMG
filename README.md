# sEMG-VR-cue-environment

Entorno de **realidad virtual (VR)** desarrollado en **Unity 6.3 LTS** para el visor **Meta Quest 3**, que guía al sujeto durante la adquisición de señales **sEMG faciales** indicándole *cuándo* y *qué* expresión realizar. Es el módulo de captura de datos del proyecto [`sEMG-for-facial-recognition-tests`](https://github.com/andyys27/sEMG-for-facial-recognition-tests), que se encarga del procesamiento, segmentación y clasificación de las señales.

---

## Objetivo del proyecto

Estandarizar el protocolo de adquisición para que cada sujeto realice las mismas expresiones, con los mismos tiempos, dentro de un entorno inmersivo controlado. Esto permite:

- Obtener sesiones **repetibles y comparables entre sujetos** (clave para la validación LOSO del repo de sEMG).
- Contar con **marcas de tiempo (timestamps) de cada indicación** para etiquetar y verificar la segmentación automática de eventos.
- Reducir la variabilidad causada por instrucciones ambiguas o tiempos inconsistentes entre sesiones.

---

## Acciones (expresiones) que guía el entorno

Las mismas clases que clasifica el pipeline de sEMG. Cada indicación combina un **avatar-espejo** frente al sujeto que muestra el gesto objetivo, un **panel de color** y un **texto** con la instrucción:

| Clase | Indicación en VR | Color del panel |
|---|---|---|
| **Reposo** | Avatar neutro y un círculo que se expande y contrae suavemente. Texto: *"Relaja el rostro"* | Gris |
| **Sonrisa** | Avatar sonriendo con amplitud y un sol brillante detrás. Texto: *"Sonríe"* | Amarillo |
| **Sorprendido** | Avatar con cejas arriba, ojos muy abiertos y boca en "O", con destellos alrededor. Texto: *"Sorpréndete"* | Naranja |
| **Disgusto** | Avatar arrugando la nariz y elevando el labio superior, con una nube verdosa. Texto: *"Muestra disgusto"* | Verde |
| **Triste** | Avatar con comisuras hacia abajo y cejas inclinadas, con gotas de lluvia cayendo. Texto: *"Muestra tristeza"* | Azul |

Durante el **countdown** se muestran los números 3-2-1 en grande junto con el ícono de la emoción que viene, para que el sujeto se prepare.

---

## Protocolo de una sesión

Cada ensayo sigue la misma secuencia, que coincide con las etiquetas que genera `run_segmentation.py` en el repo de sEMG (Reposo / Countdown / Emoción):

```
Reposo (5 s)  →  Countdown (3 s)  →  Expresión (5 s)
```

El orden de las emociones es **fijo**:

```
Sonrisa → Sorprendido → Disgusto → Triste
```

Esa secuencia se repite **5 veces**, lo que da **20 ensayos por sesión** (5 repeticiones por emoción).

| Parámetro | Valor |
|---|---|
| Duración de reposo | 5 s |
| Duración de countdown | 3 s |
| Duración de la expresión | 5 s |
| Duración por ensayo | 13 s |
| Repeticiones por emoción | 5 |
| Ensayos totales | 20 |
| **Duración total aproximada** | **~4 min 20 s** |
| Orden | Sonrisa, Sorprendido, Disgusto, Triste (fijo) |

---

## Requisitos

**Hardware**
- Visor **Meta Quest 3**
- Sistema de adquisición sEMG **FREEEMG** de 4 canales faciales
- PC con Windows y GPU dedicada compatible con Quest Link / Air Link `[especificaciones de tu PC]`

**Software**
- **Unity 6.3 LTS** (con módulo de *Windows Build Support*)
- **OpenXR** (paquete `XR Plugin Management` + `OpenXR Plugin`, con el perfil de interacción de Meta Quest activado)
- App **Meta Quest Link** (o Air Link) en Windows, para probar la escena desde el editor
- Software de adquisición **FREEEMG** (genera `FREEEMG_EMG_with_timestamp.csv`)
- Python 3.x (solo para el pipeline del repo de sEMG)

---

## Instalación

```bash
git clone https://github.com/[usuario]/sEMG-VR-cue-environment.git
cd sEMG-VR-cue-environment
```

1. Abre el proyecto con **Unity Hub** usando la versión **Unity 6.3 LTS**.
2. En `Edit > Project Settings > XR Plug-in Management`, activa **OpenXR** en la pestaña de PC (Windows).
3. En `OpenXR`, agrega el perfil de interacción **Meta Quest Touch Pro / Touch Plus** (según tus controles).
4. Instala y abre **Meta Quest Link** en Windows, conecta el Quest 3 (cable USB-C o Air Link) y activa Link en el visor.
5. Abre la escena principal: `[Assets/Scenes/Main.unity]`.

---

## Uso

### 1. Preparar al sujeto
- Coloca los electrodos según la configuración de canales/grupos musculares definida en el repo de sEMG (`channel_groups`).
- Verifica la calidad de la señal en FREEEMG **con el visor puesto** (sin ruido excesivo ni electrodos flojos).
- Explica las 4 expresiones y haz un ensayo de práctica.

### 2. Iniciar la adquisición
1. Inicia la grabación en **FREEEMG**.
2. Presiona **Play** en Unity (con el Quest 3 conectado por Link).
3. Ingresa el **ID del sujeto** (por ejemplo `Test3`) y presiona **Iniciar**.
4. El entorno guía automáticamente los 20 ensayos.

### 3. Finalizar
- Detén la grabación en FREEEMG al terminar la última indicación.
- Verifica que se generaron tanto el CSV de sEMG como el log de eventos del entorno VR.

---

## Logs y sincronización con la señal sEMG

Cada sesión guarda su registro de eventos en la carpeta **`recording_num/`**, con el timestamp de inicio y fin de cada fase (reposo, countdown, expresión):

```
recording_num/
└── events.csv
```

Formato del log de eventos:

```csv
timestamp,evento,emocion,ensayo
12.500,inicio_reposo,Reposo,1
17.500,inicio_countdown,Sonrisa,1
20.500,inicio_expresion,Sonrisa,1
25.500,fin_expresion,Sonrisa,1
```

Para poder alinear cada indicación con su segmento de señal, el log debe compartir referencia temporal con `FREEEMG_EMG_with_timestamp.csv` `[describir método: reloj del sistema, marca de sincronización inicial, etc.]`.

---

## Integración con el repo de sEMG

Una vez terminada la sesión:

1. Crea la carpeta del nuevo sujeto en el repo de sEMG: `TestN/Data/`.
2. Copia ahí la señal cruda (`FREEEMG_EMG_with_timestamp.csv`) y, opcionalmente, el log de eventos del entorno VR.
3. Ejecuta el pipeline según el README del repo de sEMG:
   ```bash
   python main.py                        # preprocesamiento
   python -m main.run_segmentation       # segmentación y etiquetado
   python -m main.build_dataset          # dataset de features
   python -m main.train_models           # LOSO modelos tabulares
   python -m main.train_cnn              # LOSO CNN
   ```
4. Agrega el sujeto a `subject_specs` en `build_dataset.py` y `train_cnn.py`.

> Cuantos más sujetos se registren con este protocolo estandarizado, más confiables serán las métricas LOSO.

---

## Estructura del repositorio

```
.
├── Assets/
│   ├── Scenes/              # Escena principal
│   ├── Scripts/             # Lógica del protocolo (secuencia, timers, logging)
│   └── UI/                  # Avatar, paneles e indicaciones de cada expresión
├── Packages/                # Dependencias de Unity (OpenXR, XR Management)
├── ProjectSettings/
├── recording_num/           # Logs de eventos por sesión
└── README.md
```

---

## Notas y limitaciones

- La calidad del etiquetado depende de que el sujeto **realmente ejecute** el gesto durante la ventana indicada; por eso el repo de sEMG segmenta la actividad muscular real en lugar de confiar solo en el tiempo de la indicación.
- El visor Quest 3 puede modificar la posición/presión de los electrodos faciales; verifica la señal **con el visor puesto** antes de iniciar.
- Los tiempos de reacción varían entre sujetos; los 5 s de expresión buscan capturar el gesto completo, pero puede haber retraso en el onset respecto al inicio de la indicación.
- Al ser un orden fijo de emociones, pueden aparecer efectos de fatiga u orden; considerar aleatorizar en versiones futuras.

---

## Referencias

- Repo de procesamiento y clasificación: [sEMG-for-facial-recognition-tests](https://github.com/andyys27/sEMG-for-facial-recognition-tests)
- Hudgins, B., Parker, P., & Scott, R. N. (1993). *A new strategy for multifunction myoelectric control*. IEEE Transactions on Biomedical Engineering.
- Phinyomark, A., Phukpattaranont, P., & Limsakul, C. (2012). *Feature reduction and selection for EMG signal classification*. Expert Systems with Applications.
