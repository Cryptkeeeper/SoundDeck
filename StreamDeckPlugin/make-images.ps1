# Draws the plugin's PNG icons (normal + @2x) into the folder given as the first argument.
param([string]$out)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $out | Out-Null

function Save($name, $draw) {
    foreach ($scale in 1, 2) {
        $size = 72 * $scale
        $bmp = New-Object Drawing.Bitmap $size, $size
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
        $g.ScaleTransform($scale, $scale)
        & $draw $g
        $g.Dispose()
        $file = if ($scale -eq 1) { "$out\$name.png" } else { "$out\$name@2x.png" }
        $bmp.Save($file, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    }
}

function Center($g, $text, $size, $y, $color) {
    $font = New-Object Drawing.Font 'Segoe UI', $size, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object Drawing.StringFormat; $fmt.Alignment = 'Center'
    $g.DrawString($text, $font, (New-Object Drawing.SolidBrush $color), (New-Object Drawing.RectangleF 0, $y, 72, 40), $fmt)
}

$channels = @{ game = @('G', '#FF6A3D'); chat = @('C', '#3DDC97'); media = @('M', '#4DA3FF'); aux = @('A', '#B57BFF') }
foreach ($k in $channels.Keys) {
    $letter = $channels[$k][0]; $color = [Drawing.ColorTranslator]::FromHtml($channels[$k][1])
    $file = if ($k -eq 'aux') { 'auxch' } else { $k }  # "aux" is a reserved file name on Windows
    Save $file { param($g) $g.FillEllipse((New-Object Drawing.SolidBrush $color), 8, 8, 56, 56); Center $g $letter 30 17 ([Drawing.Color]::White) }.GetNewClosure()
    # Output-switch key: speaker in the channel color.
    Save "${k}out" {
        param($g)
        $g.Clear([Drawing.ColorTranslator]::FromHtml('#1E2127'))
        $b = New-Object Drawing.SolidBrush $color
        $g.FillRectangle($b, 14, 28, 12, 16)
        $g.FillPolygon($b, [Drawing.PointF[]]@((New-Object Drawing.PointF 24, 28), (New-Object Drawing.PointF 38, 16), (New-Object Drawing.PointF 38, 56), (New-Object Drawing.PointF 24, 44)))
        $pen = New-Object Drawing.Pen $color, 4; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawArc($pen, 34, 24, 16, 24, -50, 100); $g.DrawArc($pen, 34, 16, 28, 40, -50, 100)
    }.GetNewClosure()
}

$orange = [Drawing.ColorTranslator]::FromHtml('#FF6A3D')
$logo = {
    param($g)
    $g.FillEllipse((New-Object Drawing.SolidBrush $orange), 4, 4, 64, 64)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), 6; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLine($pen, 24, 44, 24, 30); $g.DrawLine($pen, 36, 52, 36, 20); $g.DrawLine($pen, 48, 42, 48, 32)
}.GetNewClosure()
Save 'plugin' $logo
Save 'category' $logo

$dark = [Drawing.ColorTranslator]::FromHtml('#2A2E36'); $green = [Drawing.ColorTranslator]::FromHtml('#1E8C5F')
Save 'mix_off' { param($g) $g.Clear($dark); Center $g 'MUSIC' 15 10 ([Drawing.Color]::Gainsboro); Center $g 'IN MIC' 15 27 ([Drawing.Color]::Gainsboro); Center $g 'OFF' 13 47 ([Drawing.Color]::Gray) }.GetNewClosure()
$muteRed = [Drawing.ColorTranslator]::FromHtml('#962D2D')
Save 'chat_live' { param($g) $g.Clear($dark); Center $g 'CHAT' 15 10 ([Drawing.Color]::Gainsboro); Center $g 'MIC' 15 27 ([Drawing.Color]::Gainsboro); Center $g 'LIVE' 13 47 ([Drawing.Color]::FromArgb(61, 220, 151)) }.GetNewClosure()
Save 'chat_muted' { param($g) $g.Clear($muteRed); Center $g 'CHAT' 15 10 ([Drawing.Color]::White); Center $g 'MIC' 15 27 ([Drawing.Color]::White); Center $g 'MUTED' 13 47 ([Drawing.Color]::White) }.GetNewClosure()
Save 'side_off' { param($g) $g.Clear($dark); Center $g 'HEAR' 15 10 ([Drawing.Color]::Gainsboro); Center $g 'MYSELF' 15 27 ([Drawing.Color]::Gainsboro); Center $g 'OFF' 13 47 ([Drawing.Color]::Gray) }.GetNewClosure()
Save 'side_on' { param($g) $g.Clear($green); Center $g 'HEAR' 15 10 ([Drawing.Color]::White); Center $g 'MYSELF' 15 27 ([Drawing.Color]::White); Center $g 'ON' 13 47 ([Drawing.Color]::White) }.GetNewClosure()
Save 'ptt_idle' { param($g) $g.Clear($dark); Center $g 'PUSH' 15 10 ([Drawing.Color]::Gainsboro); Center $g 'TO TALK' 15 27 ([Drawing.Color]::Gainsboro); Center $g 'HOLD' 13 47 ([Drawing.Color]::Gray) }.GetNewClosure()
Save 'ptt_live' { param($g) $g.Clear($green); Center $g 'PUSH' 15 10 ([Drawing.Color]::White); Center $g 'TO TALK' 15 27 ([Drawing.Color]::White); Center $g 'TALKING' 13 47 ([Drawing.Color]::White) }.GetNewClosure()
$amber = [Drawing.ColorTranslator]::FromHtml('#E8A33D'); $red = [Drawing.ColorTranslator]::FromHtml('#B33A3A')
Save 'sound' { param($g) $g.Clear($dark); $g.FillEllipse((New-Object Drawing.SolidBrush $amber), 14, 14, 44, 44); $pts = [Drawing.PointF[]]@((New-Object Drawing.PointF 30, 25), (New-Object Drawing.PointF 46, 36), (New-Object Drawing.PointF 30, 47)); $g.FillPolygon([Drawing.Brushes]::White, $pts) }.GetNewClosure()
Save 'stopsounds' { param($g) $g.Clear($dark); $g.FillEllipse((New-Object Drawing.SolidBrush $red), 14, 8, 44, 44); $g.FillRectangle([Drawing.Brushes]::White, 28, 22, 16, 16); Center $g 'STOP' 12 52 ([Drawing.Color]::Gainsboro) }.GetNewClosure()
Save 'profile' { param($g) $g.Clear($dark); $b = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#3DDC97')); $g.FillRectangle($b, 16, 20, 40, 6); $g.FillRectangle($b, 16, 33, 40, 6); $g.FillRectangle($b, 16, 46, 40, 6); $g.FillEllipse([Drawing.Brushes]::White, 38, 17, 12, 12); $g.FillEllipse([Drawing.Brushes]::White, 22, 30, 12, 12); $g.FillEllipse([Drawing.Brushes]::White, 32, 43, 12, 12) }.GetNewClosure()
Save 'mix_on' { param($g) $g.Clear($green); Center $g 'MUSIC' 15 10 ([Drawing.Color]::White); Center $g 'IN MIC' 15 27 ([Drawing.Color]::White); Center $g 'ON' 13 47 ([Drawing.Color]::White) }.GetNewClosure()
$purple = [Drawing.ColorTranslator]::FromHtml('#B57BFF')
Save 'voicefx' { param($g) $g.Clear($dark); Center $g 'VOICE' 15 10 $purple; Center $g 'FX' 15 27 $purple; $pen = New-Object Drawing.Pen ([Drawing.Color]::White), 3; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $g.DrawBezier($pen, 18, 56, 28, 44, 34, 66, 44, 54); $g.DrawLine($pen, 44, 54, 54, 54) }.GetNewClosure()
Save 'bleep_idle' { param($g) $g.Clear($dark); Center $g 'BLEEP' 17 14 ([Drawing.Color]::Gainsboro); Center $g 'HOLD' 13 44 ([Drawing.Color]::Gray) }.GetNewClosure()
Save 'bleep_live' { param($g) $g.Clear($red); Center $g 'BLEEP' 17 14 ([Drawing.Color]::White); Center $g '* * *' 15 42 ([Drawing.Color]::White) }.GetNewClosure()
Save 'mon_off' { param($g) $g.Clear($dark); Center $g 'LISTEN' 15 10 ([Drawing.Color]::Gainsboro); Center $g 'STREAM' 15 27 ([Drawing.Color]::Gainsboro); Center $g 'OFF' 13 47 ([Drawing.Color]::Gray) }.GetNewClosure()
Save 'mon_on' { param($g) $g.Clear($green); Center $g 'LISTEN' 15 10 ([Drawing.Color]::White); Center $g 'STREAM' 15 27 ([Drawing.Color]::White); Center $g 'ON' 13 47 ([Drawing.Color]::White) }.GetNewClosure()
