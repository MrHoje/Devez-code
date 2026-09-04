import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const root = dirname(dirname(fileURLToPath(import.meta.url)));
const { Terminal } = require(join(root, 'Resources', 'Terminal', 'web', 'xterm6.min.js'));
const devezWidth = require(join(root, 'Resources', 'Terminal', 'web', 'devez-unicode-width.js'));
const terminal = new Terminal({ allowProposedApi: true });
const unicode = terminal._core?.unicodeService;

if (!unicode || unicode.activeVersion !== '6') {
  throw new Error(`xterm Unicode 6 provider is not active: ${unicode?.activeVersion}`);
}

const actual = ['🐾', '👩‍💻', '🇰🇷', '가', '\u0301', '\u1ab0', '\u{1d167}']
  .map(text => unicode.getStringCellWidth(text));
const expected = [1, 2, 2, 2, 0, 1, 0];
if (JSON.stringify(actual) !== JSON.stringify(expected)) {
  throw new Error(`xterm width mismatch: ${JSON.stringify(actual)}`);
}

if (!devezWidth.install(terminal, 'devezvibe')) {
  throw new Error('Devez Vibe width provider was not installed.');
}
const adjusted = ['🐾', '👩‍💻', '🇰🇷', '가', '\u0301', '\u1ab0', '\u{1d167}']
  .map(text => unicode.getStringCellWidth(text));
const adjustedExpected = [2, 2, 2, 2, 0, 1, 0];
if (unicode.activeVersion !== devezWidth.version ||
    JSON.stringify(adjusted) !== JSON.stringify(adjustedExpected)) {
  throw new Error(`Devez Vibe width mismatch: ${unicode.activeVersion} ${JSON.stringify(adjusted)}`);
}
await new Promise(resolve => terminal.write('A🐾B', resolve));
const line = terminal.buffer.active.getLine(0);
if (!line || line.getCell(1)?.getChars() !== '🐾' || line.getCell(1)?.getWidth() !== 2 ||
    line.getCell(2)?.getWidth() !== 0 || line.getCell(3)?.getChars() !== 'B') {
  throw new Error('Devez Vibe xterm buffer did not reserve the paw print continuation cell.');
}

terminal.dispose();
console.log('PASS: Devez Vibe paw-print width matches its renderer without changing other Unicode 6 widths.');
