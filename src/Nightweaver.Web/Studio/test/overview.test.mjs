import assert from 'node:assert/strict';
import { test } from 'node:test';
import { attentionPreview, survivingAttentionDisclosures, visibleAttention } from '../src/bead-model.ts';

test('current context preview bounds multiline evidence without splitting Unicode characters', () => {
  assert.equal(attentionPreview('Which boundary?'), 'Which boundary?');
  assert.equal(attentionPreview('a\n\n'.repeat(100)), ('a '.repeat(46) + 'a').trim());
  assert.equal(attentionPreview('😀'.repeat(141)), '😀'.repeat(140));
});

test('attention preview stays bounded while a focused row moves beyond its boundary', () => {
  const items = Array.from({length: 12}, (_, i) => ({beadId: `paused-${i}`}));
  assert.deepEqual(visibleAttention(items, 3), items.slice(0, 3));
  const reordered = [...items.slice(1), items[0]];
  assert.deepEqual(visibleAttention(reordered, 3, items[0].beadId), [...reordered.slice(0, 3), items[0]]);
  assert.deepEqual(visibleAttention(reordered, 3), reordered.slice(0, 3));
});

test('attention preview never resurrects a removed focused row or fabricates empty items', () => {
  assert.deepEqual(visibleAttention([], 3, 'removed'), []);
  const items = [{beadId: 'recovery'}, {beadId: 'decision'}];
  assert.deepEqual(visibleAttention(items, 3, 'removed'), items);
});

test('opened context survives reorder independently of focus, and closing restores the bound', () => {
  const items = Array.from({length: 12}, (_, i) => ({beadId: `paused-${i}`}));
  const opened = new Set([items[0].beadId]);
  const reordered = [...items.slice(1), items[0]];
  assert.deepEqual(visibleAttention(reordered, 3, undefined, opened), [...reordered.slice(0, 3), items[0]]);
  assert.equal(survivingAttentionDisclosures(reordered, opened), opened);
  assert.deepEqual(visibleAttention(items, 3, undefined, opened), items.slice(0, 3));
  assert.deepEqual(visibleAttention(reordered, 3, undefined, new Set()), reordered.slice(0, 3));
});

test('actual removal retires opened identities while unavailable data preserves context', () => {
  const opened = new Set(['survivor', 'removed']);
  assert.equal(survivingAttentionDisclosures(null, opened), opened);
  const remaining = survivingAttentionDisclosures([{beadId: 'survivor'}], opened);
  assert.deepEqual([...remaining], ['survivor']);
  assert.deepEqual(visibleAttention([{beadId: 'a'}, {beadId: 'b'}, {beadId: 'c'}, {beadId: 'removed'}], 3, undefined, remaining).map(row => row.beadId), ['a', 'b', 'c']);
  assert.equal(survivingAttentionDisclosures([], remaining).size, 0);
});
