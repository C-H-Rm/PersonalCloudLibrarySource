"""Exercise the real sync coordinator over a disposable loopback WebDAV server."""
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.request


def main():
    rclone = os.environ.get('PCLS_RCLONE') or shutil.which('rclone')
    dotnet = os.environ.get('PCLS_DOTNET') or shutil.which('dotnet')
    if not rclone or not dotnet:
        raise SystemExit('Install rclone and .NET 8, or set PCLS_RCLONE and PCLS_DOTNET.')
    root = Path(tempfile.mkdtemp(prefix='pcls-webdav-test-', dir=os.environ.get('TMPDIR')))
    cloud = root / 'cloud'
    cloud.mkdir()
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    log = (root / 'server.log').open('wb')
    server = subprocess.Popen([rclone, 'serve', 'webdav', str(cloud), '--addr', f'127.0.0.1:{port}', '--dir-cache-time', '0'], stdout=log, stderr=log)
    try:
        url = f'http://127.0.0.1:{port}'
        for _ in range(30):
            try:
                with urllib.request.urlopen(url, timeout=1) as response:
                    if response.status != 200:
                        raise RuntimeError('Unexpected WebDAV readiness response')
                break
            except OSError:
                if server.poll() is not None:
                    raise RuntimeError('WebDAV test server exited')
                time.sleep(0.1)
        else:
            raise RuntimeError('WebDAV test server did not become ready')
        # Use a new, test-only remote; do not inherit the user's production remotes.
        config = root / 'rclone.conf'
        config.write_text(f'[pclstest]\ntype = webdav\nurl = {url}\nvendor = other\n')
        env = {key: value for key, value in os.environ.items() if not key.startswith('RCLONE_')}
        env.update(DOTNET_CLI_TELEMETRY_OPTOUT='1', RCLONE_CONFIG=str(config),
                   PCLS_WEBDAV_ROOT=str(root), PCLS_RCLONE=rclone)
        project = Path(__file__).with_name('ModSync.WebDav.Tests.csproj')
        result = subprocess.run([dotnet, 'run', '--project', str(project)], env=env, timeout=180)
        print('Isolated fixture retained for inspection:', root)
        return result.returncode
    finally:
        server.terminate()
        try:
            server.wait(timeout=10)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait()
        log.close()


if __name__ == '__main__':
    raise SystemExit(main())
