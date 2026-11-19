// Checks the licence of every package the website installs (after `npm ci`), as tools/check-licences.ps1 does for
// OpenBook's NuGet packages: permissive licences pass, anything else fails unless it's listed in `buildOnly` below.
// The website's packages only build the site; none of them ships with OpenBook.

import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../node_modules');

const permissive = new Set([
	'MIT',
	'ISC',
	'Apache-2.0',
	'BSD-2-Clause',
	'BSD-3-Clause',
	'0BSD',
	'BlueOak-1.0.0',
	'CC0-1.0',
	'Unlicense',
	'Python-2.0',
	'OFL-1.1', // fonts
]);

// Not permissive, but GPL-compatible tools Astro builds with, whose output (HTML, CSS, images) carries none of their
// code; each has a package per platform. Decided 2026-11-19: allowed for the website only, never in src/.
const buildOnly = [
	// MPL-2.0: the CSS minifier Vite and Astro use
	/^lightningcss(-[a-z0-9-]+)?$/,
	// LGPL-3.0-or-later: libvips, inside sharp, which resizes the screenshots
	/^@img\/sharp-[a-z0-9-]+$/,
];

/** True when an SPDX expression is all permissive licences: "A OR B" needs one, "A AND B" needs both. */
function allowed(expression) {
	const text = expression.replace(/[()]/g, ' ').trim();
	if (/\sOR\s/.test(text)) return text.split(/\s+OR\s+/).some(allowed);
	return text.split(/\s+AND\s+/).every((id) => permissive.has(id.trim()));
}

const refused = [];
let count = 0;
(function walk(dir) {
	for (const name of readdirSync(dir)) {
		if (name === '.bin' || name.startsWith('.')) continue;
		const path = join(dir, name);
		if (name.startsWith('@')) {
			walk(path);
			continue;
		}
		const manifest = join(path, 'package.json');
		if (!existsSync(manifest)) continue;
		const pkg = JSON.parse(readFileSync(manifest, 'utf8'));
		const licence =
			typeof pkg.license === 'string'
				? pkg.license
				: pkg.license?.type ?? pkg.licenses?.map((l) => l.type).join(' OR ') ?? '(none)';
		count++;
		if (!allowed(licence) && !buildOnly.some((name) => name.test(pkg.name))) refused.push(`${pkg.name}@${pkg.version}: ${licence}`);
		if (existsSync(join(path, 'node_modules'))) walk(join(path, 'node_modules'));
	}
})(root);

if (refused.length) {
	console.error(`Licences the website can't take (or that need reading and listing in buildOnly):\n  ${refused.join('\n  ')}`);
	process.exit(1);
}
console.log(`Checked ${count} packages: every licence is permissive or listed as build-only.`);
