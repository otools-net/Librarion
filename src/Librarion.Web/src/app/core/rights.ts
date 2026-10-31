// The rights' ids, the same as the server's RightCatalog (agents/docs/features/rights.md). Their texts are
// `right.<id>` and `right.<id>.hint` in en.json (ClientI18nTests checks every right of the catalog has them).
export const Rights = {
    browse: 'library.browse',
    librarySettings: 'library.settings',
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
