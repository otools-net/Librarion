// axe-core's WCAG 2.2 A and AA rules on the screen shown (as SFE): serious and critical findings fail, moderate and
// minor ones are listed in the report.
import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { expect, test } from './test';

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

export async function audit(page: Page, screen: string): Promise<void> {
    const { violations } = await new AxeBuilder({ page }).withTags(TAGS).analyze();
    const line = (v: (typeof violations)[number]) =>
        `${v.id} (${v.impact}): ${v.help} — ${v.nodes.slice(0, 4).map(node => node.target.join(' ')).join(' | ')}`;
    const minor = violations.filter(v => v.impact !== 'serious' && v.impact !== 'critical');
    if (minor.length)
        test.info().annotations.push({ type: `a11y ${screen}`, description: minor.map(line).join('\n') });
    expect(violations.filter(v => v.impact === 'serious' || v.impact === 'critical').map(line), `${screen}: serious or critical`).toEqual([]);
}
