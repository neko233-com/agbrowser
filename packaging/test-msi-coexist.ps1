$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build = [IO.Path]::GetFullPath((Join-Path $repo 'build'))
$id = [guid]::NewGuid().ToString('N')
$name = 'agbupgrade' + $id
$progid = 'AgbUpgrade' + $id + '.Document'
$exeName = $name + '.exe'
$oldUpgrade = [guid]::NewGuid().ToString()
$newUpgrade = [guid]::NewGuid().ToString()
$oldSource = Join-Path $build ($name + '-old.wxs')
$newSource = Join-Path $build ($name + '-new.wxs')
$oldMsi = Join-Path $build ($name + '-old.msi')
$newMsi = Join-Path $build ($name + '-new.msi')
$target = [IO.Path]::GetFullPath((Join-Path $build ($name + '-install')))
$menu = Join-Path $env:APPDATA ('Microsoft\Windows\Start Menu\Programs\' + $name + ' Desktop')
$marker = 'Software\' + $name + '\marker'
$paths = @(('Software\' + $name), ('Software\' + $name + '-desktop'), ('Software\Classes\' + $progid), ('Software\Classes\Applications\' + $exeName))
$views = @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)
function Get-Key([Microsoft.Win32.RegistryView]$view, [string]$path) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    try { $key = $base.OpenSubKey($path); if ($key) { $key.Dispose(); return $true }; return $false } finally { $base.Dispose() }
}
function Get-Value([Microsoft.Win32.RegistryView]$view, [string]$path, [string]$valueName) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    try { $key = $base.OpenSubKey($path); if (-not $key) { return $null }; try { return $key.GetValue($valueName) } finally { $key.Dispose() } } finally { $base.Dispose() }
}
if (-not $target.StartsWith($build + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $menu)) { throw 'Unsafe or existing fixture target' }
foreach ($view in $views) {
    foreach ($path in $paths) { if (Get-Key $view $path) { throw 'Fixture registry key already exists' } }
    if (Get-Value $view 'Software\RegisteredApplications' ($name + '.desktop')) { throw 'Fixture registration already exists' }
}
$old = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Package Name="$name" Manufacturer="fixture" Version="0.1.0" UpgradeCode="$oldUpgrade" Scope="perUser">
<StandardDirectory Id="LocalAppDataFolder" />
<ComponentGroup Id="Probe" Directory="LocalAppDataFolder">
<Component Id="Legacy"><RegistryValue Root="HKCU" Key="Software\$name" Name="OldInstalled" Type="string" Value="yes" KeyPath="yes" /><RemoveRegistryKey Root="HKCU" Key="$marker" Action="removeOnUninstall" /></Component>
<Component Id="Marker"><RegistryValue Root="HKCU" Key="$marker" Name="Keep" Type="string" Value="untouched" KeyPath="yes" /></Component>
</ComponentGroup><Feature Id="Main"><ComponentGroupRef Id="Probe" /></Feature></Package></Wix>
"@
$new = [IO.File]::ReadAllText((Join-Path $repo 'packaging\agbrowser.wxs'))
$new = $new.Replace('Agbrowser.Document', $progid).Replace('AgBrowser.Desktop.exe', $exeName).Replace('agbrowser', $name).Replace('B354E2D2-C816-4961-8F6D-A5CEAB83A219', $newUpgrade)
$new = [regex]::Replace($new, 'Guid="[0-9A-Fa-f-]{36}"', [Text.RegularExpressions.MatchEvaluator]{ param($m) 'Guid="' + [guid]::NewGuid().ToString() + '"' })
[IO.File]::WriteAllText($oldSource, $old, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($newSource, $new, [Text.UTF8Encoding]::new($false))
$wix = Join-Path $build 'wix5\wix.exe'
& $wix build -arch x86 -o $oldMsi $oldSource
if ($LASTEXITCODE -ne 0) { throw 'Old fixture build failed' }
& $wix build -arch x64 -d 'Version=0.1.1' -d ('LauncherExe=' + (Join-Path $build 'desktop\AgBrowser.Desktop.exe')) -d ('IconFile=' + (Join-Path $repo 'packages\launcher\assets\icon.ico')) -d ('LicenseRtf=' + (Join-Path $build 'license.rtf')) -ext WixToolset.UI.wixext -o $newMsi $newSource
if ($LASTEXITCODE -ne 0) { throw 'New fixture build failed' }
function Run-Msi([string[]]$arguments) { $p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait; return $p.ExitCode }
$oldInstalled = $false; $newInstalled = $false
try {
    $exit = Run-Msi @('/i', ('"' + $oldMsi + '"'), '/qn', '/norestart')
    $oldInstalled = $exit -in @(0, 3010)
    if ($exit -ne 0) { throw "Old fixture install failed: $exit" }
    $view64 = [Microsoft.Win32.RegistryView]::Registry64
    if ((Get-Value $view64 $marker 'Keep') -ne 'untouched') { throw 'Old fixture marker missing before upgrade' }
    $log = Join-Path $build ($name + '-coexist.log')
    $exit = Run-Msi @('/i', ('"' + $newMsi + '"'), '/qn', '/norestart', ('INSTALLFOLDER="' + $target + '"'), '/l*v', ('"' + $log + '"'))
    $newInstalled = $exit -in @(0, 3010)
    if ($exit -ne 0) { throw "New fixture install failed: $exit" }
    if ((Get-Value $view64 $marker 'Keep') -ne 'untouched' -or (Get-Value $view64 ('Software\' + $name) 'OldInstalled') -ne 'yes') { throw 'Old fixture or marker was modified during new install' }
    if (-not (Test-Path -LiteralPath (Join-Path $target $exeName))) { throw 'New fixture executable missing' }
    $expected = 'Software\Classes\Applications\' + $exeName + '\Capabilities'
    if ((Get-Value $view64 'Software\RegisteredApplications' ($name + '.desktop')) -ne $expected) { throw 'New fixture registration missing' }
    $exit = Run-Msi @('/x', ('"' + $newMsi + '"'), '/qn', '/norestart')
    if ($exit -ne 0) { throw "New fixture uninstall failed: $exit" }
    $newInstalled = $false
    if ((Get-Value $view64 $marker 'Keep') -ne 'untouched' -or (Get-Value $view64 ('Software\' + $name) 'OldInstalled') -ne 'yes') { throw 'Old fixture or marker was modified during new uninstall' }
    Write-Output 'PASS old product and simulated association survive separate desktop install and uninstall'
} finally {
    if ($newInstalled) { $exit = Run-Msi @('/x', ('"' + $newMsi + '"'), '/qn', '/norestart'); if ($exit -ne 0) { throw "New fixture uninstall failed: $exit" } }
    if ($oldInstalled) { $exit = Run-Msi @('/x', ('"' + $oldMsi + '"'), '/qn', '/norestart'); if ($exit -notin @(0, 1605)) { throw "Old fixture uninstall failed: $exit" } }
    foreach ($view in $views) {
        foreach ($path in $paths) { if (Get-Key $view $path) { throw "Fixture key remains: $path" } }
        if (Get-Value $view 'Software\RegisteredApplications' ($name + '.desktop')) { throw 'Fixture registration remains' }
    }
    if ((Test-Path -LiteralPath (Join-Path $target $exeName)) -or (Test-Path -LiteralPath $menu)) { throw 'Fixture files or menu remain' }
    Write-Output 'PASS isolated coexistence fixture cleanup'
}
