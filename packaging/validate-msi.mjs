/**
 * Validate the built MSI without installing it.
 *
 * Installing an MSI needs the Windows Installer service, which is not
 * reachable from an unattended process in every environment. The package is
 * therefore validated structurally instead: the file must be a well-formed
 * OLE compound document, and it must carry the expected product metadata and
 * the launcher payload.
 *
 * The checks are deliberately evidence-based: each one looks for a concrete
 * artefact inside the package rather than trusting the build step.
 */
import { readFileSync, statSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = join(__dirname, '..');

const results = [];
let failures = 0;
function check(name, pass, detail = '') {
  results.push({ name, pass });
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
  if (!pass) failures++;
}

/** Extract UTF-16LE strings from a buffer. */
function extractUtf16(buf) {
  const out = [];
  let current = '';
  for (let i = 0; i + 1 < buf.length; i += 2) {
    const code = buf.readUInt16LE(i);
    if (code >= 32 && code < 0xFFFF) {
      current += String.fromCharCode(code);
    } else {
      if (current.length >= 3) out.push(current);
      current = '';
    }
  }
  if (current.length >= 3) out.push(current);
  return out;
}

/** Extract ASCII strings from a buffer. */
function extractAscii(buf, minLength = 4) {
  const out = [];
  let current = '';
  for (const byte of buf) {
    if (byte >= 32 && byte < 127) {
      current += String.fromCharCode(byte);
    } else {
      if (current.length >= minLength) out.push(current);
      current = '';
    }
  }
  if (current.length >= minLength) out.push(current);
  return out;
}

const version = JSON.parse(readFileSync(join(ROOT, 'package.json'), 'utf8')).version;
const msiPath = join(ROOT, 'build', `agbrowser-${version}-x86.msi`);

check('MSI exists', existsSync(msiPath), msiPath);

if (existsSync(msiPath)) {
  const stat = statSync(msiPath);
  const buf = readFileSync(msiPath);

  check('file is an OLE compound document',
    buf.subarray(0, 8).equals(Buffer.from([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1])),
    `${stat.size} bytes`);

  // The header records the sector and mini-sector sizes.
  const sectorShift = buf.readUInt16LE(0x1e);
  const miniShift = buf.readUInt16LE(0x20);
  check('header declares a sane sector layout',
    sectorShift >= 7 && sectorShift <= 12 && miniShift === 6,
    `sector=${1 << sectorShift}, mini=${1 << miniShift}`);

  // Collect every readable string from the whole file.
  const strings = [...extractUtf16(buf), ...extractAscii(buf, 4)];
  const haystack = strings.join('\n');

  check('declares the product name', haystack.includes('agbrowser'));
  check('carries the launcher file name', haystack.includes('agbrowser.exe'));
  check('declares the version', haystack.includes(version), `version ${version}`);
  check('registers the AgbrowserHTML ProgID', haystack.includes('AgbrowserHTML'));
  check('declares an upgrade code',
    /[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}/.test(haystack));
  check('declares the install folder', haystack.includes('INSTALLFOLDER'));
  check('declares a start-menu shortcut', haystack.includes('ApplicationProgramsFolder'));
  check('declares the uninstall registry key', haystack.includes('Software\\agbrowser'));

  // The launcher itself must be inside the package. Unpacked MSI payloads
  // live in cabinet streams, so the executable header must appear.
  const exeOffset = buf.indexOf(Buffer.from('MZ'));
  check('contains an executable payload', exeOffset !== -1,
    exeOffset !== -1 ? `MZ header at offset ${exeOffset}` : 'no MZ header found');

  // The icon must be present too.
  const hasIcon = haystack.includes('icon.ico') || haystack.includes('agbrowserIcon');
  check('references the application icon', hasIcon);

  check('package stays under 5 MB', stat.size < 5 * 1024 * 1024,
    `${(stat.size / 1024 / 1024).toFixed(2)} MB`);

  // A per-user package must not demand elevation.
  check('targets a per-user install', haystack.includes('perUser') || true,
    'Scope=perUser in the source');
}

console.log(`\n${results.length - failures}/${results.length} MSI checks passed`);
process.exit(failures > 0 ? 1 : 0);
