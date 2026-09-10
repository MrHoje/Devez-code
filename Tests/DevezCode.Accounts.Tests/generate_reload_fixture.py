"""Compile the production account-change method with isolated process/UI doubles.

Only credential store locations and the usage-only disconnect marker are replaced;
the account-change/error handling method is extracted unchanged otherwise.
"""
from pathlib import Path
import json
import re

root = Path(__file__).resolve().parents[2]
source = (root / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
start = source.index("    public async Task SwitchCliAccountAsync(")
end = source.index("    private async Task ReloadAllSessionsForThemeCoreAsync()", start)
method = source[start:end].replace("CliAccountStore.Instance", "TestStore")
method = method.replace("CodexCredentialStore.Enable();", 'Record("usage-enabled");')
out = Path(__file__).parent / "obj" / "SessionSwitchMethod.g.cs"
out.parent.mkdir(exist_ok=True)
content = "using DevezCode.Services;\nusing System.Windows;\nnamespace DevezCode;\npublic partial class MainWindow {\n" + method + "\n}\n"
content = "#nullable enable\nusing System.IO;\n" + content
if not out.exists() or out.read_text(encoding="utf-8") != content:
    temp = out.with_suffix(".tmp")
    temp.write_text(content, encoding="utf-8")
    temp.replace(out)

app_source = (root / "App.xaml.cs").read_text(encoding="utf-8-sig")
palette_source = app_source[app_source.index("public void SetTheme("):]
palette_source = palette_source[:palette_source.index('res["BgColor"]')]
brushes = {"bg": "BgBrush", "panel": "PanelBrush", "panelSoft": "PanelSoftBrush", "line": "LineBrush",
           "text": "TextBrush", "textMuted": "TextMutedBrush", "primary": "PrimaryBrush", "primaryHover": "PrimaryHoverBrush",
           "primaryPressed": "PrimaryPressedBrush", "primarySoft": "PrimarySoftBrush", "danger": "DangerBrush"}
palettes = {}
for match in re.finditer(r'(?:if \(theme == "([^"]+)"\)|else // (minimal))\s*\{(.*?)\n        \}', palette_source, re.S):
    values = {}
    for color in re.finditer(r'(\w+)\s*=\s*Color.FromRgb\((0x[\da-fA-F]+),\s*(0x[\da-fA-F]+),\s*(0x[\da-fA-F]+)\)', match[3]):
        if color[1] in brushes:
            values[brushes[color[1]]] = '#' + ''.join(f'{int(color[i], 16):02X}' for i in (2, 3, 4))
    for color in re.finditer(r'(\w+)\s*=\s*Colors.White;', match[3]):
        if color[1] in brushes:
            values[brushes[color[1]]] = '#FFFFFF'
    assert len(values) == len(brushes), "Incomplete production theme palette"
    palettes[match[1] or match[2]] = values
assert len(palettes) == 6
palette_file = out.parent / "ThemePalette.json"
palette_file.write_text(json.dumps(palettes), encoding="utf-8")
