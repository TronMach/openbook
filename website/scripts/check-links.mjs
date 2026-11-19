// Checks every link inside the built site (dist/): the page exists, and so does the heading or id it points at.
// Run after `npm run build`; exits 1 when a link is broken. Links to other sites aren't fetched.

import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, posix, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const dist = resolve(dirname(fileURLToPath(import.meta.url)), '../dist');
const base = (process.env.SITE_BASE ?? '/openbook').replace(/\/$/, '');

const pages = [];
(function walk(dir) {
	for (const name of readdirSync(dir)) {
		const path = join(dir, name);
		if (statSync(path).isDirectory()) walk(path);
		else if (name.endsWith('.html')) pages.push(path);
	}
})(dist);

const ids = new Map();
const idsOf = (file) => {
	if (!ids.has(file)) ids.set(file, new Set([...readFileSync(file, 'utf8').matchAll(/\sid="([^"]+)"/g)].map((m) => m[1])));
	return ids.get(file);
};

const broken = [];
for (const page of pages) {
	const html = readFileSync(page, 'utf8');
	const at = '/' + relative(dist, page).replace(/\\/g, '/').replace(/index\.html$/, '');
	for (const [, raw] of html.matchAll(/<a\s[^>]*href="([^"]+)"/g)) {
		const href = raw.replace(/&amp;/g, '&');
		if (/^[a-z]+:/i.test(href) || href.startsWith('//')) continue;
		const [path, hash] = href.split('#');
		let target = path === '' ? at : posix.join(at, path);
		if (path.startsWith('/')) {
			if (!path.startsWith(base + '/')) {
				broken.push(`${at}: ${href} (outside ${base}/)`);
				continue;
			}
			target = path.slice(base.length);
		}
		const file = join(dist, decodeURI(target), target.endsWith('/') ? 'index.html' : '');
		if (!existsSync(file) || statSync(file).isDirectory()) {
			broken.push(`${at}: ${href} (no page)`);
			continue;
		}
		if (hash && !idsOf(file).has(decodeURIComponent(hash))) broken.push(`${at}: ${href} (no #${hash} there)`);
	}
}

if (broken.length) {
	console.error(`${broken.length} broken link(s):\n  ${broken.join('\n  ')}`);
	process.exit(1);
}
console.log(`Checked the links of ${pages.length} pages: none broken.`);
