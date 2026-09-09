#!/usr/bin/env python3
"""Linux integration test: inherited listener, idle exit, then reuse the same listener."""
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request

root = Path(__file__).resolve().parents[1]
binary = root / 'MultiImageClient/bin/Release/net10.0/MultiImageClient.dll'
dotnet = Path.home() / '.dotnet/dotnet'
wrapper = '''import os,sys
fd=int(sys.argv[1]);os.dup2(fd,3);os.set_inheritable(3,True)
os.environ['LISTEN_PID']=str(os.getpid());os.environ['LISTEN_FDS']='1'
os.execv(sys.argv[2],sys.argv[2:])
'''

with tempfile.TemporaryDirectory(prefix='mic-socket-test-') as directory, socket.socket() as listener:
    folder = Path(directory)
    listener.bind(('127.0.0.1', 0))
    listener.listen(64)
    port = listener.getsockname()[1]
    settings = {'LogFilePath': str(folder / 'app.log'), 'ImageDownloadBaseFolder': str(folder / 'saves'),
                'UiIdleTimeoutSeconds': 60, 'PromptFiles': []}
    (folder / 'settings.json').write_text(json.dumps(settings))
    env = {**os.environ, 'MULTIIMAGECLIENT_SETTINGS': str(folder / 'settings.json'), 'DOTNET_EnableWriteXorExecute': '0'}
    def start():
        return subprocess.Popen([sys.executable, '-c', wrapper, str(listener.fileno()), str(dotnet), str(binary),
                                 '--ui', '--ui-port', str(port), '--ui-no-open'],
                                cwd=folder, env=env, pass_fds=(listener.fileno(),), stdout=log, stderr=log)
    def get(path):
        with urllib.request.urlopen(f'http://127.0.0.1:{port}/{path}', timeout=20) as response:
            return response.read()
    with (folder / 'stdout.log').open('w') as log:
        process = start()
        try:
            assert json.loads(get('api/config'))['socketActivationSupported']
            started = time.monotonic()
            while process.poll() is None and time.monotonic() - started < 100:
                # Frequent health probes must not keep an otherwise idle process awake.
                if time.monotonic() - started < 50:
                    get('healthz')
                time.sleep(2)
            assert process.poll() == 0, 'The UI did not exit cleanly after its idle timeout.'
            print('PASS: inherited loopback listener; health probes do not prevent clean idle exit.', flush=True)
            process = start()
            assert json.loads(get('api/config'))['socketActivationSupported']
            assert process.poll() is None
            print('PASS: a new process serves the same retained activation socket.', flush=True)
        except Exception:
            log.flush()
            print((folder / 'stdout.log').read_text(), file=sys.stderr)
            raise
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=20)
