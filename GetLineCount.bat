@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "LINE_COUNTER_BAT=%~f0"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; $marker = '#__LINE_COUNTER_POWERSHELL__'; $bat = $env:LINE_COUNTER_BAT; $content = Get-Content -Raw -LiteralPath $bat; $start = $content.LastIndexOf($marker); if ($start -lt 0) { throw 'Embedded PowerShell block was not found.' }; $script = $content.Substring($start + $marker.Length).TrimStart(\"`r\", \"`n\"); Invoke-Expression $script"

if errorlevel 1 (
    echo.
    echo Line counter failed.
    pause
    exit /b 1
)

echo.
pause
exit /b 0

#__LINE_COUNTER_POWERSHELL__
$scriptDirectory = Split-Path -Parent $env:LINE_COUNTER_BAT
$root = $scriptDirectory
try {
    $gitRoot = & git -C $scriptDirectory rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($gitRoot)) {
        $root = $gitRoot.Trim()
    }
}
catch {
    $root = $scriptDirectory
}

$root = [System.IO.Path]::GetFullPath($root)

$skipDirectories = @(
    '.git',
    '.vs',
    '.vscode',
    '.idea',
    '.agents',
    'bin',
    'obj',
    'packages',
    'node_modules',
    'artifacts',
    'dist',
    'build',
    'Debug',
    'Release'
)

$projectFileExtensions = @(
    '.csproj',
    '.fsproj',
    '.proj',
    '.sqlproj',
    '.vbproj',
    '.vcxproj'
)

$languageByExtension = @{
    '.asax'         = 'ASP.NET'
    '.ascx'         = 'ASP.NET'
    '.asmx'         = 'ASP.NET'
    '.aspx'         = 'ASP.NET'
    '.bat'          = 'Batch'
    '.cmd'          = 'Batch'
    '.c'            = 'C'
    '.c++'          = 'C++'
    '.cc'           = 'C++'
    '.config'       = 'XML'
    '.cpp'          = 'C++'
    '.cuh'          = 'CUDA C++'
    '.cu'           = 'CUDA C++'
    '.cxx'          = 'C++'
    '.cs'           = 'C#'
    '.cshtml'       = 'Razor'
    '.csproj'       = 'MSBuild/XML'
    '.css'          = 'CSS'
    '.editorconfig' = 'EditorConfig'
    '.fs'           = 'F#'
    '.fsproj'       = 'MSBuild/XML'
    '.h'            = 'C++'
    '.hh'           = 'C++'
    '.hpp'          = 'C++'
    '.htm'          = 'HTML'
    '.html'         = 'HTML'
    '.hxx'          = 'C++'
    '.inl'          = 'C++'
    '.ixx'          = 'C++'
    '.java'         = 'Java'
    '.js'           = 'JavaScript'
    '.json'         = 'JSON'
    '.jsonc'        = 'JSON'
    '.jsx'          = 'JavaScript'
    '.md'           = 'Markdown'
    '.props'        = 'MSBuild/XML'
    '.ps1'          = 'PowerShell'
    '.psd1'         = 'PowerShell'
    '.psm1'         = 'PowerShell'
    '.py'           = 'Python'
    '.razor'        = 'Razor'
    '.resx'         = 'XML'
    '.scss'         = 'SCSS'
    '.sh'           = 'Shell'
    '.sln'          = 'Solution'
    '.sql'          = 'SQL'
    '.targets'      = 'MSBuild/XML'
    '.ts'           = 'TypeScript'
    '.tsx'          = 'TypeScript'
    '.txt'          = 'Text'
    '.vb'           = 'Visual Basic'
    '.vbproj'       = 'MSBuild/XML'
    '.vcxproj'      = 'MSBuild/XML'
    '.vcxproj.filters' = 'MSBuild/XML'
    '.xaml'         = 'XAML'
    '.xml'          = 'XML'
    '.yaml'         = 'YAML'
    '.yml'          = 'YAML'
}

function Test-SkippedPath {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo] $File
    )

    $relative = $File.FullName.Substring($root.Length).TrimStart('\', '/')
    if ([string]::IsNullOrWhiteSpace($relative)) {
        return $false
    }

    $parts = $relative -split '[\\/]'
    foreach ($part in $parts) {
        if ($skipDirectories -contains $part) {
            return $true
        }
    }

    return $false
}

function Get-LanguageName {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo] $File
    )

    $name = $File.Name.ToLowerInvariant()
    if ($languageByExtension.ContainsKey($name)) {
        return $languageByExtension[$name]
    }

    $extension = $File.Extension.ToLowerInvariant()
    if ($languageByExtension.ContainsKey($extension)) {
        return $languageByExtension[$extension]
    }

    foreach ($suffix in ($languageByExtension.Keys | Sort-Object -Property Length -Descending)) {
        if ($name.EndsWith($suffix)) {
            return $languageByExtension[$suffix]
        }
    }

    return $null
}

function Get-NonEmptyLineCount {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $count = 0
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $count++
        }
    }

    return $count
}

function Get-ProjectName {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo] $File
    )

    $directory = [System.IO.Path]::GetFullPath($File.DirectoryName).TrimEnd('\', '/')
    while (-not [string]::IsNullOrWhiteSpace($directory)) {
        if ($projectRoots.ContainsKey($directory)) {
            return $projectRoots[$directory]
        }

        if ($directory.Equals($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }

        $parent = [System.IO.Directory]::GetParent($directory)
        if ($null -eq $parent) {
            break
        }

        $directory = $parent.FullName.TrimEnd('\', '/')
    }

    $relative = $File.FullName.Substring($root.Length).TrimStart('\', '/')
    $parts = $relative -split '[\\/]'
    if ($parts.Count -gt 1) {
        return ('(No project) {0}' -f $parts[0])
    }

    return '(Repo root)'
}

$projectRoots = @{}
Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { -not (Test-SkippedPath -File $_) } |
    Where-Object { $projectFileExtensions -contains $_.Extension.ToLowerInvariant() } |
    Sort-Object -Property FullName |
    ForEach-Object {
        $projectDirectory = [System.IO.Path]::GetFullPath($_.DirectoryName).TrimEnd('\', '/')
        if (-not $projectRoots.ContainsKey($projectDirectory)) {
            $projectRoots[$projectDirectory] = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
        }
    }

$stats = @{}
$projectStats = @{}
$projectTotals = @{}
$scannedFiles = 0
$unmappedFiles = 0
$unmappedFileNames = New-Object 'System.Collections.Generic.List[string]'
$unreadableFiles = 0

Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { -not (Test-SkippedPath -File $_) } |
    ForEach-Object {
        $language = Get-LanguageName -File $_
        if ($null -eq $language) {
            $unmappedFiles++
            [void]$unmappedFileNames.Add($_.Name)
            return
        }

        try {
            $lines = Get-NonEmptyLineCount -Path $_.FullName
        }
        catch {
            $unreadableFiles++
            return
        }

        $project = Get-ProjectName -File $_

        if (-not $stats.ContainsKey($language)) {
            $stats[$language] = [pscustomobject]@{
                Language = $language
                Files    = 0
                Lines    = 0
            }
        }

        $stats[$language].Files = [int]$stats[$language].Files + 1
        $stats[$language].Lines = [int]$stats[$language].Lines + $lines

        if (-not $projectTotals.ContainsKey($project)) {
            $projectTotals[$project] = [pscustomobject]@{
                Project = $project
                Files   = 0
                Lines   = 0
            }
        }

        $projectTotals[$project].Files = [int]$projectTotals[$project].Files + 1
        $projectTotals[$project].Lines = [int]$projectTotals[$project].Lines + $lines

        $projectLanguageKey = '{0}|{1}' -f $project, $language
        if (-not $projectStats.ContainsKey($projectLanguageKey)) {
            $projectStats[$projectLanguageKey] = [pscustomobject]@{
                Project  = $project
                Language = $language
                Files    = 0
                Lines    = 0
            }
        }

        $projectStats[$projectLanguageKey].Files = [int]$projectStats[$projectLanguageKey].Files + 1
        $projectStats[$projectLanguageKey].Lines = [int]$projectStats[$projectLanguageKey].Lines + $lines
        $scannedFiles++
    }

$rows = @($stats.Values | Sort-Object -Property Lines, Language -Descending)
$projectRows = @($projectTotals.Values | Sort-Object -Property Lines, Project -Descending)
$projectLanguageRows = @(
    $projectStats.Values |
        Sort-Object `
            @{ Expression = { $projectTotals[$_.Project].Lines }; Descending = $true },
            @{ Expression = { $_.Project }; Descending = $false },
            @{ Expression = { $_.Lines }; Descending = $true },
            @{ Expression = { $_.Language }; Descending = $false }
)
$totalFiles = 0
$totalLines = 0
foreach ($row in $rows) {
    $totalFiles += [int]$row.Files
    $totalLines += [int]$row.Lines
}

$art = @'
 _     ___ _   _ _____   ____ ___  _   _ _   _ _____ _____ ____
| |   |_ _| \ | | ____| / ___/ _ \| | | | \ | |_   _| ____|  _ \
| |    | ||  \| |  _|  | |  | | | | | | |  \| | | | |  _| | |_) |
| |___ | || |\  | |___ | |__| |_| | |_| | |\  | | | | |___|  _ <
|_____|___|_| \_|_____| \____\___/ \___/|_| \_| |_| |_____|_| \_\
'@

Clear-Host
Write-Host $art -ForegroundColor Cyan
Write-Host ''
Write-Host ('Root        : {0}' -f $root)
Write-Host ('Count rule  : non-empty, non-whitespace lines only')
Write-Host ('Project rule: nearest ancestor project file wins')
Write-Host ('Skipped dirs: {0}' -f ($skipDirectories -join ', '))
Write-Host ''

if ($rows.Count -eq 0) {
    Write-Host 'No mapped source/text files were found.'
    exit 0
}

$languageWidth = [Math]::Max(8, [int](($rows | ForEach-Object { $_.Language.Length } | Measure-Object -Maximum).Maximum))
$fileWidth = [Math]::Max(5, ([string]$totalFiles).Length)
$lineWidth = [Math]::Max(5, ([string]$totalLines).Length)
$barWidth = 42
$maxLines = [int](($rows | Measure-Object -Property Lines -Maximum).Maximum)

$border = '+-' + ('-' * $languageWidth) + '-+-' + ('-' * $fileWidth) + '-+-' + ('-' * $lineWidth) + '-+-' + ('-' * $barWidth) + '-+'

Write-Host $border -ForegroundColor DarkCyan
Write-Host ('| {0} | {1} | {2} | {3} |' -f 'Language'.PadRight($languageWidth), 'Files'.PadLeft($fileWidth), 'Lines'.PadLeft($lineWidth), 'ASCII Diagram'.PadRight($barWidth))
Write-Host $border -ForegroundColor DarkCyan

foreach ($row in $rows) {
    $barLength = 0
    if ($maxLines -gt 0 -and $row.Lines -gt 0) {
        $barLength = [Math]::Max(1, [Math]::Round(([double]$row.Lines / [double]$maxLines) * $barWidth))
    }

    $bar = ('#' * $barLength).PadRight($barWidth)
    Write-Host ('| {0} | {1} | {2} | {3} |' -f $row.Language.PadRight($languageWidth), ([string]$row.Files).PadLeft($fileWidth), ([string]$row.Lines).PadLeft($lineWidth), $bar)
}

Write-Host $border -ForegroundColor DarkCyan
Write-Host ('| {0} | {1} | {2} | {3} |' -f 'TOTAL'.PadRight($languageWidth), ([string]$totalFiles).PadLeft($fileWidth), ([string]$totalLines).PadLeft($lineWidth), ('=' * $barWidth))
Write-Host $border -ForegroundColor DarkCyan
Write-Host ''

if ($projectRows.Count -gt 0) {
    Write-Host 'PROJECT TOTALS' -ForegroundColor Cyan

    $projectWidth = [Math]::Max(7, [int](($projectRows | ForEach-Object { $_.Project.Length } | Measure-Object -Maximum).Maximum))
    $projectFileWidth = [Math]::Max(5, ([string]$totalFiles).Length)
    $projectLineWidth = [Math]::Max(5, ([string]$totalLines).Length)
    $projectBarWidth = 34
    $maxProjectLines = [int](($projectRows | Measure-Object -Property Lines -Maximum).Maximum)
    $projectBorder = '+-' + ('-' * $projectWidth) + '-+-' + ('-' * $projectFileWidth) + '-+-' + ('-' * $projectLineWidth) + '-+-' + ('-' * $projectBarWidth) + '-+'

    Write-Host $projectBorder -ForegroundColor DarkCyan
    Write-Host ('| {0} | {1} | {2} | {3} |' -f 'Project'.PadRight($projectWidth), 'Files'.PadLeft($projectFileWidth), 'Lines'.PadLeft($projectLineWidth), 'ASCII Diagram'.PadRight($projectBarWidth))
    Write-Host $projectBorder -ForegroundColor DarkCyan

    foreach ($projectRow in $projectRows) {
        $barLength = 0
        if ($maxProjectLines -gt 0 -and $projectRow.Lines -gt 0) {
            $barLength = [Math]::Max(1, [Math]::Round(([double]$projectRow.Lines / [double]$maxProjectLines) * $projectBarWidth))
        }

        $bar = ('#' * $barLength).PadRight($projectBarWidth)
        Write-Host ('| {0} | {1} | {2} | {3} |' -f $projectRow.Project.PadRight($projectWidth), ([string]$projectRow.Files).PadLeft($projectFileWidth), ([string]$projectRow.Lines).PadLeft($projectLineWidth), $bar)
    }

    Write-Host $projectBorder -ForegroundColor DarkCyan
    Write-Host ''

    Write-Host 'PROJECT LANGUAGE BREAKDOWN' -ForegroundColor Cyan

    $projectLanguageProjectWidth = $projectWidth
    $projectLanguageLanguageWidth = [Math]::Max(8, [int](($projectLanguageRows | ForEach-Object { $_.Language.Length } | Measure-Object -Maximum).Maximum))
    $projectLanguageFileWidth = [Math]::Max(5, ([string]$totalFiles).Length)
    $projectLanguageLineWidth = [Math]::Max(5, ([string]$totalLines).Length)
    $projectLanguageBarWidth = 28
    $maxProjectLanguageLines = [int](($projectLanguageRows | Measure-Object -Property Lines -Maximum).Maximum)
    $projectLanguageBorder = '+-' + ('-' * $projectLanguageProjectWidth) + '-+-' + ('-' * $projectLanguageLanguageWidth) + '-+-' + ('-' * $projectLanguageFileWidth) + '-+-' + ('-' * $projectLanguageLineWidth) + '-+-' + ('-' * $projectLanguageBarWidth) + '-+'

    Write-Host $projectLanguageBorder -ForegroundColor DarkCyan
    Write-Host ('| {0} | {1} | {2} | {3} | {4} |' -f 'Project'.PadRight($projectLanguageProjectWidth), 'Language'.PadRight($projectLanguageLanguageWidth), 'Files'.PadLeft($projectLanguageFileWidth), 'Lines'.PadLeft($projectLanguageLineWidth), 'ASCII Diagram'.PadRight($projectLanguageBarWidth))
    Write-Host $projectLanguageBorder -ForegroundColor DarkCyan

    foreach ($projectLanguageRow in $projectLanguageRows) {
        $barLength = 0
        if ($maxProjectLanguageLines -gt 0 -and $projectLanguageRow.Lines -gt 0) {
            $barLength = [Math]::Max(1, [Math]::Round(([double]$projectLanguageRow.Lines / [double]$maxProjectLanguageLines) * $projectLanguageBarWidth))
        }

        $bar = ('#' * $barLength).PadRight($projectLanguageBarWidth)
        Write-Host ('| {0} | {1} | {2} | {3} | {4} |' -f $projectLanguageRow.Project.PadRight($projectLanguageProjectWidth), $projectLanguageRow.Language.PadRight($projectLanguageLanguageWidth), ([string]$projectLanguageRow.Files).PadLeft($projectLanguageFileWidth), ([string]$projectLanguageRow.Lines).PadLeft($projectLanguageLineWidth), $bar)
    }

    Write-Host $projectLanguageBorder -ForegroundColor DarkCyan
    Write-Host ''
}

Write-Host ('Mapped files   : {0}' -f $scannedFiles)
Write-Host ('Unmapped files : {0}' -f $unmappedFiles)
Write-Host ('Unreadable     : {0}' -f $unreadableFiles)

if ($unmappedFileNames.Count -gt 0) {
    Write-Host ''
    Write-Host 'UNMAPPED FILES' -ForegroundColor Yellow

    $sortedUnmappedFiles = @($unmappedFileNames | Sort-Object)
    $unmappedIndexWidth = [Math]::Max(1, ([string]$sortedUnmappedFiles.Count).Length)
    $unmappedNameWidth = [Math]::Max(4, [int](($sortedUnmappedFiles | ForEach-Object { $_.Length } | Measure-Object -Maximum).Maximum))
    $unmappedBorder = '+-' + ('-' * $unmappedIndexWidth) + '-+-' + ('-' * $unmappedNameWidth) + '-+'

    Write-Host $unmappedBorder -ForegroundColor DarkYellow
    Write-Host ('| {0} | {1} |' -f '#'.PadLeft($unmappedIndexWidth), 'Name'.PadRight($unmappedNameWidth))
    Write-Host $unmappedBorder -ForegroundColor DarkYellow

    for ($i = 0; $i -lt $sortedUnmappedFiles.Count; $i++) {
        Write-Host ('| {0} | {1} |' -f ([string]($i + 1)).PadLeft($unmappedIndexWidth), $sortedUnmappedFiles[$i].PadRight($unmappedNameWidth))
    }

    Write-Host $unmappedBorder -ForegroundColor DarkYellow
}
