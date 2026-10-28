# Builds build\LGBrightnessHotkeys.exe with the C# compiler that ships with Windows (.NET Framework 4.8.1).
# No SDK needed. On ARM64 the ARM64 compiler is used; the manifest makes the exe run natively there.
# The light sensor API (Windows.Devices.Sensors) is referenced through the WinRT metadata in System32\WinMetadata.
# -Test also builds and runs build\Tests.exe (tests\Tests.cs).
param([switch]$Test)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\FrameworkArm64\v4.0.30319'
if (-not (Test-Path "$fw\csc.exe")) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319' }
$winmd = Join-Path $env:WINDIR 'System32\WinMetadata'
$refs = @(
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    "/r:$winmd\Windows.Devices.winmd", "/r:$winmd\Windows.Foundation.winmd",
    "/r:$fw\System.Runtime.dll", "/r:$fw\System.Runtime.InteropServices.WindowsRuntime.dll"
)
$common = @('/nologo', '/platform:anycpu', '/optimize+', '/codepage:65001', '/win32manifest:src\app.manifest') + $refs
New-Item -ItemType Directory -Force build | Out-Null
& "$fw\csc.exe" @common /target:winexe /out:build\LGBrightnessHotkeys.exe src\LGBrightnessHotkeys.cs
if ($LASTEXITCODE -ne 0 -or -not $Test) { exit $LASTEXITCODE }
& "$fw\csc.exe" @common /target:exe /main:LGBrightnessHotkeys.Tests /out:build\Tests.exe src\LGBrightnessHotkeys.cs tests\Tests.cs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& build\Tests.exe
exit $LASTEXITCODE
