export const findNewDirtyWorktreeDiagnostic = (comments, knownCommentIds) => {
  const comment = [...comments].reverse().find(candidate => {
    if (knownCommentIds.has(candidate.id)) return false;
    try {
      const diagnostic = JSON.parse(candidate.body);
      const reason = `${diagnostic.reason ?? ''} ${diagnostic.latest_findings ?? ''}`.toLowerCase();
      return diagnostic.schema === 'nightweaver-needs-human-v1'
        && diagnostic.cause === 'delivery-outcome-failure'
        && reason.includes('worktree') && reason.includes('clean');
    } catch { return false; }
  });
  if (!comment) return null;
  return { comment, diagnostic: JSON.parse(comment.body) };
};

export const requireForeignClaimId = manifest => {
  if (typeof manifest.foreignClaim !== 'string' || manifest.foreignClaim.trim() === '')
    throw new Error('Manual Delivery QA manifest must contain a non-empty root foreignClaim ID.');
  return manifest.foreignClaim;
};

export const isTransientWorkspaceReadConflict = error => error?.status === 409
  && error.problem?.title === 'Bead changed'
  && ['The Bead workflow changed while its workspace was being read. Refresh and try again.',
    'The Bead disappeared or its workflow changed while its workspace was being read. Refresh and try again.']
    .includes(error.problem?.detail);

export const matchConsole409Errors = (consoleErrors, validatedResponses, maxDistanceMs = 2000) => {
  const availableResponses = [...validatedResponses];
  const matches = [];
  const unmatched = [];

  for (const consoleError of consoleErrors) {
    const locationUrl = consoleError.location?.url ?? '';
    const explicitRequestPath = `${consoleError.text} ${locationUrl}`.match(/\/api\/beads\/[^\s"'?#)]+/)?.[0];
    const candidates = availableResponses
      .map((response, index) => ({ response, index, distance: Math.abs(consoleError.observedAt - response.observedAt) }))
      .filter(candidate => candidate.distance <= maxDistanceMs
        && (!explicitRequestPath || explicitRequestPath === candidate.response.pathname))
      .sort((left, right) => left.distance - right.distance);
    if (!candidates.length || (candidates[1] && candidates[0].distance === candidates[1].distance)) {
      unmatched.push(consoleError);
      continue;
    }
    const [match] = candidates;
    matches.push({ consoleError, response: match.response });
    availableResponses.splice(match.index, 1);
  }

  return { matches, unmatched, unpairedResponses: availableResponses };
};
