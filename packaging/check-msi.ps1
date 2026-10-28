param([Parameter(Mandatory=$true)][string]$Msi, [Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference = 'Stop'
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$installer,@([IO.Path]::GetFullPath($Msi), 0))
function Read-Rows([string]$query) {
  $view = $db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@($query))
  $view.GetType().InvokeMember('Execute','InvokeMethod',$null,$view,$null) | Out-Null
  while ($row = $view.GetType().InvokeMember('Fetch','InvokeMethod',$null,$view,$null)) {
    $count = $row.GetType().InvokeMember('FieldCount','GetProperty',$null,$row,$null)
    $values = for ($i=1; $i -le $count; $i++) { $row.GetType().InvokeMember('StringData','GetProperty',$null,$row,@($i)) }
    [pscustomobject]@{ Fields = @($values) }
  }
}
$files = @(Read-Rows 'SELECT `FileName` FROM `File`')
$properties = @(Read-Rows 'SELECT `Property`, `Value` FROM `Property`')
$registry = @(Read-Rows 'SELECT `Key`, `Name`, `Value` FROM `Registry`')
$upgrades = @(Read-Rows 'SELECT `UpgradeCode`, `VersionMax`, `Attributes`, `ActionProperty` FROM `Upgrade`')
$directories = @(Read-Rows 'SELECT `Directory`, `DefaultDir` FROM `Directory`')
if (-not ($files | Where-Object { $_.Fields[0] -match '(^|\|)AgBrowser\.Desktop\.exe$' })) { throw 'desktop executable missing from File table' }
if (-not ($properties | Where-Object { $_.Fields[0] -eq 'ProductVersion' -and $_.Fields[1] -eq $Version })) { throw 'incorrect product version' }
if (-not ($registry | Where-Object { $_.Fields[0] -eq 'Software\RegisteredApplications' -and $_.Fields[1] -eq 'agbrowser.desktop' })) { throw 'Default Apps registration missing' }
if (-not ($registry | Where-Object { $_.Fields[0] -eq 'Software\Classes\Agbrowser.Document\shell\open\command' -and $_.Fields[2] -match 'AgBrowser\.Desktop\.exe.*"%1"' })) { throw 'ProgID command missing or URL unquoted' }
foreach ($extension in @('.htm', '.html')) {
  if (-not ($registry | Where-Object { $_.Fields[0] -eq 'Software\Classes\Applications\AgBrowser.Desktop.exe\Capabilities\FileAssociations' -and $_.Fields[1] -eq $extension -and $_.Fields[2] -eq 'Agbrowser.Document' })) { throw "missing $extension capability" }
}
foreach ($scheme in @('http', 'https')) {
  if (-not ($registry | Where-Object { $_.Fields[0] -eq 'Software\Classes\Applications\AgBrowser.Desktop.exe\Capabilities\URLAssociations' -and $_.Fields[1] -eq $scheme -and $_.Fields[2] -eq 'Agbrowser.Document' })) { throw "missing $scheme capability" }
}
if ($registry | Where-Object { $_.Fields[0] -match 'UserChoice|Software\\Classes\\https?\\shell' }) { throw 'unsafe default or legacy URL association' }
if ($upgrades | Where-Object { $_.Fields[0] -eq '{7C0F2A64-9B1E-4E52-9E2D-5A3F1B8D6C40}' }) { throw 'MSI must not upgrade the legacy product' }
if (-not ($properties | Where-Object { $_.Fields[0] -eq 'UpgradeCode' -and $_.Fields[1] -eq '{B354E2D2-C816-4961-8F6D-A5CEAB83A219}' })) { throw 'desktop upgrade identity missing' }
if (-not ($directories | Where-Object { $_.Fields[0] -eq 'INSTALLFOLDER' -and $_.Fields[1] -match '(^|\|)agbrowser-desktop$' })) { throw 'desktop install directory is not isolated' }
if (-not ($directories | Where-Object { $_.Fields[0] -eq 'ApplicationProgramsFolder' -and $_.Fields[1] -match '(^|\|)agbrowser Desktop$' })) { throw 'desktop start menu directory is not isolated' }
Write-Output 'PASS Windows Installer tables: desktop executable, version, application registration and quoted URL handler'
