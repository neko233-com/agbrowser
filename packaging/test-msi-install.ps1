param([string]$Msi)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build = [IO.Path]::GetFullPath((Join-Path $root 'build'))
$version = (Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json).version
$expectedMsi = [IO.Path]::GetFullPath((Join-Path $build ("agbrowser-$version-x64.msi")))
if (-not $Msi) { $Msi = $expectedMsi }
$msiPath = (Resolve-Path -LiteralPath $Msi).Path
if (-not $msiPath.Equals($expectedMsi, [StringComparison]::OrdinalIgnoreCase)) { throw 'Only the current workspace MSI can be tested' }
$target = [IO.Path]::GetFullPath((Join-Path $build ('msi-install-test-' + [guid]::NewGuid().ToString('N'))))
if (-not $target.StartsWith($build + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test directory is outside build' }
if (Test-Path -LiteralPath $target) { throw 'Test directory already exists' }

$views = @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)
function Test-UserKey([Microsoft.Win32.RegistryView]$view, [string]$path) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    try { $key = $base.OpenSubKey($path); if ($key) { $key.Dispose(); return $true }; return $false }
    finally { $base.Dispose() }
}
function Get-UserValue([Microsoft.Win32.RegistryView]$view, [string]$path, [string]$name) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    try { $key = $base.OpenSubKey($path); if (-not $key) { return $null }; try { return $key.GetValue($name) } finally { $key.Dispose() } }
    finally { $base.Dispose() }
}
foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
    foreach ($view in $views) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
        try {
            $uninstall = $base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall')
            if ($uninstall) {
                try { foreach ($name in $uninstall.GetSubKeyNames()) { $key = $uninstall.OpenSubKey($name); if ($key) { try { if ($key.GetValue('DisplayName') -like '*agbrowser*') { throw 'An agbrowser installation already exists; refusing to modify it' } } finally { $key.Dispose() } } } }
                finally { $uninstall.Dispose() }
            }
        } finally { $base.Dispose() }
    }
}
$ownedKeys = @('Software\Classes\Agbrowser.Document', 'Software\Classes\Applications\AgBrowser.Desktop.exe', 'Software\agbrowser-desktop')
$registration = 'Software\RegisteredApplications'
foreach ($view in $views) {
    foreach ($key in $ownedKeys) { if (Test-UserKey $view $key) { throw "Registry key already exists ($view): $key" } }
    if (Get-UserValue $view $registration 'agbrowser.desktop') { throw "Browser registration already exists ($view)" }
}
$menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\agbrowser Desktop'
if (Test-Path -LiteralPath $menu) { throw 'Start menu directory already exists' }

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$installer,@($msiPath, 0))
function Get-MsiProperty([string]$name) {
    $query = 'SELECT `Value` FROM `Property` WHERE `Property` = ''' + $name + ''''
    $view = $db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@($query))
    $view.GetType().InvokeMember('Execute','InvokeMethod',$null,$view,$null) | Out-Null
    $row = $view.GetType().InvokeMember('Fetch','InvokeMethod',$null,$view,$null)
    if (-not $row) { throw "MSI property missing: $name" }
    return $row.GetType().InvokeMember('StringData','GetProperty',$null,$row,@(1))
}
$productCode = Get-MsiProperty 'ProductCode'
if ($productCode -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { throw 'Invalid MSI product code' }
if ((Get-MsiProperty 'ProductName') -ne 'agbrowser Desktop' -or (Get-MsiProperty 'UpgradeCode') -ne '{B354E2D2-C816-4961-8F6D-A5CEAB83A219}') { throw 'Unexpected MSI identity' }
$view = $db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@('SELECT `Action`, `Type`, `Target` FROM `CustomAction`'))
$view.GetType().InvokeMember('Execute','InvokeMethod',$null,$view,$null) | Out-Null
while ($row = $view.GetType().InvokeMember('Fetch','InvokeMethod',$null,$view,$null)) {
    $action = $row.GetType().InvokeMember('StringData','GetProperty',$null,$row,@(1))
    if ($action -notin @('WixUIPrintEula_X64', 'WixUIValidatePath_X64')) { throw "Unexpected MSI custom action: $action" }
}

function Run-Installer([string[]]$installerArgs) {
    $p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $installerArgs -WindowStyle Hidden -PassThru -Wait
    return $p.ExitCode
}
function Get-Sha256([string]$file) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $stream = [IO.File]::OpenRead($file); try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') } finally { $stream.Dispose() } }
    finally { $sha.Dispose() }
}

$installed = $false
try {
    $installLog = Join-Path $build ('msi-install-' + [guid]::NewGuid().ToString('N') + '.log')
    $installCode = Run-Installer @('/i', ('"' + $msiPath + '"'), '/qn', '/norestart', ('INSTALLFOLDER="' + $target + '"'), '/l*v', ('"' + $installLog + '"'))
    $installed = $installCode -in @(0, 3010)
    if ($installCode -ne 0) { throw "Windows Installer install returned $installCode" }
    $exe = Join-Path $target 'AgBrowser.Desktop.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Installed executable missing' }
    $expectedHash = Get-Sha256 (Join-Path $build 'desktop\AgBrowser.Desktop.exe')
    if ((Get-Sha256 $exe) -ne $expectedHash) { throw 'Installed executable differs from the published build' }
    $registered = Get-UserValue ([Microsoft.Win32.RegistryView]::Registry64) $registration 'agbrowser.desktop'
    if ($registered -ne 'Software\Classes\Applications\AgBrowser.Desktop.exe\Capabilities') { throw 'Browser registration has an unexpected target' }
    $command = Get-UserValue ([Microsoft.Win32.RegistryView]::Registry64) 'Software\Classes\Agbrowser.Document\shell\open\command' ''
    $expectedCommand = '"' + $exe + '" --single-argument "%1"'
    if ($command -ne $expectedCommand) {
        $path = 'Software\Classes\Agbrowser.Document\shell\open\command'
        $presence = foreach ($view in $views) { "$view=$(Test-UserKey $view $path)" }
        $parents = foreach ($view in $views) { "$view=$(Test-UserKey $view 'Software\Classes\Agbrowser.Document')" }
        $foundUserHives = 0
        $users = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::Users, [Microsoft.Win32.RegistryView]::Registry64)
        try {
            foreach ($hiveName in $users.GetSubKeyNames()) {
                foreach ($suffix in @('Software\Classes\Agbrowser.Document', 'Agbrowser.Document')) {
                    try { $candidate = $users.OpenSubKey($hiveName + '\' + $suffix) }
                    catch [System.Security.SecurityException] { continue }
                    if ($candidate) { $foundUserHives++; $candidate.Dispose() }
                }
            }
        } finally { $users.Dispose() }
        throw "Installed browser command differs: actual=[$command], expected=[$expectedCommand], views=[$($presence -join ', ')], parents=[$($parents -join ', ')], matching user-hive paths=$foundUserHives"
    }
    $app = 'Software\Classes\Applications\AgBrowser.Desktop.exe'
    $view64 = [Microsoft.Win32.RegistryView]::Registry64
    if ((Get-UserValue $view64 ($app + '\shell\open\command') '') -ne $expectedCommand) { throw 'Application open command missing or incorrect' }
    if ((Get-UserValue $view64 ($app + '\Capabilities') 'ApplicationName') -ne 'agbrowser') { throw 'Application name capability missing or incorrect' }
    foreach ($scheme in @('http', 'https')) { if ((Get-UserValue $view64 ($app + '\Capabilities\URLAssociations') $scheme) -ne 'Agbrowser.Document') { throw "URL capability missing: $scheme" } }
    foreach ($extension in @('.htm', '.html')) { if ((Get-UserValue $view64 ($app + '\Capabilities\FileAssociations') $extension) -ne 'Agbrowser.Document') { throw "File capability missing: $extension" } }
    if (-not (Test-Path -LiteralPath (Join-Path $menu 'agbrowser.lnk'))) { throw 'Start menu shortcut missing' }
    Write-Output 'PASS per-user MSI install: executable hash, browser registration and Start menu shortcut'
}
finally {
    if ($installed -or (Test-UserKey ([Microsoft.Win32.RegistryView]::Registry64) ('Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $productCode))) {
        $uninstallLog = Join-Path $build ('msi-uninstall-' + [guid]::NewGuid().ToString('N') + '.log')
        $uninstallCode = Run-Installer @('/x', $productCode, '/qn', '/norestart', '/l*v', ('"' + $uninstallLog + '"'))
        if ($uninstallCode -ne 0) { throw "Windows Installer uninstall returned $uninstallCode" }
        if (Test-Path -LiteralPath (Join-Path $target 'AgBrowser.Desktop.exe')) { throw 'Executable remains after uninstall' }
        foreach ($view in $views) {
            if (Get-UserValue $view $registration 'agbrowser.desktop') { throw "Browser registration remains after uninstall ($view)" }
            foreach ($key in $ownedKeys) { if (Test-UserKey $view $key) { throw "Registry key remains after uninstall ($view): $key" } }
            if (Test-UserKey $view ('Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $productCode)) { throw "Uninstall entry remains ($view)" }
        }
        if (Test-Path -LiteralPath (Join-Path $menu 'agbrowser.lnk')) { throw 'Start menu shortcut remains after uninstall' }
        if (Test-Path -LiteralPath $menu) { throw 'Start menu directory remains after uninstall' }
        Write-Output 'PASS per-user MSI uninstall: executable, registry entries and Start menu directory absent'
    }
}
