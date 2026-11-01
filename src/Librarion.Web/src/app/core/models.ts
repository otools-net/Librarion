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
    /** Books in it (kept by scans). */
    bookCount?: number;
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

/** A folder a library's books are found under (`api/libraries/{id}/roots`). */
export interface LibraryRootInfo {
    id: number;
    libraryId: number;
    path: string;
    created: string;
    exists: boolean;
}

export type TaskOutcome = 'running' | 'succeeded' | 'failed' | 'cancelled' | 'interrupted' | 'skipped';

export interface ScanProgress {
    filesSeen: number;
    archivesDone: number;
    added: number;
    changed: number;
    removed: number;
    current: string | null;
    /** Books that went missing (kept, not removed). */
    missing: number;
    /** Missing books found again. */
    returned: number;
}

export interface ScanCounts {
    added: number;
    changed: number;
    removed: number;
    unchanged: number;
    archivesRead: number;
    archivesSkipped: number;
    unreadable: number;
    missingRoots: string[];
    problems: string[];
    missing: number;
    returned: number;
}

/** A run of a background task (`api/libraries/{id}/tasks`, `api/tasks/{run}`). */
export interface TaskRunInfo {
    id: number;
    libraryId: number | null;
    task: 'libraryScan' | 'removeRoot' | 'removeMissing';
    trigger: 'manual' | 'schedule';
    started: string;
    finished: string | null;
    outcome: TaskOutcome;
    summary: string | null;
    counts: ScanCounts | null;
    progress: ScanProgress | null;
    waiting: boolean;
}

/** A book whose files are gone, kept until they come back or its removal is accepted (`api/libraries/{id}/missing`). */
export interface MissingBookItem {
    id: number;
    name: string;
    path: string;
    missingSince: string;
    /** Missing longer than the library's retention: offered for removal first. */
    ready: boolean;
}

export interface MissingBookPage {
    items: MissingBookItem[];
    next: string | null;
    total: number;
    ready: number;
    keepDays: number;
}

/** Which missing books the user accepted to remove: chosen ones, those past the retention, or all. */
export interface MissingRemoval {
    bookIds?: number[];
    ready?: boolean;
    all?: boolean;
}

export type ScheduleKind = 'interval' | 'weekly';

export interface ScheduleInput {
    kind: ScheduleKind;
    /** UTC; for weekly schedules its time of day (server's zone) is the time. */
    start: string;
    intervalMinutes: number;
    /** Bit 0 = Sunday. */
    days: number;
    enabled: boolean;
}

export interface ScheduleInfo extends ScheduleInput {
    id: number;
    libraryId: number;
    lastRun: string | null;
    nextRun: string | null;
}
