---
name: update-changelog
description: >-
  Drafts a plain-English changelog entry for the TV Emulator landing page from
  changes that are not on master (or main). Use when the user runs
  /update-changelog or asks to update the landing-page changelog from pending
  git changes.
disable-model-invocation: true
---

# Update changelog

Update `docs/changelog.json` on the landing page. Describe what a person would notice. Do not paste commit subjects.

## Base branch

Pending work is everything not yet on the default branch.

1. `git fetch origin`. If fetch fails, continue with local refs and say so.
2. Pick the first ref that exists, in order: `origin/master`, `origin/main`, `master`, `main`.
3. Call that ref the base.

## What is pending

Collect both committed and uncommitted work that the base does not have.

```bash
git log --pretty=format:"%h %ad %s" --date=short <base>..HEAD
git diff --stat <base>...HEAD
git status --short
git diff --stat HEAD
git diff --cached --stat
```

Read the diffs that explain behavior (not lockfiles, engine binaries, or `logs/`). Include untracked source and docs. Skip `engines/chromium-*` and other downloaded browsers.

If the range and the working tree are empty, stop. Tell the user there is nothing pending. Do not add an entry.

## Draft

Read `docs/changelog.json` first. Do not add a second entry for a change that is already covered.

Write one new entry:

- `date`: today, `YYYY-MM-DD`
- `title`: a short name for the change
- `items`: 3–8 sentences a user can understand

Group related commits into one entry. Prefer what changed for someone running the app. Drop typo-only and formatting-only noise. If the only pending work is that noise, say so and ask before writing an entry.

Show the draft in the chat. Write the file only after the user confirms, unless their message already told you to apply it.

## Write

Prepend the entry to the `entries` array. Keep the JSON valid. Do not edit `docs/index.html` or `docs/styles.css` unless the page can no longer render the schema.

Do not commit unless the user asks.

## Example

Pending: a debug preview for console, network, and memory.

```json
{
  "date": "2026-10-25",
  "title": "Live debug preview",
  "items": [
    "A phone-friendly preview on your LAN shows the console, network traffic, JavaScript memory, and the session log while the TV page is running.",
    "The memory tab tracks the page heap and the video-plane heap separately.",
    "The network tab lists requests as they happen, with filters and a look at headers and bodies."
  ]
}
```
