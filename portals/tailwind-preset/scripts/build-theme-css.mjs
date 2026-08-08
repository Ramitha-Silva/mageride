/**
 * Emits `dist/theme.css` from the compiled tokens. Run by `npm run build`
 * after `tsc`, before the smoke page is compiled — the smoke build is the
 * proof that what this writes is a stylesheet Tailwind actually accepts.
 */
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { renderThemeCss } from '../dist/theme-css.js';

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const target = resolve(packageRoot, 'dist/theme.css');

await mkdir(dirname(target), { recursive: true });
await writeFile(target, renderThemeCss(), 'utf8');

process.stdout.write(`tailwind-preset: wrote ${target}\n`);
