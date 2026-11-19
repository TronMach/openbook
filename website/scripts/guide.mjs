// Copies the user guide (docs/user-guide, the same Markdown the app shows as help) into the site's content
// folder, and builds the sidebar from the guide's own README, so the guide is written in one place only.
//
// Each chapter's first heading becomes its page title, numbered file names become plain addresses
// ("04-sales.md" -> /guide/sales/), and links between chapters are rewritten to match. The copies under
// src/content/docs/guide are generated: never edit them, edit docs/user-guide instead.

import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const source = resolve(here, '../../docs/user-guide');
const target = resolve(here, '../src/content/docs/guide');
const repo = 'https://github.com/TronMach/openbook';

/** "04-sales.md" -> "sales" */
const slugOf = (file) => file.replace(/\.md$/, '').replace(/^\d+-/, '');

/** Rewrites one Markdown link target as seen from a page at /guide/<page>/ (or /guide/ when `fromIndex`). */
function rewriteLink(href, fromIndex) {
	if (/^[a-z]+:/i.test(href) || href.startsWith('#')) return href;
	const [path, hash = ''] = href.split('#');
	const anchor = hash ? `#${hash}` : '';
	const up = fromIndex ? '' : '../';
	if (path === 'README.md') return `${up || './'}${anchor}`;
	if (/^\d+-[a-z0-9-]+\.md$/.test(path)) return `${up}${slugOf(path)}/${anchor}`;
	// Anything else (the developer notes, the admin guides) lives in the repository, not on the site.
	const inRepo = join('docs/user-guide', path).replace(/\\/g, '/');
	return `${repo}/blob/master/${inRepo}${anchor}`;
}

/** Rewrites the links in Markdown text, leaving code spans and code blocks alone. */
function rewriteLinks(markdown, fromIndex) {
	return markdown
		.split(/(```[\s\S]*?```|`[^`\n]*`)/)
		.map((part, i) =>
			i % 2 === 1 ? part : part.replace(/\]\(([^)\s]+)\)/g, (_, href) => `](${rewriteLink(href, fromIndex)})`)
		)
		.join('');
}

/** Splits off the first "# Heading" line: returns [title, rest]. */
function takeTitle(markdown, file) {
	const match = markdown.match(/^#\s+(.+)\r?\n/m);
	if (!match) throw new Error(`docs/user-guide/${file} has no "# Title" line.`);
	return [match[1].trim(), markdown.slice(match.index + match[0].length).replace(/^\s+/, '')];
}

const frontmatter = (fields) =>
	'---\n' +
	Object.entries(fields)
		.filter(([, value]) => value !== undefined)
		.map(([key, value]) => `${key}: ${JSON.stringify(value)}`) // a JSON string is valid YAML
		.join('\n') +
	'\n---\n\n';

/** Reads the README's sections ("## Start here") and the chapters listed under each, in order. */
function readIndex(readme) {
	const groups = [];
	const described = new Map();
	for (const line of readme.split(/\r?\n/)) {
		const heading = line.match(/^##\s+(.+)/);
		if (heading) {
			groups.push({ label: heading[1].trim(), files: [] });
			continue;
		}
		const item = line.match(/^\d+[.)]\s+\[([^\]]+)\]\((\d+-[a-z0-9-]+\.md)\)(?::\s*(.+))?/);
		if (item && groups.length) {
			groups.at(-1).files.push({ file: item[2], label: item[1] });
			if (item[3]) described.set(item[2], item[3].replace(/\.$/, '').replace(/[*`]/g, ''));
		}
	}
	return { groups: groups.filter((g) => g.files.length), described };
}

function writeIfChanged(path, text) {
	if (existsSync(path) && readFileSync(path, 'utf8') === text) return;
	writeFileSync(path, text);
}

/** Copies the guide into the content folder and returns the sidebar for it. */
export function syncGuide() {
	mkdirSync(target, { recursive: true });
	const readme = readFileSync(join(source, 'README.md'), 'utf8');
	const { groups, described } = readIndex(readme);
	const chapters = readdirSync(source).filter((f) => /^\d+-.+\.md$/.test(f)).sort();

	const written = new Set(['index.md']);
	for (const file of chapters) {
		const [title, body] = takeTitle(readFileSync(join(source, file), 'utf8'), file);
		const description = described.get(file);
		const page = `${slugOf(file)}.md`;
		written.add(page);
		writeIfChanged(
			join(target, page),
			frontmatter({
				title,
				description: description && description[0].toUpperCase() + description.slice(1) + '.',
				editUrl: `${repo}/edit/master/docs/user-guide/${file}`,
			}) + rewriteLinks(body, false)
		);
	}

	const [, intro] = takeTitle(readme, 'README.md');
	writeIfChanged(
		join(target, 'index.md'),
		frontmatter({
			title: 'User guide',
			description: 'How to use OpenBook, area by area, written for the people who keep the books.',
			editUrl: `${repo}/edit/master/docs/user-guide/README.md`,
		}) + rewriteLinks(intro, true)
	);

	// Pages left from a chapter since renamed or removed.
	for (const old of readdirSync(target)) if (!written.has(old)) rmSync(join(target, old));

	// A chapter the README doesn't list still gets a place, so nothing written goes missing.
	const listed = new Set(groups.flatMap((g) => g.files.map((f) => f.file)));
	const unlisted = chapters.filter((f) => !listed.has(f));
	if (unlisted.length) {
		groups.push({ label: 'More', files: unlisted.map((file) => ({ file, label: slugOf(file) })) });
	}

	// A section of one chapter ("Coming from QuickBooks") is just that chapter, not a heading over its own name.
	const link = ({ file, label }) => ({ label, slug: `guide/${slugOf(file)}` });
	return [
		{ label: 'Contents', link: '/guide/' },
		...groups.map((group) =>
			group.files.length === 1 ? link(group.files[0]) : { label: group.label, items: group.files.map(link) }
		),
	];
}
