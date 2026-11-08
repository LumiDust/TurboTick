#!/usr/bin/env python3
"""Private implementation of the container's install/run/health interface."""
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET

# Expected TurboTick version, supplied by the release pipeline. This used to be a literal ("0.5.2"), so
# every build after 0.5.2 failed here even though the image and the mod came from the same artifact. Empty
# means "accept whatever the mod declares" - the payload inventory below is what actually guards integrity.
MOD_VERSION = os.environ.get("TURBOTICK_MOD_VERSION", "").strip()
DLLS = ("TurboTick.dll", "TurboTick.Networking.dll", "TurboTick.Simulation.dll")


def fail(message):
    raise ValueError(message)


def xml(path):
    return ET.parse(path).getroot()


def inventory(root):
    if root.is_symlink() or not root.is_dir():
        fail(f"expected a real directory: {root}")
    entries = {}
    for item in sorted(root.rglob("*")):
        if item.is_symlink():
            fail(f"symlinks are not supported in the packaged Mod: {item}")
        name = item.relative_to(root).as_posix()
        if item.is_file():
            entries[name] = hashlib.sha256(item.read_bytes()).hexdigest()
        elif item.is_dir():
            entries[name + "/"] = None
        else:
            fail(f"unexpected special file: {item}")
    return entries


def mod_version(root):
    return xml(root / "About/About.xml").findtext("modVersion", "").strip()


def mod_layout(root):
    about = xml(root / "About/About.xml")
    if about.findtext("packageId", "").strip().lower() != "lumidust.turbotick":
        fail(f"incorrect TurboTick packageId: {root}")
    version = mod_version(root)
    if MOD_VERSION and version != MOD_VERSION:
        fail(f"TurboTick version {version!r}; image requires {MOD_VERSION}; run install with the matching image")
    if not version:
        fail(f"TurboTick package declares no modVersion: {root}")
    folders = xml(root / "LoadFolders.xml")
    loaded = [n.text.strip() for n in folders.findall("v1.7/li") if n.text]
    if not {"/", "1.7"}.issubset(loaded):
        fail(f"LoadFolders.xml must load / and 1.7: {root}")
    for name in DLLS:
        path = root / "1.7/Assemblies" / name
        if not path.is_file() or path.stat().st_size == 0:
            fail(f"missing or empty Mod assembly: {path}")
    forbidden = {"0harmony.dll", "assembly-csharp.dll", "rimworld.dll", "waylith.dll", "unitystubs.dll"}
    for path in root.rglob("*"):
        if path.is_file() and path.name.lower() in forbidden:
            fail(f"game/Harmony assemblies must not be embedded in TurboTick: {path}")


def verify(root, manifest):
    mod_layout(root)
    expected = json.loads(manifest.read_text(encoding="utf-8"))
    actual = inventory(root)
    if expected != actual:
        changed = sorted(k for k in expected.keys() | actual.keys() if expected.get(k, False) != actual.get(k, False))
        fail("TurboTick payload differs from this image (missing, modified, or extra entries): " + ", ".join(changed[:12]) + "; run install while stopped")


def game_layout(root):
    for name in ("Version.txt", "RimWorld", "Assembly-CSharp.dll", "RimWorld.dll", "Waylith.dll", "UnityStubs.dll", "Assets.zip"):
        if not (root / name).is_file():
            fail(f"missing game file: {root / name}; run install first")
    if not (root / "Data/Core").is_dir():
        fail(f"missing game directory: {root / 'Data/Core'}")
    if not os.access(root / "RimWorld", os.X_OK):
        fail(f"game executable needs execute permission: {root / 'RimWorld'}")
    version = (root / "Version.txt").read_text(encoding="utf-8-sig").splitlines()[0].strip()
    token = version.split()[0]
    if not re.fullmatch(r"1\.7(?:\.[0-9]+)*", token):
        fail(f"expected RimWorld 1.7 Linux game, got {version!r}")
    expected = os.environ.get("TURBOTICK_EXPECTED_GAME_VERSION", "1.7")
    if not (version == expected or version.startswith(expected + ".") or version.startswith(expected + " ")):
        fail(f"game version {version!r} does not match TURBOTICK_EXPECTED_GAME_VERSION={expected!r}")
    return token


def harmony(root, optional=False, validate=True):
    found = []
    mods = root / "Mods"
    if mods.is_dir():
        for item in sorted(mods.iterdir()):
            about = item / "About/About.xml"
            if about.is_file() and xml(about).findtext("packageId", "").strip().lower() == "brrainz.harmony":
                found.append(item)
    if len(found) > 1:
        fail("multiple Harmony installations found: " + ", ".join(map(str, found)))
    if not found:
        if optional:
            return None
        fail("Harmony is missing under /game/Mods; install it first (workshop 2009463077)")
    if validate:
        validate_harmony(found[0])
    return found[0]


def validate_harmony(root):
    about = xml(root / "About/About.xml")
    if about.findtext("packageId", "").strip().lower() != "brrainz.harmony":
        fail(f"not official Harmony package brrainz.harmony: {root}")
    folders = xml(root / "LoadFolders.xml")
    loaded = folders.find("v1.7")
    if loaded is None:
        fail(f"Harmony has no explicit RimWorld 1.7 LoadFolders entry: {root}")
    assemblies = []
    for node in loaded.findall("li"):
        name = (node.text or "").strip().strip("/")
        if ".." in Path(name).parts:
            fail(f"invalid Harmony load folder: {name}")
        assemblies.append(root / name / "Assemblies/0Harmony.dll")
    if not any(p.is_file() and p.stat().st_size > 0 for p in assemblies):
        fail(f"Harmony 1.7 assembly is missing: {root}")


def writable(root):
    if not root.is_dir():
        fail(f"mount directory missing: {root}; create it and assign uid:gid 10001:10001")
    try:
        with tempfile.TemporaryFile(dir=root):
            pass
    except OSError as error:
        fail(f"mount is not writable by uid:gid {os.getuid()}:{os.getgid()}: {root}: {error}; fix host ownership/permissions")


def profile(root, version=None):
    config = root / "Config/ModsConfig.xml"
    if version is not None:
        config.parent.mkdir(parents=True, exist_ok=True)
        # Exclusive create preserves existing configs, including custom DLC/mod order.
        try:
            with config.open("x", encoding="utf-8") as output:
                output.write('<?xml version="1.0" encoding="utf-8"?>\n<ModsConfigData>\n'
                             f'  <version>{version}</version>\n  <activeMods>\n'
                             '    <li>ludeon.rimworld</li>\n    <li>brrainz.harmony</li>\n'
                             '    <li>lumidust.turbotick</li>\n  </activeMods>\n'
                             '  <knownExpansions />\n</ModsConfigData>\n')
            print(f"[container] created {config} for game {version}")
        except FileExistsError:
            print(f"[container] preserving existing {config}")
        return
    if not config.is_file():
        fail(f"profile configuration missing: {config}; run install first")
    mods = [n.text.strip().lower() for n in xml(config).findall("activeMods/li") if n.text]
    required = ("ludeon.rimworld", "brrainz.harmony", "lumidust.turbotick")
    if not all(name in mods for name in required) or [mods.index(n) for n in required if n in mods] != sorted(mods.index(n) for n in required if n in mods):
        fail(f"{config} must enable Core, Harmony, TurboTick in that order; existing config was retained, edit it explicitly")


def replace_tree(source, target):
    # Stage a complete copy, so stale files from previous packages never survive.
    if target.is_symlink():
        fail(f"Mod destination must not be a symlink: {target}")
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix=".turbotick-install-", dir=target.parent))
    try:
        staged = temporary / "payload"
        shutil.copytree(source, staged)
        # /opt is read-only to the runtime UID. Installed copies keep identical
        # bytes but need owner write permission for the next explicit update.
        for item in [staged, *staged.rglob("*")]:
            item.chmod(item.stat().st_mode | 0o200)
        if target.exists():
            for item in [target, *target.rglob("*")]:
                if not item.is_symlink() and item.is_dir():
                    item.chmod(item.stat().st_mode | 0o200)
            shutil.rmtree(target)
        staged.rename(target)
    finally:
        shutil.rmtree(temporary)


def health(path):
    status = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(status, dict) or not isinstance(status.get("utc"), str):
        fail("headless status must be a JSON object with an ISO UTC timestamp")
    maximum = int(os.environ.get("TURBOTICK_HEALTH_MAX_AGE_SECONDS", "30"))
    if maximum < 1:
        fail("TURBOTICK_HEALTH_MAX_AGE_SECONDS must be positive")
    stamp = dt.datetime.fromisoformat(status["utc"].replace("Z", "+00:00"))
    if stamp.tzinfo is None:
        fail("status utc must contain a timezone")
    age = (dt.datetime.now(dt.timezone.utc) - stamp).total_seconds()
    if not -5 <= age <= maximum or not -5 <= dt.datetime.now().timestamp() - path.stat().st_mtime <= maximum:
        fail(f"headless status is stale or from the future (age={age:.1f}s, max={maximum}s)")
    if status.get("connected") is not True or status.get("state") not in ("lobby", "playing", "restoring", "loading-initial-save", "publishing-initial-world") or status.get("failure"):
        fail(f"headless unhealthy: state={status.get('state')!r}, connected={status.get('connected')!r}, failure={status.get('failure')!r}")
    print(f"[health] healthy state={status['state']} age={age:.1f}s")


def main():
    command, *args = sys.argv[1:]
    paths = [Path(a) for a in args]
    if command == "manifest":
        root, manifest = paths
        mod_layout(root)
        manifest.write_text(json.dumps(inventory(root), indent=2, sort_keys=True) + "\n", encoding="utf-8")
        print(f"[container] manifest: {len(inventory(root))} payload entries, TurboTick {mod_version(root)}")
    elif command == "verify":
        verify(*paths)
    elif command == "writable":
        for path in paths:
            writable(path)
    elif command == "preflight":
        game, prof, manifest = paths
        version = game_layout(game)
        verify(game / "Mods/TurboTick", manifest)
        harmony(game)
        profile(prof)
        print(f"[container] preflight OK: RimWorld {version}, TurboTick {mod_version(game / 'Mods/TurboTick')}, complete payload verified")
    elif command == "profile":
        game, prof = paths
        profile(prof, game_layout(game))
    elif command in ("harmony", "harmony-target"):
        found = harmony(paths[0], optional=True, validate=command == "harmony")
        if found:
            print(found)
    elif command == "install-mod":
        source, target, manifest = paths
        verify(source, manifest)
        replace_tree(source, target)
        verify(target, manifest)
    elif command == "install-harmony":
        source, target = paths
        validate_harmony(source)
        replace_tree(source, target)
        validate_harmony(target)
    elif command == "health":
        health(paths[0])
    else:
        fail(f"unknown container helper command: {command}")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, ET.ParseError, KeyError, IndexError, TypeError) as error:
        print(f"[container] error: {error}", file=sys.stderr)
        sys.exit(1)
