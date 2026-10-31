// Shapes of the API's answers (camelCase JSON, enums as camelCase strings).

export type DatabaseProvider = 'sqlite' | 'postgres';

export interface SystemInfo {
    version: string;
    commit: string | null;
    database: DatabaseProvider;
}
