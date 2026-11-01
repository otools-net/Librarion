// The harness itself (as SFE): phone specs act with a finger, so a spec failing means the app, not the harness.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '../fixtures/test';

// A mouse call in a phone spec tests what a phone never does. A line meant for the desktop project says so with a
// `// mouse: <why>` comment.
test('no mouse in the specs, unless a line says why', async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'android', 'reads the files: once per run');
    const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
    const files = ['specs', 'fixtures'].flatMap(folder => fs.readdirSync(path.join(root, folder))
        .filter(name => name.endsWith('.ts')).map(name => path.join(folder, name)));
    const mouse = /\.(click|dblclick|hover)\(|mouse\.(click|down|up|move|dblclick|wheel)\(/;
    const found = files.flatMap(file => fs.readFileSync(path.join(root, file), 'utf8').split('\n')
        .map((line, index) => ({ line, at: `${file.replaceAll(path.sep, '/')}:${index + 1}` }))
        .filter(({ line }) => mouse.test(line) && !/\/\/ mouse: \S/.test(line) && !line.trim().startsWith('//') && !line.trim().startsWith('*'))
        .map(({ line, at }) => `${at} ${line.trim()}`));
    expect(found, 'mouse calls without a "// mouse: <why>" comment').toEqual([]);
});
