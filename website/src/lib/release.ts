// The newest release's Windows installer, read from GitHub when the site is built: its version, its SHA-256 and, once the
// Virus scan workflow has run (.github/workflows/virustotal.yml), what VirusTotal found. That workflow publishes the site
// again after each release. Without an answer from GitHub the site still builds and says less.

const api = 'https://api.github.com/repos/TronMach/openbook/releases/latest';

export interface Installer {
	version: string;
	file: string;
	sha256: string;
	page: string;
	scan?: { scanned: Date; engines: number; flagged: number; report: string };
}

interface Asset {
	name: string;
	digest?: string | null;
	browser_download_url: string;
}

async function json(url: string, headers: Record<string, string> = {}) {
	const response = await fetch(url, { headers, signal: AbortSignal.timeout(20_000) });
	if (!response.ok) throw new Error(`${url} answered ${response.status}`);
	return response.json();
}

async function scanOf(assets: Asset[], sha256: string): Promise<Installer['scan']> {
	const asset = assets.find((a) => a.name === 'virustotal.json');
	if (!asset) return undefined;
	const scan = await json(asset.browser_download_url);
	const count = (value: unknown) => typeof value === 'number' && Number.isInteger(value) && value >= 0;
	// A scan of any other file says nothing about this one.
	if (scan.sha256 !== sha256) throw new Error('virustotal.json is about another file');
	if (!count(scan.engines) || scan.engines === 0 || !count(scan.flagged) || !/^\d{4}-\d\d-\d\d$/.test(scan.scanned)) {
		throw new Error('virustotal.json has no result');
	}
	return {
		scanned: new Date(`${scan.scanned}T00:00:00Z`),
		engines: scan.engines,
		flagged: scan.flagged,
		report: `https://www.virustotal.com/gui/file/${sha256}`,
	};
}

async function read(): Promise<Installer | undefined> {
	try {
		// On GitHub's runners the workflow's token, since their shared addresses run out of anonymous calls.
		const token = process.env.GITHUB_TOKEN;
		const release = await json(api, token ? { authorization: `Bearer ${token}` } : {});
		const assets: Asset[] = release.assets;
		const setup = assets.find((a) => /^OpenBook-Setup-\d+\.\d+\.\d+\.exe$/.test(a.name));
		const sha256 = setup?.digest?.match(/^sha256:([0-9a-f]{64})$/)?.[1];
		if (!setup || !sha256) throw new Error('the latest release has no installer with a SHA-256');
		const scan = await scanOf(assets, sha256).catch((error) => {
			console.warn(`No virus scan on the site: ${error.message}`);
			return undefined;
		});
		return {
			version: setup.name.slice('OpenBook-Setup-'.length, -'.exe'.length),
			file: setup.name,
			sha256,
			page: `https://github.com/TronMach/openbook/releases/tag/${encodeURIComponent(release.tag_name)}`,
			scan,
		};
	} catch (error) {
		console.warn(`No release on the site: ${(error as Error).message}`);
		return undefined;
	}
}

let installer: ReturnType<typeof read> | undefined;
export const latestInstaller = () => (installer ??= read());
