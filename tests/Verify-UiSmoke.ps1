param([string]$Path = (Join-Path $PSScriptRoot '../artifacts/published-ui-smoke.json'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$state = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
function Assert-Ui([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
Assert-Ui $state.initialized 'The WinUI window must initialize.'
Assert-Ui ($state.bindingControls -eq 16 -and $state.regionButtons -eq 8 -and $state.edgeRangeControls -eq 4) 'All gesture and region controls must exist.'
Assert-Ui $state.saveButtonInFixedFooter 'Save must remain outside the scrolling page.'
Assert-Ui ($state.sourceErrors.Count -eq 0) 'The input host must start without errors.'
foreach ($property in $state.editor.PSObject.Properties) {
    Assert-Ui ($property.Value -eq $true) ("Save lifecycle: " + $property.Name)
}
foreach ($property in $state.spatialEditor.PSObject.Properties | Where-Object { $_.Value -is [bool] }) {
    Assert-Ui $property.Value ("Region editing: " + $property.Name)
}
$center = $state.spatialEditor.centered
Assert-Ui (-not $center.panelVisible) 'Initial view must have no region editor.'
Assert-Ui ([Math]::Abs($center.previewX - ($center.viewportWidth - $center.previewWidth) / 2) -lt 1) 'Preview must be horizontally centered.'
Assert-Ui ([Math]::Abs($center.previewY - ($center.viewportHeight - $center.previewHeight) / 2) -lt 1) 'Preview must be vertically centered.'
$layouts = @($state.spatialEditor.positions) + @($state.spatialEditor.narrowPositions)
Assert-Ui ($layouts.Count -eq 16) 'Normal and narrow layouts must cover all eight regions.'
foreach ($layout in $layouts) {
    $region = $layout.region
    Assert-Ui $layout.panelVisible ("Missing region editor: " + $region)
    $expectedCount = if ($region -in @('Top','Bottom','Left','Right')) { 3 } else { 1 }
    Assert-Ui ($layout.editorControls -eq $expectedCount) ("Wrong gesture count: " + $region)
    Assert-Ui ($layout.panelX -ge 0 -and $layout.panelY -ge 0 -and
        $layout.panelX + $layout.panelWidth -le $layout.viewportWidth + .5 -and
        $layout.panelY + $layout.panelHeight -le $layout.viewportHeight + .5) ("Editor outside viewport: " + $region)
    $separated = switch -Regex ($region) {
        '^Top$' { $layout.panelY + $layout.panelHeight -lt $layout.previewY; break }
        '^Bottom$' { $layout.previewY + $layout.previewHeight -lt $layout.panelY; break }
        'Left$' { $layout.panelX + $layout.panelWidth -lt $layout.previewX; break }
        'Right$' { $layout.previewX + $layout.previewWidth -lt $layout.panelX; break }
    }
    Assert-Ui $separated ("Preview must leave space for its editor: " + $region)
    Assert-Ui ($layout.previewX -lt $layout.viewportWidth -and $layout.previewY -lt $layout.viewportHeight -and
        $layout.previewX + $layout.previewWidth -gt 0 -and $layout.previewY + $layout.previewHeight -gt 0) ("Preview completely outside viewport: " + $region)
}
$custom = $state.spatialEditor.narrowPositions | Where-Object { $_.region -eq 'Left' }
Assert-Ui ($custom.editorScrollableHeight -gt 0) 'A tall custom-shortcut editor must remain scrollable.'
Write-Output 'WinUI checks passed: centered preview, 16 region layouts, editor drafts, range mode and sticky save.'

foreach ($property in $state.settingsInteraction.PSObject.Properties | Where-Object { $_.Value -is [bool] }) {
    Assert-Ui $property.Value ("Settings interaction: " + $property.Name)
}
foreach ($layout in @($state.settingsInteraction.normal, $state.settingsInteraction.narrow)) {
    Assert-Ui $layout.settingsButtonBelowResize 'Sensitivity must have its own button below range adjustment.'
    Assert-Ui ($layout.region -eq 'Settings' -and $layout.panelVisible) 'Sensitivity must open its settings editor.'
    Assert-Ui ($layout.previewX + $layout.previewWidth -lt $layout.panelX) 'Preview must move left to leave room for settings.'
    Assert-Ui ($layout.panelX -ge 0 -and $layout.panelY -ge 0 -and
        $layout.panelX + $layout.panelWidth -le $layout.viewportWidth + .5 -and
        $layout.panelY + $layout.panelHeight -le $layout.viewportHeight + .5) 'Settings must remain inside the viewport.'
}
$cleared = $state.settingsInteraction.afterBlank
Assert-Ui (-not $cleared.panelVisible) 'Blank click must hide the settings editor.'
Assert-Ui ([Math]::Abs($cleared.previewX - ($cleared.viewportWidth - $cleared.previewWidth) / 2) -lt 1) 'Blank click must recenter the preview.'
Write-Output 'Settings checks passed: button placement, blank dismissal, control interaction and unsaved edits.'

foreach ($property in $state.startupOption.PSObject.Properties) {
    Assert-Ui $property.Value ("Startup and UI cleanup: " + $property.Name)
}
Write-Output 'Startup UI checks passed: registration state, settings control and reduced notices.'

foreach ($property in $state.shortcutInput.PSObject.Properties) {
    Assert-Ui $property.Value ("Shortcut input: " + $property.Name)
}
Write-Output 'Shortcut input checks passed: native focus, character messages, key recording and persistence.'
