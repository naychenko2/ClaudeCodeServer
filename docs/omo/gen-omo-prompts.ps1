# Генерация partial-файлов OmoPrompts из переводов docs/omo/translations:
# тело markdown (без frontmatter и секции «Адаптации») -> C# raw string const.
# Запуск из любого места: пути отсчитываются от расположения скрипта (docs/omo).
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$src = Join-Path $root 'docs\omo\translations'
$dst = Join-Path $root 'backend\ClaudeHomeServer\Services\Prompts'

function Get-Body($path) {
    $lines = [System.IO.File]::ReadAllLines($path)
    $dashes = @()
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -eq '---') { $dashes += $i } }
    $start = $dashes[1] + 1
    $end = $lines.Count - 1
    for ($i = $start; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^## Адаптации') { $end = $i - 1; break }
    }
    while ($end -ge $start -and ($lines[$end] -eq '---' -or $lines[$end] -eq '')) { $end-- }
    return (($lines[$start..$end]) -join "`n").Trim()
}

function Write-Const($mdFile, $csFile, $constName, $comment) {
    $body = Get-Body (Join-Path $src $mdFile)
    if ($body.Contains('"""""')) { throw "$mdFile содержит 5 кавычек подряд — поменяй делимитер" }
    $content = @"
namespace ClaudeHomeServer.Services.Prompts;

// $comment
// Сгенерировано из docs/omo/translations/$mdFile (не редактировать вручную —
// правь перевод и перегенерируй скриптом docs/omo/gen-omo-prompts.ps1).
public static partial class OmoPrompts
{
    public const string $constName = """""
$body
""""";
}
"@
    [System.IO.File]::WriteAllText((Join-Path $dst $csFile), $content, (New-Object System.Text.UTF8Encoding $true))
    Write-Host "$csFile : $((($body -split "`n").Count)) строк тела"
}

Write-Const 'ultrawork.md' 'OmoPrompts.Ultrawork.cs' 'Ultrawork' 'Режим максимального усилия ultrawork — инжект по магическому слову (флаг ultrawork-keyword).'
Write-Const 'categories.md' 'OmoPrompts.Categories.cs' 'DelegationCategories' 'Категории делегирования — как резать работу на субагентов (подсказка оркестратору/исполнителю).'

# Полные регламенты пантеона — один partial-файл каталога с 8 константами
$pantheon = @(
    @{ md = 'sisyphus.md';   const = 'SisyphusInstructions' },
    @{ md = 'hephaestus.md'; const = 'HephaestusInstructions' },
    @{ md = 'prometheus.md'; const = 'PrometheusInstructions' },
    @{ md = 'atlas.md';      const = 'AtlasInstructions' },
    @{ md = 'metis.md';      const = 'MetisInstructions' },
    @{ md = 'momus.md';      const = 'MomusInstructions' },
    @{ md = 'oracle.md';     const = 'OracleInstructions' },
    @{ md = 'librarian.md';  const = 'LibrarianInstructions' }
)
$sb = New-Object System.Text.StringBuilder
$null = $sb.AppendLine('namespace ClaudeHomeServer.Services.Prompts;')
$null = $sb.AppendLine('')
$null = $sb.AppendLine('// Полные регламенты ролей пантеона OmO (слот instructions контракта персоны).')
$null = $sb.AppendLine('// Сгенерировано из docs/omo/translations/*.md (не редактировать вручную —')
$null = $sb.AppendLine('// правь перевод и перегенерируй скриптом docs/omo/gen-omo-prompts.ps1).')
$null = $sb.AppendLine('public static partial class OmoPantheonCatalog')
$null = $sb.AppendLine('{')
foreach ($e in $pantheon) {
    $body = Get-Body (Join-Path $src $e.md)
    if ($body.Contains('"""""')) { throw "$($e.md) содержит 5 кавычек подряд" }
    $null = $sb.AppendLine("    internal const string $($e.const) = `"`"`"`"`"")
    $null = $sb.AppendLine($body)
    $null = $sb.AppendLine('""""";')
    $null = $sb.AppendLine('')
    Write-Host "$($e.const): $((($body -split "`n").Count)) строк"
}
$null = $sb.AppendLine('}')
[System.IO.File]::WriteAllText((Join-Path $dst 'OmoPantheonCatalog.Instructions.cs'), $sb.ToString(), (New-Object System.Text.UTF8Encoding $true))
Write-Host 'OmoPantheonCatalog.Instructions.cs готов'
