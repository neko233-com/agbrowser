import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const build = join(root, 'build');
const version = process.env.AGB_VERSION ?? JSON.parse(readFileSync(join(root, 'package.json'), 'utf8')).version;
const desktopOutput = process.env.AGB_DESKTOP_OUTPUT ?? join(build, 'desktop');
const wix = process.env.AGB_WIX ?? join(build, 'wix5', 'wix.exe');
const wixPdbType = process.env.AGB_WIX_PDB === '1' ? 'full' : 'none';
if (!existsSync(wix)) throw new Error('WiX 5.0.2 is required: dotnet tool install wix --version 5.0.2 --tool-path build/wix5');
mkdirSync(build, { recursive: true });
const run = (command, args) => execFileSync(command, args, { cwd: root, stdio: 'inherit' });
console.log('Publishing the self-contained Windows desktop app...');
run('dotnet', ['publish', 'packages/desktop/AgBrowser.Desktop.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', `-p:Version=${version}`, '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', desktopOutput]);
const license = join(build, 'license.rtf');
writeFileSync(license, '{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0\\fnil Segoe UI;}}\\fs18 agbrowser.\\par\\par This software is provided under the MIT licence. See LICENSE for the full text.\\par}');
const extension = join(root, '.wix', 'extensions', 'WixToolset.UI.wixext', '5.0.2', 'wixext5', 'WixToolset.UI.wixext.dll');
if (!existsSync(extension)) {
  console.log('Installing WiX UI extension into the workspace cache...');
  run(wix, ['extension', 'add', 'WixToolset.UI.wixext/5.0.2']);
}
const output = join(build, `agbrowser-${version}-x64.msi`);
run(wix, ['build', '-arch', 'x64', '-pdbtype', wixPdbType, '-d', `Version=${version}`, '-d', `LauncherExe=${join(desktopOutput, 'AgBrowser.Desktop.exe')}`, '-d', `IconFile=${join(root, 'packages', 'launcher', 'assets', 'icon.ico')}`, '-d', `LicenseRtf=${license}`, '-ext', 'WixToolset.UI.wixext', '-o', output, join(root, 'packaging', 'agbrowser.wxs')]);
console.log(`Built ${output} (${(statSync(output).size / 1024 / 1024).toFixed(2)} MiB)`);
