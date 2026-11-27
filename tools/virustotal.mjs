// Scans a release's installer with VirusTotal, for the Virus scan workflow (.github/workflows/virustotal.yml).
// Sends the file (or asks for a fresh scan of one VirusTotal already has), waits for the result, writes it to a small
// JSON file that the website reads (website/src/lib/release.ts), and puts it in the release's notes. Counts only: what
// an engine called the file is on VirusTotal's own report. No packages; needs Node 20 or later.
//
//   VIRUSTOTAL_API_KEY=… node tools/virustotal.mjs --file OpenBook-Setup-1.2.0.exe --out virustotal.json [--notes notes.md]
//
// The notes file is the release's notes: the lines from an earlier scan are replaced, or new ones added at the end.
// VIRUSTOTAL_API (another address) and --poll (seconds between looks) are for trying the script without VirusTotal.

import { createHash } from 'node:crypto';
import { createReadStream, openAsBlob, readFileSync, writeFileSync } from 'node:fs';
import { basename } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { parseArgs } from 'node:util';

const { values: options } = parseArgs({
	options: {
		file: { type: 'string' },
		out: { type: 'string' },
		notes: { type: 'string' },
		poll: { type: 'string', default: '30' },
	},
});
const key = process.env.VIRUSTOTAL_API_KEY;
if (!options.file || !options.out) throw new Error('Give --file and --out.');
if (!key) throw new Error('VIRUSTOTAL_API_KEY is not set.');

const api = process.env.VIRUSTOTAL_API ?? 'https://www.virustotal.com/api/v3';
// The free API allows 4 requests a minute, so one look every 30 seconds leaves room for the rest.
const poll = Number(options.poll) * 1000;
const longest = 45 * 60_000;

/** One call: the answer's JSON, or null when VirusTotal doesn't know what was asked for. Waits out limits and outages. */
async function call(path, { method = 'GET', form } = {}) {
	for (let attempt = 1; ; attempt++) {
		let response;
		try {
			response = await fetch(path.startsWith('http') ? path : `${api}${path}`, {
				method,
				headers: { 'x-apikey': key, accept: 'application/json' },
				// A form is sent once per attempt, so it's made anew each time.
				body: form ? await form() : undefined,
				// The key must never follow a redirect somewhere else.
				redirect: 'error',
			});
		} catch (error) {
			if (attempt >= 5) throw error;
			console.log(`No answer from VirusTotal (${error.cause?.code ?? error.message}); trying again.`);
			await sleep(poll * 2);
			continue;
		}
		if (response.ok) return response.json();
		const text = (await response.text()).slice(0, 300);
		if (response.status === 404) return null;
		if ((response.status !== 429 && response.status < 500) || attempt >= 5) {
			throw new Error(`VirusTotal answered ${response.status}: ${text}`);
		}
		console.log(`VirusTotal answered ${response.status}; trying again.`);
		await sleep(poll * 2);
	}
}

async function sha256(file) {
	const hash = createHash('sha256');
	for await (const chunk of createReadStream(file)) hash.update(chunk);
	return hash.digest('hex');
}

/** Sends the file and returns the analysis to wait for. Files over 32 MB go to an address VirusTotal hands out. */
async function upload(file) {
	const target = new URL((await call('/files/upload_url')).data);
	// The key goes along, so only over HTTPS and only to VirusTotal.
	if (!process.env.VIRUSTOTAL_API) {
		target.protocol = 'https:';
		if (!/(^|\.)virustotal\.com$/.test(target.hostname)) {
			throw new Error(`Unexpected upload address: ${target.hostname}`);
		}
	}
	const sent = await call(target.href, {
		method: 'POST',
		form: async () => {
			const form = new FormData();
			form.append('file', await openAsBlob(file), basename(file));
			return form;
		},
	});
	return sent?.data?.id;
}

const name = basename(options.file);
const hash = await sha256(options.file);
const report = `https://www.virustotal.com/gui/file/${hash}`;

const known = await call(`/files/${hash}`);
console.log(known ? `VirusTotal has ${name} (${hash}): asking for a fresh scan.` : `Sending ${name} (${hash}).`);
const id = known
	? (await call(`/files/${hash}/analyse`, { method: 'POST' }))?.data?.id
	: await upload(options.file);
if (!id) throw new Error('VirusTotal gave no analysis to wait for.');

let result;
for (const started = Date.now(); ; ) {
	result = (await call(`/analyses/${encodeURIComponent(id)}`))?.data?.attributes;
	if (result?.status === 'completed') break;
	if (Date.now() - started > longest) throw new Error(`The scan didn't finish in 45 minutes; see ${report}`);
	await sleep(poll);
}

// Engines that couldn't read the file, or ran out of time, gave no verdict and aren't counted.
const count = (kind) => {
	const value = result.stats?.[kind] ?? 0;
	if (!Number.isInteger(value) || value < 0) throw new Error(`Unexpected count for ${kind}.`);
	return value;
};
const flagged = count('malicious') + count('suspicious');
const engines = flagged + count('undetected') + count('harmless');
if (engines === 0) throw new Error(`No engine gave a verdict; see ${report}`);

const scanned = new Date((result.date ?? Date.now() / 1000) * 1000).toISOString().slice(0, 10);
writeFileSync(
	options.out,
	JSON.stringify({ file: name, sha256: hash, scanned, engines, flagged, report }, null, 2) + '\n',
);

const verdict = `${flagged === 0 ? 'none' : flagged} of ${engines} antivirus engines flagged`;
if (options.notes) {
	const block = [
		'<!-- virustotal -->',
		`**Virus scan:** ${verdict} \`${name}\` on ${scanned} ([VirusTotal report](${report})).`,
		'',
		`SHA-256: \`${hash}\``,
		'<!-- /virustotal -->',
	].join('\n');
	const notes = readFileSync(options.notes, 'utf8');
	const before = notes.replace(/\s*<!-- virustotal -->[\s\S]*?<!-- \/virustotal -->/g, '');
	writeFileSync(options.notes, `${before.trimEnd()}\n\n${block}\n`);
}
console.log(`Scanned ${scanned}: ${verdict} it. ${report}`);
