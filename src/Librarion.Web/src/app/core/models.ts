// Shapes of the API's answers (camelCase JSON, enums as camelCase strings).

export type DatabaseProvider = 'sqlite' | 'postgres';

export interface SystemInfo {
    version: string;
    commit: string | null;
    database: DatabaseProvider;
}

// ---- Session and setup ----

export interface SessionAccount {
    id: number;
    userName: string;
    displayName: string | null;
    isServerAdmin: boolean;
    /** A local account: its password can be changed here. */
    local: boolean;
}

export interface SessionTenant {
    id: number;
    slug: string;
    name: string;
}

export interface SessionInfo {
    user: SessionAccount | null;
    tenant: SessionTenant | null;
    /** The tenants the account can switch to (server administrators: every one). */
    tenants: SessionTenant[];
    /** Its rights in the tenant (library rights: held in some library). */
    rights: string[];
    setupRequired: boolean;
    /** The settings in effect for the account, by key. */
    preferences: Record<string, unknown>;
}

export interface SetupState {
    required: boolean;
    available: boolean;
    codeConfigured: boolean;
    codeTooShort: boolean;
    recover: boolean;
    minimumCodeLength: number;
    codeGenerated: boolean;
    /** Where the generated code is kept; only while the page needs it. */
    codeFile: string | null;
}

export interface SetupInput {
    code: string;
    userName: string;
    password: string;
    displayName?: string | null;
    tenantName?: string | null;
    libraryName?: string | null;
}

// ---- Administration ----

export type RightScope = 'library' | 'tenant' | 'server';
export type RightKind = 'read' | 'write';
export type BuiltInGroup = 'administrators' | 'users';

export interface RightInfo {
    id: string;
    scope: RightScope;
    kind: RightKind;
    area: string;
    defaultGroups: BuiltInGroup[];
    /** The server takes it from everyone (`Librarion:Rights:Disabled`). */
    disabledByServer: boolean;
}

export interface TenantInfo {
    id: number;
    slug: string;
    name: string;
    disabled: boolean;
    created: string;
    disabledRights: string[];
}

export interface TenantInput {
    name: string;
    disabled?: boolean;
    libraryName?: string | null;
    disabledRights?: string[] | null;
}

export interface LibraryInfo {
    id: number;
    tenantId: number;
    name: string;
    created: string;
}

export interface UserInfo {
    id: number;
    tenantId: number | null;
    userName: string;
    displayName: string | null;
    source: string;
    isServerAdmin: boolean;
    disabled: boolean;
    lockedOut: boolean;
    created: string;
    lastSignIn: string | null;
    groups: { groupId: number; group: string }[];
}

export interface UserInput {
    userName: string;
    displayName?: string | null;
    /** Null keeps the current one. */
    password?: string | null;
    isServerAdmin?: boolean;
    disabled?: boolean;
    groupIds?: number[] | null;
    unlock?: boolean;
    /** New accounts: the tenant (the session's by default). */
    tenantId?: number | null;
}

export interface GroupInfo {
    id: number;
    tenantId: number;
    name: string;
    builtIn: BuiltInGroup | null;
    externalName: string | null;
    allLibraries: boolean;
    rights: string[];
    libraryIds: number[];
    memberIds: number[];
}

export interface GroupInput {
    name: string;
    allLibraries: boolean;
    rights?: string[] | null;
    libraryIds?: number[] | null;
    memberIds?: number[] | null;
}

// ---- Settings ----

export type SettingLevel = 'server' | 'tenant' | 'library' | 'user';

export interface SettingState {
    key: string;
    section: string;
    kind: 'choice' | 'integer';
    choices: string[] | null;
    min: number;
    max: number;
    value: unknown;
    inherited: unknown;
    /** The level whose value is in effect; null = the default. */
    source: SettingLevel | null;
    locked: boolean;
    lockedAbove: boolean;
    editable: boolean;
}

export interface SettingChanges {
    values?: Record<string, unknown>;
    lock?: string[];
    unlock?: string[];
}

// ---- Security log ----

export interface SecurityEventInfo {
    id: number;
    time: string;
    kind: string;
    tenantId: number | null;
    userId: number | null;
    userName: string | null;
    address: string | null;
    detail: string | null;
}
