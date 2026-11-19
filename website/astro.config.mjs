// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import { syncGuide } from './scripts/guide.mjs';

// Served from GitHub Pages at https://tronmach.github.io/openbook/ until the project has a domain of its own;
// then set `site` to it and `base` to '/'.
const site = process.env.SITE_URL ?? 'https://tronmach.github.io';
const base = process.env.SITE_BASE ?? '/openbook';

export default defineConfig({
	site,
	base,
	trailingSlash: 'always',
	integrations: [
		starlight({
			title: 'OpenBook',
			description:
				'Open-source bookkeeping for small businesses and nonprofits, with your books in a file or database you control.',
			logo: { src: './src/assets/logo.png', alt: '' },
			favicon: '/favicon.png',
			head: [{ tag: 'link', attrs: { rel: 'apple-touch-icon', href: `${base}/apple-touch-icon.png` } }],
			social: [{ icon: 'github', label: 'OpenBook on GitHub', href: 'https://github.com/TronMach/openbook' }],
			customCss: ['@fontsource-variable/source-serif-4', './src/styles/site.css'],
			components: { Hero: './src/components/Hero.astro' },
			sidebar: syncGuide(),
			lastUpdated: false,
			credits: false,
		}),
	],
});
