(() => {
  const byId = id => document.getElementById(`cleanup-${id}`);
  const rows = byId('rows'), range = byId('range');
  const labels = { pending: 'Pending', 'retry-scheduled': 'Retry scheduled', 'needs-attention': 'Needs attention', resolved: 'Resolved' };
  const disclosures = new Map(), busy = new Set(), messages = new Map(), requestIds = new Map();
  let page = 1, snapshot, generation = 0, timer, lastActionBead;
  const text = (tag, value, className) => {
    const node = document.createElement(tag); node.textContent = value;
    if (className) node.className = className;
    return node;
  };
  const date = value => value ? new Date(value).toLocaleString() : 'None';
  const announce = (node, value) => { if (node.textContent !== value) node.textContent = value; };
  function render(data) {
    snapshot = data; page = data.page;
    byId('scope').textContent = data.project ? `Project ${data.project} · ${data.repository}` : '';
    byId('health').textContent = data.counts
      ? `Owner ${data.canRetry ? 'live · accepting Retry' : data.availability + ' · Retry unavailable'}. Observed ${date(data.observedAt)}. ${data.reason ?? ''}`
      : `Unavailable — ${data.reason}`;
    byId('counts').replaceChildren(...Object.entries(data.counts ?? {}).map(([key, count]) =>
      text('span', `${count} ${labels[key]}`, `cleanup-count cleanup-${key}`)));
    const active = document.activeElement;
    const identity = rows.contains(active) ? active.dataset.cleanupFocus : null;
    const children = data.items.map(item => {
      const record = item.record;
      const card = text('article', '', `cleanup-row cleanup-${item.status}`);
      const heading = text('div', '', 'cleanup-row-heading');
      const link = text('a', item.beadId); link.href = `#bead/${encodeURIComponent(item.beadId)}`;
      link.dataset.cleanupFocus = `${item.beadId}:link`;
      heading.append(link, text('span', labels[item.status], 'cleanup-state')); card.append(heading);
      if (record.diagnostic) card.append(text('p', record.diagnostic));
      card.append(text('p', `${record.attempts} total attempts · Next: ${date(record.nextAttempt)}`, 'cleanup-help'));
      const details = document.createElement('details'); details.open = disclosures.get(item.beadId) ?? false;
      const summary = text('summary', 'Resources & attempt evidence'); summary.dataset.cleanupFocus = `${item.beadId}:details`;
      details.append(summary);
      details.addEventListener('toggle', () => { if (details.isConnected) disclosures.set(item.beadId, details.open); });
      for (const [name, resource] of [['Worktree', record.worktree], ['Temporary files', record.tmp]]) {
        details.append(text('p', `${name}: ${resource.confirmedAbsent ? 'confirmed absent' : 'not confirmed absent'}`), text('code', resource.path));
      }
      details.append(text('p', `Last attempt: ${date(record.lastAttempt)} · Cycle attempts: ${record.cycleAttempts ?? record.attempts} · Resolved: ${date(record.resolvedAt)}`));
      for (const [name, value] of [['Previous failure', record.lastFailure], ['Policy/access denial', record.lastDenial], ['Verified head', record.verifiedHead], ['Merge commit', record.mergeCommit]])
        if (value) details.append(text('p', `${name}: ${value}`));
      card.append(details);
      const retry = item.retry;
      if (item.status === 'resolved' && messages.has(item.beadId)) {
        messages.set(item.beadId, 'Cleanup confirmed: worktree, registration and temporary files are absent.');
        if (lastActionBead === item.beadId) announce(byId('message'), `${item.beadId}: ${messages.get(item.beadId)}`);
      }
      if (retry) {
        const outcome = retry.outcome === 'accepted' && record.lastRetryRequestId === retry.requestId && (record.cycleAttempts ?? 0) > 0
          ? 'attempted' : retry.outcome;
        card.append(text('p', `Cleanup Retry: ${outcome}${outcome === 'queued' ? ' — awaiting owner inspection' : ''}${['stale', 'protected', 'expired'].includes(outcome) ? ' — rejected; refresh and inspect before retrying' : ''}.`, 'cleanup-help'));
      }
      if (item.status === 'needs-attention') {
        const button = text('button', busy.has(item.beadId) ? 'Queuing…' : 'Retry cleanup', 'secondary-action');
        button.type = 'button'; button.dataset.cleanupFocus = `${item.beadId}:retry`;
        button.disabled = !data.canRetry || busy.has(item.beadId) || retry?.outcome === 'queued';
        button.addEventListener('click', () => retryCleanup(item)); card.append(button);
      }
      if (messages.has(item.beadId)) card.append(text('p', messages.get(item.beadId), 'cleanup-help'));
      return card;
    });
    rows.replaceChildren(...children);
    announce(range, data.counts ? data.total ? `${(page - 1) * 8 + 1}–${Math.min(page * 8, data.total)} of ${data.total} matching obligations · Page ${page} of ${data.pages}` : 'No matching cleanup obligations.' : 'Cleanup evidence unavailable; this is not an empty queue.');
    byId('previous').disabled = page <= 1 || !data.counts;
    byId('next').disabled = page >= data.pages || !data.counts;
    if (identity) {
      const replacement = [...rows.querySelectorAll('[data-cleanup-focus]')].find(node => node.dataset.cleanupFocus === identity && !node.disabled);
      (replacement ?? range).focus({ preventScroll: true });
    } else if (active?.disabled && [byId('previous'), byId('next')].includes(active)) range.focus({ preventScroll: true });
  }
  async function load() {
    const current = ++generation;
    const query = new URLSearchParams({ page, q: byId('search').value });
    if (byId('filter').value) query.set('status', byId('filter').value);
    try {
      const response = await fetch(`/api/cleanup?${query}`, { cache: 'no-store' });
      if (!response.ok) throw new Error('Housekeeping read failed. Refresh to inspect current evidence.');
      const data = await response.json();
      if (current === generation) render(data);
    } catch (error) {
      if (current === generation) render({ availability: 'unavailable', reason: error.message, counts: null, page: 1, pages: 1, items: [] });
    }
  }
  async function retryCleanup(item) {
    if (busy.has(item.beadId)) return;
    busy.add(item.beadId);
    lastActionBead = item.beadId;
    const owner = snapshot.runtimeId;
    const key = `${owner}:${item.beadId}:${item.revision}`;
    if (!requestIds.has(key)) requestIds.set(key, crypto.randomUUID().replaceAll('-', ''));
    render(snapshot);
    try {
      const response = await fetch(`/api/cleanup/${encodeURIComponent(item.beadId)}/retry`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Nightweaver-Request': 'console-v1' },
        body: JSON.stringify({ runtimeId: owner, revision: item.revision, requestId: requestIds.get(key) })
      });
      const result = await response.json();
      if (!response.ok) throw new Error(`${result.title}: ${result.detail}`);
      messages.set(item.beadId, result.request.outcome === 'queued'
        ? 'Queued for owner inspection. Accepted requests do not confirm disposal.'
        : `Request ${result.request.outcome}. Refresh and inspect the current obligation.`);
      if (result.request.outcome !== 'queued') requestIds.delete(key);
      byId('message').textContent = `${item.beadId}: ${messages.get(item.beadId)}`;
    } catch (error) {
      messages.set(item.beadId, error.message);
      byId('message').textContent = `${item.beadId}: ${error.message}`;
    } finally { busy.delete(item.beadId); await load(); }
  }
  byId('refresh').addEventListener('click', load);
  byId('filter').addEventListener('change', () => { page = 1; load(); });
  byId('search').addEventListener('input', () => { clearTimeout(timer); ++generation; timer = setTimeout(() => { page = 1; load(); }, 250); });
  for (const [name, direction] of [['previous', -1], ['next', 1]])
    byId(name).addEventListener('click', () => { page += direction; load(); });
  load(); setInterval(load, 5000);
})();
