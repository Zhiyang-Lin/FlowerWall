"""Download a file over HTTPS using Python's bundled OpenSSL.

Why this exists: in some restricted environments Windows Schannel cannot
complete a TLS handshake (SEC_E_NO_CREDENTIALS), while Python's OpenSSL stack
works fine. The build scripts use this as their downloader.

Usage:
    python scripts/download.py <url> <destination>
"""

from __future__ import annotations

import sys
import urllib.request

USER_AGENT = "desktop-wallpaper-bootstrap"
CHUNK_SIZE = 1 << 16


def download(url: str, destination: str) -> int:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    written = 0
    with urllib.request.urlopen(request, timeout=180) as response:
        with open(destination, "wb") as handle:
            while True:
                chunk = response.read(CHUNK_SIZE)
                if not chunk:
                    break
                handle.write(chunk)
                written += len(chunk)
    return written


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2

    url, destination = argv[1], argv[2]
    try:
        size = download(url, destination)
    except Exception as error:  # noqa: BLE001 - surfaced to the caller as a message
        print(f"download failed: {type(error).__name__}: {error}", file=sys.stderr)
        return 1

    print(f"downloaded {size} bytes -> {destination}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
