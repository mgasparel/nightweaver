// Adapt deterministic legacy digest projections to the shared v1 observation wire.
export async function digestFixture(page, read) {
  await page.route('**/api/operations/v1/projects/qa/resources?**', async route => {
    const key = new URL(route.request().url()).searchParams.get('key');
    if (!key?.startsWith('base:digest:') && !key?.startsWith('digest-merges:')) return route.continue();
    const actual = await route.fetch();
    const value = await actual.json();
    const window = key.startsWith('base:digest:') ? key.slice(12) : key.slice(14);
    const result = read(window);
    if (result.fail) return route.fulfill({status: 503, json: {detail: 'Tracker offline'}});
    const item = row => ({bead: {...row, updatedAt: row.activityAt}, activityAt: row.activityAt, activityKind: row.activityKind, fromId: row.fromId, fromRelation: row.fromRelation});
    const data = result.data;
    value.data = key.startsWith('base:digest:') ? {...data, window: window === 'auto' ? data.window : window,
      threads: data.threads.map(thread => ({...thread, root: item(thread.root), items: thread.items.map(item)})),
      standaloneCompleted: data.standaloneCompleted.map(item), standaloneOpen: data.standaloneOpen.map(item)} : {
      schema: 'nightweaver-digest-merges-v1', sections: Object.fromEntries([...data.threads.flatMap(thread => [thread.root, ...thread.items]), ...data.standaloneCompleted, ...data.standaloneOpen].map(row => [row.id, {
        beadId: row.id, updatedAt: row.activityAt, state: 'unavailable', pending: false, validatedAgeMs: 0, value: {state: 'missing'},
        source: {health: 'fresh', observedAt: new Date().toISOString()}, outcomeLabel: row.outcomeLabel, summary: row.summary, commits: row.commits, pullRequests: row.pullRequests
      }]))};
    value.health = 'fresh'; value.error = null; value.observedAt = new Date().toISOString();
    value.lastAttemptAt = value.observedAt;
    // Synthetic revisions are bounded to this test's response fixture, not runtime evidence.
    value.revision = String(BigInt(value.revision) + BigInt(Date.now()));
    for (const source of Object.values(value.sources)) { source.health = 'fresh'; source.error = null; source.observedAt = value.observedAt; }
    await route.fulfill({response: actual, json: value});
  });
}
export async function refreshDigest(page) {
  const response = page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/resources') && new URL(r.url()).searchParams.get('key')?.startsWith('base:digest:'));
  await page.locator('#digest-refresh').evaluate(button => button.click());
  await response;
  await page.waitForTimeout(80);
}
