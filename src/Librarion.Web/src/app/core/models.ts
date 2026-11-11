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
    /** Changes with the book: the cover's address carries it. */
    coverVersion?: string | null;
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
    /** May change the book's plain tags (`library.tag` in its library). */
    canTag: boolean;
    /** May change its title, description, rating and fields (`library.edit`). */
    canEdit: boolean;
    coverVersion?: string | null;
    /** How its cover is chosen: the generated style it has, whether a user chose the cover (an upload, a style over its own). */
    cover?: { template: number; variant: number; chosen: boolean } | null;
    /** May merge it with its duplicates (`library.merge` in its library). */
    canMerge?: boolean;
}

/** A group of possible duplicates on the Duplicates page: its books the session sees in the library. */
export interface DuplicateGroupItem {
    id: number;
    level: BookDuplicate['reason'];
    /** The identifier's scheme, for an `identifier` group. */
    scheme: string | null;
    /** Its first books, to show (at most six). */
    books: BookListItem[];
    /** Every one of its books the session sees: compared, merged or set apart together. */
    bookIds: number[];
}

export interface DuplicatePage {
    items: DuplicateGroupItem[];
    next: string | null;
    /** The groups were built (else the next scan or import, or Find again, makes them). */
    built: boolean;
}

/** A book merge (the Duplicates page's Merges): the kept book and the first names of those merged into it (`books` of them). */
export interface BookMergeInfo {
    id: number;
    created: string;
    targetId: number;
    targetName: string;
    sourceNames: string[];
    books: number;
    undone: string | null;
}

/** Books merged into `targetId`; each value from the book named (the target when null). */
export interface BookMergeInput {
    targetId: number;
    sourceIds: number[];
    titleFrom?: number | null;
    descriptionFrom?: number | null;
    ratingFrom?: number | null;
    coverFrom?: number | null;
}

/** The generated cover styles: template names by index, and how many palettes each has. */
export interface CoverStyles {
    templates: string[];
    palettes: number;
    /** The templates the tenant turned off (Cover styles): not offered, but for the book's own. */
    off: number[];
}

/** A generated template on Cover styles (`api/covers/templates`): new books get those on. */
export interface CoverTemplateInfo {
    index: number;
    name: string;
    on: boolean;
}

export interface CoverTemplates {
    templates: CoverTemplateInfo[];
    palettes: number;
}

/** A tag a book's field is to have: by id, or by name (found in the field, else made). */
export interface BookTagEdit {
    id?: number | null;
    name?: string | null;
    number?: number | null;
}

/** A plain tag among selected books: how many of them have it (all, some). */
export interface SelectionTag {
    id: number;
    name: string;
    color: string | null;
    books: number;
}

export interface SelectionTags {
    /** The selected books the caller may tag. */
    books: number;
    tags: SelectionTag[];
}

/** Plain tags to give to many books (by id, or by name: found, else made) and to take away. */
export interface TagChangeInput {
    add?: BookTagEdit[];
    remove?: number[];
}

/** Every book of a library's list: all, those with a tag (its page), those matching a search, those in a folder. */
export interface BookScope {
    library: number;
    tag?: number | null;
    q?: string | null;
    folder?: number | null;
}

/** A folder of the folder view: a directory, a ZIP (`zip`), a directory inside one; counts of present files. */
export interface FolderItem {
    id: number;
    name: string;
    zip: boolean;
    /** Files directly in it; `totalFiles` with those of every folder under it. */
    files: number;
    totalFiles: number;
    /** Folders directly under it. */
    folders: number;
    /** Its own default tags (the badge on its row). */
    defaultTags: number;
}

/** A default tag of a library (`folderId` null) or a folder; `from` names where an inherited one is set (null: the library). */
export interface DefaultTagItem {
    tagId: number;
    name: string;
    color: string | null;
    folderId: number | null;
    from: string | null;
}

/** A library's or folder's own default tags, then those it inherits, nearest first. */
export interface DefaultTagList {
    own: DefaultTagItem[];
    inherited: DefaultTagItem[];
}

/** A folder with the folders above it (a library's only root left out: the library is the top). */
export interface FolderDetail {
    folder: FolderItem;
    libraryId: number;
    path: { id: number; name: string }[];
}

/** What a save of the book editor changes (`PUT api/books/{id}`); a part left out stays as it is. */
export interface BookEdit {
    columns?: { title: string | null; description: string | null; rating: number | null } | null;
    /** Each field's tags set whole, in order; `field` null for the plain tags. */
    fields?: { field: string | null; tags: BookTagEdit[] }[] | null;
}

/**
 * What the search box offers for the term being typed: a field to type a value of, a value as a whole term, or a
 * special term (`special:untagged`; `name` is `untagged`).
 */
export interface SearchSuggestion {
    kind: 'field' | 'value' | 'special';
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

/**
 * `number`: by the number on the tag's link (a series' order; books without one are left out), only with a tag;
 * `shelf`: a shelf's own order, only on a shelf (`added` there: put on the shelf last, first).
 */
export type BookSort = 'title' | 'author' | 'added' | 'number' | 'shelf';

/** One of the account's shelves (agents/docs/features/shelves.md); `bookCount` counts rows, seen now or not. */
export interface ShelfInfo {
    id: number;
    name: string;
    bookCount: number;
    created: string;
    updated: string;
}

/** What putting books on a shelf did: those put on, and those it had already. */
export interface ShelfAdded {
    shelf: ShelfInfo;
    added: number;
    already: number;
}

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
/** A tag the picker offers (`api/tags/pick`): `alias` the other name it was found by, `parent` the one telling it apart. */
export interface PickedTag {
    id: number;
    name: string;
    color: string | null;
    field: string | null;
    shorthand: string | null;
    alias: string | null;
    parent: string | null;
    books: number;
}

export interface PickedTags {
    items: PickedTag[];
    /** A tag has the typed name itself: no "Create" then. */
    exact: boolean;
}

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

/** What an API key may be used for: the feeds, reading the API, everything in the API. */
export type ApiKeyScope = 'opds' | 'api:read' | 'api:write';

export const API_KEY_SCOPES: ApiKeyScope[] = ['opds', 'api:read', 'api:write'];

/** An API key as listed (never its secret). */
export interface ApiKeyInfo {
    id: number;
    name: string;
    scopes: ApiKeyScope[];
    /** The libraries it is limited to; empty = every library its account may reach. */
    libraryIds: number[];
    pathAllowed: boolean;
    created: string;
    expires: string | null;
    revoked: string | null;
    lastUsed: string | null;
    lastAddress: string | null;
}

export interface ApiKeyInput {
    name: string;
    scopes: ApiKeyScope[];
    libraryIds: number[];
    pathAllowed: boolean;
    /** UTC; null = until revoked. */
    expires: string | null;
}

/** A key just made: its whole text, shown this once. */
export interface NewApiKey {
    info: ApiKeyInfo;
    key: string;
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
    task: 'libraryScan' | 'removeRoot' | 'removeMissing' | 'import' | 'tagDelete' | 'tagMerge' | 'fieldMerge' | 'mergeUndo' | 'tagCounts' | 'scopeTags' | 'tagIgnore' | 'coverReassign' | 'duplicateBuild' | 'bookMerge' | 'tagDuplicateBuild';
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

/** A field in the tag manager (`api/tags/manage/fields`): its values counted up to 10,000 (`capped` past it). */
export interface ManagedField {
    id: number;
    key: string;
    name: string;
    builtIn: boolean;
    color: string | null;
    isHidden: boolean;
    values: number;
    capped: boolean;
}

export interface ManagedFields {
    fields: ManagedField[];
    plainTags: number;
    plainCapped: boolean;
}

/** A tag in the tag manager: its books in every library, the tags directly under it. */
export interface ManagedTag {
    id: number;
    name: string;
    sortName: string;
    color: string | null;
    isCategory: boolean;
    isHidden: boolean;
    builtIn: boolean;
    /** With the books of the tags under it, each once. */
    books: number;
    /** Linked to it itself: what a delete or merge moves. */
    tagged: number;
    children: number;
}

/** A page of the tag manager's list; a search is one page, `capped` when it found more. */
export interface ManagedPage {
    items: ManagedTag[];
    next: string | null;
    capped: boolean;
}

/** A tag as its editor has it (`api/tags/{id}/definition`). */
export interface TagDefinition {
    id: number;
    name: string;
    sortName: string;
    shorthand: string | null;
    color: string | null;
    isCategory: boolean;
    isHidden: boolean;
    disambiguationParentId: number | null;
    fieldId: number | null;
    fieldKey: string | null;
    builtIn: boolean;
    aliases: string[];
    parents: { id: number; name: string }[];
    books: number;
}

export interface TagInput {
    name: string;
    sortName?: string | null;
    shorthand?: string | null;
    color?: string | null;
    isCategory?: boolean;
    isHidden?: boolean;
    disambiguationParentId?: number | null;
    aliases?: string[];
    parentIds?: number[];
    /** A new value of this field (create only). */
    fieldId?: number | null;
    /** A new field (create only). */
    fieldKey?: string | null;
}

/** A change of books' links: done (`runId` null) or running in the background; `links` moved or removed. */
/** A tag of a possible duplicates group: its books in every library (direct links). */
export interface TagDuplicateTag {
    id: number;
    name: string;
    books: number;
}

/** Why tags may be one: the same words, words that sound alike, a name and its initials. */
export type TagDuplicateLevel = 'words' | 'sound' | 'initials';

/** A group of possible duplicate tags of one field (Tags › Possible duplicates), the most books first. */
export interface TagDuplicateItem {
    id: number;
    level: TagDuplicateLevel;
    books: number;
    tags: TagDuplicateTag[];
}

export interface TagDuplicatePage {
    items: TagDuplicateItem[];
    next: string | null;
    built: boolean;
}

/** A tag that may be the same as the one shown (its page). */
export interface TagLookAlike {
    level: TagDuplicateLevel;
    tag: TagDuplicateTag;
}

export interface TagWork {
    runId: number | null;
    logId: number | null;
    links: number;
}

/** A tag or field merge (`api/merges`), undone while nothing later stands in the way. */
export interface MergeLogInfo {
    id: number;
    kind: 'tag' | 'field' | 'ignore';
    created: string;
    sourceId: number;
    sourceName: string;
    targetId: number;
    targetName: string;
    links: number;
    completed: boolean;
    undone: string | null;
}

/** Where an imported name came from (`ImportTagSourceKind`). */
export type ImportSource = 'fb2' | 'epub' | 'mobi' | 'pdf' | 'comicInfo' | 'calibre' | 'inpx';
export const IMPORT_SOURCES: readonly ImportSource[] = ['fb2', 'epub', 'mobi', 'pdf', 'comicInfo', 'calibre', 'inpx'];

export type ImportedStatus = 'review' | 'reviewed' | 'all';

/** What the caller may do on the Imported tags page (`library.importedTags`). */
export interface ImportedContext {
    /** The libraries whose page it may read. */
    libraries: number[];
    /** Keep and ignore: the right in every library of the tenant (the mappings are the tenant's). */
    canChange: boolean;
    /** Map onto another tag: also `tenant.tags`. */
    canMap: boolean;
}

/** Plain tags (`key` null) or a field, with its tags that have imported names (`capped` past 10,000). */
export interface ImportedField {
    id: number | null;
    key: string | null;
    name: string;
    builtIn: boolean;
    tags: number;
    capped: boolean;
}

export interface ImportedName {
    name: string;
    source: ImportSource;
    reviewed: boolean;
}

/** A tag imports give: its books in the library (direct links) and its raw names. */
export interface ImportedTag {
    id: number;
    name: string;
    color: string | null;
    books: number;
    names: ImportedName[];
    reviewed: boolean;
}

/** How much of the field's imported links its first `topCount` and its reviewed tags hold. */
export interface ImportedSummary {
    tags: number;
    links: number;
    topCount: number;
    top: number;
    reviewed: number;
}

/** An ignore that stands: the tag, its raw names, the imported links taken off. */
export interface IgnoredTagInfo {
    id: number;
    created: string;
    tagId: number;
    tagName: string;
    names: string[];
    links: number;
    completed: boolean;
}
