import { I18n } from './i18n.service';
import { TaskRunInfo } from './models';

/*
 * How a task run is worded (the progress panel, the Background tasks page, the runs lists): its title while it runs
 * ("Scanning Comics…"), the counts so far or at its end, the time it took. Texts `taskRun.*`, `tasks.*`.
 */

/** Runs over a whole tenant's tags (no library): their counts are links. */
const TAG_TASKS = new Set<TaskRunInfo['task']>(['tagDelete', 'tagMerge', 'fieldMerge', 'mergeUndo', 'tagCounts', 'scopeTags', 'tagIgnore']);

/** "Scanning Comics…" while it runs; "Scan · Comics" once it ended (its outcome is shown beside). */
export function runTitle(i18n: I18n, run: TaskRunInfo, library: string): string {
    return run.outcome === 'running'
        ? i18n.t(`taskRun.running.${run.task}`, { library })
        : i18n.t('taskRun.ended', { task: i18n.t(`task.${run.task}`), library });
}

/** What it did so far (or did): the counts, waiting, or the summary the server kept (a reason it failed). */
export function runDetail(i18n: I18n, run: TaskRunInfo): string {
    if (run.outcome === 'running') {
        if (run.waiting)
            return i18n.t('tasks.waiting');
        const progress = run.progress;
        if (!progress)
            return i18n.t('tasks.starting');
        if (run.task === 'coverReassign')
            return i18n.t('tasks.coverProgress', { books: progress.changed });
        if (run.task === 'duplicateBuild')
            return i18n.t('tasks.duplicateProgress', { groups: progress.added, rows: progress.filesSeen });
        if (run.task === 'tagDuplicateBuild')
            return i18n.t('tasks.tagDuplicateProgress', { groups: progress.added, tags: progress.filesSeen });
        if (run.task === 'bookMerge')
            return i18n.t('tasks.mergeProgress', { merged: progress.changed, skipped: progress.skipped });
        if (TAG_TASKS.has(run.task))
            return i18n.t('tasks.tagProgress', { links: progress.changed + progress.removed, tag: progress.current ?? '' });
        return run.task === 'import'
            ? i18n.t('tasks.importProgress', { records: progress.filesSeen, added: progress.added, changed: progress.changed, skipped: progress.skipped })
            : i18n.t('tasks.progress', { files: progress.filesSeen, added: progress.added, changed: progress.changed, missing: progress.missing, removed: progress.removed });
    }
    const counts = run.counts;
    const parts = [
        counts && TAG_TASKS.has(run.task) && i18n.t('tasks.tagCounts', { links: counts.changed + counts.removed }),
        counts && run.task === 'coverReassign' && i18n.t('tasks.coverCounts', { books: counts.changed }),
        counts && (run.task === 'duplicateBuild' || run.task === 'tagDuplicateBuild') && i18n.t('tasks.duplicateCounts', { groups: counts.changed }),
        counts && run.task === 'bookMerge' && i18n.t(counts.changed === 1 ? 'tasks.mergeCounts.one' : 'tasks.mergeCounts', { merged: counts.changed, skipped: counts.skipped }),
        counts && !TAG_TASKS.has(run.task) && !['coverReassign', 'duplicateBuild', 'bookMerge', 'tagDuplicateBuild'].includes(run.task) && (run.task === 'import'
            ? i18n.t('tasks.importCounts', { added: counts.added, changed: counts.changed, unchanged: counts.unchanged, skipped: counts.skipped })
            : i18n.t('tasks.counts', { added: counts.added, changed: counts.changed, missing: counts.missing, returned: counts.returned, removed: counts.removed, unchanged: counts.unchanged })),
        run.summary,
    ];
    return parts.filter(Boolean).join(' · ');
}

/** Seconds as "0:42", "12:05" or "1:02:03" (tabular, for a ticking clock). */
export function formatElapsed(seconds: number): string {
    const total = Math.max(0, Math.floor(seconds));
    const [h, m, s] = [Math.floor(total / 3600), Math.floor(total / 60) % 60, total % 60];
    const two = (n: number) => String(n).padStart(2, '0');
    return h ? `${h}:${two(m)}:${two(s)}` : `${m}:${two(s)}`;
}

/** How long it ran, or has run so far (`now` while running). */
export function runElapsed(run: TaskRunInfo, now: number): string {
    const end = run.finished ? Date.parse(run.finished) : now;
    return formatElapsed((end - Date.parse(run.started)) / 1000);
}

/** "in 5 hours", "2 minutes ago": a time near now in words. */
export function formatRelative(utc: string, now: number, locale?: string): string {
    const seconds = (Date.parse(utc) - now) / 1000;
    const format = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
    const units: [Intl.RelativeTimeFormatUnit, number][] = [['day', 86400], ['hour', 3600], ['minute', 60]];
    for (const [unit, size] of units)
        if (Math.abs(seconds) >= size)
            return format.format(Math.round(seconds / size), unit);
    return format.format(Math.round(seconds), 'second');
}

/** A moment as the page shows it: short date and time. */
export function formatMoment(utc: string): string {
    return new Intl.DateTimeFormat(undefined, { dateStyle: 'short', timeStyle: 'short' }).format(new Date(utc));
}
