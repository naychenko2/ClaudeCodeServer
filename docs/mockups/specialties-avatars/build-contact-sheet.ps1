# Contact sheet of the specialty avatar set: 14 roles, big tile + 40px circle, plus a 40px strip.
Add-Type -AssemblyName System.Drawing

$dir    = $PSScriptRoot
$root   = Split-Path (Split-Path (Split-Path $dir -Parent) -Parent) -Parent
$assets = Join-Path $root 'frontend\src\assets\specialties'

# Role keys - same as SpecialtyCatalog.cs (none entry excluded)
$roles = @(
    @{ Key = 'analyst';          Title = 'Analyst' },
    @{ Key = 'planner';          Title = 'Planner' },
    @{ Key = 'reviewer';         Title = 'Reviewer' },
    @{ Key = 'executor';         Title = 'Executor' },
    @{ Key = 'secretary';        Title = 'Secretary' },
    @{ Key = 'coordinator';      Title = 'Coordinator' },
    @{ Key = 'mentor';           Title = 'Mentor' },
    @{ Key = 'designer';         Title = 'Designer' },
    @{ Key = 'consultant';       Title = 'Consultant' },
    @{ Key = 'librarian';        Title = 'Librarian' },
    @{ Key = 'tester';           Title = 'Tester' },
    @{ Key = 'backendExecutor';  Title = 'Executor (backend)' },
    @{ Key = 'frontendExecutor'; Title = 'Executor (frontend)' },
    @{ Key = 'devopsExecutor';   Title = 'Executor (DevOps)' }
)

$cell = 200; $gap = 16; $left = 24; $top = 68; $chip = 40; $cols = 5
$cellH  = $cell + 6 + $chip + 20
$rowsN  = [Math]::Ceiling($roles.Count / $cols)
$width  = $left * 2 + $cell * $cols + $gap * ($cols - 1)
$stripY = $top + ($cellH + $gap) * $rowsN + 24
$height = $stripY + 30 + $chip + 40

$bmp = New-Object System.Drawing.Bitmap($width, $height)
$g   = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.InterpolationMode = 'HighQualityBicubic'
$g.Clear([System.Drawing.ColorTranslator]::FromHtml('#F4F0E8'))

$fontH1  = New-Object System.Drawing.Font('Segoe UI', 15, [System.Drawing.FontStyle]::Bold)
$fontCap = New-Object System.Drawing.Font('Segoe UI', 9)
$brushDark = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#2A251F'))
$brushMute = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#756B5E'))

$g.DrawString('Specialties - set B (cut-paper), 14 catalog roles', $fontH1, $brushDark, $left, 22)

function Draw-Circle($g, $img, $x, $y, $d) {
    $gs = $g.Save()
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddEllipse($x, $y, $d, $d)
    $g.SetClip($p)
    $g.DrawImage($img, $x, $y, $d, $d)
    $g.Restore($gs)
}

for ($i = 0; $i -lt $roles.Count; $i++) {
    $r = $roles[$i]
    $path = Join-Path $assets ("{0}.jpg" -f $r.Key)
    $img  = [System.Drawing.Image]::FromFile($path)
    $x = $left + ($cell + $gap) * ($i % $cols)
    $y = $top  + ($cellH + $gap) * [Math]::Floor($i / $cols)

    $g.DrawImage($img, $x, $y, $cell, $cell)
    Draw-Circle $g $img ($x + [int](($cell - $chip) / 2)) ($y + $cell + 6) $chip
    $g.DrawString(("{0}  ({1})" -f $r.Title, $r.Key), $fontCap, $brushMute, $x, $y + $cell + 6 + $chip + 2)
    $img.Dispose()
}

$g.DrawString('List exam: all 14 in a 40px circle', $fontH1, $brushDark, $left, $stripY - 6)
for ($i = 0; $i -lt $roles.Count; $i++) {
    $path = Join-Path $assets ("{0}.jpg" -f $roles[$i].Key)
    $img  = [System.Drawing.Image]::FromFile($path)
    Draw-Circle $g $img ($left + ($chip + 12) * $i) ($stripY + 30) $chip
    $img.Dispose()
}

$out = Join-Path $dir 'contact-sheet.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output $out
