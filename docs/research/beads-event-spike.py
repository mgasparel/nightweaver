"""Isolated ns-dkq experiment. Python stdlib + bd 1.3.1 + Git; no application startup.

Usage: python docs/research/beads-event-spike.py NEW_OUTPUT_DIRECTORY
Offline model: python docs/research/beads-event-spike.py --verify RESULT_JSON NEW_MODEL_JSON
Refuses an existing directory. Retains fixture, raw commands and results; never cleans.
"""
import ctypes
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import time


def verify_saved(result_path, output_path):
    """Deterministic cache model over captured real events; no CLI or database IO."""
    path = Path(result_path)
    result = json.loads(path.read_text(encoding="utf-8"))
    commands = json.loads((path.parent / "commands.json").read_text(encoding="utf-8"))
    events = {}
    for command in commands:
        if "events" not in command["args"] or "tail" not in command["args"] or command["exit"]:
            continue
        raw = command["stdout"].lstrip()
        while raw:
            row, end = json.JSONDecoder().raw_decode(raw)
            if "seq" in row:
                if row["seq"] in events:
                    assert events[row["seq"]] == row
                events[row["seq"]] = row
            raw = raw[end:].lstrip()
    assert sorted(events) == list(range(1, max(events)+1))
    projected = {}
    for _, event in sorted(events.items()):
        if event["op"] == "delete":
            projected.pop(event["issue_id"], None)
        else:
            projected[event["issue_id"]] = event["issue"]
    final = next(json.loads(c["stdout"]) for c in reversed(commands)
                 if "list" in c["args"] and "--all" in c["args"])
    assert set(projected) == {b["id"] for b in final}
    for bead in final:
        for field in ("title", "status", "priority", "assignee", "labels"):
            assert projected[bead["id"]].get(field) == bead.get(field), field
    # Generation-based dirty set: a duplicate is ignored; a new event during a
    # read cannot be cleared by completion of that older read.
    generation = 0
    seen = 0
    dirty = {}
    def receive(event):
        nonlocal generation, seen
        if event["seq"] <= seen:
            return
        seen = event["seq"]
        generation += 1
        dirty["inventory"] = generation
        if event["op"] == "comment":
            dirty["comments:" + event["issue_id"]] = generation
    ordered = [e for _, e in sorted(events.items())]
    for e in ordered[:-1]:
        receive(e)
    captured = dict(dirty)
    # Replay the old prefix; it creates no extra work.
    old_generation = generation
    for e in ordered[:-1]:
        receive(e)
    assert generation == old_generation
    receive(ordered[-1])
    for key, value in captured.items():
        if dirty.get(key) == value:
            del dirty[key]
    assert "inventory" in dirty and any(k.startswith("comments:") for k in dirty)
    first_completion_pending = sorted(dirty)
    dirty.clear()  # second read captures and completes the newest generation
    assert not dirty
    evidence = dict(assertions="passed", unique_real_events=len(events),
                    duplicate_prefix_ignored=len(ordered)-1,
                    first_read_completion_pending=first_completion_pending,
                    modeled_read_turns=2, final_inventory_core_fields_equal=True,
                    limitation="Deterministic model, not production scheduler integration or a race stress test")
    with Path(output_path).open("x", encoding="utf-8") as f:
        json.dump(evidence, f, indent=2)
    print(json.dumps(evidence, indent=2))


def main():
    root = Path(sys.argv[1]).resolve()
    root.mkdir(parents=True, exist_ok=False)
    repo = root / "fixture"
    repo.mkdir()
    env = {k: v for k, v in os.environ.items()
           if not k.upper().startswith(("BEADS_", "BD_", "GIT_"))}
    env.update(BD_IMPORT_AUTO="false", BD_EXPORT_AUTO="false", BD_BACKUP_ENABLED="false",
               BD_DOLT_AUTO_PUSH="false", BD_EVENTS_EXPORT="false", BD_DISABLE_METRICS="1",
               BD_DISABLE_EVENT_FLUSH="1", BD_NO_HOOKS="true", OTEL_SDK_DISABLED="true",
               BD_DOLT_AUTO_COMMIT="on", BD_DOLT_AUTO_START="false")
    log = []

    def run(args, ok=True):
        start = time.perf_counter()
        p = subprocess.run(args, cwd=repo, env=env, capture_output=True, text=True,
                           encoding="utf-8", timeout=30)
        record = dict(args=args, exit=p.returncode, ms=(time.perf_counter()-start)*1000,
                      stdout=p.stdout, stderr=p.stderr)
        log.append(record)
        (root / "commands.json").write_text(json.dumps(log, indent=2), encoding="utf-8")
        if ok and p.returncode:
            raise RuntimeError(record)
        return record

    def bd(*args, ok=True):
        return run(["bd", "--sandbox", *args, "--json"], ok)

    def rows(after=0):
        raw = bd("--readonly", "events", "tail", "--since", str(after))["stdout"]
        # Accept concatenated objects as well as indented error envelopes.
        decoder = json.JSONDecoder()
        values = []
        while raw.strip():
            value, end = decoder.raw_decode(raw.lstrip())
            values.append(value)
            raw = raw.lstrip()[end:]
        return values

    run(["git", "init", "-b", "main"])
    run(["git", "config", "user.name", "Isolated spike"])
    run(["git", "config", "user.email", "spike@example.invalid"])
    version = run(["bd", "version"])["stdout"].strip()
    bd("init", "--prefix", "probe", "--skip-hooks", "--skip-agents", "--non-interactive")
    bd("config", "set", "events-journal", "true")
    a = json.loads(bd("create", "--title", "A", "--type", "task")["stdout"])["id"]
    b = json.loads(bd("create", "--title", "B", "--type", "task")["stdout"])["id"]
    cases = []

    def case(name, args):
        before = rows()
        cursor = before[-1]["seq"] if before else 0
        bd(*args)
        events = rows(cursor)
        cases.append(dict(name=name, events=events))
        return events

    case("title", ["update", a, "--title", "A revised"])
    case("label", ["label", "add", a, "needs-human"])
    case("comment", ["comments", "add", a, "isolated evidence"])
    case("dependency-add", ["dep", "add", a, b])
    ready_blocked = json.loads(bd("--readonly", "ready")["stdout"])
    case("dependency-duplicate", ["dep", "add", a, b])
    case("blocker-close", ["close", b])
    ready_unblocked = json.loads(bd("--readonly", "ready")["stdout"])
    case("blocker-reopen", ["reopen", b])
    case("dependency-remove", ["dep", "remove", a, b])
    case("dependency-remove-missing", ["dep", "remove", a, b])
    case("claim", ["--actor", "probe-actor", "update", a, "--claim"])
    case("label-remove", ["label", "remove", a, "needs-human"])
    cursor = rows()[-1]["seq"]
    # A disconnected consumer resumes after its last processed sequence.
    bd("comments", "add", a, "written while disconnected")
    bd("update", b, "--priority", "1")
    replay = rows(cursor)
    assert len(replay) == 2 and replay == rows(cursor)
    events = rows()
    assert [e["seq"] for e in events] == list(range(1, len(events)+1))
    assert a not in {r["id"] for r in ready_blocked}
    assert a in {r["id"] for r in ready_unblocked}
    by_case = {c["name"]: c["events"] for c in cases}
    for name in ["title", "label", "comment", "dependency-add", "dependency-duplicate",
                 "blocker-close", "blocker-reopen", "dependency-remove", "claim", "label-remove"]:
        assert by_case[name], name
    assert not by_case["dependency-remove-missing"]
    assert any(e["op"] == "close" and e["issue_id"] == b for e in by_case["blocker-close"])
    # Representative invalidation model, deliberately not a production cache.
    def dirty(batch):
        result = set()
        for e in batch:
            result.add("inventory")
            if e["op"] == "comment":
                result.add("comments:" + e["issue_id"])
            if e["op"] in ("dep_add", "dep_remove"):
                result.update(("relations:" + e["issue_id"], "relations:" + e["dep"]["target"]))
            # readiness is derived and can affect transitive dependents
            if e["op"] != "comment":
                result.update(("ready", "blocked"))
        return sorted(result)
    burst = [e for c in cases for e in c["events"]]
    invalidation = dirty(burst)
    assert dirty(burst * 3) == invalidation
    assert dirty(by_case["comment"]) == sorted(["inventory", "comments:"+a])
    # SQL bypass is documented upstream but unsupported by this embedded CLI.
    sql = bd("sql", "UPDATE issues SET title = 'SQL bypass' WHERE id = '" + b + "'", ok=False)
    assert sql["exit"] != 0 and "not yet supported" in sql["stderr"]
    # A disabled-writer interval demonstrates why timer reconciliation is mandatory.
    before = rows()[-1]["seq"]
    bd("config", "set", "events-journal", "false")
    bd("update", b, "--title", "Unjournaled interval")
    disabled_tail = bd("--readonly", "events", "tail", "--since", str(before))
    assert rows(before) == []
    assert "disabled" in disabled_tail["stderr"]
    assert json.loads(bd("--readonly", "show", b)["stdout"])[0]["title"] == "Unjournaled interval"
    bd("config", "set", "events-journal", "true")
    case("delete", ["delete", b, "--force"])
    # One idle follower, same options as BeadsCommand, capture Windows process costs.
    head = rows()[-1]["seq"]
    out = open(root / "follow.stdout", "w", encoding="utf-8")
    err = open(root / "follow.stderr", "w", encoding="utf-8")
    p = subprocess.Popen(["bd", "--sandbox", "--readonly", "events", "tail", "--follow",
                          "--since", str(head), "--json"], cwd=repo, env=env, stdout=out, stderr=err)

    class IO(ctypes.Structure):
        _fields_ = [(n, ctypes.c_ulonglong) for n in
                    ["reads", "writes", "other", "read_bytes", "write_bytes", "other_bytes"]]

    def sample():
        io = IO()
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.GetProcessIoCounters.argtypes = [ctypes.c_void_p, ctypes.POINTER(IO)]
        if not kernel.GetProcessIoCounters(int(p._handle), ctypes.byref(io)):
            raise ctypes.WinError(ctypes.get_last_error())
        times = [ctypes.c_ulonglong() for _ in range(4)]
        kernel.GetProcessTimes.argtypes = [ctypes.c_void_p] + [ctypes.c_void_p]*4
        if not kernel.GetProcessTimes(int(p._handle), *[ctypes.byref(t) for t in times]):
            raise ctypes.WinError(ctypes.get_last_error())
        return dict(**{n: getattr(io, n) for n, _ in IO._fields_},
                    cpu_seconds=(times[2].value+times[3].value)/1e7)

    try:
        time.sleep(2)  # startup excluded
        s0 = sample()
        start = time.perf_counter()
        time.sleep(10)
        s1 = sample()
        idle = dict(seconds=time.perf_counter()-start,
                    delta={k: s1[k]-s0[k] for k in s0}, alive=p.poll() is None)
        assert idle["alive"]
        # A write must be delivered through the already-running tail.
        bd("comments", "add", a, "live follower delivery")
        expected = rows(head)
        deadline = time.monotonic()+5
        while "live follower delivery" not in (root/"follow.stdout").read_text(encoding="utf-8"):
            assert time.monotonic() < deadline, "live delivery timeout"
            time.sleep(.05)
    finally:
        p.terminate()
        p.wait(timeout=10)
        out.close()
        err.close()
    # Paired read costs, no follower: 5 samples per command, alternating order.
    commands = {"inventory": ["list", "--all", "--limit", "10001", "--include-ephemeral",
                               "--include-gates", "--include-infra", "--include-templates"],
                "comments": ["comments", a], "ready": ["ready", "--limit", "10001"],
                "empty-tail": ["events", "tail", "--since", str(rows()[-1]["seq"]) ]}
    costs = {k: [] for k in commands}
    for i in range(5):
        for name in list(commands)[::1 if i % 2 == 0 else -1]:
            r = bd("--readonly", *commands[name])
            costs[name].append(dict(ms=r["ms"], bytes=len(r["stdout"].encode("utf-8"))))
    # Force a genuine retention gap on the disposable database only.
    bd("config", "set", "events-journal-retain-days", "0")
    bd("config", "set", "events-journal-retain-rows", "0")
    bd("events", "prune", "--before", str(head))
    gap = bd("--readonly", "events", "tail", "--since", "1", ok=False)
    assert gap["exit"] != 0
    gap_data = json.loads(gap["stdout"])
    assert gap_data["code"] == "events_journal_truncated"
    recovery_inventory = json.loads(bd("--readonly", "list", "--all", "--limit", "0")["stdout"])
    assert len(recovery_inventory) == 1
    assert rows(gap_data["head"]) == []
    summary = dict(version=version, fixture=str(repo), cases=cases,
                   ready_blocked=[r["id"] for r in ready_blocked],
                   ready_unblocked=[r["id"] for r in ready_unblocked],
                   ordered_events=len(events), reconnect=replay, invalidation=invalidation,
                   burst_records=len(burst)*3, coalesced_flushes=1, idle_follower=idle,
                   live_events=expected, costs=costs, gap=gap_data,
                   sql_limitation=sql["stderr"], disabled_tail=disabled_tail,
                   recovery_ids=[r["id"] for r in recovery_inventory],
                   assertions="passed", note="Synthetic tiny fixture; OS logical IO, not physical disk bytes")
    (root/"result.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps({"result": str(root/"result.json"), "assertions": "passed",
                      "idle": idle, "median_ms": {k: statistics.median(x["ms"] for x in v)
                                                    for k, v in costs.items()}}, indent=2))


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "--verify":
        verify_saved(sys.argv[2], sys.argv[3])
    else:
        main()
