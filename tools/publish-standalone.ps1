$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\WallpaperSwitcher\WallpaperSwitcher.csproj'))
$output = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\dist\standalone-win-x64'))
& dotnet publish $project -p:PublishProfile=StandaloneWinX64 --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }
$files = @(Get-ChildItem -LiteralPath $output -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'WallpaperSwitcher.exe') {
    throw 'Expected exactly one WallpaperSwitcher.exe in the standalone output folder.'
}
$files | Select-Object FullName, @{ Name='SizeMiB'; Expression={ [Math]::Round($_.Length / 1MB, 2) } }
