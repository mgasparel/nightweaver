import test from 'node:test';
import assert from 'node:assert/strict';
import { InvocationController, isCurrent, runtimeState } from '../src/delivery-model.ts';
const selected = { runtime: 'runtime-one', slot: { slot_id: 'delivery-1', status: 'running', invocation: { invocation_id: 'invocation-one' } } };
function setup() {
  const streams = [];
  const controller = new InvocationController(url => {
    const listeners = {};
    const stream = { url, closed: false, addEventListener(name, callback) { listeners[name] = callback; }, close() { this.closed = true; }, emit(name, data) { listeners[name]({ data: data === undefined ? undefined : JSON.stringify(data) }); } };
    streams.push(stream); return stream;
  }, () => {});
  controller.open(selected);
  return { controller, streams, source: streams[0] };
}
test('runtime state distinguishes occupancy, retained idle, stale, stopped and unavailable independently of tracker status', () => {
  const factory = { availability: 'live', snapshot: { runtime_id: selected.runtime, delivery: { slots: [structuredClone(selected.slot)] }, merger: { slots: [] } } };
  assert.equal(runtimeState(factory, false), 'live');
  factory.snapshot.delivery.slots[0].invocation.status = 'failed';
  assert.equal(runtimeState(factory, false), 'live');
  assert.equal(runtimeState(factory, true), 'stale');
  factory.snapshot.delivery.slots[0].status = 'idle';
  assert.equal(runtimeState(factory, false), 'idle');
  factory.availability = 'stopped'; assert.equal(runtimeState(factory, false), 'stopped');
  assert.equal(runtimeState(undefined, false), 'unavailable');
});
test('selection remains exact across retained idle, slot occupant and runtime replacement', () => {
  const factory = { snapshot: { runtime_id: selected.runtime, delivery: { slots: [structuredClone(selected.slot)] }, merger: { slots: [] } } };
  assert.equal(isCurrent(factory, selected), true);
  factory.snapshot.delivery.slots[0].status = 'idle'; assert.equal(isCurrent(factory, selected), true);
  factory.snapshot.delivery.slots[0].invocation.invocation_id = 'new'; assert.equal(isCurrent(factory, selected), false);
  factory.snapshot.delivery.slots[0] = structuredClone(selected.slot); factory.snapshot.runtime_id = 'new';
  assert.equal(isCurrent(factory, selected), false);
});
test('reconnect retains source; byte cursors reject duplicate and foreign output; failed terminal stops', () => {
  const { controller, source, streams } = setup();
  assert.deepEqual(Object.fromEntries(new URL(source.url, 'http://local').searchParams), { runtime_id: selected.runtime, slot_id: 'delivery-1', invocation_id: 'invocation-one' });
  source.emit('log', { cursor: 10, text: 'first' }); source.emit('error');
  assert.match(controller.state.status, /Reconnecting/); assert.equal(source.closed, false);
  source.emit('log', { cursor: 10, text: 'duplicate' }); source.emit('log', { invocationId: 'foreign', cursor: 20, text: 'wrong' });
  assert.equal(controller.state.text, 'first'); assert.equal(streams.length, 1);
  source.emit('log', { cursor: 20, text: 'second' });
  source.emit('end', { status: 'failed' }); assert.equal(source.closed, true); assert.equal(controller.state.status, 'Stream ended: failed');
  source.emit('log', { cursor: 30, text: 'late' }); assert.equal(controller.state.text, 'firstsecond');
});
test('output keeps UTF-8 and line/byte bounds, strips controls and resets byte cursor', () => {
  const { controller, source } = setup();
  source.emit('log', { cursor: 1, text: '\x1b[31m' + Array.from({ length: 2010 }, (_, i) => i).join('\n') + '\0' });
  assert.equal(controller.state.text.split('\n').length, 2000); assert.ok(!controller.state.text.includes('\x1b')); assert.ok(!controller.state.text.includes('\0'));
  source.emit('log', { cursor: 2, text: '界'.repeat(100000) });
  assert.ok(new TextEncoder().encode(controller.state.text).length <= 256 * 1024); assert.ok(!controller.state.text.includes('\ufffd'));
  assert.equal(controller.state.truncated, true);
  source.emit('reset', { cursor: 100 }); assert.equal(controller.state.text, ''); assert.equal(controller.state.truncated, false);
  source.emit('log', { cursor: 101, text: 'after reset' }); assert.equal(controller.state.text, 'after reset');
});
test('replacement and close invalidate callbacks of the old stream', () => {
  const { controller, source, streams } = setup();
  controller.open({ ...selected, runtime: 'runtime-two' });
  source.emit('end', { status: 'failed' }); source.emit('log', { cursor: 100, text: 'late' });
  assert.equal(controller.state.ended, false); assert.equal(controller.state.text, ''); assert.equal(source.closed, true);
  controller.close(); assert.equal(streams[1].closed, true);
});
