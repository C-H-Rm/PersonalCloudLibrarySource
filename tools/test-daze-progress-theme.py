"""Exercise the actual pinned Daze package and real PowerShell host inserter."""
import argparse
import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--input', required=True, type=Path)
parser.add_argument('--pwsh', default='pwsh')
args, extra = parser.parse_known_args()
spec = importlib.util.spec_from_file_location('prepare_daze', Path(__file__).with_name('prepare-daze-progress-theme.py'))
assert spec is not None and spec.loader is not None
pack = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pack)
W = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'
X = '{http://schemas.microsoft.com/winfx/2006/xaml}'


class DazePackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix='pcls-daze-package-test-')
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.directory = Path(cls.temporary.name)
        cls.output = cls.directory / 'Daze-Cloud-Progress.pthm'
        cls.original = args.input.read_bytes()
        cls.receipt = pack.prepare(args.input, cls.output, args.pwsh)

    def test_both_actual_views_host_progress_immediately_above_action_row(self):
        with zipfile.ZipFile(self.output) as archive:
            for view in pack.VIEWS:
                root = ET.fromstring(archive.read(view.replace('/', '\\')))
                hosts = [e for e in root.iter(W + 'ContentControl') if e.get(X + 'Name') == 'PCLS_InstallProgress']
                self.assertEqual(len(hosts), 1, view)
                parents = {child: parent for parent in root.iter() for child in parent}
                column = parents[hosts[0]]
                self.assertEqual(column.tag, W + 'StackPanel')
                siblings = list(column)
                row = siblings[siblings.index(hosts[0]) + 1]
                self.assertEqual(row.get('Orientation'), 'Horizontal')
                self.assertTrue(any(e.get('Name') == 'PART_ButtonContextAction' or e.get(X + 'Name') == 'PART_ButtonContextAction' for e in row.iter(W + 'Button')))

    def test_only_manifest_and_two_views_change_all_other_daze_assets_preserved(self):
        with zipfile.ZipFile(args.input) as original, zipfile.ZipFile(self.output) as prepared:
            changed = [n for n in original.namelist() if original.read(n) != prepared.read(n)]
            self.assertEqual(set(changed), {'theme.yaml', *(v.replace('/', '\\') for v in pack.VIEWS)})
            self.assertEqual(set(prepared.namelist()) - set(original.namelist()), {'LICENSE-Daze.txt', 'PCLS-INTEGRATION.txt'})
            self.assertEqual(len(prepared.namelist()), len(set(n.replace('\\', '/').lower() for n in prepared.namelist())))
            manifest = prepared.read('theme.yaml').decode('utf-8')
            self.assertIn('Name: Daze - Cloud Progress', manifest)
            self.assertIn('Id: ' + pack.THEME_ID, manifest)
            self.assertNotIn('Id: ' + pack.SOURCE_ID, manifest)
            self.assertIn('ThemeApiVersion: 2.1.0', manifest)
            self.assertIn('Mode: Desktop', manifest)
        self.assertEqual(args.input.read_bytes(), self.original)
        self.assertEqual(hashlib.sha256(self.output.read_bytes()).hexdigest(), self.receipt['sha256'])

    def test_original_and_existing_output_cannot_be_overwritten(self):
        for target in (args.input, self.output):
            before = target.read_bytes()
            with self.assertRaises(ValueError):
                pack.prepare(args.input, target, args.pwsh)
            self.assertEqual(target.read_bytes(), before)

    def test_unknown_or_modified_source_is_refused_without_output(self):
        changed = self.directory / 'modified.pthm'
        changed.write_bytes(self.original + b'modified fixture')
        target = self.directory / 'refused.pthm'
        with self.assertRaises(ValueError):
            pack.prepare(changed, target, args.pwsh)
        self.assertFalse(target.exists())


if __name__ == '__main__':
    unittest.main(argv=[__file__, *extra], verbosity=2)
