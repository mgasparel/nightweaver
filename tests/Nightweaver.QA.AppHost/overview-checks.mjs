import assert from 'node:assert/strict';

// Extend the existing Studio journey; only reads are substituted. No new native setup.
export async function overviewChecks(page, url, base, projectId, output) {
  const native = await (await page.request.get(`${url}/api/operations/v1/projects/${projectId}/resources?key=attention`)).json();
  assert.ok(native.data.length, 'Native fixture supplies an attention item');
  const decision = { ...native.data[0], beadId: 'qa-overview-decision', title: 'Choose the next delivery boundary', kind: 'product-decision-required', question: 'Which boundary should the next delivery verify?' };
  const longQuestion = 'Which delivery boundary should we verify?\n\n' + 'Preserve the complete current question and its evidence.\n'.repeat(80) + 'END OF CURRENT CONTEXT';
  let mode = 'decision';
  await page.route('**/api/operations/v1/projects/*/resources?**', async route => {
    const key = new URL(route.request().url()).searchParams.get('key');
    if (!['attention', 'factory'].includes(key)) return route.continue();
    const actual = await route.fetch(); const value = await actual.json();
    value.health = 'fresh'; value.error = null; value.observedAt = new Date().toISOString();
    for (const source of Object.values(value.sources)) { source.health = 'fresh'; source.error = null; source.observedAt = value.observedAt; }
    if (key === 'attention') {
      value.data = mode === 'empty' ? [] : mode === 'unavailable' ? null : [structuredClone(decision)];
      if (mode === 'long-question') value.data[0].question = longQuestion;
      if (mode === 'long') value.data = Array.from({length: 12}, (_, i) => ({ ...decision, beadId: 'qa-long-identity-' + i + '-'.repeat(90), title: 'Long operational title '.repeat(12), kind: i ? 'needs-human' : 'product-decision-required' }));
      if (mode === 'stale') { value.health = 'stale'; value.error = {code: 'fixture-stale', message: 'Retained attention requires current verification', retryable: true}; }
      if (mode === 'unavailable') { value.health = 'unavailable'; value.observedAt = null; }
    } else if (mode === 'unavailable' || mode === 'empty') value.data = { availability: 'unavailable' };
    await route.fulfill({ response: actual, json: value });
  });
  for (const state of ['decision', 'long-question', 'long', 'empty', 'stale', 'unavailable']) {
    mode = state;
    for (const width of [1440, 375, 720]) {
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
        if (state === 'decision' || state === 'long-question') {
          const box = await review.boundingBox();
          assert.ok(box.y >= 0 && box.y + box.height <= 900, `Review visible without scrolling at ${width}: ${JSON.stringify(box)}`);
          const question = state === 'long-question' ? longQuestion : decision.question;
          assert.equal(await page.locator('.attention-question').innerText(), Array.from(question).slice(0, 140).join('').replace(/\s+/g, ' ').trim());
          assert.equal(await page.locator('.attention-context').getAttribute('open'), null);
          await review.focus(); assert.equal(await review.evaluate(node => node === document.activeElement), true);
          assert.ok((await review.getAttribute('href')).endsWith('/beads/' + decision.beadId));
          if (state === 'long-question') {
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
    }
  }
}
