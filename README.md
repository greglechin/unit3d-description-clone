# unit3d-description-clone

Copies torrent descriptions from one UNIT3D tracker to another. Images embedded in the
description are automatically rehosted to a compatible image host so they
remain accessible on the target tracker.

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
7. Any text after the last image tag is stripped (credits, source-site footers, etc.).
8. An optional `description_append.txt` file is appended to the final description.
9. The tool logs in to the target tracker (caching the session in `cache/`), opens the
   torrent edit page, fills in the new description, and submits the form.

## Source tracker selection

Multiple `[from_tracker]` sections can be defined in the config file, each covering one
or more release groups. When processing a torrent, the tool checks the torrent name
against the `release_group` values of each section in order, and uses the first section
that matches. The match is a case-insensitive substring search.

Each `[from_tracker]` section can list one or more `release_group` entries:

```ini
[from_tracker]
url = https://source1.example
api_key = <key>
release_group = GroupA
release_group = GroupB

[from_tracker]
url = https://source2.example
api_key = <key>
release_group = GroupC
```

If no `[from_tracker]` section matches the torrent name, the tool aborts with an error message.

## Source tracker lookup

The tool supports two strategies for finding the matching torrent on the source tracker,
controlled by the `supports_file_name_search` option in `[from_tracker]`.

### File-name search (default)

When `supports_file_name_search = true` (the default — also applies when the option is omitted), the tool searches the source
tracker's `/api/torrents/filter` endpoint using the `file_name` parameter, matching
against the first file listed in the target torrent.

### TMDB ID search

When `supports_file_name_search = false`, the tool falls back to matching by TMDB ID.
This is necessary for trackers that do not implement the `file_name` filter parameter.

1. The TMDB ID is read from the target torrent's metadata.
2. The source tracker's `/api/torrents/filter` endpoint is queried with the `tmdbId`
   parameter.
3. The first result's ID is used to fetch the full torrent record from
   `/api/torrents/{id}`, which ensures the complete description is retrieved.

If the target torrent has no TMDB ID the tool aborts with an error message.

## Requirements

- .NET 10 SDK or later
- A UNIT3D source tracker with API access
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
; One or more release group names (repeated keys). The torrent name is checked for a
; case-insensitive substring match against each value. The first matching section is used.
release_group = GroupA
release_group = GroupB
; Optional. Set to false if the tracker does not support the file_name filter on
; /api/torrents/filter. Torrents will then be matched by TMDB ID instead.
; Defaults to true when omitted.
; supports_file_name_search = false

; Additional [from_tracker] sections can be added for other source trackers.
;[from_tracker]
;url = https://source-tracker2.example
;api_key = <source API key>
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
```

`totp_secret` is the raw TOTP secret shown when setting up two-factor authentication,
encoded in Base32 (spaces are ignored). Leave it blank if the account does not have
2FA enabled.

## Building

```
dotnet build
```

To produce a self-contained single-file binary for Linux x64:

```
dotnet publish -c Release -r linux-x64
```

The output is placed in `publish/`.

## Usage

Clone a single torrent description by its ID on the target tracker:

```
unit3d-description-clone <torrent-id>
```

Backfill all torrents on the target tracker whose name matches a release group:

```
unit3d-description-clone backfill "<release group name>"
```

In backfill mode the tool paginates through all matching results and processes each
torrent. A JSON file is written to `cache/<id>.json` once a torrent is processed so
that subsequent runs skip it.

## Session caching

After the first successful login the session cookies are saved to
`cache/target-cookies.json`. Subsequent runs reuse those cookies and skip the login
step. Delete this file to force a fresh login.

## Optional: strip_lines

The optional `[strip_lines]` section removes individual lines from the source description
before it is submitted. Each `pattern` value is a .NET regular expression evaluated
case-insensitively against every line. If any pattern matches, the entire line is removed.
Repeat the `pattern` key to define multiple patterns:

```ini
[strip_lines]
pattern = Created by L4G's Upload Assistant
pattern = Uploaded with.*\bTool\b
```

## Optional: description_append.txt

If a file named `description_append.txt` exists in the working directory its contents
are appended to every description that is submitted. Use it to add a standard footer
or attribution note.

## Cache directory

The `cache/` directory is created automatically in the working directory. It stores:

- `target-cookies.json` -- session cookies for the target tracker
- `<torrent-id>.json` -- processed torrent records written during backfill runs
