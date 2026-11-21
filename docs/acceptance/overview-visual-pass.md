# Overview refined A visual pass

The [three-column comparison](overview-visual-pass/comparison.html) retains the
approved target, controlled before and candidate whole-page captures. All use
1440/720/375 CSS-pixel widths and 900px viewport height. The before source is
`9bcd7fc14d50e91aff62971c299f5388acc48ba5`; both production captures use the
same `overview-visual-checks.mjs` read fixture on the isolated QA AppHost.

| Width | Approved A | Controlled before | Candidate |
| --- | --- | --- | --- |
| 1440 | [Target](overview-visual-pass/target-1440.png) | [Before](overview-visual-pass/before-1440.png) | [Candidate](overview-visual-pass/candidate-1440.png) |
| 720 | [Target](overview-visual-pass/target-720.png) | [Before](overview-visual-pass/before-720.png) | [Candidate](overview-visual-pass/candidate-720.png) |
| 375 | [Target](overview-visual-pass/target-375.png) | [Before](overview-visual-pass/before-375.png) | [Candidate](overview-visual-pass/candidate-375.png) |

Visual review: the question now leads a single amber-edged navy surface with an
adjacent violet Review decision action and an attributed recommendation. Delivery
and completed outcomes share the desktop row; narrow layouts preserve Attention,
Delivery, Catch up order. Bays form a vertical stack with quiet identity/activity,
four thin rounded violet segments and one stage caption. Catch up shows outcomes
without expanding or managing checkpoints. Border, surface and text contrast now
follow refined A across all three sections.

Data and geometry differences are deliberate and visible:

- Current-project navigation and synchronization/search remain the shipped shell;
  the prototype's project switcher, sample counts and pretend projects are absent.
  This adds mobile shell height compared with the prototype.
- The factory wire has no title/type or activity-summary fields. Both production
  captures retain explicit Title unavailable / Type unavailable, observed role and
  actual invocation start time. No sample title, elapsed stage duration or activity
  is inferred. The bays retain separate exact Bead and invocation controls.
- The controlled decision uses the target question and a shorter supported Planner
  recommendation. Full-context/source and observation disclosures, exact identity
  and access to all paused Beads add secondary footer height. No waiting duration
  is invented. Before and candidate have the same one-decision inventory.
- The two controlled closed outcomes use the target titles, current-project `qa`
  and actual fixture timestamps. They say Closed, because the fixture supplies no
  verified merge evidence. The period is the actual 24-hour digest window rather
  than the prototype's sample timeline. No fabricated merge or completion count
  is introduced.

## Verification and retained evidence

Invocation evidence root:
`H:/Nightweaver/prod/tmp/nightshift/ns-jxv/20261121T0220450062649Z-implementer/`.

- `before-2/studio-qa`: controlled before and existing edge-state matrix; 9.028s
  probe wall time. The preceding `before` attempt exposed a strict locator matching
  both preview and hidden full context; the locator was made specific.
- `final-studio-2/studio-qa`: final controlled captures, digest empty/stale/
  unavailable/long-copy states and the existing Attention matrix, long/recovering
  questions, keyboard Review/full context, retained disclosure reorder/removal,
  routes, history, current-project scoping and sidebar focus; 13.141s probe wall
  time, zero browser errors and zero mutations.
- `candidate-3/runtime-refresh-qa`: Delivery, Overview-to-floor and Workspace
  Output refresh checks, equal-attempt polling, explicit retry and duplicate POST
  coalescing; 32.817s probe wall time, zero browser errors.
- `final-factory`: existing stage geometry, pause/unknown/stale positions, exact
  Bead navigation, immutable invocation selection, log bounds, focus and Escape.
  Its settled result and measured time are recorded in the implementation comment.
- `final-studio` retains a failed startup observation: Playwright's first API read
  received ECONNREFUSED, while the retained web log subsequently reported listening
  on that same discovered endpoint. The exact stack was stopped. The repeat used
  a fresh exact stack, with no assertion/deadline changes.

Every stack is started isolated and stopped by the existing wrapper's `finally`.
Studio TypeScript/Vite build and all 78 component tests pass, including the added
cheap closed-outcome deduplication/lineage/order case. No new native journey,
fixture publication, polling request, coordinator or backend query was added.
The additional controlled screenshots reuse the existing Studio native setup;
the before-to-final probe difference includes the added twelve digest edge-state
captures and is diagnostic, not a paired machine benchmark. The complete
candidate measurement, retained baseline identity and comparability limitations
are recorded in the Bead implementation comment after the exact commit is known.
