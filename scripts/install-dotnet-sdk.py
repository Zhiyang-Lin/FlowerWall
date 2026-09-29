"""Install the .NET SDK from the official release feed.

Why this exists instead of dotnet-install.ps1:
  dotnet-install.ps1 downloads through BITS / Schannel, which fails in sandboxed
  or otherwise restricted environments (SEC_E_NO_CREDENTIALS). Python's bundled
  OpenSSL has no such limitation, and extracting an SDK archive is simply unzip +
  a couple of marker files (.version / .productVersion), so doing it here keeps
  the bootstrap dependable and reproducible.

Usage:
    python scripts/install-dotnet-sdk.py --install-dir .tools/dotnet [--channel 8.0]
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import tempfile
import urllib.request
import zipfile

RELEASES_INDEX = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json"
USER_AGENT = "flowerwall-bootstrap"


def fetch_json(url: str) -> dict:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.load(response)


def find_sdk(releases_index: list, channel: str, architecture: str) -> tuple[str, str]:
    """Return (version, download_url) for the newest SDK in the given channel."""
    entry = next((item for item in releases_index if item.get("channel-version") == channel), None)
    if entry is None:
        available = ", ".join(str(item.get("channel-version")) for item in releases_index)
        raise SystemExit(f"channel {channel!r} not found; available: {available}")

    latest_sdk = entry["latest-sdk"]
    channel_data = fetch_json(entry["releases.json"])

    for release in channel_data["releases"]:
        if release["sdk"]["version"] != latest_sdk:
            continue

        for file in release["sdk"]["files"]:
            if file["rid"] == f"win-{architecture}" and file["name"].endswith(".zip"):
                return latest_sdk, file["url"]

    raise SystemExit(f"no win-{architecture} zip found for SDK {latest_sdk}")


def download(url: str, destination: str) -> None:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=600) as response:
        total = int(response.headers.get("Content-Length") or 0)
        written = 0
        with open(destination, "wb") as handle:
            while True:
                chunk = response.read(1 << 20)
                if not chunk:
                    break
                handle.write(chunk)
                written += len(chunk)
                if total:
                    percent = written * 100 // total
                    print(f"\r  downloading {percent:3d}%  {written / 1e6:7.1f} / {total / 1e6:.1f} MB", end="")
                else:
                    print(f"\r  downloading {written / 1e6:7.1f} MB", end="")
    print()


def extract(archive: str, install_dir: str) -> None:
    with zipfile.ZipFile(archive) as zipped:
        names = zipped.namelist()
        total = len(names)
        for index, name in enumerate(names, start=1):
            zipped.extract(name, install_dir)
            if index % 200 == 0 or index == total:
                print(f"\r  extracting {index * 100 // total:3d}%  ({index}/{total})", end="")
    print()


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Install the .NET SDK from the official feed.")
    parser.add_argument("--install-dir", required=True, help="target directory for the SDK")
    parser.add_argument("--channel", default="8.0", help="release channel, e.g. 8.0 (default)")
    parser.add_argument("--architecture", default="x64", help="CPU architecture, default x64")
    args = parser.parse_args(argv[1:])

    install_dir = os.path.abspath(args.install_dir)
    dotnet_exe = os.path.join(install_dir, "dotnet.exe")

    print(f"Resolving newest .NET SDK in channel {args.channel} / win-{args.architecture} ...")
    index = fetch_json(RELEASES_INDEX)
    version, url = find_sdk(index["releases-index"], args.channel, args.architecture)
    print(f"  SDK {version}")
    print(f"  {url}")

    if os.path.isfile(dotnet_exe):
        sdk_marker = os.path.join(install_dir, "sdk", version)
        if os.path.isdir(sdk_marker):
            print(f"SDK {version} is already installed in {install_dir}, nothing to do.")
            return 0

    os.makedirs(install_dir, exist_ok=True)
    archive = os.path.join(tempfile.gettempdir(), f"dotnet-sdk-{version}-win-{args.architecture}.zip")

    try:
        if not os.path.isfile(archive) or os.path.getsize(archive) == 0:
            print(f"Downloading SDK archive to {archive} ...")
            download(url, archive)
        else:
            print(f"Reusing cached archive {archive} ({os.path.getsize(archive) / 1e6:.1f} MB)")

        # 解压前先校验：损坏的缓存会导致难以排查的失败。
        if not zipfile.is_zipfile(archive):
            raise SystemExit(f"archive is not a valid zip: {archive}")

        print(f"Extracting into {install_dir} ...")
        extract(archive, install_dir)
    finally:
        if os.path.isfile(archive):
            try:
                os.remove(archive)
            except OSError:
                pass

    if not os.path.isfile(dotnet_exe):
        raise SystemExit(f"installation finished but {dotnet_exe} is missing")

    print("Done.")
    print(f"  dotnet : {dotnet_exe}")
    print(f"  version: {version}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv))
    except KeyboardInterrupt:
        print("\ninterrupted", file=sys.stderr)
        raise SystemExit(130)
