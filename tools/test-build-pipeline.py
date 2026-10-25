"""Local integration tests for the actual PowerShell build/package entrypoint.

Tests committed source in a clean clone; no GitHub/Nextcloud API calls or pushes.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pwsh', default=shutil.which('pwsh'))
    parser.add_argument('--dotnet', default=shutil.which('dotnet'))
    parser.add_argument('--framework-root', help='Optional .NET Framework reference pack build directory for Linux cross-builds')
    parser.add_argument('--actionlint', default=shutil.which('actionlint'))
    args = parser.parse_args()
    if not args.pwsh or not args.dotnet or not args.actionlint:
        parser.error('pwsh, dotnet and actionlint are required (PATH or explicit options).')
    source = Path(__file__).resolve().parent.parent
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=source, text=True).strip()
    if subprocess.check_output(['git', 'status', '--porcelain'], cwd=source, text=True).strip():
        raise SystemExit('Commit the candidate locally first: this test must exercise exact, clean source.')
    root = Path(tempfile.mkdtemp(prefix='pcls-ci-test-', dir=os.environ.get('TMPDIR')))
    fixture = root / 'clean checkout with spaces'
    subprocess.run(['git', 'clone', '--quiet', '--no-hardlinks', str(source), str(fixture)], check=True)
    assert subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=fixture, text=True).strip() == commit
    assert not (fixture / 'PersonalCloudLibrarySource/bin').exists()
    assert not (fixture / 'PersonalCloudLibrarySource/obj').exists()
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1')
    output_file = root / 'github output.txt'
    env['GITHUB_OUTPUT'] = str(output_file)
    env['PATH'] = str(Path(args.dotnet).resolve().parent) + os.pathsep + env.get('PATH', '')
    cases = []

    def run(command, expected=0, cwd=fixture):
        p = subprocess.run(command, cwd=cwd, env=env, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, text=True, timeout=180)
        if (expected == 0 and p.returncode != 0) or (expected != 0 and p.returncode == 0):
            raise AssertionError(p.stdout)
        return p.stdout

    def passed(name):
        cases.append(name)
        print('PASS pipeline ' + name, flush=True)

    def package(extra=(), explicit=True):
        output_file.write_text('')
        command = [args.pwsh, '-NoLogo', '-NoProfile', '-File', 'tools/package-extension.ps1']
        if explicit:
            command += ['-MSBuildPath', str(Path(args.dotnet).resolve())]
        if args.framework_root:
            command += ['-TargetFrameworkRootPath', str(Path(args.framework_root).resolve()) + os.sep]
        return command + list(extra)

    run([args.actionlint, '.github/workflows/build-plugin.yml'])
    passed('workflow passes actionlint')
    build_log = run(package())
    outputs = dict(line.split('=', 1) for line in output_file.read_text().splitlines())
    assert set(outputs) == {'package_path', 'package_name', 'receipt_path', 'checksum_path', 'version'}
    plugin = fixture / outputs['package_path']
    receipt = json.loads((fixture / outputs['receipt_path']).read_text())
    assert receipt['sourceCommit'] == commit and receipt['sourceDirty'] is False
    assert receipt['runtimeTested'] is False
    original_hash = hashlib.sha256(plugin.read_bytes()).hexdigest()
    assert receipt['sha256'] == original_hash
    assert (fixture / outputs['checksum_path']).read_text().strip() == original_hash + '  ' + plugin.name
    with zipfile.ZipFile(plugin) as archive:
        assert archive.testzip() is None
        assert set(archive.namelist()) == {'PersonalCloudLibrarySource.dll', 'extension.yaml', 'icon.png', 'Localization/en_US.xaml'}
    passed('clean checkout rebuilds real plugin, WPF resources, receipts and GitHub outputs')
    guard_log = run([args.pwsh, '-NoLogo', '-NoProfile', '-File', 'tools/test-package-extension.ps1', '-PackagePath', str(plugin)])
    guards = re.search(r'RESULT packaging: (\d+) passed, (\d+) failed', guard_log)
    assert guards and int(guards.group(2)) == 0
    passed('real package and intentionally broken package guards pass')

    code = fixture / 'PersonalCloudLibrarySource/PersonalCloudLibrarySource.cs'
    original_code = code.read_bytes()
    try:
        code.write_bytes(original_code + b'\n#error PCLS_INTENTIONAL_BUILD_FAILURE\n')
        failure_log = run(package(), expected=1)
        assert 'PCLS_INTENTIONAL_BUILD_FAILURE' in failure_log
        assert not output_file.read_text()
        assert hashlib.sha256(plugin.read_bytes()).hexdigest() == original_hash
        assert not list((fixture / 'dist').glob('.package-*'))
    finally:
        code.write_bytes(original_code)
    passed('real compiler failure emits no successful artifact paths or replacement package')

    localization = fixture / 'PersonalCloudLibrarySource/Localization/en_US.xaml'
    original_localization = localization.read_bytes()
    try:
        localization.write_text('<broken>')
        validation_failure_log = run(package(), expected=1)
        assert not output_file.read_text()
        assert hashlib.sha256(plugin.read_bytes()).hexdigest() == original_hash
        assert not list((fixture / 'dist').glob('.package-*'))
    finally:
        localization.write_bytes(original_localization)
    passed('invalid built localization fails packaging without publishing outputs or stale stage files')

    debug_log = run(package(['-DebugSymbols']))
    debug_package = fixture / ('dist/PersonalCloudLibrarySource-' + outputs['version'] + '-debug-symbols.zip')
    with zipfile.ZipFile(debug_package) as archive:
        assert archive.namelist() == ['PersonalCloudLibrarySource.pdb']
    with zipfile.ZipFile(plugin) as archive:
        assert not any(name.endswith('.pdb') or name.endswith('Playnite.SDK.dll') for name in archive.namelist())
    passed('optional PDB archive stays separate from installable plugin')

    fallback_log = run(package(explicit=False))
    assert output_file.read_text()
    passed('PATH discovery builds without a hard-coded Visual Studio path')
    final_receipt = json.loads((fixture / outputs['receipt_path']).read_text())
    assert final_receipt['sourceDirty'] is False and final_receipt['sourceCommit'] == commit
    dest = source / 'dist'
    dest.mkdir(exist_ok=True)
    for key in ('package_path', 'receipt_path', 'checksum_path'):
        shutil.copy2(fixture / outputs[key], dest / Path(outputs[key]).name)
    # Raw logs can contain formatted/truncated absolute paths. Keep them only
    # in the private disposable fixture; export structured evidence instead.
    raw_logs = {'build': build_log, 'guards': guard_log, 'compiler_failure': failure_log,
                'validation_failure': validation_failure_log, 'debug_symbols': debug_log,
                'discovery': fallback_log}
    (root / 'raw-logs.json').write_text(json.dumps(raw_logs, indent=2))

    report = {
        'sourceCommit': commit,
        'hostedGitHubActionsRun': False,
        'windowsPlayniteRuntimeTested': False,
        'pipelineCasesPassed': len(cases),
        'packagingGuardsPassed': int(guards.group(1)),
        'cases': cases,
        'package': plugin.name,
        'packageSha256': final_receipt['sha256'],
        'rawLogsExported': False,
    }
    text = json.dumps(report, indent=2)
    assert 'logs' not in report
    assert not re.search(r'(?i)[a-z]:[\\/]|\\\\|(?<![a-z0-9])/[a-z0-9_.-]+', text), 'Absolute path found in exported evidence'
    assert all(str(path).casefold() not in text.casefold() for path in (root, fixture, source))
    passed('exported evidence excludes raw logs and absolute machine paths')
    report['pipelineCasesPassed'] = len(cases)
    text = json.dumps(report, indent=2)
    (dest / 'ci-local-verification.json').write_text(text)
    print('RESULT pipeline:', len(cases), 'passed; packaging guards:', guards.group(1), 'passed')
    print('Build receipt and installable package copied to dist. Disposable fixture retained:', root)


if __name__ == '__main__':
    main()
