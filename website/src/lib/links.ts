/** A path on this site, with the base it's served under (/openbook/ on GitHub Pages). */
export const local = (path: string) => `${import.meta.env.BASE_URL.replace(/\/$/, '')}${path}`;

export const repo = 'https://github.com/TronMach/openbook';
export const releases = `${repo}/releases`;
export const latestRelease = `${releases}/latest`;
export const inRepo = (path: string) => `${repo}/blob/master/${path}`;
