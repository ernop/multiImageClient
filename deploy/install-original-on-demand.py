#!/usr/bin/env python3
"""Enable socket activation only for the existing original environment."""
import base64
import hashlib
import hmac
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import urllib.request


def run(*args):
    subprocess.run(args, check=True)


def main():
    if os.geteuid() != 0:
        raise RuntimeError("Run as root on the production host.")
    settings_path = Path('/etc/multiimageclient/settings.json')
    settings = json.loads(settings_path.read_text())
    if settings.get('UiEnvironmentId') != 'original' or not settings.get('UiEnvironmentController'):
        raise RuntimeError('The selected service is not the original controller environment.')
    auth = json.loads(Path(settings['UiAuthFilePath']).read_text())
    owner = next(a for a in auth['accounts'] if a['username'] == 'ernieMultiZone')
    signature = base64.urlsafe_b64encode(hmac.new(auth['secret'].encode(),
        (owner['username'] + '\n' + owner['passwordHash']).encode(), hashlib.sha256).digest()).decode().rstrip('=')
    def read(path):
        request = urllib.request.Request('http://127.0.0.1:5960/' + path,
            headers={'Cookie': 'mic_auth=' + owner['username'] + '.' + signature})
        with urllib.request.urlopen(request, timeout=15) as response:
            return json.load(response)
    status = read('api/status')
    if any(status[k] for k in ('pendingJobCount', 'queuedTargetRequests', 'runningTargetRequests')):
        raise RuntimeError('Wait for all original-environment jobs to finish before enabling sleep.')
    if read('api/goal-loops')['runningCount']:
        raise RuntimeError('Wait for original-environment goal loops to finish before enabling sleep.')
    # Install only after the socket-aware binary has been released.
    if not read('api/config').get('socketActivationSupported'):
        raise RuntimeError('Release socket-activation support before enabling it.')
    backup = settings_path.with_name('settings.before-on-demand.json')
    if not backup.exists():
        shutil.copy2(settings_path, backup)
    settings['UiIdleTimeoutSeconds'] = 900
    info = settings_path.stat()
    fd, temporary = tempfile.mkstemp(dir=settings_path.parent)
    try:
        with os.fdopen(fd, 'w') as output:
            json.dump(settings, output, indent=2)
            output.write('\n')
        os.chmod(temporary, info.st_mode & 0o777)
        os.chown(temporary, info.st_uid, info.st_gid)
        os.replace(temporary, settings_path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    unit_root = Path('/etc/systemd/system')
    shutil.copyfile(Path(__file__).with_name('multiimageclient-ui.socket'), unit_root / 'multiimageclient-ui.socket')
    os.chmod(unit_root / 'multiimageclient-ui.socket', 0o644)
    dropin = unit_root / 'multiimageclient-ui.service.d/on-demand.conf'
    dropin.parent.mkdir(exist_ok=True)
    dropin.write_text('[Unit]\nRequires=multiimageclient-ui.socket\nAfter=multiimageclient-ui.socket\n\n[Service]\nRestart=on-failure\n')
    os.chmod(dropin, 0o644)
    run('systemctl', 'stop', 'multiimageclient-ui.service')
    run('systemctl', 'daemon-reload')
    run('systemctl', 'disable', 'multiimageclient-ui.service')
    run('systemctl', 'enable', '--now', 'multiimageclient-ui.socket')
    print('Original environment now wakes on demand and sleeps after 15 idle minutes. Vibecoders remains resident.')


if __name__ == '__main__':
    main()
