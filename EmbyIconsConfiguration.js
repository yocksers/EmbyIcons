define(['baseView', 'loading', 'dialogHelper', 'toast', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading, dialogHelper, toast) {
    'use strict';

    const pluginId = "b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f";

    const ApiRoutes = {
        DefaultProfile: "EmbyIcons/DefaultProfile",
        RefreshCache: "EmbyIcons/RefreshCache",
        IconManagerReport: "EmbyIcons/IconManagerReport",
        Preview: "EmbyIcons/Preview",
        ValidatePath: "EmbyIcons/ValidatePath"
    };

    const transparentPixel = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=';

    function debounce(func, wait) {
        let timeout;
        return function (...args) {
            const context = this;
            clearTimeout(timeout);
            timeout = setTimeout(() => func.apply(context, args), wait);
        };
    }

    class EmbyIconsConfigurationView extends BaseView {
        constructor(view, params) {
            super(view, params);

            this.pluginConfiguration = {};
            this.allLibraries = [];
            this.currentProfileId = null;
            this.previewUpdateTimer = null;
            this.configSaveTimer = null;
            this.folderValidationTimer = null;

            this.getDomElements(view);
            this.bindEvents();
        }

        getDomElements(view) {
            this.dom = {
                view: view,
                form: view.querySelector('.embyIconsForm'),
                allConfigInputs: view.querySelectorAll('[data-config-key]'),
                allProfileInputs: view.querySelectorAll('[data-profile-key]'),
                allProfileSelects: view.querySelectorAll('select[is="emby-select"][data-profile-key]'),
                navButtons: view.querySelectorAll('.localnav .nav-button'),
                profileSelector: view.querySelector('#selActiveProfile'),
                btnAddProfile: view.querySelector('#btnAddProfile'),
                btnRenameProfile: view.querySelector('#btnRenameProfile'),
                btnDeleteProfile: view.querySelector('#btnDeleteProfile'),
                btnSelectIconsFolder: view.querySelector('#btnSelectIconsFolder'),
                txtIconsFolder: view.querySelector('#txtIconsFolder'),
                folderWarningIcon: view.querySelector('#folderWarningIcon'),
                btnClearCache: view.querySelector('#btnClearCache'),
                btnRunIconScan: view.querySelector('#btnRunIconScan'),
                librarySelectionContainer: view.querySelector('#librarySelectionContainer'),
                opacitySlider: view.querySelector('[data-profile-key="CommunityScoreBackgroundOpacity"]'),
                opacityValue: view.querySelector('.valCommunityScoreBackgroundOpacity'),
                ratingAppearanceControls: view.querySelector('#ratingAppearanceControls'),
                pages: view.querySelectorAll('#settingsPage, #readmePage, #iconManagerPage'),
                previewImage: view.querySelector('.previewImage'),
                iconManagerReportContainer: view.querySelector('#iconManagerReportContainer'),
                alignmentGrid: view.querySelector('.alignment-grid'),
                prioritySelects: view.querySelectorAll('[data-profile-key$="Priority"]')
            };
        }

        bindEvents() {
            this.dom.form.addEventListener('change', this.onFormChange.bind(this));
            this.dom.opacitySlider.addEventListener('input', this.onFormChange.bind(this));

            this.dom.navButtons.forEach(navButton => {
                navButton.addEventListener('click', this.onTabChange.bind(this));
            });

            this.dom.previewImage.addEventListener('error', () => {
                this.dom.previewImage.src = transparentPixel;
                toast({ type: 'error', text: 'Preview generation failed.' });
            });

            this.dom.profileSelector.addEventListener('change', this.onProfileSelected.bind(this));
            this.dom.btnAddProfile.addEventListener('click', this.addProfile.bind(this));
            this.dom.btnRenameProfile.addEventListener('click', this.renameProfile.bind(this));
            this.dom.btnDeleteProfile.addEventListener('click', this.deleteProfile.bind(this));
            this.dom.btnSelectIconsFolder.addEventListener('click', this.selectIconsFolder.bind(this));
            this.dom.btnClearCache.addEventListener('click', this.clearCache.bind(this));
            this.dom.btnRunIconScan.addEventListener('click', this.runIconScan.bind(this));
            this.dom.txtIconsFolder.addEventListener('input', debounce(this.validateIconsFolder.bind(this), 500));
            this.dom.prioritySelects.forEach(select => {
                select.addEventListener('change', this.onPriorityChange.bind(this));
            });

            this.dom.form.addEventListener('submit', (e) => {
                e.preventDefault();
                this.saveData();
                return false;
            });
        }

        onFormChange(event) {
            this.triggerConfigSave();
            this.triggerPreviewUpdate();

            if (event.target.matches('[data-profile-key="CommunityScoreBackgroundOpacity"]')) {
                this.dom.opacityValue.textContent = event.target.value + '%';
            }
            if (event.target.matches('[data-profile-key="CommunityScoreIconAlignment"]')) {
                this.toggleRatingAppearanceControls();
            }
            if (event.target.matches('[data-profile-key="UseSeriesLiteMode"]')) {
                this.toggleDependentSetting('UseSeriesLiteMode', 'ShowSeriesIconsIfAllEpisodesHaveLanguage', 'This setting is ignored when Lite Mode is enabled, as Lite Mode only scans one episode.');
            }
            if (event.target.matches('[data-profile-key="UseCollectionLiteMode"]')) {
                this.toggleDependentSetting('UseCollectionLiteMode', 'ShowCollectionIconsIfAllChildrenHaveLanguage', 'This setting is ignored when Lite Mode is enabled, as Lite Mode only scans one item.');
            }
        }

        onPriorityChange(event) {
            const changedSelect = event.target;
            const cornerGroup = changedSelect.closest('[data-corner-group]');
            if (!cornerGroup) return;

            const groupName = cornerGroup.getAttribute('data-corner-group');
            this.updatePriorityOptionsForGroup(groupName);
        }

        updatePriorityOptionsForGroup(groupName) {
            const groupSelects = this.dom.alignmentGrid.querySelectorAll(`[data-corner-group="${groupName}"] [data-profile-key$="Priority"]`);
            const selectedPriorities = new Set();

            groupSelects.forEach(select => {
                if (select.value !== '0') {
                    selectedPriorities.add(select.value);
                }
            });

            groupSelects.forEach(select => {
                const ownValue = select.value;
                select.querySelectorAll('option').forEach(option => {
                    if (option.value !== '0' && option.value !== ownValue) {
                        option.disabled = selectedPriorities.has(option.value);
                    } else {
                        option.disabled = false;
                    }
                });
            });
        }

        updateAllPriorityGroups() {
            const groupNames = new Set();
            this.dom.prioritySelects.forEach(select => {
                const cornerGroup = select.closest('[data-corner-group]');
                if (cornerGroup) groupNames.add(cornerGroup.getAttribute('data-corner-group'));
            });
            groupNames.forEach(name => this.updatePriorityOptionsForGroup(name));
        }

        toggleDependentSetting(controllerKey, dependentKey, dependentMessage) {
            const controllerCheckbox = this.dom.view.querySelector(`[data-profile-key="${controllerKey}"]`);
            const dependentCheckbox = this.dom.view.querySelector(`[data-profile-key="${dependentKey}"]`);
            if (controllerCheckbox && dependentCheckbox) {
                const container = dependentCheckbox.closest('.checkboxContainer');
                const isDisabled = controllerCheckbox.checked;
                dependentCheckbox.disabled = isDisabled;
                if (container) {
                    container.style.opacity = isDisabled ? '0.6' : '1';
                    container.style.pointerEvents = isDisabled ? 'none' : 'auto';
                    container.title = isDisabled ? dependentMessage : '';
                }
            }
        }

        toggleRatingAppearanceControls() {
            const ratingAlignmentSelect = this.dom.view.querySelector('[data-profile-key="CommunityScoreIconAlignment"]');
            this.dom.ratingAppearanceControls.style.display = ratingAlignmentSelect.value === 'Disabled' ? 'none' : 'block';
        }

        onTabChange(e) {
            const currentTarget = e.currentTarget;
            this.dom.view.querySelector('.localnav .ui-btn-active')?.classList.remove('ui-btn-active');
            currentTarget.classList.add('ui-btn-active');
            const targetId = currentTarget.getAttribute('data-target');
            this.dom.pages.forEach(page => {
                page.classList.toggle('hide', page.id !== targetId);
            });
        }

        onResume(options) {
            super.onResume(options);
            this.loadData();
        }

        async loadData() {
            loading.show();
            try {
                const [config, virtualFolders] = await Promise.all([
                    ApiClient.getPluginConfiguration(pluginId),
                    ApiClient.getVirtualFolders()
                ]);
                this.pluginConfiguration = config;
                this.loadGlobalSettings(config);

                const ignoredLibraryTypes = ['music', 'collections', 'playlists', 'boxsets'];
                this.allLibraries = virtualFolders.Items.filter(lib => !lib.CollectionType || !ignoredLibraryTypes.includes(lib.CollectionType.toLowerCase()));

                this.populateProfileSelector();
                this.loadProfileSettings(this.dom.profileSelector.value);
                this.validateIconsFolder();
            } catch (error) {
                console.error('Failed to load EmbyIcons configuration', error);
                toast({ type: 'error', text: 'Error loading configuration.' });
            } finally {
                loading.hide();
            }
        }

        loadGlobalSettings(config) {
            this.dom.allConfigInputs.forEach(el => {
                const key = el.getAttribute('data-config-key');
                const value = config[key];
                if (el.type === 'checkbox') {
                    el.checked = value;
                } else {
                    el.value = value ?? '';
                }
            });
        }

        populateProfileSelector() {
            const select = this.dom.profileSelector;
            select.innerHTML = this.pluginConfiguration.Profiles.map(p => `<option value="${p.Id}">${p.Name}</option>`).join('');
            this.currentProfileId = select.value;
            if (select.embyselect) select.embyselect.refresh();
        }

        onProfileSelected(e) {
            this.loadProfileSettings(e.target.value);
        }

        loadProfileSettings(profileId) {
            this.currentProfileId = profileId;
            const profile = this.pluginConfiguration.Profiles.find(p => p.Id === profileId);
            if (!profile) return;

            this.renderProfileSettings(profile.Settings);
            this.populateLibraryAssignments(profileId);
            this.triggerPreviewUpdate();
            this.updateAllPriorityGroups();
        }

        renderProfileSettings(settings) {
            this.dom.allProfileInputs.forEach(el => {
                const key = el.getAttribute('data-profile-key');
                const value = settings[key];
                if (el.type === 'checkbox') {
                    el.checked = value;
                } else {
                    el.value = value ?? '';
                }
            });
            this.dom.allProfileSelects.forEach(s => {
                if (s.embyselect) s.embyselect.refresh();
            });

            this.dom.opacityValue.textContent = this.dom.opacitySlider.value + '%';

            this.toggleRatingAppearanceControls();
            this.toggleDependentSetting('UseSeriesLiteMode', 'ShowSeriesIconsIfAllEpisodesHaveLanguage', 'This setting is ignored when Lite Mode is enabled, as Lite Mode only scans one episode.');
            this.toggleDependentSetting('UseCollectionLiteMode', 'ShowCollectionIconsIfAllChildrenHaveLanguage', 'This setting is ignored when Lite Mode is enabled, as Lite Mode only scans one item.');
        }

        populateLibraryAssignments(profileId) {
            const container = this.dom.librarySelectionContainer;
            container.innerHTML = this.allLibraries.map(library => `<div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" data-library-id="${library.Id}" /><span>${library.Name}</span></label></div>`).join('');
            const mappedLibraryIds = new Set(this.pluginConfiguration.LibraryProfileMappings.filter(m => m.ProfileId === profileId).map(m => m.LibraryId));
            container.querySelectorAll('input[type=checkbox]').forEach(checkbox => {
                checkbox.checked = mappedLibraryIds.has(checkbox.getAttribute('data-library-id'));
                checkbox.addEventListener('change', this.onFormChange.bind(this));
            });
        }

        getCurrentProfileSettingsFromForm() {
            const settings = {};
            this.dom.allProfileInputs.forEach(el => {
                const key = el.getAttribute('data-profile-key');
                if (el.type === 'checkbox') {
                    settings[key] = el.checked;
                } else if (el.type === 'number' || el.classList.contains('slider') || key.endsWith('Priority')) {
                    settings[key] = parseInt(el.value, 10) || 0;
                } else {
                    settings[key] = el.value;
                }
            });
            return settings;
        }

        saveCurrentProfileSettings() {
            const profile = this.pluginConfiguration.Profiles.find(p => p.Id === this.currentProfileId);
            if (!profile) return;

            Object.assign(profile.Settings, this.getCurrentProfileSettingsFromForm());

            this.pluginConfiguration.LibraryProfileMappings = this.pluginConfiguration.LibraryProfileMappings.filter(m => m.ProfileId !== this.currentProfileId);
            this.dom.librarySelectionContainer.querySelectorAll('input:checked').forEach(checkbox => {
                this.pluginConfiguration.LibraryProfileMappings.push({
                    LibraryId: checkbox.getAttribute('data-library-id'),
                    ProfileId: this.currentProfileId
                });
            });
        }

        showDialog(templateId, dialogOptions) {
            const template = this.dom.view.querySelector(templateId);
            const dlg = dialogHelper.createDialog(dialogOptions);
            dlg.innerHTML = '';
            dlg.appendChild(template.content.cloneNode(true));
            dialogHelper.open(dlg);
            return dlg;
        }

        addProfile() {
            const dlg = this.showDialog('#addProfileDialogTemplate', { removeOnClose: true, size: 'small' });

            dlg.querySelector('form').addEventListener('submit', async (e) => {
                e.preventDefault();
                loading.show();
                try {
                    const newName = dlg.querySelector('#txtNewProfileName').value;
                    const newProfile = await ApiClient.ajax({ type: "GET", url: ApiClient.getUrl(ApiRoutes.DefaultProfile), dataType: "json" });
                    newProfile.Name = newName;

                    this.pluginConfiguration.Profiles.push(newProfile);
                    this.populateProfileSelector();
                    this.dom.profileSelector.value = newProfile.Id;
                    if (this.dom.profileSelector.embyselect) this.dom.profileSelector.embyselect.refresh();
                    this.loadProfileSettings(newProfile.Id);
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

        renameProfile() {
            const profile = this.pluginConfiguration.Profiles.find(p => p.Id === this.currentProfileId);
            if (!profile) return;

            const dlg = this.showDialog('#renameProfileDialogTemplate', { removeOnClose: true, size: 'small' });
            const input = dlg.querySelector('#txtRenameProfile');
            input.value = profile.Name;

            dlg.querySelector('form').addEventListener('submit', (e) => {
                e.preventDefault();
                profile.Name = input.value;
                this.populateProfileSelector();
                this.triggerConfigSave();
                toast('Profile renamed.');
                dialogHelper.close(dlg);
                return false;
            });
            dlg.querySelector('.btnCancel').addEventListener('click', () => dialogHelper.close(dlg));
        }

        deleteProfile() {
            if (this.pluginConfiguration.Profiles.length <= 1) {
                toast({ type: 'error', text: 'Cannot delete the last profile.' });
                return;
            }
            const profile = this.pluginConfiguration.Profiles.find(p => p.Id === this.currentProfileId);
            if (!profile) return;

            dialogHelper.confirm(`Are you sure you want to delete the profile "${profile.Name}"?`, 'Delete Profile').then(result => {
                if (result) {
                    this.pluginConfiguration.Profiles = this.pluginConfiguration.Profiles.filter(p => p.Id !== this.currentProfileId);
                    this.pluginConfiguration.LibraryProfileMappings = this.pluginConfiguration.LibraryProfileMappings.filter(m => m.ProfileId !== this.currentProfileId);
                    this.populateProfileSelector();
                    this.loadProfileSettings(this.dom.profileSelector.value);
                    this.triggerConfigSave();
                }
            });
        }

        async saveData() {
            if (!this.pluginConfiguration.Profiles || !this.pluginConfiguration.Profiles.length) {
                toast({ type: 'error', text: 'Cannot save settings. You must create at least one profile.' });
                return;
            }

            loading.show();
            clearTimeout(this.configSaveTimer);
            this.saveCurrentProfileSettings();

            this.dom.allConfigInputs.forEach(el => {
                const key = el.getAttribute('data-config-key');
                if (el.type === 'checkbox') {
                    this.pluginConfiguration[key] = el.checked;
                } else {
                    this.pluginConfiguration[key] = el.value;
                }
            });
            try {
                const result = await ApiClient.updatePluginConfiguration(pluginId, this.pluginConfiguration);
                Dashboard.processPluginConfigurationUpdateResult(result);
            } catch (error) {
                console.error('Error saving EmbyIcons settings', error);
                toast({ type: 'error', text: 'Error saving settings.' });
            } finally {
                loading.hide();
            }
        }

        triggerConfigSave() {
            clearTimeout(this.configSaveTimer);
            this.configSaveTimer = setTimeout(() => {
                if (this.dom.view) {
                    this.saveCurrentProfileSettings();
                }
            }, 400);
        }

        triggerPreviewUpdate() {
            clearTimeout(this.previewUpdateTimer);
            this.previewUpdateTimer = setTimeout(() => {
                if (this.dom.view) this.updatePreview();
            }, 300);
        }

        updatePreview() {
            const currentSettings = this.getCurrentProfileSettingsFromForm();
            if (!currentSettings) return;

            this.dom.previewImage.src = ApiClient.getUrl(ApiRoutes.Preview, {
                OptionsJson: JSON.stringify(currentSettings),
                v: new Date().getTime()
            });
        }

        selectIconsFolder() {
            require(['directorybrowser'], (directorybrowser) => {
                const browser = new directorybrowser();
                browser.show({
                    header: 'Select Icons Folder',
                    path: this.dom.txtIconsFolder.value,
                    callback: (path) => {
                        if (path) {
                            this.dom.txtIconsFolder.value = path;
                            this.onFormChange({ target: this.dom.txtIconsFolder });
                            this.validateIconsFolder();
                        }
                        browser.close();
                    }
                });
            });
        }

        async validateIconsFolder() {
            const path = this.dom.txtIconsFolder.value;
            if (!path) {
                this.dom.folderWarningIcon.style.display = 'none';
                return;
            }

            try {
                const result = await ApiClient.ajax({
                    type: 'GET',
                    url: ApiClient.getUrl(ApiRoutes.ValidatePath, { Path: path }),
                    dataType: 'json'
                });

                if (!result.Exists) {
                    this.dom.folderWarningIcon.style.display = 'block';
                    this.dom.folderWarningIcon.title = 'The specified folder does not exist on the server.';
                } else if (!result.HasImages) {
                    this.dom.folderWarningIcon.style.display = 'block';
                    this.dom.folderWarningIcon.title = 'The specified folder exists, but no supported image files were found inside.';
                } else {
                    this.dom.folderWarningIcon.style.display = 'none';
                }
            } catch (err) {
                console.error('Error validating path', err);
                this.dom.folderWarningIcon.style.display = 'none';
            }
        }

        async clearCache() {
            loading.show();
            try {
                await ApiClient.ajax({ type: "POST", url: ApiClient.getUrl(ApiRoutes.RefreshCache) });
                toast('Cache cleared, icons will be redrawn.');
            } catch (error) {
                console.error('Error clearing EmbyIcons cache', error);
                toast({ type: 'error', text: 'Error clearing icon cache.' });
            } finally {
                loading.hide();
            }
        }

        async runIconScan() {
            loading.show();
            this.dom.btnRunIconScan.disabled = true;
            this.dom.btnRunIconScan.querySelector('span').textContent = 'Scanning...';

            const container = this.dom.iconManagerReportContainer;
            container.innerHTML = '<p>Scanning your library... This may take several minutes on the first run.</p>';

            try {
                const report = await ApiClient.ajax({ type: "GET", url: ApiClient.getUrl(ApiRoutes.IconManagerReport), dataType: "json" });
                this.renderIconManagerReport(report);
            } catch (error) {
                console.error('Error getting icon manager report', error);
                container.innerHTML = '<p style="color: #ff4444;">An error occurred while generating the report. Please check the server logs.</p>';
            } finally {
                loading.hide();
                this.dom.btnRunIconScan.disabled = false;
                this.dom.btnRunIconScan.querySelector('span').textContent = 'Scan Library & Icons';
            }
        }

        renderIconManagerReport(report) {
            const container = this.dom.iconManagerReportContainer;
            const friendlyNames = { Language: 'Audio Languages', Subtitle: 'Subtitle Languages', Channel: 'Audio Channels', AudioCodec: 'Audio Codecs', VideoCodec: 'Video Codecs', VideoFormat: 'Video Formats', Resolution: 'Resolutions', AspectRatio: 'Aspect Ratios', Tag: 'Custom Tags', ParentalRating: 'Parental Ratings' };
            const prefixMap = { Language: "lang.", Subtitle: "sub.", Channel: "ch.", AudioCodec: "ac.", VideoCodec: "vc.", VideoFormat: "hdr.", Resolution: "res.", AspectRatio: "ar.", Tag: "tag.", ParentalRating: "pr." };

            const htmlParts = [];
            htmlParts.push(`<p class="fieldDescription">Report generated on: ${new Date(report.ReportDate).toLocaleString()}</p>`);

            for (const groupName in report.Groups) {
                if (Object.prototype.hasOwnProperty.call(report.Groups, groupName) && friendlyNames[groupName]) {
                    const group = report.Groups[groupName];
                    const librarySet = new Set(group.FoundInLibrary.map(i => i.toLowerCase()));
                    const folderSet = new Set(group.FoundInFolder.map(i => i.toLowerCase()));
                    const found = [...folderSet].filter(i => librarySet.has(i)).sort();
                    const missing = [...librarySet].filter(i => !folderSet.has(i)).sort();
                    const unused = [...folderSet].filter(i => !librarySet.has(i)).sort();

                    if (found.length === 0 && missing.length === 0 && unused.length === 0) continue;

                    htmlParts.push(`<div class="paper-card" style="margin-top: 1.5em; padding: 1em 1.5em;">`);
                    htmlParts.push(`<h3 style="margin-top: 0; cursor: pointer;" onclick="this.nextElementSibling.style.display = this.nextElementSibling.style.display === 'none' ? 'block' : 'none';">${friendlyNames[groupName]}</h3>`);
                    htmlParts.push(`<div class="collapsible-content" style="display: none;">`);

                    if (missing.length > 0) {
                        htmlParts.push(`<h4><span style="color: #ffc107;">!</span> Missing Icons (${missing.length})</h4><p class="fieldDescription">Your library needs these icons, but they were not found in your custom folder. You can add them or rely on the built-in fallback icons (if enabled).</p>`);
                        htmlParts.push(`<div class="icon-grid">${missing.map(i => `<code>${prefixMap[groupName] || ''}${i}.png</code>`).join('')}</div>`);
                    }
                    if (found.length > 0) {
                        htmlParts.push(`<h4 style="margin-top: 1.5em;"><span style="color: #4CAF50;">✓</span> Found Icons (${found.length})</h4><p class="fieldDescription">These custom icons are correctly configured and used by your library.</p>`);
                        htmlParts.push(`<div class="icon-grid">${found.map(i => `<code>${prefixMap[groupName] || ''}${i}.png</code>`).join('')}</div>`);
                    }
                    if (unused.length > 0) {
                        htmlParts.push(`<h4 style="margin-top: 1.5em;"><span style="color: #888;">-</span> Unused Icons (${unused.length})</h4><p class="fieldDescription">These icons exist in your folder but are not currently needed by any media.</p>`);
                        htmlParts.push(`<div class="icon-grid">${unused.map(i => `<code>${prefixMap[groupName] || ''}${i}.png</code>`).join('')}</div>`);
                    }
                    htmlParts.push(`</div></div>`);
                }
            }
            htmlParts.push(`<style>.icon-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 0.5em; } .icon-grid code { background-color: rgba(128,128,128,0.2); padding: 0.2em 0.4em; border-radius: 3px; word-break: break-all; }</style>`);

            container.innerHTML = htmlParts.join('');
        }
    }

    return EmbyIconsConfigurationView;
});