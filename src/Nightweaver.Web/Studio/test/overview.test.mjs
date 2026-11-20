import assert from 'node:assert/strict';
import { test } from 'node:test';
import { attentionPreview, visibleAttention } from '../src/bead-model.ts';

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
