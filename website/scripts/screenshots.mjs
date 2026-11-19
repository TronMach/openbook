// Takes the homepage's screenshots from a running OpenBook with a demo company, in light and dark.
//
//   dotnet run --project src/OpenBook.Web -- make-demo --ConnectionStrings:OpenBook="Data Source=<new file>" --years 2 --invoices 60 --password <pw>
//   dotnet run --project src/OpenBook.Web -- --urls http://localhost:5190 --ConnectionStrings:OpenBook="Data Source=<that file>"
//   OPENBOOK_PASSWORD=<pw> npm run screenshots            (or OPENBOOK_PASSWORD_FILE=<file>)
//
// Uses the installed Edge (or Chrome with OPENBOOK_BROWSER=chrome); downloads no browser. Only ever point it at a
// demo company: the pictures are published.

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const here = dirname(fileURLToPath(import.meta.url));
const out = resolve(here, '../src/assets/screens');
const url = (process.env.OPENBOOK_URL ?? 'http://localhost:5190').replace(/\/$/, '');
const user = process.env.OPENBOOK_USER ?? 'owner';
const password =
	process.env.OPENBOOK_PASSWORD ??
	(process.env.OPENBOOK_PASSWORD_FILE && readFileSync(process.env.OPENBOOK_PASSWORD_FILE, 'utf8').trim());
if (!password) throw new Error('Set OPENBOOK_PASSWORD or OPENBOOK_PASSWORD_FILE to the demo owner\'s password.');

// name -> page (`path`, or the first link matching `pick` on page `from`) and window size. The homepage shows
// invoice and pnl half width, so they're taken in a narrower window, from below the menu bar (`belowMenu`, the
// picture's height), to keep their text readable there. A run can name some: npm run screenshots -- dashboard pnl
const invoiceLink = 'main a[href^="/sales/invoices/"]:not([href$="/new"]):not([href*="batch"])';
const wide = { width: 1280, height: 800 };
const half = { width: 980, height: 900 };
const shots = {
	dashboard: { path: '/dashboard', size: wide },
	invoice: { from: '/sales', pick: invoiceLink, size: half, belowMenu: 620 },
	pnl: { path: '/reports/profit-and-loss', size: half, belowMenu: 620 },
};
const only = process.argv.slice(2);

const browser = await chromium.launch({ channel: process.env.OPENBOOK_BROWSER === 'chrome' ? 'chrome' : 'msedge' });
try {
	for (const theme of ['light', 'dark']) {
		const context = await browser.newContext({
			viewport: { width: 1280, height: 800 },
			deviceScaleFactor: 1.5,
			colorScheme: theme,
		});
		await context.addInitScript((t) => localStorage.setItem('ob-theme', t), theme);
		const page = await context.newPage();
		await page.goto(`${url}/login`);
		await page.fill('#username', user);
		await page.fill('#password', password);
		await Promise.all([page.waitForURL((u) => !u.pathname.startsWith('/login')), page.click('form button[type=submit]')]);

		for (const [name, shot] of Object.entries(shots)) {
			if (only.length && !only.includes(name)) continue;
			await page.setViewportSize(shot.size);
			let path = shot.path;
			if (!path) {
				await page.goto(`${url}${shot.from}`, { waitUntil: 'networkidle' });
				path = await page.locator(shot.pick).first().getAttribute('href');
			}
			await page.goto(`${url}${path}`, { waitUntil: 'networkidle' });
			let clip;
			if (shot.belowMenu) {
				const top = await page.evaluate(() => document.querySelector('main')?.getBoundingClientRect().top ?? 0);
				clip = { x: 0, y: top, width: shot.size.width, height: shot.belowMenu };
			}
			await page.screenshot({ path: `${out}/${name}-${theme}.png`, clip });
			console.log(`${name}-${theme}.png  ${path}`);
		}
		await context.close();
	}
} finally {
	await browser.close();
}
