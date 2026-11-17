param([string] $OutputPath = (Join-Path $PSScriptRoot 'BorderlessGameWindow.exe'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework 4.x x64 compiler is required.' }
$sources = (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs').FullName
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warnaserror+ /codepage:65001 "/out:$OutputPath" "/win32manifest:$PSScriptRoot\app.manifest" "/win32icon:$PSScriptRoot\app.ico" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.Linq.dll $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
