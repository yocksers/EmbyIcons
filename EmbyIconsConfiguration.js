define(['baseView', 'loading', 'dialogHelper', 'toast', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading, dialogHelper, toast) {
    'use strict';

    const pluginId = "b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f";

    function generateUUID() {
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
            const r = Math.random() * 16 | 0,
                v = c == 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });
    }

    function View(view, params) {
        BaseView.apply(this, arguments);

        this.pluginConfiguration = {};
        this.allLibraries = [];
        this.currentProfileId = null;
        this.previewUpdateTimer = null;

        this.bindEvents(view);
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.bindEvents = function (view) {
        const form = view.querySelector('.embyIconsForm');
        form.addEventListener('change', this.onFormChange.bind(this));
        view.querySelectorAll('select[is="emby-select"]:not(#selActiveProfile)').forEach(select => {
            select.addEventListener('change', this.onFormChange.bind(this));
        });
        const opacitySlider = view.querySelector('[data-profile-key="CommunityScoreBackgroundOpacity"]');
        if (opacitySlider) {
            opacitySlider.addEventListener('input', this.onFormChange.bind(this));
        }
        view.querySelectorAll('.localnav .nav-button').forEach(navButton => {
            navButton.addEventListener('click', this.onTabChange.bind(this));
        });
        view.querySelector('#selActiveProfile').addEventListener('change', this.onProfileSelected.bind(this));
        view.querySelector('#btnAddProfile').addEventListener('click', this.addProfile.bind(this));
        view.querySelector('#btnRenameProfile').addEventListener('click', this.renameProfile.bind(this));
        view.querySelector('#btnDeleteProfile').addEventListener('click', this.deleteProfile.bind(this));
        view.querySelector('#btnSelectIconsFolder').addEventListener('click', this.selectIconsFolder.bind(this));
        view.querySelector('#btnClearCache').addEventListener('click', this.clearCache.bind(this));
        view.querySelector('#btnRunIconScan').addEventListener('click', this.runIconScan.bind(this));
        form.addEventListener('submit', (e) => {
            e.preventDefault();
            this.saveData();
            return false;
        });
    };

    View.prototype.onFormChange = function (event) {
        this.saveCurrentProfileSettings();
        this.triggerPreviewUpdate();
        if (event.target.matches('[data-profile-key="CommunityScoreBackgroundOpacity"]')) {
            const opacityValue = this.view.querySelector('.valCommunityScoreBackgroundOpacity');
            if (opacityValue) {
                opacityValue.textContent = event.target.value + '%';
            }
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
    };

    View.prototype.toggleDependentSetting = function (controllerKey, dependentKey, dependentMessage) {
        const controllerCheckbox = this.view.querySelector(`[data-profile-key="${controllerKey}"]`);
        const dependentCheckbox = this.view.querySelector(`[data-profile-key="${dependentKey}"]`);
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
    };

    View.prototype.toggleRatingAppearanceControls = function () {
        const ratingAlignmentSelect = this.view.querySelector('[data-profile-key="CommunityScoreIconAlignment"]');
        const ratingControlsContainer = this.view.querySelector('#ratingAppearanceControls');
        if (ratingAlignmentSelect && ratingControlsContainer) {
            const isDisabled = ratingAlignmentSelect.value === 'Disabled';
            ratingControlsContainer.style.display = isDisabled ? 'none' : 'block';
        }
    };

    View.prototype.onTabChange = function (e) {
        const currentTarget = e.currentTarget;
        const previouslyActive = this.view.querySelector('.localnav .ui-btn-active');
        if (previouslyActive) {
            previouslyActive.classList.remove('ui-btn-active');
        }
        currentTarget.classList.add('ui-btn-active');
        const targetId = currentTarget.getAttribute('data-target');
        this.view.querySelectorAll('#settingsPage, #readmePage, #iconManagerPage').forEach(page => {
            page.classList.toggle('hide', page.id !== targetId);
        });
    };

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        this.loadData();
    };

    View.prototype.loadData = async function () {
        loading.show();
        try {
            const [config, virtualFolders] = await Promise.all([
                ApiClient.getPluginConfiguration(pluginId),
                ApiClient.getVirtualFolders()
            ]);
            this.pluginConfiguration = config;
            this.view.querySelectorAll('[data-config-key]').forEach(el => {
                const key = el.getAttribute('data-config-key');
                const value = config[key];
                if (el.type === 'checkbox') {
                    el.checked = value;
                } else {
                    el.value = value == null ? '' : value;
                }
            });
            const ignoredLibraryTypes = ['music', 'collections', 'playlists', 'boxsets'];
            this.allLibraries = virtualFolders.Items.filter(lib => !lib.CollectionType || !ignoredLibraryTypes.includes(lib.CollectionType.toLowerCase()));
            this.populateProfileSelector();
            this.loadProfileSettings(this.view.querySelector('#selActiveProfile').value);
        } catch (error) {
            console.error('Failed to load EmbyIcons configuration', error);
            toast({ type: 'error', text: 'Error loading configuration.' });
        } finally {
            loading.hide();
        }
    };

    View.prototype.populateProfileSelector = function () {
        const select = this.view.querySelector('#selActiveProfile');
        select.innerHTML = this.pluginConfiguration.Profiles.map(p => `<option value="${p.Id}">${p.Name}</option>`).join('');
        this.currentProfileId = select.value;
        if (select.embyselect) select.embyselect.refresh();
    };

    View.prototype.onProfileSelected = function (e) {
        this.loadProfileSettings(e.target.value);
    };

    View.prototype.loadProfileSettings = function (profileId) {
        this.currentProfileId = profileId;
        const profile = this.pluginConfiguration.Profiles.find(p => p.Id === profileId);
        if (!profile) return;
        this.view.querySelectorAll('[data-profile-key]').forEach(el => {
            const key = el.getAttribute('data-profile-key');
            const value = profile.Settings[key];
            if (el.type === 'checkbox') {
                el.checked = value;
            } else {
                el.value = value == null ? '' : value;
            }
        });
        this.view.querySelectorAll('select[is="emby-select"][data-profile-key]').forEach(s => {
            if (s.embyselect) s.embyselect.refresh();
        });
        const opacitySlider = this.view.querySelector('[data-profile-key="CommunityScoreBackgroundOpacity"]');
        const opacityValue = this.view.querySelector('.valCommunityScoreBackgroundOpacity');
        if (opacitySlider && opacityValue) {
            opacityValue.textContent = opacitySlider.value + '%';
        }
        this.toggleRatingAppearanceControls();
        this.toggleDependentSetting('UseSeriesLiteMode', 'ShowSeriesIconsIfAllEpisodesHaveLanguage', 'This setting is ignored when Lite Mode is enabled, as Lite Mode only scans one episode.');
        this.toggleDependentSetting('UseCollectionLiteMode', 'ShowCollectionIconsIfAllChildrenHaveLanguage', 'This setting is ignored when Lite Mode is enabled, as Lite Mode only scans one item.');
        this.populateLibraryAssignments(profileId);
        this.triggerPreviewUpdate();
    };

    View.prototype.populateLibraryAssignments = function (profileId) {
        const container = this.view.querySelector('#librarySelectionContainer');
        container.innerHTML = this.allLibraries.map(library => `<div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" data-library-id="${library.Id}" /><span>${library.Name}</span></label></div>`).join('');
        const mappedLibraryIds = new Set(this.pluginConfiguration.LibraryProfileMappings.filter(m => m.ProfileId === profileId).map(m => m.LibraryId));
        container.querySelectorAll('input[type=checkbox]').forEach(checkbox => {
            checkbox.checked = mappedLibraryIds.has(checkbox.getAttribute('data-library-id'));
            checkbox.addEventListener('change', this.onFormChange.bind(this));
        });
    };

    View.prototype.saveCurrentProfileSettings = function () {
        const profile = this.pluginConfiguration.Profiles.find(p => p.Id === this.currentProfileId);
        if (!profile) return;
        this.view.querySelectorAll('[data-profile-key]').forEach(el => {
            const key = el.getAttribute('data-profile-key');
            if (el.type === 'checkbox') {
                profile.Settings[key] = el.checked;
            } else if (el.type === 'number' || el.classList.contains('slider') || key.endsWith('Priority')) {
                profile.Settings[key] = parseInt(el.value, 10) || 0;
            } else {
                profile.Settings[key] = el.value;
            }
        });
        this.pluginConfiguration.LibraryProfileMappings = this.pluginConfiguration.LibraryProfileMappings.filter(m => m.ProfileId !== this.currentProfileId);
        this.view.querySelector('#librarySelectionContainer').querySelectorAll('input:checked').forEach(checkbox => {
            this.pluginConfiguration.LibraryProfileMappings.push({
                LibraryId: checkbox.getAttribute('data-library-id'),
                ProfileId: this.currentProfileId
            });
        });
    };

    View.prototype.getDefaultProfileSettings = function () {
        return { EnableForPosters: true, EnableForThumbs: false, EnableForBanners: false, ShowOverlaysForEpisodes: true, ShowSeriesIconsIfAllEpisodesHaveLanguage: true, ShowCollectionIconsIfAllChildrenHaveLanguage: true, UseCollectionLiteMode: true, AudioIconAlignment: "TopLeft", AudioOverlayHorizontal: true, AudioIconPriority: 1, SubtitleIconAlignment: "BottomLeft", SubtitleOverlayHorizontal: true, SubtitleIconPriority: 2, ChannelIconAlignment: "Disabled", ChannelOverlayHorizontal: true, ChannelIconPriority: 7, AudioCodecIconAlignment: "Disabled", AudioCodecOverlayHorizontal: true, AudioCodecIconPriority: 8, VideoFormatIconAlignment: "Disabled", VideoFormatOverlayHorizontal: true, VideoFormatIconPriority: 4, VideoCodecIconAlignment: "Disabled", VideoCodecOverlayHorizontal: true, VideoCodecIconPriority: 5, TagIconAlignment: "Disabled", TagOverlayHorizontal: false, TagIconPriority: 6, ResolutionIconAlignment: "Disabled", ResolutionOverlayHorizontal: true, ResolutionIconPriority: 3, CommunityScoreIconAlignment: "Disabled", CommunityScoreOverlayHorizontal: true, CommunityScoreIconPriority: 9, AspectRatioIconAlignment: "Disabled", AspectRatioOverlayHorizontal: true, AspectRatioIconPriority: 10, ParentalRatingIconAlignment: "Disabled", ParentalRatingOverlayHorizontal: true, ParentalRatingIconPriority: 11, CommunityScoreBackgroundShape: "None", CommunityScoreBackgroundColor: "#404040", CommunityScoreBackgroundOpacity: 80, IconSize: 10, JpegQuality: 75, EnableImageSmoothing: false, UseSeriesLiteMode: true };
    };

    View.prototype.addProfile = function () {
        const result = window.prompt('Enter new profile name:', 'New Profile');
        if (result) {
            const currentProfile = this.pluginConfiguration.Profiles.find(p => p.Id === this.currentProfileId);
            const newSettings = currentProfile ? JSON.parse(JSON.stringify(currentProfile.Settings)) : this.getDefaultProfileSettings();
            const newProfile = {
                Id: generateUUID(),
                Name: result,
                Settings: newSettings
            };
            this.pluginConfiguration.Profiles.push(newProfile);
            this.populateProfileSelector();
            this.view.querySelector('#selActiveProfile').value = newProfile.Id;
            this.loadProfileSettings(newProfile.Id);
        }
    };

    View.prototype.renameProfile = function () {
        const profile = this.pluginConfiguration.Profiles.find(p => p.Id === this.currentProfileId);
        if (!profile) return;
        const result = window.prompt('Enter new name for profile:', profile.Name);
        if (result) {
            profile.Name = result;
            this.populateProfileSelector();
        }
    };

    View.prototype.deleteProfile = function () {
        if (this.pluginConfiguration.Profiles.length <= 1) {
            toast({ type: 'error', text: 'Cannot delete the last profile.' });
            return;
        }
        const profile = this.pluginConfiguration.Profiles.find(p => p.Id === this.currentProfileId);
        if (!profile) return;
        if (window.confirm(`Are you sure you want to delete the profile "${profile.Name}"?`)) {
            this.pluginConfiguration.Profiles = this.pluginConfiguration.Profiles.filter(p => p.Id !== this.currentProfileId);
            this.pluginConfiguration.LibraryProfileMappings = this.pluginConfiguration.LibraryProfileMappings.filter(m => m.ProfileId !== this.currentProfileId);
            this.populateProfileSelector();
            this.loadProfileSettings(this.view.querySelector('#selActiveProfile').value);
        }
    };

    View.prototype.saveData = async function () {
        if (!this.pluginConfiguration.Profiles || !this.pluginConfiguration.Profiles.length) {
            toast({ type: 'error', text: 'Cannot save settings. You must create at least one profile.' });
            return;
        }

        loading.show();
        this.saveCurrentProfileSettings();
        this.view.querySelectorAll('[data-config-key]').forEach(el => {
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
    };

    View.prototype.triggerPreviewUpdate = function () {
        if (this.previewUpdateTimer) clearTimeout(this.previewUpdateTimer);
        this.previewUpdateTimer = setTimeout(() => {
            if (this.view) this.updatePreview();
        }, 300);
    };

    View.prototype.updatePreview = function () {
        const profile = this.pluginConfiguration.Profiles.find(p => p.Id === this.currentProfileId);
        if (!profile) return;
        this.view.querySelector('.previewImage').src = ApiClient.getUrl('EmbyIcons/Preview', {
            OptionsJson: JSON.stringify(profile.Settings),
            v: new Date().getTime()
        });
    };

    View.prototype.selectIconsFolder = function () {
        require(['directorybrowser'], (directorybrowser) => {
            new directorybrowser().show({
                header: 'Select Icons Folder',
                path: this.view.querySelector('#txtIconsFolder').value,
                callback: (path) => {
                    if (path) {
                        this.view.querySelector('#txtIconsFolder').value = path;
                        this.onFormChange({ target: this.view.querySelector('#txtIconsFolder') });
                    }
                }
            });
        });
    };

    View.prototype.clearCache = async function () {
        loading.show();
        try {
            await ApiClient.ajax({ type: "POST", url: ApiClient.getUrl("EmbyIcons/RefreshCache") });
            toast('Cache cleared, icons will be redrawn.');
        } catch (error) {
            console.error('Error clearing EmbyIcons cache', error);
            toast({ type: 'error', text: 'Error clearing icon cache.' });
        } finally {
            loading.hide();
        }
    };

    View.prototype.runIconScan = async function () {
        loading.show();
        const container = this.view.querySelector('#iconManagerReportContainer');
        container.innerHTML = '<p>Scanning your library... This may take several minutes on the first run.</p>';
        try {
            const report = await ApiClient.ajax({ type: "GET", url: ApiClient.getUrl("EmbyIcons/IconManagerReport"), dataType: "json" });
            this.renderIconManagerReport(report);
        } catch (error) {
            console.error('Error getting icon manager report', error);
            container.innerHTML = '<p style="color: #ff4444;">An error occurred while generating the report. Please check the server logs.</p>';
        } finally {
            loading.hide();
        }
    };

    View.prototype.renderIconManagerReport = function (report) {
        const container = this.view.querySelector('#iconManagerReportContainer');
        const friendlyNames = { Language: 'Audio Languages', Subtitle: 'Subtitle Languages', Channel: 'Audio Channels', AudioCodec: 'Audio Codecs', VideoCodec: 'Video Codecs', VideoFormat: 'Video Formats', Resolution: 'Resolutions', AspectRatio: 'Aspect Ratios', Tag: 'Custom Tags', ParentalRating: 'Parental Ratings' };
        const prefixMap = { Language: "lang.", Subtitle: "sub.", Channel: "ch.", AudioCodec: "ac.", VideoCodec: "vc.", VideoFormat: "hdr.", Resolution: "res.", AspectRatio: "ar.", Tag: "tag.", ParentalRating: "pr." };
        let html = `<p class="fieldDescription">Report generated on: ${new Date(report.ReportDate).toLocaleString()}</p>`;
        for (const groupName in report.Groups) {
            if (Object.prototype.hasOwnProperty.call(report.Groups, groupName) && friendlyNames[groupName]) {
                const group = report.Groups[groupName];
                const librarySet = new Set(group.FoundInLibrary.map(i => i.toLowerCase()));
                const folderSet = new Set(group.FoundInFolder.map(i => i.toLowerCase()));
                const found = [...folderSet].filter(i => librarySet.has(i)).sort();
                const missing = [...librarySet].filter(i => !folderSet.has(i)).sort();
                const unused = [...folderSet].filter(i => !librarySet.has(i)).sort();
                if (found.length === 0 && missing.length === 0 && unused.length === 0) continue;
                html += `<div class="paper-card" style="margin-top: 1.5em; padding: 1em 1.5em;">`;
                html += `<h3 style="margin-top: 0; cursor: pointer;" onclick="this.nextElementSibling.style.display = this.nextElementSibling.style.display === 'none' ? 'block' : 'none';">${friendlyNames[groupName]}</h3>`;
                html += `<div class="collapsible-content" style="display: none;">`;
                if (missing.length > 0) {
                    html += `<h4><span style="color: #ffc107;">!</span> Missing Icons (${missing.length})</h4><p class="fieldDescription">Your library needs these icons, but they were not found in your custom folder. You can add them or rely on the built-in fallback icons (if enabled).</p>`;
                    html += `<div class="icon-grid">${missing.map(i => `<code>${prefixMap[groupName] || ''}${i}.png</code>`).join('')}</div>`;
                }
                if (found.length > 0) {
                    html += `<h4 style="margin-top: 1.5em;"><span style="color: #4CAF50;">✓</span> Found Icons (${found.length})</h4><p class="fieldDescription">These custom icons are correctly configured and used by your library.</p>`;
                    html += `<div class="icon-grid">${found.map(i => `<code>${prefixMap[groupName] || ''}${i}.png</code>`).join('')}</div>`;
                }
                if (unused.length > 0) {
                    html += `<h4 style="margin-top: 1.5em;"><span style="color: #888;">-</span> Unused Icons (${unused.length})</h4><p class="fieldDescription">These icons exist in your folder but are not currently needed by any media.</p>`;
                    html += `<div class="icon-grid">${unused.map(i => `<code>${prefixMap[groupName] || ''}${i}.png</code>`).join('')}</div>`;
                }
                html += `</div></div>`;
            }
        }
        html += `<style>.icon-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 0.5em; } .icon-grid code { background-color: rgba(128,128,128,0.2); padding: 0.2em 0.4em; border-radius: 3px; word-break: break-all; }</style>`;
        container.innerHTML = html;
    };

    return View;
});