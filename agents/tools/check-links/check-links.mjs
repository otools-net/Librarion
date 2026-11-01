// Relative links in AGENTS.md, README.md, agents/**/*.md, docs/**/*.md and tests/e2e/README.md point to files that
// exist (anchors are not checked). Exit 1 and a list when some don't. No packages: node agents/tools/check-links/check-links.mjs
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');

function markdownFiles(folder) {
    const full = path.join(repo, folder);
    if (!fs.existsSync(full))
        return [];
    return fs.readdirSync(full, { recursive: true, withFileTypes: true })
        .filter(entry => entry.isFile() && entry.name.endsWith('.md') && !entry.parentPath.includes('node_modules'))
        .map(entry => path.join(entry.parentPath, entry.name));
}

const files = [
    path.join(repo, 'AGENTS.md'),
    path.join(repo, 'README.md'),
    path.join(repo, 'tests/e2e/README.md'),
    ...markdownFiles('agents'),
    ...markdownFiles('docs'),
].filter(file => fs.existsSync(file));

const broken = [];
for (const file of files) {
    const text = fs.readFileSync(file, 'utf8');
    for (const match of text.matchAll(/\]\(([^)\s]+)\)/g)) {
        const target = match[1].split('#')[0];
        if (!target || /^[a-z]+:/i.test(target))
            continue;
        if (!fs.existsSync(path.resolve(path.dirname(file), decodeURIComponent(target))))
            broken.push(`${path.relative(repo, file)} -> ${match[1]}`);
    }
}

if (broken.length) {
    console.error(`Broken links (${broken.length}):\n` + broken.join('\n'));
    process.exit(1);
}
console.log(`Links fine in ${files.length} files.`);
