param([string]$OutputPath, [switch]$Test)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Windows .NET Framework C# compiler was not found.'
}
$assemblies = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Xaml.dll',
    'WPF\WindowsBase.dll',
    'WPF\PresentationCore.dll',
    'WPF\PresentationFramework.dll',
    'WPF\UIAutomationTypes.dll'
)
$references = $assemblies | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$sources = 'Program.cs', 'Dashboard.cs', 'ActivityTracker.cs', 'ApplicationIcons.cs', 'UsageLog.cs' |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
if ($Test) {
    $testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('app-usage-tests-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $testDirectory | Out-Null
    $testExecutable = Join-Path $testDirectory 'UsageLogTests.exe'
    $testSource = Join-Path $PSScriptRoot 'tests\UsageLogTests.cs'
    & $compiler /nologo /target:exe /optimize+ /codepage:65001 /main:AppUsage.UsageLogTests `
        "/out:$testExecutable" @references @sources $testSource
    if ($LASTEXITCODE -ne 0) {
        throw "Test build failed with exit code $LASTEXITCODE."
    }
    & $testExecutable $testDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed with exit code $LASTEXITCODE. Test files are in $testDirectory."
    }
    return
}
# Express the Unicode file name without depending on PowerShell 5.1 source encoding.
$name = -join ([char[]](0x8F6F, 0x4EF6, 0x4F7F, 0x7528, 0x60C5, 0x51B5))
$output = if ($OutputPath) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
} else {
    Join-Path $PSScriptRoot ($name + '.exe')
}
$icon = Join-Path $PSScriptRoot 'App.ico'
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 "/win32icon:$icon" "/out:$output" @references @sources
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}
Write-Output $output
