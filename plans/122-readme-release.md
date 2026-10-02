# Plan 122: Correct the README release section to match the workflow that ships

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise.
>
> **Drift check (run first)**: `git diff --stat 0f0d71a8..HEAD -- README.md .github/workflows/release.yml cli/docs.test.js`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P3
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: docs
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

The README's release section documents a process that does not exist and asks
for a secret the release deliberately refuses to use.

It says the trigger is a push to `main` with a version bump, and that the
workflow "creates a GitHub Release tagged `v<version>`". The workflow's actual
trigger is `release: [published]` or a manual `workflow_dispatch` with a `tag`
input; it never triggers on a push, and it only **verifies** an existing
release — `release.ps1` creates the tag and the release. So an operator reading
the README looks for a workflow that is not there.

It then says "Required secret: `NPM_TOKEN`". The release step runs
`unset NODE_AUTH_TOKEN NPM_TOKEN`, deletes any `_authToken` line from the
runner's npmrc, and publishes through OIDC Trusted Publishing
(`permissions: id-token: write`). The documented secret is actively scrubbed,
so it sits unused in repository secrets. `cli/docs.test.js:16-29` checks eight
README strings and none of them is in this section, so the error is green in
`npm test`.

## Current state

`README.md:741-744` — the section to correct. It asserts, in prose:
trigger = push to `main` with a `package.json` version bump; publishes to npm
**and creates** a GitHub Release tagged `v<version>`; required secret =
`NPM_TOKEN`.

The actual flow, from the files:

- `.github/workflows/release.yml:3-7` — triggers are
  `release: [types: [published]]` and `workflow_dispatch` with a `tag` input.
  There is no `push` trigger.
- `.github/workflows/release.yml:97-115` — the job **verifies** the existing
  release's assets (including byte-comparing the `.vsix` against the copy
  inside `publish.zip`, at `:92-113` and `:245-248`) and publishes to npm. It
  does not create the tag or the release.
- `.github/workflows/release.yml:285-298` — "Enforce OIDC-only npm auth":
  `unset NODE_AUTH_TOKEN NPM_TOKEN`, removes `_authToken` from the runner
  npmrc, and switches to Trusted Publishing.
- `.github/workflows/release.yml:17` — `permissions: id-token: write`.
- `release.ps1` is the script that builds, tags and creates the release. It is
  referenced from `AGENTS.md` and `docs/release_protocol.md`.
- `cli/docs.test.js:16-29` — the existing README assertion list, and the place
  to add guards.

Repo conventions:

- `docs/release_protocol.md` is the authoritative release document. Read it
  before rewriting; the README should point at it, not duplicate it.
- Every regression guard gets a mutation check (`AGENTS.md`).

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Read the workflow | `Select-String -Path .github\workflows\release.yml -Pattern 'on:|NPM_TOKEN|id-token|_authToken' -Context 0,3` | confirms the facts above |
| CLI suite | `npm test` | exit 0, including `cli/docs.test.js` |
| Lint | `npm run lint` | exit 0 |

## Scope

**In scope**:

- `README.md`
- `cli/docs.test.js`

**Out of scope** (do NOT touch):

- `.github/workflows/release.yml` — the workflow is the source of truth here.
- `release.ps1`, `scripts/release-*.ps1`, `docs/release_protocol.md`.
- `AGENTS.md`.
- Any npm configuration, `.npmrc`, or repository setting.
- Deleting the `NPM_TOKEN` secret from the repository — that is an operator
  action on GitHub settings, not a repository change. Report it as a follow-up
  for the user instead.

## Git workflow

- Branch `advisor/122-readme-release`, commit in your worktree.
- Prose commit subject, e.g.
  `"Describe the release flow that actually ships, and stop asking for a token OIDC refuses"`.
- `CHANGELOG.md` under `## Unreleased` → `### Internal`.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Rewrite the README release section

Replace `README.md:741-744` so it states the flow that exists:

1. `release.ps1` (see `docs/release_protocol.md`) builds, tags, and creates the
   GitHub Release. **That** is the entry point; it is not a push to `main`.
2. Creating the release fires `.github/workflows/release.yml` on
   `release: [published]`, which verifies the release assets and publishes to
   npm.
3. npm authentication is **OIDC Trusted Publishing**. There is no npm token
   secret to configure, and the workflow actively unsets any that is present.
   Say this explicitly, because the current text asks for one.

Keep the section short and link to `docs/release_protocol.md` for detail. Do not
copy the protocol document into the README.

Preserve any genuinely useful surrounding content (prerequisites, versioning
convention) — you are correcting the three wrong claims, not deleting the
section.

### Step 2: Extend `cli/docs.test.js` to guard the correction

Add assertions in the style of the existing list at `:16-29`:

- The release section does **not** mention `NPM_TOKEN` (as a required secret).
  Scope the assertion to the release section, not the whole file, so a
  legitimate historical mention elsewhere does not fail the test.
- The release section **does** mention the `release` event / published trigger.
- The release section **does** mention OIDC or Trusted Publishing.

If the existing test file has no notion of "the release section", add a small
locator that finds it by its heading — and make sure the locator is specific
enough that editing prose above it does not silently change what is asserted.
That is the same class of brittleness flagged in plan 121; handle it here
rather than shipping a whole-file `DoesNotContain`.

### Step 3: Mutation-check the new assertions

Put `NPM_TOKEN` back into the README section and confirm the negative
assertion fails. Restore it.

**Verify**: with `NPM_TOKEN` reinstated, `cli/docs.test.js` FAILS.

### Step 4: Validate

```powershell
npm test
npm run lint
```

**Verify**: both exit 0.

## Test plan

- Modified file: `cli/docs.test.js`
  - 3 new assertions: no `NPM_TOKEN` in the release section, the release
    trigger named, OIDC named.
- Structural pattern: the existing eight assertions at `:16-29`.
- Verification: `npm test` exits 0; the mutation makes it red.

## Done criteria

- [ ] `README.md`'s release section no longer claims a push-to-`main` trigger.
- [ ] It no longer claims the workflow creates the release/tag.
- [ ] It no longer instructs the reader to create an `NPM_TOKEN` secret.
- [ ] It states that `release.ps1` creates the release and the workflow verifies and publishes.
- [ ] It points at `docs/release_protocol.md` rather than duplicating it.
- [ ] `cli/docs.test.js` asserts all three corrections, scoped to the release section.
- [ ] The mutation (reinstating `NPM_TOKEN`) makes the test red.
- [ ] `.github/workflows/**`, `release.ps1` and `docs/**` are untouched.
- [ ] `npm test` and `npm run lint` exit 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Internal`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- The README's release section is generated from a template or is
  byte-compared by a release guard, so editing it breaks that guard. Report
  the guard.
- The `NPM_TOKEN` mention appears in a part of the README that is genuinely
  about the *legacy* npm-token flow and is correct there. Report the context;
  do not delete an accurate historical statement.
- `cli/docs.test.js` has no section-locating helper and adding one would
  require touching a shared test utility outside the in-scope list. Report it.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- Interacts with: `docs/release_protocol.md` (authoritative) and
  `.github/workflows/release.yml` (the fact source). If the release mechanism
  changes, both the README and the Step 2 assertions change with it.
- A reviewer should scrutinise the section locator in Step 2. A whole-file
  `DoesNotContain("NPM_TOKEN")` would pass today and fail the first time the
  word appears in a legitimate context — or, worse, force someone to delete an
  accurate statement.
- Follow-up for the user, not for this plan: if an unused `NPM_TOKEN` secret
  exists in the repository settings, remove it there. That is a GitHub
  settings action, outside the repository.
