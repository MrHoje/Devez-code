$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$terminalSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Views\TerminalHostView.cs') -Raw -Encoding UTF8
$terminalHtml = Get-Content -LiteralPath (Join-Path $repoRoot 'Resources\Terminal\web\terminal.html') -Raw -Encoding UTF8
$paneSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Views\WorkspacePaneView.xaml.cs') -Raw -Encoding UTF8
$paneXaml = Get-Content -LiteralPath (Join-Path $repoRoot 'Views\WorkspacePaneView.xaml') -Raw -Encoding UTF8
$browserSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Views\BrowserHostView.xaml.cs') -Raw -Encoding UTF8
$fileExplorerSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Views\FileExplorerView.xaml.cs') -Raw -Encoding UTF8
$pdfSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Views\PdfFileEditorView.cs') -Raw -Encoding UTF8
$monacoSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Views\MonacoHost.cs') -Raw -Encoding UTF8
$monacoDiffSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Views\MonacoDiffHostView.xaml.cs') -Raw -Encoding UTF8
$windowSource = Get-Content -LiteralPath (Join-Path $repoRoot 'MainWindow.xaml.cs') -Raw -Encoding UTF8
$appSource = Get-Content -LiteralPath (Join-Path $repoRoot 'App.xaml.cs') -Raw -Encoding UTF8
$notificationSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Views\NotificationPopup.xaml') -Raw -Encoding UTF8
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-Match([string]$source, [string]$pattern, [string]$message) {
    if ($source -notmatch $pattern) { $script:failures.Add($message) }
}

function Assert-NotMatch([string]$source, [string]$pattern, [string]$message) {
    if ($source -match $pattern) { $script:failures.Add($message) }
}

function Assert-Order([string]$source, [string]$first, [string]$second, [string]$message) {
    $firstIndex = $source.IndexOf($first, [StringComparison]::Ordinal)
    $secondIndex = $source.IndexOf($second, [StringComparison]::Ordinal)
    if ($firstIndex -lt 0 -or $secondIndex -lt 0 -or $firstIndex -ge $secondIndex) {
        $script:failures.Add($message)
    }
}

function Assert-Count([string]$source, [string]$pattern, [int]$expected, [string]$message) {
    if ([regex]::Matches($source, $pattern).Count -ne $expected) { $script:failures.Add($message) }
}

function Get-SourceSlice([string]$source, [string]$start, [string]$end, [string]$message) {
    $startIndex = $source.IndexOf($start, [StringComparison]::Ordinal)
    $endIndex = if ($startIndex -ge 0) {
        $source.IndexOf($end, $startIndex + $start.Length, [StringComparison]::Ordinal)
    } else { -1 }
    if ($startIndex -lt 0 -or $endIndex -lt 0) {
        $script:failures.Add($message)
        return ''
    }
    return $source.Substring($startIndex, $endIndex - $startIndex)
}

$focusTerminalSource = Get-SourceSlice $terminalSource 'public void FocusTerminal(bool forceImeReattach = false,' 'public void CancelPendingFocusTransfer()' 'Could not isolate FocusTerminal.'
$cancelFocusSource = Get-SourceSlice $terminalSource 'public void CancelPendingFocusTransfer()' 'private void StopFocusBounceTimer()' 'Could not isolate focus cancellation.'
$acceptNativeSource = Get-SourceSlice $terminalSource 'private void AcceptNativeTerminalFocus()' 'private void ApplyTerminalFocus(' 'Could not isolate native focus acceptance.'
$interactSource = Get-SourceSlice $terminalSource 'case "interact":' 'case "inputIntent":' 'Could not isolate native terminal interaction.'
$applyFocusSource = Get-SourceSlice $terminalSource 'private void ApplyTerminalFocus(' 'private async Task InitWebViewAsync()' 'Could not isolate terminal focus application.'
$focusBridgeSource = Get-SourceSlice $terminalHtml "case 'focus': {" "case 'imeAbort':" 'Could not isolate JavaScript terminal focus bridge.'
$imeCommitSource = Get-SourceSlice $terminalHtml 'let keydownCommitSent = null;' 'const composerCols = syncDevezVibeComposerLayout();' 'Could not isolate deterministic IME commit routing.'
$pageReadySource = Get-SourceSlice $terminalSource 'private void OnPageReady()' 'private void ApplyTerminalFontFamily()' 'Could not isolate page-ready replay.'
$showFileDropSource = Get-SourceSlice $paneSource 'public void ShowFileDropOverlay()' 'private void HideFileDropOverlay(' 'Could not isolate file-drop overlay.'
$suspendOnlySource = Get-SourceSlice $paneSource 'public async Task SuspendTerminalOnlyAsync(' 'public async Task PrepareShutdownSnapshotAsync()' 'Could not isolate terminal-only suspension.'
$resumeOnlySource = Get-SourceSlice $paneSource 'public void ResumeTerminalOnly(' 'public void DisposeTerminal()' 'Could not isolate terminal-only resume.'
$tabClickSource = Get-SourceSlice $paneSource 'private void Tab_Click(' 'private void Tab_RightClick(' 'Could not isolate tab click routing.'
$coordinatorSource = Get-SourceSlice $windowSource 'private bool IsWorkspaceNavigationBlocked =>' 'private async void RunPanelToggleCovered(' 'Could not isolate transition navigation coordinator.'
$runPanelToggleSource = Get-SourceSlice $windowSource 'private async void RunPanelToggleCovered(Action change, bool imeBoundaryOnRestore = false)' 'private async void ShellTerminalBtn_Click(' 'Could not isolate panel transition.'
$toggleShellSource = Get-SourceSlice $windowSource 'private async Task ToggleShellPanelAsync(bool open)' 'private void UpdateShellToggleVisual()' 'Could not isolate shell transition.'
$onPaneFocusSource = Get-SourceSlice $windowSource 'private void OnPaneFocusRequested(WorkspacePaneView pane)' 'private bool _inOwnershipRouting;' 'Could not isolate pane focus routing.'
$splitOpenSource = Get-SourceSlice $windowSource 'private async Task AnimateSplitOpenAsync()' 'private void RestoreSplitState()' 'Could not isolate split-open transition.'
$splitCloseSource = Get-SourceSlice $windowSource 'private async Task AnimateSplitCloseAsync(bool swapped, bool animate = true)' 'private void UpdatePaneFocusVisual(' 'Could not isolate split-close transition.'
$fullOverlaySuspendSource = Get-SourceSlice $windowSource 'private async Task<bool> SuspendTerminalWithSnapshotAsync(bool blankCurtain = false)' 'private void ResumeTerminal()' 'Could not isolate full-overlay suspension.'
$modalBoundarySource = Get-SourceSlice $windowSource 'internal void PrepareForModalInputBoundary()' 'private long CancelAllPendingTerminalFocusTransfers()' 'Could not isolate modal input boundary.'
$focusSchedulerSource = Get-SourceSlice $windowSource 'internal void ScheduleTerminalFocusRestore(ComboBox? releasedCombo, bool imeBoundary = false)' 'private bool FocusShellTerminal(' 'Could not isolate focus restoration scheduler.'
$rightTerminalSuspendSource = Get-SourceSlice $windowSource 'private async Task SuspendTerminalOnlyAsync()' 'private void ResumeTerminalOnly()' 'Could not isolate right-overlay terminal suspension.'
$fullScreenSource = Get-SourceSlice $windowSource 'private async void RunFullScreenTransitionCovered(Action change, bool solidCover = false)' 'private void UpdateFullScreenTopmost()' 'Could not isolate full-screen transition.'
$openRightOverlaySource = Get-SourceSlice $windowSource 'private async Task OpenRightOverlay()' 'private async Task CloseRightOverlayAsync(' 'Could not isolate right-overlay open.'
$closeRightOverlaySource = Get-SourceSlice $windowSource 'private async Task CloseRightOverlayAsync(bool restoreFocus = true)' 'private async Task ResumeRightOverlayTerminalsAsync(' 'Could not isolate right-overlay close.'
$resumeRightOverlaySource = Get-SourceSlice $windowSource 'private async Task ResumeRightOverlayTerminalsAsync(long focusRequest, bool restoreFocus)' 'private void RightScrim_Click(' 'Could not isolate right-overlay resume.'
$shutdownSource = Get-SourceSlice $windowSource 'private async void OnWindowClosing(' 'private void CheckHookSetup()' 'Could not isolate shutdown.'
$titleDropSource = Get-SourceSlice $windowSource 'private void TitleBarFileOpen_PreviewDrop(' 'private void Splitter_PreviewDragOver(' 'Could not isolate title-bar file drop.'
$taskSendSource = Get-SourceSlice $windowSource 'public bool SendTextToActiveSession(string text)' 'public bool TryRestartActiveClaudeSession()' 'Could not isolate task-queue send.'
$crossDropSource = Get-SourceSlice $windowSource 'private bool OnTryCommitCrossTabDrop(' 'private WorkspacePaneView? PaneAtScreen(' 'Could not isolate cross-pane drop.'
$hintDetectSource = Get-SourceSlice $terminalHtml 'function detectComposerHintCols(caret)' 'function updateComposerHintMask(preedit)' 'Could not isolate the composer hint fallback.'
$hintMaskSource = Get-SourceSlice $terminalHtml 'function updateComposerHintMask(preedit)' 'el._setDevezComposerHintCols = function' 'Could not isolate the IME composer hint mask.'

# TerminalHostView: sticky IME boundary, one timer per host, and one ordering epoch across hosts.
Assert-Order $focusTerminalSource 'long globalRequest = InvalidateGlobalFocusRequests();' 'if (!_pageReady || _activeRoomId == null)' 'Cold focus must invalidate older host timers before returning.'
Assert-Match $focusTerminalSource '_imeReattachPending \|= forceImeReattach \|\| hostChanged;' 'IME boundaries and host changes must remain sticky.'
Assert-Match $focusTerminalSource 'bool nativeFocusIntent = false' 'Terminal focus must distinguish an authoritative native-surface click.'
Assert-Match $focusTerminalSource '_pendingFocusGlobalRequest = globalRequest;[\s\S]*?_pendingFocusWpfElement = System\.Windows\.Input\.Keyboard\.FocusedElement;' 'Cold focus must retain its epoch and authorizing WPF surface.'
Assert-Match $pageReadySource 'replacedByWpfInput = !ReferenceEquals\(wpfElement, currentWpfElement\)[\s\S]*?currentWpfElement != null[\s\S]*?!ReferenceEquals\(currentWpfElement, owner\)[\s\S]*?!IsKeyboardFocusWithin' 'Page-ready replay must reject a changed external WPF surface while allowing its owner and own host.'
Assert-Match $pageReadySource 'globalRequest == System\.Threading\.Volatile\.Read\(ref s_globalFocusRequestGeneration\)[\s\S]*?FocusTerminal' 'Page-ready replay must require the original global epoch.'
Assert-Order $focusTerminalSource 'StopFocusBounceTimer();' 'int request = ++_focusRequestGeneration;' 'A new request must stop the old timer before publishing its generation.'
Assert-Match $focusTerminalSource 'owner is \{ IsActive: false \}[\s\S]*?return;[\s\S]*?if \(!IsVisible \|\| _webView is not \{ IsVisible: true \}\) return;' 'Inactive or hidden hosts must not receive focus.'
Assert-Match $focusTerminalSource 'if \(bounce\)[\s\S]*?FocusManager\.SetFocusedElement\(scope, null\);[\s\S]*?FocusManager\.SetFocusedElement\(owner, null\);[\s\S]*?Keyboard\.ClearFocus\(\);' 'A forced boundary must clear logical and keyboard focus.'
Assert-Match $focusTerminalSource 'FocusManager\.SetFocusedElement\(owner, owner\);[\s\S]*?Keyboard\.Focus\(owner\)' 'The first bounce step must pass through the WPF input provider.'
Assert-NotMatch $terminalSource 'private static extern IntPtr SetFocus\(' 'Raw SetFocus must not restore a remembered child HWND.'
Assert-Match $focusTerminalSource 'else if \(System\.Windows\.Input\.Keyboard\.FocusedElement is[\s\S]*?TextBoxBase or PasswordBox or ComboBox or MenuItem\)[\s\S]*?Keyboard\.ClearFocus\(\);' 'Ordinary focus must clear only stale WPF input, not an already-focused WebView.'
Assert-Match $focusTerminalSource 'timer\.Tick \+= [\s\S]*?request != _focusRequestGeneration[\s\S]*?globalRequest != System\.Threading\.Volatile\.Read[\s\S]*?!IsVisible \|\| _webView is not \{ IsVisible: true \}[\s\S]*?focusedElement != null && !ReferenceEquals\(focusedElement, owner\)[\s\S]*?!IsKeyboardFocusWithin[\s\S]*?_imeReattachPending = false;[\s\S]*?ApplyTerminalFocus\(room, request, globalRequest\);' 'The delayed transfer must validate owner, host, epochs, visibility, and newer WPF focus before consuming the boundary.'
Assert-Match $focusTerminalSource '!nativeFocusIntent && focusedElement != null && !ReferenceEquals\(focusedElement, owner\)' 'An authoritative native click must not be rejected by stale WPF focus state.'
Assert-Match $cancelFocusSource '\+\+_focusRequestGeneration;[\s\S]*?StopFocusBounceTimer\(\);' 'Cancellation must invalidate and stop the owned timer.'
Assert-Match $acceptNativeSource 'bool reattachBoundary = _imeReattachPending \|\| _focusBounceTimer != null;[\s\S]*?CancelPendingFocusTransfer\(\);[\s\S]*?if \(reattachBoundary\)[\s\S]*?FocusTerminal\(forceImeReattach: true, nativeFocusIntent: true\);' 'A direct click during a live IME boundary must re-arm an authoritative bounce instead of focusing in the same frame.'
Assert-Order $interactSource 'UserInteracted?.Invoke();' 'AcceptNativeTerminalFocus();' 'Native click routing must invalidate outer focus epochs before publishing its authoritative recovery request.'
Assert-NotMatch $acceptNativeSource '_imeReattachPending = false;' 'A direct click must not consume a pending IME reattachment boundary.'
Assert-Match $applyFocusSource 'request != _focusRequestGeneration[\s\S]*?_activeRoomId != room[\s\S]*?globalRequest != System\.Threading\.Volatile\.Read' 'Delayed settle diagnostics must reject stale requests.'
Assert-Match $terminalSource 'case "inputIntent":\s*InputIntent\?\.Invoke\(\);' 'Native terminal keyboard input must cancel deferred surface changes.'
Assert-Match $terminalSource 'case "fileDrop":[\s\S]{0,700}?ExternalFileDropReceived\(paths\)' 'Native terminal file drops must be routed to the pane instead of focusing during capture.'

Assert-Match $modalBoundarySource 'pane\.Terminal\.MarkImeReattachBoundary\(\);[\s\S]*?ShellTerminal\.MarkImeReattachBoundary\(\);' 'Leaving for another window or modal must mark every terminal host, not only the window-level flag.'

# JavaScript bridge: native pointer, keyboard, composition, and modal boundaries.
Assert-Match $terminalHtml "document\.addEventListener\('mousedown',[\s\S]{0,250}?post\(\{ type: 'interact' \}\);" 'Native terminal pointer input must notify the host.'
Assert-Match $terminalHtml "document\.addEventListener\('keydown', function \(\) \{ post\(\{ type: 'inputIntent' \}\); \}, true\);" 'Terminal key input must supersede a pending navigation.'
Assert-Match $terminalHtml "document\.addEventListener\('compositionstart', function \(\) \{ post\(\{ type: 'inputIntent' \}\); \}, true\);" 'IME composition start must supersede a pending navigation.'
Assert-Match $terminalHtml 'ime-composer-hint-mask[\s\S]*?background: var\(--term-bg' 'The IME hint mask must cover stale composer guidance with the terminal background.'
Assert-Match $terminalHtml '\.xterm \.composition-view[\s\S]*?z-index: 2' 'The active IME preedit must stay above the full composer hint mask.'
Assert-Match $terminalHtml 'devezComposerHintCols: 0' 'Each terminal must start without stale Devez Vibe hint metadata.'
Assert-Match $terminalHtml 'devez-composer-hint-v1;\(\\d\+\)\$[\s\S]*?_setDevezComposerHintCols\(columns\)' 'Devez Vibe hint metadata must reach the active terminal mask.'
Assert-Match $hintDetectSource 'Tab: Change dvz agent' 'The host must recover the idle composer hint when its metadata signal was missed.'
Assert-Match $hintDetectSource 'Enter: steer' 'The host must recover the busy composer hint when its metadata signal was missed.'
Assert-Match $hintDetectSource 'Alt\+Enter: queue' 'The recovered busy hint must cover its queue shortcut too.'
Assert-Match $terminalHtml 'function updateComposerHintMask\(preedit\)[\s\S]*?signaledHintCols \|\| detectComposerHintCols[\s\S]*?style\.left = Math\.round\(frozenImeCaret\.col \* cw\)[\s\S]*?style\.width = Math\.ceil\(hintCols \* cw\)' 'The mask must cover the full hint from the composer caret.'
Assert-NotMatch $hintMaskSource 'setTimeout\(clearComposerHintMask, 800\)' 'The hint mask must not expire while IME composition is still active.'
Assert-Match $terminalHtml "ta\.addEventListener\('compositionend',[\s\S]*?composerHintMaskTimer = setTimeout\(clearComposerHintMask, 800\)" 'Only a completed IME composition may arm the hint-mask fallback timeout.'
Assert-Match $terminalHtml 'function apply\(\)[\s\S]*?updateComposerHintMask\(' 'IME composition updates must refresh the empty-composer hint mask.'
Assert-Match $terminalHtml 'function clearImeAfterBlur\(reason\)[\s\S]*?clearComposerHintMask\(\)' 'Leaving the IME boundary must clear the hint mask.'
Assert-Match $terminalHtml "case 'imeAbort':[\s\S]*?clearPendingComposeState\(\);[\s\S]*?_abortIme\(\);" 'Modal boundaries must clear pending composition state.'
Assert-Match $terminalHtml "el\._abortIme = function \(\)[\s\S]*?ta\.value = '';[\s\S]*?CompositionEvent\('compositionend', \{ data: '' \}\)[\s\S]*?ta\.blur\(\);[\s\S]*?clearImeAfterBlur\('modal'\);" 'Modal abort must end xterm composition before blur without submitting unfinished text.'
Assert-Match $terminalHtml 'function containsHangulText\(s\)[\s\S]*?\\u1100-\\u11ff[\s\S]*?\\uac00-\\ud7af' 'Remote IME recovery must be scoped to Hangul text.'
Assert-Match $imeCommitSource "imeCore\._inputEvent = function \(e\)[\s\S]*?e\.inputType === 'insertText'[\s\S]*?containsHangulText\(data\)[\s\S]*?!imeHelper\._isComposing[\s\S]*?!imeHelper\._isSendingComposition[\s\S]*?triggerDataEvent\(data, true\)" 'Non-composition Hangul insertText must bypass xterm keydown rollover loss.'
Assert-Match $imeCommitSource 'rememberCompositionInput\(data\);[\s\S]*?keydownCommitSent[\s\S]*?queueImeCommit\(data\);' 'Composition commits must suppress their matching insertText before queueing the canonical data.'
Assert-Match $imeCommitSource 'imeHelper\._handleAnyTextareaChanges = function \(\) \{\};' 'The deferred keyCode 229 textarea diff must stay disabled after direct insertText recovery.'
Assert-Match $focusBridgeSource 'let retries = 40;' 'Window activation focus recovery must outlast delayed WebView2 document activation.'
Assert-Order $focusBridgeSource 'if (!document.hasFocus())' 'const active = document.activeElement;' 'Focus restoration must not blur the helper textarea before the WebView document is active.'
Assert-Order $focusBridgeSource 'if (!composing) active.blur();' 't.term.focus();' 'Focus restoration must preserve composition and then restore the active terminal textarea.'

# Workspace pane: all active native surfaces report focus and suspension owns its exact surface.
Assert-Match $paneSource 'forceImeReattach \|= _termParked;' 'Returning from another embedded surface must force IME reattachment.'
Assert-Match $paneSource 'OpenSession\(session, forceImeReattach: true\);' 'A new session must force its first IME attachment.'
Assert-Match $paneSource 'else OpenSession\(s, forceImeReattach: true\);' 'A WPF terminal-tab switch must force IME reattachment.'
Assert-Match $showFileDropSource 'IsTerminalVisualTransitionBusy == true\) return;[\s\S]*?_terminal\.CancelPendingFocusTransfer\(\);[\s\S]*?TerminalHostView\.InvalidateGlobalFocusRequests\(\);' 'File-drop overlay must neither mutate a suspended surface nor leave delayed focus alive.'
Assert-Match $suspendOnlySource '_terminalOnlySuspended = true;[\s\S]*?if \(_activeTab is FileTabItem file\)[\s\S]*?if \(webCover\) return;[\s\S]*?CaptureSnapshotAsync\(\)[\s\S]*?FileEditorHostContainer\.Visibility = Visibility\.Collapsed;' 'Right-drawer suspension must guard and hide active file WebViews.'
Assert-Match $resumeOnlySource '_terminalOnlySuspended = false;[\s\S]*?if \(_activeTab is FileTabItem\)[\s\S]*?FileEditorHostContainer\.Visibility = Visibility\.Visible;' 'Right-drawer resume must clear the guard and restore file WebViews.'
Assert-Match $paneSource 'UpdateEmptyState\(\)[\s\S]*?hiddenByTerminalOnly = _terminalOnlySuspended && !_terminalOnlyWebCover;[\s\S]*?!_overlaySuspended && !hiddenByTerminalOnly[\s\S]*?TerminalHostContainer\.Visibility = Visibility\.Visible;[\s\S]*?!_overlaySuspended && !_terminalOnlySuspended\) UnparkBrowserHost\(\);' 'State notifications must not resurrect a native surface hidden behind the right drawer.'
Assert-Match $paneSource 'ExternalFileDropReceived \+= paths =>[\s\S]*?DeferWorkspaceNavigationIfBusy[\s\S]*?InsertDroppedPathsIntoTerminal' 'Native terminal drops must defer until the visual transition is stable.'
Assert-Match $paneSource 'InsertDroppedPathsIntoTerminal[\s\S]*?ReferenceEquals\(_activeSession, session\)[\s\S]*?_terminal\.InsertFilePaths\(paths, forceImeReattach: true\);' 'Deferred terminal drops must revalidate the session and force IME attachment.'
Assert-Match $paneSource '_terminal\.InputIntent \+= \(\) => InputIntent\?\.Invoke\(this\);' 'Terminal key intent must reach the main coordinator.'
Assert-Match $tabClickSource 'DeferWorkspaceNavigationIfBusy[\s\S]*?FocusRequested\?\.Invoke\(this\);[\s\S]*?ActivateClickedTab\(tab\);' 'A tab click must defer focus routing and activation as one command.'
Assert-Match $paneSource 'ActivateClickedTab\(TabItemBase tab\)[\s\S]*?!IsVisible[\s\S]*?!ReferenceEquals\(_activeProject, parent\)[\s\S]*?!ShowsTab\(tab\)' 'A deferred tab click must reject a hidden or moved pane.'
Assert-Match $paneSource 'OnExternalSessionEnded\(SessionItem session\)[\s\S]*?DeferWorkspaceContinuationIfBusy' 'External-session completion must not change the active surface during capture.'
Assert-Match $paneSource 'OnHideStopFinished\(SessionItem session\)[\s\S]*?DeferWorkspaceContinuationIfBusy' 'Hide-stop completion must not reactivate during capture.'
Assert-Match $paneSource 'CompleteSessionReload\(IReadOnlyList<SessionItem> allSessions[\s\S]*?DeferWorkspaceContinuationIfBusy' 'Reload completion must not reactivate during capture.'
Assert-Match $paneXaml 'PreviewMouseDown="Pane_PreviewInteract"' 'Left and right pane input must route during tunneling.'
Assert-NotMatch $paneXaml '(?<!Preview)MouseLeftButtonDown="Pane_PreviewInteract"' 'Pane routing must not run again in bubbling.'
Assert-Match $browserSource '_view\.GotKeyboardFocus \+= \(_, _\) => NativeSurfaceFocused\?\.Invoke\(\);' 'Browser WebView focus must report its native input surface.'
Assert-Match $fileExplorerSource 'Browser\.NativeSurfaceFocused \+= \(\) => NativeSurfaceFocused\?\.Invoke\(\);' 'Docked browser native focus must reach MainWindow.'
Assert-Match $pdfSource '_webView\.GotKeyboardFocus \+= \(_, _\) => NativeSurfaceFocused\?\.Invoke\(this, EventArgs\.Empty\);' 'PDF WebView native focus must reach pane routing.'
Assert-NotMatch $pdfSource 'core\.Navigate\([\s\S]{0,180}?_webView\.Focus\(\)' 'PDF initialization must not steal focus after a later user action.'
Assert-Match $monacoSource '_webView\.GotKeyboardFocus \+= \(_, _\) => UserInteracted\?\.Invoke\(\);' 'Monaco WebView focus must reach its host.'
Assert-Match $monacoDiffSource '_host\.UserInteracted \+= \(\) => NativeSurfaceFocused\?\.Invoke\(this, EventArgs\.Empty\);' 'Monaco native focus must reach pane routing.'

# Main window: restoration is sticky, shutdown-safe, and respects the latest real input surface.
Assert-Match $windowSource 'OnDeactivated\(EventArgs e\)[\s\S]*?PrepareForModalInputBoundary\(\);[\s\S]*?base\.OnDeactivated' 'Window deactivation must abort composition before returning.'
Assert-Match $windowSource 'PrepareForModalInputBoundary\(\)[\s\S]*?pane\.Terminal\.AbortIme\(\);[\s\S]*?ShellTerminal\.AbortIme\(\);[\s\S]*?_terminalImeReattachPending = true;[\s\S]*?CancelAllPendingTerminalFocusTransfers\(\);' 'Every xterm host must abort stale composition at modal boundaries.'
Assert-Match $windowSource 'ContextMenu\.OpenedEvent[\s\S]{0,2000}?PrepareForModalInputBoundary\(\);' 'Opening a context menu must abort stale composition.'
Assert-Match $windowSource 'ContextMenu\.ClosedEvent[\s\S]*?imeBoundary: true' 'Closing a context menu must restore through an IME boundary.'
Assert-Match $focusSchedulerSource 'if \(_shuttingDown\) return;[\s\S]*?_terminalImeReattachPending \|= imeBoundary;[\s\S]*?if \(_shuttingDown \|\| generation != _terminalFocusRestoreGeneration \|\| !IsActive' 'Focus restore must be sticky and reject shutdown, stale, and inactive callbacks.'
Assert-Match $focusSchedulerSource '_terminalVisualTransitionGate\.CurrentCount == 0[\s\S]*?RetryTerminalFocusRestoreAfterTransitionAsync' 'A restore blocked by capture must retry after the common transition gate.'
Assert-Match $focusSchedulerSource 'focusedElement != null && !ReferenceEquals\(focusedElement, this\)[\s\S]*?!ReferenceEquals\(focusedElement, releasedCombo\) && !terminalFocused' 'Restoration must preserve every newer non-terminal WPF focus surface.'
Assert-Match $focusSchedulerSource 'ShellTerminalPanel\.IsVisible && _shellWasLastInputSurface[\s\S]*?FocusShellTerminal\(forceImeReattach\)[\s\S]*?FocusActiveSessionTerminal\(forceImeReattach\)[\s\S]*?if \(restored\)[\s\S]*?_terminalImeReattachPending = false;' 'Restoration must route to the last terminal surface and consume the boundary only on success.'
Assert-Match $windowSource '_fileExplorerWasLastInputSurface && FileExplorer\.IsVisible' 'Visible FileExplorer input must suppress terminal restoration.'
Assert-Match $windowSource 'ShellTerminal\.InputIntent \+= \(\) =>[\s\S]*?RegisterUserInputIntent\(\);[\s\S]*?SupersedePendingTerminalFocus\(\);' 'Bottom-shell typing must cancel stale navigation and focus.'
Assert-Match $windowSource 'pane\.InputIntent \+= _ =>[\s\S]*?RegisterUserInputIntent\(\);[\s\S]*?SupersedePendingTerminalFocus\(\);' 'Workspace-terminal typing must cancel stale navigation and focus.'
Assert-Match $onPaneFocusSource 'MarkWorkspaceInputSurface\(\);[\s\S]*?DeferWorkspaceNavigationIfBusy[\s\S]*?if \(!pane\.IsVisible\) return;[\s\S]*?_focusedPane = pane;' 'Pane focus mutation must defer and revalidate visibility.'
Assert-Match $onPaneFocusSource 'OnPaneNativeSurfaceFocused\(WorkspacePaneView pane\)[\s\S]*?SupersedePendingTerminalFocus\(\);[\s\S]*?if \(IsWorkspaceNavigationBlocked \|\| !pane\.IsVisible' 'Programmatic native focus must cancel stale terminal focus without replacing pending navigation.'
Assert-NotMatch $onPaneFocusSource 'OnPaneNativeSurfaceFocused\(WorkspacePaneView pane\)[\s\S]*?DeferWorkspaceNavigationIfBusy' 'Programmatic native focus must not overwrite a pending user navigation.'

# One coordinator owns transient gates and persistent overlay suspension.
Assert-Match $coordinatorSource 'IsWorkspaceNavigationBlocked => _terminalVisualTransitionGate\.CurrentCount == 0[\s\S]*?_overlaySuspended \|\| _rightOverlayOpen \|\| _rightOverlayTerminalSuspended;' 'Navigation blocking must include persistent full and right overlays.'
Assert-Match $coordinatorSource 'DeferWorkspaceNavigationIfBusy\(Action navigation\)[\s\S]*?RegisterUserInputIntent\(\);[\s\S]*?if \(!IsWorkspaceNavigationBlocked[\s\S]*?_pendingWorkspaceNavigation = navigation;[\s\S]*?_pendingWorkspaceNavigationInputGeneration = inputGeneration;[\s\S]*?CloseRightOverlayForPendingWork\(\);' 'User navigation must be last-wins and close a persistent right drawer before replay.'
Assert-Match $coordinatorSource 'DeferWorkspaceContinuationIfBusy\(Action continuation\)[\s\S]*?_pendingWorkspaceContinuations\.Add\(continuation\);' 'Background completions must use a queue separate from user navigation.'
Assert-Match $coordinatorSource 'Dispatcher\.BeginInvoke\(new Action\(\(\) =>[\s\S]*?_pendingWorkspaceNavigationInputGeneration == _workspaceInputGeneration[\s\S]*?CloseRightOverlayAsync\(restoreFocus: false\)' 'A queued right-drawer close must recheck that its navigation is still the latest input.'
Assert-Match $coordinatorSource 'SchedulePendingWorkspaceNavigationDrain\(\)[\s\S]*?if \(IsWorkspaceNavigationBlocked\) return;[\s\S]*?_pendingWorkspaceContinuations\.ToArray\(\)[\s\S]*?foreach \(var continuation in continuations\)[\s\S]*?inputGeneration != _workspaceInputGeneration[\s\S]*?_runningPendingWorkspaceNavigation = true;[\s\S]*?SupersedePendingTerminalFocus\(\);[\s\S]*?navigation\(\);' 'Drain must wait for stability, run validated internal completion first, reject stale input, then supersede focus before user replay.'
Assert-Count $windowSource '_terminalVisualTransitionGate\.Release\(\)' 1 'Only ReleaseTerminalVisualTransition may release the common gate.'
Assert-Match $coordinatorSource 'ReleaseTerminalVisualTransition\(\)[\s\S]*?_terminalVisualTransitionGate\.Release\(\);[\s\S]*?SchedulePendingWorkspaceNavigationDrain\(\);' 'Every gate release must schedule pending work.'
Assert-Match $titleDropSource 'var pane = _focusedPane;[\s\S]*?var project = pane\.ActiveProject;[\s\S]*?DeferWorkspaceNavigationIfBusy[\s\S]*?OpenTitleBarDroppedFiles\(pane, project, files\)' 'Title-bar file drop must defer one stable pane/project command.'
Assert-Match $taskSendSource 'if \(IsTerminalVisualTransitionBusy\) return false;[\s\S]*?return SendTextToPane' 'Task send must preserve queue data instead of entering a replaceable navigation slot.'
Assert-Match $crossDropSource 'PaneAtScreen\(screen\), other[\s\S]*?if \(IsWorkspaceNavigationBlocked\) return true;[\s\S]*?OnPaneSplitViewRequested' 'A cross-pane drop during capture must be consumed without committing a local reorder.'

# Every visual transition keeps one captured cohort and uses the release helper.
Assert-Match $runPanelToggleSource 'await _terminalVisualTransitionGate\.WaitAsync\(\);[\s\S]*?frozenPanes = VisibleWorkspaceTerminalPanes\(\);[\s\S]*?FreezeWorkspaceTerminalsAsync\(frozenPanes\);[\s\S]*?UnfreezeWorkspaceTerminals\(frozenPanes\);[\s\S]*?ReleaseTerminalVisualTransition\(\);' 'Panel transition must resume its captured pane cohort.'
Assert-Match $toggleShellSource 'List<WorkspacePaneView>\? frozenPanes = null;[\s\S]*?await _terminalVisualTransitionGate\.WaitAsync\(\);[\s\S]*?if \(frozenPanes != null\)[\s\S]*?UnfreezeWorkspaceTerminals\(frozenPanes\);[\s\S]*?ReleaseTerminalVisualTransition\(\);' 'Shell transition must resume only a cohort it acquired.'
Assert-Match $toggleShellSource 'IsGlobalFocusRequestCurrent\(shellFocusRequest\)[\s\S]*?MarkShellInputSurface\(\)[\s\S]*?_shellPanelOpen && _shellWasLastInputSurface && IsActive[\s\S]*?ShellTerminal\.FocusTerminal' 'A delayed shell open must not overwrite newer input.'
Assert-Match $splitOpenSource 'await _terminalVisualTransitionGate\.WaitAsync\(\);[\s\S]*?finally[\s\S]*?PaneA\.ResumeTerminalOnly[\s\S]*?PaneB\.ResumeTerminalOnly[\s\S]*?ReleaseTerminalVisualTransition\(\);' 'Split open must own the gate through resume.'
Assert-Match $splitCloseSource 'if \(!animate\) \{ FinishLayout\(\); return; \}[\s\S]*?await _terminalVisualTransitionGate\.WaitAsync\(\);[\s\S]*?finally[\s\S]*?ReleaseTerminalVisualTransition\(\);' 'Split close must not release another transition and must own animated capture.'
Assert-Match $fullScreenSource 'await _terminalVisualTransitionGate\.WaitAsync\(\);[\s\S]*?FreezeWorkspaceTerminalsAsync\(covered, stretchCover: true\)[\s\S]*?UnfreezeWorkspaceTerminals\(covered\)[\s\S]*?ReleaseTerminalVisualTransition\(\);' 'Full-screen transition must use the common gate and captured cohort.'
Assert-Order $fullOverlaySuspendSource 'CancelAllPendingTerminalFocusTransfers();' '_overlaySuspended = true;' 'Full overlay must cancel focus before publishing suspension.'
Assert-Match $fullOverlaySuspendSource 'await _terminalVisualTransitionGate\.WaitAsync\(\);[\s\S]*?FileExplorer\.SuspendBrowserAsync\(\);[\s\S]*?SuspendShellPanelAsync\(\);[\s\S]*?ReleaseTerminalVisualTransition\(\);' 'Full overlay must own the common gate across every native surface.'
Assert-Match $rightTerminalSuspendSource 'foreach \(var pane in _panes\) await pane\.SuspendTerminalOnlyAsync\(\);[\s\S]*?_rightOverlayShellSuspended = true;[\s\S]*?await SuspendShellPanelAsync\(\);' 'Right drawer must suspend every pane and the visible bottom shell.'
Assert-Order $openRightOverlaySource '_rightOverlayOpen = true;' 'await _terminalVisualTransitionGate.WaitAsync();' 'Right-drawer desired state must be visible before its first await.'
Assert-Match $openRightOverlaySource 'await SuspendTerminalOnlyAsync\(\);[\s\S]*?_rightOverlayTerminalSuspended = true;[\s\S]*?if \(!_rightOverlayOpen \|\| _narrow != true \|\| _overlaySuspended \|\| _shuttingDown\) return;' 'Right-drawer open must retain suspension ownership across stale continuations.'
Assert-Match $openRightOverlaySource 'catch \(Exception ex\)[\s\S]*?ResumeTerminalOnly\(\);[\s\S]*?_rightOverlayTerminalSuspended = false;[\s\S]*?finally[\s\S]*?ReleaseTerminalVisualTransition\(\);' 'Failed right-drawer open must resume owned native surfaces and release the gate.'
Assert-Match $closeRightOverlaySource '_rightOverlayOpen = false;[\s\S]*?ResumeRightOverlayTerminalsAsync\(focusRequest, restoreFocus\);' 'Right-drawer close must publish close before serialized resume.'
Assert-Match $resumeRightOverlaySource 'if \(_rightOverlayOpen\) return;[\s\S]*?ResumeTerminalOnly\(\);[\s\S]*?_rightOverlayTerminalSuspended = false;[\s\S]*?ReleaseTerminalVisualTransition\(\);[\s\S]*?imeBoundary: true' 'Right-drawer resume must reject reopen and restore the IME boundary.'
Assert-Match $shutdownSource '_shuttingDown = true;[\s\S]*?CancelAllPendingTerminalFocusTransfers\(\);[\s\S]*?CloseRightOverlayAsync\(restoreFocus: false\);[\s\S]*?_terminalVisualTransitionGate\.WaitAsync\(\);[\s\S]*?ReleaseTerminalVisualTransition\(\);' 'Shutdown must cancel focus, close the right drawer, and own the transition gate.'

# Passive popup and asynchronous modal behavior.
Assert-Match $notificationSource 'ShowActivated="False"' 'Passive notifications must never activate their HWND.'
Assert-Match $appSource 'ShowAgentUpdateResults[\s\S]*?PrepareForModalInputBoundary\(\);[\s\S]*?win\.ShowDialog\(\);' 'Asynchronous update-result modal must abort active composition before activation.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "FAIL: $_" }
    throw "Terminal IME focus regression checks failed: $($failures.Count)"
}

Write-Output 'PASS: terminal IME focus, native input, transition ownership, and popup boundaries are guarded.'
