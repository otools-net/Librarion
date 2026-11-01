import en from '../../i18n/en.json';
import { settingCatalog } from '../../testing/fakes';
import { markWords, searchAdmin, settableAt } from './admin-search';
import { ADMIN_SECTIONS } from './admin-sections';

const texts = en as Record<string, string>;
const t = (key: string) => texts[key] ?? key;
const catalog = settingCatalog();
const search = (query: string, sections = ADMIN_SECTIONS) => searchAdmin(query, sections, catalog, t);
const titles = (query: string) => search(query).map(result => result.title);

describe('searchAdmin', () => {
    it('finds nothing without a word', () => {
        expect(search('  ')).toEqual([]);
    });

    it('finds every setting on each settings page whose level may set it, and nowhere else', () => {
        const pages = ADMIN_SECTIONS.flatMap(section => section.subpages.filter(page => page.settings).map(page => ({ section, page })));
        expect(pages.map(({ section, page }) => `${section.id}/${page.id}`)).toEqual(['account/preferences', 'librarySettings/settings', 'settings/tenant', 'settings/server']);
        for (const setting of catalog) {
            const found = search(setting.key).filter(result => result.setting === setting.key).map(result => `${result.section.id}/${result.subpage.id}`);
            const wanted = pages.filter(({ page }) => settableAt(setting, page.settings!)).map(({ section, page }) => `${section.id}/${page.id}`);
            expect(found).toEqual(wanted);
            expect(found.length).toBeGreaterThan(0);
        }
        expect(search('library.keepMissingDays').map(result => result.section.id)).not.toContain('account');
    });

    it('says where a setting is: section, sub-page and its group', () => {
        const found = search('Theme').filter(result => result.setting === 'ui.theme').map(result => result.where);
        expect(found).toEqual([
            'My account › Preferences › Interface', 'Library settings › Interface', 'Settings › This tenant › Interface', 'Settings › Whole server › Interface',
        ]);
    });

    it('needs every word, in any case and with or without accents, and lists the titles holding them first', () => {
        expect(titles('CÔLORS')[0]).toBe(t('setting.ui.theme'));
        expect(search('ui.THEME').every(result => result.setting === 'ui.theme')).toBe(true);
        // "removal" is only in the hint of the missing books' days: found, after nothing titled.
        const removal = search('offers removal');
        expect(removal.length).toBeGreaterThan(0);
        expect(removal.every(result => result.setting === 'library.keepMissingDays')).toBe(true);
        // A section titled with the word comes before the settings that only mention it.
        const libraries = titles('libraries');
        expect(libraries[0]).toBe('Libraries');
        expect(search('nosuchword theme')).toEqual([]);
    });

    it('finds the choices of a list, the sections and the sub-pages', () => {
        expect(search('comfortable').map(result => result.setting)).toContain('ui.density');
        const [password] = search('password');
        expect(password).toMatchObject({ title: 'Password', where: 'My account' });
        expect(password.setting).toBeUndefined();
        expect(search('security log')[0]).toMatchObject({ title: 'Security log', where: 'Tenant and server' });
    });

    it('only looks in the sections given', () => {
        const account = ADMIN_SECTIONS.filter(section => section.id === 'account');
        expect(search('keepMissingDays', account)).toEqual([]);
        expect(search('theme', account).map(result => result.where)).toEqual(['My account › Preferences › Interface']);
    });
});

describe('markWords', () => {
    it('marks each match of the words, accents and case aside', () => {
        expect(markWords('Thème du thème', ['theme'])).toEqual([
            { text: 'Thème', match: true }, { text: ' du ', match: false }, { text: 'thème', match: true },
        ]);
        expect(markWords('Largest page', [])).toEqual([{ text: 'Largest page', match: false }]);
    });
});
