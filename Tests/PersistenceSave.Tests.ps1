$ErrorActionPreference = 'Stop'

$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Services\SettingsService.cs') -Raw
$workspace = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Services\WorkspaceStore.cs') -Raw
$pane = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Views\WorkspacePaneView.xaml.cs') -Raw
$window = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\MainWindow.xaml.cs') -Raw

function Assert-Match([string]$text, [string]$pattern, [string]$message) {
    if ($text -notmatch $pattern) { throw $message }
}

Assert-Match $settings 'private static void Save\(\)\s*\{\s*_ = TrySave\(\);' `
    'Ordinary settings saves must retain their synchronous durability contract.'
Assert-Match $settings 'private static bool TrySave\(\)[\s\S]*?SaveQueue\.Enqueue[\s\S]*?SaveQueue\.Flush\(version\)' `
    'Result-sensitive settings saves must wait for their exact queued version.'
Assert-Match $settings 'private static void SaveDeferred\(\)[\s\S]*?SaveQueue\.Enqueue\(SerializeCurrent\(\)\)' `
    'Only transition-time settings saves may return before the queued snapshot is durable.'
Assert-Match $settings 'SaveRoomRegistration[\s\S]*?if \(changed\) SaveDeferred\(\);' `
    'New-session registration must use the deferred transition save.'
Assert-Match $settings 'SaveLastActive[\s\S]*?SaveDeferred\(\);' `
    'Active-session tracking must use the deferred transition save.'
Assert-Match $settings 'public static bool FlushPendingSaves\(\) => SaveQueue\.FlushAll\(\);' `
    'Settings must expose a normal-close flush.'
Assert-Match $workspace 'SaveQueue\.Enqueue\(new SaveSnapshot\([\s\S]*?SerializeSessionsIndex\(list\)' `
    'Workspace and session-index snapshots must be queued together.'
Assert-Match $workspace 'public static void Save\([\s\S]*?SaveQueue\.Flush\(queuedVersion\)' `
    'Ordinary workspace saves must retain their synchronous durability contract.'
Assert-Match $workspace 'AtomicFile\.WriteAllText\(WorkspacePath, snapshot\.WorkspaceJson\);[\s\S]*?AtomicFile\.WriteAllText\(SessionsIndexPath, snapshot\.SessionsIndexJson\);' `
    'The background writer must preserve workspace-before-index ordering.'
Assert-Match $window 'WorkspaceStore\.FlushPendingSaves\(\);\s*SettingsService\.FlushPendingSaves\(\);' `
    'Normal close must flush both persistence queues after final state updates.'

$addStart = $pane.IndexOf('public SessionItem? AddSession(ProjectItem proj)', [StringComparison]::Ordinal)
$addEnd = $pane.IndexOf('public void ForkSession(SessionItem source)', $addStart, [StringComparison]::Ordinal)
if ($addStart -lt 0 -or $addEnd -le $addStart) { throw 'Could not isolate AddSession.' }
$add = $pane.Substring($addStart, $addEnd - $addStart)
Assert-Match $add 'SaveRoomRegistration\(session\.Id, proj\.Path, agentId\);' `
    'New-session path and agent must be persisted in one settings snapshot.'
Assert-Match $add 'SetActiveTabRef\(proj, "S:" \+ session\.Id\);\s*WorkspaceStore\.SaveDeferred\(Projects\);' `
    'New-session active-tab state must be included in its structural workspace snapshot.'
if ($add -match 'SaveClaudeCodeRoomDir\(session\.Id|SaveAgentForRoom\(session\.Id') {
    throw 'New-session registration still performs duplicate settings saves.'
}

Assert-Match $pane 'private void RecordActiveTab[\s\S]*?WorkspaceStore\.SaveDeferred\(Projects\);' `
    'Tab switches must not wait for durable workspace I/O on the UI thread.'

Write-Output 'PASS: settings, workspace, shutdown, and new-session persistence wiring is guarded.'
