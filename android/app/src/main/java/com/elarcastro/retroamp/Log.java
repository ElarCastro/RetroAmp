package com.elarcastro.retroamp;

import java.io.File;
import java.io.FileWriter;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

/** Registro de errores (registro.txt dentro de la app) para poder corregirlos. */
public final class Log {
    static File file;
    public static synchronized void w(String m) {
        android.util.Log.w("RetroAmp", m);
        if (file == null) return;
        try {
            if (file.length() > 512 * 1024) file.delete();
            try (FileWriter fw = new FileWriter(file, true)) {
                fw.write(new SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.ROOT).format(new Date()) + "  " + m + "\n");
            }
        } catch (Exception ignored) { }
    }
}
