/*
 * The sections of the administration (port of SFE's admin-sections.ts; agents/docs/features/admin-page.md): groups of
 * sections, each with its sub-pages (`admin/:section/:page`). A sub-page shows to whoever has one of its `rights`; a
 * section with no sub-page left is not listed. Texts: `adminGroup.<group>`, `adminSection.<section>` (+ `.hint`, its
 * description), `adminPage.<section>.<page>` (+ `.hint`). My account comes first: everyone has it.
 */

import { IconName } from '../shared/icon';
import { Rights } from '../core/rights';
import { SettingLevel } from '../core/models';

export interface AdminSubpage {
    id: string;
    /** Rights, one of which shows it. */
    rights: readonly string[];
    /** The settings level its form edits (the search finds the keys that level may set there). */
    settings?: SettingLevel;
}

export interface AdminSection {
    id: string;
    group: string;
    icon: IconName;
    /** The first one opens by default; with a single one, nothing is listed under the section. */
    subpages: AdminSubpage[];
}

const page = (id: string, ...rights: string[]): AdminSubpage => ({ id, rights });
const settingsPage = (id: string, settings: SettingLevel, ...rights: string[]): AdminSubpage => ({ id, rights, settings });

export const ADMIN_SECTIONS: AdminSection[] = [
    { id: 'account', group: 'account', icon: 'user', subpages: [settingsPage('preferences', 'user', Rights.ownPreferences), page('password', Rights.ownPassword)] },
    { id: 'libraries', group: 'libraries', icon: 'library', subpages: [page('list', Rights.manageLibraries, Rights.manageTenants)] },
    { id: 'librarySettings', group: 'libraries', icon: 'sliders', subpages: [settingsPage('settings', 'library', Rights.librarySettings, Rights.manageTenants)] },
    { id: 'tags', group: 'catalog', icon: 'tag', subpages: [page('tags', Rights.manageTags), page('merges', Rights.manageTags)] },
    { id: 'duplicates', group: 'catalog', icon: 'copy', subpages: [page('groups', Rights.mergeBooks), page('merges', Rights.mergeBooks)] },
    { id: 'coverStyles', group: 'catalog', icon: 'image', subpages: [page('templates', Rights.coverStyles)] },
    { id: 'importedTags', group: 'catalog', icon: 'import', subpages: [page('names', Rights.importedTags), page('ignored', Rights.importedTags)] },
    { id: 'users', group: 'people', icon: 'users', subpages: [page('list', Rights.manageUsers, Rights.manageAllUsers)] },
    { id: 'groups', group: 'people', icon: 'key', subpages: [page('list', Rights.manageUsers, Rights.manageTenants)] },
    { id: 'tasks', group: 'server', icon: 'activity', subpages: [page('tasks', Rights.scan), page('schedules', Rights.scan), page('history', Rights.scan)] },
    { id: 'tenants', group: 'server', icon: 'tenant', subpages: [page('list', Rights.manageTenants)] },
    { id: 'settings', group: 'server', icon: 'settings', subpages: [settingsPage('tenant', 'tenant', Rights.tenantSettings), settingsPage('server', 'server', Rights.serverSettings)] },
    { id: 'security', group: 'server', icon: 'shield', subpages: [page('log', Rights.tenantSecurityLog, Rights.serverSecurityLog)] },
    { id: 'system', group: 'server', icon: 'server', subpages: [page('about', Rights.apiDocs, Rights.serverSettings)] },
];

/**
 * Sub-pages that moved, from "section/page" to where they are now: addresses kept in bookmarks and history still open
 * them. Never remove an entry. (The pages of stage 4, `admin/users` and the others, kept their section ids; `/account`
 * is a redirect of the routes.)
 */
export const ADMIN_MOVED: Readonly<Record<string, { section: string; page: string }>> = {};

/** Where the sub-page of an address that moved is now, if it moved. */
export function movedPage(section: string | null, page: string | null, moved = ADMIN_MOVED): { section: string; page: string } | undefined {
    return section && page ? moved[`${section}/${page}`] : undefined;
}

/** The sections by group, in order. */
export function groupsOf(sections: AdminSection[]): { id: string; sections: AdminSection[] }[] {
    return [...new Set(sections.map(s => s.group))].map(id => ({ id, sections: sections.filter(s => s.group === id) }));
}

/** The id of a group's heading on the overview, the breadcrumbs' link to it. */
export const groupAnchor = (group: string) => `admin-group-${group}`;

/** The sections and sub-pages a session sees: those with one of their rights. */
export function visibleSections(canAny: (...rights: string[]) => boolean): AdminSection[] {
    return ADMIN_SECTIONS
        .map(section => ({ ...section, subpages: section.subpages.filter(page => canAny(...page.rights)) }))
        .filter(section => section.subpages.length > 0);
}

