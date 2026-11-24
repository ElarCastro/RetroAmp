import re, html, sys
src = open(sys.argv[1], encoding='utf-8').read()
a = src.index('<div id="helpBody" class="hbody">') + len('<div id="helpBody" class="hbody">')
b = src.index('<div class="mgrip"', a)
body = src[a:b].strip()
body = body[:body.rstrip().rfind('</div>')].strip()
body = re.sub(r'<a href="#" data-h="(\w+)">([^<]*)</a>', r'<a href="#\1">\2</a>', body)
body = re.sub(r'<a href="#" class="spHelp">[^<]*</a>', '', body)
FIG = {
 'hInicio': [('general.png','Vista general del programa con el tema Clásico y las ventanas unidas: reproductor, lista, ecualizador, búsqueda, biblioteca y Visualización.', 'big')],
 'hTeclas': [('ayuda.png','La ventana de Ayuda (botón ? o tecla F1). Este manual tiene el mismo contenido.', '')],
 'hControles': [('reproductor.png','El reproductor: pantalla con el tiempo y la gráfica, nombre de la canción, volumen, balance, barra de posición y botones.', ''), ('mini.png','El mini reproductor: una barra pequeña con los controles básicos.', 'small')],
 'hLista': [('lista.png','La lista de reproducción con canciones de la PC (♪) y una película (🎬). Arriba, el cuadro «Buscar en esta lista», que busca solo entre sus canciones.', '')],
 'hYT': [('busqueda.png','La ventana BÚSQUEDA: arriba su cuadro para buscar en YouTube o pegar un enlace; abajo los resultados (clic para añadir, doble clic para escuchar).', '')],
 'hBib': [('biblioteca.png','La biblioteca: a la izquierda Reproduciendo ahora, Música local, Listas guardadas, Radios por internet, Internet TV y En vivo; arriba artistas y álbumes y abajo la tabla de canciones.', '')],
 'hSpot': [('opciones-spotify.png','Preferencias › Spotify, con los pasos para configurarlo.', '')],
 'hEq': [('ecualizador.png','El ecualizador con el tema Clásico: ON, curva, ajustes predefinidos y 10 bandas de +12 a -12 dB.', '')],
 'hVid': [('efectos.png','La ventana Visualización mostrando el efecto Espectro. Abajo: EFECTOS, VIDEO, CARÁTULA, EN VIVO y GRABADORA (apagada fuera de EN VIVO).', ''), ('flotante-aviso.png','Con el video en la ventana flotante, la pantalla lo avisa y los botones que no aplican se apagan.', '')],
 'hPeli': [('pelicula.png','Una película con subtítulos. Abajo a la derecha, el selector SUB.', '')],
 'hDj': [('dj.png','La consola DJ: arriba las ondas y los datos de cada plato; a los lados LOOPS, EFECTOS, SAMPLES y HOT CUES, tempo y los platos con su carátula; al centro el mezclador; abajo REV, BRK, SYNC, CUE y PLAY, la lista de canciones y el sampler.', 'big'), ('dj-grabar.png','La pestaña GRABAR de la consola grabando una mezcla en MP3.', 'small')],
 'hRec': [('grabadora.png','La GRABADORA en pausa: arriba lo que suena, el tiempo grabado y el archivo; los botones GRABAR, CONTINUAR, TERMINAR y la carpeta; abajo las grabaciones recientes.', '')],
 'hTv': [('tv-biblioteca.png','Internet TV en la Biblioteca: los canales de una lista agrupados por categoría.', ''), ('tv-visualizacion.png','Un canal de TV en la ventana Visualización, con el botón EN VIVO encendido.', ''), ('paginas-en-vivo.png','En vivo › Mis Páginas en Vivo: los canales de TV Azteca y, al final, uno agregado por ti. Arriba, la rama En vivo también tiene YouTube en vivo. Doble clic abre su página oficial dentro de Visualización.', '')],
 'hNuevo': [('actualizacion.png','Al abrir el programa, si hay una versión nueva en GitHub, esta ventana muestra lo que trae de nuevo. «No mostrar actualizaciones» la apaga (se vuelve a activar en Preferencias › Actualizaciones).', '')],
 'hOpc': [('opciones.png','La ventana PREFERENCIAS: a la izquierda el árbol de secciones y a la derecha las opciones de la elegida (aquí Páginas en vivo).', ''), ('pref-apariencia.png','Preferencias › Apariencia: temas, la lista de skins y la vista previa del skin elegido con sus datos. Doble clic o USAR ESTE lo aplica.', ''), ('pref-medios.png','Preferencias › Medios locales: tus carpetas de música y video para la Biblioteca, más las del programa (media\\musica y media\\video).', '')],
}
toc = []
def fig(f):
    out = ''
    for img, cap, cls in f:
        out += f'<figure class="{cls}"><img src="{img}"><figcaption>{html.escape(cap)}</figcaption></figure>'
    return out
def sub(m):
    hid, title = m.group(1), m.group(2)
    toc.append((hid, title))
    t = title.capitalize()
    t = re.sub(r'\b(mp3|yt|pc|bib|eq|vid|opc|dj|wsz|tv|iptv)\b', lambda x: x.group(0).upper(), t, flags=re.I)
    return f'</section><section class="sec"><h2 id="{hid}"><span class="num">{len(toc)}</span>{t}</h2>' + fig(FIG.get(hid, []))
body = re.sub(r'<h3 id="(\w+)">([^<]*)</h3>', sub, body)
body = body.replace('</section>', '', 1) + '</section>'
def cap(t):
    t = t.capitalize(); return re.sub(r'\b(mp3|yt|pc|bib|eq|vid|opc|dj|wsz|tv|iptv)\b', lambda x: x.group(0).upper(), t, flags=re.I)
tochtml = ''.join(f'<li><a href="#{h}"><span class="n">{i+1}</span>{html.escape(cap(t))}</a></li>' for i, (h, t) in enumerate(toc))
ver = re.search(r'Versión ([\d.]+)', src).group(1)
page = f'''<!DOCTYPE html><html lang="es"><head><meta charset="utf-8"><title>RetroAmp · Manual de usuario</title>
<style>
@page {{ size: A4; margin: 16mm 15mm 18mm; }}
* {{ box-sizing: border-box }}
body {{ font: 10.5pt/1.5 "Segoe UI", "DejaVu Sans", Arial, sans-serif; color: #1d1d2a; margin: 0 }}
.cover {{ height: 258mm; display: flex; flex-direction: column; justify-content: center; align-items: center; text-align: center;
  background: linear-gradient(#2a2a44, #15151f); color: #eee; margin: 0; padding: 12mm; page-break-after: always; border-radius: 2mm; }}
.cover .bar {{ width: 70%; height: 10px; background: repeating-linear-gradient(to bottom,#e4cf92 0 1px,#6e5c30 1px 2px,transparent 2px 3px); margin: 6mm 0 }}
.cover h1 {{ font: 700 40pt "DejaVu Sans Mono", monospace; letter-spacing: 6px; margin: 0; color: #f1ecd6 }}
.cover .lcd {{ font: 22pt "DejaVu Sans Mono", monospace; color: #3dff3d; background: #000; padding: 4mm 8mm; border: 1px solid #50507a; margin-top: 4mm }}
.cover p {{ color: #c9c9da; font-size: 12pt; max-width: 130mm }}
.cover img {{ width: 150mm; border: 1px solid #50507a; margin-top: 8mm }}
.toc {{ page-break-after: always }}
.toc h2, .sec h2 {{ font-size: 17pt; color: #2a2a44; border-bottom: 3px solid #c9b278; padding-bottom: 2mm; margin: 0 0 4mm }}
.toc ol {{ list-style: none; padding: 0; columns: 2; column-gap: 10mm }}
.toc li {{ margin: 2mm 0; break-inside: avoid }}
.toc a {{ color: #1d1d2a; text-decoration: none; font-size: 12pt }}
.n, .num {{ display: inline-block; min-width: 9mm; height: 7mm; line-height: 7mm; text-align: center; background: #2a2a44; color: #3dff3d; font-family: "DejaVu Sans Mono", monospace; margin-right: 3mm; border-radius: 1mm }}
.sec {{ page-break-before: always }}
.sec h2 .num {{ font-size: 13pt }}
p {{ margin: 2mm 0 }}
ul, ol {{ margin: 2mm 0; padding-left: 6mm }}
li {{ margin: 1.2mm 0 }}
b {{ color: #11112a }}
a {{ color: #2b4fa8 }}
kbd {{ font: 9pt "DejaVu Sans Mono", monospace; border: 1px solid #888; border-bottom-width: 2px; border-radius: 2px; padding: 0 1.2mm; background: #f2f2f6 }}
figure {{ margin: 4mm auto 5mm; text-align: center; break-inside: avoid }}
figure img {{ max-width: 100%; max-height: 120mm; border: 1px solid #2a2a44; box-shadow: 0 1mm 3mm rgba(0,0,0,.25) }}
figure.big img {{ max-height: 135mm }}
figure.small img {{ max-width: 110mm }}
figcaption {{ font-size: 9pt; color: #555; font-style: italic; margin-top: 1.5mm }}
.note {{ background: #f4f1e4; border-left: 4px solid #c9b278; padding: 3mm 4mm; margin: 4mm 0 }}
</style></head><body>
<div class="cover"><h1>RetroAmp</h1><div class="bar"></div>
<div class="lcd">MANUAL DE USUARIO</div>
<p>Reproductor de música, videos y películas para Windows con aspecto retro: YouTube, la música de tu PC, radios por internet, tus listas de Spotify y películas con subtítulos.</p>
<img src="general.png"><p style="margin-top:8mm">Versión {ver} · gratuito, para uso personal</p></div>
<div class="toc"><h2>Contenido</h2><ol>{tochtml}</ol>
<div class="note"><b>Cómo usar este manual.</b> Cada capítulo explica una parte del programa, con una imagen de la ventana correspondiente. Las imágenes usan el tema Clásico; con otros temas solo cambian los colores. Dentro del programa tienes la misma información en el botón <b>?</b> (o la tecla <kbd>F1</kbd>).</div></div>
<section>{body}
</body></html>'''
open(sys.argv[2], 'w', encoding='utf-8').write(page)
print(len(toc), 'secciones')
