define(['baseView', 'loading', 'dialogHelper', 'toast', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading, dialogHelper, toast) {
    'use strict';

    const pluginId = "b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f";

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
            this.selectedSeriesId = null;
            this.libraryMap = new Map();
            this.profileMap = new Map();
            this.apiRoutes = null;

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
                pages: view.querySelectorAll('#settingsPage, #readmePage, #iconManagerPage, #troubleshooterPage'),
                previewImage: view.querySelector('.previewImage'),
                iconManagerReportContainer: view.querySelector('#iconManagerReportContainer'),
                alignmentGrid: view.querySelector('.alignment-grid'),
                prioritySelects: view.querySelectorAll('[data-profile-key$="Priority"]'),
                txtSeriesSearch: view.querySelector('#txtSeriesSearch'),
                seriesSearchResults: view.querySelector('#seriesSearchResults'),
                btnRunSeriesScan: view.querySelector('#btnRunSeriesScan'),
                btnRunFullSeriesScan: view.querySelector('#btnRunFullSeriesScan'),
                seriesReportContainer: view.querySelector('#seriesReportContainer'),
                troubleshooterChecks: view.querySelectorAll('#troubleshooterChecksContainer input[type=checkbox]'),
                btnCalculateAspectRatio: view.querySelector('#btnCalculateAspectRatio'),
                txtAspectRatioWidth: view.querySelector('#txtAspectRatioWidth'),
                txtAspectRatioHeight: view.querySelector('#txtAspectRatioHeight'),
                aspectRatioResultContainer: view.querySelector('#aspectRatioResultContainer'),
                aspectDecimalValue: view.querySelector('#aspectDecimalValue'),
                aspectSnappedIconName: view.querySelector('#aspectSnappedIconName'),
                aspectPreciseIconName: view.querySelector('#aspectPreciseIconName'),
                filenameMappingsContainer: view.querySelector('#filenameMappingsContainer'),
                btnAddFilenameMapping: view.querySelector('#btnAddFilenameMapping'),
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

            this.dom.txtSeriesSearch.addEventListener('input', debounce(this.searchForSeries.bind(this), 300));
            this.dom.seriesSearchResults.addEventListener('click', this.onSeriesSearchResultClick.bind(this));
            this.dom.btnRunSeriesScan.addEventListener('click', this.runSeriesScan.bind(this));
            this.dom.btnRunFullSeriesScan.addEventListener('click', this.runFullSeriesScan.bind(this));
            this.dom.seriesReportContainer.addEventListener('click', this.onSeriesReportHeaderClick.bind(this));

            this.dom.btnCalculateAspectRatio.addEventListener('click', this.calculateAspectRatio.bind(this));

            this.dom.btnAddFilenameMapping.addEventListener('click', this.addFilenameMappingRow.bind(this, null));
            this.dom.filenameMappingsContainer.addEventListener('click', this.onFilenameMappingButtonClick.bind(this));

            this.dom.form.addEventListener('submit', (e) => {
                e.preventDefault();
                this.saveData();
                return false;
            });

            document.addEventListener('click', (e) => {
                if (!this.dom.seriesSearchResults.contains(e.target) && !this.dom.txtSeriesSearch.contains(e.target)) {
                    this.dom.seriesSearchResults.style.display = 'none';
                }
            });
        }

        async calculateAspectRatio() {
            const width = parseInt(this.dom.txtAspectRatioWidth.value, 10);
            const height = parseInt(this.dom.txtAspectRatioHeight.value, 10);

            if (!width || !height || width <= 0 || height <= 0) {
                toast({ type: 'error', text: 'Please enter valid width and height values.' });
                this.dom.aspectRatioResultContainer.style.display = 'none';
                return;
            }

            try {
                const result = await ApiClient.ajax({
                    type: "GET",
                    url: ApiClient.getUrl(this.apiRoutes.AspectRatio, { Width: width, Height: height }),
                    dataType: "json"
                });

                this.dom.aspectDecimalValue.textContent = result.DecimalRatio.toFixed(4);
                this.dom.aspectSnappedIconName.textContent = result.SnappedName;
                this.dom.aspectPreciseIconName.textContent = result.PreciseName;
                this.dom.aspectRatioResultContainer.style.display = 'block';

            } catch (err) {
                toast({ type: 'error', text: 'Error calculating aspect ratio.' });
                this.dom.aspectRatioResultContainer.style.display = 'none';
            }
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

        async onResume(options) {
            super.onResume(options);
            loading.show();
            try {
                await this.fetchApiRoutes();
                await this.loadData();
            } catch (error) {
                console.error('Failed to initialize EmbyIcons configuration page', error);
                toast({ type: 'error', text: 'Error loading page. Please refresh.' });
            } finally {
                loading.hide();
            }
        }

        async fetchApiRoutes() {
            if (this.apiRoutes) {
                return;
            }
            this.apiRoutes = await ApiClient.ajax({
                type: "GET",
                url: ApiClient.getUrl("EmbyIcons/ApiRoutes"),
                dataType: "json"
            });
        }

        async loadData() {
            try {
                const [config, virtualFolders, user] = await Promise.all([
                    ApiClient.getPluginConfiguration(pluginId),
                    ApiClient.getVirtualFolders(),
                    ApiClient.getCurrentUser()
                ]);

                this.pluginConfiguration = config;
                this.currentUser = user;
                this.loadGlobalSettings(config);

                const ignoredLibraryTypes = ['music', 'collections', 'playlists', 'boxsets'];
                this.allLibraries = virtualFolders.Items.filter(lib => !lib.CollectionType || !ignoredLibraryTypes.includes(lib.CollectionType.toLowerCase()));
                this.libraryMap = new Map(this.allLibraries.map(lib => [lib.Id, lib.Name]));
                this.profileMap = new Map(this.pluginConfiguration.Profiles.map(p => [p.Id, p.Name]));

                this.populateProfileSelector();
                this.loadProfileSettings(this.dom.profileSelector.value);
                this.validateIconsFolder();
            } catch (error) {
                console.error('Failed to load EmbyIcons configuration', error);
                toast({ type: 'error', text: 'Error loading configuration.' });
                throw error;
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
            this.loadFilenameMappings(profile);
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

        async populateLibraryAssignments(profileId) {
            const container = this.dom.librarySelectionContainer;
            if (!this.allLibraries) {
                const virtualFolders = await ApiClient.getVirtualFolders();
                const ignoredLibraryTypes = ['music', 'collections', 'playlists', 'boxsets'];
                this.allLibraries = virtualFolders.Items.filter(lib => !lib.CollectionType || !ignoredLibraryTypes.includes(lib.CollectionType.toLowerCase()));
            }

            const libraryToProfileMap = new Map(this.pluginConfiguration.LibraryProfileMappings.map(m => [m.LibraryId, m.ProfileId]));

            let html = '';
            for (const library of this.allLibraries) {
                const assignedProfileId = libraryToProfileMap.get(library.Id);
                const isAssignedToCurrent = assignedProfileId === profileId;
                const isAssignedToOther = assignedProfileId && !isAssignedToCurrent;

                const isChecked = isAssignedToCurrent;
                const isDisabled = isAssignedToOther;

                let title = '';
                if (isDisabled) {
                    const otherProfileName = this.profileMap.get(assignedProfileId) || 'another profile';
                    title = ` title="This library is managed by the '${otherProfileName}' profile."`;
                }

                html += `<div class="checkboxContainer"${title}>
                                <label>
                                    <input is="emby-checkbox" type="checkbox" data-library-id="${library.Id}" ${isChecked ? 'checked' : ''} ${isDisabled ? 'disabled' : ''} />
                                    <span>${library.Name}</span>
                                </label>
                             </div>`;
            }
            container.innerHTML = html;
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
            this.saveFilenameMappings(profile);

            this.pluginConfiguration.LibraryProfileMappings = this.pluginConfiguration.LibraryProfileMappings.filter(m => m.ProfileId !== this.currentProfileId);
            this.dom.librarySelectionContainer.querySelectorAll('input:checked:not(:disabled)').forEach(checkbox => {
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
                    const newProfile = await ApiClient.ajax({ type: "GET", url: ApiClient.getUrl(this.apiRoutes.DefaultProfile), dataType: "json" });
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

            this.dom.previewImage.src = ApiClient.getUrl(this.apiRoutes.Preview, {
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
                    url: ApiClient.getUrl(this.apiRoutes.ValidatePath, { Path: path }),
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
                await ApiClient.ajax({ type: "POST", url: ApiClient.getUrl(this.apiRoutes.RefreshCache) });
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
                const report = await ApiClient.ajax({ type: "GET", url: ApiClient.getUrl(this.apiRoutes.IconManagerReport), dataType: "json" });
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

        async searchForSeries() {
            const searchTerm = this.dom.txtSeriesSearch.value;
            const resultsContainer = this.dom.seriesSearchResults;

            if (searchTerm.length < 2) {
                resultsContainer.innerHTML = '';
                resultsContainer.style.display = 'none';
                return;
            }

            try {
                const results = await ApiClient.getItems(this.currentUser.Id, {
                    IncludeItemTypes: 'Series',
                    Recursive: true,
                    SearchTerm: searchTerm,
                    Limit: 10,
                    Fields: 'Path'
                });

                if (results.Items.length) {
                    resultsContainer.innerHTML = results.Items.map(item =>
                        `<div class="searchResultItem" data-id="${item.Id}" data-name="${item.Name}" style="padding: 0.75em 1em; cursor: pointer;">${item.Name} <span style="color:#aaa; font-size:0.9em;">(${item.Path || 'Unknown'})</span></div>`
                    ).join('');
                    resultsContainer.style.display = 'block';
                } else {
                    resultsContainer.innerHTML = '<div style="padding: 0.75em 1em; color: #aaa;">No results found</div>';
                    resultsContainer.style.display = 'block';
                }
            } catch (err) {
                console.error('Error searching for series', err);
                resultsContainer.style.display = 'none';
            }
        }

        onSeriesSearchResultClick(e) {
            const target = e.target.closest('.searchResultItem');
            if (target) {
                this.selectedSeriesId = target.getAttribute('data-id');
                this.dom.txtSeriesSearch.value = target.getAttribute('data-name');
                this.dom.seriesSearchResults.style.display = 'none';
                this.dom.seriesSearchResults.innerHTML = '';
            }
        }

        getTroubleshooterChecks() {
            return Array.from(this.dom.troubleshooterChecks)
                .filter(cb => cb.checked)
                .map(cb => cb.getAttribute('data-check-name'))
                .join(',');
        }

        async runSeriesScan() {
            if (!this.selectedSeriesId) {
                toast({ type: 'error', text: 'Please search for and select a TV show first.' });
                return;
            }
            const checksToRun = this.getTroubleshooterChecks();
            if (!checksToRun) {
                toast({ type: 'error', text: 'Please select at least one property to check.' });
                return;
            }

            loading.show();
            this.dom.btnRunSeriesScan.disabled = true;
            const container = this.dom.seriesReportContainer;
            container.innerHTML = '<p>Scanning series... This might take a moment.</p>';

            try {
                const reports = await ApiClient.ajax({
                    type: "GET",
                    url: ApiClient.getUrl(this.apiRoutes.SeriesTroubleshooter, { SeriesId: this.selectedSeriesId, ChecksToRun: checksToRun }),
                    dataType: "json"
                });
                this.renderSeriesReport(reports);
            } catch (error) {
                console.error('Error getting series troubleshooter report', error);
                container.innerHTML = '<p style="color: #ff4444;">An error occurred while generating the report. Please check the server logs.</p>';
            } finally {
                loading.hide();
                this.dom.btnRunSeriesScan.disabled = false;
            }
        }

        async runFullSeriesScan() {
            const checksToRun = this.getTroubleshooterChecks();
            if (!checksToRun) {
                toast({ type: 'error', text: 'Please select at least one property to check.' });
                return;
            }

            loading.show();
            this.dom.btnRunFullSeriesScan.disabled = true;
            const container = this.dom.seriesReportContainer;
            container.innerHTML = '<p>Scanning all TV shows in your library... This can take several minutes.</p>';

            try {
                const reports = await ApiClient.ajax({
                    type: "GET",
                    url: ApiClient.getUrl(this.apiRoutes.SeriesTroubleshooter, { ChecksToRun: checksToRun }),
                    dataType: "json"
                });
                this.renderSeriesReport(reports);
            } catch (error) {
                console.error('Error getting full series troubleshooter report', error);
                container.innerHTML = '<p style="color: #ff4444;">An error occurred while generating the report. Please check the server logs.</p>';
            } finally {
                loading.hide();
                this.dom.btnRunFullSeriesScan.disabled = false;
            }
        }

        renderSeriesReport(reports) {
            const container = this.dom.seriesReportContainer;
            if (!reports || reports.length === 0) {
                container.innerHTML = '<h2>Scan Complete</h2><p>No inconsistencies found for the selected criteria.</p>';
                return;
            }

            const html = reports.map(report => {
                const mismatchChecks = report.Checks.filter(c => c.Status === 'Mismatch');
                if (mismatchChecks.length === 0) {
                    return ''; // Don't render shows that are OK for the full scan
                }

                const checksHtml = mismatchChecks.map(check => {
                    const episodesHtml = check.MismatchedEpisodes.map(ep =>
                        `<li>
                            <code>${ep.EpisodeName || `Item ID: ${ep.EpisodeId}`}</code>
                            <br>
                            <span style="font-size: 0.9em;">
                                Expected: <code class="report-code report-code-ok">${check.DominantValues.join(', ') || 'N/A'}</code>, 
                                Found: <code class="report-code report-code-warn">${ep.Actual.join(', ') || 'Nothing'}</code>
                            </span>
                         </li>`
                    ).join('');

                    return `<div class="paper-card" style="padding: 1em 1.5em; margin-top: 1em;">
                                <h4 style="margin:0; color: #ffc107;">${check.CheckName}</h4>
                                <p class="fieldDescription" style="margin: 0.5em 0;">${check.Message}</p>
                                <ul style="margin: 1em 0 0.5em; padding-left: 1.5em; list-style-type: disc;">${episodesHtml}</ul>
                            </div>`;
                }).join('');

                return `<div class="report-group collapsible" style="margin-bottom: 1em;">
                            <div class="collapsible-header paper-card" style="padding: 1em 1.5em; cursor: pointer; display: flex; align-items: center; justify-content: space-between;">
                                <h2 style="margin: 0;">${report.SeriesName} <span class="fieldDescription">(${report.TotalEpisodes} episodes)</span></h2>
                                <i class="md-icon collapsible-indicator" style="transition: transform 0.2s ease;">keyboard_arrow_down</i>
                            </div>
                            <div class="collapsible-content" style="display: none; padding-top: 0.5em;">
                                ${checksHtml}
                            </div>
                        </div>`;

            }).join('');

            container.innerHTML = (html.trim() === '') ? '<h2>Scan Complete</h2><p>No inconsistencies found in any TV show for the selected criteria.</p>' : html;

            container.insertAdjacentHTML('beforeend', `<style>
                .report-code { background-color: rgba(128,128,128,0.2); padding: 0.2em 0.4em; border-radius: 3px; }
                .report-code-ok { color: #4CAF50; }
                .report-code-warn { color: #ffc107; }
            </style>`);
        }

        onSeriesReportHeaderClick(e) {
            const header = e.target.closest('.collapsible-header');
            if (!header) return;

            const content = header.nextElementSibling;
            const indicator = header.querySelector('.collapsible-indicator');

            if (content && content.classList.contains('collapsible-content')) {
                const isVisible = content.style.display !== 'none';
                content.style.display = isVisible ? 'none' : 'block';

                if (indicator) {
                    indicator.style.transform = isVisible ? '' : 'rotate(-180deg)';
                }
            }
        }

        addFilenameMappingRow(mapping) {
            const keyword = mapping ? mapping.Keyword : '';
            const iconName = mapping ? mapping.IconName : '';

            const newRow = document.createElement('div');
            newRow.classList.add('filenameMappingRow');
            newRow.style.display = 'flex';
            newRow.style.gap = '1em';
            newRow.style.alignItems = 'center';
            newRow.style.marginBottom = '1em';

            newRow.innerHTML = `
                <div class="inputContainer" style="flex-grow: 1; margin: 0;">
                    <input is="emby-input" type="text" label="Keyword:" value="${keyword}" class="txtFilenameKeyword" />
                    <div class="fieldDescription">Case-insensitive text to find in the filename.</div>
                </div>
                <div class="inputContainer" style="flex-grow: 1; margin: 0;">
                    <input is="emby-input" type="text" label="Icon Name:" value="${iconName}" class="txtFilenameIconName" />
                    <div class="fieldDescription">e.g., 'remux' for 'source.remux.png'</div>
                </div>
                <button is="emby-button" type="button" class="raised button-cancel btnDeleteFilenameMapping" title="Delete Mapping"><span></span></button>
            `;

            this.dom.filenameMappingsContainer.appendChild(newRow);
        }

        onFilenameMappingButtonClick(e) {
            const deleteButton = e.target.closest('.btnDeleteFilenameMapping');
            if (deleteButton) {
                deleteButton.closest('.filenameMappingRow').remove();
                this.triggerConfigSave();
            }
        }

        loadFilenameMappings(profile) {
            this.dom.filenameMappingsContainer.innerHTML = '';
            const mappings = profile.Settings.FilenameBasedIcons || [];
            mappings.forEach(mapping => this.addFilenameMappingRow(mapping));
        }

        saveFilenameMappings(profile) {
            const mappings = [];
            this.dom.filenameMappingsContainer.querySelectorAll('.filenameMappingRow').forEach(row => {
                const keyword = row.querySelector('.txtFilenameKeyword').value.trim();
                const iconName = row.querySelector('.txtFilenameIconName').value.trim();
                if (keyword && iconName) {
                    mappings.push({ Keyword: keyword, IconName: iconName });
                }
            });
            profile.Settings.FilenameBasedIcons = mappings;
        }
    }

    return EmbyIconsConfigurationView;
});