# unit3d-description-clone

Copies torrent descriptions from one tracker to another. Images embedded in the
description are automatically rehosted to a compatible image host so they
remain accessible on the target tracker.

Supported source tracker APIs:
- **UNIT3D** — standard UNIT3D REST API
- **F3NIX** — F3NIX-style POST API

## How it works

1. Given a torrent ID on the target tracker, the tool fetches that torrent's metadata
   from the target tracker API.
2. The torrent name is matched against the `release_group` values of each `[from_tracker]`
   section to select the appropriate source tracker.
3. It locates a matching torrent on the selected source tracker using one of two strategies
   (see [Source tracker lookup](#source-tracker-lookup) below).
4. The description is copied from the source torrent.
5. Any lines in the description matching a configured `[strip_lines]` pattern are removed.
6. Every image URL found in the BBCode description is downloaded and re-uploaded to
   the configured image host. SVG images are converted to PNG before uploading.
   (This step can be skipped with `--no-rehost`.)
7. The optional `[description_append]` config section is appended to the final description.
8. The tool logs in to the target tracker (caching the session in `cache/`), opens the
   torrent edit page, fills in the new description, and submits the form.

## Source tracker selection

Multiple `[from_tracker]` sections can be defined in the config file, each covering one
or more release groups. When processing a torrent, the tool checks the torrent name
against the `release_group` values of each section in order, and uses the first section
that matches. The match is a case-insensitive substring search.

Each `[from_tracker]` section can list one or more `release_group` entries and optionally
specify the tracker `type` (defaults to UNIT3D):

```ini
[from_tracker]
url = https://source1.example
api_key = <key>
type = UNIT3D
release_group = GroupA
release_group = GroupB

[from_tracker]
url = https://source2.example
api_key = <key>
type = F3NIX
release_group = GroupC
```

The `type` key is optional and defaults to `UNIT3D` when omitted.

If no `[from_tracker]` section matches the torrent name, the tool aborts with an error message.

## Source tracker lookup

The tool supports two strategies for finding the matching torrent on the source tracker,
controlled by the `supports_file_name_search` option in `[from_tracker]`.

### File-name search (default)

When `supports_file_name_search = true` (the default — also applies when the option is omitted):

- **UNIT3D**: searches `/api/torrents/filter?file_name=…` using the first file listed in the target torrent.
- **F3NIX**: POSTs `action=search` with `file_name=…` to the API endpoint.

### TMDB ID search

When `supports_file_name_search = false`, the tool falls back to matching by TMDB ID.
This is necessary for trackers that do not implement the `file_name` filter parameter.

**UNIT3D:**
1. The TMDB ID is read from the target torrent's metadata.
2. The source tracker's `/api/torrents/filter` endpoint is queried with the `tmdbId`
   parameter.
3. The first result's ID is used to fetch the full torrent record from
   `/api/torrents/{id}`, which ensures the complete description is retrieved.

**F3NIX:**
1. The TMDB ID is read from the target torrent's metadata.
2. The API is POSTed with `action=search` and `tmdb_id=movie/{id}` (then `tv/{id}` if
   the first attempt returns no results).
3. The matching torrent's ID is used to fetch full details via `action=details`.

If the target torrent has no TMDB ID the tool aborts with an error message.

## Requirements

- .NET 10 SDK or later
- A source tracker with API access (UNIT3D or F3NIX)
- A UNIT3D target tracker with API access and a user account with torrent modification privileges
- A compatible image host with API access

## Configuration

Copy the default config file and fill in your values:

```
cp unit3d-description-clone.ini.default unit3d-description-clone.ini
```

```ini
[from_tracker]
url = https://source-tracker.example
api_key = <source API key>
; API type: UNIT3D (default) or F3NIX.
; type = UNIT3D
; One or more release group names (repeated keys). The torrent name is checked for a
; case-insensitive substring match against each value. The first matching section is used.
release_group = GroupA
release_group = GroupB
; Optional. Set to false if the tracker does not support the file_name filter.
; Torrents will then be matched by TMDB ID instead. Defaults to true when omitted.
; supports_file_name_search = false

; Additional [from_tracker] sections can be added for other source trackers.
;[from_tracker]
;url = https://source-tracker2.example
;api_key = <source API key>
;type = F3NIX
;release_group = GroupC

[to_tracker]
url = https://target-tracker.example
api_key = <target API key>
username = <your username>
password = <your password>
totp_secret = <Base32-encoded TOTP secret, leave blank if 2FA is not enabled>

[image_host]
url = https://images.example
api_key = <Image host API key>

; Optional: map source image URLs directly to already-rehosted URLs.
; Useful when running the tool repeatedly and some images are already uploaded.
; Multiple source URLs may map to the same rehosted URL.
[known_images]
; https://old-host.example/image.png = https://images.example/image.png

; Optional: remove lines from the source description that match any pattern.
; Patterns are .NET regular expressions (case-insensitive). Repeat the key for multiple patterns.
;[strip_lines]
;pattern = Created by L4G's Upload Assistant
;pattern = Uploaded with.*\bTool\b

; Optional: text to append to every description submitted to the target tracker.
; Must be the last section in the file. All lines after the header are used verbatim —
; blank lines and lines starting with ; are preserved.
;[description_append]
;Encoded and uploaded by Example.
;[url=https://example.com]example.com[/url]
```

## Building

```
dotnet build src/
```

To produce a self-contained single-file binary for Linux x64:

```
dotnet publish src/ -c Release -r linux-x64
```

The output is placed in `publish/`.

## Usage

Clone a single torrent description by its ID on the target tracker:

```
unit3d-description-clone [--no-rehost] <torrent-id>
```

Backfill all torrents on the target tracker whose name matches a release group, uploaded by a specific user:

```
unit3d-description-clone [--no-rehost] backfill "<release group name>" "<uploader username>"
```

### Flags

| Flag | Description |
|------|-------------|
| `--no-rehost` | Skip image rehosting. Images in the description are left pointing at their original URLs. |

In backfill mode the tool filters the target tracker by both torrent name and uploader, paginates through all matching results and processes each
torrent. A JSON file is written to `cache/<id>.json` once a torrent is processed so
that subsequent runs skip it.

## Cache directory

The `cache/` directory is created automatically in the working directory. It stores:

- `target-cookies.json` -- session cookies for the target tracker
- `<torrent-id>.json` -- processed torrent records written during backfill runs
