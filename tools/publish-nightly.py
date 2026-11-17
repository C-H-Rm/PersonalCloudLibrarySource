"""Publish verified main-build artifacts as the fork's managed nightly prerelease."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import zipfile
from typing import Any

REPOSITORY = 'C-H-Rm/PersonalCloudLibrarySource'
MARKER = '<!-- pcls-managed-nightly-v1 -->'
TAG = 'nightly'


def gh(*args, data=None, missing_ok=False) -> Any:
    result = subprocess.run(['gh', *args], input=json.dumps(data) if data is not None else None,
                            capture_output=True, text=True, check=False)
    if result.returncode:
        try:
            missing = str(json.loads(result.stdout).get('status')) == '404'
        except (ValueError, AttributeError):
            missing = False
        if missing_ok and missing:
            return None
        raise RuntimeError(result.stderr.strip() or 'GitHub command failed')
    return json.loads(result.stdout) if args[0] == 'api' and result.stdout.strip() else result.stdout


def api(path, method='GET', data=None, missing_ok=False):
    args = ['api', f'repos/{REPOSITORY}/{path}', '--method', method]
    if data is not None:
        args += ['--input', '-']
    return gh(*args, data=data, missing_ok=missing_ok)


def artifacts(directory, sha):
    """Reject stale, tampered or incomplete packages before any remote mutation."""
    directory = Path(directory)
    packages = list(directory.glob('*.pext'))
    if len(packages) != 1:
        raise ValueError('Exactly one installable .pext is required')
    package = packages[0]
    if not re.fullmatch(r'CHRCloudLibrary-[0-9]+(?:\.[0-9]+){2,3}\.pext', package.name):
        raise ValueError('Unexpected package filename')
    receipt = directory / (package.stem + '.build.json')
    checksum = directory / (package.name + '.sha256')
    metadata = json.loads(receipt.read_text(encoding='utf-8-sig'))
    digest = hashlib.sha256(package.read_bytes()).hexdigest()
    if (metadata.get('sourceCommit') != sha or metadata.get('sourceDirty') is not False
            or metadata.get('package') != package.name or metadata.get('sha256') != digest
            or checksum.read_text(encoding='utf-8-sig').strip() != f'{digest}  {package.name}'):
        raise ValueError('Package hash, source commit or clean-build receipt does not match')
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        required = {'CHRCloudLibrary.dll', 'extension.yaml', 'icon.png'}
        if (not required.issubset(names) or len(names) != len(set(names))
                or any(n not in required and not n.startswith('Localization/') for n in names)
                or any('..' in n.split('/') or '\\' in n or n.startswith('/') for n in names)
                or archive.testzip() is not None):
            raise ValueError('Invalid installable package layout')
    return [package, checksum, receipt]


def publish(directory, env=None):
    env = os.environ if env is None else env
    if (env.get('GITHUB_REPOSITORY') != REPOSITORY
            or env.get('GITHUB_REF') != 'refs/heads/main'
            or env.get('GITHUB_EVENT_NAME') not in {'push', 'workflow_dispatch'}):
        raise ValueError('Only successful trusted main workflow builds may publish')
    sha = env.get('GITHUB_SHA', '')
    run_id = env.get('GITHUB_RUN_ID', '')
    if not re.fullmatch('[0-9a-f]{40}', sha) or not re.fullmatch('[0-9]+', run_id):
        raise ValueError('Invalid workflow source identity')
    files = artifacts(directory, sha)
    if api('git/ref/heads/main')['object']['sha'] != sha:
        print('Skipping outdated build: main has advanced.')
        return
    release = api(f'releases/tags/{TAG}', missing_ok=True)
    if release and (MARKER not in (release.get('body') or '') or not release['prerelease']):
        raise ValueError('Refusing to overwrite an unmanaged or stable release')
    # Hide a partially updated release on failure; never present mismatched assets.
    if release:
        api(f"releases/{release['id']}", 'PATCH', {'draft': True})
    ref = api(f'git/ref/tags/{TAG}', missing_ok=True)
    if ref:
        api(f'git/refs/tags/{TAG}', 'PATCH', {'sha': sha, 'force': True})
    else:
        api('git/refs', 'POST', {'ref': f'refs/tags/{TAG}', 'sha': sha})
    if not release:
        release = api('releases', 'POST', {'tag_name': TAG, 'target_commitish': sha,
            'name': 'Nightly', 'body': MARKER, 'draft': True, 'prerelease': True, 'make_latest': 'false'})
    gh('release', 'upload', TAG, *[str(p) for p in files], '--repo', REPOSITORY, '--clobber')
    # Verify the actual uploaded bytes before exposing the release publicly.
    with tempfile.TemporaryDirectory(prefix='pcls-nightly-verify-') as scratch:
        patterns = [arg for p in files for arg in ('--pattern', p.name)]
        gh('release', 'download', TAG, '--repo', REPOSITORY, '--dir', scratch, *patterns)
        for path in files:
            if hashlib.sha256((Path(scratch) / path.name).read_bytes()).digest() != hashlib.sha256(path.read_bytes()).digest():
                raise ValueError('Uploaded release asset readback failed')
    expected = {p.name for p in files}
    for asset in api(f"releases/{release['id']}/assets?per_page=100"):
        if (asset['name'] not in expected and re.fullmatch(
                r'CHRCloudLibrary-[0-9.]+\.(?:pext(?:\.sha256)?|build\.json)', asset['name'])):
            api(f"releases/assets/{asset['id']}", 'DELETE')
    notes = (f'{MARKER}\nAutomated prerelease from `main` at `{sha}`.\n\n'
             f'[Verified Windows build](https://github.com/{REPOSITORY}/actions/runs/{run_id})\n\n'
             'Download the `.pext` below to install in Playnite. SHA-256 and source build receipt are included. '
             'This is a development build, separate from stable releases; Playnite UI/runtime testing is not implied.')
    api(f"releases/{release['id']}", 'PATCH', {'name': 'Nightly', 'body': notes,
        'target_commitish': sha, 'prerelease': True, 'draft': False, 'make_latest': 'false'})
    actual = api(f'releases/tags/{TAG}')
    actual_ref = api(f'git/ref/tags/{TAG}')
    if (actual['draft'] or not actual['prerelease'] or actual['tag_name'] != TAG
            or actual_ref['object']['sha'] != sha):
        raise ValueError('Published nightly state could not be verified')
    print(actual['html_url'])
    if env.get('GITHUB_STEP_SUMMARY'):
        with open(env['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as summary:
            summary.write(f"### Nightly release\n{actual['html_url']}\n\nSource: `{sha}`\n")


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', default='dist')
    publish(parser.parse_args().artifacts)
