// The rights' ids, the same as the server's RightCatalog (agents/docs/features/rights.md). Their texts are
// `right.<id>` and `right.<id>.hint` in en.json (ClientI18nTests checks every right of the catalog has them).
import type { RightInfo } from './models';
export const Rights = {
    browse: 'library.browse',
    scan: 'library.scan',
    download: 'library.download',
    import: 'library.import',
    librarySettings: 'library.settings',
    tagBooks: 'library.tag',
    editBooks: 'library.edit',
    mergeBooks: 'library.merge',
    importedTags: 'library.importedTags',
    defaultTags: 'library.defaultTags',
    manageTags: 'tenant.tags',
    manageUsers: 'tenant.users',
    manageLibraries: 'tenant.libraries',
    tenantSettings: 'tenant.settings',
    tenantSecurityLog: 'tenant.securityLog',
    ownPreferences: 'account.preferences',
    ownPassword: 'account.password',
    systemInfo: 'system.info',
    apiDocs: 'system.apiDocs',
    manageTenants: 'server.tenants',
    manageAllUsers: 'server.users',
    serverSettings: 'server.settings',
    serverSecurityLog: 'server.securityLog',
} as const;

/** Any of these opens the administration. */
export const ADMIN_RIGHTS: string[] = [
    Rights.manageUsers, Rights.manageLibraries, Rights.tenantSettings, Rights.tenantSecurityLog, Rights.librarySettings,
    Rights.manageTenants, Rights.manageAllUsers, Rights.serverSettings, Rights.serverSecurityLog,
];

/** The rights a group can hold or a tenant switch off (not the server's), by area in the catalog's order. */
export function rightAreas(catalog: RightInfo[]): { name: string; rights: RightInfo[] }[] {
    const areas: { name: string; rights: RightInfo[] }[] = [];
    for (const right of catalog.filter(r => r.scope !== 'server')) {
        let area = areas.find(a => a.name === right.area);
        if (!area)
            areas.push(area = { name: right.area, rights: [] });
        area.rights.push(right);
    }
    return areas;
}
