// Imanes entre ventanas: al acercar una ventana a otra (o a la orilla de la pantalla) se pega sin hueco.
using System;
using System.Collections.Generic;
using System.Drawing;

namespace RetroAmp
{
    public static class Snap
    {
        static bool Near(int a, int t) { return Math.Abs(a) <= t; }

        // Cuánto mover el rectángulo para que se pegue (dx, dy). T: distancia del imán en píxeles.
        public static Point Offset(Rectangle r, IList<Rectangle> wins, IList<Rectangle> areas, int T)
        {
            int bx = T + 1, by = T + 1, dx = 0, dy = 0;
            Action<int> cx = v => { if (Math.Abs(v) < bx) { bx = Math.Abs(v); dx = v; } };
            Action<int> cy = v => { if (Math.Abs(v) < by) { by = Math.Abs(v); dy = v; } };
            foreach (Rectangle o in wins)
            {
                bool vOver = r.Top < o.Bottom + T && r.Bottom > o.Top - T;   // se enciman en lo alto
                bool hOver = r.Left < o.Right + T && r.Right > o.Left - T;   // se enciman en lo ancho
                if (vOver) { cx(o.Right - r.Left); cx(o.Left - r.Right); }
                if (hOver) { cy(o.Bottom - r.Top); cy(o.Top - r.Bottom); }
                // una arriba de la otra: también se alinean sus orillas izquierda y derecha
                if (hOver && (Near(o.Bottom - r.Top, T) || Near(o.Top - r.Bottom, T))) { cx(o.Left - r.Left); cx(o.Right - r.Right); }
                // una junto a la otra: se alinean arriba y abajo
                if (vOver && (Near(o.Right - r.Left, T) || Near(o.Left - r.Right, T))) { cy(o.Top - r.Top); cy(o.Bottom - r.Bottom); }
            }
            foreach (Rectangle a in areas)
            {
                if (!a.IntersectsWith(Rectangle.Inflate(r, T, T))) continue;
                cx(a.Left - r.Left); cx(a.Right - r.Right); cy(a.Top - r.Top); cy(a.Bottom - r.Bottom);
            }
            return new Point(dx, dy);
        }

        // Al cambiar el tamaño desde la esquina: pega la orilla derecha y la de abajo.
        public static Size Grow(Rectangle r, IList<Rectangle> wins, IList<Rectangle> areas, int T)
        {
            int bx = T + 1, by = T + 1, dw = 0, dh = 0;
            Action<int> cx = v => { if (Math.Abs(v) < bx) { bx = Math.Abs(v); dw = v; } };
            Action<int> cy = v => { if (Math.Abs(v) < by) { by = Math.Abs(v); dh = v; } };
            foreach (Rectangle o in wins)
            {
                bool vOver = r.Top < o.Bottom + T && r.Bottom > o.Top - T, hOver = r.Left < o.Right + T && r.Right > o.Left - T;
                if (vOver) { cx(o.Left - r.Right); cx(o.Right - r.Right); }
                if (hOver) { cy(o.Top - r.Bottom); cy(o.Bottom - r.Bottom); }
            }
            foreach (Rectangle a in areas) { if (!a.IntersectsWith(Rectangle.Inflate(r, T, T))) continue; cx(a.Right - r.Right); cy(a.Bottom - r.Bottom); }
            return new Size(dw, dh);
        }

        // ¿Están pegadas? (orilla con orilla y encimadas en el otro sentido)
        public static bool Touch(Rectangle a, Rectangle b)
        {
            const int G = 2;
            bool vOver = a.Top < b.Bottom && a.Bottom > b.Top, hOver = a.Left < b.Right && a.Right > b.Left;
            return (vOver && (Near(a.Right - b.Left, G) || Near(a.Left - b.Right, G))) || (hOver && (Near(a.Bottom - b.Top, G) || Near(a.Top - b.Bottom, G)));
        }

        // Las ventanas pegadas a la principal, directa o indirectamente (se mueven con ella)
        public static List<int> Group(Rectangle main, IList<Rectangle> wins)
        {
            List<int> g = new List<int>(); List<Rectangle> front = new List<Rectangle> { main };
            bool[] used = new bool[wins.Count];
            while (front.Count > 0)
            {
                Rectangle c = front[front.Count - 1]; front.RemoveAt(front.Count - 1);
                for (int i = 0; i < wins.Count; i++) if (!used[i] && Touch(c, wins[i])) { used[i] = true; g.Add(i); front.Add(wins[i]); }
            }
            return g;
        }
    }
}
