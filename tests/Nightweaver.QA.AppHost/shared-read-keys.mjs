// Select receipts for the resources that the current consumer actually mounts.
// Overview composes independent Attention, Digest and runtime resources.
export function surfaceKeys(surface, latest, selected) {
  if (surface === 'overview' && latest('base:attention')?.dataPresent
    && latest('base:digest:auto')?.dataPresent) {
    return ['base:attention', 'base:digest:auto', 'factory'];
  }
  const base = { explorer: 'base:explorer:all::1', attention: 'base:attention',
    workspace: `base:workspace:${selected}`, digest: 'base:digest:24h' };
  if (base[surface] && latest(base[surface])?.dataPresent) return [base[surface]];
  return { overview: ['attention', 'digest:auto', 'factory'], explorer: ['explorer:all::1'],
    attention: ['attention'], workspace: [`workspace:${selected}`], digest: ['digest:24h'] }[surface];
}
