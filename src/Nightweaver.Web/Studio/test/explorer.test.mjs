import { test } from 'node:test';
import assert from 'node:assert/strict';
import { explorerQuery } from '../src/explorer-model.ts';

test('Explorer defaults to operational readiness and keys include normalized Unicode search and page', () => {
  assert.equal(explorerQuery(new URLSearchParams()).key, 'explorer:ready::1');
  const query = explorerQuery(new URLSearchParams({ view: 'blocked', q: '  café  work  ', page: '3' }));
  assert.equal(query.key, 'explorer:blocked:Y2Fmw6kgIHdvcms:3');
  assert.equal(query.search, 'café  work');
});

test('malformed URL state never becomes an observation request', () => {
  for (const text of ['view=foreign', 'page=0', 'page=01', 'page=251', 'page=1.5', 'q='+ 'x'.repeat(201), 'view=all&view=ready', 'q=a&q=b', 'page=1&page=2']) {
    const result = explorerQuery(new URLSearchParams(text));
    assert(!result.valid); assert.equal(result.key, '');
  }
});
