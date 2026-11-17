"""Offline tests for the guarded XAML copy helper; never modify a live theme."""
import argparse
from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--pwsh', default='pwsh')
args, extra = parser.parse_known_args()
SCRIPT = Path(__file__).with_name('add-install-progress-to-theme.ps1')
W = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'
X = 'http://schemas.microsoft.com/winfx/2006/xaml'
THEME = f'''<ResourceDictionary xmlns="{W}" xmlns:x="{X}" xmlns:d="urn:design-fixture">
<Style TargetType="{{x:Type ContentControl}}"><Setter Property="Template"><Setter.Value>
<ControlTemplate><StackPanel><TextBlock Text="Unrelated existing content"/>
<StackPanel Orientation="Horizontal"><Button Name="PART_ButtonContextAction"/><Button Name="Other"/></StackPanel>
</StackPanel></ControlTemplate></Setter.Value></Setter></Style></ResourceDictionary>'''


class ThemeTests(unittest.TestCase):
    def grid_theme(self, extra=''):
        return f'''<ResourceDictionary xmlns="{W}" xmlns:x="{X}"><ControlTemplate>
<Grid DockPanel.Dock="Top" Margin="-3,0,0,0">
<StackPanel Orientation="Horizontal" Grid.Row="2" Panel.ZIndex="3" Margin="0,10,0,10">
<Button Name="PART_ButtonContextAction" Width="150"/><Button Name="Other"/></StackPanel>{extra}
</Grid></ControlTemplate></ResourceDictionary>'''

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='pcls-theme-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / 'view.xaml'
        self.output = self.root / 'prepared.xaml'
        self.source.write_text(THEME)

    def run_helper(self, source=None, output=None, source_name=None):
        return subprocess.run([args.pwsh, '-NoProfile', '-File', str(SCRIPT),
            '-InputFile', str(source or self.source), '-OutputFile', str(output or self.output)]
            + (['-SourceName', source_name] if source_name else []),
            capture_output=True, text=True, timeout=20)

    def test_supported_layout_puts_one_host_above_button_row_without_editing_source(self):
        original = self.source.read_bytes()
        result = self.run_helper()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.source.read_bytes(), original)
        root = ET.parse(self.output).getroot()
        column = root.find('.//{' + W + '}ControlTemplate/{' + W + '}StackPanel')
        assert column is not None
        children = list(column)
        self.assertEqual(children[1].tag, '{' + W + '}ContentControl')
        self.assertEqual(children[1].get('{' + X + '}Name'), 'PCLS_InstallProgress')
        self.assertEqual(children[2].get('Orientation'), 'Horizontal')
        self.assertIn('xmlns:d="urn:design-fixture"', self.output.read_text())
        self.assertIn('Unrelated existing content', self.output.read_text())

    def test_idempotent_copy_does_not_duplicate_hosts(self):
        self.assertEqual(self.run_helper().returncode, 0)
        again = self.root / 'again.xaml'
        result = self.run_helper(source=self.output, output=again)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(again.read_bytes(), self.output.read_bytes())
        self.assertEqual(self.run_helper().returncode, 0)

    def test_standalone_host_is_distinct_and_idempotent(self):
        first = self.run_helper(source_name='CHRCL')
        self.assertEqual(first.returncode, 0, first.stderr)
        content = self.output.read_text()
        self.assertIn('CHRCL_InstallProgress', content)
        self.assertNotIn('PCLS_InstallProgress', content)
        again = self.root / 'again.xaml'
        result = self.run_helper(source=self.output, output=again, source_name='CHRCL')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(again.read_bytes(), self.output.read_bytes())

    def test_cannot_insert_a_second_provider_into_an_already_prepared_view(self):
        self.assertEqual(self.run_helper().returncode, 0)
        again = self.root / 'again.xaml'
        result = self.run_helper(source=self.output, output=again, source_name='CHRCL')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(again.exists())

    def test_cannot_overwrite_active_input(self):
        original = self.source.read_bytes()
        self.assertNotEqual(self.run_helper(output=self.source).returncode, 0)
        self.assertEqual(self.source.read_bytes(), original)

    def test_daze_grid_single_row_wraps_above_actions_preserving_placement(self):
        self.source.write_text(self.grid_theme())
        original = self.source.read_bytes()
        result = self.run_helper()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.source.read_bytes(), original)
        grid = ET.parse(self.output).find('.//{' + W + '}Grid')
        assert grid is not None
        self.assertEqual(grid.get('DockPanel.Dock'), 'Top')
        column, = list(grid)
        self.assertEqual(column.tag, '{' + W + '}StackPanel')
        self.assertEqual(column.get('Grid.Row'), '2')
        self.assertEqual(column.get('Panel.ZIndex'), '3')
        host, row = list(column)
        self.assertEqual(host.get('{' + X + '}Name'), 'PCLS_InstallProgress')
        self.assertEqual(row.get('Margin'), '0,10,0,10')
        self.assertEqual(list(row)[0].get('Width'), '150')

    def test_daze_grid_prepared_copy_is_idempotent(self):
        self.source.write_text(self.grid_theme())
        first = self.run_helper()
        self.assertEqual(first.returncode, 0, first.stderr)
        again = self.root / 'again.xaml'
        result = self.run_helper(source=self.output, output=again)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(again.read_bytes(), self.output.read_bytes())

    def test_ambiguous_grid_with_another_child_is_refused(self):
        self.source.write_text(self.grid_theme('<TextBlock Text="Unrelated overlay"/>'))
        self.assertNotEqual(self.run_helper().returncode, 0)
        self.assertFalse(self.output.exists())

    def test_existing_different_output_is_preserved(self):
        self.output.write_text('Keep this existing file')
        self.assertNotEqual(self.run_helper().returncode, 0)
        self.assertEqual(self.output.read_text(), 'Keep this existing file')

    def test_unknown_layout_is_refused(self):
        self.source.write_text(THEME.replace('Orientation="Horizontal"', 'Orientation="Vertical"'))
        self.assertNotEqual(self.run_helper().returncode, 0)
        self.assertFalse(self.output.exists())

    def test_duplicate_button_is_refused(self):
        self.source.write_text(THEME.replace('<Button Name="Other"/>', '<Button Name="PART_ButtonContextAction"/>'))
        self.assertNotEqual(self.run_helper().returncode, 0)
        self.assertFalse(self.output.exists())

    def test_duplicate_hosts_are_refused(self):
        self.source.write_text(THEME.replace('<TextBlock', '<ContentControl x:Name="PCLS_InstallProgress"/><ContentControl x:Name="PCLS_InstallProgress"/><TextBlock'))
        self.assertNotEqual(self.run_helper().returncode, 0)
        self.assertFalse(self.output.exists())

    def test_dtd_is_refused(self):
        self.source.write_text('<!DOCTYPE ResourceDictionary [<!ENTITY anything "fixture">]>' + THEME)
        self.assertNotEqual(self.run_helper().returncode, 0)
        self.assertFalse(self.output.exists())

    def test_missing_button_is_refused(self):
        self.source.write_text(THEME.replace('PART_ButtonContextAction', 'UnrelatedButton'))
        self.assertNotEqual(self.run_helper().returncode, 0)
        self.assertFalse(self.output.exists())


if __name__ == '__main__':
    unittest.main(argv=[__file__, *extra], verbosity=2)
