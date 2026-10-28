$ErrorActionPreference = 'Stop'
$id = [guid]::NewGuid().ToString('N')
$upgrade = [guid]::NewGuid().ToString('B')
$name = 'AgbProbe' + $id
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\build'))
$source = Join-Path $root ($name + '.wxs')
$msi = Join-Path $root ($name + '.msi')
$installLog = Join-Path $root ($name + '-install.log')
$uninstallLog = Join-Path $root ($name + '-uninstall.log')
$wix = Join-Path $root 'wix5\wix.exe'
$paths = @(('Software\Classes\' + $name + 'A'), ('Software\' + $name + 'B'), ('Software\Classes\' + $name + 'C'), ('Software\Classes\' + $name + 'D'), ('Software\Classes\' + $name + 'D\shell\open\command'))
$browserPath = 'Software\Classes\Applications\' + $name + '.exe'
$progidPath = 'Software\Classes\' + $name + 'HTML'
$views = @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)
function Get-Probe([Microsoft.Win32.RegistryView]$view, [string]$path, [string]$valueName = 'Probe') {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    try { $key = $base.OpenSubKey($path); if ($key) { try { return $key.GetValue($valueName) } finally { $key.Dispose() } }; return $null }
    finally { $base.Dispose() }
}
function Has-Key([Microsoft.Win32.RegistryView]$view, [string]$path) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    try { $key = $base.OpenSubKey($path); if ($key) { $key.Dispose(); return $true }; return $false }
    finally { $base.Dispose() }
}
foreach ($view in $views) { foreach ($path in $paths) { if (Has-Key $view $path) { throw 'Probe key already exists' } } }
foreach ($view in $views) { if ((Has-Key $view $progidPath) -or (Has-Key $view $browserPath) -or (Get-Probe $view 'Software\RegisteredApplications' $name)) { throw 'Browser fixture registration already exists' } }
$xml = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="$name" Manufacturer="agbrowser test" Version="1.0.0" UpgradeCode="$upgrade" Scope="perUser">
    <StandardDirectory Id="LocalAppDataFolder" />
    <ComponentGroup Id="Probes" Directory="LocalAppDataFolder">
      <Component Id="UserClasses"><RegistryValue Root="HKCU" Key="Software\Classes\${name}A" Name="Probe" Value="ok" Type="string" KeyPath="yes" /></Component>
      <Component Id="UserSoftware"><RegistryValue Root="HKCU" Key="Software\${name}B" Name="Probe" Value="ok" Type="string" KeyPath="yes" /></Component>
      <Component Id="MergedClasses"><RegistryValue Root="HKCR" Key="${name}C" Name="Probe" Value="ok" Type="string" KeyPath="yes" /></Component>
      <Component Id="NestedClasses"><RegistryKey Root="HKCU" Key="Software\Classes\${name}D"><RegistryValue Type="string" Value="description" /><RegistryValue Name="Probe" Type="string" Value="ok" /><RegistryKey Key="shell\open\command"><RegistryValue Type="string" Value="command" /><RegistryValue Name="Probe" Type="string" Value="ok" /></RegistryKey></RegistryKey></Component>
      <Component Id="BrowserProgId"><RegistryKey Root="HKCU" Key="Software\Classes\${name}HTML"><RegistryValue Type="string" Value="test HTML Document" /><RegistryValue Name="URL Protocol" Type="string" Value="" /><RegistryKey Key="DefaultIcon"><RegistryValue Type="string" Value="test.exe,0" /></RegistryKey><RegistryKey Key="shell\open\command"><RegistryValue Type="string" Value="test.exe &quot;%1&quot;" /></RegistryKey></RegistryKey></Component>
      <Component Id="BrowserRegistration"><RegistryValue Root="HKCU" Key="Software\RegisteredApplications" Name="$name" Type="string" Value="Software\Classes\Applications\${name}.exe\Capabilities" KeyPath="yes" /></Component>
      <Component Id="BrowserApplication"><RegistryKey Root="HKCU" Key="Software\Classes\Applications\${name}.exe"><RegistryValue Name="Path" Type="string" Value="test" /><RegistryKey Key="shell\open\command"><RegistryValue Type="string" Value="test.exe &quot;%1&quot;" /></RegistryKey><RegistryKey Key="Capabilities"><RegistryValue Name="ApplicationName" Type="string" Value="$name" /><RegistryKey Key="URLAssociations"><RegistryValue Name="http" Type="string" Value="${name}HTML" /><RegistryValue Name="https" Type="string" Value="${name}HTML" /></RegistryKey></RegistryKey></RegistryKey></Component>
    </ComponentGroup>
    <Feature Id="Main"><ComponentGroupRef Id="Probes" /></Feature>
  </Package>
</Wix>
"@
[IO.File]::WriteAllText($source, $xml, [Text.UTF8Encoding]::new($false))
& $wix build -arch x64 -o $msi $source
if ($LASTEXITCODE -ne 0) { throw "Fixture build failed: $LASTEXITCODE" }
$installed = $false
try {
    $p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList @('/i', ('"' + $msi + '"'), '/qn', '/norestart', '/l*v', ('"' + $installLog + '"')) -WindowStyle Hidden -PassThru -Wait
    $installed = $p.ExitCode -in @(0, 3010)
    if ($p.ExitCode -ne 0) { throw "Fixture install returned $($p.ExitCode)" }
    foreach ($view in $views) { foreach ($path in $paths) { if ((Get-Probe $view $path) -ne 'ok') { throw "Fixture value missing: $view $path" } } }
    $view64 = [Microsoft.Win32.RegistryView]::Registry64
    if ((Get-Probe $view64 $paths[4] '') -ne 'command') { throw 'Nested default command missing' }
    if ((Get-Probe $view64 ($progidPath + '\shell\open\command') '') -ne 'test.exe "%1"') { throw 'Browser ProgID command missing' }
    if ((Get-Probe $view64 ($browserPath + '\Capabilities') 'ApplicationName') -ne $name) { throw 'Browser application capabilities missing' }
    if ((Get-Probe $view64 'Software\RegisteredApplications' $name) -ne ('Software\Classes\Applications\' + $name + '.exe\Capabilities')) { throw 'Registered application missing' }
    Write-Output 'PASS temporary MSI registry fixture: ordinary keys, nested classes and browser registration read back'
} finally {
    if ($installed) {
        $p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList @('/x', ('"' + $msi + '"'), '/qn', '/norestart', '/l*v', ('"' + $uninstallLog + '"')) -WindowStyle Hidden -PassThru -Wait
        if ($p.ExitCode -ne 0) { throw "Fixture uninstall returned $($p.ExitCode)" }
        foreach ($view in $views) { foreach ($path in $paths) { if (Has-Key $view $path) { throw "Fixture cleanup failed: $view $path" } } }
        foreach ($view in $views) {
            if ((Has-Key $view $progidPath) -or (Has-Key $view $browserPath) -or (Get-Probe $view 'Software\RegisteredApplications' $name)) { throw "Browser fixture cleanup failed: $view" }
        }
        Write-Output 'PASS temporary MSI registry fixture uninstall removed its keys and registration'
    }
}
Write-Output "Fixture logs: $installLog ; $uninstallLog"
