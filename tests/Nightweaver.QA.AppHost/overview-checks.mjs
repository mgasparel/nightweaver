import assert from 'node:assert/strict';

// Extend the existing Studio journey; only reads are substituted. No new native setup.
export async function overviewChecks(page, url, base, projectId, output) {
  const native = await (await page.request.get(`${url}/api/operations/v1/projects/${projectId}/resources?key=attention`)).json();
  assert.ok(native.data.length, 'Native fixture supplies an attention item');
  const decision = { ...native.data[0], beadId: 'qa-overview-decision', title: 'Choose the next delivery boundary', kind: 'product-decision-required', question: 'Which boundary should the next delivery verify?' };
  const longQuestion = 'Which delivery boundary should we verify?\n\n' + 'Preserve the complete current question and its evidence.\n'.repeat(80) + 'END OF CURRENT CONTEXT';
  let mode = 'decision';
  let releaseFactory;
  let factoryGate;
  let liveItems;
  await page.route('**/api/operations/v1/projects/*/resources?**', async route => {
    const key = new URL(route.request().url()).searchParams.get('key');
    if (!['attention', 'factory'].includes(key)) return route.continue();
    const actual = await route.fetch(); const value = await actual.json();
    // Keep the real coordinator recovering while Attention is already readable.
    // A pending sibling read must not let the longer header hide its Review link.
    if (key === 'factory' && mode === 'recovering-long-question') await factoryGate;
    value.health = 'fresh'; value.error = null; value.observedAt = new Date().toISOString();
    for (const source of Object.values(value.sources)) { source.health = 'fresh'; source.error = null; source.observedAt = value.observedAt; }
    if (key === 'attention') {
      value.data = mode === 'empty' ? [] : mode === 'unavailable' ? null : [structuredClone(decision)];
      if (mode === 'disclosure') value.data = liveItems;
      if (mode === 'long-question' || mode === 'recovering-long-question') value.data[0].question = longQuestion;
      if (mode === 'long') value.data = Array.from({length: 12}, (_, i) => ({ ...decision, beadId: 'qa-long-identity-' + i + '-'.repeat(90), title: 'Long operational title '.repeat(12), kind: i ? 'needs-human' : 'product-decision-required' }));
      if (mode === 'stale') { value.health = 'stale'; value.error = {code: 'fixture-stale', message: 'Retained attention requires current verification', retryable: true}; }
      if (mode === 'unavailable') { value.health = 'unavailable'; value.observedAt = null; }
    } else if (mode === 'unavailable' || mode === 'empty') value.data = { availability: 'unavailable' };
    await route.fulfill({ response: actual, json: value });
  });
  for (const state of ['decision', 'recovering-long-question', 'long-question', 'long', 'empty', 'stale', 'unavailable']) {
    mode = state;
    for (const width of [1440, 375, 720]) {
      factoryGate = new Promise(resolve => { releaseFactory = resolve; });
      try {
        await page.setViewportSize({width, height: 900});
        await page.goto(url + base + '/overview');
        await page.locator('.attention-panel').waitFor();
        if (state === 'empty') {
          await page.getByText('No paused Beads in this observation. Runtime activity is reported separately.').waitFor();
          assert.match(await page.locator('.overview-summary').innerText(), /0 paused Beads/);
          await page.waitForFunction(() => document.querySelector('.overview-summary').textContent.includes('Runtime · unavailable'));
        } else if (state === 'unavailable') {
          await page.getByText('Attention is unavailable. Refresh to try again.').waitFor();
          assert.match(await page.locator('.overview-summary').innerText(), /Attention unavailable/);
        } else {
          const review = page.locator('.attention-panel .attention-row .primary').first();
          await review.waitFor();
          if (state === 'decision' || state === 'long-question' || state === 'recovering-long-question') {
            if (state === 'recovering-long-question') await page.waitForFunction(() =>
              document.querySelector('.synchronization [role="status"]')?.textContent.includes('Recovering updates channel'));
            await page.screenshot({path: `${output}/overview-${state}-${width}-before-review.png`, fullPage: true});
            const box = await review.boundingBox();
            console.log(JSON.stringify({state, width, review: box, synchronization: await page.locator('.synchronization').innerText()}));
            assert.ok(box.y >= 0 && box.y + box.height <= 900, `Review visible without scrolling at ${width}: ${JSON.stringify(box)}`);
            const question = state === 'decision' ? decision.question : longQuestion;
            assert.equal(await page.locator('.attention-question').innerText(), Array.from(question).slice(0, 140).join('').replace(/\s+/g, ' ').trim());
            assert.equal(await page.locator('.attention-context').getAttribute('open'), null);
            await review.focus(); assert.equal(await review.evaluate(node => node === document.activeElement), true);
            assert.ok((await review.getAttribute('href')).endsWith('/beads/' + decision.beadId));
            if (state !== 'decision') {
              await page.keyboard.press('Tab');
              assert.equal(await page.locator('.attention-context > summary').evaluate(node => node === document.activeElement), true);
              await page.keyboard.press('Enter');
              assert.equal(await page.locator('.attention-context > p').first().innerText(), longQuestion);
              await page.keyboard.press('Enter');
              assert.equal(await page.locator('.attention-context').getAttribute('open'), null);
            }
          }
          if (state === 'long') assert.equal(await page.locator('.attention-row').count(), 3);
          if (state === 'stale') await page.getByRole('alert').filter({hasText: 'Retained attention requires current verification'}).waitFor();
        }
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${state} overflow at ${width}`);
        const attention = await page.locator('.attention-panel').boundingBox();
        const floor = await page.locator('.floor-panel').boundingBox();
        assert.ok(attention.y + attention.height <= floor.y, 'Attention precedes Delivery');
        await page.screenshot({path: `${output}/overview-${state}-${width}.png`, fullPage: true});
      } finally { releaseFactory(); }
    }
  }

  // Replace observations in the mounted Overview, rather than reloading away
  // the disclosure. Native stream resync hints trigger read-only replacements.
  await page.addInitScript(() => {
    const Native = window.EventSource;
    window.EventSource = class extends Native {
      constructor(...args) {
        super(...args);
        if (String(args[0]).includes('/operations/v1/')) {
          window.overviewStream = this;
          this.addEventListener('hello', event => { window.overviewHello = JSON.parse(event.data); });
        }
      }
    };
  });
  mode = 'disclosure';
  const rows = Array.from({length: 12}, (_, i) => ({...decision, beadId: `qa-context-${i}`, question: longQuestion}));
  liveItems = rows;
  await page.setViewportSize({width: 720, height: 900});
  await page.goto(url + base + '/overview');
  const row = page.locator('.attention-row').filter({has: page.getByRole('link', {name: 'Review qa-context-0', exact: true})});
  const context = row.locator('.attention-context');
  await context.locator('summary').click();
  await page.locator('h1').focus();
  const replace = async (items, expectedIds) => {
    liveItems = items;
    const response = page.waitForResponse(r => new URL(r.url()).searchParams.get('key') === 'attention');
    await page.waitForFunction(() => !!window.overviewHello);
    await page.evaluate(() => window.overviewStream.dispatchEvent(new MessageEvent('resync', {data: JSON.stringify(window.overviewHello)})));
    await response;
    await page.waitForFunction(ids => JSON.stringify([...document.querySelectorAll('.attention-row .bead-row-meta > span:first-child')].map(node => node.textContent)) === JSON.stringify(ids), expectedIds);
  };
  const reordered = [...rows.slice(1), rows[0]];
  await replace(reordered, ['qa-context-1', 'qa-context-2', 'qa-context-3', 'qa-context-0']);
  assert.equal(await context.getAttribute('open'), '');
  assert.equal(await context.locator('p').first().innerText(), longQuestion);
  assert.equal(await page.locator('h1').evaluate(node => node === document.activeElement), true);
  await page.screenshot({path: `${output}/overview-open-context-reordered-720.png`, fullPage: true});
  await replace(rows, ['qa-context-0', 'qa-context-1', 'qa-context-2']);
  assert.equal(await context.getAttribute('open'), '');
  // Closing releases the retained row once focus moves outside the inventory.
  await context.locator('summary').click();
  await page.locator('h1').focus();
  await replace(reordered, ['qa-context-1', 'qa-context-2', 'qa-context-3']);
  await replace(rows, ['qa-context-0', 'qa-context-1', 'qa-context-2']);
  assert.equal(await context.getAttribute('open'), null);
  await context.locator('summary').click();
  await page.locator('h1').focus();
  await replace(rows.slice(1), ['qa-context-1', 'qa-context-2', 'qa-context-3']);
  await replace(rows, ['qa-context-0', 'qa-context-1', 'qa-context-2']);
  assert.equal(await context.getAttribute('open'), null, 'Removed identity returns closed');
  console.log('Overview disclosure survives live reorder without focus; close and actual removal retire retention.');
}
