"""Disposable CLI/HTTP probe, not a production observer. Start exact QA AppHost first.

python observation-cost.py --output <results.json>
Discovers only this AppHost's web resource. Never accepts a repository path.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', required=True)
    parser.add_argument('--runtime-only', action='store_true',
                        help='Append independent two-second runtime samples to existing evidence')
    args = parser.parse_args()
    apphost = Path(__file__).resolve().with_name('Nightweaver.QA.AppHost.csproj')
    describe = subprocess.run(
        [shutil.which('aspire'), 'describe', '--format', 'Json', '--non-interactive',
         '--apphost', str(apphost)], check=True, capture_output=True, text=True).stdout
    resources = json.loads(describe[describe.index('{'):])['resources']
    web, = [r for r in resources if r['displayName'] == 'web']
    config = Path(web['environment']['Nightweaver__ConfigPath']).resolve()
    root = config.parent
    assert root.name.startswith('nightweaver-qa-'), 'Not a disposable QA root'
    assert config.name == 'project.json' and root.is_dir()
    repo = Path(json.loads(config.read_text())['target_repository']['path']).resolve()
    assert repo == root / 'repo' and (repo / '.beads').is_dir()
    base, = [u['url'] for u in web['urls'] if u['name'] == 'http']
    env = dict(os.environ)
    for key in list(env):
        if key.startswith(('BEADS_', 'BD_', 'GIT_')):
            del env[key]
    for key in ('HOME', 'USERPROFILE', 'APPDATA', 'XDG_CONFIG_HOME'):
        env[key] = str(root / 'home')
    env.update(GIT_CONFIG_GLOBAL=str(root / 'home' / 'gitconfig'), GIT_CONFIG_NOSYSTEM='1',
               BD_DISABLE_METRICS='1', BD_NO_HOOKS='true', BD_DOLT_AUTO_START='false',
               BEADS_DOLT_SERVER_HOST='127.0.0.1', OTEL_SDK_DISABLED='true')
    for key in ('BD_IMPORT_AUTO', 'BD_EXPORT_AUTO', 'BD_BACKUP_ENABLED', 'BD_DOLT_AUTO_PUSH',
                'BD_EVENTS_EXPORT'):
        env[key] = 'false'
    commands = []

    def bd(*argv, read=True):
        start = time.perf_counter()
        command = (['--readonly', '--sandbox'] if read else ['--sandbox']) + list(argv)
        result = subprocess.run([shutil.which('bd'), *command], cwd=repo, env=env,
                                capture_output=True, encoding='utf-8', timeout=30, check=True)
        elapsed = (time.perf_counter() - start) * 1000
        commands.append(dict(args=command, ms=round(elapsed, 2),
                             bytes=len(result.stdout.encode('utf-8'))))
        return json.loads(result.stdout) if '--json' in argv else result.stdout

    def inventory():
        return bd('list', '--all', '--limit', '0', '--include-ephemeral', '--include-gates',
                  '--include-infra', '--include-templates', '--json')

    def http(path):
        start = time.perf_counter()
        with urllib.request.urlopen(base + path, timeout=30) as response:
            value = json.load(response)
        return value, round((time.perf_counter() - start) * 1000, 2)

    def digest(value):
        return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()

    def sample_runtime():
        samples = []
        start = time.perf_counter()
        for index in range(4):
            if index:
                time.sleep(max(0, start + index * 2 - time.perf_counter()))
            state, elapsed = http('/api/factory/state')
            samples.append(dict(atMs=round((time.perf_counter()-start)*1000, 2),
                                httpMs=elapsed, availability=state['availability'],
                                observedAt=(state.get('snapshot') or {}).get('observed_at')))
        return samples

    if args.runtime_only:
        output = json.loads(Path(args.output).read_text(encoding='utf-8'))
        output['runtimeSamples'] = sample_runtime()
        Path(args.output).write_text(json.dumps(output, indent=2) + '\n', encoding='utf-8', newline='\n')
        print(json.dumps(output['runtimeSamples']))
        return

    def cycle(selected, paused):
        # Serial sources, releasing the conceptual observer lane between commands.
        start = time.perf_counter()
        inv = inventory()
        ready = bd('ready', '--limit', '0', '--json')
        blocked = bd('blocked', '--json')
        comments = {bead: bd('comments', bead, '--json') for bead in paused}
        detail = bd('show', selected, '--json')
        up = bd('dep', 'list', selected, '--direction', 'up', '--json')
        down = bd('dep', 'list', selected, '--direction', 'down', '--json')
        selected_comments = comments.get(selected)
        if selected_comments is None:
            selected_comments = bd('comments', selected, '--json')
        final = inventory()
        return dict(inventory=inv, ready=ready, blocked=blocked, comments=comments,
                    detail=detail, up=up, down=down, selectedComments=selected_comments,
                    finalInventory=final), round((time.perf_counter() - start) * 1000, 2)

    output = dict(schema='nightweaver-observation-cost-v1',
                  bdVersion=bd('version'), fixtureSizes=[], commands=commands,
                  measuredAt=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
                  runtimeSamples=sample_runtime())
    for size, pause_count in [(50, 4), (2000, 32)]:
        current = inventory()
        added = size - len(current)
        assert added > pause_count, 'Start a fresh QA AppHost before running this probe'
        # Public CLI import only; no database files, tables or production data.
        records = [dict(id=f'qa-cost{i:05}', title=f'Observation fixture {i}',
                        description='Representative specification. ' * 32,
                        issue_type='task', status='open', priority=2,
                        created_at='2026-11-14T00:00:00Z', updated_at='2026-11-14T00:00:00Z')
                   for i in range(len(current), len(current) + added)]
        seed = root / f'observation-{size}.jsonl'
        seed.write_text('\n'.join(json.dumps(r) for r in records), encoding='utf-8')
        bd('import', str(seed), read=False)
        inv = inventory()
        assert len(inv) == size
        selected = records[-1]['id']
        blocker = records[-2]['id']
        for bead in [r['id'] for r in records[:pause_count]]:
            bd('update', bead, '--add-label', 'needs-human', read=False)
            bd('comments', 'add', bead, 'Disposable pause question. ' * 80, read=False)
        paused = [r['id'] for r in inventory() if r['status'] not in ('closed', 'tombstone') and
                  any(label in r.get('labels', []) for label in
                      ('needs-human', 'product-decision-required'))]
        samples = []
        for _ in range(3):
            begin = len(commands)
            _, elapsed = cycle(selected, paused)
            samples.append(dict(ms=elapsed, commandCount=len(commands)-begin))
        http_times = {}
        for name, path in [('attention', '/api/attention'), ('ready', '/api/beads?view=ready'),
                           ('blocked', '/api/beads?view=blocked'),
                           ('workspace', '/api/beads/' + selected), ('factory', '/api/factory/state')]:
            _, http_times[name] = http(path)
        changes = []
        for kind in ('comment', 'dependency', 'readiness'):
            before = inventory()
            before_row = next(r for r in before if r['id'] == selected)
            begin = time.perf_counter()
            if kind == 'comment':
                bd('comments', 'add', selected, f'comments-only size={size}', read=False)
            elif kind == 'dependency':
                bd('dep', 'add', selected, blocker, read=False)
            else:
                bd('close', blocker, read=False)
            mutation_ms = (time.perf_counter()-begin)*1000
            # Worst phase for a 1s start-to-start scheduler: mutation just missed its slot.
            time.sleep(1)
            observed, cycle_ms = cycle(selected, paused)
            collection_end_to_end = (time.perf_counter()-begin)*1000
            row = next(r for r in observed['inventory'] if r['id'] == selected)
            ready_ids = [r['id'] for r in observed['ready']]
            if kind == 'comment':
                assert any(f'comments-only size={size}' in c['text'] for c in observed['selectedComments'])
            elif kind == 'dependency':
                assert selected not in ready_ids
                assert any(r['id'] == selected for r in observed['blocked'])
            else:
                assert selected in ready_ids
            # Finish at a real projected HTTP response, not just the CLI output.
            # This is intentionally a conservative serial probe, not a claim that
            # a production observer or browser transport already exists.
            if kind == 'comment':
                projected, http_ms = http('/api/beads/' + selected)
                assert any(f'comments-only size={size}' in c['body'] for c in projected['comments'])
            else:
                projected, http_ms = http('/api/beads?view=ready')
                assert (selected in [r['id'] for r in projected]) == (kind == 'readiness')
            end_to_end = (time.perf_counter()-begin)*1000
            changes.append(dict(kind=kind, inventoryChanged=digest(before)!=digest(observed['inventory']),
                                selectedUpdatedAtChanged=before_row['updated_at']!=row['updated_at'],
                                changedSelectedFields=sorted(k for k in before_row if before_row[k]!=row.get(k)),
                                mutationMs=round(mutation_ms, 2), cycleMs=cycle_ms,
                                collectionEndToEndMs=round(collection_end_to_end, 2), httpMs=http_ms,
                                endToEndMs=round(end_to_end, 2),
                                committedToHttpMs=round(end_to_end-mutation_ms, 2)))
        output['fixtureSizes'].append(dict(size=size, seededPaused=pause_count,
                                           actualPaused=len(paused),
                                           cycles=samples, legacyHttpMs=http_times, changes=changes))
        Path(args.output).write_text(json.dumps(output, indent=2) + '\n', encoding='utf-8', newline='\n')
        print(json.dumps(output['fixtureSizes'][-1]), flush=True)


if __name__ == '__main__':
    main()
