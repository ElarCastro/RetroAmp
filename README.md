# RetroAmp 🎵

Reproductor de música **retro, inspirado en el Winamp clásico**, para Windows. Solo audio: reproduce canciones y listas de YouTube.

![Captura de RetroAmp](docs/captura.png)

## ✨ Funciones

- **Ventana propia** del tamaño exacto del reproductor, con esquinas redondeadas en Windows 11.
- **Diseño "Cromo"** plateado con pantalla azul, y **12 temas**: Cromo, Clásico, Medianoche, Matrix, Ámbar, Vaporwave, Plata, Fuego, Océano, Sakura, Carbón y Ártico.
- **👻 Modo fantasma**: el reproductor queda encima de todo, semitransparente, y el mouse lo atraviesa para que no estorbe mientras trabajas.
  - Mantén **Ctrl** o **Alt** para poder tocarlo.
  - **Ctrl+Alt+F** lo activa y **Ctrl+W** lo desactiva.
- **Mini reproductor** de una línea, acoplado junto a la barra de tareas.
- **Botones ⏮ ▶ ⏭ en la miniatura** de la barra de tareas, y el avance de la canción en el icono.
- **Teclas multimedia** del teclado.
- **Ecualizador de 10 bandas** con ajustes listos.
- **Listas de reproducción** guardables.
- **Descarga a MP3** con carátula y datos de la canción.
- **Instalador .exe** con accesos directos y opción de **iniciar con Windows**. No pide permisos de administrador.

## 🆕 Novedades de la versión 1.1

- El motor ahora es **un solo programa compilado** (`RetroAmp.exe`): ya no usa PowerShell ni compila código al abrirse. Arranca más rápido y evita las falsas alarmas de algunos antivirus.
- Ya no descarga nada al iniciar. yt-dlp se actualiza **cuando tú lo pides**: clic derecho en el icono junto al reloj → *Actualizar el motor de YouTube (yt-dlp)*.
- *Iniciar con Windows* viene **desactivado** en el instalador (lo activas si quieres; si ya lo tenías activado, se conserva).

## 📥 Descargar e instalar

### 👉 [Descargar RetroAmp (última versión, instalador de 54 MB)](https://github.com/ElarCastro/RetroAmp/releases/latest/download/RetroAmp-1.1-Instalador.zip)

También puedes ver todas las versiones y sus novedades en [Releases](https://github.com/ElarCastro/RetroAmp/releases).

1. Descarga el archivo `RetroAmp-1.1-Instalador.zip` con el enlace de arriba.
2. Descomprímelo y ejecuta `RetroAmp-Setup.exe`.
3. Si Windows muestra *"Windows protegió su PC"*, pulsa **Más información → Ejecutar de todas formas**. El instalador no tiene firma digital de pago.

**Requisitos:** Windows 10 u 11, de 64 bits.

## ⌨️ Atajos

| Tecla | Acción |
|---|---|
| Z / X / C / V / B | Anterior / Reproducir / Pausa / Detener / Siguiente |
| ← → | Adelantar o retroceder 5 s |
| ↑ ↓ | Volumen |
| S / R | Aleatorio / Repetir |
| L | Buscar canción |
| Ctrl+Alt+F | Activar el modo fantasma (desde cualquier programa) |
| Ctrl+W | Desactivar el modo fantasma |

## 🛠️ Cómo está hecho

- **Interfaz:** HTML/CSS/JS (`RetroAmp.html`).
- **Motor:** C# compilado en `RetroAmp.exe` (`Program.cs` + `Server.cs`). Sirve la interfaz solo en `127.0.0.1:8765` y usa [yt-dlp](https://github.com/yt-dlp/yt-dlp) + [Deno](https://deno.com) para obtener el audio, y [ffmpeg](https://ffmpeg.org) para los MP3 (se descarga de su página oficial la primera vez que guardas un MP3).
- **Ventana:** WinForms + WebView2 (`Host.cs`), dentro del mismo ejecutable.
- **Instalador:** NSIS (`instalador/RetroAmp.nsi`).

### Compilar

1. Copia `yt-dlp.exe` y `deno.exe` en la carpeta `bin\`.
2. Ejecuta `compilar.bat` (usa el compilador de C# que ya trae Windows). Genera `instalador\app\`.
3. Con [NSIS 3](https://nsis.sourceforge.io): `makensis instalador\RetroAmp.nsi` → `RetroAmp-Setup.exe`.

## 🙌 Créditos

- Basado en un proyecto que descargué (antes llamado *WinampYT*):
- Mejorado y empaquetado con la ayuda de **Claude (Anthropic)**: nuevo nombre, instalador, modo fantasma, diseño Cromo, temas, inicio con Windows y, en la 1.1, el motor compilado en C#.
- yt-dlp, Deno, ffmpeg y WebView2 pertenecen a sus respectivos autores.

## ⚠️ Aviso

Proyecto personal y sin fines de lucro. **No está afiliado a Winamp** (marca de sus respectivos dueños) **ni a YouTube/Google**; "inspirado en Winamp" solo describe su estilo visual. Úsalo respetando los términos de servicio de YouTube y los derechos de autor de la música.
