# RetroAmp - motor del reproductor (solo audio, usa yt-dlp + Deno)
# Sirve el reproductor en http://127.0.0.1:8765/ y lo abre en su propia ventana (WebView2).
# Si la ventana propia no puede abrirse, usa Chrome o Edge como respaldo.
param([switch]$Inicio)
$ErrorActionPreference = 'Stop'
$env:RETROAMP_INICIO = $(if ($Inicio) { '1' } else { '0' })
$port    = 8765
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin     = Join-Path $root 'bin'
$cache   = Join-Path $root 'cache'
$plsDir  = Join-Path $root 'playlists'
$profDir = Join-Path $root 'perfil'
$logFile = Join-Path $root 'registro.txt'
$ytdlp   = Join-Path $bin 'yt-dlp.exe'
$deno    = Join-Path $bin 'deno.exe'
$wvDir   = Join-Path $bin 'webview2'
$url     = "http://127.0.0.1:$port/"
try { $Host.UI.RawUI.WindowTitle = 'RetroAmp - motor (no cierres esta ventana)' } catch {}

function Log($m) { try { Add-Content -LiteralPath $logFile -Value ("[" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + "] " + $m) } catch {} }
function Fail($m) {
    Log "ERROR: $m"
    Set-Console 1
    Write-Host ''
    Write-Host "ERROR: $m" -ForegroundColor Red
    Read-Host 'Pulsa Enter para cerrar'
    exit 1
}

function Open-Browser {
    $candidates = @(
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
    )
    $exe = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    try {
        if ($exe) { Start-Process -FilePath $exe -ArgumentList "--app=$url", '--window-size=500,760', '--autoplay-policy=no-user-gesture-required' }
        else { Start-Process $url }
    } catch { Write-Host "Abre en tu navegador: $url" -ForegroundColor Yellow }
}

Add-Type -Name Win -Namespace RetroAmpCon -MemberDefinition '[DllImport("kernel32.dll")] public static extern System.IntPtr GetConsoleWindow(); [DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int n);' -ErrorAction SilentlyContinue
function Set-Console([int]$mode) { try { [RetroAmpCon.Win]::ShowWindow([RetroAmpCon.Win]::GetConsoleWindow(), $mode) | Out-Null } catch {} }

# Una sola copia: si otra se esta abriendo, esperar a que este lista y mostrarla
$script:appMutex = New-Object System.Threading.Mutex($false, 'Local\RetroAmp_App')
$owns = $false
try { $owns = $script:appMutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $owns = $true }
if (-not $owns) {
    for ($i = 0; $i -lt 120; $i++) {
        try { Invoke-RestMethod -UseBasicParsing -Uri ($url + 'api/show') -TimeoutSec 3 | Out-Null; break } catch { Start-Sleep -Milliseconds 500 }
    }
    exit
}

# Si ya esta abierto, solo mostrar la ventana existente
try {
    $probe = New-Object System.Net.Sockets.TcpClient
    $probe.Connect('127.0.0.1', $port); $probe.Close()
    $isHost = $false
    try { $r = Invoke-RestMethod -UseBasicParsing -Uri ($url + 'api/show') -TimeoutSec 5; $isHost = [bool]$r.host } catch {}
    if (-not $isHost) { Open-Browser }
    exit
} catch {}

Write-Host '=== RetroAmp ==='
New-Item -ItemType Directory -Force -Path $bin, $cache, $plsDir, $profDir | Out-Null
# Quitar la marca "descargado de internet" (si no, Windows bloquea las DLL de la ventana)
try { Get-ChildItem -LiteralPath $root -Recurse -File | Unblock-File -ErrorAction SilentlyContinue } catch {}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'

if (-not (Test-Path $ytdlp)) {
    Write-Host 'Descargando yt-dlp (solo la primera vez)...'
    try {
        Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile $ytdlp
    } catch { Remove-Item $ytdlp -ErrorAction SilentlyContinue; Fail "No pude descargar yt-dlp. Revisa tu conexion a internet. ($($_.Exception.Message))" }
} else {
    # Actualizar yt-dlp en segundo plano (no retrasa la apertura)
    try { Start-Process -FilePath $ytdlp -ArgumentList '-U' -WindowStyle Hidden } catch {}
}

if (-not (Test-Path $deno)) {
    Write-Host 'Descargando Deno, necesario para YouTube (unos 40 MB, solo la primera vez)...'
    $zip = Join-Path $bin 'deno.zip'
    try {
        Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip' -OutFile $zip
        Expand-Archive -LiteralPath $zip -DestinationPath $bin -Force
        Remove-Item $zip -ErrorAction SilentlyContinue
    } catch { Remove-Item $zip -ErrorAction SilentlyContinue; Fail "No pude descargar Deno. ($($_.Exception.Message))" }
}

Get-ChildItem -LiteralPath $cache -File -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match '\.(part|ytdl|temp)$' -or $_.Name -match '\.part-' -or $_.LastWriteTime -lt (Get-Date).AddDays(-30)
} | Remove-Item -Force -ErrorAction SilentlyContinue

try {
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $port)
    $listener.Start()
} catch { Fail "No pude abrir el puerto $port. ($($_.Exception.Message))" }

$musicBase = ''
try { $musicBase = [Environment]::GetFolderPath('MyMusic') } catch {}
if (-not $musicBase -and $env:USERPROFILE) { $musicBase = Join-Path $env:USERPROFILE 'Music' }
if (-not $musicBase) { $musicBase = $root }
$musicDir = Join-Path $musicBase 'RetroAmp'

$ctx = [hashtable]::Synchronized(@{
    Html     = (Join-Path $root 'RetroAmp.html')
    Cache    = $cache
    Pls      = $plsDir
    State    = (Join-Path $root 'estado.json')
    Ytdlp    = $ytdlp
    Deno     = $deno
    Stop     = $false
    Show     = $false
    HostMode = $false
    Bin      = $bin
    MusicDir = $musicDir
    Mp3Q     = [System.Collections.ArrayList]::Synchronized((New-Object System.Collections.ArrayList))
    Mp3Cur   = ''
    Mp3Done  = 0
    Mp3Fail  = 0
    Mp3Err   = ''
})

# ---------------- Atencion de cada peticion (corre en paralelo) ----------------
$handler = {
    param($client, $ctx)

    function Send-Bytes($stream, [int]$code, [string]$type, [byte[]]$body, [string]$extra = '') {
        $status = @{200='OK';206='Partial Content';400='Bad Request';404='Not Found';416='Range Not Satisfiable';500='Internal Server Error'}[$code]
        $hdr = "HTTP/1.1 $code $status`r`nContent-Type: $type`r`nContent-Length: $($body.Length)`r`nCache-Control: no-store`r`n$extra" + "Connection: close`r`n`r`n"
        $h = [System.Text.Encoding]::ASCII.GetBytes($hdr)
        $stream.Write($h, 0, $h.Length)
        if ($body.Length) { $stream.Write($body, 0, $body.Length) }
        $stream.Flush()
    }
    function Send-Json($stream, $obj, [int]$code = 200) {
        $json = ConvertTo-Json -InputObject $obj -Depth 5 -Compress
        Send-Bytes $stream $code 'application/json; charset=utf-8' ([System.Text.Encoding]::UTF8.GetBytes($json))
    }
    function Send-Items($stream, $items) {
        $parts = foreach ($it in $items) { ConvertTo-Json -InputObject $it -Compress }
        $json = '{"ok":true,"items":[' + (@($parts) -join ',') + ']}'
        Send-Bytes $stream 200 'application/json; charset=utf-8' ([System.Text.Encoding]::UTF8.GetBytes($json))
    }
    function Quote-Arg([string]$a) {
        if ($a -and $a -notmatch '[\s"]') { return $a }
        return '"' + (($a -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
    }
    function Run-Ytdlp([string[]]$argList, [int]$timeoutMs) {
        $all = @('--no-warnings', '--encoding', 'utf-8', '--js-runtimes', "deno:$($ctx.Deno)") + $argList
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $ctx.Ytdlp
        $psi.Arguments = ($all | ForEach-Object { Quote-Arg $_ }) -join ' '
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
        $psi.EnvironmentVariables['PYTHONIOENCODING'] = 'utf-8'
        $psi.EnvironmentVariables['PYTHONUTF8'] = '1'
        $p = [System.Diagnostics.Process]::Start($psi)
        $o = $p.StandardOutput.ReadToEndAsync()
        $e = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit($timeoutMs)) {
            try { $p.Kill() } catch {}
            return @{ code = -1; out = ''; err = 'Se agoto el tiempo de espera' }
        }
        $p.WaitForExit()
        return @{ code = $p.ExitCode; out = $o.Result; err = $e.Result }
    }
    function Short-Err([string]$err) {
        $lines = @($err -split "`r?`n" | Where-Object { $_ -match 'ERROR' })
        if ($lines.Count) { $t = $lines[-1] } else { $t = ($err.Trim() -split "`r?`n")[-1] }
        if (-not $t) { $t = 'error desconocido' }
        return ($t -replace '^ERROR:\s*', '').Trim()
    }
    function Parse-Entries([string]$text) {
        $list = New-Object System.Collections.ArrayList
        foreach ($ln in ($text -split "`r?`n")) {
            $p = $ln -split '\|~\|'
            if ($p.Count -lt 4 -or $p[0] -notmatch '^[\w-]{11}$') { continue }
            $d = 0.0
            [void][double]::TryParse($p[1], [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)
            $ch = $p[2]; if ($ch -eq 'NA') { $ch = '' }
            $title = ($p[3..($p.Count - 1)] -join '|~|')
            [void]$list.Add([pscustomobject]@{ id = $p[0]; dur = [int]$d; ch = $ch; title = $title })
        }
        return ,$list
    }
    function Parse-Lists([string]$text) {
        $list = New-Object System.Collections.ArrayList
        foreach ($ln in ($text -split "`r?`n")) {
            $p = $ln -split '\|~\|'
            if ($p.Count -lt 4 -or $p[0] -notmatch '^[\w-]{12,}$') { continue }
            $ch = $p[2]; if ($ch -eq 'NA') { $ch = '' }
            [void]$list.Add([pscustomobject]@{ id = $p[0]; ch = $ch; title = ($p[3..($p.Count - 1)] -join '|~|') })
        }
        return ,$list
    }
    function Find-Audio([string]$id) {
        Get-ChildItem -LiteralPath $ctx.Cache -File -ErrorAction SilentlyContinue |
            Where-Object { $_.BaseName -eq $id -and $_.Extension -in '.m4a', '.webm', '.mp4', '.opus', '.ogg', '.mp3' } |
            Select-Object -First 1
    }
    function Get-Query([string]$qs) {
        $q = @{}
        foreach ($kv in ($qs -split '&')) {
            if (-not $kv) { continue }
            $i = $kv.IndexOf('=')
            if ($i -lt 0) { $k = $kv; $v = '' } else { $k = $kv.Substring(0, $i); $v = $kv.Substring($i + 1) }
            $q[[Uri]::UnescapeDataString($k.Replace('+', ' '))] = [Uri]::UnescapeDataString($v.Replace('+', ' '))
        }
        return $q
    }
    function Safe-Name([string]$n) {
        $n = ($n -replace '[\\/:*?"<>|\x00-\x1f]', '').Trim().TrimEnd('.')
        if ($n.Length -gt 60) { $n = $n.Substring(0, 60).Trim() }
        return $n
    }
    function Send-Raw($stream, [string]$file) {
        if (Test-Path -LiteralPath $file) { Send-Bytes $stream 200 'application/json; charset=utf-8' ([System.IO.File]::ReadAllBytes($file)) }
        else { Send-Bytes $stream 200 'application/json; charset=utf-8' ([System.Text.Encoding]::ASCII.GetBytes('{}')) }
    }
    function Save-Text([string]$file, [string]$text) {
        $tmp = $file + '.tmp'
        [System.IO.File]::WriteAllText($tmp, $text, [System.Text.Encoding]::ASCII)
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
        Move-Item -LiteralPath $tmp -Destination $file
    }
    $tpl = '%(id)s|~|%(duration|0)s|~|%(channel,uploader|)s|~|%(title)s'

    try {
        $client.ReceiveTimeout = 10000
        $client.SendTimeout = 30000
        $stream = $client.GetStream()
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::ASCII, $false, 8192, $true)
        $line = $reader.ReadLine()
        $headers = @{}
        while ($true) {
            $h = $reader.ReadLine()
            if ($h -eq $null -or $h -eq '') { break }
            $i = $h.IndexOf(':'); if ($i -gt 0) { $headers[$h.Substring(0, $i).Trim().ToLower()] = $h.Substring($i + 1).Trim() }
        }
        if (-not $line -or $line -notmatch '^([A-Z]+)\s+(\S+)') { return }
        $method = $Matches[1]
        $target = $Matches[2]
        $body = ''
        if ($method -eq 'POST' -and $headers['content-length']) {
            $n = [int]$headers['content-length']
            if ($n -gt 0 -and $n -le 8MB) {
                $buf = New-Object char[] $n; $got = 0
                while ($got -lt $n) { $k = $reader.Read($buf, $got, $n - $got); if ($k -le 0) { break }; $got += $k }
                $body = New-Object string($buf, 0, $got)
            }
        }
        $qi = $target.IndexOf('?')
        if ($qi -ge 0) { $path = $target.Substring(0, $qi); $q = Get-Query $target.Substring($qi + 1) } else { $path = $target; $q = @{} }

        if ($path -eq '/' -or $path -eq '/index.html' -or $path -eq '/RetroAmp.html') {
            Send-Bytes $stream 200 'text/html; charset=utf-8' ([System.IO.File]::ReadAllBytes($ctx.Html))
        }
        elseif ($path -eq '/api/ping') {
            Send-Json $stream @{ ok = $true }
        }
        elseif ($path -eq '/api/search') {
            $term = ("" + $q['q']) -replace '["\r\n]', ' '
            if (-not $term.Trim()) { Send-Json $stream @{ ok = $false; error = 'Busqueda vacia' }; return }
            $r = Run-Ytdlp @('--flat-playlist', '--print', $tpl, "ytsearch20:$($term.Trim())") 90000
            $items = Parse-Entries $r.out
            if ($items.Count) { Send-Items $stream $items }
            else { Send-Json $stream @{ ok = $false; error = (Short-Err $r.err) } }
        }
        elseif ($path -eq '/api/searchlists') {
            $term = ("" + $q['q']) -replace '["\r\n]', ' '
            if (-not $term.Trim()) { Send-Json $stream @{ ok = $false; error = 'Busqueda vacia' }; return }
            $su = 'https://www.youtube.com/results?search_query=' + [Uri]::EscapeDataString($term.Trim()) + '&sp=EgIQAw%253D%253D'
            $r = Run-Ytdlp @('--flat-playlist', '--playlist-end', '12', '--print', $tpl, $su) 90000
            $items = Parse-Lists $r.out
            if ($items.Count) { Send-Items $stream $items }
            else { Send-Json $stream @{ ok = $false; error = (Short-Err $r.err) } }
        }
        elseif ($path -eq '/api/list') {
            $u = "" + $q['url']
            if ($u -notmatch '^https://[^\s"]+$') { Send-Json $stream @{ ok = $false; error = 'Enlace no valido' }; return }
            $r = Run-Ytdlp @('--flat-playlist', '--print', $tpl, $u) 180000
            $items = Parse-Entries $r.out
            if ($items.Count) { Send-Items $stream $items }
            else { Send-Json $stream @{ ok = $false; error = (Short-Err $r.err) } }
        }
        elseif ($path -eq '/api/prep') {
            $id = "" + $q['id']
            if ($id -notmatch '^[\w-]{11}$') { Send-Json $stream @{ ok = $false; error = 'id no valido' }; return }
            $mutex = New-Object System.Threading.Mutex($false, "Local\RetroAmp_$id")
            $got = $false
            try {
                try { $got = $mutex.WaitOne(300000) } catch [System.Threading.AbandonedMutexException] { $got = $true }
                $f = Find-Audio $id
                $errMsg = ''
                if (-not $f) {
                    $out = Join-Path $ctx.Cache '%(id)s.%(ext)s'
                    $r = Run-Ytdlp @('-f', 'bestaudio[ext=m4a]/bestaudio/best', '--no-playlist', '--no-progress', '--no-mtime', '-q', '-o', $out, "https://www.youtube.com/watch?v=$id") 300000
                    $f = Find-Audio $id
                    if (-not $f) { $errMsg = Short-Err $r.err }
                }
                if ($f) { Send-Json $stream @{ ok = $true; url = "/audio/$($f.Name)"; size = $f.Length } }
                else { Send-Json $stream @{ ok = $false; error = $errMsg } }
            } finally {
                if ($got) { try { $mutex.ReleaseMutex() } catch {} }
                $mutex.Dispose()
            }
        }
        elseif ($path -like '/audio/*') {
            $name = $path.Substring(7)
            if ($name -notmatch '^[\w-]{11}\.(m4a|webm|mp4|opus|ogg|mp3)$') { Send-Bytes $stream 404 'text/plain' ([byte[]]@()); return }
            $fp = Join-Path $ctx.Cache $name
            if (-not (Test-Path -LiteralPath $fp)) { Send-Bytes $stream 404 'text/plain' ([byte[]]@()); return }
            $type = @{ m4a = 'audio/mp4'; mp4 = 'audio/mp4'; webm = 'audio/webm'; opus = 'audio/ogg'; ogg = 'audio/ogg'; mp3 = 'audio/mpeg' }[$Matches[1]]
            $fs = [System.IO.File]::Open($fp, 'Open', 'Read', 'ReadWrite')
            try {
                $len = $fs.Length; $start = [long]0; $end = $len - 1; $code = 200
                if ($headers['range'] -match 'bytes=(\d*)-(\d*)') {
                    $code = 206
                    if ($Matches[1] -ne '') {
                        $start = [long]$Matches[1]
                        if ($Matches[2] -ne '') { $end = [Math]::Min([long]$Matches[2], $len - 1) }
                    } elseif ($Matches[2] -ne '') { $start = [Math]::Max([long]0, $len - [long]$Matches[2]) }
                }
                if ($start -ge $len -or $start -gt $end) {
                    Send-Bytes $stream 416 'text/plain' ([byte[]]@()) "Content-Range: bytes */$len`r`n"; return
                }
                $count = $end - $start + 1
                $status = if ($code -eq 206) { 'Partial Content' } else { 'OK' }
                $hdr = "HTTP/1.1 $code $status`r`nContent-Type: $type`r`nContent-Length: $count`r`nAccept-Ranges: bytes`r`n"
                if ($code -eq 206) { $hdr += "Content-Range: bytes $start-$end/$len`r`n" }
                $hdr += "Cache-Control: no-store`r`nConnection: close`r`n`r`n"
                $hb = [System.Text.Encoding]::ASCII.GetBytes($hdr)
                $stream.Write($hb, 0, $hb.Length)
                [void]$fs.Seek($start, 'Begin')
                $buf = New-Object byte[] 65536
                $left = $count
                while ($left -gt 0) {
                    $n = $fs.Read($buf, 0, [int][Math]::Min($buf.Length, $left))
                    if ($n -le 0) { break }
                    $stream.Write($buf, 0, $n)
                    $left -= $n
                }
                $stream.Flush()
            } finally { $fs.Close() }
        }
        elseif ($path -eq '/api/show') {
            $ctx.Show = $true
            Send-Json $stream @{ ok = $true; host = [bool]$ctx.HostMode }
        }
        elseif ($path -eq '/api/state') {
            if ($method -eq 'POST') {
                if ($body.StartsWith('{')) { Save-Text $ctx.State $body; Send-Json $stream @{ ok = $true } }
                else { Send-Json $stream @{ ok = $false; error = 'datos no validos' } }
            } else { Send-Raw $stream $ctx.State }
        }
        elseif ($path -eq '/api/pls') {
            $names = @(Get-ChildItem -LiteralPath $ctx.Pls -Filter '*.json' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | ForEach-Object { $_.BaseName })
            $parts = foreach ($nm in $names) { ConvertTo-Json -InputObject ([string]$nm) -Compress }
            $json = '{"ok":true,"names":[' + (@($parts) -join ',') + ']}'
            Send-Bytes $stream 200 'application/json; charset=utf-8' ([System.Text.Encoding]::UTF8.GetBytes($json))
        }
        elseif ($path -like '/api/pls/*') {
            $nm = Safe-Name ("" + $q['name'])
            if (-not $nm) { Send-Json $stream @{ ok = $false; error = 'Nombre no valido' }; return }
            $file = Join-Path $ctx.Pls ($nm + '.json')
            if ($path -eq '/api/pls/get') {
                if (Test-Path -LiteralPath $file) { Send-Raw $stream $file } else { Send-Json $stream @{ ok = $false; error = 'No existe esa lista' } }
            } elseif ($path -eq '/api/pls/save' -and $method -eq 'POST') {
                if ($body.StartsWith('{')) { Save-Text $file $body; Send-Json $stream @{ ok = $true; name = $nm } }
                else { Send-Json $stream @{ ok = $false; error = 'datos no validos' } }
            } elseif ($path -eq '/api/pls/del') {
                Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
                Send-Json $stream @{ ok = $true }
            } else { Send-Bytes $stream 404 'text/plain' ([byte[]]@()) }
        }
        elseif ($path -eq '/api/mp3') {
            $id = "" + $q['id']
            if ($id -notmatch '^[\w-]{11}$') { Send-Json $stream @{ ok = $false; error = 'id no valido' }; return }
            $dir = $ctx.MusicDir
            $sub = Safe-Name ("" + $q['sub'])
            if ($sub) { $dir = Join-Path $dir $sub }
            $dup = $false
            foreach ($j in @($ctx.Mp3Q.ToArray())) { if ($j.id -eq $id -and $j.dir -eq $dir) { $dup = $true } }
            if (-not $dup) { [void]$ctx.Mp3Q.Add(@{ id = $id; title = ("" + $q['title']); dir = $dir }) }
            Send-Json $stream @{ ok = $true; pending = $ctx.Mp3Q.Count }
        }
        elseif ($path -eq '/api/mp3/status') {
            Send-Json $stream @{ ok = $true; pending = $ctx.Mp3Q.Count; current = $ctx.Mp3Cur; done = $ctx.Mp3Done; failed = $ctx.Mp3Fail; lastError = $ctx.Mp3Err; dir = $ctx.MusicDir }
        }
        elseif ($path -eq '/api/mp3/open') {
            New-Item -ItemType Directory -Force -Path $ctx.MusicDir | Out-Null
            $dir = $ctx.MusicDir
            $sub = Safe-Name ("" + $q['sub'])
            if ($sub -and (Test-Path -LiteralPath (Join-Path $dir $sub))) { $dir = Join-Path $dir $sub }
            try { Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + $dir + '"') } catch {}
            Send-Json $stream @{ ok = $true; dir = $dir }
        }
        elseif ($path -eq '/apagar') {
            Send-Bytes $stream 200 'text/plain; charset=utf-8' ([System.Text.Encoding]::UTF8.GetBytes('RetroAmp detenido'))
            $ctx.Stop = $true
        }
        else {
            Send-Bytes $stream 404 'text/plain' ([byte[]]@())
        }
    } catch {
        # conexion cortada por el navegador u otro error: se ignora
    } finally {
        try { $client.Close() } catch {}
    }
}


# ---------------- Servidor en segundo plano ----------------
$serverLoop = {
    param($listener, $ctx, $handlerText)
    $pool = [RunspaceFactory]::CreateRunspacePool(1, 32)
    $pool.Open()
    $jobs = New-Object System.Collections.ArrayList
    while (-not $ctx.Stop) {
        if ($listener.Pending()) {
            $client = $listener.AcceptTcpClient()
            $ps = [PowerShell]::Create()
            $ps.RunspacePool = $pool
            [void]$ps.AddScript($handlerText).AddArgument($client).AddArgument($ctx)
            [void]$jobs.Add(@{ ps = $ps; h = $ps.BeginInvoke() })
        } else {
            Start-Sleep -Milliseconds 15
        }
        for ($i = $jobs.Count - 1; $i -ge 0; $i--) {
            if ($jobs[$i].h.IsCompleted) {
                try { [void]$jobs[$i].ps.EndInvoke($jobs[$i].h) } catch {}
                $jobs[$i].ps.Dispose()
                $jobs.RemoveAt($i)
            }
        }
    }
    Start-Sleep -Milliseconds 300
    $listener.Stop()
    $pool.Close()
}
# ---------------- Conversion a MP3 en segundo plano ----------------
$mp3Worker = {
    param($ctx)
    function Q([string]$a) { if ($a -and $a -notmatch '[\s"]') { return $a }; return '"' + (($a -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"' }
    $ffmpeg = Join-Path $ctx.Bin 'ffmpeg.exe'
    while (-not $ctx.Stop) {
        $job = $null
        try { if ($ctx.Mp3Q.Count -gt 0) { $job = $ctx.Mp3Q[0]; $ctx.Mp3Q.RemoveAt(0) } } catch {}
        if (-not $job) { Start-Sleep -Milliseconds 300; continue }
        try {
            if (-not (Test-Path $ffmpeg)) {
                $ctx.Mp3Cur = 'Descargando el conversor MP3 (solo la primera vez, unos 150 MB)...'
                $ProgressPreference = 'SilentlyContinue'
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                $zip = Join-Path $ctx.Bin 'ffmpeg.zip'
                $tmp = Join-Path $ctx.Bin 'ffmpeg-tmp'
                Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip' -OutFile $zip
                $ctx.Mp3Cur = 'Preparando el conversor MP3...'
                Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
                foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe') {
                    $f = Get-ChildItem -LiteralPath $tmp -Recurse -Filter $exe | Select-Object -First 1
                    if ($f) { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $ctx.Bin $exe) -Force }
                }
                Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
                if (-not (Test-Path $ffmpeg)) { throw 'No pude instalar el conversor MP3 (ffmpeg)' }
            }
            $ctx.Mp3Cur = $(if ($job.title) { $job.title } else { $job.id })
            New-Item -ItemType Directory -Force -Path $job.dir | Out-Null
            $argList = @('--no-warnings', '--js-runtimes', "deno:$($ctx.Deno)", '--ffmpeg-location', $ctx.Bin,
                '-f', 'bestaudio/best', '-x', '--audio-format', 'mp3', '--audio-quality', '0',
                '--embed-metadata', '--embed-thumbnail', '--convert-thumbnails', 'jpg',
                '--no-playlist', '--no-progress', '-q', '--no-mtime',
                '-o', (Join-Path $job.dir '%(title)s.%(ext)s'), "https://www.youtube.com/watch?v=$($job.id)")
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $ctx.Ytdlp
            $psi.Arguments = ($argList | ForEach-Object { Q $_ }) -join ' '
            $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
            $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
            $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8; $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
            $psi.EnvironmentVariables['PYTHONIOENCODING'] = 'utf-8'; $psi.EnvironmentVariables['PYTHONUTF8'] = '1'
            $p = [System.Diagnostics.Process]::Start($psi)
            $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
            if (-not $p.WaitForExit(900000)) { try { $p.Kill() } catch {}; throw 'Se agoto el tiempo de espera' }
            $p.WaitForExit()
            if ($p.ExitCode -ne 0) {
                $lines = @($e.Result -split "`r?`n" | Where-Object { $_ -match 'ERROR' })
                if ($lines.Count) { $t = $lines[-1] } else { $t = 'error desconocido' }
                throw (($t -replace '^ERROR:\s*', '').Trim())
            }
            $ctx.Mp3Done = $ctx.Mp3Done + 1
        } catch {
            $ctx.Mp3Fail = $ctx.Mp3Fail + 1
            $ctx.Mp3Err = "" + $(if ($job.title) { $job.title } else { $job.id }) + ': ' + $_.Exception.Message
        }
        $ctx.Mp3Cur = ''
    }
}
$mp3PS = [PowerShell]::Create()
[void]$mp3PS.AddScript($mp3Worker.ToString()).AddArgument($ctx)
[void]$mp3PS.BeginInvoke()

$srvPS = [PowerShell]::Create()
[void]$srvPS.AddScript($serverLoop.ToString()).AddArgument($listener).AddArgument($ctx).AddArgument($handler.ToString())
$srvHandle = $srvPS.BeginInvoke()

Write-Host ''
Write-Host "Listo. Reproductor en $url" -ForegroundColor Green

# ---------------- Ventana propia (WebView2) ----------------
$hostErr = $null
$core = Join-Path $wvDir 'Microsoft.Web.WebView2.Core.dll'
$wf   = Join-Path $wvDir 'Microsoft.Web.WebView2.WinForms.dll'
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } elseif ([Environment]::Is64BitProcess) { 'win-x64' } else { 'win-x86' }
$loaderDir = Join-Path $wvDir "runtimes\$arch\native"
if ($env:RETROAMP_NAVEGADOR) { $hostErr = 'Modo navegador forzado por RETROAMP_NAVEGADOR' }
elseif (-not (Test-Path $core) -or -not (Test-Path $wf)) { $hostErr = 'Faltan los archivos de WebView2 en bin\webview2' }
else {
    try {
        Write-Host 'Abriendo la ventana del reproductor...'
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        Add-Type -ErrorAction Stop -TypeDefinition @'
using System; using System.IO; using System.Reflection;
public static class RetroAmpResolver {
    static string dir;
    public static void Install(string d) { dir = d; AppDomain.CurrentDomain.AssemblyResolve += OnResolve; }
    static Assembly OnResolve(object s, ResolveEventArgs e) {
        string n = new AssemblyName(e.Name).Name;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) { try { if (a.GetName().Name == n) return a; } catch { } }
        string p = Path.Combine(dir, n + ".dll");
        if (File.Exists(p)) return Assembly.LoadFrom(p);
        return null;
    }
}
'@
        [RetroAmpResolver]::Install($wvDir)
        [void][System.Reflection.Assembly]::LoadFrom($core)
        [void][System.Reflection.Assembly]::LoadFrom($wf)
        $cs = [System.IO.File]::ReadAllText((Join-Path $root 'host.cs'))
        Add-Type -TypeDefinition $cs -ReferencedAssemblies @('System.Windows.Forms', 'System.Drawing', $core, $wf) -IgnoreWarnings -ErrorAction Stop
    } catch { $hostErr = "No se pudo preparar la ventana: $($_.Exception.Message)" }
}

if (-not $hostErr) {
    $ctx.HostMode = $true
    Set-Console 0   # ocultar esta ventana: el reproductor tiene su icono junto al reloj
    try { $r = [RetroAmp.Host]::Run($url, $profDir, $root, $ctx, $loaderDir); if ($r) { $hostErr = $r } }
    catch { $hostErr = $_.Exception.ToString() }
    $ctx.HostMode = $false
    if ($hostErr -and -not $ctx.Stop) { Set-Console 1 }
}

if ($hostErr -and -not $ctx.Stop) {
    Log $hostErr
    Write-Host ''
    Write-Host 'No pude abrir la ventana propia; uso Chrome/Edge en su lugar.' -ForegroundColor Yellow
    Write-Host '(El modo fantasma y los botones de la barra de tareas solo funcionan en la ventana propia.'
    Write-Host " El detalle del problema quedo en registro.txt)"
    Write-Host 'Esta ventana es el motor del reproductor: puedes minimizarla, pero no la cierres.'
    Open-Browser
    Start-Sleep -Seconds 3
    Set-Console 6
    while (-not $ctx.Stop) { Start-Sleep -Milliseconds 500 }
}

$ctx.Stop = $true
try { [void]$srvHandle.AsyncWaitHandle.WaitOne(3000) } catch {}
