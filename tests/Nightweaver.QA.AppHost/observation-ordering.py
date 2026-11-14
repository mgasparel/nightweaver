"""Disposable v1.1 protocol model, not an observer or transport implementation.

No tracker, filesystem, network, or wall-clock access. Run with Python 3.
The real backend/coordinator must repeat these cases against their own code.
"""

from collections import OrderedDict
from dataclasses import dataclass


@dataclass(frozen=True)
class Envelope:
    epoch: str
    revision: int
    data: str | None


class Host:
    def __init__(self, epoch="A", counter=0):
        self.epoch = epoch
        self.counter = counter
        self.entries = OrderedDict()

    def allocate(self):
        if self.counter == 2**64 - 1:
            raise OverflowError("503; shut down without publishing or wrapping")
        self.counter += 1
        return self.counter

    def admit(self, key, now):
        if key not in self.entries:
            envelope = Envelope(self.epoch, self.allocate(), None)
            self.entries[key] = (object(), envelope, now)
        self.entries.move_to_end(key)
        return self.entries[key][1]

    def collect(self, key, identity, data):
        current, old, idle_since = self.entries[key]
        if current is not identity:
            return False
        envelope = old if old.data == data else Envelope(
            self.epoch, self.allocate(), data
        )
        self.entries[key] = (current, envelope, idle_since)
        return True

    def evict(self, reason, now):
        # All entries in these scenarios are unpinned after disconnect grace.
        if reason == "ttl":
            for key, (_, _, idle_since) in list(self.entries.items()):
                if now - idle_since >= 30:
                    del self.entries[key]
        elif reason == "lru":
            while len(self.entries) > 2:
                self.entries.popitem(last=False)
        else:
            raise ValueError(reason)


class Client:
    def __init__(self):
        self.epoch = None
        self.generation = 0
        self.watermark = 0
        self.observation = None

    def disconnect(self):
        self.generation += 1

    def hello(self, envelope):
        self.generation += 1
        if self.epoch != envelope.epoch:
            self.epoch = envelope.epoch
            self.watermark = 0
            self.observation = None
        self.watermark = max(self.watermark, envelope.revision)

    def accept(self, envelope, generation):
        if (generation != self.generation or envelope.epoch != self.epoch
                or envelope.revision < self.watermark):
            return False
        self.watermark = envelope.revision
        self.observation = envelope
        return True


def eviction_case(reason, changed):
    host, client, key = Host(), Client(), "workspace:qa-1"
    host.admit(key, 0)
    old_identity = host.entries[key][0]
    assert host.collect(key, old_identity, "before")
    old = host.entries[key][1]
    client.hello(old)
    old_generation = client.generation
    assert client.accept(old, old_generation)
    client.disconnect()
    assert not client.accept(old, old_generation)
    if reason == "lru":
        host.admit("explorer:ready:", 31)
        host.admit("attention", 31)
    host.evict(reason, 61)  # Past disconnect grace plus idle TTL.
    assert key not in host.entries
    source = "after" if changed else "before"
    hello = host.admit(key, 61)
    assert hello.epoch == old.epoch and hello.revision > old.revision
    client.hello(hello)
    generation = client.generation
    assert client.accept(hello, generation)
    assert client.observation.data is None
    new_identity = host.entries[key][0]
    assert host.collect(key, new_identity, source)
    new = host.entries[key][1]
    assert new.revision > hello.revision
    assert client.accept(new, generation) and client.observation.data == source
    counter = host.counter
    assert not host.collect(key, old_identity, "late obsolete collection")
    assert host.counter == counter and host.entries[key][1] == new
    assert not client.accept(old, old_generation)
    assert not client.accept(old, generation)  # Watermark independently guards.
    assert client.observation == new
    # No eviction: equal revision reconnect/freshness response remains legal.
    client.disconnect()
    client.hello(new)
    assert client.accept(new, client.generation)
    # Epoch change permits a lower revision, but never old-host responses.
    replacement = Host(epoch="B").admit(key, 0)
    client.hello(replacement)
    assert client.accept(replacement, client.generation)
    assert not client.accept(new, client.generation)
    assert client.observation == replacement
    print(f"PASS {reason}: changed={changed}, {old.revision} < "
          f"{hello.revision} < {new.revision}; obsolete results rejected")


def main():
    for reason in ("ttl", "lru"):
        for changed in (False, True):
            eviction_case(reason, changed)
    exhausted = Host(counter=2**64 - 1)
    try:
        exhausted.admit("attention", 0)
    except OverflowError:
        assert not exhausted.entries and exhausted.counter == 2**64 - 1
    else:
        raise AssertionError("Counter must fail closed, never wrap")
    # Demonstrate the reported v1 per-key reset fails the retained watermark.
    client = Client()
    prior = Envelope("A", 9, "before")
    client.hello(prior)
    assert client.accept(prior, client.generation)
    client.disconnect()
    reset = Envelope("A", 0, None)
    client.hello(reset)
    assert not client.accept(reset, client.generation)
    assert not client.accept(Envelope("A", 1, "after"), client.generation)
    print("PASS exhaustion without wrap; old per-key reset reproduces rejection")


if __name__ == "__main__":
    main()
