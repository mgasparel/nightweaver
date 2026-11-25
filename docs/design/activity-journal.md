# Activity journal v1

The Activity journal stores bounded display evidence. It has no lifecycle
authority. This slice adds storage and a reader. A later slice connects runtime
producers and HTTP routes. Existing factory work calls neither module yet.

`HostPaths.ActivityRoot` selects `<root>/telemetry/<project>/activity`. The store
binds the Project, full Target Repository path, GitHub repository and journal ID.
It rejects storage inside the Target Repository. Windows directory pins reject
reparse points. Read and mutation handles reject hard links. The writer holds
one exclusive `writer.lock` until all storage work settles.

## Submission and failure

One `ActivityJournal` owns one background worker and one 512-entry channel.
`TrySubmit` checks input lengths and tries one queue write. It does not serialize,
open a file, wait for storage or call a viewer. Source adapters supply typed
evidence and accepted merge proof. The worker uses `ActivityProjector` before
writing. Queued identifiers and merge proof have finite size limits.

A full queue drops the new entry. Saturating loss counters remain outside the
queue. The worker writes `RecordsLost` before the next ordinary record. The loss
reason stays unspecified because queue loss and rejected input can share that
count. Counts above the schema limit become unknown in the record. The health
object retains the full saturating count for this runtime.

`Health` exposes availability, a safe reason code and runtime loss count. It
never stores exception text. An uncertain storage failure closes the writer
queue and drops pending entries. This prevents an append after a torn write.
The next runtime performs recovery. A failed writer does not restart the
factory or change a claim. Runtime replacement reports that earlier records can
be missing. Shutdown allows two seconds for settlement, then requests
cancellation and returns. A blocked worker retains its lease and bounded queue
until it settles. Force stop needs no compensating journal work.

## Format and retention

The journal has one canonical JSON manifest. It creates the manifest once with
an exclusive safe handle. It uses no temporary manifest. The manifest limit is
16 KiB. An incomplete, corrupt or foreign manifest makes storage unavailable.
The store preserves all retained files in that case.

Eight fixed names, `segment-0.activity` through `segment-7.activity`, are the only
segment names that this module reads or retires. Each segment has a 4096-byte
identity header and at most 255 record slots. Each slot contains one canonical
JSON record, a newline and zero padding. The newline and padding must be valid.
All slots count against the byte budget. A segment is at most 1 MiB. Eight
segments hold at most 2040 records and 8 MiB, below the contract's 4096-record
ceiling. Padding trades record density for direct bounded seeks. There is no
minimum retention duration or retained count guarantee.

The writer retires the oldest whole segment before it creates a replacement.
It validates the opened header before retirement. It does not enumerate or
remove raw logs, transcripts, snapshots, unknown names or unrelated files.
Record writes flush managed buffers. They do not request a physical disk barrier
for each observation. Initial manifest and segment headers use a disk flush.
Activity evidence does not promise loss-free storage after a machine failure.

Startup scans at most eight segments, 8 MiB and 2040 records. It keeps complete
records before a partial tail. It marks that tail as truncated with a history
gap. A corrupt record makes its segment unavailable. Other valid segments remain
readable. A corrupt or foreign header stops writer recovery. Safe readers can
still return other valid segments with an invalid/gap state. The module preserves
unknown header data. It never repairs it by guessing identity.

A new runtime seals old segments and starts its own segment. It reserves unused
offsets in the last old segment, so offsets can have gaps. Offsets never use wall
time. Sequences increase within a runtime. Reusing a retained runtime ID fails.
Exhausted counters fail closed before wrap. They grant no recovery authority.

## Bounded shared reads

One `ActivityJournalFiles` reader can serve all viewers in a Web host. It needs
no writer lease, producer queue or per-viewer file reader. Each read checks the
manifest and at most eight fixed headers. It then seeks directly to slots after
the cursor. The total metadata, predecessor and record bytes examined stay
within 256 KiB. It returns at most 100 records. A reader on the writer instance
uses the worker's bounded identity snapshot instead of rereading headers first.

The page contains the available oldest/newest offsets and a versioned cursor.
The cursor carries journal ID and last examined offset. A filter uses closed
kind, phase and strength enums and one safe Bead ID. An empty filtered page can
advance its cursor. A caller can continue without scanning earlier records.

Page flags distinguish unknown, invalid, truncated, expired and gap states.
A foreign or expired cursor returns a gap and restarts at the oldest available
record. A future offset or unsupported cursor version is invalid. A corrupt
segment advances continuation past that segment with an explicit gap. Reads
re-project records and require their canonical bytes, fixed messages, schema,
identity, runtime, sequence and safe fields. A viewer cannot supply a path.
Reader handles allow append and retirement. They never make the writer wait for
a viewer. HTTP and streaming policy belong to the later transport slice.

## Verification

`ActivityJournalTests` uses held and failed storage plus a controlled clock for
queue bounds, loss, shutdown, cancellation and clock changes. Disposable files
prove actual write/read framing, rotation, byte and record bounds, cursor
continuation, partial tails, corrupt records, runtime replacement, identity
rejection and shared reads. Sentinel checks preserve unrelated data. A short
junction command and one hard-link call prove the actual Windows no-follow
boundary. An injected path response cannot prove this boundary. These tests add
no factory, Agent, tracker, Aspire or browser journey and no fixture publication.
