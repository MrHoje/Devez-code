import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const root = dirname(dirname(fileURLToPath(import.meta.url)));
const { Terminal } = require(join(root, 'Resources', 'Terminal', 'web', 'xterm6.min.js'));
const terminal = new Terminal();
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

terminal.dispose();
console.log('PASS: bundled xterm6 emoji, CJK, and combining widths match Unicode 6.');
