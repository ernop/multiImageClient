#!/usr/bin/env python3
"""Root-owned reconciler for the owner's explicitly requested environments.

Application processes cannot execute arbitrary privileged commands. The controller
reads validated names and IDs from a bounded registry and uses fixed host paths.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import pwd
import grp
import re
import shutil
import subprocess
import types
import urllib.request

ROOT = Path('/var/lib/multiimageclient-control')
SETTINGS = Path('/etc/multiimageclient/settings.json')
SITE = Path('/etc/nginx/sites-enabled/multiimageclient.conf')
PUBLISH = Path('/opt/multiimageclient')
spec = importlib.util.spec_from_file_location('provision', Path(__file__).with_name('create-environment.py'))
provision = importlib.util.module_from_spec(spec)
spec.loader.exec_module(provision)


def private_json(path, value, uid=0, gid=0):
    temporary = path.with_suffix('.tmp')
    with temporary.open('w') as stream:
        os.chmod(temporary, 0o640); os.chown(temporary, uid, gid)
        json.dump(value, stream, indent=2); stream.flush(); os.fsync(stream.fileno())
    temporary.replace(path)


def initialize():
    provision.require(not ROOT.exists(), 'The global controller already exists.')
    source = json.loads(SETTINGS.read_text())
    auth_path = Path(source['UiAuthFilePath'])
    auth = json.loads(auth_path.read_text())
    provision.require(auth.get('version') == 2 and auth.get('enabled') is True
                      and sum(a['username'] == 'ernieMultiZone' for a in auth['accounts']) == 1,
                      'Expected the existing hashed global owner account.')
    provision.run('groupadd', '--system', 'mic-auth')
    owner = pwd.getpwnam('multiimageclient'); group = grp.getgrnam('mic-auth')
    provision.run('usermod', '-a', '-G', 'mic-auth', 'multiimageclient')
    ROOT.mkdir(mode=0o750); os.chown(ROOT, 0, group.gr_gid)
    (ROOT / 'state').mkdir(mode=0o2750); os.chown(ROOT / 'state', owner.pw_uid, group.gr_gid); os.chmod(ROOT / 'state', 0o2750)
    # One canonical account file. Preserve every existing hash and the cookie signing secret.
    private_json(ROOT / 'auth.json', auth, 0, group.gr_gid)
    private_json(ROOT / 'state/links.json', {'version': 1, 'accounts': []}, owner.pw_uid, group.gr_gid)
    from urllib.parse import urlparse
    slug = urlparse(source['UiPublicBaseUrl']).path.strip('/')
    provision.require(re.fullmatch('[a-z0-9][a-z0-9-]{0,63}', slug), 'Invalid existing private route.')
    registry = {'version': 1, 'environments': [
        {'id': 'original', 'slug': slug, 'name': 'Ernie, Austin & Victor', 'original': True,
         'goalLoops': True, 'video': True, 'promptRewrite': True,
         'members': [a['username'] for a in auth['accounts'] if a['username'] != 'ernieMultiZone']},
        {'id': 'vibecoders-ai-generation', 'slug': 'vibecoders-ai-generation', 'name': 'Vibecoders AI Generation',
         'original': False, 'goalLoops': True, 'video': True, 'promptRewrite': True,
         'members': [], 'defaultGenerators': ['gpt2', 'googlepro']}]}
    private_json(ROOT / 'state/registry.json', registry, owner.pw_uid, group.gr_gid)
    backup = SETTINGS.with_name('settings.before-global-environments.json')
    provision.require(not backup.exists(), 'The settings backup already exists.')
    shutil.copy2(SETTINGS, backup)
    source.update({'UiEnvironmentId': 'original', 'UiEnvironmentName': 'Ernie, Austin & Victor',
                   'UiEnvironmentController': True, 'UiEnvironmentRegistryPath': str(ROOT / 'state/registry.json'),
                   'UiAuthFilePath': str(ROOT / 'auth.json'), 'UiLoginLinksFilePath': str(ROOT / 'state/links.json')})
    previous = SETTINGS.stat(); private_json(SETTINGS, source, previous.st_uid, previous.st_gid)
    dropin = Path('/etc/systemd/system/multiimageclient-ui.service.d')
    dropin.mkdir(exist_ok=True)
    (dropin / 'environments.conf').write_text('[Service]\nSupplementaryGroups=mic-auth\nReadWritePaths=/var/lib/multiimageclient-control/state\n')
    provision.run('systemctl', 'daemon-reload')
    print('Prepared global account configuration. Existing credentials and the original route were preserved.')


def reconcile():
    import fcntl
    with open('/run/multiimageclient-environment-controller.lock', 'w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        state_path = ROOT / 'state/registry.json'
        provision.require(state_path.stat().st_size <= 1048576, 'Registry exceeds its size limit.')
        registry = json.loads(state_path.read_text())
        envs = registry['environments']
        provision.require(registry['version'] == 1 and 1 <= len(envs) <= 16
                          and sum(e.get('original') is True for e in envs) == 1, 'Invalid registry.')
        ids, slugs = set(), set()
        for env in envs:
            provision.validate_id(env['id'])
            provision.require(env['id'] not in ids and env['slug'] not in slugs
                              and re.fullmatch('[a-z0-9][a-z0-9-]{0,63}', env['slug']), 'Invalid or duplicate environment identity.')
            ids.add(env['id']); slugs.add(env['slug'])
        outcomes = {}
        for env in envs:
            if env.get('original'): continue
            ident = env['id']; service = 'multiimageclient-env-' + ident
            config = Path('/etc') / service; manifest_path = config / 'manifest.json'
            try:
                if not manifest_path.exists():
                    # Keep the measured no-swap host's extra process allowance bounded.
                    installed = list(Path('/etc').glob('multiimageclient-env-*/manifest.json'))
                    provision.require(len(installed) < 1, 'Host capacity allows one additional environment. Increase capacity before adding another.')
                    bundle = Path('/root') / ('mic-environment-' + ident)
                    if not bundle.exists():
                        provision.prepare(types.SimpleNamespace(id=ident, name=env['name'], slug=env['slug'], managed=True,
                            settings_source=str(SETTINGS), output=str(bundle), nginx_site=str(SITE),
                            dotnet='/home/tparkour/.dotnet/dotnet', port=5961, memory_high_mib=384,
                            memory_max_mib=512, max_requests=1, providers=','.join(env.get('defaultGenerators') or []),
                            copy_grok_session=False))
                    provision.install(types.SimpleNamespace(bundle=str(bundle), publish=str(PUBLISH)))
                manifest = json.loads(manifest_path.read_text())
                provision.require(manifest['id'] == ident and manifest.get('managed') is True, 'Environment identity mismatch.')
                include = Path('/etc/nginx') / (service + '.locations')
                provision.require(include.is_file(), 'Environment installation is incomplete. Its public route has not been installed.')
                with urllib.request.urlopen(f"http://127.0.0.1:{manifest['port']}/healthz", timeout=5) as response:
                    provision.require(response.status == 200, 'The environment failed its health check.')
                if manifest['privatePath'] != env['slug']:
                    manifest['privatePath'] = env['slug']
                    include = Path('/etc/nginx') / (service + '.locations')
                    old = include.read_bytes(); include.write_text(provision.locations(manifest))
                    try:
                        provision.run('nginx', '-t'); provision.run('systemctl', 'reload', 'nginx')
                    except Exception:
                        include.write_bytes(old); raise
                    private_json(manifest_path, manifest)
                outcomes[ident] = {'status': 'ready'}
            except Exception as exc:
                # ValueError messages from provision are designed to exclude credentials and private paths.
                outcomes[ident] = {'status': 'blocked', 'error': str(exc) if isinstance(exc, ValueError)
                                   and not isinstance(exc, json.JSONDecodeError) else 'Provisioning failed. Inspect this environment on the server.'}
        group = grp.getgrnam('mic-auth'); private_json(ROOT / 'provisioning.json', outcomes, 0, group.gr_gid)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('--initialize', action='store_true')
    args = parser.parse_args()
    provision.require(os.geteuid() == 0, 'The controller requires root.')
    if args.initialize: initialize()
    else: reconcile()
