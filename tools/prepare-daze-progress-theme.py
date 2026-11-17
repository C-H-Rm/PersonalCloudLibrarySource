"""Prepare a separate Daze theme with PCLS hosts; never edit an installed theme.

Input is the pinned official Daze 2.3.1 .pthm. This repacks its existing
Playnite theme archive, retaining every unchanged member byte-for-byte.
"""
import argparse
import hashlib
import io
import json
import re
import subprocess
import tempfile
import uuid
import zipfile
from pathlib import Path

SOURCE_SHA256 = 'e82e507465b831cbc9128f457e3122bb517ecf77eb30440596e15ce25a214111'
SOURCE_ID = 'Daze_27790ca9-d3a4-480f-bffe-914ec6768363'
THEME_ID = 'Daze_PCLS_Progress_' + str(uuid.uuid5(uuid.NAMESPACE_URL, 'https://github.com/dariolupo/Daze_Theme#pcls-install-progress'))
THEME_NAME = 'Daze - Cloud Progress'
VIEWS = ('Views/DetailsViewGameOverview.xaml', 'Views/GridViewGameOverview.xaml')


def prepare(source, output, pwsh):
    source, output = Path(source).resolve(), Path(output).resolve()
    if source == output or output.exists():
        raise ValueError('Use a new output file; the original and existing outputs are never overwritten.')
    original = source.read_bytes()
    if hashlib.sha256(original).hexdigest() != SOURCE_SHA256:
        raise ValueError('Expected the pinned official Daze 2.3.1 package; refusing another or modified input.')
    support = Path(__file__).resolve().parent
    with zipfile.ZipFile(io.BytesIO(original)) as archive:
        infos = archive.infolist()
        names = {info.filename.replace('\\', '/'): info.filename for info in infos}
        if len(names) != len(infos) or len({n.lower() for n in names}) != len(names):
            raise ValueError('Duplicate archive names.')
        for name in names:
            if name.startswith('/') or ':' in name or any(p in ('..', '.') for p in name.split('/')):
                raise ValueError('Unsafe archive member.')
        manifest = archive.read(names['theme.yaml']).decode('utf-8-sig')
        if re.findall(r'^Id:\s*(\S+)\s*$', manifest, re.M) != [SOURCE_ID]:
            raise ValueError('Unexpected source theme identity.')
        replacements = {'Id': THEME_ID, 'Name': THEME_NAME,
                        'Author': 'Super Dario; PCLS integration by C-H-R', 'Version': '2.3.1.1'}
        for key, value in replacements.items():
            manifest, count = re.subn(r'^' + key + r':[^\r\n]*', key + ': ' + value, manifest, flags=re.M)
            if count != 1:
                raise ValueError('Missing or duplicate manifest field: ' + key)
        manifest += '\nMode: Desktop\n'
        changes = {names['theme.yaml']: manifest.encode('utf-8')}
        with tempfile.TemporaryDirectory(prefix='pcls-daze-') as temporary:
            for index, view in enumerate(VIEWS):
                incoming, prepared = Path(temporary) / f'{index}-original.xaml', Path(temporary) / f'{index}-prepared.xaml'
                incoming.write_bytes(archive.read(names[view]))
                result = subprocess.run([pwsh, '-NoProfile', '-File', str(support / 'add-install-progress-to-theme.ps1'),
                    '-InputFile', str(incoming), '-OutputFile', str(prepared)], capture_output=True, text=True, timeout=30)
                if result.returncode:
                    raise ValueError('Cannot integrate ' + view + ': ' + result.stderr)
                changes[names[view]] = prepared.read_bytes()
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, 'w', compression=zipfile.ZIP_DEFLATED) as packed:
            for info in infos:
                packed.writestr(info, changes.get(info.filename, archive.read(info)))
            packed.writestr('LICENSE-Daze.txt', (support / 'theme-support/Daze-LICENSE.txt').read_bytes())
            packed.writestr('PCLS-INTEGRATION.txt',
                'Local derivative of Daze 2.3.1 by Super Dario. Original theme is not replaced.\n'
                'Install this .pthm and select Daze - Cloud Progress in Playnite Desktop theme settings.\n'
                'Requires the Personal Cloud Library plugin build with PCLS_InstallProgress support.\n'
                'Hosts are placed above the action row in both Details and Grid game overviews.\n'
                'The status is hidden when the selected game has no active download/background status.\n'
                'Source: https://github.com/dariolupo/Daze_Theme/releases/tag/v2.3.1\n'
                'No Windows rendered-UI test has been performed by this packaging tool.\n')
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open('xb') as stream:
        stream.write(buffer.getvalue())
    return {'source_sha256': SOURCE_SHA256, 'output': str(output), 'sha256': hashlib.sha256(output.read_bytes()).hexdigest(),
            'theme_id': THEME_ID, 'name': THEME_NAME, 'modified_members': sorted(changes),
            'windows_runtime_tested': False}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--pwsh', default='pwsh')
    args = parser.parse_args()
    print(json.dumps(prepare(args.input, args.output, args.pwsh), indent=2))
