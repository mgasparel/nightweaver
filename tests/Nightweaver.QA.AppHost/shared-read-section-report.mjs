import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// Section receipts have their own state and identity. A base receipt cannot
// establish section completion. Keep retained state separate from new timings.
export function sectionReport(sample, browser, selected, cutoffMs) {
  const keys = sample.surface === 'workspace'
    ? ['text','history','relations','environment'].map(name => `section:${selected}:${name}`)
    : sample.surface === 'overview' ? [`section:${selected}:history`, 'digest-merges:auto']
    : sample.surface === 'digest' ? ['digest-merges:24h'] : [];
  return keys.map(key => {
    const receipts = browser.resources.filter(row => row.key === key && row.atMs >= sample.startMs && row.atMs < cutoffMs);
    const retained = sample.sections?.[key];
    const state = row => row?.data?.state ?? null;
    const complete = row => row?.health === 'fresh' && ['available','empty'].includes(state(row))
      && row.data.baseIdentity?.inventoryRevision;
    const first = receipts.find(complete);
    const latest = receipts.at(-1) ?? retained ?? null;
    const mergeStates = latest?.data?.sections
      ? Object.fromEntries(Object.entries(latest.data.sections).map(([id, value]) => [id,
        { state: value.state, source: value.source, updatedAt: value.updatedAt, reason: value.reason,
          validatedAgeMs: value.validatedAgeMs, pendingAgeMs: value.pendingAgeMs }])) : null;
    return { key, receiptCount: receipts.length, completedMs: first ? first.atMs - sample.startMs : null,
      retainedSnapshot: !first && complete(retained) ? retained : null,
      state: state(latest), sourceEnvelope: latest && { health: latest.health, sources: latest.sources }, mergeStates,
      unavailableReason: first ? null : mergeStates
        ? 'Aggregate receipt is not completion of every merge section. Each entry retains its own state and source.'
        : complete(retained) ? 'Available retained snapshot. No new completion receipt in this navigation interval.'
        : 'No validated successful section receipt before the next navigation or sample end.' };
  });
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const root = path.resolve(process.argv[2]);
  const metadata = JSON.parse(fs.readFileSync(path.join(root, 'result.json')));
  const trials = metadata.runs.map(run => {
    const directory = `${run.activity}-${run.surface}-${run.repetition}`;
    const browser = JSON.parse(fs.readFileSync(path.join(root, directory, 'browser.json')));
    return { directory, samples: browser.samples.map((sample, index) => ({
      surface: sample.surface, mode: sample.mode, cycle: sample.cycle, usefulMs: sample.usefulMs,
      cutoffMs: browser.samples[index + 1]?.startMs ?? sample.endMs,
      sections: sectionReport(sample, browser, browser.manifest.selected, browser.samples[index + 1]?.startMs ?? sample.endMs)
    })) };
  });
  fs.writeFileSync(process.argv[3], JSON.stringify({ schema: 'nightweaver-shared-read-sections-v1',
    revision: metadata.revision, latencyTarget: null,
    note: 'Explorer and unselected Attention request no detail section. Non-Workspace legacy detailMs fields on the candidate measure base-key completion only.', trials }, null, 2));
}
