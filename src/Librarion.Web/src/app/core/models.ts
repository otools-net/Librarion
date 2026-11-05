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
export type BuiltInGroup = 'administrators' | 'users' | 'curators';

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

/** A book in a list (`api/books`): what a card or a row shows; `title` null = shown by its file name. */
export interface BookListItem {
    id: number;
    libraryId: number;
    title: string | null;
    fileName: string | null;
    authors: string[];
    series: string | null;
    seriesNumber: number | null;
    formats: string[];
    coverSource: 'book' | 'calibre' | 'uploaded' | 'generated';
}

/** A link of a book to a tag: `field` the field's key (null for a plain tag); `number` its place in a series. */
export interface BookTag {
    id: number;
    field: string | null;
    name: string;
    number: number | null;
}

/** A copy or format of a book: `name` its own (an archive entry's), `archive` the ZIP holding it; `missing`: not where the last scan found it. */
export interface BookFile {
    id: number;
    format: string;
    size: number;
    name: string;
    inArchive: boolean;
    archive: string | null;
    missing: boolean;
}

export interface KeyValue {
    key: string;
    value: string;
}

/**
 * A book (`api/books/{id}`): its tags in field order, files, identifiers (`isbn`…), imported properties (Calibre's
 * custom columns); `rating` 0–10 (half stars); `mergedIntoId`: it now lives in that book (the rest is empty).
 */
export interface BookDetail {
    id: number;
    libraryId: number;
    title: string | null;
    fileName: string | null;
    description: string | null;
    rating: number | null;
    added: string;
    coverSource: BookListItem['coverSource'];
    tags: BookTag[];
    files: BookFile[];
    identifiers: KeyValue[];
    mergedIntoId: number | null;
    properties: KeyValue[] | null;
    canDownload: boolean;
}

/** What the search box offers for the term being typed: a field to type a value of, or a value as a whole term. */
export interface SearchSuggestion {
    kind: 'field' | 'value';
    /** The term put in place of the one being typed (`author:"Stephen King"`, `author:`). */
    insert: string;
    /** The value's name, or the field's key. */
    name: string;
    field: string | null;
    bookCount: number | null;
}

/** Suggestions for the last term of a search; `from`: where that term starts in it. */
export interface SuggestionList {
    from: number;
    items: SearchSuggestion[];
}

/** A value of a facet: its books among those counted, and the search term finding them (`category:"Fantasy"`, `format:epub`). */
export interface FacetValue {
    /** The tag; null for a format. */
    id: number | null;
    name: string;
    term: string;
    count: number;
}

/** A field's most frequent values among the books (`field`: its key; `tag` plain tags, `format` the files' formats). */
export interface Facet {
    field: string;
    values: FacetValue[];
}

/** Facets of a library or a search: counted over `counted` books, at most the first matches when `capped`. */
export interface FacetList {
    counted: number;
    capped: boolean;
    fields: Facet[];
}

/** Why another book may be the same: a file of the same size and CRC, a shared identifier (`scheme`), the same title and author. */
export interface BookDuplicate {
    book: BookListItem;
    reason: 'file' | 'identifier' | 'work';
    scheme: string | null;
}

/** A page of a list by key: `next` is the `after` of the following page, null at the end. */
export interface KeysetPage<T> {
    items: T[];
    next: string | null;
}

/** `number`: by the number on the tag's link (a series' order; books without one are left out), only with a tag. */
export type BookSort = 'title' | 'author' | 'added' | 'number';

/** A field of the tenant (`api/fields`): a built-in one's name is translated from its key. */
export interface FieldInfo {
    id: number;
    key: string;
    name: string;
    builtIn: boolean;
}

/** A field value or plain tag in a list; `bookCount`: books of the library asked about linked to it. */
export interface FieldValue {
    id: number;
    name: string;
    sortName: string;
    bookCount: number | null;
}

/** A tag in another's detail; `field`: its field's key, null for a plain tag. */
export interface TagRef {
    id: number;
    name: string;
    field: string | null;
}

/** A tag or field value (`api/tags/{id}`): its field, the tags it is under and those under it, its book count. */
export interface TagDetail {
    id: number;
    name: string;
    sortName: string;
    field: string | null;
    fieldName: string | null;
    isCategory: boolean;
    bookCount: number | null;
    parents: TagRef[];
    children: TagRef[];
    moreChildren: boolean;
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

/** A key of the settings catalog (`api/settings/catalog`): what the administration's search looks in. */
export interface SettingInfo {
    key: string;
    section: string;
    kind: 'choice' | 'integer';
    choices: string[] | null;
    min: number;
    max: number;
    /** The narrowest level that may set it. */
    lowestLevel: SettingLevel;
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
    /** Records an import left out. */
    skipped: number;
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
    skipped: number;
}

/** An import from another source: its kind, the source's path on the server, the folder of its books' files (default: the source's). */
export interface ImportInput {
    kind: string;
    source: string;
    files?: string;
}

/** A run of a background task (`api/libraries/{id}/tasks`, `api/tasks/{run}`). */
export interface TaskRunInfo {
    id: number;
    libraryId: number | null;
    task: 'libraryScan' | 'removeRoot' | 'removeMissing' | 'import';
    trigger: 'manual' | 'schedule';
    started: string;
    finished: string | null;
    outcome: TaskOutcome;
    summary: string | null;
    counts: ScanCounts | null;
    progress: ScanProgress | null;
    waiting: boolean;
}

/** The Background tasks page (`api/tasks/overview`): in the libraries the session may scan. */
export interface TaskOverview {
    running: TaskRunInfo[];
    /** The last finished run of each kind that ran. */
    last: TaskRunInfo[];
    schedules: ScheduleInfo[];
    /** The libraries the session may scan. */
    libraries: number[];
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
    /** What it runs: a scan of the library's folders, or an import (with its kind, source and files folder). */
    task: 'libraryScan' | 'import';
    importKind: string | null;
    source: string | null;
    files: string | null;
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
