# Operational synchronization

The [v1.1 observation contract](operational-observations-v1.md) is implemented by
the shared observer, `OperationalEventStream`, and Studio's `QueryCoordinator`.
Invocation output keeps its independent existing telemetry stream.

`GET /api/operations/v1/projects/{projectId}/events?key=...` validates configured
Project identity and canonical keys, admits at most 16 streams, then uses the
observer's atomic subscribe/pin/version snapshot. It writes hello followed by
reconnect resync. Changes during hello stay queued. Each subscriber retains only
the latest revision per key, at most 64 keys/64 KiB; overflow replaces pending
invalidations with resync. Network writes/flushes have a five-second timeout.
Ten-second heartbeats carry no observation freshness authority. Cancellation
unsubscribes and releases pins without stopping shared collection. There is no
event ID, replay log, or Last-Event-ID resume assumption.

## Frontend API

`Studio/src/operations.ts` has no React, DOM lifecycle or network dependencies.
Its transport and scheduler are injected for deterministic tests. The browser
adapter exports one `operations` coordinator and one `interactions` store per
tab; feature components must reuse them. `useObservation(projectId, key)` adapts
that owner through React's external-store subscription API. It surfaces admission
errors in the snapshot rather than crashing rendering.

```ts
const identity = { projectId, key: "workspace:" + beadId };
const unsubscribe = operations.subscribe(identity, render);
const { observation, connectionState, stale, refreshing, error } =
  operations.getSnapshot(identity);
await operations.refresh([identity]); // resolves on 202 acceptance only
unsubscribe();
```

Identity includes the exact configured Project and canonical resource key.
`queryKey` encodes trimmed UTF-8 search for Explorer/Housekeeping while preserving
case and interior spaces. Components must not create another response cache or
EventSource. Selection and mounting coalesce stream replacement; old connection
closes before new connection opens. At most 32 keys are active. A different
Project cannot join the current Project's stream.

Reads begin only after complete hello. Every response must match entry lifetime,
request object, connection generation, Project, key and epoch. BigInt revision
comparison uses both the maximum advertised version and the retained accepted
watermark. Watermarks survive same-epoch reconnects with their cache entry;
new epochs reset them and mark retained display stale. Higher-revision initial
unavailable envelopes after server eviction replace old data. An invalidation
during a read coalesces one follow-up, with no simultaneous reads per key. HTTP
failures and behind-watermark responses retain stale display; the periodic
freshness read retries without a busy retry loop.

While visible, a two-second timer refetches cached authoritative envelopes even
at equal revision. Focus/visibility restoration resynchronizes. Twenty-five
seconds without frames disconnects immediately and invalidates requests, then
retries with jittered exponential backoff capped at fifteen seconds. Stream
errors also close the native EventSource, so its built-in reconnect cannot race
the coordinator. Heartbeats update connection liveness only. Render observation
health/source states separately from connectionState and the coordinator's
stale flag; retained display is never current while handshake/read is pending.

Manual Refresh POSTs reconciliation, then reads the cache; acceptance is not
observation success. `refreshing` remains true until a changed attempt timestamp
is observed. The UI still allows another explicit Refresh if collection stalls.
Concurrent equal refresh sets share a POST. After an HTTP command completes or
has an uncertain outcome, call `invalidateAfterCommand("tracker")` for workflow,
guidance or intake, `"checkpoint"` for catch-up, or `"cleanup"` for cleanup Retry.
These methods reconcile the corresponding active resource families. They never
send/retry a command, fabricate a revision, or bypass authoritative guards.

The clean cache has 64 entries/16 MiB, inactive LRU eviction and 30-second idle
TTL. Active data is never silently evicted; oversized responses preserve prior
display with an explicit storage error. Eviction invalidates the old entry's
requests and watermark. Interaction drafts are separately keyed by Project+Bead:
`interactions.get/set/discard`. Twenty dirty drafts/1 MiB are supported in memory;
excess writes throw a visible-limit error and leave every draft unchanged.
Consumers must display that error and offer explicit send/discard. Guidance is
never automatically persisted. Feature slices own disclosure presentation.

Studio's initial consumers are the Attention navigation/overview count and compact
connection/source status with manual Refresh. Feature bodies and commands remain
owned by subsequent migration slices. A foreign Project route opens no stream.

## Verification

From `src/Nightweaver.Web/Studio`, run `npm ci --no-audit --no-fund`, `npm test`
and `npm run build`. The tests use Node's built-in runner and TypeScript transform
(CI Node 24); no separate unit-test framework is required. They use fake transport,
clock and deferred HTTP promises. The hosted workflow runs them after the normal
solution build under the existing draft gating policy.

`OperationalEventStreamTests` exercises real HTTP framing against a fake source,
publication during hello, host shutdown, admission validation, bounded
overflow/coalescing and cancellation/capacity reuse.
Run with `dotnet test --project tests/Nightweaver.Tests --filter-class
Nightweaver.Tests.OperationalEventStreamTests --minimum-expected-tests 9`.

For the real browser stream, follow the exact isolated Aspire lifecycle in
[QA AppHost README](../../tests/Nightweaver.QA.AppHost/README.md), then run
`node operations-qa.mjs`. It verifies a real stream across route mounts, external
disposable tracker mutation with notifications deliberately missed, automatic
count convergence, reconnect hello/resync, guarded refresh acceptance, no command
replay and foreign Project isolation. It captures desktop/375/720 screenshots.
Always stop that exact AppHost afterward.

Studio's MSBuild target runs before static-asset discovery and removes evaluated
old asset items before adding Vite's new hashed files. This supports an ordinary
incremental build after frontend changes as well as a source-only build/publish.
