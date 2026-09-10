---
name: nexusmods
description: Search Nexus Mods, inspect requirements and indexed archive contents, identify local archives, and download files.
---

# Nexus Mods

Requires Windows x64.
Run commands from this skill directory.

## Setup

```powershell
mise trust mise.toml
mise install
```

Use `mise exec -- nexusmods.exe <command> --help` for options, defaults, and limits.

**No API key required:**

- Search
- Mod inspection
- Indexed archive contents
- Archive identification

**API key required:**

- File-version dependencies
- Downloads (Premium subscription required)

For authenticated operations, the user configures a key from the [API Access page](https://www.nexusmods.com/users/myaccount?tab=api) in an interactive terminal:

```powershell
mise exec -- nexusmods.exe auth set
```

`auth set` reads the key without echo, validates it with Nexus, and stores it in Windows Credential Manager.
`auth status` reports configuration and account status;
`auth remove` deletes the stored key.

## Game selection

`search`, `inspect`, and `download` require `--game`.
Use the game's Nexus URL domain, such as `skyrimspecialedition` or `fallout4`.

## Search

```powershell
mise exec -- nexusmods.exe search --game skyrimspecialedition --query "SkyUI" --field name
mise exec -- nexusmods.exe search --game fallout4 --query "patch" --field name-stemmed --limit 10
```

`name` matches an exact mod name and defaults to name ascending.
`name-stemmed` searches for words in mod names, ignoring punctuation.
`description` searches mod descriptions.
Both text search modes default to relevance descending.
Page through results with `--offset` and `--limit`.

## Inspect

```powershell
mise exec -- nexusmods.exe inspect --game skyrimspecialedition --mod 12604 --description
mise exec -- nexusmods.exe inspect --game skyrimspecialedition --mod 12604 --file 749043 --changelog
mise exec -- nexusmods.exe inspect --game skyrimspecialedition --mod 12604 --file 749043 --contents --content-extension .esp
```

`--mod` accepts a mod ID or a Nexus mod URL in the selected game.
Without `--file`, `data.files` contains the mod's upload list.
Use a `file_id` from `data.files.records` for `--file`.
The selected upload appears under `data.selected_file`; `data.files` is omitted.

The file list is ordered by upload time, newest first, then by descending file ID.
Use `--file-offset` and `--file-limit` to page toward older uploads;
`--file-category` filters the list case-insensitively.
These file-list options cannot be combined with `--file`.
File descriptions are included.
`--description` adds the mod description;
`--changelog` adds the selected file's changelog.
File `uri` values are Nexus storage references, which can be filenames or internal paths.

### Requirements

| Field under `data` | Contents |
| --- | --- |
| `author_declared_requirements` | Active mod requirements, external links, DLC declarations, and author notes. |
| `dependencies.raw` | Selected-file requirement definitions, with alternative version ranges or DLC targets grouped within each definition. |
| `dependencies.materialized` | Candidate files and versions grouped by requirement definition. |

Requirement pagination is automatic.
If a later page fails, completed pages remain available with errors.
An on-site requirement can have an empty `url`;
its `game_id` and `mod_id` still identify the mod.

`dependencies` is added when a file is selected.
A `legacy_dependency_model` result applies only to that section;
author-declared requirements are still retrieved separately.

### Indexed contents

`--contents` requires `--file` and queries Nexus's archive index.
A valid upload can have an empty index.

- `--content-extension` accepts `esp` or `.esp`.
- `--content-path` takes a literal substring of at least two characters, not a glob.
- `--content-offset` and `--content-limit` page through matching entries.

## Identify

```powershell
mise exec -- nexusmods.exe identify --path "C:\Downloads\archive.7z"
```

`data.local` contains the archive's MD5 and size.
`data.associations` contains every Nexus hash record across games, including conflicting sizes and records without a linked file.
Multiple uploads can share the same hash.

## Download

```powershell
mise exec -- nexusmods.exe download --game skyrimspecialedition --mod 12604 --file 749043 --output-dir "C:\Downloads"
```

The output directory must exist and be absolute.
Nexus supplies the archive filename;
existing destinations are preserved.

After an interruption, retry the same command with `<archive>.part` and `<archive>.nexus-state.json` in place.
The saved state must match the game and mod/file IDs.
The transfer resumes when possible and may restart from the beginning.

`<archive>.nexus-lock` prevents overlapping downloads.
If a stopped process leaves its lock, confirm that no download is running and remove only the lock before retrying.
Keep the partial archive and state file.

`data.transfer` is the receipt for the saved archive.
It includes `final_path`, `size_bytes`, and `resumed_from_byte`.
A `partial` result with this receipt means the archive was saved but cleanup failed;
the cleanup errors identify the affected paths.

## Output

Results are JSON documents written to stdout;
help and version output are plain text.
Check `status`: `complete` and `partial` exit 0;
`failed` exits 1.
A `partial` result retains usable `data` alongside source-attributed `errors`.

`meta` includes retrieval time, sources, and available rate limits.
Numeric IDs and byte sizes are decimal strings;
composite and opaque identifiers retain their source text.
Timestamps are UTC ISO-8601.
