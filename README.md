# unit3d-description-clone

Copies torrent descriptions from one tracker to another. Images embedded in the
description are automatically rehosted to a compatible image host so they
remain accessible on the target tracker.

## How it works

1. Given a torrent ID on the target tracker, the tool fetches that torrent's metadata
   from the target tracker API.
2. The torrent name is matched against the `release_group` values of each `[from_tracker]`
   section to select the applicable source trackers.
3. It locates a matching torrent on each applicable source tracker in configuration order,
   stopping at the first result (see [Source tracker lookup](#source-tracker-lookup) below).
4. If the target torrent name already contains `-TRUMPABLE`, the torrent is skipped.
5. The source torrent file is downloaded and parsed. If the source and target files
   trip the trumpable checks, only the target torrent name is changed and the
   description is left unchanged (see [Trumpable logic](#trumpable-logic) below).
6. The description and MediaInfo are copied from the source torrent.
7. Any lines in the description matching a configured `[strip_lines]` pattern are removed.
8. Several BBCode transformations are applied for compatibility with the target tracker:
   - `[hide]`/`[/hide]` tags are converted to `[spoiler]`/`[/spoiler]`.
   - `[align=left|center|right]` tags are normalized to `[left]`, `[center]`, `[right]`.
   - A zero-width space is inserted into `h:m:s` timestamps to prevent unwanted BBCode
     interpretation.
9. The description is wrapped in `[code]...[/code]`.
10. The existing description on the target torrent is preserved in a
   `[spoiler=original info]...[/spoiler]` block appended after the new description. If
   such a block already exists from a previous run, it is reused rather than nested.
11. Every image URL found in `[img]`, `[url][img]`, and `[comparison]` BBCode tags is
   downloaded and re-uploaded to the configured image host. SVG images are converted to
   PNG before uploading. Images listed in `[known_images]` are substituted directly
   without re-uploading. (This step can be skipped with `--no-rehost`.)
12. The optional `[description_append]` config section is appended to the final description
    unless skipped with `--no-append`.
13. The tool logs in to the target tracker (caching the session in `cache/`), opens the
    torrent edit page, fills in the new description, and submits the form. If the source
    torrent provided MediaInfo and the target form's MediaInfo field is empty, it is also
    populated.

## Trumpable logic

The tool marks a target torrent as trumpable instead of cloning the description when:

- The source and target torrents have different numbers of `.mkv` files.
- A target `.mkv` cannot be matched to a source `.mkv` by full path or unique filename.
- Any matched `.mkv` has a different byte size.
- Any target `.mkv` is more than one folder deep.

Those cases rename the torrent to `{OriginalName}-TRUMPABLE`. If source trackers are
selected but no matching source torrent is found on any of them, the torrent is renamed to
`{OriginalName}-TRUMPABLE-TOREVIEW` instead.

## Configuration

Copy the default config file and fill in your values:

```
cp unit3d-description-clone.ini.default unit3d-description-clone.ini
```

```ini
[from_tracker]
url = https://source-tracker.example
api_key = <source API key>
; Optional for F3NIX. Required for download_url in API responses.
rss_key = <source RSS key>
; API type: UNIT3D (default), F3NIX, or TORZNAB.
; type = UNIT3D
; Optional for TORZNAB. When true, use embedded NFO/comment metadata from the
; downloaded .torrent as the source description when available.
grab_nfo_from_torrent_file = false
; One or more release group names (repeated keys). The torrent name is checked for a
; case-insensitive suffix match against each value.
release_group = GroupA
release_group = GroupB
; Optional. Set to false if the tracker does not support the file_name filter.
; Torrents will then be matched by TMDB ID instead. Defaults to true when omitted.
; supports_file_name_search = false

; Additional [from_tracker] sections can be added for other source trackers. If multiple
; sections match a release group, they are searched in configuration order until found.
;[from_tracker]
;url = https://source-tracker2.example
;api_key = <source API key>
;rss_key = <source RSS key>
;type = F3NIX
;grab_nfo_from_torrent_file = false
;release_group = GroupA

[to_tracker]
url = https://target-tracker.example
api_key = <target API key>
username = <your username>
password = <your password>
totp_secret = <Base32-encoded TOTP secret, leave blank if 2FA is not enabled>

[image_host]
url = https://images.example
api_key = <Image host API key>
; Optional: URL to substitute when an image cannot be fetched after all retries.
; If omitted, the clone is aborted when an image fails to download.
; placeholder_image = https://images.example/placeholder.png

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

## Usage

Clone a single torrent description by its ID on the target tracker:

```
unit3d-description-clone [--no-rehost] [--no-append] [--allow-rerun] [--from-id <from-tracker>/<id>] <torrent-id>
```

Backfill all torrents on the target tracker whose name matches a release group, uploaded by a specific user:

```
unit3d-description-clone [--no-rehost] [--no-append] [--allow-rerun] backfill "<release group name>" "<uploader username>"
```

### Flags

| Flag | Description |
|------|-------------|
| `--no-rehost` | Skip image rehosting. Images in the description are left pointing at their original URLs. |
| `--no-append` | Skip appending the optional `[description_append]` config section. |
| `--allow-rerun` | Reprocess torrents whose target description already contains `[spoiler=original info]`. |
| `--from-id <from-tracker>/<id>` | Fetch an exact source torrent instead of searching, for example `tracker1/12345` for a tracker configured with `url = https://tracker1.cc`. Supported for UNIT3D, F3NIX, and TORZNAB; not available in backfill mode. |

In backfill mode the tool filters the target tracker by both torrent name and uploader, paginates through all matching results and processes each
torrent. A JSON file is written to `cache/<id>.json` once a torrent is processed so
that subsequent runs skip it.

## Cache directory

The `cache/` directory is created automatically in the working directory. It stores:

- `target-cookies.json` -- session cookies for the target tracker
- `<torrent-id>.json` -- processed torrent records written during backfill runs
