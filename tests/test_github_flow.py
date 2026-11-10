"""End-to-end: base -> publish (gh mocked) -> fake GitHub with 302 redirects -> TalesUntoldLauncher.exe --update (Mono)."""
import http.server, json, os, shutil, subprocess, sys, tempfile, threading
from pathlib import Path
HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import publish_github as pg

tmp = Path(tempfile.mkdtemp())
base_client, work, player, releases = tmp / 'base', tmp / 'work', tmp / 'player', tmp / 'releases'
for d in (base_client / 'system', ):
    d.mkdir(parents=True)
(base_client / 'system/l2.exe').write_bytes(b'game')
(base_client / 'system/Interface.xdat').write_bytes(b'iface-v0')
(base_client / 'system/ItemName-e.dat').write_bytes(b'items-v0')
(base_client / 'system/Option.ini').write_text('base option')
shutil.copytree(base_client, work); shutil.copytree(base_client, player)
(player / 'system/Option.ini').write_text('player option')

subprocess.run([sys.executable, str(HERE / 'publish_github.py'), 'base', str(base_client), str(tmp / 'base.json')], check=True)

# Fake gh: release create copies assets to releases/<tag>/, release download serves the latest manifest.
fake = tmp / 'bin'; fake.mkdir()
(fake / 'gh').write_text(f'''#!{sys.executable}
import sys, shutil, json, os
from pathlib import Path
R = Path({str(releases)!r}); a = sys.argv[1:]
if a[:2] == ['release', 'download']:
    tags = sorted(R.iterdir()) if R.exists() else []
    if not tags: sys.stderr.write('release not found'); sys.exit(1)
    shutil.copy(tags[-1] / 'manifest.json', Path(a[a.index('--dir') + 1]) / 'manifest.json'); sys.exit(0)
if a[:2] == ['release', 'create']:
    d = R / a[2]; d.mkdir(parents=True)
    for f in a: 
        if os.path.isfile(f): shutil.copy(f, d / os.path.basename(f))
    sys.exit(0)
sys.exit(2)
''')
(fake / 'gh').chmod(0o755)
env = dict(os.environ, PATH=f'{fake}:{os.environ["PATH"]}')

def publish(version):
    out = subprocess.run([sys.executable, str(HERE / 'publish_github.py'), 'publish', str(work), '--base', str(tmp / 'base.json'),
                          '--version', version, '--repo', 'test/repo'], check=True, env=env, capture_output=True, text=True).stdout
    print(out); return out

# Release 1: interface changed, launcher added.
(work / 'system/Interface.xdat').write_bytes(b'iface-v1')
shutil.copy(HERE / 'launcher/TalesUntoldLauncher.exe', work / 'TalesUntoldLauncher.exe')
o1 = publish('v1')
assert '2 files differ' in o1 and '2 new to upload' in o1, o1
# Release 2: item names changed too; interface blob must be reused from v1.
(work / 'system/ItemName-e.dat').write_bytes(b'items-v2')
o2 = publish('v2')
assert '3 files differ' in o2 and '1 new to upload' in o2, o2
m2 = json.loads((releases / 'client-v2/manifest.json').read_text())
iface = next(f for f in m2['files'] if f['path'] == 'system/Interface.xdat')
assert '/download/client-v1/' in iface['url'], iface
assert not any(f['path'].endswith('Option.ini') for f in m2['files'])

# Fake GitHub: /test/repo/releases/latest/download/X -> 302 -> /test/repo/releases/download/<latest>/X -> 302 -> /objects/<tag>/X
class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_GET(self):
        path = self.path.split('?')[0]
        latest = sorted(p.name for p in releases.iterdir())[-1]
        if path.startswith('/test/repo/releases/latest/download/'):
            self.send_response(302); self.send_header('Location', f'/test/repo/releases/download/{latest}/' + path.rsplit('/', 1)[1]); self.end_headers(); return
        if path.startswith('/test/repo/releases/download/'):
            tag, name = path.split('/')[-2:]
            self.send_response(302); self.send_header('Location', f'http://127.0.0.1:{port}/objects/{tag}/{name}'); self.end_headers(); return
        if path.startswith('/objects/'):
            tag, name = path.split('/')[-2:]; f = releases / tag / name
            if f.is_file():
                data = f.read_bytes(); self.send_response(200); self.send_header('Content-Length', str(len(data))); self.end_headers(); self.wfile.write(data); return
        self.send_response(404); self.end_headers()
srv = http.server.ThreadingHTTPServer(('127.0.0.1', 0), H); port = srv.server_address[1]
threading.Thread(target=srv.serve_forever, daemon=True).start()
# Rewrite manifest urls to the fake host (real ones point at github.com).
for rel in releases.iterdir():
    m = json.loads((rel / 'manifest.json').read_text())
    for f in m['files']: f['url'] = f['url'].replace('https://github.com', f'http://127.0.0.1:{port}')
    (rel / 'manifest.json').write_text(json.dumps(m))

# Player has the base client + launcher from Google Drive (an old launcher copy, so the self-update path is not triggered here).
shutil.copy(HERE / 'launcher/TalesUntoldLauncher.exe', player / 'TalesUntoldLauncher.exe')
(player / 'launcher.ini').write_text(f'ManifestUrl=http://127.0.0.1:{port}/test/repo/releases/latest/download/manifest.json\n')
r = subprocess.run(['mono', str(player / 'TalesUntoldLauncher.exe'), '--update'], capture_output=True, text=True, timeout=120)
print(r.stdout[-800:], r.stderr[-800:])
assert r.returncode == 0 and 'OK v2 updated 2' in r.stdout, r.returncode
assert (player / 'system/Interface.xdat').read_bytes() == b'iface-v1'
assert (player / 'system/ItemName-e.dat').read_bytes() == b'items-v2'
assert (player / 'system/Option.ini').read_text() == 'player option'
r = subprocess.run(['mono', str(player / 'TalesUntoldLauncher.exe'), '--update'], capture_output=True, text=True, timeout=120)
assert 'OK v2 updated 0' in r.stdout, r.stdout
# A tampered asset must be rejected and leave the old file in place.
bad = next(f for f in m2['files'] if f['path'] == 'system/ItemName-e.dat')
(releases / 'client-v2' / bad['sha256']).write_bytes(b'tampered')
(player / 'system/ItemName-e.dat').write_bytes(b'items-v0')
r = subprocess.run(['mono', str(player / 'TalesUntoldLauncher.exe'), '--update'], capture_output=True, text=True, timeout=120)
assert r.returncode != 0 and (player / 'system/ItemName-e.dat').read_bytes() == b'items-v0', (r.returncode, r.stdout[-300:])
print('PASS base/publish/reuse blobs, launcher follows GitHub-style redirects, keeps Option.ini, rejects tampered asset')
