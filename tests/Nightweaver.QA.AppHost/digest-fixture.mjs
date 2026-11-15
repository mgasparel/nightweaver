// Adapt deterministic legacy digest projections to the shared v1 observation wire.
export async function digestFixture(page, read) {
  await page.route('**/api/operations/v1/projects/qa/resources?**', async route => {
    const key = new URL(route.request().url()).searchParams.get('key');
    if (!key?.startsWith('digest:')) return route.continue();
    const actual = await route.fetch();
    const value = await actual.json();
    const result = read(key.slice(7));
    if (result.fail) return route.fulfill({status: 503, json: {detail: 'Tracker offline'}});
    value.data = {...result.data, window: key.slice(7) === 'auto' ? result.data.window : key.slice(7)};
    value.health = 'fresh'; value.error = null; value.observedAt = new Date().toISOString();
    value.lastAttemptAt = value.observedAt;
    // Synthetic revisions are bounded to this test's response fixture, not runtime evidence.
    value.revision = String(BigInt(value.revision) + BigInt(Date.now()));
    for (const source of Object.values(value.sources)) { source.health = 'fresh'; source.error = null; source.observedAt = value.observedAt; }
    await route.fulfill({response: actual, json: value});
  });
}
export async function refreshDigest(page) {
  const response = page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/resources') && new URL(r.url()).searchParams.get('key')?.startsWith('digest:'));
  await page.locator('#digest-refresh').evaluate(button => button.click());
  await response;
  await page.waitForTimeout(80);
}
