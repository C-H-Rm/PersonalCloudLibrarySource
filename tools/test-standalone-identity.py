"""Standalone identity contract; optional verification of a genuinely built .pext."""
import argparse
from pathlib import Path
import re
import unittest
import zipfile

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / 'PersonalCloudLibrarySource'
NEW_ID = '402561c2-ea33-4c60-a8f8-ec25e997a1d5'
ADDON_ID = 'CHRCloudLibrary_' + NEW_ID
LEGACY_ID = '61993828-67a8-4468-93a2-293442e36328'
PACKAGE = None


class IdentityTests(unittest.TestCase):
    def test_distinct_public_identity_module_and_fork_links(self):
        manifest = (SOURCE / 'extension.yaml').read_text()
        self.assertRegex(manifest, r'(?m)^Id: ' + re.escape(ADDON_ID) + '$')
        self.assertRegex(manifest, r'(?m)^Name: Personal Cloud Library Source \(Standalone\)$')
        self.assertRegex(manifest, r'(?m)^Module: CHRCloudLibrary.dll$')
        self.assertNotIn(LEGACY_ID, manifest)
        self.assertNotIn('github.com/KurtusCobain/', manifest)
        self.assertIn('github.com/C-H-Rm/PersonalCloudLibrarySource', manifest)
        self.assertNotIn('AddonId:', manifest)

    def test_runtime_and_assembly_use_fork_identity(self):
        main = (SOURCE / 'PersonalCloudLibrarySource.cs').read_text()
        self.assertIn('public override Guid Id { get; } = ForkIdentity.PluginId;', main)
        identity = (SOURCE / 'ForkIdentity.cs').read_text()
        self.assertIn(NEW_ID, identity)
        self.assertIn(LEGACY_ID, identity)  # Explicit adoption, never silent ownership alias.
        project = (SOURCE / 'PersonalCloudLibrarySource.csproj').read_text()
        self.assertIn('<AssemblyName>CHRCloudLibrary</AssemblyName>', project)

    def test_adoption_does_not_rewrite_copied_settings(self):
        source = (SOURCE / 'LegacyMigrationIntegration.cs').read_text()
        callback = source.split('private void LoadCopiedSettings()', 1)[1].split('public override', 1)[0]
        self.assertNotIn('SavePluginSettings', callback)
        self.assertNotIn('copied.LibraryDisplayName =', callback)

    def test_built_payload_is_the_standalone_assembly_and_manifest(self):
        if not PACKAGE:
            self.skipTest('Pass --package after the full SDK/WPF rebuild.')
        with zipfile.ZipFile(PACKAGE) as archive:
            self.assertEqual(set(archive.namelist()), {
                'CHRCloudLibrary.dll', 'extension.yaml', 'icon.png', 'Localization/en_US.xaml'})
            self.assertIsNone(archive.testzip())
            self.assertIn('Id: ' + ADDON_ID, archive.read('extension.yaml').decode('utf-8-sig'))
            self.assertEqual(archive.read('CHRCloudLibrary.dll'),
                             (SOURCE / 'bin/Release/CHRCloudLibrary.dll').read_bytes())


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', type=Path)
    args = parser.parse_args()
    PACKAGE = args.package
    unittest.main(argv=['test-standalone-identity.py'], verbosity=2)
