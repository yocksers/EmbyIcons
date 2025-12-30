define(['loading', 'toast'], function (loading, toast) {
    'use strict';

    async function populateProfileSelector(instance) {
        const select = instance.dom.profileSelector;
        select.innerHTML = instance.pluginConfiguration.Profiles.map(p => `<option value="${p.Id}">${p.Name}</option>`).join('');
        instance.currentProfileId = select.value;
        if (select.embyselect) select.embyselect.refresh();
    }

    function getCurrentProfileSettingsFromForm(instance) {
        const settings = {};
        instance.dom.allProfileInputs.forEach(el => {
            const key = el.getAttribute('data-profile-key');
            if (el.type === 'checkbox') {
                settings[key] = el.checked;
            } else if (key === 'RatingFontSizeMultiplier' || key === 'RatingTextVerticalOffset') {
                settings[key] = parseFloat(el.value) || (key === 'RatingFontSizeMultiplier' ? 0.75 : 0);
            } else if (el.type === 'number' || el.classList.contains('slider') || key.endsWith('Priority')) {
                settings[key] = parseInt(el.value, 10) || 0;
            } else {
                settings[key] = el.value;
            }
        });
        return settings;
    }

    function saveCurrentProfileSettings(instance) {
        const profile = instance.pluginConfiguration.Profiles.find(p => p.Id === instance.currentProfileId);
        if (!profile) return;

        Object.assign(profile.Settings, getCurrentProfileSettingsFromForm(instance));
        
        // Save filename-based icon mappings
        saveFilenameMappings(instance, profile);

        instance.pluginConfiguration.LibraryProfileMappings = instance.pluginConfiguration.LibraryProfileMappings.filter(m => m.ProfileId !== instance.currentProfileId);
        instance.dom.librarySelectionContainer.querySelectorAll('input:checked:not(:disabled)').forEach(checkbox => {
            instance.pluginConfiguration.LibraryProfileMappings.push({
                LibraryId: checkbox.getAttribute('data-library-id'),
                ProfileId: instance.currentProfileId
            });
        });
    }

    async function addProfile(instance) {
        const dlg = instance.showDialog('#addProfileDialogTemplate', { removeOnClose: true, size: 'small' });

        dlg.querySelector('form').addEventListener('submit', async (e) => {
            e.preventDefault();
            loading.show();
            try {
                const newName = dlg.querySelector('#txtNewProfileName').value;
                const newProfile = await ApiClient.ajax({ type: "GET", url: ApiClient.getUrl(instance.apiRoutes.DefaultProfile), dataType: "json" });
                newProfile.Name = newName;

                instance.pluginConfiguration.Profiles.push(newProfile);
                await populateProfileSelector(instance);
                instance.dom.profileSelector.value = newProfile.Id;
                if (instance.dom.profileSelector.embyselect) instance.dom.profileSelector.embyselect.refresh();
                instance.loadProfileSettings(newProfile.Id);
                toast('New profile created.');
            } catch (err) {
                toast({ type: 'error', text: 'Error creating profile.' });
            } finally {
                loading.hide();
                dialogHelper.close(dlg);
            }
            return false;
        });
        dlg.querySelector('.btnCancel').addEventListener('click', () => dialogHelper.close(dlg));
    }

    function renameProfile(instance) {
        const profile = instance.pluginConfiguration.Profiles.find(p => p.Id === instance.currentProfileId);
        if (!profile) return;

        const dlg = instance.showDialog('#renameProfileDialogTemplate', { removeOnClose: true, size: 'small' });
        const input = dlg.querySelector('#txtRenameProfile');
        input.value = profile.Name;

        dlg.querySelector('form').addEventListener('submit', (e) => {
            e.preventDefault();
            profile.Name = input.value;
            instance.populateProfileSelector();
            instance.triggerConfigSave();
            toast('Profile renamed.');
            dialogHelper.close(dlg);
            return false;
        });
        dlg.querySelector('.btnCancel').addEventListener('click', () => dialogHelper.close(dlg));
    }

    function deleteProfile(instance) {
        if (!instance.pluginConfiguration || !Array.isArray(instance.pluginConfiguration.Profiles)) {
            toast({ type: 'error', text: 'Configuration error: profiles missing.' });
            return;
        }

        if (instance.pluginConfiguration.Profiles.length <= 1) {
            toast({ type: 'error', text: 'Cannot delete the last profile.' });
            return;
        }

        const profile = instance.pluginConfiguration.Profiles.find(p => p.Id === instance.currentProfileId);
        if (!profile) {
            toast({ type: 'error', text: 'Profile not found.' });
            return;
        }

        const confirmFn = (typeof dialogHelper !== 'undefined' && dialogHelper && typeof dialogHelper.confirm === 'function')
            ? (msg, title) => dialogHelper.confirm(msg, title)
            : (msg) => Promise.resolve(window.confirm(msg));

        confirmFn(`Are you sure you want to delete the profile "${profile.Name}"?`, 'Delete Profile')
            .then(result => {
                try {
                    if (result) {
                        instance.pluginConfiguration.Profiles = instance.pluginConfiguration.Profiles.filter(p => p.Id !== instance.currentProfileId);
                        if (Array.isArray(instance.pluginConfiguration.LibraryProfileMappings)) {
                            instance.pluginConfiguration.LibraryProfileMappings = instance.pluginConfiguration.LibraryProfileMappings.filter(m => m.ProfileId !== instance.currentProfileId);
                        }
                        instance.populateProfileSelector();
                        const newProfileId = instance.dom.profileSelector ? instance.dom.profileSelector.value : (instance.pluginConfiguration.Profiles[0] && instance.pluginConfiguration.Profiles[0].Id);
                        if (newProfileId) instance.loadProfileSettings(newProfileId);
                        instance.triggerConfigSave();
                        toast('Profile deleted.');
                    }
                } catch (innerErr) {
                    toast({ type: 'error', text: 'Error deleting profile.' });
                }
            }).catch(err => {
                toast({ type: 'error', text: 'Error displaying confirmation dialog.' });
            });
    }

    function loadFilenameMappings(instance, profile) {
        instance.dom.filenameMappingsContainer.innerHTML = '';
        const mappings = (profile && profile.Settings && profile.Settings.FilenameBasedIcons) || [];
        mappings.forEach(mapping => instance.addFilenameMappingRow(mapping));
    }

    function saveFilenameMappings(instance, profile) {
        const mappings = [];
        instance.dom.filenameMappingsContainer.querySelectorAll('.filenameMappingRow').forEach(row => {
            const keyword = row.querySelector('.txtFilenameKeyword').value.trim();
            const iconName = row.querySelector('.txtFilenameIconName').value.trim();
            if (keyword && iconName) mappings.push({ Keyword: keyword, IconName: iconName });
        });
        profile.Settings.FilenameBasedIcons = mappings;
    }

    function downloadJson(jsonString, filename) {
        const blob = new Blob([jsonString], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }

    async function exportCurrentProfile(instance) {
        if (!instance.currentProfileId) {
            toast('No profile selected');
            return;
        }

        try {
            loading.show();
            if (!instance.apiRoutes) await instance.fetchApiRoutes();
            const response = await ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(instance.apiRoutes.ExportProfiles, { ProfileIds: instance.currentProfileId }), dataType: 'json' });

            if (response.Success && response.JsonData) {
                const profile = instance.pluginConfiguration.Profiles.find(p => p.Id === instance.currentProfileId);
                const filename = `emby-icons-profile-${profile.Name.replace(/[^a-z0-9]/gi, '_').toLowerCase()}.json`;
                downloadJson(response.JsonData, filename);
                toast('Profile exported successfully');
            } else {
                toast('Export failed: ' + (response.Error || 'Unknown error'));
            }
        } catch (error) {
            toast('Export failed: ' + error.message);
        } finally {
            loading.hide();
        }
    }

    async function exportAllProfiles(instance) {
        try {
            loading.show();
            if (!instance.apiRoutes) await instance.fetchApiRoutes();
            const response = await ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(instance.apiRoutes.ExportProfiles), dataType: 'json' });

            if (response.Success && response.JsonData) {
                const filename = `emby-icons-profiles-all-${new Date().toISOString().split('T')[0]}.json`;
                downloadJson(response.JsonData, filename);
                toast(`Exported ${response.ProfileCount} profile(s) successfully`);
            } else {
                toast('Export failed: ' + (response.Error || 'Unknown error'));
            }
        } catch (error) {
            toast('Export failed: ' + error.message);
        } finally {
            loading.hide();
        }
    }

    async function importProfiles(instance) {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = '.json';
        input.onchange = async (e) => {
            const file = e.target.files[0];
            if (!file) return;

            try {
                loading.show();
                const jsonData = await file.text();
                if (!instance.apiRoutes) await instance.fetchApiRoutes();
                const validation = await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl(instance.apiRoutes.ValidateProfileImport), data: JSON.stringify({ JsonData: jsonData }), contentType: 'application/json', dataType: 'json' });

                if (!validation.IsValid) {
                    alert('Import validation failed:\n\n' + validation.Errors.join('\n'));
                    loading.hide();
                    return;
                }

                if (validation.Warnings && validation.Warnings.length > 0) {
                    const warnings = validation.Warnings.join('\n');
                    const proceed = confirm(`Import warnings:\n\n${warnings}\n\nDo you want to continue?`);
                    if (!proceed) {
                        loading.hide();
                        return;
                    }
                }

                const overwrite = confirm('Overwrite existing profiles with the same name?\n\nYes = Overwrite\nNo = Rename and create new');

                const result = await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl(instance.apiRoutes.ImportProfiles), data: JSON.stringify({ JsonData: jsonData, OverwriteExisting: overwrite, RenameOnConflict: !overwrite }), contentType: 'application/json', dataType: 'json' });

                if (result.Success) {
                    let message = `Imported: ${result.ImportedCount}\n`;
                    if (result.UpdatedCount > 0) message += `Updated: ${result.UpdatedCount}\n`;
                    if (result.SkippedCount > 0) message += `Skipped: ${result.SkippedCount}\n`;
                    if (result.FailedCount > 0) message += `Failed: ${result.FailedCount}\n`;
                    alert('Import completed:\n\n' + message);
                    await instance.loadData();
                    toast('Profiles imported successfully');
                } else {
                    alert('Import failed:\n\n' + (result.Error || 'Unknown error'));
                }
            } catch (error) {
                alert('Import failed: ' + error.message);
            } finally {
                loading.hide();
            }
        };
        input.click();
    }

    return {
        populateProfileSelector,
        getCurrentProfileSettingsFromForm,
        saveCurrentProfileSettings,
        addProfile,
        renameProfile,
        deleteProfile,
        loadFilenameMappings,
        saveFilenameMappings,
        exportCurrentProfile,
        exportAllProfiles,
        importProfiles,
        downloadJson
    };
});
