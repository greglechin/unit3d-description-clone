# unit3d-description-clone

Copies torrent descriptions from one tracker to another. Images embedded in the
description are automatically rehosted to a compatible image host so they
remain accessible on the target tracker.

## How it works

1. Given a torrent ID on the target tracker, the tool fetches that torrent's metadata
   from the target tracker API.
2. The torrent name is matched against the `release_group` values of each `[from_tracker]`
   section to select the appropriate source tracker.
3. It locates a matching torrent on the selected source tracker using one of two strategies
   (see [Source tracker lookup](#source-tracker-lookup) below).
4. If the target torrent name already contains `-TRUMPABLE`, the torrent is skipped.
5. The source torrent file is downloaded and parsed. The source and target torrents
   must have the same number of `.mkv` files, and each target `.mkv` must exist in
   the source torrent with the same byte size. If counts differ, or any target `.mkv`
   is missing or size-mismatched, only the target torrent name is changed to
   `{OriginalName}-TRUMPABLE`; the description is left unchanged.
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
12. The optional `[description_append]` config section is appended to the final description.
13. The tool logs in to the target tracker (caching the session in `cache/`), opens the
    torrent edit page, fills in the new description, and submits the form. If the source
    torrent provided MediaInfo and the target form's MediaInfo field is empty, it is also
    populated.

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
;rss_key = <source RSS key>
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
