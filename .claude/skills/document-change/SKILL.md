---
name: document-change
description: Bring CHANGELOG.md, README.md, docs/MODERNIZATION-PLAN.md and CLAUDE.md into step with a change to Offstream. Use before opening a pull request, when asked to update the docs or the changelog, when a change has landed but its documentation has not, or when a decision was taken in conversation that nothing on disk records yet. Encodes this repo's rules — every pull request touches CHANGELOG.md under [Unreleased], the plan is authoritative for phases and carries the findings, and a decision that binds future work goes in CLAUDE.md.
tools: Read, Grep, Glob, Bash, Edit, Write
---

# Document the change

Four files carry Offstream's documentation, and each answers a different question. A change is
documented when every file it owes an entry has one — not when the changelog has been touched.

| File | Answers | Audience |
| --- | --- | --- |
| `CHANGELOG.md` | What changed in this release, and why it mattered | Someone upgrading |
| `README.md` | How the app works today | Someone using it |
| `docs/MODERNIZATION-PLAN.md` | Why it is built this way, and what is left | Someone changing it |
| `CLAUDE.md` | What must not be broken | Whoever works on it next |
| `docs/decisions/NNNN-*.md` | A phase-level decision and how it was verified | The record |

## 1. Read the diff, not the conversation

Start from what is actually on disk. A summary of the work reliably drops the part that turned
out to matter.

```bash
git diff main...HEAD --stat
git diff main...HEAD
git log main..HEAD --format='%s%n%n%b'
```

If the work is uncommitted, `git diff HEAD` and `git status --short` instead.

Two things to extract, and they are not the same thing:

- **What a user would notice.** This drives `CHANGELOG.md` and `README.md`.
- **What the next person would trip over.** This drives the plan's findings and `CLAUDE.md`.

A change can owe an entry to one, both, or neither.

## 2. Route it

Work through these in order. Most changes owe two or three; almost none owe all five.

**`CHANGELOG.md` — every pull request, without exception.** CI fails a pull request whose diff
does not include the file (`.github/workflows/`, job `changelog`). A genuinely user-invisible
change — a test-only fix, a comment, a rename with no observable effect — says so with the
`no-changelog` label rather than by quietly skipping the entry.

**`README.md` — when observable behaviour changed.** A new setting, a renamed control, a default
that moved, a changed file layout. Not for internal refactors, however large.

**`docs/MODERNIZATION-PLAN.md` — when the change taught you something, or moved a phase.** The
plan is authoritative for architecture, phases and acceptance criteria, so a decision that
contradicts it makes the plan wrong, not the code. Add a finding when a fact was expensive to
learn and is invisible from the code alone.

**`CLAUDE.md` — when a decision binds future work.** Not "we did X", but "X is how this is done
from now on, and here is what breaks otherwise". A one-off does not belong here; a rule that
someone will otherwise violate by accident does.

**`docs/decisions/NNNN-*.md` — a phase-level decision with a verification story.** Look at
`0001` and `0002` for the shape: a date, a status, the phase, a `Verify with:` command, and a
result table. Rare. Most decisions are a plan finding instead.

## 3. Write the changelog entry

Entries go under `## [Unreleased]`, in the file's own voice: **what changed and why it mattered
— the defect, not the patch.** The reader wants to know what was wrong with the world before
this landed. `Changed`, `Removed` and `Fixed` are written against the predecessor (Spytify), so
they say how Offstream differs from the app it replaces.

A worked contrast, on the same change:

> ✗ Collapsed `ExistingFilePolicy` and `SkipAlreadyRecordedTracks` into a single four-valued
> enum and updated the ViewModel and XAML accordingly.

That is the patch. It names types the reader does not have, and does not say why anyone should
care.

> ✓ **Telling Spotify to move on is a fourth answer to "when that file already exists", not a
> switch beside it.** It was a separate on/off setting that did nothing under two of the three
> policies — overwriting the file and saving a second copy both record the track again, leaving
> nothing to move past — so it greyed itself out half the time and needed a sentence explaining
> why.

That is the defect. The lead sentence is bold and stands alone; the rest earns it.

Mechanics that have gone wrong before:

- **Append to the existing section heading, never add a second one.** The file already carries
  `### Added`, `### Changed`, `### Removed` and `### Fixed` under `[Unreleased]`, and their order
  in the file is not the canonical Keep a Changelog order. Anchor on unique surrounding text when
  editing, because `### Removed` alone matches more than one place in the file.
- **Never let two entries in the same unreleased block contradict each other.** If this change
  reworks something an unreleased entry already describes, amend that entry. Neither has shipped,
  so the block should read as one coherent description of what the release will contain — not as
  a diary of what was tried.
- **The file is CRLF.** Wrap prose at about 100 columns, matching what is there.
- **No inherited names.** `EspionSpotify`, `Spytify` and `spy-spotify` appear only where the
  predecessor is being named as the predecessor.

## 4. Write the plan finding

Findings sit under the phase they belong to, headed with the date:

```markdown
### Finding: the Advanced page cannot be measured by a test (2026-08-29)
```

A good finding states what was tried, what happened, why it happened, and what to do instead. It
is written for someone who is about to have the same idea. If it does not save that person an
afternoon, it is not a finding — it is a commit message in the wrong file.

Also check, in the same pass:

- The **feature parity matrix** (§7) — strike through what has been dropped rather than deleting
  the row, so the decision stays visible.
- The **phase status** (§10) and the summary in `CLAUDE.md`, if a phase moved.
- Any statement the change **falsified**. A number measured before the change is not a number
  measured after it; say which, rather than letting it read as current.

## 5. Write the CLAUDE.md rule

Only for a decision that binds future work. State the rule, the date it was taken, and the
failure it prevents — the failure is the load-bearing part, because a rule without one reads as
taste and gets overridden.

```markdown
- **A setting's description is a tooltip, not a line under its label** (decided 2026-08-29). The
  Advanced page has no `ScrollViewer` … it has been clipped off the bottom three times.
```

Convert relative dates to absolute ones. "Last week" is unreadable in six months.

## 6. Verify

```bash
# The check CI runs on a pull request.
git diff --name-only main...HEAD | grep -qx 'CHANGELOG.md' && echo 'changelog: ok'

# Nothing inherited crept into the prose THIS change added. Scoped to added lines on
# purpose: the plan, the README and CLAUDE.md all name the predecessor legitimately, so
# grepping whole files answers "yes, dozens" every time and stops being read.
git diff main...HEAD -- '*.md' | grep '^+' | grep 'EspionSpotify\|spy-spotify\|Spytify'

# Resource keys stay in step across languages, if strings changed.
grep -c '<data name=' src/Offstream.App/Resources/Strings.resx src/Offstream.App/Resources/Strings.fr.resx
```

Then read the `[Unreleased]` block start to finish. It should read as a description of one
release by one author, not as a stack of separately-written notes.

## Related: the commit and the pull request

They follow the same rule — the defect, not the patch — at greater length. `git log -3
--format='%B'` is the reference; the bodies there are prose paragraphs explaining what was wrong
and what each decision cost, not bulleted summaries of the diff. This skill does not write them,
but an entry that reads well here usually starts as a sentence from there.
