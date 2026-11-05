import en from '../../i18n/en.json';
import { ADMIN_SECTIONS, groupsOf, movedPage, visibleSections } from './admin-sections';

describe('admin-sections', () => {
    const texts = en as Record<string, string>;

    it('has the texts of every group, section and sub-page (a section of one sub-page uses its own)', () => {
        const missing: string[] = [];
        for (const section of ADMIN_SECTIONS) {
            for (const key of [`adminGroup.${section.group}`, `adminSection.${section.id}`, `adminSection.${section.id}.hint`])
                if (!texts[key])
                    missing.push(key);
            if (section.subpages.length > 1)
                for (const page of section.subpages)
                    for (const key of [`adminPage.${section.id}.${page.id}`, `adminPage.${section.id}.${page.id}.hint`])
                        if (!texts[key])
                            missing.push(key);
        }
        expect(missing).toEqual([]);
    });

    it('keeps the groups in the order of D-5.3, and ids unique', () => {
        expect(groupsOf(ADMIN_SECTIONS).map(group => group.id)).toEqual(['account', 'libraries', 'people', 'server']);
        expect(new Set(ADMIN_SECTIONS.map(section => section.id)).size).toBe(ADMIN_SECTIONS.length);
    });

    it('shows a section only with one of its sub-pages\' rights, and only those sub-pages', () => {
        const rights = new Set(['tenant.settings', 'account.password']);
        const sections = visibleSections((...wanted) => wanted.some(right => rights.has(right)));

        expect(sections.map(section => [section.id, section.subpages.map(page => page.id)]))
            .toEqual([['account', ['password']], ['settings', ['tenant']]]);
    });

    it('finds where a moved sub-page went', () => {
        const moved = { 'old/page': { section: 'new', page: 'here' } };

        expect(movedPage('old', 'page', moved)).toEqual({ section: 'new', page: 'here' });
        expect(movedPage('old', null, moved)).toBeUndefined();
        expect(movedPage('users', 'list', moved)).toBeUndefined();
    });
});
