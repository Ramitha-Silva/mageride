/**
 * Emits `smoke/dist/index.html` — the page the Tailwind CLI then compiles a
 * stylesheet for. Runs after `build-theme-css.mjs`, before the CLI.
 */
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { renderSmokePage } from '../dist/smoke-page.js';

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const target = resolve(packageRoot, 'smoke/dist/index.html');

await mkdir(dirname(target), { recursive: true });
await writeFile(target, renderSmokePage(), 'utf8');

process.stdout.write(`tailwind-preset: wrote ${target}\n`);
