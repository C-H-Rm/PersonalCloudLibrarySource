"""Offline publication tests: real fixture ZIP/hash checks, explicitly fake GitHub."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location('nightly', Path(__file__).with_name('publish-nightly.py'))
assert spec and spec.loader
nightly = importlib.util.module_from_spec(spec)
spec.loader.exec_module(nightly)
SHA = 'a' * 40
ENV = {'GITHUB_REPOSITORY': nightly.REPOSITORY, 'GITHUB_REF': 'refs/heads/main',
       'GITHUB_EVENT_NAME': 'push', 'GITHUB_SHA': SHA, 'GITHUB_RUN_ID': '1234'}


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='pcls-nightly-unit-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.package = self.root / 'CloudStorageIntegration-1.2.3.pext'
        with zipfile.ZipFile(self.package, 'w') as archive:
            for name in ('CloudStorageIntegration.dll', 'extension.yaml', 'icon.png', 'Localization/en_US.xaml'):
                archive.writestr(name, 'Explicit unit-test fixture, not a compiled plugin')
        digest = hashlib.sha256(self.package.read_bytes()).hexdigest()
        self.receipt = self.root / 'CloudStorageIntegration-1.2.3.build.json'
        self.receipt.write_text(json.dumps({'sourceCommit': SHA, 'sourceDirty': False,
            'sha256': digest, 'package': self.package.name}))
        self.checksum = self.root / (self.package.name + '.sha256')
        self.checksum.write_text(f'{digest}  {self.package.name}\n')

    def test_valid_artifacts_are_exactly_package_checksum_and_receipt(self):
        self.assertEqual(nightly.artifacts(self.root, SHA), [self.package, self.checksum, self.receipt])

    def test_untrusted_events_refs_and_repositories_never_reach_github(self):
        for key, value in [('GITHUB_EVENT_NAME', 'pull_request'), ('GITHUB_REF', 'refs/heads/feature'),
                           ('GITHUB_REPOSITORY', 'someone/else'), ('GITHUB_SHA', 'not-a-sha')]:
            with self.subTest(key=key), patch.object(nightly, 'api') as api:
                with self.assertRaises(ValueError):
                    nightly.publish(self.root, {**ENV, key: value})
                api.assert_not_called()

    def test_tampered_or_wrong_source_artifact_prevents_remote_calls(self):
        original = self.receipt.read_text()
        for changes in ({'sourceDirty': True}, {'sourceCommit': 'b' * 40}, {'sha256': '0' * 64}):
            self.receipt.write_text(json.dumps({**json.loads(original), **changes}))
            with self.subTest(changes=changes), patch.object(nightly, 'api') as api:
                with self.assertRaises(ValueError):
                    nightly.publish(self.root, ENV)
                api.assert_not_called()
        self.receipt.write_text(original)
        self.checksum.write_text('wrong checksum')
        with self.assertRaises(ValueError):
            nightly.artifacts(self.root, SHA)

    def test_outdated_build_does_not_modify_nightly(self):
        with patch.object(nightly, 'api', return_value={'object': {'sha': 'b' * 40}}) as api:
            nightly.publish(self.root, ENV)
            self.assertEqual(api.call_count, 1)

    def test_unmanaged_or_stable_release_is_not_changed(self):
        for existing in ({'body': 'Manual release', 'prerelease': True},
                         {'body': nightly.MARKER, 'prerelease': False}):
            with self.subTest(existing=existing), patch.object(nightly, 'api', side_effect=[
                    {'object': {'sha': SHA}}, existing]) as api:
                with self.assertRaises(ValueError):
                    nightly.publish(self.root, ENV)
                self.assertTrue(all(len(c.args) == 1 for c in api.call_args_list))

    def fake_publish(self, existing, fail_upload=False, corrupt_readback=False):
        remote = {'id': 7, 'tag_name': 'nightly', 'prerelease': True, 'draft': False,
                  'body': nightly.MARKER, 'html_url': 'https://example.invalid/nightly'} if existing else None
        ref = {'object': {'sha': 'b' * 40}} if existing else None
        mutations = []
        deleted = []
        def api(path, method='GET', data=None, missing_ok=False):
            nonlocal remote, ref
            if method != 'GET':
                mutations.append((path, method, copy.deepcopy(data)))
            if path == 'git/ref/heads/main':
                return {'object': {'sha': SHA}}
            if path == 'releases/tags/nightly':
                return copy.deepcopy(remote)
            if path == 'git/ref/tags/nightly':
                return copy.deepcopy(ref)
            if path in {'git/refs', 'git/refs/tags/nightly'}:
                assert isinstance(data, dict)
                ref = {'object': {'sha': data['sha']}}
                return ref
            if path == 'releases':
                assert isinstance(data, dict)
                remote = {**data, 'id': 7, 'html_url': 'https://example.invalid/nightly'}
                return copy.deepcopy(remote)
            if path == 'releases/7':
                assert remote is not None and isinstance(data, dict)
                remote.update(data)
                return copy.deepcopy(remote)
            if path == 'releases/7/assets?per_page=100':
                return [{'id': 8, 'name': 'CloudStorageIntegration-1.0.0.pext'},
                        {'id': 9, 'name': 'manual-notes.txt'}]
            if path.startswith('releases/assets/') and method == 'DELETE':
                deleted.append(path)
                return None
            raise AssertionError((path, method))
        def gh(*args, **kwargs):
            if args[:2] == ('release', 'upload'):
                if fail_upload:
                    raise RuntimeError('Explicit fake upload failure')
            elif args[:2] == ('release', 'download'):
                dest = Path(args[args.index('--dir') + 1])
                for source in (self.package, self.checksum, self.receipt):
                    (dest / source.name).write_bytes(b'corrupt' if corrupt_readback else source.read_bytes())
            else:
                raise AssertionError(args)
        with patch.object(nightly, 'api', side_effect=api), patch.object(nightly, 'gh', side_effect=gh):
            if fail_upload or corrupt_readback:
                with self.assertRaises((RuntimeError, ValueError)):
                    nightly.publish(self.root, ENV)
                assert remote is not None
                self.assertTrue(remote['draft'])
                self.assertFalse(any(body and body.get('draft') is False for _, _, body in mutations))
            else:
                nightly.publish(self.root, ENV)
                assert remote is not None and ref is not None
                self.assertFalse(remote['draft'])
                self.assertTrue(remote['prerelease'])
                self.assertEqual(remote['make_latest'], 'false')
                self.assertEqual(ref['object']['sha'], SHA)
                self.assertEqual(deleted, ['releases/assets/8'])
                self.assertIn(SHA, remote['body'])

    def test_first_publication_creates_only_managed_nightly(self):
        self.fake_publish(existing=False)

    def test_update_moves_nightly_and_preserves_unrelated_assets(self):
        self.fake_publish(existing=True)

    def test_failed_upload_does_not_publish_partial_release(self):
        self.fake_publish(existing=True, fail_upload=True)

    def test_wrong_uploaded_bytes_do_not_publish(self):
        self.fake_publish(existing=False, corrupt_readback=True)

    def test_missing_api_resource_is_distinct_from_permission_error(self):
        result = subprocess.CompletedProcess([], 1, '{"status":"404"}', 'not found')
        with patch.object(nightly.subprocess, 'run', return_value=result):
            self.assertIsNone(nightly.api('releases/tags/nightly', missing_ok=True))
        result = subprocess.CompletedProcess([], 1, '{"status":"403"}', 'permission denied')
        with patch.object(nightly.subprocess, 'run', return_value=result):
            with self.assertRaises(RuntimeError):
                nightly.api('releases/tags/nightly', missing_ok=True)


if __name__ == '__main__':
    unittest.main(verbosity=2)
