# Nexus Mods CLI

`nexusmods` searches Nexus Mods, inspects requirements and indexed archive contents,
identifies local archives, and downloads files.

## Requirements

Windows x64 and the [.NET 10 runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

For agent use, see the [skill](.agents/skills/nexusmods/SKILL.md).

## Usage

### Search

`name` matches an exact mod name.
`name-stemmed` searches for words in mod names, ignoring punctuation.
`description` searches mod descriptions.

```powershell
nexusmods search --game skyrimspecialedition --query "SkyUI" --field name
nexusmods search --game fallout4 --query "patch" --field name-stemmed --limit 10
```

### Inspect

Inspection returns mod details, uploads ordered newest first,
and the requirements listed on the mod page, including DLC, off-site links, and author notes.
Selecting a file shows that upload instead of the list
and retrieves any dependencies declared for that version.
These are reported separately from mod-page requirements,
with alternatives and candidate versions grouped by requirement.

```powershell
nexusmods inspect --game skyrimspecialedition --mod 12604 --description
nexusmods inspect --game skyrimspecialedition --mod 12604 --file 749043 --contents
```

`--mod` accepts a mod ID or a Nexus mod URL.
Mod descriptions and selected-file changelogs are opt-in through `--description` and `--changelog`.

`--contents` queries Nexus's archive index.
An empty result can mean that Nexus has no indexed contents for that upload.

### Identify

Identification uses the local archive's MD5 to find matching Nexus uploads across games.
The results include the upload records and their linked mod and file information where available.

```powershell
nexusmods identify --path "C:\Downloads\archive.7z"
```

### Download

Downloads save the selected upload using the archive filename supplied by Nexus.
The output directory must exist and be absolute.

```powershell
nexusmods download --game skyrimspecialedition --mod 12604 --file 749043 --output-dir "C:\Downloads"
```

Retrying the same command resumes an interrupted transfer when possible;
a retry may instead restart from the beginning.
A completed download returns the saved archive's path and size.

## Authentication

**No API key required:**

- Search
- Mod inspection
- Indexed archive contents
- Archive identification

**API key required:**

- File-version dependencies
- Downloads (Premium subscription required)

Save a personal key from Nexus's [API Access page](https://www.nexusmods.com/users/myaccount?tab=api):

```powershell
nexusmods auth set
```

`auth set` prompts for the key without echo, validates it with Nexus,
and stores it in Windows Credential Manager.
`nexusmods auth status` checks configuration and account status;
`nexusmods auth remove` deletes the stored key.

## Output

JSON results place retrieved information in `data` and report problems in `errors`.
`meta` includes the sources, retrieval time, and available API rate limits.

For example, `inspect` can return `partial` when a dependency lookup fails
but the mod details and selected file remain available.

| Status | Meaning | Exit code |
| --- | --- | --- |
| `complete` | The requested operation succeeded. | 0 |
| `partial` | Usable data is available alongside errors. | 0 |
| `failed` | No successful result is available. | 1 |

## Development

Development uses the .NET 10 SDK, managed through [mise](https://mise.jdx.dev/).

```powershell
mise trust
mise install
mise run build
mise run test
mise run publish
```

`mise run publish` creates the Windows x64 distribution in
`artifacts/nexusmods-<version>-win-x64/` and a matching ZIP.
