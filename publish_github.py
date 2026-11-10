"""Publishes TalesUntold client updates to GitHub Releases for TalesUntoldLauncher.exe.

The full client is handed out once (Google Drive). Releases only carry the files that differ from that base
client, so a release is megabytes, not the whole 23 GB client.

1. Once, on the exact client you upload to Google Drive:
       python publish_github.py base <client folder> base.json
2. For every update (after changing files in your working client):
       python publish_github.py publish <client folder> --base base.json --version 2026-11-10 [--notes "text"] [--dry-run]

publish compares the client with base.json, uploads files that are new or changed as assets named by their SHA256
(files already uploaded to an earlier release are reused, not uploaded again), and creates a release whose
manifest.json lists every changed file with its download url. The launcher reads
https://github.com/<repo>/releases/latest/download/manifest.json, so a new release becomes live the moment it is
published. Needs the GitHub CLI (gh) logged in with write access to the repo.

Personal and temporary files (settings, logs, screenshots, backups) are never published, so the launcher never
overwrites them.
"""
from pathlib import Path, PurePosixPath
import argparse, datetime, hashlib, json, os, shutil, subprocess, sys, tempfile

REPO = 'Sant0shiq/TalesUntold-updates'
EXCLUDE_DIRS = {'_backup', 'logs', 'log', 'screenshot', 'screenshots', 'replay', 'crashreport', 'crashreports', 'temp', 'tmp', '.launcher-update'}
EXCLUDE_FILES = {'option.ini', 'windowsinfo.ini', 'user.ini', 'chatfilter.ini', 'l2.log', 'launcher.ini', 'launcher-cache.json'}
ROOT_FILES = {'talesuntoldlauncher.exe', 'launcher-bg.jpg', 'readme-talesuntold.txt'}
EXCLUDE_SUFFIXES = {'.log', '.dmp', '.tmp', '.bak', '.7z', '.zip', '.download'}
MAX_ASSET = 2 * 1024 ** 3 - 1  # GitHub release asset limit
CACHE_NAME = '.publish-hash-cache.json'


def published(relative):
    parts = PurePosixPath(relative).parts
    if any(p.lower().startswith('_backup') for p in parts):
        return False
    if len(parts) == 1 and parts[0].lower() not in ROOT_FILES:
        return False
    if any(p.lower() in EXCLUDE_DIRS for p in parts[:-1]):
        return False
    name = parts[-1].lower()
    return name not in EXCLUDE_FILES and Path(name).suffix not in EXCLUDE_SUFFIXES


def sha256(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as stream:
        for block in iter(lambda: stream.read(1 << 20), b''):
            digest.update(block)
    return digest.hexdigest()


def scan(client, cache_file=None):
    """{relative path: {'size', 'sha256'}} for every publishable file. Hashes are cached by size and mtime."""
    cache = {}
    if cache_file and cache_file.is_file():
        try:
            cache = json.loads(cache_file.read_text(encoding='utf-8'))
        except ValueError:
            cache = {}
    result, fresh = {}, {}
    for root, dirs, names in os.walk(client):
        dirs[:] = sorted(d for d in dirs if d.lower() not in EXCLUDE_DIRS and not d.lower().startswith('_backup'))
        for name in sorted(names):
            source = Path(root) / name
            relative = source.relative_to(client).as_posix()
            if not published(relative):
                continue
            stat = source.stat()
            old = cache.get(relative)
            if old and old['size'] == stat.st_size and old['mtime'] == stat.st_mtime_ns:
                digest = old['sha256']
            else:
                digest = sha256(source)
            fresh[relative] = {'size': stat.st_size, 'mtime': stat.st_mtime_ns, 'sha256': digest}
            result[relative] = {'size': stat.st_size, 'sha256': digest}
    if cache_file:
        cache_file.write_text(json.dumps(fresh), encoding='utf-8')
    return result


def changed_files(current, base):
    """Files that are new or differ from the base client, plus the launcher itself (always listed for self-update)."""
    out = []
    for path, info in sorted(current.items()):
        old = base.get(path)
        if old is None or old['sha256'] != info['sha256'] or path.lower() in ROOT_FILES:
            out.append({'path': path, 'size': info['size'], 'sha256': info['sha256']})
    return out


def build_manifest(files, previous, repo, tag, version):
    """Gives each file a url; returns (manifest, files to upload as {asset name: path}). Blobs from earlier releases are reused."""
    known = {}
    for entry in (previous or {}).get('files', []):
        if entry.get('url'):
            known[entry['sha256']] = entry['url']
    uploads, entries = {}, []
    for f in files:
        if f['size'] > MAX_ASSET:
            raise SystemExit(f"{f['path']} is larger than the 2 GB GitHub asset limit")
        url = known.get(f['sha256'])
        if url is None:
            url = f'https://github.com/{repo}/releases/download/{tag}/{f["sha256"]}'
            uploads[f['sha256']] = f['path']
            known[f['sha256']] = url
        entries.append(dict(f, url=url))
    return {'version': version, 'files': entries}, uploads


def gh(*args, capture=False):
    return subprocess.run(['gh', *args], check=True, text=True, capture_output=capture).stdout


def latest_manifest(repo):
    with tempfile.TemporaryDirectory() as tmp:
        try:
            subprocess.run(['gh', 'release', 'download', '--repo', repo, '--pattern', 'manifest.json', '--dir', tmp],
                           check=True, capture_output=True, text=True)
        except subprocess.CalledProcessError as e:
            if 'release not found' in (e.stderr or '').lower() or 'no releases' in (e.stderr or '').lower():
                return None
            raise
        return json.loads((Path(tmp) / 'manifest.json').read_text(encoding='utf-8'))


def cmd_base(args):
    client = args.client.resolve()
    files = scan(client, args.output.resolve().parent / CACHE_NAME)
    args.output.write_text(json.dumps({'files': files}, ensure_ascii=False, indent=1), encoding='utf-8')
    print(f'base: {len(files)} files, {sum(f["size"] for f in files.values()) / 1024 ** 3:.2f} GB -> {args.output}')


def cmd_publish(args):
    client = args.client.resolve()
    base = json.loads(args.base.read_text(encoding='utf-8'))['files']
    version = args.version or datetime.datetime.now().strftime('%Y%m%d-%H%M')
    tag = 'client-' + version
    files = changed_files(scan(client, args.base.resolve().parent / CACHE_NAME), base)
    previous = None if args.dry_run else latest_manifest(args.repo)
    manifest, uploads = build_manifest(files, previous, args.repo, tag, version)
    size = sum((client / p).stat().st_size for p in uploads.values())
    print(f'version {version}: {len(files)} files differ from base, {len(uploads)} new to upload ({size / 1024 ** 2:.1f} MB)')
    for f in files:
        print(('  + ' if f['sha256'] in uploads else '  = ') + f['path'])
    if args.dry_run:
        print('dry run: nothing uploaded')
        return
    with tempfile.TemporaryDirectory() as tmp:
        stage = Path(tmp)
        for name, path in uploads.items():
            try:
                os.link(client / path, stage / name)
            except OSError:
                shutil.copy2(client / path, stage / name)
        (stage / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=1), encoding='utf-8')
        notes = args.notes or f'Обновление клиента {version}: файлов {len(files)}.'
        # gh uploads all assets to a draft first and publishes at the end, so players never see a half-uploaded release.
        gh('release', 'create', tag, '--repo', args.repo, '--title', f'Клиент {version}', '--notes', notes, '--latest',
           *[str(p) for p in sorted(stage.iterdir())])
    print(f'published https://github.com/{args.repo}/releases/tag/{tag}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    b = sub.add_parser('base', help='hash the base client that players download from Google Drive')
    b.add_argument('client', type=Path)
    b.add_argument('output', type=Path)
    p = sub.add_parser('publish', help='upload files that differ from the base as a new GitHub release')
    p.add_argument('client', type=Path)
    p.add_argument('--base', type=Path, required=True)
    p.add_argument('--version')
    p.add_argument('--notes')
    p.add_argument('--repo', default=REPO)
    p.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    (cmd_base if args.command == 'base' else cmd_publish)(args)
