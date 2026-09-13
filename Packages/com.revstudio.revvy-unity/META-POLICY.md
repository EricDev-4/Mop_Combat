# `.meta` file policy

## Current state

**This skeleton ships no `.meta` files.** They were deliberately not
hand-authored — a `.meta` file carries a GUID that Unity treats as an identity,
and inventing those by hand risks collisions and malformed importer blocks that
are worse than having none.

## Why it matters for a git-URL package

A package installed from a git URL is cached read-only under
`Library/PackageCache/`. When Unity imports a package asset that has no `.meta`
file it generates one *into that cache*, which means:

- the GUID differs on every machine and every reinstall,
- any asset reference into the package (a UXML file, an icon, a
  `ScriptableObject` default) silently breaks across machines,
- `MonoBehaviour` / `ScriptableObject` script references stored in user scenes or
  prefabs break, because a script's identity is its `.meta` GUID.

This package is currently **all C# with no serialized asset references and no
`MonoBehaviour`/`ScriptableObject` types**, so the practical damage today is
limited to warning noise. That stops being true the moment someone adds a UXML
layout, an icon, or a `ScriptableObject` settings asset.

## Required before tagging a release

1. Open the package once in a Unity **6000.3+** editor. The easiest route is
   `Tests/Unity/BridgeCompileCheck/`, whose `Packages/manifest.json` already
   references this folder as
   `"com.revstudio.revvy-unity": "file:../../../../Bridges/Unity/com.revstudio.revvy-unity"`.
2. A `file:` dependency is a *mutable* package, so Unity writes the generated
   `.meta` files straight back into this directory — no copying needed. (A
   `git:` dependency would instead write them into the read-only
   `Library/PackageCache/`, which is exactly the failure mode described above.)
3. Confirm every file and folder got one, then commit them alongside the sources.
4. From that point on, `.meta` files are committed like any other source file
   and their GUIDs are **never** regenerated or edited by hand.

## Rules once meta files exist

- Move or rename a file with the Unity editor (or move the `.meta` with it) —
  never with a bare `git mv` that leaves the `.meta` behind.
- Never edit a `guid:` line. Changing one is a breaking change for every project
  that references the asset.
- Never add `*.meta` to `.gitignore` for this directory. Checked as of this
  commit: the repo root `.gitignore` (written for an Unreal plugin) has no
  `*.meta` rule and does not exclude `Bridges/`, so meta files will commit
  cleanly. Re-check if that file changes.
- Deleting a file means deleting its `.meta` in the same commit.

## Verification

After step 3, every file and folder under
`Bridges/Unity/com.revstudio.revvy-unity/` — including `package.json`,
`README.md`, and each directory — must have a sibling `.meta`. Unity logs a
warning per missing one on import; a clean import log is the check.
