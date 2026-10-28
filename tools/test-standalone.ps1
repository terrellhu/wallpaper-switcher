param([string]$ExecutablePath = (Join-Path $PSScriptRoot '..\dist\standalone-win-x64\WallpaperSwitcher.exe'))
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($ExecutablePath)
$testRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('..\dist\package-check-' + [Guid]::NewGuid().ToString('N'))))
$isolated = Join-Path $testRoot 'Only EXE 中文目录'
$extractRoot = Join-Path $testRoot 'bundled-runtime'
$report = Join-Path $testRoot 'verification.json'
[IO.Directory]::CreateDirectory($isolated) | Out-Null
[IO.Directory]::CreateDirectory($extractRoot) | Out-Null
$executable = Join-Path $isolated 'WallpaperSwitcher.exe'
Copy-Item -LiteralPath $source -Destination $executable
if (@(Get-ChildItem -LiteralPath $isolated -File).Count -ne 1) { throw 'Isolated folder must contain only the executable.' }

$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = $executable
$info.WorkingDirectory = $isolated
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$info.ArgumentList.Add('--verify-package')
$info.ArgumentList.Add($report)
# Scope these overrides to this test process, without changing the machine's .NET install.
$info.Environment['DOTNET_ROOT'] = Join-Path $testRoot 'no-installed-dotnet'
$info.Environment['DOTNET_ROOT_X64'] = Join-Path $testRoot 'no-installed-dotnet'
$info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$info.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $extractRoot
$info.Environment['DOTNET_HOST_TRACE'] = '1'
$info.Environment['DOTNET_HOST_TRACEFILE'] = Join-Path $testRoot 'host-trace.log'
$info.Environment['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
$process = [Diagnostics.Process]::Start($info)
try {
    if (-not $process.WaitForExit(30000)) {
        $process.Kill()
        throw 'Package verification exceeded 30 seconds.'
    }
    if ($process.ExitCode -ne 0) { throw "Package verification failed (exit $($process.ExitCode)). Report: $report" }
} finally { $process.Dispose() }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $result.Success -or -not $result.DesktopApiInitialized -or $result.IconWidth -ne 32 -or $result.LogoWidth -ne 512) {
    throw "Resource or desktop initialization check failed. Report: $report"
}
$runtimeIsExtracted = $result.RuntimeModule.StartsWith($extractRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
$runtimeIsLinked = $result.RuntimeLinkedIntoExecutable -and $result.RuntimeModule.Equals($executable, [StringComparison]::OrdinalIgnoreCase)
if (-not ($runtimeIsExtracted -or $runtimeIsLinked)) {
    throw "App did not use its bundled runtime: $($result.RuntimeModule)"
}
$workerReport = Join-Path $testRoot 'worker-verification.json'
$info.ArgumentList.Clear()
$info.ArgumentList.Add('--verify-worker')
$info.ArgumentList.Add($workerReport)
$workerCheck = [Diagnostics.Process]::Start($info)
try {
    if (-not $workerCheck.WaitForExit(20000)) { $workerCheck.Kill($true); throw 'Isolated worker check timed out.' }
    if ($workerCheck.ExitCode -ne 0) { throw "Isolated worker check failed: $workerReport" }
} finally { $workerCheck.Dispose() }
$workerResult = Get-Content -LiteralPath $workerReport -Raw | ConvertFrom-Json
if (-not $workerResult.Success) { throw "Worker verification failed: $workerReport" }
[PSCustomObject]@{
    Success = $true
    IsolatedExecutable = $executable
    Runtime = $result.Runtime
    BundledRuntimeUsed = $result.RuntimeModule
    IconAndLogoVerified = $true
    IsolatedWorkerVerified = $true
    Report = $report
} | ConvertTo-Json
