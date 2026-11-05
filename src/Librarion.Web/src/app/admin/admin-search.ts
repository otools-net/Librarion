/*
 * The search box of the administration (port of SFE's admin-search.ts; agents/docs/features/admin-search.md): finds
 * the sections, sub-pages and settings of the sections a session sees, by every word typed (any case, accents ignored)
 * in their titles, descriptions, choices and keys. A setting is found on each settings sub-page whose level may set it
 * (the catalog's `lowestLevel`). Those whose title holds every word come first.
 */

import { SettingInfo, SettingLevel } from '../core/models';
import { AdminSection, AdminSubpage } from './admin-sections';

export interface AdminSearchResult {
    section: AdminSection;
    subpage: AdminSubpage;
    /** The setting found; none for a section or a sub-page. */
    setting?: string;
    title: string;
    /** Where it is: "Section › Sub-page › Group". */
    where: string;
    text?: string;
}

/** The most results listed. */
export const ADMIN_SEARCH_LIMIT = 40;

/** Widest first: a key goes down to its `lowestLevel`. */
const LEVELS: readonly SettingLevel[] = ['server', 'tenant', 'library', 'user'];

/** A key the level `level` may set. */
export const settableAt = (setting: SettingInfo, level: SettingLevel) => LEVELS.indexOf(level) <= LEVELS.indexOf(setting.lowestLevel);

/** Lower case without accents, so that "theme" finds "Thème" and the other way round. */
export const normalize = (text: string) => text.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();

/** The words of a query, normalized. */
export const searchWords = (query: string) => normalize(query).split(/\s+/).filter(Boolean);

type Translate = (key: string) => string;

/** Matches of `query` among `sections` (those the session sees), best first; the page lists `ADMIN_SEARCH_LIMIT` of them. */
export function searchAdmin(query: string, sections: AdminSection[], catalog: readonly SettingInfo[], t: Translate): AdminSearchResult[] {
    const words = searchWords(query);
    if (!words.length)
        return [];
    const titled: AdminSearchResult[] = [];
    const others: AdminSearchResult[] = [];
    const add = (result: AdminSearchResult, ...more: string[]) => {
        const title = normalize(result.title);
        const all = normalize([result.title, result.where, result.text ?? '', ...more].join('\n'));
        if (words.every(word => title.includes(word)))
            titled.push(result);
        else if (words.every(word => all.includes(word)))
            others.push(result);
    };
    for (const section of sections) {
        const sectionTitle = t(`adminSection.${section.id}`);
        const single = section.subpages.length === 1;
        add({ section, subpage: section.subpages[0], title: sectionTitle, where: t(`adminGroup.${section.group}`), text: t(`adminSection.${section.id}.hint`) });
        for (const subpage of section.subpages) {
            const pageTitle = t(`adminPage.${section.id}.${subpage.id}`);
            const where = single ? sectionTitle : `${sectionTitle} › ${pageTitle}`;
            if (!single)
                add({ section, subpage, title: pageTitle, where: sectionTitle, text: t(`adminPage.${section.id}.${subpage.id}.hint`) });
            const level = subpage.settings;
            if (!level)
                continue;
            for (const setting of catalog.filter(s => settableAt(s, level)))
                add({
                    section, subpage, setting: setting.key, title: t(`setting.${setting.key}`),
                    where: `${where} › ${t(`settingSection.${setting.section}`)}`, text: t(`setting.${setting.key}.hint`),
                }, setting.key, ...(setting.choices ?? []).map(choice => t(`setting.${setting.key}.${choice}`)));
        }
    }
    return [...titled, ...others];
}

/** `text` cut into the parts matching one of `words` and the others, to mark the first ones. */
export function markWords(text: string, words: string[]): { text: string; match: boolean }[] {
    // Normalizing keeps the length of the usual letters (é becomes e plus a mark, removed): compare letter by letter.
    const plain = [...text].map(letter => normalize(letter));
    const matched = new Array<boolean>(plain.length).fill(false);
    const joined = plain.join('');
    // Positions in `joined` back to letters of `text`.
    const letterAt: number[] = [];
    plain.forEach((part, index) => { for (let i = 0; i < part.length; i++) letterAt.push(index); });
    for (const word of words)
        for (let at = joined.indexOf(word); at >= 0; at = joined.indexOf(word, at + word.length))
            for (let i = at; i < at + word.length; i++)
                matched[letterAt[i]] = true;
    const parts: { text: string; match: boolean }[] = [];
    [...text].forEach((letter, index) => {
        const last = parts[parts.length - 1];
        if (last && last.match === matched[index])
            last.text += letter;
        else
            parts.push({ text: letter, match: matched[index] });
    });
    return parts;
}
