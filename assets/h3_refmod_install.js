// The RefMod itself is selected through SwarmUI's embedding picker. This only
// adds the normal feature-install button when ComfyUI-MiniMaxH3Mod is missing.
function hideRefModInstallerPlaceholderWhenAvailable() {
    if (currentBackendFeatureSet.includes('minimax_h3_refmods')) {
        // Keep the group in the generated parameter tree. Removing an input
        // group while SwarmUI is applying backend feature changes can leave
        // other UI handlers with stale DOM references. The placeholder merely
        // gives the installer a stable target, so hide that control instead.
        let placeholder = document.getElementById('input_h3_refmod_installer_placeholder');
        let placeholderRow = placeholder?.closest('.auto-input');
        if (placeholderRow) {
            placeholderRow.style.display = 'none';
        }
    }
}

postParamBuildSteps.push(hideRefModInstallerPlaceholderWhenAvailable);
hideParamCallbacks.push(hideRefModInstallerPlaceholderWhenAvailable);
addInstallButton('refmod', 'minimax_h3_refmods', 'minimax_h3_refmods', 'Install MiniMax H3 RefMod Nodes');
