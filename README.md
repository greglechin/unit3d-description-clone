# unit3d-description-clone

Copies torrent descriptions from one UNIT3D tracker to another. Images embedded in the
description are automatically rehosted to a compatible image host so they
remain accessible on the target tracker.

## How it works

1. Given a torrent ID on the target tracker, the tool looks up the first file in that
   torrent and searches for a matching torrent on the source tracker by filename.
2. The description is copied from the source torrent.
3. Every image URL found in the BBCode description is downloaded and re-uploaded to
   the configured image host. SVG images are converted to PNG before uploading.
4. Any text after the last image tag is stripped (credits, source-site footers, etc.).
5. An optional `description_append.txt` file is appended to the final description.
6. The tool logs in to the target tracker (caching the session in `cache/`), opens the
   torrent edit page, fills in the new description, and submits the form.

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

[to_tracker]
url = https://target-tracker.example
api_key = <target API key>
username = <your username>
password = <your password>
totp_secret = <Base32-encoded TOTP secret, leave blank if 2FA is not enabled>

[image_host]
url = https://images.example
api_key = <Chevereto API key>

; Optional: map source image URLs directly to already-rehosted URLs.
; Useful when running the tool repeatedly and some images are already uploaded.
; Multiple source URLs may map to the same rehosted URL.
[known_images]
; https://old-host.example/image.png = https://images.example/image.png
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

## Optional: description_append.txt

If a file named `description_append.txt` exists in the working directory its contents
are appended to every description that is submitted. Use it to add a standard footer
or attribution note.

## Cache directory

The `cache/` directory is created automatically in the working directory. It stores:

- `target-cookies.json` -- session cookies for the target tracker
- `<torrent-id>.json` -- processed torrent records written during backfill runs
